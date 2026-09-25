\set ON_ERROR_STOP on

BEGIN;

-- Advisory transaction locks have no native timeout. Poll without blocking so
-- the role-recipe lock is bounded independently of policy-DDL lock waits.
DO $lock$
DECLARE deadline timestamptz := clock_timestamp() + interval '30 seconds';
BEGIN
  LOOP
    EXIT WHEN pg_catalog.pg_try_advisory_xact_lock(4707181168775217740);
    IF clock_timestamp() >= deadline THEN
      RAISE EXCEPTION 'Durable role-recipe lock timed out after 30 seconds; retry after the schema operation or maintenance window completes.';
    END IF;
    PERFORM pg_catalog.pg_sleep(0.05);
  END LOOP;
END
$lock$;

-- Check psql inputs before the first interpolation, so omitted variables receive
-- bounded operator guidance instead of reaching PostgreSQL as raw tokens.
\if :{?role_pairs_json}
\else
  \echo 'Missing required role_pairs_json manifest (version 1).'
  \echo 'Cause: psql was invoked without the complete reviewed role-pair manifest.'
  \echo 'Fix: pass -v role_pairs_json=<JSON manifest> containing every configured pair; omission never retires a pair.'
  \echo 'Guide: Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md (Role recipe and role-pair manifest).'
  SELECT 1 / 0;
\endif
\if :{?migration_owner_role}
\else
  \echo 'Missing required migration_owner_role.'
  \echo 'Cause: psql was invoked without the explicit Durable object-owner role name.'
  \echo 'Fix: pass -v migration_owner_role=<existing restricted owner role> as data.'
  \echo 'Guide: Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md (Role recipe and role-pair manifest).'
  SELECT 1 / 0;
\endif
\if :{?retention_operator_role}
\else
  \echo 'Missing required retention_operator_role.'
  \echo 'Cause: psql was invoked without the explicit Durable retention operator role name.'
  \echo 'Fix: pass -v retention_operator_role=<existing restricted retention role> as data.'
  \echo 'Guide: Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md (Role recipe and role-pair manifest).'
  SELECT 1 / 0;
\endif

-- Preserve original json long enough to detect duplicate property names.
SELECT pg_catalog.pg_input_is_valid(:'role_pairs_json', 'json') AS manifest_is_json \gset
\if :manifest_is_json
\else
  \echo 'Invalid role_pairs_json: the value is not valid JSON.'
  SELECT 1 / 0;
\endif
CREATE TEMP TABLE role_manifest_input(raw json NOT NULL) ON COMMIT DROP;
INSERT INTO role_manifest_input VALUES (:'role_pairs_json'::json);
SELECT json_typeof(raw) = 'object'
   AND CASE
         WHEN json_typeof(raw) <> 'object' THEN false
         ELSE (SELECT count(*) FROM json_each(raw)) = 2
          AND (SELECT count(*) FROM json_each(raw) WHERE key IN ('version', 'pairs')) = 2
          AND json_typeof(raw->'version') = 'number'
          AND raw->>'version' = '1'
          AND json_typeof(raw->'pairs') = 'array'
          AND json_array_length(raw->'pairs') BETWEEN 1 AND 32
       END AS manifest_top_level_valid
FROM role_manifest_input \gset
\if :manifest_top_level_valid
\else
  \echo 'Invalid role_pairs_json: expected exactly version 1 and pairs containing 1-32 entries; duplicate or unknown properties are rejected.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS (
  SELECT 1 FROM role_manifest_input
  CROSS JOIN LATERAL json_array_elements(raw->'pairs') AS entry(value)
  WHERE CASE WHEN json_typeof(value) <> 'object' THEN true ELSE
        (SELECT count(*) FROM json_each(value)) <> 3
     OR (SELECT count(*) FROM json_each(value) WHERE key IN ('dispatcher','runtime','dispatcher_profile')) <> 3
     OR json_typeof(value->'dispatcher') <> 'string'
     OR json_typeof(value->'runtime') <> 'string'
     OR json_typeof(value->'dispatcher_profile') <> 'string'
     OR value->>'dispatcher_profile' NOT IN ('full','work_only')
     OR octet_length(value->>'dispatcher') NOT BETWEEN 1 AND 63
     OR octet_length(value->>'runtime') NOT BETWEEN 1 AND 63
     OR value->>'dispatcher' ~ '[[:cntrl:]]'
     OR value->>'runtime' ~ '[[:cntrl:]]'
       END
) AS manifest_pairs_valid \gset
\if :manifest_pairs_valid
\else
  \echo 'Invalid role_pairs_json entry: require exactly dispatcher, runtime, and explicit dispatcher_profile (full or work_only), with untruncated role names.'
  SELECT 1 / 0;
\endif

CREATE TEMP TABLE role_pair (
  dispatcher_name text NOT NULL,
  runtime_name text NOT NULL,
  dispatcher_profile text NOT NULL,
  dispatcher_oid oid,
  runtime_oid oid
) ON COMMIT DROP;
INSERT INTO role_pair
SELECT value->>'dispatcher', value->>'runtime', value->>'dispatcher_profile', d.oid, r.oid
FROM role_manifest_input
CROSS JOIN LATERAL json_array_elements(raw->'pairs') AS entry(value)
LEFT JOIN pg_catalog.pg_roles d ON d.rolname = value->>'dispatcher'
LEFT JOIN pg_catalog.pg_roles r ON r.rolname = value->>'runtime';

SELECT bool_and(dispatcher_oid IS NOT NULL AND runtime_oid IS NOT NULL
                AND dispatcher_name = dispatcher_name::name::text
                AND runtime_name = runtime_name::name::text)
   AND count(*) = count(DISTINCT dispatcher_oid)
   AND count(*) = count(DISTINCT runtime_oid)
   AND (SELECT count(*) FROM (
          SELECT dispatcher_oid AS role_oid FROM role_pair
          UNION SELECT runtime_oid FROM role_pair
        ) all_pair_roles) = count(*) * 2
   AND NOT EXISTS (
     SELECT 1 FROM role_pair p CROSS JOIN LATERAL
       (VALUES (p.dispatcher_oid), (p.runtime_oid)) roles(role_oid)
     JOIN pg_catalog.pg_roles r ON r.oid = roles.role_oid
     WHERE NOT r.rolcanlogin OR r.rolsuper OR r.rolcreatedb OR r.rolcreaterole
        OR r.rolreplication OR r.rolbypassrls
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid OR m.roleid = r.oid)
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_database db WHERE db.datdba = r.oid)
   ) AS pair_roles_valid
FROM role_pair
\gset
\if :pair_roles_valid
\else
  \echo 'Manifest roles must resolve to distinct restricted, membership-free LOGIN leaves that own no database; repair the listed roles and retry.'
  SELECT 1 / 0;
\endif

SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'migration_owner_role')
   AND EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = :'retention_operator_role')
   AND octet_length(:'migration_owner_role') BETWEEN 1 AND 63
   AND octet_length(:'retention_operator_role') BETWEEN 1 AND 63
   AND :'migration_owner_role' !~ '[[:cntrl:]]'
   AND :'retention_operator_role' !~ '[[:cntrl:]]'
   AND :'migration_owner_role' <> :'retention_operator_role'
   AND NOT EXISTS (SELECT 1 FROM role_pair WHERE dispatcher_name IN (:'migration_owner_role', :'retention_operator_role')
                                         OR runtime_name IN (:'migration_owner_role', :'retention_operator_role'))
   AND NOT EXISTS (
     SELECT 1 FROM pg_catalog.pg_roles r
     WHERE r.rolname = :'retention_operator_role'
       AND (NOT r.rolcanlogin OR r.rolsuper OR r.rolcreatedb OR r.rolcreaterole
            OR r.rolreplication OR r.rolbypassrls
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid OR m.roleid = r.oid)
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_database db WHERE db.datdba = r.oid))
   )
   AS fixed_roles_valid \gset
\if :fixed_roles_valid
\else
  \echo 'Migration owner and retention operator must exist and remain distinct from every manifest role.'
  SELECT 1 / 0;
\endif

-- Reject unexplained and omitted principals before changing owners, grants or policies.
-- First report structural RLS drift with its dedicated diagnostic; it is checked
-- again below immediately before any catalog mutation.
SELECT bool_and(
    object.relrowsecurity = (object.relname IN
      ('scope','scope_history','work','work_history','dispatch','work_operator_command','effect_permit','runtime_heartbeat',
       'flow_instance','flow_command','flow_history','flow_wait','flow_timer','flow_dispatch','flow_repair_command','flow_repair_collision',
       'schedule_definition','schedule_generation','schedule_command','schedule_occurrence','schedule_dispatch','schedule_history',
       'flow_trace_context','flow_retention_manifest','flow_retention_manifest_item','flow_retention_manifest_summary',
       'flow_retention_manifest_event','flow_retention_command') OR object.relname LIKE 'schedule_history_%')
    AND object.relforcerowsecurity = (object.relname IN
      ('scope','scope_history','work','work_history','dispatch','work_operator_command','effect_permit','runtime_heartbeat',
       'flow_instance','flow_command','flow_history','flow_wait','flow_timer','flow_dispatch','flow_repair_command','flow_repair_collision',
       'schedule_definition','schedule_generation','schedule_command','schedule_occurrence','schedule_dispatch','schedule_history',
       'flow_trace_context','flow_retention_manifest','flow_retention_manifest_item','flow_retention_manifest_summary',
       'flow_retention_manifest_event','flow_retention_command') OR object.relname LIKE 'schedule_history_%'))
  AS early_rls_flags_are_exact
FROM pg_catalog.pg_class object
JOIN pg_catalog.pg_namespace namespace ON namespace.oid=object.relnamespace
WHERE namespace.nspname='appsurface_durable' AND object.relkind IN ('r','p') \gset
\if :early_rls_flags_are_exact
\else
  \echo 'Durable row-level security flags must exactly match the package migration.'
  WITH observed(role_oid) AS (
    SELECT a.grantee FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
      CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) a WHERE n.nspname='appsurface_durable'
    UNION SELECT a.grantee FROM pg_catalog.pg_attribute at JOIN pg_catalog.pg_class c ON c.oid=at.attrelid
      JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
      CROSS JOIN LATERAL pg_catalog.aclexplode(at.attacl) a WHERE n.nspname='appsurface_durable'
    UNION SELECT a.grantee FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
      CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a WHERE n.nspname='appsurface_durable'
    UNION SELECT target.role_oid FROM pg_catalog.pg_policy p
      JOIN pg_catalog.pg_class c ON c.oid=p.polrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
      CROSS JOIN LATERAL unnest(p.polroles) target(role_oid) WHERE n.nspname='appsurface_durable'
  ), omitted AS (
    SELECT DISTINCT r.rolname FROM observed o JOIN pg_catalog.pg_roles r ON r.oid=o.role_oid
    WHERE o.role_oid<>0 AND r.rolname NOT IN (:'migration_owner_role',:'retention_operator_role')
      AND NOT EXISTS (SELECT 1 FROM role_pair p WHERE p.dispatcher_oid=o.role_oid OR p.runtime_oid=o.role_oid)
  )
  SELECT left(COALESCE(string_agg(regexp_replace(rolname,'[[:cntrl:]]','?','g'), ', ' ORDER BY rolname),''),512)
           AS early_unmanifested_names,
         EXISTS(SELECT 1 FROM omitted) AS early_has_unmanifested_names
  FROM omitted \gset
  \if :early_has_unmanifested_names
    \echo 'Rejected unmanifested Durable role principal(s):' :early_unmanifested_names
    \echo 'Cause: the catalog also contains prior service roles or unexplained principals absent from role_pairs_json.'
    \echo 'Fix: include every authorized pair in the reviewed manifest or resolve hostile catalog drift through the reviewed repair procedure.'
  \endif
  SELECT 1 / 0;
