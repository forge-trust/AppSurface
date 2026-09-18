using System.Collections.ObjectModel;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>One immutable host-local index of all declared configuration identities.</summary>
/// <remarks>
/// Raw declarations are resolved only after the final service provider exists, so parser options are finalized and
/// wrapper activation is never used to discover keys. Exact canonical duplicates merge metadata. Case-only and
/// legacy/canonical conflicts fail before a wrapper can be activated.
/// The registry remains internal; provider packages share it through friend-assembly access. Public logical identity
/// is the immutable <see cref="AppSurfaceConfigKey"/> documented by the
/// <see href="https://appsurface.dev/guides/config-logical-keys">logical-key contract</see>.
/// </remarks>
internal sealed class ConfigDeclarationRegistry
{
    private readonly IReadOnlyDictionary<Type, ConfigAuditKnownEntry> _byConfigType;

    /// <summary>Builds the complete immutable index once from the finalized container's declarations.</summary>
    /// <param name="typedEntries">Explicit known entries registered directly in dependency injection.</param>
    /// <param name="rawDeclarations">Ordered immutable descriptors captured by discovery and manual registration.</param>
    /// <param name="parser">The singleton parser containing finalized host options.</param>
    /// <exception cref="InvalidOperationException">Two declaration spellings claim the same logical identity.</exception>
    public ConfigDeclarationRegistry(
        IEnumerable<ConfigAuditKnownEntry> typedEntries,
        IEnumerable<ConfigAuditRawDeclaration> rawDeclarations,
        IConfigKeyInputParser parser)
    {
        ArgumentNullException.ThrowIfNull(typedEntries);
        ArgumentNullException.ThrowIfNull(rawDeclarations);
        ArgumentNullException.ThrowIfNull(parser);

        var candidates = typedEntries
            .Concat(rawDeclarations.Select(declaration => declaration.Resolve(parser)))
            .ToList();
        var byValue = new Dictionary<string, ConfigAuditKnownEntry>(StringComparer.Ordinal);
        var byIdentity = new Dictionary<AppSurfaceConfigKey, ConfigAuditKnownEntry>();

        foreach (var candidate in candidates)
        {
            if (byValue.TryGetValue(candidate.LogicalKey.Value, out var exact))
            {
                if (IsLegacyCanonicalConflict(exact.LogicalKey, candidate.LogicalKey))
                {
                    throw CreateCollision(exact.LogicalKey, candidate.LogicalKey);
                }

                byValue[candidate.LogicalKey.Value] = Merge(exact, candidate);
                continue;
            }

            if (byIdentity.TryGetValue(candidate.LogicalKey, out var equivalent))
            {
                throw CreateCollision(equivalent.LogicalKey, candidate.LogicalKey);
            }

            byValue.Add(candidate.LogicalKey.Value, candidate);
            byIdentity.Add(candidate.LogicalKey, candidate);
        }

        var ordered = byValue.Values
            .OrderBy(entry => entry.LogicalKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LogicalKey.Value, StringComparer.Ordinal)
            .ToArray();
        Entries = new ReadOnlyCollection<ConfigAuditKnownEntry>(ordered);
        Keys = new ReadOnlyCollection<AppSurfaceConfigKey>(ordered.Select(entry => entry.LogicalKey).ToArray());
        var mergedByValue = byValue;
        _byConfigType = new ReadOnlyDictionary<Type, ConfigAuditKnownEntry>(
            candidates.Where(entry => entry.ConfigType != null)
                .GroupBy(entry => entry.ConfigType!)
                .ToDictionary(
                    group => group.Key,
                    group => mergedByValue[group.First().LogicalKey.Value]));
    }

    /// <summary>Gets merged immutable entries in deterministic order.</summary>
    internal IReadOnlyList<ConfigAuditKnownEntry> Entries { get; }

    /// <summary>Gets the corresponding immutable typed keys in deterministic order.</summary>
    internal IReadOnlyList<AppSurfaceConfigKey> Keys { get; }

    /// <summary>Finds the merged entry for each wrapper type, including multiple wrappers sharing one exact key.</summary>
    /// <param name="configType">The discovered wrapper being activated.</param>
    /// <returns>The immutable merged entry shared with providers and audit reporting.</returns>
    /// <exception cref="InvalidOperationException">The wrapper was not declared in this host.</exception>
    internal ConfigAuditKnownEntry GetForConfigType(Type configType) =>
        _byConfigType.TryGetValue(configType, out var entry)
            ? entry
            : throw new InvalidOperationException($"No config declaration was registered for {configType.FullName}.");

