using System.Globalization;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Converts textual configuration provider values into AppSurface configuration value types.
/// </summary>
/// <remarks>
/// Providers that store text, such as local secret stores and remote secret managers, should use this helper so scalar,
/// enum, <see cref="Guid"/>, and JSON object conversion stay consistent. The helper intentionally returns a boolean
/// instead of exposing raw parse exception messages because provider diagnostics must not leak secret payloads.
/// </remarks>
public static class ConfigValueConverter
{
    /// <summary>
    /// Attempts to convert <paramref name="raw"/> into <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The requested configuration type.</typeparam>
    /// <param name="raw">The raw provider text.</param>
    /// <param name="value">The converted value when conversion succeeds.</param>
    /// <returns><see langword="true"/> when conversion succeeds.</returns>
    public static bool TryConvert<T>(string raw, out T? value)
    {
        if (TryConvert(raw, typeof(T), out var converted))
        {
            value = (T?)converted;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Attempts to convert <paramref name="raw"/> into <paramref name="targetType"/>.
    /// </summary>
    /// <param name="raw">The raw provider text.</param>
    /// <param name="targetType">The requested configuration type.</param>
    /// <param name="value">The converted value when conversion succeeds.</param>
    /// <returns><see langword="true"/> when conversion succeeds.</returns>
    public static bool TryConvert(string raw, Type targetType, out object? value)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(targetType);

        try
        {
            if (targetType == typeof(string))
            {
                value = raw;
                return true;
            }

            var nullableTarget = Nullable.GetUnderlyingType(targetType);
            if (nullableTarget != null && raw.Length == 0)
            {
                value = null;
                return true;
            }

            var effectiveTarget = nullableTarget ?? targetType;
            if (effectiveTarget.IsEnum)
            {
                value = Enum.Parse(effectiveTarget, raw, ignoreCase: true);
                return true;
            }

            if (effectiveTarget == typeof(Guid))
            {
                value = Guid.Parse(raw);
                return true;
            }

            if (effectiveTarget.IsPrimitive || effectiveTarget == typeof(decimal))
            {
                value = Convert.ChangeType(raw, effectiveTarget, CultureInfo.InvariantCulture);
                return true;
            }

            value = JsonSerializer.Deserialize(raw, effectiveTarget);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException or JsonException)
        {
            value = null;
            return false;
        }
    }
}
