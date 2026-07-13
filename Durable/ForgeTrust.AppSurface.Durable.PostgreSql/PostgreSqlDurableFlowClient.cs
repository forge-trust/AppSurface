using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Starts, resumes, and cancels durable Flow instances in PostgreSQL.
/// </summary>
/// <remarks>
/// Authorization remains application-owned. The client requires a trusted <see cref="DurableScopeId"/> on every
/// request, applies row-level scope context inside each transaction, and never accepts an instance or event id as
/// authorization by itself. Schema migration is an explicit deployment operation and is never performed here.
/// </remarks>
public sealed class PostgreSqlDurableFlowClient : IDurableFlowClient
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDurableFlowRegistry _flowRegistry;
    private readonly IDurablePayloadCodecRegistry _payloadCodecs;
    private readonly Guid _runtimeEpoch;
    private readonly bool _sendWakeNotification;
    private readonly PostgreSqlDurableFlowStore _store;

    /// <summary>
    /// Initializes a PostgreSQL durable Flow client.
    /// </summary>
    /// <param name="dataSource">Runtime-role data source with no ownership or <c>BYPASSRLS</c> privilege.</param>
    /// <param name="flowRegistry">Immutable registered Flow definitions and codecs.</param>
    /// <param name="payloadCodecs">Explicit allowlist used to validate persisted context and event payloads.</param>
    /// <param name="runtimeEpoch">Out-of-band recovery epoch shared with the durable runtime pump.</param>
    /// <param name="sendWakeNotification">
    /// Whether accepted commands emit metadata-only PostgreSQL wake hints. Correctness never depends on the hint.
    /// </param>
    public PostgreSqlDurableFlowClient(
        NpgsqlDataSource dataSource,
        IDurableFlowRegistry flowRegistry,
        IDurablePayloadCodecRegistry payloadCodecs,
        Guid runtimeEpoch,
        bool sendWakeNotification = true)
        : this(
            dataSource,
            flowRegistry,
            payloadCodecs,
            runtimeEpoch,
            sendWakeNotification,
            onTerminalApplied: null)
    {
    }

    internal PostgreSqlDurableFlowClient(
        NpgsqlDataSource dataSource,
        IDurableFlowRegistry flowRegistry,
        IDurablePayloadCodecRegistry payloadCodecs,
        Guid runtimeEpoch,
        bool sendWakeNotification,
        Func<NpgsqlTransaction, DurableScopeId, DurableFlowInstanceId, string, CancellationToken, ValueTask>?
            onTerminalApplied)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _flowRegistry = flowRegistry ?? throw new ArgumentNullException(nameof(flowRegistry));
        _payloadCodecs = payloadCodecs ?? throw new ArgumentNullException(nameof(payloadCodecs));
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        _runtimeEpoch = runtimeEpoch;
        _sendWakeNotification = sendWakeNotification;
        _store = new PostgreSqlDurableFlowStore(
            dataSource,
            runtimeEpoch,
            sendWakeNotification,
            onTerminalApplied);
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
        var allowlistedCodec = _payloadCodecs.GetRequired(
            registration.ContextCodec.PayloadType,
            registration.ContextCodec.ContractName,
            registration.ContextCodec.ContractVersion);
        if (!ReferenceEquals(allowlistedCodec, registration.ContextCodec))
        {
            throw new InvalidOperationException(
                $"Flow '{registration.FlowId}' version '{registration.FlowVersion}' context codec is not the exact allowlisted registration.");
        }

        _ = registration.ContextCodec.DecodeObject(request.Context);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await PostgreSqlDurableFlowStore.StartAsync(
                transaction,
                request,
                registration,
                _runtimeEpoch,
                _sendWakeNotification,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationResult<DurableFlowCommandResult>> RaiseEventAsync(
        DurableFlowEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Payload is { } payload)
        {
            _ = _payloadCodecs.GetRequired(payload.ContractName, payload.ContractVersion).DecodeObject(payload);
        }

        return await _store.RaiseEventAsync(request, cancellationToken).ConfigureAwait(false);
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
