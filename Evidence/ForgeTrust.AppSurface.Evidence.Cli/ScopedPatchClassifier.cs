using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>Why a scoped patch coverage claim passed, failed or could not be evaluated.</summary>
/// <remarks>
/// Each reason maps to one producer outcome: <see cref="Complete"/> to <c>Passed</c>; <see cref="BelowThreshold"/>,
/// <see cref="ScopeAssemblyNotInstrumented"/>, <see cref="NothingInScope"/> and <see cref="RunFailed"/> to
/// <c>Failed</c>, as does <see cref="AssemblyMismatch"/>; <see cref="SolutionUnavailable"/> and <see cref="DiffUnavailable"/> to <c>Unavailable</c>; and
/// <see cref="DeadlineExceeded"/> to <c>TimedOut</c>.
/// </remarks>
internal enum ScopedCoverageReason
{
    /// <summary>Every declared assembly was reported, something changed in scope, and each declared metric met its threshold or had nothing to measure.</summary>
    Complete,

    /// <summary>A declared patch metric with at least one measurable point was under its threshold after tolerance.</summary>
    BelowThreshold,

    /// <summary>A declared assembly was missing from the merged report, so the declared projects did not load it.</summary>
    ScopeAssemblyNotInstrumented,

    /// <summary>A changed in-scope file had rows only under a package other than its declared assembly, so the declared assembly name is wrong for it.</summary>
    AssemblyMismatch,

    /// <summary>No changed file lay under a source pattern or inside a declared test project directory.</summary>
    NothingInScope,

    /// <summary>A declared test, the merge, the report step or the classification failed.</summary>
    RunFailed,

    /// <summary>No <c>--solution</c> was supplied.</summary>
    SolutionUnavailable,

    /// <summary>No diff snapshot was supplied. A diff that cannot be parsed fails the run with <see cref="RunFailed"/>.</summary>
    DiffUnavailable,

    /// <summary>The producer's <c>timeoutSeconds</c> expired before the claim was evaluated.</summary>
    DeadlineExceeded,
}

/// <summary>The receipt form of one patch metric, in the shape of the coverage contract's patch metric result.</summary>
/// <param name="Outcome"><c>measured</c>, <c>notApplicable</c> or <c>unverified</c>.</param>
/// <param name="Reason"><c>reported-points</c>, <c>no-reported-patch-points</c> or <c>no-report-classified</c>.</param>
/// <param name="ChangedLines">In-scope added lines; <see langword="null"/> when unverified.</param>
/// <param name="Measurable">Measurable lines or branch conditions; <see langword="null"/> when unverified.</param>
/// <param name="Covered">Covered lines or branch conditions; <see langword="null"/> when unverified.</param>
/// <param name="Percent">Percentage rounded to four places; <see langword="null"/> unless measured, never 100 for nothing measured.</param>
internal sealed record ScopedPatchMetric(string Outcome, string Reason, int? ChangedLines, int? Measurable, int? Covered, decimal? Percent)
{
    /// <summary>The metric when no report was classified.</summary>
    public static ScopedPatchMetric Unverified { get; } = new("unverified", "no-report-classified", null, null, null, null);

    /// <summary>Gets whether this metric has at least one measured point.</summary>
    public bool IsMeasured => string.Equals(Outcome, "measured", StringComparison.Ordinal);

    internal static ScopedPatchMetric From(int changedLines, int measurable, int covered) => measurable == 0
        ? new("notApplicable", "no-reported-patch-points", changedLines, 0, 0, null)
        : new("measured", "reported-points", changedLines, measurable, covered,
            Math.Round(covered * 100m / measurable, 4, MidpointRounding.AwayFromZero));
}

/// <summary>One declared source entry and whether the merged report listed its assembly.</summary>
/// <param name="Pattern">Declared source pattern.</param>
/// <param name="Assembly">Declared assembly name.</param>
/// <param name="AssemblyReported">Whether the report listed the assembly; <see langword="null"/> when no report was read.</param>
internal sealed record ScopedSourceStatus(string Pattern, string Assembly, bool? AssemblyReported);

