using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Writes best-effort slow-test diagnostic artifacts for <c>coverage run</c>.
/// </summary>
/// <remarks>
/// The writer consumes AppSurface-managed JUnit files only. Parser problems are preserved as
/// diagnostic warnings so slow-test reporting cannot change the coverage result.
/// <list type="bullet">
/// <item><description>Only the first managed JUnit artifact for a project is parsed; additional JUnit artifacts emit warnings.</description></item>
/// <item><description><c>WriteAsync</c> may write artifacts twice when aggregation timing changes during the initial write.</description></item>
/// <item><description>Legacy or externally managed test-result files are not consumed.</description></item>
/// <item><description>Missing files and parser failures are reported as warnings instead of failing coverage.</description></item>
/// </list>
/// </remarks>
internal static class CoverageRunSlowTestDiagnosticsWriter
{
    /// <summary>
    /// Schema version written to the diagnostics JSON artifact.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// File name for the human-readable slow-test diagnostics artifact.
    /// </summary>
    public const string MarkdownFileName = "slow-test-diagnostics.md";

    /// <summary>
    /// File name for the machine-readable slow-test diagnostics artifact.
    /// </summary>
    public const string JsonFileName = "slow-test-diagnostics.json";

    private const int MaxTopTests = 20;
    private const int MaxTopProjects = 20;
    private const int MaxWarnings = 100;

