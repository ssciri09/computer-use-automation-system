using System.Drawing.Imaging;
using Cua.Core.Artifacts;
using Cua.Core.Hitl;
using Cua.Core.Surface;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Application = FlaUI.Core.Application;

namespace Cua.Desktop;

/// <summary>
/// Native-desktop ISurface. Artifact locators are unchanged: css "#id" maps to
/// AutomationId, name attributes and text locators map to Name, coords stay
/// clickable points. Frame paths are window/pane names.
/// </summary>
public sealed class DesktopSurface : ISurface
{
    private readonly UIA3Automation _automation = new();
    private Application? _app;
    private AutomationElement? _root;
    private string _identity = "desktop";

    public SessionControl Control { get; } = new();

    public static DesktopSurface Create() => new();

    public Task NavigateAsync(string url, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var target = SurfaceTarget.Parse(url);
        _identity = target.Identity;
        _app?.Dispose();
        _app = null;

        if (target.ProcessId is { } pid)
        {
            _app = Application.Attach(pid);
            _root = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20));
        }
        else if (target.IsLaunch && target.Executable is not null)
        {
            _app = string.IsNullOrWhiteSpace(target.Arguments)
                ? Application.Launch(target.Executable)
                : Application.Launch(target.Executable, target.Arguments);
            _root = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20));
        }
        else
        {
            var title = target.WindowTitle ?? target.Identity;
            _root = _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(title))
                ?? throw new InvalidOperationException($"no window named '{title}' to attach");
        }

        return Task.CompletedTask;
    }

    public Task<string> CurrentUrlAsync() => Task.FromResult($"app://{_identity}");

    public Task<SurfaceObservation> ObserveAsync(CancellationToken ct)
    {
        var root = RequireRoot();
        var frames = new List<FrameObservation>();
        Collect(root, [], frames, depth: 0);
        return Task.FromResult(new SurfaceObservation { Url = $"app://{_identity}", Frames = frames });
    }

    public Task<IResolvedTarget?> ResolveAsync(TargetRef target, CancellationToken ct)
    {
        var scope = Scope(target.Frame);
        if (scope is null) return Task.FromResult<IResolvedTarget?>(null);

        for (var i = 0; i < target.Locator.Candidates.Count; i++)
        {
            var hit = TryCandidate(scope, target.Locator.Candidates[i], i);
            if (hit is not null) return Task.FromResult<IResolvedTarget?>(hit);
        }
        return Task.FromResult<IResolvedTarget?>(null);
    }

    public Task ClickAsync(IResolvedTarget target, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var el = ((DesktopTarget)target).Element;
        if (el is null)
        {
            var (x, y) = ((DesktopTarget)target).Point
                ?? throw new InvalidOperationException("desktop click has no element or point");
            Mouse.Click(new System.Drawing.Point((int)x, (int)y));
            return Task.CompletedTask;
        }
        el.Click();
        return Task.CompletedTask;
    }

    public Task TypeAsync(IResolvedTarget target, string text, bool clearFirst, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var el = ((DesktopTarget)target).Element
            ?? throw new InvalidOperationException("desktop type requires a resolved element");
        el.Focus();
        if (el.Patterns.Value.IsSupported)
        {
            var next = clearFirst ? text : (el.Patterns.Value.Pattern.Value ?? "") + text;
            el.Patterns.Value.Pattern.SetValue(next);
        }
        else
        {
            if (clearFirst) el.Patterns.LegacyIAccessible.Pattern.Select(0);
            Keyboard.Type(text);
        }
        return Task.CompletedTask;
    }

    public Task SelectAsync(IResolvedTarget target, string value, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var el = ((DesktopTarget)target).Element?.AsComboBox()
            ?? throw new InvalidOperationException("desktop select requires a combo box");
        el.Select(value);
        return Task.CompletedTask;
    }

    public Task<string> ReadTextAsync(IResolvedTarget target, CancellationToken ct)
    {
        var el = ((DesktopTarget)target).Element;
        return Task.FromResult(el is null ? "" : NameOf(el));
    }

    public async Task<bool> WaitForAsync(
        IReadOnlyList<string> framePath, Locator locator, WaitState state, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var hit = await ResolveAsync(new TargetRef
            {
                Frame = framePath,
                Locator = new LocatorChain { Candidates = [locator] },
                TimeoutMs = 250,
            }, ct);
            var visible = hit is not null;
            if (state == WaitState.Visible && visible) return true;
            if (state == WaitState.Absent && !visible) return true;
            await Task.Delay(150, ct);
        }
        return false;
    }

    public Task<bool> IsConditionMetAsync(ConditionDef condition, IReadOnlyList<string> defaultFrame, CancellationToken ct)
    {
        var frame = condition.Frame ?? defaultFrame;
        var text = FrameText(frame);
        return Task.FromResult(condition.By switch
        {
            ConditionKind.TextContains => text.Contains(condition.Value, StringComparison.OrdinalIgnoreCase),
            ConditionKind.TextEquals => string.Equals(text.Trim(), condition.Value, StringComparison.OrdinalIgnoreCase),
            ConditionKind.Css => TryCandidate(Scope(frame), new Locator { By = LocatorKind.Css, Value = condition.Value }, 0) is not null,
            _ => false,
        });
    }

    public Task<string> FrameTextAsync(IReadOnlyList<string> framePath, CancellationToken ct) =>
        Task.FromResult(FrameText(framePath));

    public Task<byte[]> ScreenshotAsync(CancellationToken ct)
    {
        try
        {
            var img = _root is not null ? Capture.Element(_root) : Capture.Screen();
            using var bmp = img.Bitmap;
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Task.FromResult(ms.ToArray());
        }
        catch
        {
            return Task.FromResult(MinimalPng());
        }
    }

    public Task<IReadOnlyList<HumanAction>> DrainHumanActionsAsync() =>
        Task.FromResult<IReadOnlyList<HumanAction>>([]);

    public ValueTask DisposeAsync()
    {
        _automation.Dispose();
        _app?.Dispose();
        return ValueTask.CompletedTask;
    }

    private AutomationElement RequireRoot() =>
        _root ?? throw new InvalidOperationException("desktop surface has not been launched");

    private AutomationElement? Scope(IReadOnlyList<string> frame)
    {
        var el = _root;
        if (el is null) return null;
        foreach (var name in frame)
        {
            el = el.FindFirstDescendant(cf => cf.ByName(name));
            if (el is null) return null;
        }
        return el;
    }

    private DesktopTarget? TryCandidate(AutomationElement? scope, Locator loc, int index)
    {
        if (scope is null) return null;
        var q = LocatorMapping.ToDesktop(loc);
        if (q is null) return null;
        try
        {
            switch (q.Kind)
            {
                case DesktopQueryKind.Coordinates:
                    var parts = q.Value.Split(',');
                    if (parts.Length != 2) return null;
                    var origin = scope.BoundingRectangle;
                    var x = origin.X + float.Parse(parts[0]);
                    var y = origin.Y + float.Parse(parts[1]);
                    return new DesktopTarget(null, index, $"coords({q.Value})", x, y);
                case DesktopQueryKind.AutomationId:
                    var byId = scope.FindFirstDescendant(cf => cf.ByAutomationId(q.Value));
                    return byId is null ? null : Wrap(byId, index);
                case DesktopQueryKind.Name:
                    var byName = scope.FindFirstDescendant(cf => cf.ByName(q.Value));
                    return byName is null ? null : Wrap(byName, index);
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    private static DesktopTarget Wrap(AutomationElement el, int index)
    {
        var r = el.BoundingRectangle;
        return new DesktopTarget(el, index, $"{el.ControlType}:{NameOf(el)}", r.X + r.Width / 2.0, r.Y + r.Height / 2.0)
        {
            Meta = new ElementMeta
            {
                Id = el.AutomationId,
                Name = el.Name,
                Tag = el.ControlType.ToString(),
                Text = NameOf(el),
                X = r.X + r.Width / 2.0,
                Y = r.Y + r.Height / 2.0,
            },
        };
    }

    private void Collect(AutomationElement el, List<string> path, List<FrameObservation> output, int depth)
    {
        if (depth > 5 || output.Count > 12) return;
        List<ObservedElement> fields = [], controls = [];
        List<ObservedMessage> msgs = [];
        try
        {
            foreach (var child in el.FindAllChildren())
            {
                var name = NameOf(child);
                var type = child.ControlType;
                if (type is ControlType.Edit or ControlType.Document or ControlType.ComboBox or ControlType.Spinner)
                    fields.Add(new ObservedElement
                    {
                        Tag = type.ToString().ToLowerInvariant(),
                        Id = child.AutomationId,
                        Name = child.Name,
                        Text = name,
                        Value = child.Patterns.Value.IsSupported ? child.Patterns.Value.Pattern.Value : name,
                    });
                else if (type is ControlType.Button or ControlType.Hyperlink or ControlType.MenuItem or ControlType.TabItem
                         or ControlType.CheckBox or ControlType.RadioButton)
                    controls.Add(new ObservedElement { Tag = type.ToString().ToLowerInvariant(), Id = child.AutomationId, Text = name });
                else if ((type is ControlType.Text or ControlType.StatusBar) && !string.IsNullOrWhiteSpace(name))
                    msgs.Add(new ObservedMessage { Id = child.AutomationId, Text = name });

                if ((type is ControlType.Window or ControlType.Pane or ControlType.Group) && !string.IsNullOrWhiteSpace(name)
                    && path.Count < 4)
                {
                    path.Add(name);
                    Collect(child, path, output, depth + 1);
                    path.RemoveAt(path.Count - 1);
                }
            }
        }
        catch { /* tree can mutate while we walk it */ }

        if (fields.Count > 0 || controls.Count > 0 || msgs.Count > 0 || path.Count == 0)
        {
            output.Add(new FrameObservation
            {
                Path = [.. path],
                Title = NameOf(el),
                Fields = fields.Take(40).ToList(),
                Controls = controls.Take(60).ToList(),
                Messages = msgs.Take(25).ToList(),
            });
        }
    }

    private string FrameText(IReadOnlyList<string> frame)
    {
        var scope = Scope(frame);
        return scope is null ? "" : NameOf(scope) + " " + string.Join(" ",
            scope.FindAllDescendants().Select(NameOf).Where(s => s.Length > 0).Take(80));
    }

    private static string NameOf(AutomationElement el)
    {
        try { return (el.Name ?? el.AutomationId ?? "").Trim(); }
        catch { return ""; }
    }

    private static byte[] MinimalPng() =>
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private sealed class DesktopTarget(AutomationElement? element, int index, string description, double? x, double? y)
        : IResolvedTarget
    {
        public AutomationElement? Element { get; } = element;
        public (double X, double Y)? Point => x is not null && y is not null ? (x.Value, y.Value) : null;
        public ElementMeta Meta { get; init; } = new() { Tag = "desktop" };
        public int CandidateIndex { get; } = index;
        public string Description { get; } = description;
    }
}
