using System.Reflection;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// A module that registers configuration management services and automatically discovers and registers configuration objects.
/// </summary>
/// <remarks>
/// <see cref="AppSurfaceConfigModule"/> registers core configuration services immediately, including audit reporting,
/// diagnostics, sanitized diff comparison, diff rendering, and command-runner helpers, then defers typed
/// <see cref="IConfig"/> discovery through <see cref="StartupContext.CustomRegistrations"/> so all module
/// dependencies are known first. The deferred scan inspects dependency module assemblies, the entry assembly, and the
/// root module assembly. Pitfall: config dependencies should be registered before the custom registration callback
/// runs; otherwise discovered config objects may activate before their supporting services are available.
/// </remarks>
public class AppSurfaceConfigModule : IAppSurfaceModule
{
    /// <summary>
    /// Registers AppSurface config services and schedules typed config discovery after module registration.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigureServices(StartupContext, IServiceCollection)"/> adds the default manager, providers,
    /// audit reporter, audit redactor, text renderer, diagnostics runner, file-location provider, and sanitized diff
    /// services, then appends a <see cref="StartupContext.CustomRegistrations"/> callback. The diff registrations include
    /// <see cref="ConfigAuditReportDiffer"/> for pure typed snapshot comparison, <see cref="ConfigAuditDiffTextRenderer"/>
    /// for deterministic operator output, and <see cref="ConfigAuditDiffCommandRunner"/> for command-framework-agnostic
    /// same-host and captured-snapshot workflows. The custom callback scans dependency, entry, and root-module assemblies
    /// for concrete <see cref="IConfig"/> implementations and registers each as a singleton initialized from
    /// <see cref="IConfigManager"/> and <see cref="IEnvironmentProvider"/>. Wrappers decorated with
    /// <see cref="ConfigAuditCollectionTraversalAttribute"/> also contribute audit traversal options for their key.
    /// </remarks>
    /// <param name="context">Startup context that supplies assemblies, dependency modules, and the custom registration log.</param>
    /// <param name="services">Service collection that receives the default configuration services.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<IConfigManager, DefaultConfigManager>();
        services.AddSingleton<IConfigAuditReporter, ConfigAuditReporter>();
        services.AddOptions<ConfigAuditDictionaryKeyCorrelationOptions>();
        services.AddSingleton<ConfigDiagnosticsCommandRunner>();
        services.AddSingleton<ConfigAuditDiffCommandRunner>();
        services.AddSingleton<ConfigAuditRedactor>();
        services.AddSingleton<ConfigAuditTextRenderer>();
        services.AddSingleton<ConfigAuditReportDiffer>();
        services.AddSingleton<ConfigAuditDiffTextRenderer>();
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
            var auditOptions = type.GetCustomAttribute<ConfigAuditCollectionTraversalAttribute>(inherit: true)
                ?.ToOptions();
            services.AddSingleton(new ConfigAuditKnownEntry(
                ConfigKeyAttribute.GetKeyPath(type),
                type,
                GetConfigValueType(type),
                auditOptions));
        }
    }

    private static Type GetConfigValueType(Type type)
    {
        var current = type;
        while (current != null)
        {
            if (current.IsGenericType
                && (current.GetGenericTypeDefinition() == typeof(Config<>)
                    || current.GetGenericTypeDefinition() == typeof(ConfigStruct<>)))
            {
                return current.GetGenericArguments()[0];
            }

            current = current.BaseType;
        }

        return typeof(object);
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
