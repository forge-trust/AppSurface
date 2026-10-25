using System.Collections.Frozen;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Freezes native mappings and claim capacity once for a service container.</summary>
/// <remarks>
/// Read finalized IOptions.Value before calling this constructor. Host callbacks remain outside framework
/// validation handling. Direct provider construction creates an isolated snapshot through this same path.
/// </remarks>
internal sealed class EnvironmentConfigMappingSnapshot
{
    /// <summary>Validates and copies the finalized mapping options without any source access.</summary>
    /// <param name="options">Finalized options. Mutation concurrent with this capture is unsupported.</param>
    /// <exception cref="OptionsValidationException">Existing mapping/capacity validation rejects the options.</exception>
    internal EnvironmentConfigMappingSnapshot(AppSurfaceEnvironmentConfigOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            Mappings = options.Snapshot();
        }
        catch (OptionsValidationException exception)
        {
            EnvironmentConfigCandidateErrors.Enrich(exception, "config-candidate-options-invalid", "FreezeMappings",
                "Repair the named mapping validation failure and rebuild the service container.", "rebuild_container",
                EnvironmentConfigCandidateErrors.LogicalKeyGuideUri + "#config-candidate-options-invalid");
            throw;
        }

        MaxAdHocClaims = options.MaxAdHocClaims;
    }

    /// <summary>Gets the immutable exact-key suffix replacements.</summary>
    internal FrozenDictionary<AppSurfaceConfigKey, string> Mappings { get; }

    /// <summary>Gets the validated native provider claim capacity captured with the mappings.</summary>
    internal int MaxAdHocClaims { get; }
}
