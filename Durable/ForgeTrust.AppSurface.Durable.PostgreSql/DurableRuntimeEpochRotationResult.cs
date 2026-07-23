namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Reports an explicit compare-and-swap recovery-epoch rotation.</summary>
/// <param name="PreviousEpoch">Epoch active before rotation.</param>
/// <param name="ActiveEpoch">New epoch fencing prior runtimes.</param>
/// <param name="RotatedAtUtc">Authoritative PostgreSQL rotation timestamp.</param>
public sealed record DurableRuntimeEpochRotationResult(Guid PreviousEpoch, Guid ActiveEpoch, DateTimeOffset RotatedAtUtc);
