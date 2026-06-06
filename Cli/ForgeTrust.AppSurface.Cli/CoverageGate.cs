using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Evaluates a Cobertura coverage file against line and branch thresholds.
/// </summary>
/// <remarks>
/// This is the stable v1 coverage command. It reads a local Cobertura artifact, writes private
/// JSON and Markdown reports, optionally appends the Markdown report to GitHub's step summary, and
/// exits non-zero when the configured quality gate fails.
/// </remarks>
[Command("coverage gate", Description = "Enforce line and branch thresholds from a Cobertura coverage file.")]
internal sealed partial class CoverageGateCommand : ICommand
{
    /// <summary>
    /// Gets or sets the Cobertura XML file to evaluate.
    /// </summary>
    [CommandOption("coverage", Description = "Cobertura XML path. Defaults to TestResults/coverage-merged/coverage.cobertura.xml.")]
    public string CoveragePath { get; set; } = Path.Join("TestResults", "coverage-merged", "coverage.cobertura.xml");

    /// <summary>
    /// Gets or sets the minimum line coverage percentage from 0 to 100.
    /// </summary>
    [CommandOption("min-line", Description = "Minimum line coverage percentage from 0 to 100.")]
    public decimal MinLine { get; set; }

    /// <summary>
    /// Gets or sets the minimum branch coverage percentage from 0 to 100.
    /// </summary>
    [CommandOption("min-branch", Description = "Minimum branch coverage percentage from 0 to 100.")]
    public decimal MinBranch { get; set; }

    /// <summary>
    /// Gets or sets the git ref or commit used to compute changed-line coverage.
    /// </summary>
    [CommandOption("diff-base", Description = "Git ref or commit used with HEAD to estimate changed-line coverage.")]
    public string? DiffBase { get; set; }

    /// <summary>
    /// Gets or sets the minimum changed-line coverage percentage from 0 to 100.
    /// </summary>
    [CommandOption("min-patch-line", Description = "Minimum changed-line coverage percentage from 0 to 100. Requires --diff-base.")]
    public decimal? MinPatchLine { get; set; }

    /// <summary>
    /// Gets or sets the minimum changed-branch coverage percentage from 0 to 100.
    /// </summary>
    [CommandOption("min-patch-branch", Description = "Minimum changed-branch coverage percentage from 0 to 100. Requires --diff-base.")]
    public decimal? MinPatchBranch { get; set; }

    /// <summary>
    /// Gets or sets the directory where coverage-gate.json and coverage-gate.md are written.
    /// </summary>
    [CommandOption("output", Description = "Output directory for coverage-gate.json and coverage-gate.md.")]
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Markdown report should be appended to $GITHUB_STEP_SUMMARY.
    /// </summary>
    [CommandOption("github-summary", Description = "Append the Markdown report to $GITHUB_STEP_SUMMARY when it is set.")]
    public bool GithubSummary { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether GitHub summary output should be suppressed.
    /// </summary>
    [CommandOption("no-github-summary", Description = "Do not append to $GITHUB_STEP_SUMMARY even when it is set.")]
    public bool NoGithubSummary { get; set; }

    /// <summary>
    /// Executes the coverage gate.
    /// </summary>
    /// <param name="console">CliFx console used for command output.</param>
    /// <returns>A task that completes after report files are written.</returns>
    [ExcludeFromCodeCoverage]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await ExecuteAsync(console, console.RegisterCancellationHandler());
    }

    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        var request = CreateRequest();
        var result = await CoverageGateEvaluator.EvaluateAsync(request, cancellationToken);
        await CoverageGateReportWriter.WriteAsync(result, request, cancellationToken);

        var status = result.Passed ? "PASS" : "FAIL";
        await console.Output.WriteLineAsync(RenderConsoleSummary(status, result, request));
        await console.Output.WriteLineAsync($"Reports: {result.JsonReportPath} and {result.MarkdownReportPath}");

