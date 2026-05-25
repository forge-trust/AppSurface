using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes a configuration entry known to AppSurface's audit system.
/// </summary>
public sealed class ConfigAuditKnownEntry
{
    private readonly ConfigAuditEntryOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditKnownEntry"/> class.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="configType">The config wrapper type, when one exists.</param>
    /// <param name="valueType">The declared value type.</param>
    public ConfigAuditKnownEntry(string key, Type? configType, Type valueType)
        : this(key, configType, valueType, options: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditKnownEntry"/> class with audit entry options.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="configType">The config wrapper type, when one exists.</param>
    /// <param name="valueType">The declared value type.</param>
    /// <param name="options">The entry-specific audit options. The entry snapshots these values.</param>
    public ConfigAuditKnownEntry(string key, Type? configType, Type valueType, ConfigAuditEntryOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueType);

        Key = key;
        ConfigType = configType;
        ValueType = valueType;
        _options = new ConfigAuditEntryOptions(options);
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

    /// <summary>
    /// Gets a copy of the audit entry options captured for this known entry.
    /// </summary>
    /// <remarks>
    /// The returned options object is immutable. Configure options during registration by using
    /// <see cref="ConfigAuditServiceCollectionExtensions.AddConfigAuditKey{T}(IServiceCollection,string,Action{ConfigAuditEntryOptionsBuilder})"/>
    /// or the constructor overload that accepts <see cref="ConfigAuditEntryOptions"/>.
    /// </remarks>
    public ConfigAuditEntryOptions Options => new(_options);

    internal ConfigAuditEntryOptions OptionsSnapshot => _options;

    internal bool HasNonDefaultOptions => !_options.HasDefaultValues;

    internal ConfigAuditKnownEntry WithOptions(ConfigAuditEntryOptions options) =>
        new(Key, ConfigType, ValueType, options);
}

/// <summary>
/// Controls optional expansion behavior for one configuration audit entry.
/// </summary>
/// <remarks>
/// Defaults preserve the original audit behavior: object members are reported, collection parent values remain opaque,
/// and collection elements are not traversed unless <see cref="TraverseCollectionElements"/> is enabled. Instances are
/// copied by <see cref="ConfigAuditKnownEntry"/>, so registration captures a stable snapshot. Use
/// <see cref="ConfigAuditEntryOptionsBuilder"/> with the service-collection registration callback when mutable
/// callback configuration is more convenient than an object initializer.
/// </remarks>
public sealed class ConfigAuditEntryOptions
{
    internal const int DefaultMaxCollectionDepth = 4;
    internal const int DefaultMaxCollectionElements = 128;
    internal const int DefaultMaxReportNodes = 4096;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditEntryOptions"/> class with safe defaults.
    /// </summary>
    public ConfigAuditEntryOptions()
    {
    }

    internal ConfigAuditEntryOptions(ConfigAuditEntryOptions? source)
    {
        if (source == null)
        {
            return;
        }

        TraverseCollectionElements = source.TraverseCollectionElements;
        MaxCollectionDepth = source.MaxCollectionDepth;
        MaxCollectionElements = source.MaxCollectionElements;
        MaxReportNodes = source.MaxReportNodes;
        DisplayDictionaryKeys = source.DisplayDictionaryKeys;
    }

    /// <summary>
    /// Gets a value indicating whether arrays, lists, and dictionaries should emit child element entries.
    /// </summary>
    public bool TraverseCollectionElements { get; init; }

    /// <summary>
    /// Gets the maximum nested collection depth traversed when collection traversal is enabled.
    /// </summary>
    public int MaxCollectionDepth { get; init; } = DefaultMaxCollectionDepth;

    /// <summary>
    /// Gets the maximum number of elements reported from any one traversed collection.
    /// </summary>
    public int MaxCollectionElements { get; init; } = DefaultMaxCollectionElements;

    /// <summary>
    /// Gets the maximum number of child nodes created for this entry before traversal stops.
    /// </summary>
    public int MaxReportNodes { get; init; } = DefaultMaxReportNodes;

    /// <summary>
    /// Gets a value indicating whether non-sensitive dictionary keys may appear as element labels.
    /// </summary>
    public bool DisplayDictionaryKeys { get; init; } = true;

    internal bool HasDefaultValues =>
        !TraverseCollectionElements
        && MaxCollectionDepth == DefaultMaxCollectionDepth
        && MaxCollectionElements == DefaultMaxCollectionElements
        && MaxReportNodes == DefaultMaxReportNodes
        && DisplayDictionaryKeys;

