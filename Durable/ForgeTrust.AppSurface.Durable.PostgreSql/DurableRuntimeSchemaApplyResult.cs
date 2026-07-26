namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Describes one explicit migration application.</summary>
public sealed record DurableRuntimeSchemaApplyResult
{
    /// <summary>Initializes an immutable migration application result.</summary>
    public DurableRuntimeSchemaApplyResult(
        int previousVersion,
        int currentVersion,
        IReadOnlyList<int> appliedVersions)
    {
        ArgumentNullException.ThrowIfNull(appliedVersions);
        PreviousVersion = previousVersion;
        CurrentVersion = currentVersion;
        AppliedVersions = appliedVersions.ToArray();
    }

    /// <summary>Gets the version before application.</summary>
    public int PreviousVersion { get; }

    /// <summary>Gets the version after application.</summary>
    public int CurrentVersion { get; }

    /// <summary>Gets the versions applied by this operation.</summary>
    public IReadOnlyList<int> AppliedVersions { get; }
}
