using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// Builds the best-effort slow-test diagnostics artifacts for coverage runs.
/// </summary>
/// <remarks>
/// Diagnostics intentionally analyze only artifact metadata and JUnit structure. They do not read or
/// copy raw per-project logs by default, which keeps the report focused on timing evidence and avoids
/// turning diagnostic output into an unbounded log mirror. Parser warnings are preserved in the
/// report instead of being promoted to coverage-run failures.
/// </remarks>
internal static class SlowTestDiagnosticsWriter
{
    public const int SchemaVersion = 1;
    public const string MarkdownFileName = "slow-test-diagnostics.md";
    public const string JsonFileName = "slow-test-diagnostics.json";

    private const int MaxTopTests = 20;
    private const int MaxTopProjects = 20;
    private const int MaxWarnings = 100;

    /// <summary>
    /// Parses JUnit artifacts and builds a slow-test diagnostic report without writing files.
    /// </summary>
    /// <param name="options">Coverage runner options that locate the output directory and selected group.</param>
    /// <param name="results">
    /// Project run results from a normal run. When empty, the collector falls back to copied top-level
    /// JUnit files and marks project metadata as incomplete, which is the expected merge-only shape.
    /// </param>
    /// <param name="cancellationToken">Cancellation token used while opening XML artifacts.</param>
    /// <returns>A report model that can be written once measured aggregation overhead is known.</returns>
    public static async Task<SlowTestDiagnosticsReport> CollectAsync(
        CoverageRunnerOptions options,
        IReadOnlyList<ProjectRunResult> results,
        CancellationToken cancellationToken)
    {
        return await CollectAsync(options, results, File.OpenRead, cancellationToken);
    }

    /// <summary>
    /// Parses JUnit artifacts with a caller-provided stream opener for deterministic failure testing.
    /// </summary>
    /// <param name="options">Coverage runner options that locate the output directory and selected group.</param>
    /// <param name="results">Project run results from a normal run.</param>
    /// <param name="openJunitStream">Opens a JUnit artifact path for reading.</param>
    /// <param name="cancellationToken">Cancellation token used while opening XML artifacts.</param>
    /// <returns>A report model that can be written once measured aggregation overhead is known.</returns>
    internal static async Task<SlowTestDiagnosticsReport> CollectAsync(
        CoverageRunnerOptions options,
        IReadOnlyList<ProjectRunResult> results,
        Func<string, Stream> openJunitStream,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var projectEntries = results
            .OrderBy(result => result.Index)
            .Select(result => new SlowTestProjectEntry(
                result.Project.RelativePath,
                result.Project.Group,
                result.Project.IsExclusive,
                result.Seconds,
                result.ExitCode,
                result.JunitFile,
                result.LogFile))
            .ToArray();

        var junitInputs = projectEntries.Length > 0
            ? projectEntries.Select(project => new JunitInput(project.JunitFile, project)).ToArray()
            : EnumerateCopiedJunitInputs(options.OutputDirectory).ToArray();

        var testCases = new List<SlowTestCaseEntry>();
        foreach (var input in junitInputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            testCases.AddRange(await ReadJunitFileAsync(input, warnings, openJunitStream, cancellationToken));
        }

        if (junitInputs.Length == 0)
        {
            AddWarning(warnings, "No JUnit files were available for slow-test diagnostics.");
        }

        var categorySummaries = testCases
            .SelectMany(test => test.EvidenceCategories.Select(category => new { Test = test, Category = category }))
            .GroupBy(item => item.Category.Category, StringComparer.Ordinal)
            .Select(group =>
            {
                var examples = group
                    .Select(item => item.Category.Evidence)
                    .Where(evidence => !string.IsNullOrWhiteSpace(evidence))
                    .Distinct(StringComparer.Ordinal)
                    .Take(3)
                    .ToArray();

                return new SlowTestCategorySummary(
                    group.Key,
                    group.Count(),
                    group.Max(item => item.Test.Seconds ?? 0),
                    ChooseCategoryConfidence(group.Select(item => item.Category.Confidence)),
                    examples);
            })
            .OrderByDescending(category => category.MaxSeconds)
            .ThenByDescending(category => category.TestCaseCount)
            .ThenBy(category => category.Category, StringComparer.Ordinal)
            .ToArray();

        return new SlowTestDiagnosticsReport(
            SchemaVersion,
            DateTimeOffset.UtcNow,
            options.GroupName,
            MetadataComplete: projectEntries.Length > 0,
            junitInputs.Count(input => File.Exists(input.JunitFile)),
            projectEntries,
            testCases
                .OrderByDescending(test => test.Seconds ?? 0)
                .ThenBy(test => test.ClassName, StringComparer.Ordinal)
                .ThenBy(test => test.Name, StringComparer.Ordinal)
                .ToArray(),
            categorySummaries,
            warnings.Take(MaxWarnings).ToArray());
    }

