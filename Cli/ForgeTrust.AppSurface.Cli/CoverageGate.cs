using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
        await console.Output.WriteLineAsync(
            FormattableString.Invariant(
                $"Coverage gate {status}: lines {result.LineCoverage.Percent:0.00}% >= {request.MinLinePercent:0.##}%, branches {result.BranchCoverage.Percent:0.00}% >= {request.MinBranchPercent:0.##}%."));
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

        var coveragePath = Path.GetFullPath(CoveragePath);
        var outputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
            ? Path.GetDirectoryName(coveragePath) ?? Directory.GetCurrentDirectory()
            : Path.GetFullPath(OutputDirectory);

        return new CoverageGateRequest(
            coveragePath,
            outputDirectory,
            MinLine,
            MinBranch,
            GithubSummary && !NoGithubSummary,
            Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"));
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
internal sealed record CoverageGateRequest(
    string CoveragePath,
    string OutputDirectory,
    decimal MinLinePercent,
    decimal MinBranchPercent,
    bool WriteGithubSummary,
    string? GithubStepSummaryPath);

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
internal sealed record CoverageGateResult(
    string CoveragePath,
    CoverageMetric LineCoverage,
    CoverageMetric BranchCoverage,
    decimal MinLinePercent,
    decimal MinBranchPercent,
    bool Passed,
    string JsonReportPath,
    string MarkdownReportPath);

/// <summary>
/// One Cobertura coverage metric expressed as optional covered/valid counts and a percentage.
/// </summary>
/// <param name="Covered">Covered line or branch count, when Cobertura provides it.</param>
/// <param name="Valid">Valid line or branch count, when Cobertura provides it.</param>
/// <param name="Percent">Coverage percentage from 0 to 100.</param>
internal sealed record CoverageMetric(int? Covered, int? Valid, decimal Percent);

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
                var passed = lineCoverage.Percent >= request.MinLinePercent && branchCoverage.Percent >= request.MinBranchPercent;
                return new CoverageGateResult(
                    request.CoveragePath,
                    lineCoverage,
                    branchCoverage,
                    request.MinLinePercent,
                    request.MinBranchPercent,
                    passed,
                    Path.Join(request.OutputDirectory, "coverage-gate.json"),
                    Path.Join(request.OutputDirectory, "coverage-gate.md"));
            }
        }
        catch (XmlException ex)
        {
            throw new CommandException($"ASCOV006 Failed to parse Cobertura XML '{request.CoveragePath}': {ex.Message}");
        }

        throw new CommandException($"ASCOV006 Cobertura file does not contain a <coverage> root element: {request.CoveragePath}");
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
                },
                line = ToJson(result.LineCoverage),
                branch = ToJson(result.BranchCoverage),
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
        return builder.ToString();
    }

    private static object ToJson(CoverageMetric metric) => new
    {
        covered = metric.Covered,
        valid = metric.Valid,
        percent = metric.Percent,
    };

    private static void AppendMetric(StringBuilder builder, string label, CoverageMetric metric, decimal threshold)
    {
        var result = metric.Percent >= threshold ? "pass" : "fail";
        var coverage = metric.Covered.HasValue && metric.Valid.HasValue
            ? FormattableString.Invariant($"{metric.Percent:0.00}% ({metric.Covered}/{metric.Valid})")
            : FormattableString.Invariant($"{metric.Percent:0.00}% (count unavailable)");
        builder.AppendLine(FormattableString.Invariant($"| {label} | {coverage} | {threshold:0.##}% | {result} |"));
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
        foreach (var ch in value)
        {
            if (!char.IsControl(ch) || ch is '\r' or '\n' or '\t')
            {
                builder.Append(ch);
            }
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
