using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
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
    private const long DefaultExternalDiffSizeLimitBytes = 20 * 1024 * 1024;
    private const int MaxDiffLabelLength = 200;

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
    /// Gets or sets a unified diff file used to compute changed-line coverage.
    /// </summary>
    [CommandOption("diff-file", Description = "Unified diff file used to estimate changed-line coverage without local git history.")]
    public string? DiffFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether unified diff text should be read from stdin.
    /// </summary>
    [CommandOption("diff-stdin", Description = "Read unified diff text from stdin to estimate changed-line coverage.")]
    public bool DiffStdin { get; set; }

    /// <summary>
    /// Gets or sets an optional display label for the selected patch diff source.
    /// </summary>
    [CommandOption("diff-label", Description = "Display label for the selected patch diff source in reports.")]
    public string? DiffLabel { get; set; }

    /// <summary>
    /// Gets or sets the repository root used for Cobertura and diff path matching.
    /// </summary>
    [CommandOption("repository-root", Description = "Repository root used for Cobertura and diff path matching. Defaults to the git worktree root when available.")]
    public string? RepositoryRoot { get; set; }

    /// <summary>
    /// Gets or sets the minimum changed-line coverage percentage from 0 to 100.
    /// </summary>
    [CommandOption("min-patch-line", Description = "Minimum changed-line coverage percentage from 0 to 100. Requires one patch source: --diff-base, --diff-file, or --diff-stdin.")]
    public decimal? MinPatchLine { get; set; }

    /// <summary>
    /// Gets or sets the minimum changed-branch coverage percentage from 0 to 100.
    /// </summary>
    [CommandOption("min-patch-branch", Description = "Minimum changed-branch coverage percentage from 0 to 100. Requires one patch source: --diff-base, --diff-file, or --diff-stdin.")]
    public decimal? MinPatchBranch { get; set; }

    internal long ExternalDiffSizeLimitBytes { get; set; } = DefaultExternalDiffSizeLimitBytes;

    internal Func<bool>? IsInputRedirectedProvider { get; set; }

    internal Func<CancellationToken, Task<string>>? StdinTextProvider { get; set; }

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

    /// <summary>
    /// Executes the coverage gate with an explicit cancellation token.
    /// </summary>
    /// <param name="console">CliFx console used for command output.</param>
    /// <param name="cancellationToken">Cancellation token for evaluator and report IO.</param>
    /// <returns>A task that completes after report files are written and gate outcome is emitted.</returns>
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

        if (string.IsNullOrWhiteSpace(CoveragePath))
        {
            throw new CommandException("ASCOV001 --coverage must point to a Cobertura XML file.");
        }

        var diffSourceCount = CountDiffSources();
        if ((MinPatchLine.HasValue || MinPatchBranch.HasValue) && diffSourceCount == 0)
        {
            throw new CommandException("ASCOV011 patch coverage thresholds require exactly one patch diff source: --diff-base, --diff-file, or --diff-stdin.");
        }

        if (diffSourceCount > 1)
        {
            throw new CommandException("ASCOV012 patch coverage accepts exactly one patch diff source. Use only one of --diff-base, --diff-file, or --diff-stdin.");
        }

        if (diffSourceCount == 0 && !string.IsNullOrWhiteSpace(DiffLabel))
        {
            throw new CommandException("ASCOV016 --diff-label requires a patch diff source.");
        }

        var coveragePath = GetFullPathOrThrow(
            CoveragePath.Trim(),
            "ASCOV001 --coverage must point to a Cobertura XML file.");
        var outputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
            ? Path.GetDirectoryName(coveragePath) ?? Directory.GetCurrentDirectory()
            : GetFullPathOrThrow(
                OutputDirectory,
                "ASCOV009 --output must point to a coverage report directory.");
        var patchCoverage = diffSourceCount == 0
            ? null
            : new CoveragePatchRequest(
                ResolveRepositoryRoot(),
                CreateDiffSource(),
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

    private static string GetFullPathOrThrow(string path, string diagnostic)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CommandException(diagnostic);
        }
    }

    private int CountDiffSources()
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(DiffBase))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(DiffFile))
        {
            count++;
        }

        if (DiffStdin)
        {
            count++;
        }

        return count;
    }

    private PatchDiffSource CreateDiffSource()
    {
        var label = NormalizeDiffLabel(DiffLabel);
        if (!string.IsNullOrWhiteSpace(DiffBase))
        {
            var diffBase = DiffBase.Trim();
            return PatchDiffSource.ForGitBase(diffBase, label ?? diffBase);
        }

        if (!string.IsNullOrWhiteSpace(DiffFile))
        {
            var path = GetFullPathOrThrow(
                DiffFile.Trim(),
                "ASCOV013 --diff-file must point to a readable unified diff file.");
            if (Directory.Exists(path))
            {
                throw new CommandException($"ASCOV013 --diff-file must point to a file, not a directory: {path}");
            }

            return PatchDiffSource.ForFile(path, label ?? path, ExternalDiffSizeLimitBytes);
        }

        if (StdinTextProvider is null && !(IsInputRedirectedProvider?.Invoke() ?? System.Console.IsInputRedirected))
        {
            throw new CommandException("ASCOV014 --diff-stdin was requested, but stdin is interactive. Pipe unified diff text into the command or use --diff-file.");
        }

        Func<CancellationToken, Task<string>> stdinProvider = StdinTextProvider
            ?? (cancellationToken => ReadStdinBoundedAsync(ExternalDiffSizeLimitBytes, cancellationToken));
        return PatchDiffSource.ForStdin(
            label ?? "stdin",
            ExternalDiffSizeLimitBytes,
            stdinProvider);
    }

    private static async Task<string> ReadStdinBoundedAsync(long maxBytes, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        long byteCount = 0;
        while (true)
        {
            var read = await System.Console.In.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }

            byteCount += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (byteCount > maxBytes)
            {
                throw new CommandException($"ASCOV013 --diff-stdin input is too large. Limit is {maxBytes} bytes.");
            }

            builder.Append(buffer, 0, read);
        }
    }

    private string ResolveRepositoryRoot()
    {
        if (!string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            var root = GetFullPathOrThrow(
                RepositoryRoot.Trim(),
                "ASCOV016 --repository-root must point to an existing repository directory.");
            if (!Directory.Exists(root))
            {
                throw new CommandException($"ASCOV016 --repository-root must point to an existing repository directory: {root}");
            }

            return root;
        }

        return GitRepositoryRootResolver.FindRepositoryRoot(Directory.GetCurrentDirectory());
    }

    private static string? NormalizeDiffLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Where(ch => !char.IsControl(ch)))
        {
            builder.Append(ch);
        }

        var label = builder.ToString().Trim();
        if (label.Length == 0 || label.Length > MaxDiffLabelLength)
        {
            throw new CommandException($"ASCOV016 --diff-label must be from 1 to {MaxDiffLabelLength} display characters after trimming control characters.");
        }

        return label;
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
/// Request for estimating coverage on lines changed by a patch diff source.
/// </summary>
/// <param name="RepositoryRoot">Repository root used for diff acquisition and path normalization.</param>
/// <param name="DiffSource">Patch diff source used to acquire unified diff text.</param>
/// <param name="MinPatchLinePercent">Optional changed-line threshold.</param>
/// <param name="MinPatchBranchPercent">Optional changed-branch threshold.</param>
internal sealed partial record CoveragePatchRequest(
    string RepositoryRoot,
    PatchDiffSource DiffSource,
    decimal? MinPatchLinePercent,
    decimal? MinPatchBranchPercent = null);

