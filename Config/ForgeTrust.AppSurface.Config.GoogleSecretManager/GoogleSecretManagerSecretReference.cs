namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

internal sealed record GoogleSecretManagerSecretReference(
    string LogicalKey,
    string ResourceName,
    string? RequestedVersion)
{
    private const string VersionSegment = "/versions/";

    public static bool IsFullVersionResourceName(string value) =>
        value.StartsWith("projects/", StringComparison.Ordinal)
        && value.Contains("/secrets/", StringComparison.Ordinal)
        && value.Contains(VersionSegment, StringComparison.Ordinal);

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
            return new GoogleSecretManagerSecretReference(mapping.LogicalKey, mapping.SecretIdOrResourceName, null);
        }

        var version = mapping.Version ?? options.DefaultVersion!;
        return new GoogleSecretManagerSecretReference(
            mapping.LogicalKey,
            BuildResourceName(options.ProjectId!, mapping.SecretIdOrResourceName, version),
            version);
    }

    public static GoogleSecretManagerSecretReference FromConvention(
        AppSurfaceGoogleSecretManagerOptions options,
        AppSurfaceGoogleSecretConvention convention,
        string logicalKey)
    {
        var version = convention.Version ?? options.DefaultVersion!;
        var suffix = logicalKey[convention.LogicalKeyPrefix.Length..];
        var normalized = NormalizeSecretId(suffix);
        var secretId = $"{convention.SecretIdPrefix}{normalized}";
        return new GoogleSecretManagerSecretReference(
            logicalKey,
            BuildResourceName(options.ProjectId!, secretId, version),
            version);
    }

    private static string BuildResourceName(string projectId, string secretId, string version) =>
        $"projects/{projectId}/secrets/{secretId}/versions/{version}";

    private static string NormalizeSecretId(string logicalKey) =>
        logicalKey
            .Replace(':', '-')
            .Replace('.', '-')
            .Replace('_', '-')
            .ToLowerInvariant();
}
