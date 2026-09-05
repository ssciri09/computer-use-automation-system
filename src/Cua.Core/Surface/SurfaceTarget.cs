namespace Cua.Core.Surface;

/// <summary>
/// Normalized launch/attach target plus the allowlist token (process or host).
/// Web kinds use http(s) URLs; desktop uses an exe path, file:// URI, or app://name.
/// </summary>
public sealed record SurfaceTarget
{
    public required string Identity { get; init; }
    public string? Executable { get; init; }
    public string? Arguments { get; init; }
    public string? WindowTitle { get; init; }
    public int? ProcessId { get; init; }
    public string? Url { get; init; }

    public bool IsLaunch => Executable is not null;

    public static SurfaceTarget Parse(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            throw new ArgumentException("surface entry is empty");
        var t = entry.Trim().Trim('"');

        if (t.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = t["app://".Length..];
            if (rest.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(rest["pid:".Length..], out var pid))
                return new SurfaceTarget { Identity = pid.ToString(), ProcessId = pid };
            return new SurfaceTarget { Identity = rest, WindowTitle = rest };
        }

        if (t.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(t, UriKind.Absolute, out var fileUri))
        {
            var path = Uri.UnescapeDataString(fileUri.LocalPath);
            return FromExecutable(path);
        }

        if (Uri.TryCreate(t, UriKind.Absolute, out var http) &&
            (http.Scheme == Uri.UriSchemeHttp || http.Scheme == Uri.UriSchemeHttps))
            return new SurfaceTarget { Identity = http.Authority, Url = t };

        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || File.Exists(t) ||
            (t.Length >= 2 && t[1] == ':'))
            return FromExecutable(t);

        return new SurfaceTarget { Identity = t, WindowTitle = t };
    }

    public static string IdentityOf(string entry) => Parse(entry).Identity;

    private static SurfaceTarget FromExecutable(string path)
    {
        var exe = path.Trim().Trim('"');
        string? args = null;
        var split = exe.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        if (split > 0)
        {
            args = exe[(split + 5)..].Trim();
            exe = exe[..(split + 4)];
        }

        // Artifacts carry repo-relative entry paths so they stay portable;
        // process launch needs an absolute one.
        if (!Path.IsPathRooted(exe))
        {
            var resolved = Path.GetFullPath(exe);
            if (File.Exists(resolved)) exe = resolved;
        }

        return new SurfaceTarget
        {
            Identity = Path.GetFileNameWithoutExtension(exe),
            Executable = exe,
            Arguments = args,
        };
    }
}