    /// <summary>
    /// Writes Markdown and JSON diagnostics with measured aggregation overhead.
    /// </summary>
    /// <param name="options">Coverage runner options that locate the output directory.</param>
    /// <param name="report">Diagnostic report model returned by the collection step.</param>
    /// <param name="getAggregationSeconds">Reads whole seconds spent collecting and writing diagnostics.</param>
    /// <param name="calculateAggregationPercent">Calculates diagnostic seconds as a percent of runner time.</param>
    /// <param name="cancellationToken">Cancellation token used for artifact writes.</param>
    /// <returns>Artifact locations and high-level diagnostic metadata for summary and timings output.</returns>
    public static async Task<SlowTestDiagnosticsRun> WriteAsync(
        CoverageRunnerOptions options,
        SlowTestDiagnosticsReport report,
        Func<long> getAggregationSeconds,
        Func<long, decimal> calculateAggregationPercent,
        CancellationToken cancellationToken)
    {
        var markdownPath = Path.Join(options.OutputDirectory, MarkdownFileName);
        var jsonPath = Path.Join(options.OutputDirectory, JsonFileName);
        var aggregationSeconds = getAggregationSeconds();
        await WriteArtifactsAsync(
            report,
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
                markdownPath,
                jsonPath,
                aggregationSeconds,
                calculateAggregationPercent(aggregationSeconds),
                cancellationToken);
        }

