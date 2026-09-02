namespace Cua.Core.Artifacts;

/// <summary>
/// A capability artifact: the typed, versioned, reviewable contract that a
/// successful discovery run compiles down to, and that deterministic replay
/// executes. The LLM discovers, the artifact is the reusable capability,
/// deterministic replay is how it is invoked in production.
/// </summary>
public sealed record CapabilityArtifact
{
    public string SchemaVersion { get; init; } = "1.0";
    public required string CapabilityId { get; init; }
    public required int CapabilityVersion { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    /// <summary>The original natural-language goal, kept for reviewers.</summary>
    public string? Goal { get; init; }

    public VendorInfo? Vendor { get; init; }
    public TenantInfo Tenant { get; init; } = new();
    public required SurfaceInfo Surface { get; init; }

    public IReadOnlyList<InputDef> Inputs { get; init; } = [];
    public IReadOnlyList<OutputDef> Outputs { get; init; } = [];
    public CredentialsRef? Credentials { get; init; }

    /// <summary>Conditions checked before every step (unexpected dialogs, session expiry).</summary>
    public IReadOnlyList<GuardDef> Guards { get; init; } = [];
    public required IReadOnlyList<StepDef> Steps { get; init; }

    public RedactionSpec Redaction { get; init; } = new();
    public ProvenanceInfo Provenance { get; init; } = new();
}

public sealed record VendorInfo
{
    public required string Product { get; init; }
    public string? VersionRange { get; init; }
}

/// <summary>
/// Multi-tenant seam: an artifact is recorded against a base tenant; other
/// tenants running the same vendor product reference the base artifact and
/// carry only overrides (locator substitutions, entry URL, cosmetic drift).
/// </summary>
public sealed record TenantInfo
{
    public string RecordedFor { get; init; } = "tenant_default";
    /// <summary>Per-tenant specialization, keyed by step id (locator/value overrides).</summary>
    public IReadOnlyDictionary<string, string> Overrides { get; init; } =
        new Dictionary<string, string>();
}

public sealed record SurfaceInfo
{
    /// <summary>web | legacy_web | desktop — selects the perception/action adapter.</summary>
    public string Kind { get; init; } = "web";
    public required string EntryUrl { get; init; }
    public required IReadOnlyList<string> Allowlist { get; init; }
}

public enum Sensitivity { None, Pii, Secret }

public sealed record InputDef
{
    public required string Name { get; init; }
    public string Type { get; init; } = "string";
    public bool Required { get; init; } = true;
    public string? Pattern { get; init; }
    public Sensitivity Sensitivity { get; init; } = Sensitivity.None;
    public bool RedactInLogs { get; init; }
}

public sealed record OutputDef
{
    public required string Name { get; init; }
    public string Type { get; init; } = "string";
    public IReadOnlyList<string>? EnumValues { get; init; }
    public bool Required { get; init; }
}

/// <summary>
/// Credentials are never stored in the artifact — only a reference that the
/// executing runtime resolves (env:// here; vault:// in production).
/// </summary>
public sealed record CredentialsRef
{
    public required string Ref { get; init; }
}

public enum StepAction { Navigate, Click, Type, Select, Read, Checkpoint }

public enum RiskLevel { Safe, Write, Irreversible }

public sealed record StepDef
{
    public required string Id { get; init; }
    public required StepAction Action { get; init; }
    /// <summary>Steps in the "auth" phase can be re-run by the session-expiry guard.</summary>
    public string Phase { get; init; } = "main";
    /// <summary>Frame name path from the top document. Empty = top document. Frame paths are part of the locator, not ambient state.</summary>
    public IReadOnlyList<string> Frame { get; init; } = [];
    public LocatorChain? Locator { get; init; }
    /// <summary>Literal value for type/select (only when not parameterized).</summary>
    public string? Value { get; init; }
    /// <summary>"inputs.name" or "credentials.field" — substituted at replay time, never stored resolved.</summary>
    public string? ValueRef { get; init; }
    public bool ClearFirst { get; init; } = true;
    /// <summary>For navigate steps.</summary>
    public string? Url { get; init; }
    /// <summary>Wait applied after the action (e.g. busy spinner must disappear).</summary>
    public WaitSpec? WaitAfter { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    /// <summary>For read steps: the output name the element text is captured into.</summary>
    public string? ReadInto { get; init; }
    /// <summary>Declared outcome classification, evaluated in order after the action.</summary>
    public IReadOnlyList<AssertionDef> Assertions { get; init; } = [];
    /// <summary>Regex extractions over the frame text (checkpoint outputs).</summary>
    public IReadOnlyList<ExtractDef> Extracts { get; init; } = [];
    public int TimeoutMs { get; init; } = 15_000;
    public string? Note { get; init; }
}

public enum LocatorKind { Css, Text, Xpath, Coords }

public sealed record Locator
{
    public required LocatorKind By { get; init; }
    public required string Value { get; init; }
    /// <summary>Optional CSS scope for text locators (e.g. "span.x-btn-text").</summary>
    public string? Within { get; init; }
}

/// <summary>
/// Ranked fallback chain. Auto-generated ids on legacy apps (ext-genNN) churn
/// between vendor releases, so replay tries: id → stable attribute → text → coordinates.
/// </summary>
public sealed record LocatorChain
{
    public required IReadOnlyList<Locator> Candidates { get; init; }
    /// <summary>Why the chain is shaped this way — reviewable robustness reasoning.</summary>
    public string? Robustness { get; init; }
}

public enum WaitState { Visible, Absent }

public sealed record WaitSpec
{
    public required Locator Locator { get; init; }
    /// <summary>Frame path for the wait; null = the step's frame.</summary>
    public IReadOnlyList<string>? Frame { get; init; }
    public WaitState State { get; init; } = WaitState.Visible;
    public int TimeoutMs { get; init; } = 30_000;
}

/// <summary>
/// The outcome taxonomy. There is deliberately no "hard_failure" class: a hard
/// failure is what replay reports when NO declared condition matches within the
/// step timeout — the absence of a recognized outcome, never a pattern match.
/// </summary>
public enum AssertionClass { Success, BusinessOutcome, Recoverable, Escalate }

public enum ConditionKind { Css, TextContains, TextEquals, UrlContains }

public sealed record ConditionDef
{
    public required ConditionKind By { get; init; }
    public required string Value { get; init; }
    /// <summary>Frame path the condition is evaluated in; null = the step's frame. Escalation modals often escape to the top frame — the frame is part of the assertion.</summary>
    public IReadOnlyList<string>? Frame { get; init; }
}

public sealed record RetrySpec
{
    public int MaxAttempts { get; init; } = 3;
    public IReadOnlyList<int> BackoffMs { get; init; } = [2000, 5000, 9000];
}

public sealed record EscalationSpec
{
    public required string Reason { get; init; }
    /// <summary>Step id to resume at once the human hands control back; null = re-evaluate the current step.</summary>
    public string? ResumeAt { get; init; }
}

public sealed record AssertionDef
{
    public required AssertionClass Classify { get; init; }
    public required ConditionDef When { get; init; }
    /// <summary>Outputs to set when this classification matches (e.g. outcome=not_permitted).</summary>
    public IReadOnlyDictionary<string, string>? Emit { get; init; }
    /// <summary>Business outcomes are terminal by default: the run ends, cleanly, with that outcome.</summary>
    public bool Terminal { get; init; }
    public RetrySpec? Retry { get; init; }
    public EscalationSpec? Escalate { get; init; }
    public string? Note { get; init; }
}

public sealed record ExtractDef
{
    public required string Name { get; init; }
    public required string Regex { get; init; }
}

public enum GuardResponse { Escalate, RecoverAuth, Fail }

public sealed record GuardDef
{
    public required string Id { get; init; }
    public required ConditionDef When { get; init; }
    public required GuardResponse Response { get; init; }
    public EscalationSpec? Escalation { get; init; }
    public string? Note { get; init; }
}

public sealed record RedactionSpec
{
    /// <summary>Regexes scrubbed from every persisted log line and transcript.</summary>
    public IReadOnlyList<string> LogPatterns { get; init; } = [];
}

public sealed record ProvenanceInfo
{
    public DateTimeOffset? RecordedAt { get; init; }
    public string? Model { get; init; }
    public string? DiscoveryRunId { get; init; }
    /// <summary>draft → approved: unattended replay of irreversible steps is gated on approval.</summary>
    public string Approval { get; init; } = "draft";
}
