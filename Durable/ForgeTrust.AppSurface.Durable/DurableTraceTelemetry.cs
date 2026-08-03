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
        activity.SetTag(CorrelationToken, correlationToken.ToString("D"));
        activity.SetTag(ContextStatus, contextStatus.ToString().ToLowerInvariant());
    }
}

