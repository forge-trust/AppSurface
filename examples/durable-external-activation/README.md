# Durable external activation host

This executable .NET 10 sample composes AppSurface Durable's provider-neutral
[`IDurableExternalActivationService`](../../Durable/external-activation-v1.md) into a passive authenticated HTTP wake
host. One authorized empty request performs a bounded health observation and, when eligible, one authoritative
provider admission attempt. The host does not start a worker loop or select Work from the wake payload; typed Work is
accepted separately through the sample CLI.

Use the [canonical external activation v1 reference](../../Durable/external-activation-v1.md) for the public API,
health validation, all ten service outcomes, cancellation and budget behavior, recovery, and telemetry contract. The
[PostgreSQL example](../durable-postgresql/README.md) explains schema and role setup. The approved sample keeps typed
Work acceptance out of the empty wake request and uses the same reviewed PostgreSQL boundaries.

Use the [runtime doctor](../../Durable/runtime-doctor.md) after this reference's reviewed schema/role/epoch setup to diagnose the connected store. Its [automation consumer](https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/consume-doctor-report.py) demonstrates version/exit/check validation, including all four required store/runtime checks before accepting a clean report. Run its [consumer regressions](https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/test_consume_doctor_report.py) with `python3 -m unittest discover -s examples/durable-external-activation -p 'test_*.py'` from the repository root. The reference keeps the provider's 15-second stale default without an override; deployed applications must supply their own effective host threshold. Doctor's clean result still hands off to this application's composition verification.

## HTTP contract

The approved reference-host contract exposes four HTTP endpoints:

| Endpoint | Policy | Purpose and response |
|---|---|---|
| `GET /live` | Public | Process liveness only; returns HTTP 200 with `{"status":"Live"}`. It does not query PostgreSQL. |
| `GET /compatibility` | Public | Returns an `Assessment` with the observed health state, `canEnableActivation`, `isReady`, and nullable `problemCode`. HTTP 200 means activation is compatible; otherwise HTTP 503. |
| `GET /ready` | Public | Returns the same assessment shape. HTTP 200 means Durable is ready; otherwise HTTP 503. |
| `POST /private/durable/activate` | Authenticated and authorized | Requires an empty body, then invokes one external activation request. It returns the bounded outcome, observed state, optional problem code, and aggregate only for `Completed`. |

Authentication and authorization run before activation handling. An unauthenticated private request returns a
bodyless 401 with `WWW-Authenticate: Bearer`; an authenticated principal without the required `durable-activation`
permission gets a bodyless 403. A nonempty wake body receives HTTP 400 with `{"error":"WakeBodyMustBeEmpty"}` and does
not call health or admission.

The endpoint always requires an authenticated principal in addition to the named `DurableActivation` policy. A
production host supplies its own scheme and permission requirements; a permissive named policy cannot remove the
endpoint's authentication requirement.

Each probe response uses these exact camel-case fields: `outcome`, `observedHealthState`, `canEnableActivation`,
`isReady`, and `problemCode`. An assessment supplies the observed values; `ProbeFailed` has null assessment fields and
`problemCode:"ASDUR407"` with HTTP 503; `ProbeCanceled` has null assessment fields and a null problem code with HTTP
408. Neither response invents an assessment. `GET /live` returns HTTP 200 with `{"status":"Live"}` and performs no
Durable health read.

