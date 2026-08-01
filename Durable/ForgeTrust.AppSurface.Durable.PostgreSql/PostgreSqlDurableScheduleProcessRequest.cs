namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Requests one bounded, manually invoked PostgreSQL Schedule processing pass.
/// </summary>
/// <remarks>
/// This is a passive provider operation. Applications may call it from an external trigger or test, but must not loop
/// it in an ASP.NET request or register hosted work; hosted activation belongs to Slice 6.
/// </remarks>
public sealed record PostgreSqlDurableScheduleProcessRequest
{
    /// <summary>Initializes one bounded Schedule processing pass.</summary>
    /// <param name="leaseOwner">Opaque processor identity used only for a short dispatch lease.</param>
    /// <param name="maximumSchedules">Maximum dispatch rows this pass may claim; defaults to one.</param>
    public PostgreSqlDurableScheduleProcessRequest(string leaseOwner, int maximumSchedules = 1)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 200 || leaseOwner.Any(char.IsControl))
        {
            throw new ArgumentException("The Schedule lease owner must contain 1 to 200 non-control characters.", nameof(leaseOwner));
        }

        if (maximumSchedules is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSchedules));
        }

        LeaseOwner = leaseOwner;
        MaximumSchedules = maximumSchedules;
    }

    /// <summary>Gets the opaque processor identity recorded on a transient dispatch lease.</summary>
    public string LeaseOwner { get; }

    /// <summary>Gets the maximum number of Schedule rows the pass may claim.</summary>
    public int MaximumSchedules { get; }
}

/// <summary>
/// Reports the durable facts produced by one bounded Schedule processing pass.
/// </summary>
public sealed record PostgreSqlDurableScheduleProcessResult
{
    /// <summary>Initializes the result of one bounded Schedule processing pass.</summary>
    public PostgreSqlDurableScheduleProcessResult(
        int claimedSchedules,
        int recordedOccurrences,
        int materializedWorkTargets,
        int suspendedSchedules)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(claimedSchedules);
        ArgumentOutOfRangeException.ThrowIfNegative(recordedOccurrences);
        ArgumentOutOfRangeException.ThrowIfNegative(materializedWorkTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(suspendedSchedules);
        ClaimedSchedules = claimedSchedules;
        RecordedOccurrences = recordedOccurrences;
        MaterializedWorkTargets = materializedWorkTargets;
        SuspendedSchedules = suspendedSchedules;
    }

    /// <summary>Gets the number of payload-free dispatch rows claimed by this pass.</summary>
    public int ClaimedSchedules { get; }

    /// <summary>Gets the number of new or coalesced Schedule occurrence facts recorded.</summary>
    public int RecordedOccurrences { get; }

    /// <summary>Gets the number of Work target identities materialized by this pass.</summary>
    public int MaterializedWorkTargets { get; }

    /// <summary>Gets the number of Schedules suspended by a safety fence.</summary>
    public int SuspendedSchedules { get; }
}
