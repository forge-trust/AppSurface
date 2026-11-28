using System.Text.Json;
using System.Xml;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>One declared test project resolved for execution.</summary>
/// <param name="DeclaredPath">The repository-relative path from the policy.</param>
/// <param name="FullPath">The absolute project file path.</param>
/// <param name="Filter">The declared VSTest filter, or <see langword="null"/>.</param>
internal sealed record TestRunProject(string DeclaredPath, string FullPath, string? Filter)
{
    /// <summary>Gets the directory every child process for this project runs in.</summary>
    public string Directory => Path.GetDirectoryName(FullPath)!;
}

/// <summary>The outcome of one producer step for one project.</summary>
/// <param name="Phase">Stable phase name: <c>evaluate</c>, <c>restore</c>, <c>build</c>, <c>discover</c> or <c>run</c>.</param>
/// <param name="Command">The executable followed by the exact arguments used, for the rerun hint.</param>
/// <param name="Failure">The reason the step failed, or <see langword="null"/> on success.</param>
/// <param name="SubReason">Detail for <see cref="TestRunReason.DiscoveryFailed"/>; otherwise <see langword="null"/>.</param>
/// <param name="Detail">Short operator detail free of child output, such as the host path or a <c>global.json</c> runner.</param>
internal sealed record TestRunStep(string Phase, IReadOnlyList<string> Command, TestRunReason? Failure = null, string? SubReason = null, string? Detail = null);

/// <summary>The discovered inventory of one project.</summary>
/// <param name="Step">The discovery step and its failure, if any.</param>
/// <param name="Identities">Discovered identities with multiplicity; empty when the step failed.</param>
internal sealed record TestRunInventory(TestRunStep Step, IReadOnlyList<string> Identities);

/// <summary>The reported results of one project.</summary>
/// <param name="Step">The run step and its failure, if any.</param>
/// <param name="Results">Top-level results with multiplicity; empty when the step failed.</param>
internal sealed record TestRunResults(TestRunStep Step, IReadOnlyList<TestCaseResult> Results);

/// <summary>
/// Supplies everything the <c>test</c> producer learns from the outside world about one project.
/// </summary>
/// <remarks>
/// This is the seam that keeps the producer and the reconciler free of process and text-format knowledge. The v1
/// implementation is <see cref="DotnetCliTestRunSource"/>; a typed VSTest source can replace it without touching
/// reconciliation. Implementations must never return child output in <see cref="TestRunStep.Detail"/>.
/// </remarks>
internal interface ITestRunSource
{
    /// <summary>Evaluates toolchain, target frameworks, run settings and runner for a project.</summary>
    Task<TestRunStep> EvaluateAsync(TestRunProject project, CancellationToken cancellationToken);

    /// <summary>Restores the project.</summary>
    Task<TestRunStep> RestoreAsync(TestRunProject project, CancellationToken cancellationToken);

    /// <summary>Builds the project without restoring.</summary>
    Task<TestRunStep> BuildAsync(TestRunProject project, CancellationToken cancellationToken);

    /// <summary>Lists the tests the project and filter select.</summary>
    Task<TestRunInventory> DiscoverAsync(TestRunProject project, CancellationToken cancellationToken);

    /// <summary>Runs the selected tests and reads their results.</summary>
    /// <param name="project">The project.</param>
    /// <param name="resultsDirectory">A directory that must not exist yet; the source creates it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TestRunResults> RunAsync(TestRunProject project, string resultsDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// The v1 test-run source: <c>dotnet test --list-tests</c> for the inventory and a TRX file for the results, with
/// identity defined as the printed test name.
/// </summary>
/// <remarks>
/// <para>
/// Listing rule (strict): split standard output on LF and strip one trailing CR; exactly one line must equal
/// <see cref="ListingMarker"/>; after it every non-empty line must be four spaces followed by a non-space character,
/// or the zero-match notice. Anything else fails closed as <c>DiscoveryFailed/ListingShapeUnrecognized</c>.
/// </para>
/// <para>
/// Results rule: only <c>UnitTestResult</c> elements directly under <c>Results</c> count, and the
/// <c>ResultSummary</c> counter <c>total</c> must equal the parsed count.
/// </para>
/// <para>
/// Known limit: identity is the display name. Two different cases that swap one name between the listing and the run
/// are not detected, and names that print identically are reported as ambiguous by the reconciler.
/// </para>
/// </remarks>
internal sealed class DotnetCliTestRunSource : ITestRunSource
{
    /// <summary>The line that precedes the test names in <c>dotnet test --list-tests</c> output.</summary>
    public const string ListingMarker = "The following Tests are available:";

