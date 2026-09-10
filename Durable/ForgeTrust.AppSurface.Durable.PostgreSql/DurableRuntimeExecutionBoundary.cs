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
    /// <summary>Invokes one prepared provider operation and returns its encoded exit fact.</summary>
    /// <remarks>Forwards cancellation to provider execution and owns no claim, permit, completion, or tracing state.</remarks>
    ValueTask<DurableEncodedWorkExit> InvokeExitAsync(
        DurablePreparedWorkInvocation invocation,
        CancellationToken cancellationToken);
}

internal sealed class UninstrumentedDurableRuntimeExecutionBoundary : IDurableRuntimeExecutionBoundary
{
    public ValueTask<DurableEncodedWorkExit> InvokeExitAsync(
        DurablePreparedWorkInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return invocation.InvokeExitAsync(cancellationToken);
    }
}
