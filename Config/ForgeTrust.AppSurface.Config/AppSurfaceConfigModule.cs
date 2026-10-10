using System.Reflection;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// A module that registers configuration management services and automatically discovers and registers configuration objects.
/// </summary>
/// <remarks>
/// <see cref="AppSurfaceConfigModule"/> registers core configuration services immediately, including audit reporting,
/// diagnostics, sanitized diff comparison, diff rendering, and command-runner helpers, then defers typed
/// <see cref="IConfig"/> discovery through <see cref="StartupContext.CustomRegistrations"/> so all module
/// dependencies are known first. The deferred scan inspects dependency module assemblies, the entry assembly, and the
/// root module assembly. Discovery records lazy singletons without activating wrappers. Register singleton-safe
/// constructor dependencies before building the final provider; they resolve when a wrapper is first requested.
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
    /// for concrete <see cref="IConfig"/> implementations through the same registrar as
    /// <see cref="AppSurfaceConfigServiceCollectionExtensions.AddAppSurfaceConfig{TConfig}"/>. Each lazy singleton initializes from
    /// <see cref="IConfigManager"/> and <see cref="IEnvironmentProvider"/>. Wrappers decorated with
    /// <see cref="ConfigAuditCollectionTraversalAttribute"/> also contribute audit traversal options for their key.
    /// Each assembly is checked against the
    /// <see href="https://appsurface.dev/guides/config-key-migration">package compatibility contract</see>
    /// before reflecting over its types, including when callers invoke custom registration directly.
    /// </remarks>
    /// <param name="context">Startup context that supplies assemblies, dependency modules, and the custom registration log.</param>
    /// <param name="services">Service collection that receives the default configuration services.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<IConfigManager, DefaultConfigManager>();
        services.AddOptions<AppSurfaceConfigOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AppSurfaceConfigOptions>, AppSurfaceConfigOptionsValidator>());
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(sp => new ConfigCompositionEngine(
            sp.GetRequiredService<IEnvironmentConfigProvider>(), sp.GetServices<IConfigProvider>(),
            sp.GetServices<IConfigSecretProvider>(), sp.GetServices<IConfigSecretDeclarationSource>(),
            sp.GetRequiredService<IOptions<AppSurfaceConfigOptions>>().Value, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHostedService, ConfigCompositionStartupValidator>();
        ConfigAuditServiceCollectionExtensions.EnsureDeclarationInfrastructure(services);
        services.AddOptions<ConfigResourceOptions>()
            .Validate(options =>
            {
                _ = options.Snapshot();
                return true;
            }, "Configuration resource limits must be positive.")
            .ValidateOnStart();
        services.AddOptions<AppSurfaceEnvironmentConfigOptions>();
        services.AddOptions<ConfigEnvironmentStartupOptions>()
            .Validate<IEnvironmentConfigProvider>((_, _) => true,
                "Environment configuration mappings must have unambiguous native identities.")
            .ValidateOnStart();
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
        services.AddSingleton<FileBasedConfigProvider>();
        services.AddSingleton<IConfigProvider>(sp => sp.GetRequiredService<FileBasedConfigProvider>());
        services.AddSingleton<IConfigCompositionValueProvider>(sp => sp.GetRequiredService<FileBasedConfigProvider>());

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
        ConfigPackageCompatibility.ValidateAssemblies([assembly]);
        var configTypes = assembly.DefinedTypes
            .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters)
            .Where(t => typeof(IConfig).IsAssignableFrom(t.AsType()))
            .Select(t => t.AsType())
            .Distinct()
            .ToList();

        foreach (var type in configTypes)
        {
            ConfigTypeRegistration.Register(services, type);
        }
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
