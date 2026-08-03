using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Owns the per-invocation execution seam that later tracing integration instruments.</summary>
/// <remarks>
/// Slice 6 deliberately makes this a no-op wrapper. Durable Flow trace context, Activities, links, tags, and exports
/// remain #685's responsibility; its narrow integration can replace this implementation without changing claim,
/// permit, completion, or hosted-lifecycle ownership.
/// </remarks>
internal interface IDurableRuntimeExecutionBoundary
{
    ValueTask<DurableEncodedPayload> InvokeAsync(
        DurablePreparedWorkInvocation invocation,
        CancellationToken cancellationToken);
}

internal sealed class UninstrumentedDurableRuntimeExecutionBoundary : IDurableRuntimeExecutionBoundary
{
    public ValueTask<DurableEncodedPayload> InvokeAsync(
        DurablePreparedWorkInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return invocation.InvokeAsync(cancellationToken);
    }
}
