#!/usr/bin/env bash
set -euo pipefail

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

cleanup() {
  docker rm --force "$CONTAINER_NAME" >/dev/null 2>&1 || true
}
interrupt() {
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
stop_watchdog() {
  if [[ -n "$WATCHDOG_PID" ]]; then
    kill "$WATCHDOG_PID" >/dev/null 2>&1 || true
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
APPSURFACE_DURABLE_PREREQUISITE_PORT="$LOCAL_PORT" \
  bash "$ROOT_DIR/examples/durable-postgresql/check-prerequisites.sh"

printf '[run-local-proof] starting disposable PostgreSQL 16.5 on 127.0.0.1:%s\n' "$LOCAL_PORT"
docker run --detach --rm \
  --name "$CONTAINER_NAME" \
  --env POSTGRES_PASSWORD="$POSTGRES_ADMIN_PASSWORD" \
  --env POSTGRES_DB="$DATABASE_NAME" \
  --publish "127.0.0.1:$LOCAL_PORT:5432" \
  "$POSTGRES_IMAGE" >/dev/null

ready=0
for _ in {1..30}; do
  if docker exec "$CONTAINER_NAME" pg_isready -U postgres -d "$DATABASE_NAME" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
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
printf '%s\n' "$ROLE_SQL" |
  docker exec -i "$CONTAINER_NAME" \
    psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" >/dev/null
unset ROLE_SQL

export APPSURFACE_DURABLE_MIGRATION_CONNECTION="Host=127.0.0.1;Port=$LOCAL_PORT;Database=$DATABASE_NAME;Username=$MIGRATION_OWNER_ROLE;Password=$MIGRATION_OWNER_PASSWORD"
export APPSURFACE_DURABLE_DISPATCHER_CONNECTION="Host=127.0.0.1;Port=$LOCAL_PORT;Database=$DATABASE_NAME;Username=$DISPATCHER_ROLE;Password=$DISPATCHER_PASSWORD"
export APPSURFACE_DURABLE_RUNTIME_CONNECTION="Host=127.0.0.1;Port=$LOCAL_PORT;Database=$DATABASE_NAME;Username=$RUNTIME_ROLE;Password=$RUNTIME_PASSWORD"

dotnet build "$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj" \
  --configuration Release \
  -m:1 \
  -p:UseSharedCompilation=false
dotnet run --project "$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli" \
  --configuration Release \
  --no-build \
  -- durable schema apply \
  --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION \
  --apply
printf '[ok] durable schema applied through the package-required version\n'

docker exec -i "$CONTAINER_NAME" \
  psql -v ON_ERROR_STOP=1 -U postgres -d "$DATABASE_NAME" \
  -v migration_owner_role="$MIGRATION_OWNER_ROLE" \
  -v dispatcher_role="$DISPATCHER_ROLE" \
  -v runtime_role="$RUNTIME_ROLE" \
  -v retention_operator_role="$RETENTION_ROLE" \
  -f - < "$ROOT_DIR/Durable/configure-postgresql-roles.sql" >/dev/null
printf '[ok] canonical PostgreSQL roles reconciled\n'

APPSURFACE_DURABLE_RUNTIME_EPOCH="$(docker exec "$CONTAINER_NAME" \
  psql -Aqt -U postgres -d "$DATABASE_NAME" -c 'SELECT gen_random_uuid();')"
export APPSURFACE_DURABLE_RUNTIME_EPOCH

dotnet build "$ROOT_DIR/examples/durable-postgresql/DurablePostgreSqlLocalExample.csproj" \
  --configuration Release \
  -m:1 \
  -p:UseSharedCompilation=false
DOTNET_ENVIRONMENT=Development APPSURFACE_DURABLE_LOCAL_PROOF=1 \
  dotnet run --project "$ROOT_DIR/examples/durable-postgresql" \
  --configuration Release \
  --no-build \
  -- schema-bootstrap-dev

DOTNET_ENVIRONMENT=Development APPSURFACE_DURABLE_LOCAL_PROOF=1 \
  dotnet run --project "$ROOT_DIR/examples/durable-postgresql" \
  --configuration Release \
  --no-build \
  -- verify-local

stop_watchdog
ELAPSED_SECONDS="$(( $(date +%s) - STARTED_AT_SECONDS ))"
printf '[ok] local operational-assessment proof completed in %s seconds\n' "$ELAPSED_SECONDS"
