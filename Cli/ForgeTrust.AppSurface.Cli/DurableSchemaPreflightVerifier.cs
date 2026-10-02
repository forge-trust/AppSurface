using System.Diagnostics;
using System.Globalization;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Immutable, secret-safe evidence from one complete fenced catalog snapshot.</summary>
/// <remarks>
/// Pair indices are one-based and identify only the observed runtime credential. An owner-diagnostic result
/// never covers a runtime. Null epochs remain null; preflight does not initialize or rotate store identity.
/// Copies prevent caller mutation. See Durable/heartbeat-retention-operations.md for the combined guard gate.
/// </remarks>
internal sealed class DurablePreflightResult
{
    /// <summary>Initializes a copied status, caller identity and bounded fixed-check result.</summary>
    internal DurablePreflightResult(DurableSchemaStatusView status, Guid storeId, Guid? activeRuntimeEpoch,
        string callerEvidenceKind, string? callerRole, int? pairIndex, IEnumerable<string> failedChecks)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(failedChecks);
        Status = status with { PendingVersions = Array.AsReadOnly(status.PendingVersions.ToArray()) };
        StoreId = storeId;
        ActiveRuntimeEpoch = activeRuntimeEpoch;
        CallerEvidenceKind = callerEvidenceKind;
        CallerRole = callerRole;
        PairIndex = pairIndex;
        FailedChecks = Array.AsReadOnly(failedChecks.ToArray());
    }

    /// <summary>Gets package-defined compatibility from the shared provider reader.</summary>
    internal DurableSchemaStatusView Status { get; }
    /// <summary>Gets the provider-observed StoreId.</summary>
    internal Guid StoreId { get; }
    /// <summary>Gets the nullable provider-observed active epoch.</summary>
    internal Guid? ActiveRuntimeEpoch { get; }
    /// <summary>Gets runtime, owner-diagnostic, or unresolved caller evidence.</summary>
    internal string CallerEvidenceKind { get; }
    /// <summary>Gets a request-validated caller role, never uncontrolled server text.</summary>
    internal string? CallerRole { get; }
    /// <summary>Gets a one-based runtime pair, or null for owner/unresolved credentials.</summary>
    internal int? PairIndex { get; }
    /// <summary>Gets deterministic fixed categories, optionally qualified by validated pair identity.</summary>
    internal IReadOnlyList<string> FailedChecks { get; }
}

/// <summary>Fixed safe failure for bounded preflight operations or uncertain resource cleanup.</summary>
internal sealed class DurablePreflightException(string category) : Exception
{
    /// <summary>Gets the trusted diagnostic category; no provider message or connection data is retained.</summary>
    internal string Category { get; } = category;
}

/// <summary>Owns a single dedicated session, shared fence and read-only repeatable-read verification.</summary>
/// <remarks>
/// Acquire the session fence BEFORE beginning repeatable read, so waiting for a preceding writer never pins
/// a stale snapshot. One monotonic 30-second budget reserves up to two seconds for independent cleanup.
/// Pooling, ambient enlistment and multiplexing are disabled. Callers must review endpoint session affinity;
/// these settings cannot make a transaction/statement pooler safe. No success escapes before commit, explicit
/// unlock and physical disposal. This is an internal CLI adapter, not a provider preflight or activation API.
/// </remarks>
internal static class DurableSchemaPreflightVerifier
{
    internal static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan WorkTimeout = TimeSpan.FromSeconds(28);
    private static readonly string[] GlobalChecks =
    [
        "role_resolution", "role_alias", "forced_rls", "function_signature", "function_owner", "security_definer",
        "search_path", "heartbeat_policies", "runtime_policy_set", "function_acl", "due_health_acl", "retention_index"
    ];
    private static readonly string[] RoleChecks =
        ["runtime_role", "role_membership", "runtime_ownership", "runtime_table_privileges", "role_alias", "function_acl", "due_health_acl"];

