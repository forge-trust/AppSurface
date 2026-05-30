using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ForgeTrust.AppSurface.Docs.Standalone;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsStandaloneStatusPageTests
{
    [Fact]
    public async Task MissingDocsRoute_ShouldRenderStandaloneDocsRecoveryPage()
    {
        await using var runningHost = await StartStandaloneHostAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/docs/missing-page");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningHost.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Documentation page not found", html);
        Assert.Contains("Search documentation", html);
        Assert.Contains("href=\"/docs/search\"", html);
        Assert.Contains("/docs/missing-page", html);
    }

    [Fact]
    public async Task MissingDocsRoute_ShouldRenderStandaloneDocsRecoveryPageInDevelopment()
    {
        await using var runningHost = await StartStandaloneHostAsync(args: ["--environment", "Development"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/docs/missing-page");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningHost.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Documentation page not found", html);
        Assert.Contains("href=\"/docs/search\"", html);
    }

    [Fact]
    public async Task ExecutableRunAsync_ShouldRenderStandaloneDocsRecoveryPage()
    {
        var repositoryRoot = CreateRepositoryRoot();
        var port = GetAvailableTcpPort();
        using var process = StartStandaloneExecutable(port, repositoryRoot);

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}")
            };

            var html = await WaitForStandaloneStatusPageAsync(client, process);

            Assert.Contains("Documentation page not found", html);
            Assert.Contains("href=\"/docs/search\"", html);
            Assert.DoesNotContain("AppSurface default 404", html);
        }
        finally
        {
            StopProcess(process);

            if (Directory.Exists(repositoryRoot))
            {
                Directory.Delete(repositoryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecutableRunAsync_ShouldPreserveStandaloneRootDocsRoutesAndAssets()
    {
        var repositoryRoot = CreateRepositoryRoot();
        var port = GetAvailableTcpPort();
        using var process = StartStandaloneExecutable(port, repositoryRoot);

        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}")
            };

            _ = await WaitForStandaloneStatusPageAsync(client, process);

            using var rootResponse = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.Found, rootResponse.StatusCode);
            Assert.Equal("/docs", rootResponse.Headers.Location?.OriginalString);

            using var stylesheetResponse = await client.GetAsync("/css/site.gen.css?v=42");
            Assert.Equal(HttpStatusCode.Found, stylesheetResponse.StatusCode);
            Assert.Equal(
                "/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css?v=42",
                stylesheetResponse.Headers.Location?.OriginalString);

            using var faviconResponse = await client.GetAsync("/favicon.ico");
            Assert.Equal(HttpStatusCode.OK, faviconResponse.StatusCode);
            Assert.Equal("image/svg+xml", faviconResponse.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            StopProcess(process);

            if (Directory.Exists(repositoryRoot))
            {
                Directory.Delete(repositoryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldRenderStandaloneDocsRecoveryWithoutOriginalPath()
    {
        await using var runningHost = await StartStandaloneHostAsync();

        using var response = await runningHost.Client.GetAsync("/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Documentation page not found", html);
        Assert.Contains("the requested documentation page", html);
        Assert.Contains("href=\"/docs/search\"", html);
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldUseLiveDocsRootForVersionedSearchRecovery()
    {
        var catalogPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "catalog.json");

        await using var runningHost = await StartStandaloneHostAsync(
            configurationOverrides:
            new Dictionary<string, string?>
            {
                ["AppSurfaceDocs:Routing:RouteRootPath"] = "/foo/bar",
                ["AppSurfaceDocs:Routing:DocsRootPath"] = "/foo/bar/next",
                ["AppSurfaceDocs:Versioning:Enabled"] = "true",
                ["AppSurfaceDocs:Versioning:CatalogPath"] = catalogPath
            });

        using var response = await runningHost.Client.GetAsync("/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/foo/bar/next/search\"", html);
        Assert.DoesNotContain("href=\"/foo/bar/search\"", html);
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldRenderPathBaseAwareSearchRecovery()
    {
        await using var runningHost = await StartStandaloneHostAsync(pathBase: "/mounted");

        using var response = await runningHost.Client.GetAsync("/mounted/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/mounted/docs/search\"", html);
        Assert.Contains("href=\"/mounted/docs\"", html);
    }

    [Fact]
    public async Task ConfigureOptions_ShouldAllowCallerToDisableStandaloneBrowserStatusPages()
    {
        await using var runningHost = await StartStandaloneHostAsync(
            configureOptions: options => options.Errors.DisableBrowserStatusPages());

        using var response = await runningHost.Client.GetAsync("/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Documentation page not found", html);
        Assert.DoesNotContain("Search documentation", html);
    }

    private static async Task<RunningStandaloneHost> StartStandaloneHostAsync(
        string[]? args = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null,
        string? pathBase = null,
        Action<WebOptions>? configureOptions = null)
    {
        var repositoryRoot = CreateRepositoryRoot();
        var configuration = new Dictionary<string, string?>
        {
            ["AppSurfaceDocs:Source:RepositoryRoot"] = repositoryRoot
        };

        if (configurationOverrides is not null)
        {
            foreach (var (key, value) in configurationOverrides)
            {
                configuration[key] = value;
            }
        }

        var builder = AppSurfaceDocsStandaloneHost.CreateBuilder(
            args ?? [],
            environmentProvider: null,
            configureOptions: configureOptions);
        builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(configuration));
        builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new PathBaseStartupFilter(pathBase)));
        }

        var host = builder.Build();
        await host.StartAsync();

        var server = host.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        var baseAddress = Assert.Single(addresses!.Addresses);
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseAddress)
        };

        return new RunningStandaloneHost(host, client, repositoryRoot);
    }

    private static string CreateRepositoryRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(repositoryRoot);
        File.WriteAllText(
            Path.Combine(repositoryRoot, "README.md"),
            """
            # Test Docs

            This source tree exists so the standalone host can harvest a small documentation snapshot.
            """);
        return repositoryRoot;
    }

    private static Process StartStandaloneExecutable(int port, string repositoryRoot)
    {
        var assemblyPath = typeof(AppSurfaceDocsStandaloneHost).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = GetStandaloneProjectDirectory(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["AppSurfaceDocs__Source__RepositoryRoot"] = repositoryRoot;

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Failed to start the standalone docs executable.");
    }

    private static async Task<string> WaitForStandaloneStatusPageAsync(HttpClient client, Process process)
    {
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!requestTimeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                var output = await process.StandardOutput.ReadToEndAsync(requestTimeout.Token);
                var error = await process.StandardError.ReadToEndAsync(requestTimeout.Token);
                throw new InvalidOperationException(
                    $"Standalone docs executable exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)} before serving the status page.{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }

            try
            {
                using var response = await client.GetAsync("/_appsurface/errors/404", requestTimeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(requestTimeout.Token);
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!requestTimeout.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), requestTimeout.Token);
        }

        throw new TimeoutException("Timed out waiting for the standalone docs executable to serve the status page.");
    }

    private static int GetAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string GetStandaloneProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Web",
                "ForgeTrust.AppSurface.Docs.Standalone",
                "ForgeTrust.AppSurface.Docs.Standalone.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the standalone docs project directory.");
    }

    private static void StopProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        process.WaitForExit(TimeSpan.FromSeconds(5));
    }

    private sealed class PathBaseStartupFilter(string pathBase) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UsePathBase(pathBase);
                next(app);
            };
        }
    }

    private sealed class RunningStandaloneHost(IHost host, HttpClient client, string repositoryRoot) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();

            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();

                if (Directory.Exists(repositoryRoot))
                {
                    Directory.Delete(repositoryRoot, recursive: true);
                }
            }
        }
    }
}
