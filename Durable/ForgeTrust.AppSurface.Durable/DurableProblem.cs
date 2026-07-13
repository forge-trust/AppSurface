namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Provides privacy-safe, actionable details for a failed durable operation.
/// </summary>
public sealed record DurableProblem
{
    /// <summary>
    /// Initializes a new durable problem.
    /// </summary>
    /// <param name="code">Stable machine-readable code.</param>
    /// <param name="problem">Safe description of what failed.</param>
    /// <param name="cause">Safe description of the likely cause.</param>
    /// <param name="fix">Safe corrective guidance.</param>
    /// <param name="documentationUrl">Canonical documentation URL.</param>
    /// <param name="correlationId">Opaque correlation identifier.</param>
    public DurableProblem(
        string code,
        string problem,
        string cause,
        string fix,
        Uri documentationUrl,
        string correlationId)
    {
        Code = DurableIdentifier.Require(code, nameof(code), 120);
        Problem = DurableIdentifier.RequireText(problem, nameof(problem), 500);
        Cause = DurableIdentifier.RequireText(cause, nameof(cause), 1_000);
        Fix = DurableIdentifier.RequireText(fix, nameof(fix), 1_000);
        DocumentationUrl = documentationUrl ?? throw new ArgumentNullException(nameof(documentationUrl));
        if (!DocumentationUrl.IsAbsoluteUri || DocumentationUrl.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Durable documentation URLs must be absolute HTTP or HTTPS URLs.", nameof(documentationUrl));
        }

        CorrelationId = DurableIdentifier.Require(correlationId, nameof(correlationId), 200);
    }

    /// <summary>
    /// Gets the stable machine-readable code.
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
    /// Gets the safe corrective guidance.
    /// </summary>
    public string Fix { get; }

    /// <summary>
    /// Gets the canonical documentation URL.
    /// </summary>
    public Uri DocumentationUrl { get; }

    /// <summary>
    /// Gets the opaque correlation identifier.
    /// </summary>
    public string CorrelationId { get; }
}

/// <summary>
/// Represents either a successful durable operation value or an actionable problem.
/// </summary>
/// <typeparam name="T">Successful value type.</typeparam>
public sealed record DurableOperationResult<T>
{
    private DurableOperationResult(T? value, DurableProblem? problem)
    {
        Value = value;
        Problem = problem;
    }

    /// <summary>
    /// Gets whether the operation succeeded.
    /// </summary>
    public bool IsSuccess => Problem is null;

    /// <summary>
    /// Gets the successful value, or <see langword="null"/> when the operation failed.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Gets the problem, or <see langword="null"/> when the operation succeeded.
    /// </summary>
    public DurableProblem? Problem { get; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static DurableOperationResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static DurableOperationResult<T> Failure(DurableProblem problem) =>
        new(default, problem ?? throw new ArgumentNullException(nameof(problem)));
}
