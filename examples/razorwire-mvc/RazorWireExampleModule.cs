using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.AppSurface.Web.Tailwind;
using ForgeTrust.RazorWire;
using RazorWireWebExample.Services;

namespace RazorWireWebExample;

/// <summary>
/// A module that demonstrates the usage of RazorWire in an ASP.NET Core MVC application.
/// </summary>
public class RazorWireExampleModule : IAppSurfaceWebModule
{
    /// <summary>
    /// Registers services required by the Razor Wire example module.
    /// </summary>
    /// <param name="context">The startup context providing environment and configuration for module initialization.</param>
    /// <param name="services">The service collection to which module services are added.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<Services.IUserPresenceService, InMemoryUserPresenceService>();
        services.AddSingleton<Services.IMessageStore, Services.InMemoryMessageStore>();
        services.AddHostedService<Services.UserPresenceBackgroundService>();
        services.AddTailwind();
    }

    /// <summary>
    /// Declares this module's dependency on the RazorWireWebModule.
    /// </summary>
    /// <param name="builder">A builder used to register dependent modules for the application.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<RazorWireWebModule>();
    }

    /// <summary>
    /// Allows the module to configure the host builder before dependency injection services are registered.
    /// </summary>
    /// <param name="context">Contextual information for startup (environment, configuration, module metadata).</param>
    /// <param name="builder">The host builder to modify (e.g., to add configuration sources, logging, or host-level services).</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Performs host-level configuration after services have been registered.
    /// </summary>
    /// <param name="context">Contextual information about the startup environment.</param>
    /// <param name="builder">The host builder to modify (e.g., configure hosting, logging, or lifetime) after service registration.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the web application's middleware and request pipeline.
    /// </summary>
    /// <param name="context">Contextual startup information (environment, configuration, module metadata) available during web application configuration.</param>
    /// <param name="app">The application builder used to add middleware and configure request handling.</param>
    public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
    }

    /// <summary>
    /// Configures the module's endpoint routing by registering the default MVC controller route.
    /// </summary>
    /// <param name="context">The startup context for the module.</param>
    /// <param name="endpoints">The endpoint route builder used to register routes; the default controller route "{controller=Home}/{action=Index}/{id?}" is added to it.</param>
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        endpoints.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");
    }
}