\endif
SELECT NOT EXISTS (
  SELECT 1
  FROM pg_catalog.pg_policy policy
  JOIN pg_catalog.pg_class relation ON relation.oid=policy.polrelid
  JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
  WHERE namespace.nspname='appsurface_durable'
    AND relation.relname='work'
    AND policy.polname NOT IN ('work_scope_isolation','work_contract_discovery_owner')
) AS early_work_policies_are_exact \gset
\if :early_work_policies_are_exact
\else
  \echo 'Durable row-level security policies must exactly match the package migration.'
  SELECT 1 / 0;
\endif

-- Refuse hostile PUBLIC ACLs before generic principal reporting, while still
-- admitting only PostgreSQL's non-grantable default function EXECUTE entry.
SELECT NOT EXISTS (
  SELECT 1 FROM pg_catalog.pg_namespace n
  CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
  UNION ALL
  SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
  UNION ALL
  SELECT 1 FROM pg_catalog.pg_attribute at JOIN pg_catalog.pg_class c ON c.oid=at.attrelid
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(at.attacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
  UNION ALL
  SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
    AND (a.privilege_type<>'EXECUTE' OR a.is_grantable)
) AS early_public_acl_is_migration_baseline \gset
\if :early_public_acl_is_migration_baseline
\else
  \echo 'Unexpected PUBLIC privilege or grant option exists on the Durable schema, relations, columns, sequences, or functions; remove the drift and retry.'
  SELECT 1 / 0;
\endif

WITH observed(role_oid, bootstrap_owner_entry) AS (
  SELECT target.role_oid,
         c.relowner=target.role_oid
           AND p.polname='work_contract_discovery_owner'
           AND c.relname='work'
  FROM pg_catalog.pg_policy p
  JOIN pg_catalog.pg_class c ON c.oid=p.polrelid
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL unnest(p.polroles) target(role_oid)
  WHERE n.nspname='appsurface_durable'
  UNION
  SELECT a.grantee, c.relowner=a.grantee
  FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) a WHERE n.nspname='appsurface_durable'
  UNION
  SELECT a.grantee, c.relowner=a.grantee
  FROM pg_catalog.pg_attribute att JOIN pg_catalog.pg_class c ON c.oid=att.attrelid
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(att.attacl) a WHERE n.nspname='appsurface_durable'
  UNION
  SELECT a.grantee, f.proowner=a.grantee
  FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a WHERE n.nspname='appsurface_durable'
  UNION
  SELECT a.grantee, n.nspowner=a.grantee
  FROM pg_catalog.pg_namespace n
  CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a WHERE n.nspname='appsurface_durable'
), omitted_roles AS (
  SELECT DISTINCT r.rolname AS role_name
  FROM observed o JOIN pg_catalog.pg_roles r ON r.oid=o.role_oid
  WHERE o.role_oid<>0
    AND r.rolname NOT IN (:'migration_owner_role', :'retention_operator_role')
    AND NOT EXISTS (SELECT 1 FROM role_pair p WHERE p.dispatcher_oid=o.role_oid OR p.runtime_oid=o.role_oid)
    AND NOT o.bootstrap_owner_entry
)
SELECT left(COALESCE((
  SELECT string_agg(regexp_replace(role_name,'[[:cntrl:]]','?','g'), ', '
                    ORDER BY regexp_replace(role_name,'[[:cntrl:]]','?','g')) FROM omitted_roles
), ''), 512) AS unmanifested_role_names \gset

SELECT NOT EXISTS (
  SELECT 1 FROM (
    SELECT target.role_oid,
           c.relowner=target.role_oid
             AND ((p.polname='work_contract_discovery_owner' AND c.relname='work')
               OR (p.polname='runtime_heartbeat_migration_owner' AND c.relname='runtime_heartbeat'))
                 AS bootstrap_owner_entry
    FROM pg_catalog.pg_policy p
    JOIN pg_catalog.pg_class c ON c.oid = p.polrelid
    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN LATERAL unnest(p.polroles) target(role_oid)
    WHERE n.nspname = 'appsurface_durable'
    UNION
    SELECT a.grantee, c.relowner = a.grantee
    FROM pg_catalog.pg_class c
    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) a WHERE n.nspname = 'appsurface_durable'
    UNION
    SELECT a.grantee, c.relowner = a.grantee
    FROM pg_catalog.pg_attribute att
    JOIN pg_catalog.pg_class c ON c.oid = att.attrelid
    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    CROSS JOIN LATERAL pg_catalog.aclexplode(att.attacl) a WHERE n.nspname = 'appsurface_durable'
    UNION
    SELECT a.grantee, f.proowner = a.grantee
    FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid = f.pronamespace
    CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a WHERE n.nspname = 'appsurface_durable'
    UNION
    SELECT a.grantee, n.nspowner = a.grantee
    FROM pg_catalog.pg_namespace n
    CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a WHERE n.nspname = 'appsurface_durable'
  ) observed
  WHERE role_oid <> 0
    AND role_oid NOT IN ((SELECT oid FROM pg_catalog.pg_roles WHERE rolname = :'migration_owner_role'),
                         (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = :'retention_operator_role'))
    AND NOT EXISTS (SELECT 1 FROM role_pair p WHERE p.dispatcher_oid = observed.role_oid OR p.runtime_oid = observed.role_oid)
    -- Migration-created ACLs may name their actual current object owner. The
    -- object-specific predicate above does not exempt that role on other ACLs.
    AND NOT observed.bootstrap_owner_entry
) AS catalog_principals_are_manifested \gset
\if :catalog_principals_are_manifested
\else
  \echo 'Rejected unmanifested Durable role principal(s):' :unmanifested_role_names
  \echo 'Cause: a prior dispatcher/runtime role or unexplained catalog principal is absent from role_pairs_json.'
  \echo 'Fix: include every previously authorized pair in the reviewed manifest, or resolve hostile catalog drift through the reviewed repair procedure. Do not use omission as retirement.'
  \echo 'Guide: Durable/operational-assessments.md#shared-store-role-pair-choice'
  SELECT 1 / 0;
\endif

-- A clean schema-10 migration has one PUBLIC heartbeat policy and no service
-- ACL evidence. Configured stores may have any consistent nonempty prior
-- subset of the new manifest while enrolling additional pairs.
SELECT (count(*) = 1
        AND bool_and(policy.polname = 'runtime_heartbeat_runtime_role'
                     AND policy.polroles = ARRAY[0::oid])
        AND NOT EXISTS (
          SELECT 1 FROM (
            SELECT a.grantee FROM pg_catalog.pg_class x JOIN pg_catalog.pg_namespace xn ON xn.oid=x.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(x.relacl) a WHERE xn.nspname='appsurface_durable'
            UNION ALL SELECT a.grantee FROM pg_catalog.pg_attribute at JOIN pg_catalog.pg_class x ON x.oid=at.attrelid
              JOIN pg_catalog.pg_namespace xn ON xn.oid=x.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(at.attacl) a WHERE xn.nspname='appsurface_durable'
            UNION ALL SELECT a.grantee FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace xn ON xn.oid=f.pronamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a WHERE xn.nspname='appsurface_durable'
          ) traces WHERE traces.grantee IN
            (SELECT dispatcher_oid FROM role_pair UNION SELECT runtime_oid FROM role_pair)
        )) AS runtime_policy_bootstrap
FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class c ON c.oid=policy.polrelid
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='appsurface_durable' AND policy.polname IN
  ('runtime_heartbeat_runtime_role','flow_dispatch_runtime_scope_select',
   'schedule_dispatch_runtime_scope_select','schedule_dispatch_scope_update')
\gset
SELECT (count(*) > 0 AND bool_and(cardinality(policy.polroles) > 0
                                  AND policy.polroles <@ ARRAY(SELECT runtime_oid FROM role_pair ORDER BY runtime_oid))
        AND NOT EXISTS (
          SELECT 1 FROM pg_catalog.pg_policy left_policy
          JOIN pg_catalog.pg_class lc ON lc.oid=left_policy.polrelid
          JOIN pg_catalog.pg_namespace ln ON ln.oid=lc.relnamespace
          JOIN pg_catalog.pg_policy right_policy ON right_policy.polname IN
            ('runtime_heartbeat_runtime_role','flow_dispatch_runtime_scope_select',
             'schedule_dispatch_runtime_scope_select','schedule_dispatch_scope_update')
          JOIN pg_catalog.pg_class rc ON rc.oid=right_policy.polrelid
          JOIN pg_catalog.pg_namespace rn ON rn.oid=rc.relnamespace
          WHERE ln.nspname='appsurface_durable' AND rn.nspname='appsurface_durable'
            AND left_policy.polname IN ('runtime_heartbeat_runtime_role','flow_dispatch_runtime_scope_select',
             'schedule_dispatch_runtime_scope_select','schedule_dispatch_scope_update')
            AND left_policy.polroles IS DISTINCT FROM right_policy.polroles
        )) AS runtime_policy_subset_is_consistent
FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class c ON c.oid=policy.polrelid
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='appsurface_durable' AND policy.polname IN
  ('runtime_heartbeat_runtime_role','flow_dispatch_runtime_scope_select',
   'schedule_dispatch_runtime_scope_select','schedule_dispatch_scope_update')
\gset
SELECT :'runtime_policy_bootstrap'::boolean OR :'runtime_policy_subset_is_consistent'::boolean
       AS runtime_policy_targets_are_enrollable \gset
\if :runtime_policy_targets_are_enrollable
\else
  \echo 'Runtime policy targets are inconsistent, include an unmanifested role, or are not the schema-10 PUBLIC bootstrap baseline; repair catalog state before retrying.'
  SELECT 1 / 0;
\endif

SELECT (count(*) = 3 AND bool_and(policy.polroles = ARRAY[0::oid])
        AND NOT EXISTS (
          SELECT 1 FROM (
            SELECT a.grantee FROM pg_catalog.pg_class x JOIN pg_catalog.pg_namespace xn ON xn.oid=x.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(x.relacl) a WHERE xn.nspname='appsurface_durable'
            UNION ALL SELECT a.grantee FROM pg_catalog.pg_attribute at JOIN pg_catalog.pg_class x ON x.oid=at.attrelid
              JOIN pg_catalog.pg_namespace xn ON xn.oid=x.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(at.attacl) a WHERE xn.nspname='appsurface_durable'
            UNION ALL SELECT a.grantee FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace xn ON xn.oid=f.pronamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a WHERE xn.nspname='appsurface_durable'
          ) traces WHERE traces.grantee IN
            (SELECT dispatcher_oid FROM role_pair UNION SELECT runtime_oid FROM role_pair)
        )) AS dispatcher_policy_bootstrap
FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class c ON c.oid=policy.polrelid
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='appsurface_durable' AND policy.polname IN
  ('flow_dispatch_global_discovery','schedule_dispatch_global_discovery','schedule_dispatch_global_lease')