    /// <summary>Validates one complete request on an owned session with checked, independently bounded cleanup.</summary>
    /// <param name="connectionString">Secret connection configuration; it is never part of results or diagnostics.</param>
    /// <param name="request">Already validated immutable complete manifest and independently reviewed owner.</param>
    /// <param name="cancellationToken">Cancels work; cleanup uses its own remaining-budget cancellation.</param>
    /// <returns>Evidence only after every owned resource has been safely released.</returns>
    internal static async ValueTask<DurablePreflightResult> VerifyAsync(
        string connectionString, DurablePreflightRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = CreateSessionSettings(connectionString);
        var started = Stopwatch.GetTimestamp();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        work.CancelAfter(WorkTimeout);
        var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var connection = dataSource.CreateConnection();
        NpgsqlTransaction? transaction = null;
        var transactionCompleted = false;
        var ownsFence = false;
        var cleanupSucceeded = false;
        DurablePreflightResult? result = null;
        try
        {
            await connection.OpenAsync(work.Token).ConfigureAwait(false);
            await using (var fence = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_lock_shared(@key)", connection))
            {
                fence.Parameters.AddWithValue("key", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
                await fence.ExecuteNonQueryAsync(work.Token).ConfigureAwait(false);
                ownsFence = true;
            }

            await VerifyFenceAffinityAsync(connection, null, work.Token).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, work.Token).ConfigureAwait(false);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(work.Token).ConfigureAwait(false);
            }

            var status = await new PostgreSqlDurableRuntimeSchemaManager(dataSource)
                .ReadStatusInTransactionAsync(connection, transaction, work.Token).ConfigureAwait(false);
            if (!status.IsCompatible)
            {
                result = new DurablePreflightResult(DurableSchemaStatusView.From(status), status.StoreId,
                    status.ActiveRuntimeEpoch, "unresolved", null, null, []);
            }
            else
            {
                var caller = await ReadCallerAsync(connection, transaction, request, work.Token).ConfigureAwait(false);
                await using var catalog = new NpgsqlCommand(CatalogSql, connection, transaction);
                catalog.Parameters.AddWithValue("runtime_names", request.Manifest.Pairs.Select(static pair => pair.Runtime).ToArray());
                catalog.Parameters.AddWithValue("dispatcher_names", request.Manifest.Pairs.Select(static pair => pair.Dispatcher).ToArray());
                catalog.Parameters.AddWithValue("owner_name", request.MigrationOwnerRole);
                var raw = await catalog.ExecuteScalarAsync(work.Token).ConfigureAwait(false);
                var failures = MapCatalogResult(raw, request);
                result = new DurablePreflightResult(DurableSchemaStatusView.From(status), status.StoreId,
                    status.ActiveRuntimeEpoch, caller.Kind, caller.Role, caller.Pair,
                    caller.Failure is null ? failures : new[] { caller.Failure }.Concat(failures));
            }

            await VerifyFenceAffinityAsync(connection, transaction, work.Token).ConfigureAwait(false);
            await transaction.CommitAsync(work.Token).ConfigureAwait(false);
            transactionCompleted = true;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            throw new DurablePreflightException("timeout");
        }
        finally
        {
            var remaining = TotalTimeout - Stopwatch.GetElapsedTime(started);
            using var cleanup = new CancellationTokenSource(remaining > TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, 1000))
                : TimeSpan.FromMilliseconds(1));
            try
            {
                if (transaction is not null && !transactionCompleted && transaction.Connection is not null)
                {
                    // A command timeout also constrains the physical connector's final Terminate flush.
                    // Avoid transaction disposal while pending: it retries rollback without cancellation.
                    await using var rollback = new NpgsqlCommand("ROLLBACK", connection, transaction) { CommandTimeout = 1 };
                    await rollback.ExecuteNonQueryAsync(cleanup.Token).ConfigureAwait(false);
                }
                if (ownsFence)
                {
                    await using var unlock = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock_shared(@key)", connection) { CommandTimeout = 1 };
                    unlock.Parameters.AddWithValue("key", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
                    if (await unlock.ExecuteScalarAsync(cleanup.Token).ConfigureAwait(false) is not true)
                    {
                        throw new DurablePreflightException("cleanup");
                    }
                }
                cleanupSucceeded = true;
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or OperationCanceledException or DurablePreflightException)
            {
                cleanupSucceeded = false;
            }
            finally
            {
                try
                {
                    // The nonpooled physical connector is closed, never returned to an uncertain pool slot.
                    await connection.DisposeAsync().ConfigureAwait(false);
                    // Physical teardown clears the transaction. Disposal is now local and cannot retry I/O.
                    if (transaction is not null)
                    {
                        await transaction.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or OperationCanceledException)
                {
                    cleanupSucceeded = false;
                }
                finally
                {
                    try
                    {
                        await dataSource.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or OperationCanceledException)
                    {
                        cleanupSucceeded = false;
                    }
                }
            }
            if (!cleanupSucceeded || Stopwatch.GetElapsedTime(started) >= TotalTimeout)
            {
                throw new DurablePreflightException("cleanup");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? throw new DurablePreflightException("catalog_result");
    }

    /// <summary>Constrains driver budgets without expanding any shorter supplied connection timeout.</summary>
    internal static NpgsqlConnectionStringBuilder CreateSessionSettings(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Enlist = false,
            Multiplexing = false,
            KeepAlive = 0,
            // -1 skips PostgreSQL's auxiliary cancellation connection and terminates the owned socket
            // immediately. Canceled work is never reused; this leaves the cleanup reserve for disposal.
            CancellationTimeout = -1
        };
        settings.Timeout = settings.Timeout == 0 ? 28 : Math.Min(settings.Timeout, 28);
        settings.CommandTimeout = settings.CommandTimeout == 0 ? 28 : Math.Min(settings.CommandTimeout, 28);
        return settings;
    }

    /// <summary>Rejects null, unexpected, unbounded or unknown catalog results without rendering their contents.</summary>
    internal static IReadOnlyList<string> MapCatalogResult(object? raw, DurablePreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (raw is not string[] categories || categories.Length > GlobalChecks.Length + RoleChecks.Length * request.Manifest.Pairs.Length)
        {
            return ["catalog_result"];
        }
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var category in GlobalChecks)
        {
            expected.Add(category, category);
        }
        for (var index = 0; index < request.Manifest.Pairs.Length; index++)
        {
            foreach (var category in RoleChecks)
            {
                expected.Add($"{category}:{index + 1}",
                    $"{category}[pair={index + 1},role={System.Text.Json.JsonSerializer.Serialize(request.Manifest.Pairs[index].Runtime)}]");
            }
        }
        if (categories.Any(category => category is null || !expected.ContainsKey(category)) || categories.Distinct(StringComparer.Ordinal).Count() != categories.Length)
        {
            return ["catalog_result"];
        }
        // Fixed inventory order, then manifest order, independent of catalog/ACL row order.
        return Array.AsReadOnly(expected.Where(item => categories.Contains(item.Key, StringComparer.Ordinal)).Select(static item => item.Value).ToArray());
    }

