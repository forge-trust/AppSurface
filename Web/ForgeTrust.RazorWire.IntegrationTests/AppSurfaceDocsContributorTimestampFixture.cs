namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>Owns a private source corpus and real Docs host for contributor timestamp rendering.</summary>
/// <remarks>
/// The corpus deliberately has no Git history. Rendering tests use the documented page-level timestamp override,
/// while the negative probe omits it to verify that optional Git freshness can leave source/edit links without a
/// timestamp. Neither case changes the production freshness budget or substitutes browser markup for Razor output.
/// </remarks>
internal sealed class AppSurfaceDocsContributorTimestampFixture : IAsyncDisposable
{
    private readonly string _root;
    private readonly AppSurfaceDocsInProcessHost _host;

    private AppSurfaceDocsContributorTimestampFixture(string root, AppSurfaceDocsInProcessHost host)
    {
        _root = root;
        _host = host;
    }

    /// <summary>Gets the real listener URL for the private unreleased page.</summary>
    internal string PageUrl => $"{_host.BaseUrl}/docs/releases/unreleased";

    /// <summary>Creates a source-backed corpus and host with optional deterministic timestamp metadata.</summary>
    /// <param name="includeTimestampOverride">
    /// Whether to supply exact page metadata. False is reserved for the missing-history negative probe.
    /// </param>
    /// <returns>The fixture that owns the server and corpus until asynchronous disposal.</returns>
    internal static async Task<AppSurfaceDocsContributorTimestampFixture> StartAsync(bool includeTimestampOverride = true)
    {
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), $"appsurface-contributor-time-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TestPathUtils.PathUnder(root, "releases"));
        try
        {
            var timestamp = includeTimestampOverride
                ? "contributor:\n  last_updated_override: 2026-04-22T23:19:00Z\n"
                : string.Empty;
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(root, "releases", "unreleased.md"),
                $"---\n{timestamp}trust:\n  status: provisional\n---\n# Unreleased\n\nContributor timestamp rendering fixture.\n\n<!-- appsurface:unreleased-entries section=\"included\" -->\n");
            var host = await AppSurfaceDocsInProcessHost.StartAsync(
                "http://127.0.0.1:0",
                [$"--AppSurfaceDocs:Source:RepositoryRoot={root}"],
                configureServices: null);
            return new AppSurfaceDocsContributorTimestampFixture(root, host);
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    /// <summary>Stops the host before deleting the private source corpus.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.DisposeAsync();
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
