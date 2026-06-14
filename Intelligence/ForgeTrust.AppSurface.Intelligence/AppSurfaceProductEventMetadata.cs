using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Normalizes and sanitizes product-event metadata before registry validation or sink emission.
/// </summary>
/// <remarks>
/// This internal helper separates constructor-time normalization, which trims and rejects empty required values, from
/// emission-time sanitization, which may return <see langword="null"/> for unsafe optional envelope values. Methods
/// that validate required input throw for empty or duplicate values; methods named <c>Sanitize*</c> return
/// <see langword="null"/> when values are too long, contain unsafe characters, or look like tokens, cookies, secrets,
/// connection strings, bearer headers, stack traces, full URLs, query strings, or fragments.
/// </remarks>
internal static class AppSurfaceProductEventMetadata
{
    /// <summary>
    /// Maximum length accepted for actor, session, and correlation identifiers after trimming.
    /// </summary>
    private const int MaxEnvelopeIdentifierLength = 128;

    /// <summary>
    /// Maximum length accepted for route templates or surface names after trimming.
    /// </summary>
    private const int MaxRouteLength = 160;

    /// <summary>
    /// Trims and requires a non-empty identifier-like value.
    /// </summary>
    /// <remarks>
    /// This method is for event and property names. It does not apply the stricter route or envelope sanitizers.
    /// Callers receive an <see cref="ArgumentException"/> for null, empty, or whitespace-only input.
    /// </remarks>
    internal static string RequireIdentifier(string value, string parameterName)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null)
        {
            throw new ArgumentException("The value must not be empty.", parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Trims and requires non-empty text.
    /// </summary>
    /// <remarks>
    /// Use this for registry descriptions, owners, examples, and allowed values. It throws for null, empty, or
    /// whitespace-only values, and otherwise returns the trimmed text unchanged.
    /// </remarks>
    internal static string RequireText(string value, string parameterName)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null)
        {
            throw new ArgumentException("The value must not be empty.", parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Trims optional text and normalizes null, empty, or whitespace-only values to <see langword="null"/>.
    /// </summary>
    internal static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>
    /// Copies property keys and values into an ordinal, read-only dictionary.
    /// </summary>
    /// <remarks>
    /// Property names are required and trimmed through <see cref="RequireIdentifier"/>. Property values are trimmed,
    /// with null or whitespace-only values normalized to an empty string so optional string properties can still be
    /// represented. Later duplicate keys replace earlier values according to dictionary assignment semantics.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> NormalizeProperties(
        IReadOnlyDictionary<string, string>? properties,
        string parameterName)
    {
        if (properties is null || properties.Count == 0)
        {
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            var normalizedKey = RequireIdentifier(key, parameterName);
            normalized[normalizedKey] = NormalizeOptionalText(value) ?? string.Empty;
        }

        return new ReadOnlyDictionary<string, string>(normalized);
    }

    /// <summary>
    /// Copies and validates a required property-contract sequence.
    /// </summary>
    /// <remarks>
    /// The returned list is a read-only wrapper over a private copy. Empty sequences and duplicate property names throw
    /// because every event contract must publish an explicit schema. Null property-contract entries throw as invalid
    /// contract metadata instead of surfacing later as registry or dispatcher failures.
    /// </remarks>
    internal static IReadOnlyList<AppSurfaceProductEventPropertyContract> NormalizeContracts(
        IEnumerable<AppSurfaceProductEventPropertyContract> contracts,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(contracts);

        var normalized = contracts.ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one property contract is required.", parameterName);
        }

        if (normalized.Any(property => property is null))
        {
            throw new ArgumentException("Property contract entries must not be null.", parameterName);
        }

        var duplicate = normalized
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Property '{duplicate.Key}' is registered more than once.", parameterName);
        }

        return Array.AsReadOnly(normalized);
    }

    /// <summary>
    /// Copies and validates a required list of non-empty text values.
    /// </summary>
    /// <remarks>
    /// Values are trimmed with <see cref="RequireText"/> and returned as a read-only wrapper over a private copy.
    /// Duplicate handling is left to callers because some lists may allow duplicates while others do not.
    /// </remarks>
    internal static IReadOnlyList<string> NormalizeTextList(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);

        return Array.AsReadOnly(values
            .Select(value => RequireText(value, parameterName))
            .ToArray());
    }

    /// <summary>
    /// Copies and validates an optional list of unique non-empty text values.
    /// </summary>
    /// <remarks>
    /// Null input becomes an empty read-only list. Non-null input is trimmed, duplicate values throw, and the returned
    /// value should be treated as immutable by registry contracts.
    /// </remarks>
    internal static IReadOnlyList<string> NormalizeOptionalTextList(IEnumerable<string>? values, string parameterName)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var normalized = NormalizeTextList(values, parameterName);
        var duplicate = normalized
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Value '{duplicate.Key}' is registered more than once.", parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Filters optional actor, session, or correlation identifiers for sink-safe emission.
    /// </summary>
    /// <remarks>
    /// Safe identifiers are short ASCII tokens containing only letters, digits, <c>-</c>, <c>_</c>, <c>.</c>,
    /// <c>:</c>, or <c>|</c>. Values with whitespace, PII-shaped punctuation, bearer headers, tokens, cookies,
    /// secrets, connection strings, or stack-trace text are dropped by returning <see langword="null"/>.
    /// </remarks>
    internal static string? SanitizeEnvelopeIdentifier(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null
            || normalized.Length > MaxEnvelopeIdentifierLength
            || ContainsForbiddenValueShape(normalized))
        {
            return null;
        }

        foreach (var character in normalized)
        {
            if (char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':' or '|')
            {
                continue;
            }

            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Filters optional route templates or surface names for sink-safe emission.
    /// </summary>
    /// <remarks>
    /// Routes may contain path separators and route-template braces, but full URLs, protocol-relative URLs, query
    /// strings, fragments outside route tokens, unsafe characters, high-risk value shapes, and overlong routes are
    /// dropped. Do not pass concrete route values that contain user, tenant, object, or channel identifiers.
    /// </remarks>
    internal static string? SanitizeRoute(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null
            || normalized.Length > MaxRouteLength
            || ContainsForbiddenValueShape(normalized)
            || normalized.Contains("://", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || ContainsQueryOrFragment(normalized))
        {
            return null;
        }

        foreach (var character in normalized)
        {
            if (char.IsAsciiLetterOrDigit(character)
                || character is '/' or '{' or '}' or '?' or ':' or '-' or '_' or '.' or '*')
            {
                continue;
            }

            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Filters property names before they are included in safe diagnostics.
    /// </summary>
    /// <remarks>
    /// Product-event property keys are caller input. Diagnostic output must not echo raw keys that look like URLs,
    /// email addresses, credentials, secrets, bearer headers, or other high-risk values.
    /// </remarks>
    internal static string SanitizeDiagnosticPropertyName(string value)
    {
        return SanitizeDiagnosticIdentifier(value);
    }

    /// <summary>
    /// Filters event names before they are included in safe diagnostics.
    /// </summary>
    /// <remarks>
    /// Product-event names may come from callers before registry matching. Diagnostic output must not echo raw names
    /// that look like URLs, email addresses, credentials, secrets, bearer headers, or other high-risk values.
    /// </remarks>
    internal static string SanitizeDiagnosticEventName(string value)
    {
        return SanitizeDiagnosticIdentifier(value);
    }

    /// <summary>
    /// Filters owner/source labels before they are included in safe diagnostics.
    /// </summary>
    /// <remarks>
    /// Owners are contract metadata rather than event payloads, but registration failures are often copied into issue
    /// trackers and CI logs. This keeps those diagnostics descriptive without echoing accidental addresses, URLs, or
    /// credential-shaped labels.
    /// </remarks>
    internal static string SanitizeDiagnosticOwner(string value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null
            || normalized.Length > 80
            || ContainsForbiddenValueShape(normalized))
        {
            return "[unsafe-owner]";
        }

        foreach (var character in normalized)
        {
            if (char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ' ')
            {
                continue;
            }

            return "[unsafe-owner]";
        }

        return normalized;
    }

    /// <summary>
    /// Returns whether a caller-supplied identifier can be emitted in diagnostics without scrubbing.
    /// </summary>
    /// <param name="value">Identifier to inspect.</param>
    /// <returns><see langword="true" /> when diagnostics may include the identifier as-is.</returns>
    internal static bool IsSafeDiagnosticIdentifier(string value)
    {
        return SanitizeDiagnosticIdentifier(value) == NormalizeOptionalText(value);
    }

    private static string SanitizeDiagnosticIdentifier(string value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null
            || normalized.Length > 64
            || ContainsForbiddenValueShape(normalized))
        {
            return "[unsafe-property-name]";
        }

        foreach (var character in normalized)
        {
            if (char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.')
            {
                continue;
            }

            return "[unsafe-property-name]";
        }

        return normalized;
    }

    /// <summary>
    /// Returns whether a value looks like a secret, credential, connection string, bearer header, or stack trace.
    /// </summary>
    internal static bool ContainsForbiddenValueShape(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains("token=", StringComparison.Ordinal)
            || lower.Contains("cookie=", StringComparison.Ordinal)
            || lower.Contains("secret=", StringComparison.Ordinal)
            || lower.Contains("password=", StringComparison.Ordinal)
            || lower.Contains("connectionstring", StringComparison.Ordinal)
            || lower.Contains("connection string", StringComparison.Ordinal)
            || lower.Contains("stack trace", StringComparison.Ordinal)
            || lower.Contains("bearer ", StringComparison.Ordinal)
            || lower.Contains("://", StringComparison.Ordinal)
            || lower.StartsWith("//", StringComparison.Ordinal)
            || lower.Contains("?token=", StringComparison.Ordinal)
            || lower.Contains("?secret=", StringComparison.Ordinal)
            || lower.Contains("?password=", StringComparison.Ordinal)
            || lower.Contains("&token=", StringComparison.Ordinal)
            || lower.Contains("&secret=", StringComparison.Ordinal)
            || lower.Contains("&password=", StringComparison.Ordinal)
            || LooksLikeEmailAddress(lower);
    }

    private static bool LooksLikeEmailAddress(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == value.Length - 1)
        {
            return false;
        }

        var dotAfterAt = value.IndexOf('.', at + 1);
        if (dotAfterAt <= at + 1 || dotAfterAt == value.Length - 1)
        {
            return false;
        }

        return value.All(character => !char.IsWhiteSpace(character));
    }

    /// <summary>
    /// Returns whether a route-like value contains a query string or fragment outside a route token.
    /// </summary>
    private static bool ContainsQueryOrFragment(string value)
    {
        var routeParameterDepth = 0;
        foreach (var character in value)
        {
            if (character == '{')
            {
                routeParameterDepth++;
                continue;
            }

            if (character == '}')
            {
                routeParameterDepth = Math.Max(0, routeParameterDepth - 1);
                continue;
            }

            if (character == '#'
                || (character == '?' && routeParameterDepth == 0))
            {
                return true;
            }
        }

        return false;
    }
}