    /// <summary>
    /// Parses managed JUnit files and builds a diagnostic report.
    /// </summary>
    /// <param name="results">Project run results with managed test result artifact paths.</param>
    /// <param name="cancellationToken">Cancellation token for artifact reads.</param>
    /// <returns>Slow-test diagnostic report model.</returns>
    public static async Task<CoverageRunSlowTestDiagnosticsReport> CollectAsync(
        IReadOnlyList<CoverageProjectRunResult> results,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var projects = new List<CoverageRunSlowTestProject>();
        var testCases = new List<CoverageRunSlowTestCase>();
        foreach (var result in results.OrderBy(result => result.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var junitArtifacts = result.TestResults
                .Where(artifact => artifact.Format == CoverageRunTestResultFormat.Junit)
                .ToArray();
            if (junitArtifacts.Length > 1)
            {
                AddWarning(warnings, $"Project {result.Project.RelativePath} has multiple managed JUnit artifacts; using first.");
            }

            var junit = junitArtifacts.FirstOrDefault();
            var project = new CoverageRunSlowTestProject(
                result.Project.RelativePath,
                result.Project.IsExclusive,
                result.Seconds,
                result.ExitCode,
                junit?.Path,
                "pending",
                result.LogFile);

            if (string.IsNullOrWhiteSpace(project.JunitFile))
            {
                AddWarning(warnings, $"No managed JUnit file was requested for {project.Project}.");
                projects.Add(project with { ParserStatus = "notRequested" });
                continue;
            }

            var junitResult = await ReadJunitFileAsync(project, warnings, cancellationToken);
            testCases.AddRange(junitResult.TestCases);
            projects.Add(project with { ParserStatus = junitResult.ParserStatus });
        }

        if (projects.Count == 0)
        {
            AddWarning(warnings, "No project metadata was available for slow-test diagnostics.");
        }

        return new CoverageRunSlowTestDiagnosticsReport(
            SchemaVersion,
            DateTimeOffset.UtcNow,
            MetadataComplete: projects.Count > 0 && projects.All(project => project.ParserStatus == "parsed") && warnings.Count == 0,
            projects.Count(project => project.JunitFile is not null && File.Exists(project.JunitFile)),
            projects,
            testCases
                .OrderByDescending(test => test.Seconds ?? 0)
                .ThenBy(test => test.ClassName, StringComparer.Ordinal)
                .ThenBy(test => test.Name, StringComparer.Ordinal)
                .ToArray(),
            warnings.Take(MaxWarnings).ToArray());
    }

    /// <summary>
    /// Writes diagnostic artifacts with measured aggregation overhead.
    /// </summary>
    /// <param name="outputDirectory">Coverage output directory.</param>
    /// <param name="report">Report model returned by <see cref="CollectAsync"/>.</param>
    /// <param name="getAggregationSeconds">Reads elapsed diagnostic aggregation seconds.</param>
    /// <param name="calculateAggregationPercent">Calculates aggregation overhead as a percent of runner time.</param>
    /// <param name="cancellationToken">Cancellation token for artifact writes.</param>
    /// <returns>Written artifact paths and high-level metadata.</returns>
    /// <remarks>
    /// <c>WriteAsync</c> may call <c>WriteArtifactsAsync</c> twice when <c>aggregationSeconds</c> differs from
    /// <c>finalAggregationSeconds</c>. The single re-write includes the first file-write cost in reported aggregation
    /// overhead; later timing drift is not captured.
    /// </remarks>
    public static async Task<CoverageRunSlowTestDiagnosticsRun> WriteAsync(
        string outputDirectory,
        CoverageRunSlowTestDiagnosticsReport report,
        Func<long> getAggregationSeconds,
        Func<long, decimal> calculateAggregationPercent,
        CancellationToken cancellationToken)
        => await WriteAsync(
            outputDirectory,
            outputDirectory,
            report,
            getAggregationSeconds,
            calculateAggregationPercent,
            cancellationToken);

    /// <summary>
    /// Writes diagnostics to a private staging directory while recording canonical artifact paths in their contents.
    /// </summary>
    /// <param name="stagingDirectory">Private directory that receives the uncommitted files.</param>
    /// <param name="artifactDirectory">Canonical directory represented in returned and embedded artifact paths.</param>
    /// <param name="report">Report model returned by <see cref="CollectAsync"/>.</param>
    /// <param name="getAggregationSeconds">Reads elapsed diagnostic aggregation seconds.</param>
    /// <param name="calculateAggregationPercent">Calculates aggregation overhead as a percent of runner time.</param>
    /// <param name="cancellationToken">Cancellation token for artifact writes.</param>
    /// <returns>Canonical artifact paths and high-level metadata after staging completes.</returns>
    public static async Task<CoverageRunSlowTestDiagnosticsRun> WriteAsync(
        string stagingDirectory,
        string artifactDirectory,
        CoverageRunSlowTestDiagnosticsReport report,
        Func<long> getAggregationSeconds,
        Func<long, decimal> calculateAggregationPercent,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var stagedMarkdownPath = Path.Join(stagingDirectory, MarkdownFileName);
        var stagedJsonPath = Path.Join(stagingDirectory, JsonFileName);
        var markdownPath = Path.Join(artifactDirectory, MarkdownFileName);
        var jsonPath = Path.Join(artifactDirectory, JsonFileName);
        var aggregationSeconds = getAggregationSeconds();
        await WriteArtifactsAsync(
            report,
            stagedMarkdownPath,
            stagedJsonPath,
            markdownPath,
            jsonPath,
            aggregationSeconds,
            calculateAggregationPercent(aggregationSeconds),
            cancellationToken);

        var finalAggregationSeconds = getAggregationSeconds();
        if (finalAggregationSeconds != aggregationSeconds)
        {
            aggregationSeconds = finalAggregationSeconds;
            await WriteArtifactsAsync(
                report,
                stagedMarkdownPath,
                stagedJsonPath,
                markdownPath,
                jsonPath,
                aggregationSeconds,
                calculateAggregationPercent(aggregationSeconds),
                cancellationToken);
        }

        return new CoverageRunSlowTestDiagnosticsRun(
            markdownPath,
            jsonPath,
            aggregationSeconds,
            calculateAggregationPercent(aggregationSeconds),
            report.Warnings.Count,
            report.MetadataComplete,
            report.Projects
                .Where(project => !string.IsNullOrWhiteSpace(project.JunitFile))
                .ToDictionary(project => project.JunitFile!, project => project.ParserStatus, StringComparer.Ordinal));
    }

    private static async Task WriteArtifactsAsync(
        CoverageRunSlowTestDiagnosticsReport report,
        string stagedMarkdownPath,
        string stagedJsonPath,
        string markdownPath,
        string jsonPath,
        long aggregationSeconds,
        decimal aggregationPercent,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            schemaVersion = report.SchemaVersion,
            generatedAtUtc = report.GeneratedAtUtc,
            metadataComplete = report.MetadataComplete,
            overhead = new
            {
                aggregationSeconds,
                aggregationPercent,
            },
            artifacts = new
            {
                markdown = markdownPath,
                json = jsonPath,
            },
            totals = new
            {
                projects = report.Projects.Count,
                junitFiles = report.JunitFileCount,
                testCases = report.TestCases.Count,
                failedTestCases = report.TestCases.Count(test => test.Status == "failed" || test.Status == "error"),
                skippedTestCases = report.TestCases.Count(test => test.Status == "skipped"),
                warnings = report.Warnings.Count,
            },
            topProjects = report.Projects
                .OrderByDescending(project => project.Seconds)
                .ThenBy(project => project.Project, StringComparer.Ordinal)
                .Take(MaxTopProjects),
            topTestCases = report.TestCases.Take(MaxTopTests),
            warnings = report.Warnings,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(stagedJsonPath, json + Environment.NewLine, cancellationToken);
        await File.WriteAllTextAsync(
            stagedMarkdownPath,
            RenderMarkdown(report, markdownPath, jsonPath, aggregationSeconds, aggregationPercent),
            cancellationToken);
    }

    private static async Task<CoverageRunJunitReadResult> ReadJunitFileAsync(
        CoverageRunSlowTestProject project,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(project.JunitFile!);
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(stream, settings);
            var testCases = await ReadTestCasesAsync(reader, project, warnings, cancellationToken);
            return new CoverageRunJunitReadResult(testCases, "parsed");
        }
        catch (FileNotFoundException)
        {
            AddWarning(warnings, $"JUnit file was not created: {project.JunitFile}");
            return new CoverageRunJunitReadResult([], "missing");
        }
        catch (DirectoryNotFoundException)
        {
            AddWarning(warnings, $"JUnit file was not created: {project.JunitFile}");
            return new CoverageRunJunitReadResult([], "missing");
        }
        catch (XmlException ex)
        {
            AddWarning(warnings, $"Failed to parse JUnit XML '{project.JunitFile}': {ex.Message}");
            return new CoverageRunJunitReadResult([], "parseFailed");
        }
        catch (IOException ex)
        {
            AddWarning(warnings, $"Failed to read JUnit XML '{project.JunitFile}': {ex.Message}");
            return new CoverageRunJunitReadResult([], "readFailed");
        }
        catch (UnauthorizedAccessException ex)
        {
            AddWarning(warnings, $"Failed to access JUnit XML '{project.JunitFile}': {ex.Message}");
            return new CoverageRunJunitReadResult([], "readFailed");
        }
    }

