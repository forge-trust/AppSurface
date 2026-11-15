using System.Security.Cryptography;
using System.Text.Json;
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
    /// <param name="artifactWriter">Optional fresh bounded writer in separate custody staging; requires an explicit
    /// coverage-reports multi-file slot. Omission leaves ordinary execution unchanged.</param>
    /// <param name="childLifetime">Optional coordinator-owned cooperative session. Used only when the workload explicitly
    /// opts into CooperativeChildLifetime; the coordinator closes enrollment and joins actual child work.</param>
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
    /// </remarks>
    public async Task<EvidenceProducerResult> RunAsync(
        EvidenceProducerDeclaration producer,
        string? solutionPath,
        string outputDirectory,
        EvidenceDiffSnapshot? diffSnapshot,
        CoverageTextWriters writers,
        CancellationToken cancellationToken,
        EvidenceArtifactWriter? artifactWriter = null,
        EvidenceChildLifetimeSession? childLifetime = null) =>
        (await RunWithInventoryAsync(producer, solutionPath, outputDirectory, diffSnapshot, writers,
            cancellationToken, artifactWriter, childLifetime).ConfigureAwait(false)).Result;

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
    internal async Task<CoverageEvidenceProducerExecutionResult> RunWithInventoryAsync(
        EvidenceProducerDeclaration producer,
        string? solutionPath,
        string outputDirectory,
        EvidenceDiffSnapshot? diffSnapshot,
        CoverageTextWriters writers,
        CancellationToken cancellationToken,
        EvidenceArtifactWriter? artifactWriter = null,
        EvidenceChildLifetimeSession? childLifetime = null,
        string? finalReportRoot = null)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(writers);

        CoverageEvidenceInventory? inventory = null;
        string? inventoryReason = artifactWriter is null ? "inventory-not-requested" : null;
        CoverageEvidenceProducerExecutionResult Finish(EvidenceProducerResult result) => result.Outcome == EvidenceProducerOutcome.Passed
            ? new(result, inventory, inventoryReason)
            : new(result, null, "inventory-result-not-passed");

        if (!string.Equals(producer.Kind, "coverage", StringComparison.OrdinalIgnoreCase))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], $"Producer kind '{producer.Kind}' requires an explicit consumer EvidenceHost registration."));
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

        if ((producer.CoverageGate.MinPatchLinePercent.HasValue || producer.CoverageGate.MinPatchBranchPercent.HasValue)
            && diffSnapshot is null)
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Unavailable, [], "The declared patch coverage gate requires --diff-file so the plan and coverage gate use the same explicit CI diff."));
        }

        var cooperative = producer.CoverageExecution?.CooperativeChildLifetime == true;
        var useLifetime = cooperative && childLifetime is not null;
        var captureInventory = artifactWriter is not null && useLifetime;
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
            var result = await workflow.RunAndGateAsync(
                CreateRunRequest(solutionPath, outputDirectory, producer.CoverageExecution) with { ProducerDeadline = producerDeadline, ArtifactRetention = producer.CoverageArtifactRetention },
                CreateGateRequest(outputDirectory, diffSnapshot, producer.CoverageGate),
                writers,
                executionCancellation.Token, captureInventory: captureInventory,
                captureOrdinaryPublications: finalReportRoot is not null).ConfigureAwait(false);
            if (!result.Run.Success)
            {
                return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], "The existing AppSurface coverage workflow reported a failed test, merge, or artifact step."));
            }

            var gateResult = result.Gate ?? throw new InvalidOperationException("A successful coverage run must produce a gate result.");
            var ordinary = gateResult.Passed
                ? new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed, producer.AssertionIds, $"Coverage gate passed: {gateResult.MarkdownReportPath}")
                : new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"Coverage gate did not meet its declared threshold. See {gateResult.MarkdownReportPath}.");
            inventory = result.Inventory;
            inventoryReason = result.InventoryIneligibilityReason ?? inventoryReason;
            if (gateResult.Passed && artifactWriter is not null && inventory is not null)
            {
                try
                {
                    var slot = producer.ArtifactSlots.SingleOrDefault(slot => slot.LogicalName == CoverageEvidenceInventory.LogicalName)
                        ?? throw new CoverageEvidenceInventoryException("inventory-slot-missing");
                    var artifacts = await inventory.WriteAsync(artifactWriter, slot, outputDirectory,
                        executionCancellation.Token).ConfigureAwait(false);
                    ordinary = ordinary with { Artifacts = artifacts };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (IsNonFatal(exception))
                {
                    inventory = null;
                    inventoryReason = exception is CoverageEvidenceInventoryException failure
                        ? failure.Reason : "inventory-custody-unavailable";
                }
            }
            if (finalReportRoot is not null)
            {
                if (result.OrdinaryPublications is { } ordinaryPublications
                    && result.OrdinaryPublicationIneligibilityReason is null)
                {
                    var promotion = await TryPreserveOrdinaryReportsAsync(outputDirectory, finalReportRoot, result,
                        ordinaryPublications, executionCancellation.Token).ConfigureAwait(false);
                    if (promotion.Complete)
                        ordinary = ordinary with { Diagnostic = RebaseReportPath(ordinary.Diagnostic, outputDirectory, finalReportRoot) };
                    else
                    {
                        inventory = null;
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
                    inventory = null;
                    inventoryReason = result.OrdinaryPublicationIneligibilityReason ?? "ordinary-publications-unavailable";
                }
            }
            return Finish(ordinary);
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
            catch (Exception ex) when (IsNonFatal(ex))
            {
                // The timeout outcome is authoritative; a missing or unsafe hint artifact is ignored.
            }
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.TimedOut, [], "The existing AppSurface coverage workflow exceeded its bounded execution window." + hint));
        }
        catch (OperationCanceledException)
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], "The existing AppSurface coverage workflow failed with OperationCanceledException."));
        }
        catch (CoverageExecutionException exception) when (IsNonFatal(exception))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"The existing AppSurface coverage workflow failed with {exception.Code ?? exception.GetType().Name}."));
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Finish(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Failed, [], $"The existing AppSurface coverage workflow failed with {exception.GetType().Name}."));
        }
    }

    private static CoverageRunRequest CreateRunRequest(
        string solutionPath,
        string outputDirectory,
        EvidenceCoverageExecutionOptions? execution) => new(
        solutionPath,
        TestProjects: [],
        ExcludeTestProjects: [],
        OutputDirectory: Path.Join(outputDirectory, "coverage"),
        Configuration: (execution ?? new EvidenceCoverageExecutionOptions()).Configuration,
        Parallelism: (execution ?? new EvidenceCoverageExecutionOptions()).Parallelism,
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
        TestResults: (execution ?? new EvidenceCoverageExecutionOptions()).TestResults switch
        {
            EvidenceCoverageTestResultFormat.None => CoverageRunTestResultFormat.None,
            EvidenceCoverageTestResultFormat.Junit => CoverageRunTestResultFormat.Junit,
            _ => throw new ArgumentOutOfRangeException(nameof(execution)),
        },
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
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return (false, "ordinary-report-promotion-unavailable");
        }
    }

    private static async Task PreserveOrdinaryReportsAsync(
        string sourceRoot,
        string finalRoot,
        CoverageEvidenceExecutionResult execution,
        IReadOnlyList<CoverageArtifactInventoryEntry> publications,
        CancellationToken cancellationToken)
    {
        if (!execution.Run.Success || execution.Gate is null)
            throw new InvalidDataException("Ordinary reports require successful collection and a completed gate decision.");
        var normalizedFinalRoot = Path.GetFullPath(finalRoot);
        var normalizedSourceRoot = Path.GetFullPath(sourceRoot);
        var files = publications.Select(publication => new OrdinaryCoverageReport(
                "coverage/" + publication.Path.Replace('\\', '/'), publication.Length, publication.Sha256))
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
        ValidateOrdinaryReportSet(normalizedSourceRoot, execution, files, cancellationToken);

        using var finalRootLease = CoverageRunOutputLease.AcquireArtifactDirectory(
            normalizedFinalRoot, createMissing: true, requirePrivate: false)
            ?? throw new IOException("The final coverage report root is unavailable.");
        var sourceCoverageRoot = Path.Join(normalizedSourceRoot, "coverage");
        var buffer = new byte[64 * 1024];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var components = file.RelativePath.Split('/');
            using var destinationParent = OpenOrCreateArtifactDirectory(finalRootLease, components[..^1]);
            var destinationName = components[^1];
            using var input = CoverageRunArtifactReader.OpenRegularFile(normalizedSourceRoot, sourceCoverageRoot,
                Path.Join(normalizedSourceRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (input.Length != file.LengthBytes)
                throw new IOException("A managed coverage report changed before promotion.");

            FileStream? existing = null;
            try { existing = destinationParent.OpenArtifactFile(destinationName, createNew: false, requirePrivate: false); }
            catch (FileNotFoundException) { }
            if (existing is not null)
            {
                using (existing)
                {
                    if (existing.Length != file.LengthBytes || Hash(existing, cancellationToken) != file.Sha256)
                        throw new IOException("A final coverage report path is already occupied by different bytes.");
                }
                continue;
            }

            using var destination = destinationParent.OpenArtifactFile(destinationName, createNew: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                copied = checked(copied + read);
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (copied != file.LengthBytes || input.Length != file.LengthBytes || digest != file.Sha256)
                throw new IOException("A managed coverage report changed while it was being promoted.");
        }
    }

    private static void ValidateOrdinaryReportSet(string sourceRoot, CoverageEvidenceExecutionResult execution,
        IReadOnlyList<OrdinaryCoverageReport> files, CancellationToken cancellationToken)
    {
        var gate = execution.Gate ?? throw new InvalidDataException("A successful coverage run must produce a gate result.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var pathComponents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CoverageEvidenceInventory.ValidatePath(file.RelativePath);
            if (file.LengthBytes < 0 || file.Sha256.Length != 64 || file.Sha256.Any(static value => !char.IsAsciiHexDigitLower(value)))
                throw new InvalidDataException("The managed coverage publication metadata is invalid.");
            if (!paths.Add(file.RelativePath)) throw new InvalidDataException("The managed coverage publication paths are duplicated.");
            var prefix = string.Empty;
            foreach (var component in file.RelativePath.Split('/'))
            {
                prefix = prefix.Length == 0 ? component : prefix + "/" + component;
                if (pathComponents.TryGetValue(prefix, out var existing) && existing != prefix)
                    throw new InvalidDataException("The managed coverage publication paths collide.");
                pathComponents[prefix] = prefix;
            }
        }

        void Require(string relative)
        {
            CoverageEvidenceInventory.ValidatePath(relative);
            if (!paths.Contains(relative)) throw new InvalidDataException("A required ordinary coverage report is missing from the closed publication set.");
        }

        var root = Path.GetFullPath(sourceRoot);
        foreach (var path in new[] { execution.Run.CoveragePath, gate.JsonReportPath,
            gate.MarkdownReportPath, gate.PatchTargetsJsonPath, gate.PatchTargetsMarkdownPath }.OfType<string>())
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/');
            Require(relative);
        }
        foreach (var relative in new[] { "coverage/coverage.cobertura.xml", "coverage/coverage-gate.json",
            "coverage/coverage-gate.md", "coverage/timings.json", "coverage/summary.txt" })
            Require(relative);

        using var timings = CoverageRunArtifactReader.OpenRegularFile(root, Path.Join(root, "coverage"),
            Path.Join(root, "coverage", "timings.json"));
        using var timingDocument = JsonDocument.Parse(timings);
        foreach (var project in timingDocument.RootElement.GetProperty("projects").EnumerateArray())
        {
            var slug = project.GetProperty("slug").GetString()
                ?? throw new InvalidDataException("Coverage timings contain a project without a slug.");
            Require($"coverage/projects/{slug}/{CoverageProjectManifest.FileName}");
            Require($"coverage/projects/{slug}/coverage.cobertura.xml");
            using var projectManifest = CoverageRunArtifactReader.OpenRegularFile(root, Path.Join(root, "coverage"),
                Path.Join(root, "coverage", "projects", slug, CoverageProjectManifest.FileName));
            using var projectDocument = JsonDocument.Parse(projectManifest);
            if (projectDocument.RootElement.GetProperty("schemaVersion").GetInt32() != 1
                || projectDocument.RootElement.GetProperty("slug").GetString() != slug)
                throw new InvalidDataException("A managed coverage project manifest does not match its timing entry.");
            foreach (var report in project.GetProperty("testResults").EnumerateArray())
            {
                var reportPath = report.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("Coverage timings contain a test result without a path.");
                Require("coverage/" + reportPath);
            }
        }
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

    private static string Hash(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string? RebaseReportPath(string? diagnostic, string executionRoot, string finalRoot)
    {
        if (diagnostic is null) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return diagnostic.Replace(Path.GetFullPath(executionRoot), Path.GetFullPath(finalRoot), comparison);
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;

    private sealed record OrdinaryCoverageReport(string RelativePath, long LengthBytes, string Sha256);
}

/// <summary>Internal coordinator result: ordinary coverage outcome plus complete optional receipt eligibility.</summary>
/// <param name="Result">Fresh outcome, preserving Passed even when optional custody is unavailable.</param>
/// <param name="Inventory">Complete versioned set; null means no receipt may be published.</param>
/// <param name="InventoryIneligibilityReason">Bounded reason code, containing no raw paths or exception messages.</param>
internal sealed record CoverageEvidenceProducerExecutionResult(EvidenceProducerResult Result,
    CoverageEvidenceInventory? Inventory, string? InventoryIneligibilityReason);
