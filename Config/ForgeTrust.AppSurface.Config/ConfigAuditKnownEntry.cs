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
/// callback configuration is more convenient than an object initializer. When entries with the same key are merged,
/// explicitly assigned manual options override wrapper-discovered options one property at a time.
/// </remarks>
public sealed class ConfigAuditEntryOptions
{
    internal const int DefaultMaxCollectionDepth = 4;
    internal const int DefaultMaxCollectionElements = 128;
    internal const int DefaultMaxReportNodes = 4096;

    private bool _traverseCollectionElements;
    private int _maxCollectionDepth = DefaultMaxCollectionDepth;
    private int _maxCollectionElements = DefaultMaxCollectionElements;
    private int _maxReportNodes = DefaultMaxReportNodes;
    private bool _displayDictionaryKeys = true;
    private ConfigAuditSensitivity _sensitivity = ConfigAuditSensitivity.Unknown;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditEntryOptions"/> class with safe defaults.
    /// </summary>
    public ConfigAuditEntryOptions()
    {
    }

    /// <summary>
    /// Initializes a new immutable snapshot from an existing options object.
    /// </summary>
    /// <remarks>
    /// This constructor copies option values and assignment flags at the time it is called. Later mutations to the
    /// source reference cannot affect the snapshot used by <see cref="ConfigAuditKnownEntry"/> or audit reporting.
    /// </remarks>
    /// <param name="source">The options to copy, or <see langword="null"/> to keep safe defaults.</param>
    internal ConfigAuditEntryOptions(ConfigAuditEntryOptions? source)
    {
        if (source == null)
        {
            return;
        }

        _traverseCollectionElements = source.TraverseCollectionElements;
        _maxCollectionDepth = source.MaxCollectionDepth;
        _maxCollectionElements = source.MaxCollectionElements;
        _maxReportNodes = source.MaxReportNodes;
        _displayDictionaryKeys = source.DisplayDictionaryKeys;
        _sensitivity = source.Sensitivity;
        AssignedOptions = source.AssignedOptions;
    }

    /// <summary>
    /// Initializes a new immutable options snapshot with explicit values and assignment flags.
    /// </summary>
    /// <remarks>
    /// The values are stored as provided so callers can preserve invalid wrapper or manual inputs until validation
    /// emits diagnostics. The assignment flags are part of the merge contract: they describe which properties were
    /// intentionally assigned, not whether those values differ from defaults.
    /// </remarks>
    /// <param name="traverseCollectionElements">Whether collection elements should be traversed.</param>
    /// <param name="maxCollectionDepth">The maximum nested collection depth.</param>
    /// <param name="maxCollectionElements">The maximum number of elements per collection.</param>
    /// <param name="maxReportNodes">The maximum number of child nodes reported for the entry.</param>
    /// <param name="displayDictionaryKeys">Whether non-sensitive dictionary keys may be displayed.</param>
    /// <param name="sensitivity">The entry-level sensitivity classification.</param>
    /// <param name="assignedOptions">The properties intentionally assigned by the source.</param>
    internal ConfigAuditEntryOptions(
        bool traverseCollectionElements,
        int maxCollectionDepth,
        int maxCollectionElements,
        int maxReportNodes,
        bool displayDictionaryKeys,
        ConfigAuditSensitivity sensitivity,
        ConfigAuditEntryOptionAssignments assignedOptions)
    {
        _traverseCollectionElements = traverseCollectionElements;
        _maxCollectionDepth = maxCollectionDepth;
        _maxCollectionElements = maxCollectionElements;
        _maxReportNodes = maxReportNodes;
        _displayDictionaryKeys = displayDictionaryKeys;
        _sensitivity = sensitivity;
        AssignedOptions = assignedOptions;
    }

    /// <summary>
    /// Gets a value indicating whether arrays, lists, and dictionaries should emit child element entries.
    /// </summary>
    public bool TraverseCollectionElements
    {
        get => _traverseCollectionElements;
        init
        {
            _traverseCollectionElements = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.TraverseCollectionElements;
        }
    }