\gset
SELECT NOT EXISTS (
  SELECT 1 FROM pg_catalog.pg_policy p
  JOIN pg_catalog.pg_class c ON c.oid=p.polrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname='appsurface_durable'
    AND p.polname IN ('flow_dispatch_global_discovery','schedule_dispatch_global_discovery','schedule_dispatch_global_lease')
    AND NOT (
      p.polroles <@ (ARRAY(SELECT dispatcher_oid FROM role_pair WHERE dispatcher_profile='full' ORDER BY dispatcher_oid)
                     || ARRAY[(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=:'migration_owner_role')])
      AND cardinality(p.polroles) > 0
      OR p.polroles = ARRAY[0::oid] AND :'dispatcher_policy_bootstrap'::boolean
    )
) AS dispatcher_policy_targets_are_enrollable \gset
\if :dispatcher_policy_targets_are_enrollable
\else
  \echo 'Global dispatcher policy contains a role outside the full-profile manifest; resolve omitted or hostile policy targets before retrying.'
  SELECT 1 / 0;
\endif
SELECT NOT EXISTS (
  SELECT 1 FROM (
    SELECT p.polname, p.polroles FROM pg_catalog.pg_policy p
    JOIN pg_catalog.pg_class c ON c.oid=p.polrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='appsurface_durable' AND p.polname IN
      ('flow_dispatch_global_discovery','schedule_dispatch_global_discovery','schedule_dispatch_global_lease')
  ) existing
  WHERE EXISTS (
    SELECT 1 FROM pg_catalog.pg_policy other
    JOIN pg_catalog.pg_class oc ON oc.oid=other.polrelid JOIN pg_catalog.pg_namespace onsp ON onsp.oid=oc.relnamespace
    WHERE onsp.nspname='appsurface_durable' AND other.polname IN
      ('flow_dispatch_global_discovery','schedule_dispatch_global_discovery','schedule_dispatch_global_lease')
      AND other.polroles IS DISTINCT FROM existing.polroles
  )
) AS dispatcher_policy_siblings_consistent \gset
\if :dispatcher_policy_siblings_consistent
\else
  \echo 'Global dispatcher policies disagree on their existing authorized role set; repair catalog state before enrollment.'
  SELECT 1 / 0;
\endif
SELECT string_agg(pg_catalog.format('%I', dispatcher_name), ', ' ORDER BY dispatcher_oid) AS dispatcher_roles_sql,
       string_agg(pg_catalog.format('%I', runtime_name), ', ' ORDER BY runtime_oid) AS runtime_roles_sql,
       string_agg(pg_catalog.format('%I', dispatcher_name), ', ' ORDER BY dispatcher_oid)
         FILTER (WHERE dispatcher_profile = 'full') AS full_dispatcher_roles_sql
FROM role_pair \gset

-- Ownership carries effective authority even when no ACL entry records it.
-- Reject service-role ownership before any owner transfer can erase the trace.
WITH owned_objects(role_oid, object_name) AS (
  SELECT namespace.nspowner, 'schema ' || namespace.nspname
  FROM pg_catalog.pg_namespace namespace
  WHERE namespace.nspname = 'appsurface_durable'
  UNION ALL
  SELECT object.relowner, 'relation appsurface_durable.' || object.relname
  FROM pg_catalog.pg_class object
  JOIN pg_catalog.pg_namespace namespace ON namespace.oid = object.relnamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
  UNION ALL
  SELECT routine.proowner,
         'function appsurface_durable.' || routine.proname || '(' ||
           pg_catalog.pg_get_function_identity_arguments(routine.oid) || ')'
  FROM pg_catalog.pg_proc routine
  JOIN pg_catalog.pg_namespace namespace ON namespace.oid = routine.pronamespace
  WHERE namespace.nspname = 'appsurface_durable'
), conflicts AS (
  SELECT role.rolname, owned_objects.object_name
  FROM owned_objects
  JOIN role_pair pair
    ON owned_objects.role_oid IN (pair.dispatcher_oid, pair.runtime_oid)
  JOIN pg_catalog.pg_roles role ON role.oid = owned_objects.role_oid
)
SELECT EXISTS (SELECT 1 FROM conflicts) AS service_role_ownership_conflict,
       COALESCE((SELECT left(regexp_replace(rolname, '[[:cntrl:]]', '?', 'g'), 63)
                 FROM conflicts ORDER BY rolname, object_name LIMIT 1), '') AS owner_conflict_role,
       COALESCE((SELECT left(regexp_replace(object_name, '[[:cntrl:]]', '?', 'g'), 256)
                 FROM conflicts ORDER BY rolname, object_name LIMIT 1), '') AS owner_conflict_object
\gset
\if :service_role_ownership_conflict
  \echo 'Rejected Durable ownership by manifest service role:' :owner_conflict_role 'owns' :owner_conflict_object
  \echo 'Cause: dispatcher/runtime ownership grants effective authority outside the reviewed ACL manifest and would be hidden by owner transfer.'
  \echo 'Fix: transfer this object to the migration owner through the reviewed repair procedure, then rerun the unchanged complete manifest.'
  \echo 'Guide: Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md (Role recipe and role-pair manifest).'
  SELECT 1 / 0;
\endif

SELECT format('ALTER SCHEMA appsurface_durable OWNER TO %I', :'migration_owner_role') \gexec
SELECT format(
    'ALTER %s %I.%I OWNER TO %I',
    CASE object.relkind
      WHEN 'r' THEN 'TABLE'
      WHEN 'p' THEN 'TABLE'
      WHEN 'S' THEN 'SEQUENCE'
      WHEN 'v' THEN 'VIEW'
      WHEN 'm' THEN 'MATERIALIZED VIEW'
      WHEN 'f' THEN 'FOREIGN TABLE'
    END,
    namespace.nspname,
    object.relname,
    :'migration_owner_role')
FROM pg_catalog.pg_class AS object
JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
WHERE namespace.nspname = 'appsurface_durable'
  AND object.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
  AND
  (
    object.relkind <> 'S'
    OR NOT EXISTS
    (
      SELECT 1
      FROM pg_catalog.pg_depend AS dependency
      WHERE dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
        AND dependency.objid = object.oid
        AND dependency.deptype IN ('a', 'i')
    )
  )
ORDER BY CASE WHEN object.relkind = 'S' THEN 2 ELSE 1 END, object.relname \gexec

SELECT format(
    'ALTER FUNCTION %I.%I(%s) OWNER TO %I',
    namespace.nspname,
    routine.proname,
    pg_catalog.pg_get_function_identity_arguments(routine.oid),
    :'migration_owner_role')
FROM pg_catalog.pg_proc AS routine
JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
WHERE namespace.nspname = 'appsurface_durable'
  AND routine.prokind = 'f' \gexec

SELECT format('ALTER POLICY flow_dispatch_global_discovery ON appsurface_durable.flow_dispatch TO %s',
              targets.role_list)
FROM (SELECT concat_ws(', ',
             string_agg(pg_catalog.format('%I', dispatcher_name), ', ' ORDER BY dispatcher_oid),
             pg_catalog.format('%I', :'migration_owner_role')) AS role_list
      FROM role_pair WHERE dispatcher_profile = 'full') targets \gexec
SELECT format(
    'ALTER POLICY work_contract_discovery_owner ON appsurface_durable.work TO %I',
    :'migration_owner_role') \gexec
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_policy WHERE polname = 'runtime_heartbeat_runtime_role')
  THEN format('ALTER POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat TO %s', targets.role_list)
  ELSE format('CREATE POLICY runtime_heartbeat_runtime_role ON appsurface_durable.runtime_heartbeat FOR ALL TO %s USING (true) WITH CHECK (true)', targets.role_list) END
FROM (SELECT string_agg(pg_catalog.format('%I', runtime_name), ', ' ORDER BY runtime_oid) AS role_list FROM role_pair) targets \gexec
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_policy WHERE polname = 'runtime_heartbeat_migration_owner')
  THEN format('ALTER POLICY runtime_heartbeat_migration_owner ON appsurface_durable.runtime_heartbeat TO %I', :'migration_owner_role')
  ELSE format('CREATE POLICY runtime_heartbeat_migration_owner ON appsurface_durable.runtime_heartbeat FOR ALL TO %I USING (true) WITH CHECK (true)', :'migration_owner_role') END \gexec
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_policy WHERE polname = 'flow_dispatch_runtime_scope_select')
  THEN format('ALTER POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch TO %s', targets.role_list)
  ELSE format('CREATE POLICY flow_dispatch_runtime_scope_select ON appsurface_durable.flow_dispatch FOR SELECT TO %s USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))', targets.role_list) END
FROM (SELECT string_agg(pg_catalog.format('%I', runtime_name), ', ' ORDER BY runtime_oid) AS role_list FROM role_pair) targets \gexec
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_policy WHERE polname = 'schedule_dispatch_runtime_scope_select')
  THEN format('ALTER POLICY schedule_dispatch_runtime_scope_select ON appsurface_durable.schedule_dispatch TO %s', targets.role_list)
  ELSE format('CREATE POLICY schedule_dispatch_runtime_scope_select ON appsurface_durable.schedule_dispatch FOR SELECT TO %s USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))', targets.role_list) END
FROM (SELECT string_agg(pg_catalog.format('%I', runtime_name), ', ' ORDER BY runtime_oid) AS role_list FROM role_pair) targets \gexec
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_policy WHERE polname = 'schedule_dispatch_scope_update')
  THEN format('ALTER POLICY schedule_dispatch_scope_update ON appsurface_durable.schedule_dispatch TO %s', targets.role_list)
  ELSE format('CREATE POLICY schedule_dispatch_scope_update ON appsurface_durable.schedule_dispatch FOR UPDATE TO %s USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), '''')) WITH CHECK (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))', targets.role_list) END
FROM (SELECT string_agg(pg_catalog.format('%I', runtime_name), ', ' ORDER BY runtime_oid) AS role_list FROM role_pair) targets \gexec
SELECT format('ALTER POLICY schedule_dispatch_global_discovery ON appsurface_durable.schedule_dispatch TO %s', targets.role_list)
FROM (SELECT concat_ws(', ',
             string_agg(pg_catalog.format('%I', dispatcher_name), ', ' ORDER BY dispatcher_oid),
             pg_catalog.format('%I', :'migration_owner_role')) AS role_list
      FROM role_pair WHERE dispatcher_profile = 'full') targets \gexec
SELECT format('ALTER POLICY schedule_dispatch_global_lease ON appsurface_durable.schedule_dispatch TO %s', targets.role_list)
FROM (SELECT concat_ws(', ',
             string_agg(pg_catalog.format('%I', dispatcher_name), ', ' ORDER BY dispatcher_oid),
             pg_catalog.format('%I', :'migration_owner_role')) AS role_list
      FROM role_pair WHERE dispatcher_profile = 'full') targets \gexec
SELECT CASE WHEN EXISTS (
         SELECT 1 FROM pg_catalog.pg_policy p
         JOIN pg_catalog.pg_class c ON c.oid=p.polrelid
         JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
         WHERE n.nspname='appsurface_durable' AND c.relname='flow_dispatch'
           AND p.polname='flow_dispatch_retention_scope_select'
       )
       THEN format('ALTER POLICY flow_dispatch_retention_scope_select ON appsurface_durable.flow_dispatch TO %I', :'retention_operator_role')
       ELSE format('CREATE POLICY flow_dispatch_retention_scope_select ON appsurface_durable.flow_dispatch FOR SELECT TO %I USING (scope_id = nullif(current_setting(''appsurface_durable.scope_id'', true), ''''))', :'retention_operator_role')
       END \gexec
