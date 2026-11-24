namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>
/// Caller-owned streams used for coverage progress and diagnostics.
/// </summary>
/// <remarks>
/// The coverage core never disposes, flushes, or retains these writers after an operation returns.
/// Normal progress is written to <see cref="Output"/> and watchdog or critical diagnostics are written to
/// <see cref="Error"/>.
/// </remarks>
internal sealed record CoverageTextWriters(TextWriter Output, TextWriter Error)
{
    /// <summary>
    /// Validates the supplied writer pair before it enters the core.
    /// </summary>
    public static CoverageTextWriters Create(TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        return new CoverageTextWriters(output, error);
    }
}

/// <summary>
/// Represents a stable execution failure raised by the private coverage core.
/// </summary>
/// <remarks>
/// Command adapters map this exception to their host-specific error contract. The rendered message deliberately
/// preserves the existing ASCOV diagnostic text, while <see cref="ExitCode"/> preserves terminal watchdog exit 124.
/// </remarks>
internal sealed class CoverageExecutionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CoverageExecutionException"/> class.
    /// </summary>
    public CoverageExecutionException(string message, int? exitCode = null)
        : base(message)
    {
        ExitCode = exitCode;
        Code = TryGetCode(message);
    }

    /// <summary>
    /// Gets the existing stable diagnostic code when the rendered diagnostic starts with one.
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Gets the optional host process exit code.
    /// </summary>
    public int? ExitCode { get; }

    private static string? TryGetCode(string message)
    {
        var separator = message.IndexOf(' ', StringComparison.Ordinal);
        var candidate = separator < 0 ? message : message[..separator];
        return candidate.StartsWith("ASCOV", StringComparison.Ordinal) ? candidate : null;
    }
}

/// <summary>
/// Runs coverage collection and gate evaluation in their required order for first-party Evidence.
/// </summary>
internal sealed class CoverageEvidenceExecutionWorkflow(CoverageRunWorkflow runWorkflow)
{
    /// <summary>Clones the private run engine while preserving its injected generator, clock and test seams.</summary>
    /// <remarks>The decorator applies only to this execution workflow; other invocations keep their original runner.</remarks>
    internal CoverageEvidenceExecutionWorkflow WithProcessRunner(Func<ICoverageRunProcessRunner, ICoverageRunProcessRunner> decorate) =>
        new(runWorkflow.WithProcessRunner(decorate));

