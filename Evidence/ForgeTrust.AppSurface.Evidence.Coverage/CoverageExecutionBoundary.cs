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
    /// <summary>
    /// Runs collection, then evaluates and writes the supplied gate only after a successful collection.
    /// </summary>
    public async Task<CoverageEvidenceExecutionResult> RunAndGateAsync(
        CoverageRunRequest runRequest,
        CoverageGateRequest gateRequest,
        CoverageTextWriters writers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runRequest);
        ArgumentNullException.ThrowIfNull(gateRequest);
        ArgumentNullException.ThrowIfNull(writers);

        var runResult = await runWorkflow.RunAsync(runRequest, writers, cancellationToken).ConfigureAwait(false);
        if (!runResult.Success)
        {
            return new CoverageEvidenceExecutionResult(runResult, null);
        }

        var gateResult = await CoverageGateEvaluator.EvaluateAsync(gateRequest, cancellationToken).ConfigureAwait(false);
        await CoverageGateReportWriter.WriteAsync(gateResult, gateRequest, cancellationToken).ConfigureAwait(false);
        return new CoverageEvidenceExecutionResult(runResult, gateResult);
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
}
