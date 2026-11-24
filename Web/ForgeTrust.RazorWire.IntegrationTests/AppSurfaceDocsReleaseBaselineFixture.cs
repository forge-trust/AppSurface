using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>Serves a fixed release note through a real Docs host for visual comparison.</summary>
/// <remarks>
/// Only the release screenshot uses this corpus. The other routes and the live release-page contract
/// tests continue to use the repository-backed host.
/// </remarks>
internal sealed class AppSurfaceDocsReleaseBaselineFixture : IAsyncDisposable
{
    private readonly DirectoryInfo _source;
    private readonly AppSurfaceDocsInProcessHost _host;

    private AppSurfaceDocsReleaseBaselineFixture(DirectoryInfo source, AppSurfaceDocsInProcessHost host)
    {
        _source = source;
        _host = host;
    }

    /// <summary>Gets the release route on the fixture's loopback listener.</summary>
    internal string PageUrl => $"{_host.BaseUrl}/docs/releases/unreleased";

    /// <summary>Copies the sample release into an owned source root and starts the selected theme.</summary>
    internal static async Task<AppSurfaceDocsReleaseBaselineFixture> StartAsync(
        Action<IServiceCollection> configureThemeServices)
    {
        var source = Directory.CreateTempSubdirectory("appsurface-release-baseline-");
        AppSurfaceDocsInProcessHost? host = null;
        try
        {
            var fixture = Path.Join(AppContext.BaseDirectory, "TestData", "AppSurfaceDocsReleaseBaseline");
            var releases = source.CreateSubdirectory("releases");
            File.Copy(Path.Join(fixture, "unreleased.md"), Path.Join(releases.FullName, "unreleased.md"));
            host = await AppSurfaceDocsInProcessHost.StartAsync(
                "http://127.0.0.1:0",
                [$"--AppSurfaceDocs:Source:RepositoryRoot={source.FullName}"],
                configureThemeServices);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var html = await client.GetStringAsync($"{host.BaseUrl}/docs", deadline.Token);
                if (!html.Contains("id=\"docs-harvest-page\"", StringComparison.Ordinal))
                {
                    using var response = await client.GetAsync($"{host.BaseUrl}/docs/releases/unreleased", deadline.Token);
                    response.EnsureSuccessStatusCode();
                    return new(source, host);
                }

                await Task.Delay(100, deadline.Token);
            }
        }
        catch
        {
            await DisposeAsync(source, host);
            throw;
        }
    }

    /// <summary>Stops the Docs host before removing its private source corpus.</summary>
    public ValueTask DisposeAsync() => DisposeAsync(_source, _host);

    private static async ValueTask DisposeAsync(DirectoryInfo source, AppSurfaceDocsInProcessHost? host)
    {
        try
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }
        finally
        {
            source.Delete(recursive: true);
        }
    }
}
