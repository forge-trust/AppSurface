namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Describes the result of explicitly applying durable PostgreSQL migrations.
/// </summary>
/// <param name="PreviousVersion">Schema version before the operation.</param>
/// <param name="CurrentVersion">Schema version after the operation.</param>
/// <param name="AppliedVersions">Migration versions applied by this operation.</param>
public sealed record DurableRuntimeSchemaApplyResult(
    int PreviousVersion,
    int CurrentVersion,
    IReadOnlyList<int> AppliedVersions);
