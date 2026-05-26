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

internal sealed class ConfigAuditDictionaryKeyCorrelator
{
    private const string AlgorithmVersion = "v1";
    private const int MinimumSecretBytes = 32;
    private const int TruncatedBytes = 12;

    private readonly ConfigAuditDictionaryKeyCorrelationOptions _options;

    public ConfigAuditDictionaryKeyCorrelator(ConfigAuditDictionaryKeyCorrelationOptions? options)
    {
        _options = options ?? new ConfigAuditDictionaryKeyCorrelationOptions();
    }

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

    internal static string? NormalizeKeyId(string? keyId) => keyId?.Trim();

    internal static string? NormalizeApplicationScope(string? applicationScope) => applicationScope?.Trim();

    internal static bool IsDisplaySafeKeyId(string keyId) =>
        keyId.All(c => c is >= 'A' and <= 'Z'
                       or >= 'a' and <= 'z'
                       or >= '0' and <= '9'
                       or '.'
                       or '_'
                       or '-');

    internal static string ComputeCorrelationId(
        ReadOnlySpan<byte> secretKey,
        string keyId,
        string applicationScope,
        string environment,
        string rootKey,
        string rawDictionaryKey)
    {
        using var hmac = new HMACSHA256(secretKey.ToArray());
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

    public bool IsAvailable => UnavailableReason == null;

    public string? UnavailableReason { get; }

    public static ConfigAuditDictionaryKeyCorrelationContext Available(
        byte[] secretKey,
        string keyId,
        string applicationScope,
        string environment,
        string rootKey) =>
        new(secretKey, keyId, applicationScope, environment, rootKey, unavailableReason: null);

    public static ConfigAuditDictionaryKeyCorrelationContext Unavailable(string reason) =>
        new([], string.Empty, string.Empty, string.Empty, string.Empty, reason);

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
