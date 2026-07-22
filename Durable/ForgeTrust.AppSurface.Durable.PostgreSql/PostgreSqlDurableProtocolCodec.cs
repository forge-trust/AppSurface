using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Centralizes fail-closed conversion between PostgreSQL protocol values and Durable contract enums.</summary>
internal static class PostgreSqlDurableProtocolCodec
{
    /// <summary>Projects one persisted Work state or rejects an unknown value as corruption.</summary>
    internal static DurableWorkState ParseWorkState(string state) => state switch
    {
        "pending" or "retry_wait" => DurableWorkState.Ready,
        "leased" or "effect_permitted" => DurableWorkState.Claimed,
        "cancel_pending" => DurableWorkState.CancelPending,
        "succeeded" => DurableWorkState.Succeeded,
        "succeeded_after_cancel_requested" => DurableWorkState.SucceededAfterCancelRequested,
        "failed" => DurableWorkState.FailedTerminal,
        "canceled_before_effect" => DurableWorkState.CanceledBeforeEffect,
        "reconciling" or
        "suspended_ambiguous_external_outcome" or
        "suspended_reconciliation_required" or
        "suspended_manual_resolution" or
        "suspended_contract_unavailable" => DurableWorkState.Suspended,
        _ => throw new InvalidDataException($"Unknown persisted durable Work state '{state}'."),
    };

    /// <summary>Formats one supported provider-safety value for persistence.</summary>
    internal static string FormatProviderSafety(DurableProviderSafety safety) => safety switch
    {
        DurableProviderSafety.Idempotent => "idempotent",
        DurableProviderSafety.ProviderKeyed => "provider_keyed",
        DurableProviderSafety.ReconcileBeforeRetry => "reconcile_before_retry",
        DurableProviderSafety.ManualResolution => "manual_resolution",
        _ => throw new ArgumentOutOfRangeException(nameof(safety)),
    };

    /// <summary>Parses one persisted provider-safety value or rejects corruption.</summary>
    internal static DurableProviderSafety ParseProviderSafety(string value) => value switch
    {
        "idempotent" => DurableProviderSafety.Idempotent,
        "provider_keyed" => DurableProviderSafety.ProviderKeyed,
        "reconcile_before_retry" => DurableProviderSafety.ReconcileBeforeRetry,
        "manual_resolution" => DurableProviderSafety.ManualResolution,
        _ => throw new InvalidDataException($"Unknown persisted provider safety value '{value}'."),
    };

    /// <summary>Formats one supported payload classification for persistence.</summary>
    internal static string FormatClassification(DurableDataClassification classification) => classification switch
    {
        DurableDataClassification.Operational => "operational",
        DurableDataClassification.ApprovedApplication => "approved_application",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    /// <summary>Parses one persisted payload classification or rejects corruption.</summary>
    internal static DurableDataClassification ParseClassification(string value) => value switch
    {
        "operational" => DurableDataClassification.Operational,
        "approved_application" => DurableDataClassification.ApprovedApplication,
        _ => throw new InvalidDataException($"Unknown persisted classification '{value}'."),
    };
}
