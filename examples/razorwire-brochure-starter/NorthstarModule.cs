using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire;
using Microsoft.AspNetCore.Routing;

namespace NorthstarBrochureStarter;

/// <summary>
/// Configures the package-only Northstar Field Guide MVC brochure.
/// </summary>
/// <remarks>
/// This root module intentionally depends only on <see cref="RazorWireWebModule"/>. It registers this assembly as an
/// MVC application part so the brochure's controllers and views remain application-owned while RazorWire supplies the
/// package runtime and static-export integration.
/// </remarks>
public sealed class NorthstarModule : IAppSurfaceWebModule
{
    /// <summary>
    /// Gets a value indicating that the host should discover this sample's MVC controllers and views.
    /// </summary>
    public bool IncludeAsApplicationPart => true;

    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<RazorWireWebModule>();
    }

    /// <inheritdoc />
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureWebApplication(StartupContext context, IApplicationBuilder app)
    {
    }

    /// <inheritdoc />
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
    }
}