        return new SlowTestDiagnosticsRun(
            markdownPath,
            jsonPath,
            aggregationSeconds,
            calculateAggregationPercent(aggregationSeconds),
            report.Warnings.Count,
            report.MetadataComplete);
    }

    private static async Task WriteArtifactsAsync(
        SlowTestDiagnosticsReport report,
        string markdownPath,
        string jsonPath,
        long aggregationSeconds,
        decimal aggregationPercent,
        CancellationToken cancellationToken)
    {
        var payload = CreateJsonPayload(report, markdownPath, jsonPath, aggregationSeconds, aggregationPercent);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json + Environment.NewLine, cancellationToken);
        await File.WriteAllTextAsync(
            markdownPath,
            RenderMarkdown(report, markdownPath, jsonPath, aggregationSeconds, aggregationPercent),
            cancellationToken);
    }

    private static object CreateJsonPayload(
        SlowTestDiagnosticsReport report,
        string markdownPath,
        string jsonPath,
        long aggregationSeconds,
        decimal aggregationPercent)
    {
        return new
        {
            schemaVersion = report.SchemaVersion,
            generatedAtUtc = report.GeneratedAtUtc,
            group = report.Group,
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
                .Take(MaxTopProjects)
                .Select(project => new
                {
                    project = project.Project,
                    group = project.Group,
                    exclusive = project.Exclusive,
                    seconds = project.Seconds,
                    exitCode = project.ExitCode,
                    junitFile = project.JunitFile,
                    logFile = project.LogFile,
                }),
            topTestCases = report.TestCases.Take(MaxTopTests).Select(test => new
            {
                className = test.ClassName,
                name = test.Name,
                seconds = test.Seconds,
                status = test.Status,
                project = test.Project,
                group = test.Group,
                junitFile = test.JunitFile,
                evidenceCategories = test.EvidenceCategories.Select(category => new
                {
                    category = category.Category,
                    confidence = category.Confidence,
                    evidence = category.Evidence,
                }),
            }),
            categories = report.Categories.Select(category => new
            {
                category = category.Category,
                testCaseCount = category.TestCaseCount,
                maxSeconds = category.MaxSeconds,
                confidence = category.Confidence,
                evidence = category.Evidence,
            }),
            warnings = report.Warnings,
        };
    }

    private static IEnumerable<JunitInput> EnumerateCopiedJunitInputs(string outputDirectory)
    {
        return Directory.Exists(outputDirectory)
            ? Directory.EnumerateFiles(outputDirectory, "junit-*.xml", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new JunitInput(path, Project: null))
            : [];
    }

    private static async Task<IReadOnlyList<SlowTestCaseEntry>> ReadJunitFileAsync(
        JunitInput input,
        List<string> warnings,
        Func<string, Stream> openJunitStream,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input.JunitFile))
        {
            AddWarning(warnings, $"JUnit file was not created: {input.JunitFile}");
            return [];
        }

        try
        {
            await using var stream = openJunitStream(input.JunitFile);
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(stream, settings);
            return await ReadTestCasesAsync(reader, input, warnings, cancellationToken);
        }
        catch (XmlException ex)
        {
            AddWarning(warnings, $"Failed to parse JUnit XML '{input.JunitFile}': {ex.Message}");
            return [];
        }
        catch (IOException ex)
        {
            AddWarning(warnings, $"Failed to read JUnit XML '{input.JunitFile}': {ex.Message}");
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            AddWarning(warnings, $"Failed to access JUnit XML '{input.JunitFile}': {ex.Message}");
            return [];
        }
    }

    private static async Task<IReadOnlyList<SlowTestCaseEntry>> ReadTestCasesAsync(
        XmlReader reader,
        JunitInput input,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var tests = new List<SlowTestCaseEntry>();
        TestCaseBuilder? current = null;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "testcase")
            {
                current = CreateBuilder(reader, input, warnings);
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

    private static TestCaseBuilder CreateBuilder(XmlReader reader, JunitInput input, List<string> warnings)
    {
        var className = reader.GetAttribute("classname");
        var name = reader.GetAttribute("name");
        var timeText = reader.GetAttribute("time");
        if (string.IsNullOrWhiteSpace(className))
        {
            AddWarning(warnings, $"JUnit testcase in '{input.JunitFile}' is missing classname.");
            className = "(missing classname)";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            AddWarning(warnings, $"JUnit testcase in '{input.JunitFile}' is missing name.");
            name = "(missing name)";
        }

        double? seconds = null;
        if (string.IsNullOrWhiteSpace(timeText))
        {
            AddWarning(warnings, $"JUnit testcase '{className}.{name}' in '{input.JunitFile}' is missing time.");
        }
        else if (!double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
            || parsedSeconds < 0
            || double.IsNaN(parsedSeconds)
            || double.IsInfinity(parsedSeconds))
        {
            AddWarning(warnings, $"JUnit testcase '{className}.{name}' in '{input.JunitFile}' has invalid time '{timeText}'.");
        }
        else
        {
            seconds = parsedSeconds;
        }

        return new TestCaseBuilder(input, className, name, seconds);
    }

    private static string RenderMarkdown(
        SlowTestDiagnosticsReport report,
        string markdownPath,
        string jsonPath,
        long aggregationSeconds,
        decimal aggregationPercent)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Slow test diagnostics");
        builder.AppendLine();
        builder.AppendLine(FormattableString.Invariant($"Group: {report.Group}"));
        builder.AppendLine(FormattableString.Invariant($"Generated: {report.GeneratedAtUtc:O}"));
        builder.AppendLine(FormattableString.Invariant($"Diagnostic aggregation overhead: {aggregationSeconds}s ({aggregationPercent:0.00}% of total runner time)"));
        builder.AppendLine(FormattableString.Invariant($"Project metadata complete: {report.MetadataComplete}"));
        builder.AppendLine(FormattableString.Invariant($"Markdown: {markdownPath}"));
        builder.AppendLine(FormattableString.Invariant($"JSON: {jsonPath}"));
        builder.AppendLine();

        if (!report.MetadataComplete)
        {
            builder.AppendLine("Project-level timing metadata was unavailable; this is expected for merge-only diagnostics from copied JUnit artifacts.");
            builder.AppendLine();
        }

        builder.AppendLine("## Top Projects");
        builder.AppendLine();
        if (report.Projects.Count == 0)
        {
            builder.AppendLine("No project timing metadata was available.");
        }
        else
        {
            builder.AppendLine("| Project | Seconds | Exit | Exclusive | JUnit |");
            builder.AppendLine("| --- | ---: | ---: | --- | --- |");
            foreach (var project in report.Projects.OrderByDescending(project => project.Seconds).Take(MaxTopProjects))
            {
                builder.AppendLine(FormattableString.Invariant(
                    $"| {EscapeMarkdown(project.Project)} | {project.Seconds} | {project.ExitCode} | {project.Exclusive} | {EscapeMarkdown(project.JunitFile)} |"));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Top Test Cases");
        builder.AppendLine();
        if (report.TestCases.Count == 0)
        {
            builder.AppendLine("No timed test cases were available.");
        }
        else
        {
            builder.AppendLine("| Test | Seconds | Status | Project | Evidence |");
            builder.AppendLine("| --- | ---: | --- | --- | --- |");
            foreach (var test in report.TestCases.Take(MaxTopTests))
            {
                var testDisplayName = test.ClassName + "." + test.Name;
                var project = test.Project ?? "unknown";
                var evidence = string.Join(", ", test.EvidenceCategories.Select(category => $"{category.Category} ({category.Confidence})"));
                builder.AppendLine(FormattableString.Invariant(
                    $"| {EscapeMarkdown(testDisplayName)} | {FormatSeconds(test.Seconds)} | {EscapeMarkdown(test.Status)} | {EscapeMarkdown(project)} | {EscapeMarkdown(evidence)} |"));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Evidence Categories");
        builder.AppendLine();
        if (report.Categories.Count == 0)
        {
            builder.AppendLine("No category evidence was available.");
        }
        else
        {
            builder.AppendLine("| Category | Test Cases | Max Seconds | Confidence | Evidence |");
            builder.AppendLine("| --- | ---: | ---: | --- | --- |");
            foreach (var category in report.Categories)
            {
                var evidence = string.Join("; ", category.Evidence);
                builder.AppendLine(FormattableString.Invariant(
                    $"| {EscapeMarkdown(category.Category)} | {category.TestCaseCount} | {category.MaxSeconds:0.###} | {EscapeMarkdown(category.Confidence)} | {EscapeMarkdown(evidence)} |"));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Warnings");
        builder.AppendLine();
        if (report.Warnings.Count == 0)
        {
            builder.AppendLine("None.");
        }
        else
        {
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine(FormattableString.Invariant($"- {warning}"));
            }
        }

        return builder.ToString();
    }

    private static string FormatSeconds(double? seconds)
    {
        return seconds.HasValue ? seconds.Value.ToString("0.###", CultureInfo.InvariantCulture) : "unknown";
    }

    private static string EscapeMarkdown(string text)
    {
        return text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(warning);
        }
    }

    private static string ChooseCategoryConfidence(IEnumerable<string> confidenceValues)
    {
        var values = confidenceValues.ToArray();
        if (values.Contains("high", StringComparer.Ordinal))
        {
            return "high";
        }

        return values.Contains("medium", StringComparer.Ordinal) ? "medium" : "low";
    }

    private static IReadOnlyList<SlowTestEvidenceCategory> ClassifyEvidence(JunitInput input, string className, string testName)
    {
        var subject = string.Join(' ', input.Project?.Project ?? string.Empty, className, testName);
        var categories = new List<SlowTestEvidenceCategory>();
        if (ContainsAny(subject, "Playwright", "Browser", "RazorWire", "IntegrationTests") || input.Project?.Exclusive == true)
        {
            categories.Add(new SlowTestEvidenceCategory(
                "browser-or-integration",
                input.Project?.Exclusive == true ? "high" : "medium",
                input.Project?.Exclusive == true ? "Project ran in the exclusive scheduler lane." : "Project or test name references browser or integration work."));
        }

        if (ContainsAny(subject, "CliWrap", "CommandRunner", "Process", "ProcessStartInfo", "dotnet", "ToolRestore"))
        {
            categories.Add(new SlowTestEvidenceCategory(
                "process-startup",
                "medium",
                "Project or test name references external process startup."));
        }

        if (ContainsAny(subject, "File", "Directory", "Temp", "TestRepo", "Workspace", "Artifact"))
        {
            categories.Add(new SlowTestEvidenceCategory(
                "filesystem-artifacts",
                "medium",
                "Project or test name references filesystem artifact setup."));
        }

        if (ContainsAny(subject, "Coverage", "ReportGenerator", "Cobertura", "JUnit"))
        {
            categories.Add(new SlowTestEvidenceCategory(
                "coverage-tooling",
                "medium",
                "Project or test name references coverage or report tooling."));
        }

        if (categories.Count == 0)
        {
            categories.Add(new SlowTestEvidenceCategory(
                "unit-test-execution",
                "low",
                "JUnit timing was available but no stronger category evidence matched."));
        }

        return categories;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record JunitInput(string JunitFile, SlowTestProjectEntry? Project);

    private sealed class TestCaseBuilder
    {
        private readonly JunitInput _input;

        public TestCaseBuilder(JunitInput input, string className, string name, double? seconds)
        {
            _input = input;
            ClassName = className;
            Name = name;
            Seconds = seconds;
        }

        public string ClassName { get; }

        public string Name { get; }

        public double? Seconds { get; }

        public string Status { get; set; } = "passed";

        public SlowTestCaseEntry Build()
        {
            return new SlowTestCaseEntry(
                ClassName,
                Name,
                Seconds,
                Status,
                _input.Project?.Project,
                _input.Project?.Group,
                _input.JunitFile,
                ClassifyEvidence(_input, ClassName, Name));
        }
    }
}

/// <summary>
/// Summary of diagnostics artifacts and their measured aggregation overhead.
/// </summary>
/// <param name="MarkdownPath">Full path to <c>slow-test-diagnostics.md</c>.</param>
/// <param name="JsonPath">Full path to <c>slow-test-diagnostics.json</c>.</param>
/// <param name="AggregationSeconds">Whole seconds spent parsing and aggregating JUnit diagnostics.</param>
/// <param name="AggregationPercent">Aggregation seconds as a percent of total runner seconds.</param>
/// <param name="WarningCount">Number of best-effort diagnostic warnings written to the report.</param>
/// <param name="MetadataComplete">Whether project metadata was available for each parsed JUnit input.</param>
internal sealed record SlowTestDiagnosticsRun(
    string MarkdownPath,
    string JsonPath,
    long AggregationSeconds,
    decimal AggregationPercent,
    int WarningCount,
    bool MetadataComplete);

/// <summary>
/// In-memory slow-test diagnostic report before measured overhead is attached.
/// </summary>
/// <param name="SchemaVersion">Schema version for machine consumers.</param>
/// <param name="GeneratedAtUtc">UTC timestamp for artifact traceability.</param>
/// <param name="Group">Coverage group that produced the artifacts.</param>
/// <param name="MetadataComplete">Whether project-level metadata was available.</param>
/// <param name="JunitFileCount">Number of JUnit files found while collecting diagnostics.</param>
/// <param name="Projects">Project timing entries from normal coverage runs.</param>
/// <param name="TestCases">Parsed test cases sorted by descending duration.</param>
/// <param name="Categories">Evidence category rollups from parsed tests.</param>
/// <param name="Warnings">Best-effort parser and metadata warnings.</param>
internal sealed record SlowTestDiagnosticsReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string Group,
    bool MetadataComplete,
    int JunitFileCount,
    IReadOnlyList<SlowTestProjectEntry> Projects,
    IReadOnlyList<SlowTestCaseEntry> TestCases,
    IReadOnlyList<SlowTestCategorySummary> Categories,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Project-level diagnostic input joined by the recorded JUnit path.
/// </summary>
/// <param name="Project">Project path reported by solution discovery.</param>
/// <param name="Group">Coverage group assigned to the project.</param>
/// <param name="Exclusive">Whether the project used the exclusive scheduler lane.</param>
/// <param name="Seconds">Measured project runtime in whole seconds.</param>
/// <param name="ExitCode">Exit code returned by <c>dotnet test</c>.</param>
/// <param name="JunitFile">Recorded JUnit XML path for this project.</param>
/// <param name="LogFile">Captured <c>dotnet test</c> log path for this project.</param>
internal sealed record SlowTestProjectEntry(
    string Project,
    string Group,
    bool Exclusive,
    long Seconds,
    int ExitCode,
    string JunitFile,
    string LogFile);

/// <summary>
/// Parsed JUnit test case timing and evidence category data.
/// </summary>
/// <param name="ClassName">JUnit <c>classname</c> attribute or a warning placeholder.</param>
/// <param name="Name">JUnit <c>name</c> attribute or a warning placeholder.</param>
/// <param name="Seconds">JUnit <c>time</c> attribute parsed as seconds, or <c>null</c> when unavailable.</param>
/// <param name="Status">Parsed test status: <c>passed</c>, <c>failed</c>, <c>error</c>, or <c>skipped</c>.</param>
/// <param name="Project">Project path when normal-run metadata is available.</param>
/// <param name="Group">Coverage group when normal-run metadata is available.</param>
/// <param name="JunitFile">JUnit XML artifact that supplied this test case.</param>
/// <param name="EvidenceCategories">Evidence categories inferred from project and test names.</param>
internal sealed record SlowTestCaseEntry(
    string ClassName,
    string Name,
    double? Seconds,
    string Status,
    string? Project,
    string? Group,
    string JunitFile,
    IReadOnlyList<SlowTestEvidenceCategory> EvidenceCategories);

/// <summary>
/// Evidence tag for a slow test candidate. Categories are hints, not root-cause claims.
/// </summary>
/// <param name="Category">Evidence category name.</param>
/// <param name="Confidence">Category confidence: <c>high</c>, <c>medium</c>, or <c>low</c>.</param>
/// <param name="Evidence">Short reason the category matched.</param>
internal sealed record SlowTestEvidenceCategory(string Category, string Confidence, string Evidence);

/// <summary>
/// Rollup of evidence categories found among parsed JUnit test cases.
/// </summary>
/// <param name="Category">Evidence category name.</param>
/// <param name="TestCaseCount">Number of parsed test cases assigned to the category.</param>
/// <param name="MaxSeconds">Maximum test duration observed in this category.</param>
/// <param name="Confidence">Highest confidence observed in this category.</param>
/// <param name="Evidence">Representative evidence strings.</param>
internal sealed record SlowTestCategorySummary(
    string Category,
    int TestCaseCount,
    double MaxSeconds,
    string Confidence,
    IReadOnlyList<string> Evidence);
