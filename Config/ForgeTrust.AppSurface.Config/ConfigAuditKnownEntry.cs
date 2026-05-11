using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes a configuration entry known to AppSurface's audit system.
/// </summary>
public sealed class ConfigAuditKnownEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditKnownEntry"/> class.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="configType">The config wrapper type, when one exists.</param>
    /// <param name="valueType">The declared value type.</param>
    public ConfigAuditKnownEntry(string key, Type? configType, Type valueType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueType);

        Key = key;
        ConfigType = configType;
        ValueType = valueType;
    }

    /// <summary>
    /// Gets the configuration key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the config wrapper type, when this entry came from a wrapper.
    /// </summary>
    public Type? ConfigType { get; }

    /// <summary>
    /// Gets the declared value type.
    /// </summary>
    public Type ValueType { get; }
}

/// <summary>
/// Extension methods for registering additional configuration audit keys.
/// </summary>
public static class ConfigAuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers an additional configuration key for audit reports.
    /// </summary>
    /// <remarks>
    /// Use this method for values that should appear in audit reports but are not backed by an <see cref="IConfig"/>
    /// or <see cref="Config{T}"/> wrapper discovered by assembly scanning. Prefer wrapper discovery when a typed
    /// wrapper already exists, because the wrapper can contribute defaults and validation diagnostics. Manual
    /// registration creates a <see cref="ConfigAuditKnownEntry"/> with <see cref="ConfigAuditKnownEntry.ConfigType"/>
    /// set to <see langword="null"/> and <typeparamref name="T"/> as the expected value type; for example,
    /// <c>AddConfigAuditKey&lt;Uri&gt;("Billing.Endpoint")</c> includes a provider-only key in reports.
    /// </remarks>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    public static IServiceCollection AddConfigAuditKey<T>(this IServiceCollection services, string key)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        services.AddSingleton(new ConfigAuditKnownEntry(key, configType: null, typeof(T)));
        return services;
    }
}
