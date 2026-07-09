namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

internal static class AppSurfaceKeycloakRealmImportPaths
{
    private const string ImportRootDirectoryName = "appsurface-keycloak-realms";

    public static string CreateDefaultDirectory() =>
        CreateDirectory(AppContext.BaseDirectory, AppSurfaceKeycloakDefaults.ResourceName);

    public static string CreateDirectory(string rootDirectory, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory cannot be blank.", nameof(rootDirectory));
        }

        return Path.Join(rootDirectory, ImportRootDirectoryName, GetFileNameSegment(resourceName, nameof(resourceName)));
    }

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
