using System.Reflection;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Validates the coordinated configuration SPI before activating external provider or wrapper types.</summary>
public static class ConfigPackageCompatibility
{
    /// <summary>Checks assembly references without invoking provider code or enumerating implementation types.</summary>
    /// <param name="assemblies">Loaded application/plugin assemblies whose metadata can be inspected safely.</param>
    /// <exception cref="AppSurfacePackageCompatibilityException">A package or binary needs a coordinated rebuild.</exception>
    /// <remarks>Call before GetTypes, DI scans, or plugin activation. This guard performs no compatibility adaptation.</remarks>
    public static void ValidateAssemblies(IEnumerable<Assembly> assemblies) =>
        AppSurfacePackageCompatibility.ValidateAssemblies(assemblies);
}