SELECT format('REVOKE ALL ON TABLE appsurface_durable.schedule_dispatch FROM %s', :'dispatcher_roles_sql') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) FROM %s', :'dispatcher_roles_sql') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) FROM %s', :'runtime_roles_sql') \gexec
REVOKE ALL ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) FROM PUBLIC;
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) FROM %s', :'dispatcher_roles_sql') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) FROM %s', :'runtime_roles_sql') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) FROM %I', :'retention_operator_role') \gexec
REVOKE ALL ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid) FROM PUBLIC;
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid) FROM %s', :'dispatcher_roles_sql') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid) FROM %s', :'runtime_roles_sql') \gexec
SELECT format('REVOKE ALL ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid) FROM %I', :'retention_operator_role') \gexec
SELECT format('REVOKE ALL ON TABLE appsurface_durable.dispatch FROM %s', :'dispatcher_roles_sql') \gexec

-- Remove PostgreSQL's default PUBLIC capabilities throughout the reserved
-- package schema before applying the explicit role allowlists.
REVOKE ALL ON SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA appsurface_durable FROM PUBLIC;

SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = object.relowner
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
    AND owner_role.rolname <> :'migration_owner_role'
) AS durable_objects_owned_by_migration_role \gset
\if :durable_objects_owned_by_migration_role
\else
  \echo 'Every durable table, sequence, view, and foreign table must be owned by the migration owner.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS
(
  SELECT 1
  FROM pg_catalog.pg_proc AS routine
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
  JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = routine.proowner
  WHERE namespace.nspname = 'appsurface_durable'
    AND routine.prokind = 'f'
    AND owner_role.rolname <> :'migration_owner_role'
) AS durable_functions_owned_by_migration_role \gset
\if :durable_functions_owned_by_migration_role
\else
  \echo 'Every durable function must be owned by the migration owner.'
  SELECT 1 / 0;
\endif

SELECT bool_and(
    object.relrowsecurity =
      (object.relname IN
        ('scope', 'scope_history', 'work', 'work_history', 'dispatch', 'work_operator_command', 'effect_permit', 'runtime_heartbeat',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item',
         'flow_retention_manifest_summary', 'flow_retention_manifest_event', 'flow_retention_command')
        OR object.relname LIKE 'schedule_history_%')
    AND object.relforcerowsecurity =
      (object.relname IN
        ('scope', 'scope_history', 'work', 'work_history', 'dispatch', 'work_operator_command', 'effect_permit', 'runtime_heartbeat',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item',
         'flow_retention_manifest_summary', 'flow_retention_manifest_event', 'flow_retention_command')
        OR object.relname LIKE 'schedule_history_%'))
  AS durable_rls_flags_are_exact
FROM pg_catalog.pg_class AS object
JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
WHERE namespace.nspname = 'appsurface_durable'
  AND object.relkind IN ('r', 'p') \gset
\if :durable_rls_flags_are_exact
\else
  \echo 'Durable row-level security flags must exactly match the package migration.'
  SELECT 1 / 0;
\endif

WITH schedule_history_child AS
(
  SELECT child.oid, child.relrowsecurity, child.relforcerowsecurity
  FROM pg_catalog.pg_inherits AS inheritance
  JOIN pg_catalog.pg_class AS parent ON parent.oid = inheritance.inhparent
  JOIN pg_catalog.pg_namespace AS parent_namespace ON parent_namespace.oid = parent.relnamespace
  JOIN pg_catalog.pg_class AS child ON child.oid = inheritance.inhrelid
  JOIN pg_catalog.pg_namespace AS child_namespace ON child_namespace.oid = child.relnamespace
  WHERE parent_namespace.nspname = 'appsurface_durable'
    AND parent.relname = 'schedule_history'
    AND child_namespace.nspname = 'appsurface_durable'
),
actual_child_policy AS
(
  SELECT child.relrowsecurity,
         child.relforcerowsecurity,
         policy.polname AS policy_name,
         policy.polcmd::text AS command_name,
         policy.polpermissive,
         policy.polroles,
         pg_catalog.pg_get_expr(policy.polqual, policy.polrelid) AS using_expression,
         pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid) AS check_expression
  FROM schedule_history_child AS child
  LEFT JOIN pg_catalog.pg_policy AS policy ON policy.polrelid = child.oid
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM actual_child_policy
  WHERE NOT relrowsecurity
    OR NOT relforcerowsecurity
    OR policy_name IS DISTINCT FROM 'schedule_history_scope_isolation'
    OR command_name IS DISTINCT FROM '*'
    OR NOT polpermissive
    OR polroles IS DISTINCT FROM ARRAY[0]::oid[]
    OR using_expression IS DISTINCT FROM
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'
    OR check_expression IS DISTINCT FROM
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'
) AS schedule_history_child_policies_are_exact \gset
\if :schedule_history_child_policies_are_exact
\else
  \echo 'Every schedule_history partition must have forced RLS and the exact scope-isolation policy.'
  SELECT 1 / 0;
\endif