An activation response uses `outcome`, `observedHealthState`, `problemCode`, and `pumpResult`, including explicit nulls.
`pumpResult` is null except for `Completed`, where it contains `discovered`, `claimed`, `processed`, `deferred`,
`failed`, `hasMore`, nullable `nextDueAtUtc` (UTC round-trip timestamp), and `elapsedTicks`. The bounded
`HostFailure` error with HTTP 500 is reserved for an exception outside a returned service outcome; a transport
disconnect does not produce a fabricated activation result. See the canonical
[operator and response table](../../Durable/external-activation-v1.md#operator-actions-and-recovery) for the ten
activation result mappings and their HTTP statuses.

Before the first runtime heartbeat, a compatible provider may report `NotStarted` with `ASDUR404`. The host preserves
that initial assessment: `/compatibility` can return 200 while `/ready` returns 503. The activation service accepts
the observation and omits `ASDUR404` from an activation result unless the observed state is `Stale`; see the
[health validation rules](../../Durable/external-activation-v1.md#health-observation-and-admission).

## Configuration contract

The sample is a .NET 10 ASP.NET Core application and defaults to the loopback URL `http://127.0.0.1:5080`. Its
development-only bearer token is configured with `DurableActivation:DevelopmentBearerToken`, which maps to the
environment variable `DurableActivation__DevelopmentBearerToken`. Development authentication is not a production
identity system; non-Development startup must supply host-owned authentication and authorization policy composition.

The activation limits are host-owned settings:

| Configuration key | Default | Meaning |
|---|---:|---|
| `DurableActivation:PumpMaximumItems` | 32 | Maximum Work items claimed in one pass. |
| `DurableActivation:PumpDiscoveryBudgetSeconds` | 2 | Pump discovery budget, independent of the service request budget. |
| `DurableActivation:RequestBudgetSeconds` | 10 | Cooperative budget for service setup, health observation, and the activation attempt. |

Configure ingress and request-body read deadlines before exposing this endpoint. Unknown-length bodies require one
byte of input or end-of-stream before the host can establish an empty wake; a stalled sender has not entered the
activation service and its request budget has not started. Request cancellation interrupts this bounded read.
Transport deadlines must also leave room for the provider's execution and independent terminal bookkeeping bounds;
see [deployment limits](../../Durable/external-activation-v1.md#deployment-limits).

The PostgreSQL sample reads distinct migration-owner, dispatcher, and runtime connection settings from
`APPSURFACE_DURABLE_MIGRATION_CONNECTION`, `APPSURFACE_DURABLE_DISPATCHER_CONNECTION`, and
`APPSURFACE_DURABLE_RUNTIME_CONNECTION`; its explicitly provisioned active runtime epoch comes from
`APPSURFACE_DURABLE_RUNTIME_EPOCH`. Schema and role provisioning remain explicit operator actions. Startup verifies the
installed schema and epoch and does not apply migrations or grants.

## Executable local first start

This walkthrough creates a disposable PostgreSQL 16.5 database, applies schema version 11 explicitly, configures the
canonical four-role boundary, accepts one typed Work item, then wakes the passive host and verifies the persisted
result. Run the commands from the repository root in one Bash session so exported configuration and the receipt
remain available to later steps. The walkthrough uses only loopback ports and Docker's `trust` authentication for
this disposable container; do not use that authentication mode for a shared or production database.

### Prerequisites

- .NET 10 SDK.
- Docker Engine or Docker Desktop with Linux containers and permission to pull the pinned PostgreSQL image below.
- Bash, `curl`, `jq`, `openssl`, and `uuidgen`.
- Free loopback ports `54329` and `5080`. Set `APPSURFACE_DURABLE_LOCAL_PORT` before the shell block if 54329 is occupied.
- For an end-to-end network-export measurement, an OTLP collector and reachable endpoint; this walkthrough does not
  start a collector. Configure `OTEL_EXPORTER_OTLP_ENDPOINT` or `AppSurfaceObservability__OtlpEndpoint`, and include
  collector setup and startup in the first-start clock if no collector is already available. The PostgreSQL lifecycle
  test uses an in-process SDK test exporter for its deterministic export assertion.

The four restricted PostgreSQL login roles are the migration owner, Work dispatcher, runtime, and retention operator.
The migration owner receives database `CREATE` for schema installation. The dispatcher/runtime pair uses the explicit
`work_only` profile. Every role is created without superuser, database-creation, role-creation, replication, or RLS
bypass privileges; the dispatcher and runtime are not members of any other role. The canonical role recipe applies
and verifies the Durable grants after the schema is installed.

### Start PostgreSQL and create the four roles

The database is a fresh local-only container. Pull the pinned image, start it bound to loopback, and wait for its final
server to accept a TCP query before provisioning roles:

```bash
set -euo pipefail

PG_CONTAINER="appsurface-durable-activation-$(openssl rand -hex 8)"
PG_CONTAINER_CREATED=0
POSTGRES_PORT="${APPSURFACE_DURABLE_LOCAL_PORT:-54329}"
POSTGRES_IMAGE='postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877'
HOST_PROJECT='examples/durable-external-activation/DurableExternalActivationExample.csproj'
HOST_DLL='examples/durable-external-activation/bin/Release/net10.0/DurableExternalActivationExample.dll'
BASE_URL='http://127.0.0.1:5080'
HOST_LOG="$(mktemp)"
RECEIPT_FILE="$(mktemp)"
HOST_PID=''

cleanup() {
  if [[ -n "${HOST_PID:-}" ]] && kill -0 "$HOST_PID" 2>/dev/null; then
    kill -TERM "$HOST_PID" 2>/dev/null || true
    wait "$HOST_PID" 2>/dev/null || true
  fi
  if [[ "$PG_CONTAINER_CREATED" == 1 ]]; then
    docker stop "$PG_CONTAINER" >/dev/null 2>&1 || true
  fi
  rm -f "$HOST_LOG" "$RECEIPT_FILE"
}
trap cleanup EXIT

dotnet --version
docker info >/dev/null
docker pull "$POSTGRES_IMAGE"
docker run --rm --detach --name "$PG_CONTAINER" \
  -e POSTGRES_HOST_AUTH_METHOD=trust \
  -e POSTGRES_DB=appsurface_durable_activation \
  -p "127.0.0.1:${POSTGRES_PORT}:5432" \
  "$POSTGRES_IMAGE"
PG_CONTAINER_CREATED=1

postgres_ready=0
for attempt in {1..30}; do
  if docker exec "$PG_CONTAINER" psql -X -v ON_ERROR_STOP=1 -h 127.0.0.1 \
      -U postgres -d appsurface_durable_activation -c 'SELECT 1;' >/dev/null 2>&1; then
    postgres_ready=1
    break
  fi
  sleep 1
done
if [[ "$postgres_ready" != 1 ]]; then
  docker logs "$PG_CONTAINER"
  exit 1
fi

docker exec "$PG_CONTAINER" psql -X -v ON_ERROR_STOP=1 -U postgres \
  -d appsurface_durable_activation \
  -c 'CREATE ROLE appsurface_activation_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;' \
  -c 'CREATE ROLE appsurface_activation_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;' \
  -c 'CREATE ROLE appsurface_activation_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;' \
  -c 'CREATE ROLE appsurface_activation_retention LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;' \
  -c 'GRANT CREATE ON DATABASE appsurface_durable_activation TO appsurface_activation_owner;'
```

`docker run` creates the empty database. The roles are local development identities without passwords because this
disposable container is loopback-only and uses `trust`; the host still connects through the distinct migration,
dispatcher, and runtime identities. The recipe also requires the separate restricted retention-operator role.

### Restore, build, and configure the host

Restore and build the source host once. The sample's package lock is used in locked mode:

```bash
dotnet restore "$HOST_PROJECT" --locked-mode
dotnet build "$HOST_PROJECT" --configuration Release --no-restore \
  -m:1 -p:UseSharedCompilation=false --nologo
```

Set the connection strings and development-only token in the current shell. No password or token is printed or
checked into the repository. The runtime epoch is generated once for this disposable database and is then used by
schema bootstrap, typed acceptance, and the host:

```bash
export DOTNET_ENVIRONMENT=Development
export APPSURFACE_DURABLE_MIGRATION_CONNECTION="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=appsurface_durable_activation;Username=appsurface_activation_owner"
export APPSURFACE_DURABLE_DISPATCHER_CONNECTION="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=appsurface_durable_activation;Username=appsurface_activation_dispatcher"
export APPSURFACE_DURABLE_RUNTIME_CONNECTION="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=appsurface_durable_activation;Username=appsurface_activation_runtime"
export APPSURFACE_DURABLE_RUNTIME_EPOCH="$(uuidgen | tr '[:upper:]' '[:lower:]')"
export DurableActivation__DevelopmentBearerToken="$(openssl rand -hex 32)"
export ASPNETCORE_URLS="$BASE_URL"
```

The application does not apply DDL or grants at startup. Run the Development-only migration CLI first, then pass the
canonical role recipe the single dispatcher/runtime pair and explicit `work_only` profile. The recipe is invoked as
the disposable container administrator; its input is the checked-in source at
[`Durable/configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/ecd81417065982d1803d26000fc7618a7c06108e/Durable/configure-postgresql-roles.sql), not an ad-hoc grant script.

```bash
dotnet "$HOST_DLL" schema-apply-dev
# Expected: [schema-apply-dev] schema compatible at version 11

docker exec -i "$PG_CONTAINER" psql -X -v ON_ERROR_STOP=1 -U postgres \
  -d appsurface_durable_activation \
  -v migration_owner_role=appsurface_activation_owner \
  -v retention_operator_role=appsurface_activation_retention \
  -v 'role_pairs_json={"version":1,"pairs":[{"dispatcher":"appsurface_activation_dispatcher","runtime":"appsurface_activation_runtime","dispatcher_profile":"work_only"}]}' \
  -f - < Durable/configure-postgresql-roles.sql
# Expected: psql exits successfully after the canonical recipe verifies and commits the grants.

dotnet "$HOST_DLL" epoch-bootstrap-dev
# Expected: [epoch-bootstrap-dev] active epoch initialized
```

The schema and epoch commands use only the migration-owner connection. The acceptance CLI then writes one typed
`DemoWork` item in the fixed `external-activation-demo` scope before any wake is sent:

```bash
dotnet "$HOST_DLL" accept-demo-work --value first-start > "$RECEIPT_FILE"
jq . "$RECEIPT_FILE"
WORK_ID="$(jq -er '.WorkId' "$RECEIPT_FILE")"
```

The CLI receipt uses PascalCase JSON names. Its shape is shown below; Work and command IDs plus the UTC timestamp are
generated for each run, so retain the actual receipt file and use its `WorkId` for inspection:

```json
{
  "Scope": "external-activation-demo",
  "WorkId": "<generated-work-id>",
  "CommandId": "<generated-command-id>",
  "Kind": "Accepted",
  "Revision": 1,
  "AcceptedAtUtc": "<generated-UTC-timestamp>"
}
```

### Start the passive host and prove the first activation

Start the host as a background process in this same shell so the configured token and connection strings remain
available to the `curl` and inspection commands below. It listens on loopback port 5080 by default and prints a
startup line after its schema, epoch, service graph, and authorization policy checks succeed:

```bash
dotnet "$HOST_DLL" serve >"$HOST_LOG" 2>&1 &
HOST_PID=$!

host_ready=0
for attempt in {1..30}; do
  if curl --fail --silent --show-error "$BASE_URL/live" >/dev/null; then
    host_ready=1
    break
  fi
  sleep 1
done
if [[ "$host_ready" != 1 ]]; then
  cat "$HOST_LOG"
  exit 1
fi
cat "$HOST_LOG"
# Expected: [serve] host ready at http://127.0.0.1:5080; /live /compatibility /ready
```

The first two Durable probes distinguish activation compatibility from worker readiness. Before a runtime heartbeat,
the provider's `NotStarted` assessment may carry `ASDUR404`. Compatibility is true, but readiness remains false:

```bash
curl --silent --show-error --include "$BASE_URL/live"
curl --silent --show-error --include "$BASE_URL/compatibility"
curl --silent --show-error --include "$BASE_URL/ready"
```

Expected probe bodies (the first line shown for each request is its HTTP status):

```http
HTTP/1.1 200 OK
{"status":"Live"}
```

```http
HTTP/1.1 200 OK
{"outcome":"Assessment","observedHealthState":"NotStarted","canEnableActivation":true,"isReady":false,"problemCode":"ASDUR404"}
```

```http
HTTP/1.1 503 Service Unavailable
{"outcome":"Assessment","observedHealthState":"NotStarted","canEnableActivation":true,"isReady":false,"problemCode":"ASDUR404"}
```

Send an authenticated POST with a zero-byte body. The wake does not carry Work data and does not identify the accepted
item; the retained CLI receipt and persisted inspection prove which Work reached a terminal state:

```bash
curl --silent --show-error --include --request POST \
  --header "Authorization: Bearer ${DurableActivation__DevelopmentBearerToken}" \
  --header 'Content-Length: 0' \
  "$BASE_URL/private/durable/activate"
```

For this fresh database with one accepted item, expect HTTP 200 and a `Completed` result that retains the initial
`NotStarted` observation but omits its health-only `ASDUR404` code. The real-database lifecycle test currently asserts
`claimed` and `processed` are 1 and `failed` is 0. It does not pin `discovered`, `deferred`, `hasMore`,
`nextDueAtUtc`, or `elapsedTicks`; copy the actual returned aggregate. The unpinned values shown below are
illustrative, and `elapsedTicks` varies with each run rather than promising a duration:

```json
{
  "outcome": "Completed",
  "observedHealthState": "NotStarted",
  "problemCode": null,
  "pumpResult": {
    "discovered": 1,
    "claimed": 1,
    "processed": 1,
    "deferred": 0,
    "failed": 0,
    "hasMore": false,
    "nextDueAtUtc": null,
    "elapsedTicks": 123456
  }
}
```

Inspect the exact accepted Work ID from the saved receipt and then read readiness again:

```bash
dotnet "$HOST_DLL" inspect-demo-work --scope external-activation-demo --work-id "$WORK_ID"
curl --silent --show-error --include "$BASE_URL/ready"
```

The CLI inspection uses PascalCase names. Work ID, revision, and timestamps vary; expect `State` `Succeeded`,
`AttemptNumber` `1`, a non-null `TerminalAtUtc`, and `TerminalCode` `completed`. The final readiness probe returns HTTP 200
with `observedHealthState` `Healthy` and `isReady` true:

```json
{
  "Scope": "external-activation-demo",
  "WorkId": "<same-generated-work-id>",
  "State": "Succeeded",
  "AttemptNumber": 1,
  "Revision": 3,
  "AcceptedAtUtc": "<acceptance-UTC-timestamp>",
  "UpdatedAtUtc": "<generated-UTC-timestamp>",
  "TerminalAtUtc": "<generated-UTC-timestamp>",
  "TerminalCode": "completed"
}
```

```http
HTTP/1.1 200 OK
{"outcome":"Assessment","observedHealthState":"Healthy","canEnableActivation":true,"isReady":true,"problemCode":null}
```

The example never maps typed acceptance to an HTTP route: `/demo/work` is not part of this host. Keep the acceptance
receipt separate from activation telemetry and inspect persisted Work plus application-owned effect evidence before
deciding whether recovery or retry is safe. Stop the local host and disposable database when finished:

```bash
cleanup
trap - EXIT
```

### Activation telemetry

The host registers AppSurface's canonical activation activity source. The default observability mode exports only when
an OTLP collector endpoint is configured; set `OTEL_EXPORTER_OTLP_ENDPOINT` to the endpoint of a collector you already
run, or configure `AppSurfaceObservability__OtlpEndpoint`. Listening to the source or seeing a successful HTTP response
does not prove export. For the exact `AlwaysOnSampler`, SDK test-exporter, authorized endpoint, and successful
`ForceFlush` proof, use the canonical
[`activation activity export recipe`](../../Durable/external-activation-v1.md#activation-activity-export-proof). The
PostgreSQL lifecycle test exercises the real authorized endpoint and asserts the exporter receipt, bounded tags, and
flush result.

The five-minute target is aspirational and unmeasured; see [#806](https://github.com/forge-trust/AppSurface/issues/806)
for measurement ownership. The complete first-start clock includes reading this guide, restoring/building, a cold
PostgreSQL image pull, role/schema/epoch provisioning, token and connection setup, Work acceptance, host start/probes,
authorized wake, terminal inspection, and exporter evidence. .NET 10 and Docker are declared starting prerequisites.
If no OTLP collector is already available, collector provisioning and startup belong in the clock too. This walkthrough
reports no elapsed setup time and does not time only the final curl commands; a cold pull, restore, or build may exceed
five minutes. The host and exact companion test project are [`DurableExternalActivationExample.csproj`](./DurableExternalActivationExample.csproj)
and
[`ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj`](../durable-external-activation.tests/ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj).

---

[← Examples](../README.md) · [Durable activation reference](../../Durable/external-activation-v1.md)
