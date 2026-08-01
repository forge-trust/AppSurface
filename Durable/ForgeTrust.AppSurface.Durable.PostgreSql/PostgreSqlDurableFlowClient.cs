using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Persists application-authorized durable Flow commands and payload-free queries in PostgreSQL.</summary>
/// <remarks>
/// This client does not authenticate callers, apply schema migrations, or start a processor. Applications authorize
/// the trusted <see cref="DurableScopeId"/> before calling it. The data source must use the scoped runtime role.
/// </remarks>
public sealed class PostgreSqlDurableFlowClient : IDurableFlowClient
{
    private readonly IDurableFlowRegistry _flowRegistry;
    private readonly IDurablePayloadCodecRegistry _payloadCodecs;
    private readonly PostgreSqlDurableFlowStore _store;

    /// <summary>Initializes a PostgreSQL durable Flow client without applying schema or starting background work.</summary>
    /// <param name="dataSource">Scoped runtime-role data source without ownership or <c>BYPASSRLS</c>.</param>
    /// <param name="flowRegistry">Immutable Flow definitions and durable bindings.</param>
    /// <param name="payloadCodecs">Explicit durable payload allowlist.</param>
    /// <param name="options">
    /// Validated store identity, active runtime epoch, and package-wide wake-hint behavior. Work options are reused
    /// because those values belong to the PostgreSQL protocol rather than Work retry semantics.
    /// </param>
    public PostgreSqlDurableFlowClient(
        NpgsqlDataSource dataSource,
        IDurableFlowRegistry flowRegistry,
        IDurablePayloadCodecRegistry payloadCodecs,
        PostgreSqlDurableWorkOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _flowRegistry = flowRegistry ?? throw new ArgumentNullException(nameof(flowRegistry));
        _payloadCodecs = payloadCodecs ?? throw new ArgumentNullException(nameof(payloadCodecs));
        ArgumentNullException.ThrowIfNull(options);
        _store = new PostgreSqlDurableFlowStore(dataSource, options);
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableFlowSnapshot>> GetAsync(
        DurableFlowGetRequest request,
        CancellationToken cancellationToken = default) =>
        _store.GetAsync(request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableFlowListResult>> ListAsync(
        DurableFlowListRequest request,
        CancellationToken cancellationToken = default) =>
        _store.ListAsync(request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableFlowCommandResult>> StartAsync(
        DurableFlowStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var registration = _flowRegistry.GetRequired(request.FlowId, request.FlowVersion);
        var codec = _payloadCodecs.GetRequired(
            registration.ContextCodec.PayloadType,
            registration.ContextCodec.ContractName,
            registration.ContextCodec.ContractVersion);
        if (!ReferenceEquals(codec, registration.ContextCodec))
        {
            throw new InvalidOperationException(
                $"Flow '{registration.FlowId}' version '{registration.FlowVersion}' must use its exact allowlisted context codec.");
        }

        _ = registration.ContextCodec.DecodeObject(request.Context);
        return await _store.StartAsync(request, registration, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableFlowCommandResult>> RaiseEventAsync(
        DurableFlowEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _store.RaiseEventAsync(
            request,
            payload =>
            {
                if (payload is { } eventPayload)
                {
                    _ = _payloadCodecs.GetRequired(eventPayload.ContractName, eventPayload.ContractVersion)
                        .DecodeObject(eventPayload);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableFlowCommandResult>> CancelAsync(
        DurableFlowCancelRequest request,
        CancellationToken cancellationToken = default) =>
        _store.CancelAsync(request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DurableOperationResult<DurableFlowCommandResult>> ReleaseSuspensionAsync(
        DurableFlowReleaseRequest request,
        CancellationToken cancellationToken = default) =>
        _store.ReleaseSuspensionAsync(request, _flowRegistry, cancellationToken);
}