    /// <summary>
    /// Gets the maximum nested collection depth traversed when collection traversal is enabled.
    /// </summary>
    public int MaxCollectionDepth
    {
        get => _maxCollectionDepth;
        init
        {
            _maxCollectionDepth = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.MaxCollectionDepth;
        }
    }

    /// <summary>
    /// Gets the maximum number of elements reported from any one traversed collection.
    /// </summary>
    public int MaxCollectionElements
    {
        get => _maxCollectionElements;
        init
        {
            _maxCollectionElements = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.MaxCollectionElements;
        }
    }

    /// <summary>
    /// Gets the maximum number of child nodes created for this entry before traversal stops.
    /// </summary>
    public int MaxReportNodes
    {
        get => _maxReportNodes;
        init
        {
            _maxReportNodes = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.MaxReportNodes;
        }
    }

    /// <summary>
    /// Gets a value indicating whether non-sensitive dictionary keys may appear as element labels.
    /// </summary>
    public bool DisplayDictionaryKeys
    {
        get => _displayDictionaryKeys;
        init
        {
            _displayDictionaryKeys = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.DisplayDictionaryKeys;
        }
    }

    /// <summary>
    /// Gets the entry-level sensitivity classification used by audit redaction.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ConfigAuditSensitivity.Unknown"/>. <see cref="ConfigAuditSensitivity.Sensitive"/>
    /// redacts the root value, traversed child values, and value-derived dictionary labels before report models are
    /// returned. <see cref="ConfigAuditSensitivity.NonSensitive"/> is a classification hint only: it never disables
    /// redaction from sensitive key fragments, source metadata, provider metadata, or another registration that marks
    /// the same entry sensitive. Invalid enum values emit a <c>config-audit-options-invalid</c> diagnostic and fail
    /// closed as sensitive during report generation.
    /// </remarks>
    public ConfigAuditSensitivity Sensitivity
    {
        get => _sensitivity;
        init
        {
            _sensitivity = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.Sensitivity;
        }
    }

    /// <summary>
    /// Gets the option properties that were intentionally assigned by the source registration.
    /// </summary>
    /// <remarks>
    /// Assignment tracking is independent from value comparison. For example, manually assigning
    /// <see cref="MaxCollectionDepth"/> to its default value still sets the corresponding flag and therefore
    /// overrides a wrapper attribute's custom depth during duplicate-registration merging.
    /// </remarks>
    internal ConfigAuditEntryOptionAssignments AssignedOptions { get; init; }

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

        if (!IsValidSensitivity(Sensitivity))
        {
            diagnostics.Add(
                CreateInvalidOptionDiagnostic(
                    key,
                    nameof(Sensitivity),
                    $"value '{Convert.ToInt32(Sensitivity, System.Globalization.CultureInfo.InvariantCulture)}' is not valid; use {ConfigAuditSensitivity.Unknown}, {ConfigAuditSensitivity.NonSensitive}, or {ConfigAuditSensitivity.Sensitive}. Report generation falls back to {ConfigAuditSensitivity.Sensitive} so the entry does not fail open"));
        }

