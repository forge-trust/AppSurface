namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>
/// Resolves the generated Keycloak realm import file path without an Aspire dependency.
/// </summary>
internal static class RealmImportFilePathResolver
{
    /// <summary>
    /// Resolves the generated realm import file beneath the supplied directory.
    /// </summary>
    /// <param name="realmImportDirectory">Directory that contains realm imports.</param>
    /// <param name="realm">Realm id used in the generated file name.</param>
    /// <returns>The realm import file path, relative when the supplied directory is relative.</returns>
    internal static string Resolve(string realmImportDirectory, string realm)
    {
        if (string.IsNullOrWhiteSpace(realmImportDirectory))
        {
            throw new ArgumentException("Realm import directory cannot be blank.", nameof(realmImportDirectory));
        }

        // Keep the caller's relative directory and native filename semantics.
        return Path.Join(realmImportDirectory, GetFileNameSegment($"{realm}-realm.json", nameof(realm)));
    }

    /// <summary>Extracts the final native path segment and checks that it can be appended as a single name.</summary>
    internal static string GetFileNameSegment(string value, string parameterName)
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
