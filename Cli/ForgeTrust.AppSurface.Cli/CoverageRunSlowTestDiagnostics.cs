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
    private const int ProgressBatchSize = 256;
    private const int StagedTextChunkSize = 16 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Parses managed JUnit files and builds a diagnostic report.
    /// </summary>
    /// <param name="results">Project run results with managed test result artifact paths.</param>
    /// <param name="cancellationToken">Cancellation token for artifact reads.</param>
    /// <param name="observeProgress">Optional callback that receives positive parsing and aggregation progress counts.</param>
    /// <returns>Slow-test diagnostic report model.</returns>
    public static async Task<CoverageRunSlowTestDiagnosticsReport> CollectAsync(
        IReadOnlyList<CoverageProjectRunResult> results,
        CancellationToken cancellationToken,
        Action<int>? observeProgress = null)
    {
        var warnings = new List<string>();
        var projects = new List<CoverageRunSlowTestProject>();
        var testCaseSummary = new CoverageRunSlowTestCaseSummaryBuilder(observeProgress);
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

            var junitTestCaseSummary = new CoverageRunSlowTestCaseSummaryBuilder(observeProgress);
            var junitResult = await ReadJunitFileAsync(project, warnings, junitTestCaseSummary.Add, cancellationToken, observeProgress);
            if (junitResult.ParserStatus == "parsed")
            {
                testCaseSummary.Merge(junitTestCaseSummary.Build());
            }

            projects.Add(project with { ParserStatus = junitResult.ParserStatus });
            observeProgress?.Invoke(1);
        }

        if (projects.Count == 0)
        {
            AddWarning(warnings, "No project metadata was available for slow-test diagnostics.");
        }

        var testCaseSummaryResult = testCaseSummary.Build();
        return new CoverageRunSlowTestDiagnosticsReport(
            SchemaVersion,
            DateTimeOffset.UtcNow,
            MetadataComplete: projects.Count > 0 && projects.All(project => project.ParserStatus == "parsed") && warnings.Count == 0,
            projects.Count(project => project.JunitFile is not null && File.Exists(project.JunitFile)),
            projects,
            testCaseSummaryResult.TestCaseCount,
            testCaseSummaryResult.FailedTestCaseCount,
            testCaseSummaryResult.SkippedTestCaseCount,
            testCaseSummaryResult.TopTestCases,
            warnings.Take(MaxWarnings).ToArray());
    }

    /// <summary>
    /// Writes diagnostics to private same-directory staging files while recording canonical artifact paths in their contents.
    /// </summary>
    /// <param name="stagedMarkdownPath">Unique private Markdown path alongside its canonical destination.</param>
    /// <param name="stagedJsonPath">Unique private JSON path alongside its canonical destination.</param>
    /// <param name="artifactDirectory">Canonical directory represented in returned and embedded artifact paths.</param>
    /// <param name="report">Report model returned by <see cref="CollectAsync"/>.</param>
    /// <param name="getAggregationSeconds">Reads elapsed diagnostic aggregation seconds.</param>
    /// <param name="calculateAggregationPercent">Calculates aggregation overhead as a percent of runner time.</param>
    /// <param name="cancellationToken">Cancellation token for artifact writes.</param>
    /// <param name="observeProgress">Optional callback that receives positive staging progress counts.</param>
    /// <returns>
    /// Canonical artifact paths, high-level metadata, and private staging paths after the writes complete.
    /// The caller owns the returned staging paths: promote them to their canonical destinations or call
    /// <see cref="TryDeleteStagedFile"/> after a failed or cancelled promotion.
    /// </returns>
    public static async Task<CoverageRunSlowTestDiagnosticsRun> WriteAsync(
        string stagedMarkdownPath,
        string stagedJsonPath,
        string artifactDirectory,
        CoverageRunSlowTestDiagnosticsReport report,
        Func<long> getAggregationSeconds,
        Func<long, decimal> calculateAggregationPercent,
        CancellationToken cancellationToken,
        Action<int>? observeProgress = null)
    {
        var markdownPath = Path.Join(artifactDirectory, MarkdownFileName);
        var jsonPath = Path.Join(artifactDirectory, JsonFileName);
        var currentStagedMarkdownPath = stagedMarkdownPath;
        var currentStagedJsonPath = stagedJsonPath;
        var ownedStagedPaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var aggregationSeconds = getAggregationSeconds();
            await WriteArtifactsAsync(
                report,
                currentStagedMarkdownPath,
                currentStagedJsonPath,
                markdownPath,
                jsonPath,
                aggregationSeconds,
                calculateAggregationPercent(aggregationSeconds),
                cancellationToken,
                observeProgress,
                path => ownedStagedPaths.Add(path));

            var finalAggregationSeconds = getAggregationSeconds();
            if (finalAggregationSeconds != aggregationSeconds)
            {
                var replacementStagedMarkdownPath = CreateStagedPath(stagedMarkdownPath, MarkdownFileName);
                var replacementStagedJsonPath = CreateStagedPath(stagedJsonPath, JsonFileName);
                aggregationSeconds = finalAggregationSeconds;
                await WriteArtifactsAsync(
                    report,
                    replacementStagedMarkdownPath,
                    replacementStagedJsonPath,
                    markdownPath,
                    jsonPath,
                    aggregationSeconds,
                    calculateAggregationPercent(aggregationSeconds),
                    cancellationToken,
                    observeProgress,
                    path => ownedStagedPaths.Add(path));
                TryDeleteStagedFile(currentStagedMarkdownPath);
                TryDeleteStagedFile(currentStagedJsonPath);
                ownedStagedPaths.Remove(currentStagedMarkdownPath);
                ownedStagedPaths.Remove(currentStagedJsonPath);
                currentStagedMarkdownPath = replacementStagedMarkdownPath;
                currentStagedJsonPath = replacementStagedJsonPath;
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
                    .ToDictionary(project => project.JunitFile!, project => project.ParserStatus, StringComparer.Ordinal),
                currentStagedMarkdownPath,
                currentStagedJsonPath);
        }
        catch
        {
            foreach (var ownedStagedPath in ownedStagedPaths)
            {
                TryDeleteStagedFile(ownedStagedPath);
            }

            throw;
        }
    }

    private static async Task WriteArtifactsAsync(
        CoverageRunSlowTestDiagnosticsReport report,
        string stagedMarkdownPath,
        string stagedJsonPath,
        string markdownPath,
        string jsonPath,
        long aggregationSeconds,
        decimal aggregationPercent,
        CancellationToken cancellationToken,
        Action<int>? observeProgress,
        Action<string> markStagedFileOwned)
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
                testCases = report.TestCaseCount,
                failedTestCases = report.FailedTestCaseCount,
                skippedTestCases = report.SkippedTestCaseCount,
                warnings = report.Warnings.Count,
            },
            topProjects = report.Projects
                .OrderByDescending(project => project.Seconds)
                .ThenBy(project => project.Project, StringComparer.Ordinal)
                .Take(MaxTopProjects),
            topTestCases = report.TopTestCases,
            warnings = report.Warnings,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await WriteStagedTextAsync(stagedJsonPath, json + Environment.NewLine, cancellationToken, observeProgress, markStagedFileOwned);
        await WriteStagedTextAsync(
            stagedMarkdownPath,
            RenderMarkdown(report, markdownPath, jsonPath, aggregationSeconds, aggregationPercent),
            cancellationToken,
            observeProgress,
            markStagedFileOwned);
    }

    private static async Task<CoverageRunJunitReadResult> ReadJunitFileAsync(
        CoverageRunSlowTestProject project,
        List<string> warnings,
        Action<CoverageRunSlowTestCase> addTestCase,
        CancellationToken cancellationToken,
        Action<int>? observeProgress)
    {
        try
        {
            await using var stream = new ProgressReportingStream(File.OpenRead(project.JunitFile!), observeProgress);
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(stream, settings);
            await ReadTestCasesAsync(reader, project, warnings, addTestCase, cancellationToken);
            return new CoverageRunJunitReadResult("parsed");
        }
        catch (FileNotFoundException)
        {
            AddWarning(warnings, $"JUnit file was not created: {project.JunitFile}");
            return new CoverageRunJunitReadResult("missing");
        }
        catch (DirectoryNotFoundException)
        {
            AddWarning(warnings, $"JUnit file was not created: {project.JunitFile}");
            return new CoverageRunJunitReadResult("missing");
        }
        catch (XmlException ex)
        {
            AddWarning(warnings, $"Failed to parse JUnit XML '{project.JunitFile}': {ex.Message}");
            return new CoverageRunJunitReadResult("parseFailed");
        }
        catch (IOException ex)
        {
            AddWarning(warnings, $"Failed to read JUnit XML '{project.JunitFile}': {ex.Message}");
            return new CoverageRunJunitReadResult("readFailed");
        }
        catch (UnauthorizedAccessException ex)
        {
            AddWarning(warnings, $"Failed to access JUnit XML '{project.JunitFile}': {ex.Message}");
            return new CoverageRunJunitReadResult("readFailed");
        }
    }

    private static async Task WriteStagedTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken,
        Action<int>? observeProgress,
        Action<string> markStagedFileOwned)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: StagedTextChunkSize,
            FileOptions.Asynchronous);
        markStagedFileOwned(path);
        await using var writer = new StreamWriter(stream, Utf8WithoutBom, StagedTextChunkSize, leaveOpen: true);

        for (var offset = 0; offset < contents.Length;)
        {
            var count = Math.Min(StagedTextChunkSize, contents.Length - offset);
            await writer.WriteAsync(contents.AsMemory(offset, count), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            observeProgress?.Invoke(count);
            offset += count;
        }
    }

    private static void InsertTopTestCase(List<CoverageRunSlowTestCase> topTestCases, CoverageRunSlowTestCase testCase)
    {
        var insertionIndex = 0;
        while (insertionIndex < topTestCases.Count && CompareTestCases(topTestCases[insertionIndex], testCase) <= 0)
        {
            insertionIndex++;
        }

        if (insertionIndex >= MaxTopTests)
        {
            return;
        }

        topTestCases.Insert(insertionIndex, testCase);
        if (topTestCases.Count > MaxTopTests)
        {
            topTestCases.RemoveAt(MaxTopTests);
        }
    }

    private static int CompareTestCases(CoverageRunSlowTestCase left, CoverageRunSlowTestCase right)
    {
        var seconds = (right.Seconds ?? 0).CompareTo(left.Seconds ?? 0);
        if (seconds != 0)
        {
            return seconds;
        }

        var className = StringComparer.Ordinal.Compare(left.ClassName, right.ClassName);
        return className != 0 ? className : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    private static string CreateStagedPath(string existingStagedPath, string artifactName)
    {
        var directory = Path.GetDirectoryName(existingStagedPath)
            ?? throw new IOException($"Slow-test diagnostics staging path has no parent directory: {existingStagedPath}");
        return Path.Join(directory, $".{artifactName}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Best-effort deletes a staging or backup path created by this diagnostics operation.
    /// </summary>
    /// <param name="path">Private path whose removal is safe after its creation was confirmed.</param>
    internal static void TryDeleteStagedFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the original diagnostics failure when owned staging cleanup cannot complete.
        }
    }

    /// <summary>
    /// Delegates stream operations while reporting positive byte counts from reads.
    /// </summary>
    /// <remarks>
    /// This type remains internal so the diagnostics tests can verify that all delegated stream operations
    /// preserve the wrapped stream's behavior without relying on reflection.
    /// </remarks>
    internal sealed class ProgressReportingStream(Stream inner, Action<int>? observeProgress) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Report(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private void Report(int count)
        {
            if (count > 0)
            {
                observeProgress?.Invoke(count);
            }
        }
    }

    private static async Task ReadTestCasesAsync(
        XmlReader reader,
        CoverageRunSlowTestProject project,
        List<string> warnings,
        Action<CoverageRunSlowTestCase> addTestCase,
        CancellationToken cancellationToken)
    {
        TestCaseBuilder? current = null;
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "testcase")
            {
                current = CreateBuilder(reader, project, warnings);
                if (reader.IsEmptyElement)
                {
                    addTestCase(current.Build());
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
                addTestCase(current.Build());
                current = null;
            }
        }
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
        if (report.TestCaseCount == 0)
        {
            builder.AppendLine("No JUnit test cases were available.");
        }
        else
        {
            builder.AppendLine("| Seconds | Status | Project | Test |");
            builder.AppendLine("| ---: | --- | --- | --- |");
            foreach (var test in report.TopTestCases)
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

    private sealed class CoverageRunSlowTestCaseSummaryBuilder(Action<int>? observeProgress)
    {
        private readonly List<CoverageRunSlowTestCase> _topTestCases = new(MaxTopTests);
        private int _testCaseCount;
        private int _failedTestCaseCount;
        private int _skippedTestCaseCount;

        public void Add(CoverageRunSlowTestCase testCase)
        {
            _testCaseCount++;
            if (testCase.Status is "failed" or "error")
            {
                _failedTestCaseCount++;
            }
            else if (testCase.Status == "skipped")
            {
                _skippedTestCaseCount++;
            }

            InsertTopTestCase(_topTestCases, testCase);
            if (_testCaseCount % ProgressBatchSize == 0)
            {
                observeProgress?.Invoke(1);
            }
        }

        public CoverageRunSlowTestCaseSummary Build()
            => new(_testCaseCount, _failedTestCaseCount, _skippedTestCaseCount, _topTestCases);

        public void Merge(CoverageRunSlowTestCaseSummary summary)
        {
            _testCaseCount += summary.TestCaseCount;
            _failedTestCaseCount += summary.FailedTestCaseCount;
            _skippedTestCaseCount += summary.SkippedTestCaseCount;
            foreach (var testCase in summary.TopTestCases)
            {
                InsertTopTestCase(_topTestCases, testCase);
            }
        }
    }

    private sealed record CoverageRunSlowTestCaseSummary(
        int TestCaseCount,
        int FailedTestCaseCount,
        int SkippedTestCaseCount,
        IReadOnlyList<CoverageRunSlowTestCase> TopTestCases);
}

/// <summary>
/// Written slow-test diagnostic artifact metadata.
/// </summary>
/// <remarks>
/// <see cref="StagedMarkdownPath"/> and <see cref="StagedJsonPath"/> are newly created private files.
/// Once this record is returned, the caller is responsible for promoting or deleting them. Failed or
/// cancelled writes clean up only staging files created by the writer.
/// </remarks>
/// <param name="MarkdownPath">Canonical Markdown destination recorded in the diagnostics.</param>
/// <param name="JsonPath">Canonical JSON destination recorded in the diagnostics.</param>
/// <param name="AggregationSeconds">Elapsed time spent collecting and writing diagnostics.</param>
/// <param name="AggregationPercent">Diagnostic overhead relative to the coverage run.</param>
/// <param name="WarningCount">Number of collected diagnostics warnings.</param>
/// <param name="MetadataComplete">Whether all requested test metadata was collected.</param>
/// <param name="ParserStatuses">Parse status for each managed JUnit artifact.</param>
/// <param name="StagedMarkdownPath">Private Markdown staging path the caller must promote or delete.</param>
/// <param name="StagedJsonPath">Private JSON staging path the caller must promote or delete.</param>
internal sealed record CoverageRunSlowTestDiagnosticsRun(
    string MarkdownPath,
    string JsonPath,
    long AggregationSeconds,
    decimal AggregationPercent,
    int WarningCount,
    bool MetadataComplete,
    IReadOnlyDictionary<string, string> ParserStatuses,
    string StagedMarkdownPath,
    string StagedJsonPath);

/// <summary>
/// Slow-test diagnostic report before overhead fields are finalized.
/// </summary>
/// <param name="SchemaVersion">Version of the serialized diagnostics schema.</param>
/// <param name="GeneratedAtUtc">UTC time at which diagnostics aggregation completed.</param>
/// <param name="MetadataComplete">Whether every requested managed JUnit artifact parsed without warnings.</param>
/// <param name="JunitFileCount">Number of managed JUnit artifacts that were present during aggregation.</param>
/// <param name="Projects">Per-project execution and parser metadata.</param>
/// <param name="TestCaseCount">Total number of parsed JUnit test cases.</param>
/// <param name="FailedTestCaseCount">Number of parsed test cases with failed or error status.</param>
/// <param name="SkippedTestCaseCount">Number of parsed skipped test cases.</param>
/// <param name="TopTestCases">Bounded, descending-duration test-case list for rendered artifacts.</param>
/// <param name="Warnings">Bounded diagnostics warnings encountered during aggregation.</param>
internal sealed record CoverageRunSlowTestDiagnosticsReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    bool MetadataComplete,
    int JunitFileCount,
    IReadOnlyList<CoverageRunSlowTestProject> Projects,
    int TestCaseCount,
    int FailedTestCaseCount,
    int SkippedTestCaseCount,
    IReadOnlyList<CoverageRunSlowTestCase> TopTestCases,
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