        return diagnostics;
    }

    /// <summary>
    /// Returns options with invalid traversal limits replaced by safe defaults.
    /// </summary>
    /// <remarks>
    /// Normalization preserves <see cref="TraverseCollectionElements"/>, <see cref="DisplayDictionaryKeys"/>, and
    /// <see cref="AssignedOptions"/>. Invalid <see cref="Sensitivity"/> values fail closed as
    /// <see cref="ConfigAuditSensitivity.Sensitive"/> after diagnostics have captured the bad value. It is intended
    /// for report generation after diagnostics have captured invalid inputs; it should not be used as a signal that an
    /// option was unassigned.
    /// </remarks>
    internal ConfigAuditEntryOptions Normalize() =>
        new(
            TraverseCollectionElements,
            MaxCollectionDepth >= 0 ? MaxCollectionDepth : DefaultMaxCollectionDepth,
            MaxCollectionElements >= 0 ? MaxCollectionElements : DefaultMaxCollectionElements,
            MaxReportNodes >= 1 ? MaxReportNodes : DefaultMaxReportNodes,
            DisplayDictionaryKeys,
            NormalizeSensitivity(Sensitivity),
            AssignedOptions);

    /// <summary>
    /// Applies explicitly assigned option values from a later registration over this options snapshot.
    /// </summary>
    /// <remarks>
    /// Merging happens per property. A property in <paramref name="overrides"/> wins only when its assignment flag is
    /// present, and it wins even when the overriding value equals the default. This preserves duplicate-registration
    /// precedence where wrapper-discovered options provide a complete policy, while manual provider options can
    /// intentionally reset any individual setting back to a default value.
    /// </remarks>
    /// <param name="overrides">The options whose assigned properties should override this snapshot.</param>
    /// <returns>A new options snapshot containing merged values and the union of assignment flags.</returns>
    internal ConfigAuditEntryOptions ApplyAssignedOverrides(ConfigAuditEntryOptions overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        return new ConfigAuditEntryOptions(
            overrides.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.TraverseCollectionElements)
                ? overrides.TraverseCollectionElements
                : TraverseCollectionElements,
            overrides.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.MaxCollectionDepth)
                ? overrides.MaxCollectionDepth
                : MaxCollectionDepth,
            overrides.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.MaxCollectionElements)
                ? overrides.MaxCollectionElements
                : MaxCollectionElements,
            overrides.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.MaxReportNodes)
                ? overrides.MaxReportNodes
                : MaxReportNodes,
            overrides.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.DisplayDictionaryKeys)
                ? overrides.DisplayDictionaryKeys
                : DisplayDictionaryKeys,
            overrides.AssignedOptions.HasFlag(ConfigAuditEntryOptionAssignments.Sensitivity)
                ? MergeSensitivity(Sensitivity, overrides.Sensitivity)
                : Sensitivity,
            AssignedOptions | overrides.AssignedOptions);
    }

    /// <summary>
    /// Merges two assigned entry sensitivity values using the most restrictive valid classification.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigAuditSensitivity.Sensitive"/> wins over <see cref="ConfigAuditSensitivity.NonSensitive"/>,
    /// and <see cref="ConfigAuditSensitivity.NonSensitive"/> wins over <see cref="ConfigAuditSensitivity.Unknown"/>.
    /// If either value is outside the known enum members, that invalid value is preserved so validation can emit an
    /// actionable diagnostic before normalization fails closed. This helper is pure and does not throw for invalid enum
    /// values.
    /// </remarks>
    /// <param name="current">The current sensitivity value.</param>
    /// <param name="candidate">The candidate sensitivity value being merged in.</param>
    /// <returns>The merged sensitivity value.</returns>
    internal static ConfigAuditSensitivity MergeSensitivity(
        ConfigAuditSensitivity current,
        ConfigAuditSensitivity candidate)
    {
        if (!IsValidSensitivity(current))
        {
            return current;
        }

        if (!IsValidSensitivity(candidate))
        {
            return candidate;
        }

        return current > candidate ? current : candidate;
    }

    /// <summary>
    /// Returns a report-generation-safe sensitivity value.
    /// </summary>
    /// <remarks>
    /// Valid values are returned unchanged. Invalid enum values normalize to
    /// <see cref="ConfigAuditSensitivity.Sensitive"/> so redaction fails closed after validation has captured the
    /// original value. This helper is pure and does not throw.
    /// </remarks>
    /// <param name="sensitivity">The sensitivity value to normalize.</param>
    /// <returns>
    /// <paramref name="sensitivity"/> when it is valid; otherwise <see cref="ConfigAuditSensitivity.Sensitive"/>.
    /// </returns>
    internal static ConfigAuditSensitivity NormalizeSensitivity(ConfigAuditSensitivity sensitivity) =>
        IsValidSensitivity(sensitivity) ? sensitivity : ConfigAuditSensitivity.Sensitive;

    /// <summary>
    /// Determines whether a sensitivity value is one of the supported enum members.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigAuditSensitivity.Unknown"/>, <see cref="ConfigAuditSensitivity.NonSensitive"/>, and
    /// <see cref="ConfigAuditSensitivity.Sensitive"/> are valid. All other numeric values are invalid and should be
    /// reported before being normalized. This helper is pure and does not throw.
    /// </remarks>
    /// <param name="sensitivity">The sensitivity value to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="sensitivity"/> is a supported enum member.</returns>
    internal static bool IsValidSensitivity(ConfigAuditSensitivity sensitivity) =>
        sensitivity is ConfigAuditSensitivity.Unknown
            or ConfigAuditSensitivity.NonSensitive
            or ConfigAuditSensitivity.Sensitive;

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
/// registering the audit key, so later builder mutations cannot affect reports. Property setters are also tracked as
/// explicit assignments, allowing manual registrations to override wrapper attribute options even when the assigned
/// value is the option's default.
/// </remarks>
public sealed class ConfigAuditEntryOptionsBuilder
{
    private bool _traverseCollectionElements;
    private int _maxCollectionDepth = ConfigAuditEntryOptions.DefaultMaxCollectionDepth;
    private int _maxCollectionElements = ConfigAuditEntryOptions.DefaultMaxCollectionElements;
    private int _maxReportNodes = ConfigAuditEntryOptions.DefaultMaxReportNodes;
    private bool _displayDictionaryKeys = true;
    private ConfigAuditSensitivity _sensitivity = ConfigAuditSensitivity.Unknown;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditEntryOptionsBuilder"/> class with safe defaults.
    /// </summary>
    public ConfigAuditEntryOptionsBuilder()
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether arrays, lists, and dictionaries should emit child element entries.
    /// </summary>
    public bool TraverseCollectionElements
    {
        get => _traverseCollectionElements;
        set
        {
            _traverseCollectionElements = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.TraverseCollectionElements;
        }
    }

