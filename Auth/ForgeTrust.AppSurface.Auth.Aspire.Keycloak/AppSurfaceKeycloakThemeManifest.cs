using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Represents deterministic, secret-safe evidence for a Keycloak login theme source tree.
/// </summary>
/// <remarks>
/// The manifest contains normalized relative paths, byte lengths, and content digests. It intentionally omits source
/// machine paths and file contents so it can be retained as safe build evidence.
/// </remarks>
public sealed record AppSurfaceKeycloakThemeManifest(
    string ThemeName,
    IReadOnlyList<AppSurfaceKeycloakThemeManifestEntry> Files,
    string Digest)
{
    private const int MaximumFileCount = 256;
    private const long MaximumFileBytes = 1_048_576;
    private const long MaximumTotalBytes = 8_388_608;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".ftl", ".gif", ".htm", ".html", ".jpeg", ".jpg", ".js", ".png", ".properties", ".svg", ".ttf", ".webp", ".woff", ".woff2",
    };

    private static readonly HashSet<string> TemplateExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ftl" };

    /// <summary>
    /// Creates a deterministic manifest for a resolved Keycloak theme source directory.
    /// </summary>
    /// <param name="themeName">Validated Keycloak theme name.</param>
    /// <param name="sourceDirectory">Absolute source directory containing the theme's <c>login</c> directory.</param>
    /// <returns>A deterministic manifest with ordinally sorted entries.</returns>
    internal static AppSurfaceKeycloakThemeManifest Create(string themeName, string sourceDirectory)
        => CreateSafely(themeName, sourceDirectory, AllowedExtensions, requireThemeProperties: true);

    internal static AppSurfaceKeycloakThemeManifest CreateTemplateBaseline(string themeName, string sourceDirectory)
        => CreateSafely(themeName, sourceDirectory, TemplateExtensions, requireThemeProperties: false);

    private static AppSurfaceKeycloakThemeManifest CreateSafely(
        string themeName,
        string sourceDirectory,
        IReadOnlySet<string> allowedExtensions,
        bool requireThemeProperties)
    {
        try
        {
            return CreateCore(themeName, sourceDirectory, allowedExtensions, requireThemeProperties);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SourceInvalid($"the source could not be inspected safely ({exception.GetType().Name}).");
        }
    }

    private static AppSurfaceKeycloakThemeManifest CreateCore(
        string themeName,
        string sourceDirectory,
        IReadOnlySet<string> allowedExtensions,
        bool requireThemeProperties)
    {
        if (!Path.IsPathFullyQualified(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            throw SourceInvalid("the source directory does not exist.");
        }

        if (IsReparsePoint(sourceDirectory))
        {
            throw SourceInvalid("the source directory cannot be a symbolic link or other reparse point.");
        }

        var entries = new List<AppSurfaceKeycloakThemeManifestEntry>();
        var collisionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (IsReparsePoint(entry))
            {
                throw SourceInvalid("the source directory cannot contain symbolic links or other reparse points.");
            }

            if (Directory.Exists(entry))
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(sourceDirectory, entry);
            if (!collisionKeys.Add(relativePath))
            {
                throw SourceCollision($"source entries collide at normalized path '{relativePath}'.");
            }

            var extension = Path.GetExtension(relativePath);
            if (!allowedExtensions.Contains(extension))
            {
                throw SourceInvalid($"the source contains unsupported file '{relativePath}'.");
            }

            var length = new FileInfo(entry).Length;
            if (length > MaximumFileBytes)
            {
                throw SourceInvalid($"file '{relativePath}' exceeds the {MaximumFileBytes} byte limit.");
            }

            totalBytes += length;
            if (totalBytes > MaximumTotalBytes)
            {
                throw SourceInvalid($"the source exceeds the {MaximumTotalBytes} byte total limit.");
            }

            entries.Add(new AppSurfaceKeycloakThemeManifestEntry(relativePath, length, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry)))));
            if (entries.Count > MaximumFileCount)
            {
                throw SourceInvalid($"the source exceeds the {MaximumFileCount} file limit.");
            }
        }

        var orderedFiles = entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
        if (requireThemeProperties && !orderedFiles.Any(entry => string.Equals(entry.RelativePath, "login/theme.properties", StringComparison.Ordinal)))
        {
            throw SourceInvalid("the source must contain login/theme.properties.");
        }

        var canonical = string.Join(
            "\n",
            orderedFiles.Select(entry => $"{entry.RelativePath}\n{entry.Length}\n{entry.Sha256}"));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new AppSurfaceKeycloakThemeManifest(themeName, orderedFiles, digest);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string NormalizeRelativePath(string sourceDirectory, string path)
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.StartsWith("../", StringComparison.Ordinal)
            || relativePath.Contains("//", StringComparison.Ordinal)
            || relativePath.Split('/').Any(segment => segment is "." or ".."))
        {
            throw SourceInvalid("a source file resolves outside the declared theme directory.");
        }

        return relativePath;
    }

    private static AppSurfaceKeycloakException SourceInvalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid,
            $"Problem: the AppSurface Keycloak login theme source is invalid. Cause: {detail} Fix: keep a bounded, regular-file theme tree rooted at login/theme.properties. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#source-policy. Code: {AppSurfaceKeycloakDiagnosticCodes.ThemeSourceInvalid}.");

    private static AppSurfaceKeycloakException SourceCollision(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.ThemeSourceCollision,
            $"Problem: AppSurface Keycloak login theme source paths collide. Cause: {detail} Fix: rename one file so slash-normalized paths are unique on every supported host. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#source-policy. Code: {AppSurfaceKeycloakDiagnosticCodes.ThemeSourceCollision}.");
}

/// <summary>
/// Represents one normalized, content-addressed file in a Keycloak login theme manifest.
/// </summary>
/// <param name="RelativePath">The slash-separated path relative to the theme root.</param>
/// <param name="Length">The exact file length in bytes.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 digest of the file content.</param>
public sealed record AppSurfaceKeycloakThemeManifestEntry(string RelativePath, long Length, string Sha256);
