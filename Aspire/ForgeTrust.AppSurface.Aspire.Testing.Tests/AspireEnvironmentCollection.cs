using Xunit;

/// <summary>
/// Defines the non-parallel xUnit collection for tests that mutate Aspire process-wide environment or exit-code state.
/// </summary>
/// <remarks>
/// Every test that changes Aspire-related process environment variables or process exit-code state must join this
/// collection so it cannot run in parallel with another process-state test.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AspireEnvironmentCollection
{
    /// <summary>
    /// Gets the collection name used by process-state tests.
    /// </summary>
    public const string Name = "Aspire process environment";
}
