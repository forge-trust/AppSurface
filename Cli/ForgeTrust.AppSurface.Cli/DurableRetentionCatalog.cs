namespace ForgeTrust.AppSurface.Cli;

/// <summary>Shared PostgreSQL predicates for the Durable heartbeat-retention structure.</summary>
/// <remarks>
/// The fragments assume the aliases documented beside each property. They describe local structure only;
/// complete preflight adds its independently reviewed migration-owner identity and exact manifest ACL rules.
/// Keep this helper internal so catalog inspection does not become a deployment-authorization API.
/// </remarks>
internal static class DurableRetentionCatalog
{
    /// <summary>Requires the routine owner to match the owner of the Durable schema.</summary>
    /// <remarks>SQL aliases: <c>routine</c> is <c>pg_proc</c>; <c>namespace</c> is <c>pg_namespace</c>.</remarks>
    internal const string FunctionOwnerShapePredicateSql = "routine.proowner = namespace.nspowner";

    /// <summary>Requires a retention routine to execute as its owner.</summary>
    /// <remarks>SQL alias: <c>routine</c> is <c>pg_proc</c>.</remarks>
    internal const string FunctionSecurityDefinerPredicateSql = "routine.prosecdef";

    /// <summary>Requires the exact schema-11 hardened function search path.</summary>
    /// <remarks>SQL alias: <c>routine</c> is <c>pg_proc</c>; configuration text is compared only inside PostgreSQL.</remarks>
    internal const string FunctionSearchPathPredicateSql =
        "routine.proconfig = ARRAY['search_path=pg_catalog, appsurface_durable, pg_temp']::text[]";

    /// <summary>Requires the exact ascending, unfiltered schema-11 heartbeat-retention B-tree.</summary>
    /// <remarks>
    /// SQL aliases: <c>heartbeat</c> is the <c>runtime_heartbeat</c> table; <c>index_meta</c> is its
    /// <c>pg_index</c> row; <c>index_class</c> is the index's <c>pg_class</c> row; and <c>method</c> is
    /// its <c>pg_am</c> row. Each key definition and key-to-column collation is compared inside SQL without
    /// rendering the whole index, whose relation qualification can change with the caller's search path.
    /// No catalog definition text crosses the connection boundary.
    /// </remarks>
    internal const string RetentionIndexShapePredicateSql =
        """
        index_class.relname = 'ix_runtime_heartbeat_retention' AND index_class.relkind = 'i'
          AND method.amname = 'btree' AND index_meta.indisvalid AND index_meta.indisready AND NOT index_meta.indisunique
          AND index_meta.indnkeyatts = 2 AND index_meta.indnatts = 2
          AND index_meta.indpred IS NULL AND index_meta.indexprs IS NULL
          AND index_meta.indoption[0] = 0 AND index_meta.indoption[1] = 0
          AND (SELECT pg_catalog.array_agg(attribute.attname ORDER BY key.ordinality)
            FROM pg_catalog.unnest(index_meta.indkey) WITH ORDINALITY key(attnum,ordinality)
            JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = heartbeat.oid AND attribute.attnum = key.attnum)
            = ARRAY['last_heartbeat_at','worker_id']::name[]
          AND (SELECT pg_catalog.array_agg(opclass.opcname ORDER BY key.ordinality)
            FROM pg_catalog.unnest(index_meta.indclass) WITH ORDINALITY key(opclass_oid,ordinality)
            JOIN pg_catalog.pg_opclass opclass ON opclass.oid = key.opclass_oid
            JOIN pg_catalog.pg_am opclass_method ON opclass_method.oid = opclass.opcmethod
            JOIN pg_catalog.pg_namespace opnamespace ON opnamespace.oid = opclass.opcnamespace
            WHERE opclass_method.amname = 'btree' AND opnamespace.nspname = 'pg_catalog'
              AND opclass.opcdefault)
            = ARRAY['timestamptz_ops','text_ops']::name[]
          AND NOT EXISTS (
            SELECT 1 FROM pg_catalog.unnest(index_meta.indkey) WITH ORDINALITY key(attnum,ordinality)
            JOIN pg_catalog.pg_attribute attribute
              ON attribute.attrelid = heartbeat.oid AND attribute.attnum = key.attnum
            WHERE index_meta.indcollation[(key.ordinality - 1)::integer]
              IS DISTINCT FROM attribute.attcollation)
          AND pg_catalog.pg_get_indexdef(index_class.oid, 1, false) = 'last_heartbeat_at'
          AND pg_catalog.pg_get_indexdef(index_class.oid, 2, false) = 'worker_id'
        """;
}
