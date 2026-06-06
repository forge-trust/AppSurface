namespace ForgeTrust.AppSurface.Intelligence;

internal static class AppSurfaceProductEventMetadata
{
    private const int MaxEnvelopeIdentifierLength = 128;
    private const int MaxRouteLength = 160;

    internal static string RequireIdentifier(string value, string parameterName)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null)
        {
            throw new ArgumentException("The value must not be empty.", parameterName);
        }

        return normalized;
    }

    internal static string RequireText(string value, string parameterName)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null)
        {
            throw new ArgumentException("The value must not be empty.", parameterName);
        }

        return normalized;
    }

    internal static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    internal static IReadOnlyDictionary<string, string> NormalizeProperties(
        IReadOnlyDictionary<string, string>? properties,
        string parameterName)
    {
        if (properties is null || properties.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            var normalizedKey = RequireIdentifier(key, parameterName);
            normalized[normalizedKey] = NormalizeOptionalText(value) ?? string.Empty;
        }

        return normalized;
    }

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

        var duplicate = normalized
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Property '{duplicate.Key}' is registered more than once.", parameterName);
        }

        return normalized;
    }

    internal static IReadOnlyList<string> NormalizeTextList(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values
            .Select(value => RequireText(value, parameterName))
            .ToArray();
    }

    internal static IReadOnlyList<string> NormalizeOptionalTextList(IEnumerable<string>? values, string parameterName)
    {
        if (values is null)
        {
            return [];
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
            || lower.Contains("bearer ", StringComparison.Ordinal);
    }

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
