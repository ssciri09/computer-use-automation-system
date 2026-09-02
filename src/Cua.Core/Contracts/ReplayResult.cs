namespace Cua.Core.Contracts;

/// <summary>
/// The result contract every caller (an AI agent in production) receives.
/// The taxonomy separates what callers must never conflate:
///   Success            — checkpoint verified, declared outputs returned.
///   BusinessOutcome    — the app legitimately said no ("no such member",
///                        "account closed"). A result, not a crash.
///   EscalationPending  — a human intervention was raised but not resolved
///                        (unattended mode); the session context is preserved.
///   HardFailure        — replay could not recognize the state it reached;
///                        carries step, expected vs observed, and evidence.
/// A run resolved live by a human operator completes as Success/BusinessOutcome
/// with <see cref="ReplayResult.HumanAssisted"/> set and the intervention recorded.
/// </summary>
public enum RunStatus { Success, BusinessOutcome, EscalationPending, HardFailure }

public sealed record FailureDetail
{
    public required string StepId { get; init; }
    public required string Expected { get; init; }
    public required string Observed { get; init; }
    public string? ScreenshotPath { get; init; }
}

public sealed record InterventionRecord
{
    public required string Reason { get; init; }
    public required string AtStepId { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? OperatorNotes { get; init; }
    /// <summary>Evidence file with the recorded human actions taken during takeover.</summary>
    public string? HumanActionsPath { get; init; }
    /// <summary>resolved | abandoned | pending</summary>
    public required string Resolution { get; init; }
}

public sealed record ReplayResult
{
    public required RunStatus Status { get; init; }
    public required string CapabilityId { get; init; }
    public required int CapabilityVersion { get; init; }
    public required string RunId { get; init; }
    /// <summary>Declared outputs (only meaningful for Success/BusinessOutcome).</summary>
    public IReadOnlyDictionary<string, string> Outputs { get; init; } =
        new Dictionary<string, string>();
    /// <summary>Convenience mirror of Outputs["outcome"] when declared.</summary>
    public string? Outcome { get; init; }
    public FailureDetail? Failure { get; init; }
    public InterventionRecord? Intervention { get; init; }
    public bool HumanAssisted { get; init; }
    public required string EvidenceDir { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public long DurationMs => (long)(EndedAt - StartedAt).TotalMilliseconds;
}

/// <summary>Result of a discovery run (the LLM-driven recording pass).</summary>
public sealed record DiscoveryResult
{
    public required bool Succeeded { get; init; }
    public string? ArtifactPath { get; init; }
    public string? FailureReason { get; init; }
    public required string RunId { get; init; }
    public required string EvidenceDir { get; init; }
    public int Steps { get; init; }
    public int ModelTurns { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
}
