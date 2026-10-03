using Npgsql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Reads fixed credential and retention evidence from a caller-owned PostgreSQL snapshot.</summary>
/// <remarks>
/// Each operation executes one bounded statement and returns only ordered, package-defined failed categories.
/// Callers establish session affinity, compatible schema where required, and a read-only transaction first.
/// Catalog values such as role names, ACLs, function configuration and index definitions stay inside SQL.
/// </remarks>
internal static class DurableDoctorCatalog
{
    private const string InvalidProjectionMessage = "The Durable doctor catalog projection was malformed.";

    private static readonly string CredentialSql =
        """
        WITH role_ids AS (
          SELECT
            (SELECT role.oid FROM pg_catalog.pg_roles role
             WHERE role.rolname::text COLLATE "C" = session_user::text COLLATE "C") AS session_oid,
            (SELECT role.oid FROM pg_catalog.pg_roles role
             WHERE role.rolname::text COLLATE "C" = current_user::text COLLATE "C") AS current_oid
        ), durable_namespace AS (
          SELECT namespace.oid, namespace.nspowner
          FROM pg_catalog.pg_namespace namespace
          WHERE namespace.nspname = 'appsurface_durable'
        )
        SELECT
          COALESCE((SELECT session_oid IS NOT NULL AND current_oid IS NOT NULL AND session_oid = current_oid
                    FROM role_ids), false),
          COALESCE((SELECT role.rolcanlogin AND NOT role.rolsuper AND NOT role.rolcreatedb
                            AND NOT role.rolcreaterole AND NOT role.rolreplication AND NOT role.rolbypassrls
                    FROM pg_catalog.pg_roles role CROSS JOIN role_ids
                    WHERE role.oid = role_ids.current_oid), false),
          NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_auth_members membership CROSS JOIN role_ids
            WHERE membership.roleid = role_ids.current_oid OR membership.member = role_ids.current_oid),
          NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_database database_row CROSS JOIN role_ids
            WHERE database_row.datdba = role_ids.current_oid
            UNION ALL
            SELECT 1 FROM durable_namespace namespace CROSS JOIN role_ids
            WHERE namespace.nspowner = role_ids.current_oid
            UNION ALL
            SELECT 1 FROM pg_catalog.pg_class relation
            JOIN durable_namespace namespace ON namespace.oid = relation.relnamespace
            CROSS JOIN role_ids WHERE relation.relowner = role_ids.current_oid
            UNION ALL
            SELECT 1 FROM pg_catalog.pg_proc routine
            JOIN durable_namespace namespace ON namespace.oid = routine.pronamespace
            CROSS JOIN role_ids WHERE routine.proowner = role_ids.current_oid),
          NOT (
            EXISTS (
              SELECT 1 FROM durable_namespace namespace CROSS JOIN role_ids
              WHERE pg_catalog.has_schema_privilege(role_ids.current_oid, namespace.oid, 'USAGE WITH GRANT OPTION')
                 OR pg_catalog.has_schema_privilege(role_ids.current_oid, namespace.oid, 'CREATE WITH GRANT OPTION'))
            OR EXISTS (
              SELECT 1 FROM pg_catalog.pg_class relation
              JOIN durable_namespace namespace ON namespace.oid = relation.relnamespace
              CROSS JOIN role_ids
              WHERE relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                AND (pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'SELECT WITH GRANT OPTION')
                  OR pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'INSERT WITH GRANT OPTION')
                  OR pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'UPDATE WITH GRANT OPTION')
                  OR pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'DELETE WITH GRANT OPTION')
                  OR pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'TRUNCATE WITH GRANT OPTION')
                  OR pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'REFERENCES WITH GRANT OPTION')
                  OR pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'TRIGGER WITH GRANT OPTION')
                  OR CASE WHEN pg_catalog.current_setting('server_version_num')::integer >= 170000
                     THEN pg_catalog.has_table_privilege(role_ids.current_oid, relation.oid, 'MAINTAIN WITH GRANT OPTION')
                     ELSE false END)
              UNION ALL
              SELECT 1 FROM pg_catalog.pg_class relation
              JOIN durable_namespace namespace ON namespace.oid = relation.relnamespace
              CROSS JOIN role_ids
              WHERE relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                AND (pg_catalog.has_any_column_privilege(role_ids.current_oid, relation.oid, 'SELECT WITH GRANT OPTION')
                  OR pg_catalog.has_any_column_privilege(role_ids.current_oid, relation.oid, 'INSERT WITH GRANT OPTION')
                  OR pg_catalog.has_any_column_privilege(role_ids.current_oid, relation.oid, 'UPDATE WITH GRANT OPTION')
                  OR pg_catalog.has_any_column_privilege(role_ids.current_oid, relation.oid, 'REFERENCES WITH GRANT OPTION'))
              UNION ALL
              SELECT 1 FROM pg_catalog.pg_class relation
              JOIN durable_namespace namespace ON namespace.oid = relation.relnamespace
              CROSS JOIN role_ids
              WHERE relation.relkind = 'S'
                AND (pg_catalog.has_sequence_privilege(role_ids.current_oid, relation.oid, 'USAGE WITH GRANT OPTION')
                  OR pg_catalog.has_sequence_privilege(role_ids.current_oid, relation.oid, 'SELECT WITH GRANT OPTION')
                  OR pg_catalog.has_sequence_privilege(role_ids.current_oid, relation.oid, 'UPDATE WITH GRANT OPTION'))
              UNION ALL
              SELECT 1 FROM pg_catalog.pg_proc routine
              JOIN durable_namespace namespace ON namespace.oid = routine.pronamespace
              CROSS JOIN role_ids
              WHERE pg_catalog.has_function_privilege(role_ids.current_oid, routine.oid, 'EXECUTE WITH GRANT OPTION'))),
          NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_class heartbeat
            JOIN durable_namespace namespace ON namespace.oid = heartbeat.relnamespace
            CROSS JOIN role_ids
            WHERE heartbeat.relname = 'runtime_heartbeat' AND heartbeat.relkind = 'r'
              AND (pg_catalog.has_table_privilege(role_ids.current_oid, heartbeat.oid, 'DELETE')
                OR pg_catalog.has_table_privilege(role_ids.current_oid, heartbeat.oid, 'TRUNCATE')))
        """;