internal sealed partial record CoveragePatchRequest
{
    public CoveragePatchRequest(
        string RepositoryRoot,
        string DiffBase,
        decimal? MinPatchLinePercent,
        Func<CancellationToken, Task<string>>? DiffProvider = null,
        decimal? MinPatchBranchPercent = null)
        : this(
            RepositoryRoot,
            PatchDiffSource.ForGitBase(DiffBase, DiffBase, DiffProvider),
            MinPatchLinePercent,
            MinPatchBranchPercent)
    {
    }
}

internal enum PatchDiffSourceKind
{
    GitBase,
    File,
    Stdin,
}

internal sealed record PatchDiffSource(
    PatchDiffSourceKind Kind,
    string Label,
    string? DiffBase,
    string? Path,
    bool IsExternalArtifact,
    Func<string, CancellationToken, Task<PatchDiffArtifact>> ReadAsync)
{
    public static PatchDiffSource ForGitBase(
        string diffBase,
        string label,
        Func<CancellationToken, Task<string>>? diffProvider = null) =>
        new(
            PatchDiffSourceKind.GitBase,
            label,
            diffBase,
            null,
            false,
            async (repositoryRoot, cancellationToken) =>
            {
                var text = diffProvider is null
                    ? await GitDiffReader.ReadDiffAsync(repositoryRoot, diffBase, cancellationToken)
                    : await diffProvider(cancellationToken);
                return PatchDiffArtifact.FromText(text);
            });

    public static PatchDiffSource ForFile(string path, string label, long maxBytes) =>
        new(
            PatchDiffSourceKind.File,
            label,
            null,
            path,
            true,
            async (_, cancellationToken) =>
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        throw new CommandException($"ASCOV013 --diff-file must point to a file, not a directory: {path}");
                    }

                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        throw new CommandException($"ASCOV013 --diff-file was not found: {path}");
                    }

                    if (info.Length > maxBytes)
                    {
                        throw new CommandException($"ASCOV013 --diff-file is too large. Limit is {maxBytes} bytes: {path}");
                    }

                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    return CreateArtifactFromBoundedBytes(bytes, maxBytes, $"--diff-file is too large. Limit is {maxBytes} bytes: {path}");
                }
                catch (CommandException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new CommandException($"ASCOV013 Failed to read --diff-file '{path}': {ex.Message}");
                }
            });

    public static PatchDiffSource ForStdin(
        string label,
        long maxBytes,
        Func<CancellationToken, Task<string>> stdinProvider) =>
        new(
            PatchDiffSourceKind.Stdin,
            label,
            null,
            null,
            true,
            async (_, cancellationToken) =>
            {
                var text = await stdinProvider(cancellationToken);
                var artifact = PatchDiffArtifact.FromText(text);
                if (artifact.Bytes > maxBytes)
                {
                    throw new CommandException($"ASCOV013 --diff-stdin input is too large. Limit is {maxBytes} bytes.");
                }

                return artifact;
            });

    internal static PatchDiffArtifact CreateArtifactFromBoundedBytes(
        byte[] bytes,
        long maxBytes,
        string diagnostic)
    {
        if (bytes.LongLength > maxBytes)
        {
            throw new CommandException($"ASCOV013 {diagnostic}");
        }

        return PatchDiffArtifact.FromBytes(bytes);
    }
}