    internal IReadOnlyList<ConfigAuditDiagnostic> Validate(string key)
    {
        var diagnostics = new List<ConfigAuditDiagnostic>();
        if (MaxCollectionDepth < 0)
        {
            diagnostics.Add(CreateInvalidOptionDiagnostic(key, nameof(MaxCollectionDepth), "must be greater than or equal to 0"));
        }

        if (MaxCollectionElements < 0)
        {
            diagnostics.Add(CreateInvalidOptionDiagnostic(key, nameof(MaxCollectionElements), "must be greater than or equal to 0"));
        }

        if (MaxReportNodes < 1)
        {
            diagnostics.Add(CreateInvalidOptionDiagnostic(key, nameof(MaxReportNodes), "must be greater than or equal to 1"));
        }

        return diagnostics;
    }

    internal ConfigAuditEntryOptions Normalize() =>
        Validate("unused").Count == 0
            ? new ConfigAuditEntryOptions(this)
            : new ConfigAuditEntryOptions
            {
                TraverseCollectionElements = TraverseCollectionElements,
                DisplayDictionaryKeys = DisplayDictionaryKeys
            };

    private static ConfigAuditDiagnostic CreateInvalidOptionDiagnostic(string key, string optionName, string rule) =>
        new()
        {
            Severity = ConfigAuditDiagnosticSeverity.Error,
            Code = "config-audit-options-invalid",
            Key = key,
            ConfigPath = key,
            Message = $"Config audit option {optionName} for '{key}' is invalid: {rule}."
        };
}

/// <summary>
/// Mutable builder used by registration callbacks to create immutable <see cref="ConfigAuditEntryOptions"/>.
/// </summary>
/// <remarks>
/// The builder exists only for configuration ergonomics. AppSurface snapshots it into immutable options when
/// registering the audit key, so later builder mutations cannot affect reports.
/// </remarks>
public sealed class ConfigAuditEntryOptionsBuilder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditEntryOptionsBuilder"/> class with safe defaults.
    /// </summary>
    public ConfigAuditEntryOptionsBuilder()
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether arrays, lists, and dictionaries should emit child element entries.
    /// </summary>
    public bool TraverseCollectionElements { get; set; }

    /// <summary>
    /// Gets or sets the maximum nested collection depth traversed when collection traversal is enabled.
    /// </summary>
    public int MaxCollectionDepth { get; set; } = ConfigAuditEntryOptions.DefaultMaxCollectionDepth;

    /// <summary>
    /// Gets or sets the maximum number of elements reported from any one traversed collection.
    /// </summary>
    public int MaxCollectionElements { get; set; } = ConfigAuditEntryOptions.DefaultMaxCollectionElements;

    /// <summary>
    /// Gets or sets the maximum number of child nodes created for this entry before traversal stops.
    /// </summary>
    public int MaxReportNodes { get; set; } = ConfigAuditEntryOptions.DefaultMaxReportNodes;

    /// <summary>
    /// Gets or sets a value indicating whether non-sensitive dictionary keys may appear as element labels.
    /// </summary>
    public bool DisplayDictionaryKeys { get; set; } = true;

    internal ConfigAuditEntryOptions ToOptions() =>
        new()
        {
            TraverseCollectionElements = TraverseCollectionElements,
            MaxCollectionDepth = MaxCollectionDepth,
            MaxCollectionElements = MaxCollectionElements,
            MaxReportNodes = MaxReportNodes,
            DisplayDictionaryKeys = DisplayDictionaryKeys
        };
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
        return AddConfigAuditKey<T>(services, key, configure: null);
    }

    /// <summary>
    /// Registers an additional configuration key for audit reports with entry-specific options.
    /// </summary>
    /// <remarks>
    /// Use <paramref name="configure"/> to opt into collection element traversal for this key only. Options are
    /// snapshotted when the registration is created; collection traversal remains disabled by default so existing
    /// reports keep their previous shape unless callers explicitly enable it.
    /// </remarks>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="configure">A callback that customizes the audit options for this key.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    public static IServiceCollection AddConfigAuditKey<T>(
        this IServiceCollection services,
        string key,
        Action<ConfigAuditEntryOptionsBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var options = new ConfigAuditEntryOptionsBuilder();
        configure?.Invoke(options);

        services.AddSingleton(new ConfigAuditKnownEntry(key, configType: null, typeof(T), options.ToOptions()));
        return services;
    }
}
