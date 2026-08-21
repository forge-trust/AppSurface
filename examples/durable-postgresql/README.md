# Durable PostgreSQL local tutorial

This public-preview tutorial proves the current [`ForgeTrust.AppSurface.Durable.PostgreSql`](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md) adoption path on a disposable PostgreSQL 16+ database. It is a local composition reference, not production operations guidance. Application startup never applies DDL.

The [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands) owns migration status, reviewed scripts, preflight, and guarded apply. This example owns only two local-proof commands:

:::callout danger
Application startup never applies DDL. Generate, review, and apply migrations through the explicit migration-owner workflow before any runtime host starts.
:::

- `schema-bootstrap-dev` initializes one active epoch only with `DOTNET_ENVIRONMENT=Development`, `APPSURFACE_DURABLE_LOCAL_PROOF=1`, a loopback migration-owner connection, and the tutorial's migration-owner role; it does not apply migrations.
- `verify-local` uses the separate loopback runtime and dispatcher identities to register Work, Flow, Schedule, health, drain, and the bounded pump; it starts the explicitly composed `AddWorkerHost()` until it completes a hosted pass, verifies the durable catalog and migration metadata are unchanged, then stops it without doing DDL.

## Prerequisites

Install all of the following before starting:

- .NET 10 SDK.
- Docker Engine or Docker Desktop with Linux containers.
- A free local TCP port. The transcript defaults to `54329` but uses one shell variable so a different free loopback port stays consistent.
- A repository checkout. The transcript creates its four separate local PostgreSQL roles: migration owner,
  payload-free dispatcher, scoped runtime, and dedicated retention operator.

The canonical [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) recipe owns the reviewed grants. Do not substitute ad-hoc grants or a copied role script. The dispatcher, runtime, and retention-operator roles must be distinct non-owner login roles without `SUPERUSER`, `CREATEDB`, `CREATEROLE`, `REPLICATION`, or `BYPASSRLS`.

:::tabs "Which environment are you preparing?"
:::tab "Local proof"
Continue with the disposable PostgreSQL 17 transcript below. It creates restricted loopback-only roles and proves one bounded worker pass without application-startup DDL.
:::
:::tab "Production"
Use the reviewed migration-owner workflow, deployment secrets, role recipe, preflight, and forward-only recovery guidance. The local transcript is not a production runbook.
:::
:::

Only these configuration names are required. Values are placeholders and must never be committed or printed:

```text
DOTNET_ENVIRONMENT
APPSURFACE_DURABLE_LOCAL_PROOF
APPSURFACE_DURABLE_PASSFILE
PGPASSFILE
APPSURFACE_DURABLE_MIGRATION_CONNECTION
APPSURFACE_DURABLE_DISPATCHER_CONNECTION
APPSURFACE_DURABLE_RUNTIME_CONNECTION
APPSURFACE_DURABLE_RUNTIME_EPOCH
```

### Copy-paste prerequisite check

Choose the loopback port once, then run this Bash check before starting the PostgreSQL container. It reports a missing
.NET 10 SDK, Docker daemon, or occupied local port before any migration command runs. It only prints the
later-required configuration names, never their values. The offline script in the next section still needs none of
these variables or a running database.

```bash
export APPSURFACE_DURABLE_LOCAL_PORT="${APPSURFACE_DURABLE_LOCAL_PORT:-54329}"
APPSURFACE_DURABLE_PREREQUISITE_PORT="$APPSURFACE_DURABLE_LOCAL_PORT" \
  bash examples/durable-postgresql/check-prerequisites.sh
```

The checked-in script uses Bash's loopback TCP probe, so it fails closed when the selected port is occupied without
requiring optional host PostgreSQL tools.

## Ten-minute PostgreSQL transcript

In Terminal 1, run a disposable database:

```console
docker run --rm --name appsurface-durable-postgres \
  -e POSTGRES_HOST_AUTH_METHOD=trust \
  -e POSTGRES_DB=appsurface_durable_example \
  -p "127.0.0.1:${APPSURFACE_DURABLE_LOCAL_PORT}:5432" postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877
```

