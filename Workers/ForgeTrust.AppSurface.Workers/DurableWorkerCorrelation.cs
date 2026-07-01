namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Correlates one durable worker operation across claims, executor activity, completion facts, and projection repair.
/// </summary>
/// <remarks>
/// Correlation values should be stable safe identifiers. Do not use provider URLs, raw payload ids that expose private
/// content, credentials, email bodies, prompts, or model output as correlation values.
/// </remarks>
public sealed record DurableWorkerCorrelation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableWorkerCorrelation"/> class.
    /// </summary>
    /// <param name="workerName">Stable worker or chain name.</param>
    /// <param name="workId">Stable app-owned work identifier.</param>
    /// <param name="instanceId">Durable runtime instance identifier.</param>
    /// <param name="attemptId">Stable attempt, fence, or generation identifier.</param>
    /// <exception cref="ArgumentException">Thrown when any value is null, empty, or whitespace.</exception>
    public DurableWorkerCorrelation(string workerName, string workId, string instanceId, string attemptId)
    {
        WorkerName = RequireText(workerName, nameof(workerName));
        WorkId = RequireText(workId, nameof(workId));
        InstanceId = RequireText(instanceId, nameof(instanceId));
        AttemptId = RequireText(attemptId, nameof(attemptId));
    }

    /// <summary>
    /// Gets the stable worker or chain name.
    /// </summary>
    public string WorkerName { get; }

    /// <summary>
    /// Gets the stable app-owned work identifier.
    /// </summary>
    public string WorkId { get; }

    /// <summary>
    /// Gets the durable runtime instance identifier.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// Gets the attempt, fence, or generation identifier.
    /// </summary>
    public string AttemptId { get; }

    /// <summary>
    /// Validates and trims a durable worker identifier.
    /// </summary>
    /// <param name="value">Identifier value to validate.</param>
    /// <param name="paramName">Caller parameter name used in thrown validation exceptions.</param>
    /// <returns>The trimmed identifier value.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is null, empty, or whitespace.</exception>
    internal static string RequireText(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Durable worker identifiers must not be null, empty, or whitespace.", paramName);
        }

        return value.Trim();
    }
}
