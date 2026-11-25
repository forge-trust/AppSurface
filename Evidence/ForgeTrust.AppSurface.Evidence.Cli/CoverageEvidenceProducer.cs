using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

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

    /// <summary>Creates an Evidence adapter with a clock for the producer deadline.</summary>
    /// <param name="executionWorkflow">Shared private coverage run and gate engine.</param>
    /// <param name="clock">Clock that schedules deadline cancellation and measures the remaining test budget. Defaults to the system clock.</param>
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
    /// the configured input-order collector run and its merged gate report.
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
    /// The private core owns collection-before-gate ordering. This adapter forwards the declared configuration,
    /// parallelism, and managed test-result format while retaining input-order scheduling, collector coverage,
    /// fail-mode no-progress handling, clean output, and the established AppSurface test exclusion.
    /// Its monotonic producer budget limits the automatic no-dump VSTest timer without replacing deadline cancellation;
    /// it does not expose a general-purpose coverage configuration surface to Evidence consumers.
    /// Use <see cref="RunWithInventoryAsync"/> when the caller manages receipt custody and needs the eligibility result.
    /// </remarks>
    public async Task<EvidenceProducerResult> RunAsync(
        EvidenceProducerDeclaration producer,
        string? solutionPath,
        string outputDirectory,
        EvidenceDiffSnapshot? diffSnapshot,
        CoverageTextWriters writers,
        CancellationToken cancellationToken) =>
        (await RunWithInventoryAsync(producer, solutionPath, outputDirectory, diffSnapshot, writers,
            cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>Runs coverage with optional complete custody and returns coordinator-owned eligibility data.</summary>
    /// <remarks>
    /// Pass a fresh writer rooted in a separate exclusive staging directory and explicitly declare
    /// <see cref="CoverageEvidenceInventory.CreateSlot"/>. Null preserves the ordinary fresh path.
    /// A custody limit or validation failure preserves Passed and all original reports, returns no artifact subset,
    /// and sets a bounded reason. The coordinator must never publish partial custody; original execution reports remain available.
    /// A missing workload opt-in runs the original fresh engine without channel credentials and reports
    /// reuse.child_lifetime_required. An opted-in custody request also requires the coordinator's session.
    /// This result makes no SDK child-settlement claim; the coordinator closes enrollment after the whole producer,
    /// joins actual child settlement and applies publication admission. It must not close enrollment per command.
    /// </remarks>
    /// <param name="producer">Declared coverage producer and its thresholds.</param>
    /// <param name="solutionPath">Explicit solution passed to the existing coverage engine.</param>
    /// <param name="outputDirectory">Exclusive execution root retaining the original reports.</param>
    /// <param name="diffSnapshot">Immutable planning snapshot used by patch gates.</param>
    /// <param name="writers">Caller-owned progress and diagnostic streams.</param>
    /// <param name="cancellationToken">Cancels the entire producer, including report delivery.</param>
    /// <param name="artifactWriter">Optional bounded receipt-custody writer.</param>
    /// <param name="childLifetime">Optional explicitly cooperative child coordinator.</param>
    /// <param name="finalReportRoot">Optional current-run root for ordinary reports; receipt bounds never change the gate outcome.</param>
    /// <param name="beforeCustodyForTests">Optional scheduling seam after required report delivery and before optional custody; production omits it.</param>
    /// <param name="repositoryRoot">
    /// Repository root that a scoped producer's declared project paths and the patch paths are relative to. Null
    /// resolves the Git repository containing the current directory. Only a scoped producer reads it.
    /// </param>
    /// <remarks>
    /// A declaration with <see cref="EvidenceProducerDeclaration.CoverageScope"/> takes the scoped branch: it runs only
    /// the declared projects, scores the scoped claim, writes its receipt through <paramref name="artifactWriter"/>
    /// for every outcome, and never publishes receipt custody, so its inventory is always ineligible.
    /// </remarks>
    internal async Task<CoverageEvidenceProducerExecutionResult> RunWithInventoryAsync(
        EvidenceProducerDeclaration producer,
        string? solutionPath,
        string outputDirectory,
        EvidenceDiffSnapshot? diffSnapshot,
        CoverageTextWriters writers,
        CancellationToken cancellationToken,
        EvidenceArtifactWriter? artifactWriter = null,
        EvidenceChildLifetimeSession? childLifetime = null,
        string? finalReportRoot = null,
        Action? beforeCustodyForTests = null,
        string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writers);

        static CoverageEvidenceProducerExecutionResult Finish(EvidenceProducerResult result)
            => new(result, null, "inventory-result-not-passed");

        if (!string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], $"Producer kind '{producer.Kind}' requires an explicit consumer EvidenceHost registration."));
        }

        if (producer.CoverageScope is { } scope)
        {
            var scoped = await RunScopedAsync(producer, scope, solutionPath, outputDirectory, diffSnapshot, writers,
                artifactWriter, repositoryRoot, cancellationToken).ConfigureAwait(false);
            return new(scoped, null, "scoped-coverage-not-reusable");
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], "The coverage producer requires --solution so it can reuse AppSurface coverage discovery."));
        }

        if (producer.CoverageGate is null)
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Invalid, [], "Coverage producer declarations must include explicit coverageGate requirements before they can close a coverage assertion."));
        }

        if (producer.CoverageArtifactRetention is not ("managed" or "full"))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Invalid, [], "ASCOV208 coverageArtifactRetention must be managed or full before execution starts."));
        }

        if (producer.CoverageGate.RequiresPatch && diffSnapshot is null)
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], "The declared patch coverage gate requires --diff-file so the plan and coverage gate use the same explicit CI diff."));
        }

        var cooperative = producer.CoverageExecution?.CooperativeChildLifetime == true;
        var useLifetime = cooperative && childLifetime is not null;
        var receiptWriter = useLifetime ? artifactWriter : null;
        string? inventoryReason = artifactWriter is null ? "inventory-not-requested" : null;
        if (!cooperative) inventoryReason = "reuse.child_lifetime_required";
        else if (childLifetime is null) inventoryReason = "reuse.child_lifetime_session_required";
        var workflow = useLifetime
            ? _executionWorkflow.WithProcessRunner(runner => new EvidenceCoverageProcessRunner(runner, childLifetime!))
            : _executionWorkflow;
        var deadlineToken = CancellationToken.None;
        try
        {
            var timeout = TimeSpan.FromSeconds(producer.TimeoutSeconds);
            var producerDeadline = new CoverageProducerDeadline(
                _clock,
                _clock.GetTimestamp(),
                timeout);
            using var deadline = new CancellationTokenSource(timeout, _clock);
            deadlineToken = deadline.Token;
            using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
            var runRequest = CreateRunRequest(solutionPath, outputDirectory, new(producer.CoverageExecution)) with
            {
                ProducerDeadline = producerDeadline,
                ArtifactRetention = producer.CoverageArtifactRetention,
            };
            var result = await workflow.RunAndGateAsync(
                runRequest,
                CreateGateRequest(outputDirectory, diffSnapshot, producer.CoverageGate),
                writers,
                executionCancellation.Token,
                captureOrdinaryPublications: finalReportRoot is not null || receiptWriter is not null).ConfigureAwait(false);
            if (!result.Run.Success)
            {
                return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], "The existing AppSurface coverage workflow reported a failed test, merge, or artifact step."));
            }

            return await CompletePostGateAsync(
                producer,
                outputDirectory,
                finalReportRoot,
                result,
                producerDeadline,
                receiptWriter,
                cancellationToken,
                executionCancellation.Token,
                beforeCustodyForTests,
                inventoryReason).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
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
            catch (Exception ex) when (EvidenceExceptionPolicy.IsNonFatalException(ex))
            {
                // The timeout outcome is authoritative; a missing or unsafe hint artifact is ignored.
            }
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.TimedOut, [], "The existing AppSurface coverage workflow exceeded its bounded execution window." + hint));
        }
        catch (OperationCanceledException)
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], "The existing AppSurface coverage workflow failed with OperationCanceledException."));
        }
        catch (CoverageExecutionException exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"The existing AppSurface coverage workflow failed with {exception.Code ?? exception.GetType().Name}."));
        }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"The existing AppSurface coverage workflow failed with {exception.GetType().Name}."));
        }
    }

    private async Task<CoverageEvidenceProducerExecutionResult> CompletePostGateAsync(
        EvidenceProducerDeclaration producer,
        string outputDirectory,
        string? finalReportRoot,
        CoverageEvidenceExecutionResult execution,
        CoverageProducerDeadline producerDeadline,
        EvidenceArtifactWriter? receiptWriter,
        CancellationToken callerCancellationToken,
        CancellationToken executionCancellationToken,
        Action? beforeCustodyForTests,
        string? inventoryReason)
    {
        CoverageEvidenceInventory? inventory = null;
        var gateResult = execution.Gate ?? throw new InvalidOperationException("A successful coverage run must produce a gate result.");
        var ordinary = gateResult.Passed
            ? new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, producer.AssertionIds, $"Coverage gate passed: {gateResult.MarkdownReportPath}")
            : new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"Coverage gate did not meet its declared threshold. See {gateResult.MarkdownReportPath}.");
        if (finalReportRoot is not null)
        {
            if (execution.OrdinaryPublications is { } ordinaryPublications
                && execution.OrdinaryPublicationIneligibilityReason is null)
            {
                var promotion = await TryPreserveOrdinaryReportsAsync(outputDirectory, finalReportRoot, execution,
                    ordinaryPublications, executionCancellationToken).ConfigureAwait(false);
                if (promotion.Complete)
                    ordinary = ordinary with { Diagnostic = RebaseReportPath(ordinary.Diagnostic, outputDirectory, finalReportRoot) };
                else
                {
                    inventoryReason = promotion.Reason;
                    ordinary = ordinary with
                    {
                        Outcome = EvidenceProducerOutcome.Failed,
                        SatisfiedAssertionIds = [],
                        Artifacts = null,
                        Diagnostic = ordinary.Diagnostic + " Ordinary report promotion is unavailable ("
                            + promotion.Reason + "). Original reports remain in the execution output.",
                    };
                }
            }
            else
            {
                // Optional capture uncertainty cannot rewrite the actual collection/gate result. Keep the
                // original reports and their actionable diagnostic; no receipt or artifact subset is admitted.
                inventoryReason = execution.OrdinaryPublicationIneligibilityReason ?? "ordinary-publications-unavailable";
            }
        }
        if (receiptWriter is not null && ordinary.Outcome == EvidenceProducerOutcome.Passed)
        {
            // Ordinary delivery has already completed. Optional custody consumes only the original remaining
            // producer budget, using its own cancellation source so expiration cannot replace that fresh result.
            var custodyBudget = producerDeadline.Remaining;
            using var custodyDeadline = new CancellationTokenSource(custodyBudget, _clock);
            if (custodyBudget == TimeSpan.Zero) custodyDeadline.Cancel();
            using var custodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken, custodyDeadline.Token);
            try
            {
                beforeCustodyForTests?.Invoke();
                var publications = execution.OrdinaryPublications
                    ?? throw new CoverageEvidenceInventoryException(execution.OrdinaryPublicationIneligibilityReason ?? "inventory-unavailable");
                inventory = CoverageEvidenceInventory.Capture(outputDirectory, execution, publications, custodyCancellation.Token);
                var slot = producer.ArtifactSlots.SingleOrDefault(slot => slot.LogicalName == CoverageEvidenceInventory.LogicalName)
                    ?? throw new CoverageEvidenceInventoryException("inventory-slot-missing");
                inventory.ValidateSlot(receiptWriter, slot);
                IReadOnlyList<EvidenceArtifactResult> artifacts;
                if (finalReportRoot is not null)
                {
                    // Required reports already exist here. Atomic, non-overwriting delivery adds only missing
                    // optional metadata; rejected custody can never overwrite or poison an ordinary report.
                    await PromoteReportsAsync(outputDirectory, finalReportRoot, execution, inventory.Files,
                        custodyCancellation.Token).ConfigureAwait(false);
                    artifacts = inventory.AdoptDelivered(receiptWriter, slot, finalReportRoot, custodyCancellation.Token);
                }
                else
                    artifacts = await inventory.WriteAsync(receiptWriter, slot, outputDirectory, custodyCancellation.Token).ConfigureAwait(false);
                ordinary = ordinary with { Artifacts = artifacts };
            }
            catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested && custodyDeadline.IsCancellationRequested)
            {
                inventory = null;
                inventoryReason = "inventory-deadline";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
            {
                inventory = null;
                inventoryReason = exception is CoverageEvidenceInventoryException failure
                    ? failure.Reason : "inventory-custody-unavailable";
            }
        }

        return ordinary.Outcome == EvidenceProducerOutcome.Passed
            ? new(ordinary, inventory, inventoryReason)
            : new(ordinary, null, "inventory-result-not-passed");
    }

    /// <summary>
    /// Runs the declared test projects, scores the scoped patch claim, and writes its receipt for every outcome.
    /// </summary>
    /// <remarks>
    /// Order: solution check (<see cref="ScopedCoverageReason.SolutionUnavailable"/>), diff check
    /// (<see cref="ScopedCoverageReason.DiffUnavailable"/>), then one coverage run of the declared projects into
    /// <c>coverage-scoped/&lt;producer id&gt;</c>, so two scoped producers never share report files and none can
    /// collide with the reserved children of the repository-wide producer's <c>coverage</c> directory. The run is followed by the
    /// existing gate with zero overall thresholds and no patch thresholds: its verdict is ignored, and it runs only so
    /// <c>coverage-patch-targets.json</c> and <c>.md</c> hold per-line detail beside the merged report. The merged report
    /// is then read by package and classified by <see cref="ScopedPatchClassifier"/>. Expiry of
    /// <c>timeoutSeconds</c> writes a <see cref="ScopedCoverageReason.DeadlineExceeded"/> receipt from the timeout
    /// handler; caller cancellation propagates and writes nothing.
    /// </remarks>
    private async Task<EvidenceProducerResult> RunScopedAsync(
        EvidenceProducerDeclaration producer,
        EvidenceCoverageScope scope,
        string? solutionPath,
        string outputDirectory,
        EvidenceDiffSnapshot? diffSnapshot,
        CoverageTextWriters writers,
        EvidenceArtifactWriter? artifactWriter,
        string? repositoryRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return await FinishScopedAsync(producer, scope, ScopedPatchClassification.Unclassified(scope, ScopedCoverageReason.SolutionUnavailable),
                "The scoped coverage producer requires --solution, as the repository-wide coverage producer does.", artifactWriter).ConfigureAwait(false);
        }

        if (diffSnapshot is null)
        {
            return await FinishScopedAsync(producer, scope, ScopedPatchClassification.Unclassified(scope, ScopedCoverageReason.DiffUnavailable),
                "The scoped coverage producer requires --diff-file so the plan and the scored lines come from the same diff.", artifactWriter).ConfigureAwait(false);
        }

        var coverageDirectory = Path.Join(outputDirectory, ScopedCoverageRootName, producer.Id);
        var deadlineToken = CancellationToken.None;
        string? detail;
        try
        {
            var root = repositoryRoot ?? GitRepositoryRootResolver.FindRepositoryRoot(Directory.GetCurrentDirectory());
            var timeout = TimeSpan.FromSeconds(producer.TimeoutSeconds);
            var producerDeadline = new CoverageProducerDeadline(_clock, _clock.GetTimestamp(), timeout);
            using var deadline = new CancellationTokenSource(timeout, _clock);
            deadlineToken = deadline.Token;
            using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineToken);
            var runRequest = CreateRunRequest(solutionPath, outputDirectory, new(producer.CoverageExecution)) with
            {
                TestProjects = [.. scope.Projects.Select(project => Path.GetFullPath(Path.Join(root, project)))],
                OutputDirectory = coverageDirectory,
                ProducerDeadline = producerDeadline,
                ArtifactRetention = producer.CoverageArtifactRetention,
            };
            var patchRequest = new CoveragePatchRequest(
                root,
                PatchDiffSource.ForSnapshot(diffSnapshot.Bytes.ToArray(), diffSnapshot.Label, EvidencePlan.MaximumCapturedPatchBytes, diffSnapshot.Sha256),
                MinPatchLinePercent: null,
                MinPatchBranchPercent: null,
                string.Equals(scope.PatchLineMode, "codecov", StringComparison.OrdinalIgnoreCase) ? PatchLineMode.Codecov : PatchLineMode.Measurable);
            var coveragePath = Path.Join(coverageDirectory, "coverage.cobertura.xml");
            var gateRequest = new CoverageGateRequest(coveragePath, coverageDirectory, 0, 0, patchRequest, scope.TolerancePercent);
            var execution = await _executionWorkflow.RunAndGateAsync(runRequest, gateRequest, writers, executionCancellation.Token).ConfigureAwait(false);
            if (!execution.Run.Success)
            {
                return await FinishScopedAsync(producer, scope, ScopedPatchClassification.Unclassified(scope, ScopedCoverageReason.RunFailed),
                    "The coverage run of the declared projects reported a failed test, merge, or artifact step.", artifactWriter).ConfigureAwait(false);
            }

            var report = await PatchCoverageEvaluator.ReadPackageCoverageAsync(coveragePath, root, executionCancellation.Token).ConfigureAwait(false);
            var diffText = diffSnapshot.Text;
            var changedFiles = string.IsNullOrWhiteSpace(diffText) ? [] : EvidenceUnifiedDiffReader.Read(diffText);
            var changedLines = PatchCoverageEvaluator.ParseChangedLines(diffText);
            var classification = ScopedPatchClassifier.Classify(scope, changedFiles, changedLines, report);
            detail = execution.Gate?.PatchTargetsJsonPath is { } targets ? $"Per-line detail: {targets}." : null;
            return await FinishScopedAsync(producer, scope, classification, detail, artifactWriter).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
        {
            detail = "The coverage run of the declared projects exceeded the producer's timeoutSeconds.";
            return await FinishScopedAsync(producer, scope, ScopedPatchClassification.Unclassified(scope, ScopedCoverageReason.DeadlineExceeded), detail, artifactWriter).ConfigureAwait(false);
        }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            var code = exception is CoverageExecutionException { Code: { } ascov } ? ascov : exception.GetType().Name;
            return await FinishScopedAsync(producer, scope, ScopedPatchClassification.Unclassified(scope, ScopedCoverageReason.RunFailed),
                $"The scoped coverage run failed with {code}.", artifactWriter).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the receipt and builds the result; a receipt that cannot be written withholds the assertion.</summary>
    private static async Task<EvidenceProducerResult> FinishScopedAsync(
        EvidenceProducerDeclaration producer,
        EvidenceCoverageScope scope,
        ScopedPatchClassification classification,
        string? detail,
        EvidenceArtifactWriter? artifactWriter)
    {
        var outcome = ScopedCoverageReceipt.ToOutcome(classification.Reason);
        var diagnostic = ScopedCoverageReceipt.FormatDiagnostic(scope, classification, detail);
        IReadOnlyList<EvidenceArtifactResult>? artifacts = null;
        if (artifactWriter is not null)
        {
            try
            {
                // Not cancellable: a timed-out or failed run still needs its receipt.
                artifacts =
                [
                    await artifactWriter.WriteAsync(
                        EvidenceScopedCoverageContract.ReceiptSlotLogicalName,
                        ScopedCoverageReceipt.ReceiptRelativePath,
                        ScopedCoverageReceipt.Serialize(scope, classification),
                        CancellationToken.None).ConfigureAwait(false),
                ];
            }
            catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
            {
                diagnostic += $" The receipt could not be written ({exception.GetType().Name}).";
                if (outcome == EvidenceProducerOutcome.Passed)
                {
                    outcome = EvidenceProducerOutcome.Failed;
                }
            }
        }

        return new EvidenceProducerResult(
            producer.Id,
            outcome,
            outcome == EvidenceProducerOutcome.Passed ? producer.AssertionIds : [],
            diagnostic)
        {
            Artifacts = artifacts,
        };
    }

    /// <summary>The output child that holds one <c>&lt;producer id&gt;</c> directory per scoped coverage producer.</summary>
    internal const string ScopedCoverageRootName = "coverage-scoped";

    /// <summary>Builds the coverage engine request for the resolved built-in settings.</summary>
    /// <param name="solutionPath">Solution submitted to the engine.</param>
    /// <param name="outputDirectory">Invocation root containing the coverage directory.</param>
    /// <param name="settings">Resolved settings shared with receipt identity.</param>
    /// <returns>The request before applying the invocation deadline and artifact retention.</returns>
    internal static CoverageRunRequest CreateRunRequest(
        string solutionPath,
        string outputDirectory,
        CoverageEvidenceRunSettings settings) => new(
        solutionPath,
        TestProjects: [],
        ExcludeTestProjects: [],
        OutputDirectory: Path.Join(outputDirectory, "coverage"),
        Configuration: settings.Configuration,
        Parallelism: settings.Parallelism,
        ScheduleMode: CoverageEvidenceRunSettings.Schedule,
        ScheduleTimingsPath: null,
        PriorityTestProjects: [],
        NoRestore: CoverageEvidenceRunSettings.NoRestore,
        Build: CoverageEvidenceRunSettings.Build,
        NoBuild: false,
        IncludeFilter: null,
        ExcludeFilter: CoverageEvidenceRunSettings.TestExclusion,
        DryRun: false,
        NoDiscoverExclusive: false,
        ExclusiveTestProjects: [],
        Loggers: [],
        TestArguments: [],
        TestResults: settings.TestResults switch
        {
            EvidenceCoverageTestResultFormat.None => CoverageRunTestResultFormat.None,
            EvidenceCoverageTestResultFormat.Junit => CoverageRunTestResultFormat.Junit,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.TestResults, "Unsupported coverage test result format."),
        },
        SlowTestDiagnostics: false,
        Clean: true,
        Verbosity: "minimal",
        HeartbeatInterval: TimeSpan.FromSeconds(30),
        NoProgressTimeout: TimeSpan.FromMinutes(10),
        WatchdogMode: CoverageRunHangPolicy.DefaultWatchdogMode,
        CoverageDriver: CoverageEvidenceRunSettings.Driver,
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
                PatchDiffSource.ForSnapshot(diffSnapshot.Bytes.ToArray(), diffSnapshot.Label,
                    EvidencePlan.MaximumCapturedPatchBytes, diffSnapshot.Sha256),
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

    private static async Task<(bool Complete, string? Reason)> TryPreserveOrdinaryReportsAsync(
        string sourceRoot, string finalRoot, CoverageEvidenceExecutionResult execution,
        IReadOnlyList<CoverageArtifactInventoryEntry> publications, CancellationToken cancellationToken)
    {
        try
        {
            await PreserveOrdinaryReportsAsync(sourceRoot, finalRoot, execution, publications, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (EvidenceExceptionPolicy.IsNonFatalException(exception))
        {
            return (false, "ordinary-report-promotion-unavailable");
        }
    }

    /// <summary>Validates and copies one managed execution's closed ordinary report set.</summary>
    /// <param name="sourceRoot">Execution root retaining the original reports. Source files are never removed.</param>
    /// <param name="finalRoot">Current invocation's report root, opened without following links.</param>
    /// <param name="execution">Successful collection and completed gate decision identifying required reports.</param>
    /// <param name="publications">Complete writer-owned publication inventory, relative to the coverage directory.</param>
    /// <param name="cancellationToken">Cancels validation, hashing, and copying; every opened handle is disposed.</param>
    /// <param name="beforeReportPromotion">Optional per-call scheduling seam after a report is closed and verified;
    /// production omits it. Cancellation is checked again before the atomic, non-overwriting promotion.</param>
    /// <remarks>
    /// This internal boundary is intentionally available to contract tests without reflection. Production calls it
    /// through the producer's failure-reporting wrapper. It checks canonical paths, case collisions, required aggregate
    /// and project reports, lengths, and digests before claiming successful delivery. Existing destination files are
    /// accepted only when their bytes match; differing or linked files are preserved and rejected. Cancellation or an
    /// I/O failure can leave already completed destination files, so callers must withhold completion on any exception.
    /// Incomplete reports remain in private temporary files and never occupy a canonical report path; a failed
    /// delivery removes only its own temporary file, allowing a later delivery to retry safely.
    /// This operation has no receipt-size limit and does not publish a receipt or change the coverage gate verdict.
    /// </remarks>
    /// <exception cref="InvalidDataException">The execution or closed publication metadata is incomplete or invalid.</exception>
    /// <exception cref="IOException">A source changed or a destination cannot safely accept its exact bytes.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled validation or delivery.</exception>
    internal static Task PreserveOrdinaryReportsAsync(
        string sourceRoot,
        string finalRoot,
        CoverageEvidenceExecutionResult execution,
        IReadOnlyList<CoverageArtifactInventoryEntry> publications,
        CancellationToken cancellationToken,
        Action? beforeReportPromotion = null)
        => PromoteReportsAsync(sourceRoot, finalRoot, execution,
            publications.Select(static publication => CoverageReportFile.FromPublication(publication, normalizeSeparators: true)),
            cancellationToken, beforeReportPromotion);

    /// <summary>Validates and delivers a complete set of run-relative reports for either ordinary delivery or custody.</summary>
    private static async Task PromoteReportsAsync(
        string sourceRoot,
        string finalRoot,
        CoverageEvidenceExecutionResult execution,
        IEnumerable<CoverageReportFile> reports,
        CancellationToken cancellationToken,
        Action? beforeReportPromotion = null)
    {
        if (!execution.Run.Success || execution.Gate is null)
            throw new InvalidDataException("Ordinary reports require successful collection and a completed gate decision.");
        var normalizedFinalRoot = Path.GetFullPath(finalRoot);
        var normalizedSourceRoot = Path.GetFullPath(sourceRoot);
        var files = reports.OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
        ValidateOrdinaryReportSet(normalizedSourceRoot, execution, files, cancellationToken);

        using var finalRootLease = CoverageRunOutputLease.AcquireArtifactDirectory(
            normalizedFinalRoot, createMissing: true, requirePrivate: false)
            ?? throw new IOException("The final coverage report root is unavailable.");
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var components = file.RelativePath.Split('/');
            using var destinationParent = OpenOrCreateArtifactDirectory(finalRootLease, components[..^1]);
            var destinationName = components[^1];
            using var input = CoverageReportFile.OpenRead(normalizedSourceRoot, file.RelativePath);
            if (input.Length != file.LengthBytes)
                throw new IOException("A managed coverage report changed before promotion.");

            FileStream? existing = null;
            try { existing = destinationParent.OpenArtifactFile(destinationName, createNew: false, requirePrivate: false); }
            catch (FileNotFoundException) { }
            if (existing is not null)
            {
                using (existing)
                {
                    var (status, digest) = await EvidenceArtifactStream.HashAsync(existing, file.LengthBytes, cancellationToken).ConfigureAwait(false);
                    if (status != EvidenceArtifactLengthStatus.Exact || digest != file.Sha256)
                        throw new IOException("A final coverage report path is already occupied by different bytes.");
                }
                continue;
            }

            var stagingName = $".report-{Guid.NewGuid():N}.tmp";
            string? stagedIdentity = null;
            try
            {
                using (var destination = destinationParent.OpenArtifactFile(stagingName, createNew: true))
                {
                    stagedIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(destination.SafeFileHandle);
                    var (status, digest) = await EvidenceArtifactStream.HashAsync(input, file.LengthBytes, cancellationToken, destination).ConfigureAwait(false);
                    if (status == EvidenceArtifactLengthStatus.Long)
                        throw new IOException("A managed coverage report grew while it was being promoted.");
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (status != EvidenceArtifactLengthStatus.Exact || input.Length != file.LengthBytes || digest != file.Sha256)
                        throw new IOException("A managed coverage report changed while it was being promoted.");
                }
                beforeReportPromotion?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                destinationParent.MoveArtifactEntry(stagingName, destinationParent, destinationName,
                    expectedSourceIdentity: stagedIdentity);
                stagedIdentity = null;
            }
            finally
            {
                if (stagedIdentity is not null)
                    destinationParent.DeleteArtifactEntry(stagingName, 1, stagedIdentity);
            }
        }
    }

    private static void ValidateOrdinaryReportSet(string sourceRoot, CoverageEvidenceExecutionResult execution,
        IReadOnlyList<CoverageReportFile> files, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new EvidencePathPrefixes();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CoverageReportFile.ValidatePath(file.RelativePath);
            if (file.LengthBytes < 0 || !file.HasCanonicalDigest)
                throw new InvalidDataException("The managed coverage publication metadata is invalid.");
            if (!paths.Add(file.RelativePath)) throw new InvalidDataException("The managed coverage publication paths are duplicated.");
            if (!prefixes.TryAdd(file.RelativePath))
                throw new InvalidDataException("The managed coverage publication paths collide.");
        }

        CoverageReportSet.ValidateOrdinaryDelivery(sourceRoot, paths, execution, cancellationToken);
    }

    private static CoverageRunOutputLease OpenOrCreateArtifactDirectory(CoverageRunOutputLease root,
        IReadOnlyList<string> components)
    {
        var current = root;
        var ownsCurrent = false;
        try
        {
            foreach (var component in components)
            {
                CoverageRunOutputLease.ValidateArtifactComponent(component);
                var childPath = Path.Join(current.ArtifactDirectoryPath, component);
                var child = CoverageRunOutputLease.AcquireArtifactDirectory(childPath, createMissing: false, requirePrivate: false)
                    ?? current.CreateArtifactDirectory(component);
                if (ownsCurrent) current.Dispose();
                current = child;
                ownsCurrent = true;
            }
            if (!ownsCurrent) throw new InvalidDataException("A managed coverage report has no parent directory.");
            return current;
        }
        catch
        {
            if (ownsCurrent) current.Dispose();
            throw;
        }
    }

    private static string? RebaseReportPath(string? diagnostic, string executionRoot, string finalRoot)
    {
        if (diagnostic is null) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return diagnostic.Replace(Path.GetFullPath(executionRoot), Path.GetFullPath(finalRoot), comparison);
    }
}

/// <summary>Internal coordinator result: ordinary coverage outcome plus complete optional receipt eligibility.</summary>
/// <param name="Result">Fresh outcome, preserving Passed even when optional custody is unavailable.</param>
/// <param name="Inventory">Complete versioned set; null means no receipt may be published.</param>
/// <param name="InventoryIneligibilityReason">Bounded reason code, containing no raw paths or exception messages.</param>
internal sealed record CoverageEvidenceProducerExecutionResult(EvidenceProducerResult Result,
    CoverageEvidenceInventory? Inventory, string? InventoryIneligibilityReason);