internal sealed record PatchDiffArtifact(string Text, long Bytes, string Sha256, bool Empty)
{
    public static PatchDiffArtifact FromText(string text) => FromBytes(Encoding.UTF8.GetBytes(text));

    public static PatchDiffArtifact FromBytes(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new PatchDiffArtifact(text, bytes.Length, sha256, bytes.Length == 0);
    }
}

internal sealed record PatchDiffSourceReport(
    PatchDiffSourceKind Kind,
    string Label,
    string? DiffBase,
    string? Path,
    long Bytes,
    string Sha256,
    bool Empty,
    bool IsExternalArtifact);

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
/// <param name="PatchDiffSource">Optional patch diff source provenance.</param>
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
    PatchBranchCoverageMetric? PatchBranchCoverage = null,
    PatchDiffSourceReport? PatchDiffSource = null);

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
    string? DiffBase,
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
    string? DiffBase,
    int ChangedLines,
    int MeasurableBranches,
    int CoveredBranches,
    decimal Percent);

/// <summary>
/// Coverage estimates for changed lines and changed-line branch conditions.
/// </summary>
/// <param name="SourceReport">Patch diff source provenance captured during evaluation.</param>
/// <param name="LineCoverage">Changed-line coverage metric.</param>
/// <param name="BranchCoverage">Changed-branch coverage metric.</param>
internal sealed record PatchCoverageMetrics(
    PatchDiffSourceReport SourceReport,
    PatchLineCoverageMetric LineCoverage,
    PatchBranchCoverageMetric BranchCoverage);

