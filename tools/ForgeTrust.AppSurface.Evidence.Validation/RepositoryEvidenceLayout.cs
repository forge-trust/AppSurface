namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Stable result filenames and slash-normalized entries in retained local evidence archives.</summary>
internal static class RepositoryEvidenceLayout
{
    internal const string PlanDirectory = "plan";
    internal const string ProofDirectory = "proof";
    internal const string ArtifactsDirectory = "artifacts";
    internal const string PlanFile = "evidence-plan.json";
    internal const string ManifestFile = "evidence-manifest.json";
    internal const string SummaryFile = "evidence-summary.md";
    internal const string SelectionFile = "selection.json";
    internal const string ChangedPathsFile = "changed-paths.json";
    internal const string LocalPolicyFile = "local-policy.json";
    internal const string SourceStateFile = "source-state.json";
    internal const string PatchFile = "changed.patch";
    internal const string PackageBuildLog = "package-build.log";

    internal const string PlanPrefix = PlanDirectory + "/";
    internal const string ProofPrefix = ProofDirectory + "/";
    internal const string ArtifactPrefix = ProofPrefix + ArtifactsDirectory + "/";
    internal const string PlanEntry = PlanPrefix + PlanFile;
    internal const string ManifestEntry = ProofPrefix + ManifestFile;

    /// <summary>Identifies a producer's manifest-bound artifact in the retained ZIP.</summary>
    internal static string ArtifactEntry(string producerId, string relativePath) => ArtifactPrefix + producerId + "/" + relativePath;
}
