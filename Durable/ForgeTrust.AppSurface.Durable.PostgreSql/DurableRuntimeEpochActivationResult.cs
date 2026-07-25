namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Reports explicit one-time activation of a store recovery epoch.</summary>
/// <param name="ActiveEpoch">Epoch activated for this store.</param>
/// <param name="ActivatedAtUtc">Authoritative PostgreSQL activation timestamp.</param>
public sealed record DurableRuntimeEpochActivationResult(Guid ActiveEpoch, DateTimeOffset ActivatedAtUtc);
