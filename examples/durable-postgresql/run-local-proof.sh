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
RETENTION_ROLE="appsurface_durable_retention"
STARTED_AT_SECONDS="$(date +%s)"
POSTGRES_ADMIN_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
MIGRATION_OWNER_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
DISPATCHER_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
RUNTIME_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
RETENTION_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
FOREGROUND_PID=""
FOREGROUND_PID_FILE="$(mktemp -t appsurface-durable-proof-pid.XXXXXX)"
RUNTIME_EPOCH_FILE="$(mktemp -t appsurface-durable-proof-epoch.XXXXXX)"
ROLE_SQL_FILE="$(mktemp -t appsurface-durable-proof-roles.XXXXXX)"

cleanup() {
  docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
  rm -f "$FOREGROUND_PID_FILE" "$RUNTIME_EPOCH_FILE" "$ROLE_SQL_FILE"
}
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
terminate_foreground() {
  local pid="${FOREGROUND_PID:-}"
  if [[ -z "$pid" && -s "$FOREGROUND_PID_FILE" ]]; then
    pid="$(<"$FOREGROUND_PID_FILE")"
  fi
  terminate_process_group "$pid"
}
interrupt() {
  terminate_foreground
  cleanup
  exit 130
}
trap cleanup EXIT
trap interrupt INT TERM

WATCHDOG_PID=""
if [[ -z "${APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS:-}" ]]; then
  APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS=420
fi
if [[ ! "$APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ ]]; then
  printf 'APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS must be a positive integer.\n' >&2
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
  set -m
  ("$@") &
  FOREGROUND_PID="$!"
  printf '%s' "$FOREGROUND_PID" > "$FOREGROUND_PID_FILE"
  set +m

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

port_is_free() {
  ! (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null
}

select_port() {
  if [[ -n "${APPSURFACE_DURABLE_LOCAL_PORT:-}" ]]; then
    printf '%s' "$APPSURFACE_DURABLE_LOCAL_PORT"
    return
  fi

  local candidate
  for candidate in {54329..54349}; do
    if port_is_free "$candidate"; then
      printf '%s' "$candidate"
      return
    fi
  done

  printf 'No free loopback port was found from 54329 through 54349.\n' >&2
  return 1
}

LOCAL_PORT="$(select_port)"
run_foreground env APPSURFACE_DURABLE_PREREQUISITE_PORT="$LOCAL_PORT" \
  bash "$ROOT_DIR/examples/durable-postgresql/check-prerequisites.sh"

printf '[run-local-proof] starting disposable PostgreSQL 16.5 on 127.0.0.1:%s\n' "$LOCAL_PORT"
run_foreground docker run --detach --rm \
  --name "$CONTAINER_NAME" \
  --env POSTGRES_PASSWORD="$POSTGRES_ADMIN_PASSWORD" \
  --env POSTGRES_DB="$DATABASE_NAME" \
  --publish "127.0.0.1:$LOCAL_PORT:5432" \
  "$POSTGRES_IMAGE" >/dev/null

ready=0
for _ in {1..30}; do
  if run_foreground docker exec "$CONTAINER_NAME" pg_isready -U postgres -d "$DATABASE_NAME" >/dev/null 2>&1; then
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

run_foreground docker exec -i "$CONTAINER_NAME" \
  psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" \
  -v migration_owner_role="$MIGRATION_OWNER_ROLE" \
  -v dispatcher_role="$DISPATCHER_ROLE" \
  -v runtime_role="$RUNTIME_ROLE" \
  -v retention_operator_role="$RETENTION_ROLE" \
  -f - < "$ROOT_DIR/Durable/configure-postgresql-roles.sql" >/dev/null
printf '[ok] canonical PostgreSQL roles reconciled\n'

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