    /// <summary>Rejects incomplete successful evidence before rendering output from the service boundary.</summary>
    /// <remarks>A null epoch is valid for a structural pass; only the combined activation gate requires initialization.</remarks>
    internal static void ValidateEvidenceResult(DurablePreflightResult? result, DurablePreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (result is null || !Enum.IsDefined(result.Status.Compatibility)
            || result.Status.InstalledVersion < 0 || result.Status.RequiredVersion < 0)
        {
            throw new DurablePreflightException("catalog_result");
        }
        var allowedFailures = new HashSet<string>(GlobalChecks, StringComparer.Ordinal)
        {
            "caller_identity", "caller_role", "catalog_result"
        };
        for (var index = 0; index < request.Manifest.Pairs.Length; index++)
        {
            foreach (var category in RoleChecks)
            {
                allowedFailures.Add($"{category}[pair={index + 1},role={System.Text.Json.JsonSerializer.Serialize(request.Manifest.Pairs[index].Runtime)}]");
            }
        }
        if (result.FailedChecks.Count > allowedFailures.Count
            || result.FailedChecks.Any(category => category is null || !allowedFailures.Contains(category))
            || result.FailedChecks.Distinct(StringComparer.Ordinal).Count() != result.FailedChecks.Count)
        {
            throw new DurablePreflightException("catalog_result");
        }
        if (!result.Status.IsCompatible || result.FailedChecks.Count != 0)
        {
            return;
        }
        var validCaller = result.CallerEvidenceKind switch
        {
            "owner-diagnostic" => result.PairIndex is null
                && string.Equals(result.CallerRole, request.MigrationOwnerRole, StringComparison.Ordinal),
            "runtime" => result.PairIndex is { } pair && pair >= 1 && pair <= request.Manifest.Pairs.Length
                && string.Equals(result.CallerRole, request.Manifest.Pairs[pair - 1].Runtime, StringComparison.Ordinal),
            _ => false
        };
        if (!validCaller || result.StoreId == Guid.Empty || result.Status.PendingVersions.Count != 0
            || result.Status.RequiredVersion == 0 || result.Status.InstalledVersion < result.Status.RequiredVersion)
        {
            throw new DurablePreflightException("catalog_result");
        }
    }

