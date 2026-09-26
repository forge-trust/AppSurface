using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>
/// Runs the private AppSurface coverage engine for the first-party Evidence workflow.
/// </summary>
/// <remarks>
/// This adapter owns Evidence-to-coverage translation and stable claim outcomes. The core owns the required
/// collection-then-gate ordering, so this code neither recreates coverage execution nor invokes a second
/// <c>appsurface</c> process.
/// </remarks>
internal sealed class CoverageEvidenceProducer
{
    private readonly CoverageEvidenceExecutionWorkflow _executionWorkflow;
    private readonly TimeProvider _clock;

    /// <summary>Creates an Evidence adapter with the same monotonic clock as the coverage workflow.</summary>
    /// <param name="executionWorkflow">Shared private coverage run and gate engine.</param>
    /// <param name="clock">Optional monotonic clock used to measure the producer's remaining test budget.</param>
    public CoverageEvidenceProducer(CoverageEvidenceExecutionWorkflow executionWorkflow, TimeProvider? clock = null)
    {
        _executionWorkflow = executionWorkflow ?? throw new ArgumentNullException(nameof(executionWorkflow));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs one declared coverage producer through the private coverage core and returns its stable evidence outcome.
    /// </summary>
    /// <param name="producer">
    /// The explicit <c>coverage</c> declaration to execute. It must include coverage-gate requirements; unsupported
    /// kinds, missing gates, and patch gates without a captured diff return stable unavailable or invalid outcomes
    /// without starting the core.
    /// </param>
    /// <param name="solutionPath">
    /// The nonempty solution path used for AppSurface coverage discovery. The producer does not infer this value, so a
    /// missing path returns an unavailable outcome rather than selecting a solution implicitly.
    /// </param>
    /// <param name="outputDirectory">
    /// The nonempty Evidence output directory. The producer creates and cleans its <c>coverage</c> child directory for
    /// the fixed Debug, serial, input-order collector run and its merged gate report.
    /// </param>
    /// <param name="diffSnapshot">
    /// The optional bounded immutable diff captured during planning. Patch gates require this snapshot; callers must
    /// pass the planning snapshot instead of reopening its source file.
    /// </param>
    /// <param name="writers">
    /// The nonnull ordered standard-output and standard-error writers owned by the caller for the shared core's
    /// diagnostics.
    /// </param>
    /// <param name="cancellationToken">
    /// The caller cancellation token. Caller cancellation is propagated; only expiration of the producer's linked
    /// deadline yields <see cref="EvidenceProducerOutcome.TimedOut"/>.
    /// </param>
    /// <returns>
    /// A passed result only when collection and the declared gate both pass; a failed result for collection, gate, or
    /// nonfatal core failures (including independently thrown cancellation exceptions); or an unavailable or invalid
    /// result for declaration inputs that cannot safely start coverage.
    /// </returns>
    /// <exception cref="OperationCanceledException">The caller cancels <paramref name="cancellationToken"/>.</exception>
    /// <remarks>
    /// The private core owns collection-before-gate ordering. This adapter fixes the coverage run defaults to Debug,
    /// serial input-order execution, collector coverage, fail-mode no-progress handling, clean output, and the established AppSurface test exclusion.
    /// Its monotonic producer budget limits the automatic no-dump VSTest timer without replacing deadline cancellation;
    /// it does not expose a general-purpose coverage configuration surface to Evidence consumers.
    /// </remarks>
    public async Task<EvidenceProducerResult> RunAsync(
        EvidenceProducerDeclaration producer,
        string? solutionPath,
        string outputDirectory,
        EvidenceDiffSnapshot? diffSnapshot,
        CoverageTextWriters writers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writers);

        if (!string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase))
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], $"Producer kind '{producer.Kind}' requires an explicit consumer EvidenceHost registration.");
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], "The coverage producer requires --solution so it can reuse AppSurface coverage discovery.");
        }

        if (producer.CoverageGate is null)
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Invalid, [], "Coverage producer declarations must include explicit coverageGate requirements before they can close a coverage assertion.");
        }

        if ((producer.CoverageGate.MinPatchLinePercent.HasValue || producer.CoverageGate.MinPatchBranchPercent.HasValue)
            && diffSnapshot is null)
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], "The declared patch coverage gate requires --diff-file so the plan and coverage gate use the same explicit CI diff.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var producerDeadline = new CoverageProducerDeadline(
                _clock,
                _clock.GetTimestamp(),
                TimeSpan.FromSeconds(producer.TimeoutSeconds));
            deadline.CancelAfter(TimeSpan.FromSeconds(producer.TimeoutSeconds));
            var result = await _executionWorkflow.RunAndGateAsync(
                CreateRunRequest(solutionPath, outputDirectory) with { ProducerDeadline = producerDeadline },
                CreateGateRequest(outputDirectory, diffSnapshot, producer.CoverageGate),
                writers,
                deadline.Token).ConfigureAwait(false);
            if (!result.Run.Success)
            {
                return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], "The existing AppSurface coverage workflow reported a failed test, merge, or artifact step.");
            }

            var gateResult = result.Gate ?? throw new InvalidOperationException("A successful coverage run must produce a gate result.");
            return gateResult.Passed
                ? new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, producer.AssertionIds, $"Coverage gate passed: {gateResult.MarkdownReportPath}")
                : new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"Coverage gate did not meet its declared threshold. See {gateResult.MarkdownReportPath}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var timingsPath = Path.Join(outputDirectory, "coverage", "timings.json");
            var hint = string.Empty;
            try
            {
                using var artifact = CoverageRunArtifactReader.OpenRegularFile(
                    outputDirectory,
                    Path.Join(outputDirectory, "coverage"),
                    timingsPath);
                hint = " See coverage/timings.json for bounded diagnostics.";
            }
            catch (Exception ex) when (IsNonFatal(ex))
            {
                // The timeout outcome is authoritative; a missing or unsafe hint artifact is ignored.
            }
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.TimedOut, [], "The existing AppSurface coverage workflow exceeded its bounded execution window." + hint);
        }
        catch (OperationCanceledException)
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], "The existing AppSurface coverage workflow failed with OperationCanceledException.");
        }
        catch (CoverageExecutionException exception) when (IsNonFatal(exception))
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"The existing AppSurface coverage workflow failed with {exception.Code ?? exception.GetType().Name}.");
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"The existing AppSurface coverage workflow failed with {exception.GetType().Name}.");
        }
    }

    private static CoverageRunRequest CreateRunRequest(string solutionPath, string outputDirectory) => new(
        solutionPath,
        TestProjects: [],
        ExcludeTestProjects: [],
        OutputDirectory: Path.Join(outputDirectory, "coverage"),
        Configuration: "Debug",
        Parallelism: 1,
        ScheduleMode: CoverageRunScheduleMode.InputOrder,
        ScheduleTimingsPath: null,
        PriorityTestProjects: [],
        NoRestore: false,
        Build: true,
        NoBuild: false,
        IncludeFilter: null,
        ExcludeFilter: "[*.Tests]*,[*.IntegrationTests]*",
        DryRun: false,
        NoDiscoverExclusive: false,
        ExclusiveTestProjects: [],
        Loggers: [],
        TestArguments: [],
        TestResults: CoverageRunTestResultFormat.None,
        SlowTestDiagnostics: false,
        Clean: true,
        Verbosity: "minimal",
        HeartbeatInterval: TimeSpan.FromSeconds(30),
        NoProgressTimeout: TimeSpan.FromMinutes(10),
        WatchdogMode: CoverageRunHangPolicy.DefaultWatchdogMode,
        CoverageDriver: CoverageRunDriver.Collector,
        RequireNonSandbox: false);

    private static CoverageGateRequest CreateGateRequest(
        string outputDirectory,
        EvidenceDiffSnapshot? diffSnapshot,
        EvidenceCoverageGateRequirements requirements)
    {
        var coverageOutputDirectory = Path.Join(outputDirectory, "coverage");
        var patchCoverage = diffSnapshot is null
            ? null
            : new CoveragePatchRequest(
                GitRepositoryRootResolver.FindRepositoryRoot(Directory.GetCurrentDirectory()),
                PatchDiffSource.ForSnapshot(diffSnapshot.Bytes.ToArray(), diffSnapshot.Label, 20L * 1024 * 1024, diffSnapshot.Sha256),
                requirements.MinPatchLinePercent,
                requirements.MinPatchBranchPercent,
                string.Equals(requirements.PatchLineMode, "codecov", StringComparison.OrdinalIgnoreCase)
                    ? PatchLineMode.Codecov
                    : PatchLineMode.Measurable);
        return new CoverageGateRequest(
            Path.Join(coverageOutputDirectory, "coverage.cobertura.xml"),
            coverageOutputDirectory,
            requirements.MinLinePercent,
            requirements.MinBranchPercent,
            PatchCoverage: patchCoverage,
            TolerancePercent: requirements.TolerancePercent);
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;
}
