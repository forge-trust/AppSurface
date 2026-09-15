using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Config;

/// <summary>A synchronous, provider-neutral resolver for scalar file-declared references.</summary>
/// <remarks>
/// Register one singleton through DI. Validation is local-only; resolution must cap its native synchronous timeout
/// to <see cref="ConfigSecretResolutionContext.Remaining"/> and must not block on asynchronous work.
/// Providers unable to honor that contract must reject local validation. See
/// <see href="https://appsurface.dev/config/secret-references#provider-contract">provider compatibility</see>.
/// </remarks>
public interface IConfigSecretProvider
{
    /// <summary>A unique lower-case kebab-case id, compared case-insensitively.</summary>
    string Id { get; }
    /// <summary>Validates ownership and syntax without secret-store I/O, including disabled references.</summary>
    ConfigSecretReferenceValidation ValidateReference(ConfigSecretReference reference);
    /// <summary>Resolves one enabled reference without formatting payloads or arbitrary exception text.</summary>
    ConfigSecretProviderResolution Resolve(ConfigSecretReference reference, ConfigSecretResolutionContext context);
}

/// <summary>Inspects existing code-configured mappings locally without broadening convention discovery.</summary>
public interface IConfigSecretDeclarationSource
{
    /// <summary>Reports mappings intersecting the root and a convention only if it already claims that root.</summary>
    IReadOnlyList<ConfigSecretConfiguredClaim> InspectClaims(string rootLogicalPath, IReadOnlyList<string> secretDestinationPaths);
}

/// <summary>Classifies an existing code-configured claim.</summary>
public enum ConfigSecretConfiguredClaimKind
{
    /// <summary>An explicitly mapped destination.</summary>
    ExactMapping,
    /// <summary>An explicitly mapped requested root.</summary>
    RootMapping,
    /// <summary>An existing convention claiming the requested root.</summary>
    RootConvention
}

/// <summary>A local mapping claim. Resource identity is excluded from default formatting and JSON.</summary>
/// <param name="Kind">The origin of the claim.</param>
/// <param name="LogicalPath">The AppSurface path; never an external resource name.</param>
/// <param name="ProviderId">The registered canonical provider id.</param>
/// <param name="Key">The opaque resource input, preserved verbatim.</param>
/// <param name="Version">The opaque optional version input.</param>
public sealed record ConfigSecretConfiguredClaim(ConfigSecretConfiguredClaimKind Kind, string LogicalPath,
    string ProviderId, [property: JsonIgnore] string Key, [property: JsonIgnore] string? Version)
{
    /// <inheritdoc />
    public override string ToString() => $"ConfigSecretConfiguredClaim {{ Kind = {Kind}, LogicalPath = {LogicalPath}, ProviderId = {ProviderId} }}";
}

/// <summary>An immutable reference passed to local validation and runtime resolution.</summary>
/// <param name="Environment">The requested environment.</param>
/// <param name="LogicalPath">The canonical colon-delimited destination path.</param>
/// <param name="Key">Opaque provider input; never normalized as an AppSurface path.</param>
/// <param name="Version">Optional opaque version interpreted by the provider.</param>
public sealed record ConfigSecretReference(string Environment, string LogicalPath,
    [property: JsonIgnore] string Key, [property: JsonIgnore] string? Version)
{
    /// <inheritdoc />
    public override string ToString() => $"ConfigSecretReference {{ Environment = {Environment}, LogicalPath = {LogicalPath} }}";
}

/// <summary>A cooperative monotonic deadline. Synchronous providers cannot be forcibly preempted.</summary>
public sealed class ConfigSecretResolutionContext
{
    private readonly TimeProvider _time;
    private readonly long _start;
    private readonly TimeSpan _budget;

    /// <summary>Creates a deadline, optionally sharing the beginning of the root invocation.</summary>
    internal ConfigSecretResolutionContext(TimeProvider time, TimeSpan budget, long? start = null)
    {
        _time = time;
        _budget = budget;
        _start = start ?? time.GetTimestamp();
    }

