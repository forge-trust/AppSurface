using ForgeTrust.AppSurface.Core;
using static ForgeTrust.AppSurface.Evidence.Validation.PackedCandidatePackages;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Builds the source-bound package feeds consumed by the neutral and packed reuse proofs.</summary>
internal static class PackedCandidateFeed
{
    /// <summary>Selects a fresh neutral feed, a fresh complete reuse feed, or additions to a prepared neutral feed.</summary>
    internal enum Mode
    {
        Neutral,
        Reuse,
        ExtendNeutral,
    }

    /// <summary>Inputs for preparing one candidate feed with a single version and source commit.</summary>
    /// <param name="Repository">Checkout containing the projects to build.</param>
    /// <param name="Dotnet">The selected absolute SDK host.</param>
    /// <param name="Output">Caller-owned feed destination.</param>
    /// <param name="Version">Exact version shared by all candidate packages.</param>
    /// <param name="SourceCommit">Full source commit to write into every built package.</param>
    /// <param name="Selection">Selects the project set and whether the destination must already hold the neutral feed.</param>
    /// <param name="ToolArchive">Optional same-version CLI archive to copy when creating a fresh feed.</param>
    /// <param name="LogDirectory">Destination for per-project logs. Defaults to a directory under the feed.</param>
    /// <param name="NoRestore">Uses assets already restored by the calling test project.</param>
    internal sealed record Request(string Repository, string Dotnet, string Output, string Version, string SourceCommit,
        Mode Selection, string? ToolArchive = null, string? LogDirectory = null, bool NoRestore = false);

    /// <summary>A feed failure with a safe operator-facing explanation and an optional retained pack log.</summary>
    internal sealed class PreparationException(string message, string? logPath = null) : InvalidOperationException(message)
    {
        /// <summary>Gets the retained redacted pack log when the failing command produced one.</summary>
        internal string? LogPath { get; } = logPath;
    }

    /// <summary>Packs the selected projects, stopping at the first failure without overwriting existing archives.</summary>
    /// <remarks>The consumers independently validate archive provenance and the restored dependency graph.</remarks>
    internal static async Task PrepareAsync(Request request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PackedCandidateIdentity.IsVersion(request.Version))
            throw new PreparationException("A valid candidate package version is required.");
        if (!PackedCandidateIdentity.IsSourceCommit(request.SourceCommit))
            throw new PreparationException("A full source commit is required.");

        var feed = Path.GetFullPath(request.Output);
        var plan = request.Selection switch
        {
            Mode.Neutral => FreshFeed(NeutralLibraries, request.ToolArchive),
            Mode.Reuse => FreshFeed([.. NeutralLibraries, .. ReuseLibraries], request.ToolArchive),
            Mode.ExtendNeutral when request.ToolArchive is null => new FeedPlan(ReuseLibraries, [Tool, .. NeutralLibraries]),
            Mode.ExtendNeutral => throw new PreparationException("Extending a neutral feed uses its existing CLI archive."),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        ValidatePreflight(plan, feed, request.Version);

        Directory.CreateDirectory(feed);
        if (plan.ToolArchive is { } suppliedArchive)
            File.Copy(suppliedArchive, PathUtils.PathUnder(feed, Tool.ArchiveName(request.Version)));
        var logs = request.LogDirectory ?? PathUtils.PathUnder(feed, "pack-logs");
        Directory.CreateDirectory(logs);
        foreach (var package in plan.ProjectsToPack)
            await PackAsync(package, request, feed, logs, cancellationToken).ConfigureAwait(false);
    }

    private static FeedPlan FreshFeed(IReadOnlyList<Package> libraries, string? toolArchive)
        => toolArchive is null ? new([Tool, .. libraries], []) : new(libraries, [], toolArchive);

    private static void ValidatePreflight(FeedPlan plan, string feed, string version)
    {
        if (plan.RequiredNeutralArchives.Count == 0 && Path.Exists(feed))
            throw new PreparationException("Candidate feed output must be fresh.");
        foreach (var package in plan.RequiredNeutralArchives)
            if (!IsRegularArchive(PathUtils.PathUnder(feed, package.ArchiveName(version))))
                throw new PreparationException($"Missing or nonregular neutral archive for '{package.Id}'.");

        if (plan.ToolArchive is { } archive)
        {
            if (!Path.GetFileName(archive).Equals(Tool.ArchiveName(version), StringComparison.Ordinal))
                throw new PreparationException($"The supplied archive for '{Tool.Id}' must match the requested version.");
            if (!IsRegularArchive(archive))
                throw new PreparationException($"Missing or nonregular supplied archive for '{Tool.Id}'.");
        }
        foreach (var package in plan.ProjectsToPack)
            if (Path.Exists(PathUtils.PathUnder(feed, package.ArchiveName(version))))
                throw new PreparationException($"The feed already contains '{package.Id}'.");
    }

    private static async Task PackAsync(Package package, Request request, string feed, string logs, CancellationToken cancellationToken)
    {
        var stage = "pack-" + package.Id;
        var arguments = new List<string>
        {
            "pack", PathUtils.PathUnder(request.Repository, package.ProjectPath), "-c", "Release", "--output", feed,
            "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false",
            "-p:PackageVersion=" + request.Version, "-p:RepositoryCommit=" + request.SourceCommit,
            "-p:SourceRevisionId=" + request.SourceCommit,
        };
        if (request.NoRestore) arguments.Add("--no-restore");
        var start = PackedProofProcess.CreateStartInfo(request.Dotnet, arguments, request.Repository);
        var result = await PackedProofProcess.RunAsync(stage, start, PathUtils.PathUnder(logs, stage + ".log"),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new PreparationException($"Candidate stage '{stage}' exited with code {result.ExitCode}.", result.LogPath);
        if (!IsRegularArchive(PathUtils.PathUnder(feed, package.ArchiveName(request.Version))))
            throw new PreparationException($"Candidate stage '{stage}' did not produce a regular archive for '{package.Id}'.", result.LogPath);
    }

    private static bool IsRegularArchive(string path)
        => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private sealed record FeedPlan(IReadOnlyList<Package> ProjectsToPack, IReadOnlyList<Package> RequiredNeutralArchives,
        string? ToolArchive = null);
}
