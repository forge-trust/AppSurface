using System.Reflection;

namespace ForgeTrust.AppSurface.Core;

/// <summary>Rejects incompatible pre-1.0 configuration contracts before module or plugin activation.</summary>
/// <remarks>
/// Applications loading plugins must call <see cref="ValidateAssemblies"/> after loading assembly metadata and before
/// enumerating types or constructing providers. Standard AppSurface startup checks already loaded assemblies before
/// its string entry point invokes the root factory, before host-builder configuration, and before the first dependency
/// registration callback. The guard checks each supplied assembly's identity and direct assembly references, without
/// loading transitive dependencies. It does not intercept future assembly loads or code that runs before startup.
/// A coordinated rebuild is required; this guard does not adapt the previous provider SPI or catch unrelated loader errors.
/// </remarks>
public static class AppSurfacePackageCompatibility
{
    /// <summary>Gets the assembly-contract version for the coordinated logical-key release.</summary>
    public static Version ConfigurationContractVersion { get; } = new(0, 2, 0, 0);

    private static readonly HashSet<string> ContractAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForgeTrust.AppSurface.Core",
        "ForgeTrust.AppSurface.Config",
        "ForgeTrust.AppSurface.Config.LocalSecrets",
        "ForgeTrust.AppSurface.Config.GoogleSecretManager",
        "ForgeTrust.AppSurface.Config.Testing"
    };

    /// <summary>Validates loaded module metadata without enumerating or activating implementation types.</summary>
    /// <param name="assemblies">Assemblies to validate, normally the loaded application and module assemblies.</param>
    /// <exception cref="ArgumentNullException">The sequence or an assembly is null.</exception>
    /// <exception cref="AppSurfacePackageCompatibilityException">A loaded package or reference uses a different contract.</exception>
    /// <remarks>
    /// Contract assembly names are matched case-insensitively and must have assembly version 0.2.0.0. This is an
    /// assembly-contract check, not a NuGet package-version or API-content check. Dynamic assemblies are skipped because
    /// they do not provide a finalized persisted reference table. Metadata read failures propagate unchanged.
    /// </remarks>
    public static void ValidateAssemblies(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly, nameof(assemblies));
            if (assembly.IsDynamic)
            {
                continue;
            }

            ValidateReference(assembly.GetName());
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                ValidateReference(reference);
            }
        }
    }

    /// <summary>Validates an assembly identity without reading values, activating a type, or loading a dependency.</summary>
    internal static void ValidateReference(AssemblyName reference)
    {
        if (reference.Name is not null && ContractAssemblies.Contains(reference.Name)
            && reference.Version != ConfigurationContractVersion)
        {
            throw new AppSurfacePackageCompatibilityException();
        }
    }
}

/// <summary>A value-safe startup error for an incompatible coordinated package or external provider binary.</summary>
public sealed class AppSurfacePackageCompatibilityException : Exception
{
    /// <summary>Creates a deterministic error without retaining raw loader exceptions or application metadata.</summary>
    public AppSurfacePackageCompatibilityException()
        : base("Code: config-package-version-mismatch\n" +
               "Problem: Configuration package contracts do not match.\n" +
               "Cause: A package, provider, or wrapper targets an incompatible pre-1.0 contract.\n" +
               "Fix: Upgrade all AppSurface packages together and rebuild external providers and wrappers.\n" +
               "Docs: https://appsurface.dev/guides/config-key-migration\n" +
               "Retryable: false")
    {
    }

    /// <summary>Gets the stable startup diagnostic code.</summary>
    public string Code => "config-package-version-mismatch";
}
