namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

internal sealed record GoogleSecretManagerSecretReference(
    string LogicalKey,
    string ResourceName,
    string? RequestedVersion,
    AppSurfaceConfigKey Key)
{
    private const string VersionSegment = "/versions/";

    public static bool IsFullVersionResourceName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith("projects/", StringComparison.Ordinal)
        && value.Contains("/secrets/", StringComparison.Ordinal)
        && value.Contains(VersionSegment, StringComparison.Ordinal)
        && GetVersionFromFullVersionResourceName(value).Length > 0;

    public static string GetVersionFromFullVersionResourceName(string value)
    {
        var versionStart = value.LastIndexOf(VersionSegment, StringComparison.Ordinal);
        return versionStart < 0 ? string.Empty : value[(versionStart + VersionSegment.Length)..];
    }

    public static GoogleSecretManagerSecretReference FromMapping(
        AppSurfaceGoogleSecretManagerOptions options,
        AppSurfaceGoogleSecretMapping mapping)
    {
        if (IsFullVersionResourceName(mapping.SecretIdOrResourceName))
        {
            var key = AppSurfaceConfigKey.Parse(mapping.LogicalKey);
            return new GoogleSecretManagerSecretReference(mapping.LogicalKey, mapping.SecretIdOrResourceName, null, key);
        }

        var version = mapping.Version ?? options.DefaultVersion!;
        var mappingKey = AppSurfaceConfigKey.Parse(mapping.LogicalKey);
        return new GoogleSecretManagerSecretReference(mapping.LogicalKey, BuildResourceName(options.ProjectId!, mapping.SecretIdOrResourceName, version), version, mappingKey);
    }

    public static GoogleSecretManagerSecretReference FromConvention(
        AppSurfaceGoogleSecretManagerOptions options,
        AppSurfaceGoogleSecretConvention convention,
        string logicalKey)
    {
        var version = convention.Version ?? options.DefaultVersion!;
        var key = AppSurfaceConfigKey.Parse(logicalKey);
        var prefix = AppSurfaceConfigKey.Parse(convention.LogicalKeyPrefix);
        if (!key.IsSameOrDescendantOf(prefix))
        {
            throw new ArgumentException("The logical key is outside the convention prefix.", nameof(logicalKey));
        }

        var encoded = EncodeKey(key);
        var secretId = $"{convention.SecretIdPrefix}{encoded}";
        if (!IsValidSecretId(secretId))
        {
            throw new FormatException("The convention result cannot be represented as a Google Secret Manager secret id.");
        }

        return new GoogleSecretManagerSecretReference(logicalKey, BuildResourceName(options.ProjectId!, secretId, version), version, key);
    }

    private static string BuildResourceName(string projectId, string secretId, string version) =>
        $"projects/{projectId}/secrets/{secretId}/versions/{version}";

    internal static bool TryEncodeKey(AppSurfaceConfigKey key, out string encoded)
    {
        encoded = string.Empty;
        var segments = new List<string>(key.Segments.Length);
        foreach (var segment in key.Segments)
        {
            if (segment.Length == 0 || segment.StartsWith('-') || segment.EndsWith('-') || segment.Contains("--", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                {
                    return false;
                }
            }

            segments.Add(segment.ToLowerInvariant());
        }

        encoded = string.Join("--", segments);
        // AppSurfaceConfigKey guarantees at least one non-empty segment.
        return encoded.Length <= 255;
    }

    internal static bool IsValidSecretId(string value) =>
        value.Length is > 0 and <= 255
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string EncodeKey(AppSurfaceConfigKey key) =>
        TryEncodeKey(key, out var encoded)
            ? encoded
            : throw new FormatException("The logical key cannot be represented as a Google Secret Manager secret id.");
}
