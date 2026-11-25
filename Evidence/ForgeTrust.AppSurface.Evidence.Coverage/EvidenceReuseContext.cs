using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Defines the protected contexts that cannot use local producer receipts.</summary>
internal static class EvidenceReuseContext
{
    /// <summary>Checks for a release profile or a GitHub Actions invocation.</summary>
    /// <param name="scope">The selected evidence profile scope.</param>
    /// <param name="getEnvironmentVariable">The invocation's environment reader.</param>
    /// <returns>True when a caller must reject local producer reuse.</returns>
    /// <remarks>Call only for opted-in reuse. A release profile needs no environment probe.</remarks>
    internal static bool IsProtected(EvidenceProfileScope scope, Func<string, string?> getEnvironmentVariable)
        => scope == EvidenceProfileScope.Release
            || string.Equals(getEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
}
