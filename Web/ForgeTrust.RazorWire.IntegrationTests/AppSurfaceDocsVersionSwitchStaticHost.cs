using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>Serves a completed Release Pages tree over real HTTP without adding response security policy.</summary>
/// <remarks>
/// The static version-switch browser tests must observe the generated early CSP meta element itself. This host only
/// maps ordinary files from the completed publication directory and deliberately does not synthesize a CSP header.
/// </remarks>
internal sealed class AppSurfaceDocsVersionSwitchStaticHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly PhysicalFileProvider _fileProvider;

    private AppSurfaceDocsVersionSwitchStaticHost(
        WebApplication application,
        PhysicalFileProvider fileProvider,
        string baseUrl)
    {
        _application = application;
        _fileProvider = fileProvider;
        BaseUrl = baseUrl;
    }

    /// <summary>Gets the loopback origin that serves the published files.</summary>
    internal string BaseUrl { get; }

    /// <summary>Starts a Kestrel static-file host over a completed publication tree.</summary>
    internal static async Task<AppSurfaceDocsVersionSwitchStaticHost> StartAsync(string pagesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pagesRoot);
        var root = Path.GetFullPath(pagesRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The completed static Docs publication tree was not found: {root}.");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            WebRootPath = root,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var application = builder.Build();
        var fileProvider = new PhysicalFileProvider(root);
        application.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider
        });

        try
        {
            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The static Docs fixture expected one loopback address; found {addresses?.Count ?? 0}.");
            }

            return new AppSurfaceDocsVersionSwitchStaticHost(application, fileProvider, addresses.Single());
        }
        catch
        {
            fileProvider.Dispose();
            await application.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _application.StopAsync();
        }
        finally
        {
            _fileProvider.Dispose();
            await _application.DisposeAsync();
        }
    }
}
