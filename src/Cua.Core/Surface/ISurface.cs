using Cua.Core.Artifacts;
using Cua.Core.Hitl;

namespace Cua.Core.Surface;

/// <summary>
/// The surface seam. Everything above this interface (discovery loop, replay
/// engine, policy, artifacts) is surface-neutral: it speaks in frame paths,
/// locator chains, and observations. A web implementation resolves those with
/// a browser; a desktop implementation would resolve them with an accessibility
/// tree (frame path → window/pane path, css id → automation id). The recorded
/// flow never references browser concepts directly.
/// </summary>
public interface ISurface : IAsyncDisposable
{
    SessionControl Control { get; }

    Task NavigateAsync(string url, CancellationToken ct);
    Task<string> CurrentUrlAsync();

    /// <summary>Digest of the current UI state across all frames — what the model "sees".</summary>
    Task<SurfaceObservation> ObserveAsync(CancellationToken ct);

    /// <summary>
    /// Resolve a locator chain in a frame: candidates are tried in order until
    /// one matches a visible element or the deadline passes. Returns null when
    /// nothing resolves — the caller decides what that means.
    /// </summary>
    Task<IResolvedTarget?> ResolveAsync(TargetRef target, CancellationToken ct);

    Task ClickAsync(IResolvedTarget target, CancellationToken ct);
    Task TypeAsync(IResolvedTarget target, string text, bool clearFirst, CancellationToken ct);
    Task SelectAsync(IResolvedTarget target, string value, CancellationToken ct);
    Task<string> ReadTextAsync(IResolvedTarget target, CancellationToken ct);

    /// <summary>Wait for an element state (visible / absent). Absent waits are how slow host calls are sequenced.</summary>
    Task<bool> WaitForAsync(IReadOnlyList<string> framePath, Locator locator, WaitState state, int timeoutMs, CancellationToken ct);

    /// <summary>Single-shot condition check (no polling) — used by assertions and guards.</summary>
    Task<bool> IsConditionMetAsync(ConditionDef condition, IReadOnlyList<string> defaultFrame, CancellationToken ct);

    /// <summary>Full visible text of a frame — used for extraction regexes and failure diagnostics.</summary>
    Task<string> FrameTextAsync(IReadOnlyList<string> framePath, CancellationToken ct);

    Task<byte[]> ScreenshotAsync(CancellationToken ct);

    /// <summary>Drain the buffer of recorded human actions (populated during a takeover).</summary>
    Task<IReadOnlyList<HumanAction>> DrainHumanActionsAsync();
}

/// <summary>A locator chain plus the frame it lives in — the unit replay acts on.</summary>
public sealed record TargetRef
{
    public required IReadOnlyList<string> Frame { get; init; }
    public required LocatorChain Locator { get; init; }
    public int TimeoutMs { get; init; } = 15_000;
}

/// <summary>
/// Opaque handle to a resolved element. Metadata is captured at resolution time
/// so discovery can compile robust fallback chains from ground truth.
/// </summary>
public interface IResolvedTarget
{
    ElementMeta Meta { get; }
    /// <summary>Which candidate in the chain resolved (0 = primary).</summary>
    int CandidateIndex { get; }
    string Description { get; }
}

public sealed record ElementMeta
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public required string Tag { get; init; }
    public string? Text { get; init; }
    public string? Classes { get; init; }
    /// <summary>data-testid / data-test-id — put there deliberately for automation; the most stable rank when present.</summary>
    public string? TestId { get; init; }
    /// <summary>Accessible name (aria-label / labelled control), stable across restyles.</summary>
    public string? AriaLabel { get; init; }
    /// <summary>Explicit or implicit ARIA role, for role+name targeting.</summary>
    public string? Role { get; init; }
    /// <summary>Frame-relative center point, for coordinate fallback locators.</summary>
    public double? X { get; init; }
    public double? Y { get; init; }
}

public sealed record FrameObservation
{
    public required IReadOnlyList<string> Path { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<ObservedElement> Fields { get; init; } = [];
    public IReadOnlyList<ObservedElement> Controls { get; init; } = [];
    public IReadOnlyList<ObservedMessage> Messages { get; init; } = [];
    public IReadOnlyList<ObservedTable> Tables { get; init; } = [];
}

public sealed record ObservedElement
{
    public required string Tag { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? Value { get; init; }
    public string? InputType { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
}

public sealed record ObservedMessage
{
    public string? Id { get; init; }
    public string? Classes { get; init; }
    public required string Text { get; init; }
}

public sealed record ObservedTable
{
    public string? Id { get; init; }
    public IReadOnlyList<string> Rows { get; init; } = [];
}

public sealed record SurfaceObservation
{
    public required string Url { get; init; }
    public required IReadOnlyList<FrameObservation> Frames { get; init; }

    /// <summary>Compact, token-frugal rendering for the model and for evidence digests.</summary>
    public string ToPromptText(int maxChars = 6000)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"URL: {Url}");
        foreach (var f in Frames)
        {
            var path = f.Path.Count == 0 ? "(top)" : string.Join(">", f.Path);
            sb.AppendLine($"== frame {path} {(f.Title is null ? "" : $"\"{f.Title}\"")}");
            foreach (var m in f.Messages)
                sb.AppendLine($"  msg [{m.Id ?? m.Classes}] {m.Text}");
            foreach (var e in f.Fields)
            {
                var opts = e.Options is { Count: > 0 } ? $" options=[{string.Join(",", e.Options)}]" : "";
                sb.AppendLine($"  field <{e.Tag}{(e.InputType is null ? "" : $" type={e.InputType}")}> id={e.Id} name={e.Name} value=\"{e.Value}\"{opts}");
            }
            foreach (var e in f.Controls)
                sb.AppendLine($"  control <{e.Tag}> id={e.Id} text=\"{e.Text}\"");
            foreach (var t in f.Tables)
            {
                sb.AppendLine($"  table id={t.Id}");
                foreach (var r in t.Rows) sb.AppendLine($"    | {r}");
            }
        }
        var s = sb.ToString();
        return s.Length <= maxChars ? s : s[..maxChars] + "\n…(truncated)";
    }
}

/// <summary>One recorded action a human took during a takeover.</summary>
public sealed record HumanAction
{
    public required string Kind { get; init; }
    public string? Frame { get; init; }
    public string? Tag { get; init; }
    public string? Id { get; init; }
    public string? Text { get; init; }
    /// <summary>Never populated for password/secret fields.</summary>
    public string? Value { get; init; }
    public DateTimeOffset At { get; init; }
}
