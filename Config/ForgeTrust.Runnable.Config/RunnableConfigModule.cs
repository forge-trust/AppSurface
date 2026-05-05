using System.Reflection;
using ForgeTrust.Runnable.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.Runnable.Config;

/// <summary>
/// A module that registers configuration management services and automatically discovers and registers configuration objects.
/// </summary>
public class RunnableConfigModule : IRunnableModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<IConfigManager, DefaultConfigManager>();
        services.AddSingleton<IEnvironmentConfigProvider, EnvironmentConfigProvider>();
        services.AddSingleton<IConfigFileLocationProvider, DefaultConfigFileLocationProvider>();
        services.AddSingleton<IConfigProvider, FileBasedConfigProvider>();

        // Execute the config registration log from the CustomRegistrations
        // because it needs to be done after all modules have been registered
        context.CustomRegistrations.Add(sp =>
        {
            var distinctAssemblies = context.GetDependencies()
                .Select(x => x.GetType().Assembly)
                .Append(context.EntryPointAssembly)
                .Append(context.RootModuleAssembly)
                .Distinct();

            foreach (var assembly in distinctAssemblies)
            {
                RegisterConfigFromAssembly(assembly, sp);
            }
        });
    }

    private void RegisterConfigFromAssembly(Assembly assembly, IServiceCollection services)
    {
        var configTypes = assembly.DefinedTypes
            .Where(t => !t.IsAbstract && !t.IsInterface && !t.ContainsGenericParameters)
            .Where(t => typeof(IConfig).IsAssignableFrom(t.AsType()))
            .Select(t => t.AsType())
            .Distinct()
            .ToList();

        foreach (var type in configTypes)
        {
            services.AddSingleton(
                type,
                sp =>
                {
                    var key = ConfigKeyAttribute.GetKeyPath(type);
                    var instance = (IConfig)ActivatorUtilities.CreateInstance(sp, type);
                    instance.Init(
                        sp.GetRequiredService<IConfigManager>(),
                        sp.GetRequiredService<IEnvironmentProvider>(),
                        key);

                    return instance;
                });
        }
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
