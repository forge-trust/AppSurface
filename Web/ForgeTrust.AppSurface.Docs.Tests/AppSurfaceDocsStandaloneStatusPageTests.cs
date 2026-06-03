using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ForgeTrust.AppSurface.Docs;
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
        Assert.Contains("Browse trusted entry points", html);
        Assert.Contains("Search documentation", html);
        Assert.Contains("href=\"/docs/search\"", html);
        Assert.Contains("href=\"/docs/sections/start-here\" data-rw-export-ignore=\"true\"", html);
        Assert.Contains("href=\"/docs/sections/packages\" data-rw-export-ignore=\"true\"", html);
        Assert.Contains("href=\"/docs\" data-rw-export-ignore=\"true\"", html);
        Assert.Contains("background: #0d182a", html);
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
            Assert.Contains("href=\"/docs/sections/start-here\" data-rw-export-ignore=\"true\"", html);
            Assert.Contains("href=\"/docs/sections/packages\" data-rw-export-ignore=\"true\"", html);
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
        Assert.Contains("href=\"/docs/sections/start-here\" data-rw-export-ignore=\"true\"", html);
        Assert.Contains("href=\"/docs/sections/packages\" data-rw-export-ignore=\"true\"", html);
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldUseLiveDocsRootForVersionedSearchRecovery()
    {
        var catalogDirectory = Directory.CreateDirectory(TestPathUtils.PathUnder(Path.GetTempPath(), Path.GetRandomFileName()));
        var catalogPath = TestPathUtils.PathUnder(catalogDirectory.FullName, "catalog.json");

        try
        {
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
            Assert.Contains("href=\"/foo/bar/next/sections/start-here\" data-rw-export-ignore=\"true\"", html);
            Assert.Contains("href=\"/foo/bar/next/sections/packages\" data-rw-export-ignore=\"true\"", html);
            Assert.DoesNotContain("href=\"/foo/bar/search\"", html);
        }
        finally
        {
            if (Directory.Exists(catalogDirectory.FullName))
            {
                Directory.Delete(catalogDirectory.FullName, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReservedNotFoundRoute_ShouldRenderPathBaseAwareSearchRecovery()
    {
        await using var runningHost = await StartStandaloneHostAsync(pathBase: "/mounted");

        using var response = await runningHost.Client.GetAsync("/mounted/_appsurface/errors/404");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/mounted/docs/search\"", html);
        Assert.Contains("href=\"/mounted/docs/sections/start-here\" data-rw-export-ignore=\"true\"", html);
        Assert.Contains("href=\"/mounted/docs/sections/packages\" data-rw-export-ignore=\"true\"", html);
        Assert.Contains("href=\"/mounted/docs\" data-rw-export-ignore=\"true\"", html);
    }

    [Fact]
    public async Task MissingDocsRoute_ShouldNotTakeOverNonBrowserRequests()
    {
        await using var runningHost = await StartStandaloneHostAsync();

        using var jsonRequest = new HttpRequestMessage(HttpMethod.Get, "/docs/missing-page");
        jsonRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var jsonResponse = await runningHost.Client.SendAsync(jsonRequest);
        var jsonBody = await jsonResponse.Content.ReadAsStringAsync();

        using var noAcceptResponse = await runningHost.Client.GetAsync("/docs/another-missing-page");
        var noAcceptBody = await noAcceptResponse.Content.ReadAsStringAsync();

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/docs/missing-page");
        postRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var postResponse = await runningHost.Client.SendAsync(postRequest);
        var postBody = await postResponse.Content.ReadAsStringAsync();

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/docs/missing-page");
        headRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var headResponse = await runningHost.Client.SendAsync(headRequest);
        var headBody = await headResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, jsonResponse.StatusCode);
        Assert.DoesNotContain("Documentation page not found", jsonBody);
        Assert.Equal(HttpStatusCode.NotFound, noAcceptResponse.StatusCode);
        Assert.DoesNotContain("Documentation page not found", noAcceptBody);
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);
        Assert.DoesNotContain("Documentation page not found", postBody);
        Assert.Equal(HttpStatusCode.NotFound, headResponse.StatusCode);
        Assert.Empty(headBody);
    }

    [Fact]
    public async Task MissingJsonLikeDocsRoute_ShouldNotRenderBrowserRecovery_WhenJsonIsRequested()
    {
        await using var runningHost = await StartStandaloneHostAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/docs/missing.json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await runningHost.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Documentation page not found", body);
        Assert.DoesNotContain("Search documentation", body);
    }

    [Fact]
    public async Task MissingDocsRoute_ShouldHtmlEncodeOriginalPath()
    {
        await using var runningHost = await StartStandaloneHostAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/docs/%3Cscript%3Ealert(1)%3Cscript%3E%22quote");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await runningHost.Client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("&lt;script&gt;alert(1)&lt;script&gt;&quot;quote", html);
        Assert.DoesNotContain("<script>alert(1)<script>\"quote", html);
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
            ["AppSurfaceDocs:Source:RepositoryRoot"] = repositoryRoot,
            ["AppSurfaceDocs:Harvest:StartupMode"] = nameof(AppSurfaceDocsHarvestStartupMode.Blocking)
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
        var repositoryRoot = TestPathUtils.PathUnder(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(repositoryRoot);
        File.WriteAllText(
            TestPathUtils.PathUnder(repositoryRoot, "README.md"),
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
        Exception? lastProbeException = null;

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
            catch (HttpRequestException ex)
            {
                lastProbeException = ex;
            }
            catch (TaskCanceledException ex) when (!requestTimeout.IsCancellationRequested)
            {
                lastProbeException = ex;
            }
            catch (OperationCanceledException ex) when (requestTimeout.IsCancellationRequested)
            {
                lastProbeException = ex;
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), requestTimeout.Token);
            }
            catch (OperationCanceledException ex) when (requestTimeout.IsCancellationRequested)
            {
                lastProbeException = ex;
                break;
            }
        }

        var timeoutMessage = lastProbeException is null
            ? "Timed out waiting for the standalone docs executable to serve the status page."
            : $"Timed out waiting for the standalone docs executable to serve the status page. Last probe failed with {lastProbeException.GetType().Name}: {lastProbeException.Message}";
        throw new TimeoutException(timeoutMessage, lastProbeException);
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetStandaloneProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = TestPathUtils.PathUnder(
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
            await DisposeHostAndRepositoryAsync(host, repositoryRoot);
        }

        private static async Task DisposeHostAndRepositoryAsync(IHost hostToDispose, string repositoryRootToDelete)
        {
            using var _ = hostToDispose;

            try
            {
                await hostToDispose.StopAsync();
            }
            finally
            {
                if (Directory.Exists(repositoryRootToDelete))
                {
                    Directory.Delete(repositoryRootToDelete, recursive: true);
                }
            }
        }
    }
}
