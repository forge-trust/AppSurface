# Test plan: Durable runtime doctor (#801)

The [command reference](../../Durable/runtime-doctor.md) defines the shipped contract. This plan maps the required proof surfaces to their runners; it does not replace execution receipts or the repository coverage gate. There are no browser pages or routes. All database probes use owned disposable PostgreSQL fixtures and synthetic credentials.

## User paths and proof levels

```text
Raw argv
  +-- invalid/ambiguous --> one safe ASDUR413 result, exit 3, no database I/O
  +-- help --> command reference, exit 0, no environment resolution
  +-- valid selected environment names --> resolve once, validate exact durations
        --> owned physical session --> shared migration fence --> read-only snapshot
              +-- unsafe credential --> ASDUR408; dependent checks not checked
              +-- incompatible schema --> schema diagnosis; dependent checks not checked
              +-- compatible --> retention + independent store epoch + optional worker
        --> checked rollback/unlock/physical release --> classification --> one result
              +-- findings --> exit 2; selected-name retry or schema-status action
              +-- unavailable --> exit 4; partial success facts discarded
              +-- canceled/unexpected --> exit 1; fixed secret-free diagnosis
              +-- passed --> exit 0; application-verifier handoff
```

The [single 28-row matrix](../../Cli/ForgeTrust.AppSurface.Cli.Tests/Fixtures/durable-doctor-v1.json) owns check-state, finding order, exit and action expectations. [Pure classification tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorClassificationTests.cs) verify exact tick and future-time boundaries. [PostgreSQL/CLI tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorIntegrationTests.cs) exercise the reached database paths. [Installed-tool tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorInstalledToolTests.cs) pack, install and execute the exact candidate in an isolated feed/cache/home; a source run alone does not satisfy distribution proof. Live-clock cases use safe margins, with equality and one-tick comparisons proved by fixed observations.

## Required families

| Family | Runner and required evidence |
| --- | --- |
| Input and environment selection | [Input tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorInputTests.cs): paired options, selected-name replacement, missing/blank values, exact invariant decimal duration bounds, malformed/raw argv, help, no invalid-input service invocation, process-global state restoration. |
| Classification and precedence | [Classification tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorClassificationTests.cs): all matrix rows, store-level epoch without a worker, epoch prerequisite suppression, draining precedence, exact threshold, future heartbeat, combined findings, malformed/null/bounded immutable facts. |
| Output and secret custody | [Renderer tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorRendererTests.cs) and input/installed tests: exact v1 order/null shapes, text/JSON agreement, culture independence, canonical selected-name argv, 32 KiB bound, one output/newline, no connection/exception/argv sentinel in either stream, failed writer exits 1 without retry. |
| Database interpretation and nonmutation | [Integration tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorIntegrationTests.cs) use [the shared fixture](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorFixture.cs): credential/schema short circuits, one captured database timestamp, same StoreId/epoch snapshot, worker row facts, state/catalog/sequence fingerprints before and after, pruning trap never invoked. |
| Credential and retention structure | [Catalog tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorCatalogTests.cs): fixed projection shape, each role restriction, both membership directions, ownership/grant options, heartbeat DELETE/TRUNCATE, exact routine/ACL/configuration and index mutations, caller search-path independence. |
| Complete preflight regression | [Existing preflight integration tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurablePreflightIntegrationTests.cs): complete reviewed role manifest, independent migration-owner identity, unexpected grantees and reassigned owners remain separate deployment authority. |
| Bounded authoritative schema reader | [Status transaction tests](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlStatusTransactionTests.cs): zero/exact-limit/65th history row, oversized name/hash server guards, unknown canonical entries and unchanged public status/preflight behavior. |
| Timeout, cancellation and resource release | [Lifetime tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorLifetimeTests.cs), [concurrency tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorConcurrencyTests.cs) and [failure-classifier tests](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlDurableFailureClassifierTests.cs): fake monotonic time at each stage, real blocked handshake/fence/query, shrinking total/cleanup budgets, original caller versus provider/deadline cancellation, both completion orders, no late owned work, release of backend and fence before output. |
| Exact candidate package | [Installed-tool test](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorInstalledToolTests.cs): coordinated source closure, isolated restore/pack/install, exact package identity/bytes and dependency closure, protected source/lock hashes unchanged, matrix through child stdout/stderr/exits, deterministic interrupt and nonmutation. |
| Diagnostic compatibility and public API | [Catalog tests](../../Durable/ForgeTrust.AppSurface.Durable.Tests/DurableDiagnosticCatalogTests.cs), runtime health and external-activation suites: append-only codes, immutable descriptors, existing guidance and diagnostic anchors, predicates and caller correlation preserved. [Packed consumers](../../Durable/verify-packed-consumers.sh) compile the public contracts. |
| Documentation, automation and operator interpretation | Renderer sample contract, [JSON consumer](../../examples/durable-external-activation/consume-doctor-report.py) and [consumer regressions](../../examples/durable-external-activation/test_consume_doctor_report.py), Markdown snippets, package-index validation and [operator comparison](../../Durable/evidence/executable-contract-adoption.md#runtime-doctor-operator-comparison): required clean-report checks, selected-name epoch/schema recovery on the same store, actual output interpretation, measured existing-host intervals, cold-host and adopter-comprehension limits stated. |

## Final commands

Run the CLI test project and affected Durable contract/provider/PostgreSQL projects with `dotnet test`; run `./Durable/verify-packed-consumers.sh`; run the documented package-index, Markdown snippet and generated Web asset checks. Format affected C# source with `dotnet format` and run `git diff --check`.

Run the report-consumer regressions with `python3 -m unittest discover -s examples/durable-external-activation -p 'test_*.py'`. A clean report must have all four store/runtime checks requested and passed; the optional worker must either be unrequested or pass.

The hard final gate is the unchanged `./scripts/coverage-solution.sh`: 95% line and 85% branch coverage for both the aggregate and the Codecov patch against `origin/main`. A build, test or coverage failure is work remaining. Run on the final merged candidate, and rerun after changes to production code, tests or build inputs.