    private static ConfigAuditKnownEntry Merge(ConfigAuditKnownEntry first, ConfigAuditKnownEntry second)
    {
        var selected = first.ConfigType == null && second.ConfigType != null ? second : first;
        var mergedOptions = new ConfigAuditEntryOptions(selected.OptionsSnapshot);
        if (first.ConfigType == null)
        {
            mergedOptions = mergedOptions.ApplyAssignedOverrides(first.OptionsSnapshot);
        }

        if (second.ConfigType == null)
        {
            mergedOptions = mergedOptions.ApplyAssignedOverrides(second.OptionsSnapshot);
        }

        return selected.WithOptions(mergedOptions);
    }

    private static bool IsLegacyCanonicalConflict(AppSurfaceConfigKey first, AppSurfaceConfigKey second) =>
        first.InputOrigin == ConfigKeyInputOrigin.TranslatedDot
            && second.InputOrigin == ConfigKeyInputOrigin.TranslatedDot
            ? !StringComparer.Ordinal.Equals(first.OriginalInput, second.OriginalInput)
            : first.InputOrigin != second.InputOrigin
                && (first.InputOrigin == ConfigKeyInputOrigin.TranslatedDot
                    || second.InputOrigin == ConfigKeyInputOrigin.TranslatedDot);

    private static InvalidOperationException CreateCollision(
        AppSurfaceConfigKey first,
        AppSurfaceConfigKey second)
    {
        var firstInput = ConfigDiagnosticText.Identifier(first.OriginalInput ?? first.Value);
        var secondInput = ConfigDiagnosticText.Identifier(second.OriginalInput ?? second.Value);
        return new InvalidOperationException(
            $"config-key-collision: declarations '{firstInput}' and '{secondInput}' identify the same logical key. " +
            $"Keep exactly one spelling per declaration identity. See {ConfigDiagnosticCatalog.Reference}.");
    }
}

/// <summary>Empty options marker that forces the finalized declaration registry to be validated at host startup.</summary>
/// <remarks>
/// Its validator depends on the immutable registry. The options validation pipeline therefore resolves all deferred
/// declarations before provider traversal or wrapper activation.
/// </remarks>
internal sealed class ConfigDeclarationStartupOptions
{
}

/// <summary>Forces the environment provider's finalized declaration and native-mapping validation at startup.</summary>
/// <remarks>
/// This marker is separate from environment options because the provider consumes those options in its constructor.
/// Depending on the provider while validating its own options would create a circular dependency.
/// </remarks>
internal sealed class ConfigEnvironmentStartupOptions
{
}

/// <summary>Validates that the finalized host declaration registry can be constructed.</summary>
internal sealed class ConfigDeclarationStartupValidator : IValidateOptions<ConfigDeclarationStartupOptions>
{
    private readonly ConfigDeclarationRegistry _registry;

    /// <summary>Requires the registry to finish construction before startup validation can succeed.</summary>
    /// <param name="registry">The host's complete immutable declaration index.</param>
    public ConfigDeclarationStartupValidator(ConfigDeclarationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ConfigDeclarationStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = _registry.Entries;
        return ValidateOptionsResult.Success;
    }
}

/// <summary>Immutable declaration captured before DI finalization.</summary>
/// <param name="RawKey">An application string deferred to the finalized parser, when not attributed or typed.</param>
/// <param name="ConfigType">The wrapper type, or null for a manual audit declaration.</param>
/// <param name="ValueType">The expected resolved value type.</param>
/// <param name="Options">The immutable option snapshot captured at registration time.</param>
/// <param name="IsAttributeDeclaration">Whether declaring-type fragments supply the logical key.</param>
/// <param name="TypedKey">An already immutable key whose identity and provenance must pass through unchanged.</param>
internal sealed record ConfigAuditRawDeclaration(
    string? RawKey,
    Type? ConfigType,
    Type ValueType,
    ConfigAuditEntryOptions Options,
    bool IsAttributeDeclaration,
    AppSurfaceConfigKey? TypedKey = null)
{
    /// <summary>Resolves captured fragments once, or preserves an already typed key and its input provenance.</summary>
    internal ConfigAuditKnownEntry Resolve(IConfigKeyInputParser parser)
    {
        var key = TypedKey ?? (IsAttributeDeclaration
            ? ConfigKeyAttribute.GetLogicalKey(ConfigType!, parser)
            : parser.Parse(RawKey!));
        return new ConfigAuditKnownEntry(key, ConfigType, ValueType, Options);
    }
}
