using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Applies the built-in audit redaction and value formatting policy.
/// </summary>
/// <remarks>
/// Matching is fragment based and case-insensitive across keys, config paths, applied paths, and environment variable
/// names. Sensitive values are replaced with a fixed placeholder before rendering. Non-sensitive complex objects may
/// produce a <see langword="null"/> display value so callers can rely on child entries instead of an unsafe dump.
/// Formatting failures are swallowed intentionally to keep audit generation best-effort.
/// </remarks>
internal sealed class ConfigAuditRedactor
{
    private const string Placeholder = "[redacted]";

    private static readonly string[] SensitiveFragments =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "apikey",
        "api_key",
        "key",
        "connectionstring",
        "connection_string",
        "credential",
        "private"
    ];

    /// <summary>
    /// Creates a snapshot of the redaction policy applied to reports.
    /// </summary>
    /// <returns>A policy snapshot with a copy of the sensitive fragments and the placeholder text.</returns>
    public ConfigAuditRedaction CreatePolicy() =>
        new()
        {
            Enabled = true,
            MatchedFragments = SensitiveFragments.ToArray(),
            Placeholder = Placeholder
        };

    /// <summary>
    /// Formats <paramref name="value"/> for display and redacts it when the key or sources look sensitive.
    /// </summary>
    /// <param name="key">The configuration key being formatted.</param>
    /// <param name="value">The resolved value, if any.</param>
    /// <param name="sources">The sources that contributed to the value.</param>
    /// <returns>The display value and whether it was replaced by the redaction placeholder.</returns>
    public RedactedValue FormatValue(
        string key,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources)
    {
        if (IsSensitive(key, sources))
        {
            return new RedactedValue(Placeholder, true);
        }

        return new RedactedValue(FormatUnsafe(value), false);
    }

    private static bool IsSensitive(string key, IReadOnlyList<ConfigAuditSourceRecord> sources)
    {
        if (ContainsSensitiveFragment(key))
        {
            return true;
        }

        return sources.Any(source => source.Sensitivity == ConfigAuditSensitivity.Sensitive
                                     || ContainsSensitiveFragment(source.ConfigPath)
                                     || ContainsSensitiveFragment(source.AppliedToPath)
                                     || ContainsSensitiveFragment(source.EnvironmentVariableName));
    }

    private static bool ContainsSensitiveFragment(string? value) =>
        value != null
        && SensitiveFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string? FormatUnsafe(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string s)
        {
            return s;
        }

        if (value is IEnumerable && value.GetType() != typeof(string))
        {
            try
            {
                return JsonSerializer.Serialize(value);
            }
            catch (NotSupportedException)
            {
                return SafeToString(value);
            }
            catch (JsonException)
            {
                return SafeToString(value);
            }
            catch (InvalidOperationException)
            {
                return SafeToString(value);
            }
        }

        if (!ConfigScalarTypes.IsScalar(value.GetType()))
        {
            return null;
        }

        try
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return SafeToString(value);
        }
    }

    private static string SafeToString(object value)
    {
        try
        {
            return value.ToString() ?? value.GetType().Name;
        }
        catch (Exception)
        {
            return value.GetType().Name;
        }
    }
}

/// <summary>
/// Describes a formatted value after applying the redaction policy.
/// </summary>
/// <param name="DisplayValue">The safe display value, or <see langword="null"/> for non-scalar objects.</param>
/// <param name="IsRedacted">A value indicating whether the placeholder replaced the original value.</param>
internal sealed record RedactedValue(string? DisplayValue, bool IsRedacted);
