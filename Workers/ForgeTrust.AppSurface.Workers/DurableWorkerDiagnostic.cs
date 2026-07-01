namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Privacy-safe diagnostic details for a durable worker outcome.
/// </summary>
/// <remarks>
/// Diagnostics are suitable for logs, repair dashboards, and durable records. Keep them explanatory but sanitized:
/// include stable codes, cause and fix guidance, safe counts, and opaque ids. Do not include raw provider payloads,
/// tokens, credentials, prompts, model output, email bodies, attachments, or unclassified sensitive values.
/// </remarks>
public sealed record DurableWorkerDiagnostic
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableWorkerDiagnostic"/> class.
    /// </summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="problem">Short safe description of the problem.</param>
    /// <param name="cause">Safe explanation of the likely cause.</param>
    /// <param name="fix">Safe guidance for automatic repair or operator action.</param>
    /// <param name="retryability">Retry classification for this diagnostic.</param>
    /// <param name="metadata">Optional safe metadata values.</param>
    /// <exception cref="ArgumentException">Thrown when required text or metadata is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="retryability"/> is not defined.</exception>
    /// <exception cref="DurableWorkerUnsafeMetadataException">Thrown when metadata appears unsafe.</exception>
    public DurableWorkerDiagnostic(
        string code,
        string problem,
        string cause,
        string fix,
        DurableWorkerRetryability retryability,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (!Enum.IsDefined(retryability))
        {
            throw new ArgumentOutOfRangeException(nameof(retryability), "Durable worker retryability must be defined.");
        }

        Code = DurableWorkerMetadataSafety.CopySafeDiagnosticText(code, "diagnostic code", nameof(code));
        Problem = DurableWorkerMetadataSafety.CopySafeDiagnosticText(problem, "diagnostic problem", nameof(problem));
        Cause = DurableWorkerMetadataSafety.CopySafeDiagnosticText(cause, "diagnostic cause", nameof(cause));
        Fix = DurableWorkerMetadataSafety.CopySafeDiagnosticText(fix, "diagnostic fix", nameof(fix));
        Retryability = retryability;
        Metadata = DurableWorkerMetadataSafety.CopySafe(metadata);
    }

    /// <summary>
    /// Gets the stable machine-readable diagnostic code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the safe problem description.
    /// </summary>
    public string Problem { get; }

    /// <summary>
    /// Gets the safe cause description.
    /// </summary>
    public string Cause { get; }

    /// <summary>
    /// Gets the safe fix guidance.
    /// </summary>
    public string Fix { get; }

    /// <summary>
    /// Gets the retry classification for this diagnostic.
    /// </summary>
    public DurableWorkerRetryability Retryability { get; }

    /// <summary>
    /// Gets sanitized diagnostic metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