    private static readonly string RetentionSql =
        $"""
        WITH durable_namespace AS (
          SELECT namespace.oid, namespace.nspowner
          FROM pg_catalog.pg_namespace namespace
          WHERE namespace.nspname = 'appsurface_durable'
        ), retention_signature AS (
          SELECT pg_catalog.to_regprocedure(
            'appsurface_durable.prune_runtime_heartbeats(pg_catalog.interval,pg_catalog.int4,pg_catalog.text,pg_catalog.uuid)')::oid AS oid
        ), routine AS (
          SELECT function_row.* FROM pg_catalog.pg_proc function_row
          JOIN durable_namespace namespace ON namespace.oid = function_row.pronamespace
          CROSS JOIN retention_signature signature
          WHERE function_row.oid = signature.oid
        ), heartbeat AS (
          SELECT relation.oid FROM pg_catalog.pg_class relation
          JOIN durable_namespace namespace ON namespace.oid = relation.relnamespace
          WHERE relation.relname = 'runtime_heartbeat' AND relation.relkind = 'r'
        ), retention_indexes AS (
          SELECT index_meta.*, index_class.oid AS index_oid, heartbeat.oid AS heartbeat_oid,
                 index_class.relname, index_class.relkind, method.amname
          FROM heartbeat
          JOIN pg_catalog.pg_index index_meta ON index_meta.indrelid = heartbeat.oid
          JOIN pg_catalog.pg_class index_class ON index_class.oid = index_meta.indexrelid
          JOIN pg_catalog.pg_am method ON method.oid = index_class.relam
          WHERE index_class.relname = 'ix_runtime_heartbeat_retention'
        )
        SELECT
          COALESCE((SELECT routine.prokind = 'f'
                       AND routine.prorettype = 'pg_catalog.int4'::pg_catalog.regtype::oid
                       AND NOT routine.proretset FROM routine), false),
          NOT EXISTS (SELECT 1 FROM routine)
            OR EXISTS (SELECT 1 FROM routine CROSS JOIN durable_namespace namespace
                       WHERE ({DurableRetentionCatalog.FunctionOwnerShapePredicateSql})),
          NOT EXISTS (SELECT 1 FROM routine)
            OR EXISTS (SELECT 1 FROM routine WHERE {DurableRetentionCatalog.FunctionSecurityDefinerPredicateSql}),
          NOT EXISTS (SELECT 1 FROM routine)
            OR EXISTS (SELECT 1 FROM routine WHERE {DurableRetentionCatalog.FunctionSearchPathPredicateSql}),
          NOT EXISTS (SELECT 1 FROM routine)
            OR EXISTS (
              SELECT 1 FROM routine CROSS JOIN LATERAL pg_catalog.aclexplode(
                COALESCE(routine.proacl, pg_catalog.acldefault('f', routine.proowner))) privilege
              WHERE privilege.grantee = (SELECT role.oid FROM pg_catalog.pg_roles role
                                          WHERE role.rolname::text COLLATE "C" = current_user::text COLLATE "C")
                AND privilege.privilege_type = 'EXECUTE' AND NOT privilege.is_grantable
                AND pg_catalog.has_function_privilege(
                  (SELECT role.oid FROM pg_catalog.pg_roles role
                   WHERE role.rolname::text COLLATE "C" = current_user::text COLLATE "C"),
                  routine.oid, 'EXECUTE')),
          NOT EXISTS (SELECT 1 FROM routine)
            OR NOT EXISTS (
              SELECT 1 FROM routine CROSS JOIN LATERAL pg_catalog.aclexplode(
                COALESCE(routine.proacl, pg_catalog.acldefault('f', routine.proowner))) privilege
              WHERE privilege.grantee = 0 AND privilege.privilege_type = 'EXECUTE'),
          NOT EXISTS (SELECT 1 FROM routine)
            OR NOT EXISTS (
              SELECT 1 FROM routine WHERE pg_catalog.has_function_privilege(
                (SELECT role.oid FROM pg_catalog.pg_roles role
                 WHERE role.rolname::text COLLATE "C" = current_user::text COLLATE "C"),
                routine.oid, 'EXECUTE WITH GRANT OPTION')),
          EXISTS (SELECT 1 FROM retention_indexes),
          NOT EXISTS (SELECT 1 FROM retention_indexes)
            OR EXISTS (
              SELECT 1 FROM retention_indexes index_meta
              JOIN pg_catalog.pg_class index_class ON index_class.oid = index_meta.index_oid
              JOIN pg_catalog.pg_am method ON method.oid = index_class.relam
              JOIN pg_catalog.pg_class heartbeat ON heartbeat.oid = index_meta.heartbeat_oid
              WHERE ({DurableRetentionCatalog.RetentionIndexShapePredicateSql}))
        """;