    /// <summary>The start of the notice printed when a filter matches no test.</summary>
    public const string ZeroMatchNoticePrefix = "No test matches the given testcase filter";

    /// <summary>The fixed TRX file name requested from the logger.</summary>
    public const string ResultsFileName = "results.trx";

    /// <summary>Maximum TRX size read, 256 MiB. A larger file is reported as missing or stale.</summary>
    public const long MaximumResultsBytes = 256L * 1024 * 1024;

    private static readonly string[] BuildServerFlags = ["--disable-build-servers", "-nodeReuse:false", "-tl:off", "--nologo"];

    private readonly IChildProcessRunner _runner;
    private readonly string _dotnetPath;
    private readonly string _configuration;
    private readonly bool _lockedMode;

    /// <summary>Creates the source.</summary>
    /// <param name="runner">Child process runner.</param>
    /// <param name="dotnetPath">The <c>dotnet</c> host used for every step.</param>
    /// <param name="configuration">Build configuration.</param>
    /// <param name="lockedMode">Whether to restore packages using NuGet locked mode.</param>
    public DotnetCliTestRunSource(IChildProcessRunner runner, string dotnetPath, string configuration, bool lockedMode = false)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        _dotnetPath = dotnetPath;
        _configuration = configuration;
        _lockedMode = lockedMode;
    }

    /// <inheritdoc />
    public async Task<TestRunStep> EvaluateAsync(TestRunProject project, CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "msbuild", Path.GetFileName(project.FullPath), $"-property:Configuration={_configuration}",
            "-getProperty:TargetFrameworks", "-getProperty:TargetFramework", "-getProperty:RunSettingsFilePath",
            "-getProperty:VSTestSetting", "-getProperty:TestingPlatformDotnetTestSupport", "-nologo",
        ];
        var step = new TestRunStep("evaluate", Command(arguments));
        var result = await _runner.RunAsync(new ChildProcessRequest(_dotnetPath, arguments, project.Directory), cancellationToken).ConfigureAwait(false);
        if (!result.Started || IsMissingSdk(result))
        {
            return step with { Failure = TestRunReason.ToolchainUnavailable, Detail = $"dotnet host '{_dotnetPath}'" };
        }

        if (result.ExitCode != 0 || !TryReadProperties(result.StandardOutput, out var properties, out var usesVsTestSetting))
        {
            return step with { Failure = TestRunReason.BuildFailed, Detail = "the project could not be evaluated" };
        }

        if (properties.DeclaresTargetFrameworks && properties.TargetFrameworks.Count > 1)
        {
            return step with { Failure = TestRunReason.MultiTargetUnsupported, Detail = string.Join(';', properties.TargetFrameworks) };
        }

        // VSTest reads VSTestSetting first and falls back to RunSettingsFilePath, so either one selects a settings file.
        if (properties.RunSettingsFilePath is not null || usesVsTestSetting)
        {
            return step with
            {
                Failure = TestRunReason.RunSettingsUnsupported,
                Detail = usesVsTestSetting ? "VSTestSetting is set" : "RunSettingsFilePath is set",
            };
        }

        var globalJson = VsTestRunnerProbe.ClassifyGlobalJson(project.Directory);
        if (!globalJson.IsSupported)
        {
            return step with { Failure = TestRunReason.UnsupportedRunner, Detail = $"global.json selects '{globalJson.Detail}'" };
        }

