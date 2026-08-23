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
internal sealed class CoverageEvidenceProducer(CoverageEvidenceExecutionWorkflow executionWorkflow)
{
    private readonly CoverageEvidenceExecutionWorkflow _executionWorkflow = executionWorkflow ?? throw new ArgumentNullException(nameof(executionWorkflow));

    /// <summary>
    /// Runs one declared coverage producer and returns only its stable evidence outcome.
    /// </summary>
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

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(producer.TimeoutSeconds));
            var result = await _executionWorkflow.RunAndGateAsync(
                CreateRunRequest(solutionPath, outputDirectory),
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.TimedOut, [], "The existing AppSurface coverage workflow exceeded its bounded execution window.");
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
        WatchdogMode: CoverageRunWatchdogMode.Warn,
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
