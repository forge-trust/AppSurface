using ForgeTrust.AppSurface.Config;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>Validates convention mappings against the finalized declaration registry during options startup validation.</summary>
internal sealed class AppSurfaceGoogleSecretManagerDeclarationValidator : IValidateOptions<AppSurfaceGoogleSecretManagerOptions>
{
    private readonly ConfigDeclarationRegistry _declarations;

    public AppSurfaceGoogleSecretManagerDeclarationValidator(ConfigDeclarationRegistry declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        _declarations = declarations;
    }

    public ValidateOptionsResult Validate(string? name, AppSurfaceGoogleSecretManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseResult = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(name, options);
        var failures = baseResult.Failed ? baseResult.Failures.ToList() : [];
        var snapshot = options.Snapshot();

        foreach (var declaration in _declarations.Entries)
        {
            if (snapshot.Mappings.Any(mapping =>
                    AppSurfaceConfigKey.TryParse(mapping.LogicalKey, out var mappedKey)
                    && mappedKey!.Equals(declaration.LogicalKey)))
            {
                continue;
            }

            var matching = snapshot.Conventions.Where(convention =>
            {
                if (!AppSurfaceConfigKey.TryParse(convention.LogicalKeyPrefix, out var prefix))
                {
                    return false;
                }

                return declaration.LogicalKey.IsSameOrDescendantOf(prefix!);
            }).ToArray();

            if (matching.Length == 0)
            {
                continue;
            }

            try
            {
                _ = GoogleSecretManagerSecretReference.FromConvention(snapshot, matching[0], declaration.LogicalKey.Value);
            }
            catch (FormatException)
            {
                failures.Add($"Google Secret Manager declaration '{ConfigDiagnosticText.Identifier(declaration.LogicalKey.Value)}' cannot be represented by its convention.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
