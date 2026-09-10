using Cua.Core.Artifacts;
using Cua.Core.Hitl;
using Cua.Core.Surface;

namespace Cua.Tests;

/// <summary>
/// Scripted in-memory surface: "present" element keys drive condition checks,
/// click handlers mutate state, frame texts feed extraction. Lets replay-engine
/// semantics (retry, outcome taxonomy, escalation, guards) be tested without a
/// browser.
/// </summary>
public sealed class FakeSurface : ISurface
{
    public SessionControl Control { get; } = new();

    /// <summary>Keys: "framePath|cssSelector" with framePath "a>b" ("" = top).</summary>
    public HashSet<string> Present { get; } = [];
    public Dictionary<string, string> FrameTexts { get; } = [];
    public Dictionary<string, Action<FakeSurface, int>> ClickHandlers { get; } = [];
    public Dictionary<string, int> ClickCounts { get; } = [];
    public List<string> ActionLog { get; } = [];
    public List<HumanAction> PendingHumanActions { get; } = [];
    public Exception? NavigateError { get; set; }

    public static string Key(IReadOnlyList<string> frame, string css) =>
        string.Join(">", frame) + "|" + css;

    public Task NavigateAsync(string url, CancellationToken ct)
    {
        if (NavigateError is not null) throw NavigateError;
        ActionLog.Add($"navigate:{url}");
        return Task.CompletedTask;
    }

    public Task<string> CurrentUrlAsync() => Task.FromResult("http://127.0.0.1:8080/");

    public Task<SurfaceObservation> ObserveAsync(CancellationToken ct) =>
        Task.FromResult(new SurfaceObservation { Url = "http://fake/", Frames = [] });

    public Task<IResolvedTarget?> ResolveAsync(TargetRef target, CancellationToken ct)
    {
        var primary = target.Locator.Candidates[0];
        if (primary.By == LocatorKind.Css && !Present.Contains(Key(target.Frame, primary.Value)))
        {
            // try fallbacks: any candidate whose key is present
            var hit = target.Locator.Candidates
                .Select((c, i) => (c, i))
                .FirstOrDefault(x => x.c.By == LocatorKind.Css && Present.Contains(Key(target.Frame, x.c.Value)));
            if (hit.c is null) return Task.FromResult<IResolvedTarget?>(null);
            return Task.FromResult<IResolvedTarget?>(new FakeTarget(hit.c.Value, hit.i));
        }
        return Task.FromResult<IResolvedTarget?>(new FakeTarget(primary.Value, 0));
    }

    public Task ClickAsync(IResolvedTarget target, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var key = ((FakeTarget)target).Selector;
        var count = ClickCounts.GetValueOrDefault(key) + 1;
        ClickCounts[key] = count;
        ActionLog.Add($"click:{key}#{count}");
        if (ClickHandlers.TryGetValue(key, out var handler)) handler(this, count);
        return Task.CompletedTask;
    }

    public Task TypeAsync(IResolvedTarget target, string text, bool clearFirst, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        ActionLog.Add($"type:{((FakeTarget)target).Selector}={text}");
        return Task.CompletedTask;
    }

    public Task SelectAsync(IResolvedTarget target, string value, CancellationToken ct)
    {
        ActionLog.Add($"select:{((FakeTarget)target).Selector}={value}");
        return Task.CompletedTask;
    }

    public Task<string> ReadTextAsync(IResolvedTarget target, CancellationToken ct) =>
        Task.FromResult("read-value");

    public Task<bool> WaitForAsync(IReadOnlyList<string> framePath, Locator locator, WaitState state, int timeoutMs, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<bool> IsConditionMetAsync(ConditionDef condition, IReadOnlyList<string> defaultFrame, CancellationToken ct)
    {
        var frame = condition.Frame ?? defaultFrame;
        var path = string.Join(">", frame);
        return Task.FromResult(condition.By switch
        {
            ConditionKind.Css => Present.Contains(path + "|" + condition.Value),
            ConditionKind.TextContains => FrameTexts.GetValueOrDefault(path, "")
                .Contains(condition.Value, StringComparison.OrdinalIgnoreCase),
            ConditionKind.TextEquals => FrameTexts.GetValueOrDefault(path, "").Split('\n')
                .Any(l => l.Trim().Equals(condition.Value.Trim(), StringComparison.OrdinalIgnoreCase)),
            _ => false,
        });
    }

    public Task<string> FrameTextAsync(IReadOnlyList<string> framePath, CancellationToken ct) =>
        Task.FromResult(FrameTexts.GetValueOrDefault(string.Join(">", framePath), ""));

    public Task<byte[]> ScreenshotAsync(CancellationToken ct) => Task.FromResult(Array.Empty<byte>());

    public Task<IReadOnlyList<HumanAction>> DrainHumanActionsAsync()
    {
        var copy = PendingHumanActions.ToList();
        PendingHumanActions.Clear();
        return Task.FromResult<IReadOnlyList<HumanAction>>(copy);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record FakeTarget(string Selector, int CandidateIndex) : IResolvedTarget
    {
        public ElementMeta Meta { get; } = new() { Tag = "div", Text = Selector };
        public string Description => Selector;
    }
}

public sealed class FakeOperatorChannel(
    Func<InterventionRequest, InterventionResolution> handler,
    Action? beforeResolve = null) : IOperatorChannel
{
    public List<InterventionRequest> Requests { get; } = [];

    public Task<InterventionResolution> RequestInterventionAsync(
        InterventionRequest request, SessionControl control, CancellationToken ct)
    {
        Requests.Add(request);
        control.TransferTo(ControlHolder.Human);
        beforeResolve?.Invoke();
        control.TransferTo(ControlHolder.Automation);
        return Task.FromResult(handler(request));
    }
}
