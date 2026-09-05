using Cua.Core.Artifacts;
using Cua.Core.Surface;
using Cua.Desktop;
using Cua.Web;

namespace Cua.Hosting;

/// <summary>
/// Composition root for ISurface. Kind selects the adapter; discovery, the
/// compiler, and replay never switch on kind themselves.
/// </summary>
public static class SurfaceFactory
{
    public static async Task<ISurface> LaunchAsync(SurfaceKind kind, bool headed) =>
        kind == SurfaceKind.Desktop
            ? DesktopSurface.Create()
            : await PlaywrightSurface.LaunchAsync(headed);
}
