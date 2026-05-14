namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Resolves the checked-in package manifest into the exact package set that should be packed.
/// </summary>
internal sealed class PackagePublishPlanResolver
{
    private readonly PackageProjectScanner _scanner;
    private readonly IProjectMetadataProvider _metadataProvider;
    private readonly PackageManifestLoader _manifestLoader;

    /// <summary>
    /// Creates a resolver that reads the chooser manifest and evaluates project metadata.
    /// </summary>
    /// <param name="scanner">Project scanner used to discover candidate package projects.</param>
    /// <param name="metadataProvider">Metadata provider used to evaluate discovered projects.</param>
    /// <param name="manifestLoader">Manifest loader used to parse the package manifest.</param>
    internal PackagePublishPlanResolver(
        PackageProjectScanner scanner,
        IProjectMetadataProvider metadataProvider,
        PackageManifestLoader manifestLoader)
    {
        _scanner = scanner;
        _metadataProvider = metadataProvider;
        _manifestLoader = manifestLoader;
    }

    /// <summary>
    /// Resolves and validates the package publish plan.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root.</param>
    /// <param name="manifestPath">Absolute path to the package manifest.</param>
    /// <param name="cancellationToken">Cancellation token used while loading metadata.</param>
    /// <returns>The ordered package publish plan.</returns>
    internal async Task<PackagePublishPlan> ResolveAsync(
        string repositoryRoot,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repositoryRoot))
        {
            throw new PackageIndexException($"Repository root '{repositoryRoot}' does not exist.");
        }

        if (!File.Exists(manifestPath))
        {
            throw new PackageIndexException($"Manifest '{Path.GetRelativePath(repositoryRoot, manifestPath)}' does not exist.");
        }

        var manifest = await _manifestLoader.LoadAsync(manifestPath, cancellationToken);
        var candidateProjects = _scanner.DiscoverProjects(repositoryRoot);
        var metadataByProject = await LoadMetadataAsync(repositoryRoot, candidateProjects, cancellationToken);
        var resolvedEntries = PackageIndexGenerator.ResolveEntries(
            repositoryRoot,
            manifest,
            candidateProjects,
            metadataByProject);

        ValidatePublishFields(resolvedEntries);
        ValidateProjectReferenceDependencies(repositoryRoot, resolvedEntries, metadataByProject);

        return new PackagePublishPlan(
            resolvedEntries
                .Where(entry => entry.Manifest.PublishDecision is PackagePublishDecision.Publish or PackagePublishDecision.SupportPublish)
                .OrderBy(entry => entry.Manifest.Order)
                .Select(entry => new PackagePublishPlanEntry(
                    entry.Manifest.Project,
                    entry.Metadata.PackageId,
                    entry.Manifest.PublishDecision!.Value,
                    entry.Manifest.ExpectedDependencyPackageIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    entry.Metadata.IsTool))
                .ToArray());
    }

    private async Task<IReadOnlyDictionary<string, PackageProjectMetadata>> LoadMetadataAsync(
        string repositoryRoot,
        IReadOnlyList<string> candidateProjects,
        CancellationToken cancellationToken)
    {
        var metadataByPath = new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in candidateProjects)
        {
            var metadata = await _metadataProvider.GetMetadataAsync(repositoryRoot, projectPath, cancellationToken);
            metadataByPath.Add(projectPath, metadata);
        }

        return metadataByPath;
    }

    private static void ValidatePublishFields(IReadOnlyList<ResolvedPackageEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Manifest.Classification == PackageClassification.Public
                && entry.Manifest.PublishDecision != PackagePublishDecision.Publish)
            {
                throw new PackageIndexException($"Public manifest entry '{entry.Manifest.Project}' must use publish_decision 'publish'.");
            }

            if (entry.Manifest.Classification == PackageClassification.Support
                && entry.Manifest.PublishDecision == PackagePublishDecision.Publish)
            {
                throw new PackageIndexException($"Support manifest entry '{entry.Manifest.Project}' must use publish_decision 'support_publish' or 'do_not_publish'.");
            }

            if (entry.Manifest.Classification == PackageClassification.ProofHost
                && entry.Manifest.PublishDecision == PackagePublishDecision.Publish)
            {
                throw new PackageIndexException($"Proof-host manifest entry '{entry.Manifest.Project}' must use publish_decision 'support_publish' or 'do_not_publish'.");
            }

            if (entry.Manifest.Classification == PackageClassification.Excluded
                && entry.Manifest.PublishDecision != PackagePublishDecision.DoNotPublish)
            {
                throw new PackageIndexException($"Excluded manifest entry '{entry.Manifest.Project}' must use publish_decision 'do_not_publish'.");
            }

        }
    }

    private static void ValidateProjectReferenceDependencies(
        string repositoryRoot,
        IReadOnlyList<ResolvedPackageEntry> entries,
        IReadOnlyDictionary<string, PackageProjectMetadata> metadataByProject)
    {
        var metadataByProjectPath = metadataByProject.ToDictionary(
            pair => NormalizeRepositoryPath(pair.Key),
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries.Where(entry => entry.Manifest.PublishDecision is PackagePublishDecision.Publish or PackagePublishDecision.SupportPublish))
        {
            if (entry.Metadata.IsTool)
            {
                if (entry.Manifest.ExpectedDependencyPackageIds.Count > 0)
                {
                    throw new PackageIndexException(
                        $"Tool manifest entry '{entry.Manifest.Project}' must not define expected package dependencies because .NET tool packages embed their project references.");
                }

                continue;
            }

            var actualPackageIds = entry.Metadata.ProjectReferences
                .Select(reference => NormalizeProjectReferencePath(repositoryRoot, reference))
                .Where(referencePath => metadataByProjectPath.ContainsKey(referencePath))
                .Select(referencePath => metadataByProjectPath[referencePath].PackageId)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var expectedPackageIds = entry.Manifest.ExpectedDependencyPackageIds
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!actualPackageIds.SequenceEqual(expectedPackageIds, StringComparer.OrdinalIgnoreCase))
            {
                throw new PackageIndexException(
                    $"Manifest entry '{entry.Manifest.Project}' expected dependency package ids [{string.Join(", ", expectedPackageIds)}], but project references resolve to [{string.Join(", ", actualPackageIds)}].");
            }
        }
    }

    private static string NormalizeProjectReferencePath(string repositoryRoot, string referencePath)
    {
        var path = Path.IsPathRooted(referencePath)
            ? Path.GetRelativePath(repositoryRoot, referencePath)
            : referencePath;
        return NormalizeRepositoryPath(path);
    }

    private static string NormalizeRepositoryPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}

/// <summary>
/// Ordered package set selected for artifact packing and validation.
/// </summary>
/// <param name="Entries">Packages that should be packed, in deterministic manifest order.</param>
internal sealed record PackagePublishPlan(IReadOnlyList<PackagePublishPlanEntry> Entries);

/// <summary>
/// One package selected for pack artifact production.
/// </summary>
/// <param name="ProjectPath">Repository-relative project path to pack.</param>
/// <param name="PackageId">Expected NuGet package id.</param>
/// <param name="Decision">Publish decision from the manifest.</param>
/// <param name="ExpectedDependencyPackageIds">Expected same-version package dependencies.</param>
/// <param name="IsTool">Whether the package is a .NET tool package.</param>
internal sealed record PackagePublishPlanEntry(
    string ProjectPath,
    string PackageId,
    PackagePublishDecision Decision,
    IReadOnlyList<string> ExpectedDependencyPackageIds,
    bool IsTool);
