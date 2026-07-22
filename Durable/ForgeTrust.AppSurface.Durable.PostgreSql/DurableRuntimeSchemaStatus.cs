namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Reports the durable PostgreSQL schema identity, version, epoch, and compatibility.</summary>
public sealed record DurableRuntimeSchemaStatus
{
    /// <summary>Initializes an immutable schema status snapshot.</summary>
    public DurableRuntimeSchemaStatus(
        DurableRuntimeSchemaCompatibility compatibility,
        Guid storeId,
        Guid? activeRuntimeEpoch,
        int installedVersion,
        int requiredVersion,
        int minimumReaderVersion,
        int maximumReaderVersion,
        int minimumWriterVersion,
        int maximumWriterVersion,
        IReadOnlyList<int> appliedVersions,
        IReadOnlyList<int> pendingVersions,
        string? problem)
    {
        ArgumentNullException.ThrowIfNull(appliedVersions);
        ArgumentNullException.ThrowIfNull(pendingVersions);
        Compatibility = compatibility;
        StoreId = storeId;
        ActiveRuntimeEpoch = activeRuntimeEpoch;
        InstalledVersion = installedVersion;
        RequiredVersion = requiredVersion;
        MinimumReaderVersion = minimumReaderVersion;
        MaximumReaderVersion = maximumReaderVersion;
        MinimumWriterVersion = minimumWriterVersion;
        MaximumWriterVersion = maximumWriterVersion;
        AppliedVersions = appliedVersions.ToArray();
        PendingVersions = pendingVersions.ToArray();
        Problem = problem;
    }

    /// <summary>Gets the compatibility verdict.</summary>
    public DurableRuntimeSchemaCompatibility Compatibility { get; }

    /// <summary>Gets the immutable store identity, or empty when unavailable.</summary>
    public Guid StoreId { get; }

    /// <summary>Gets the active recovery epoch, or null before explicit initialization.</summary>
    public Guid? ActiveRuntimeEpoch { get; }

    /// <summary>Gets the highest installed migration version.</summary>
    public int InstalledVersion { get; }

    /// <summary>Gets the schema version required by this package.</summary>
    public int RequiredVersion { get; }

    /// <summary>Gets the oldest runtime protocol allowed to read.</summary>
    public int MinimumReaderVersion { get; }

    /// <summary>Gets the newest runtime protocol allowed to read.</summary>
    public int MaximumReaderVersion { get; }

    /// <summary>Gets the oldest runtime protocol allowed to write.</summary>
    public int MinimumWriterVersion { get; }

    /// <summary>Gets the newest runtime protocol allowed to write.</summary>
    public int MaximumWriterVersion { get; }

    /// <summary>Gets an immutable copy of ordered applied migration versions.</summary>
    public IReadOnlyList<int> AppliedVersions { get; }

    /// <summary>Gets an immutable copy of ordered pending migration versions.</summary>
    public IReadOnlyList<int> PendingVersions { get; }

    /// <summary>Gets the actionable incompatibility explanation, when present.</summary>
    public string? Problem { get; }

    /// <summary>Gets whether schema reads and writes may begin.</summary>
    public bool IsCompatible => Compatibility == DurableRuntimeSchemaCompatibility.Compatible;
}
