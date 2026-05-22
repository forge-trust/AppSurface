using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed class AppSurfaceDocsHarvestPathPolicySnapshot : IHarvestPathPolicy
{
    private readonly AppSurfaceDocsHarvestPathPolicy _configuredPolicy;
    private readonly AppSurfaceDocsHarvestVcsIgnorePolicy _vcsIgnorePolicy;

    public AppSurfaceDocsHarvestPathPolicySnapshot(
        AppSurfaceDocsHarvestPathPolicy configuredPolicy,
        AppSurfaceDocsHarvestVcsIgnorePolicy vcsIgnorePolicy)
    {
        _configuredPolicy = configuredPolicy ?? throw new ArgumentNullException(nameof(configuredPolicy));
        _vcsIgnorePolicy = vcsIgnorePolicy ?? throw new ArgumentNullException(nameof(vcsIgnorePolicy));
    }

    public AppSurfaceDocsHarvestPathDecision Evaluate(
        string relativePath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return _configuredPolicy.Evaluate(relativePath, sourceKind, _vcsIgnorePolicy.EvaluateFile);
    }

    public bool ShouldIncludeFilePath(
        string relativePath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return Evaluate(relativePath, sourceKind).Included;
    }

    public bool ShouldPruneDirectory(
        string relativeDirectory,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return _configuredPolicy.ShouldPruneDirectory(relativeDirectory, sourceKind)
               || _vcsIgnorePolicy.ShouldPruneDirectory(relativeDirectory, sourceKind);
    }

    public IEnumerable<string> EnumerateCandidateFiles(
        string rootPath,
        AppSurfaceDocsHarvestSourceKind sourceKind,
        string searchPattern,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(searchPattern);

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDirectory = pendingDirectories.Pop();
            foreach (var file in Directory.EnumerateFiles(currentDirectory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                var attributes = File.GetAttributes(directory);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var relativeDirectory = Path.GetRelativePath(rootPath, directory).Replace('\\', '/');
                if (ShouldPruneDirectory(relativeDirectory, sourceKind))
                {
                    continue;
                }

                pendingDirectories.Push(directory);
            }
        }
    }

    public AppSurfaceDocsHarvestVcsIgnoreDiagnostics GetVcsIgnoreDiagnostics()
    {
        return _vcsIgnorePolicy.GetDiagnostics();
    }

    public IReadOnlyList<DocHarvestDiagnostic> CreateVcsIgnoreHealthDiagnostics()
    {
        return _vcsIgnorePolicy.CreateHealthDiagnostics();
    }
}

internal sealed class AppSurfaceDocsHarvestPathPolicySnapshotFactory
{
    private readonly AppSurfaceDocsOptions _options;
    private readonly ILogger _logger;

    public AppSurfaceDocsHarvestPathPolicySnapshotFactory(
        AppSurfaceDocsOptions options,
        ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AppSurfaceDocsHarvestPathPolicySnapshot Create(string repositoryRoot)
    {
        var configuredPolicy = new AppSurfaceDocsHarvestPathPolicy(_options, Microsoft.Extensions.Logging.Abstractions.NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance);
        var vcsIgnoreOptions = _options.Harvest?.Paths?.VcsIgnore ?? new AppSurfaceDocsHarvestVcsIgnoreOptions();
        var vcsIgnorePolicy = new AppSurfaceDocsHarvestVcsIgnorePolicy(repositoryRoot, vcsIgnoreOptions, _logger);
        return new AppSurfaceDocsHarvestPathPolicySnapshot(configuredPolicy, vcsIgnorePolicy);
    }
}

internal sealed record DocHarvestContext(
    string RepositoryRoot,
    AppSurfaceDocsHarvestPathPolicySnapshot PathPolicy);
