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
                if (left.StartsWith(right, StringComparison.Ordinal)
                    || right.StartsWith(left, StringComparison.Ordinal))
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
