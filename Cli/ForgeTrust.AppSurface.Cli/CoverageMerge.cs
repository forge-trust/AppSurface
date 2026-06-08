using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Merges existing Cobertura shards into AppSurface coverage artifacts.
/// </summary>
/// <remarks>
/// Use this command when another workflow already produced Cobertura files, such as a GitHub Actions
/// matrix job that downloads shard artifacts into one fan-in directory. Repositories that want
/// AppSurface to discover test projects and run Coverlet should use <c>coverage run</c> instead.
/// </remarks>
[Command("coverage merge", Description = "Merge existing Cobertura shards into local AppSurface coverage artifacts.")]
internal sealed partial class CoverageMergeCommand : ICommand
{
    private readonly CoverageMergeWorkflow _workflow;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageMergeCommand"/> class.
    /// </summary>
    /// <param name="workflow">Coverage merge workflow.</param>
    public CoverageMergeCommand(CoverageMergeWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    /// <summary>
    /// Gets or sets the directory that contains Cobertura shard files named <c>coverage.cobertura.xml</c>.
    /// </summary>
    [CommandOption("source", Description = "Required directory containing coverage.cobertura.xml shard files to merge.")]
    public string? SourceDirectory { get; set; }

    /// <summary>
    /// Gets or sets the merged coverage output directory.
    /// </summary>
    [CommandOption("output", Description = "Coverage output directory. Defaults to TestResults/coverage-merged.")]
    public string OutputDirectory { get; set; } = Path.Join("TestResults", "coverage-merged");

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await ExecuteAsync(console, console.RegisterCancellationHandler());
    }

    /// <summary>
    /// Executes the coverage merge with an explicit cancellation token.
    /// </summary>
    /// <param name="console">Console used for user-visible output.</param>
    /// <param name="cancellationToken">Cancellation token for package and artifact IO.</param>
    /// <returns>A task that completes when the merge finishes.</returns>
    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        var request = new CoverageMergeRequest(SourceDirectory, OutputDirectory, Clean: true);
        await _workflow.MergeAsync(request, console, cancellationToken);
    }
}

/// <summary>
/// Request for merging existing Cobertura coverage shards.
/// </summary>
/// <param name="SourceDirectory">Directory recursively searched for shard files named <c>coverage.cobertura.xml</c>.</param>
/// <param name="OutputDirectory">Directory that receives merged AppSurface coverage artifacts.</param>
/// <param name="Clean">Whether existing AppSurface-owned merge artifacts should be cleaned first.</param>
internal sealed record CoverageMergeRequest(string? SourceDirectory, string OutputDirectory, bool Clean);

/// <summary>
/// Result of a public coverage merge.
/// </summary>
/// <param name="OutputDirectory">Absolute output directory.</param>
/// <param name="CoveragePath">Absolute merged Cobertura path.</param>
/// <param name="SelectedReports">Absolute selected input report paths.</param>
internal sealed record CoverageMergeResult(string OutputDirectory, string CoveragePath, IReadOnlyList<string> SelectedReports);

