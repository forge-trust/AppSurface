using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Defines a module that can configure services and register dependencies within the AppSurface host.
/// </summary>
/// <remarks>
/// Implement <see cref="IAppSurfaceModule"/> for reusable service-registration units that are not responsible for host
/// lifecycle hooks. Create host-specific abstractions, or implement <see cref="IAppSurfaceHostModule"/>, when a module
/// must configure hosting, middleware, endpoints, or other lifecycle behavior. Module methods run during startup; keep
/// them idempotent, avoid heavy side effects and global state mutation, and throw clear configuration exceptions when
/// required dependencies are missing.
/// </remarks>
public interface IAppSurfaceModule
{
    /// <summary>
    /// Configures the services for this module.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigureServices"/> should add deterministic registrations to <paramref name="services"/> and should
    /// tolerate being called during host construction before the application is running. Do not resolve runtime services
    /// or perform blocking I/O here unless the module explicitly documents that startup cost.
    /// </remarks>
    /// <param name="context">The context for the current startup process.</param>
    /// <param name="services">The service collection to add registrations to.</param>
    void ConfigureServices(StartupContext context, IServiceCollection services);

    /// <summary>
    /// Registers other modules that this module depends on.
    /// </summary>
    /// <remarks>
    /// Dependencies should be declared before their services are required. <see cref="ModuleDependencyBuilder"/> handles
    /// duplicate module types and pre-registers modules to avoid recursive cycles, but modules should still avoid
    /// relying on incidental traversal order for behavior.
    /// </remarks>
    /// <param name="builder">The builder used to declare module dependencies.</param>
    void RegisterDependentModules(ModuleDependencyBuilder builder);
}