    /// <summary>Reads the six fixed connected-credential checks from the caller's active transaction.</summary>
    /// <param name="connection">The already-affinitized physical PostgreSQL session.</param>
    /// <param name="transaction">The caller-owned transaction containing the catalog snapshot.</param>
    /// <param name="cancellationToken">Cancels this one bounded catalog statement.</param>
    /// <returns>Credential failure categories in the fixed doctor contract order.</returns>
    /// <remarks>Ownership, grant-option and direct heartbeat ACL checks use the live catalog namespace; no relation is resolved through <c>regclass</c>.</remarks>
    internal static ValueTask<IReadOnlyList<string>> ReadCredentialAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        ReadProjectionAsync(connection, transaction, cancellationToken, CredentialSql, DurableDoctorChecks.Credential,
            MapCredentialProjection);

    /// <summary>Reads the nine fixed schema-11 retention checks from a compatible-schema snapshot.</summary>
    /// <param name="connection">The already-affinitized physical PostgreSQL session.</param>
    /// <param name="transaction">The caller-owned transaction containing the catalog snapshot.</param>
    /// <param name="cancellationToken">Cancels this one bounded catalog statement.</param>
    /// <returns>Retention failure categories in the fixed doctor contract order.</returns>
    /// <remarks>Missing the exact function signature contributes only that finding; absent dependent function facts are treated as not applicable.</remarks>
    internal static ValueTask<IReadOnlyList<string>> ReadRetentionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        ReadProjectionAsync(connection, transaction, cancellationToken, RetentionSql, DurableDoctorChecks.Retention,
            MapRetentionProjection);

    /// <summary>Maps a six-boolean credential projection without retaining provider-controlled values.</summary>
    /// <param name="projection">The fixed SQL projection captured by the reader.</param>
    /// <returns>Failed categories in the credential contract order.</returns>
    /// <exception cref="InvalidOperationException">The projection is missing, has the wrong width, or contains a non-boolean value.</exception>
    internal static IReadOnlyList<string> MapCredentialProjection(object?[]? projection) =>
        MapProjection(projection, DurableDoctorChecks.Credential);

    /// <summary>Maps a nine-boolean retention projection without retaining provider-controlled values.</summary>
    /// <param name="projection">The fixed SQL projection captured by the reader.</param>
    /// <returns>Failed categories in the retention contract order.</returns>
    /// <exception cref="InvalidOperationException">The projection is missing, has the wrong width, or contains a non-boolean value.</exception>
    internal static IReadOnlyList<string> MapRetentionProjection(object?[]? projection) =>
        MapProjection(projection, DurableDoctorChecks.Retention);

    private static async ValueTask<IReadOnlyList<string>> ReadProjectionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken,
        string sql, IReadOnlyList<string> categories, Func<object?[]?, IReadOnlyList<string>> mapper)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (reader.FieldCount != categories.Count || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw InvalidProjection();
        }

        var projection = new object?[reader.FieldCount];
        for (var ordinal = 0; ordinal < projection.Length; ordinal++)
        {
            projection[ordinal] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw InvalidProjection();
        }
        return mapper(projection);
    }

    private static IReadOnlyList<string> MapProjection(object?[]? projection, IReadOnlyList<string> categories)
    {
        if (projection is null || projection.Length != categories.Count || projection.Any(static value => value is not bool))
        {
            throw InvalidProjection();
        }

        var failures = new List<string>();
        for (var ordinal = 0; ordinal < projection.Length; ordinal++)
        {
            if (projection[ordinal] is false)
            {
                failures.Add(categories[ordinal]);
            }
        }
        return Array.AsReadOnly(failures.ToArray());
    }

    private static InvalidOperationException InvalidProjection() => new(InvalidProjectionMessage);
}