    private static async ValueTask VerifyFenceAffinityAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_catalog.pg_backend_pid() = @backend AND EXISTS (
                SELECT 1 FROM pg_catalog.pg_locks WHERE locktype = 'advisory'
                  AND pid = @backend AND granted AND mode = 'ShareLock' AND objsubid = 1
                  AND classid::bigint = (@key >> 32) AND objid::bigint = (@key & 4294967295))
            """, connection, transaction);
        command.Parameters.AddWithValue("backend", connection.ProcessID);
        command.Parameters.AddWithValue("key", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new DurablePreflightException("cleanup");
        }
    }

    /// <summary>Classifies the credential and effective role inside the caller-owned fenced snapshot.</summary>
    /// <remarks>
    /// Neither opens a connection nor changes session identity or transaction ownership. Both PostgreSQL
    /// identities must resolve to the same OID on the original backend. This internal seam also allows tests
    /// to construct an actual SET ROLE session without depending on driver startup-option behavior.
    /// </remarks>
    internal static async ValueTask<(string Kind, string? Role, int? Pair, string? Failure)> ReadCallerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DurablePreflightRequest request, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT session_role.oid, assumed_role.oid,
                   array_position(@runtime_names::text[] COLLATE "C", session_role.rolname::text COLLATE "C"),
                   session_role.rolname::text COLLATE "C" = @owner_name::text COLLATE "C",
                   pg_catalog.pg_backend_pid()
            FROM pg_catalog.pg_roles session_role, pg_catalog.pg_roles assumed_role
            WHERE session_role.rolname::text COLLATE "C" = session_user::text COLLATE "C"
              AND assumed_role.rolname::text COLLATE "C" = current_user::text COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("runtime_names", request.Manifest.Pairs.Select(static pair => pair.Runtime).ToArray());
        command.Parameters.AddWithValue("owner_name", request.MigrationOwnerRole);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1)
            || reader.GetFieldValue<uint>(0) != reader.GetFieldValue<uint>(1) || reader.GetInt32(4) != connection.ProcessID)
        {
            return ("unresolved", null, null, "caller_identity");
        }
        var pair = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        var isOwner = !reader.IsDBNull(3) && reader.GetBoolean(3);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ("unresolved", null, null, "caller_identity");
        }
        if (pair is { } index && index >= 1 && index <= request.Manifest.Pairs.Length)
        {
            return ("runtime", request.Manifest.Pairs[index - 1].Runtime, index, null);
        }
        return isOwner ? ("owner-diagnostic", request.MigrationOwnerRole, null, null)
            : ("unresolved", null, null, "caller_role");
    }

    /// <summary>Exact raw policy-target predicate reused before any role join can erase unknown OIDs.</summary>
    /// <remarks>
    /// The owning SQL supplies polroles and runtimes.oids as oid arrays. Null, empty, PUBLIC, duplicates,
    /// omissions and extra unresolved OIDs fail. This internal expression seam also permits verification of
    /// catalog shapes that supported PostgreSQL DDL cannot construct, without unsafe catalog writes.
    /// </remarks>
    internal const string ExactRoleSetSql =
        "polroles IS NOT NULL AND cardinality(polroles) > 0 AND cardinality(runtimes.oids) > 0 " +
        "AND cardinality(polroles) = cardinality(runtimes.oids) " +
        "AND polroles @> runtimes.oids AND polroles <@ runtimes.oids " +
        "AND NOT 0::oid = ANY(polroles) " +
        "AND cardinality(polroles) = (SELECT count(DISTINCT value) FROM unnest(polroles) value)";

    private static readonly string CatalogSql =
        $"""
        WITH runtime_roles AS (
          SELECT names.name, names.ordinality AS pair, role.*
          FROM unnest(@runtime_names::text[]) WITH ORDINALITY names(name, ordinality)
          LEFT JOIN pg_catalog.pg_roles role ON role.rolname::text COLLATE "C" = names.name COLLATE "C"
        ), dispatcher_roles AS (
          SELECT names.name, role.oid
          FROM unnest(@dispatcher_names::text[]) names(name)
          LEFT JOIN pg_catalog.pg_roles role ON role.rolname::text COLLATE "C" = names.name COLLATE "C"
        ), reviewed_owner AS (
          SELECT oid FROM pg_catalog.pg_roles WHERE rolname::text COLLATE "C" = @owner_name::text COLLATE "C"
        ), namespace AS (
          SELECT oid, nspowner FROM pg_catalog.pg_namespace WHERE nspname = 'appsurface_durable'
        ), heartbeat AS (
          SELECT relation.* FROM pg_catalog.pg_class relation JOIN namespace ON namespace.oid = relation.relnamespace
          WHERE relation.relname = 'runtime_heartbeat' AND relation.relkind = 'r'
        ), runtimes AS (
          SELECT COALESCE(array_agg(oid ORDER BY oid) FILTER (WHERE oid IS NOT NULL), ARRAY[]::oid[]) AS oids FROM runtime_roles
        ), prune AS (
          SELECT routine.* FROM pg_catalog.pg_proc routine JOIN namespace ON namespace.oid = routine.pronamespace
          WHERE routine.proname = 'prune_runtime_heartbeats' AND routine.prokind = 'f'
            AND routine.prorettype = 'pg_catalog.int4'::regtype::oid AND NOT routine.proretset AND routine.pronargs = 4
            AND routine.proargtypes[0] = 'pg_catalog.interval'::regtype::oid
            AND routine.proargtypes[1] = 'pg_catalog.int4'::regtype::oid
            AND routine.proargtypes[2] = 'pg_catalog.text'::regtype::oid
            AND routine.proargtypes[3] = 'pg_catalog.uuid'::regtype::oid
        ), due AS (
          SELECT routine.* FROM pg_catalog.pg_proc routine JOIN namespace ON namespace.oid = routine.pronamespace
          WHERE routine.proname = 'runtime_due_dispatch_health' AND routine.prokind = 'f'
            AND routine.pronargs = 1 AND routine.proargtypes[0] = 'pg_catalog.int4'::regtype::oid
        ), acl_checks AS (
          SELECT routine.oid,
            NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(routine.proacl, pg_catalog.acldefault('f',routine.proowner))) privilege
              WHERE privilege.privilege_type <> 'EXECUTE' OR privilege.grantee = 0
                OR privilege.grantee <> (SELECT oid FROM reviewed_owner) AND NOT privilege.grantee = ANY(runtimes.oids)
                OR privilege.is_grantable AND privilege.grantee = ANY(runtimes.oids))
            AND NOT EXISTS (SELECT 1 FROM runtime_roles runtime WHERE runtime.oid IS NULL
              OR NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(routine.proacl, pg_catalog.acldefault('f',routine.proowner))) privilege
                WHERE privilege.grantee = runtime.oid AND privilege.privilege_type = 'EXECUTE' AND NOT privilege.is_grantable)
              OR NOT pg_catalog.has_function_privilege(runtime.oid,routine.oid,'EXECUTE')
              OR pg_catalog.has_function_privilege(runtime.oid,routine.oid,'EXECUTE WITH GRANT OPTION')) AS exact
          FROM (SELECT * FROM prune UNION ALL SELECT * FROM due) routine CROSS JOIN runtimes
        ), other_policies AS (
          SELECT expected.table_name, expected.policy_name, policy.polroles
          FROM (VALUES ('flow_dispatch','flow_dispatch_runtime_scope_select'),
            ('schedule_dispatch','schedule_dispatch_runtime_scope_select'),
            ('schedule_dispatch','schedule_dispatch_scope_update')) expected(table_name,policy_name)
          LEFT JOIN pg_catalog.pg_class relation ON relation.relname::text = expected.table_name
            AND relation.relnamespace = (SELECT oid FROM namespace)
          LEFT JOIN pg_catalog.pg_policy policy ON policy.polrelid = relation.oid AND policy.polname::text = expected.policy_name
        ), retention_index AS (
          SELECT index_class.oid FROM heartbeat
          JOIN pg_catalog.pg_index index_meta ON index_meta.indrelid = heartbeat.oid
          JOIN pg_catalog.pg_class index_class ON index_class.oid = index_meta.indexrelid
          JOIN pg_catalog.pg_am method ON method.oid = index_class.relam
          WHERE index_class.relname = 'ix_runtime_heartbeat_retention' AND method.amname = 'btree'
            AND index_meta.indisvalid AND index_meta.indisready AND NOT index_meta.indisunique
            AND index_meta.indnkeyatts = 2 AND index_meta.indnatts = 2
            AND index_meta.indpred IS NULL AND index_meta.indexprs IS NULL
            AND index_meta.indoption[0] = 0 AND index_meta.indoption[1] = 0
            AND (SELECT array_agg(attribute.attname ORDER BY key.ordinality)
              FROM unnest(index_meta.indkey) WITH ORDINALITY key(attnum,ordinality)
              JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = heartbeat.oid AND attribute.attnum = key.attnum)
              = ARRAY['last_heartbeat_at','worker_id']::name[]
            AND (SELECT array_agg(opclass.opcname ORDER BY key.ordinality)
              FROM unnest(index_meta.indclass) WITH ORDINALITY key(opclass_oid,ordinality)
              JOIN pg_catalog.pg_opclass opclass ON opclass.oid = key.opclass_oid
              JOIN pg_catalog.pg_am method ON method.oid = opclass.opcmethod
              JOIN pg_catalog.pg_namespace opnamespace ON opnamespace.oid = opclass.opcnamespace
              WHERE method.amname = 'btree' AND opnamespace.nspname = 'pg_catalog')
              = ARRAY['timestamptz_ops','text_ops']::name[]
        ), global_checks AS (
          SELECT array_remove(ARRAY[
            CASE WHEN NOT (SELECT count(*) = cardinality(@runtime_names::text[]) AND count(oid) = count(*) FROM runtime_roles)
              OR NOT (SELECT count(oid) = cardinality(@dispatcher_names::text[]) FROM dispatcher_roles)
              OR (SELECT count(*) FROM reviewed_owner) <> 1 THEN 'role_resolution' END,
            CASE WHEN (SELECT count(DISTINCT oid) <> count(*) FROM runtime_roles)
              OR (SELECT count(DISTINCT oid) <> count(*) FROM dispatcher_roles)
              OR EXISTS (SELECT 1 FROM runtime_roles r JOIN dispatcher_roles d ON d.oid = r.oid)
              OR EXISTS (SELECT 1 FROM reviewed_owner o WHERE o.oid IN (SELECT oid FROM runtime_roles UNION ALL SELECT oid FROM dispatcher_roles)) THEN 'role_alias' END,
            CASE WHEN NOT (SELECT count(*) = 1 FROM heartbeat WHERE relrowsecurity AND relforcerowsecurity) THEN 'forced_rls' END,
            CASE WHEN (SELECT count(*) FROM prune) <> 1 THEN 'function_signature' END,
            CASE WHEN NOT (SELECT count(*) = 1 FROM namespace WHERE nspowner = (SELECT oid FROM reviewed_owner))
              OR NOT (SELECT count(*) = 1 FROM heartbeat WHERE relowner = (SELECT oid FROM reviewed_owner))
              OR NOT (SELECT count(*) = 1 FROM prune WHERE proowner = (SELECT oid FROM reviewed_owner))
              OR NOT (SELECT count(*) = 1 FROM due WHERE proowner = (SELECT oid FROM reviewed_owner)) THEN 'function_owner' END,
            CASE WHEN NOT (SELECT count(*) = 1 FROM prune WHERE prosecdef) THEN 'security_definer' END,
            CASE WHEN NOT (SELECT count(*) = 1 FROM prune WHERE proconfig = ARRAY['search_path=pg_catalog, appsurface_durable, pg_temp']::text[]) THEN 'search_path' END,
            CASE WHEN (SELECT count(*) FROM pg_catalog.pg_policy policy WHERE policy.polrelid = (SELECT oid FROM heartbeat)) <> 2
              OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy CROSS JOIN runtimes
                WHERE policy.polrelid = (SELECT oid FROM heartbeat) AND policy.polname = 'runtime_heartbeat_runtime_role'
                  AND ({ExactRoleSetSql})
                  AND policy.polcmd = '*' AND policy.polpermissive
                  AND pg_catalog.pg_get_expr(policy.polqual,policy.polrelid) = 'true'
                  AND pg_catalog.pg_get_expr(policy.polwithcheck,policy.polrelid) = 'true')
              OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy WHERE policy.polrelid = (SELECT oid FROM heartbeat)
                AND policy.polname = 'runtime_heartbeat_migration_owner' AND policy.polroles = ARRAY[(SELECT oid FROM reviewed_owner)]::oid[]
                AND policy.polcmd = '*' AND policy.polpermissive AND pg_catalog.pg_get_expr(policy.polqual,policy.polrelid) = 'true'
                AND pg_catalog.pg_get_expr(policy.polwithcheck,policy.polrelid) = 'true') THEN 'heartbeat_policies' END,
            CASE WHEN (SELECT count(*) FROM other_policies) <> 3 OR EXISTS (SELECT 1 FROM other_policies CROSS JOIN runtimes
              WHERE NOT COALESCE(({ExactRoleSetSql}), false)) THEN 'runtime_policy_set' END,
            CASE WHEN NOT (SELECT count(*) = 1 FROM prune JOIN acl_checks USING(oid) WHERE exact) THEN 'function_acl' END,
            CASE WHEN NOT (SELECT count(*) = 1 FROM due JOIN acl_checks USING(oid) WHERE exact) THEN 'due_health_acl' END,
            CASE WHEN (SELECT count(*) FROM retention_index) <> 1 THEN 'retention_index' END
          ]::text[],NULL) AS failures
        ), role_checks AS (
          SELECT runtime.pair, array_remove(ARRAY[
            CASE WHEN runtime.oid IS NULL OR NOT runtime.rolcanlogin OR runtime.rolsuper OR runtime.rolcreatedb OR runtime.rolcreaterole
              OR runtime.rolreplication OR runtime.rolbypassrls THEN 'runtime_role:' || runtime.pair END,
            CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members membership WHERE membership.roleid = runtime.oid OR membership.member = runtime.oid)
              THEN 'role_membership:' || runtime.pair END,
            CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datdba = runtime.oid)
              OR EXISTS (SELECT 1 FROM namespace WHERE nspowner = runtime.oid)
              OR EXISTS (SELECT 1 FROM pg_catalog.pg_class relation JOIN namespace ON namespace.oid = relation.relnamespace WHERE relation.relowner = runtime.oid)
              OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc routine JOIN namespace ON namespace.oid = routine.pronamespace WHERE routine.proowner = runtime.oid)
              THEN 'runtime_ownership:' || runtime.pair END,
            CASE WHEN runtime.oid IS NULL OR (SELECT count(*) FROM heartbeat) <> 1
              OR EXISTS (SELECT 1 FROM heartbeat WHERE pg_catalog.has_table_privilege(runtime.oid,heartbeat.oid,'DELETE')
                OR pg_catalog.has_table_privilege(runtime.oid,heartbeat.oid,'TRUNCATE')) THEN 'runtime_table_privileges:' || runtime.pair END,
            CASE WHEN runtime.oid IS NULL OR (SELECT count(*) FROM prune) <> 1
              OR EXISTS (SELECT 1 FROM prune routine WHERE NOT pg_catalog.has_function_privilege(runtime.oid,routine.oid,'EXECUTE')
                OR pg_catalog.has_function_privilege(runtime.oid,routine.oid,'EXECUTE WITH GRANT OPTION')
                OR NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(routine.proacl,pg_catalog.acldefault('f',routine.proowner))) privilege
                  WHERE privilege.grantee = runtime.oid AND privilege.privilege_type = 'EXECUTE' AND NOT privilege.is_grantable))
              THEN 'function_acl:' || runtime.pair END,
            CASE WHEN runtime.oid IS NULL OR (SELECT count(*) FROM due) <> 1
              OR EXISTS (SELECT 1 FROM due routine WHERE NOT pg_catalog.has_function_privilege(runtime.oid,routine.oid,'EXECUTE')
                OR pg_catalog.has_function_privilege(runtime.oid,routine.oid,'EXECUTE WITH GRANT OPTION')
                OR NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(routine.proacl,pg_catalog.acldefault('f',routine.proowner))) privilege
                  WHERE privilege.grantee = runtime.oid AND privilege.privilege_type = 'EXECUTE' AND NOT privilege.is_grantable))
              THEN 'due_health_acl:' || runtime.pair END,
            CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class relation ON relation.oid = policy.polrelid
              JOIN namespace ON namespace.oid = relation.relnamespace
              WHERE policy.polname = 'flow_dispatch_retention_scope_select' AND runtime.oid = ANY(policy.polroles))
              THEN 'role_alias:' || runtime.pair END
          ]::text[],NULL) AS failures FROM runtime_roles runtime
        )
        SELECT (SELECT failures FROM global_checks) || COALESCE(
          (SELECT array_agg(category ORDER BY pair,ordinality) FROM role_checks
           CROSS JOIN LATERAL unnest(failures) WITH ORDINALITY checks(category,ordinality)), ARRAY[]::text[]);
        """;
}
