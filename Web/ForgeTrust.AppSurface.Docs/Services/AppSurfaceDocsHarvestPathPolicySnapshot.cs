using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Captures the configured harvest path policy and repository VCS ignore policy used for one harvest snapshot.
/// </summary>
/// <remarks>
/// The snapshot keeps VCS ignore parsing stable for a single aggregation pass and exposes only normalized,
/// repository-relative decisions to harvesters through <see cref="IHarvestPathPolicy"/>. The configured policy is
/// evaluated first, and VCS ignore decisions are applied as an additional exclusion layer with allow-glob restoration
/// for intentionally public ignored content.
/// </remarks>
internal sealed class AppSurfaceDocsHarvestPathPolicySnapshot : IHarvestPathPolicy
{
    private readonly AppSurfaceDocsHarvestPathPolicy _configuredPolicy;
    private readonly AppSurfaceDocsHarvestVcsIgnorePolicy _vcsIgnorePolicy;

    /// <summary>
    /// Initializes a new instance of <see cref="AppSurfaceDocsHarvestPathPolicySnapshot"/>.
    /// </summary>
    /// <param name="configuredPolicy">The configured AppSurface Docs path policy.</param>
    /// <param name="vcsIgnorePolicy">The repository VCS ignore policy loaded for the harvest root.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configuredPolicy"/> or <paramref name="vcsIgnorePolicy"/> is <see langword="null"/>.
    /// </exception>
    public AppSurfaceDocsHarvestPathPolicySnapshot(
        AppSurfaceDocsHarvestPathPolicy configuredPolicy,
        AppSurfaceDocsHarvestVcsIgnorePolicy vcsIgnorePolicy)
    {
        _configuredPolicy = configuredPolicy ?? throw new ArgumentNullException(nameof(configuredPolicy));
        _vcsIgnorePolicy = vcsIgnorePolicy ?? throw new ArgumentNullException(nameof(vcsIgnorePolicy));
    }

    /// <inheritdoc />
    public AppSurfaceDocsHarvestPathDecision Evaluate(
        string relativePath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return _configuredPolicy.Evaluate(relativePath, sourceKind, _vcsIgnorePolicy.EvaluateFile);
    }

    /// <inheritdoc />
    public bool ShouldIncludeFilePath(
        string relativePath,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return Evaluate(relativePath, sourceKind).Included;
    }

    /// <inheritdoc />
    public bool ShouldPruneDirectory(
        string relativeDirectory,
        AppSurfaceDocsHarvestSourceKind sourceKind)
    {
        return _configuredPolicy.ShouldPruneDirectory(relativeDirectory, sourceKind)
               || _vcsIgnorePolicy.ShouldPruneDirectory(relativeDirectory, sourceKind);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Traversal is lazy and depth-first using an explicit stack. File paths are yielded as absolute paths, directory
    /// paths are normalized to forward-slash repository-relative values before pruning, and directory reparse points
    /// are skipped to avoid following symlinks or junctions outside the repository. Cancellation is checked before each
    /// directory expansion, so callers can stop large repository walks without waiting for every descendant to be listed.
    /// The method throws <see cref="ArgumentNullException"/> for a <see langword="null"/> <paramref name="rootPath"/> or
    /// <paramref name="searchPattern"/>; file-system enumeration exceptions are allowed to flow to the harvester so the
    /// aggregation layer can report the failure consistently.
    /// </remarks>
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

    /// <summary>
    /// Gets repository VCS ignore loading diagnostics for this harvest snapshot.
    /// </summary>
    /// <returns>VCS ignore status, loaded ignore files, and warning information.</returns>
    public AppSurfaceDocsHarvestVcsIgnoreDiagnostics GetVcsIgnoreDiagnostics()
    {
        return _vcsIgnorePolicy.GetDiagnostics();
    }

    /// <summary>
    /// Creates health diagnostics that summarize VCS ignore behavior for the snapshot.
    /// </summary>
    /// <returns>Diagnostics safe for harvest health reporting. Sample paths are redacted before client exposure.</returns>
    public IReadOnlyList<DocHarvestDiagnostic> CreateVcsIgnoreHealthDiagnostics()
    {
        return _vcsIgnorePolicy.CreateHealthDiagnostics();
    }
}

/// <summary>
/// Creates per-harvest path policy snapshots for a repository root.
/// </summary>
/// <remarks>
/// A new snapshot should be created for each aggregation pass so changes to repository ignore files are observed at the
/// beginning of the next harvest while remaining stable for all harvesters in the current pass.
/// </remarks>
internal sealed class AppSurfaceDocsHarvestPathPolicySnapshotFactory
{
    private readonly AppSurfaceDocsOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AppSurfaceDocsHarvestPathPolicySnapshotFactory"/>.
    /// </summary>
    /// <param name="options">The AppSurface Docs options that define configured harvest path rules.</param>
    /// <param name="logger">The logger used by repository VCS ignore policy loading.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public AppSurfaceDocsHarvestPathPolicySnapshotFactory(
        AppSurfaceDocsOptions options,
        ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a path policy snapshot for the supplied repository root.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root whose VCS ignore files should be read.</param>
    /// <returns>A snapshot combining configured path rules with repository VCS ignore rules.</returns>
    public AppSurfaceDocsHarvestPathPolicySnapshot Create(string repositoryRoot)
    {
        var configuredPolicy = new AppSurfaceDocsHarvestPathPolicy(_options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance);
        var vcsIgnoreOptions = _options.Harvest?.Paths?.VcsIgnore ?? new AppSurfaceDocsHarvestVcsIgnoreOptions();
        var vcsIgnorePolicy = new AppSurfaceDocsHarvestVcsIgnorePolicy(repositoryRoot, vcsIgnoreOptions, _logger);
        return new AppSurfaceDocsHarvestPathPolicySnapshot(configuredPolicy, vcsIgnorePolicy);
    }
}

/// <summary>
/// Carries repository-scoped harvest dependencies shared by built-in harvesters during one aggregation pass.
/// </summary>
/// <param name="RepositoryRoot">The absolute repository root that harvesters should scan.</param>
/// <param name="PathPolicy">The path policy snapshot exposed through the harvester path-policy contract.</param>
/// <remarks>
/// The context keeps harvesters on a single policy instance for consistent VCS ignore decisions. Consumers should treat
/// <paramref name="PathPolicy"/> as the authority for traversal and file inclusion, and should not cache the context
/// beyond the aggregation pass that created it.
/// </remarks>
internal sealed record DocHarvestContext(
    string RepositoryRoot,
    IHarvestPathPolicy PathPolicy);