        if (!result.Passed)
        {
            throw new CommandException("ASCOV020 Coverage gate failed. Raise coverage or lower the configured thresholds intentionally.");
        }
    }

    private CoverageGateRequest CreateRequest()
    {
        if (!CoverageGateEvaluator.IsPercentInRange(MinLine))
        {
            throw new CommandException("ASCOV007 --min-line must be between 0 and 100.");
        }

        if (!CoverageGateEvaluator.IsPercentInRange(MinBranch))
        {
            throw new CommandException("ASCOV007 --min-branch must be between 0 and 100.");
        }

        if (MinPatchLine.HasValue && !CoverageGateEvaluator.IsPercentInRange(MinPatchLine.Value))
        {
            throw new CommandException("ASCOV007 --min-patch-line must be between 0 and 100.");
        }

        if (MinPatchBranch.HasValue && !CoverageGateEvaluator.IsPercentInRange(MinPatchBranch.Value))
        {
            throw new CommandException("ASCOV007 --min-patch-branch must be between 0 and 100.");
        }

        if ((MinPatchLine.HasValue || MinPatchBranch.HasValue) && string.IsNullOrWhiteSpace(DiffBase))
        {
            throw new CommandException("ASCOV007 patch coverage thresholds require --diff-base.");
        }

        var coveragePath = Path.GetFullPath(CoveragePath);
        var outputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
            ? Path.GetDirectoryName(coveragePath) ?? Directory.GetCurrentDirectory()
            : Path.GetFullPath(OutputDirectory);
        var patchCoverage = string.IsNullOrWhiteSpace(DiffBase)
            ? null
            : new CoveragePatchRequest(
                Path.GetFullPath(Directory.GetCurrentDirectory()),
                DiffBase.Trim(),
                MinPatchLine,
                MinPatchBranchPercent: MinPatchBranch);

        return new CoverageGateRequest(
            coveragePath,
            outputDirectory,
            MinLine,
            MinBranch,
            GithubSummary && !NoGithubSummary,
            Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"),
            patchCoverage);
    }

    private static string RenderConsoleSummary(
        string status,
        CoverageGateResult result,
        CoverageGateRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(FormattableString.Invariant(
            $"Coverage gate {status}: lines {result.LineCoverage.Percent:0.00}% >= {request.MinLinePercent:0.##}%, branches {result.BranchCoverage.Percent:0.00}% >= {request.MinBranchPercent:0.##}%"));

        if (result.PatchLineCoverage is { } patchCoverage)
        {
            builder.Append(FormattableString.Invariant($", patch lines {patchCoverage.Percent:0.00}%"));
            if (request.PatchCoverage?.MinPatchLinePercent is { } threshold)
            {
                builder.Append(FormattableString.Invariant($" >= {threshold:0.##}%"));
            }
        }

        if (result.PatchBranchCoverage is { } patchCoverageBranch)
        {
            builder.Append(FormattableString.Invariant($", patch branches {patchCoverageBranch.Percent:0.00}%"));
            if (request.PatchCoverage?.MinPatchBranchPercent is { } threshold)
            {
                builder.Append(FormattableString.Invariant($" >= {threshold:0.##}%"));
            }
        }

        builder.Append('.');
        return builder.ToString();
    }
}

/// <summary>
/// Request for evaluating a Cobertura coverage quality gate.
/// </summary>
/// <param name="CoveragePath">Absolute path to the Cobertura XML file.</param>
/// <param name="OutputDirectory">Absolute directory where private report artifacts should be written.</param>
/// <param name="MinLinePercent">Minimum accepted line coverage percentage.</param>
/// <param name="MinBranchPercent">Minimum accepted branch coverage percentage.</param>
/// <param name="WriteGithubSummary">Whether to append Markdown output to <paramref name="GithubStepSummaryPath"/>.</param>
/// <param name="GithubStepSummaryPath">Optional GitHub step summary file path.</param>
/// <param name="PatchCoverage">Optional changed-line coverage request.</param>
internal sealed record CoverageGateRequest(
    string CoveragePath,
    string OutputDirectory,
    decimal MinLinePercent,
    decimal MinBranchPercent,
    bool WriteGithubSummary,
    string? GithubStepSummaryPath,
    CoveragePatchRequest? PatchCoverage = null);

/// <summary>
/// Request for estimating coverage on lines changed by a git diff.
/// </summary>
/// <param name="RepositoryRoot">Repository root used for git diff and path normalization.</param>
/// <param name="DiffBase">Git ref or commit compared with HEAD.</param>
/// <param name="MinPatchLinePercent">Optional changed-line threshold.</param>
/// <param name="DiffProvider">Optional test seam for supplying unified diff text without invoking git.</param>
/// <param name="MinPatchBranchPercent">Optional changed-branch threshold.</param>
internal sealed record CoveragePatchRequest(
    string RepositoryRoot,
    string DiffBase,
    decimal? MinPatchLinePercent,
    Func<CancellationToken, Task<string>>? DiffProvider = null,
    decimal? MinPatchBranchPercent = null);

/// <summary>
/// Result of evaluating coverage against line and branch thresholds.
/// </summary>
/// <param name="CoveragePath">Cobertura file that was evaluated.</param>
/// <param name="LineCoverage">Line coverage metric.</param>
/// <param name="BranchCoverage">Branch coverage metric.</param>
/// <param name="MinLinePercent">Minimum accepted line coverage percentage.</param>
/// <param name="MinBranchPercent">Minimum accepted branch coverage percentage.</param>
/// <param name="Passed">Whether both metrics met their thresholds.</param>
/// <param name="JsonReportPath">JSON report path.</param>
/// <param name="MarkdownReportPath">Markdown report path.</param>
/// <param name="MinPatchLinePercent">Optional minimum accepted changed-line coverage percentage.</param>
/// <param name="PatchLineCoverage">Optional changed-line coverage metric.</param>
/// <param name="MinPatchBranchPercent">Optional minimum accepted changed-branch coverage percentage.</param>
/// <param name="PatchBranchCoverage">Optional changed-branch coverage metric.</param>
internal sealed record CoverageGateResult(
    string CoveragePath,
    CoverageMetric LineCoverage,
    CoverageMetric BranchCoverage,
    decimal MinLinePercent,
    decimal MinBranchPercent,
    bool Passed,
    string JsonReportPath,
    string MarkdownReportPath,
    decimal? MinPatchLinePercent = null,
    PatchLineCoverageMetric? PatchLineCoverage = null,
    decimal? MinPatchBranchPercent = null,
    PatchBranchCoverageMetric? PatchBranchCoverage = null);

