using System.Text;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures bounded operator evidence for one named-canary result.
/// </summary>
/// <remarks>
/// The result snapshots these values after the callback completes. Text supplied here is returned only from the
/// protected named-canary endpoint; AppSurface bounds and validates its shape but does not classify or redact it.
/// Never include secrets, personal data, provider payloads, URLs, prompts, source text, or private generated content.
/// </remarks>
public sealed class AppSurfaceCanaryResultOptions
{
    private readonly Dictionary<string, string> _details = new(StringComparer.Ordinal);

    /// <summary>Initializes callback-local result options.</summary>
    internal AppSurfaceCanaryResultOptions()
    {
    }

    /// <summary>Gets or sets the optional time at which the evaluator observed its proof.</summary>
    /// <remarks>The value is normalized to UTC when the result is created. No default is inferred.</remarks>
    public DateTimeOffset? ObservedAt { get; set; }

    /// <summary>Gets or sets an optional non-negative count of matching proofs.</summary>
    public int? MatchedCount { get; set; }

    /// <summary>Gets or sets an optional stable machine-readable reason code.</summary>
    /// <remarks>
    /// A reason code is 1-64 lowercase ASCII letters, digits, or internal hyphens and must start and end with a letter
    /// or digit. It is response-only and is intended for machine branching.
    /// </remarks>
    public string? ReasonCode { get; set; }

    /// <summary>Gets or sets optional operator-safe explanatory text of at most 256 UTF-8 bytes.</summary>
    /// <remarks>Blank text, malformed Unicode, and Unicode control scalars are rejected.</remarks>
    public string? Summary { get; set; }

    /// <summary>Gets or sets an optional non-secret correlation identifier.</summary>
    /// <remarks>
    /// The identifier is 1-128 ASCII characters from letters, digits, period, underscore, colon, and hyphen, and it
    /// must start and end with a letter or digit. It is not added to completion logs.
    /// </remarks>
    public string? CorrelationId { get; set; }

    /// <summary>Adds one declared, response-only detail value.</summary>
    /// <param name="key">A lowercase dot-separated key of at most 64 ASCII characters.</param>
    /// <param name="value">A nonblank operator-safe value of at most 128 UTF-8 bytes.</param>
    /// <remarks>
    /// The key must also appear in <see cref="AppSurfaceCanaryRegistrationOptions.AllowedDetailKeys"/> for the same
    /// canary. That registration-specific check occurs after evaluation and fails closed as <c>ASCAN301</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">The key or value violates its grammar, byte bound, or scalar rules.</exception>
    /// <exception cref="InvalidOperationException">The key is duplicated or a seventeenth unique detail is added.</exception>
    public void AddDetail(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        AppSurfaceCanaryResultValidation.ValidateDetailKey(key, nameof(key));
        AppSurfaceCanaryResultValidation.ValidateBoundedText(value, 128, nameof(value), "Detail values");

        if (_details.ContainsKey(key))
        {
            throw new InvalidOperationException($"A named-canary detail with key '{key}' was already added.");
        }

        if (_details.Count == AppSurfaceCanaryResultValidation.MaximumDetails)
        {
            throw new InvalidOperationException(
                $"A named-canary result may contain at most {AppSurfaceCanaryResultValidation.MaximumDetails} details.");
        }

        _details.Add(key, value);
    }

    internal IReadOnlyDictionary<string, string> DetailValues => _details;
}

/// <summary>Validates intrinsic named-canary result evidence.</summary>
internal static partial class AppSurfaceCanaryResultValidation
{
    internal const int MaximumDetails = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void ValidateOptions(AppSurfaceCanaryResultOptions options)
    {
        if (options.MatchedCount < 0)
        {
            throw InvalidConfiguration("MatchedCount must be non-negative.");
        }

        if (options.ReasonCode is not null
            && (options.ReasonCode.Length > 64 || !ReasonCodeRegex().IsMatch(options.ReasonCode)))
        {
            throw InvalidConfiguration(
                "ReasonCode must be 1-64 lowercase ASCII letters, digits, or internal hyphens and start and end with a letter or digit.");
        }

        if (options.Summary is not null)
        {
            ValidateBoundedText(options.Summary, 256, "configure", "Summary");
        }

        if (options.CorrelationId is not null
            && (options.CorrelationId.Length > 128 || !CorrelationIdRegex().IsMatch(options.CorrelationId)))
        {
            throw InvalidConfiguration(
                "CorrelationId must be 1-128 ASCII letters, digits, periods, underscores, colons, or hyphens and start and end with a letter or digit.");
        }
    }

    internal static void ValidateDetailKey(string key, string parameterName)
    {
        if (!IsValidDetailKey(key))
        {
            throw new ArgumentException(
                "Detail keys must be 1-64 lowercase ASCII characters in dot-separated letter, digit, and internal-hyphen segments.",
                parameterName);
        }
    }

    internal static bool IsValidDetailKey(string key) =>
        key.Length <= 64 && DetailKeyRegex().IsMatch(key);

    internal static void ValidateBoundedText(string value, int maximumUtf8Bytes, string parameterName, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} must not be blank.", parameterName);
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Control)
            {
                throw new ArgumentException($"{label} must not contain Unicode control characters.", parameterName);
            }
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
            {
                throw new ArgumentException(
                    $"{label} must not exceed {maximumUtf8Bytes} UTF-8 bytes.",
                    parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"{label} must contain well-formed Unicode.", parameterName, exception);
        }
    }

    private static ArgumentException InvalidConfiguration(string message) => new(message, "configure");

    [GeneratedRegex("\\A[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCodeRegex();

    [GeneratedRegex("\\A[A-Za-z0-9](?:[A-Za-z0-9._:-]{0,126}[A-Za-z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CorrelationIdRegex();

    [GeneratedRegex("\\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DetailKeyRegex();
}
