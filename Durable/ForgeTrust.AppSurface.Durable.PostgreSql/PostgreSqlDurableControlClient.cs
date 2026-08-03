using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;
using NpgsqlTypes;
using static ForgeTrust.AppSurface.Durable.PostgreSql.PostgreSqlDurableProtocolCodec;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Implements application-authorized Work inventory, cancellation, and scope fencing over PostgreSQL.</summary>
/// <remarks>
/// This is an internal provider implementation of the public Provider SPI. Applications must authorize an operation
/// before calling it; opaque scope and aggregate identities are not authorization grants.
/// </remarks>
internal sealed class PostgreSqlDurableControlClient : IDurableWorkControlClient, IDurableScopeControlClient
{
    private static readonly Uri WorkDocumentation = new("https://appsurface.dev/docs/durable/work#operations");
    private static readonly Uri ScopeDocumentation = new("https://appsurface.dev/docs/durable/scopes");
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlDurableWorkStore _workStore;
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly Guid _runtimeEpoch;

    internal PostgreSqlDurableControlClient(
        PostgreSqlDurableRuntimeRegistration registration,
        IDurableRuntimeSchemaManager schemaManager,
        PostgreSqlDurableWorkStore workStore)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _dataSource = registration.RuntimeDataSource;
        _runtimeEpoch = registration.WorkOptions.RuntimeEpoch;
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
    }

    public async ValueTask<DurableOperationResult<DurableWorkListResult>> ListAsync(
        DurableWorkListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken)
                .ConfigureAwait(false);
            const string sql = """
                SELECT work_id, activity_id, work_name, work_version, state, provider_safety, attempt_number, revision,
                       accepted_at, due_at, updated_at, terminal_code, cancellation_requested_at IS NOT NULL,
                       runtime_epoch <> @runtime_epoch
                         AND state NOT IN ('succeeded', 'succeeded_after_cancel_requested', 'failed', 'canceled_before_effect')
                         AS requires_recovery_release
                FROM appsurface_durable.work
                WHERE scope_id = @scope_id
                  AND (@continuation_token IS NULL OR work_id > @continuation_token)
                  AND (@states IS NULL OR state = ANY(@states))
                  AND
                  (
                      NOT @recovery_only
                      OR
                      (
                          runtime_epoch <> @runtime_epoch
                          AND state NOT IN ('succeeded', 'succeeded_after_cancel_requested', 'failed', 'canceled_before_effect')
                      )
                  )
                ORDER BY work_id
                LIMIT @query_size;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.Add(new NpgsqlParameter("continuation_token", NpgsqlDbType.Text)
            {
                Value = request.ContinuationToken is null ? DBNull.Value : request.ContinuationToken,
            });
            command.Parameters.Add(new NpgsqlParameter("states", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = request.State is { } state ? GetPersistedStates(state) : DBNull.Value,
            });
            command.Parameters.AddWithValue("recovery_only", request.RequiresRecoveryReleaseOnly);
            command.Parameters.AddWithValue("runtime_epoch", _runtimeEpoch);
            command.Parameters.AddWithValue("query_size", request.PageSize + 1);
            var items = new List<DurableWorkListItem>(request.PageSize + 1);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    items.Add(new DurableWorkListItem(
                        new DurableWorkId(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        ParseWorkState(reader.GetString(4)),
                        ParseProviderSafety(reader.GetString(5)),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        ReadUtc(reader, 8),
                        ReadUtc(reader, 9),
                        ReadUtc(reader, 10),
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.GetBoolean(12),
                        reader.GetBoolean(13)));
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var hasMore = items.Count > request.PageSize;
            if (hasMore)
            {
                items.RemoveAt(items.Count - 1);
            }

            return DurableOperationResult<DurableWorkListResult>.Success(new DurableWorkListResult(
                items,
                hasMore ? items[^1].WorkId.Value : null));
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<DurableOperationResult<DurableWorkSnapshot>> GetAsync(
        DurableWorkGetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetScopeAsync(connection, transaction, request.ScopeId, cancellationToken)
                .ConfigureAwait(false);
            const string sql = """
                SELECT activity_id, work_name, work_version, state, provider_safety, activity_id, attempt_number,
                       revision, accepted_at, due_at, updated_at, terminal_at, terminal_code, result_contract_id,
                       result_schema_version, result_classification, result_payload, result_sha256,
                       result_retention_policy_id
                FROM appsurface_durable.work
                WHERE scope_id = @scope_id AND work_id = @work_id;
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("work_id", request.WorkId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.CloseAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<DurableWorkSnapshot>(
                    DurableProblemCodes.WorkNotFound,
                    "The Work aggregate was not found in the authorized durable scope.",
                    "The Work does not exist or belongs to another scope.",
                    "Verify the trusted scope and opaque Work identity before retrying.",
                    WorkDocumentation,
                    request.WorkId.Value);
            }

            var result = ReadResult(reader);
            var snapshot = new DurableWorkSnapshot(
                request.ScopeId,
                request.WorkId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseWorkState(reader.GetString(3)),
                ParseProviderSafety(reader.GetString(4)),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                ReadUtc(reader, 8),
                ReadUtc(reader, 9),
                ReadUtc(reader, 10),
                reader.IsDBNull(11) ? null : ReadUtc(reader, 11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                result);
            await reader.CloseAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DurableOperationResult<DurableWorkSnapshot>.Success(snapshot);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<DurableOperationResult<DurableWorkCancelResult>> CancelAsync(
        DurableWorkCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        var result = await _workStore.RequestCancellationAsync(
            request.ScopeId,
            request.WorkId,
            request.ActorId,
            request.ReasonCode,
            request.ExpectedRevision,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            PostgreSqlCancellationOutcome.Applied => DurableOperationResult<DurableWorkCancelResult>.Success(
                new DurableWorkCancelResult(request.WorkId, DurableWorkCancelOutcome.Applied, result.State!.Value, result.Revision)),
            PostgreSqlCancellationOutcome.AlreadyTerminal => DurableOperationResult<DurableWorkCancelResult>.Success(
                new DurableWorkCancelResult(request.WorkId, DurableWorkCancelOutcome.AlreadyTerminal, result.State!.Value, result.Revision)),
            PostgreSqlCancellationOutcome.NotFound => Failure<DurableWorkCancelResult>(
                DurableProblemCodes.WorkNotFound,
                "The Work cancellation target was not found in the authorized durable scope.",
                "The Work does not exist or belongs to another scope.",
                "Verify the trusted scope and opaque Work identity before retrying.",
                WorkDocumentation,
                request.WorkId.Value),
            PostgreSqlCancellationOutcome.RevisionConflict => Failure<DurableWorkCancelResult>(
                DurableProblemCodes.WorkRevisionConflict,
                "The Work cancellation did not match the current aggregate revision.",
                "Another worker or command advanced authoritative state.",
                "Read the latest Work snapshot and make a new authorized decision.",
                WorkDocumentation,
                request.WorkId.Value),
            _ => throw new InvalidDataException($"Unknown PostgreSQL cancellation outcome '{result.Outcome}'."),
        };
    }

    public async ValueTask<DurableOperationResult<DurableScopeDisableResult>> DisableAsync(
        DurableScopeDisableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _schemaManager.ValidateAsync(cancellationToken).ConfigureAwait(false);
        var result = await _workStore.DisableScopeAsync(
            request.ScopeId,
            request.ActorId,
            request.ReasonCode,
            request.ExpectedGeneration,
            cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            PostgreSqlScopeMutationOutcome.Applied => DurableOperationResult<DurableScopeDisableResult>.Success(
                new DurableScopeDisableResult(request.ScopeId, DurableScopeDisableOutcome.Applied, result.Generation)),
            PostgreSqlScopeMutationOutcome.AlreadyDisabled => DurableOperationResult<DurableScopeDisableResult>.Success(
                new DurableScopeDisableResult(request.ScopeId, DurableScopeDisableOutcome.AlreadyDisabled, result.Generation)),
            PostgreSqlScopeMutationOutcome.NotFound => Failure<DurableScopeDisableResult>(
                DurableProblemCodes.ScopeNotFound,
                "The durable scope could not be disabled because it does not exist.",
                "No durable aggregate has established the requested scope.",
                "Verify the trusted scope identity before retrying.",
                ScopeDocumentation,
                request.ScopeId.Value),
            PostgreSqlScopeMutationOutcome.GenerationConflict => Failure<DurableScopeDisableResult>(
                DurableProblemCodes.ScopeGenerationConflict,
                "The scope disable command did not match the current lifecycle generation.",
                "Another lifecycle command already advanced the durable scope.",
                "Read the owning application lifecycle and make a new authorized decision.",
                ScopeDocumentation,
                request.ScopeId.Value),
            _ => throw new InvalidDataException($"Unknown PostgreSQL scope mutation outcome '{result.Outcome}'."),
        };
    }

    private static DurableEncodedPayload? ReadResult(NpgsqlDataReader reader)
    {
        if (reader.IsDBNull(16))
        {
            return null;
        }

        var result = new DurableEncodedPayload(
            reader.GetString(13),
            reader.GetString(14),
            ParseClassification(reader.GetString(15)),
            reader.GetFieldValue<byte[]>(16),
            reader.GetString(18));
        if (!reader.GetFieldValue<byte[]>(17).AsSpan().SequenceEqual(Convert.FromHexString(result.Sha256)))
        {
            throw new InvalidDataException("The durable Work result hash does not match its authoritative bytes.");
        }

        return result;
    }

    private static DurableOperationResult<T> Failure<T>(
        string code,
        string problem,
        string cause,
        string fix,
        Uri documentation,
        string correlationId)
        where T : class => DurableOperationResult<T>.Failure(
        new DurableProblem(code, problem, cause, fix, documentation, correlationId));

    private static DurableWorkState ParseWorkState(string state) => state switch
    {
        "pending" or "retry_wait" => DurableWorkState.Ready,
        "leased" or "effect_permitted" => DurableWorkState.Claimed,
        "reconciling" => DurableWorkState.Suspended,
        "cancel_pending" => DurableWorkState.CancelPending,
        "succeeded" => DurableWorkState.Succeeded,
        "succeeded_after_cancel_requested" => DurableWorkState.SucceededAfterCancelRequested,
        "failed" => DurableWorkState.FailedTerminal,
        "canceled_before_effect" => DurableWorkState.CanceledBeforeEffect,
        "suspended_ambiguous_external_outcome" or "suspended_reconciliation_required" or
            "suspended_manual_resolution" or "suspended_contract_unavailable" => DurableWorkState.Suspended,
        _ => throw new InvalidDataException($"Unknown persisted durable Work state '{state}'."),
    };

    private static string[] GetPersistedStates(DurableWorkState state) => state switch
    {
        DurableWorkState.Ready => ["pending", "retry_wait"],
        DurableWorkState.Claimed => ["leased", "effect_permitted"],
        DurableWorkState.CancelPending => ["cancel_pending"],
        DurableWorkState.Succeeded => ["succeeded"],
        DurableWorkState.SucceededAfterCancelRequested => ["succeeded_after_cancel_requested"],
        DurableWorkState.FailedTerminal => ["failed"],
        DurableWorkState.CanceledBeforeEffect => ["canceled_before_effect"],
        DurableWorkState.Suspended =>
        [
            "reconciling", "suspended_ambiguous_external_outcome", "suspended_reconciliation_required",
            "suspended_manual_resolution", "suspended_contract_unavailable",
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static DurableProviderSafety ParseProviderSafety(string value) => value switch
    {
        "idempotent" => DurableProviderSafety.Idempotent,
        "provider_keyed" => DurableProviderSafety.ProviderKeyed,
        "reconcile_before_retry" => DurableProviderSafety.ReconcileBeforeRetry,
        "manual_resolution" => DurableProviderSafety.ManualResolution,
        _ => throw new InvalidDataException($"Unknown persisted provider safety '{value}'."),
    };

    private static DurableDataClassification ParseClassification(string value) => value switch
    {
        "operational" => DurableDataClassification.Operational,
        "approved_application" => DurableDataClassification.ApprovedApplication,
        _ => throw new InvalidDataException($"Unknown persisted data classification '{value}'."),
    };

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    private static async ValueTask SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask TryRollbackAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
        {
            // Preserve the original database failure.
        }
    }
}
