namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Resolves safe local paths for generated Keycloak realm import artifacts.
/// </summary>
internal static class AppSurfaceKeycloakRealmImportPaths
{
    private const string ImportRootDirectoryName = "appsurface-keycloak-realms";

    /// <summary>
    /// Gets the default realm import directory without creating it.
    /// </summary>
    /// <returns>The default directory beneath the application base directory.</returns>
    public static string GetDefaultImportDirectory() =>
        ResolveImportDirectory(AppContext.BaseDirectory, AppSurfaceKeycloakDefaults.ResourceName);

    /// <summary>
    /// Resolves a resource-specific realm import directory without creating it.
    /// </summary>
    /// <param name="rootDirectory">Root directory that owns generated realm imports.</param>
    /// <param name="resourceName">Resource name used as one safe path segment.</param>
    /// <returns>The resolved import directory.</returns>
    public static string ResolveImportDirectory(string rootDirectory, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory cannot be blank.", nameof(rootDirectory));
        }

        return Path.Join(rootDirectory, ImportRootDirectoryName, GetFileNameSegment(resourceName, nameof(resourceName)));
    }

    /// <summary>
    /// Resolves the generated realm import file beneath an import directory.
    /// </summary>
    /// <param name="realmImportDirectory">Directory that contains realm imports.</param>
    /// <param name="realm">Realm id used in the generated file name.</param>
    /// <returns>The safe realm import file path.</returns>
    public static string GetRealmImportFilePath(string realmImportDirectory, string realm)
    {
        if (string.IsNullOrWhiteSpace(realmImportDirectory))
        {
            throw new ArgumentException("Realm import directory cannot be blank.", nameof(realmImportDirectory));
        }

        return Path.Join(realmImportDirectory, GetFileNameSegment($"{realm}-realm.json", nameof(realm)));
    }

    private static string GetFileNameSegment(string value, string parameterName)
    {
        var fileName = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(fileName)
            || string.Equals(fileName, ".", StringComparison.Ordinal)
            || string.Equals(fileName, "..", StringComparison.Ordinal)
            || Path.IsPathRooted(fileName)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Path segment must resolve to a relative file name.", parameterName);
        }

        return fileName;
    }
}
