using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Defines a module that exposes web-specific configuration, endpoints, and middleware.
/// </summary>
public interface IAppSurfaceWebModule : IAppSurfaceHostModule
{
    /// <summary>
    /// Configures <see cref="WebOptions"/> for the application, such as MVC, CORS, and static files.
    /// </summary>
    /// <param name="context">The startup context for the application.</param>
    /// <param name="options">The options to be configured.</param>
    void ConfigureWebOptions(StartupContext context, WebOptions options)
    {
        // Default implementation does nothing, so we don't force an implementation.
    }

    /// <summary>
    /// Allows the module to configure endpoint routes for the application.
    /// </summary>
    /// <param name="context">Startup context providing environment and configuration for the module.</param>
    /// <param name="endpoints">Endpoint route builder used to map endpoints (routes, hubs, etc.).</param>
    void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        // Default implementation does nothing, so we don't force an implementation.
    }

    /// <summary>
    /// Configure the ASP.NET Core request pipeline for this module.
    /// </summary>
    /// <param name="context">Startup information and services available to the module during application initialization.</param>
    /// <param name="app">The application's request pipeline builder used to register middleware, routing, and other pipeline components.</param>
    void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
        // Default implementation does nothing, so we don't force an implementation.
    }

    /// <summary>
    /// Configures middleware that can inspect endpoint routing metadata before endpoints execute.
    /// </summary>
    /// <remarks>
    /// AppSurface invokes this hook after <c>UseRouting</c> and AppSurface-managed CORS middleware, and before
    /// <c>UseEndpoints</c>. Middleware registered here can inspect <c>HttpContext.GetEndpoint()</c> at request time, but
    /// unmatched requests can still have no selected endpoint. Root or host integration modules should register global
    /// authentication and authorization middleware here before feature modules add endpoint-aware middleware they own. Do
    /// not call <c>UseRouting</c>, <c>UseCors</c>, <c>UseEndpoints</c>, or map endpoints from this hook; use
    /// <see cref="ConfigureEndpoints"/> for endpoint mapping.
    /// </remarks>
    /// <param name="context">Startup information and services available to the module during application initialization.</param>
    /// <param name="app">The application's request pipeline builder used to register endpoint-aware middleware.</param>
    void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
    {
        // Default implementation does nothing, so existing web modules keep compiling and running unchanged.
    }

    /// <summary>
    /// Gets a value indicating whether this module's assembly should be searched for MVC application parts (controllers, views, etc.).
    /// Defaults to false.
    /// </summary>
    bool IncludeAsApplicationPart => false;
}
