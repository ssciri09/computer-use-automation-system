using Cua.Core.Artifacts;

namespace Cua.Core.Surface;

/// <summary>
/// How artifact locator kinds are interpreted on a given surface. The compiler
/// always emits the same chain (css #id → name → text → coords); adapters map
/// those ranks onto DOM vs accessibility-tree primitives.
/// </summary>
public static class LocatorMapping
{
    public static string RobustnessNote(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Desktop =>
            "ranked AutomationId (css #id) → Name (name attribute or visible text) → " +
            "frame-relative coordinates; coordinates are last-resort for owner-drawn / bitmap surfaces with no automation tree",
        SurfaceKind.LegacyWeb =>
            "ranked id → name attribute → scoped visible text → frame-relative coordinates; " +
            "auto-generated ids (ext-genNN style) churn across vendor releases, so every rank below id " +
            "is a genuine fallback, and coordinates are last-resort only",
        _ =>
            "ranked id / test-id → name attribute → scoped visible text → frame-relative coordinates; " +
            "prefer stable attributes when the DOM has them, keep text and coordinates as fallbacks",
    };

    public static DesktopQuery? ToDesktop(Locator loc)
    {
        switch (loc.By)
        {
            case LocatorKind.Coords:
                return new DesktopQuery(DesktopQueryKind.Coordinates, loc.Value);
            case LocatorKind.Text:
                return new DesktopQuery(DesktopQueryKind.Name, loc.Value);
            case LocatorKind.Css:
                var v = loc.Value.Trim();
                if (v.StartsWith('#') && v.Length > 1 && !v.Contains(' '))
                    return new DesktopQuery(DesktopQueryKind.AutomationId, v[1..]);
                var name = ExtractAttr(v, "name") ?? ExtractAttr(v, "aria-label");
                if (name is not null)
                    return new DesktopQuery(DesktopQueryKind.Name, name);
                var testId = ExtractAttr(v, "data-testid") ?? ExtractAttr(v, "data-test-id");
                if (testId is not null)
                    return new DesktopQuery(DesktopQueryKind.AutomationId, testId);
                return null;
            default:
                return null; // xpath has no accessibility-tree equivalent
        }
    }

    private static string? ExtractAttr(string css, string attr)
    {
        var needle = $"[{attr}='";
        var i = css.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            needle = $"[{attr}=\"";
            i = css.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
        }
        var start = i + needle.Length;
        var end = css.IndexOf(needle.EndsWith("\"") ? '"' : '\'', start);
        return end < 0 ? null : css[start..end];
    }
}

public enum DesktopQueryKind { AutomationId, Name, Coordinates }

public sealed record DesktopQuery(DesktopQueryKind Kind, string Value);
