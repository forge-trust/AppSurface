namespace AspireAppHostExample.Tests;

/// <summary>
/// Runs the sample's live Aspire application lifecycles sequentially within this test assembly.
/// </summary>
/// <remarks>
/// Keep the typed-profile DCP startup and disposal checks in this collection so additional
/// live AppHost cases cannot overlap them. The Evidence regression shares the collection but
/// rejects before startup. This does not change deadlines or serialize other test assemblies.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AspireAppHostIntegrationCollection
{
    /// <summary>Gets the shared collection name for the sample's live AppHost tests.</summary>
    public const string Name = "Aspire AppHost integration";
}
