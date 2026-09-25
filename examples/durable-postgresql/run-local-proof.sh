#!/usr/bin/env bash
set -euo pipefail
set -m

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
POSTGRES_IMAGE="postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877"
DATABASE_NAME="appsurface_durable_example"
CONTAINER_NAME="appsurface-durable-proof-$$"
MIGRATION_OWNER_ROLE="appsurface_durable_owner"
DISPATCHER_ROLE="appsurface_durable_dispatcher"
RUNTIME_ROLE="appsurface_durable_runtime"
SOURCE_DISPATCHER_ROLE="appsurface_durable_source_dispatcher"
SOURCE_RUNTIME_ROLE="appsurface_durable_source_runtime"
RETENTION_ROLE="appsurface_durable_retention"
ROLE_PAIRS_MANIFEST="$ROOT_DIR/examples/durable-postgresql/role-pairs.example.json"
STARTED_AT_SECONDS="$(date +%s)"
POSTGRES_ADMIN_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
MIGRATION_OWNER_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
DISPATCHER_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
RUNTIME_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
SOURCE_DISPATCHER_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
SOURCE_RUNTIME_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
RETENTION_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
FOREGROUND_PID=""
FOREGROUND_PID_FILE="$(mktemp -t appsurface-durable-proof-pid.XXXXXX)"
RUNTIME_EPOCH_FILE="$(mktemp -t appsurface-durable-proof-epoch.XXXXXX)"
ROLE_SQL_FILE="$(mktemp -t appsurface-durable-proof-roles.XXXXXX)"
LOCAL_PORT_FILE="$(mktemp -t appsurface-durable-proof-port.XXXXXX)"
OMISSION_OUTPUT_FILE="$(mktemp -t appsurface-durable-proof-omission.XXXXXX)"
INTERRUPT_REQUESTED=0
LAUNCHING_FOREGROUND=0
MAX_TIMEOUT_SECONDS=86400

signal_process_group() {
  local signal="$1"
  local pid="$2"
  if [[ -z "$pid" ]]; then
    return
  fi

  kill "$signal" -- "-$pid" >/dev/null 2>&1 || kill "$signal" "$pid" >/dev/null 2>&1 || true
}
process_group_is_alive() {
  local pid="$1"
  kill -0 -- "-$pid" >/dev/null 2>&1 || kill -0 "$pid" >/dev/null 2>&1
}
terminate_process_group() {
  local pid="$1"
  if [[ -z "$pid" ]]; then
    return
  fi

  signal_process_group -TERM "$pid"
  for _ in {1..4}; do
    if ! process_group_is_alive "$pid"; then
      return
    fi
    sleep 0.05
  done
  signal_process_group -KILL "$pid"
}
cleanup_container() {
  set -m
  (docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1) &
  local cleanup_pid="$!"
  set +m
  for _ in {1..40}; do
    if ! process_group_is_alive "$cleanup_pid"; then
      wait "$cleanup_pid" 2>/dev/null || true
      return
    fi
    sleep 0.05
  done

  signal_process_group -KILL "$cleanup_pid"
  wait "$cleanup_pid" 2>/dev/null || true
}
cleanup() {
  cleanup_container
  rm -f "$FOREGROUND_PID_FILE" "$RUNTIME_EPOCH_FILE" "$ROLE_SQL_FILE" "$LOCAL_PORT_FILE" "$OMISSION_OUTPUT_FILE"
}
terminate_foreground() {
  local pid="${FOREGROUND_PID:-}"
  if [[ -z "$pid" && -s "$FOREGROUND_PID_FILE" ]]; then
    pid="$(<"$FOREGROUND_PID_FILE")"
  fi
  terminate_process_group "$pid"
}
interrupt() {
  INTERRUPT_REQUESTED=1
  if [[ "$LAUNCHING_FOREGROUND" == 1 && -z "${FOREGROUND_PID:-}" ]]; then
    return
  fi
  terminate_foreground
  exit 130
}
trap cleanup EXIT
trap interrupt INT TERM

WATCHDOG_PID=""
if [[ -z "${APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS:-}" ]]; then
  APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS=420