/// <summary>
/// One Cobertura coverage metric expressed as optional covered/valid counts and a percentage.
/// </summary>
/// <param name="Covered">Covered line or branch count, when Cobertura provides it.</param>
/// <param name="Valid">Valid line or branch count, when Cobertura provides it.</param>
/// <param name="Percent">Coverage percentage from 0 to 100.</param>
internal sealed record CoverageMetric(int? Covered, int? Valid, decimal Percent);

/// <summary>
/// Coverage estimate for changed lines from a git diff.
/// </summary>
/// <param name="DiffBase">Git ref or commit compared with HEAD.</param>
/// <param name="ChangedLines">Total added or modified lines in the diff.</param>
/// <param name="MeasurableLines">Changed lines present in the Cobertura line map.</param>
/// <param name="CoveredLines">Measurable changed lines with at least one hit.</param>
/// <param name="Percent">Changed-line coverage percentage.</param>
internal sealed record PatchLineCoverageMetric(
    string DiffBase,
    int ChangedLines,
    int MeasurableLines,
    int CoveredLines,
    decimal Percent);

/// <summary>
/// Coverage estimate for branch conditions on changed lines from a git diff.
/// </summary>
/// <param name="DiffBase">Git ref or commit compared with HEAD.</param>
/// <param name="ChangedLines">Total added or modified lines in the diff.</param>
/// <param name="MeasurableBranches">Changed-line branch conditions present in the Cobertura line map.</param>
/// <param name="CoveredBranches">Measurable changed-line branch conditions that were covered.</param>
/// <param name="Percent">Changed-branch coverage percentage.</param>
internal sealed record PatchBranchCoverageMetric(
    string DiffBase,
    int ChangedLines,
    int MeasurableBranches,
    int CoveredBranches,
    decimal Percent);

/// <summary>
/// Coverage estimates for changed lines and changed-line branch conditions.
/// </summary>
/// <param name="LineCoverage">Changed-line coverage metric.</param>
/// <param name="BranchCoverage">Changed-branch coverage metric.</param>
internal sealed record PatchCoverageMetrics(
    PatchLineCoverageMetric LineCoverage,
    PatchBranchCoverageMetric BranchCoverage);

