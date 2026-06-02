namespace ForgeTrust.AppSurface.Flow;

/// <summary>
/// Configures local AppSurface Flow execution behavior.
/// </summary>
/// <remarks>
/// These options affect the in-memory runner only. Durable hosts should keep replay, persistence, external events, and
/// timer policy in their durable orchestration layer and use this package's contracts for typed graph behavior.
/// </remarks>
public sealed class AppSurfaceFlowOptions
{
    /// <summary>
    /// Gets or sets the maximum number of synchronous node transitions allowed during one in-memory run.
    /// </summary>
    /// <remarks>
    /// The default value, 1000, protects local tests and examples from accidental infinite loops. Set a higher value for
    /// intentionally dense local workflows. Values less than 1 are treated as configuration errors when a runner starts.
    /// </remarks>
    public int MaxStepsPerRun { get; set; } = 1000;
}
