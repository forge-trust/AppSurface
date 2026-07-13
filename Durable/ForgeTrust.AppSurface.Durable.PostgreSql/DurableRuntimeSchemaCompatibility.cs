namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Describes whether the installed PostgreSQL durable schema can be used by this package version.
/// </summary>
public enum DurableRuntimeSchemaCompatibility
{
    /// <summary>
    /// The durable schema is not installed.
    /// </summary>
    Missing = 0,

    /// <summary>
    /// The installed schema is compatible with both reads and writes.
    /// </summary>
    Compatible = 1,

    /// <summary>
    /// The installed schema is valid but older than this package requires.
    /// </summary>
    UpgradeRequired = 2,

    /// <summary>
    /// The installed schema is newer than this package can safely understand.
    /// </summary>
    StoreTooNew = 3,

    /// <summary>
    /// Migration metadata is incomplete, modified, or otherwise inconsistent.
    /// </summary>
    Inconsistent = 4,
}
