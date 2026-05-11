using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalar.AspNetCore;

namespace ForgeTrust.AppSurface.Web.Scalar;

/// <summary>
/// A web module that integrates Scalar API reference documentation into the application.
/// </summary>
public class AppSurfaceWebScalarModule : IAppSurfaceWebModule
{
    /// <summary>
    /// Configures services needed for Scalar; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="services">The service collection.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
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
    /// Maps the Scalar API reference endpoint.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="endpoints">The endpoint route builder.</param>
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapScalarApiReference();
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
