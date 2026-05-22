namespace ForgeTrust.AppSurface.Docs.Services;

internal interface IHarvestPathPolicy
{
    AppSurfaceDocsHarvestPathDecision Evaluate(string relativePath, AppSurfaceDocsHarvestSourceKind sourceKind);

    bool ShouldIncludeFilePath(string relativePath, AppSurfaceDocsHarvestSourceKind sourceKind);

    bool ShouldPruneDirectory(string relativeDirectory, AppSurfaceDocsHarvestSourceKind sourceKind);

    IEnumerable<string> EnumerateCandidateFiles(
        string rootPath,
        AppSurfaceDocsHarvestSourceKind sourceKind,
        string searchPattern,
        CancellationToken cancellationToken);
}
