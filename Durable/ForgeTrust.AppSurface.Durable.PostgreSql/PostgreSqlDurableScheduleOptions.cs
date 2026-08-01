namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Controls the PostgreSQL Schedule processor's runtime-role and temporal safety fences.
/// </summary>
/// <remarks>
/// The role name is checked with <c>current_user</c> before a Schedule processor sets its scoped RLS setting or bridges
/// an occurrence to Work. The safety window limits how far a single database-clock observation may advance an interval
/// cursor; a larger jump suspends rather than consumes future occurrences.
/// </remarks>
public sealed record PostgreSqlDurableScheduleOptions
{
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Initializes Schedule processing options for one exact runtime login role.
    /// </summary>
    /// <param name="runtimeRole">Exact PostgreSQL login role that may enter scoped Schedule transactions.</param>
    /// <param name="maximumClockAdvance">Largest safe processor-clock advance beyond the cursor.</param>
    /// <param name="leaseDuration">Lease duration for a claimed payload-free Schedule dispatch row.</param>
    public PostgreSqlDurableScheduleOptions(
        string runtimeRole,
        TimeSpan? maximumClockAdvance = null,
        TimeSpan? leaseDuration = null)
    {
        if (string.IsNullOrWhiteSpace(runtimeRole) || runtimeRole.Length > 63 || runtimeRole.Any(char.IsControl))
        {
            throw new ArgumentException("The PostgreSQL runtime role must contain 1 to 63 non-control characters.", nameof(runtimeRole));
        }

        RuntimeRole = runtimeRole;
        MaximumClockAdvance = maximumClockAdvance ?? TimeSpan.FromDays(31);
        LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2);
        if (MaximumClockAdvance <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumClockAdvance));
        }

        if (LeaseDuration <= TimeSpan.Zero || LeaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                $"The Schedule lease duration must be greater than zero and no longer than {MaximumLeaseDuration}.");
        }
    }

    /// <summary>Gets the exact role required before Schedule bridge scope is set.</summary>
    public string RuntimeRole { get; }

    /// <summary>Gets the maximum safe single-pass database-clock advance beyond a stored cursor.</summary>
    public TimeSpan MaximumClockAdvance { get; }

    /// <summary>Gets the dispatcher-owned Schedule discovery lease duration, capped at ten minutes.</summary>
    public TimeSpan LeaseDuration { get; }
}
