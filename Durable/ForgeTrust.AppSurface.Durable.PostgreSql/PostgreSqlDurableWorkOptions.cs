namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Controls PostgreSQL Work acceptance for one validated store and runtime epoch.</summary>
public sealed record PostgreSqlDurableWorkOptions
{
    /// <summary>Initializes validated Work acceptance options.</summary>
    /// <param name="runtimeEpoch">Non-empty active runtime epoch returned by schema status.</param>
    /// <param name="expectedStoreId">Non-empty durable store identity returned by schema status.</param>
    /// <param name="wakeNotificationMode">Whether acceptance emits a metadata-only wake hint.</param>
    public PostgreSqlDurableWorkOptions(
        Guid runtimeEpoch,
        Guid expectedStoreId,
        PostgreSqlDurableWakeNotificationMode wakeNotificationMode = PostgreSqlDurableWakeNotificationMode.Disabled)
    {
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        if (expectedStoreId == Guid.Empty)
        {
            throw new ArgumentException("The expected durable store id must not be empty.", nameof(expectedStoreId));
        }

        if (!Enum.IsDefined(wakeNotificationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(wakeNotificationMode));
        }

        RuntimeEpoch = runtimeEpoch;
        ExpectedStoreId = expectedStoreId;
        WakeNotificationMode = wakeNotificationMode;
    }

    /// <summary>Gets the active out-of-band recovery epoch.</summary>
    public Guid RuntimeEpoch { get; }

    /// <summary>Gets the expected physical durable store identity.</summary>
    public Guid ExpectedStoreId { get; }

    /// <summary>Gets whether acceptance emits a metadata-only PostgreSQL wake hint.</summary>
    public PostgreSqlDurableWakeNotificationMode WakeNotificationMode { get; }
}

/// <summary>Controls advisory PostgreSQL wake notifications after Work acceptance.</summary>
public enum PostgreSqlDurableWakeNotificationMode
{
    /// <summary>Do not emit a wake notification. Authoritative polling remains available.</summary>
    Disabled = 0,

    /// <summary>Emit a metadata-only wake hint. Consumers must still poll authoritative due state.</summary>
    Enabled = 1,
}
