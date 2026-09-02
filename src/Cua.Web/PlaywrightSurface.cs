using System.Text.Json;
using Cua.Core.Artifacts;
using Cua.Core.Hitl;
using Cua.Core.Surface;
using Microsoft.Playwright;

namespace Cua.Web;

/// <summary>
/// Web implementation of the surface seam, built on Playwright. Designed for
/// hostile legacy pages: frames are addressed by explicit name paths (nested
/// iframes injected late by script), locator chains fall back id → attribute →
/// text → coordinates, and "absent" waits handle spinners that appear after a
/// grace period. Nothing above this class knows it is a browser.
/// </summary>
public sealed class PlaywrightSurface : ISurface
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly List<HumanAction> _humanActions = [];
    private readonly object _humanLock = new();
    private DateTimeOffset _recordHumanSince = DateTimeOffset.MaxValue;

    public SessionControl Control { get; } = new();

    private PlaywrightSurface()
    {
        Control.Changed += holder =>
        {
            // only buffer page events as "human actions" while a human holds control
            _recordHumanSince = holder == ControlHolder.Human
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.MaxValue;
        };
    }

    public static async Task<PlaywrightSurface> LaunchAsync(bool headed)
    {
        var s = new PlaywrightSurface();
        s._pw = await Playwright.CreateAsync();
        s._browser = await s._pw.Chromium.LaunchAsync(new() { Headless = !headed });
        var ctx = await s._browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 860 } });
        s._page = await ctx.NewPageAsync();
        await s.InstallHumanActionRecorderAsync();
        return s;
    }

    private IPage Page => _page ?? throw new InvalidOperationException("surface not launched");

    // ------------------------------------------------------------------ nav

    public async Task NavigateAsync(string url, CancellationToken ct) =>
        await Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

    public Task<string> CurrentUrlAsync() => Task.FromResult(Page.Url);

    // ---------------------------------------------------------------- frames

    /// <summary>
    /// Resolve a frame name path from the top document, waiting for frames that
    /// are injected late by script (the modFrm case).
    /// </summary>
    private async Task<IFrame?> ResolveFrameAsync(IReadOnlyList<string> path, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var frame = TryWalk(path);
            if (frame is not null) return frame;
            if (DateTime.UtcNow > deadline) return null;
            await Task.Delay(250, ct);
        }
    }

    private IFrame? TryWalk(IReadOnlyList<string> path)
    {
        IFrame cur = Page.MainFrame;
        foreach (var name in path)
        {
            var next = cur.ChildFrames.FirstOrDefault(f => f.Name == name);
            if (next is null) return null;
            cur = next;
        }
        return cur;
    }

    // -------------------------------------------------------------- resolve

    private sealed class ResolvedTarget : IResolvedTarget
    {
        public required ElementMeta Meta { get; init; }
        public required int CandidateIndex { get; init; }
        public required string Description { get; init; }
        public ILocator? Locator { get; init; }
        public IFrame? Frame { get; init; }
        /// <summary>Set for coordinate fallbacks: page-absolute click point.</summary>
        public (float X, float Y)? PagePoint { get; init; }
    }

    public async Task<IResolvedTarget?> ResolveAsync(TargetRef target, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var deadline = DateTime.UtcNow.AddMilliseconds(target.TimeoutMs);
        while (true)
        {
            var frame = TryWalk(target.Frame);
            if (frame is not null)
            {
                for (var i = 0; i < target.Locator.Candidates.Count; i++)
                {
                    var resolved = await TryCandidateAsync(frame, target.Frame, target.Locator.Candidates[i], i, ct);
                    if (resolved is not null) return resolved;
                }
            }
            if (DateTime.UtcNow > deadline) return null;
            await Task.Delay(300, ct);
        }
    }

    private async Task<ResolvedTarget?> TryCandidateAsync(
        IFrame frame, IReadOnlyList<string> framePath, Locator loc, int index, CancellationToken ct)
    {
        try
        {
            if (loc.By == LocatorKind.Coords)
            {
                var parts = loc.Value.Split(',');
                if (parts.Length != 2) return null;
                var point = await FrameToPagePointAsync(framePath, float.Parse(parts[0]), float.Parse(parts[1]));
                if (point is null) return null;
                return new ResolvedTarget
                {
                    Meta = new ElementMeta { Tag = "coords", Text = loc.Value },
                    CandidateIndex = index,
                    Description = $"coords({loc.Value}) in {FramePathText(framePath)}",
                    PagePoint = point,
                };
            }

            var locator = BuildLocator(frame, loc);
            if (await locator.CountAsync() == 0) return null;
            locator = locator.First;
            if (!await locator.IsVisibleAsync()) return null;

            var meta = await ReadMetaAsync(locator);
            return new ResolvedTarget
            {
                Meta = meta,
                CandidateIndex = index,
                Description = $"{loc.By}:{loc.Value} in {FramePathText(framePath)}",
                Locator = locator,
                Frame = frame,
            };
        }
        catch (PlaywrightException) { return null; }
        catch (TimeoutException) { return null; }
    }

    private static ILocator BuildLocator(IFrame frame, Locator loc) => loc.By switch
    {
        LocatorKind.Css => frame.Locator(loc.Value),
        LocatorKind.Xpath => frame.Locator("xpath=" + loc.Value),
        LocatorKind.Text when loc.Within is not null =>
            frame.Locator(loc.Within).Filter(new() { HasText = loc.Value }),
        LocatorKind.Text => frame.GetByText(loc.Value, new() { Exact = false }),
        _ => throw new ArgumentOutOfRangeException(nameof(loc)),
    };

    private static async Task<ElementMeta> ReadMetaAsync(ILocator locator)
    {
        var json = await locator.EvaluateAsync<JsonElement>(
            "el => { const r = el.getBoundingClientRect(); return { id: el.id || null, name: el.getAttribute('name'), tag: el.tagName.toLowerCase(), text: (el.innerText || el.value || '').trim().slice(0, 120), cls: el.className || null, x: r.x + r.width / 2, y: r.y + r.height / 2 }; }");
        return new ElementMeta
        {
            Id = GetStr(json, "id"),
            Name = GetStr(json, "name"),
            Tag = GetStr(json, "tag") ?? "unknown",
            Text = GetStr(json, "text"),
            Classes = GetStr(json, "cls"),
            X = json.TryGetProperty("x", out var x) ? x.GetDouble() : null,
            Y = json.TryGetProperty("y", out var y) ? y.GetDouble() : null,
        };
    }

    private async Task<(float, float)?> FrameToPagePointAsync(IReadOnlyList<string> framePath, float x, float y)
    {
        // Walk frame elements, accumulating offsets to translate a frame-relative
        // point into page coordinates.
        float ox = 0, oy = 0;
        IFrame cur = Page.MainFrame;
        foreach (var name in framePath)
        {
            var next = cur.ChildFrames.FirstOrDefault(f => f.Name == name);
            if (next is null) return null;
            var el = await next.FrameElementAsync();
            var box = await el.BoundingBoxAsync();
            if (box is null) return null;
            ox += (float)box.X;
            oy += (float)box.Y;
            cur = next;
        }
        return (ox + x, oy + y);
    }

    private static string FramePathText(IReadOnlyList<string> path) =>
        path.Count == 0 ? "(top)" : string.Join(">", path);

    // -------------------------------------------------------------- actions

    public async Task ClickAsync(IResolvedTarget target, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var t = (ResolvedTarget)target;
        if (t.PagePoint is { } p) { await Page.Mouse.ClickAsync(p.X, p.Y); return; }
        await t.Locator!.ClickAsync(new() { Timeout = 10_000 });
    }

    public async Task TypeAsync(IResolvedTarget target, string text, bool clearFirst, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var t = (ResolvedTarget)target;
        if (t.Locator is null) throw new InvalidOperationException("cannot type into a coordinate target");
        if (clearFirst) await t.Locator.FillAsync(text, new() { Timeout = 10_000 });
        else await t.Locator.PressSequentiallyAsync(text, new() { Timeout = 10_000 });
    }

    public async Task SelectAsync(IResolvedTarget target, string value, CancellationToken ct)
    {
        Control.AssertAutomationHasControl();
        var t = (ResolvedTarget)target;
        if (t.Locator is null) throw new InvalidOperationException("cannot select on a coordinate target");
        try { await t.Locator.SelectOptionAsync(new SelectOptionValue { Value = value }, new() { Timeout = 10_000 }); }
        catch (PlaywrightException)
        {
            await t.Locator.SelectOptionAsync(new SelectOptionValue { Label = value }, new() { Timeout = 10_000 });
        }
    }

    public async Task<string> ReadTextAsync(IResolvedTarget target, CancellationToken ct)
    {
        var t = (ResolvedTarget)target;
        if (t.Locator is null) return "";
        return (await t.Locator.InnerTextAsync(new() { Timeout = 10_000 })).Trim();
    }

    // ---------------------------------------------------------------- waits

    public async Task<bool> WaitForAsync(
        IReadOnlyList<string> framePath, Locator locator, WaitState state, int timeoutMs, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(framePath, Math.Min(timeoutMs, 20_000), ct);
        if (frame is null) return state == WaitState.Absent; // no frame → nothing visible in it

        var pl = BuildLocator(frame, locator).First;
        try
        {
            if (state == WaitState.Visible)
            {
                await pl.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
                return true;
            }

            // Absent: the interesting case is a busy indicator that appears
            // shortly AFTER the triggering action. "Already absent" must not be
            // confused with "not yet present": give it a grace period to show
            // up, then require it to go away.
            try
            {
                await pl.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 1500 });
            }
            catch (TimeoutException) { /* never appeared — fine */ }
            await pl.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = timeoutMs });
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (PlaywrightException) { return state == WaitState.Absent; }
    }

    // ----------------------------------------------------------- conditions

    public async Task<bool> IsConditionMetAsync(
        ConditionDef condition, IReadOnlyList<string> defaultFrame, CancellationToken ct)
    {
        var framePath = condition.Frame ?? defaultFrame;
        if (condition.By == ConditionKind.UrlContains)
            return Page.Url.Contains(condition.Value, StringComparison.OrdinalIgnoreCase);

        var frame = TryWalk(framePath);
        if (frame is null) return false;
        try
        {
            switch (condition.By)
            {
                case ConditionKind.Css:
                    var loc = frame.Locator(condition.Value).First;
                    return await loc.CountAsync() > 0 && await loc.IsVisibleAsync();
                case ConditionKind.TextContains:
                {
                    var text = await SafeFrameTextAsync(frame);
                    return text.Contains(condition.Value, StringComparison.OrdinalIgnoreCase);
                }
                case ConditionKind.TextEquals:
                {
                    var text = await SafeFrameTextAsync(frame);
                    return text.Split('\n').Any(l =>
                        string.Equals(l.Trim(), condition.Value.Trim(), StringComparison.OrdinalIgnoreCase));
                }
                default: return false;
            }
        }
        catch (PlaywrightException) { return false; }
    }

    public async Task<string> FrameTextAsync(IReadOnlyList<string> framePath, CancellationToken ct)
    {
        var frame = TryWalk(framePath);
        return frame is null ? "" : await SafeFrameTextAsync(frame);
    }

    private static async Task<string> SafeFrameTextAsync(IFrame frame)
    {
        try { return await frame.InnerTextAsync("body", new() { Timeout = 5000 }); }
        catch (PlaywrightException) { return ""; }
        catch (TimeoutException) { return ""; }
    }

    // ----------------------------------------------------------- observation

    private const string ObserveScript = """
        () => {
          const trim = (s, n) => (s || '').replace(/\s+/g, ' ').trim().slice(0, n);
          const fields = [], controls = [], msgs = [], tables = [];
          document.querySelectorAll('input, select, textarea').forEach(el => {
            if (el.type === 'hidden') return;
            const f = { tag: el.tagName.toLowerCase(), id: el.id || null, name: el.getAttribute('name'),
                        value: el.type === 'password' ? '***' : trim(el.value, 60), input_type: el.type || null };
            if (el.tagName === 'SELECT') f.options = Array.from(el.options).map(o => o.value).slice(0, 12);
            fields.push(f);
          });
          document.querySelectorAll('[onclick], a, button').forEach(el => {
            const text = trim(el.innerText, 80);
            if (!text && !el.id) return;
            controls.push({ tag: el.tagName.toLowerCase(), id: el.id || null, text });
          });
          document.querySelectorAll('.x-msg-ok, .x-msg-err, .x-msg-warn, .x-form-invalid-msg, .x-panel-header, .x-window-hd, .x-toolbar, #loading-spinner, [role=alert], [role=dialog]').forEach(el => {
            const text = trim(el.innerText, 220);
            if (text) msgs.push({ id: el.id || null, cls: trim(el.className, 60), text });
          });
          document.querySelectorAll('table.x-grid3, table[class*=grid], table[border]').forEach(t => {
            const rows = Array.from(t.querySelectorAll('tr')).slice(0, 10)
              .map(r => Array.from(r.cells).map(c => trim(c.innerText, 40)).join(' | '))
              .filter(r => r.replace(/[|\s]/g, '').length > 0);
            if (rows.length) tables.push({ id: t.id || null, rows });
          });
          return { title: document.title || null,
                   fields: fields.slice(0, 40), controls: controls.slice(0, 60),
                   msgs: msgs.slice(0, 25), tables: tables.slice(0, 6) };
        }
        """;

    public async Task<SurfaceObservation> ObserveAsync(CancellationToken ct)
    {
        var frames = new List<FrameObservation>();
        await ObserveFrameTreeAsync(Page.MainFrame, [], frames, ct);
        return new SurfaceObservation { Url = Page.Url, Frames = frames };
    }

    private async Task ObserveFrameTreeAsync(
        IFrame frame, List<string> path, List<FrameObservation> output, CancellationToken ct)
    {
        try
        {
            var json = await frame.EvaluateAsync<JsonElement>(ObserveScript);
            output.Add(ParseFrameObservation(json, [.. path]));
        }
        catch (PlaywrightException) { /* frame navigating or detached — skip */ }

        foreach (var child in frame.ChildFrames)
        {
            if (string.IsNullOrEmpty(child.Name)) continue;
            path.Add(child.Name);
            await ObserveFrameTreeAsync(child, path, output, ct);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static FrameObservation ParseFrameObservation(JsonElement json, IReadOnlyList<string> path)
    {
        List<ObservedElement> fields = [], controls = [];
        List<ObservedMessage> msgs = [];
        List<ObservedTable> tables = [];
        if (json.TryGetProperty("fields", out var fs))
            foreach (var f in fs.EnumerateArray())
                fields.Add(new ObservedElement
                {
                    Tag = GetStr(f, "tag") ?? "input",
                    Id = GetStr(f, "id"),
                    Name = GetStr(f, "name"),
                    Value = GetStr(f, "value"),
                    InputType = GetStr(f, "input_type"),
                    Options = f.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                        ? [.. o.EnumerateArray().Select(v => v.GetString() ?? "")] : null,
                });
        if (json.TryGetProperty("controls", out var cs))
            foreach (var c in cs.EnumerateArray())
                controls.Add(new ObservedElement { Tag = GetStr(c, "tag") ?? "div", Id = GetStr(c, "id"), Text = GetStr(c, "text") });
        if (json.TryGetProperty("msgs", out var ms))
            foreach (var m in ms.EnumerateArray())
                msgs.Add(new ObservedMessage { Id = GetStr(m, "id"), Classes = GetStr(m, "cls"), Text = GetStr(m, "text") ?? "" });
        if (json.TryGetProperty("tables", out var ts))
            foreach (var t in ts.EnumerateArray())
                tables.Add(new ObservedTable
                {
                    Id = GetStr(t, "id"),
                    Rows = t.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array
                        ? [.. rows.EnumerateArray().Select(r => r.GetString() ?? "")] : [],
                });
        return new FrameObservation
        {
            Path = path, Title = GetStr(json, "title"),
            Fields = fields, Controls = controls, Messages = msgs, Tables = tables,
        };
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------------------------------------------------- human action capture

    private async Task InstallHumanActionRecorderAsync()
    {
        await Page.Context.ExposeFunctionAsync<string, int>("__cuaHumanEvent", payload =>
        {
            var since = _recordHumanSince;
            if (DateTimeOffset.UtcNow < since) return 0; // automation holds control — ignore
            try
            {
                var json = JsonDocument.Parse(payload).RootElement;
                var action = new HumanAction
                {
                    Kind = GetStr(json, "kind") ?? "unknown",
                    Frame = GetStr(json, "frame"),
                    Tag = GetStr(json, "tag"),
                    Id = GetStr(json, "id"),
                    Text = GetStr(json, "text"),
                    Value = GetStr(json, "value"),
                    At = DateTimeOffset.UtcNow,
                };
                lock (_humanLock) _humanActions.Add(action);
            }
            catch (JsonException) { }
            return 0;
        });

        // Runs in every frame (init scripts propagate to child frames), so the
        // recorder sees clicks and edits wherever the human acts — including
        // modals injected into the top document.
        await Page.Context.AddInitScriptAsync("""
            (() => {
              const frameName = (() => { try { return window.name || '(top)'; } catch { return '?'; } })();
              const send = obj => { try { window.__cuaHumanEvent(JSON.stringify(obj)); } catch {} };
              const describe = el => ({
                frame: frameName,
                tag: (el.tagName || '').toLowerCase(),
                id: el.id || null,
                text: ((el.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 80)) || null,
              });
              document.addEventListener('click', e => {
                if (!e.isTrusted) return; // synthetic (automation) clicks are not human actions
                send({ kind: 'click', ...describe(e.target) });
              }, true);
              document.addEventListener('change', e => {
                if (!e.isTrusted) return;
                const el = e.target;
                const secret = el.type === 'password' || /pin|pass|secret/i.test(el.id || '');
                send({ kind: 'input', ...describe(el), value: secret ? '***' : String(el.value || '').slice(0, 60) });
              }, true);
            })();
            """);
    }

    public Task<IReadOnlyList<HumanAction>> DrainHumanActionsAsync()
    {
        lock (_humanLock)
        {
            var copy = _humanActions.ToList();
            _humanActions.Clear();
            return Task.FromResult<IReadOnlyList<HumanAction>>(copy);
        }
    }

    // ------------------------------------------------------------------ misc

    public async Task<byte[]> ScreenshotAsync(CancellationToken ct) =>
        await Page.ScreenshotAsync(new() { FullPage = false });

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
    }
}
