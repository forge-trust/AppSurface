namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Reports an explicit compare-and-swap rotation of the store-wide recovery epoch.</summary>
/// <param name="PreviousEpoch">Epoch that was active before rotation.</param>
/// <param name="ActiveEpoch">New epoch that fences every prior worker.</param>
/// <param name="RotatedAtUtc">Authoritative PostgreSQL rotation timestamp.</param>
public sealed record DurableRuntimeEpochRotationResult(
    Guid PreviousEpoch,
    Guid ActiveEpoch,
    DateTimeOffset RotatedAtUtc);