/// <summary>
/// Parses Cobertura XML and evaluates coverage thresholds.
/// </summary>
/// <remarks>
/// XML parsing disables DTD processing and external resolution because coverage files are CI
/// artifacts, not trusted configuration. The evaluator accepts either Cobertura rate attributes or
/// covered/valid counts and validates that the resulting metrics are finite percentages.
/// </remarks>
internal static class CoverageGateEvaluator
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    /// <summary>
    /// Evaluates a Cobertura file against the requested line and branch thresholds.
    /// </summary>
    /// <param name="request">Coverage gate request.</param>
    /// <param name="cancellationToken">Cancellation token for file IO.</param>
    /// <returns>The gate result.</returns>
    /// <exception cref="CommandException">Thrown when inputs are missing, unsafe, or unsupported.</exception>
    public static async Task<CoverageGateResult> EvaluateAsync(
        CoverageGateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        try
        {
            await using var stream = File.OpenRead(request.CoveragePath);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            while (await reader.ReadAsync())
            {
                if (reader.NodeType != XmlNodeType.Element || !string.Equals(reader.Name, "coverage", StringComparison.Ordinal))
                {
                    continue;
                }

                var lineCoverage = ReadMetric(reader, "line", "lines-covered", "lines-valid", "line-rate");
                var branchCoverage = ReadMetric(reader, "branch", "branches-covered", "branches-valid", "branch-rate");
                var patchCoverage = request.PatchCoverage is null
                    ? null
                    : await PatchCoverageEvaluator.EvaluateAsync(request.CoveragePath, request.PatchCoverage, cancellationToken);
                var passed = lineCoverage.Percent >= request.MinLinePercent
                    && branchCoverage.Percent >= request.MinBranchPercent
                    && IsPatchCoveragePassing(patchCoverage?.LineCoverage, request.PatchCoverage?.MinPatchLinePercent)
                    && IsPatchCoveragePassing(patchCoverage?.BranchCoverage, request.PatchCoverage?.MinPatchBranchPercent);
                return new CoverageGateResult(
                    request.CoveragePath,
                    lineCoverage,
                    branchCoverage,
                    request.MinLinePercent,
                    request.MinBranchPercent,
                    passed,
                    Path.Join(request.OutputDirectory, "coverage-gate.json"),
                    Path.Join(request.OutputDirectory, "coverage-gate.md"),
                    request.PatchCoverage?.MinPatchLinePercent,
                    patchCoverage?.LineCoverage,
                    request.PatchCoverage?.MinPatchBranchPercent,
                    patchCoverage?.BranchCoverage);
            }
        }
        catch (XmlException ex)
        {
            throw new CommandException($"ASCOV006 Failed to parse Cobertura XML '{request.CoveragePath}': {ex.Message}");
        }

        throw new CommandException($"ASCOV006 Cobertura file does not contain a <coverage> root element: {request.CoveragePath}");
    }

    private static bool IsPatchCoveragePassing(PatchLineCoverageMetric? patchCoverage, decimal? threshold)
    {
        if (patchCoverage is null || threshold is null)
        {
            return true;
        }

        return patchCoverage.Percent >= threshold.Value;
    }

    private static bool IsPatchCoveragePassing(PatchBranchCoverageMetric? patchCoverage, decimal? threshold)
    {
        if (patchCoverage is null || threshold is null)
        {
            return true;
        }

        return patchCoverage.Percent >= threshold.Value;
    }

    /// <summary>
    /// Gets whether a threshold percentage is in the valid inclusive range.
    /// </summary>
    /// <param name="value">Candidate percentage.</param>
    /// <returns><c>true</c> when <paramref name="value"/> is from 0 through 100.</returns>
    public static bool IsPercentInRange(decimal value) => value is >= 0 and <= 100;

    private static void ValidateRequest(CoverageGateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CoveragePath))
        {
            throw new CommandException("ASCOV001 --coverage must point to a Cobertura XML file.");
        }

        if (!File.Exists(request.CoveragePath))
        {
            throw new CommandException($"ASCOV001 Cobertura file not found: {request.CoveragePath}");
        }

        if (!IsPercentInRange(request.MinLinePercent))
        {
            throw new CommandException("ASCOV007 --min-line must be between 0 and 100.");
        }

        if (!IsPercentInRange(request.MinBranchPercent))
        {
            throw new CommandException("ASCOV007 --min-branch must be between 0 and 100.");
        }

        if (request.PatchCoverage is { MinPatchLinePercent: { } minPatchLinePercent }
            && !IsPercentInRange(minPatchLinePercent))
        {
            throw new CommandException("ASCOV007 --min-patch-line must be between 0 and 100.");
        }

        if (request.PatchCoverage is { MinPatchBranchPercent: { } minPatchBranchPercent }
            && !IsPercentInRange(minPatchBranchPercent))
        {
            throw new CommandException("ASCOV007 --min-patch-branch must be between 0 and 100.");
        }

        CoverageOutputPathPolicy.ValidateReportOutput(request.CoveragePath, request.OutputDirectory);
    }

    private static CoverageMetric ReadMetric(
        XmlReader reader,
        string metricName,
        string coveredAttribute,
        string validAttribute,
        string rateAttribute)
    {
        var hasCovered = TryReadIntAttribute(reader, coveredAttribute, out var covered);
        var hasValid = TryReadIntAttribute(reader, validAttribute, out var valid);
        var hasRate = TryReadRateAttribute(reader, rateAttribute, out var percent);

        if (!hasCovered || !hasValid)
        {
            if (!hasRate)
            {
                throw new CommandException(
                    $"ASCOV006 Cobertura coverage is missing {metricName} counts and {rateAttribute}.");
            }

            return new CoverageMetric(null, null, percent);
        }

        if (covered < 0 || valid < 0 || covered > valid)
        {
            throw new CommandException($"ASCOV006 Cobertura {metricName} counts are out of range.");
        }

        if (valid == 0)
        {
            throw new CommandException($"ASCOV006 Cobertura {metricName} coverage has zero valid items.");
        }

        percent = Math.Round(covered * 100m / valid, 4, MidpointRounding.AwayFromZero);
        if (!IsPercentInRange(percent))
        {
            throw new CommandException($"ASCOV006 Cobertura {metricName} rate is outside the 0-100 range.");
        }

        return new CoverageMetric(covered, valid, percent);
    }

    private static bool TryReadIntAttribute(XmlReader reader, string attributeName, out int value)
    {
        value = 0;
        var text = reader.GetAttribute(attributeName);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadRateAttribute(XmlReader reader, string attributeName, out decimal percent)
    {
        percent = 0;
        var text = reader.GetAttribute(attributeName);
        if (string.IsNullOrWhiteSpace(text)
            || !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            return false;
        }

        if (rate < 0 || rate > 1)
        {
            throw new CommandException($"ASCOV006 Cobertura {attributeName} must be a decimal rate from 0 to 1.");
        }

        percent = Math.Round(rate * 100m, 4, MidpointRounding.AwayFromZero);
        return true;
    }
}

