namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Reports the durable PostgreSQL schema version and its compatibility with the running package.
/// </summary>
/// <param name="Compatibility">Compatibility verdict.</param>
/// <param name="InstalledVersion">Highest installed migration, or zero when the schema is absent.</param>
/// <param name="RequiredVersion">Schema version required by this package.</param>
/// <param name="MinimumReaderVersion">Oldest runtime protocol allowed to read the installed schema.</param>
/// <param name="MaximumReaderVersion">Newest runtime protocol allowed to read the installed schema.</param>
/// <param name="MinimumWriterVersion">Oldest runtime protocol allowed to write the installed schema.</param>
/// <param name="MaximumWriterVersion">Newest runtime protocol allowed to write the installed schema.</param>
/// <param name="AppliedVersions">Ordered migration versions recorded by the store.</param>
/// <param name="PendingVersions">Ordered package migration versions not yet applied.</param>
/// <param name="Problem">Actionable explanation when the schema is not compatible.</param>
public sealed record DurableRuntimeSchemaStatus(
    DurableRuntimeSchemaCompatibility Compatibility,
    int InstalledVersion,
    int RequiredVersion,
    int MinimumReaderVersion,
    int MaximumReaderVersion,
    int MinimumWriterVersion,
    int MaximumWriterVersion,
    IReadOnlyList<int> AppliedVersions,
    IReadOnlyList<int> PendingVersions,
    string? Problem)
{
    /// <summary>
    /// Gets a value indicating whether runtime claims and writes may begin.
    /// </summary>
    public bool IsCompatible => Compatibility == DurableRuntimeSchemaCompatibility.Compatible;
}
