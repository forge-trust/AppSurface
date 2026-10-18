using System.Reflection;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Shares descriptor ownership and attributed metadata rules between explicit selection and discovery.</summary>
/// <remarks>
/// Registration inspects the current collection without mutable selection markers or provider probes. Expected
/// shape, compatibility, conflict, and metadata failures precede helper additions. This is not transactional
/// rollback for a custom collection's Add failure or a catastrophic allocation failure.
/// </remarks>
internal static class ConfigTypeRegistration
{
    /// <summary>Prepares and registers a selected wrapper without activation or logical-key parsing.</summary>
    /// <param name="services">The current host or cloned worker-stage collection.</param>
    /// <param name="configType">A closed concrete IConfig class with a public instance constructor.</param>
    /// <exception cref="ArgumentNullException">Either input is null.</exception>
    /// <exception cref="ArgumentException">The type shape or constructor visibility is unsupported.</exception>
    /// <exception cref="AppSurfacePackageCompatibilityException">Selected assembly metadata is incompatible.</exception>
    /// <exception cref="InvalidOperationException">Current exact unkeyed wrapper descriptors conflict.</exception>
    /// <remarks>
    /// The compatibility guard precedes constructor and traversal reflection; discovery also guards before
    /// DefinedTypes. CLR type loading before this call is outside that guard. One caller singleton is preserved
    /// without framework Init. Known unkeyed attributed instance declarations deduplicate by wrapper type and
    /// declaration kind; manual declarations and opaque factories remain independent. Invalid traversal limits
    /// are copied for the existing audit diagnostic/fallback boundary. No public enclosing-type visibility is required.
    /// </remarks>
    internal static void Register(IServiceCollection services, Type configType)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configType);

        if (!configType.IsClass || configType.IsAbstract || configType.ContainsGenericParameters
            || !typeof(IConfig).IsAssignableFrom(configType))
        {
            throw InvalidShape(configType);
        }

        ConfigPackageCompatibility.ValidateAssemblies([configType.Assembly]);
        if (configType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length == 0)
        {
            throw InvalidShape(configType);
        }

        var descriptors = services.Where(descriptor =>
            descriptor.ServiceType == configType && !descriptor.IsKeyedService).ToArray();
        if (descriptors.Length > 1
            || (descriptors.Length == 1 && descriptors[0].Lifetime != ServiceLifetime.Singleton))
        {
            throw new InvalidOperationException(
                "Code: config-registration-conflict\n" +
                $"Problem: Configuration wrapper '{ConfigDiagnosticText.Identifier(configType.FullName)}' has conflicting registrations.\n" +
                $"Cause: Found {descriptors.Length} exact unkeyed descriptor(s) with lifetime(s): {string.Join(", ", descriptors.Select(descriptor => descriptor.Lifetime))}.\n" +
                "Fix: Keep exactly one unkeyed singleton or remove the conflicting descriptors before registration. Use singleton Replace only on a valid collection.\n" +
                $"Docs: {ConfigDiagnosticCatalog.Reference}");
        }

        ConfigAuditRawDeclaration? declaration = null;
        var alreadyDeclared = services.Any(descriptor =>
            descriptor.ServiceType == typeof(ConfigAuditRawDeclaration)
            && !descriptor.IsKeyedService
            && descriptor.ImplementationInstance is ConfigAuditRawDeclaration
            {
                IsAttributeDeclaration: true
            } existing
            && existing.ConfigType == configType);
        if (!alreadyDeclared)
        {
            var options = configType.GetCustomAttribute<ConfigAuditCollectionTraversalAttribute>(inherit: true)?.ToOptions();
            declaration = new ConfigAuditRawDeclaration(
                RawKey: null,
                ConfigType: configType,
                ValueType: GetValueType(configType),
                Options: new ConfigAuditEntryOptions(options),
                IsAttributeDeclaration: true);
        }

        ConfigAuditServiceCollectionExtensions.EnsureDeclarationInfrastructure(services);
        if (descriptors.Length == 0)
        {
            services.AddSingleton(configType, provider =>
            {
                var key = provider.GetRequiredService<ConfigDeclarationRegistry>().GetForConfigType(configType).LogicalKey;
                var wrapper = (IConfig)ActivatorUtilities.CreateInstance(provider, configType);
                wrapper.Init(
                    provider.GetRequiredService<IConfigManager>(),
                    provider.GetRequiredService<IEnvironmentProvider>(),
                    key);
                return wrapper;
            });
        }

        if (declaration is not null)
        {
            services.AddSingleton(declaration);
        }
    }

    private static ArgumentException InvalidShape(Type configType) =>
        new($"Configuration type '{ConfigDiagnosticText.Identifier(configType.FullName)}' must be a closed, concrete IConfig class with a public instance constructor.",
            nameof(configType));

    private static Type GetValueType(Type configType)
    {
        for (var current = configType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && (current.GetGenericTypeDefinition() == typeof(Config<>)
                    || current.GetGenericTypeDefinition() == typeof(ConfigStruct<>)))
            {
                return current.GetGenericArguments()[0];
            }
        }

        return typeof(object);
    }
}
