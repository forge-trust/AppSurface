using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class AppSurfaceCliReadmeContractTests
{
    [Fact]
    public void Readme_Should_Link_AuthenticatedCommandDesign()
    {
        var readme = File.ReadAllText(GetAppSurfaceCliReadmePath());
        var authReadme = File.ReadAllText(GetAppSurfaceAuthReadmePath());
        var standaloneAppSettings = File.ReadAllText(GetStandaloneDocsAppSettingsPath());

        Assert.Contains("[authenticated command design](docs/authenticated-command-design.md)", readme, StringComparison.Ordinal);
        Assert.Contains("[AppSurface CLI authenticated command design](../../Cli/ForgeTrust.AppSurface.Cli/docs/authenticated-command-design.md)", authReadme, StringComparison.Ordinal);
        Assert.Contains("CLI auth remains outside this package", authReadme, StringComparison.Ordinal);
        Assert.Contains("\"Cli/**/docs/**/*.md\"", standaloneAppSettings, StringComparison.Ordinal);
        Assert.Contains("appsurface docs publish --archive ./dist/docs --site <site>", readme, StringComparison.Ordinal);
        Assert.Contains("RFC 8628 device flow", readme, StringComparison.Ordinal);
        Assert.Contains("CI no-prompt behavior", readme, StringComparison.Ordinal);
        Assert.Contains("secure token-cache boundaries", readme, StringComparison.Ordinal);
        Assert.Contains("`ASCLI1xx` diagnostics", readme, StringComparison.Ordinal);
        Assert.Contains("packed-tool readiness proof", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedCommandDesign_Should_Document_CliAuth_Boundaries()
    {
        var design = File.ReadAllText(GetAuthenticatedCommandDesignPath());

        Assert.Contains("Issue `#425` defines the design contract", design, StringComparison.Ordinal);
        Assert.Contains("does not add auth commands yet", design, StringComparison.Ordinal);
        Assert.Contains("`ForgeTrust.AppSurface.Auth` stays passive", design, StringComparison.Ordinal);
        Assert.Contains("CLI auth must not depend on ASP.NET Core", design, StringComparison.Ordinal);
        Assert.Contains("For v0, the CLI-auth boundary lives inside `ForgeTrust.AppSurface.Cli`", design, StringComparison.Ordinal);
        Assert.Contains("Promote those contracts to a future package such as `ForgeTrust.AppSurface.Auth.Cli` only after", design, StringComparison.Ordinal);
        Assert.Contains("OAuth Device Authorization Grant is required for headless", design, StringComparison.Ordinal);
        Assert.Contains("browser/loopback PKCE", design, StringComparison.Ordinal);
        Assert.Contains("CI/non-interactive + missing token", design, StringComparison.Ordinal);
        Assert.Contains("AppSurface will not fall back to plaintext refresh-token storage", design, StringComparison.Ordinal);
        Assert.Contains("ASCLI101 not_logged_in", design, StringComparison.Ordinal);
        Assert.Contains("verify-packages --package-version 0.0.0-ci.local", design, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticatedCommandDesign_Should_Define_Output_Diagnostics_And_StateMachines()
    {
        var design = File.ReadAllText(GetAuthenticatedCommandDesignPath());

        Assert.Contains("Expected first-run output contract", design, StringComparison.Ordinal);
        Assert.Contains("ASCLI100 auth_status_ready", design, StringComparison.Ordinal);
        Assert.Contains("ASCLI130 command_authorized", design, StringComparison.Ordinal);
        Assert.Contains("Expires: <future-utc-expiry>", design, StringComparison.Ordinal);
        Assert.DoesNotContain("Expires: 2026-06-21T20:30:00Z", design, StringComparison.Ordinal);
        Assert.Contains("When credentials are missing in CI or `--non-interactive` mode, the command must fail with `ASCLI102 ci_prompt_blocked` on stderr and exit code `12`.", design, StringComparison.Ordinal);
        Assert.Contains("| `ASCLI103` | `cache_unavailable` | stderr | 13 | yes |", design, StringComparison.Ordinal);
        Assert.Contains("| `ASCLI107` | `profile_ambiguous` | stderr | 17 | yes |", design, StringComparison.Ordinal);
        Assert.Contains("multiple active profiles     -> ASCLI107 profile_ambiguous", design, StringComparison.Ordinal);
        Assert.Contains("Auth status and success markers write to stdout.", design, StringComparison.Ordinal);
        Assert.Contains("Token values, refresh-token state, raw provider payloads, email, display name, and unredacted subject claims must never appear on either stream.", design, StringComparison.Ordinal);
        Assert.Contains("### Device-Flow Polling", design, StringComparison.Ordinal);
        Assert.Contains("### Token Refresh Lifecycle", design, StringComparison.Ordinal);
        Assert.Contains("### Profile And Tenant Selection", design, StringComparison.Ordinal);
        Assert.Contains("### Cache Corruption And Migration", design, StringComparison.Ordinal);
        Assert.Contains("USER_CODE_DISPLAYED", design, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Should_CrossReference_StaticWebsiteDeploymentExtras_Boundary()
    {
        var readme = File.ReadAllText(GetAppSurfaceCliReadmePath());

        Assert.Contains("Static website deployment extras", readme, StringComparison.Ordinal);
        Assert.Contains("../../Web/ForgeTrust.RazorWire.Cli/README.md#static-website-deployment-extras", readme, StringComparison.Ordinal);
        Assert.Contains("opaque files such as `CNAME` belong in the deployment publish root through `--publish-root-extras ./deploy/export-extras.yml`", readme, StringComparison.Ordinal);
        Assert.Contains("RazorWire `RWEXPORT007`", readme, StringComparison.Ordinal);
        Assert.Contains("exporter-owned provider artifacts such as `_redirects` are generated by the exporter", readme, StringComparison.Ordinal);
        Assert.Contains("`appsurface docs export` intentionally does not expose `--publish-root-extras`.", readme, StringComparison.Ordinal);
        Assert.Contains("`.appsurface-docs-route-manifest.json` and `.appsurface-docs-release-manifest.json` describe the files that belong to the archive", readme, StringComparison.Ordinal);
        Assert.Contains("surrounding publish root", readme, StringComparison.Ordinal);
        Assert.Contains("immutable exact release archives", readme, StringComparison.Ordinal);
        Assert.Contains("do not copy `/_redirects` or `/_headers`", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Should_CrossReference_HybridHostingGuide()
    {
        var readme = File.ReadAllText(GetAppSurfaceCliReadmePath());

        Assert.Contains("../../Web/ForgeTrust.RazorWire/Docs/hybrid-hosting.md", readme, StringComparison.Ordinal);
        Assert.Contains("Cloud Run live-origin recipe", readme, StringComparison.Ordinal);
        Assert.Contains("first-interaction cold-start tradeoff", readme, StringComparison.Ordinal);
        Assert.Contains("appsurface docs export \\", readme, StringComparison.Ordinal);
        Assert.Contains("--public-origin https://docs.example.com", readme, StringComparison.Ordinal);
        Assert.Contains("--live-origin https://api.example.com", readme, StringComparison.Ordinal);
        Assert.Contains("lazy anti-forgery refresh", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Should_Document_CoverageRun_PublicConsumerPath()
    {
        var readme = File.ReadAllText(GetAppSurfaceCliReadmePath());

        Assert.Contains("### `appsurface coverage run`", readme, StringComparison.Ordinal);
        Assert.Contains("### `appsurface coverage merge`", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet tool run appsurface coverage run --solution ./MyApp.slnx --dry-run", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet tool run appsurface coverage merge --source ./TestResults/coverage-shards --output ./TestResults/coverage-merged", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet add tests/MyApp.Tests/MyApp.Tests.csproj package coverlet.msbuild", readme, StringComparison.Ordinal);
        Assert.Contains("package-owned ReportGenerator", readme, StringComparison.Ordinal);
        Assert.Contains("`.appsurface-coverage-output`", readme, StringComparison.Ordinal);
        Assert.Contains("Every `ASCOV###` diagnostic includes the problem, likely cause, exact fix, docs anchor, and a log path", readme, StringComparison.Ordinal);
        Assert.Contains("Every merge diagnostic uses the `ASCOV130` through `ASCOV139` range", readme, StringComparison.Ordinal);
        Assert.Contains("| `ASCOV103` | No Coverlet Cobertura files were produced.", readme, StringComparison.Ordinal);
        Assert.Contains("| `ASCOV131` | No `coverage.cobertura.xml` files were found.", readme, StringComparison.Ordinal);
        Assert.Contains("- run: dotnet restore ./MyApp.slnx", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet tool run appsurface coverage run --solution ./MyApp.slnx --configuration Release --no-restore", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("intentionally does not expose run or merge orchestration yet", readme, StringComparison.Ordinal);
    }

    private static string GetAppSurfaceCliReadmePath()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        return Path.Join(repositoryRoot, "Cli", "ForgeTrust.AppSurface.Cli", "README.md");
    }

    private static string GetAppSurfaceAuthReadmePath()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        return Path.Join(repositoryRoot, "Auth", "ForgeTrust.AppSurface.Auth", "README.md");
    }

    private static string GetAuthenticatedCommandDesignPath()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        return Path.Join(
            repositoryRoot,
            "Cli",
            "ForgeTrust.AppSurface.Cli",
            "docs",
            "authenticated-command-design.md");
    }

    private static string GetStandaloneDocsAppSettingsPath()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        return Path.Join(
            repositoryRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs.Standalone",
            "appsettings.json");
    }
}
