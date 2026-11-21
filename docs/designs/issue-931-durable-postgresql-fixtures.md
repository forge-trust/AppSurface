# Durable PostgreSQL fixture experiment

**Outcome, 21 November 2026.** Keep fresh setup as the default. The original five-pair sample gained 8.16% against a required 8.98% variability threshold. A later verification gained 4.90%. After cleanup and acceptance fixes, the final build passed all 73 fixture acceptance cases and gained 8.43% in its own five pairs. All three runs used the same 34 Work and Flow cases. [Measured results and evidence archives](../../artifacts/issue-931-fixture-benchmark/results.md) · [Contributor commands](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/README.md)

## Historical planning record

The plan, review notes and task list below record what was known at approval. References to pending measurements or tests describe that point in time.

### URGENT, outside this plan

None.

## Implementation plan
### Template-backed PostgreSQL fixtures for Durable tests

**Approved for implementation on 21 November 2026** · Internal engineering · AppSurface at `3f3126a5`

The owner approved Approach B and accepted the unavailable final spec review. Adoption remains open until the isolation, cleanup, and paired timing evidence below is available.

## Decision and scope

Choose **Approach B**. The internal PostgreSQL test database helper owns creation, identity registration, and checked disposal. Each of the two measured xUnit classes owns one asynchronously disposed fixture that asks the helper for its template. This puts the deletion decision in one place. Approach A put the same ownership rules in a cohort-specific adapter and would have required a separate disposal path. The experiment ships as test source in the repository. A supported helper or further rollout requires a separate decision. [Issue #931](https://github.com/forge-trust/AppSurface/issues/931) · [Existing database helper](https://github.com/forge-trust/AppSurface/blob/3f3126a5febff2cb94847bf8a1092b5d61657a18/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlIntegrationTestDatabase.cs)

### Provisioning contract

| Caller | Database it receives |
|---|---|
| Existing empty-database callers, including migration and upgrade tests | A new empty database through the existing entry points. Preserve configured-server and Testcontainers behavior. |
| Work and Flow cases that need a ready store | An owned database and verified store ID and epoch. Preserve each case’s actor, reason, roles, and rows. Unset or `fresh` uses creation and migrations per case. `template` uses a clone. |
| Flow’s `FlowClient_StartRejectsANonAuthoritativeContextCodec` | An empty database in both modes. Its runtime and outcome count in both arms. |

Only the two cohort fixtures read `APPSURFACE_DURABLE_POSTGRES_TEST_FIXTURE`. Reject values other than `fresh` or `template` when set. Benchmark both modes on the same dedicated disposable server via `APPSURFACE_POSTGRES_TEST_CONNECTION`. Template mode requires this setting and surfaces setup failures directly.

## From creation to first use

1. From the `postgres` maintenance database, create a uniquely named template. Apply the existing migrations over nonpooled connections. Record the template’s store ID with its epoch unset. Close every template connection before cloning. [PostgreSQL template rules](https://www.postgresql.org/docs/16/sql-createdatabase.html)
2. For each owned database, record the intended server and generated name before sending create. After acknowledged creation, capture the catalog OID and register the tuple before honoring caller cancellation. Bound this interval independently. Use one fixed server and fixture-exclusive generated names throughout the lifecycle. The private registry distinguishes templates from clones, serializes initialization and disposal per entry, and rejects operations on the wrong kind of entry. [Database identity in PostgreSQL](https://www.postgresql.org/docs/16/catalog-pg-database.html)
3. On the clone, open a transaction and take the existing Durable migration advisory lock in exclusive transaction mode. Verify the connected database matches the registry entry. Use `ReadStatusInTransactionAsync` to verify canonical migration checksums and current compatible version. Require exactly one metadata row with the copied template store ID and null epoch. Set transaction-local row_security = off and reject insufficient visibility. Include a planted hidden row in the acceptance tests. Enumerate every ordinary and partitioned table in `appsurface_durable`; all except `schema_migration` and `store_metadata` must be empty, including epoch history and provider state. [Transactional reader](https://github.com/forge-trust/AppSurface/blob/3f3126a5febff2cb94847bf8a1092b5d61657a18/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs#L170-L202)
4. In that transaction, set `store_id = DEFAULT` on the singleton row conditional on the expected template ID and null epoch. Require one returned, nonempty ID distinct from the template’s. Commit and release the lock. Call `InitializeRuntimeEpochAsync` with a fresh epoch, then read back both identities before exposing the clone. A rejected precondition rolls back. A failure after rekeying sends the owned clone to cleanup. [Identity default](https://github.com/forge-trust/AppSurface/blob/3f3126a5febff2cb94847bf8a1092b5d61657a18/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/Migrations/0001_work_shared.sql#L11-L26)

## Cancellation and cleanup

Dispose case data sources and use a fresh bounded cleanup token. From maintenance, recheck the registered server, name, and OID before dropping an owned database. Scope role and dependency cleanup to recorded case-owned resources. Preserve the setup failure and report any cleanup failure alongside it. Dispose the template after its clones. Confirm absence on the configured server. Catalog checks followed by a name-based drop rely on the dedicated server’s fixture-exclusive names throughout that operation. [PostgreSQL drop rules](https://www.postgresql.org/docs/16/sql-dropdatabase.html)

If the create acknowledgement is lost, identity capture fails, or a name resolves to a different OID, stop destructive cleanup of that target. Preserve it, report the generated name and incomplete cleanup, and disqualify the run. Process death ends in-process cleanup. The disposable server’s owner handles residual resources. The independent reviews identified this window. Claude’s response failed automated severity-label validation. The in-host review independently rated the ownership gap high. The owner chose preservation when identity is uncertain.

## Acceptance evidence

- Create two clones. Prove distinct database names, three distinct nonempty store IDs across template and clones, distinct clone epochs, and a write visible only in its originating clone.
- Independently inject a checksum mismatch, runtime row, active epoch, second initialization, and unowned target. Compare each target before and after rejection. Exercise ordinary cancellation and failure after creation. Verify owned databases and roles disappear, including the template at fixture disposal, and an unrelated database survives.
- Simulate acknowledged server-side creation with its success reply withheld from the helper. Before harness disposal, use an independent nonpooled maintenance connection to inspect the preserved target and unrelated database by OID and sentinel contents. Inspect the incomplete-cleanup diagnostic. Replace a previously registered database with another of the same name before cleanup and verify its new OID and contents survive. Regress the existing empty and fresh entry points on their supported server paths.

## Benchmark and adoption

Build once in Release. Pin the SDK, machine, server settings, coverage collector, and existing two-worker cap. Run Work then Flow as separate serial processes per arm. Work has 4 cases. Flow has 30, of which 29 need a ready store. Its declared class is **`DurableSlice4ReferenceWorkloadTests`**. Freeze the 34 fully qualified identities from the pinned revision and verify them against discovery before timing. Use one class-qualified filter per process, matching the exact manifest. [Work source](https://github.com/forge-trust/AppSurface/blob/3f3126a5febff2cb94847bf8a1092b5d61657a18/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlDurableWorkDefinitionTests.cs) · [Flow class declaration](https://github.com/forge-trust/AppSurface/blob/3f3126a5febff2cb94847bf8a1092b5d61657a18/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlDurableFlowStoreTests.cs#L9)

Use the PostgreSQL test project with `dotnet test --configuration Release --no-build --no-restore`, TRX logging, and `--collect "XPlat Code Coverage"`. Warm up once per mode. Run five measured pairs in fresh/template, template/fresh, fresh/template, template/fresh, fresh/template order. Record OS, SDK, PostgreSQL version, worker cap, and one-minute load average at each arm boundary. Time each process from launch through exit and add the two durations for each arm.

For each process, set `APPSURFACE_DURABLE_FIXTURE_TIMINGS_FILE` to a unique CSV path in its results directory. Emit monotonic `phase,elapsed_ms` rows. Report counts and summed durations for `database_setup` (creation through readiness, or creation alone for the empty case) and `database_teardown` (disposal through drop). Candidate processes also report `template_prepare` and `template_teardown`. These measurements explain where time went.

Report per-pair seconds saved and gain = 100 × (fresh − template) / fresh. Recommend adoption for these cohorts only if all isolation and cleanup checks pass, all five pairs contain the expected 34 passing identities in both arms, median gain ≥ 5%, median gain > twice its median absolute deviation, and neither cohort slows by ≥ 5% in three or more pairs. Post the pairs, environment, command lines with connection details omitted, and decision on #931 after measurement. A documented no-go closes the issue with fresh as default. Keep existing CI jobs and gates. A claim about total contributor wait needs full coverage-step timing. [Earlier measurement design](https://github.com/forge-trust/AppSurface/blob/3f3126a5febff2cb94847bf8a1092b5d61657a18/artifacts/issue-728-test-efficiency/results.md)

## The assignment

Before implementing cloning, have a maintainer compare the discovered Work and Flow identities with the pinned source and record which Flow case takes the empty path. This gives both benchmark arms the same independently checkable inventory.

## What I noticed about how you think

You chose the shared helper even though the cohort adapter would be smaller. You also chose to fail a run when ownership is uncertain. Together those decisions give every caller one place to find the deletion rule.

<!-- gstack:office-hours:concerns:start -->
## Reviewer Concerns

Disposition: UNREVIEWED

Stop: UNREVIEWED

Unavailable reason: "The independent reviewer could not save a verdict. No recoverable finding inventory, score or seal receipt was returned. Its dotnet help probe was refused."

Findings below are retained from the last completed review; the current document remains unreviewed.

Historical office-hours review: no verdict or finding inventory was returned.
<!-- gstack:office-hours:concerns:end -->

<!-- autoplan-accepted:ceo -->
- Prove that clone validation sees all physical rows. In the validation transaction set row_security = off locally so queries subject to row filtering fail; require the validator's actual access to every schema table, including forced-RLS partitions. Seed an otherwise invisible row, then reject or fail for insufficient visibility before rekeying. Never elevate the configured role or alter policies.
- Bind the five paired comparisons to the same Release binaries, test identities and fixed server. Freeze a sorted 4/30 manifest independently from source and reconcile discovery, TRX complete pass sets, process exit and coverage output on every arm. Report invalid or incomplete evidence separately from a measured no-go. Keep all recorded attempts; never replace an outlier silently.
- Reconcile phase counts per Work/Flow process (setup and teardown 4/30, template prepare and teardown once each only in candidate, including the empty case in Flow). Separate setup charge for clone-specific work from symmetric timing overhead and document fresh-path call parity against the pinned implementation. Report absolute durations and pair-level vetoes alongside the aggregate gain.
- For expected failure-injection tests, observe the preserved target and unrelated database from an independent observer before the test harness disposes its own sacrificial server or resources. A real benchmark arm with uncertain cleanup is invalid. Preserve the approved rule for ordinary cancellation, ambiguous creation and same-name replacement.
- For each process verify a successful exit, completed TRX, unique expected case identities with Passed outcomes, and a process-local XPlat collector attachment containing parseable Cobertura XML with positive lines-valid for the instrumented workload. Missing, duplicate, malformed or stale results, wrong phase counts, negative/nonfinite timings or a collector failure invalidate the attempt. Use identical instrumentation in both modes. Charge clone validation and epoch initialization to database_setup, template migrations to template_prepare and CSV emission outside the phase timer but inside process wall time. Record baseline create, migrate, initialize, status and dispose calls and explicitly itemize every new common query.
- The UNREVIEWED office-hours record above is historical: its final spec reviewer returned no verdict. All new planning reviews are recorded separately; the historical zero finding count provides no assurance.
- Before implementing the candidate, retain the source-derived 34-method manifest and the matching discovery output, including the empty Flow case, with the experiment evidence. Explain the maintenance cost of a second path alongside seconds saved.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:design -->
- Retain the pre-implementation manifest and discovery output under a run-specific results root with an inventory file identifying the empty Flow case. Index raw evidence by pair, arm and cohort in separate directories for warmups and measured runs. Provide a durable maintainer-accessible location and a redacted issue summary with a verdict, seconds and gain per pair, median and MAD, per-cohort veto counts, eligibility and links. Inspect all links before posting.
- Guard failure and cleanup diagnostics identify phase, resource kind, generated name, expected and observed OIDs when available, and a nonsecret server label. Use the test failure/exception path recorded in TRX; preserve original and cleanup errors. Keep role dependencies tied to uncertain or replaced databases intact and report them; test this against an independent observer. Unwritable or missing requested timing files invalidate a measured process.
- Label the visible-row validation and hidden-row rejection in the operative lifecycle instructions. The transaction-local check uses the existing credential, including on forced-RLS tables. Record the actual server major and configuration; document departures from the supported-version reference.
<!-- /autoplan-accepted:design -->

<!-- autoplan-accepted:dx -->
- Document the test-only contract in the PostgreSQL test-project README and link it from Durable/README.md. Give a copyable pinned-SDK restore/build, discovery and measured command for each class, listing the namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests with class names PostgreSqlDurableWorkDefinitionTests and DurableSlice4ReferenceWorkloadTests. Filter by the full class name plus method separator or by a disjunction of exact fully qualified manifest entries. Check the returned inventories for exact equality. Describe configured-server and Testcontainers behavior, server version 16+, safe secret injection, both worker/coverage settings and where evidence is retained.
- Use ordinal lowercase mode values. An unset selector means fresh; explicitly empty, whitespace, surrounding whitespace, different casing and unknown values cause a safe configuration error for the named cohorts. Default operation has no timing file. A supplied timing destination must be new, writable and unique per process. Document how a runner sets it from its chosen results root; include the relative result path and safe cause on I/O failure.
- Each failure message names the operation and category, safe context, expected and observed values if known, cleanup disposition and next action. Redact raw Npgsql messages/connection strings, credentials, payloads and role secrets from exceptions, TRX and published reports. An unset template connection directs the maintainer to configure a dedicated disposable server. Insufficient visibility, epoch or checksum mismatch rejects the clone. An uncertain identity instructs the operator to retain the run and reconcile through the disposable-server owner before retry. Never advise deleting by guessed name. Verify redaction with synthetic sentinel values.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Use the pinned [source and discovery manifest](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/artifacts/issue-931-fixture-benchmark/inventory/manifest.json) and its [build provenance](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/artifacts/issue-931-fixture-benchmark/inventory/provenance.txt). The 4 Work and 30 Flow methods matched discovery on SDK 10.0.402. All four Work and 29 Flow methods initialize the schema and epoch. FlowClient_StartRejectsANonAuthoritativeContextCodec owns an empty database. Verify exact per-process TRX identity multiplicity of one for every expected method, zero unexpected, skipped or failed results, and exact result/collector association. Keep preparatory discovery separate from execution and outcome evidence.
- Use the repository’s xUnit 2.9.3 class-fixture model. Keep its constructible fixture surface inside the test assembly and put ownership state and per-instance fault hooks in the internal helper. Use async class disposal, await outstanding creates/clone releases before disposing the template, prevent new acquisitions once closing, and make repeated disposal idempotent. Dispose each primary and additional data source. Use nonpooled maintenance/template connections and recheck server/name/OID before the existing forced drop. Preserve fresh pooling semantics. Exercise idle and checked-out connections, including the bounded failure case, and prove unrelated sessions survive.
- For template mode require a dedicated disposable, single-server endpoint with no concurrent database or role administration outside the fixture. A catalog check and a subsequent drop by name do not form an atomic operation. Record any role's generated name and OID and its database dependency at acknowledged creation. Before reopening a surviving database or cleaning role dependencies, revalidate both identities. In particular, bypass the existing name-only cleanup in the finally path on ambiguity. A missing resource can count as gone only when the observer can establish that the registered resource is absent. Report uncertain disposition and preserve dependent resources. Serialize lifecycle transitions, including parallel disposal and initialization.
- Use a monotonic independent deadline for the create/identity window, then a new independent deadline for cleanup. Start with 60 seconds for each on the disposable server, record any timeout and verify this budget in a smoke run before pairing. Include connection open, acknowledgment, catalog lookup and dependency cleanup within the applicable deadlines. Observe caller cancellation before dispatch and again after identity capture. Keep any noncancellable disposal owned by the fixture until completion or a declared incomplete result; never issue a competing retry after a timeout. Use narrow per-instance callbacks following the existing disposal seam to withhold an acknowledgement or inject failure immediately after committed rekey. Keep real SQL, credentials, catalog reads and transaction boundaries in integration tests.
- Reuse MigrationAdvisoryLock with an exclusive transaction lock and the existing caller-owned status reader on the same connection. Inspect each clone’s own catalog and fail closed on incomplete visibility; quote identifiers and bind data values. Verify the copied store and virgin metadata, commit the conditional rekey and initialize the epoch using the case's original actor and reason. Inject post-commit initialization failure with successful and failing cleanup. Independently confirm clone/role disposition and survival of template and unrelated database, and retain both error categories. Split the charge for template preparation, 33 clone validations and one empty-database setup as specified. Record the actual PostgreSQL create strategy and new queries. Validate each clone independently before handing it out.
- Before any go recommendation, run the regression, fault and evidence matrix from the engineering test plan. Prove actual session behavior on the configured server, ensure no role-dependent cleanup connects to a same-name replacement, and include intentional pool/drop failures and failure after rekey. Preserve the fresh call sequence per existing case and demonstrate it against the pinned source. Release build and --list-tests are the only completed checks at planning time.
<!-- /autoplan-accepted:eng -->
## Review record

### CEO intake and decisions

GitHub base main, pinned source 3f3126a5. The pre-review scan found no later fixture work on the fetched base. Methodology read lines 1–600, 601–1200, 1201–1800 and 1801–2160. The scope scan matched command once, SDK and require twice each, and PostgreSQL documentation links as HTML (input 3977e88ef85ac69124a275ad3ff6cc6a858ef7f480cfdc12aaaef164844cb66b).

| Decision | Classification | Rationale and alternative |
|---|---|---|
| C1 Check row visibility | Mechanical, completeness | Forced row-level security could make a nonempty table appear empty. A transaction-local validation setting and a planted-row check exercise the actual credential. |
| C2 Validate the measured inventory | Mechanical, completeness | A successful empty filter or partial TRX gives false evidence. Compare actual cases, process status and collector outputs. |
| C3 Publish counts and baselines | Mechanical, explicit | Detect missing work and expose asymmetric costs. A speed claim about all CI would require different measurements. |
| C4 Isolate intentional residue | Mechanical, pragmatic | The deliberate test produces an observable safe refusal. A measured run with a leak cannot justify adoption. |
| C5 Avoid cross-run cleanup service | Mechanical, reuse | Disposable server ownership provides the recovery boundary. A general janitor would require durable ownership proofs. |
| C6 Further adoption | Deferred | Reconsider a supported fixture only after a local go and a named downstream consumer. |

<!-- autoplan-accepted:ceo -->
- Prove that clone validation sees all physical rows. In the validation transaction set row_security = off locally so queries subject to row filtering fail; require the validator's actual access to every schema table, including forced-RLS partitions. Seed an otherwise invisible row, then reject or fail for insufficient visibility before rekeying. Never elevate the configured role or alter policies.
- Bind the five paired comparisons to the same Release binaries, test identities and fixed server. Freeze a sorted 4/30 manifest independently from source and reconcile discovery, TRX complete pass sets, process exit and coverage output on every arm. Report invalid or incomplete evidence separately from a measured no-go. Keep all recorded attempts; never replace an outlier silently.
- Reconcile phase counts per Work/Flow process (setup and teardown 4/30, template prepare and teardown once each only in candidate, including the empty case in Flow). Separate setup charge for clone-specific work from symmetric timing overhead and document fresh-path call parity against the pinned implementation. Report absolute durations and pair-level vetoes alongside the aggregate gain.
- For expected failure-injection tests, observe the preserved target and unrelated database from an independent observer before the test harness disposes its own sacrificial server or resources. A real benchmark arm with uncertain cleanup is invalid. Preserve the approved rule for ordinary cancellation, ambiguous creation and same-name replacement.
- For each process verify a successful exit, completed TRX, unique expected case identities with Passed outcomes, and a process-local XPlat collector attachment containing parseable Cobertura XML with positive lines-valid for the instrumented workload. Missing, duplicate, malformed or stale results, wrong phase counts, negative/nonfinite timings or a collector failure invalidate the attempt. Use identical instrumentation in both modes. Charge clone validation and epoch initialization to database_setup, template migrations to template_prepare and CSV emission outside the phase timer but inside process wall time. Record baseline create, migrate, initialize, status and dispose calls and explicitly itemize every new common query.
- The UNREVIEWED office-hours record above is historical: its final spec reviewer returned no verdict. All new planning reviews are recorded separately; the historical zero finding count provides no assurance.
- Before implementing the candidate, retain the source-derived 34-method manifest and the matching discovery output, including the empty Flow case, with the experiment evidence. Explain the maintenance cost of a second path alongside seconds saved.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-baseline-edits:ceo {"sourceSha256":"3977e88ef85ac69124a275ad3ff6cc6a858ef7f480cfdc12aaaef164844cb66b","replacements":[{"oldText":"No unresolved findings.","newText":"Historical office-hours review: no verdict or finding inventory was returned."},{"oldText":"Inspect the incomplete-cleanup diagnostic and the preserved target.","newText":"Before harness disposal, use an independent nonpooled maintenance connection to inspect the preserved target and unrelated database by OID and sentinel contents. Inspect the incomplete-cleanup diagnostic."}]} -->

Scope spec round 3 stopped at the cap, 7/10. The two requested changes are explicitly applied at their operative locations. This is textual verification, without a fourth reviewer. Earlier coverage attachment and baseline-call obligations were reviewer-confirmed. Temporal sequence: first hour verify manifest and access, hours 2–3 establish owned lifetime and cancellation, hours 4–5 integrate Work and Flow and visible-row proof, then collect independent negative controls, five pairs and the recommendation. Estimated 2–3 engineer-days or 5–9 assisted hours plus benchmark runtime.


### CEO review

| Section | What the reviewer found and what the plan requires |
|---|---|
| 1. Architecture | The helper already creates per-case databases. Keep template lifetime with the class fixture and the destructive decision in the helper. |
| 2. Error handling | Unknown creation outcome can leave a database without recorded ownership. Report the generated name and stop cleanup on that target. |
| 3. Security | A restricted role can see zero rows in a populated table. Fail the clone guard if the full table cannot be observed with the actual configured credential. |
| 4. Data flow | `empty → migrate → use` remains the fresh workflow. In candidate mode `prepare → clone → verify → rekey → activate → use → dispose`; the empty Flow case independently tests the early rejection. |
| 5. Code quality | Keep one option parser at cohort entry and an internal owned-resource abstraction. Avoid retrofitting every consumer to opt into an experiment. |
| 6. Tests | Faults before ownership, after registration and during cleanup have different postconditions. The safety suite runs separately so injected delays never count as benchmark time. |
| 7. Performance | The small Work host amortizes one template across four cases. Separate its process wall time so the larger Flow class cannot conceal the agreed slowdown veto. |
| 8. Diagnostics | Missing collector output and zero selected tests both create persuasive false positives. Reconcile a process exit, test manifest, TRX, timing CSV and coverage attachment. |
| 9. Rollout | An unset selector keeps fresh behavior. Reverting the two cohort opt-ins removes exposure to the alternate path. |
| 10. Trajectory | The next twelve months could yield repeatable setup measurements across projects. Generalizing identity mutation before a named adopter needs it would be premature. |
| 11. Design | HTML links point to reference manuals. Only human-readable benchmark evidence and failure reports can affect presentation. |

#### Error and rescue registry

| Failure | Observable result | Recovery and evidence |
|---|---|---|
| Create outcome ambiguous | Incomplete cleanup with generated identifier | Preserve target, invalidate arm, use independent observer in sacrificial test. |
| Validation denied or data present | Guard fails before release | Roll back and dispose only a verified owned clone. |
| Collector or test host incomplete | Invalid experiment receipt | Retain original files and diagnostic; a complete rerun is a separate attempt. |
| Genuine small/no gain | Valid measured no-go | Publish both cohorts and variability; keep fresh. |

#### What already exists and what is outside scope

The Npgsql maintenance connection already selects postgres and disables pooling. Schema manager supplies checksums, store identity and epoch initialization. Existing disposal tests inject drop and role-cleanup failures. Forced-RLS migrations are concrete evidence for C1. No second CI job, public helper, backend janitor or claim about end-to-end CI time. Full-step timing is separately required for a CI claim. C6 owns the future adoption question.

#### Reality and completion

REALITY: premise-table pass Repeated migrations observed; actual savings require measurement. unverified=1 Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlDurableWorkDefinitionTests.cs:242
REALITY: already-done pass No later fixture implementation in fetched main. 3f3126a5
REALITY: surface-check pass Pinned SDK help confirms test collection, filter, results, TRX and discovery switches. global.json:3
REALITY: binding-decisions pass Shared helper and fail-closed ownership were explicitly approved. docs/designs/issue-931-durable-postgresql-fixtures.md:7
REALITY: numbers pass Cohort membership is pinned to source; performance thresholds are preregistered. docs/designs/issue-931-durable-postgresql-fixtures.md:48
REALITY: deferred-asks pass Downstream API and adoption are gated on evidence. docs/designs/issue-931-durable-postgresql-fixtures.md:11
REALITY: pr-count pass One experiment PR, with later rollout contingent on its results. docs/designs/issue-931-durable-postgresql-fixtures.md:11
REALITY: transaction-semantics finding Test transaction must obtain exclusive migration fence; manager status reader leaves fence and transaction to caller. Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs:170
REALITY: units pass Percent gains use per-pair launch-to-exit seconds; phase milliseconds remain diagnostic. docs/designs/issue-931-durable-postgresql-fixtures.md:50
REALITY: perf-table finding Machine and live durations are unknown until paired runs; cohort case counts and worker cap fixed. Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/xunit.runner.json:3
PLAN_REALITY: phase=ceo rows=10/10 missing=none unverified=1 verdict=complete

The local migration count is observable in source; comparative performance remains unmeasured. The check covered six decision dimensions, eleven review sections and four error families. The distinct baseline risks are false-empty state, lost creation acknowledgement, misselected cases and understated timing costs. The preserved restore point permits recovery of the approved design. Implementation task T1 retains the 4/30 baseline before editing either class.


#### CEO independent voices and consensus

| Dimension | In-host | Claude | Result |
|---|---|---|---|
| Premises | Isolate per case; prove practical savings | Hidden rows and fixed cost challenge proof | Both require real evidence; the RLS repair already appears in C1 |
| Problem | Measured maintainer wait | Wants fixture phase arithmetic primary | Disagreement; retain owner’s metric, alternative at gate |
| Scope | Keep helper confined to cohorts | Wants another CI job after go | Disagreement; keep CI fixed and use existing gates |
| Alternatives | Questions cost of a shared helper | Wants reset/rollback comparison | Both require alternatives examined; transaction reset cannot demonstrate separate identities and per-case database ownership |
| Market | Internal engineering only | No market claim | Confirmed |
| Trajectory | Separate decision on expansion | Dormant opt-in could rot | Both require maintenance decision |

Consensus 4/6. The in-host reviewer returned four findings; Claude returned four. The nominal floor in absolute seconds, extra measured pairs, and a new CI job lack owner approval. Five valid pairs keep the original decision rule. Table truncation would introduce a shared mutable database. Existing container reuse already amortizes server startup. One taste decision on timing primacy, a separate option for future CI exercise, and no corroborated change of direction. Full independent reports retained with the run.


### Design review of the evidence and diagnostics

Methodology read 1–600, 601–1200, 1201–1557. Initial assessment 5/10 for the report. The two Web DESIGN.md files govern separate applications. No webpage, component, form, or viewport is in scope. The source links activated the review. Use the repository’s existing Markdown results convention.

| Pass | Before → specified | Finding and resolution |
|---|---|---|
| Information architecture | 5 → 9 | Lead the issue with verdict and seconds, followed by paired data, validity and evidence links. Put operational requirements next to the step the maintainer executes. |
| States | 5 → 9 | Define go, measured no-go and invalid independently. A cleanup failure includes operator recovery and a scope-limited identifier. |
| Journey | 6 → 9 | A maintainer first records the class manifest, then opens the matching per-process folder from the results index. |
| Visual restraint | 9 → 9 | A single comparison table and textual verdict answer the question; no dashboard or generated mockup is needed. |
| System alignment | 8 → 9 | Follow issue #728’s Markdown record and standard test results layout. Use PostgreSQL 16 as the minimum reference; record the actual server version. |
| Accessibility and narrow layouts | 6 → 8 | Use descriptive links, units in headings and explicit text outcomes. Put detailed per-phase counts in the linked process record to avoid a wide issue table. |
| Decisions | Unscored | Use a private raw-evidence location with a sanitized shareable summary. Any future UI requires separate scope. |

Litmus: issue and subject identifiable yes; verdict anchor yes; headlines scannable yes; one job per section yes; cards required no; motion useful no; shadows unnecessary yes. Native raised two discoverability issues. Claude raised four issues around placement of requirements, role preservation, diagnostics and doc versions. Both identify the plan/report as the audience-facing artifact. Native did not assess every visual dimension, so numeric cross-model confirmation is limited to the shared hierarchy concern. No material aesthetic choice.

<!-- autoplan-accepted:design -->
- Retain the pre-implementation manifest and discovery output under a run-specific results root with an inventory file identifying the empty Flow case. Index raw evidence by pair, arm and cohort in separate directories for warmups and measured runs. Provide a durable maintainer-accessible location and a redacted issue summary with a verdict, seconds and gain per pair, median and MAD, per-cohort veto counts, eligibility and links. Inspect all links before posting.
- Guard failure and cleanup diagnostics identify phase, resource kind, generated name, expected and observed OIDs when available, and a nonsecret server label. Use the test failure/exception path recorded in TRX; preserve original and cleanup errors. Keep role dependencies tied to uncertain or replaced databases intact and report them; test this against an independent observer. Unwritable or missing requested timing files invalidate a measured process.
- Label the visible-row validation and hidden-row rejection in the operative lifecycle instructions. The transaction-local check uses the existing credential, including on forced-RLS tables. Record the actual server major and configuration; document departures from the supported-version reference.
<!-- /autoplan-accepted:design -->

<!-- autoplan-baseline-edits:design {"sourceSha256":"dd050477dc9129c81fac660453dec4b7258e4a2e83ed7baee7c9c2e8533edbaa","replacements":[{"oldText":"Enumerate every ordinary and partitioned table","newText":"Set transaction-local row_security = off and reject insufficient visibility. Include a planted hidden row in the acceptance tests. Enumerate every ordinary and partitioned table"}]} -->


### DX investigation

Internal contributor, using the repo-pinned .NET SDK and a disposable PostgreSQL 16+ server. “I want to tell whether my local run exercised Flow and whether the result counts before I wait for all five pairs.” This narrative describes the review persona. The benchmark has not run. The useful first result is a matched, named test inventory before a candidate is built.

| Stage | User action | Friction and planned proof |
|---|---|---|
| Discover | Start in Durable documentation | Link a single maintainer recipe beside test-source instructions. |
| Prepare | Resolve the pinned SDK, build once | Wrong SDK failed locally; a user-local 10.0.402 host successfully displayed VSTest flags. |
| Select | Discover Work then Flow | Class declaration differs from filename. Show correct FQNs and expected 4/30 counts. |
| First success | Verify fresh discovery and one smoke | Target under five minutes from a prepared checkout and disposable server; excludes initial restore/build, and remains unmeasured. |
| Operate | Run both modes and five pairs | Unique directories, fixed order, redacted diagnostics and clear invalid state. |
| Return | Read results or revert opt-in | Human reviewer can follow a process link and see what changed at the owning layer. |

Official reference patterns: the Testcontainers xUnit guide separates shared context from per-test resources, VSTest exposes discovery/filter/TRX/collector switches, and Npgsql documents that its data source owns pooling. Reuse these existing interfaces. No new package install for fixture users. [Testcontainers](https://dotnet.testcontainers.org/test_frameworks/xunit_net/) · [VSTest](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest) · [Npgsql](https://www.npgsql.org/doc/basic-usage.html)


### DX POLISH review and checklist

| Dimension | Initial → planned /10 | Maintainer operation |
|---|---|---|
| Getting started | 4 → 8 | A test-project README gives a discovery command before a database is needed, then the first fresh and template process runs. |
| API/CLI | 7 → 9 | Existing empty entry point, explicit ready store, two accepted selector values. Keep test-only names within the project. |
| Diagnostics | 4 → 8 | Distinguish invalid setting, missing server, ownership uncertainty, schema mismatch, cleanup and timing I/O. Expected and observed summaries plus safe action reach TRX. |
| Documentation | 4 → 8 | Start at Durable/README.md. One link takes the maintainer to the recipe, inventory, sanitized examples and decisions. |
| Upgrade | 8 → 9 | Existing migrations and callers exercise empty/fresh. Unsetting the mode restores fresh setup in both cohorts. |
| Environment | 5 → 8 | Show pinned SDK resolution, server version and privileges, and evidence root. Refuse a limited validator before advertising a ready store. |
| Community | 7 → 8 | The maintainer-facing handoff is the issue and test documentation. External API support remains separately scoped. |
| Feedback | 6 → 9 | A complete receipt or specific invalidation reason is visible for every process, with an absolute seconds figure in the verdict. |

Planned DX score 8/10, initial 5.6/10 (rubric estimates; pre-implementation). TTHW unmeasured, target under five minutes with prerequisites warm. A clean checkout can take materially longer. The six cap findings were resolved in specification with recovery, redaction and evidence tests; execution remains pending. Design the first success as a correctly selected workload, not merely a successful tool exit.

| Consensus dimension | In-host | Claude | Resolution |
|---|---|---|---|
| First run | Commands missing | Wrong class filter can be vacuously green | Both need explicit instructions |
| Names | Mode grammar unclear | Misspelling needs targeted diagnosis | Confirmed |
| Errors | Six error/recovery gaps | Missing setting/fallback message | Confirmed |
| Docs | Link recipe and examples | Add test README | Confirmed |
| Upgrade | Fresh default sensible | Requests lower minimum for force drop | Preserve existing PG16+ contract; no version downgrade |
| Environment | Server override/evidence location | Dedicated server prerequisite | Confirmed |

Checklist: run documented commands from repo root, reconcile both manifests, check TRX captures redacted configuration and guard failures, demonstrate default fresh and unchanged empty-path behavior, verify every linked evidence file belongs to the stated process. A missing setting fails before database creation. No new framework, daemon or global environment mutation. Native reviewer raised ten items and Claude four, with five of six dimensions supported in both. The pinned repo requirement overrides the outside suggestion that PostgreSQL 13 suffices.

<!-- autoplan-accepted:dx -->
- Document the test-only contract in the PostgreSQL test-project README and link it from Durable/README.md. Give a copyable pinned-SDK restore/build, discovery and measured command for each class, listing the namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests with class names PostgreSqlDurableWorkDefinitionTests and DurableSlice4ReferenceWorkloadTests. Filter by the full class name plus method separator or by a disjunction of exact fully qualified manifest entries. Check the returned inventories for exact equality. Describe configured-server and Testcontainers behavior, server version 16+, safe secret injection, both worker/coverage settings and where evidence is retained.
- Use ordinal lowercase mode values. An unset selector means fresh; explicitly empty, whitespace, surrounding whitespace, different casing and unknown values cause a safe configuration error for the named cohorts. Default operation has no timing file. A supplied timing destination must be new, writable and unique per process. Document how a runner sets it from its chosen results root; include the relative result path and safe cause on I/O failure.
- Each failure message names the operation and category, safe context, expected and observed values if known, cleanup disposition and next action. Redact raw Npgsql messages/connection strings, credentials, payloads and role secrets from exceptions, TRX and published reports. An unset template connection directs the maintainer to configure a dedicated disposable server. Insufficient visibility, epoch or checksum mismatch rejects the clone. An uncertain identity instructs the operator to retain the run and reconcile through the disposable-server owner before retry. Never advise deleting by guessed name. Verify redaction with synthetic sentinel values.
<!-- /autoplan-accepted:dx -->

### Engineering review

The experiment has one primary module, the PostgreSQL test project. The alternative per-cohort adapter would duplicate responsibility for destructive cleanup. Separate template databases give cases that create roles, schemas and multiple connections their own state. A reset or rollback on one shared database would require a different isolation proof. Sequence helper changes, cohort integration and evidence collection in one worktree. Documentation and sample evidence can be drafted alongside the helper. One proposed PR.

#### What already exists

The helper creates empty databases, reuses its configured or container server, opens unpooled maintenance connections and tracks extra data sources. Its normal disposal already requests a forced drop. Its recovery path can open a surviving database by name after a failed drop. Inspect [helper lifecycle](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlIntegrationTestDatabase.cs) and [existing database tests](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlIntegrationTestDatabaseTests.cs). Use the existing [transactional status reader](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs) and [pinned xUnit version](../../Directory.Packages.props). The scope excludes public APIs, runtime migrations, a new CI job, pooled cross-case data and a global janitor.

#### Architecture

```text
Work class fixture       Flow class fixture
       |                       |
       +------ fixture mode ----+
                    |
       internal test database helper ----> fixed maintenance endpoint
            |                 |                 |
       fresh per case    template per class     ownership registry
            |                 |                 |
       existing setup    clone -> guard -> rekey -> epoch -> test
                                                 |
                            dispose pools -> check identity -> release
```

Preserve the current fresh hot path. New common catalog calls go in the baseline cost ledger. Candidate-only catalog scans, migration verification, rekey and activation count in setup. Enumerating each clone costs work proportional to its table count. A probe for the first row suffices to reject a nonempty table. Memory scales with live owned resources and that process’s small evidence file. Use the xUnit worker cap. A result on these two cohorts establishes only their combined process time. No performance gain has yet been measured.

#### Codepath-to-test map

```text
selector              -> parser plus CLI errors, redaction, destination collision
empty / fresh         -> current helper, migration/upgrade, two cohort regressions
ack -> register       -> real create + withheld reply, before/after cancellation
template -> clone     -> quiescent template, two siblings, distinct IDs, isolated writes
validate -> rekey     -> competing requests, hidden row, checksum/metadata/epoch rejection
commit -> init        -> injected init failure, independent catalog and original error
dispose -> roles      -> extra pools, busy sessions, same-name replacement, double dispose
capture -> verdict    -> exact TRX multiset, collector and CSV, veto and threshold boundaries
```

Live PostgreSQL integration is required for database semantics. Parser, reporting and threshold arithmetic use deterministic input files. No LLM or product UI surface changes. Preserve all existing domain assertions. Test-plan artifact for QA: `andrew-HEAD-eng-review-test-plan-20261121-030000.md` in the gstack project. [Pinned inputs](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/artifacts/issue-931-fixture-benchmark/inventory/manifest.json).

#### Failure modes and decisions

| Decision | Trigger and consequence | Implementation obligation | Evidence gap |
|---|---|---|---|
| E1 Register before cancellation | Lost acknowledgement confuses ownership | Bounded transition, no name-only fallback, independent observer | Critical until fault-tested |
| E2 Pool and dependency disposal | A borrowed session or surviving namesake makes cleanup unsafe | Per-resource disposal, verify role and database, preserve both errors | Critical until run against PostgreSQL |
| E3 Validate clone identity and visibility | Copied identity or filtered table creates false isolation | Lock, inspect each clone, conditional rekey and epoch verification | Critical until hidden-row and sibling tests |
| E4 Inject post-commit failure | Partly initialized clone leaks or escapes | Fail between commit and epoch, verify resource disposition and primary error | Critical until fault-tested |
| E5 Keep failure hooks narrow | Ambient fault policy can contaminate parallel classes | Per-helper hooks plus real SQL and independent reads | Verify concurrency and late completion |
| E6 Require exact process evidence | Duplicate outcomes or an omitted collector makes a false go possible | Exact manifest multiplicities, fresh baseline parity, separate invalid verdict | Static and discovery checks complete; execution pending |

#### Independent engineering reviews

| Dimension | In-host Codex | Claude Code | Disposition |
|---|---|---|---|
| Architecture | Ownership structure sound | Wants an observable injection seam | Compatible findings, different claims |
| Tests | Missing pool and post-commit checks | Missing pool and transition failure proof | Both require more lifecycle tests |
| Performance | Exact output identity accounting | Wants a stronger decision rule, fewer clone queries | Gate the rule preference; retain physical validation |
| Security and isolation | Endorses OID checks and independent observer | Requires recheck before force | Both require checked ownership |
| Error handling | Borrowed session and combined errors | Busy database must yield explicit incomplete cleanup | Both confirm |
| Deployment | No separate verdict | No separate verdict | Unrated |

Native raised three findings, Claude four. Three of six dimensions have agreement from both reviewers. The outside preference for stronger timing thresholds goes to the owner. Both reviewers asked for pool coverage. The existing helper already uses FORCE. An active connection by itself does not establish a drop will fail. Reproduce actual success and failure outcomes. The outside estimate of 29 validations covers Flow only; Work adds four.

#### Scope reality

REALITY: premise-table pass Discovery equals the pinned source for both classes, with one empty-database case. artifacts/issue-931-fixture-benchmark/inventory/provenance.txt:12
REALITY: already-done pass Fetched base has the per-case create helper and no cohort template. 3f3126a5
REALITY: surface-check pass Release test discovery ran with both class filters on the pinned SDK. artifacts/issue-931-fixture-benchmark/inventory/provenance.txt:9
REALITY: binding-decisions pass Bound shared internal helper, preserve uncertain identity, normal fresh and fixed CI. docs/designs/issue-931-durable-postgresql-fixtures.md:11
REALITY: numbers pass Raw discovery and pinned method bodies reconcile four Work and thirty Flow identities. artifacts/issue-931-fixture-benchmark/inventory/provenance.txt:12
REALITY: deferred-asks pass Wider rollout requires separate evidence from a second adopter and a maintenance owner. docs/designs/issue-931-durable-postgresql-fixtures.md:11
REALITY: pr-count pass One scoped test-project change can hold the proof and documentation together. docs/designs/issue-931-durable-postgresql-fixtures.md:11
REALITY: transaction-semantics finding A shared status fence does not supply the required exclusive transaction lock. Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs:928
REALITY: units pass Wall seconds yield pair percentages, milliseconds remain phase diagnostics. docs/designs/issue-931-durable-postgresql-fixtures.md:48
REALITY: perf-table finding Main has discovery evidence only. Pair runs on a fixed server must supply observed durations and dispersion. artifacts/issue-931-fixture-benchmark/inventory/provenance.txt:9

PLAN_REALITY: phase=eng rows=10/10 missing=none verdict=complete

#### Completion summary

Source and discovery agree on all 34 methods. Checks completed in planning comprise restore, Release build with zero warnings and errors, and VSTest discovery. Comparative runtime and cleanup behavior remain unverified until the live tests and five-pair experiment. T4–T7 cover ownership, cohort setup, regression proof and measurement. T1's inventory is frozen; per-process reconciliation awaits execution. The broader helper follow-up is recorded in [TODOS.md](../../TODOS.md). No owner directive was overturned.

<!-- autoplan-accepted:eng -->
- Use the pinned [source and discovery manifest](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/artifacts/issue-931-fixture-benchmark/inventory/manifest.json) and its [build provenance](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/artifacts/issue-931-fixture-benchmark/inventory/provenance.txt). The 4 Work and 30 Flow methods matched discovery on SDK 10.0.402. All four Work and 29 Flow methods initialize the schema and epoch. FlowClient_StartRejectsANonAuthoritativeContextCodec owns an empty database. Verify exact per-process TRX identity multiplicity of one for every expected method, zero unexpected, skipped or failed results, and exact result/collector association. Keep preparatory discovery separate from execution and outcome evidence.
- Use the repository’s xUnit 2.9.3 class-fixture model. Keep its constructible fixture surface inside the test assembly and put ownership state and per-instance fault hooks in the internal helper. Use async class disposal, await outstanding creates/clone releases before disposing the template, prevent new acquisitions once closing, and make repeated disposal idempotent. Dispose each primary and additional data source. Use nonpooled maintenance/template connections and recheck server/name/OID before the existing forced drop. Preserve fresh pooling semantics. Exercise idle and checked-out connections, including the bounded failure case, and prove unrelated sessions survive.
- For template mode require a dedicated disposable, single-server endpoint with no concurrent database or role administration outside the fixture. A catalog check and a subsequent drop by name do not form an atomic operation. Record any role's generated name and OID and its database dependency at acknowledged creation. Before reopening a surviving database or cleaning role dependencies, revalidate both identities. In particular, bypass the existing name-only cleanup in the finally path on ambiguity. A missing resource can count as gone only when the observer can establish that the registered resource is absent. Report uncertain disposition and preserve dependent resources. Serialize lifecycle transitions, including parallel disposal and initialization.
- Use a monotonic independent deadline for the create/identity window, then a new independent deadline for cleanup. Start with 60 seconds for each on the disposable server, record any timeout and verify this budget in a smoke run before pairing. Include connection open, acknowledgment, catalog lookup and dependency cleanup within the applicable deadlines. Observe caller cancellation before dispatch and again after identity capture. Keep any noncancellable disposal owned by the fixture until completion or a declared incomplete result; never issue a competing retry after a timeout. Use narrow per-instance callbacks following the existing disposal seam to withhold an acknowledgement or inject failure immediately after committed rekey. Keep real SQL, credentials, catalog reads and transaction boundaries in integration tests.
- Reuse MigrationAdvisoryLock with an exclusive transaction lock and the existing caller-owned status reader on the same connection. Inspect each clone’s own catalog and fail closed on incomplete visibility; quote identifiers and bind data values. Verify the copied store and virgin metadata, commit the conditional rekey and initialize the epoch using the case's original actor and reason. Inject post-commit initialization failure with successful and failing cleanup. Independently confirm clone/role disposition and survival of template and unrelated database, and retain both error categories. Split the charge for template preparation, 33 clone validations and one empty-database setup as specified. Record the actual PostgreSQL create strategy and new queries. Validate each clone independently before handing it out.
- Before any go recommendation, run the regression, fault and evidence matrix from the engineering test plan. Prove actual session behavior on the configured server, ensure no role-dependent cleanup connects to a same-name replacement, and include intentional pool/drop failures and failure after rekey. Preserve the fresh call sequence per existing case and demonstrate it against the pinned source. Release build and --list-tests are the only completed checks at planning time.
<!-- /autoplan-accepted:eng -->

<!-- AUTONOMOUS DECISION LOG -->
## Decision Audit Trail

| ID | Phase | Decision and rationale | Alternative considered |
|---|---|---|---|
| C1 | CEO | Require real row visibility to establish a clean clone. | Trust row counts under default policy. |
| C2 | CEO | Pin discovery, process, TRX and coverage evidence to fix the workload. | Accept a zero exit alone. |
| C3 | CEO | Reconcile setup counts and report phase and wall durations. | Report a single aggregate percentage. |
| C4 | CEO | Observe intentional refusal before sacrificial teardown. | Infer preservation from a helper exception. |
| C5 | CEO | Delegate residual recovery to the disposable-server owner. | Build a cross-run cleanup service. |
| C6 | CEO | Defer reusable packaging pending an adopter and owner. | Add a public API in this experiment. |
| V1 | Design | Index the verdict and process evidence by pair, arm and cohort. | Publish a table without source evidence. |
| V2 | Design | Show actionable redacted failure details and preserve dependent roles. | Collapse cleanup into a generic failure. |
| V3 | Design | Place RLS validation and actual server version in operative steps. | Rely on appendix context alone. |
| X1 | DX | Link the test README and use class-qualified selection. | Guess the class name from the filename. |
| X2 | DX | Enforce exact selectors and unique timing outputs. | Silently accept spelling or output collisions. |
| X3 | DX | Distinguish setup errors and ownership recovery with secret-safe diagnostics. | Forward raw provider messages. |
| E1 | Eng | Register identity within a bounded independent create window. | Abandon an acknowledged create when cancellation arrives. |
| E2 | Eng | Recheck both resource identities and close only case-owned pools. | Use a name-only finally path or clear unrelated pools. |
| E3 | Eng | Validate catalog and store identity separately for every clone. | Cache a template table list as the authority for all clones. |
| E4 | Eng | Prove the post-rekey failure path with an independent observer. | Test only pre-commit rollback. |
| E5 | Eng | Use per-instance transition hooks and the actual database. | Introduce a generic provider command interface for all calls. |
| E6 | Eng | Require exactly one passing result per expected method per process. | Treat equal totals as evidence of equal workloads. |

On 21 November 2026 the owner approved all three recommendations. Measure elapsed time from process launch through exit, use the existing CI gates, and apply the five-pair adoption rule with its per-cohort slowdown veto.

### Implementation tasks

- T1. Preserve the pinned inventory and reconcile each measured process. The discovery portion is complete.
- T2. Write the issue verdict and retain its evidence index.
- T3. Document the two cohort commands and redacted recovery instructions.
- T4. Implement owned registration and checked disposal in the existing helper.
- T5. Integrate the two class fixtures and per-clone guards.
- T6. Run the database, cancellation, isolation and disposal proof.
- T7. Measure five eligible pairs and document a go, a measured no-go, or incomplete evidence.

### GSTACK REVIEW REPORT

The owner approved the experiment plan for implementation on 21 November 2026 by replying `rec` to gate revision 1. The recorded choices are `d1a d2a d3a p1a`. Autoplan reviewed scope, evidence presentation, contributor experience and engineering. The CEO specification review reached its three-attempt cap at 7/10. The primary reviewer checked the final two edits. Fixture adoption still depends on live isolation, cleanup and paired timing evidence.
