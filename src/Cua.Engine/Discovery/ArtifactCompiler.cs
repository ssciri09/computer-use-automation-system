using System.Text.Json;
using System.Text.RegularExpressions;
using Cua.Core.Artifacts;
using Cua.Core.Surface;

namespace Cua.Engine.Discovery;

/// <summary>
/// Compiles a capability artifact from the two halves of a discovery run:
///   1. the mechanical trace — the actions that actually executed, with the
///      resolved element metadata (ground truth for steps and locator chains);
///   2. the model's declaration — the semantic layer (outcome classification,
///      checkpoint, outputs, auth steps) that requires judgment and is
///      declared at record time, never inferred during replay.
/// The raw transcript is evidence only; nothing in it reaches the artifact.
/// </summary>
public static class ArtifactCompiler
{
    public sealed record Input
    {
        public required DiscoveryConfig Config { get; init; }
        public required IReadOnlyList<TraceStep> Trace { get; init; }
        public required JsonElement Declaration { get; init; }
        public required IReadOnlyList<string> AllowedHosts { get; init; }
        public required string RunId { get; init; }
        public required int NextVersion { get; init; }
    }

    public const string CheckpointStepId = "checkpoint";

    public static CapabilityArtifact Compile(Input input)
    {
        var decl = input.Declaration;
        var authSteps = StringSet(decl, "auth_steps");
        var guarded = ParseGuardedSteps(decl);

        // Probe steps ground the declaration but are not part of the flow.
        // The recorded flow is the contiguous prefix up to the FIRST probe:
        // probing is only allowed after the flow is complete, and models that
        // forget the probe flag on some exploratory actions must not leak them
        // into the artifact (observed in practice: flagged probe typing
        // followed by unflagged probe clicks).
        var flow = input.Trace.TakeWhile(t => !t.Probe).ToList();
        var steps = new List<StepDef>();
        for (var i = 0; i < flow.Count; i++)
        {
            var t = flow[i];
            guarded.TryGetValue(t.Id, out var guard);
            steps.Add(CompileStep(t, guard, NextStepPrimary(flow, i, decl), authSteps, input.Config.SurfaceKind));
        }
        steps.Add(CompileCheckpoint(decl));

        var inputs = input.Config.Parameters.Keys.Select(name => new InputDef
        {
            Name = name,
            Required = true,
            Pattern = InferPattern(input.Config.Parameters[name]),
            Sensitivity = Sensitivity.Pii, // account-scoped values in a banking context: treat as PII by default
            RedactInLogs = true,
        }).ToList();

        var outputs = ParseOutputs(decl);
        var extractPatterns = ExtractRegexes(decl);

        return new CapabilityArtifact
        {
            CapabilityId = input.Config.CapabilityId,
            CapabilityVersion = input.NextVersion,
            DisplayName = decl.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? input.Config.CapabilityId : input.Config.CapabilityId,
            Description = OptStr(decl, "description"),
            Goal = input.Config.Goal,
            Vendor = input.Config.VendorProduct is null ? null : new VendorInfo { Product = input.Config.VendorProduct },
            Surface = new SurfaceInfo
            {
                Kind = input.Config.SurfaceKind,
                AppBinding = input.Config.AppBinding,
                EntryUrl = input.Config.EntryUrl,
                Allowlist = [.. input.AllowedHosts],
            },
            Inputs = inputs,
            Outputs = outputs,
            Credentials = new CredentialsRef { Ref = input.Config.CredentialsRef },
            Guards = BuildGuards(input.Trace, authSteps),
            Steps = steps,
            Redaction = new RedactionSpec { LogPatterns = extractPatterns },
            Provenance = new ProvenanceInfo
            {
                RecordedAt = DateTimeOffset.UtcNow,
                Model = input.Config.Model,
                DiscoveryRunId = input.RunId,
                Approval = "draft",
            },
        };
    }

    // ------------------------------------------------------------------ steps

