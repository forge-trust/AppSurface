namespace ForgeTrust.AppSurface.Config;

/// <summary>A value-safe failure at one canonical destination or at the root plan.</summary>
/// <remarks>Instances come from the framework diagnostic catalog. They cannot retain a payload, resource key,
/// version, provider exception, or serialized application object.</remarks>
public sealed class ConfigCompositionFailure
{
    /// <summary>Creates a catalog-backed failure without accepting arbitrary diagnostic text.</summary>
    internal ConfigCompositionFailure(string path, string code, string? providerId = null, bool retryable = false,
        ConfigAuditSourceRecord? source = null, string? relatedPath = null, string? environmentVariableName = null)
    {
        Path = path;
        Code = code;
        ProviderId = providerId;
        Retryable = retryable;
        Source = source;
        RelatedPath = relatedPath;
        EnvironmentVariableName = environmentVariableName;
        (Problem, Cause, Fix, var anchor) = ConfigCompositionDiagnostics.Describe(code);
        Docs = $"https://appsurface.dev/config/secret-references#{anchor}";
    }
    /// <summary>The canonical colon-delimited destination.</summary>
    public string Path { get; }
    /// <summary>A stable framework code.</summary>
    public string Code { get; }
    /// <summary>A validated provider id, when relevant.</summary>
    public string? ProviderId { get; }
    /// <summary>Whether a later attempt may repair a transient failure.</summary>
    public bool Retryable { get; }
    /// <summary>A safe declaration location, never the descriptor or resource identity.</summary>
    public ConfigAuditSourceRecord? Source { get; }
    /// <summary>The other canonical destination in a path or environment-alias conflict.</summary>
    public string? RelatedPath { get; }
    /// <summary>The colliding environment variable name; never its value.</summary>
    public string? EnvironmentVariableName { get; }
    /// <summary>The operator-facing problem.</summary>
    public string Problem { get; }
    /// <summary>The framework-classified cause.</summary>
    public string Cause { get; }
    /// <summary>The suggested corrective action.</summary>
    public string Fix { get; }
    /// <summary>The canonical diagnostic reference.</summary>
    public string Docs { get; }
    /// <inheritdoc />
    public override string ToString() => $"{Code} at {Path}: {Problem} Cause: {Cause} Fix: {Fix} Docs: {Docs}"
        + (RelatedPath is null ? "" : $" Related path: {RelatedPath}.")
        + (EnvironmentVariableName is null ? "" : $" Environment candidate: {EnvironmentVariableName}.");
}

/// <summary>Reports every unresolved failure in an opted-in root, without changing legacy resolution exceptions.</summary>
/// <remarks>Failures are ordered by canonical path, code and provider id. No provider exception is retained as an
/// inner exception. Exact valid environment values can rescue runtime slot failures, but cannot rescue plan errors.</remarks>
public sealed class ConfigurationCompositionException : Exception
{
    /// <summary>Creates a deterministic exception from nonempty framework-produced failures.</summary>
    /// <param name="environmentName">The requested environment.</param>
    /// <param name="rootKey">The requested logical root.</param>
    /// <param name="failures">Value-safe failures to snapshot and order.</param>
    public ConfigurationCompositionException(string environmentName, string rootKey, IReadOnlyList<ConfigCompositionFailure> failures)
        : this(environmentName, rootKey, Order(failures)) { }

    private ConfigurationCompositionException(string environmentName, string rootKey, ConfigCompositionFailure[] failures)
        : base($"Configuration composition failed for '{rootKey}' in '{environmentName}'.{Environment.NewLine}{string.Join(Environment.NewLine, failures.Select(f => f.ToString()))}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);
        EnvironmentName = environmentName;
        RootKey = rootKey;
        Failures = Array.AsReadOnly(failures);
    }
    /// <summary>The requested environment.</summary>
    public string EnvironmentName { get; }
    /// <summary>The requested root key.</summary>
    public string RootKey { get; }
    /// <summary>The immutable ordered failure snapshot.</summary>
    public IReadOnlyList<ConfigCompositionFailure> Failures { get; }

    private static ConfigCompositionFailure[] Order(IReadOnlyList<ConfigCompositionFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0 || failures.Any(f => f is null))
            throw new ArgumentException("At least one non-null composition failure is required.", nameof(failures));
        return failures.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Code, StringComparer.Ordinal)
            .ThenBy(f => f.ProviderId, StringComparer.Ordinal).ToArray();
    }
}

