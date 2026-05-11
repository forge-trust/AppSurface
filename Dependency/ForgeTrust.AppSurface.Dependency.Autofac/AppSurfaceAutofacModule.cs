using Autofac;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Dependency.Autofac;

/// <summary>
///     This class is simply a wrapper around the standard Autofac Module.
///     Its goal is to ensure that your autofac use is consistent across your projects.
/// </summary>
public abstract class AppSurfaceAutofacModule : Module, IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        // This method is intentionally left empty.
        // The services should be configured in the Autofac module itself.
    }

    /// <inheritdoc />
    public virtual void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        // This method should be overridden in derived classes to register any dependent modules.
    }
}