    /// <summary>
    /// Gets or sets the maximum nested collection depth traversed when collection traversal is enabled.
    /// </summary>
    public int MaxCollectionDepth
    {
        get => _maxCollectionDepth;
        set
        {
            _maxCollectionDepth = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.MaxCollectionDepth;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of elements reported from any one traversed collection.
    /// </summary>
    public int MaxCollectionElements
    {
        get => _maxCollectionElements;
        set
        {
            _maxCollectionElements = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.MaxCollectionElements;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of child nodes created for this entry before traversal stops.
    /// </summary>
    public int MaxReportNodes
    {
        get => _maxReportNodes;
        set
        {
            _maxReportNodes = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.MaxReportNodes;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether non-sensitive dictionary keys may appear as element labels.
    /// </summary>
    public bool DisplayDictionaryKeys
    {
        get => _displayDictionaryKeys;
        set
        {
            _displayDictionaryKeys = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.DisplayDictionaryKeys;
        }
    }

    /// <summary>
    /// Gets or sets the entry-level sensitivity classification used by audit redaction.
    /// </summary>
    /// <remarks>
    /// Use <see cref="ConfigAuditSensitivity.Sensitive"/> when the key's domain-specific value should redact even if
    /// the key name does not contain a built-in sensitive fragment. <see cref="ConfigAuditSensitivity.NonSensitive"/>
    /// documents intent but is not an opt-out from conservative redaction and never downgrades another sensitive
    /// signal.
    /// </remarks>
    public ConfigAuditSensitivity Sensitivity
    {
        get => _sensitivity;
        set
        {
            _sensitivity = value;
            AssignedOptions |= ConfigAuditEntryOptionAssignments.Sensitivity;
        }
    }

    /// <summary>
    /// Gets the option properties that were assigned through this builder.
    /// </summary>
    /// <remarks>
    /// The builder tracks setter calls rather than non-default values so duplicate-registration merging can
    /// distinguish "caller left the wrapper value alone" from "caller explicitly reset this option to its default."
    /// </remarks>
    internal ConfigAuditEntryOptionAssignments AssignedOptions { get; private set; }

    /// <summary>
    /// Creates an immutable options snapshot from the current builder state.
    /// </summary>
    /// <remarks>
    /// The returned options copy current values and assignment flags. Later builder mutations do not affect the
    /// returned options, which is important because registrations are snapshotted before reports are built or
    /// serialized.
    /// </remarks>
    internal ConfigAuditEntryOptions ToOptions() =>
        new(
            TraverseCollectionElements,
            MaxCollectionDepth,
            MaxCollectionElements,
            MaxReportNodes,
            DisplayDictionaryKeys,
            Sensitivity,
            AssignedOptions);
}

/// <summary>
/// Tracks which audit entry options were intentionally assigned by a wrapper attribute or manual registration.
/// </summary>
/// <remarks>
/// These flags control duplicate-registration precedence. They are not a serialization format and should not be
/// inferred from option values, because default-valued assignments are meaningful overrides.
/// </remarks>
[Flags]
internal enum ConfigAuditEntryOptionAssignments
{
    /// <summary>
    /// No options were explicitly assigned.
    /// </summary>
    None = 0,

    /// <summary>
    /// <see cref="ConfigAuditEntryOptions.TraverseCollectionElements"/> was explicitly assigned.
    /// </summary>
    TraverseCollectionElements = 1 << 0,

    /// <summary>
    /// <see cref="ConfigAuditEntryOptions.MaxCollectionDepth"/> was explicitly assigned.
    /// </summary>
    MaxCollectionDepth = 1 << 1,

    /// <summary>
    /// <see cref="ConfigAuditEntryOptions.MaxCollectionElements"/> was explicitly assigned.
    /// </summary>
    MaxCollectionElements = 1 << 2,

    /// <summary>
    /// <see cref="ConfigAuditEntryOptions.MaxReportNodes"/> was explicitly assigned.
    /// </summary>
    MaxReportNodes = 1 << 3,

    /// <summary>
    /// <see cref="ConfigAuditEntryOptions.DisplayDictionaryKeys"/> was explicitly assigned.
    /// </summary>
    DisplayDictionaryKeys = 1 << 4,

    /// <summary>
    /// <see cref="ConfigAuditEntryOptions.Sensitivity"/> was explicitly assigned.
    /// </summary>
    Sensitivity = 1 << 5,

    /// <summary>
    /// All collection traversal options were explicitly assigned.
    /// </summary>
    CollectionTraversal = TraverseCollectionElements | MaxCollectionDepth | MaxCollectionElements | MaxReportNodes | DisplayDictionaryKeys,

    /// <summary>
    /// Every audit entry option was explicitly assigned.
    /// </summary>
    All = CollectionTraversal | Sensitivity
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
    /// Use <paramref name="configure"/> to opt into collection element traversal for this key only or to classify a
    /// domain-specific value with <see cref="ConfigAuditEntryOptionsBuilder.Sensitivity"/>. Options are snapshotted
    /// when the registration is created; collection traversal remains disabled by default so existing reports keep
    /// their previous shape unless callers explicitly enable it. If this key is also discovered from a config wrapper,
    /// the wrapper supplies metadata and validation while explicitly assigned manual options override wrapper audit
    /// options per property. Sensitivity merges monotonically, so <see cref="ConfigAuditSensitivity.NonSensitive"/>
    /// never downgrades an effective sensitive entry.
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