    /// <summary>Gets the nonnegative time remaining; cap native call timeouts to this value.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var remaining = _budget - _time.GetElapsedTime(_start);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}

/// <summary>Local ownership and reference syntax classification.</summary>
public enum ConfigSecretReferenceValidationStatus
{
    /// <summary>The provider accepts the reference and the synchronous deadline contract.</summary>
    Supported,
    /// <summary>The provider does not recognize this reference shape.</summary>
    Unclaimed,
    /// <summary>The provider recognizes but rejects the reference; no resolution may occur.</summary>
    Invalid
}

/// <summary>A local-only validation result. It carries no resource identity or arbitrary provider message.</summary>
public sealed class ConfigSecretReferenceValidation
{
    private ConfigSecretReferenceValidation(ConfigSecretReferenceValidationStatus status) => Status = status;
    /// <summary>Gets the ownership/syntax outcome.</summary>
    public ConfigSecretReferenceValidationStatus Status { get; }
    /// <summary>A stable framework-owned code safe for diagnostics.</summary>
    public string Code => Status switch
    {
        ConfigSecretReferenceValidationStatus.Supported => "secret-reference-supported",
        ConfigSecretReferenceValidationStatus.Unclaimed => "secret-reference-unsupported",
        _ => "secret-reference-invalid"
    };
    /// <summary>Accepts this shape without reading a secret.</summary>
    public static ConfigSecretReferenceValidation Supported() => new(ConfigSecretReferenceValidationStatus.Supported);
    /// <summary>Declines ownership of this shape.</summary>
    public static ConfigSecretReferenceValidation Unclaimed() => new(ConfigSecretReferenceValidationStatus.Unclaimed);
    /// <summary>Rejects a recognized reference without retaining raw error text.</summary>
    public static ConfigSecretReferenceValidation Invalid() => new(ConfigSecretReferenceValidationStatus.Invalid);
    /// <inheritdoc />
    public override string ToString() => Code;
}

/// <summary>Classifies a single provider response. Only missing and unclaimed can fall through.</summary>
public enum ConfigSecretProviderResolutionStatus
{
    /// <summary>The provider did not claim the reference.</summary>
    Unclaimed,
    /// <summary>The claimed reference was not found.</summary>
    Missing,
    /// <summary>A non-null textual payload was obtained.</summary>
    Resolved,
    /// <summary>Access was denied.</summary>
    AccessDenied,
    /// <summary>The provider was unavailable or timed out.</summary>
    Unavailable,
    /// <summary>Runtime resolution rejected the reference.</summary>
    InvalidReference,
    /// <summary>An unexpected provider failure occurred.</summary>
    ProviderFailed
}

/// <summary>Allowlisted source metadata; resource keys and requested versions are intentionally absent.</summary>
public sealed class ConfigSecretSourceMetadata
{
    private ConfigSecretSourceMetadata(string providerId, string sourceKind, string? correlationToken)
    {
        ProviderId = ConfigSecretSafety.ProviderId(providerId);
        if (sourceKind is not ("remote" or "local" or "custom"))
            throw new ArgumentException("Source kind must be remote, local, or custom.", nameof(sourceKind));
        SourceKind = sourceKind;
        CorrelationToken = correlationToken;
    }

