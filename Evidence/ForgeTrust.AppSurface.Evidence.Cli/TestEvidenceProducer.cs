using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>
/// Options a host supplies to <see cref="TestEvidenceProducer"/>.
/// </summary>
/// <param name="RepositoryRoot">Repository root that declared project paths are relative to and must stay inside.</param>
internal sealed record TestEvidenceProducerOptions(string RepositoryRoot)
{
    /// <summary>Gets the private scratch directory; <see langword="null"/> means a new unique directory under the system temporary directory.</summary>
    /// <remarks>Results files hold test names, so scratch is never placed under the evidence output and is deleted on every exit.</remarks>
    public string? ScratchRoot { get; init; }

    /// <summary>Gets the build configuration. Defaults to <c>Debug</c>.</summary>
    public string Configuration { get; init; } = "Debug";

    /// <summary>Gets the <c>dotnet</c> host used for every child process. Defaults to <c>dotnet</c> on PATH.</summary>
    public string DotnetPath { get; init; } = "dotnet";

    /// <summary>Gets the directory for the trace file of an unhandled producer error, or <see langword="null"/> to write none.</summary>
    /// <remarks>The CLI passes <c>&lt;output&gt;/diagnostics</c>. It must never be under the artifact root.</remarks>
    public string? DiagnosticsDirectory { get; init; }

    /// <summary>Gets the writer for progress lines; <see langword="null"/> means standard error.</summary>
    public TextWriter? Progress { get; init; }
}

/// <summary>What happened to one declared project, kept for console output. Never contains a test name.</summary>
/// <param name="Path">The declared project path.</param>
/// <param name="FilterSha256">Lower-case SHA-256 of the UTF-8 filter text, or <see langword="null"/> without a filter.</param>
/// <param name="MinimumTests">The effective minimum.</param>
/// <param name="Counts">Counts; zero where a step never ran.</param>
/// <param name="Reason">The single reason.</param>
/// <param name="SubReason">Detail for <see cref="TestRunReason.DiscoveryFailed"/>.</param>
/// <param name="Phase">The phase that decided the reason: <c>preflight</c>, a step phase, <c>reconcile</c> or <c>deadline</c>.</param>
/// <param name="Command">The exact command of the deciding step, for the rerun hint; empty when no process decided it.</param>
/// <param name="Detail">Short operator detail free of child output.</param>
internal sealed record TestProjectOutcome(
    string Path,
    string? FilterSha256,
    int MinimumTests,
    TestRunCounts Counts,
    TestRunReason Reason,
    string? SubReason,
    string Phase,
    IReadOnlyList<string> Command,
    string? Detail);

/// <summary>
/// The preview built-in <c>test</c> producer: runs the declared test projects and closes
/// <c>appsurface/test/discovery-complete@1</c> only when every selected test of every project ran once and passed.
/// </summary>
/// <remarks>
/// <para>Sequence:</para>
/// <code>
/// all projects:  file checks ................. ProjectNotFound
/// all projects:  evaluate (dotnet msbuild) ... ToolchainUnavailable, MultiTargetUnsupported,
///                                              RunSettingsUnsupported, UnsupportedRunner
/// per project:   restore ..................... RestoreFailed
///                build --no-restore .......... BuildFailed
///                discover (--list-tests) ..... DiscoveryFailed; zero identities skip the run
///                run (TRX) ................... RunAborted, ResultsMissingOrStale
///                reconcile ................... EmptyDiscovery .. TestsSkipped, Complete
/// internal deadline ......................... DeadlineExceeded (in flight), NotRun (not reached)
/// always:        write test-receipt.json, then delete scratch
/// </code>
/// <para>
/// The producer owns a deadline earlier than the declared timeout so it can still write the receipt: the declared
/// timeout minus a reserve of one tenth, rounded up to whole seconds, at least 1 and at most 30 seconds. Every
/// declared project is attempted even after an earlier one is incomplete, so the receipt is whole.
/// </para>
/// <para>
/// Pitfalls: it builds and runs repository code with the caller's environment; it writes build output into the
/// working tree, so it must not run beside another build of the same project; and it proves "ran and passed", not
/// that the tests exercise a change. Test names never leave scratch: child output is captured in memory and dropped.
/// </para>
/// </remarks>
internal sealed class TestEvidenceProducer : IEvidenceProducer
{
    /// <summary>The receipt path inside the producer's artifact directory.</summary>
    public const string ReceiptRelativePath = "test/test-receipt.json";