In Terminal 2, wait at most 30 seconds before any migration operation:

```console
for attempt in $(seq 1 30); do
  docker exec appsurface-durable-postgres pg_isready -U postgres -d appsurface_durable_example && break
  sleep 1
done
docker exec appsurface-durable-postgres pg_isready -U postgres -d appsurface_durable_example || exit 1
```

Create the local-only roles with the disposable container's bootstrap administrator. This disposable container binds
only to loopback and uses Docker's local `trust` bootstrap mode, so it has no bootstrap password. The password setup
below reads each service password from the terminal without placing it in shell history, writes a mode-0600 temporary
PostgreSQL passfile, and removes it when the terminal exits. Production creates and rotates credentials through its
own secret system. The dispatcher, runtime, and retention-operator roles are explicit restricted login leaves, while the migration owner
receives only the database `CREATE` privilege required to create the package schema:

```console
docker exec appsurface-durable-postgres \
  psql -U postgres -d appsurface_durable_example -v ON_ERROR_STOP=1 \
  -c "CREATE ROLE appsurface_durable_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  -c "CREATE ROLE appsurface_durable_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  -c "CREATE ROLE appsurface_durable_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  -c "CREATE ROLE appsurface_durable_retention LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;" \
  -c "GRANT CREATE ON DATABASE appsurface_durable_example TO appsurface_durable_owner;"

read -r -s -p 'Migration-owner password: ' migration_owner_password; printf '\n'
read -r -s -p 'Dispatcher password: ' dispatcher_password; printf '\n'
read -r -s -p 'Runtime password: ' runtime_password; printf '\n'
read -r -s -p 'Retention-operator password: ' retention_operator_password; printf '\n'
case $- in *x*) appsurface_restore_xtrace=1; set +x ;; esac
umask 077
export APPSURFACE_DURABLE_PASSFILE="$(mktemp)"
trap 'rm -f "$APPSURFACE_DURABLE_PASSFILE"' EXIT
escape_pgpass_field() {
  case "$1" in *$'\n'*|*$'\r'*) return 1 ;; esac
  printf '%s' "$1" | sed 's/[\\:]/\\&/g'
}
migration_owner_passfile_password="$(escape_pgpass_field "$migration_owner_password")" || { printf 'Password cannot contain a newline.\n' >&2; exit 1; }
dispatcher_passfile_password="$(escape_pgpass_field "$dispatcher_password")" || { printf 'Password cannot contain a newline.\n' >&2; exit 1; }
runtime_passfile_password="$(escape_pgpass_field "$runtime_password")" || { printf 'Password cannot contain a newline.\n' >&2; exit 1; }
retention_operator_passfile_password="$(escape_pgpass_field "$retention_operator_password")" || { printf 'Password cannot contain a newline.\n' >&2; exit 1; }
printf '127.0.0.1:%s:appsurface_durable_example:appsurface_durable_owner:%s\n' "$APPSURFACE_DURABLE_LOCAL_PORT" "$migration_owner_passfile_password" > "$APPSURFACE_DURABLE_PASSFILE"
printf '127.0.0.1:%s:appsurface_durable_example:appsurface_durable_dispatcher:%s\n' "$APPSURFACE_DURABLE_LOCAL_PORT" "$dispatcher_passfile_password" >> "$APPSURFACE_DURABLE_PASSFILE"
printf '127.0.0.1:%s:appsurface_durable_example:appsurface_durable_runtime:%s\n' "$APPSURFACE_DURABLE_LOCAL_PORT" "$runtime_passfile_password" >> "$APPSURFACE_DURABLE_PASSFILE"
printf '127.0.0.1:%s:appsurface_durable_example:appsurface_durable_retention:%s\n' "$APPSURFACE_DURABLE_LOCAL_PORT" "$retention_operator_passfile_password" >> "$APPSURFACE_DURABLE_PASSFILE"
printf '%s\n%s\n' "$migration_owner_password" "$migration_owner_password" | \
  docker exec -i appsurface-durable-postgres psql -U postgres -d appsurface_durable_example -c '\password appsurface_durable_owner'
printf '%s\n%s\n' "$dispatcher_password" "$dispatcher_password" | \
  docker exec -i appsurface-durable-postgres psql -U postgres -d appsurface_durable_example -c '\password appsurface_durable_dispatcher'
printf '%s\n%s\n' "$runtime_password" "$runtime_password" | \
  docker exec -i appsurface-durable-postgres psql -U postgres -d appsurface_durable_example -c '\password appsurface_durable_runtime'
printf '%s\n%s\n' "$retention_operator_password" "$retention_operator_password" | \
  docker exec -i appsurface-durable-postgres psql -U postgres -d appsurface_durable_example -c '\password appsurface_durable_retention'
unset migration_owner_password dispatcher_password runtime_password retention_operator_password migration_owner_passfile_password dispatcher_passfile_password runtime_passfile_password retention_operator_passfile_password
if [ "${appsurface_restore_xtrace:-0}" = 1 ]; then set -x; fi
unset appsurface_restore_xtrace
export PGPASSFILE="$APPSURFACE_DURABLE_PASSFILE"
```

