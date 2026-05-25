using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace ForgeTrust.AppSurface.Web.Scalar;

/// <summary>
/// A web module that integrates Scalar API reference documentation into the application.
/// </summary>
/// <remarks>
/// The Scalar UI is available in development by default and hidden in non-development environments unless both Scalar
/// and the AppSurface-owned OpenAPI document are explicitly exposed. Exposing Scalar does not add authentication or
/// authorization; production hosts must protect the route separately when it is reachable by untrusted users.
/// </remarks>
public class AppSurfaceWebScalarModule : IAppSurfaceWebModule
{
    /// <summary>
    /// Configures services needed for Scalar, including endpoint exposure options.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="services">The service collection.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddOptions<AppSurfaceWebScalarOptions>()
            .BindConfiguration(AppSurfaceWebScalarOptions.SectionName)
            .Validate(
                options => Enum.IsDefined(options.ExposeEndpoint),
                $"{AppSurfaceWebScalarOptions.SectionName}:ExposeEndpoint must be one of DevelopmentOnly, Always, or Never.");
    }

    /// <summary>
    /// Registers dependencies for this module, specifically <see cref="AppSurfaceWebOpenApiModule"/>.
    /// </summary>
    /// <param name="builder">The module dependency builder.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceWebOpenApiModule>();
    }

    /// <summary>
    /// Maps the Scalar API reference endpoint when Scalar and OpenAPI exposure options both allow it.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="endpoints">The endpoint route builder.</param>
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        var scalarOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<AppSurfaceWebScalarOptions>>().Value;
        var openApiOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<AppSurfaceWebOpenApiOptions>>().Value;
        if (!AllowsEndpointExposure(scalarOptions.ExposeEndpoint, context)
            || !AllowsEndpointExposure(openApiOptions.ExposeEndpoint, context))
        {
            return;
        }

        endpoints.MapScalarApiReference();
    }

    private static bool AllowsEndpointExposure(
        AppSurfaceApiDocumentationEndpointExposure exposure,
        StartupContext context)
    {
        return exposure == AppSurfaceApiDocumentationEndpointExposure.Always
            || (exposure == AppSurfaceApiDocumentationEndpointExposure.DevelopmentOnly && context.IsDevelopment);
    }

    /// <summary>
    /// Executes pre-service host configuration; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Executes post-service host configuration; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the web application pipeline; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="app">The application builder.</param>
    public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
    }
}
