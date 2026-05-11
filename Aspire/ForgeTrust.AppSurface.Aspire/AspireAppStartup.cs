using System.Reflection;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Aspire;

internal class AspireAppStartup<TModule> : ConsoleStartup<TModule>
    where TModule : IAppSurfaceHostModule, new()
{
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
