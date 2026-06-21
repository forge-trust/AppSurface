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
        Assert.Contains("`--publish-root-extras` are deployment-owned publish-root files, not routes.", readme, StringComparison.Ordinal);
        Assert.Contains("version: 1", readme, StringComparison.Ordinal);
        Assert.Contains("publishPath: /CNAME", readme, StringComparison.Ordinal);
        Assert.Contains("`CNAME`, `.nojekyll`, and `/.well-known/security.txt` are deployment-owned extras", readme, StringComparison.Ordinal);
        Assert.Contains("`/_appsurface/errors/404`", readme, StringComparison.Ordinal);
        Assert.Contains("root `404.html`", readme, StringComparison.Ordinal);
        Assert.Contains("through a host integration using `ExportRedirectStrategy.Netlify` or through `appsurface docs export --redirects netlify`", readme, StringComparison.Ordinal);
        Assert.Contains("--publish-root-extras ./deploy/export-extras.yml", readme, StringComparison.Ordinal);
        Assert.Contains("test -f ./dist/CNAME", readme, StringComparison.Ordinal);
        Assert.Contains("Why not `public/`?", readme, StringComparison.Ordinal);
        Assert.Contains("`/_redirects` and `/_headers` are reserved in v1.", readme, StringComparison.Ordinal);
        Assert.Contains("`RWEXPORT007 [target-reserved]`", readme, StringComparison.Ordinal);
        Assert.Contains("`RWEXPORT007`: a publish-root deployment extra could not be accepted.", readme, StringComparison.Ordinal);
        Assert.Contains("Do not copy the repository root", readme, StringComparison.Ordinal);
        Assert.Contains(".appsurface-docs-release-manifest.json", readme, StringComparison.Ordinal);
        Assert.Contains("surrounding publish root", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Should_Document_ArtifactRedirectProvenance_Boundary()
    {
        var readme = File.ReadAllText(GetRazorWireCliReadmePath());

        Assert.Contains("Exporter-managed artifact fetches own their HTTP redirect chain", readme, StringComparison.Ordinal);
        Assert.Contains("redirects outside that export origin and app path fail with `RWEXPORT008`", readme, StringComparison.Ordinal);
        Assert.Contains("host-registered redirect aliases (`RWEXPORT005`)", readme, StringComparison.Ordinal);
        Assert.Contains("URL-source readiness probing", readme, StringComparison.Ordinal);
        Assert.Contains("`RWEXPORT008`: an exporter-managed artifact fetch was redirected outside the configured export origin and app path", readme, StringComparison.Ordinal);
        Assert.Contains("Route normalization validates requested URLs before the exporter fetches them. Redirect enforcement validates the response chain before artifact content is read or written.", readme, StringComparison.Ordinal);
        Assert.Contains("Hybrid export also fails unsafe artifact redirects with `RWEXPORT008`", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Should_CrossReference_HybridHostingGuide()
    {
        var readme = File.ReadAllText(GetRazorWireCliReadmePath());

        Assert.Contains("../ForgeTrust.RazorWire/Docs/hybrid-hosting.md", readme, StringComparison.Ordinal);
        Assert.Contains("Cloud Run live-origin deployment recipe", readme, StringComparison.Ordinal);
        Assert.Contains("CORS setup", readme, StringComparison.Ordinal);
        Assert.Contains("cold-start tradeoffs", readme, StringComparison.Ordinal);
        Assert.Contains("appsurface docs export", readme, StringComparison.Ordinal);
        Assert.Contains("--live-origin https://api.example.com", readme, StringComparison.Ordinal);
        Assert.Contains("The runtime wakes the live origin only when the user interacts with the form", readme, StringComparison.Ordinal);
    }

    private static string GetRazorWireCliReadmePath()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        return TestPathUtils.PathUnder(repositoryRoot, "Web", "ForgeTrust.RazorWire.Cli", "README.md");
    }
}
