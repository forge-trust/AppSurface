namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Describes a possible host-owned login prompt without executing sign-in or redirects.
/// </summary>
/// <remarks>
/// The prompt is passive. It never writes cookies, challenges a caller, redirects a response, or invokes an identity
/// provider. Host UI or host adapters decide whether and how to act on it.
/// </remarks>
public sealed class AppSurfaceLoginPrompt
{
    /// <summary>
    /// Creates a passive login prompt.
    /// </summary>
    /// <param name="targetPath">Optional app-relative target. Null or whitespace means no target.</param>
    /// <param name="displayText">Optional display text. Null or whitespace values are normalized to <see langword="null"/>.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    public AppSurfaceLoginPrompt(
        string? targetPath = null,
        string? displayText = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        TargetPath = AppSurfaceAuthPromptTarget.NormalizeTarget(targetPath, nameof(targetPath));
        DisplayText = AppSurfaceAuthMetadata.NormalizeOptionalText(displayText);
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the optional app-relative target for host-owned login UI.
    /// </summary>
    public string? TargetPath { get; }

    /// <summary>
    /// Gets optional display text for host-owned login UI.
    /// </summary>
    public string? DisplayText { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve host-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>
/// Describes a possible host-owned logout prompt without executing sign-out or redirects.
/// </summary>
/// <remarks>
/// The prompt is passive. It never clears cookies, signs out a caller, redirects a response, or invokes an identity
/// provider. Host UI or host adapters decide whether and how to act on it.
/// </remarks>
public sealed class AppSurfaceLogoutPrompt
{
    /// <summary>
    /// Creates a passive logout prompt.
    /// </summary>
    /// <param name="targetPath">Optional app-relative target. Null or whitespace means no target.</param>
    /// <param name="displayText">Optional display text. Null or whitespace values are normalized to <see langword="null"/>.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    public AppSurfaceLogoutPrompt(
        string? targetPath = null,
        string? displayText = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        TargetPath = AppSurfaceAuthPromptTarget.NormalizeTarget(targetPath, nameof(targetPath));
        DisplayText = AppSurfaceAuthMetadata.NormalizeOptionalText(displayText);
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the optional app-relative target for host-owned logout UI.
    /// </summary>
    public string? TargetPath { get; }

    /// <summary>
    /// Gets optional display text for host-owned logout UI.
    /// </summary>
    public string? DisplayText { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve host-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

internal static class AppSurfaceAuthPromptTarget
{
    public static string? NormalizeTarget(string? targetPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        if (!IsSafeAppRelativePath(targetPath))
        {
            throw new ArgumentException("Prompt targets must be safe app-relative paths.", parameterName);
        }

        return targetPath;
    }

    private static bool IsSafeAppRelativePath(string targetPath)
    {
        if (targetPath[0] != '/'
            || targetPath.Length > 1 && (targetPath[1] == '/' || targetPath[1] == '\\')
            || targetPath.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return !targetPath.Any(char.IsControl);
    }
}
