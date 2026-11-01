namespace AspireAppHostExample.Tests;

/// <summary>
/// Runs the sample's live Aspire application lifecycles sequentially within this test assembly.
/// </summary>
/// <remarks>
/// Both integration classes start DCP and the same web project. Keep those single-application
/// readiness and disposal checks in one collection so unrelated AppHost startup cannot overlap
/// them. This does not change each test's cancellation deadline or serialize other test assemblies.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AspireAppHostIntegrationCollection
{
    /// <summary>Gets the shared collection name for the sample's live AppHost tests.</summary>
    public const string Name = "Aspire AppHost integration";
}
