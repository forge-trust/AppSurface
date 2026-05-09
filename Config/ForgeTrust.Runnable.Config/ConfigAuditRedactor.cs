using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace ForgeTrust.Runnable.Config;

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

    public ConfigAuditRedaction CreatePolicy() =>
        new()
        {
            Enabled = true,
            MatchedFragments = SensitiveFragments.ToArray(),
            Placeholder = Placeholder
        };

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
                return value.ToString();
            }
            catch (JsonException)
            {
                return value.ToString();
            }
            catch (InvalidOperationException)
            {
                return value.ToString();
            }
        }

        if (!ConfigScalarTypes.IsScalar(value.GetType()))
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}

internal sealed record RedactedValue(string? DisplayValue, bool IsRedacted);
