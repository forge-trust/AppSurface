using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Configures scoped dictionary-key correlation identifiers for configuration audit reports.
/// </summary>
/// <remarks>
/// These options are only used by entries whose <see cref="ConfigAuditEntryOptions.DictionaryKeyCorrelationMode"/> is
/// <see cref="ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac"/>. The secret key is never copied into reports.
/// Reports may include <see cref="KeyId"/>, <see cref="ApplicationScope"/>, and derived correlation identifiers, which
/// are still sensitive support metadata because they reveal key equality, churn, and absence across reports.
/// </remarks>
public sealed class ConfigAuditDictionaryKeyCorrelationOptions
{
    /// <summary>
    /// Gets or sets the deployment-local HMAC secret used to derive correlation identifiers.
    /// </summary>
    /// <remarks>
    /// The value is interpreted as UTF-8 bytes and must contain at least 32 bytes. Store it in a secret manager or
    /// equivalent protected configuration source. Rotating this value intentionally breaks historical correlation.
    /// </remarks>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Gets or sets the display-safe identifier for the active correlation key.
    /// </summary>
    /// <remarks>
    /// The key id appears in reports so operators can tell which secret produced a correlation identifier. It must be
    /// non-empty and can contain only ASCII letters, digits, <c>.</c>, <c>_</c>, and <c>-</c>.
    /// </remarks>
    public string? KeyId { get; set; }

    /// <summary>
    /// Gets or sets the application or product scope included in correlation derivation.
    /// </summary>
    /// <remarks>
    /// Use a stable public identifier for the app or product, not a secret. Changing this value intentionally breaks
    /// historical correlation and prevents ids from matching across unrelated apps that share a key.
    /// </remarks>
    public string? ApplicationScope { get; set; }
}

/// <summary>
/// Creates per-entry dictionary-key correlation contexts from deployment-level options.
/// </summary>
/// <remarks>
/// This internal helper is used only when at least one audit entry requests
/// <see cref="ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac"/>. A context is available only when the configured
/// secret is at least <c>MinimumSecretBytes</c> UTF-8 bytes, the key id is display-safe, and the application scope is
/// present. Unavailable contexts preserve report generation and later emit
/// <c>config-audit-key-correlation-unavailable</c> diagnostics at requested dictionary paths.
/// </remarks>
internal sealed class ConfigAuditDictionaryKeyCorrelator
{
    private const string AlgorithmVersion = "v1";
    private const int MinimumSecretBytes = 32;
    private const int TruncatedBytes = 12;

    private readonly ConfigAuditDictionaryKeyCorrelationOptions _options;

    /// <summary>
    /// Initializes a correlator from optional deployment-level correlation settings.
    /// </summary>
    /// <param name="options">
    /// The configured options. A <see langword="null"/> value disables correlation availability and produces an
    /// unavailable context when requested.
    /// </param>
    public ConfigAuditDictionaryKeyCorrelator(ConfigAuditDictionaryKeyCorrelationOptions? options)
    {
        _options = options ?? new ConfigAuditDictionaryKeyCorrelationOptions();
    }

    /// <summary>
    /// Creates an immutable context for one report environment and root audit key.
    /// </summary>
    /// <param name="environment">The report environment included in the scoped HMAC input.</param>
    /// <param name="rootKey">The root audit key included in the scoped HMAC input.</param>
    /// <returns>
    /// An available context when all correlation options are valid; otherwise an unavailable context carrying the reason
    /// reported by <c>config-audit-key-correlation-unavailable</c>.
    /// </returns>
    /// <remarks>
    /// Available contexts derive ids in the form <c>v1:{keyId}:{24-hex-chars}</c>. The suffix is the first
    /// <c>TruncatedBytes</c> bytes (96 bits) of an HMAC-SHA256 digest over <c>AlgorithmVersion</c>, application scope,
    /// environment, root key, and raw dictionary key.
    /// </remarks>
    public ConfigAuditDictionaryKeyCorrelationContext CreateContext(string environment, string rootKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);

        var secretKey = _options.SecretKey;
        var keyId = NormalizeKeyId(_options.KeyId);
        var applicationScope = NormalizeApplicationScope(_options.ApplicationScope);
        if (string.IsNullOrEmpty(secretKey))
        {
            return ConfigAuditDictionaryKeyCorrelationContext.Unavailable("a secret key was not configured");
        }