/// <summary>The result of classifying a diff against a scope and a merged report.</summary>
/// <param name="Reason">The verdict.</param>
/// <param name="Line">Changed-line metric.</param>
/// <param name="Branch">Changed-branch metric.</param>
/// <param name="Sources">Each declared source with its assembly status, in declaration order.</param>
/// <param name="InScopeFiles">Changed files under a source pattern or a declared test project directory; <see langword="null"/> when not classified.</param>
/// <param name="OutOfScopeFiles">Other changed files; <see langword="null"/> when not classified.</param>
/// <param name="UnknownFiles">Up to 50 in-scope changed paths whose assembly was not reported, or whose rows are only under another package, each cut to 512 characters.</param>
/// <param name="UnknownFilesTruncated">Whether a path was cut or more files than the list holds were unknown.</param>
internal sealed record ScopedPatchClassification(
    ScopedCoverageReason Reason,
    ScopedPatchMetric Line,
    ScopedPatchMetric Branch,
    IReadOnlyList<ScopedSourceStatus> Sources,
    int? InScopeFiles,
    int? OutOfScopeFiles,
    IReadOnlyList<string> UnknownFiles,
    bool UnknownFilesTruncated)
{
    /// <summary>Creates the result for a run in which no report was classified.</summary>
    /// <param name="scope">The declared scope.</param>
    /// <param name="reason">A reason other than the four classifier verdicts.</param>
    /// <returns>Unverified metrics, unknown assembly status, no counts and no files.</returns>
    public static ScopedPatchClassification Unclassified(EvidenceCoverageScope scope, ScopedCoverageReason reason)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(reason, ScopedPatchMetric.Unverified, ScopedPatchMetric.Unverified,
            [.. scope.Sources.Select(static source => new ScopedSourceStatus(source.Pattern, source.Assembly, null))],
            null, null, [], false);
    }
}

/// <summary>
/// Scores the changed lines of a diff against a declared source scope and a merged Cobertura report read by package.
/// </summary>
/// <remarks>
/// <para>
/// This is a pure function: no process, file or clock access, so every class and verdict is tested from its inputs.
/// </para>
/// <para>Each added line under a source pattern gets one class, scored only from the source entry's declared assembly:</para>
/// <list type="bullet">
/// <item><description>Covered or uncovered: the assembly has a row for the file and line; the line mode decides partial branches.</description></item>
/// <item><description>Not applicable: no row, and the assembly is reported. True for a line with no sequence point, and also, as a stated gap, for a file excluded from coverage.</description></item>
/// <item><description>Mismatched: the declared assembly has no row for the file at all, but another package does. The declared assembly name is wrong for that file, so the run fails instead of calling its lines not applicable.</description></item>
/// <item><description>Unknown: no row, and the assembly is not reported. Unknown lines never reach scoring, because a missing assembly fails first.</description></item>
/// </list>
/// <para>Verdict, first that applies:</para>
/// <code>
/// 1. a declared assembly is not in the report            -> ScopeAssemblyNotInstrumented
/// 2. a changed in-scope file has rows only under another
///    package                                             -> AssemblyMismatch
/// 3. no changed file is under a source pattern or inside
///    a declared test project directory                   -> NothingInScope
/// 4. a declared metric with measurable points is under
///    its threshold after tolerance                       -> BelowThreshold
/// 5. otherwise                                           -> Complete
/// </code>
/// <para>
/// "Changed file" means a file record in the patch, its new and previous path both considered, so deletion-only and
/// rename-only changes count. Added lines are keyed by their new path.
/// </para>
/// </remarks>
internal static class ScopedPatchClassifier
{
    /// <summary>Classifies one diff.</summary>
    /// <param name="scope">The declared scope, validated by the planner.</param>
    /// <param name="changedFiles">File records from the patch, including deletions and renames.</param>
    /// <param name="changedLines">Added line numbers keyed by new repository-relative path.</param>
    /// <param name="report">The merged report read by package.</param>
    /// <returns>The verdict, both metrics, per-source assembly status, file counts and unknown files.</returns>
    public static ScopedPatchClassification Classify(
        EvidenceCoverageScope scope,
        IReadOnlyList<NormalizedDiffPath> changedFiles,
        IReadOnlyDictionary<string, IReadOnlySet<int>> changedLines,
        CoberturaPackageCoverage report)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(changedLines);
        ArgumentNullException.ThrowIfNull(report);

        var sources = scope.Sources
            .Select(source => new ScopedSourceStatus(source.Pattern, source.Assembly, report.ReportedPackages.Contains(source.Assembly)))
            .ToArray();
        var testDirectories = scope.Projects
            .Select(static project => project.LastIndexOf('/') is var slash and > 0 ? project[..(slash + 1)] : string.Empty)
            .ToArray();

        var inScopeFiles = 0;
        var outOfScopeFiles = 0;
        var unknownFiles = new List<string>();
        var unknownTruncated = false;
        void AddUnknown(string path)
        {
            if (unknownFiles.Count < EvidenceScopedCoverageContract.MaximumUnknownFiles)
            {
                var (value, cut) = Cut(path);
                unknownFiles.Add(value);
                unknownTruncated |= cut;
            }
            else
            {
                unknownTruncated = true;
            }
        }

