using System.Text.RegularExpressions;
using Cua.Core.Artifacts;
using Cua.Core.Contracts;
using Cua.Core.Evidence;
using Cua.Core.Hitl;
using Cua.Core.Policy;
using Cua.Core.Redaction;
using Cua.Core.Surface;
using Cua.Engine.Common;

namespace Cua.Engine.Replay;

public sealed record ReplayOptions
{
    /// <summary>Execute irreversible steps without pausing. Requires an approved artifact; otherwise every irreversible step raises an intervention for confirmation.</summary>
    public bool AckRisk { get; init; }
    /// <summary>Permit replay of artifacts still in draft approval state.</summary>
    public bool AllowDraft { get; init; }
    public TimeSpan MaxRunTime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The production execution path: replays a capability artifact with zero LLM
/// involvement. Locator chains give stable targeting; declared assertions give
/// the outcome taxonomy (success / business outcome / recoverable / escalate);
/// anything unrecognized is a hard failure with expected-vs-observed evidence.
/// Single-use: run state lives on the instance, so construct one engine per
/// run — a second RunAsync on the same instance throws.
/// </summary>
public sealed class ReplayEngine(
    ISurface surface,
    PolicyGate policy,
    IOperatorChannel operatorChannel,
    RunLogger log,
    Redactor redactor)
{
    private sealed class RunAborted(ReplayResult result) : Exception { public ReplayResult Result { get; } = result; }

    private CapabilityArtifact _artifact = null!;
    private IReadOnlyDictionary<string, string> _inputs = null!;
    private readonly Dictionary<string, string> _outputs = [];
    private InterventionRecord? _intervention;
    private bool _humanAssisted;
    private bool _authRecovered;
    private DateTimeOffset _startedAt;
    private ReplayOptions _options = new();
    private int _runs;

    public async Task<ReplayResult> RunAsync(
        CapabilityArtifact artifact, IReadOnlyDictionary<string, string> inputs,
        ReplayOptions options, CancellationToken outerCt)
    {
        if (Interlocked.Exchange(ref _runs, 1) != 0)
            throw new InvalidOperationException(
                "ReplayEngine is single-use: run state (outputs, intervention, auth recovery) " +
                "lives on the instance — construct a new engine per run");
        _artifact = artifact;
        _inputs = inputs;
        _options = options;
        _startedAt = DateTimeOffset.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(options.MaxRunTime);
        var ct = cts.Token;

        ArtifactValidator.Validate(artifact);
        ValidateInputs(artifact, inputs);
        if (artifact.Provenance.Approval != "approved" && !options.AllowDraft)
            throw new InvalidOperationException(
                $"artifact is '{artifact.Provenance.Approval}', not approved — review it and run 'cua approve', or pass --allow-draft");

        log.Log("replay_started", new
        {
            capability = artifact.CapabilityId,
            version = artifact.CapabilityVersion,
            surface_kind = artifact.Surface.Kind,
            inputs = inputs.Keys,
            approval = artifact.Provenance.Approval,
        });

        try
        {
            var navigation = policy.CheckNavigation(artifact.Surface.EntryUrl);
            if (navigation.IsBlocked)
                throw await HardAsync("(navigation)", "entry target to be inside the replay allowlist",
                    navigation.Reason ?? "entry target blocked by policy", ct);
            await surface.NavigateAsync(artifact.Surface.EntryUrl, ct);

            var steps = artifact.Steps;
            var i = 0;
            while (i < steps.Count)
            {
                var step = steps[i];
                if (step.Phase != "auth")
                    await CheckGuardsAsync(step, ct);

                var jumpTo = await ExecuteStepAsync(step, ct);
                if (jumpTo is not null)
                {
                    var target = FindStepIndex(steps, jumpTo);
                    log.Log("resume_jump", new { from = step.Id, to = jumpTo });
                    i = target;
                    continue;
                }
                i++;
            }

            var missingOutputs = artifact.Outputs
                .Where(o => o.Required && !_outputs.ContainsKey(o.Name))
                .Select(o => o.Name)
                .ToList();
            if (missingOutputs.Count > 0)
                return await HardFailureAsync("(outputs)",
                    $"required outputs [{string.Join(", ", missingOutputs)}] to be produced",
                    "checkpoint matched but required outputs were missing", ct);
            return Finish(RunStatus.Success);
        }
        catch (RunAborted a)
        {
            return a.Result;
        }
        catch (OperationCanceledException)
        {
            return await HardFailureAsync("(run)", "run to finish within limits",
                "run timed out or was cancelled", CancellationToken.None);
        }
        catch (Exception ex)
        {
            return await HardFailureAsync("(runtime)", "surface operation to complete",
                $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ steps

    /// <summary>Returns a step id to jump to (escalation resume), or null to continue sequentially.</summary>
    private async Task<string?> ExecuteStepAsync(StepDef step, CancellationToken ct)
    {
        log.Log("step_started", new { step = step.Id, action = step.Action, frame = step.Frame, risk = step.Risk, note = step.Note });

        for (var attempt = 0; ; attempt++)
        {
            var effectiveRisk = await PerformActionAsync(step, ct);

            if (step.WaitAfter is { } wait)
            {
                var ok = await surface.WaitForAsync(
                    wait.Frame ?? step.Frame, wait.Locator, wait.State, wait.TimeoutMs, ct);
                log.Log(ok ? "wait_ok" : "wait_timeout",
                    new { step = step.Id, wait.Locator.Value, state = wait.State });
                if (!ok && step.Assertions.Count == 0)
                    throw await HardAsync(step.Id,
                        $"{wait.State} state for {wait.Locator.Value}", "wait condition never satisfied", ct);
            }

            if (step.Assertions.Count == 0)
            {
                await ApplyExtractsAsync(step, ct);
                log.Log("step_ok", new { step = step.Id });
                return null;
            }

            var (verdict, jump) = await EvaluateAssertionsAsync(step, effectiveRisk, attempt, ct);
            switch (verdict)
            {
                case StepVerdict.Proceed:
                    await ApplyExtractsAsync(step, ct);
                    log.Log("step_ok", new { step = step.Id });
                    return jump;
                case StepVerdict.RetryStep:
                    continue; // re-perform the action
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    private enum StepVerdict { Proceed, RetryStep }

    private async Task<(StepVerdict, string?)> EvaluateAssertionsAsync(
        StepDef step, RiskLevel effectiveRisk, int attempt, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(step.TimeoutMs, 10_000));
        while (true)
        {
            if (step.Phase != "auth")
                await CheckGuardsAsync(step, ct);
            foreach (var assertion in step.Assertions)
            {
                if (!await surface.IsConditionMetAsync(assertion.When, step.Frame, ct)) continue;

                log.Log("assertion_matched", new
                {
                    step = step.Id,
                    @class = assertion.Classify,
                    when = $"{assertion.When.By}:{assertion.When.Value}",
                    note = assertion.Note,
                });

                switch (assertion.Classify)
                {
                    case AssertionClass.Recoverable:
                    {
                        if (effectiveRisk == RiskLevel.Irreversible)
                        {
                            // A transient AFTER an irreversible action is ambiguous:
                            // the write may or may not have posted. Re-executing
                            // risks a double-post, so this never retries — it hands
                            // the session to a human, and the following step
                            // (typically the checkpoint) re-verifies state on resume.
                            log.Log("retry_refused", new
                            {
                                step = step.Id,
                                reason = "recoverable condition on an irreversible step: posting state unknown",
                            });
                            var resumeAt = await EscalateAsync(step, new AssertionDef
                            {
                                Classify = AssertionClass.Escalate,
                                When = assertion.When,
                                Escalate = new EscalationSpec
                                {
                                    Reason = $"transient condition '{assertion.When.Value}' after an irreversible action — " +
                                             "posting state unknown; automatic retry refused",
                                },
                            }, ct);
                            return (StepVerdict.Proceed, resumeAt);
                        }

                        var retry = assertion.Retry ?? new RetrySpec();
                        if (attempt >= retry.MaxAttempts)
                            throw await HardAsync(step.Id,
                                $"recoverable condition '{assertion.When.Value}' to clear within {retry.MaxAttempts} retries",
                                "transient condition persisted", ct);
                        var backoff = retry.BackoffMs.Count > 0
                            ? retry.BackoffMs[Math.Min(attempt, retry.BackoffMs.Count - 1)]
                            : 2000;
                        log.Log("retry", new { step = step.Id, attempt = attempt + 1, backoff_ms = backoff });
                        await Task.Delay(backoff, ct);
                        return (StepVerdict.RetryStep, null);
                    }

                    case AssertionClass.BusinessOutcome:
                    {
                        MergeEmit(assertion.Emit);
                        if (assertion.Terminal)
                            throw new RunAborted(Finish(RunStatus.BusinessOutcome));
                        break; // recorded; keep evaluating/continue
                    }

                    case AssertionClass.Escalate:
                    {
                        var resumeAt = await EscalateAsync(step, assertion, ct);
                        return (StepVerdict.Proceed, resumeAt);
                    }

                    case AssertionClass.Success:
                        MergeEmit(assertion.Emit);
                        return (StepVerdict.Proceed, null);
                }
            }

            if (DateTime.UtcNow > deadline)
                throw await HardAsync(step.Id,
                    Describe(step.Assertions), "none of the declared outcome conditions matched", ct);
            await Task.Delay(400, ct);
        }
    }

    private async Task<RiskLevel> PerformActionAsync(StepDef step, CancellationToken ct)
    {
        surface.Control.AssertAutomationHasControl();
        switch (step.Action)
        {
            case StepAction.Navigate:
            {
                var url = step.Url ?? _artifact.Surface.EntryUrl;
                var navigation = policy.CheckNavigation(url);
                if (navigation.IsBlocked)
                    throw await HardAsync(step.Id, "navigation target to be inside the replay allowlist",
                        navigation.Reason ?? "navigation blocked by policy", ct);
                await surface.NavigateAsync(url, ct);
                return RiskLevel.Safe;
            }

            case StepAction.Checkpoint:
                // no interaction: assertions + extracts do the verification
                await EnforceActionPolicyAsync(step, null, ct);
                return RiskLevel.Safe;

            case StepAction.Click:
            case StepAction.Type:
            case StepAction.Select:
            case StepAction.Read:
            {
                var target = await ResolveOrFailAsync(step, ct);
                var effectiveRisk = await EnforceActionPolicyAsync(step, target.Meta.Text, ct);
                await ConfirmRiskIfNeededAsync(step, effectiveRisk, _artifact, _options, ct);
                switch (step.Action)
                {
                    case StepAction.Click:
                        await surface.ClickAsync(target, ct);
                        break;
                    case StepAction.Type:
                        await surface.TypeAsync(target, ResolveValue(step), step.ClearFirst, ct);
                        break;
                    case StepAction.Select:
                        await surface.SelectAsync(target, ResolveValue(step), ct);
                        break;
                    case StepAction.Read:
                        var text = await surface.ReadTextAsync(target, ct);
                        if (step.ReadInto is not null) _outputs[step.ReadInto] = text;
                        break;
                }
                return effectiveRisk;
            }
        }
        throw new InvalidOperationException($"unsupported action '{step.Action}'");
    }

    private async Task<RiskLevel> EnforceActionPolicyAsync(
        StepDef step, string? targetText, CancellationToken ct)
    {
        var decision = policy.CheckAction(
            step.Action, await surface.CurrentUrlAsync(), step.Risk, targetText);
        if (decision.IsBlocked)
            throw await HardAsync(step.Id, $"policy to permit {step.Action}",
                decision.Reason ?? "action blocked by policy", ct);
        log.Log("policy_allowed", new
        {
            step = step.Id,
            action = step.Action,
            declared_risk = step.Risk,
            effective_risk = decision.EffectiveRisk,
            verdict = decision.Verdict,
        }, echo: false);
        return decision.EffectiveRisk;
    }

    private async Task<IResolvedTarget> ResolveOrFailAsync(StepDef step, CancellationToken ct)
    {
        var chain = step.Locator ?? throw new InvalidOperationException($"step {step.Id} has no locator");
        var target = await surface.ResolveAsync(
            new TargetRef { Frame = step.Frame, Locator = chain, TimeoutMs = step.TimeoutMs }, ct);
        if (target is null)
            throw await HardAsync(step.Id,
                $"one of [{string.Join(" | ", chain.Candidates.Select(c => $"{c.By}:{c.Value}"))}] to resolve in frame {FrameText(step.Frame)}",
                "no locator candidate matched a visible element", ct);
        if (target.CandidateIndex > 0)
            log.Log("locator_fallback", new { step = step.Id, used = target.Description, index = target.CandidateIndex });
        return target;
    }

    private string ResolveValue(StepDef step)
    {
        if (step.ValueRef is { } vr)
        {
            if (vr.StartsWith("inputs.", StringComparison.Ordinal))
            {
                var name = vr["inputs.".Length..];
                return _inputs.TryGetValue(name, out var v)
                    ? v
                    : throw new InvalidOperationException($"missing input '{name}' for step {step.Id}");
            }
            if (vr.StartsWith("credentials.", StringComparison.Ordinal))
            {
                var value = CredentialResolver.Resolve(_artifact.Credentials, vr["credentials.".Length..]);
                redactor.AddLiteral(value);
                return value;
            }
            throw new InvalidOperationException($"unknown value_ref '{vr}' on step {step.Id}");
        }
        return step.Value ?? "";
    }

    // ------------------------------------------------------------------ guards

    private async Task CheckGuardsAsync(StepDef step, CancellationToken ct)
    {
        foreach (var guard in _artifact.Guards)
        {
            if (!await surface.IsConditionMetAsync(guard.When, step.Frame, ct)) continue;
            log.Log("guard_triggered", new { guard = guard.Id, at_step = step.Id, response = guard.Response });

            switch (guard.Response)
            {
                case GuardResponse.RecoverAuth:
                    if (_authRecovered)
                        throw await HardAsync(step.Id, "session to stay authenticated",
                            $"guard '{guard.Id}' triggered again after one auth recovery", ct);
                    _authRecovered = true;
                    foreach (var authStep in _artifact.Steps.Where(s => s.Phase == "auth"))
                        await ExecuteStepAsync(authStep, ct);
                    log.Log("auth_recovered", new { resumed_at = step.Id });
                    return;

                case GuardResponse.Escalate:
                    await EscalateAsync(step, new AssertionDef
                    {
                        Classify = AssertionClass.Escalate,
                        When = guard.When,
                        Escalate = guard.Escalation ?? new EscalationSpec { Reason = $"guard '{guard.Id}' matched" },
                    }, ct);
                    return;

                case GuardResponse.Fail:
                    throw await HardAsync(step.Id, $"guard '{guard.Id}' not to match",
                        $"guard condition '{guard.When.Value}' matched", ct);
            }
        }
    }

    // -------------------------------------------------------------- escalation

    private async Task ConfirmRiskIfNeededAsync(
        StepDef step, RiskLevel effectiveRisk, CapabilityArtifact artifact, ReplayOptions options, CancellationToken ct)
    {
        if (effectiveRisk != RiskLevel.Irreversible) return;
        if (options.AckRisk && artifact.Provenance.Approval == "approved")
        {
            log.Log("risk_flagged", new { step = step.Id, note = "irreversible step executed under approved artifact + --ack-risk" });
            return;
        }

        var resolution = await RaiseInterventionAsync(step,
            $"irreversible step requires human confirmation (approval={artifact.Provenance.Approval}, ack_risk={options.AckRisk})",
            "confirm in the live browser is NOT needed — just review and press ENTER to authorize this step, or 'abort'", ct);
        if (!resolution.Resolved)
            throw new RunAborted(Finish(
                _intervention?.Resolution == "pending" ? RunStatus.EscalationPending : RunStatus.HardFailure,
                failure: new FailureDetail
                {
                    StepId = step.Id,
                    Expected = "human authorization for irreversible step",
                    Observed = _intervention?.Resolution == "pending"
                        ? "queued for operator; run parked as escalation_pending"
                        : "operator declined",
                }));

        // The operator authorized the step: close out the record, or the run
        // reports success while still carrying a "pending" intervention.
        _humanAssisted = true;
        _intervention = _intervention! with
        {
            ResolvedAt = DateTimeOffset.UtcNow,
            Resolution = "resolved",
            OperatorNotes = resolution.OperatorNotes ?? "authorized the irreversible step",
        };
        log.Log("risk_authorized", new { step = step.Id, notes = _intervention.OperatorNotes });
    }

    private async Task<string?> EscalateAsync(StepDef step, AssertionDef assertion, CancellationToken ct)
    {
        var spec = assertion.Escalate ?? new EscalationSpec { Reason = "escalation condition matched" };
        var resolution = await RaiseInterventionAsync(step, spec.Reason,
            spec.ResumeAt is null
                ? "on handback the current step is re-verified"
                : $"on handback the run resumes at step {spec.ResumeAt}", ct);

        if (!resolution.Resolved)
            throw new RunAborted(Finish(
                _intervention!.Resolution == "pending" ? RunStatus.EscalationPending : RunStatus.HardFailure,
                failure: _intervention.Resolution == "pending" ? null : new FailureDetail
                {
                    StepId = step.Id,
                    Expected = "operator to resolve the escalation",
                    Observed = "operator abandoned the run",
                }));

        _humanAssisted = true;
        var humanActions = await surface.DrainHumanActionsAsync();
        foreach (var action in humanActions) log.AppendJsonl("human_actions.jsonl", action);
        _intervention = _intervention! with
        {
            ResolvedAt = DateTimeOffset.UtcNow,
            Resolution = "resolved",
            OperatorNotes = resolution.OperatorNotes,
            HumanActionsPath = humanActions.Count > 0 ? "human_actions.jsonl" : null,
        };
        log.Log("intervention_resolved", new { step = step.Id, human_actions = humanActions.Count, notes = resolution.OperatorNotes });
        return spec.ResumeAt;
    }

    private async Task<InterventionResolution> RaiseInterventionAsync(
        StepDef step, string reason, string resumePlan, CancellationToken ct)
    {
        var shot = log.SaveScreenshot(await surface.ScreenshotAsync(ct), $"escalation-{step.Id}");
        var observation = await surface.ObserveAsync(ct);
        var request = new InterventionRequest
        {
            RunId = log.RunId,
            CapabilityId = _artifact.CapabilityId,
            Goal = _artifact.Goal ?? _artifact.DisplayName,
            StepId = step.Id,
            Reason = reason,
            Detail = step.Note,
            ScreenshotPath = shot,
            ObservationDigest = redactor.ApplyToDocument(observation.ToPromptText(2500)),
            ResumePlan = resumePlan,
        };
        _intervention = new InterventionRecord
        {
            Reason = reason,
            AtStepId = step.Id,
            RaisedAt = request.RaisedAt,
            Resolution = "pending",
        };
        log.Log("intervention_raised", new { step = step.Id, reason });
        var resolution = await operatorChannel.RequestInterventionAsync(request, surface.Control, ct);
        if (!resolution.Resolved && resolution.OperatorNotes?.Contains("queued") != true)
            _intervention = _intervention with { Resolution = "abandoned", OperatorNotes = resolution.OperatorNotes };
        return resolution;
    }

    // ----------------------------------------------------------------- results

    private async Task ApplyExtractsAsync(StepDef step, CancellationToken ct)
    {
        if (step.Extracts.Count == 0) return;
        var text = await surface.FrameTextAsync(step.Frame, ct);
        foreach (var extract in step.Extracts)
        {
            var match = Regex.Match(text, extract.Regex, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (match.Success)
                _outputs[extract.Name] = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            else
                log.Log("extract_miss", new { step = step.Id, extract.Name, extract.Regex });
        }
    }

    private void MergeEmit(IReadOnlyDictionary<string, string>? emit)
    {
        if (emit is null) return;
        foreach (var (k, v) in emit) _outputs[k] = v;
    }

    private ReplayResult Finish(RunStatus status, FailureDetail? failure = null) => new()
    {
        Status = status,
        CapabilityId = _artifact.CapabilityId,
        CapabilityVersion = _artifact.CapabilityVersion,
        RunId = log.RunId,
        Outputs = new Dictionary<string, string>(_outputs),
        Outcome = _outputs.GetValueOrDefault("outcome"),
        Failure = failure,
        Intervention = _intervention,
        HumanAssisted = _humanAssisted,
        EvidenceDir = log.Dir,
        StartedAt = _startedAt,
        EndedAt = DateTimeOffset.UtcNow,
    };

    private async Task<RunAborted> HardAsync(string stepId, string expected, string observed, CancellationToken ct)
    {
        return new RunAborted(await HardFailureAsync(stepId, expected, observed, ct));
    }

    private async Task<ReplayResult> HardFailureAsync(
        string stepId, string expected, string observed, CancellationToken ct)
    {
        string? shotPath = null;
        var observedDetail = observed;
        try
        {
            shotPath = log.SaveScreenshot(await surface.ScreenshotAsync(ct), $"failure-{stepId}");
            var obs = await surface.ObserveAsync(ct);
            var messages = obs.Frames
                .SelectMany(f => f.Messages.Select(m => $"[{FrameText(f.Path)}] {m.Text}"))
                .Take(8);
            var digest = string.Join(" ;; ", messages);
            if (digest.Length > 0) observedDetail = $"{observed} — on-screen: {digest}";
            log.SaveDocument($"failure-{stepId}-observation.txt", obs.ToPromptText(8000));
        }
        catch { /* evidence capture is best-effort on a broken surface */ }

        var failure = new FailureDetail
        {
            StepId = stepId,
            Expected = expected,
            Observed = redactor.ApplyToDocument(observedDetail),
            ScreenshotPath = shotPath,
        };
        log.Log("hard_failure", failure);
        return Finish(RunStatus.HardFailure, failure);
    }

    // ------------------------------------------------------------------ misc

    private static void ValidateInputs(CapabilityArtifact artifact, IReadOnlyDictionary<string, string> inputs)
    {
        foreach (var def in artifact.Inputs)
        {
            if (!inputs.TryGetValue(def.Name, out var value))
            {
                if (def.Required)
                    throw new ArgumentException($"missing required input '{def.Name}'");
                continue;
            }
            if (def.Pattern is not null && !Regex.IsMatch(value, def.Pattern))
                throw new ArgumentException($"input '{def.Name}' does not match pattern {def.Pattern}");
        }
    }

    private static int FindStepIndex(IReadOnlyList<StepDef> steps, string id)
    {
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].Id == id) return i;
        throw new InvalidOperationException($"resume_at references unknown step '{id}'");
    }

    private static string Describe(IReadOnlyList<AssertionDef> assertions) =>
        "one of: " + string.Join(" | ", assertions.Select(a => $"{a.Classify}({a.When.By}:{a.When.Value})"));

    private static string FrameText(IReadOnlyList<string> path) =>
        path.Count == 0 ? "(top)" : string.Join(">", path);
}