        if (Encoding.UTF8.GetByteCount(secretKey) < MinimumSecretBytes)
        {
            return ConfigAuditDictionaryKeyCorrelationContext.Unavailable(
                $"the configured secret key must contain at least {MinimumSecretBytes.ToString(CultureInfo.InvariantCulture)} UTF-8 bytes");
        }

        if (string.IsNullOrEmpty(keyId))
        {
            return ConfigAuditDictionaryKeyCorrelationContext.Unavailable("a display-safe key id was not configured");
        }

        if (!IsDisplaySafeKeyId(keyId))
        {
            return ConfigAuditDictionaryKeyCorrelationContext.Unavailable(
                "the configured key id can contain only ASCII letters, digits, '.', '_', or '-'");
        }

        if (string.IsNullOrEmpty(applicationScope))
        {
            return ConfigAuditDictionaryKeyCorrelationContext.Unavailable("an application scope was not configured");
        }

        return ConfigAuditDictionaryKeyCorrelationContext.Available(
            Encoding.UTF8.GetBytes(secretKey),
            keyId,
            applicationScope,
            environment,
            rootKey);
    }

    /// <summary>
    /// Trims the configured key id before validation and report metadata rendering.
    /// </summary>
    /// <param name="keyId">The configured display-safe key id.</param>
    /// <returns>The trimmed key id, or <see langword="null"/> when no value was configured.</returns>
    internal static string? NormalizeKeyId(string? keyId) => keyId?.Trim();

    /// <summary>
    /// Trims the configured application scope before validation and HMAC input construction.
    /// </summary>
    /// <param name="applicationScope">The configured application or product scope.</param>
    /// <returns>The trimmed application scope, or <see langword="null"/> when no value was configured.</returns>
    internal static string? NormalizeApplicationScope(string? applicationScope) => applicationScope?.Trim();

    /// <summary>
    /// Determines whether a key id is safe to render in report metadata and text output.
    /// </summary>
    /// <param name="keyId">The normalized key id to inspect.</param>
    /// <returns>
    /// <see langword="true"/> when the key id contains only ASCII letters, digits, <c>.</c>, <c>_</c>, or <c>-</c>;
    /// otherwise <see langword="false"/>.
    /// </returns>
    internal static bool IsDisplaySafeKeyId(string keyId) =>
        keyId.All(c => c is >= 'A' and <= 'Z'
                       or >= 'a' and <= 'z'
                       or >= '0' and <= '9'
                       or '.'
                       or '_'
                       or '-');

    /// <summary>
    /// Computes the stable opaque id for a raw dictionary key within a fully scoped report context.
    /// </summary>
    /// <param name="secretKey">The UTF-8 encoded HMAC secret. The array is passed directly to avoid extra copies.</param>
    /// <param name="keyId">The display-safe key id rendered into the id.</param>
    /// <param name="applicationScope">The application or product scope included in the HMAC input.</param>
    /// <param name="environment">The report environment included in the HMAC input.</param>
    /// <param name="rootKey">The root audit key included in the HMAC input.</param>
    /// <param name="rawDictionaryKey">The unredacted dictionary key included in the HMAC input.</param>
    /// <returns>A <c>v1:{keyId}:{24-hex-chars}</c> correlation id with a 96-bit truncated HMAC-SHA256 suffix.</returns>
    internal static string ComputeCorrelationId(
        byte[] secretKey,
        string keyId,
        string applicationScope,
        string environment,
        string rootKey,
        string rawDictionaryKey)
    {
        using var hmac = new HMACSHA256(secretKey);
        var input = BuildInput(applicationScope, environment, rootKey, rawDictionaryKey);
        var hash = hmac.ComputeHash(input);
        return $"{AlgorithmVersion}:{keyId}:{Convert.ToHexString(hash.AsSpan(0, TruncatedBytes)).ToLowerInvariant()}";
    }

    private static byte[] BuildInput(
        string applicationScope,
        string environment,
        string rootKey,
        string rawDictionaryKey)
    {
        var builder = new StringBuilder();
        AppendPart(builder, AlgorithmVersion);
        AppendPart(builder, applicationScope);
        AppendPart(builder, environment);
        AppendPart(builder, rootKey);
        AppendPart(builder, rawDictionaryKey);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AppendPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }
}