/// <summary>
/// Estimates coverage for lines and branch conditions added or modified by a git diff.
/// </summary>
internal static class PatchCoverageEvaluator
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    private readonly record struct PatchBranchCoverageCounts(int Covered, int Valid)
    {
        public PatchBranchCoverageCounts Merge(PatchBranchCoverageCounts other) =>
            new(Covered + other.Covered, Valid + other.Valid);
    }

    private sealed record PatchLineCoverageData(bool Covered, PatchBranchCoverageCounts? Branches)
    {
        public PatchLineCoverageData Merge(bool covered, PatchBranchCoverageCounts? branches)
        {
            PatchBranchCoverageCounts? mergedBranches = (Branches, branches) switch
            {
                ({ } left, { } right) => left.Merge(right),
                ({ } left, null) => left,
                (null, { } right) => right,
                _ => null,
            };

            return new PatchLineCoverageData(Covered || covered, mergedBranches);
        }
    }

    /// <summary>
    /// Evaluates changed-line and changed-branch coverage by reading <c>git diff</c> and the Cobertura line map.
    /// </summary>
    /// <param name="coveragePath">Cobertura XML file.</param>
    /// <param name="request">Changed-line coverage request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Changed-line and changed-branch coverage metrics.</returns>
    public static async Task<PatchCoverageMetrics> EvaluateAsync(
        string coveragePath,
        CoveragePatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DiffBase))
        {
            throw new CommandException("ASCOV007 --diff-base must not be blank.");
        }

        var diffText = request.DiffProvider is null
            ? await GitDiffReader.ReadDiffAsync(request.RepositoryRoot, request.DiffBase, cancellationToken)
            : await request.DiffProvider(cancellationToken);
        return await EvaluateAsync(coveragePath, request, diffText, cancellationToken);
    }

    /// <summary>
    /// Evaluates changed-line and changed-branch coverage from supplied git diff text.
    /// </summary>
    /// <param name="coveragePath">Cobertura XML file.</param>
    /// <param name="request">Changed-line coverage request.</param>
    /// <param name="diffText">Unified git diff text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Changed-line and changed-branch coverage metrics.</returns>
    public static async Task<PatchCoverageMetrics> EvaluateAsync(
        string coveragePath,
        CoveragePatchRequest request,
        string diffText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var changedLines = ParseChangedLines(diffText);
        var lineHits = await ReadLineCoverageMapAsync(coveragePath, request.RepositoryRoot, cancellationToken);
        var changedLineCount = 0;
        var measurableLineCount = 0;
        var coveredLineCount = 0;
        var measurableBranchCount = 0;
        var coveredBranchCount = 0;

        foreach (var (file, lines) in changedLines)
        {
            changedLineCount += lines.Count;
            if (!lineHits.TryGetValue(file, out var fileHits))
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (!fileHits.TryGetValue(line, out var lineCoverage))
                {
                    continue;
                }

                measurableLineCount++;
                if (lineCoverage.Covered)
                {
                    coveredLineCount++;
                }

                if (lineCoverage.Branches is { } branches)
                {
                    measurableBranchCount += branches.Valid;
                    coveredBranchCount += branches.Covered;
                }
            }
        }

        var linePercent = measurableLineCount == 0
            ? 100m
            : Math.Round(coveredLineCount * 100m / measurableLineCount, 4, MidpointRounding.AwayFromZero);
        var branchPercent = measurableBranchCount == 0
            ? 100m
            : Math.Round(coveredBranchCount * 100m / measurableBranchCount, 4, MidpointRounding.AwayFromZero);
        return new PatchCoverageMetrics(
            new PatchLineCoverageMetric(
                request.DiffBase,
                changedLineCount,
                measurableLineCount,
                coveredLineCount,
                linePercent),
            new PatchBranchCoverageMetric(
                request.DiffBase,
                changedLineCount,
                measurableBranchCount,
                coveredBranchCount,
                branchPercent));
    }

    internal static IReadOnlyDictionary<string, IReadOnlySet<int>> ParseChangedLines(string diffText)
    {
        var changedLines = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        string? currentFile = null;
        int? currentNewLine = null;

        foreach (var line in SplitLines(diffText))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentFile = NormalizeDiffPath(line["+++ ".Length..]);
                currentNewLine = null;
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                currentNewLine = TryReadNewHunkStart(line, out var start) ? start : null;
                continue;
            }

            if (currentFile is null || currentNewLine is null)
            {
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal))
            {
                AddChangedLine(changedLines, currentFile, currentNewLine.Value);
                currentNewLine++;
                continue;
            }

            if (line.StartsWith(" ", StringComparison.Ordinal))
            {
                currentNewLine++;
            }
        }

        return changedLines.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<int>)pair.Value,
            StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, Dictionary<int, PatchLineCoverageData>>> ReadLineCoverageMapAsync(
        string coveragePath,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var lineHits = new Dictionary<string, Dictionary<int, PatchLineCoverageData>>(StringComparer.Ordinal);

        try
        {
            await using var stream = File.OpenRead(coveragePath);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            string? currentFile = null;

            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.Element
                    && string.Equals(reader.Name, "class", StringComparison.Ordinal))
                {
                    currentFile = NormalizeCoveragePath(reader.GetAttribute("filename"), repositoryRoot);
                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement
                    && string.Equals(reader.Name, "class", StringComparison.Ordinal))
                {
                    currentFile = null;
                    continue;
                }

                if (currentFile is null
                    || reader.NodeType != XmlNodeType.Element
                    || !string.Equals(reader.Name, "line", StringComparison.Ordinal)
                    || !int.TryParse(reader.GetAttribute("number"), NumberStyles.None, CultureInfo.InvariantCulture, out var lineNumber)
                    || !int.TryParse(reader.GetAttribute("hits"), NumberStyles.None, CultureInfo.InvariantCulture, out var hits))
                {
                    continue;
                }

                if (!lineHits.TryGetValue(currentFile, out var fileHits))
                {
                    fileHits = new Dictionary<int, PatchLineCoverageData>();
                    lineHits.Add(currentFile, fileHits);
                }

                PatchBranchCoverageCounts? branches = TryReadBranchCoverage(reader, out var branchCoverage)
                    ? branchCoverage
                    : null;
                if (fileHits.TryGetValue(lineNumber, out var existing))
                {
                    fileHits[lineNumber] = existing.Merge(hits > 0, branches);
                    continue;
                }

                fileHits[lineNumber] = new PatchLineCoverageData(hits > 0, branches);
            }
        }
        catch (XmlException ex)
        {
            throw new CommandException($"ASCOV006 Failed to parse Cobertura XML '{coveragePath}': {ex.Message}");
        }

        return lineHits;
    }

    private static bool TryReadBranchCoverage(XmlReader reader, out PatchBranchCoverageCounts coverage)
    {
        coverage = default;
        var conditionCoverage = reader.GetAttribute("condition-coverage");
        if (string.IsNullOrWhiteSpace(conditionCoverage))
        {
            return false;
        }

        var openIndex = conditionCoverage.IndexOf('(', StringComparison.Ordinal);
        var slashIndex = conditionCoverage.IndexOf('/', StringComparison.Ordinal);
        var closeIndex = conditionCoverage.IndexOf(')', StringComparison.Ordinal);
        if (openIndex < 0
            || slashIndex <= openIndex
            || closeIndex <= slashIndex
            || !int.TryParse(conditionCoverage.AsSpan(openIndex + 1, slashIndex - openIndex - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var covered)
            || !int.TryParse(conditionCoverage.AsSpan(slashIndex + 1, closeIndex - slashIndex - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var valid)
            || covered < 0
            || valid < 0
            || covered > valid)
        {
            throw new CommandException("ASCOV006 Cobertura line condition coverage is out of range.");
        }

        if (valid == 0)
        {
            return false;
        }

        coverage = new PatchBranchCoverageCounts(covered, valid);
        return true;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string? NormalizeDiffPath(string value)
    {
        var path = value.Trim();
        if (string.Equals(path, "/dev/null", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith("b/", StringComparison.Ordinal) || path.StartsWith("a/", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return NormalizeRelativePath(path);
    }

    private static string? NormalizeCoveragePath(string? value, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim();
        if (Path.IsPathFullyQualified(path))
        {
            var relative = Path.GetRelativePath(repositoryRoot, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            {
                return NormalizeRelativePath(path);
            }

            path = relative;
        }

        return NormalizeRelativePath(path);
    }

    private static string NormalizeRelativePath(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path;
    }

    private static bool TryReadNewHunkStart(string header, out int start)
    {
        start = 0;
        var plusIndex = header.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex < 0)
        {
            return false;
        }

        var endIndex = plusIndex + 1;
        while (endIndex < header.Length && header[endIndex] != ' ' && header[endIndex] != ',')
        {
            endIndex++;
        }

        return int.TryParse(
            header.AsSpan(plusIndex + 1, endIndex - plusIndex - 1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out start);
    }

    private static void AddChangedLine(
        Dictionary<string, HashSet<int>> changedLines,
        string file,
        int lineNumber)
    {
        if (!changedLines.TryGetValue(file, out var lines))
        {
            lines = [];
            changedLines.Add(file, lines);
        }

        lines.Add(lineNumber);
    }
}

/// <summary>
/// Reads changed lines from git.
/// </summary>
internal static class GitDiffReader
{
    /// <summary>
    /// Reads a zero-context diff between <paramref name="diffBase"/> and <c>HEAD</c>.
    /// </summary>
    /// <param name="repositoryRoot">Repository root where git should run.</param>
    /// <param name="diffBase">Git ref or commit compared with HEAD.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Unified diff text.</returns>
    public static async Task<string> ReadDiffAsync(
        string repositoryRoot,
        string diffBase,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--unified=0");
        startInfo.ArgumentList.Add("--no-ext-diff");
        startInfo.ArgumentList.Add("--relative");
        startInfo.ArgumentList.Add($"{diffBase}...HEAD");
        startInfo.ArgumentList.Add("--");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new CommandException("ASCOV010 Failed to start git diff.");
            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput.Trim() : standardError.Trim();
                throw new CommandException(
                    $"ASCOV010 Failed to read git diff from '{diffBase}' to HEAD: {details}");
            }

            return standardOutput;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new CommandException($"ASCOV010 Failed to run git diff: {ex.Message}");
        }
    }
}

/// <summary>
/// Writes private coverage gate report artifacts.
/// </summary>
internal static class CoverageGateReportWriter
{
    private const int GithubSummaryLimitBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Writes JSON, Markdown, and optional GitHub summary output for a coverage gate result.
    /// </summary>
    /// <param name="result">Gate result to render.</param>
    /// <param name="request">Original gate request.</param>
    /// <param name="cancellationToken">Cancellation token for file IO.</param>
    /// <returns>A task that completes when output files are written.</returns>
    public static async Task WriteAsync(
        CoverageGateResult result,
        CoverageGateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);

        Directory.CreateDirectory(request.OutputDirectory);
        var json = JsonSerializer.Serialize(
            new
            {
                passed = result.Passed,
                coverage = result.CoveragePath,
                thresholds = new
                {
                    line = result.MinLinePercent,
                    branch = result.MinBranchPercent,
                    patchLine = result.MinPatchLinePercent,
                    patchBranch = result.MinPatchBranchPercent,
                },
                line = ToJson(result.LineCoverage),
                branch = ToJson(result.BranchCoverage),
                patchLine = ToJson(result.PatchLineCoverage),
                patchBranch = ToJson(result.PatchBranchCoverage),
            },
            JsonOptions);
        await File.WriteAllTextAsync(result.JsonReportPath, json + Environment.NewLine, cancellationToken);

        var markdown = RenderMarkdown(result);
        await File.WriteAllTextAsync(result.MarkdownReportPath, markdown, cancellationToken);

        if (request.WriteGithubSummary && !string.IsNullOrWhiteSpace(request.GithubStepSummaryPath))
        {
            await AppendGithubSummaryAsync(request.GithubStepSummaryPath, markdown, cancellationToken);
        }
    }

    /// <summary>
    /// Renders the Markdown gate report.
    /// </summary>
    /// <param name="result">Coverage gate result.</param>
    /// <returns>Markdown report content.</returns>
    public static string RenderMarkdown(CoverageGateResult result)
    {
        var status = result.Passed ? "PASS" : "FAIL";
        var builder = new StringBuilder();
        builder.AppendLine($"# Coverage Gate: {status}");
        builder.AppendLine();
        builder.AppendLine($"Cobertura: `{EscapeMarkdownCell(result.CoveragePath)}`");
        builder.AppendLine();
        builder.AppendLine("| Metric | Coverage | Threshold | Result |");
        builder.AppendLine("| --- | ---: | ---: | --- |");
        AppendMetric(builder, "Lines", result.LineCoverage, result.MinLinePercent);
        AppendMetric(builder, "Branches", result.BranchCoverage, result.MinBranchPercent);
        if (result.PatchLineCoverage is { } patchCoverage)
        {
            AppendPatchMetric(builder, patchCoverage, result.MinPatchLinePercent);
        }

        if (result.PatchBranchCoverage is { } branchCoverage)
        {
            AppendPatchMetric(builder, branchCoverage, result.MinPatchBranchPercent);
        }

        return builder.ToString();
    }

    private static object ToJson(CoverageMetric metric) => new
    {
        covered = metric.Covered,
        valid = metric.Valid,
        percent = metric.Percent,
    };

    private static object? ToJson(PatchLineCoverageMetric? metric)
    {
        if (metric is null)
        {
            return null;
        }

        return new
        {
            diffBase = metric.DiffBase,
            changed = metric.ChangedLines,
            measurable = metric.MeasurableLines,
            covered = metric.CoveredLines,
            percent = metric.Percent,
        };
    }

    private static object? ToJson(PatchBranchCoverageMetric? metric)
    {
        if (metric is null)
        {
            return null;
        }

        return new
        {
            diffBase = metric.DiffBase,
            changed = metric.ChangedLines,
            measurable = metric.MeasurableBranches,
            covered = metric.CoveredBranches,
            percent = metric.Percent,
        };
    }

    private static void AppendMetric(StringBuilder builder, string label, CoverageMetric metric, decimal threshold)
    {
        var result = metric.Percent >= threshold ? "pass" : "fail";
        var coverage = metric.Covered.HasValue && metric.Valid.HasValue
            ? FormattableString.Invariant($"{metric.Percent:0.00}% ({metric.Covered}/{metric.Valid})")
            : FormattableString.Invariant($"{metric.Percent:0.00}% (count unavailable)");
        builder.AppendLine(FormattableString.Invariant($"| {label} | {coverage} | {threshold:0.##}% | {result} |"));
    }

    private static void AppendPatchMetric(
        StringBuilder builder,
        PatchLineCoverageMetric metric,
        decimal? threshold)
    {
        var outcome = threshold is null ? "reported" : metric.Percent >= threshold ? "pass" : "fail";
        var thresholdText = threshold is null
            ? "report"
            : FormattableString.Invariant($"{threshold:0.##}%");
        var coverage = metric.MeasurableLines == 0
            ? FormattableString.Invariant($"{metric.Percent:0.00}% (no measurable changed lines, {metric.ChangedLines} changed)")
            : FormattableString.Invariant($"{metric.Percent:0.00}% ({metric.CoveredLines}/{metric.MeasurableLines} measurable, {metric.ChangedLines} changed)");
        builder.AppendLine(FormattableString.Invariant($"| Patch lines | {coverage} | {thresholdText} | {outcome} |"));
    }

    private static void AppendPatchMetric(
        StringBuilder builder,
        PatchBranchCoverageMetric metric,
        decimal? threshold)
    {
        var outcome = threshold is null ? "reported" : metric.Percent >= threshold ? "pass" : "fail";
        var thresholdText = threshold is null
            ? "report"
            : FormattableString.Invariant($"{threshold:0.##}%");
        var coverage = metric.MeasurableBranches == 0
            ? FormattableString.Invariant($"{metric.Percent:0.00}% (no measurable changed branches, {metric.ChangedLines} changed)")
            : FormattableString.Invariant($"{metric.Percent:0.00}% ({metric.CoveredBranches}/{metric.MeasurableBranches} measurable, {metric.ChangedLines} changed)");
        builder.AppendLine(FormattableString.Invariant($"| Patch branches | {coverage} | {thresholdText} | {outcome} |"));
    }

    private static async Task AppendGithubSummaryAsync(
        string githubStepSummaryPath,
        string markdown,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(githubStepSummaryPath);
        var sanitized = StripControls(markdown);
        var bytes = Encoding.UTF8.GetBytes(sanitized);
        if (bytes.Length > GithubSummaryLimitBytes)
        {
            sanitized = Encoding.UTF8.GetString(bytes.AsSpan(0, GithubSummaryLimitBytes));
        }

        try
        {
            await File.AppendAllTextAsync(path, sanitized + Environment.NewLine, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommandException($"ASCOV008 Failed to write GitHub step summary '{path}': {ex.Message}");
        }
    }

    private static string EscapeMarkdownCell(string value)
    {
        return StripControls(value)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
    }

    private static string StripControls(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Where(ch => !char.IsControl(ch) || ch is '\r' or '\n' or '\t'))
        {
            builder.Append(ch);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Validates coverage report output paths before report writers create or replace files.
/// </summary>
/// <remarks>
/// The coverage gate writes only two report files, but it still refuses unsafe destinations so
/// future run/merge cleanup behavior can share the same conservative policy.
/// </remarks>
internal static class CoverageOutputPathPolicy
{
    /// <summary>
    /// Validates that the report output directory is distinct from the coverage file and safe to create.
    /// </summary>
    /// <param name="coveragePath">Coverage file path being read.</param>
    /// <param name="outputDirectory">Report output directory to validate.</param>
    /// <exception cref="CommandException">Thrown when the output path is dangerous or overlaps the input file.</exception>
    public static void ValidateReportOutput(string coveragePath, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new CommandException("ASCOV009 --output must point to a coverage report directory.");
        }

        var fullOutput = NormalizeMacPrivateVar(Path.GetFullPath(outputDirectory));
        var fullCoverage = NormalizeMacPrivateVar(Path.GetFullPath(coveragePath));
        var comparison = GetPathComparison();
        var outputRoot = Path.GetPathRoot(fullOutput);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullOutput),
                Path.TrimEndingDirectorySeparator(outputRoot ?? fullOutput),
                comparison))
        {
            throw new CommandException("ASCOV009 --output must not be a filesystem root.");
        }

        var coverageDirectory = Path.GetDirectoryName(fullCoverage);
        if (coverageDirectory is not null && string.Equals(
                Path.TrimEndingDirectorySeparator(fullOutput),
                Path.TrimEndingDirectorySeparator(coverageDirectory),
                comparison))
        {
            return;
        }

        var currentDirectory = NormalizeMacPrivateVar(Path.GetFullPath(Directory.GetCurrentDirectory()));
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullOutput),
                Path.TrimEndingDirectorySeparator(currentDirectory),
                comparison))
        {
            throw new CommandException("ASCOV009 --output must not be the current working directory.");
        }

        if (IsDirectoryAncestor(fullOutput, fullCoverage, comparison))
        {
            throw new CommandException("ASCOV009 --output must not contain the input coverage file.");
        }
    }

    private static bool IsDirectoryAncestor(string ancestor, string descendant, StringComparison comparison)
    {
        var normalizedAncestor = Normalize(Path.TrimEndingDirectorySeparator(ancestor));
        var normalizedDescendant = Normalize(descendant);
        var prefix = normalizedAncestor.EndsWith('/') ? normalizedAncestor : normalizedAncestor + "/";
        return normalizedDescendant.StartsWith(prefix, comparison);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string NormalizeMacPrivateVar(string path)
    {
        return OperatingSystem.IsMacOS() && path.StartsWith("/private/var/", StringComparison.Ordinal)
            ? path["/private".Length..]
            : path;
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