    private readonly TestEvidenceProducerOptions _options;
    private readonly ITestRunSource _source;

    /// <summary>Creates a producer over the <c>dotnet</c> CLI.</summary>
    /// <param name="id">The producer declaration id this instance handles.</param>
    /// <param name="options">Host options.</param>
    public TestEvidenceProducer(string id, TestEvidenceProducerOptions options)
        : this(id, options, new DotnetCliTestRunSource(new BoundedChildProcessRunner(), options.DotnetPath, options.Configuration))
    {
    }

    /// <summary>Creates a producer over an explicit test-run source.</summary>
    /// <param name="id">The producer declaration id this instance handles.</param>
    /// <param name="options">Host options.</param>
    /// <param name="source">The test-run source.</param>
    internal TestEvidenceProducer(string id, TestEvidenceProducerOptions options, ITestRunSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RepositoryRoot);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>Gets the per-project outcomes of the most recent run, in declaration order, for console output.</summary>
    public IReadOnlyList<TestProjectOutcome> LastProjects { get; private set; } = [];

    /// <summary>Gets the path of the trace file written for an unhandled error in the most recent run, if any.</summary>
    public string? LastTracePath { get; private set; }

    /// <summary>Computes the producer's internal deadline from the declared timeout.</summary>
    /// <param name="timeoutSeconds">The declared <c>timeoutSeconds</c>.</param>
    /// <returns>The declared timeout minus the reserve, never less than one second.</returns>
    internal static TimeSpan GetInternalDeadline(int timeoutSeconds)
    {
        var reserve = Math.Clamp((timeoutSeconds + 9) / 10, 1, 30);
        return TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds - reserve));
    }

    /// <inheritdoc />
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var declared = context.Producer.Test?.Projects
            ?? throw new InvalidOperationException($"Producer '{context.Producer.Id}' has no test declaration.");
        var started = context.TimeProvider.GetTimestamp();
        var scratch = _options.ScratchRoot ?? Path.Join(Path.GetTempPath(), "appsurface-test-evidence-" + Guid.NewGuid().ToString("N"));
        var outcomes = new TestProjectOutcome?[declared.Count];
        LastProjects = [];
        LastTracePath = null;
        try
        {
            CreateScratch(scratch);
            using var deadline = new CancellationTokenSource(GetInternalDeadline(context.Producer.TimeoutSeconds), context.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            var inFlight = -1;
            try
            {
                await RunProjectsAsync(declared, outcomes, scratch, index => inFlight = index, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                for (var index = 0; index < declared.Count; index++)
                {
                    outcomes[index] ??= Outcome(declared[index], index == inFlight ? TestRunReason.DeadlineExceeded : TestRunReason.NotRun, "deadline");
                }
            }

            var projects = outcomes.Select(static outcome => outcome!).ToArray();
            LastProjects = projects;
            var artifacts = await WriteReceiptAsync(context, projects).ConfigureAwait(false);
            var outcomeValue = TestRunReasons.Aggregate(projects.Select(static project => project.Reason));
            return new EvidenceProducerResult(
                context.Producer.Id,
                outcomeValue,
                outcomeValue == EvidenceProducerOutcome.Passed ? [EvidenceTestProducerContract.AssertionId] : [],
                FormatDiagnostic(projects),
                artifacts,
                (long)context.TimeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastTracePath = TryWriteTrace(exception);
            return new EvidenceProducerResult(
                context.Producer.Id,
                EvidenceProducerOutcome.Unavailable,
                [],
                "The test producer stopped on an internal error before it could write a receipt. No coverage claim was made.",
                null,
                (long)context.TimeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            TryDeleteScratch(scratch);
        }
    }

    private async Task RunProjectsAsync(
        IReadOnlyList<EvidenceTestProject> declared,
        TestProjectOutcome?[] outcomes,
        string scratch,
        Action<int> setInFlight,
        CancellationToken cancellationToken)
    {
        var root = ResolveRealPath(Path.GetFullPath(_options.RepositoryRoot));
        var projects = new TestRunProject?[declared.Count];
        for (var index = 0; index < declared.Count; index++)
        {
            var fullPath = Path.GetFullPath(Path.Join(root, declared[index].Path.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath) && IsInside(root, ResolveRealPath(fullPath)))
            {
                projects[index] = new TestRunProject(declared[index].Path, fullPath, declared[index].Filter);
            }
            else
            {
                outcomes[index] = Outcome(declared[index], TestRunReason.ProjectNotFound, "preflight", detail: "policy error: the declared project file does not exist inside the repository");
            }
        }

        for (var index = 0; index < declared.Count; index++)
        {
            if (projects[index] is not { } project)
            {
                continue;
            }

            setInFlight(index);
            Report(index, declared.Count, "evaluating");
            var step = await _source.EvaluateAsync(project, cancellationToken).ConfigureAwait(false);
            if (step.Failure is { } failure)
            {
                outcomes[index] = Outcome(declared[index], failure, step);
                projects[index] = null;
            }
        }

        for (var index = 0; index < declared.Count; index++)
        {
            if (projects[index] is not { } project)
            {
                continue;
            }

            setInFlight(index);
            outcomes[index] = await RunProjectAsync(declared[index], project, index, declared.Count, scratch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TestProjectOutcome> RunProjectAsync(
        EvidenceTestProject declared,
        TestRunProject project,
        int index,
        int count,
        string scratch,
        CancellationToken cancellationToken)
    {
        Report(index, count, "restoring");
        var restore = await _source.RestoreAsync(project, cancellationToken).ConfigureAwait(false);
        if (restore.Failure is { } restoreFailure)
        {
            return Outcome(declared, restoreFailure, restore);
        }

        Report(index, count, "building");
        var build = await _source.BuildAsync(project, cancellationToken).ConfigureAwait(false);
        if (build.Failure is { } buildFailure)
        {
            return Outcome(declared, buildFailure, build);
        }

        Report(index, count, "discovering");
        var inventory = await _source.DiscoverAsync(project, cancellationToken).ConfigureAwait(false);
        if (inventory.Step.Failure is { } discoveryFailure)
        {
            return Outcome(declared, discoveryFailure, inventory.Step);
        }

        var minimum = declared.MinimumTests ?? 1;
        if (inventory.Identities.Count == 0)
        {
            return Outcome(declared, TestRunReason.EmptyDiscovery, inventory.Step);
        }

        Report(index, count, $"running {inventory.Identities.Count} tests");
        var results = await _source.RunAsync(project, Path.Join(scratch, $"results-{index}"), cancellationToken).ConfigureAwait(false);
        if (results.Step.Failure is { } runFailure)
        {
            return Outcome(declared, runFailure, results.Step) with { Counts = new TestRunCounts(inventory.Identities.Count, 0, 0, 0, 0) };
        }

        var reconciliation = TestRunReconciler.Reconcile(inventory.Identities, results.Results, minimum);
        return Outcome(declared, reconciliation.Reason, results.Step with { Phase = "reconcile" }) with { Counts = reconciliation.Counts };
    }

    private static TestProjectOutcome Outcome(EvidenceTestProject declared, TestRunReason reason, TestRunStep step) =>
        Outcome(declared, reason, step.Phase, step.Command, step.SubReason, step.Detail);

    private static TestProjectOutcome Outcome(
        EvidenceTestProject declared,
        TestRunReason reason,
        string phase,
        IReadOnlyList<string>? command = null,
        string? subReason = null,
        string? detail = null) =>
        new(
            declared.Path,
            declared.Filter is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(declared.Filter))).ToLowerInvariant(),
            declared.MinimumTests ?? 1,
            TestRunCounts.Zero,
            reason,
            subReason,
            phase,
            command ?? [],
            detail);

    private static async Task<IReadOnlyList<EvidenceArtifactResult>?> WriteReceiptAsync(EvidenceProducerContext context, IReadOnlyList<TestProjectOutcome> projects)
    {
        if (context.Artifacts is null)
        {
            return null;
        }

        // The receipt is not cancellable: an incomplete run still needs its receipt.
        var artifact = await context.Artifacts.WriteAsync(
            EvidenceTestProducerContract.ReceiptSlotLogicalName,
            ReceiptRelativePath,
            SerializeReceipt(projects),
            CancellationToken.None).ConfigureAwait(false);
        return [artifact];
    }

    /// <summary>Serializes receipt schema version 1. It holds counts, reasons and a filter hash; never a test name or filter text.</summary>
    /// <param name="projects">Per-project outcomes in declaration order.</param>
    /// <returns>UTF-8 JSON bytes.</returns>
    internal static byte[] SerializeReceipt(IReadOnlyList<TestProjectOutcome> projects)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("projects");
            foreach (var project in projects)
            {
                writer.WriteStartObject();
                writer.WriteString("path", project.Path);
                if (project.FilterSha256 is null)
                {
                    writer.WriteNull("filterSha256");
                }
                else
                {
                    writer.WriteString("filterSha256", project.FilterSha256);
                }

                writer.WriteNumber("minimumTests", project.MinimumTests);
                writer.WriteNumber("discovered", project.Counts.Discovered);
                writer.WriteNumber("executed", project.Counts.Executed);
                writer.WriteNumber("passed", project.Counts.Passed);
                writer.WriteNumber("failed", project.Counts.Failed);
                writer.WriteNumber("skipped", project.Counts.Skipped);
                writer.WriteString("reason", project.Reason.ToString());
                if (project.SubReason is null)
                {
                    writer.WriteNull("subReason");
                }
                else
                {
                    writer.WriteString("subReason", project.SubReason);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Formats the one bounded diagnostic line recorded in the manifest.</summary>
    internal static string FormatDiagnostic(IReadOnlyList<TestProjectOutcome> projects)
    {
        var failing = projects.Count(static project => project.Reason == TestRunReason.TestsFailed);
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{projects.Count} project(s), {projects.Sum(static project => project.Counts.Discovered)} discovered, {projects.Sum(static project => project.Counts.Executed)} executed.");
        if (failing > 0)
        {
            line += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" {failing} project(s) with failing tests.");
        }

        return line + " No coverage claim was made.";
    }

    private void Report(int index, int count, string phase) =>
        (_options.Progress ?? Console.Error).WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{index + 1}/{count}] {phase}"));

    private static void CreateScratch(string scratch)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(scratch);
        }
        else
        {
            Directory.CreateDirectory(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void TryDeleteScratch(string scratch)
    {
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a cleanup failure never changes the outcome.
        }
    }

    private string? TryWriteTrace(Exception exception)
    {
        if (_options.DiagnosticsDirectory is not { } directory)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Join(directory, $"test-producer-trace-{Id}.txt");
            var text = new StringBuilder().AppendLine(exception.GetType().FullName);
            foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
            {
                var method = frame.GetMethod();
                text.Append("   at ").Append(method?.DeclaringType?.FullName).Append('.').AppendLine(method?.Name);
            }

            File.WriteAllText(path, text.ToString());
            return path;
        }
        catch (Exception traceFailure) when (traceFailure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsInside(string root, string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Resolves every symbolic link on a path, so containment is judged on the real location.</summary>
    private static string ResolveRealPath(string fullPath, int depth = 0)
    {
        var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = pathRoot;
        foreach (var segment in fullPath[pathRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                // A link target can itself sit below linked directories; bound the recursion so a cycle fails closed.
                current = depth < 16 ? ResolveRealPath(target.FullName, depth + 1) : string.Empty;
            }
        }

        return current;
    }
}