    private static StepDef CompileStep(
        TraceStep t, GuardedStep? guard, ConditionDef? synthesizedSuccess, HashSet<string> authSteps,
        SurfaceKind kind)
    {
        var assertions = new List<AssertionDef>();
        if (guard is not null)
        {
            // Declared order is the evaluation order: transient conditions first
            // (they may co-exist with stale content), terminal outcomes next,
            // escalations, and success last.
            foreach (var r in guard.Recoverable)
                assertions.Add(new AssertionDef
                {
                    Classify = AssertionClass.Recoverable,
                    When = r.When,
                    Retry = new RetrySpec
                    {
                        MaxAttempts = r.MaxAttempts ?? 3,
                        BackoffMs = r.BackoffMs ?? [2000, 5000, 9000],
                    },
                    Note = r.Note,
                });
            foreach (var b in guard.BusinessOutcomes)
                assertions.Add(new AssertionDef
                {
                    Classify = AssertionClass.BusinessOutcome,
                    When = b.When,
                    Emit = b.Emit,
                    Terminal = true,
                    Note = b.Note,
                });
            foreach (var e in guard.Escalations)
                assertions.Add(new AssertionDef
                {
                    Classify = AssertionClass.Escalate,
                    When = e.When,
                    Escalate = new EscalationSpec { Reason = e.Reason, ResumeAt = e.ResumeAt ?? CheckpointStepId },
                    Note = e.Note,
                });
            var success = guard.SuccessWhen ?? synthesizedSuccess;
            if (success is not null)
                assertions.Add(new AssertionDef
                {
                    Classify = AssertionClass.Success,
                    When = success,
                    Note = guard.SuccessWhen is null
                        ? "synthesized: the next recorded step's primary target is present"
                        : null,
                });
        }

        return new StepDef
        {
            Id = t.Id,
            Action = t.Action,
            Phase = authSteps.Contains(t.Id) ? "auth" : "main",
            Frame = t.Frame,
            Locator = t.Action == StepAction.Navigate ? null : BuildChain(t, kind),
            Value = t.Value,
            ValueRef = t.ValueRef,
            ClearFirst = t.ClearFirst,
            Url = t.Url,
            WaitAfter = t.WaitAfter,
            Risk = t.Risk,
            ReadInto = t.ReadInto,
            Assertions = assertions,
            TimeoutMs = assertions.Count > 0 ? 30_000 : 15_000,
            Note = t.Note,
        };
    }

    private static StepDef CompileCheckpoint(JsonElement decl)
    {
        if (!decl.TryGetProperty("checkpoint", out var cp))
            throw new InvalidOperationException("declaration is missing the checkpoint");
        var when = ParseCondition(cp);
        var emit = ParseEmit(cp);
        var extracts = new List<ExtractDef>();
        if (cp.TryGetProperty("extracts", out var ex) && ex.ValueKind == JsonValueKind.Array)
            foreach (var e in ex.EnumerateArray())
            {
                var regex = OptStr(e, "regex");
                var name = OptStr(e, "name");
                if (regex is null || name is null) continue;
                _ = new Regex(regex); // validate at compile time, not at replay time
                extracts.Add(new ExtractDef { Name = name, Regex = regex });
            }

        return new StepDef
        {
            Id = CheckpointStepId,
            Action = StepAction.Checkpoint,
            Frame = when.Frame ?? [],
            Assertions =
            [
                new AssertionDef { Classify = AssertionClass.Success, When = when, Emit = emit },
            ],
            Extracts = extracts,
            TimeoutMs = 30_000,
            Note = "verifies the goal state and extracts declared outputs",
        };
    }

