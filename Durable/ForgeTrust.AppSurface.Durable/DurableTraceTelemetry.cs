using System.Diagnostics;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Owns the fixed, value-free vocabulary emitted by durable trace instrumentation.</summary>
internal static class DurableTraceTelemetry
{
    internal const string ContractVersion = "appsurface.durable.trace.contract_version";
    internal const string ExecutionKind = "appsurface.durable.execution.kind";
    internal const string TriggerKind = "appsurface.durable.trigger.kind";
    internal const string FlowState = "appsurface.durable.flow.state";
    internal const string Outcome = "appsurface.durable.outcome";
    internal const string CorrelationToken = "appsurface.durable.correlation_token";
    internal const string ContextStatus = "appsurface.durable.context.status";

    /// <summary>
    /// Applies the fixed, value-free Durable trace tag vocabulary to an active execution activity.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> <paramref name="activity"/> is a no-op because no listener requested an activity.
    /// The emitted keys are <see cref="ContractVersion"/>, <see cref="ExecutionKind"/>, <see cref="TriggerKind"/>,
    /// <see cref="FlowState"/>, <see cref="Outcome"/>, <see cref="ContextStatus"/>, and, only when available,
    /// <see cref="CorrelationToken"/>. <paramref name="contextStatus"/> is a
    /// <see cref="DurableTraceContextStatus"/> value from validated durable trace capture; header values, baggage,
    /// and tenant data are never emitted.
    /// </remarks>
    internal static void Apply(
        Activity? activity,
        string executionKind,
        string triggerKind,
        string flowState,
        string outcome,
        Guid correlationToken,
        DurableTraceContextStatus contextStatus)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ContractVersion, "1");
        activity.SetTag(ExecutionKind, executionKind);
        activity.SetTag(TriggerKind, triggerKind);
        activity.SetTag(FlowState, flowState);
        activity.SetTag(Outcome, outcome);
        if (correlationToken != Guid.Empty)
        {
            activity.SetTag(CorrelationToken, correlationToken.ToString("D"));
        }

        activity.SetTag(ContextStatus, contextStatus.ToString().ToLowerInvariant());
    }
}
