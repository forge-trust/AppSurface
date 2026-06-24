using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// WebApplicationFactory helpers for AppSurface auth testing.
/// </summary>
public static class AppSurfaceTestAuthWebApplicationFactoryExtensions
{
    /// <summary>
    /// Returns a WebApplicationFactory configured with AppSurface test authentication.
    /// </summary>
    /// <typeparam name="TEntryPoint">Application entry point type used by WebApplicationFactory.</typeparam>
    /// <param name="factory">Factory to clone with test auth services.</param>
    /// <param name="configure">Optional test auth options callback.</param>
    /// <returns>A cloned factory with AppSurface test auth registered.</returns>
    /// <remarks>
    /// The cloned factory uses the <c>Testing</c> environment and then registers the harness through
    /// <see cref="AppSurfaceTestAuthServiceCollectionExtensions.AddAppSurfaceTestAuth" />.
    /// </remarks>
    public static WebApplicationFactory<TEntryPoint> WithAppSurfaceTestAuth<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        Action<AppSurfaceTestAuthOptions>? configure = null)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services => services.AddAppSurfaceTestAuth(configure));
        });
    }

    /// <summary>
    /// Creates an HTTP client that selects a configured AppSurface test auth persona for every request.
    /// </summary>
    /// <typeparam name="TEntryPoint">Application entry point type used by WebApplicationFactory.</typeparam>
    /// <param name="factory">Factory previously configured with <see cref="WithAppSurfaceTestAuth{TEntryPoint}" />.</param>
    /// <param name="personaName">Configured persona name to select.</param>
    /// <param name="options">Optional WebApplicationFactory client options.</param>
    /// <returns>A client whose default headers select the persona.</returns>
    public static HttpClient CreateAppSurfaceClient<TEntryPoint>(
        this WebApplicationFactory<TEntryPoint> factory,
        string personaName,
        WebApplicationFactoryClientOptions? options = null)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(personaName);

        var registry = factory.Services.GetService(typeof(AppSurfaceTestPersonaRegistry)) as AppSurfaceTestPersonaRegistry
            ?? throw new InvalidOperationException(
                "Problem: AppSurface test auth is not registered. Cause: CreateAppSurfaceClient was called on a factory that was not configured with WithAppSurfaceTestAuth. Fix: call factory.WithAppSurfaceTestAuth(...) before creating persona clients. Docs: Auth/ForgeTrust.AppSurface.Auth.Testing/README.md.");
        registry.Require(personaName);

        var client = options is null ? factory.CreateClient() : factory.CreateClient(options);
        client.DefaultRequestHeaders.Remove(AppSurfaceTestAuthTransport.PersonaHeaderName);
        client.DefaultRequestHeaders.Add(AppSurfaceTestAuthTransport.PersonaHeaderName, personaName);
        return client;
    }
}