/// <summary>
/// Coordinates public coverage merge source discovery, package-owned ReportGenerator execution, and artifacts.
/// </summary>
internal sealed class CoverageMergeWorkflow
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    private readonly ICoverageRunReportGenerator _reportGenerator;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageMergeWorkflow"/> class.
    /// </summary>
    /// <param name="reportGenerator">Package-owned ReportGenerator wrapper.</param>
    /// <param name="timeProvider">Time provider used for timings.</param>
    public CoverageMergeWorkflow(ICoverageRunReportGenerator reportGenerator, TimeProvider timeProvider)
    {
        _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Merges existing Cobertura shard files into AppSurface coverage artifacts.
    /// </summary>
    /// <param name="request">Coverage merge request.</param>
    /// <param name="console">Console used for user-visible output.</param>
    /// <param name="cancellationToken">Cancellation token for package and artifact IO.</param>
    /// <returns>Coverage merge result.</returns>
    public async Task<CoverageMergeResult> MergeAsync(
        CoverageMergeRequest request,
        IConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(console);

        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        var sourceDirectory = ResolveSourceDirectory(request.SourceDirectory, currentDirectory);
        var outputDirectory = ResolveOutputDirectory(request.OutputDirectory, currentDirectory);
        CoverageMergeOutputGuard.Prepare(outputDirectory, sourceDirectory, request.Clean);
        var selectedReports = CoverageMergeSourceResolver.Resolve(sourceDirectory, outputDirectory);

        await PrintDiscoveryAsync(console, sourceDirectory, outputDirectory, selectedReports, currentDirectory);

        var totalStarted = _timeProvider.GetTimestamp();
        var stagingDirectory = Path.Join(outputDirectory, "reportgenerator-input");
        StageReports(sourceDirectory, selectedReports, stagingDirectory, cancellationToken);

        var mergeStarted = _timeProvider.GetTimestamp();
        var reportGeneratorDirectory = Path.Join(outputDirectory, "reportgenerator");
        Directory.CreateDirectory(reportGeneratorDirectory);
        var reportGlob = Path.Join(stagingDirectory, "**", "coverage.cobertura.xml");
        CoverageRunMergeResult merge;
        try
        {
            merge = await _reportGenerator.MergeAsync([reportGlob], reportGeneratorDirectory, cancellationToken);
        }
        catch (CommandException exception) when (exception.Message.Contains("ASCOV114", StringComparison.Ordinal))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV134",
                "ReportGenerator package dependency was not found.",
                exception.Message,
                "Restore or reinstall ForgeTrust.AppSurface.Cli so its package-owned dependencies are present.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }

        var mergeSeconds = ElapsedSeconds(mergeStarted);
        if (merge.ExitCode != 0 || !File.Exists(merge.CoberturaPath))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV135",
                "Coverage merge failed.",
                $"ReportGenerator exit code: {merge.ExitCode.ToString(CultureInfo.InvariantCulture)}.",
                "Inspect selected Cobertura shards and rerun coverage merge after fixing malformed inputs.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }

        var mergedCoveragePath = Path.Join(outputDirectory, "coverage.cobertura.xml");
        try
        {
            File.Copy(merge.CoberturaPath, mergedCoveragePath, overwrite: true);
            if (File.Exists(merge.SummaryPath))
            {
                File.Copy(merge.SummaryPath, Path.Join(outputDirectory, "reportgenerator-summary.txt"), overwrite: true);
            }

            await WriteSummaryAsync(outputDirectory, mergedCoveragePath, currentDirectory, console, cancellationToken);
            await WriteTimingsAsync(
                sourceDirectory,
                outputDirectory,
                mergedCoveragePath,
                selectedReports,
                mergeSeconds,
                ElapsedSeconds(totalStarted),
                merge.ExitCode,
                currentDirectory,
                cancellationToken);
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV137",
                "Coverage merge artifacts could not be written.",
                exception.Message,
                "Use a writable dedicated output directory and rerun coverage merge.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }

        await console.Output.WriteLineAsync($"Coverage artifacts: {FormatDisplayPath(outputDirectory, currentDirectory)}");
        await console.Output.WriteLineAsync($"Next: appsurface coverage gate --coverage {FormatDisplayPath(mergedCoveragePath, currentDirectory)} --min-line <percent> --min-branch <percent>");

        return new CoverageMergeResult(outputDirectory, mergedCoveragePath, selectedReports);
    }

    private static string ResolveSourceDirectory(string? requestedSource, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedSource))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV130",
                "--source is required.",
                "coverage merge needs a directory that contains coverage.cobertura.xml shards.",
                "Pass --source with the directory produced by your shard downloads.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }

        try
        {
            var source = Path.GetFullPath(requestedSource.Trim(), currentDirectory);
            if (!Directory.Exists(source))
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV130",
                    "Coverage merge source directory was not found.",
                    $"Path: {source}.",
                    "Pass --source with an existing directory that contains coverage.cobertura.xml shards.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
            }

            return source;
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV130",
                "Coverage merge source path is invalid.",
                exception.Message,
                "Pass --source with an existing directory that contains coverage.cobertura.xml shards.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }
    }

    private static string ResolveOutputDirectory(string outputDirectory, string currentDirectory)
    {
        try
        {
            return Path.GetFullPath(outputDirectory, currentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV138",
                "Coverage merge output path is invalid.",
                exception.Message,
                "Pass --output with a dedicated writable artifact directory such as TestResults/coverage-merged.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }
    }

    private static async Task PrintDiscoveryAsync(
        IConsole console,
        string sourceDirectory,
        string outputDirectory,
        IReadOnlyList<string> selectedReports,
        string currentDirectory)
    {
        await console.Output.WriteLineAsync("Coverage merge inputs");
        await console.Output.WriteLineAsync($"  Source: {FormatDisplayPath(sourceDirectory, currentDirectory)}");
        await console.Output.WriteLineAsync($"  Output: {FormatDisplayPath(outputDirectory, currentDirectory)}");
        await console.Output.WriteLineAsync($"Discovered {selectedReports.Count.ToString(CultureInfo.InvariantCulture)} Cobertura shard(s).");
        foreach (var report in selectedReports.Take(5))
        {
            await console.Output.WriteLineAsync($"  include {Path.GetRelativePath(sourceDirectory, report)}");
        }

        if (selectedReports.Count > 5)
        {
            await console.Output.WriteLineAsync($"  ... {selectedReports.Count - 5} more shard(s)");
        }
    }

    private static void StageReports(
        string sourceDirectory,
        IReadOnlyList<string> selectedReports,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            for (var index = 0; index < selectedReports.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var report = selectedReports[index];
                var relative = Path.GetRelativePath(sourceDirectory, report);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..12].ToLowerInvariant();
                var stagedDirectory = Path.Join(stagingDirectory, $"{(index + 1).ToString("D6", CultureInfo.InvariantCulture)}-{hash}");
                Directory.CreateDirectory(stagedDirectory);
                File.Copy(report, Path.Join(stagedDirectory, "coverage.cobertura.xml"), overwrite: true);
            }

            var stagedReports = Directory.EnumerateFiles(stagingDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories).Count();
            if (stagedReports != selectedReports.Count)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV139",
                    "Coverage merge staging did not preserve every selected shard.",
                    $"Selected {selectedReports.Count.ToString(CultureInfo.InvariantCulture)} shard(s) but staged {stagedReports.ToString(CultureInfo.InvariantCulture)}.",
                    "Clean the output directory or choose a new --output path, then rerun coverage merge.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
            }
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV137",
                "Coverage merge staging failed.",
                exception.Message,
                "Use a writable dedicated output directory and rerun coverage merge.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }
    }

    private static async Task WriteSummaryAsync(
        string outputDirectory,
        string coveragePath,
        string currentDirectory,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var (linePercent, branchPercent) = ReadCoberturaSummary(coveragePath, "ASCOV135");
        var summary = FormattableString.Invariant($"""
            Coverage merge summary
            Line coverage: {linePercent:0.00}%
            Branch coverage: {branchPercent:0.00}%
            Cobertura: {FormatDisplayPath(coveragePath, currentDirectory)}
            Timings: {FormatDisplayPath(Path.Join(outputDirectory, "timings.json"), currentDirectory)}
            """);

        await File.WriteAllTextAsync(Path.Join(outputDirectory, "summary.txt"), summary, cancellationToken);
        await console.Output.WriteLineAsync(summary);
    }

    private static (decimal LinePercent, decimal BranchPercent) ReadCoberturaSummary(string coveragePath, string diagnosticCode)
    {
        try
        {
            using var stream = File.OpenRead(coveragePath);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            var root = XDocument.Load(reader).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "coverage", StringComparison.Ordinal))
            {
                throw CoverageRunDiagnostics.Create(
                    diagnosticCode,
                    "Merged Cobertura file is malformed.",
                    $"Path: {coveragePath}.",
                    "Regenerate coverage and inspect ReportGenerator output.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
            }

            var linesCovered = ReadDecimal(root, "lines-covered");
            var linesValid = ReadDecimal(root, "lines-valid");
            var branchesCovered = ReadDecimal(root, "branches-covered");
            var branchesValid = ReadDecimal(root, "branches-valid");
            var linePercent = linesValid > 0 ? linesCovered * 100m / linesValid : ReadRate(root, "line-rate") * 100m;
            var branchPercent = branchesValid > 0 ? branchesCovered * 100m / branchesValid : ReadRate(root, "branch-rate") * 100m;
            return (linePercent, branchPercent);
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            throw CoverageRunDiagnostics.Create(
                diagnosticCode,
                "Cobertura file is malformed or unreadable.",
                exception.Message,
                "Regenerate coverage and inspect the selected Cobertura XML.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }
    }

    private static decimal ReadDecimal(XElement root, string name)
    {
        return decimal.TryParse(root.Attribute(name)?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private static decimal ReadRate(XElement root, string name)
    {
        return decimal.TryParse(root.Attribute(name)?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private static async Task WriteTimingsAsync(
        string sourceDirectory,
        string outputDirectory,
        string coveragePath,
        IReadOnlyList<string> selectedReports,
        long mergeSeconds,
        long totalSeconds,
        int mergeExitCode,
        string currentDirectory,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            schemaVersion = 1,
            command = "coverage merge",
            sourceDirectory = FormatDisplayPath(sourceDirectory, currentDirectory),
            outputDirectory = FormatDisplayPath(outputDirectory, currentDirectory),
            durations = new
            {
                coverageMergeSeconds = mergeSeconds,
                totalSeconds,
            },
            merge = new
            {
                exitCode = mergeExitCode,
            },
            artifacts = new
            {
                coverageFiles = selectedReports.Count,
                cobertura = FormatDisplayPath(coveragePath, currentDirectory),
            },
            reports = selectedReports
                .Select(report => FormatDisplayPath(report, currentDirectory))
                .ToArray(),
        };

        await File.WriteAllTextAsync(
            Path.Join(outputDirectory, "timings.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private long ElapsedSeconds(long started)
    {
        var elapsed = _timeProvider.GetElapsedTime(started);
        return Math.Max(0, (long)Math.Ceiling(elapsed.TotalSeconds));
    }

    private static string FormatDisplayPath(string path, string currentDirectory)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetFullPath(currentDirectory);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(full, current, comparison) || full.StartsWith(current + Path.DirectorySeparatorChar, comparison))
        {
            return Path.GetRelativePath(current, full);
        }

        return full;
    }
}

internal static class CoverageMergeSourceResolver
{
    public static IReadOnlyList<string> Resolve(string sourceDirectory, string outputDirectory)
    {
        try
        {
            var output = Path.GetFullPath(outputDirectory);
            var reports = Directory
                .EnumerateFiles(sourceDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Where(path => !IsSameOrChild(path, output))
                .Where(path => !IsInMergeStagingDirectory(sourceDirectory, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            if (reports.Length == 0)
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV131",
                    "No Cobertura shard files were found.",
                    $"Searched recursively under {sourceDirectory} for files named coverage.cobertura.xml.",
                    "Download or copy shard artifacts under --source, keeping the coverage.cobertura.xml file name.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
            }

            foreach (var report in reports)
            {
                ValidateCoberturaInput(report);
            }

            return reports;
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV130",
                "Coverage merge source could not be read.",
                exception.Message,
                "Use a readable source directory that contains coverage.cobertura.xml shards.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }
    }

    private static void ValidateCoberturaInput(string report)
    {
        try
        {
            using var stream = File.OpenRead(report);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            });
            var root = XDocument.Load(reader).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "coverage", StringComparison.Ordinal))
            {
                throw CoverageRunDiagnostics.Create(
                    "ASCOV132",
                    "Coverage merge input is not a Cobertura file.",
                    $"Path: {report}.",
                    "Keep only valid Cobertura XML files named coverage.cobertura.xml under --source.",
                    "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
            }
        }
        catch (CommandException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV132",
                "Coverage merge input is malformed or unreadable.",
                $"{report}: {exception.Message}",
                "Regenerate the shard or remove it before rerunning coverage merge.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedCandidate = Trim(CoverageMergePathSafety.ResolvePhysicalPath(candidate));
        var normalizedParent = Trim(CoverageMergePathSafety.ResolvePhysicalPath(parent));

        return string.Equals(normalizedCandidate, normalizedParent, comparison)
            || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsInMergeStagingDirectory(string sourceDirectory, string reportPath)
    {
        var relative = Path.GetRelativePath(sourceDirectory, reportPath);
        return relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "reportgenerator-input", StringComparison.Ordinal));
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
}

internal static class CoverageMergeOutputGuard
{
    private const string MarkerFileName = ".appsurface-coverage-output";

    public static void Prepare(string outputDirectory, string sourceDirectory, bool clean)
    {
        ValidateCore(outputDirectory, sourceDirectory);

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var marker = Path.Join(output, MarkerFileName);
        if (clean && File.Exists(marker))
        {
            DeleteKnownMergeOutput(output);
        }

        File.WriteAllText(marker, "AppSurface coverage output directory" + Environment.NewLine);
    }

    private static void ValidateCore(string outputDirectory, string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw UnsafeOutput("The output path was blank.");
        }

        string output;
        string source;
        try
        {
            output = Path.GetFullPath(outputDirectory);
            source = Path.GetFullPath(sourceDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV138",
                "Coverage merge path could not be normalized.",
                exception.Message,
                "Use ordinary source and output directory paths without invalid characters.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }

        if (File.Exists(output))
        {
            throw UnsafeOutput($"--output points to a file: {output}");
        }

        var comparison = GetPathComparison();
        var trimmedOutput = Trim(output);
        var root = Path.GetPathRoot(output);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(trimmedOutput, Trim(root), comparison))
        {
            throw UnsafeOutput("--output must not be a filesystem root.");
        }

        var current = Trim(Path.GetFullPath(Directory.GetCurrentDirectory()));
        if (string.Equals(trimmedOutput, current, comparison))
        {
            throw UnsafeOutput("--output must not be the current working directory.");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && string.Equals(trimmedOutput, Trim(home), comparison))
        {
            throw UnsafeOutput("--output must not be the user home directory.");
        }

        if (IsSameOrChild(output, source) || IsSameOrChild(source, output))
        {
            throw CoverageRunDiagnostics.Create(
                "ASCOV133",
                "Coverage merge source and output overlap.",
                "--source and --output must be separate directories.",
                "Download shards into one directory and write merged artifacts to a separate dedicated output directory.",
                "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
        }

        var marker = Path.Join(output, MarkerFileName);
        if (Directory.Exists(output))
        {
            var entries = Directory.EnumerateFileSystemEntries(output)
                .Where(path => !string.Equals(Path.GetFileName(path), MarkerFileName, StringComparison.Ordinal))
                .ToArray();
            if (entries.Length > 0 && !File.Exists(marker))
            {
                throw UnsafeOutput("--output already contains files and is not marked as AppSurface-owned.");
            }
        }
    }

    private static void DeleteKnownMergeOutput(string output)
    {
        foreach (var path in new[] { "coverage.cobertura.xml", "summary.txt", "timings.json", "reportgenerator-summary.txt" }
            .Select(file => Path.Join(output, file))
            .Where(File.Exists))
        {
            File.Delete(path);
        }

        foreach (var path in new[] { "reportgenerator", "reportgenerator-input" }
            .Select(directory => Path.Join(output, directory))
            .Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static CommandException UnsafeOutput(string cause)
    {
        return CoverageRunDiagnostics.Create(
            "ASCOV136",
            "Coverage merge output path is unsafe.",
            cause,
            "Use a dedicated artifact directory, for example TestResults/coverage-merged.",
            "Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-merge-diagnostics");
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var comparison = GetPathComparison();
        var normalizedCandidate = Trim(CoverageMergePathSafety.ResolvePhysicalPath(candidate));
        var normalizedParent = Trim(CoverageMergePathSafety.ResolvePhysicalPath(parent));

        return string.Equals(normalizedCandidate, normalizedParent, comparison)
            || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, comparison);
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}

internal static class CoverageMergePathSafety
{
    public static string ResolvePhysicalPath(string path)
        => ResolvePhysicalPath(path, depth: 0);

    private static string ResolvePhysicalPath(string path, int depth)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var current = root;

        var relative = fullPath[root.Length..];
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Join(current, segment);
            current = ResolveExistingPathOrSelf(next, depth);
        }

        return current;
    }

    private static string ResolveExistingPathOrSelf(string path, int depth)
    {
        FileSystemInfo? info = null;
        if (Directory.Exists(path))
        {
            info = new DirectoryInfo(path);
        }
        else if (File.Exists(path))
        {
            info = new FileInfo(path);
        }

        if (info is null)
        {
            return Path.GetFullPath(path);
        }

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        if (target is null || depth >= 16)
        {
            return Path.GetFullPath(path);
        }

        return ResolvePhysicalPath(target.FullName, depth + 1);
    }
}