/// <summary>One safe problem/cause/fix catalog used by runtime, audit and documentation checks.</summary>
internal static class ConfigCompositionDiagnostics
{
    /// <summary>Returns framework text and the canonical guide anchor for a stable code.</summary>
    internal static (string Problem, string Cause, string Fix, string Anchor) Describe(string code) => code switch
    {
        "secret-descriptor-incomplete" => ("The winning declaration is missing a nonblank key.", "File descriptors replace a complete lower declaration.", "Repeat the complete intended descriptor in the winning file.", "atomic-file-layers"),
        "secret-descriptor-invalid" => ("The secret declaration is invalid.", "The value is not a complete descriptor or contains invalid members.", "Use key, optional version, provider, and a static boolean enabled.", "descriptor"),
        "secret-provider-access-denied" => ("The provider could not access the requested secret.", "The runtime identity or resource policy denied access.", "Grant access, correct the reference, or apply the exact emergency override.", "access-denied"),
        "secret-provider-unavailable" => ("The secret provider is unavailable.", "The service or network failed or a native timeout expired.", "Restore connectivity or apply the exact emergency override.", "provider-failures"),
        "secret-provider-failed" => ("The secret provider failed.", "An unexpected provider condition prevented resolution.", "Inspect the provider implementation or use the exact emergency override.", "provider-failures"),
        "secret-provider-not-registered" => ("The reference has no registered provider.", "The provider constraint or registration set cannot be satisfied.", "Register the intended provider, including for disabled declarations.", "provider-selection"),
        "secret-provider-id-invalid" or "secret-provider-id-duplicate" => ("Secret provider registration is invalid.", "Provider ids must be unique lower-case kebab-case identifiers.", "Register each canonical id once and alias its singleton capabilities.", "provider-contract"),
        "secret-reference-invalid" => ("A provider rejected the reference.", "The recognized key/version shape violates local provider policy.", "Correct the complete reference using the provider guide.", "provider-selection"),
        "secret-reference-unsupported" => ("No provider supports the reference.", "All applicable providers declined ownership of this shape.", "Use a supported reference and register its intended provider.", "provider-selection"),
        "secret-not-found" => ("The declared secret was not found.", "No compatible provider returned a value.", "Provision the resource or apply an exact valid environment override.", "provider-failures"),
        "secret-provider-ambiguous" => ("Multiple providers resolved the reference.", "Providerless resolution requires one unique success.", "Constrain provider explicitly or remove duplicate ownership.", "provider-selection"),
        "secret-providerless-resolution-budget-exceeded" => ("Providerless uniqueness could not be established in time.", "The shared cooperative root deadline expired.", "Constrain provider, repair latency, or adjust the positive root budget.", "resolution-budget"),
        "secret-value-conversion-failed" => ("A secret value could not be converted.", "A contribution was null or incompatible with the scalar destination.", "Correct the secret payload or supply an exact valid environment override.", "scalar-values"),
        "secret-claim-overlap" or "secret-mapped-destination-unsupported" => ("Configuration declarations overlap or leave an incompatible mapped sibling.", "The requested root mixes file declarations with intersecting legacy claims.", "Migrate the complete requested root and remove old mappings in the same change.", "migrate-map-secret"),
        "secret-environment-alias-collision" => ("Distinct secret destinations share an environment candidate.", "Normalized environment names cannot identify one destination uniquely.", "Rename the conflicting serialized members before enabling composition.", "path-identity"),
        "secret-path-collision" or "secret-path-invalid" => ("Configuration paths are ambiguous.", "Member names or file paths collapse to the same logical identity.", "Use distinct serialized names without literal dots, colons, or empty segments.", "path-identity"),
        "secret-destination-type-unsupported" or "secret-graph-limit-exceeded" => ("The secret destination graph is unsupported.", "Discovery encountered an unsupported scalar, collection, recursive graph, serializer contract, or graph limit.", "Use a finite reference-type root with scalar Secret<T> members and supported JSON metadata.", "supported-models"),
        "config-composition-provider-unsupported" => ("A base provider cannot participate in composition.", "The provider returns typed values and cannot prove this root unclaimed.", "Implement raw resolution or a local claim inspector for this provider.", "provider-contract"),
        "config-composition-base-failed" => ("The selected base provider failed.", "A terminal whole-root condition prevents lower-source fallback.", "Repair the base provider or provide a complete direct environment root.", "base-sources"),
        "config-composition-bind-failed" => ("The composed root could not be bound.", "The raw root or destination contract could not be materialized.", "Correct the root object and use the supported JSON contract.", "supported-models"),
        "config-composition-file-invalid" => ("An applicable configuration file could not be safely loaded.", "A file was unreadable, malformed, duplicated, or case-colliding.", "Repair the source file before resolving opted-in roots.", "atomic-file-layers"),
        _ => ("Configuration composition could not complete.", "A required composition invariant failed.", "Inspect the stable code and correct the declared root.", "diagnostics")
    };
}
