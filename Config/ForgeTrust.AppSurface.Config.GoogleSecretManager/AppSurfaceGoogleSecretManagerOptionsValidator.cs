using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Validates <see cref="AppSurfaceGoogleSecretManagerOptions"/> before provider lookup.
/// </summary>
public sealed class AppSurfaceGoogleSecretManagerOptionsValidator : IValidateOptions<AppSurfaceGoogleSecretManagerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AppSurfaceGoogleSecretManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        if (options.LookupTimeout <= TimeSpan.Zero)
        {
            errors.Add("LookupTimeout must be greater than zero.");
        }

        if (options.CacheTtl is { } cacheTtl && cacheTtl <= TimeSpan.Zero)
        {
            errors.Add("CacheTtl must be greater than zero when set.");
        }

        var duplicateKeys = options.Mappings
            .GroupBy(mapping => mapping.LogicalKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicateKey in duplicateKeys)
        {
            errors.Add($"Logical key '{duplicateKey}' is mapped more than once.");
        }

        foreach (var mapping in options.Mappings)
        {
            ValidateLogicalKey(mapping.LogicalKey, errors);
            ValidateSecretReference(
                options,
                mapping.SecretIdOrResourceName,
                mapping.Version,
                $"Mapping '{mapping.LogicalKey}'",
                errors);
        }

        var duplicateConventionPrefixes = options.Conventions
            .GroupBy(convention => convention.LogicalKeyPrefix, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicateConventionPrefix in duplicateConventionPrefixes)
        {
            errors.Add($"Convention prefix '{duplicateConventionPrefix}' is configured more than once.");
        }

        for (var i = 0; i < options.Conventions.Count; i++)
        {
            for (var j = i + 1; j < options.Conventions.Count; j++)
            {
                var left = options.Conventions[i].LogicalKeyPrefix;
                var right = options.Conventions[j].LogicalKeyPrefix;
                if (!string.IsNullOrWhiteSpace(left)
                    && !string.IsNullOrWhiteSpace(right)
                    && (left.StartsWith(right, StringComparison.Ordinal)
                        || right.StartsWith(left, StringComparison.Ordinal)))
                {
                    errors.Add($"Convention prefixes '{left}' and '{right}' overlap and could claim the same key.");
                }
            }
        }

        foreach (var convention in options.Conventions)
        {
            ValidateLogicalKey(convention.LogicalKeyPrefix, errors, fieldName: "convention prefix");
            if (string.IsNullOrWhiteSpace(options.ProjectId))
            {
                errors.Add($"Convention '{convention.LogicalKeyPrefix}' uses short secret ids and requires ProjectId.");
            }

            ValidateVersion(options, convention.Version ?? options.DefaultVersion, $"Convention '{convention.LogicalKeyPrefix}'", errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateLogicalKey(string logicalKey, List<string> errors, string fieldName = "logical key")
    {
        if (string.IsNullOrWhiteSpace(logicalKey))
        {
            errors.Add($"Google Secret Manager {fieldName} must not be empty.");
        }
    }

    private static void ValidateSecretReference(
        AppSurfaceGoogleSecretManagerOptions options,
        string secretIdOrResourceName,
        string? version,
        string context,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(secretIdOrResourceName))
        {
            errors.Add($"{context} must specify a secret id or resource name.");
            return;
        }

        if (GoogleSecretManagerSecretReference.IsFullVersionResourceName(secretIdOrResourceName))
        {
            if (!string.IsNullOrWhiteSpace(version))
            {
                errors.Add($"{context} uses a full version resource name and must not also specify Version.");
            }

            ValidateVersion(
                options,
                GoogleSecretManagerSecretReference.GetVersionFromFullVersionResourceName(secretIdOrResourceName),
                context,
                errors);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            errors.Add($"{context} uses a short secret id and requires ProjectId.");
        }

        ValidateVersion(options, version ?? options.DefaultVersion, context, errors);
    }

    /// <summary>Validates a file declaration locally using the same project/default/latest policy as mappings.</summary>
    /// <param name="options">The provider's isolated options snapshot.</param>
    /// <param name="key">A short id or a complete projects/.../secrets/.../versions/... resource.</param>
    /// <param name="version">An optional version or alias for short ids only.</param>
    /// <returns>Whether the declaration is structurally valid and satisfies the existing options policy.</returns>
    /// <remarks>The historical mapping validator remains permissive about resource syntax for compatibility.
    /// File declarations additionally reject malformed segments locally, including disabled references. Numeric
    /// versions and Google version aliases are accepted; latest still requires explicit opt-in in every environment.
    /// No resource identity or diagnostic text escapes this method, and no client is constructed.</remarks>
    internal static bool IsValidDeclarationReference(
        AppSurfaceGoogleSecretManagerOptions options, string key, string? version)
    {
        var errors = new List<string>();
        ValidateSecretReference(options, key, version, "Secret declaration", errors);
        if (errors.Count != 0)
        {
            return false;
        }

        if (key.StartsWith("projects/", StringComparison.Ordinal))
        {
            var segments = key.Split('/');
            return version is null
                && segments.Length == 6
                && segments[2] == "secrets"
                && segments[4] == "versions"
                && IsProjectSegment(segments[1])
                && IsSecretId(segments[3])
                && IsVersionSegment(segments[5]);
        }

        return IsProjectSegment(options.ProjectId)
            && IsSecretId(key)
            && IsVersionSegment(version ?? options.DefaultVersion);
    }

    /// <summary>Rejects path separators, escaping, whitespace and empty project segments without normalizing ids.</summary>
    /// <remarks>Project ids/numbers and legacy domain-scoped projects are preserved; project existence is remote.</remarks>
    private static bool IsProjectSegment(string? value) => !string.IsNullOrWhiteSpace(value)
        && value[0] != '.' && value[^1] != '.'
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or ':');

    /// <summary>Checks the Secret Manager secret-id character set and 255-character limit.</summary>
    private static bool IsSecretId(string value) => value.Length is > 0 and <= 255
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Accepts numeric versions or bounded aliases, preserving case and explicit latest policy.</summary>
    /// <remarks>Alias syntax follows the
    /// <see href="https://cloud.google.com/secret-manager/docs/reference/rest/v1/projects.secrets">Secret resource's versionAliases contract</see>.
    /// NEW is reserved.
    /// This structural check does not resolve aliases or establish that a numeric version exists.</remarks>
    private static bool IsVersionSegment(string? value) => !string.IsNullOrEmpty(value)
        && (value.All(char.IsAsciiDigit)
            || (value.Length <= 63 && char.IsAsciiLetter(value[0]) && value != "NEW"
                && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')));

    private static void ValidateVersion(
        AppSurfaceGoogleSecretManagerOptions options,
        string? version,
        string context,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            errors.Add($"{context} must specify a secret version or configure DefaultVersion.");
            return;
        }

        if (string.Equals(version, AppSurfaceGoogleSecretManagerOptions.LatestVersion, StringComparison.OrdinalIgnoreCase)
            && !options.AllowLatestVersion)
        {
            errors.Add($"{context} uses 'latest'; call AllowLatest() to opt in explicitly.");
        }
    }
}
