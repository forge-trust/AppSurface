using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Runs one bounded, manually invoked PostgreSQL Schedule due pass.
/// </summary>
/// <remarks>
/// The processor first claims a payload-free dispatch lease through the dispatcher data source, then opens a separate
/// scoped runtime transaction to record and bridge Schedule facts. It does not start a loop, execute Work, invoke a
/// provider, or start Flow targets. Cancellation is observed before each additional lease; work already committed by a
/// prior claim remains durable.
/// </remarks>
public sealed class PostgreSqlDurableScheduleProcessor
{
    private readonly NpgsqlDataSource _dispatcherDataSource;
    private readonly PostgreSqlDurableScheduleStore _store;
    private readonly PostgreSqlDurableScheduleOptions _options;

    /// <summary>Initializes a passive processor with distinct dispatcher and scoped-runtime data sources.</summary>
    /// <param name="dispatcherDataSource">Trusted dispatcher-role data source used only for payload-free discovery leases.</param>
    /// <param name="runtimeDataSource">Exact runtime-role data source used for scoped Schedule and Work bridge transactions.</param>
    /// <param name="workRegistry">Immutable Work registrations used to validate and bridge persisted Work targets.</param>
    /// <param name="workOptions">Validated durable StoreId and runtime epoch.</param>
    /// <param name="scheduleOptions">Runtime-role, clock-safety, and lease-duration settings.</param>
    public PostgreSqlDurableScheduleProcessor(
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource runtimeDataSource,
        IDurableWorkRegistry workRegistry,
        PostgreSqlDurableWorkOptions workOptions,
        PostgreSqlDurableScheduleOptions scheduleOptions)
    {
        _dispatcherDataSource = dispatcherDataSource ?? throw new ArgumentNullException(nameof(dispatcherDataSource));
        _store = new PostgreSqlDurableScheduleStore(
            runtimeDataSource,
            workRegistry,
            workOptions,
            scheduleOptions);
        _options = scheduleOptions ?? throw new ArgumentNullException(nameof(scheduleOptions));
    }

    /// <summary>
    /// Claims and processes up to the requested number of eligible Schedule dispatch rows.
    /// </summary>
    /// <param name="request">Bounded pass identity and maximum claim count.</param>
    /// <param name="cancellationToken">Cancels before the next lease; it never undoes a committed Schedule fact.</param>
    /// <returns>Counts of claimed Schedule rows and resulting durable facts. An empty pass returns zero counts.</returns>
    public async ValueTask<PostgreSqlDurableScheduleProcessResult> ProcessDueAsync(
        PostgreSqlDurableScheduleProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var claimed = 0;
        var occurrences = 0;
        var materializedWork = 0;
        var suspended = 0;
        for (var index = 0; index < request.MaximumSchedules; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = await ClaimNextAsync(request.LeaseOwner, cancellationToken).ConfigureAwait(false);
            if (claim is null)
            {
                break;
            }

            claimed++;
            var outcome = await _store.ProcessClaimAsync(claim, cancellationToken).ConfigureAwait(false);
            occurrences += outcome.RecordedOccurrences;
            materializedWork += outcome.MaterializedWorkTargets;
            suspended += outcome.SuspendedSchedules;
        }

        return new PostgreSqlDurableScheduleProcessResult(claimed, occurrences, materializedWork, suspended);
    }

    private async ValueTask<ScheduleDispatchClaim?> ClaimNextAsync(string leaseOwner, CancellationToken cancellationToken)
    {
        await using var connection = await _dispatcherDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            const string sql = """
                SELECT scope_id, schedule_id, dispatch_revision
                FROM appsurface_durable.claim_schedule_dispatch(@lease_owner, @lease_duration);
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("lease_owner", leaseOwner);
            command.Parameters.AddWithValue("lease_duration", _options.LeaseDuration);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var claim = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new ScheduleDispatchClaim(
                    new DurableScopeId(reader.GetString(0)),
                    new DurableScheduleId(reader.GetString(1)),
                    reader.GetInt64(2))
                : null;
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claim;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (PostgreSqlDurableExceptionFilters.IsExpectedCleanupFailure(exception))
            {
                // Preserve the original database or transport failure; disposal owns transaction cleanup.
            }

            throw;
        }
    }
}
