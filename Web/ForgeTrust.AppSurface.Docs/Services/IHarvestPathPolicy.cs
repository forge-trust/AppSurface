namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Defines the repository-relative path policy used by AppSurface Docs harvesters.
/// </summary>
/// <remarks>
/// Implementations combine configured include or exclude rules with traversal pruning so harvesters can avoid
/// enumerating large excluded directory trees. All paths passed to this contract must be repository-relative and
/// normalized with forward slashes. Absolute paths, parent-directory traversal, and platform-specific separators
/// should be normalized by the caller before evaluation.
/// </remarks>
internal interface IHarvestPathPolicy
{
    /// <summary>
    /// Evaluates whether a repository-relative file path should be harvested for the specified source kind.
    /// </summary>
    /// <param name="relativePath">Repository-relative file path normalized with forward slashes.</param>
    /// <param name="sourceKind">The harvester source kind requesting the decision.</param>
    /// <returns>A decision containing the final inclusion result and diagnostic trace entries.</returns>
    AppSurfaceDocsHarvestPathDecision Evaluate(string relativePath, AppSurfaceDocsHarvestSourceKind sourceKind);

    /// <summary>
    /// Determines whether a repository-relative file path should be included for the specified source kind.
    /// </summary>
    /// <param name="relativePath">Repository-relative file path normalized with forward slashes.</param>
    /// <param name="sourceKind">The harvester source kind requesting the decision.</param>
    /// <returns><see langword="true"/> when the file should be read by the harvester; otherwise <see langword="false"/>.</returns>
    bool ShouldIncludeFilePath(string relativePath, AppSurfaceDocsHarvestSourceKind sourceKind);

    /// <summary>
    /// Determines whether a repository-relative directory can be skipped before its descendants are enumerated.
    /// </summary>
    /// <param name="relativeDirectory">Repository-relative directory path normalized with forward slashes.</param>
    /// <param name="sourceKind">The harvester source kind requesting the decision.</param>
    /// <returns><see langword="true"/> when the directory subtree is excluded and can be pruned.</returns>
    bool ShouldPruneDirectory(string relativeDirectory, AppSurfaceDocsHarvestSourceKind sourceKind);

    /// <summary>
    /// Lazily enumerates candidate files below a repository root while applying directory pruning and skipping reparse
    /// points.
    /// </summary>
    /// <param name="rootPath">Absolute repository root to traverse.</param>
    /// <param name="sourceKind">The harvester source kind requesting candidates.</param>
    /// <param name="searchPattern">A file-system search pattern such as <c>*.md</c>.</param>
    /// <param name="cancellationToken">A token observed before each directory is expanded.</param>
    /// <returns>
    /// Absolute file paths that match <paramref name="searchPattern"/>, are not below a pruned directory, and are not
    /// file-system reparse points.
    /// </returns>
    /// <remarks>
    /// File-level include checks are intentionally separate; callers must still pass returned files through
    /// <see cref="ShouldIncludeFilePath"/> after converting them back to repository-relative paths. Implementations
    /// skip reparse-point files and directories before yielding or descending so candidate traversal does not follow
    /// symlinks, junctions, or similar filesystem indirection outside the selected repository root.
    /// </remarks>
    IEnumerable<string> EnumerateCandidateFiles(
        string rootPath,
        AppSurfaceDocsHarvestSourceKind sourceKind,
        string searchPattern,
        CancellationToken cancellationToken);
}
