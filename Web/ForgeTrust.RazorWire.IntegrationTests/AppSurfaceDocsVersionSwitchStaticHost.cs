using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>Serves a completed Release Pages tree over real HTTP without adding response security policy.</summary>
/// <remarks>
/// The static version-switch browser tests must observe the generated early CSP meta element itself. This host only
/// maps ordinary files from the completed publication directory and deliberately does not synthesize a CSP header.
/// </remarks>
internal sealed class AppSurfaceDocsVersionSwitchStaticHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private AppSurfaceDocsVersionSwitchStaticHost(
        WebApplication application,
        string baseUrl)
    {
        _application = application;
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
        // The container owns the provider for the whole host lifetime, including startup failure.
        builder.Services.AddSingleton(_ => new PhysicalFileProvider(root));
        var application = builder.Build();
        try
        {
            application.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = application.Services.GetRequiredService<PhysicalFileProvider>()
            });
            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The static Docs fixture expected one loopback address; found {addresses?.Count ?? 0}.");
            }

            return new AppSurfaceDocsVersionSwitchStaticHost(application, addresses.Single());
        }
        catch
        {
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
            await _application.DisposeAsync();
        }
    }
}
