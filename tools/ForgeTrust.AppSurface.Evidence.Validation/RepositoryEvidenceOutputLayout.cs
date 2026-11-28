using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Names the repository proof files and their directories within a local run.</summary>
/// <remarks>Managed archives preserve this relative layout so extracted proof can be verified.</remarks>
internal static class RepositoryEvidenceOutputLayout
{
    /// <summary>The local run directory for planning output.</summary>
    internal const string PlanDirectoryName = "plan";

    /// <summary>The local run directory for execution output.</summary>
    internal const string ProofDirectoryName = "proof";

    /// <summary>The canonical plan in each planning or execution output directory.</summary>
    internal const string PlanFileName = "evidence-plan.json";

    /// <summary>The canonical manifest in the execution output directory.</summary>
    internal const string ManifestFileName = "evidence-manifest.json";

    /// <summary>The human-readable evidence summary.</summary>
    internal const string SummaryFileName = "evidence-summary.md";

    /// <summary>The resolved policy selection written alongside the plan.</summary>
    internal const string SelectionFileName = "selection.json";

    /// <summary>The directory containing the execution's producer artifacts.</summary>
    internal const string ArtifactsDirectoryName = "artifacts";

    /// <summary>The package-check producer's output directory beneath an execution proof directory.</summary>
    internal const string PackageChecksDirectoryName = "package-checks";

    /// <summary>The package inventory retained with package check diagnostics.</summary>
    internal const string PackageArtifactManifestFileName = "package-artifact-manifest.json";

    /// <summary>The package-gate command stage name.</summary>
    internal const string PackageGateStageName = "gate";

    /// <summary>The generated-documentation verification command stage name.</summary>
    internal const string PackageVerifyStageName = "verify";

    /// <summary>The package-artifact validation command stage name.</summary>
    internal const string PackageVerifyPackagesStageName = "verify-packages";

    private static readonly string[] PackageCheckStageNameArray =
    [
        PackageGateStageName,
        PackageVerifyStageName,
        PackageVerifyPackagesStageName,
    ];

    /// <summary>The closed set of package-check command stages that produce retained logs.</summary>
    internal static IReadOnlyList<string> PackageCheckStageNames { get; } = Array.AsReadOnly(PackageCheckStageNameArray);

    /// <summary>Resolves the package-check producer log path for one declared command stage.</summary>
    /// <param name="proofDirectory">The execution proof directory that contains producer output.</param>
    /// <param name="stageName">One of the declared package-check command stages.</param>
    /// <returns>The full path to the stage log beneath the proof directory.</returns>
    internal static string PackageCheckLogPath(string proofDirectory, string stageName)
    {
        ValidatePackageCheckStage(stageName);
        return PathUtils.PathUnder(proofDirectory, PackageChecksDirectoryName, stageName + ".log");
    }

    /// <summary>Builds the exact zip entry name for one declared package-check command stage log.</summary>
    /// <param name="stageName">One of the declared package-check command stages.</param>
    /// <returns>The forward-slash-separated archive entry name.</returns>
    internal static string PackageCheckLogArchiveEntryName(string stageName)
    {
        ValidatePackageCheckStage(stageName);
        return ProofDirectoryName + "/" + PackageChecksDirectoryName + "/" + stageName + ".log";
    }

    private static void ValidatePackageCheckStage(string stageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        if (!PackageCheckStageNames.Contains(stageName, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(stageName), stageName, "Unknown package-check stage.");
    }
}
