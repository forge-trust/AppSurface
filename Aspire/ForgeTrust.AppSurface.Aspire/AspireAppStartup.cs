using System.Reflection;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Starts an Aspire AppHost with an AppSurface root module that implements <see cref="IAppSurfaceHostModule"/>.
/// </summary>
/// <remarks>
/// <see cref="AspireAppStartup{TModule}"/> is the Aspire-specific bootstrapper behind
/// <see cref="AspireApp{TModule}.RunAsync(string[])"/>. The <c>new()</c> constraint activates the root module with a
/// parameterless constructor before dependency injection is available, so module constructors should avoid service
/// resolution, blocking work, and disposable ownership. Host-module service registrations run through
/// <see cref="ConsoleStartup{TModule}"/> before <see cref="ConfigureAdditionalServices"/> discovers Aspire components.
/// </remarks>
/// <typeparam name="TModule">The root AppSurface host module type for the Aspire AppHost.</typeparam>
internal class AspireAppStartup<TModule> : ConsoleStartup<TModule>
    where TModule : IAppSurfaceHostModule, new()
{
    /// <summary>
    /// Discovers <see cref="IAspireComponent"/> types from the entry assembly and registers each concrete type as a singleton.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigureAdditionalServices"/> runs after the root <see cref="IAppSurfaceHostModule"/> and dependency
    /// modules have registered services. Components are registered by concrete type only, not by implemented
    /// interfaces, so consumers should resolve concrete component types directly. The registration is startup-only and
    /// does not transfer disposal ownership beyond normal container ownership.
    /// </remarks>
    /// <param name="context">The AppSurface startup context that supplies the entry assembly to scan.</param>
    /// <param name="services">The service collection receiving discovered Aspire component registrations.</param>
    protected override void ConfigureAdditionalServices(StartupContext context, IServiceCollection services)
    {
        var componentTypes = GetComponentTypes(context.EntryPointAssembly);

        foreach (var type in componentTypes)
        {
            // We want to ensure that each component is registered as a singleton
            // so that we don't have multiple instances of the same component.
            // We are currently only registering the concrete type, not any interfaces,
            // which matches our current expectations for how components are used.
            services.AddSingleton(type);
        }
    }

    private IReadOnlyList<Type> GetComponentTypes(Assembly hostAssembly)
    {
        var componentTypes = hostAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IAspireComponent).IsAssignableFrom(t))
            .ToList();

        return componentTypes;
    }
}