WITH expected_policy(relation_name, policy_name, command_name, using_expression, check_expression) AS
(
  VALUES
    ('dispatch', 'dispatch_global_discovery', 'r', 'true', NULL::text),
    ('dispatch', 'dispatch_scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('dispatch', 'dispatch_scope_update', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_command', 'flow_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_dispatch', 'flow_dispatch_global_discovery', 'r', 'true', NULL::text),
    ('flow_dispatch', 'flow_dispatch_runtime_scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('flow_dispatch', 'flow_dispatch_retention_scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('flow_dispatch', 'flow_dispatch_scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_dispatch', 'flow_dispatch_scope_update', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_history', 'flow_history_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_repair_collision', 'flow_repair_collision_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_repair_command', 'flow_repair_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_trace_context', 'flow_trace_context_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_command', 'flow_retention_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest', 'flow_retention_manifest_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest_event', 'flow_retention_manifest_event_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest_item', 'flow_retention_manifest_item_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_retention_manifest_summary', 'flow_retention_manifest_summary_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_instance', 'flow_instance_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('runtime_heartbeat', 'runtime_heartbeat_migration_owner', '*', 'true', 'true'),
    ('runtime_heartbeat', 'runtime_heartbeat_runtime_role', '*', 'true', 'true'),
    ('flow_timer', 'flow_timer_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('flow_wait', 'flow_wait_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_command', 'schedule_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_definition', 'schedule_definition_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_dispatch', 'schedule_dispatch_global_discovery', 'r', 'true', NULL::text),
    ('schedule_dispatch', 'schedule_dispatch_global_lease', 'w', 'true', 'true'),
    ('schedule_dispatch', 'schedule_dispatch_runtime_scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('schedule_dispatch', 'schedule_dispatch_scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_dispatch', 'schedule_dispatch_scope_update', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_generation', 'schedule_generation_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_history', 'schedule_history_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('schedule_occurrence', 'schedule_occurrence_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('effect_permit', 'effect_permit_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('scope', 'scope_disable', 'w',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '((scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text)) AND (state = ''disabled''::text))'),
    ('scope', 'scope_insert', 'a', NULL::text,
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('scope', 'scope_select', 'r',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))', NULL::text),
    ('scope_history', 'scope_history_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('work', 'work_contract_discovery_owner', 'r', 'true', NULL::text),
    ('work', 'work_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('work_history', 'work_history_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))'),
    ('work_operator_command', 'work_operator_command_scope_isolation', '*',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))',
      '(scope_id = NULLIF(current_setting(''appsurface_durable.scope_id''::text, true), ''''::text))')
),
actual_policy AS
(
  SELECT
    object.relname AS relation_name,
    policy.polname AS policy_name,
    policy.polcmd::text AS command_name,
    policy.polpermissive,
    policy.polroles,
    pg_catalog.pg_get_expr(policy.polqual, policy.polrelid) AS using_expression,
    pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid) AS check_expression
  FROM pg_catalog.pg_policy AS policy
  JOIN pg_catalog.pg_class AS object ON object.oid = policy.polrelid
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relname NOT LIKE 'schedule_history_%'
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM expected_policy AS expected
  FULL OUTER JOIN actual_policy AS actual
    ON actual.relation_name = expected.relation_name
    AND actual.policy_name = expected.policy_name
  WHERE expected.policy_name IS NULL
    OR actual.policy_name IS NULL
    OR NOT actual.polpermissive
    OR NOT
    (
        CASE
            WHEN actual.policy_name = 'work_contract_discovery_owner' THEN actual.polroles = ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'migration_owner_role')]
            WHEN actual.policy_name = 'flow_dispatch_global_discovery' THEN
                actual.polroles = ARRAY(SELECT dispatcher_oid FROM role_pair WHERE dispatcher_profile = 'full' ORDER BY dispatcher_oid)
                    || ARRAY[(SELECT oid FROM pg_catalog.pg_roles WHERE rolname = :'migration_owner_role')]
            WHEN actual.policy_name IN ('schedule_dispatch_global_discovery', 'schedule_dispatch_global_lease') THEN
                actual.polroles = ARRAY(SELECT dispatcher_oid FROM role_pair WHERE dispatcher_profile = 'full' ORDER BY dispatcher_oid)
                    || ARRAY[(SELECT oid FROM pg_catalog.pg_roles WHERE rolname = :'migration_owner_role')]
            WHEN actual.policy_name IN ('flow_dispatch_runtime_scope_select', 'schedule_dispatch_runtime_scope_select', 'schedule_dispatch_scope_update') THEN
                actual.polroles = ARRAY(SELECT runtime_oid FROM role_pair ORDER BY runtime_oid)
            WHEN actual.policy_name = 'flow_dispatch_retention_scope_select' THEN actual.polroles @> ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'retention_operator_role')]
                AND actual.polroles <@ ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'retention_operator_role')]
            WHEN actual.policy_name = 'runtime_heartbeat_runtime_role' THEN
                actual.polroles = ARRAY(SELECT runtime_oid FROM role_pair ORDER BY runtime_oid)
            WHEN actual.policy_name = 'runtime_heartbeat_migration_owner' THEN actual.polroles = ARRAY[
                (SELECT role_value.oid FROM pg_catalog.pg_roles AS role_value WHERE role_value.rolname = :'migration_owner_role')]
            ELSE actual.polroles = ARRAY[0]::oid[]
        END
    )
    OR actual.command_name <> expected.command_name
    OR actual.using_expression IS DISTINCT FROM expected.using_expression
    OR actual.check_expression IS DISTINCT FROM expected.check_expression
) AS durable_rls_policies_are_exact \gset
\if :durable_rls_policies_are_exact
\else
  \echo 'Durable row-level security policies must exactly match the package migration.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS (
  SELECT 1 FROM role_pair p
  WHERE pg_catalog.has_schema_privilege(p.dispatcher_oid, 'appsurface_durable', 'CREATE')
     OR pg_catalog.has_schema_privilege(p.runtime_oid, 'appsurface_durable', 'CREATE')
     OR pg_catalog.has_schema_privilege(p.dispatcher_oid, 'appsurface_durable', 'USAGE WITH GRANT OPTION')
     OR pg_catalog.has_schema_privilege(p.runtime_oid, 'appsurface_durable', 'USAGE WITH GRANT OPTION')
)
AND NOT
(
  pg_catalog.has_schema_privilege(:'retention_operator_role', 'appsurface_durable', 'CREATE')
  OR pg_catalog.has_schema_privilege(:'retention_operator_role', 'appsurface_durable', 'USAGE WITH GRANT OPTION')
) AS service_roles_have_safe_schema_privileges \gset
\if :service_roles_have_safe_schema_privileges
\else
  \echo 'Dispatcher, scoped runtime, and retention operator roles must not have schema CREATE or grant options.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name, role_type, dispatcher_profile) AS
(
  SELECT dispatcher_name, 'dispatcher', dispatcher_profile FROM role_pair
  UNION ALL SELECT runtime_name, 'runtime', NULL FROM role_pair
  UNION ALL SELECT :'retention_operator_role', 'retention', NULL
),
durable_relation AS
(
  SELECT object.oid, object.relname
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind IN ('r', 'p', 'v', 'm', 'f')
),
relation_privilege(privilege_name) AS
(
  SELECT privilege_name
  FROM
  (
    VALUES
      ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER'),
      ('SELECT WITH GRANT OPTION'), ('INSERT WITH GRANT OPTION'), ('UPDATE WITH GRANT OPTION'),
      ('DELETE WITH GRANT OPTION'), ('TRUNCATE WITH GRANT OPTION'), ('REFERENCES WITH GRANT OPTION'),
      ('TRIGGER WITH GRANT OPTION'),
      ('MAINTAIN'), ('MAINTAIN WITH GRANT OPTION')
  ) AS candidate(privilege_name)
  WHERE pg_catalog.current_setting('server_version_num')::integer >= 170000
     OR candidate.privilege_name NOT LIKE 'MAINTAIN%'
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_relation AS relation
  CROSS JOIN relation_privilege AS privilege
  WHERE pg_catalog.has_table_privilege(
      service.role_name::pg_catalog.name,
      relation.oid,
      privilege.privilege_name)
   AND NOT
   (
     service.role_type = 'dispatcher' AND service.dispatcher_profile = 'full'
     AND relation.relname = 'flow_dispatch'
     AND privilege.privilege_name = 'SELECT'
     OR service.role_type = 'runtime'
     AND
     (
       privilege.privilege_name = 'SELECT'
       AND relation.relname IN
       (
         'store_metadata', 'schema_migration', 'runtime_heartbeat', 'scope', 'work', 'dispatch',
         'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
       OR privilege.privilege_name = 'INSERT'
       AND relation.relname IN
       (
         'runtime_heartbeat', 'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
         'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
     )
     OR service.role_name = :'retention_operator_role'
     AND privilege.privilege_name = 'SELECT'
     AND relation.relname IN
     (
       'scope', 'work', 'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch',
       'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item', 'flow_retention_manifest_summary',
       'flow_retention_manifest_event', 'flow_retention_command'
     )
   )
) AS service_roles_have_safe_relation_privileges \gset
\if :service_roles_have_safe_relation_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-table privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name, role_type, dispatcher_profile) AS
(
  SELECT dispatcher_name, 'dispatcher', dispatcher_profile FROM role_pair
  UNION ALL SELECT runtime_name, 'runtime', NULL FROM role_pair
  UNION ALL SELECT :'retention_operator_role', 'retention', NULL
),
durable_column AS
(
  SELECT object.oid, object.relname, attribute.attnum, attribute.attname
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = object.oid
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind IN ('r', 'p', 'v', 'm', 'f')
    AND attribute.attnum > 0
    AND NOT attribute.attisdropped
),
column_privilege(privilege_name) AS
(
  VALUES
    ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES'),
    ('SELECT WITH GRANT OPTION'), ('INSERT WITH GRANT OPTION'),
    ('UPDATE WITH GRANT OPTION'), ('REFERENCES WITH GRANT OPTION')
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_column AS column_value
  CROSS JOIN column_privilege AS privilege
  WHERE pg_catalog.has_column_privilege(
      service.role_name::pg_catalog.name,
      column_value.oid,
      column_value.attnum,
      privilege.privilege_name)
   AND NOT
   (
     service.role_type = 'dispatcher' AND service.dispatcher_profile = 'full'
     AND column_value.relname = 'flow_dispatch'
     AND privilege.privilege_name = 'SELECT'
     OR service.role_type = 'runtime'
     AND
     (
       privilege.privilege_name = 'SELECT'
       AND column_value.relname IN
       (
         'store_metadata', 'schema_migration', 'runtime_heartbeat', 'scope', 'work', 'dispatch',
         'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
       OR privilege.privilege_name = 'INSERT'
       AND column_value.relname IN
       (
         'runtime_heartbeat', 'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
         'scope_history', 'work_history',
         'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'flow_repair_command', 'flow_repair_collision',
         'schedule_definition', 'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
         'schedule_history', 'flow_trace_context'
       )
       OR privilege.privilege_name = 'UPDATE'
       AND
       (
         column_value.relname = 'scope'
         AND column_value.attname IN ('generation', 'state', 'updated_at')
         OR column_value.relname = 'runtime_heartbeat'
         AND column_value.attname IN
         (
           'worker_instance_id', 'runtime_epoch', 'hosted_surfaces', 'started_at', 'last_heartbeat_at', 'last_successful_sweep_at', 'draining', 'pass_active',
           'pass_started_at', 'last_discovered', 'last_claimed', 'last_processed', 'last_deferred',
           'last_failed', 'last_pass_elapsed_ms', 'updated_at'
         )
         OR column_value.relname = 'work'
         AND column_value.attname IN
         (
           'state', 'due_at', 'updated_at', 'terminal_at', 'cancellation_requested_at', 'attempt_number',
           'lease_generation', 'lease_owner', 'lease_started_at', 'lease_expires_at', 'runtime_epoch', 'revision',
           'result_contract_id', 'result_schema_version', 'result_codec_id', 'result_classification',
           'result_retention_policy_id', 'result_payload', 'result_sha256', 'terminal_code', 'trace_context_id'
         )
         OR column_value.relname = 'dispatch'
         AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
         OR column_value.relname = 'work_operator_command'
         AND column_value.attname IN ('status', 'resulting_state', 'resulting_revision', 'resolution_kind', 'completed_at')
         OR column_value.relname = 'effect_permit'
         AND column_value.attname IN ('status', 'observed_at', 'details', 'runtime_epoch')
         OR column_value.relname = 'flow_instance'
         AND column_value.attname IN
         (
           'state', 'current_node_id', 'context_contract_id', 'context_schema_version',
           'context_codec_id', 'context_payload', 'context_sha256', 'context_classification',
           'context_retention', 'resume_event_name', 'resume_event_is_timeout',
           'resume_event_contract_id', 'resume_event_schema_version', 'resume_event_codec_id',
           'resume_event_payload', 'resume_event_sha256', 'resume_event_classification',
           'resume_event_retention', 'activity_callsite_id', 'activity_result_contract_id',
           'activity_result_schema_version', 'activity_result_codec_id', 'activity_result_payload',
           'activity_result_sha256', 'activity_result_classification', 'activity_result_retention',
           'lease_generation', 'lease_owner', 'lease_started_at', 'lease_expires_at',
           'updated_at', 'cancellation_requested_at', 'terminal_at', 'terminal_code', 'trace_context_id',
           'suspension_descriptor', 'suspended_from_state', 'suspension_descriptor_schema',
           'suspension_descriptor_sha256', 'revision', 'scope_generation', 'runtime_epoch'
         )
         OR column_value.relname IN ('flow_command', 'flow_history')
         AND column_value.attname = 'trace_context_id'
         OR column_value.relname = 'flow_wait'
         AND column_value.attname IN ('state', 'resolved_revision', 'resolved_at', 'suspension_descriptor', 'updated_at', 'trace_context_id')
         OR column_value.relname = 'flow_timer'
         AND column_value.attname IN ('state', 'resolved_at', 'updated_at', 'trace_context_id')
         OR column_value.relname = 'flow_dispatch'
         AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
         OR column_value.relname = 'schedule_definition'
         AND column_value.attname IN
         (
           'display_name', 'state', 'active_generation', 'revision', 'accepted_at_utc', 'cursor_utc', 'next_due_utc',
           'scope_generation', 'runtime_epoch', 'suspension_code', 'updated_at'
         )
         OR column_value.relname = 'schedule_occurrence'
         AND column_value.attname IN
         (
           'last_nominal_utc', 'state', 'target_kind', 'target_id', 'target_command_id', 'target_idempotency_key',
           'claimed_by', 'lease_expires_at', 'updated_at'
         )
         OR column_value.relname = 'schedule_dispatch'
         AND column_value.attname IN
         (
           'dispatch_revision', 'due_at', 'state', 'lease_owner', 'lease_generation', 'lease_expires_at', 'updated_at'
         )
       )
     )
     OR service.role_name = :'retention_operator_role'
     AND privilege.privilege_name = 'SELECT'
     AND column_value.relname IN
     (
       'scope', 'work', 'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch',
       'flow_trace_context', 'flow_retention_manifest', 'flow_retention_manifest_item', 'flow_retention_manifest_summary',
       'flow_retention_manifest_event', 'flow_retention_command'
     )
   )
) AS service_roles_have_safe_column_privileges \gset
\if :service_roles_have_safe_column_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-column privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name, role_type, dispatcher_profile) AS
(
  SELECT dispatcher_name, 'dispatcher', dispatcher_profile FROM role_pair
  UNION ALL SELECT runtime_name, 'runtime', NULL FROM role_pair
  UNION ALL SELECT :'retention_operator_role', 'retention', NULL
),
durable_sequence AS
(
  SELECT object.oid
  FROM pg_catalog.pg_class AS object
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND object.relkind = 'S'
),
sequence_privilege(privilege_name) AS
(
  VALUES
    ('USAGE'), ('SELECT'), ('UPDATE'),
    ('USAGE WITH GRANT OPTION'), ('SELECT WITH GRANT OPTION'), ('UPDATE WITH GRANT OPTION')
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_sequence AS sequence_value
  CROSS JOIN sequence_privilege AS privilege
  WHERE pg_catalog.has_sequence_privilege(
      service.role_name::pg_catalog.name,
      sequence_value.oid,
      privilege.privilege_name)
    AND NOT
    (
      service.role_type = 'runtime'
      AND privilege.privilege_name IN ('USAGE', 'SELECT')
    )
) AS service_roles_have_safe_sequence_privileges \gset
\if :service_roles_have_safe_sequence_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-sequence privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

WITH service_role(role_name, role_type, dispatcher_profile) AS
(
  SELECT dispatcher_name, 'dispatcher', dispatcher_profile FROM role_pair
  UNION ALL SELECT runtime_name, 'runtime', NULL FROM role_pair
  UNION ALL SELECT :'retention_operator_role', 'retention', NULL
),
durable_function AS
(
  SELECT routine.oid, routine.proname
  FROM pg_catalog.pg_proc AS routine
  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
  WHERE namespace.nspname = 'appsurface_durable'
    AND routine.prokind = 'f'
),
function_privilege(privilege_name) AS
(
  VALUES ('EXECUTE'), ('EXECUTE WITH GRANT OPTION')
)
SELECT NOT EXISTS
(
  SELECT 1
  FROM service_role AS service
  CROSS JOIN durable_function AS routine
  CROSS JOIN function_privilege AS privilege
  WHERE pg_catalog.has_function_privilege(
      service.role_name::pg_catalog.name,
      routine.oid,
      privilege.privilege_name)
    AND NOT
    (
      service.role_type = 'dispatcher'
      AND (routine.oid = 'appsurface_durable.discover_work_dispatch(text[], text[], integer)'::pg_catalog.regprocedure
        OR service.dispatcher_profile = 'full'
           AND routine.oid = 'appsurface_durable.claim_schedule_dispatch(text, interval)'::pg_catalog.regprocedure)
      AND privilege.privilege_name = 'EXECUTE'
      OR service.role_type = 'runtime'
      AND routine.oid IN
      (
        'appsurface_durable.runtime_due_dispatch_health(integer)'::pg_catalog.regprocedure,
        'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)'::pg_catalog.regprocedure
      )
      AND privilege.privilege_name = 'EXECUTE'
      OR service.role_type = 'retention'
      AND routine.oid IN
      (
        'appsurface_durable.create_flow_retention_manifest(text, text, text, text, char(64), text, char(64), integer, bigint, jsonb, text, text, char(64))'::pg_catalog.regprocedure,
        'appsurface_durable.apply_flow_retention_lifecycle(text, text, text, text, text, char(64), text, text, bigint, text, text, char(64), text, char(64), integer, boolean)'::pg_catalog.regprocedure
      )
      AND privilege.privilege_name = 'EXECUTE'
    )
) AS service_roles_have_safe_function_privileges \gset
\if :service_roles_have_safe_function_privileges
\else
  \echo 'Dispatcher, scoped runtime, or retention operator role has an effective durable-function privilege outside the package allowlist.'
  SELECT 1 / 0;
\endif

SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %s', :'dispatcher_roles_sql') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.discover_work_dispatch(text[], text[], integer) TO %s', :'dispatcher_roles_sql') \gexec
\if :{?full_dispatcher_roles_sql}
SELECT format('GRANT SELECT ON appsurface_durable.flow_dispatch TO %s', :'full_dispatcher_roles_sql') WHERE :'full_dispatcher_roles_sql' <> '' \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.claim_schedule_dispatch(text, interval) TO %s', :'full_dispatcher_roles_sql') WHERE :'full_dispatcher_roles_sql' <> '' \gexec
\endif
SELECT format('REVOKE ALL ON SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('REVOKE ALL ON ALL TABLES IN SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA appsurface_durable FROM %I', :'retention_operator_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %I', :'retention_operator_role') \gexec
SELECT format(
    'GRANT SELECT ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.flow_trace_context, appsurface_durable.flow_retention_manifest, appsurface_durable.flow_retention_manifest_item, appsurface_durable.flow_retention_manifest_summary, appsurface_durable.flow_retention_manifest_event, appsurface_durable.flow_retention_command TO %I',
    :'retention_operator_role') \gexec
SELECT format(
    'GRANT EXECUTE ON FUNCTION appsurface_durable.create_flow_retention_manifest(text, text, text, text, char(64), text, char(64), integer, bigint, jsonb, text, text, char(64)) TO %I',
    :'retention_operator_role') \gexec
SELECT format(
    'GRANT EXECUTE ON FUNCTION appsurface_durable.apply_flow_retention_lifecycle(text, text, text, text, text, char(64), text, text, bigint, text, text, char(64), text, char(64), integer, boolean) TO %I',
    :'retention_operator_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA appsurface_durable TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.runtime_due_dispatch_health(integer) TO %s', :'runtime_roles_sql') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid) TO %s', :'runtime_roles_sql') \gexec
SELECT NOT EXISTS (
         SELECT 1 FROM role_pair p
         WHERE NOT has_function_privilege(p.runtime_oid, 'appsurface_durable.runtime_due_dispatch_health(integer)', 'EXECUTE')
            OR has_function_privilege(p.runtime_oid, 'appsurface_durable.runtime_due_dispatch_health(integer)', 'EXECUTE WITH GRANT OPTION')
            OR has_function_privilege(p.dispatcher_oid, 'appsurface_durable.runtime_due_dispatch_health(integer)', 'EXECUTE')
       )
       AND NOT has_function_privilege('public', 'appsurface_durable.runtime_due_dispatch_health(integer)', 'EXECUTE')
       AND NOT has_function_privilege(:'retention_operator_role', 'appsurface_durable.runtime_due_dispatch_health(integer)', 'EXECUTE')
       AND NOT EXISTS
       (
           SELECT 1
           FROM pg_catalog.pg_proc AS routine
           CROSS JOIN LATERAL pg_catalog.aclexplode(routine.proacl) AS privilege
           WHERE routine.oid =
               'appsurface_durable.runtime_due_dispatch_health(integer)'::pg_catalog.regprocedure
             AND privilege.privilege_type = 'EXECUTE'
             AND privilege.grantee <> routine.proowner
             AND privilege.grantee NOT IN (SELECT runtime_oid FROM role_pair)
       )
    AS runtime_due_dispatch_health_acl_is_exact \gset
\if :runtime_due_dispatch_health_acl_is_exact
\else
  \echo 'runtime_due_dispatch_health(integer) must be executable by every manifest runtime and no dispatcher, retention operator, PUBLIC, or other principal.'
  SELECT 1 / 0;
\endif
SELECT NOT EXISTS (
         SELECT 1 FROM role_pair p
         WHERE NOT has_function_privilege(p.runtime_oid, 'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)', 'EXECUTE')
            OR has_function_privilege(p.runtime_oid, 'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)', 'EXECUTE WITH GRANT OPTION')
            OR has_function_privilege(p.dispatcher_oid, 'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)', 'EXECUTE')
       )
       AND NOT has_function_privilege('public', 'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)', 'EXECUTE')
       AND NOT has_function_privilege(:'retention_operator_role', 'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)', 'EXECUTE')
       AND NOT EXISTS
       (
           SELECT 1
           FROM pg_catalog.pg_proc AS routine
           CROSS JOIN LATERAL pg_catalog.aclexplode(routine.proacl) AS privilege
           WHERE routine.oid =
               'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)'::pg_catalog.regprocedure
             AND privilege.privilege_type = 'EXECUTE'
             AND privilege.grantee <> routine.proowner
             AND privilege.grantee NOT IN (SELECT runtime_oid FROM role_pair)
       )
    AS runtime_heartbeat_prune_acl_is_exact \gset
\if :runtime_heartbeat_prune_acl_is_exact
\else
  \echo 'prune_runtime_heartbeats(interval, integer, text, uuid) must be executable by every manifest runtime and no dispatcher, retention operator, PUBLIC, or other principal.'
  SELECT 1 / 0;
\endif
SELECT format(
    'GRANT SELECT ON appsurface_durable.store_metadata, appsurface_durable.schema_migration, appsurface_durable.runtime_heartbeat TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.flow_repair_command, appsurface_durable.flow_repair_collision, appsurface_durable.flow_trace_context, appsurface_durable.schedule_definition, appsurface_durable.schedule_generation, appsurface_durable.schedule_command, appsurface_durable.schedule_occurrence, appsurface_durable.schedule_dispatch TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'REVOKE UPDATE ON appsurface_durable.scope, appsurface_durable.work, appsurface_durable.dispatch, appsurface_durable.flow_instance, appsurface_durable.flow_command, appsurface_durable.flow_history, appsurface_durable.flow_wait, appsurface_durable.flow_timer, appsurface_durable.flow_dispatch, appsurface_durable.schedule_definition, appsurface_durable.schedule_generation, appsurface_durable.schedule_command, appsurface_durable.schedule_occurrence, appsurface_durable.schedule_dispatch FROM %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (generation, state, updated_at) ON appsurface_durable.scope TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (state, due_at, updated_at, terminal_at, cancellation_requested_at, attempt_number, lease_generation, lease_owner, lease_started_at, lease_expires_at, runtime_epoch, revision, result_contract_id, result_schema_version, result_codec_id, result_classification, result_retention_policy_id, result_payload, result_sha256, terminal_code, trace_context_id) ON appsurface_durable.work TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.dispatch TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (state, current_node_id, context_contract_id, context_schema_version, context_codec_id, context_payload, context_sha256, context_classification, context_retention, resume_event_name, resume_event_is_timeout, resume_event_contract_id, resume_event_schema_version, resume_event_codec_id, resume_event_payload, resume_event_sha256, resume_event_classification, resume_event_retention, activity_callsite_id, activity_result_contract_id, activity_result_schema_version, activity_result_codec_id, activity_result_payload, activity_result_sha256, activity_result_classification, activity_result_retention, lease_generation, lease_owner, lease_started_at, lease_expires_at, updated_at, cancellation_requested_at, terminal_at, terminal_code, suspension_descriptor, suspended_from_state, suspension_descriptor_schema, suspension_descriptor_sha256, revision, scope_generation, runtime_epoch, trace_context_id) ON appsurface_durable.flow_instance TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (trace_context_id) ON appsurface_durable.flow_command, appsurface_durable.flow_history TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (state, resolved_revision, resolved_at, suspension_descriptor, updated_at, trace_context_id) ON appsurface_durable.flow_wait TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (state, resolved_at, updated_at, trace_context_id) ON appsurface_durable.flow_timer TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (due_at, state, expected_revision, updated_at) ON appsurface_durable.flow_dispatch TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (display_name, state, active_generation, revision, accepted_at_utc, cursor_utc, next_due_utc, scope_generation, runtime_epoch, suspension_code, updated_at) ON appsurface_durable.schedule_definition TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (last_nominal_utc, state, target_kind, target_id, target_command_id, target_idempotency_key, claimed_by, lease_expires_at, updated_at) ON appsurface_durable.schedule_occurrence TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (dispatch_revision, due_at, state, lease_owner, lease_generation, lease_expires_at, updated_at) ON appsurface_durable.schedule_dispatch TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT INSERT, UPDATE (worker_instance_id, runtime_epoch, hosted_surfaces, started_at, last_heartbeat_at, last_successful_sweep_at, draining, pass_active, pass_started_at, last_discovered, last_claimed, last_processed, last_deferred, last_failed, last_pass_elapsed_ms, updated_at) ON appsurface_durable.runtime_heartbeat TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.work_operator_command, appsurface_durable.effect_permit TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (status, resulting_state, resulting_revision, resolution_kind, completed_at) ON appsurface_durable.work_operator_command TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT UPDATE (status, observed_at, details, runtime_epoch) ON appsurface_durable.effect_permit TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.scope_history, appsurface_durable.work_history TO %s',
    :'runtime_roles_sql') \gexec
SELECT format(
    'GRANT SELECT, INSERT ON appsurface_durable.schedule_history TO %s',
    :'runtime_roles_sql') \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA appsurface_durable TO %s', :'runtime_roles_sql') \gexec

-- Final package-wide PUBLIC proof includes schema, relation, column, sequence,
-- and function ACLs; the preflight separately admits only migration defaults.
SELECT NOT EXISTS (
  SELECT 1 FROM pg_catalog.pg_namespace n
  CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
  UNION ALL
  SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
  UNION ALL
  SELECT 1 FROM pg_catalog.pg_attribute at JOIN pg_catalog.pg_class c ON c.oid=at.attrelid
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(at.attacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
  UNION ALL
  SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
  CROSS JOIN LATERAL pg_catalog.aclexplode(f.proacl) a
  WHERE n.nspname='appsurface_durable' AND a.grantee=0
) AS no_public_durable_privileges \gset
\if :no_public_durable_privileges
\else
  \echo 'Final privilege proof failed: PUBLIC retains an ACL entry on a Durable schema object; transaction rolled back.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS (
  SELECT 1 FROM role_pair p
  WHERE NOT has_schema_privilege(p.dispatcher_oid,'appsurface_durable','USAGE')
     OR has_schema_privilege(p.dispatcher_oid,'appsurface_durable','CREATE')
     OR NOT has_function_privilege(p.dispatcher_oid,
       'appsurface_durable.discover_work_dispatch(text[], text[], integer)','EXECUTE')
     OR has_function_privilege(p.dispatcher_oid,
       'appsurface_durable.runtime_due_dispatch_health(integer)','EXECUTE')
     OR (p.dispatcher_profile='full' AND
       (NOT has_table_privilege(p.dispatcher_oid,'appsurface_durable.flow_dispatch','SELECT')
        OR NOT has_function_privilege(p.dispatcher_oid,
           'appsurface_durable.claim_schedule_dispatch(text, interval)','EXECUTE')))
     OR (p.dispatcher_profile='work_only' AND
       (has_table_privilege(p.dispatcher_oid,'appsurface_durable.flow_dispatch','SELECT')
        OR has_function_privilege(p.dispatcher_oid,
           'appsurface_durable.claim_schedule_dispatch(text, interval)','EXECUTE')))
     OR NOT has_function_privilege(p.runtime_oid,
       'appsurface_durable.runtime_due_dispatch_health(integer)','EXECUTE')
) AS final_pair_privileges_are_complete \gset
\if :final_pair_privileges_are_complete
\else
  \echo 'Final privilege proof failed for one or more manifest pairs; transaction rolled back.'
  SELECT 1 / 0;
\endif

WITH service_role(role_oid, role_type, dispatcher_profile) AS (
  SELECT dispatcher_oid, 'dispatcher', dispatcher_profile FROM role_pair
  UNION ALL SELECT runtime_oid, 'runtime', NULL FROM role_pair
  UNION ALL SELECT oid, 'retention', NULL FROM pg_catalog.pg_roles WHERE rolname=:'retention_operator_role'
), relation_privilege(privilege_name) AS (
  SELECT privilege_name FROM (VALUES
    ('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE'),('REFERENCES'),('TRIGGER'),
    ('SELECT WITH GRANT OPTION'),('INSERT WITH GRANT OPTION'),('UPDATE WITH GRANT OPTION'),
    ('DELETE WITH GRANT OPTION'),('TRUNCATE WITH GRANT OPTION'),('REFERENCES WITH GRANT OPTION'),
    ('TRIGGER WITH GRANT OPTION'),('MAINTAIN'),('MAINTAIN WITH GRANT OPTION')) v(privilege_name)
  WHERE current_setting('server_version_num')::integer >= 170000
     OR privilege_name NOT LIKE 'MAINTAIN%'
), durable_relation AS (
  SELECT c.oid,c.relname FROM pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname='appsurface_durable' AND c.relkind IN ('r','p','v','m','f')
)
SELECT NOT EXISTS (
  SELECT 1 FROM service_role s CROSS JOIN durable_relation r CROSS JOIN relation_privilege p
  WHERE has_table_privilege(s.role_oid,r.oid,p.privilege_name)
    AND NOT (
      s.role_type='dispatcher' AND s.dispatcher_profile='full'
        AND r.relname='flow_dispatch' AND p.privilege_name='SELECT'
      OR s.role_type='runtime' AND p.privilege_name IN ('SELECT','INSERT') AND (
        p.privilege_name='SELECT' AND r.relname IN
          ('store_metadata','schema_migration','runtime_heartbeat','scope','work','dispatch','work_operator_command','effect_permit','scope_history','work_history','flow_instance','flow_command','flow_history','flow_wait','flow_timer','flow_dispatch','flow_repair_command','flow_repair_collision','schedule_definition','schedule_generation','schedule_command','schedule_occurrence','schedule_dispatch','schedule_history','flow_trace_context')
        OR p.privilege_name='INSERT' AND r.relname IN
          ('runtime_heartbeat','scope','work','dispatch','work_operator_command','effect_permit','scope_history','work_history','flow_instance','flow_command','flow_history','flow_wait','flow_timer','flow_dispatch','flow_repair_command','flow_repair_collision','schedule_definition','schedule_generation','schedule_command','schedule_occurrence','schedule_dispatch','schedule_history','flow_trace_context')
      )
      OR s.role_type='retention' AND p.privilege_name='SELECT' AND r.relname IN
        ('scope','work','flow_instance','flow_command','flow_history','flow_wait','flow_timer','flow_dispatch',
         'flow_trace_context','flow_retention_manifest','flow_retention_manifest_item','flow_retention_manifest_summary',
         'flow_retention_manifest_event','flow_retention_command')
    )
) AS final_pair_relation_privileges_are_exact \gset
\if :final_pair_relation_privileges_are_exact
\else
  \echo 'Final privilege proof failed: a dispatcher or runtime has unexpected effective relation access; transaction rolled back.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS (
  SELECT 1 FROM role_pair p
  JOIN pg_catalog.pg_class c ON c.relname='flow_dispatch'
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace AND n.nspname='appsurface_durable'
  JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
  CROSS JOIN (VALUES ('SELECT'),('INSERT'),('UPDATE'),('REFERENCES'),
    ('SELECT WITH GRANT OPTION'),('INSERT WITH GRANT OPTION'),
    ('UPDATE WITH GRANT OPTION'),('REFERENCES WITH GRANT OPTION')) v(privilege_name)
  WHERE p.dispatcher_profile='work_only'
    AND has_column_privilege(p.dispatcher_oid,c.oid,a.attnum,v.privilege_name)
) AS final_work_only_has_no_flow_column_privileges \gset
\if :final_work_only_has_no_flow_column_privileges
\else
  \echo 'Final privilege proof failed: a work_only dispatcher has direct Flow column access; transaction rolled back.'
  SELECT 1 / 0;
\endif

WITH runtime_relation_privilege(relname, privilege_name) AS (
  VALUES
    ('store_metadata','SELECT'),('schema_migration','SELECT'),('runtime_heartbeat','SELECT'),
    ('scope','SELECT'),('work','SELECT'),('dispatch','SELECT'),('work_operator_command','SELECT'),
    ('effect_permit','SELECT'),('scope_history','SELECT'),('work_history','SELECT'),
    ('flow_instance','SELECT'),('flow_command','SELECT'),('flow_history','SELECT'),('flow_wait','SELECT'),
    ('flow_timer','SELECT'),('flow_dispatch','SELECT'),('flow_repair_command','SELECT'),
    ('flow_repair_collision','SELECT'),('schedule_definition','SELECT'),('schedule_generation','SELECT'),
    ('schedule_command','SELECT'),('schedule_occurrence','SELECT'),('schedule_dispatch','SELECT'),
    ('schedule_history','SELECT'),('flow_trace_context','SELECT'),
    ('runtime_heartbeat','INSERT'),('scope','INSERT'),('work','INSERT'),('dispatch','INSERT'),
    ('work_operator_command','INSERT'),('effect_permit','INSERT'),('scope_history','INSERT'),
    ('work_history','INSERT'),('flow_instance','INSERT'),('flow_command','INSERT'),('flow_history','INSERT'),
    ('flow_wait','INSERT'),('flow_timer','INSERT'),('flow_dispatch','INSERT'),('flow_repair_command','INSERT'),
    ('flow_repair_collision','INSERT'),('schedule_definition','INSERT'),('schedule_generation','INSERT'),
    ('schedule_command','INSERT'),('schedule_occurrence','INSERT'),('schedule_dispatch','INSERT'),
    ('schedule_history','INSERT'),('flow_trace_context','INSERT')
)
SELECT NOT EXISTS (
  SELECT 1 FROM role_pair p CROSS JOIN runtime_relation_privilege expected
  JOIN pg_catalog.pg_class c ON c.relname=expected.relname
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace AND n.nspname='appsurface_durable'
  WHERE NOT has_table_privilege(p.runtime_oid,c.oid,expected.privilege_name)
) AS final_runtime_relation_grants_are_complete \gset
\if :final_runtime_relation_grants_are_complete
\else
  \echo 'Final privilege proof failed: one or more manifest runtimes lack a required relation grant; transaction rolled back.'
  SELECT 1 / 0;
\endif

WITH allowed_runtime_update_column(relname, attname) AS (VALUES
  ('scope','generation'),('scope','state'),('scope','updated_at'),
  ('runtime_heartbeat','worker_instance_id'),('runtime_heartbeat','runtime_epoch'),('runtime_heartbeat','hosted_surfaces'),
  ('runtime_heartbeat','started_at'),('runtime_heartbeat','last_heartbeat_at'),('runtime_heartbeat','last_successful_sweep_at'),
  ('runtime_heartbeat','draining'),('runtime_heartbeat','pass_active'),('runtime_heartbeat','pass_started_at'),
  ('runtime_heartbeat','last_discovered'),('runtime_heartbeat','last_claimed'),('runtime_heartbeat','last_processed'),
  ('runtime_heartbeat','last_deferred'),('runtime_heartbeat','last_failed'),('runtime_heartbeat','last_pass_elapsed_ms'),
  ('runtime_heartbeat','updated_at'),
  ('work','state'),('work','due_at'),('work','updated_at'),('work','terminal_at'),('work','cancellation_requested_at'),
  ('work','attempt_number'),('work','lease_generation'),('work','lease_owner'),('work','lease_started_at'),
  ('work','lease_expires_at'),('work','runtime_epoch'),('work','revision'),('work','result_contract_id'),
  ('work','result_schema_version'),('work','result_codec_id'),('work','result_classification'),
  ('work','result_retention_policy_id'),('work','result_payload'),('work','result_sha256'),('work','terminal_code'),
  ('work','trace_context_id'),
  ('dispatch','due_at'),('dispatch','state'),('dispatch','expected_revision'),('dispatch','updated_at'),
  ('work_operator_command','status'),('work_operator_command','resulting_state'),('work_operator_command','resulting_revision'),
  ('work_operator_command','resolution_kind'),('work_operator_command','completed_at'),
  ('effect_permit','status'),('effect_permit','observed_at'),('effect_permit','details'),('effect_permit','runtime_epoch'),
  ('flow_instance','state'),('flow_instance','current_node_id'),('flow_instance','context_contract_id'),
  ('flow_instance','context_schema_version'),('flow_instance','context_codec_id'),('flow_instance','context_payload'),
  ('flow_instance','context_sha256'),('flow_instance','context_classification'),('flow_instance','context_retention'),
  ('flow_instance','resume_event_name'),('flow_instance','resume_event_is_timeout'),('flow_instance','resume_event_contract_id'),
  ('flow_instance','resume_event_schema_version'),('flow_instance','resume_event_codec_id'),('flow_instance','resume_event_payload'),
  ('flow_instance','resume_event_sha256'),('flow_instance','resume_event_classification'),('flow_instance','resume_event_retention'),
  ('flow_instance','activity_callsite_id'),('flow_instance','activity_result_contract_id'),
  ('flow_instance','activity_result_schema_version'),('flow_instance','activity_result_codec_id'),
  ('flow_instance','activity_result_payload'),('flow_instance','activity_result_sha256'),
  ('flow_instance','activity_result_classification'),('flow_instance','activity_result_retention'),
  ('flow_instance','lease_generation'),('flow_instance','lease_owner'),('flow_instance','lease_started_at'),
  ('flow_instance','lease_expires_at'),('flow_instance','updated_at'),('flow_instance','cancellation_requested_at'),
  ('flow_instance','terminal_at'),('flow_instance','terminal_code'),('flow_instance','trace_context_id'),
  ('flow_instance','suspension_descriptor'),('flow_instance','suspended_from_state'),
  ('flow_instance','suspension_descriptor_schema'),('flow_instance','suspension_descriptor_sha256'),
  ('flow_instance','revision'),('flow_instance','scope_generation'),('flow_instance','runtime_epoch'),
  ('flow_command','trace_context_id'),('flow_history','trace_context_id'),
  ('flow_wait','state'),('flow_wait','resolved_revision'),('flow_wait','resolved_at'),
  ('flow_wait','suspension_descriptor'),('flow_wait','updated_at'),('flow_wait','trace_context_id'),
  ('flow_timer','state'),('flow_timer','resolved_at'),('flow_timer','updated_at'),('flow_timer','trace_context_id'),
  ('flow_dispatch','due_at'),('flow_dispatch','state'),('flow_dispatch','expected_revision'),('flow_dispatch','updated_at'),
  ('schedule_definition','display_name'),('schedule_definition','state'),('schedule_definition','active_generation'),
  ('schedule_definition','revision'),('schedule_definition','accepted_at_utc'),('schedule_definition','cursor_utc'),
  ('schedule_definition','next_due_utc'),('schedule_definition','scope_generation'),('schedule_definition','runtime_epoch'),
  ('schedule_definition','suspension_code'),('schedule_definition','updated_at'),
  ('schedule_occurrence','last_nominal_utc'),('schedule_occurrence','state'),('schedule_occurrence','target_kind'),
  ('schedule_occurrence','target_id'),('schedule_occurrence','target_command_id'),
  ('schedule_occurrence','target_idempotency_key'),('schedule_occurrence','claimed_by'),
  ('schedule_occurrence','lease_expires_at'),('schedule_occurrence','updated_at'),
  ('schedule_dispatch','dispatch_revision'),('schedule_dispatch','due_at'),('schedule_dispatch','state'),
  ('schedule_dispatch','lease_owner'),('schedule_dispatch','lease_generation'),
  ('schedule_dispatch','lease_expires_at'),('schedule_dispatch','updated_at')
)
SELECT NOT EXISTS (
  SELECT 1 FROM role_pair p
  JOIN pg_catalog.pg_class c ON true
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace AND n.nspname='appsurface_durable'
  JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
  WHERE has_column_privilege(p.dispatcher_oid,c.oid,a.attnum,'UPDATE')
     OR has_column_privilege(p.dispatcher_oid,c.oid,a.attnum,'UPDATE WITH GRANT OPTION')
     OR has_column_privilege(p.dispatcher_oid,c.oid,a.attnum,'SELECT WITH GRANT OPTION')
     OR has_column_privilege(p.dispatcher_oid,c.oid,a.attnum,'INSERT WITH GRANT OPTION')
     OR has_column_privilege(p.dispatcher_oid,c.oid,a.attnum,'REFERENCES WITH GRANT OPTION')
     OR has_column_privilege(p.runtime_oid,c.oid,a.attnum,'UPDATE WITH GRANT OPTION')
     OR has_column_privilege(p.runtime_oid,c.oid,a.attnum,'SELECT WITH GRANT OPTION')
     OR has_column_privilege(p.runtime_oid,c.oid,a.attnum,'INSERT WITH GRANT OPTION')
     OR has_column_privilege(p.runtime_oid,c.oid,a.attnum,'REFERENCES WITH GRANT OPTION')
     OR (has_column_privilege(p.runtime_oid,c.oid,a.attnum,'UPDATE')
         AND NOT EXISTS (SELECT 1 FROM allowed_runtime_update_column allowed
                         WHERE c.relname=allowed.relname AND a.attname=allowed.attname))
)
AND NOT EXISTS (
  SELECT 1 FROM role_pair p
  CROSS JOIN allowed_runtime_update_column allowed
  JOIN pg_catalog.pg_class c ON c.relname=allowed.relname
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace AND n.nspname='appsurface_durable'
  JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attname=allowed.attname
    AND a.attnum>0 AND NOT a.attisdropped
  WHERE NOT has_column_privilege(p.runtime_oid,c.oid,a.attnum,'UPDATE')
)
AND NOT EXISTS (
  SELECT 1 FROM pg_catalog.pg_roles retention
  JOIN pg_catalog.pg_class c ON true
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace AND n.nspname='appsurface_durable'
  JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
  WHERE retention.rolname=:'retention_operator_role'
    AND (has_column_privilege(retention.oid,c.oid,a.attnum,'UPDATE')
      OR has_column_privilege(retention.oid,c.oid,a.attnum,'UPDATE WITH GRANT OPTION')
      OR has_column_privilege(retention.oid,c.oid,a.attnum,'SELECT WITH GRANT OPTION')
      OR has_column_privilege(retention.oid,c.oid,a.attnum,'INSERT WITH GRANT OPTION')
      OR has_column_privilege(retention.oid,c.oid,a.attnum,'REFERENCES WITH GRANT OPTION'))
) AS final_column_update_privileges_are_exact \gset
\if :final_column_update_privileges_are_exact
\else
  \echo 'Final privilege proof failed: column UPDATE access or grant options differ from the runtime allowlist; transaction rolled back.'
  SELECT 1 / 0;
\endif

WITH service_role(role_oid, role_type, dispatcher_profile) AS (
  SELECT dispatcher_oid,'dispatcher',dispatcher_profile FROM role_pair
  UNION ALL SELECT runtime_oid,'runtime',NULL FROM role_pair
  UNION ALL SELECT oid,'retention',NULL FROM pg_catalog.pg_roles WHERE rolname=:'retention_operator_role'
), durable_function AS (
  SELECT f.oid FROM pg_catalog.pg_proc f
  JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
  WHERE n.nspname='appsurface_durable' AND f.prokind='f'
)
SELECT NOT EXISTS (
  SELECT 1 FROM service_role s CROSS JOIN durable_function f
  WHERE has_function_privilege(s.role_oid,f.oid,'EXECUTE')
    AND NOT (
      s.role_type='dispatcher' AND (
        f.oid='appsurface_durable.discover_work_dispatch(text[], text[], integer)'::regprocedure
        OR s.dispatcher_profile='full' AND f.oid='appsurface_durable.claim_schedule_dispatch(text, interval)'::regprocedure)
      OR s.role_type='runtime' AND f.oid IN
        ('appsurface_durable.runtime_due_dispatch_health(integer)'::regprocedure,
         'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)'::regprocedure)
      OR s.role_type='retention' AND f.oid IN
        ('appsurface_durable.create_flow_retention_manifest(text, text, text, text, char(64), text, char(64), integer, bigint, jsonb, text, text, char(64))'::regprocedure,
         'appsurface_durable.apply_flow_retention_lifecycle(text, text, text, text, text, char(64), text, text, bigint, text, text, char(64), text, char(64), integer, boolean)'::regprocedure)
    )
    OR has_function_privilege(s.role_oid,f.oid,'EXECUTE WITH GRANT OPTION')
) AND NOT EXISTS (
  SELECT 1 FROM role_pair p
  WHERE NOT has_function_privilege(p.dispatcher_oid,'appsurface_durable.discover_work_dispatch(text[], text[], integer)','EXECUTE')
     OR NOT has_function_privilege(p.runtime_oid,'appsurface_durable.runtime_due_dispatch_health(integer)','EXECUTE')
     OR NOT has_function_privilege(p.runtime_oid,'appsurface_durable.prune_runtime_heartbeats(interval, integer, text, uuid)','EXECUTE')
) AS final_pair_function_privileges_are_exact \gset
\if :final_pair_function_privileges_are_exact
\else
  \echo 'Final privilege proof failed: function EXECUTE does not match the complete pair allowlist; transaction rolled back.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS (
  SELECT 1 FROM role_pair p
  CROSS JOIN pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname='appsurface_durable' AND c.relkind='S'
    AND (has_sequence_privilege(p.dispatcher_oid,c.oid,'USAGE')
      OR has_sequence_privilege(p.dispatcher_oid,c.oid,'SELECT')
      OR has_sequence_privilege(p.dispatcher_oid,c.oid,'UPDATE')
      OR has_sequence_privilege(p.runtime_oid,c.oid,'UPDATE'))
) AS final_pair_sequence_privileges_are_exact \gset
\if :final_pair_sequence_privileges_are_exact
\else
  \echo 'Final privilege proof failed: a dispatcher has sequence access or a runtime has sequence UPDATE; transaction rolled back.'
  SELECT 1 / 0;
\endif
SELECT NOT EXISTS (
  SELECT 1 FROM role_pair p CROSS JOIN pg_catalog.pg_class c
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname='appsurface_durable' AND c.relkind='S'
    AND (NOT has_sequence_privilege(p.runtime_oid,c.oid,'USAGE')
      OR NOT has_sequence_privilege(p.runtime_oid,c.oid,'SELECT'))
) AS final_runtime_sequence_grants_are_complete \gset
\if :final_runtime_sequence_grants_are_complete
\else
  \echo 'Final privilege proof failed: one or more manifest runtimes lack required sequence grants; transaction rolled back.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS (
  SELECT 1 FROM pg_catalog.pg_policy p
  JOIN pg_catalog.pg_class c ON c.oid=p.polrelid
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname='appsurface_durable'
    AND p.polname IN ('runtime_heartbeat_runtime_role','flow_dispatch_runtime_scope_select',
      'schedule_dispatch_runtime_scope_select','schedule_dispatch_scope_update')
    AND p.polroles IS DISTINCT FROM ARRAY(SELECT runtime_oid FROM role_pair ORDER BY runtime_oid)
) AS final_runtime_policy_targets_are_exact \gset
\if :final_runtime_policy_targets_are_exact
\else
  \echo 'Final policy proof failed: runtime policy targets do not equal the complete manifest; transaction rolled back.'
  SELECT 1 / 0;
\endif

SELECT NOT EXISTS (
  SELECT 1 FROM pg_catalog.pg_policy p
  JOIN pg_catalog.pg_class c ON c.oid=p.polrelid
  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname='appsurface_durable'
    AND p.polname IN ('flow_dispatch_global_discovery','schedule_dispatch_global_discovery','schedule_dispatch_global_lease')
    AND p.polroles IS DISTINCT FROM
      (ARRAY(SELECT dispatcher_oid FROM role_pair WHERE dispatcher_profile='full' ORDER BY dispatcher_oid)
       || ARRAY[(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=:'migration_owner_role')])
) AS final_full_dispatcher_policy_targets_are_exact \gset
\if :final_full_dispatcher_policy_targets_are_exact
\else
  \echo 'Final policy proof failed: global dispatcher policies do not match full-profile pairs and migration owner; transaction rolled back.'
  SELECT 1 / 0;
\endif

-- Test-only atomicity seam. It is inert unless a harness explicitly defines the
-- boolean psql variable; when enabled, failure occurs after all mutations and
-- assertions so PostgreSQL must roll the entire recipe transaction back.
\if :{?role_recipe_test_force_failure}
  \if :role_recipe_test_force_failure
    \echo 'Forced role-recipe test failure immediately before COMMIT; transaction must roll back.'
    SELECT 1 / 0;
  \endif
\endif

COMMIT;

-- The host creates roles and assigns membership. This recipe transfers every package relation to the migration owner,
-- never grants DDL or BYPASSRLS to service roles, and does not treat runtime credentials as application authorization.