The credential block temporarily disables shell xtrace when it was enabled, so password expansions cannot be copied into command logs; its prior tracing state is restored after the password values are unset.

Generate the migration script without configuring or opening a database connection. Review the resulting SQL before application:

```console
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- \
  durable schema script --from-version 0 --output /tmp/appsurface-durable.sql
# Expected: Wrote durable migration script: /tmp/appsurface-durable.sql
```

Set the migration-owner value only in the shell environment, then use guarded CLI apply. The connection string names
the temporary passfile, never a password; `--connection-env` names a variable and never takes a connection string:

```console
export APPSURFACE_DURABLE_MIGRATION_CONNECTION="Host=127.0.0.1;Port=$APPSURFACE_DURABLE_LOCAL_PORT;Database=appsurface_durable_example;Username=appsurface_durable_owner;Passfile=$APPSURFACE_DURABLE_PASSFILE"
  dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- \
  durable schema apply --connection-env APPSURFACE_DURABLE_MIGRATION_CONNECTION --apply
# Expected: Durable schema: 0 -> 9; applied: 0001, 0002, 0003, 0004, 0005, 0006, 0007, 0008, 0009.
```

Apply the reviewed role recipe after migrations with the disposable container's bootstrap administrator, then configure
separate dispatcher and runtime values. This keeps the transcript self-contained: no host PostgreSQL client is required.

```console
docker exec -i appsurface-durable-postgres \
  psql -v ON_ERROR_STOP=1 -U postgres -d appsurface_durable_example \
  -v migration_owner_role=appsurface_durable_owner \
  -v dispatcher_role=appsurface_durable_dispatcher \
  -v runtime_role=appsurface_durable_runtime \
  -v retention_operator_role=appsurface_durable_retention \
  -f - < Durable/configure-postgresql-roles.sql

export APPSURFACE_DURABLE_DISPATCHER_CONNECTION="Host=127.0.0.1;Port=$APPSURFACE_DURABLE_LOCAL_PORT;Database=appsurface_durable_example;Username=appsurface_durable_dispatcher;Passfile=$APPSURFACE_DURABLE_PASSFILE"
export APPSURFACE_DURABLE_RUNTIME_CONNECTION="Host=127.0.0.1;Port=$APPSURFACE_DURABLE_LOCAL_PORT;Database=appsurface_durable_example;Username=appsurface_durable_runtime;Passfile=$APPSURFACE_DURABLE_PASSFILE"
export APPSURFACE_DURABLE_RUNTIME_EPOCH='<stable UUID supplied by deployment>'
```

