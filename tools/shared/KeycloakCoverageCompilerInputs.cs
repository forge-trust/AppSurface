using ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Names the bounded compiler outputs used by Keycloak coverage fan-in.</summary>
internal static class KeycloakCoverageCompilerInputs
{
    /// <summary>Returns the three fixed Keycloak assemblies expected in the adapter test output directory.</summary>
    internal static string[] AssemblyPaths(string directory) =>
    [
        EvidenceArtifactValidation.GetContainedPath(directory, "ForgeTrust.AppSurface.Auth.Keycloak.dll"),
        EvidenceArtifactValidation.GetContainedPath(directory, "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.dll"),
        EvidenceArtifactValidation.GetContainedPath(directory, "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker.dll"),
    ];

    /// <summary>Returns the adapter test project's output directory for the requested build configuration.</summary>
    internal static string TestOutput(string root, string configuration)
    {
        if (configuration is not ("Debug" or "Release")) throw new ArgumentException("Invalid compiler configuration.", nameof(configuration));
        return EvidenceArtifactValidation.GetContainedPath(root,
            $"Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests/bin/{configuration}/net10.0");
    }
}
