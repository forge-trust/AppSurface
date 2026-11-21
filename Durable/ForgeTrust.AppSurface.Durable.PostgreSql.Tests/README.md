# PostgreSQL integration fixtures

Most tests request an empty PostgreSQL database, apply the migrations they need and own that database through disposal. The Work definition and Flow reference-workload classes additionally support the [#931 fixture experiment](../../docs/designs/issue-931-durable-postgresql-fixtures.md). Each test that needs a ready store receives a distinct database, store identity and epoch. The Flow codec-rejection test uses an empty database in both modes.

## Select and discover the cohorts

Run from the repository root with the SDK pinned by [global.json](../../global.json). Discovery needs no database.

```sh
project=Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj
work='FullyQualifiedName~ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkDefinitionTests.'
flow='FullyQualifiedName~ForgeTrust.AppSurface.Durable.PostgreSql.Tests.DurableSlice4ReferenceWorkloadTests.'
dotnet restore "$project"
dotnet build "$project" -c Release --no-restore
dotnet test "$project" -c Release --no-build --no-restore --list-tests --filter "$work"
dotnet test "$project" -c Release --no-build --no-restore --list-tests --filter "$flow"
```

Expect four Work and 30 Flow methods. The Flow class is declared in `PostgreSqlDurableFlowStoreTests.cs`. The [frozen inventory](../../artifacts/issue-931-fixture-benchmark/inventory/manifest.json) records the fully qualified names, source locations and setup each method needs.

## Run a cohort

An unset `APPSURFACE_DURABLE_POSTGRES_TEST_FIXTURE` selects fresh per-case setup. The explicit selector values are `fresh` and `template`, lowercase with no whitespace. An explicitly empty value is a configuration error. Existing empty-database callers use the configured server or the repository's process-shared Testcontainers server.

Template mode requires `APPSURFACE_POSTGRES_TEST_CONNECTION` to identify one dedicated, disposable PostgreSQL 16+ server. Supply the connection through your local environment or secret manager. The fixture connects to its `postgres` maintenance database. Its test credential needs the privileges used by the existing migrations, database and clock-role lifecycle, and full visibility during validation, including under forced row-level security. The server owner must exclude concurrent administrative renames, replacement and failover for the duration of a run. Database-level DDL takes a name after the OID check.

Use the same dedicated server for both arms when comparing them. These commands exercise the normal two-worker xUnit setting and coverage collector. Each process needs a new results directory and timing file.

```sh
results=$(mktemp -d)
mkdir "$results/work" "$results/flow"
APPSURFACE_DURABLE_POSTGRES_TEST_FIXTURE=fresh \
  APPSURFACE_DURABLE_FIXTURE_TIMINGS_FILE="$results/work/phases.csv" \
  dotnet test "$project" -c Release --no-build --no-restore --filter "$work" \
    --results-directory "$results/work" --logger trx --collect 'XPlat Code Coverage'
APPSURFACE_DURABLE_POSTGRES_TEST_FIXTURE=template \
  APPSURFACE_DURABLE_FIXTURE_TIMINGS_FILE="$results/flow/phases.csv" \
  dotnet test "$project" -c Release --no-build --no-restore --filter "$flow" \
    --results-directory "$results/flow" --logger trx --collect 'XPlat Code Coverage'
```

A requested timing file must be new and writable. Use an absolute path because the test host can start in the Release output directory. Ordinary runs can leave its setting unset. Timing rows use monotonic milliseconds. Setup covers database creation through readiness, and teardown covers resource disposal and catalog verification. Template preparation and teardown each occur once per candidate host. The benchmark uses elapsed process time from launch through exit as its decision metric.

## Lifecycle and recovery

The [database helper](PostgreSqlIntegrationTestDatabase.cs) registers the generated name, fixed server and database OID after acknowledged creation. The class fixture prepares one migrated, unactivated template over nonpooled connections. A clone must match that template’s original store ID. Under the migration advisory lock, validation checks the actual clone's catalog, migration checksums, singleton metadata and physical row visibility before rekeying. Epoch initialization uses the original case's actor and reason. The helper rechecks ownership before dropping a database and before releasing a clock role or its dependencies.

A failed create with an unknown acknowledgement, or a different OID under a recorded name, leaves the resource for the disposable-server owner to reconcile. Keep the failed run and its diagnostic. Inspect the observed and expected OIDs and any dependent roles on that same server. An incomplete cleanup invalidates a benchmark arm. Use the disposable server’s normal retirement procedure when ownership cannot be established.

If an owned clone’s drop fails, its case keeps the fixture lease. Once the failed attempt has finished and ownership is confirmed, retry disposal on the same case handle. Successful cleanup releases the lease and lets fixture disposal remove the template.

## Compare baseline and candidate

The [five-pair measurement](../../artifacts/issue-931-fixture-benchmark/results.md) produced a measured no-go. The median improvement missed the variability threshold. Fresh is the supported default for these cohorts.

Fresh creation inherits `template1`. A ready candidate uses `CREATE DATABASE … TEMPLATE` with the class-owned source. Both statements omit `STRATEGY`. On PostgreSQL 17 this selects `WAL_LOG`, which copies blocks through the write-ahead log. The template must have no connected sessions when cloning begins. [PostgreSQL 17 CREATE DATABASE reference](https://www.postgresql.org/docs/17/sql-createdatabase.html)

The pinned fresh implementation performs one create, schema apply, epoch initialization, status read and disposal for each of the 33 ready cases. The empty Flow case only creates and disposes its database. Both measured arms now pay for recording the new database OID, checking name and OID before drop, and confirming absence after drop. Clock writes check the connected database identity. When a case installs a clock role, both arms also capture the clock function and role OIDs in the creating transaction and verify cleanup. Catalog round trips and result-file emission are included in wall time.

Each candidate class adds one template migration, quiescence check and teardown. Across the two classes, each of the 33 ready cases clones a database, verifies source and clone identity, checks the canonical migrations and each table, rekeys, initializes the epoch and rereads status. The empty case bypasses cloning. Use one warmup in each mode followed by five alternating pairs, with Work then Flow within each arm. A missing or duplicate method, failed process, malformed coverage or incomplete cleanup invalidates that attempt. Retain invalid attempts separately from valid comparisons.

For each pair, gain is 100 × (fresh seconds − template seconds) / fresh seconds. The median gain must reach 5% and exceed twice the median absolute deviation of those five gains. A cohort slowdown of at least 5% in three or more pairs vetoes adoption. Isolation and cleanup checks must also pass. Record absolute seconds, both cohort veto counts and links to process evidence.

The [paired runner](../../scripts/postgresql-fixture-benchmark.py) checks the manifest against test discovery, records the Release assembly hash and creates an index for the 24 processes. Build once before starting it. Prepare a new scratch directory and collect the server version and selected settings from the dedicated measurement server. Configure `psql` through your local PostgreSQL service or environment so the command connects to that server.

```sh
scratch=$(mktemp -d)
psql -XAt --dbname postgres --set ON_ERROR_STOP=1 > "$scratch/server.json" <<'SQL'
SELECT json_build_object(
    'version', split_part(current_setting('server_version'), ' ', 1),
    'settings', (SELECT json_object_agg(name, current_setting(name))
                 FROM pg_settings
                 WHERE name IN ('autovacuum', 'checkpoint_timeout', 'fsync',
                                'full_page_writes', 'max_connections', 'max_wal_size',
                                'shared_buffers', 'synchronous_commit', 'wal_level', 'work_mem')));
SQL
python3 scripts/postgresql-fixture-benchmark.py validate \
  --dotnet "$(command -v dotnet)" --server-info "$scratch/server.json"
```

Run the fixture acceptance tests on a disposable server before measuring. The runner's `acceptance-filter` command selects the lifecycle, clone validation, configuration, timing, metadata and existing database-helper tests. The runner discovers these cases in the measured Release assembly and requires one passing result for every discovered case, including each theory row. Give this run its own results directory. Point `APPSURFACE_POSTGRES_TEST_CONNECTION` at the dedicated measurement server before starting the benchmark and leave that server idle for the duration.

Limit that connection override to the experiment commands. For [repository-wide validation](../../CONTRIBUTING.md#local-verification), the default Testcontainers server provides the `appsurface` login and TCP connection used by the Schedule and idle-session tests.

```sh
acceptance=$(python3 scripts/postgresql-fixture-benchmark.py acceptance-filter)
dotnet test "$project" -c Release --no-build --no-restore --filter "$acceptance" \
  --results-directory "$scratch/acceptance" --logger 'trx;LogFileName=acceptance.trx'
python3 scripts/postgresql-fixture-benchmark.py run \
  --dotnet "$(command -v dotnet)" --server-info "$scratch/server.json" \
  --acceptance-trx "$scratch/acceptance/acceptance.trx" --results-root "$scratch/pairs"
```

The acceptance TRX must include the server metadata test, which records the SHA-256 of its executing test assembly. The runner compares that hash with the Release assembly it measures. A separate, untimed invocation of the metadata test verifies the supplied settings against the measurement server before warmup. Read `summary.md` for the decision and `index.md` for the acceptance, server and process records. The runner refuses an existing results root. Preserve an invalid attempt and use a new directory for a repeat. Raw TRX, collector output and SDK information can contain local filesystem paths. Inspect the export for private machine or connection details before sharing it.

## Test-project entry points

| Entry point | Use |
|---|---|
| `PostgreSqlIntegrationTestDatabase.TryCreateAsync()` | Obtain an empty disposable database. Migration, upgrade and bootstrap tests start here. |
| `PostgreSqlCohortFixture<TCohort>.CreateReadyDatabaseAsync(actor, reason)` | Obtain a case-owned database, verified status and its new runtime epoch. Dispose the returned case handle after the test. |
| `CreateEmptyDatabaseAsync()` on the cohort fixture | Measure a case whose behavior requires an empty database in either mode. |
| `CreateOwnedAsync`, `PrepareTemplateAsync`, `ActivateCloneAsync` on the internal database helper | Create a registered template or clone, prepare a virgin template and activate one verified clone. These are fixture implementation and fault-test entry points. |

The [class fixture](PostgreSqlCohortFixture.cs) owns each template and waits for active cases when closing. The [schema guard](PostgreSqlIntegrationTestDatabase.CloneSchema.cs) serializes initialization with disposal. Caller cancellation before create dispatch creates no database. After dispatch the helper finishes the bounded ownership check before observing caller cancellation. The create/identity window and a subsequent cleanup attempt each have a separate 60-second deadline.
