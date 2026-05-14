using Autofac;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Dependency.Autofac;

/// <summary>
/// An Autofac <see cref="Module"/> wrapper that also participates in AppSurface module discovery.
/// </summary>
/// <remarks>
/// <see cref="AppSurfaceAutofacModule"/> implements <see cref="IAppSurfaceModule"/> so Autofac-backed modules can be
/// discovered with the rest of the AppSurface graph. Put Autofac registrations in normal Autofac module methods such
/// as <see cref="Module.Load(ContainerBuilder)"/>; <see cref="ConfigureServices"/> is intentionally a no-op for
/// interface compatibility. Override <see cref="RegisterDependentModules"/> when this module requires other AppSurface
/// modules. Pitfall: Autofac registration ordering and module dependencies still matter, so do not expect
/// <see cref="ConfigureServices"/> to bridge Microsoft DI registrations into Autofac.
/// </remarks>
public abstract class AppSurfaceAutofacModule : Module, IAppSurfaceModule
{
    /// <summary>
    /// Intentionally does not register Microsoft DI services for this Autofac module.
    /// </summary>
    /// <remarks>
    /// Use Autofac's <see cref="Module.Load(ContainerBuilder)"/> for registrations. This method exists only to satisfy
    /// <see cref="IAppSurfaceModule"/>.
    /// </remarks>
    /// <param name="context">The current startup context.</param>
    /// <param name="services">The Microsoft DI service collection, unused by this module.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        // This method is intentionally left empty.
        // The services should be configured in the Autofac module itself.
    }

    /// <summary>
    /// Registers AppSurface module dependencies required before this Autofac module is used.
    /// </summary>
    /// <param name="builder">The module dependency builder used to declare prerequisites.</param>
    public virtual void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        // This method should be overridden in derived classes to register any dependent modules.
    }
}
