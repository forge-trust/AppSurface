using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Auth;

internal static class AppSurfaceAuthMetadata
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, string> Normalize(
        IReadOnlyDictionary<string, string>? metadata,
        string parameterName)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return EmptyMetadata;
        }

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                throw new ArgumentException("Metadata keys must be non-empty strings.", parameterName);
            }

            if (item.Value is null)
            {
                throw new ArgumentException("Metadata values must not be null.", parameterName);
            }

            copy.Add(item.Key, item.Value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    public static string RequireIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier values must be non-empty strings.", parameterName);
        }

        return value;
    }

    public static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