        return properties.UsesTestingPlatform
            ? step with { Failure = TestRunReason.UnsupportedRunner, Detail = "TestingPlatformDotnetTestSupport=true" }
            : step;
    }

    /// <inheritdoc />
    public Task<TestRunStep> RestoreAsync(TestRunProject project, CancellationToken cancellationToken)
    {
        string[] lockArguments = _lockedMode ? ["--locked-mode"] : [];
        return RunStepAsync(
            project,
            "restore",
            ["restore", Path.GetFileName(project.FullPath), $"-property:Configuration={_configuration}", .. lockArguments, .. BuildServerFlags],
            TestRunReason.RestoreFailed,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<TestRunStep> BuildAsync(TestRunProject project, CancellationToken cancellationToken) =>
        RunStepAsync(
            project,
            "build",
            ["build", Path.GetFileName(project.FullPath), "-c", _configuration, "--no-restore", .. BuildServerFlags],
            TestRunReason.BuildFailed,
            cancellationToken);

    /// <inheritdoc />
    public async Task<TestRunInventory> DiscoverAsync(TestRunProject project, CancellationToken cancellationToken)
    {
        string[] arguments = [.. TestArguments(project), "--list-tests"];
        var step = new TestRunStep("discover", Command(arguments));
        var result = await _runner.RunAsync(new ChildProcessRequest(_dotnetPath, arguments, project.Directory), cancellationToken).ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0)
        {
            return new TestRunInventory(step with { Failure = TestRunReason.DiscoveryFailed, SubReason = "ExitCode" }, []);
        }

        if (result.OutputTruncated)
        {
            return new TestRunInventory(step with { Failure = TestRunReason.DiscoveryFailed, SubReason = "OutputTooLarge" }, []);
        }

        return TryParseListing(result.StandardOutput, out var identities)
            ? new TestRunInventory(step, identities)
            : new TestRunInventory(step with { Failure = TestRunReason.DiscoveryFailed, SubReason = "ListingShapeUnrecognized" }, []);
    }

    /// <inheritdoc />
    public async Task<TestRunResults> RunAsync(TestRunProject project, string resultsDirectory, CancellationToken cancellationToken)
    {
        string[] arguments = [.. TestArguments(project), "--logger", $"trx;LogFileName={ResultsFileName}", "--results-directory", resultsDirectory];

        // The rerun hint leaves out the logger and the scratch results directory: scratch is deleted after the run,
        // and the remaining arguments select exactly the same tests.
        var step = new TestRunStep("run", Command(TestArguments(project)));
        if (Directory.Exists(resultsDirectory) || File.Exists(resultsDirectory))
        {
            return new TestRunResults(step with { Failure = TestRunReason.ResultsMissingOrStale }, []);
        }

        Directory.CreateDirectory(resultsDirectory);
        var result = await _runner.RunAsync(new ChildProcessRequest(_dotnetPath, arguments, project.Directory), cancellationToken).ConfigureAwait(false);
        var readable = TryReadResults(resultsDirectory, out var results);
        if (!result.Started || (result.ExitCode != 0 && !(readable && results.Any(static item => item.Outcome == TestCaseOutcome.Failed))))
        {
            return new TestRunResults(step with { Failure = TestRunReason.RunAborted }, []);
        }

        return readable
            ? new TestRunResults(step, results)
            : new TestRunResults(step with { Failure = TestRunReason.ResultsMissingOrStale }, []);
    }

    /// <summary>Parses <c>dotnet test --list-tests</c> standard output with the strict rule.</summary>
    /// <param name="standardOutput">Captured standard output.</param>
    /// <param name="identities">The listed names with multiplicity; empty for a zero-match listing.</param>
    /// <returns>False when the output does not have exactly the recognized shape.</returns>
    internal static bool TryParseListing(string standardOutput, out IReadOnlyList<string> identities)
    {
        var names = new List<string>();
        identities = names;
        var markers = 0;
        foreach (var line in standardOutput.Split('\n').Select(static rawLine => rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine))
        {
            if (string.Equals(line, ListingMarker, StringComparison.Ordinal))
            {
                markers++;
                continue;
            }

            if (markers == 0 || line.Length == 0)
            {
                continue;
            }

            if (line.Length > 4 && line.StartsWith("    ", StringComparison.Ordinal) && line[4] != ' ')
            {
                names.Add(line[4..]);
            }
            else if (!line.StartsWith(ZeroMatchNoticePrefix, StringComparison.Ordinal))
            {
                identities = [];
                return false;
            }
        }

        if (markers != 1)
        {
            identities = [];
            return false;
        }

        return true;
    }

    /// <summary>Reads the single TRX file a run wrote into its fresh results directory.</summary>
    /// <param name="resultsDirectory">The results directory of one project.</param>
    /// <param name="results">Top-level results; empty on failure.</param>
    /// <returns>
    /// False when the directory does not hold exactly one file, named <see cref="ResultsFileName"/>, or the file is too
    /// large, malformed, uses a DTD, or its <c>total</c> counter differs from the parsed count. Subdirectories, which
    /// test frameworks create for result attachments, are neither counted nor opened.
    /// </returns>
    internal static bool TryReadResults(string resultsDirectory, out IReadOnlyList<TestCaseResult> results) =>
        TryReadResults(resultsDirectory, MaximumResultsBytes, out results);

    /// <summary>Reads the single TRX file of a run, refusing a file longer than <paramref name="maximumBytes"/>.</summary>
    /// <param name="resultsDirectory">The results directory of one project.</param>
    /// <param name="maximumBytes">
    /// Largest file length opened. Production callers pass <see cref="MaximumResultsBytes"/> through the overload
    /// without this parameter; tests pass a small value so a well-formed file can prove the length check alone
    /// rejects it.
    /// </param>
    /// <param name="results">Top-level results; empty on failure.</param>
    /// <returns>False under the conditions the overload without <paramref name="maximumBytes"/> documents.</returns>
    internal static bool TryReadResults(string resultsDirectory, long maximumBytes, out IReadOnlyList<TestCaseResult> results)
    {
        results = [];
        try
        {
            var entries = Directory.GetFiles(resultsDirectory);
            var path = Path.Join(resultsDirectory, ResultsFileName);
            if (entries.Length != 1 || !string.Equals(Path.GetFileName(entries[0]), ResultsFileName, StringComparison.Ordinal) || !File.Exists(path))
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            if (stream.Length > maximumBytes)
            {
                return false;
            }

            return TryReadTrx(stream, out results);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads top-level results from a TRX stream.</summary>
    internal static bool TryReadTrx(Stream stream, out IReadOnlyList<TestCaseResult> results)
    {
        var parsed = new List<TestCaseResult>();
        results = [];
        int? total = null;
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumResultsBytes,
            });
            var resultsDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == resultsDepth)
                {
                    resultsDepth = -1;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Depth == 1 && reader.LocalName == "Results" && !reader.IsEmptyElement)
                {
                    resultsDepth = 1;
                }
                else if (resultsDepth == 1 && reader.Depth == 2 && reader.LocalName == "UnitTestResult")
                {
                    var name = reader.GetAttribute("testName");
                    if (name is null)
                    {
                        return false;
                    }

                    parsed.Add(new TestCaseResult(name, reader.GetAttribute("outcome") switch
                    {
                        "Passed" => TestCaseOutcome.Passed,
                        "NotExecuted" => TestCaseOutcome.Skipped,
                        _ => TestCaseOutcome.Failed,
                    }));
                }
                else if (reader.Depth == 2 && reader.LocalName == "Counters" && total is null
                    && int.TryParse(reader.GetAttribute("total"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var counter))
                {
                    total = counter;
                }
            }
        }
        catch (XmlException)
        {
            return false;
        }

        if (total != parsed.Count)
        {
            return false;
        }

        results = parsed;
        return true;
    }

    private async Task<TestRunStep> RunStepAsync(TestRunProject project, string phase, string[] arguments, TestRunReason failure, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(new ChildProcessRequest(_dotnetPath, arguments, project.Directory), cancellationToken).ConfigureAwait(false);
        return new TestRunStep(phase, Command(arguments), result.Started && result.ExitCode == 0 ? null : failure);
    }

    private string[] TestArguments(TestRunProject project)
    {
        string[] arguments = ["test", Path.GetFileName(project.FullPath), "-c", _configuration, "--no-build", "--nologo", "--disable-build-servers", "-nodeReuse:false", "-tl:off"];
        return project.Filter is null ? arguments : [.. arguments, "--filter", project.Filter];
    }

    private string[] Command(string[] arguments) => [_dotnetPath, .. arguments];

    private static bool IsMissingSdk(ChildProcessResult result) =>
        result.ExitCode != 0
        && (Contains(result, "compatible .NET SDK was not found") || Contains(result, "No .NET SDKs were found"));

    private static bool Contains(ChildProcessResult result, string text) =>
        result.StandardOutput.Contains(text, StringComparison.OrdinalIgnoreCase)
        || result.StandardError.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadProperties(string standardOutput, out VsTestEvaluatedProperties properties, out bool usesVsTestSetting)
    {
        properties = new VsTestEvaluatedProperties(false, [], false, null);
        usesVsTestSetting = false;
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Properties", out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            properties = VsTestRunnerProbe.ReadEvaluatedProperties(element);
            usesVsTestSetting = element.TryGetProperty("VSTestSetting", out var setting)
                && setting.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(setting.GetString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