/// <summary>
/// Carries the correlation state used while traversing one configured audit entry.
/// </summary>
/// <remarks>
/// The context is internal because callers should configure correlation through
/// <see cref="ConfigAuditDictionaryKeyCorrelationOptions"/> and per-entry
/// <see cref="ConfigAuditEntryOptions.DictionaryKeyCorrelationMode"/>. Available contexts derive opaque ids for raw
/// dictionary keys; unavailable contexts never throw during traversal and instead let consumers emit a warning diagnostic.
/// </remarks>
internal sealed class ConfigAuditDictionaryKeyCorrelationContext
{
    private readonly byte[] _secretKey;
    private readonly string _keyId;
    private readonly string _applicationScope;
    private readonly string _environment;
    private readonly string _rootKey;

    private ConfigAuditDictionaryKeyCorrelationContext(
        byte[] secretKey,
        string keyId,
        string applicationScope,
        string environment,
        string rootKey,
        string? unavailableReason)
    {
        _secretKey = secretKey;
        _keyId = keyId;
        _applicationScope = applicationScope;
        _environment = environment;
        _rootKey = rootKey;
        UnavailableReason = unavailableReason;
    }

    /// <summary>
    /// Gets a value indicating whether this context can derive correlation identifiers.
    /// </summary>
    /// <remarks>
    /// A context is unavailable when the secret key is absent or shorter than <c>MinimumSecretBytes</c> UTF-8 bytes, the
    /// key id is missing or not display-safe, or the application scope is missing.
    /// </remarks>
    public bool IsAvailable => UnavailableReason == null;

    /// <summary>
    /// Gets the human-readable reason correlation is unavailable, or <see langword="null"/> when ids can be derived.
    /// </summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// Creates a context that can derive scoped HMAC ids for dictionary keys in one report entry.
    /// </summary>
    /// <param name="secretKey">The UTF-8 encoded secret key retained for this report traversal.</param>
    /// <param name="keyId">The display-safe key id rendered into each correlation id.</param>
    /// <param name="applicationScope">The application or product scope included in the HMAC input.</param>
    /// <param name="environment">The report environment included in the HMAC input.</param>
    /// <param name="rootKey">The root audit key included in the HMAC input.</param>
    /// <returns>An available correlation context.</returns>
    public static ConfigAuditDictionaryKeyCorrelationContext Available(
        byte[] secretKey,
        string keyId,
        string applicationScope,
        string environment,
        string rootKey) =>
        new(secretKey, keyId, applicationScope, environment, rootKey, unavailableReason: null);

    /// <summary>
    /// Creates a context that records why requested dictionary-key correlation cannot run.
    /// </summary>
    /// <param name="reason">The diagnostic reason to surface when traversal reaches a requested dictionary key.</param>
    /// <returns>An unavailable correlation context.</returns>
    public static ConfigAuditDictionaryKeyCorrelationContext Unavailable(string reason) =>
        new([], string.Empty, string.Empty, string.Empty, string.Empty, reason);

    /// <summary>
    /// Derives the opaque correlation id for a raw dictionary key when this context is available.
    /// </summary>
    /// <param name="rawDictionaryKey">The unredacted dictionary key observed during traversal.</param>
    /// <returns>
    /// A <c>v1:{keyId}:{24-hex-chars}</c> id containing a 96-bit truncated HMAC-SHA256 digest, or <see langword="null"/>
    /// when this context is unavailable.
    /// </returns>
    public string? CreateCorrelationId(string rawDictionaryKey) =>
        IsAvailable
            ? ConfigAuditDictionaryKeyCorrelator.ComputeCorrelationId(
                _secretKey,
                _keyId,
                _applicationScope,
                _environment,
                _rootKey,
                rawDictionaryKey)
            : null;

    /// <summary>
    /// Creates the warning diagnostic emitted when an entry requested correlation but this context is unavailable.
    /// </summary>
    /// <param name="path">The redacted dictionary path where correlation was requested.</param>
    /// <returns>A non-throwing warning diagnostic that omits raw dictionary keys and secret material.</returns>
    public ConfigAuditDiagnostic CreateUnavailableDiagnostic(ConfigAuditPath path) =>
        new()
        {
            Severity = ConfigAuditDiagnosticSeverity.Warning,
            Code = "config-audit-key-correlation-unavailable",
            Key = path.DisplayPath,
            ConfigPath = path.DisplayPath,
            Message = $"Dictionary key correlation for '{path.DisplayPath}' was requested, but {UnavailableReason}."
        };
}