The development-only bootstrap initializes the active epoch exactly once. It requires `DOTNET_ENVIRONMENT=Development`, `APPSURFACE_DURABLE_LOCAL_PROOF=1`, a `localhost`, `127.0.0.1`, or `::1` target, and the `appsurface_durable_owner` role before it opens the durable schema. It rejects invalid UUID values, inactive schema, and an already active epoch. For proof parity with production defaults, prefer the same 16+ migration floor when choosing local dependencies.

```console
DOTNET_ENVIRONMENT=Development APPSURFACE_DURABLE_LOCAL_PROOF=1 \
  dotnet run --project examples/durable-postgresql -- schema-bootstrap-dev
# Expected: [schema-bootstrap-dev] active epoch initialized
```

Finally, run the bounded proof. It requires the same Development and explicit local-proof confirmation, loopback targets, and the `appsurface_durable_dispatcher` and `appsurface_durable_runtime` roles before it accepts one local Work, Flow, and Work-targeted Schedule; persists W3C Flow trace context; processes one all-surfaces bounded pass; checks health; drains and resumes; then starts and stops `AddWorkerHost()` after one hosted pass while asserting the durable catalog and migration metadata are unchanged:

```console
DOTNET_ENVIRONMENT=Development APPSURFACE_DURABLE_LOCAL_PROOF=1 \
  dotnet run --project examples/durable-postgresql -- verify-local
# Expected named checkpoints include: Work accepted; Flow accepted with W3C trace context;
# Schedule accepted; bounded host pass completed; health and drain checkpoints completed.
```

## Roles and boundaries

| Actor | Configuration | May do | Must not do |
| --- | --- | --- | --- |
| Migration owner | `APPSURFACE_DURABLE_MIGRATION_CONNECTION` | Review/apply migrations, rerun the role recipe, initialize or rotate epochs through the deployment workflow. | Run the hosted worker or application traffic. |
| Dispatcher | `APPSURFACE_DURABLE_DISPATCHER_CONNECTION` | Payload-free discovery and narrow leasing. | Read payloads, apply DDL, or mutate runtime state. |
| Runtime host | `APPSURFACE_DURABLE_RUNTIME_CONNECTION`, `APPSURFACE_DURABLE_RUNTIME_EPOCH` | Run the opted-in worker, bounded passes, health, and drain. | Apply DDL, own package objects, or change the active epoch. |
| Retention operator | Application-authorized PostgreSQL connection | Create manifests, record receipts, verify source correspondence, place/release holds, and purge only through the reviewed retention lifecycle. | Apply DDL, run Work/Flow processing, or access payloads outside the retention boundary. |
| Application operator | Application-owned identity and authorization | Expose deliberately authorized application controls. | Receive generic raw database or Durable CLI access. |

`AddAppSurfaceDurablePostgreSql(...)` registration is passive. Call `.AddWorkerHost()` only in a continuously live worker process after schema, roles, and epoch have been verified. The example starts that host under the restricted tutorial roles, waits for one hosted pass, confirms it leaves the durable catalog and migration metadata unchanged, then stops it immediately.

## Recovery and upgrades

1. Run `appsurface durable schema status --connection-env APPSURFACE_DURABLE_RUNTIME_CONNECTION` and `appsurface durable schema preflight --connection-env APPSURFACE_DURABLE_RUNTIME_CONNECTION` with a scoped read-only deployment connection, such as the runtime connection in this tutorial, to identify the authoritative state. Reserve the migration-owner variable for reviewed apply and epoch operations.
2. Correct role setup or review a forward-only script generated from the installed version.
3. Apply through the explicit migration-owner workflow, rerun the canonical role recipe after migrations, then retry preflight and the local proof.
4. For rollout safety, disable the local host (`.AddWorkerHost` off), complete migration+role recipe reconciliation, verify status/epoch coherence, then re-enable host.

Never delete `appsurface_durable.schema_migration` rows, edit migration checksums, or run destructive down-migrations. A failed migration rolls back its own transaction; regenerate the correct forward script and retry from the last committed version. A runtime epoch rotation is an authorized restore operation documented in the [PostgreSQL package README](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#explicit-schema-and-epoch-deployment), not a tutorial command.