internal enum PatchDiffParseStatus
{
    Empty,
    ValidWithAddedLines,
    ValidNoAddedLines,
    Malformed,
}

internal sealed record PatchDiffParseResult(
    IReadOnlyDictionary<string, IReadOnlySet<int>> ChangedLines,
    PatchDiffParseStatus Status);

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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await reader.ReadAsync())
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
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
                    patchCoverage?.BranchCoverage,
                    patchCoverage?.SourceReport);
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

    private struct PatchDiffParserState
    {
        public bool CurrentDiffStarted;
        public string? CurrentFile;
        public int? CurrentNewLine;
        public bool CurrentDiffHasOldFileMarker;
        public bool CurrentDiffHasNewFileMarker;
        public bool CurrentDiffHasHunk;
        public bool CurrentHunkActive;
        public int CurrentHunkOldLinesRemaining;
        public int CurrentHunkNewLinesRemaining;
        public bool PreviousLineWasHunkBodyLine;
        public bool CurrentDiffHasOldMode;
        public bool CurrentDiffHasNewMode;
        public bool CurrentDiffHasRenameFrom;
        public bool CurrentDiffHasRenameTo;
        public bool CurrentDiffHasCopyFrom;
        public bool CurrentDiffHasCopyTo;
        public bool CurrentDiffHasNewFileMode;
        public bool CurrentDiffHasDeletedFileMode;
        public bool CurrentDiffHasIndex;
        public bool CurrentDiffHasEmptyFileIndex;
        public bool CurrentDiffHasBinaryMarker;
        public bool CurrentDiffHasBinaryPatchMarker;
        public bool CurrentDiffHasBinaryPatchBody;
        public bool CurrentDiffHasBinaryPatchPayload;

        public static PatchDiffParserState StartedEntry() => new()
        {
            CurrentDiffStarted = true,
        };
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

        if (request.DiffSource.Kind == PatchDiffSourceKind.GitBase
            && string.IsNullOrWhiteSpace(request.DiffSource.DiffBase))
        {
            throw new CommandException("ASCOV007 --diff-base must not be blank.");
        }

        var artifact = await request.DiffSource.ReadAsync(request.RepositoryRoot, cancellationToken);
        return await EvaluateAsync(coveragePath, request, artifact, cancellationToken);
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
        return await EvaluateAsync(coveragePath, request, PatchDiffArtifact.FromText(diffText), cancellationToken);
    }

    private static async Task<PatchCoverageMetrics> EvaluateAsync(
        string coveragePath,
        CoveragePatchRequest request,
        PatchDiffArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parseResult = ParseChangedLinesDetailed(artifact.Text);
        if (request.DiffSource.IsExternalArtifact && parseResult.Status == PatchDiffParseStatus.Malformed)
        {
            throw new CommandException(
                $"ASCOV015 The patch diff from {GetDiffSourceDisplay(request.DiffSource)} could not be parsed as unified diff text. Download a unified diff artifact or use --diff-base for local git history.");
        }

        var changedLines = parseResult.ChangedLines;
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
            new PatchDiffSourceReport(
                request.DiffSource.Kind,
                request.DiffSource.Label,
                request.DiffSource.DiffBase,
                request.DiffSource.Path,
                artifact.Bytes,
                artifact.Sha256,
                artifact.Empty,
                request.DiffSource.IsExternalArtifact),
            new PatchLineCoverageMetric(
                request.DiffSource.DiffBase,
                changedLineCount,
                measurableLineCount,
                coveredLineCount,
                linePercent),
            new PatchBranchCoverageMetric(
                request.DiffSource.DiffBase,
                changedLineCount,
                measurableBranchCount,
                coveredBranchCount,
                branchPercent));
    }

    internal static IReadOnlyDictionary<string, IReadOnlySet<int>> ParseChangedLines(string diffText)
        => ParseChangedLinesDetailed(diffText).ChangedLines;

    internal static PatchDiffParseResult ParseChangedLinesDetailed(string diffText)
    {
        var changedLines = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var state = new PatchDiffParserState();
        var sawDiffHeader = false;
        var sawFileMarker = false;
        var sawHunk = false;

        foreach (var line in SplitLines(diffText))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (state.CurrentDiffStarted && (!IsCurrentHunkComplete() || !IsCompleteDiffEntry()))
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                sawDiffHeader = true;
                ResetCurrentDiffEntry();
                continue;
            }

            if (state.CurrentHunkActive && !IsCurrentHunkComplete())
            {
                if (line.Equals("\\ No newline at end of file", StringComparison.Ordinal))
                {
                    if (!state.PreviousLineWasHunkBodyLine)
                    {
                        return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                    }

                    state.PreviousLineWasHunkBodyLine = false;
                    continue;
                }

                if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    state.CurrentHunkOldLinesRemaining--;
                    if (state.CurrentHunkOldLinesRemaining < 0)
                    {
                        return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                    }

                    state.PreviousLineWasHunkBodyLine = true;
                    continue;
                }

                if (state.CurrentFile is null || state.CurrentNewLine is null)
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    state.CurrentHunkNewLinesRemaining--;
                    if (state.CurrentHunkNewLinesRemaining < 0)
                    {
                        return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                    }

                    AddChangedLine(changedLines, state.CurrentFile, state.CurrentNewLine.Value);
                    state.CurrentNewLine++;
                    state.PreviousLineWasHunkBodyLine = true;
                    continue;
                }

                if (line.StartsWith(" ", StringComparison.Ordinal))
                {
                    state.CurrentHunkOldLinesRemaining--;
                    state.CurrentHunkNewLinesRemaining--;
                    if (state.CurrentHunkOldLinesRemaining < 0 || state.CurrentHunkNewLinesRemaining < 0)
                    {
                        return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                    }

                    state.CurrentNewLine++;
                    state.PreviousLineWasHunkBodyLine = true;
                    continue;
                }

                return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (HasPatchBodyStarted())
                {
                    if (!IsCurrentHunkComplete() || !IsCompleteDiffEntry())
                    {
                        return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                    }

                    ResetCurrentDiffEntry();
                }

                state.CurrentDiffStarted = true;
                sawFileMarker = true;
                if (state.CurrentDiffHasOldFileMarker || state.CurrentDiffHasNewFileMarker)
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                state.CurrentDiffHasOldFileMarker = true;
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (HasPatchBodyStarted())
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                if (!state.CurrentDiffHasOldFileMarker || state.CurrentDiffHasNewFileMarker)
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                state.CurrentDiffStarted = true;
                sawFileMarker = true;
                state.CurrentDiffHasNewFileMarker = true;
                state.CurrentFile = NormalizeDiffPath(line["+++ ".Length..]);
                state.CurrentNewLine = null;
                continue;
            }

            if (state.CurrentDiffStarted)
            {
                if (HasPatchBodyStarted() && IsDiffMetadataLine(line))
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                if (line.StartsWith("old mode ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasOldMode = true;
                    continue;
                }

                if (line.StartsWith("new mode ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasNewMode = true;
                    continue;
                }

                if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasRenameFrom = true;
                    continue;
                }

                if (line.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasRenameTo = true;
                    continue;
                }

                if (line.StartsWith("copy from ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasCopyFrom = true;
                    continue;
                }

                if (line.StartsWith("copy to ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasCopyTo = true;
                    continue;
                }

                if (line.StartsWith("similarity index ", StringComparison.Ordinal)
                    || line.StartsWith("dissimilarity index ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("new file mode ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasNewFileMode = true;
                    continue;
                }

                if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasDeletedFileMode = true;
                    continue;
                }

                if (line.StartsWith("index ", StringComparison.Ordinal))
                {
                    state.CurrentDiffHasIndex = true;
                    state.CurrentDiffHasEmptyFileIndex = IsEmptyFileIndexLine(line);
                    continue;
                }

                if (line.StartsWith("Binary files ", StringComparison.Ordinal))
                {
                    if (HasPatchBodyStarted())
                    {
                        return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                    }

                    state.CurrentDiffHasBinaryMarker = true;
                    continue;
                }

                if (line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    if (HasPatchBodyStarted())
                    {
                        return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                    }

                    state.CurrentDiffHasBinaryPatchMarker = true;
                    continue;
                }

                if (state.CurrentDiffHasBinaryPatchMarker
                    && (line.StartsWith("literal ", StringComparison.Ordinal)
                        || line.StartsWith("delta ", StringComparison.Ordinal)))
                {
                    state.CurrentDiffHasBinaryPatchBody = true;
                    continue;
                }

                if (state.CurrentDiffHasBinaryPatchBody && line.Length > 0)
                {
                    state.CurrentDiffHasBinaryPatchPayload = true;
                    continue;
                }
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (!state.CurrentDiffHasOldFileMarker
                    || !state.CurrentDiffHasNewFileMarker
                    || !IsCurrentHunkComplete()
                    || !TryReadHunkRange(line, out var start, out var oldCount, out var newCount))
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                sawHunk = true;
                state.CurrentDiffHasHunk = true;
                state.CurrentHunkActive = true;
                state.CurrentHunkOldLinesRemaining = oldCount;
                state.CurrentHunkNewLinesRemaining = newCount;
                state.CurrentNewLine = state.CurrentFile is null ? null : start;
                state.PreviousLineWasHunkBodyLine = false;
                continue;
            }

            if (line.StartsWith("\\ ", StringComparison.Ordinal))
            {
                if (!state.CurrentHunkActive
                    || !state.PreviousLineWasHunkBodyLine
                    || !line.Equals("\\ No newline at end of file", StringComparison.Ordinal))
                {
                    return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
                }

                state.PreviousLineWasHunkBodyLine = false;
                continue;
            }

            if (line.StartsWith("-", StringComparison.Ordinal)
                || line.StartsWith("+", StringComparison.Ordinal)
                || line.StartsWith(" ", StringComparison.Ordinal))
            {
                return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
            }

            if (state.CurrentDiffHasBinaryPatchMarker && line.Length == 0)
            {
                continue;
            }

            if (state.CurrentFile is null || state.CurrentNewLine is null)
            {
                if (line.Length == 0 && !state.CurrentDiffStarted)
                {
                    continue;
                }

                return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
            }

            return new PatchDiffParseResult(ToReadOnly(changedLines), PatchDiffParseStatus.Malformed);
        }

        var readOnly = ToReadOnly(changedLines);
        if (diffText.Length == 0)
        {
            return new PatchDiffParseResult(readOnly, PatchDiffParseStatus.Empty);
        }

        if (!sawDiffHeader && !sawFileMarker && !sawHunk)
        {
            return new PatchDiffParseResult(readOnly, PatchDiffParseStatus.Malformed);
        }

        if (state.CurrentDiffStarted && (!IsCurrentHunkComplete() || !IsCompleteDiffEntry()))
        {
            return new PatchDiffParseResult(readOnly, PatchDiffParseStatus.Malformed);
        }

        var status = readOnly.Count == 0
            ? PatchDiffParseStatus.ValidNoAddedLines
            : PatchDiffParseStatus.ValidWithAddedLines;
        return new PatchDiffParseResult(readOnly, status);

        bool IsCompleteDiffEntry()
            => state.CurrentDiffHasHunk
                || state.CurrentDiffHasBinaryMarker
                || (state.CurrentDiffHasBinaryPatchMarker && state.CurrentDiffHasBinaryPatchBody && state.CurrentDiffHasBinaryPatchPayload)
                || ((state.CurrentDiffHasNewFileMode || state.CurrentDiffHasDeletedFileMode) && state.CurrentDiffHasEmptyFileIndex)
                || (!HasContentChangeIndicator()
                    && ((state.CurrentDiffHasOldMode && state.CurrentDiffHasNewMode)
                        || (state.CurrentDiffHasRenameFrom && state.CurrentDiffHasRenameTo)
                        || (state.CurrentDiffHasCopyFrom && state.CurrentDiffHasCopyTo)));

        bool HasContentChangeIndicator()
            => state.CurrentDiffHasIndex || state.CurrentDiffHasOldFileMarker || state.CurrentDiffHasNewFileMarker;

        bool HasPatchBodyStarted()
            => state.CurrentDiffHasHunk || state.CurrentDiffHasBinaryMarker || state.CurrentDiffHasBinaryPatchMarker;

        static bool IsDiffMetadataLine(string line)
            => line.StartsWith("old mode ", StringComparison.Ordinal)
                || line.StartsWith("new mode ", StringComparison.Ordinal)
                || line.StartsWith("rename from ", StringComparison.Ordinal)
                || line.StartsWith("rename to ", StringComparison.Ordinal)
                || line.StartsWith("copy from ", StringComparison.Ordinal)
                || line.StartsWith("copy to ", StringComparison.Ordinal)
                || line.StartsWith("similarity index ", StringComparison.Ordinal)
                || line.StartsWith("dissimilarity index ", StringComparison.Ordinal)
                || line.StartsWith("new file mode ", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal);

        bool IsCurrentHunkComplete()
            => !state.CurrentHunkActive || (state.CurrentHunkOldLinesRemaining == 0 && state.CurrentHunkNewLinesRemaining == 0);

        void ResetCurrentDiffEntry()
        {
            state = PatchDiffParserState.StartedEntry();
        }
    }

    private static bool IsEmptyFileIndexLine(string line)
    {
        const string emptyBlobSha = "e69de29";
        var text = line["index ".Length..].Trim();
        var rangeEnd = text.IndexOf(' ', StringComparison.Ordinal);
        var range = rangeEnd < 0 ? text : text[..rangeEnd];
        var separator = range.IndexOf("..", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var before = range[..separator];
        var after = range[(separator + 2)..];
        return (IsAllZeroAbbreviatedSha(before) && after.StartsWith(emptyBlobSha, StringComparison.Ordinal))
            || (before.StartsWith(emptyBlobSha, StringComparison.Ordinal) && IsAllZeroAbbreviatedSha(after));
    }

    private static bool IsAllZeroAbbreviatedSha(string value)
        => value.Length >= 7 && value.All(ch => ch == '0');

    private static string GetDiffSourceDisplay(PatchDiffSource source) => source.Kind switch
    {
        PatchDiffSourceKind.File => $"--diff-file '{source.Path}'",
        PatchDiffSourceKind.Stdin => "--diff-stdin",
        _ => $"--diff-base '{source.DiffBase}'",
    };

    private static IReadOnlyDictionary<string, IReadOnlySet<int>> ToReadOnly(
        Dictionary<string, HashSet<int>> changedLines) =>
        changedLines.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<int>)pair.Value,
            StringComparer.Ordinal);

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

    private static bool TryReadHunkRange(
        string header,
        out int newStart,
        out int oldCount,
        out int newCount)
    {
        newStart = 0;
        oldCount = 0;
        newCount = 0;
        if (!header.StartsWith("@@ -", StringComparison.Ordinal))
        {
            return false;
        }

        var oldRangeStart = "@@ -".Length;
        var oldRangeEnd = header.IndexOf(' ', oldRangeStart);
        if (oldRangeEnd <= oldRangeStart
            || !TryReadHunkRangePart(header.AsSpan(oldRangeStart, oldRangeEnd - oldRangeStart), out _, out oldCount))
        {
            return false;
        }

        var plusIndex = header.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex < 0 || plusIndex <= oldRangeEnd)
        {
            return false;
        }

        var newRangeEnd = header.IndexOf(' ', plusIndex);
        if (newRangeEnd <= plusIndex + 1)
        {
            return false;
        }

        if (!TryReadHunkRangePart(
            header.AsSpan(plusIndex + 1, newRangeEnd - plusIndex - 1),
            out newStart,
            out newCount))
        {
            return false;
        }

        return oldCount > 0 || newCount > 0;
    }

    private static bool TryReadHunkRangePart(ReadOnlySpan<char> range, out int start, out int count)
    {
        start = 0;
        count = 1;
        var commaIndex = range.IndexOf(',');
        if (commaIndex < 0)
        {
            return int.TryParse(range, NumberStyles.None, CultureInfo.InvariantCulture, out start);
        }

        return int.TryParse(range[..commaIndex], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && int.TryParse(range[(commaIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out count);
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

internal static class GitRepositoryRootResolver
{
    public static string FindRepositoryRoot(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Join(current, ".git")) || File.Exists(Path.Join(current, ".git")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return Path.GetFullPath(startDirectory);
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
                patchDiffSource = ToJson(result.PatchDiffSource),
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
        builder.AppendLine($"Cobertura: {EscapeMarkdownCell(result.CoveragePath)}");
        if (result.PatchDiffSource is { } source)
        {
            builder.AppendLine($"Patch source: {EscapeMarkdownCell(GetKindText(source.Kind))}");
            builder.AppendLine($"Patch label: {EscapeMarkdownCell(source.Label)}");
            if (!string.IsNullOrWhiteSpace(source.DiffBase))
            {
                builder.AppendLine($"Patch diff base: {EscapeMarkdownCell(source.DiffBase)}");
            }

            if (!string.IsNullOrWhiteSpace(source.Path))
            {
                builder.AppendLine($"Patch diff file: {EscapeMarkdownCell(source.Path)}");
            }

            builder.AppendLine($"Patch diff bytes: {source.Bytes}");
            builder.AppendLine($"Patch diff SHA-256: {EscapeMarkdownCell(source.Sha256)}");
            builder.AppendLine($"Patch diff empty: {source.Empty.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        }

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

    private static object? ToJson(PatchDiffSourceReport? source)
    {
        if (source is null)
        {
            return null;
        }

        return new
        {
            kind = GetKindText(source.Kind),
            label = source.Label,
            strictness = source.IsExternalArtifact ? "fail-closed" : "local-git",
            diffBase = source.DiffBase,
            path = source.Path,
            bytes = source.Bytes,
            sha256 = source.Sha256,
            empty = source.Empty,
        };
    }

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

    private static string GetKindText(PatchDiffSourceKind kind) => kind switch
    {
        PatchDiffSourceKind.GitBase => "git-base",
        PatchDiffSourceKind.File => "file",
        PatchDiffSourceKind.Stdin => "stdin",
        _ => kind.ToString(),
    };

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

        var fullOutputRaw = Path.GetFullPath(outputDirectory);
        if (File.Exists(fullOutputRaw))
        {
            throw new CommandException($"ASCOV009 --output must point to a coverage report directory: {fullOutputRaw}");
        }

        var fullOutput = NormalizeMacPrivateVar(fullOutputRaw);
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