    /// <summary>
    /// Ranked chain from resolution-time ground truth:
    /// id → name attribute → visible text (scoped) → frame-relative coordinates,
    /// with the model's original locator kept when it adds anything.
    /// </summary>
    private static LocatorChain BuildChain(TraceStep t, SurfaceKind kind)
    {
        var candidates = new List<Locator>();
        var meta = t.Meta;
        if (meta?.Id is { Length: > 0 } id)
            candidates.Add(new Locator { By = LocatorKind.Css, Value = $"#{id}" });
        if (meta?.Name is { Length: > 0 } name)
            candidates.Add(new Locator { By = LocatorKind.Css, Value = $"{meta.Tag}[name='{name}']" });
        if (t.Action == StepAction.Click && meta?.Text is { Length: > 0 and <= 40 } text)
        {
            var within = meta.Classes?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
                ? $"{meta.Tag}.{first}"
                : null;
            candidates.Add(new Locator { By = LocatorKind.Text, Value = text, Within = within });
        }
        if (t.ModelLocator is { } ml && !candidates.Any(c => c.By == ml.By && c.Value == ml.Value))
            candidates.Add(ml);
        if (meta is { X: not null, Y: not null })
            candidates.Add(new Locator
            {
                By = LocatorKind.Coords,
                Value = $"{meta.X:F0},{meta.Y:F0}",
            });
        if (candidates.Count == 0 && t.ModelLocator is { } fallback)
            candidates.Add(fallback);

        return new LocatorChain
        {
            Candidates = candidates,
            Robustness = LocatorMapping.RobustnessNote(kind),
        };
    }

    /// <summary>Success condition synthesized for guarded steps that declared none: the next actionable step's primary target being present is proof the flow may proceed.</summary>
    private static ConditionDef? NextStepPrimary(IReadOnlyList<TraceStep> trace, int index, JsonElement decl)
    {
        for (var j = index + 1; j < trace.Count; j++)
        {
            var next = trace[j];
            if (next.Meta?.Id is { Length: > 0 } id)
                return new ConditionDef { By = ConditionKind.Css, Value = $"#{id}", Frame = next.Frame };
        }
        // last step before checkpoint: the checkpoint condition itself
        return decl.TryGetProperty("checkpoint", out var cp) ? ParseCondition(cp) : null;
    }

    private static IReadOnlyList<GuardDef> BuildGuards(IReadOnlyList<TraceStep> trace, HashSet<string> authSteps)
    {
        // Session-expiry guard, mechanically derived: if the field the recorded
        // credentials went into reappears mid-run, the session was lost — rerun
        // the auth phase once, then resume.
        if (authSteps.Count == 0) return [];
        var userStep = trace.FirstOrDefault(t => !t.Probe && t.ValueRef == "credentials.username");
        if (userStep?.Meta is null) return [];
        var value = userStep.Meta.Id is { Length: > 0 } id
            ? $"#{id}"
            : $"{userStep.Meta.Tag}[name='{userStep.Meta.Name}']";
        return
        [
            new GuardDef
            {
                Id = "session-expired",
                When = new ConditionDef { By = ConditionKind.Css, Value = value, Frame = userStep.Frame },
                Response = GuardResponse.RecoverAuth,
                Note = "the sign-on field reappeared mid-run: session expired; re-run the auth phase once and resume",
            },
        ];
    }

    // ------------------------------------------------------------ declaration

    private sealed record GuardedStep
    {
        public ConditionDef? SuccessWhen { get; init; }
        public IReadOnlyList<(ConditionDef When, IReadOnlyDictionary<string, string> Emit, string? Note)> BusinessOutcomes { get; init; } = [];
        public IReadOnlyList<(ConditionDef When, int? MaxAttempts, IReadOnlyList<int>? BackoffMs, string? Note)> Recoverable { get; init; } = [];
        public IReadOnlyList<(ConditionDef When, string Reason, string? ResumeAt, string? Note)> Escalations { get; init; } = [];
    }