    private static async Task<IReadOnlyList<CoverageRunSlowTestCase>> ReadTestCasesAsync(
        XmlReader reader,
        CoverageRunSlowTestProject project,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var tests = new List<CoverageRunSlowTestCase>();
        TestCaseBuilder? current = null;
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "testcase")
            {
                current = CreateBuilder(reader, project, warnings);
                if (reader.IsEmptyElement)
                {
                    tests.Add(current.Build());
                    current = null;
                }

                continue;
            }

            if (current is not null && reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "failure")
                {
                    current.Status = "failed";
                }
                else if (reader.Name == "error")
                {
                    current.Status = "error";
                }
                else if (reader.Name == "skipped")
                {
                    current.Status = "skipped";
                }
            }

            if (current is not null && reader.NodeType == XmlNodeType.EndElement && reader.Name == "testcase")
            {
                tests.Add(current.Build());
                current = null;
            }
        }

        return tests;
    }

    private static TestCaseBuilder CreateBuilder(
        XmlReader reader,
        CoverageRunSlowTestProject project,
        List<string> warnings)
    {
        var className = reader.GetAttribute("classname");
        var name = reader.GetAttribute("name");
        var timeText = reader.GetAttribute("time");
        if (string.IsNullOrWhiteSpace(className))
        {
            AddWarning(warnings, $"JUnit testcase in '{project.JunitFile}' is missing classname.");
            className = "(missing classname)";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            AddWarning(warnings, $"JUnit testcase in '{project.JunitFile}' is missing name.");
            name = "(missing name)";
        }

        double? seconds = null;
        if (string.IsNullOrWhiteSpace(timeText))
        {
            AddWarning(warnings, $"JUnit testcase '{className}.{name}' in '{project.JunitFile}' is missing time.");
        }
        else if (!double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
            || parsedSeconds < 0
            || double.IsNaN(parsedSeconds)
            || double.IsInfinity(parsedSeconds))
        {
            AddWarning(warnings, $"JUnit testcase '{className}.{name}' in '{project.JunitFile}' has invalid time '{timeText}'.");
        }
        else
        {
            seconds = parsedSeconds;
        }

        return new TestCaseBuilder(project, className, name, seconds);
    }

    private static string RenderMarkdown(
        CoverageRunSlowTestDiagnosticsReport report,
        string markdownPath,
        string jsonPath,
        long aggregationSeconds,
        decimal aggregationPercent)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Slow test diagnostics");
        builder.AppendLine();
        builder.AppendLine("Managed test results: junit");
        builder.AppendLine(FormattableString.Invariant($"Generated: {report.GeneratedAtUtc:O}"));
        builder.AppendLine(FormattableString.Invariant($"Diagnostic aggregation overhead: {aggregationSeconds}s ({aggregationPercent:0.00}% of elapsed runner time at diagnostics generation)"));
        builder.AppendLine(FormattableString.Invariant($"Project metadata complete: {report.MetadataComplete}"));
        builder.AppendLine(FormattableString.Invariant($"Markdown: {markdownPath}"));
        builder.AppendLine(FormattableString.Invariant($"JSON: {jsonPath}"));
        builder.AppendLine();

        builder.AppendLine("## Top Projects");
        builder.AppendLine();
        if (report.Projects.Count == 0)
        {
            builder.AppendLine("No project timing metadata was available.");
        }
        else
        {
            builder.AppendLine("| Project | Seconds | Exit | Exclusive | JUnit | Parser |");
            builder.AppendLine("| --- | ---: | ---: | --- | --- | --- |");
            foreach (var project in report.Projects.OrderByDescending(project => project.Seconds).Take(MaxTopProjects))
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"| {EscapeMarkdown(project.Project)} | {project.Seconds} | {project.ExitCode} | {project.Exclusive} | {EscapeMarkdown(project.JunitFile ?? string.Empty)} | {project.ParserStatus} |"));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Top Test Cases");
        builder.AppendLine();
        if (report.TestCases.Count == 0)
        {
            builder.AppendLine("No JUnit test cases were available.");
        }
        else
        {
            builder.AppendLine("| Seconds | Status | Project | Test |");
            builder.AppendLine("| ---: | --- | --- | --- |");
            foreach (var test in report.TestCases.Take(MaxTopTests))
            {
                var testName = test.ClassName + "." + test.Name;
                builder.AppendLine(FormattableString.Invariant(
                    $"| {FormatSeconds(test.Seconds)} | {test.Status} | {EscapeMarkdown(test.Project)} | {EscapeMarkdown(testName)} |"));
            }
        }

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static string FormatSeconds(double? seconds)
    {
        return seconds.HasValue ? seconds.Value.ToString("0.###", CultureInfo.InvariantCulture) : "unknown";
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(warning);
        }
    }

    private sealed class TestCaseBuilder
    {
        private readonly CoverageRunSlowTestProject _project;
        private readonly string _className;
        private readonly string _name;
        private readonly double? _seconds;

        public TestCaseBuilder(CoverageRunSlowTestProject project, string className, string name, double? seconds)
        {
            _project = project;
            _className = className;
            _name = name;
            _seconds = seconds;
        }

        public string Status { get; set; } = "passed";

        public CoverageRunSlowTestCase Build()
        {
            return new CoverageRunSlowTestCase(
                _className,
                _name,
                _seconds,
                Status,
                _project.Project,
                _project.JunitFile ?? string.Empty);
        }
    }
}

