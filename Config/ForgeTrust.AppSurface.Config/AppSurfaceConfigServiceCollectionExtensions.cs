using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Selects individual configuration wrappers, including wrappers outside module discovery.</summary>
/// <remarks>
/// Use this API with the normal Config host composition when a shared contract assembly is outside the module,
/// root, and entry assembly scan. See the
/// <see href="https://appsurface.dev/guides/config-logical-keys">logical-key and registration contract</see>.
/// </remarks>
public static class AppSurfaceConfigServiceCollectionExtensions
{
    /// <summary>Registers one wrapper's lazy singleton and attributed audit declaration.</summary>
    /// <typeparam name="TConfig">A closed concrete configuration class with a public instance constructor.</typeparam>
    /// <param name="services">The host's collection, before its final provider is built.</param>
    /// <returns>The same collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentException">The selected class is abstract or has no public instance constructor.</exception>
    /// <exception cref="Core.AppSurfacePackageCompatibilityException">The selected assembly uses an incompatible contract.</exception>
    /// <exception cref="InvalidOperationException">The current unkeyed wrapper descriptors are duplicated or non-singleton.</exception>
    /// <remarks>
    /// Repeated selection and module discovery share registration rules. One existing exact unkeyed singleton is
    /// preserved unchanged; its caller owns initialization, validation, and agreement with the attributed key.
    /// Keyed descriptors coexist independently. Later singleton Replace is supported on a valid collection;
    /// arbitrary later duplicate appends are unsupported and rejected on the next registration call.
    /// This method does not build a provider, parse keys, construct wrappers, call Init, or read providers or secrets.
    /// It ensures declaration/parser/options infrastructure; the host supplies IConfigManager, Core.IEnvironmentProvider,
    /// providers, and singleton-safe constructor dependencies. Finalized host options determine the logical key.
    /// At resolution, ActivatorUtilities selects the constructor and the framework initializes its own wrapper once
    /// per successful singleton activation per provider. Dependency, value, validation, factory, and cancellation
    /// failures retain their normal exception behavior. Audit constructs a separate inspection wrapper and may read
    /// providers; it does not initialize or inspect arbitrary state on a caller-owned singleton.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceConfig<TConfig>(this IServiceCollection services)
        where TConfig : class, IConfig
    {
        ConfigTypeRegistration.Register(services, typeof(TConfig));
        return services;
    }
}
