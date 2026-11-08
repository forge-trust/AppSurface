using System.Security.Cryptography;
using System.Text;
using Acornima;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocConceptSwitchRuntimeAssetTests
{
    [Fact]
    public void RuntimeSource_AndBothEmbeddedCopies_AreTheExactPackagedBytes()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var sourcePath = TestPathUtils.PathUnder(repositoryRoot,
            "Web", "ForgeTrust.AppSurface.Docs", "wwwroot", "docs", DocConceptSwitchRuntime.RuntimeAssetFileName);
        var sourceBytes = File.ReadAllBytes(sourcePath);

        Assert.Equal(sourceBytes, DocConceptSwitchRuntime.ReadRuntimeBytes());
        Assert.Equal(sourceBytes, ReadDocsEmbeddedBytes("AppSurfaceDocsEmbeddedAssets/docs/docs-version-switch.js"));

        var digest = SHA256.HashData(sourceBytes);
        Assert.Equal(Convert.ToHexString(digest).ToLowerInvariant(), DocConceptSwitchRuntime.RuntimeSha256Hex);
        var expectedIntegrity = $"sha256-{Convert.ToBase64String(digest)}";
        Assert.Equal(expectedIntegrity, DocConceptSwitchRuntime.RuntimeCspHashSource);
        Assert.Equal(expectedIntegrity, DocConceptSwitchRuntime.RuntimeSubresourceIntegrity);

        var source = Encoding.UTF8.GetString(sourceBytes);
        new Parser().ParseScript(source);
        Assert.DoesNotContain("fetch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Function", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StylesheetSource_AndBothEmbeddedCopies_AreTheExactPackagedBytes()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var sourcePath = TestPathUtils.PathUnder(repositoryRoot,
            "Web", "ForgeTrust.AppSurface.Docs", "wwwroot", "docs", DocConceptSwitchRuntime.StylesheetAssetFileName);
        var sourceBytes = File.ReadAllBytes(sourcePath);

        Assert.Equal(sourceBytes, DocConceptSwitchRuntime.ReadStylesheetBytesForCaller());
        Assert.Equal(sourceBytes, ReadDocsEmbeddedBytes("AppSurfaceDocsEmbeddedAssets/docs/docs-version-switch.css"));
    }

    [Fact]
    public void DeliveryMetadata_NamesThePresenterDomAndMountedAssetRoutes()
    {
        Assert.Equal("versions/switch/runtime.js", DocConceptSwitchRuntime.RuntimeRoutePath);
        Assert.Equal("versions/switch/switch.css", DocConceptSwitchRuntime.StylesheetRoutePath);
        Assert.Equal("data-docs-version-switch", DocConceptSwitchRuntime.RootAttribute);
        Assert.Equal("data-docs-version-fallback", DocConceptSwitchRuntime.FallbackAttribute);
        Assert.Equal("data-docs-version-toggle", DocConceptSwitchRuntime.ToggleAttribute);
        Assert.Equal("data-docs-version-options", DocConceptSwitchRuntime.OptionsAttribute);
        Assert.Equal("data-docs-version-data", DocConceptSwitchRuntime.DataScriptAttribute);
        Assert.Equal("data-docs-version-notice", DocConceptSwitchRuntime.NoticeAttribute);
        Assert.Equal("docs-version-explanation", DocConceptSwitchRuntime.ExplanationId);
        Assert.Equal("target", DocConceptSwitchRuntime.CurrentIdentityProperty);
        Assert.Equal("incoming", DocConceptSwitchRuntime.IncomingRowsProperty);
        Assert.Equal("docsFrom", DocConceptSwitchRuntime.SourceKeyQueryParameter);
        Assert.Equal("data-docs-version-anchor-ids", DocConceptSwitchRuntime.AnchorIdsAttribute);
    }

    [Fact]
    public void StaticThemeAuthorization_RemainsBoundToTheExistingPackageBootstrapHash()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var source = File.ReadAllText(TestPathUtils.PathUnder(root, "tools", "ForgeTrust.AppSurface.Release", "ReleaseDocsStaticConceptPublication.cs"));
        var declaration = "internal const string ThemeBootstrapHash = \"" + ForgeTrust.AppSurface.Web.Theming.AppSurfaceThemePreferenceCsp.ScriptHash + "\";";
        Assert.Contains(declaration, source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeBytes_AreReturnedAsDefensiveCopies()
    {
        var first = DocConceptSwitchRuntime.ReadRuntimeBytes();
        first[0] ^= 0xff;

        Assert.NotEqual(first, DocConceptSwitchRuntime.ReadRuntimeBytes());
    }

    private static byte[] ReadDocsEmbeddedBytes(string resourceName)
    {
        using var stream = typeof(AppSurfaceDocsConceptSwitching).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
