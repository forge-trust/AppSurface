namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Describes a registered AppSurface product-event contract.
/// </summary>
public sealed class AppSurfaceProductEventContract
{
    /// <summary>
    /// Creates an event contract for the typed product-intelligence registry.
    /// </summary>
    /// <param name="name">Stable event name.</param>
    /// <param name="lifecycle">Current lifecycle state.</param>
    /// <param name="purpose">Decision the event is meant to support.</param>
    /// <param name="owner">Owning AppSurface component or package.</param>
    /// <param name="retentionExpectation">Expected retention class for downstream sinks.</param>
    /// <param name="properties">Allowed property schema.</param>
    /// <param name="forbiddenExamples">Examples of values or property shapes that must not be captured.</param>
    public AppSurfaceProductEventContract(
        string name,
        AppSurfaceProductEventLifecycle lifecycle,
        string purpose,
        string owner,
        string retentionExpectation,
        IEnumerable<AppSurfaceProductEventPropertyContract> properties,
        IEnumerable<string> forbiddenExamples)
    {
        Name = AppSurfaceProductEventMetadata.RequireIdentifier(name, nameof(name));
        Lifecycle = lifecycle;
        Purpose = AppSurfaceProductEventMetadata.RequireText(purpose, nameof(purpose));
        Owner = AppSurfaceProductEventMetadata.RequireText(owner, nameof(owner));
        RetentionExpectation = AppSurfaceProductEventMetadata.RequireText(
            retentionExpectation,
            nameof(retentionExpectation));
        Properties = AppSurfaceProductEventMetadata.NormalizeContracts(properties, nameof(properties));
        ForbiddenExamples = AppSurfaceProductEventMetadata.NormalizeTextList(
            forbiddenExamples,
            nameof(forbiddenExamples));
    }

    /// <summary>
    /// Gets the stable event name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the current lifecycle state.
    /// </summary>
    public AppSurfaceProductEventLifecycle Lifecycle { get; }

    /// <summary>
    /// Gets the decision the event is meant to support.
    /// </summary>
    public string Purpose { get; }

    /// <summary>
    /// Gets the owning AppSurface component or package.
    /// </summary>
    public string Owner { get; }

    /// <summary>
    /// Gets the expected retention class for downstream sinks.
    /// </summary>
    public string RetentionExpectation { get; }

    /// <summary>
    /// Gets the allowed property schema.
    /// </summary>
    public IReadOnlyList<AppSurfaceProductEventPropertyContract> Properties { get; }

    /// <summary>
    /// Gets examples of values or property shapes that must not be captured.
    /// </summary>
    public IReadOnlyList<string> ForbiddenExamples { get; }
}