fi
if [[ ! "$APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ \
  || "${#APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS}" -gt 5 ]]; then
  printf 'APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS must be an integer from 1 through %s.\n' \
    "$MAX_TIMEOUT_SECONDS" >&2
  exit 2
fi
if (( 10#$APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS > MAX_TIMEOUT_SECONDS )); then
  printf 'APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS must be an integer from 1 through %s.\n' \
    "$MAX_TIMEOUT_SECONDS" >&2
  exit 2
fi
(
  sleep "$APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS"
  printf 'Local operational-assessment proof exceeded its %s-second deadline.\n' \
    "$APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS" >&2
  kill -TERM "$$" 2>/dev/null || true
) &
WATCHDOG_PID="$!"
run_foreground() {
  LAUNCHING_FOREGROUND=1
  set -m
  ("$@") &
  FOREGROUND_PID="$!"
  printf '%s' "$FOREGROUND_PID" > "$FOREGROUND_PID_FILE"
  set +m
  LAUNCHING_FOREGROUND=0
  if [[ "$INTERRUPT_REQUESTED" == 1 ]]; then
    interrupt
  fi

  local exit_code=0
  wait "$FOREGROUND_PID" 2>/dev/null || exit_code="$?"
  FOREGROUND_PID=""
  : > "$FOREGROUND_PID_FILE"
  return "$exit_code"
}
stop_watchdog() {
  if [[ -n "$WATCHDOG_PID" ]]; then
    terminate_process_group "$WATCHDOG_PID"
    wait "$WATCHDOG_PID" 2>/dev/null || true
    WATCHDOG_PID=""
  fi
}
trap 'stop_watchdog; cleanup' EXIT

LOCAL_PORT="${APPSURFACE_DURABLE_LOCAL_PORT:-}"
if [[ -n "$LOCAL_PORT" ]]; then
  run_foreground env APPSURFACE_DURABLE_PREREQUISITE_PORT="$LOCAL_PORT" \
    bash "$ROOT_DIR/examples/durable-postgresql/check-prerequisites.sh"
  PUBLISH_ARGUMENT="127.0.0.1:$LOCAL_PORT:5432"
else
  run_foreground env APPSURFACE_DURABLE_PREREQUISITE_SKIP_PORT_CHECK=true \
    bash "$ROOT_DIR/examples/durable-postgresql/check-prerequisites.sh"
  PUBLISH_ARGUMENT="127.0.0.1::5432"
fi

printf '[run-local-proof] starting disposable PostgreSQL 16.5\n'
run_foreground docker run --detach --rm \
  --name "$CONTAINER_NAME" \
  --env POSTGRES_PASSWORD="$POSTGRES_ADMIN_PASSWORD" \
  --env POSTGRES_DB="$DATABASE_NAME" \
  --publish "$PUBLISH_ARGUMENT" \
  "$POSTGRES_IMAGE" >/dev/null
if [[ -z "$LOCAL_PORT" ]]; then
  run_foreground docker port "$CONTAINER_NAME" 5432/tcp > "$LOCAL_PORT_FILE"
  LOCAL_PORT="$(sed -n 's/^127\.0\.0\.1:\([0-9][0-9]*\)$/\1/p' "$LOCAL_PORT_FILE")"
  if [[ ! "$LOCAL_PORT" =~ ^[0-9]{1,5}$ ]]; then
    printf 'Docker did not report a valid dynamically allocated loopback port.\n' >&2
    exit 1
  fi
fi
printf '[run-local-proof] PostgreSQL is published on 127.0.0.1:%s\n' "$LOCAL_PORT"

ready=0
for _ in {1..30}; do
  if run_foreground docker exec "$CONTAINER_NAME" \
    sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" exec psql -v ON_ERROR_STOP=1 -h 127.0.0.1 -U postgres -d "$1" -c "SELECT 1;"' \
    sh "$DATABASE_NAME" >/dev/null 2>&1; then
    ready=1
    break
  fi
  run_foreground sleep 1
done
if [[ "$ready" != 1 ]]; then
  printf 'PostgreSQL did not become ready within 30 seconds.\n' >&2
  exit 1
fi

printf -v ROLE_SQL '%s\n' \
  "CREATE ROLE $MIGRATION_OWNER_ROLE LOGIN PASSWORD '$MIGRATION_OWNER_PASSWORD' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  "CREATE ROLE $DISPATCHER_ROLE LOGIN PASSWORD '$DISPATCHER_PASSWORD' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  "CREATE ROLE $RUNTIME_ROLE LOGIN PASSWORD '$RUNTIME_PASSWORD' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  "CREATE ROLE $SOURCE_DISPATCHER_ROLE LOGIN PASSWORD '$SOURCE_DISPATCHER_PASSWORD' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  "CREATE ROLE $SOURCE_RUNTIME_ROLE LOGIN PASSWORD '$SOURCE_RUNTIME_PASSWORD' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  "CREATE ROLE $RETENTION_ROLE LOGIN PASSWORD '$RETENTION_PASSWORD' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  "GRANT CREATE ON DATABASE $DATABASE_NAME TO $MIGRATION_OWNER_ROLE;"
printf '%s\n' "$ROLE_SQL" > "$ROLE_SQL_FILE"
run_foreground docker exec -i "$CONTAINER_NAME" \
  psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" < "$ROLE_SQL_FILE" >/dev/null
unset ROLE_SQL

export APPSURFACE_DURABLE_MIGRATION_CONNECTION="Host=127.0.0.1;Port=$LOCAL_PORT;Database=$DATABASE_NAME;Username=$MIGRATION_OWNER_ROLE;Password=$MIGRATION_OWNER_PASSWORD"
export APPSURFACE_DURABLE_DISPATCHER_CONNECTION="Host=127.0.0.1;Port=$LOCAL_PORT;Database=$DATABASE_NAME;Username=$DISPATCHER_ROLE;Password=$DISPATCHER_PASSWORD"
export APPSURFACE_DURABLE_RUNTIME_CONNECTION="Host=127.0.0.1;Port=$LOCAL_PORT;Database=$DATABASE_NAME;Username=$RUNTIME_ROLE;Password=$RUNTIME_PASSWORD"

run_foreground dotnet build "$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj" \
  --configuration Release \
  -m:1 \
  -p:UseSharedCompilation=false
run_foreground dotnet run --project "$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli" \
  --configuration Release \
  --no-build \
  -- durable schema apply \
  --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION \
  --apply
printf '[ok] durable schema applied through the package-required version\n'

ROLE_PAIRS_JSON="$(<"$ROLE_PAIRS_MANIFEST")"
run_foreground docker exec -i "$CONTAINER_NAME" \
  psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" \
  -v migration_owner_role="$MIGRATION_OWNER_ROLE" \
  -v role_pairs_json="$ROLE_PAIRS_JSON" \
  -v retention_operator_role="$RETENTION_ROLE" \
  -f - < "$ROOT_DIR/Durable/configure-postgresql-roles.sql" >/dev/null
printf '[ok] canonical PostgreSQL roles reconciled\n'

run_foreground docker exec -i "$CONTAINER_NAME" \
  psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" \
  -v migration_owner_role="$MIGRATION_OWNER_ROLE" \
  -v role_pairs_json="$ROLE_PAIRS_JSON" \
  -v retention_operator_role="$RETENTION_ROLE" \
  -f - < "$ROOT_DIR/Durable/configure-postgresql-roles.sql" >/dev/null
printf '[ok] identical complete-manifest rerun succeeded\n'

if run_foreground docker exec -i "$CONTAINER_NAME" \
  psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" \
  -v migration_owner_role="$MIGRATION_OWNER_ROLE" \
  -v role_pairs_json='{"version":1,"pairs":[{"dispatcher":"appsurface_durable_dispatcher","runtime":"appsurface_durable_runtime","dispatcher_profile":"full"}]}' \
  -v retention_operator_role="$RETENTION_ROLE" \
  -f - < "$ROOT_DIR/Durable/configure-postgresql-roles.sql" > "$OMISSION_OUTPUT_FILE" 2>&1; then
  printf 'The role recipe accepted an omitted installed pair; expected a fail-closed refusal.\n' >&2
  exit 1
fi
if ! grep -Fq 'Rejected unmanifested Durable role principal(s):' "$OMISSION_OUTPUT_FILE" \
  || ! grep -Fq "$SOURCE_DISPATCHER_ROLE" "$OMISSION_OUTPUT_FILE"; then
  printf 'The omitted-pair recipe failed without the expected Source-role refusal; inspect PostgreSQL connectivity and recipe diagnostics.\n' >&2
  exit 1
fi
printf '[ok] omitted-pair manifest was refused\n'

run_foreground docker exec -i "$CONTAINER_NAME" \
  psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" \
  -v source_dispatcher_role="$SOURCE_DISPATCHER_ROLE" -f - <<'SQL'
SELECT has_schema_privilege(:'source_dispatcher_role', 'appsurface_durable', 'USAGE')
   AND NOT has_schema_privilege(:'source_dispatcher_role', 'appsurface_durable', 'CREATE')
   AND has_function_privilege(:'source_dispatcher_role',
       'appsurface_durable.discover_work_dispatch(text[],text[],integer)', 'EXECUTE')
   AND NOT EXISTS (
       SELECT 1
       FROM pg_catalog.pg_class AS relation
       JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
       WHERE namespace.nspname = 'appsurface_durable'
         AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
         AND (
             has_table_privilege(:'source_dispatcher_role', relation.oid, 'SELECT')
             OR has_table_privilege(:'source_dispatcher_role', relation.oid, 'INSERT')
             OR has_table_privilege(:'source_dispatcher_role', relation.oid, 'UPDATE')
             OR has_table_privilege(:'source_dispatcher_role', relation.oid, 'DELETE')
             OR has_table_privilege(:'source_dispatcher_role', relation.oid, 'TRUNCATE')
             OR has_table_privilege(:'source_dispatcher_role', relation.oid, 'REFERENCES')
             OR has_table_privilege(:'source_dispatcher_role', relation.oid, 'TRIGGER')
             OR EXISTS (
                 SELECT 1
                 FROM pg_catalog.pg_attribute AS attribute
                 CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES')) AS privilege(name)
                 WHERE attribute.attrelid = relation.oid
                   AND attribute.attnum > 0
                   AND NOT attribute.attisdropped
                   AND has_column_privilege(
                       :'source_dispatcher_role', relation.oid, attribute.attnum, privilege.name)
             )
         )
   )
   AND NOT EXISTS (
       SELECT 1
       FROM pg_catalog.pg_class AS sequence
       JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = sequence.relnamespace
       WHERE namespace.nspname = 'appsurface_durable'
         AND sequence.relkind = 'S'
         AND (
             has_sequence_privilege(:'source_dispatcher_role', sequence.oid, 'USAGE')
             OR has_sequence_privilege(:'source_dispatcher_role', sequence.oid, 'SELECT')
             OR has_sequence_privilege(:'source_dispatcher_role', sequence.oid, 'UPDATE')
         )
   )
   AND NOT EXISTS (
       SELECT 1
       FROM pg_catalog.pg_proc AS routine
       JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
       WHERE namespace.nspname = 'appsurface_durable'
         AND routine.oid <> 'appsurface_durable.discover_work_dispatch(text[],text[],integer)'::regprocedure
         AND has_function_privilege(:'source_dispatcher_role', routine.oid, 'EXECUTE')
   ) AS proof_passed
\gset
\if :proof_passed
  \echo 'Work-only dispatcher privilege proof passed.'
\else
  \echo 'Work-only dispatcher privilege proof failed.'
  SELECT 1 / 0;
\endif
SQL
printf '[ok] Work-only dispatcher has Work discovery and no direct Flow/Schedule authority\n'
printf '[run-local-proof] reviewed manifest SHA-256: '
shasum -a 256 "$ROLE_PAIRS_MANIFEST"

run_foreground docker exec "$CONTAINER_NAME" \
  psql -Aqt -U postgres -d "$DATABASE_NAME" -c 'SELECT gen_random_uuid();' > "$RUNTIME_EPOCH_FILE"
APPSURFACE_DURABLE_RUNTIME_EPOCH="$(<"$RUNTIME_EPOCH_FILE")"
export APPSURFACE_DURABLE_RUNTIME_EPOCH

run_foreground dotnet build "$ROOT_DIR/examples/durable-postgresql/DurablePostgreSqlLocalExample.csproj" \
  --configuration Release \
  -m:1 \
  -p:UseSharedCompilation=false
run_foreground env DOTNET_ENVIRONMENT=Development APPSURFACE_DURABLE_LOCAL_PROOF=1 \
  dotnet run --project "$ROOT_DIR/examples/durable-postgresql" \
  --configuration Release \
  --no-build \
  -- schema-bootstrap-dev

run_foreground env DOTNET_ENVIRONMENT=Development APPSURFACE_DURABLE_LOCAL_PROOF=1 \
  dotnet run --project "$ROOT_DIR/examples/durable-postgresql" \
  --configuration Release \
  --no-build \
  -- verify-local

stop_watchdog
ELAPSED_SECONDS="$(( $(date +%s) - STARTED_AT_SECONDS ))"
printf '[ok] local operational-assessment proof completed in %s seconds\n' "$ELAPSED_SECONDS"
