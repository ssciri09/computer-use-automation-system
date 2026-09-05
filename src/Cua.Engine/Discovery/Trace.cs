using Cua.Core.Artifacts;
using Cua.Core.Surface;

namespace Cua.Engine.Discovery;

/// <summary>
/// Ground truth from a discovery run: one entry per executed action, carrying
/// both the model's locator hint and the metadata of the element that actually
/// resolved. The compiler builds artifact steps from this — never from the raw
/// model transcript.
/// </summary>
public sealed record TraceStep
{
    public required string Id { get; init; }
    public required StepAction Action { get; init; }
    public IReadOnlyList<string> Frame { get; init; } = [];
    public Locator? ModelLocator { get; init; }
    public ElementMeta? Meta { get; init; }
    /// <summary>What was typed/selected, after credential substitution markers were applied (never a resolved secret).</summary>
    public string? Value { get; init; }
    /// <summary>inputs.* / credentials.* when the value was recognized as parameter or credential.</summary>
    public string? ValueRef { get; init; }
    public bool ClearFirst { get; init; } = true;
    public string? Url { get; init; }
    public WaitSpec? WaitAfter { get; set; }
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public string? ReadInto { get; init; }
    public string? Note { get; init; }
    /// <summary>Exploratory action used to observe an outcome state; excluded from the compiled flow.</summary>
    public bool Probe { get; init; }
}
