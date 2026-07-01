using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Validates the privacy-safe metadata and diagnostic text contract used by durable worker envelopes and diagnostics.
/// </summary>
/// <remarks>
/// The validator intentionally uses conservative name and value checks for the metadata channel. Store raw business
/// payloads in app-owned data stores, not in worker metadata. Metadata should contain opaque ids, reason codes, bounded
/// counts, and classified safe labels that can appear in logs or repair dashboards.
/// </remarks>
public static class DurableWorkerMetadataSafety
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly string[] UnsafeNameFragments =
    [
        "authorization",
        "attachment",
        "body",
        "credential",
        "emailbody",
        "modeloutput",
        "oauth",
        "password",
        "prompt",
        "providerurl",
        "rawpayload",
        "secret",
        "token",
    ];

    private static readonly string[] UnsafeValueFragments =
    [
        "authorization:",
        "bearer ",
        "client_secret",
        "oauth_",
        "password=",
        "provider://",
        "refresh_token",
        "http://",
        "https://",
        "-----begin",
    ];

    /// <summary>
    /// Validates a metadata dictionary and returns a copied read-only dictionary.
    /// </summary>
    /// <param name="metadata">Metadata to validate. A null value is treated as an empty dictionary.</param>
    /// <param name="paramName">Caller parameter name used in thrown validation exceptions.</param>
    /// <returns>A read-only copy of the supplied safe metadata.</returns>
    /// <exception cref="ArgumentException">Thrown when a key or value is null, empty, or whitespace.</exception>
    /// <exception cref="DurableWorkerUnsafeMetadataException">Thrown when a key or value appears unsafe.</exception>
    public static IReadOnlyDictionary<string, string> CopySafe(
        IReadOnlyDictionary<string, string>? metadata,
        string paramName = "metadata")
    {
        if (metadata is null || metadata.Count == 0)
        {
            return EmptyMetadata;
        }

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            var safeKey = RequireSafeText(key, "metadata key", paramName);
            var safeValue = RequireSafeText(value, $"metadata value for '{safeKey}'", paramName);

            EnsureSafeKey(safeKey, paramName);
            EnsureSafeValue(safeValue, safeKey, paramName);
            if (!copy.TryAdd(safeKey, safeValue))
            {
                throw new ArgumentException(
                    $"Durable worker metadata key '{safeKey}' appears more than once after trimming.",
                    paramName);
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>
    /// Validates that metadata is safe for diagnostics, logs, durable facts, and projection repair reports.
    /// </summary>
    /// <param name="metadata">Metadata to validate. A null value is treated as an empty dictionary.</param>
    /// <param name="paramName">Caller parameter name used in thrown validation exceptions.</param>
    /// <exception cref="ArgumentException">Thrown when a key or value is null, empty, or whitespace.</exception>
    /// <exception cref="DurableWorkerUnsafeMetadataException">Thrown when a key or value appears unsafe.</exception>
    public static void EnsureSafe(IReadOnlyDictionary<string, string>? metadata, string paramName = "metadata")
    {
        _ = CopySafe(metadata, paramName);
    }

    /// <summary>
    /// Validates diagnostic text and returns a trimmed safe value.
    /// </summary>
    /// <param name="value">Diagnostic text to validate.</param>
    /// <param name="label">Human-readable field label used in validation messages.</param>
    /// <param name="paramName">Caller parameter name used in thrown validation exceptions.</param>
    /// <returns>The trimmed diagnostic text.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is null, empty, or whitespace.</exception>
    /// <exception cref="DurableWorkerUnsafeMetadataException">Thrown when <paramref name="value"/> appears unsafe.</exception>
    public static string CopySafeDiagnosticText(string value, string label, string paramName)
    {
        var safeValue = RequireSafeText(value, label, paramName);
        EnsureSafeValue(safeValue, label, paramName);
        return safeValue;
    }

    private static string RequireSafeText(string? value, string label, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Durable worker {label} must not be null, empty, or whitespace.", paramName);
        }

        return value.Trim();
    }

    private static void EnsureSafeKey(string key, string paramName)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        if (UnsafeNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new DurableWorkerUnsafeMetadataException(
                $"Durable worker metadata key '{key}' appears to describe sensitive or raw payload data.",
                paramName);
        }
    }

    private static void EnsureSafeValue(string value, string key, string paramName)
    {
        var normalized = value.ToLowerInvariant();
        if (UnsafeValueFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            throw new DurableWorkerUnsafeMetadataException(
                $"Durable worker metadata value for '{key}' appears to contain sensitive payload data.",
                paramName);
        }
    }
}
