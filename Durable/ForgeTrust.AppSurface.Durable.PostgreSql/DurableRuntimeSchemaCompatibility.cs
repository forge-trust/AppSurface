namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Describes whether the installed durable schema can be used by this package.</summary>
public enum DurableRuntimeSchemaCompatibility
{
    /// <summary>The durable schema is not installed.</summary>
    Missing = 0,
    /// <summary>The schema is compatible with reads and writes.</summary>
    Compatible = 1,
    /// <summary>The installed schema is older than this package requires.</summary>
    UpgradeRequired = 2,
    /// <summary>The installed schema excludes this package's protocol version.</summary>
    StoreTooNew = 3,
    /// <summary>Migration metadata is incomplete, altered, or invalid.</summary>
    Inconsistent = 4,
}
