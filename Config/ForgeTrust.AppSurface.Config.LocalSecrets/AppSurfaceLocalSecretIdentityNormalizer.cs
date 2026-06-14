using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Normalizes LocalSecrets app, environment, prefix, and key values into a stable storage identity.
/// </summary>
public sealed class AppSurfaceLocalSecretIdentityNormalizer
{
    private const int MaxSegmentLength = 128;
    private const int MaxStorageNameLength = 512;

    /// <summary>
    /// Normalizes a local secret identity.
    /// </summary>
    /// <param name="applicationName">The optional configured application name.</param>
    /// <param name="environment">The AppSurface environment name.</param>
    /// <param name="keyPrefix">The optional key prefix.</param>
    /// <param name="key">The AppSurface config key.</param>
    /// <returns>A normalized identity result.</returns>
    public AppSurfaceLocalSecretIdentityResult Normalize(
        string? applicationName,
        string environment,
        string? keyPrefix,
        string key)
    {
        var app = string.IsNullOrWhiteSpace(applicationName)
            ? InferApplicationName()
            : applicationName;

        if (!TryNormalizeSegment(app, nameof(applicationName), allowDots: true, out var normalizedApp, out var diagnostic))
        {
            return AppSurfaceLocalSecretIdentityResult.Invalid(diagnostic);
        }

        if (!TryNormalizeSegment(environment, nameof(environment), allowDots: false, out var normalizedEnvironment, out diagnostic))
        {
            return AppSurfaceLocalSecretIdentityResult.Invalid(diagnostic);
        }

        string? normalizedPrefix = null;
        if (!string.IsNullOrWhiteSpace(keyPrefix) &&
            !TryNormalizeSegment(keyPrefix, nameof(keyPrefix), allowDots: true, out normalizedPrefix, out diagnostic))
        {
            return AppSurfaceLocalSecretIdentityResult.Invalid(diagnostic);
        }

        if (!TryNormalizeKey(key, out var normalizedKey, out diagnostic))
        {
            return AppSurfaceLocalSecretIdentityResult.Invalid(diagnostic);
        }

        var storageName = BuildStorageName(normalizedApp, normalizedEnvironment, normalizedPrefix, normalizedKey);
        if (storageName.Length > MaxStorageNameLength)
        {
            return AppSurfaceLocalSecretIdentityResult.Invalid(
                CreateInvalidIdentityDiagnostic(
                    "local-secret-identity-too-long",
                    "Local secret identity is too long.",
                    "The application, environment, prefix, and key combine into a platform storage name that exceeds the AppSurface limit.",
                    "Shorten the application name, prefix, or config key."));
        }

        return AppSurfaceLocalSecretIdentityResult.Valid(
            new AppSurfaceLocalSecretIdentity(
                normalizedApp,
                normalizedEnvironment,
                normalizedPrefix,
                normalizedKey,
                storageName));
    }

    private static bool TryNormalizeKey(
        string key,
        out string normalized,
        out AppSurfaceLocalSecretDiagnostic diagnostic)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            diagnostic = CreateInvalidIdentityDiagnostic(
                "local-secret-key-empty",
                "Local secret key is empty.",
                "AppSurface cannot store a local secret without a config key.",
                "Pass a non-empty AppSurface config key such as `Stripe:ApiKey`.");
            return false;
        }

        var trimmed = key.Trim();
        if (trimmed.Length > MaxSegmentLength * 2)
        {
            diagnostic = CreateInvalidIdentityDiagnostic(
                "local-secret-key-too-long",
                "Local secret key is too long.",
                "The key exceeds the AppSurface LocalSecrets key length limit.",
                "Use a shorter logical config key.");
            return false;
        }

        if (trimmed.Contains('\0', StringComparison.Ordinal)
            || trimmed.Contains('\r', StringComparison.Ordinal)
            || trimmed.Contains('\n', StringComparison.Ordinal))
        {
            diagnostic = CreateInvalidIdentityDiagnostic(
                "local-secret-key-invalid-character",
                "Local secret key contains unsupported characters.",
                "Nulls and line breaks cannot be represented safely across platform stores.",
                "Use config path separators such as `:` or `.` instead of control characters.");
            return false;
        }

        normalized = trimmed.Replace("__", ":", StringComparison.Ordinal).Replace('\\', '/');
        diagnostic = null!;
        return true;
    }

    private static bool TryNormalizeSegment(
        string? value,
        string name,
        bool allowDots,
        out string normalized,
        out AppSurfaceLocalSecretDiagnostic diagnostic)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostic = CreateInvalidIdentityDiagnostic(
                $"local-secret-{name}-empty",
                "Local secret identity segment is empty.",
                $"The `{name}` segment is required to build a stable local secret identity.",
                $"Set `{name}` to a non-empty value.");
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxSegmentLength)
        {
            diagnostic = CreateInvalidIdentityDiagnostic(
                $"local-secret-{name}-too-long",
                "Local secret identity segment is too long.",
                $"The `{name}` segment exceeds the AppSurface LocalSecrets length limit.",
                $"Use a shorter `{name}` value.");
            return false;
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' || (allowDots && ch == '.'))
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                builder.Append('-');
            }
            else
            {
                diagnostic = CreateInvalidIdentityDiagnostic(
                    $"local-secret-{name}-invalid-character",
                    "Local secret identity segment contains unsupported characters.",
                    $"The `{name}` segment contains a character that cannot be represented consistently across platform stores.",
                    $"Use ASCII letters, digits, dash, underscore{(allowDots ? ", or dot" : string.Empty)}.");
                return false;
            }
        }

        normalized = builder.ToString();
        diagnostic = null!;
        return true;
    }

    private static string BuildStorageName(string app, string environment, string? prefix, string key)
    {
        var logicalKey = string.IsNullOrWhiteSpace(prefix) ? key : $"{prefix}:{key}";
        return $"appsurface:{app}:{environment}:{logicalKey}";
    }

    [ExcludeFromCodeCoverage(
        Justification = "Entry assembly and current directory fallback paths depend on the hosting process; explicit application-name normalization is covered deterministically.")]
    private static string InferApplicationName()
    {
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(entryName))
        {
            return entryName;
        }

        var directoryName = new DirectoryInfo(Environment.CurrentDirectory).Name;
        return string.IsNullOrWhiteSpace(directoryName) ? "AppSurfaceApp" : directoryName;
    }

    private static AppSurfaceLocalSecretDiagnostic CreateInvalidIdentityDiagnostic(
        string code,
        string problem,
        string cause,
        string fix) =>
        new(code, problem, cause, fix, "local-secrets-identity", false);
}
