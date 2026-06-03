using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class RazorWireCliReadmeContractTests
{
    [Fact]
    public void Readme_Should_Document_StaticWebsiteDeploymentExtras_Boundary()
    {
        var readme = File.ReadAllText(GetRazorWireCliReadmePath());

        Assert.Contains("#### Static website deployment extras", readme, StringComparison.Ordinal);
        Assert.Contains("`--seeds` are routes, not local paths.", readme, StringComparison.Ordinal);
        Assert.Contains("`CNAME` and similar opaque deployment files are deployment-owned extras.", readme, StringComparison.Ordinal);
        Assert.Contains("`/_appsurface/errors/404`", readme, StringComparison.Ordinal);
        Assert.Contains("root `404.html`", readme, StringComparison.Ordinal);
        Assert.Contains("through a host integration using `ExportRedirectStrategy.Netlify` or through `appsurface docs export --redirects netlify`", readme, StringComparison.Ordinal);
        Assert.Contains("install -m 0644 ./deploy/CNAME ./dist/CNAME", readme, StringComparison.Ordinal);
        Assert.Contains("test -f ./dist/CNAME", readme, StringComparison.Ordinal);
        Assert.Contains("test -f ./dist/404.html", readme, StringComparison.Ordinal);
        Assert.Contains("Do not copy the repository root", readme, StringComparison.Ordinal);
        Assert.Contains(".appsurface-docs-release-manifest.json", readme, StringComparison.Ordinal);
        Assert.Contains("surrounding publish root", readme, StringComparison.Ordinal);
    }

    private static string GetRazorWireCliReadmePath()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        return Path.Join(repositoryRoot, "Web", "ForgeTrust.RazorWire.Cli", "README.md");
    }
}