    /// <summary>
    /// Runs collection, then evaluates and writes the supplied gate only after a successful collection.
    /// </summary>
    /// <param name="runRequest">Existing managed collection settings.</param>
    /// <param name="gateRequest">Gate to evaluate after collection.</param>
    /// <param name="writers">Caller-owned diagnostic streams.</param>
    /// <param name="cancellationToken">Invocation cancellation.</param>
    /// <param name="captureOrdinaryPublications">Capture the managed session's closed publications for ordinary report promotion independently of receipt custody limits.</param>
    public async Task<CoverageEvidenceExecutionResult> RunAndGateAsync(
        CoverageRunRequest runRequest,
        CoverageGateRequest gateRequest,
        CoverageTextWriters writers,
        CancellationToken cancellationToken,
        bool captureOrdinaryPublications = false)
    {
        ArgumentNullException.ThrowIfNull(runRequest);
        ArgumentNullException.ThrowIfNull(gateRequest);
        ArgumentNullException.ThrowIfNull(writers);

        var root = Path.GetDirectoryName(Path.GetFullPath(runRequest.SolutionPath ?? Directory.GetCurrentDirectory()))
            ?? Directory.GetCurrentDirectory();
        var session = runRequest.ArtifactSession;
        var ownsSession = session is null;
        if (captureOrdinaryPublications) session?.Inventory.EnableOrdinaryCapture();
        async Task<CoverageArtifactSession> CreateSessionAsync(CancellationToken token)
        {
            if (session is null)
            {
                session = await CoverageArtifactSession.BeginAsync(Path.GetFullPath(runRequest.OutputDirectory), root,
                    runRequest.Clean, writers, TimeProvider.System, token).ConfigureAwait(false);
            }
            if (captureOrdinaryPublications) session.Inventory.EnableOrdinaryCapture();
            return session;
        }

        if (session is null) runRequest = runRequest with { ArtifactSessionFactory = CreateSessionAsync };
        try
        {
            var runResult = await runWorkflow.RunAsync(runRequest, writers, cancellationToken).ConfigureAwait(false);
            if (!runResult.Success) return new CoverageEvidenceExecutionResult(runResult, null);

            // Dry-run returns before the collection workflow requests its session; gate output still needs
            // a compatible context, acquired only after run preflight has completed.
            session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            gateRequest = gateRequest with { ArtifactSession = session };
            var gateResult = await CoverageGateEvaluator.EvaluateAsync(gateRequest, cancellationToken).ConfigureAwait(false);
            await CoverageGateReportWriter.WriteAsync(gateResult, gateRequest, cancellationToken).ConfigureAwait(false);
            using var gateContext = await CoverageArtifactGateContext.AcquireAsync(gateRequest, writers.Error, cancellationToken).ConfigureAwait(false);
            await gateContext.RecordReportsAsync(gateRequest, gateResult, cancellationToken).ConfigureAwait(false);
            if (runRequest.DryRun)
            {
                await session.CompleteAsync("success", CoverageArtifactInventory.Provenance(
                    runRequest.CoverageDriver.ToString(), runRequest.Configuration, runRequest.TestProjects,
                    runRequest.IncludeFilter, runRequest.ExcludeFilter)).ConfigureAwait(false);
            }
            var result = new CoverageEvidenceExecutionResult(runResult, gateResult);
            if (!captureOrdinaryPublications) return result;

            IReadOnlyList<CoverageArtifactInventoryEntry> publications;
            try
            {
                session.VerifyBinding();
                publications = session.Inventory.CaptureOrdinary(cancellationToken);
            }
            catch (Exception exception) when (CoverageArtifactSession.IsOptionalFailure(exception)
                || exception is System.Collections.Generic.KeyNotFoundException or OverflowException or CoverageEvidenceInventoryException)
            {
                var reason = exception is CoverageEvidenceInventoryException failure
                    ? failure.Reason : "inventory-unavailable";
                return result with
                {
                    OrdinaryPublicationIneligibilityReason = reason,
                };
            }

            return result with { OrdinaryPublications = publications };
        }
        finally
        {
            if (ownsSession) session?.Dispose();
        }
    }
}

/// <summary>
/// Captures the two ordered operation results produced for an Evidence coverage declaration.
/// </summary>
internal sealed record CoverageEvidenceExecutionResult
{
    /// <summary>
    /// Initializes the ordered results from one Evidence coverage execution.
    /// </summary>
    /// <param name="run">The nonnull collection result that always completes before gate evaluation is considered.</param>
    /// <param name="gate">
    /// The gate result when collection succeeds; otherwise null because gate evaluation and report writing are skipped.
    /// </param>
    public CoverageEvidenceExecutionResult(CoverageRunResult run, CoverageGateResult? gate)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Gate = gate;
    }

    /// <summary>
    /// Gets the nonnull coverage collection result.
    /// </summary>
    public CoverageRunResult Run { get; }

    /// <summary>
    /// Gets the optional coverage-gate result produced after successful collection.
    /// </summary>
    /// <remarks>
    /// This value is null when <see cref="Run"/> reports failure because the workflow deliberately skips gate
    /// evaluation and report writing. Callers must check <see cref="CoverageRunResult.Success"/> and handle a null
    /// value before accessing gate details.
    /// </remarks>
    public CoverageGateResult? Gate { get; }

    /// <summary>Gets the managed session's closed publications for ordinary report promotion, without receipt limits.</summary>
    internal IReadOnlyList<CoverageArtifactInventoryEntry>? OrdinaryPublications { get; init; }

    /// <summary>Gets a bounded reason when the managed session could not close its ordinary publication set.</summary>
    internal string? OrdinaryPublicationIneligibilityReason { get; init; }
}
