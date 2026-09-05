using Cua.Core.Artifacts;
using Cua.Core.Surface;

namespace Cua.Tests;

public sealed class SurfaceKindTests
{
    [Theory]
    [InlineData("web", SurfaceKind.Web)]
    [InlineData("legacy_web", SurfaceKind.LegacyWeb)]
    [InlineData("legacy-web", SurfaceKind.LegacyWeb)]
    [InlineData("desktop", SurfaceKind.Desktop)]
    public void Parse_AcceptsAliases(string raw, SurfaceKind expected) =>
        Assert.Equal(expected, SurfaceKinds.Parse(raw));

    [Fact]
    public void InferFromEntry_HttpIsWeb_ExeIsDesktop()
    {
        Assert.Equal(SurfaceKind.Web, SurfaceKinds.InferFromEntry("http://127.0.0.1:8080/"));
        Assert.Equal(SurfaceKind.Desktop, SurfaceKinds.InferFromEntry(@"C:\Apps\FirstCore.exe"));
        Assert.Equal(SurfaceKind.Desktop, SurfaceKinds.InferFromEntry("app://FirstCore"));
        Assert.Equal(SurfaceKind.Desktop, SurfaceKinds.InferFromEntry("file:///C:/Apps/FirstCore.exe"));
    }

    [Fact]
    public void LocatorMapping_CssHash_BecomesAutomationId()
    {
        var q = LocatorMapping.ToDesktop(new Locator { By = LocatorKind.Css, Value = "#ext-gen77" });
        Assert.Equal(DesktopQueryKind.AutomationId, q!.Kind);
        Assert.Equal("ext-gen77", q.Value);
    }

    [Fact]
    public void LocatorMapping_NameAttribute_BecomesName()
    {
        var q = LocatorMapping.ToDesktop(new Locator { By = LocatorKind.Css, Value = "input[name='acctNbr']" });
        Assert.Equal(DesktopQueryKind.Name, q!.Kind);
        Assert.Equal("acctNbr", q.Value);
    }

    [Fact]
    public void LocatorMapping_TextAndCoords_PassThrough_XpathDropped()
    {
        Assert.Equal(DesktopQueryKind.Name,
            LocatorMapping.ToDesktop(new Locator { By = LocatorKind.Text, Value = "Waive Fee" })!.Kind);
        Assert.Equal(DesktopQueryKind.Coordinates,
            LocatorMapping.ToDesktop(new Locator { By = LocatorKind.Coords, Value = "10,20" })!.Kind);
        Assert.Null(LocatorMapping.ToDesktop(new Locator { By = LocatorKind.Xpath, Value = "//div" }));
    }

    [Fact]
    public void SurfaceTarget_Identity_FromExeAndAppUri()
    {
        Assert.Equal("FirstCore", SurfaceTarget.IdentityOf(@"C:\Bank\FirstCore.exe"));
        Assert.Equal("FirstCore", SurfaceTarget.IdentityOf("app://FirstCore"));
        Assert.Equal("127.0.0.1:8080", SurfaceTarget.IdentityOf("http://127.0.0.1:8080/portal"));
    }
}
