using System.Reflection;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Resolves the AppSurface CLI version string shown by <c>appsurface --version</c>.
/// </summary>
/// <remarks>
/// The .NET tool package version is the user-facing release identity. Runtime assemblies carry that value through
/// <see cref="AssemblyInformationalVersionAttribute"/>, sometimes with a leading release-tag <c>v</c> or build
/// metadata. This helper normalizes the display value while preserving prerelease labels so RC installs remain
/// distinguishable from stable packages.
/// </remarks>
internal static class AppSurfaceCliVersion
{
    private const string MissingPackageMetadataDisplayVersion = "unknown (package version metadata unavailable)";

    /// <summary>
    /// Resolves the display version from the supplied assembly metadata.
    /// </summary>
    /// <param name="assembly">Assembly whose informational version carries the package identity.</param>
    /// <returns>A single-line printable version suitable for CliFx <c>--version</c> output.</returns>
    internal static string ResolveDisplayVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return NormalizeDisplayVersion(informationalVersion);
    }

    /// <summary>
    /// Normalizes package identity metadata for user-facing CLI version output.
    /// </summary>
    /// <param name="informationalVersion">Raw assembly informational version.</param>
    /// <returns>
    /// The package SemVer display value without a leading release-tag <c>v</c> or build metadata, or a truthful
    /// fallback when package identity metadata is unavailable.
    /// </returns>
    internal static string NormalizeDisplayVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return MissingPackageMetadataDisplayVersion;
        }

        var value = informationalVersion.Trim();
        if (value.Any(char.IsControl))
        {
            return MissingPackageMetadataDisplayVersion;
        }

        if (value.Length > 1
            && (value[0] == 'v' || value[0] == 'V')
            && char.IsAsciiDigit(value[1]))
        {
            value = value[1..];
        }

        var metadataSeparator = value.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparator >= 0)
        {
            value = value[..metadataSeparator];
        }

        return string.IsNullOrWhiteSpace(value) || !IsSemVerLikeDisplayVersion(value)
            ? MissingPackageMetadataDisplayVersion
            : value;
    }

    private static bool IsSemVerLikeDisplayVersion(string value)
    {
        var prereleaseSeparator = value.IndexOf('-', StringComparison.Ordinal);
        var core = prereleaseSeparator >= 0 ? value[..prereleaseSeparator] : value;
        var coreParts = core.Split('.');
        if (coreParts.Length != 3 || coreParts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        if (prereleaseSeparator < 0)
        {
            return true;
        }

        var prerelease = value[(prereleaseSeparator + 1)..];
        return prerelease.Length > 0
            && prerelease.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
    }
}
