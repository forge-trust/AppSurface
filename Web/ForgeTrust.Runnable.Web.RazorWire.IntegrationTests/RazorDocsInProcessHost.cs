using ForgeTrust.Runnable.Core;
using ForgeTrust.Runnable.Web.RazorDocs.Standalone;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorWire.IntegrationTests;

internal sealed class RazorDocsInProcessHost : IAsyncDisposable
{
    private readonly IHost _host;

    private RazorDocsInProcessHost(IHost host, string baseUrl)
    {
        _host = host;
        BaseUrl = baseUrl;
        IsStarted = true;
    }

    public string BaseUrl { get; }

    public bool IsStarted { get; private set; }

    public string Diagnostics { get; private set; } = "RazorDocs standalone host is running in-process.";

    public static async Task<RazorDocsInProcessHost> StartAsync(string requestedBaseUrl)
    {
        var repoRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var builder = RazorDocsStandaloneHost.CreateBuilder(
            CreateStandaloneArgs(requestedBaseUrl, repoRoot),
            DevelopmentEnvironmentProvider.Instance);

        builder.UseContentRoot(repoRoot);
        builder.ConfigureWebHost(webHost =>
        {
            webHost.UseEnvironment(Environments.Development);
            webHost.UseUrls(requestedBaseUrl);
        });

        var host = builder.Build();
        try
        {
            await host.StartAsync();
            var baseUrl = ResolveBoundBaseUrl(host);
            return new RazorDocsInProcessHost(host, baseUrl);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsStarted)
        {
            return;
        }

        IsStarted = false;
        Diagnostics = "RazorDocs standalone host has stopped.";
        await _host.StopAsync();
        _host.Dispose();
    }

    private static string ResolveBoundBaseUrl(IHost host)
    {
        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        return ResolveBoundBaseUrl(addresses);
    }

    internal static string ResolveBoundBaseUrl(ICollection<string>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
        {
            throw new InvalidOperationException("RazorDocs standalone host did not publish a listening URL. No addresses were published.");
        }

        if (addresses.Count != 1)
        {
            throw new InvalidOperationException($"RazorDocs standalone host published {addresses.Count} listening URLs; expected exactly one. Values: '{string.Join("', '", addresses)}'.");
        }

        var baseAddress = addresses.Single();
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"RazorDocs standalone host did not publish a valid listening URL. Value: '{baseAddress}'.");
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    private static string[] CreateStandaloneArgs(string baseUrl, string repoRoot)
    {
        return
        [
            "--urls",
            baseUrl,
            "--environment",
            Environments.Development,
            "--RazorDocs:Mode",
            "Source",
            "--RazorDocs:Source:RepositoryRoot",
            repoRoot,
            "--RazorDocs:Contributor:Enabled",
            "true",
            "--RazorDocs:Contributor:DefaultBranch",
            "main",
            "--RazorDocs:Contributor:SourceUrlTemplate",
            "https://github.com/forge-trust/Runnable/blob/{branch}/{path}",
            "--RazorDocs:Contributor:EditUrlTemplate",
            "https://github.com/forge-trust/Runnable/edit/{branch}/{path}",
            "--RazorDocs:Contributor:LastUpdatedMode",
            "Git"
        ];
    }

    private sealed class DevelopmentEnvironmentProvider : IEnvironmentProvider
    {
        public static readonly DevelopmentEnvironmentProvider Instance = new();

        private DevelopmentEnvironmentProvider()
        {
        }

        public string Environment => Environments.Development;

        public bool IsDevelopment => true;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null)
        {
            if (string.Equals(name, "ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
            {
                return Environments.Development;
            }

            var value = System.Environment.GetEnvironmentVariable(name);

            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }
    }
}
