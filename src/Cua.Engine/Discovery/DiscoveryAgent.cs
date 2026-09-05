using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Cua.Core.Artifacts;
using Cua.Core.Contracts;
using Cua.Core.Evidence;
using Cua.Core.Hitl;
using Cua.Core.Policy;
using Cua.Core.Redaction;
using Cua.Core.Surface;
using Cua.Engine.Common;

namespace Cua.Engine.Discovery;

public sealed record DiscoveryConfig
{
    public required string Goal { get; init; }
    public required string EntryUrl { get; init; }
    public required string CapabilityId { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public string CredentialsRef { get; init; } = "env://CUA";
    public string Model { get; init; } = "claude-opus-5";
    public string? VendorProduct { get; init; }
    public SurfaceKind SurfaceKind { get; init; } = SurfaceKind.Web;
    /// <summary>Which installed app this recording binds to (versions count per capability+binding).</summary>
    public string? AppBinding { get; init; }
    public int MaxModelTurns { get; init; } = 60;
}

/// <summary>
/// The LLM-driven observe → decide → act loop. The model proposes actions as
/// tool calls; every proposal passes the policy gate before it may touch the
/// surface; every executed action is recorded as ground-truth trace. When the
/// model declares the capability, the compiler turns trace + declaration into
/// a replayable artifact — the model transcript itself is evidence, not the
/// artifact.
/// </summary>
public sealed class DiscoveryAgent(
    ISurface surface,
    PolicyGate policy,
    RunLogger log,
    Redactor redactor,
    IOperatorChannel operatorChannel)
{
    private readonly List<TraceStep> _trace = [];
    private int _stepCounter;
    private int _modelTurns;
    private long _inputTokens, _outputTokens;

    public async Task<DiscoveryResult> RunAsync(
        DiscoveryConfig config, ArtifactStore store, CancellationToken outerCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(policy.Config.MaxRunTime);
        var ct = cts.Token;

        // Credentials are resolved up front so their literals are redacted from
        // every artifact of this run, and so a missing credential fails fast.
        var credsRef = new CredentialsRef { Ref = config.CredentialsRef };
        var username = CredentialResolver.Resolve(credsRef, "username");
        var password = CredentialResolver.Resolve(credsRef, "password");
        redactor.AddLiteral(password);

        log.Log("discovery_started", new
        {
            goal = config.Goal, entry = config.EntryUrl, model = config.Model,
            surface_kind = config.SurfaceKind,
        });

        var nav = policy.CheckNavigation(config.EntryUrl);
        if (nav.IsBlocked) throw new InvalidOperationException($"entry url blocked by policy: {nav.Reason}");
        await surface.NavigateAsync(config.EntryUrl, ct);
        log.SaveScreenshot(await surface.ScreenshotAsync(ct), "initial");

        AnthropicClient client = new();
        var tools = AgentTools.Build(config.SurfaceKind);
        var messages = new List<MessageParam>
        {
            new() { Role = Role.User, Content = await InitialUserMessageAsync(config, ct) },
        };

        for (var turn = 0; turn < config.MaxModelTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = config.Model,
                MaxTokens = 8000,
                System = SystemPrompt(config),
                Tools = tools,
                Messages = messages,
            });
            _modelTurns++;
            _inputTokens += response.Usage.InputTokens;
            _outputTokens += response.Usage.OutputTokens;
            RecordTranscript(turn, response);

            var stop = response.StopReason?.ToString() ?? "";
            if (stop.Contains("refusal", StringComparison.OrdinalIgnoreCase))
                return Fail(config, "model refused the request (stop_reason=refusal)");

            var (assistantEcho, toolUses) = EchoAssistant(response);
            if (toolUses.Count == 0)
            {
                // plain text turn — nudge the model back into the tool loop
                messages.Add(new() { Role = Role.Assistant, Content = assistantEcho });
                messages.Add(new() { Role = Role.User, Content = "Continue via tool calls only. When the goal is met, call declare_capability." });
                continue;
            }

            messages.Add(new() { Role = Role.Assistant, Content = assistantEcho });
            var results = new List<ContentBlockParam>();
            foreach (var tu in toolUses)
            {
                var (resultText, terminal) = await HandleToolAsync(tu, config, username, password, store, ct);
                if (terminal is not null)
                {
                    // still answer the tool call so the transcript stays valid evidence
                    results.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = resultText });
                    messages.Add(new() { Role = Role.User, Content = results });
                    return terminal;
                }
                results.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = resultText });
            }
            messages.Add(new() { Role = Role.User, Content = results });
        }

        return Fail(config, $"model did not declare the capability within {config.MaxModelTurns} turns");
    }

    // ---------------------------------------------------------------- prompts

    private static string SystemPrompt(DiscoveryConfig config) => $$$"""
        You are the discovery engine of a computer-use automation system for
        back-office applications. You drive a live {{{SurfaceNoun(config.SurfaceKind)}}} through tools to
        accomplish a goal ONCE; what you learn is compiled into a deterministic,
        replayable capability that runs WITHOUT you. Work accordingly: prefer stable,
        explainable interactions over clever ones.

        Operating rules:
        {{{OperatingRules(config.SurfaceKind)}}}
        - Credentials: use {{credential:username}} and {{credential:password}}
          placeholders; you never see real values.
        - Run parameters (they parameterize the recorded capability):
          {{{string.Join("; ", config.Parameters.Select(p => $"{p.Key} = \"{p.Value}\""))}}}
          Type parameter values literally where the flow needs them.
        - Policy: act only on the allowlisted {{{(SurfaceKinds.IsWeb(config.SurfaceKind) ? "hosts" : "applications")}}}. Mark risk=irreversible on any click
          that posts/commits/waives/reverses. Blocked actions return POLICY_BLOCKED —
          do not retry them; find a compliant path or give_up.
        - If you are stuck, call escalate_to_human rather than thrashing.
        - GROUNDING (critical): every condition you declare in declare_capability —
          business outcomes, recoverable transients, escalations, success checks —
          must be text or structure you ACTUALLY OBSERVED during this session.
          Never guess or paraphrase an error message you have not seen: replay
          matches your strings verbatim against the live screen, so an invented
          string is a broken artifact. If test/variant inputs are available, use
          probe actions (probe=true on click/type/select/read) AFTER completing and
          verifying the main flow, to trigger each outcome state and read its exact
          wording. Probe steps are excluded from the recorded flow. If you cannot
          observe a state, leave it undeclared rather than inventing it.
        - Goal met and final state visible → first finish the recorded flow, then
          probe variant inputs to ground your outcome conditions, then call
          declare_capability exactly once. Classify: business outcomes (record not
          found, account closed …), recoverable transients (host busy banners) with
          retry policy, and escalations (security/override modals — check which
          frame they appear in; on nested surfaces they often escape to the top
          {{{(config.SurfaceKind == SurfaceKind.Desktop ? "window" : "document")}}}). If a probe raises a blocking modal you cannot clear, do that
          probe LAST — you can still observe its text and then declare.

        Tool results include step ids like "recorded step s3" — use those ids in
        declare_capability (auth_steps, guarded_steps.after_step). Never reference
        probe step ids there.
        """;

    private static string SurfaceNoun(SurfaceKind kind) => kind == SurfaceKind.Desktop ? "desktop application" : "browser";

    private static string OperatingRules(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Desktop => """
        - Windows and panes matter. Pass frame_path as the window/pane name path
          ([] = the main window, ["Fee Management"] = a child pane). If a pane is
          missing, wait_for it or re-observe.
        - Locators: prefer AutomationId, written as css "#the-id" (the desktop adapter
          maps #id → AutomationId). Fall back to Name (css "input[name='x']" or text).
          Never invent ids you have not seen in an observation. Coordinates are last resort.
        - After ANY action that triggers a slow host call, wait_for state=absent on the
          busy indicator before trusting what you observe.
        """,
        SurfaceKind.LegacyWeb => """
        - Frames matter. Legacy apps nest iframes; pass frame_path explicitly on every
          action ([] = the top document). Modules injected by script may take seconds
          to exist — if a frame or element is missing, wait_for it or re-observe.
        - Locators: prefer element ids (css "#the-id"); fall back to name attributes
          (css "input[name='x']"), then visible text. Never invent selectors you have
          not seen in an observation. Generated ids (ext-genNN) churn — text and name
          are real fallbacks.
        - After ANY action that triggers a host call, wait_for state=absent on the busy
          indicator before trusting what you observe. Busy indicators can take many
          seconds on these systems; be patient, not repetitive.
        """,
        _ => """
        - Prefer the main document (frame_path []). Use a frame path only when the
          observation shows a named iframe.
        - Locators: prefer stable ids or data-testid (css "#the-id"); fall back to name
          attributes, then visible text. Never invent selectors you have not seen.
        - After ANY action that triggers a network or host call, wait_for state=absent
          on the busy indicator before trusting what you observe.
        """,
    };

    private async Task<string> InitialUserMessageAsync(DiscoveryConfig config, CancellationToken ct)
    {
        var observation = await surface.ObserveAsync(ct);
        return $"""
            GOAL: {config.Goal}
            TARGET: {config.EntryUrl}
            SURFACE KIND: {config.SurfaceKind}
            CAPABILITY ID: {config.CapabilityId}
            ALLOWLIST: {string.Join(", ", policy.Config.AllowedHosts)}

            Current state of the application:
            {observation.ToPromptText()}
            """;
    }

    // ------------------------------------------------------------- tool calls

    private async Task<(string Result, DiscoveryResult? Terminal)> HandleToolAsync(
        ToolUseBlock tu, DiscoveryConfig config, string username, string password,
        ArtifactStore store, CancellationToken ct)
    {
        try
        {
            switch (tu.Name)
            {
                case "click": return (await DoClickAsync(tu, ct), null);
                case "type": return (await DoTypeAsync(tu, config, username, password, ct), null);
                case "select": return (await DoSelectAsync(tu, ct), null);
                case "read": return (await DoReadAsync(tu, ct), null);
                case "wait_for": return (await DoWaitAsync(tu, ct), null);
                case "navigate": return (await DoNavigateAsync(tu, ct), null);
                case "observe": return ("Observation:\n" + await ObserveTextAsync(ct), null);

                case "escalate_to_human":
                {
                    var reason = Str(tu, "reason") ?? "discovery agent requested help";
                    var request = new InterventionRequest
                    {
                        RunId = log.RunId,
                        CapabilityId = config.CapabilityId,
                        Goal = config.Goal,
                        StepId = _trace.Count > 0 ? _trace[^1].Id : "(start)",
                        Reason = reason,
                        ScreenshotPath = log.SaveScreenshot(await surface.ScreenshotAsync(ct), "escalation"),
                        ResumePlan = "discovery resumes with a fresh observation after handback",
                    };
                    log.Log("intervention_raised", new { reason, during = "discovery" });
                    var resolution = await operatorChannel.RequestInterventionAsync(request, surface.Control, ct);
                    if (!resolution.Resolved)
                        return ("", Fail(config, $"escalation not resolved: {resolution.OperatorNotes}"));
                    var humanActions = await surface.DrainHumanActionsAsync();
                    foreach (var a in humanActions) log.AppendJsonl("human_actions.jsonl", a);
                    return ($"A human operator intervened ({humanActions.Count} recorded actions). " +
                            $"Notes: {resolution.OperatorNotes ?? "(none)"}\nFresh observation:\n" +
                            await ObserveTextAsync(ct), null);
                }

                case "give_up":
                    return ("", Fail(config, $"model gave up: {Str(tu, "reason")}"));

                case "declare_capability":
                    return ("", CompileAndSave(tu, config, store));

                default:
                    return ($"ERROR: unknown tool {tu.Name}", null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Log("tool_error", new { tool = tu.Name, error = ex.Message });
            return ($"ERROR: {ex.Message}", null);
        }
    }

    private async Task<string> DoClickAsync(ToolUseBlock tu, CancellationToken ct)
    {
        var frame = FramePath(tu);
        var locator = LocatorFrom(tu);
        var declaredRisk = Str(tu, "risk") switch
        {
            "irreversible" => RiskLevel.Irreversible,
            "write" => RiskLevel.Write,
            _ => RiskLevel.Safe,
        };

        var target = await ResolveAsync(frame, locator, ct);
        if (target is null) return NotResolved(locator, frame);

        var decision = policy.CheckAction(StepAction.Click, await surface.CurrentUrlAsync(),
            declaredRisk, target.Meta.Text);
        if (decision.IsBlocked) return $"POLICY_BLOCKED: {decision.Reason}";
        if (decision.Verdict == PolicyVerdict.NeedsConfirmation)
            return $"POLICY_BLOCKED: {decision.Reason} (confirmation channel not available during discovery)";
        if (decision.Verdict == PolicyVerdict.AllowedWithFlag)
            log.Log("risk_flagged", new { text = target.Meta.Text, reason = decision.Reason });

        await surface.ClickAsync(target, ct);
        var step = Record(new TraceStep
        {
            Id = NextId(),
            Action = StepAction.Click,
            Frame = frame,
            ModelLocator = locator,
            Meta = target.Meta,
            Risk = decision.EffectiveRisk,
            Note = Str(tu, "note"),
        });
        log.SaveScreenshot(await surface.ScreenshotAsync(ct), $"{step.Id}-click");
        return $"OK — recorded step {step.Id} (clicked {Describe(target.Meta)}).\nObservation:\n{await ObserveTextAsync(ct)}";
    }

    private async Task<string> DoTypeAsync(
        ToolUseBlock tu, DiscoveryConfig config, string username, string password, CancellationToken ct)
    {
        var frame = FramePath(tu);
        var locator = LocatorFrom(tu);
        var rawText = Str(tu, "text") ?? "";
        var clearFirst = tu.Input.TryGetValue("clear_first", out var cf) && cf.ValueKind == JsonValueKind.False ? false : true;

        // credential placeholders → substitute real value, record only the ref
        string actualText = rawText, recordedValue = rawText;
        string? valueRef = null;
        if (rawText.Contains("{{credential:username}}"))
        {
            actualText = username; valueRef = "credentials.username"; recordedValue = "";
        }
        else if (rawText.Contains("{{credential:password}}"))
        {
            actualText = password; valueRef = "credentials.password"; recordedValue = "";
        }
        else
        {
            var param = config.Parameters.FirstOrDefault(p => p.Value == rawText);
            if (param.Key is not null) { valueRef = $"inputs.{param.Key}"; recordedValue = ""; }
        }

        var decision = policy.CheckAction(StepAction.Type, await surface.CurrentUrlAsync(), RiskLevel.Safe, null);
        if (decision.IsBlocked) return $"POLICY_BLOCKED: {decision.Reason}";

        var target = await ResolveAsync(frame, locator, ct);
        if (target is null) return NotResolved(locator, frame);

        await surface.TypeAsync(target, actualText, clearFirst, ct);
        var step = Record(new TraceStep
        {
            Id = NextId(),
            Action = StepAction.Type,
            Frame = frame,
            ModelLocator = locator,
            Meta = target.Meta,
            Value = valueRef is null ? recordedValue : null,
            ValueRef = valueRef,
            ClearFirst = clearFirst,
            Note = Str(tu, "note"),
            Probe = Probe(tu),
        });
        return $"OK — {StepLabel(step)} (typed into {Describe(target.Meta)}" +
               $"{(valueRef is null ? "" : $", parameterized as {valueRef}")}).";
    }

    private async Task<string> DoSelectAsync(ToolUseBlock tu, CancellationToken ct)
    {
        var frame = FramePath(tu);
        var locator = LocatorFrom(tu);
        var option = Str(tu, "option") ?? "";
        var target = await ResolveAsync(frame, locator, ct);
        if (target is null) return NotResolved(locator, frame);
        await surface.SelectAsync(target, option, ct);
        var step = Record(new TraceStep
        {
            Id = NextId(), Action = StepAction.Select, Frame = frame,
            ModelLocator = locator, Meta = target.Meta, Value = option,
            Probe = Probe(tu),
        });
        return $"OK — {StepLabel(step)} (selected \"{option}\").";
    }

    private async Task<string> DoReadAsync(ToolUseBlock tu, CancellationToken ct)
    {
        var frame = FramePath(tu);
        var locator = LocatorFrom(tu);
        var outputName = Str(tu, "output_name") ?? "value";
        var target = await ResolveAsync(frame, locator, ct);
        if (target is null) return NotResolved(locator, frame);
        var text = await surface.ReadTextAsync(target, ct);
        var step = Record(new TraceStep
        {
            Id = NextId(), Action = StepAction.Read, Frame = frame,
            ModelLocator = locator, Meta = target.Meta, ReadInto = outputName,
            Probe = Probe(tu),
        });
        return $"OK — {StepLabel(step)} (read {outputName} = \"{text}\").";
    }

    private async Task<string> DoWaitAsync(ToolUseBlock tu, CancellationToken ct)
    {
        var frame = FramePath(tu);
        var locator = LocatorFrom(tu);
        var state = Str(tu, "state") == "absent" ? WaitState.Absent : WaitState.Visible;
        var timeout = tu.Input.TryGetValue("timeout_ms", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : 30_000;

        var ok = await surface.WaitForAsync(frame, locator, state, timeout, ct);

        // Fold the wait into the preceding action step: at replay time it runs
        // as that step's WaitAfter, sequencing the slow host call.
        if (ok && _trace.Count > 0 && _trace[^1].Action is StepAction.Click or StepAction.Select)
        {
            _trace[^1].WaitAfter = new WaitSpec
            {
                Locator = locator, Frame = frame, State = state,
                TimeoutMs = Math.Max(timeout, 30_000),
            };
        }
        log.Log("wait", new { locator.Value, state, ok });
        return ok
            ? $"OK — condition met ({state} {locator.Value}).\nObservation:\n{await ObserveTextAsync(ct)}"
            : $"TIMEOUT — {locator.Value} did not become {state} within {timeout}ms.\nObservation:\n{await ObserveTextAsync(ct)}";
    }

    private async Task<string> DoNavigateAsync(ToolUseBlock tu, CancellationToken ct)
    {
        var url = Str(tu, "url") ?? "";
        var decision = policy.CheckNavigation(url);
        if (decision.IsBlocked) return $"POLICY_BLOCKED: {decision.Reason}";
        await surface.NavigateAsync(url, ct);
        var step = Record(new TraceStep { Id = NextId(), Action = StepAction.Navigate, Url = url });
        return $"OK — recorded step {step.Id} (navigated).\nObservation:\n{await ObserveTextAsync(ct)}";
    }

    // ------------------------------------------------------------ compilation

    private DiscoveryResult CompileAndSave(ToolUseBlock tu, DiscoveryConfig config, ArtifactStore store)
    {
        var declaration = JsonSerializer.SerializeToElement(
            tu.Input.ToDictionary(kv => kv.Key, kv => kv.Value));
        log.SaveText("declaration.json", JsonSerializer.Serialize(declaration,
            new JsonSerializerOptions { WriteIndented = true }));

        var artifact = ArtifactCompiler.Compile(new ArtifactCompiler.Input
        {
            Config = config,
            Trace = _trace,
            Declaration = declaration,
            AllowedHosts = policy.Config.AllowedHosts,
            RunId = log.RunId,
            NextVersion = store.NextVersion(config.CapabilityId, config.AppBinding),
        });
        var path = store.Save(artifact);
        log.Log("artifact_compiled", new { path, steps = artifact.Steps.Count, version = artifact.CapabilityVersion });

        return new DiscoveryResult
        {
            Succeeded = true,
            ArtifactPath = path,
            RunId = log.RunId,
            EvidenceDir = log.Dir,
            Steps = _trace.Count,
            ModelTurns = _modelTurns,
            InputTokens = _inputTokens,
            OutputTokens = _outputTokens,
        };
    }

    private DiscoveryResult Fail(DiscoveryConfig config, string reason)
    {
        log.Log("discovery_failed", new { reason });
        return new DiscoveryResult
        {
            Succeeded = false,
            FailureReason = reason,
            RunId = log.RunId,
            EvidenceDir = log.Dir,
            Steps = _trace.Count,
            ModelTurns = _modelTurns,
            InputTokens = _inputTokens,
            OutputTokens = _outputTokens,
        };
    }

    // -------------------------------------------------------------- plumbing

    private async Task<IResolvedTarget?> ResolveAsync(
        IReadOnlyList<string> frame, Locator locator, CancellationToken ct) =>
        await surface.ResolveAsync(new TargetRef
        {
            Frame = frame,
            Locator = new LocatorChain { Candidates = [locator] },
            TimeoutMs = 12_000,
        }, ct);

    private async Task<string> ObserveTextAsync(CancellationToken ct) =>
        redactor.Apply((await surface.ObserveAsync(ct)).ToPromptText());

    private TraceStep Record(TraceStep step)
    {
        _trace.Add(step);
        log.Log("trace_step", new
        {
            step = step.Id, action = step.Action, frame = step.Frame,
            locator = step.ModelLocator?.Value, target = step.Meta?.Id ?? step.Meta?.Text,
            value_ref = step.ValueRef, risk = step.Risk, probe = step.Probe,
        });
        return step;
    }

    private string NextId() => $"s{++_stepCounter}";

    private static string StepLabel(TraceStep step) =>
        step.Probe ? $"probe step {step.Id} (NOT part of the recorded flow)" : $"recorded step {step.Id}";

    private static string NotResolved(Locator locator, IReadOnlyList<string> frame) =>
        $"NOT_FOUND: {locator.By}:{locator.Value} did not resolve to a visible element in frame " +
        $"{(frame.Count == 0 ? "(top)" : string.Join(">", frame))}. Re-observe and reconsider — " +
        "the element may be in a different frame, not yet injected, or your selector may be wrong.";

    private static string Describe(ElementMeta meta) =>
        meta.Id is not null ? $"#{meta.Id}" : $"<{meta.Tag}> \"{meta.Text}\"";

    private (List<ContentBlockParam>, List<ToolUseBlock>) EchoAssistant(Message response)
    {
        List<ContentBlockParam> echo = [];
        List<ToolUseBlock> toolUses = [];
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                echo.Add(new TextBlockParam { Text = text.Text });
                log.Log("model_says", new { text = text.Text }, echo: true);
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
            {
                echo.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                toolUses.Add(tu);
            }
        }
        return (echo, toolUses);
    }

    private void RecordTranscript(int turn, Message response)
    {
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
                log.AppendJsonl("transcript.jsonl", new { turn, role = "assistant", type = "text", text = text.Text });
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
                log.AppendJsonl("transcript.jsonl", new
                {
                    turn, role = "assistant", type = "tool_use", tool = tu.Name,
                    input = tu.Input.ToDictionary(kv => kv.Key, kv => kv.Value),
                });
        }
        log.AppendJsonl("transcript.jsonl", new
        {
            turn, role = "meta",
            usage = new { input = response.Usage.InputTokens, output = response.Usage.OutputTokens },
        });
    }

    private static bool Probe(ToolUseBlock tu) =>
        tu.Input.TryGetValue("probe", out var p) && p.ValueKind == JsonValueKind.True;

    private static string? Str(ToolUseBlock tu, string key) =>
        tu.Input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> FramePath(ToolUseBlock tu) =>
        tu.Input.TryGetValue("frame_path", out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(e => e.GetString() ?? "")]
            : [];

    private static Locator LocatorFrom(ToolUseBlock tu)
    {
        var by = Str(tu, "by") switch
        {
            "text" => LocatorKind.Text,
            "xpath" => LocatorKind.Xpath,
            _ => LocatorKind.Css,
        };
        return new Locator { By = by, Value = Str(tu, "value") ?? "", Within = Str(tu, "within") };
    }
}