/// <summary>
/// Written slow-test diagnostic artifact metadata.
/// </summary>
internal sealed record CoverageRunSlowTestDiagnosticsRun(
    string MarkdownPath,
    string JsonPath,
    long AggregationSeconds,
    decimal AggregationPercent,
    int WarningCount,
    bool MetadataComplete,
    IReadOnlyDictionary<string, string> ParserStatuses);

/// <summary>
/// Slow-test diagnostic report before overhead fields are finalized.
/// </summary>
internal sealed record CoverageRunSlowTestDiagnosticsReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    bool MetadataComplete,
    int JunitFileCount,
    IReadOnlyList<CoverageRunSlowTestProject> Projects,
    IReadOnlyList<CoverageRunSlowTestCase> TestCases,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Project timing metadata included in slow-test diagnostics.
/// </summary>
internal sealed record CoverageRunSlowTestProject(
    string Project,
    bool Exclusive,
    long Seconds,
    int ExitCode,
    string? JunitFile,
    string ParserStatus,
    string LogFile);

/// <summary>
/// Best-effort parse result for one managed JUnit file.
/// </summary>
internal sealed record CoverageRunJunitReadResult(
    IReadOnlyList<CoverageRunSlowTestCase> TestCases,
    string ParserStatus);

/// <summary>
/// Parsed JUnit test case timing included in slow-test diagnostics.
/// </summary>
internal sealed record CoverageRunSlowTestCase(
    string ClassName,
    string Name,
    double? Seconds,
    string Status,
    string Project,
    string JunitFile);
