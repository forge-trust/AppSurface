namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Explicit execution inputs for solution coverage and package validation selected by repository policy.</summary>
/// <param name="RepositoryRoot">Checkout whose source and solution are being verified.</param>
/// <param name="DotnetPath">Absolute .NET host used by every child command.</param>
/// <param name="OutputDirectory">Fresh producer-owned output below the caller's evidence run.</param>
/// <param name="DiffBase">Pinned comparison commit for coverage; null means aggregate-only baseline.</param>
/// <param name="NativeCoverageDirectory">Optional same-run native capture parent for cross-platform patch fan-in.</param>
/// <param name="DiffFile">Immutable local patch snapshot including working-tree and untracked edits, when present.</param>
/// <param name="CoverageCliPath">Optional absolute installed CLI executable for collection and coverage gates; required with native fan-in.</param>
internal sealed record RepositoryCheckSettings(string RepositoryRoot, string DotnetPath, string OutputDirectory,
    string? DiffBase = null, string? NativeCoverageDirectory = null, string? DiffFile = null, string? CoverageCliPath = null)
{
    /// <summary>Native fan-in contract to require for both captures and acceptance. Defaults to v2 for explicit compatibility callers.</summary>
    internal int NativeCoverageContractVersion { get; init; } = 2;

    /// <summary>Optional stable, private local package-gate restore cache. Consumer proof caches are independent.</summary>
    internal string? PackageRestoreCache { get; init; }

    /// <summary>Optional same-run CI collection to verify and gate instead of collecting again. Never used for local patches.</summary>
    internal string? CoverageCollectionDirectory { get; init; }

    /// <summary>Optional prior timings used only to order non-exclusive projects in the longest-first schedule.</summary>
    internal string? CoverageScheduleTimingsPath { get; init; }

    /// <summary>
    /// Verifies the local source and policy digest captured by <see cref="LocalEvidenceWorkflow"/>.
    /// Required for a dirty local patch; direct command-line runs cannot supply this in-process callback.
    /// </summary>
    internal Func<CancellationToken, Task>? VerifyLocalSourceAsync { get; init; }

    /// <summary>Keeps nested SDK commands on the selected host even when child tooling invokes dotnet by name.</summary>
    internal IReadOnlyDictionary<string, string> CommandEnvironment => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_HOST_PATH"] = DotnetPath,
        ["PATH"] = Path.GetDirectoryName(DotnetPath) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
    };
}
