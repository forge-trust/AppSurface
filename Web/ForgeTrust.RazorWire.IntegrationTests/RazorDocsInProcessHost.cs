using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs.Standalone;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Starts the RazorDocs standalone executable host in-process for integration tests.
/// </summary>
/// <remarks>
/// The helper always runs the host with <see cref="Environments.Development"/> defaults and a source-backed
/// RazorDocs configuration rooted at the repository checkout. Callers should pass an explicit loopback URL;
/// using port <c>0</c> lets Kestrel choose an available port that is exposed through <see cref="BaseUrl"/>.
/// </remarks>
internal sealed class RazorDocsInProcessHost : IAsyncDisposable
{
    private readonly IHost _host;

    private RazorDocsInProcessHost(IHost host, string baseUrl)
    {
        _host = host;
        BaseUrl = baseUrl;
        IsStarted = true;
    }

    /// <summary>
    /// Gets the bound base URL in <c>scheme://host:port</c> form after Kestrel starts.
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Gets a value indicating whether the in-process host still owns a started server.
    /// </summary>
    public bool IsStarted { get; private set; }

    /// <summary>
    /// Gets a short lifecycle diagnostic describing whether the host is running or stopped.
    /// </summary>
    public string Diagnostics { get; private set; } = "RazorDocs standalone host is running in-process.";

    /// <summary>
    /// Builds and starts the RazorDocs standalone host with the requested endpoint.
    /// </summary>
    /// <param name="requestedBaseUrl">
    /// The URL passed to Kestrel, typically <c>http://127.0.0.1:0</c> so tests keep a real HTTP listener
    /// while allowing the OS to allocate the port.
    /// </param>
    /// <returns>A started host wrapper whose <see cref="BaseUrl"/> contains the resolved listener address.</returns>
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

        return await StartAsync(builder.Build());
    }

    /// <summary>
    /// Starts an already-built host and wraps the published RazorDocs listener address.
    /// </summary>
    /// <param name="host">The built host to start. Ownership transfers to the returned wrapper or to this method on failure.</param>
    /// <returns>A started host wrapper whose <see cref="BaseUrl"/> contains the resolved listener address.</returns>
    /// <remarks>
    /// This seam keeps startup-failure cleanup testable without forcing the public fixture path to create a broken
    /// Kestrel configuration. If startup or address resolution fails, the built host is disposed before the original
    /// exception is rethrown.
    /// </remarks>
    internal static async Task<RazorDocsInProcessHost> StartAsync(IHost host)
    {
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

    /// <summary>
    /// Stops and disposes the in-process host if it is still running.
    /// </summary>
    /// <remarks>
    /// Disposal is idempotent. <see cref="IsStarted"/> is cleared before shutdown so a second call becomes a no-op,
    /// and the underlying host is disposed even if graceful shutdown throws.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (!IsStarted)
        {
            return;
        }

        IsStarted = false;
        try
        {
            await _host.StopAsync();
        }
        finally
        {
            Diagnostics = "RazorDocs standalone host has stopped.";
            _host.Dispose();
        }
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

    /// <summary>
    /// Resolves the single listener address published by Kestrel into a normalized base URL.
    /// </summary>
    /// <param name="addresses">The addresses published by <see cref="IServerAddressesFeature"/>.</param>
    /// <returns>The authority portion of the published URL, including IPv6 brackets when needed.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no address is published, multiple addresses are published, or the published value is not an
    /// absolute URI. The fixture expects exactly one address because tests configure one requested endpoint.
    /// </exception>
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

        return uri.GetLeftPart(UriPartial.Authority);
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
            "https://github.com/forge-trust/AppSurface/blob/{branch}/{path}",
            "--RazorDocs:Contributor:EditUrlTemplate",
            "https://github.com/forge-trust/AppSurface/edit/{branch}/{path}",
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

            return value ?? defaultValue;
        }
    }
}
