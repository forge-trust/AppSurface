using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Registers the surface-neutral AppSurface auth composition boundary.
/// </summary>
/// <remarks>
/// <see cref="AppSurfaceAuthModule"/> is a boundary-preview module. It gives AppSurface packages a stable place to
/// compose future auth contracts without taking a dependency on ASP.NET Core authentication, authorization policies,
/// identity providers, middleware, endpoint filters, cookies, bearer tokens, or UI. Registering this module does not
/// sign users in, inspect requests, challenge callers, forbid callers, or enforce authorization; host applications must
/// continue to configure those behaviors in their host-specific security stack.
/// </remarks>
public class AppSurfaceAuthModule : IAppSurfaceModule
{
    /// <summary>
    /// Registers the AppSurface auth boundary options type.
    /// </summary>
    /// <remarks>
    /// This method registers <see cref="AppSurfaceAuthOptions"/> with the Microsoft Options pattern so later AppSurface
    /// auth contracts have a documented options home. It intentionally adds no runtime auth behavior and performs no
    /// request, principal, policy, middleware, or identity-provider configuration.
    /// </remarks>
    /// <param name="context">Startup context for the current AppSurface composition pass.</param>
    /// <param name="services">Service collection that receives the boundary-preview options registration.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddOptions<AppSurfaceAuthOptions>();
    }

    /// <summary>
    /// Registers modules required by the AppSurface auth boundary.
    /// </summary>
    /// <remarks>
    /// The boundary preview has no dependent modules. Future host-specific auth integrations should declare their own
    /// dependencies instead of relying on this module to pull in ASP.NET Core or UI packages.
    /// </remarks>
    /// <param name="builder">The module dependency builder for the current startup graph.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