    /// <summary>The canonical registered provider id.</summary>
    public string ProviderId { get; }
    /// <summary>The allowlisted source kind: remote, local, or custom.</summary>
    public string SourceKind { get; }
    /// <summary>An explicitly approved opaque correlation token, never a resource name or version.</summary>
    public string? CorrelationToken { get; }
    /// <summary>Creates source metadata without any external identity.</summary>
    public static ConfigSecretSourceMetadata Create(string providerId, string sourceKind = "custom") => new(providerId, sourceKind, null);
    /// <summary>Creates metadata with a provider-approved, 8–64 character alphanumeric/underscore/hyphen token.</summary>
    /// <remarks>The provider is responsible for ensuring the opaque token reveals no key, version or payload.</remarks>
    public static ConfigSecretSourceMetadata WithSafeCorrelationToken(string providerId, string sourceKind, string token)
    {
        if (token is null || token.Length is < 8 or > 64 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Correlation token must be an opaque 8–64 character token.", nameof(token));
        return new(providerId, sourceKind, token);
    }
    /// <inheritdoc />
    public override string ToString() => $"{ProviderId} ({SourceKind})";
}

/// <summary>A sealed provider result whose success payload is accessible only to the composition core.</summary>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class ConfigSecretProviderResolution
{
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string? _sensitiveValue;
    private ConfigSecretProviderResolution(ConfigSecretProviderResolutionStatus status, ConfigSecretSourceMetadata source,
        string? sensitiveValue = null, bool retryable = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        Status = status;
        Source = source;
        _sensitiveValue = sensitiveValue;
        Retryable = retryable;
    }
    /// <summary>The response classification.</summary>
    public ConfigSecretProviderResolutionStatus Status { get; }
    /// <summary>Allowlisted source metadata.</summary>
    public ConfigSecretSourceMetadata Source { get; }
    /// <summary>The canonical provider identity.</summary>
    public string ProviderId => Source.ProviderId;
    /// <summary>Whether retrying may repair a transient failure.</summary>
    public bool Retryable { get; }
    /// <summary>Reads transient payload storage without exposing a public serializable getter.</summary>
    internal string? ReadSensitiveValue() => _sensitiveValue;
    /// <summary>Creates a success; empty strings are valid, null is rejected.</summary>
    public static ConfigSecretProviderResolution Resolved(string sensitiveValue, ConfigSecretSourceMetadata source)
    {
        ArgumentNullException.ThrowIfNull(sensitiveValue);
        return new(ConfigSecretProviderResolutionStatus.Resolved, source, sensitiveValue);
    }
    /// <summary>Declines runtime ownership.</summary>
    public static ConfigSecretProviderResolution Unclaimed(string providerId) => new(ConfigSecretProviderResolutionStatus.Unclaimed, ConfigSecretSourceMetadata.Create(providerId));
    /// <summary>Reports a missing claimed resource.</summary>
    public static ConfigSecretProviderResolution Missing(string providerId) => new(ConfigSecretProviderResolutionStatus.Missing, ConfigSecretSourceMetadata.Create(providerId));
    /// <summary>Reports denied access without exception text.</summary>
    public static ConfigSecretProviderResolution AccessDenied(string providerId) => new(ConfigSecretProviderResolutionStatus.AccessDenied, ConfigSecretSourceMetadata.Create(providerId));
    /// <summary>Reports transient service unavailability.</summary>
    public static ConfigSecretProviderResolution Unavailable(string providerId) => new(ConfigSecretProviderResolutionStatus.Unavailable, ConfigSecretSourceMetadata.Create(providerId), retryable: true);
    /// <summary>Reports runtime reference rejection.</summary>
    public static ConfigSecretProviderResolution InvalidReference(string providerId) => new(ConfigSecretProviderResolutionStatus.InvalidReference, ConfigSecretSourceMetadata.Create(providerId));
    /// <summary>Reports an unexpected failure without retaining an exception.</summary>
    public static ConfigSecretProviderResolution ProviderFailed(string providerId, bool retryable = false) => new(ConfigSecretProviderResolutionStatus.ProviderFailed, ConfigSecretSourceMetadata.Create(providerId), retryable: retryable);
    /// <inheritdoc />
    public override string ToString() => $"{ProviderId}: {Status}";
}

/// <summary>Validates bounded identifiers before including trusted registration metadata in output.</summary>
internal static class ConfigSecretSafety
{
    /// <summary>Validates and returns a canonical id without echoing invalid input in an exception.</summary>
    internal static string ProviderId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id[0] is < 'a' or > 'z' || id[^1] == '-'
            || id.Contains("--", StringComparison.Ordinal) || id.Any(c => c is not (>= 'a' and <= 'z') && !char.IsAsciiDigit(c) && c != '-'))
            throw new ArgumentException("Provider ids must be lower-case kebab-case, at most 64 characters.", nameof(id));
        return id;
    }
}