        foreach (var file in changedFiles)
        {
            var paths = file.PreviousPath is null ? new[] { file.Path } : [file.Path, file.PreviousPath];
            var source = paths.Select(path => FindSource(sources, path)).FirstOrDefault(static match => match is not null);
            if (source is null && !paths.Any(path => testDirectories.Any(directory => IsUnder(path, directory))))
            {
                outOfScopeFiles++;
                continue;
            }

            inScopeFiles++;
            if (source is { AssemblyReported: false })
            {
                AddUnknown(file.Path);
            }
        }

        var lineMode = string.Equals(scope.PatchLineMode, "codecov", StringComparison.OrdinalIgnoreCase)
            ? PatchLineMode.Codecov
            : PatchLineMode.Measurable;
        int changed = 0, measurableLines = 0, coveredLines = 0, measurableBranches = 0, coveredBranches = 0;
        var mismatched = false;
        foreach (var (path, lines) in changedLines.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (FindSource(sources, path) is not { } source)
            {
                continue;
            }

            // Lines whose assembly is missing are counted as changed and scored by no metric; the verdict fails first.
            changed += lines.Count;
            if (source.AssemblyReported != true)
            {
                continue;
            }

            if (!report.Rows.TryGetValue(source.Assembly, out var files) || !files.TryGetValue(path, out var rows))
            {
                // No row from the declared assembly. Rows under another package mean the declaration names the wrong
                // assembly for this file, so its lines cannot be called not applicable.
                if (report.Rows.Any(package => package.Key != source.Assembly && package.Value.ContainsKey(path)))
                {
                    mismatched = true;
                    AddUnknown(path);
                }

                continue;
            }

            foreach (var line in lines)
            {
                if (!rows.TryGetValue(line, out var row))
                {
                    continue;
                }

                measurableLines++;
                if (IsCovered(row, lineMode))
                {
                    coveredLines++;
                }

                if (row.ValidConditions is { } valid)
                {
                    measurableBranches += valid;
                    coveredBranches += row.CoveredConditions ?? 0;
                }
            }
        }

        var lineMetric = ScopedPatchMetric.From(changed, measurableLines, coveredLines);
        var branchMetric = ScopedPatchMetric.From(changed, measurableBranches, coveredBranches);

        var reason = sources.Any(static source => source.AssemblyReported == false) ? ScopedCoverageReason.ScopeAssemblyNotInstrumented
            : mismatched ? ScopedCoverageReason.AssemblyMismatch
            : inScopeFiles == 0 ? ScopedCoverageReason.NothingInScope
            : IsBelow(lineMetric, scope.MinPatchLinePercent, scope.TolerancePercent)
                || IsBelow(branchMetric, scope.MinPatchBranchPercent, scope.TolerancePercent) ? ScopedCoverageReason.BelowThreshold
            : ScopedCoverageReason.Complete;
        return new(reason, lineMetric, branchMetric, sources, inScopeFiles, outOfScopeFiles, unknownFiles, unknownTruncated);
    }

    /// <summary>Returns the first source whose pattern matches the path; the planner rejects overlapping sources with different assemblies.</summary>
    private static ScopedSourceStatus? FindSource(IReadOnlyList<ScopedSourceStatus> sources, string path) =>
        sources.FirstOrDefault(source => EvidenceProducerInputPattern.MatchesPath(source.Pattern, path, CancellationToken.None));

    private static bool IsUnder(string path, string directory) =>
        directory.Length > 0 && path.StartsWith(directory, StringComparison.Ordinal);

    private static bool IsCovered(CoberturaLineRow row, PatchLineMode mode) => mode == PatchLineMode.Codecov
        ? row.Covered && (row.CoveredConditions is null || row.CoveredConditions == row.ValidConditions)
        : row.Covered;

    private static bool IsBelow(ScopedPatchMetric metric, decimal? threshold, decimal tolerance) =>
        threshold is { } value && metric.IsMeasured
        && metric.Percent < CoverageGateEvaluator.GetEffectiveThreshold(value, tolerance);

    /// <summary>Cuts a path to the receipt limit without splitting a surrogate pair.</summary>
    internal static (string Path, bool Cut) Cut(string path)
    {
        var limit = EvidenceScopedCoverageContract.MaximumReceiptPathLength;
        if (path.Length <= limit)
        {
            return (path, false);
        }

        if (char.IsHighSurrogate(path[limit - 1]))
        {
            limit--;
        }

        return (path[..limit], true);
    }
}