    private static Dictionary<string, GuardedStep> ParseGuardedSteps(JsonElement decl)
    {
        var result = new Dictionary<string, GuardedStep>();
        if (!decl.TryGetProperty("guarded_steps", out var gs) || gs.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var g in gs.EnumerateArray())
        {
            var afterStep = OptStr(g, "after_step");
            if (afterStep is null) continue;

            List<(ConditionDef, IReadOnlyDictionary<string, string>, string?)> biz = [];
            if (g.TryGetProperty("business_outcomes", out var bs) && bs.ValueKind == JsonValueKind.Array)
                foreach (var b in bs.EnumerateArray())
                    if (b.TryGetProperty("when", out var bw))
                        biz.Add((ParseCondition(bw), ParseEmit(b) ?? new Dictionary<string, string>(), OptStr(b, "note")));

            List<(ConditionDef, int?, IReadOnlyList<int>?, string?)> rec = [];
            if (g.TryGetProperty("recoverable", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                    if (r.TryGetProperty("when", out var rw))
                        rec.Add((ParseCondition(rw),
                            r.TryGetProperty("max_attempts", out var ma) && ma.ValueKind == JsonValueKind.Number ? ma.GetInt32() : null,
                            r.TryGetProperty("backoff_ms", out var bo) && bo.ValueKind == JsonValueKind.Array
                                ? [.. bo.EnumerateArray().Select(x => x.GetInt32())] : null,
                            OptStr(r, "note")));

            List<(ConditionDef, string, string?, string?)> esc = [];
            if (g.TryGetProperty("escalate", out var es) && es.ValueKind == JsonValueKind.Array)
                foreach (var e in es.EnumerateArray())
                    if (e.TryGetProperty("when", out var ew))
                        esc.Add((ParseCondition(ew), OptStr(e, "reason") ?? "escalation condition matched",
                            OptStr(e, "resume_at"), OptStr(e, "note")));

            result[afterStep] = new GuardedStep
            {
                SuccessWhen = g.TryGetProperty("success_when", out var sw) && sw.ValueKind == JsonValueKind.Object
                    ? ParseCondition(sw) : null,
                BusinessOutcomes = biz,
                Recoverable = rec,
                Escalations = esc,
            };
        }
        return result;
    }

    private static ConditionDef ParseCondition(JsonElement el)
    {
        var by = OptStr(el, "by") switch
        {
            "text_contains" => ConditionKind.TextContains,
            "text_equals" => ConditionKind.TextEquals,
            "url_contains" => ConditionKind.UrlContains,
            _ => ConditionKind.Css,
        };
        IReadOnlyList<string>? frame = null;
        if (el.TryGetProperty("frame_path", out var fp) && fp.ValueKind == JsonValueKind.Array)
            frame = [.. fp.EnumerateArray().Select(e => e.GetString() ?? "")];
        return new ConditionDef { By = by, Value = OptStr(el, "value") ?? "", Frame = frame };
    }

    private static IReadOnlyDictionary<string, string>? ParseEmit(JsonElement el)
    {
        if (!el.TryGetProperty("emit", out var em) || em.ValueKind != JsonValueKind.Object) return null;
        return em.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
    }

    private static IReadOnlyList<OutputDef> ParseOutputs(JsonElement decl)
    {
        var outputs = new List<OutputDef>();
        if (decl.TryGetProperty("outputs", out var os) && os.ValueKind == JsonValueKind.Array)
            foreach (var o in os.EnumerateArray())
            {
                var name = OptStr(o, "name");
                if (name is null) continue;
                outputs.Add(new OutputDef
                {
                    Name = name,
                    Type = OptStr(o, "type") ?? "string",
                    EnumValues = o.TryGetProperty("enum_values", out var ev) && ev.ValueKind == JsonValueKind.Array
                        ? [.. ev.EnumerateArray().Select(v => v.GetString() ?? "")] : null,
                });
            }
        return outputs;
    }

    private static IReadOnlyList<string> ExtractRegexes(JsonElement decl)
    {
        // confirmation-id patterns double as log redaction patterns: outputs go
        // to the caller, not into logs
        var patterns = new List<string>();
        if (decl.TryGetProperty("checkpoint", out var cp) &&
            cp.TryGetProperty("extracts", out var ex) && ex.ValueKind == JsonValueKind.Array)
            foreach (var e in ex.EnumerateArray())
                if (OptStr(e, "regex") is { } r) patterns.Add(r);
        return patterns;
    }

    private static string? InferPattern(string sampleValue) =>
        Regex.IsMatch(sampleValue, @"^\d+$") ? $"^[0-9]{{{Math.Max(1, sampleValue.Length - 2)},{sampleValue.Length + 4}}}$" : null;

    private static HashSet<string> StringSet(JsonElement el, string prop)
    {
        var set = new HashSet<string>();
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.GetString() is { } s) set.Add(s);
        return set;
    }

    private static string? OptStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
