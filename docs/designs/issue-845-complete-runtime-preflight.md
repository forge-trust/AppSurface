<!-- /autoplan restore point: "/Users/andrew/.gstack/projects/forge-trust-Runnable/main-autoplan-restore-20260930-201544-issue845.md" -->
## Implementation plan

**Implementation prerequisite override:** On 2026-09-30, the user selected **A** to permit disposable test roles, a disposable StoreId/epoch, and local candidate packages for implementation and the draft PR. This supersedes the historical “before implementation” requirement for actual deployment inputs below. Actual reviewed deployment inputs and named owners remain required before release and activation. Every verifier, regression, coverage, exact-artifact proof, documentation, and later authorization requirement remains in scope. Illustrative fixture values must never be presented as reviewed production values.

# Design: Complete runtime-set schema-11 preflight (#845)

Branch: `main`
Repo: `forge-trust/AppSurface`
Source inspected: `a15bd0c1bea4fd3cd84a46b51e9aaffbce80f4b2`
Builds on: [approved #823 role-pair design](issue-823-shared-store-role-pairs.md), [#845](https://github.com/forge-trust/AppSurface/issues/845), and the [schema-11 operations contract](../../Durable/heartbeat-retention-operations.md#deploy-schema-11). This is the first design for #845; it completes the deferred preflight integration without replacing #823's other decisions.

## Problem Statement

The released `0.2.0-preview.11` [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) reconciles 1–32 dispatcher/runtime pairs and grants heartbeat pruning to every manifest runtime. The [CLI preflight](../../Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs) still infers exactly one runtime from the heartbeat policy, requires a one-role policy, and accepts pruning EXECUTE only for that role plus the owner. Its runtime privilege check uses an existential condition. Relaxing the discovery CTE alone would leave a valid two-pair store rejected and could let a future set-based check pass when only one runtime is safe.

Skoolit [#659](https://github.com/forge-trust/skoolit/issues/659) needs a second restricted Source pair on the existing forwarding store. The required gate must accept the complete reviewed set and reject drift before activation. Source evidence and the release's documented limitation establish the defect; this design session has not run a live reproduction or observed a production failure.

## What Makes This Useful

An operator uses the same reviewed non-secret manifest for reconciliation and preflight, then gets a bounded result identifying any failed runtime or structural check. Passing establishes a current catalog match, rather than forcing the operator to remove a pair or bypass the activation gate. The existing forwarding Work/Flow/Schedule lane and Source Work-only dispatcher boundary remain provable together.

## Decisions and Premises

- **Independent authority:** the complete reviewed version-1 manifest defines the expected runtime set. Policies and grants supply evidence, never authorization by themselves. This carries forward the user's selection to build on #823.
- **Focused CLI ownership:** the user selected approach A, using the existing internal CLI service and no new public provider preflight API. The reusable public provider API was rejected because no second caller is demonstrated. The database role registry remains deferred under #823.
- **Complete verification:** every reviewed runtime must satisfy the restricted-role and heartbeat-retention contract; every unexpected policy target or pruning grantee fails. Preflight remains read-only and cannot reconcile drift.
- **Combined artifact proof:** one pair, second-pair enrollment, identical rerun, and a schema-10→11 upgrade with two already installed pairs are release gates using matching candidate CLI/provider artifacts. Both runtime credentials must pass and both lanes must retain their behavior.
- **Established identity boundary:** pairs separate deployed credentials, not PostgreSQL-enforced lane-specific row authorization. Scope IDs, Work selectors, StoreId, and runtime epoch keep the meanings defined by [#823](issue-823-shared-store-role-pairs.md#premises-and-constraints). Pair retirement, a general runtime doctor, new numbered migrations, and production activation are separate work.

## Selected CLI and Internal API Contract

### Operator input

Extend only `durable schema preflight` with required `--role-pairs-file <path>` and `--migration-owner-role <name>` inputs. There is no default manifest, partial manifest, inline JSON flag, or catalog-derived fallback. One-pair stores also supply their complete manifest. Connection values retain the existing [environment-variable-only contract](../../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands).

```bash
appsurface durable schema preflight \
  --role-pairs-file ./reviewed-role-pairs.json \
  --migration-owner-role appsurface_migration_owner \
  --connection-env FORWARDING_RUNTIME_CONNECTION
appsurface durable schema preflight \
  --role-pairs-file ./reviewed-role-pairs.json \
  --migration-owner-role appsurface_migration_owner \
  --connection-env SOURCE_RUNTIME_CONNECTION
```

Read the file once into bounded immutable bytes, validate before opening a database connection, and calculate SHA-256 over those exact bytes. Accept strict UTF-8 JSON without BOM, comments, or trailing commas, at most 64 KiB and depth 8. These are explicit CLI input limits; the recipe's database authority remains the [version-1 manifest contract](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#role-recipe-contract): exactly `version: 1` and `pairs`, 1–32 entries, exactly `dispatcher`, `runtime`, and `dispatcher_profile` per entry, and explicit `full` or `work_only` profiles. Reject duplicate properties before materialization, wrong types, unknown/missing fields, duplicate role names across entries and role kinds, controls, empty names, and names exceeding 63 UTF-8 bytes. Preserve case, spaces and identifier characters; never trim, normalize, case-fold, interpolate, or cast away overlength names. Database resolution must match each original name exactly and prove OID uniqueness.

The expected migration owner is the explicit deployment-reviewed `migration_owner_role` value already required by the canonical role recipe, supplied to the CLI as `--migration-owner-role`. It is separate from the version-1 pair manifest; there is no catalog-derived owner default. Validate its name with the same UTF-8 byte/control rules before database access, resolve the original name exactly to one OID, and reject overlap with any manifest role. The reviewed proof-input record binds this owner name as well as the manifest hash. The schema owner, heartbeat table owner, pruning/due-health function owners, heartbeat owner-policy singleton and allowed owner ACL principal must all equal this independently supplied OID. Consistent reassignment to another owner fails.

The CLI consumes the complete file even though retention authorization is primarily the runtime subset. Dispatcher names/profiles establish pair identity and exclusions. Runtime OIDs must be disjoint from dispatcher OIDs, the package owner, and observed retention-only policy principals; contradiction or ambiguous role classification fails. The recipe remains authoritative for the full dispatcher/retention grant matrix. This focused preflight does not become a general audit of unrelated objects.

Use the same reviewed file for `psql`'s existing `role_pairs_json` input and for every preflight invocation. The release proof binds their exact byte hash; a whitespace or pair-order edit changes the hash and needs review even if catalog set comparison is unchanged. A hash proves byte identity, not who reviewed the file. The trusted deployment workflow owns file custody and comparison with the previous reviewed deployment record, including #823's catalog-erasure limitation.

### Internal shape, ownership and ordering

Add a CLI-owned immutable manifest request containing validated pairs, ordered pair identities, distinct expected role names, the validated expected migration-owner name, and the original byte digest. Extend `IDurableSchemaCommandService` with one bounded preflight operation consuming that request and returning schema compatibility, caller evidence kind (`runtime` or `owner-diagnostic`), a validated caller role and runtime pair index when applicable, plus stable failed checks and optional validated pair/role identifiers. Keep raw JSON and connection strings out of result/exception types. Document these internal APIs and their ownership/cancellation semantics alongside the command.

One operation owns its short-lived data source, connection, fence and read-only catalog transaction, with a 30-second total online deadline including connection acquisition and lock waiting. Hold the package's shared migration/role-recipe fence before the first catalog snapshot and through compatibility plus structural verification. Use one consistent read snapshot. Do not make two separately fenced service calls and imply their results are one coherent gate.

Reuse provider schema compatibility/checksum logic through a narrow, documented internal seam if needed; do not duplicate it or add a public preflight API. The existing provider status overload starts and commits its own transaction, so it cannot be assumed to accept a caller-owned transaction. Engineering review must choose and test the exact internal seam and fence cleanup sequence before implementation. Avoid holding one pooled connection while opening another; `MaxPoolSize=1`, cancellation, lock timeout and cleanup-failure tests must prove bounded execution and release of owned resources. The current `status`, offline `script`, and explicit `apply` contracts remain; numbered migration bytes/checksums are unchanged.

With valid manifest input, incompatible schema retains the existing compatibility and pending-0011 downtime diagnostics and does not issue a passing structural result. Missing/malformed manifest is an input failure before database access. A successful preflight is a point-in-time result during the reviewed maintenance workflow, not a guarantee against a later privileged catalog change. Concurrent administrator changes that ignore the package fence are outside the activation procedure.

### Exact catalog checks

1. Resolve every supplied role name without filtering out missing/elevated roles. Require a nonempty complete set, exact name/OID resolution, and no aliases. Check every runtime as a restricted LOGIN leaf: no SUPERUSER, CREATEDB, CREATEROLE, REPLICATION or BYPASSRLS; no incoming/outgoing membership edge; no database or package schema/relation/sequence/function ownership. Ownership by the migration owner remains distinct from service-role rights.
2. Compare raw `runtime_heartbeat_runtime_role.polroles` with the expected runtime OIDs as an order-independent exact set with matching cardinality. Reject PUBLIC OID zero, missing/extra targets, duplicate targets, and unresolved targets before role joins can discard them. Require the existing exact command, permissiveness, `USING` and `WITH CHECK` expressions, exactly the two expected heartbeat policies, enabled/forced RLS, and the exact owner policy and consistent schema/table/function owner.
3. Expand the pruning function ACL, substituting PostgreSQL's default ACL when the stored ACL is null. Require EXECUTE for every expected runtime without grant option; reject any runtime grant-option entry and every grantee outside owner plus expected runtimes, including PUBLIC, dispatchers, retention-only and unreviewed principals. Preserve the owner's legitimate implicit ownership/grant rights. Check effective EXECUTE and lack of effective grant option for each runtime as well as direct ACL entries.
4. Require every runtime to lack effective heartbeat DELETE and TRUNCATE, including PUBLIC/inherited rights. No `EXISTS` over a multi-role set may stand in for this universal requirement. Keep all membership and privilege failure paths fail-closed for empty, null or unexpected query results.
5. Preserve the pruning function's exact four-argument signature, integer/non-set result, function kind, owner, SECURITY DEFINER, exact search path, and the existing valid/ready, nonunique ascending `(last_heartbeat_at, worker_id)` btree index checks, including key/opclass/predicate/expression checks.
6. Cross-check exact runtime target sets on the three other runtime policies managed by #823 (`flow_dispatch_runtime_scope_select`, `schedule_dispatch_runtime_scope_select`, `schedule_dispatch_scope_update`) and exact owner-plus-runtime EXECUTE on `runtime_due_dispatch_health(integer)`. This is consistency of the same runtime set, not new Work/Flow/Schedule doctor behavior or a complete function-body audit.

PostgreSQL already exposes these facts through [policy targets](https://www.postgresql.org/docs/16/catalog-pg-policy.html), [role attributes](https://www.postgresql.org/docs/16/view-pg-roles.html), [membership edges](https://www.postgresql.org/docs/16/catalog-pg-auth-members.html), and [ACL/effective privilege functions](https://www.postgresql.org/docs/16/functions-info.html). Use parameter data and catalog OIDs; never generate SQL identifiers from raw file contents.

### Results and recovery

Within the same fenced snapshot, resolve PostgreSQL `session_user` and `current_user` to OIDs. They must agree, preventing a connection that assumes another role from being treated as that role's credential proof. A caller matching a manifest runtime is a `runtime` run; its bounded result identifies the validated pair index and role name. A caller matching the independently supplied migration owner is an `owner-diagnostic` run, explicitly labeled and excluded from runtime activation evidence. Any other caller or unresolved/changed session identity fails, even if the catalog checks would pass. The release gate collects a successful `runtime` result for every distinct manifest pair and checks its reported caller identity; two runs under one runtime never count as both pairs. Owner-mode catalog verification may succeed for diagnosis, but cannot satisfy any runtime-credential requirement.

Retain stable existing check families and add bounded categories for manifest/role resolution, exact role-set mismatch and role aliases. Global defects identify the package object/check. Per-runtime defects identify at most the validated manifest pair index and role name; unknown grantees can be identified by numeric OID without echoing uncontrolled server text. Output ordering is deterministic in manifest/check order. Bound findings by 32 pairs and the fixed check inventory; do not emit raw ACLs, payloads, JSON, SQL, connection/environment values or provider exception messages.

Success prints schema compatibility, caller evidence kind and validated identity/pair, expected migration-owner name, runtime count, and the exact manifest SHA-256. Any failed input, compatibility or structural check exits nonzero with problem/cause/fix and a nearby canonical guide link. Unexpected result shapes, query errors, cancellation and timeout cannot produce success. Drift guidance calls for a reviewed repair and a rerun with the unchanged full manifest; the canonical recipe may itself refuse broader drift, so do not claim rerunning it automatically repairs every failure. Keep activation closed until both runtime credentials pass and the lane proofs are complete.

## Proof and Success Criteria

Use the existing pinned PostgreSQL 16.5 disposable harness and [role recipe integration proof](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs), [CLI tests](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaCommandTests.cs), [mixed-version tests](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlMixedVersionCompatibilityTests.cs), and [packed-consumer infrastructure](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-packed-consumers.sh). Test public or intentionally exposed internal seams; no reflection. Changed code aims for nearly complete branch verification under [repository guidance](../../AGENTS.md).

| Scenario | Required evidence |
| --- | --- |
| One reviewed pair | Candidate packaged recipe succeeds; candidate CLI runtime pass names its pair; a separately labeled owner-diagnostic pass never counts as runtime evidence; one-pair command migration is documented. |
| Enroll Source second pair | Same StoreId/epoch; forwarding Work/Flow/Schedule pass before and after enrollment; Source Work succeeds and its dispatcher retains direct SQL/Flow/Schedule denials; preflight passes under both runtime credentials. |
| Identical manifest rerun | Catalog owners, policy OIDs/targets/expressions, ACLs and effective privileges are unchanged; both preflights and lane proofs still pass. |
| Schema 10 with two pairs → 11 | Capture and assert both pairs before migration; migrate via candidate CLI, reconcile via candidate packaged recipe with unchanged manifest, then pass both runtime preflights and post-upgrade lane/heartbeat/prune proofs. |
| Exact-set/policy drift | Missing first or second runtime; extra runtime/dispatcher/retention/PUBLIC target; unresolved/duplicate targets where constructible; wrong expressions/command/permissiveness; extra/missing heartbeat policy; inconsistent other runtime policy set. Each fails, including when the connected runtime is healthy. |
| ACL/role drift | Prune/due-health missing EXECUTE for either runtime; extra PUBLIC/dispatcher/retention/unreviewed grantee; grant option on either runtime; membership in either direction; any forbidden role attribute/ownership; direct or effective DELETE/TRUNCATE. Mutate one property at a time and restore the fixture between cases. |
| Existing structural guards | Signature/return kind, owner, SECURITY DEFINER/search path, RLS, and each retention-index guard remain covered; dropping objects never yields a false pass. Consistently reassigning all relevant owners/policy/owner ACL to an unexpected principal fails against the reviewed owner input. |
| Input/error/resource branches | Missing/unreadable/oversized/invalid-UTF8/BOM/deep/duplicate-property/unknown-field/wrong-type manifest; 0/33 pairs; both profiles; quoted/case-sensitive/non-ASCII and 63-byte-boundary names; missing/aliased roles; missing/invalid/overlapping owner input; owner versus runtime evidence; unrelated or assumed-role callers; duplicate runtime runs cannot cover a missing pair; null query shape; provider error; cancellation/fence timeout; cleanup and single-slot pool. Assert safe deterministic diagnostics and no mutation. |

For each real-database negative case, run the gate under owner and both runtime credentials when the mutation still permits connection; otherwise assert the connection failure itself cannot pass. Snapshot the relevant catalog before and after preflight to prove it performs no reconciliation. Synthetic result-shape tests complement actual PostgreSQL cases; they do not substitute for them. Ensure the second runtime alone can fail while the first remains valid.

### Honest schema-10 baseline and artifact binding

The inspected history has a schema-10 single-pair recipe at `e0618ac8`; heartbeat retention landed at `b68e854a`, before multi-pair reconciliation at `5dc141db`. The current recipe unconditionally references the schema-11 pruning function. No shipped schema-10 multi-pair recipe was found. The preview.8 old-binary proof is schema-9 protocol on schema 11, not this upgrade scenario.

Provision the upgrade starting state with pinned migrations 0001–0010 plus a clearly test-owned SQL fixture for an operator-configured two-pair schema-10 catalog. Review the fixture against the historical schema-10 grant/policy contract and #823 pair rules, assert its pre-upgrade catalog and lane capabilities, and retain that snapshot. Do not seed it by running the current recipe on schema 10 or describe it as a historically released multi-pair recipe. Run the actual candidate-packaged CLI apply path for 0011 and its preflight path, and the actual candidate-packaged provider recipe for reconciliation.

Bind baseline source commit, normalized migration checksums, fixture byte hash, PostgreSQL image digest, candidate package IDs/versions/source commit and individual `.nupkg` SHA-256 values, extracted recipe hash, and exact reviewed manifest byte hash. Restore/install the CLI tool and provider consumer into clean, explicit package roots from the exact candidate feed; verify restored package bytes and recipe bytes. Source-compiled tests or local `dotnet run` alone cannot satisfy this package gate. A release job runs the exact artifacts proposed for publication; retain the proof with the release and perform the same disposable gate for the published artifacts. A failed or missing candidate-artifact proof blocks publication; a failed or missing published-artifact proof blocks activation. No production credentials or provider data are involved.

## Documentation, Distribution and Next Steps

Publish through the existing coordinated AppSurface CLI tool and PostgreSQL provider NuGet/release paths. Extend the Durable package-consumer/release verification with the CLI tool lane; reuse [PostgreSQL verification](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-postgresql.sh) rather than introducing another migration engine. The release owner chooses the matching next package version and pins the actual artifacts before proof; this design does not invent package hashes or assert a release succeeded.

1. Freeze the internal immutable manifest/request/result and fenced operation contract in engineering review; implement strict file handling and the complete set verifier with branch-focused unit and real-PostgreSQL regression cases.
2. Join existing pair/lane proofs with actual CLI preflight under both credentials and the honest schema-10 modeled-baseline upgrade; add exact candidate-tool/provider package proof and retained artifact identity evidence.
3. Update the [CLI reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands), [provider role/preflight reference](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md#role-recipe-contract), [operations guide](../../Durable/heartbeat-retention-operations.md#deploy-schema-11), [Durable adoption guide](../../Durable/operational-assessments.md#migration-and-role-reconciliation), [root Durable guide](../../Durable/README.md), [local example](../../examples/durable-postgresql/README.md), [diagnostics](../../troubleshooting/durable-diagnostics.md), root/package discovery links, and internal API comments together. Explain the required option including one-pair stores, required reviewed owner input, grammar/limits/hash/custody, caller evidence and per-pair coverage, exact checks/non-claims, safe diagnostics/repair, ordering and package proof; remove the obsolete promise that closed #795 will deliver the remaining gate.
4. Format changed code, resolve introduced compiler/analyzer/doc warnings, run required targeted/integration/package checks, and use [solution coverage](https://github.com/forge-trust/AppSurface/blob/main/scripts/coverage-solution.sh) when practical. Then publish matching CLI/provider/docs only after the complete artifact gate passes. Engineering planning is the next authorized design step; implementation requires its own workflow.

## Open Items for Engineering and Release

- Choose the narrow internal provider status/transaction seam and shared-fence acquisition/cleanup sequence without creating a public provider preflight API or self-starving a one-connection pool. The consistent-read and deadline requirements above are fixed, not optional.
- Identify the actual next release artifacts, reviewed deployment manifest, and named AppSurface release/Skoolit certificate owners. Actual names, credentials, package hashes and production evidence were not supplied in this session.

## The Assignment

Before implementation, have the AppSurface release owner and Skoolit deployment/certificate owner confirm the non-secret planning portion of the proof-input record: the complete reviewed manifest path and byte hash, dispatcher/runtime/profile names, reviewed migration-owner role name, unchanged shared StoreId/epoch, intended matching CLI/provider version and source revision, and who supplies the modeled schema-10 fixture and candidate artifacts. An intended source revision identifies the plan baseline or proposed implementation branch; it is not a claim that final candidate bytes already exist. After implementation and packing, complete the candidate portion with the actual implementation commit, package versions/individual hashes, fixture/image/checksum and recipe hashes plus run/artifact identity before candidate proof and publication. After publication, complete the published portion from the clean public-feed restore and compare it with the candidate before published proof and activation. Keep Source activation closed throughout. This retains the pre-implementation input review without requiring future artifact hashes prematurely; no role creation, secret change or production operation is authorized.


<!-- autoplan-accepted:ceo -->
- Add a non-secret proof-input and result-receipt checklist to the existing schema-11 operations guidance. The record binds the complete manifest byte hash and reviewed owner name, each pair/profile, unchanged StoreId/epoch, baseline source/checksums/fixture hash, PostgreSQL image digest, matching CLI/provider versions and source commit, actual package/recipe byte hashes, supplier/approver responsibilities and candidate/published proof destinations. Missing actual values block their release/activation gate; examples must be visibly illustrative and must not include credentials. No actual release or activation is authorized by this plan.
- Add complete example manifests for one full pair and for a full plus work_only pair in the existing local PostgreSQL example, with commands that pass both required flags and one connection environment variable per invocation. Reference the canonical grammar and operations guide; examples must use the unchanged complete file for recipe and both runtime preflights. Verify examples against parser/recipe grammar and document the one-pair flag transition.
- Freeze and document a narrow internal provider status-read seam during engineering review, reusing the existing embedded checksum/compatibility logic in the caller-owned connection and transaction. The seam must not open connections, begin/end transactions or reacquire the fence, and the current status API must retain its behavior. No public provider preflight API, new migration bytes or duplicate checksum logic is allowed.
- Acquire the package shared fence before establishing the consistent catalog snapshot, retain it through compatibility, caller classification and all structural checks, then release all owned resources within the bounded operation. A SELECT that waits on a transaction advisory lock after starting repeatable read must not be assumed fresh. Verify both controlled completion orders against migration/recipe: a preceding writer finishes before the new snapshot, and a writer starting after the reader fence remains blocked through verification. Include cancelled wait, timeout, cleanup failure, nonzero exit/no success, no leaked lock or pool slot, and MaxPoolSize=1 checks within the existing 30-second total online contract.
- Wire candidate proof into both existing stable/prerelease pack-and-verify paths before their frozen artifact bundle is approved for protected publication. Consume that exact bundle without repacking source, select matching CLI/provider packages, verify bytes in clean tool/consumer roots, and retain a receipt bound to source commit/run/artifact identity and the bundle manifest. Wire the same disposable proof after publication into the existing smoke-install carrier, compare the public-feed restored archive and extracted recipe bytes with the validated candidate, and document that missing/failed published proof blocks application-owned activation. Existing smoke help output alone never satisfies activation. SHA-512 package-manifest binding may coexist with the required SHA-256 per-package receipt; do not confuse or substitute them. Preserve rerun/failure propagation and artifact retention for both release channels.
- Preserve every approved source-plan behavior, input limit, exact raw policy/ACL comparison, independently reviewed owner, session/current caller identity rule, distinct runtime-credential evidence, failure/edge regression, honest schema-10 baseline, artifact gate, documentation requirement and non-goal. The original independent-opinion/coaching/reviewer metadata moves intact to review history solely to keep blind implementation input free of prior verdicts.
- Stage the proof-input record explicitly: reviewed manifest/owner, named responsibilities and intended release/source inputs remain required before implementation; actual candidate package bytes, implementation commit and receipt binding are completed after packing and before candidate proof/publication; public-feed restored byte comparison and receipt follow publication and precede activation. Do not require nonexistent final artifact hashes before implementation or move the approved manifest/owner review later. Preserve the staged record in both operations guidance and release proof.
- Before implementation, the existing planning proof-input review must confirm the second-pair deployment need and deadline, actual release/deployment and long-term proof-maintainer names, expected pipeline work and receipt retention responsibilities. Missing inputs keep implementation/deployment target approval conditional; no customer frequency, incident, deadline, ROI or named person may be invented.
- The application-owned activation procedure must hold the package shared migration/recipe/epoch fence continuously on a dedicated owner guard connection after migration and complete reconciliation, from before the first runtime preflight through every distinct runtime result, all lane proofs and activation. Shared preflight readers coexist; relevant exclusive writers wait. Guard loss, cancellation, any failed/partial pass, mismatching deployment identity or incomplete coverage invalidates the whole combined gate and requires a fresh complete rerun. Do not count earlier individual passes after the guard is lost or released. Keep the existing administrator-ignores-fence trust limitation explicit; this plan does not execute production activation.
- Include existing provider-observed StoreId and active runtime epoch in the immutable preflight result and safe success output, in addition to schema compatibility, caller/pair, reviewed owner, runtime count and manifest hash. The retained combined-gate receipt binds those same values, every distinct pair result, exact CLI/provider package identity and the continuous maintenance window; all runtime results must match the reviewed deployment identity. A catalog digest is not required by this chosen procedure and cannot substitute for the continuous guard. Verify identity mismatch, repeated credential, guard loss and an attempted exclusive writer between passes.
- Use one artifact-consuming disposable proof path shared by stable/prerelease candidate and published carriers. Run it for each released matching CLI/provider bundle, regardless of source-path changes; do not reuse an earlier artifact proof for new bytes. Preserve existing 30-minute pack-job and 20-minute source compatibility limits until measured timing justifies an explicitly reviewed adjustment. Retain stage/total duration, safe failure categories and source/run/artifact-bound receipts, owned by the AppSurface release maintainer and assigned to an actual person in the planning record. Missing or failed proof remains a closed gate, never a timeout-based coverage waiver.
- Explain in the canonical operator guide that this command verifies AppSurface-specific runtime role/policy/ACL and credential evidence in addition to existing migration-history compatibility; bespoke SQL and bypass do not establish the supported activation gate. Preserve the CLI verifier plus reused release harness rather than using release-only proof as a replacement for the operator command.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:dx -->
- Complete the existing disposable PostgreSQL example and canonical operations guide with one-pair and full-plus-work_only manifests/transcripts: prerequisite start, owner input, matching packaged recipe invocation, explicit runtime credential per command and unchanged complete file. Clearly distinguish local/source proof, exact candidate/public artifact proof and application-owned deployment gate. Source examples cannot substitute for actual package evidence.
- Make both required preflight flags and their one-pair transition visible in generated command help, safe missing/invalid-input diagnostics, CLI reference and release/upgrade guidance. Preserve explicit independently reviewed manifest/owner and environment-variable-only credentials; no inferred defaults, partial manifest or bypass.
- Map every stable affected failure category to canonical diagnostic guidance with bounded safe pair/role or unexpected numeric OID, problem/cause/fix and verified nearby docs link, supported inspection/reviewed repair and full-manifest rerun. Do not promise that the recipe repairs every drift or expose raw provider errors, JSON, SQL or secret values. Document recovery for 64KiB/depth8/32-pair/30-second limits; exceeding authorized pair count requires separate review, never partial checks or broader grants.
- Keep one authoritative operator workflow/checklist in the schema-11 operations guide mapping planning, candidate, published and deployment stages to responsible actual owners, required non-secret inputs, exact byte/run identities, receipts and closed gates. Cross-link reference/detail/example pages and replace obsolete #795 promises together. Preserve all staged prerequisite and continuous guard/identity rules.
- Validate affected help, examples, parser/recipe grammar, canonical link targets, supported Bash/SDK/container prerequisites, noninteractive clean package roots and safe cancellation/cleanup alongside the existing branch and package proof requirements. Document internal API shape/ownership/defaults/constraints/decision/pitfall content as well as public usage.
- Include one manual DX acceptance observation from the documented prepared operator start through guide reading, exact-version install, both runtime commands and correct interpretation of remaining gates, targeting2–5minutes without claiming measured success. Separately record cold disposable setup and command/CI durations. Report misses and unknowns honestly; this target is not a new timing release gate and cannot waive existing proof. Add no hosted telemetry or recurring process.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Freeze the narrow internal `ReadStatusInTransactionAsync(connection, transaction, cancellationToken)` provider seam forwarding to the existing schema/checksum reader. Validate open connection and active same-connection transaction; it neither acquires connections/fences nor begins/ends/disposes caller transactions. Add coordinated CLI friend access, document ownership/cancellation/reference/decision/pitfalls, and protect both old status callers and the new preflight with shared-contract tests. Keep public status/script/apply behavior and migration bytes unchanged.
- The unified CLI preflight uses one dedicated nonpooled physical connection with ambient enlistment and multiplexing disabled and a reviewed direct/session-affine endpoint. Acquire the existing migration-key shared session advisory lock before RepeatableRead, set transaction read-only before catalog queries, read compatibility/caller/all exact structural facts in that snapshot, complete the transaction, check explicit unlock success and physically dispose before emitting success. Preserve a single monotonic30second total deadline including connect/wait/cleanup; limit work to28seconds with up-to2seconds of remaining cleanup budget, independent bounded cleanup cancellation, and no connection-string timeout expansion. Any uncertainty/error/cancel/broken transport/cleanup failure is nonzero without success. Prove bounded driver close and no leaked owned locks/backend/pool resources; supplied MaxPoolSize1 must not trigger nested acquisition. Document unsupported transaction/statement pooler lifetime and verify backend affinity in proof; do not infer it solely from a connection string.
- Make the continuous guard executable in the exact-artifact disposable consumer controller: one dedicated nonpooled owner session acquired after all migration/reconciliation/epoch bootstrap, retained across sequential distinct runtime CLI commands, both lane proofs and a cancellable fixture activation callback on the unchanged reviewed epoch. Guard checks/monitoring are bounded by the existing stage/total deadline; loss/cancel/failure/unsafe cleanup invalidates all pending evidence, cancels/drains child processes and stops/drains any activated fixture host. The application deployment owner must supply and validate its equivalent session-affine coordinator before actual activation; individual CLI invocations or disconnected psql commands cannot provide it. Build no public guard API/daemon/production controller. Final receipts bind guard window, every actual runtime caller, manifest/owner, StoreId/epoch and exact artifact/run identity; partial or closed-window evidence cannot authorize future activation.
- Separate resource budgets: controller owns1owner guard connection,1runtime CLI process at a time and separately bounded lane pools; never share the guard one-slot data source with runtime credentials. A queued exclusive writer must lead either to safe whole-window completion or bounded whole-gate failure, guard release, writer completion and fresh complete rerun, never stale partial reuse or an assumed fairness guarantee.
- Preserve nullable uninitialized epoch in provider status and immutable preflight output; structural preflight does not bootstrap it. Combined receipt/activation requires a nonempty matching reviewed epoch and StoreId; bootstrap/rotation is completed before its shared guard. Verify null/empty/mismatch and ensure activation callback performs no exclusive epoch mutation under the guard.
- Implement strict UTF8/JSON duplicate-decoded-property validation and immutable copy/hash-once input/result handling using existing .NET APIs. Replace singleton inference with independent complete expected-name/OID authority and raw-before-join exact policy/default-ACL comparisons and universal effective privilege checks; no empty/null/unknown result may pass. Keep bounded deterministic safe diagnostics and every existing structural guard, including second-runtime-only failures. Reuse provider reader and one explicit artifact-proof entry across candidate/public carriers rather than copying checksum logic or repacking source in artifact mode.
- Implement every G01–G24 required behavior/regression group in the saved engineering Test Plan, using existing xUnit/VSTest/pinned PostgreSQL and actual exact-package subprocess/consumer boundaries. Include strict file/owner grammar/limits/names/hash/immutability; caller and independent owner classification; raw sets/default ACL/every role and ownership; all original structure; shared status seam and both writer orders; read-only/no mutation; timeout/cancel/cleanup/MaxPoolSize1; nullable epoch and guard loss/queued writer/duplicate credential; all4 actual package scenarios with honest modeled schema10 snapshot and both lanes; clean candidate/public byte/carrier failures; help/examples/docs/manual clock. Retain existing status/script/apply, role-recipe/lane, migration/checksum and preview.8 old-binary regressions; amend singleton assertions in place. No reflection, test-only public hooks, blanket test retirement or new framework. Tests must assert observable independent contracts rather than mere existence. Nearly complete changed-branch verification is the goal; report actual coverage/constructibility, do not claim this review measured it.
- Use a finite parameterized catalog-query batch within the single snapshot, bounded raw input/role/result allocations, server-side ownership/membership aggregates or streaming, no per-runtime nested acquisitions and no acceptance based on truncated raw ACL/policy/history data. Test large unrelated catalogs and hostile target/ACL sizes; deadline failures close the gate. No cross-run catalog/permission cache or receipt reuse. Measure nonpooled setup and1/2/32roles plus all scenario/restore/guard/lane/cleanup stage totals for both carriers. Preserve current30minute pack,20minute source compatibility and70minute smoke limits; optimizations cannot drop proof, and a timing change requires separate reviewed evidence. Maintain honest stage/run-bound performance records without unsupported capacity claims.
<!-- /autoplan-accepted:eng -->
## Review record

### Final Autoplan approval

**APPROVED AS-IS** — the user selected **A** at the consolidated final gate on 2026-10-01T01:58:50Z (2026-09-30 local date). This accepts all46 recorded decisions, including the7 taste choices, without overrides. Approval binds the final engineering-reviewed implementation input SHA256 `f555921ad6816f734572257826004862065fc3c480bcebf746afe308e5970a0a`; the complete requirements remain unchanged. The reviewed planning inputs, actual release/deployment owners, intended artifacts/source and unchanged deployment identity must still be confirmed before implementation. Publication and production activation retain their later proof/authorization gates. Outside coverage and the CEO independent spec recheck remain unavailable; this approval does not claim implementation, test or release success.

Earlier pending-state records below are historical phase states. The current approval is this final user answer.

### Autoplan intake

Autoplan status: REVIEWING. Source office-hours approval remains recorded in the implementation input; amendments require the final Autoplan gate.

- Source and active plan: `docs/designs/issue-845-complete-runtime-preflight.md`; base branch `main`; inspected commit `a15bd0c1bea4fd3cd84a46b51e9aaffbce80f4b2`.
- Exact restore point: `/Users/andrew/.gstack/projects/forge-trust-Runnable/main-autoplan-restore-20260930-201544-issue845.md`; original SHA-256 `ef068228f219e4e0876158afbff5a141c4cf480f5e3a1344835dfa56d7194af2`.
- Scope input SHA-256: `ef068228f219e4e0876158afbff5a141c4cf480f5e3a1344835dfa56d7194af2`; DX term threshold 2, observed matches 69 (API 7, CLI 30, command 4, flag 1, argument 1, package 17, require 4, Claude Code 1, integration 3, implement 1); developer-tool semantic trigger true; DX required. UI term matches zero; visual-design phase will be skipped.
- Runtime: `/Users/andrew/gstack/bin/gstack-autoplan-snapshot.ts`; review skill registry is the invoked `/Users/andrew/gstack/.agents/skills` sibling registry.
- Outside reviewer preflight: configuration enabled; `CODEX_MODE: not_installed` describes missing Claude Code. Native reviews remain required; consensus with outside coverage is N/A.

<!-- AUTONOMOUS DECISION LOG -->
## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 1 | Intake | Use the approved #845 design as the active input and preserve its exact restore copy. | Mechanical | P5 | User invoked Autoplan immediately after approving this feature design. | Reopen an unrelated plan. |
| 2 | Intake | Enable DX; skip visual-design review. | Mechanical | P1, P3 | CLI/package adoption has 69 detected matches and the developer-tool trigger; no UI terms occur. | Treat backend scope as grounds to omit DX. |
| 3 | Intake | Retain native reviews with outside coverage explicitly unavailable. | Mechanical | P6 | The Claude Code resolution probe returned not_installed. | Substitute another outside provider or claim consensus. |


### CEO methodology and system audit

Methodology: `/Users/andrew/.gstack/projects/forge-trust-Runnable/autoplan-ceo-methodology-lyQgQ6/methodology.md`, 2,518 lines. Successful complete ranges: 1–600 and 601–1200 in the initial session; 601–1200 reloaded after recovery; 1201–1800, 1801–2400 and 2401–2518 completed after recovery. Current phase driver reloaded; generic skip-listed sections were loaded without re-running parent setup. Source remains the invoked sibling skill registry.

Audit: base/main HEAD a15bd0c1; no code diff; 30 commits inspected. Three unrelated stashes retained. Recent churn concentrates on release workflows, package docs and Durable proof scripts. TODO markers in verification scripts mostly describe shell cleanup and proof boundaries; no new implementation TODO was found in preflight. Related prior deferrals are role retirement, a separate maintenance principal and adopter-owned telemetry. No active #845 PR was found at intake. `AGENTS.md`, `TODOS.md`, existing design, provider status implementation, CLI command and package-consumer patterns were read. Brain caches supplied no usable project or goal digest; the approved design supplies that context. No questions are needed to recover facts already approved.

Taste references: reuse provider `ReadStatusAsync` integrity/compatibility logic and its intentionally exposed synchronization seams; reuse the recipe's exact manifest/role set and provider package recipe extraction. Avoid the CLI's two independent preflight online calls and singleton policy inference; avoid the historical-version harness being mistaken for a two-pair schema-10 proof.

Research: Aside CLI absent at probe; official web documentation used. PostgreSQL catalog preflight and native privilege functions are the tried-and-true approach. A generic migrator or role inventory does not know this package's reviewed runtime intent. Layer 2 confirms shared advisory fencing and explicit pool/deadline handling. Layer 3: a catalog alone cannot authorize the expected set, and a byte hash cannot authenticate review. The complete reviewed manifest and owner input remain independent authority. Sources: [PostgreSQL 16 consistency and snapshot ordering](https://www.postgresql.org/docs/16/applevel-consistency.html), [advisory locks](https://www.postgresql.org/docs/16/explicit-locking.html#ADVISORY-LOCKS), [policy catalog](https://www.postgresql.org/docs/16/catalog-pg-policy.html), [Npgsql timeouts](https://www.npgsql.org/doc/connection-string-parameters). These support implementation conditions, not a claim that new code has been tested.

Prior learnings applied: `shared-health-connection-incompatible-timestamp` (10/10) requires one-connection execution; `net10-strict-json-duplicate-properties` (10/10) suggests the standard .NET 10 strict parser rather than another parser; `autoplan-task-full-commit` and `autoplan-task-artifact-slug` (10/10) govern task evidence.

### CEO Step 0A–0C

Real pain: a correctly enrolled second pair cannot complete the release's required activation preflight. The goal is a trustworthy read-only acceptance/rejection result for the complete expected set, under every runtime credential. Do nothing leaves Skoolit #659 blocked or encourages bypass. A mere nonempty-policy check would solve a proxy and weaken safety. Reasonable premises accepted: reviewed deployment intent exists; operator maintenance obeys the package fence; credential separation is not row-level lane authentication. No clearly wrong premise or user challenge identified.

Reuse map: strict manifest shape from the recipe, System.Text.Json/SHA-256 for bounded CLI input; `DurableSchemaCommandService` for command ownership/diagnostics; provider schema manager for embedded checksum compatibility; shared fence key for recipe/migration exclusion; existing role/lane integration fixtures, Testcontainers and package-consumer extraction for proof. No new migration engine, catalog role registry, public provider preflight surface or dependency is needed.

```text
CURRENT                          THIS PLAN                         12-MONTH IDEAL
multi-pair enrollment supported  exact complete-set preflight      repeatable reviewed deployment evidence
singleton CLI blocks activation -> one pair or 32, same contract -> runtime readiness proven from exact artifacts
separate status/structure reads  one fenced catalog observation    retirement/doctor only with adopter requirements
```

### CEO Step 0D–0G decisions

Depth: implementation-ready, with engineering still responsible for freezing the narrow internal transaction seam before code changes. Working plan is the repo design; exact restore and one CEO scope archive are separate. Mode SELECTIVE EXPANSION comes from Autoplan's explicit override. No new approach decision was needed. These automatic choices authorize plan amendments only; final human approval remains pending.

| ID and owner | Contract and evidence | Current | Proposed | Status | Exact approval and scope |
|---|---|---|---|---|---|
| OH1 / deployment | Complete #823 manifest and independently reviewed owner | Approved manifest authority | Preserve | approved | Office-hours D6/A and earlier A choices; exact original design retained |
| OH2 / CLI | Internal service; no public provider preflight API | Approved CLI ownership | Narrow internal reuse only | approved | Original selected approach A; transaction details checked by Eng |
| OH3 / release | Every runtime, two distinct credentials, owner diagnostic excluded | Full proof | Preserve all negative and lane checks | approved | Original proof and caller-evidence contract |
| OH4 / release | Candidate before publication, published before activation | Exact artifact proof | Concrete release wiring, no production work | approved | Original artifact-binding and activation closure contract |
| CEO-P1 / docs | Proof inputs have owners, digest and gate states | Assignment prose only | Add reusable non-secret proof-input/receipt checklist in operations guide | approved | Autoplan P1/P2/P5; S, low risk; human ~1h / CC ~15min; ~2 docs in blast radius |
| CEO-P2 / example | Complete one-pair and full/work_only manifests | Embedded two-pair example | Add non-secret example files plus exact commands in existing local example | approved | Autoplan P1/P2; S, low risk; human ~1h / CC ~15min; 3 files in blast radius; taste choice |
| CEO-P3 / CLI | Safe, stable bounded output | Deterministic failures and success summary | Extra progress spinner/status chatter | declined | Autoplan P3/P5; skip unnecessary output protocol and noisy CI; S, low risk, human ~30min / CC ~5min |
| CEO-P4 / CLI | Every failure names validated pair/check | Already required in approved design | Another diagnostic formatter | declined | Autoplan P4; duplicate existing requirement, reuse formatter; S, low risk, human ~30min / CC ~5min |
| CEO-P5 / lifecycle | Omission never authorizes retirement | Explicitly deferred by #823 | Role retirement workflow | deferred | Preserve existing TODOS.md role-pair retirement item; human M / CC S; medium risk; outside fix |
| CEO-C1 / implementation | One consistent snapshot follows fence acquisition | Ordering requirement; mechanism open | Require lock-before-snapshot race proof and seam freeze at Eng | approved | Autoplan P1/P5; required correctness clarification, no broader public API |
| CEO-C2 / review storage | Implementation input excludes old opinions | Office-hours opinion/coaching metadata mixed in | Move metadata intact into review history | approved | Autoplan P5; no behavior removed; exact baseline replacement record |

Complexity: estimate 20–24 touched files including docs/tests/workflow receipts, not 20 new services. More than 8 files triggers scrutiny; scope is driven by already approved cross-package distribution and proof. Minimum correct implementation is one parser/request/result family, one CLI preflight orchestration and one narrow existing-provider status seam. Split helpers only at input/query/result responsibilities; reject a new engine or public provider API. Cannot defer the upgrade gate, individual runtime proof, strict input, owner authority or documentation without weakening approval. 10x ambition is repeatable operator evidence for any 1–32-pair deployment, achieved by exact-set checks and the same command rather than broader product scope. Five adjacent delight proposals P1–P5 were independently evaluated; 2 accepted, 1 deferred, 2 skipped. Platform potential is safe internal reuse plus retained artifact receipts; public automation protocols need a real consumer first.

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 4 | CEO | Preserve all OH1–OH4 requirements and use SELECTIVE EXPANSION. | Mechanical | P1, P6 | Explicit Autoplan mode; no existing requirement can be silently cut. | Singleton shortcut, broad rewrite. |
| 5 | CEO | Add non-secret proof-input and receipt checklist (P1). | Mechanical | P1, P2 | Existing assignment and gates need a reproducible operator record. | Invent owner names or accept incomplete evidence. |
| 6 | CEO | Add complete sample manifests/commands in existing example (P2). | Taste | P1, P2 | Three adjacent files improve adoption and make both profiles concrete. | Keep all examples embedded only. |
| 7 | CEO | Skip progress spinner/status chatter (P3). | Mechanical | P3, P5 | Stable scriptable result already covers the operation. | Add a new noisy output contract. |
| 8 | CEO | Reuse approved deterministic diagnostics (P4). | Mechanical | P4 | Duplicate proposal does not add an uncovered outcome. | Another formatter. |
| 9 | CEO | Retain existing retirement deferral (P5). | Mechanical | P3 | Separate lifecycle mutation outside this read-only fix. | Add retirement to #845. |
| 10 | CEO | Require fence-before-snapshot controlled race proof (C1). | Mechanical | P1, P5 | First SELECT can freeze an old snapshot while a fence waits. | Assume transaction lock query supplies fresh snapshot. |
| 11 | CEO | Move prior opinion/coaching to review history (C2). | Mechanical | P5 | Blind reviewer receives requirements; no behavior changes. | Leak prior verdicts into independent input. |

### CEO Step 0H input

Fixed amendment checkpoint: `/Users/andrew/.gstack/projects/forge-trust-Runnable/autoplan-ceo-IRXn3I/ceo-implementation.md`. It is prior state, not a current spec or voice input. CEO scope summary: `/Users/andrew/.gstack/projects/forge-trust-Runnable/ceo-plans/2026-09-30-issue845-complete-runtime-preflight.md`. Spec review pending; no phase completion claimed.

### CEO release carrier feasibility (source research)

Raman completed bounded repository research. Both `nuget-stable-publish.yml` and `nuget-prerelease-publish.yml` have candidate `pack-and-verify` → protected publish → published `smoke-install` carriers. PackageIndex transports exact archives and a SHA-512 package manifest; existing CLI proof installs the selected candidate for coverage, while published smoke only installs exact versions and runs help. Durable packed consumers repack source and cannot alone prove an already frozen candidate. The new Durable gate must consume those frozen archives, retain source/run/artifact binding and compare public restored bytes; activation remains application-owned. This grounds OH4 without a new release system. No source builds or live reproduction were run by research.

| 12 | CEO | Bind candidate/published proof to existing release carriers and exact frozen bundle. | Mechanical | P1, P4, P5 | Research found a usable carrier but no Durable gate or published byte comparison. | Repack source, treat tool help as activation proof. |

<!-- autoplan-baseline-edits:ceo {"sourceSha256":"ef068228f219e4e0876158afbff5a141c4cf480f5e3a1344835dfa56d7194af2","replacements":[{"oldText":"## Independent Perspective\n\nA fresh `combo/sub`-routed Codex subagent read the structured problem/premises without inherited conversation and recommended the CLI-owned immutable request and parameterized set verifier. It emphasized raw-policy comparison, drift affecting only the second runtime, explicit manifest binding, and the provider status transaction-ownership constraint. We adopted bounded per-runtime diagnostic metadata. We call the result a current structural check rather than an attestation, because file hashing cannot authenticate review. Claude Code outside coverage was unavailable at preflight; the underlying native review model identity was not reported. This opinion is design advice, not implementation or database evidence.\n\n","newText":""},{"oldText":"## What I Noticed About How You Think\n\n- Your first A selection kept the approved #823 manifest contract as the starting point instead of reopening role enrollment architecture.\n- You confirmed a full artifact/upgrade proof and then selected the smaller internal CLI implementation surface. That separates a complete outcome from a broader public API commitment; it does not reduce the failure cases we must verify.\n\n<!-- gstack:office-hours:concerns:start -->\n## Reviewer Concerns\n\nDisposition: COMPLETED\n\nStop: PASS\n\nNo unresolved findings.\n<!-- gstack:office-hours:concerns:end -->\n","newText":""},{"oldText":"Generated by /office-hours on 2026-09-30  \nBranch: `main`  \nRepo: `forge-trust/AppSurface`  \nSource inspected: `a15bd0c1bea4fd3cd84a46b51e9aaffbce80f4b2`  \nStatus: APPROVED  \nApproved: 2026-09-30, user selection D6/A  \nMode: Builder  \n","newText":"Branch: `main`  \nRepo: `forge-trust/AppSurface`  \nSource inspected: `a15bd0c1bea4fd3cd84a46b51e9aaffbce80f4b2`  \n"},{"oldText":"Before implementation, have the AppSurface release owner and Skoolit deployment/certificate owner confirm one non-secret proof-input record: exact complete manifest path and byte hash, dispatcher/runtime/profile names, reviewed migration-owner role name, unchanged shared StoreId/epoch, intended matching CLI/provider version and source commit, and who supplies the modeled schema-10 baseline fixture versus actual candidate artifacts. Keep Source activation closed. This is a review of evidence inputs, not role creation, a secret change, or a production operation.","newText":"Before implementation, have the AppSurface release owner and Skoolit deployment/certificate owner confirm the non-secret planning portion of the proof-input record: the complete reviewed manifest path and byte hash, dispatcher/runtime/profile names, reviewed migration-owner role name, unchanged shared StoreId/epoch, intended matching CLI/provider version and source revision, and who supplies the modeled schema-10 fixture and candidate artifacts. An intended source revision identifies the plan baseline or proposed implementation branch; it is not a claim that final candidate bytes already exist. After implementation and packing, complete the candidate portion with the actual implementation commit, package versions/individual hashes, fixture/image/checksum and recipe hashes plus run/artifact identity before candidate proof and publication. After publication, complete the published portion from the clean public-feed restore and compare it with the candidate before published proof and activation. Keep Source activation closed throughout. This retains the pre-implementation input review without requiring future artifact hashes prematurely; no role creation, secret change or production operation is authorized."}]} -->

<!-- autoplan-accepted:ceo -->
- Add a non-secret proof-input and result-receipt checklist to the existing schema-11 operations guidance. The record binds the complete manifest byte hash and reviewed owner name, each pair/profile, unchanged StoreId/epoch, baseline source/checksums/fixture hash, PostgreSQL image digest, matching CLI/provider versions and source commit, actual package/recipe byte hashes, supplier/approver responsibilities and candidate/published proof destinations. Missing actual values block their release/activation gate; examples must be visibly illustrative and must not include credentials. No actual release or activation is authorized by this plan.
- Add complete example manifests for one full pair and for a full plus work_only pair in the existing local PostgreSQL example, with commands that pass both required flags and one connection environment variable per invocation. Reference the canonical grammar and operations guide; examples must use the unchanged complete file for recipe and both runtime preflights. Verify examples against parser/recipe grammar and document the one-pair flag transition.
- Freeze and document a narrow internal provider status-read seam during engineering review, reusing the existing embedded checksum/compatibility logic in the caller-owned connection and transaction. The seam must not open connections, begin/end transactions or reacquire the fence, and the current status API must retain its behavior. No public provider preflight API, new migration bytes or duplicate checksum logic is allowed.
- Acquire the package shared fence before establishing the consistent catalog snapshot, retain it through compatibility, caller classification and all structural checks, then release all owned resources within the bounded operation. A SELECT that waits on a transaction advisory lock after starting repeatable read must not be assumed fresh. Verify both controlled completion orders against migration/recipe: a preceding writer finishes before the new snapshot, and a writer starting after the reader fence remains blocked through verification. Include cancelled wait, timeout, cleanup failure, nonzero exit/no success, no leaked lock or pool slot, and MaxPoolSize=1 checks within the existing 30-second total online contract.
- Wire candidate proof into both existing stable/prerelease pack-and-verify paths before their frozen artifact bundle is approved for protected publication. Consume that exact bundle without repacking source, select matching CLI/provider packages, verify bytes in clean tool/consumer roots, and retain a receipt bound to source commit/run/artifact identity and the bundle manifest. Wire the same disposable proof after publication into the existing smoke-install carrier, compare the public-feed restored archive and extracted recipe bytes with the validated candidate, and document that missing/failed published proof blocks application-owned activation. Existing smoke help output alone never satisfies activation. SHA-512 package-manifest binding may coexist with the required SHA-256 per-package receipt; do not confuse or substitute them. Preserve rerun/failure propagation and artifact retention for both release channels.
- Preserve every approved source-plan behavior, input limit, exact raw policy/ACL comparison, independently reviewed owner, session/current caller identity rule, distinct runtime-credential evidence, failure/edge regression, honest schema-10 baseline, artifact gate, documentation requirement and non-goal. The original independent-opinion/coaching/reviewer metadata moves intact to review history solely to keep blind implementation input free of prior verdicts.
- Stage the proof-input record explicitly: reviewed manifest/owner, named responsibilities and intended release/source inputs remain required before implementation; actual candidate package bytes, implementation commit and receipt binding are completed after packing and before candidate proof/publication; public-feed restored byte comparison and receipt follow publication and precede activation. Do not require nonexistent final artifact hashes before implementation or move the approved manifest/owner review later. Preserve the staged record in both operations guidance and release proof.
- Before implementation, the existing planning proof-input review must confirm the second-pair deployment need and deadline, actual release/deployment and long-term proof-maintainer names, expected pipeline work and receipt retention responsibilities. Missing inputs keep implementation/deployment target approval conditional; no customer frequency, incident, deadline, ROI or named person may be invented.
- The application-owned activation procedure must hold the package shared migration/recipe/epoch fence continuously on a dedicated owner guard connection after migration and complete reconciliation, from before the first runtime preflight through every distinct runtime result, all lane proofs and activation. Shared preflight readers coexist; relevant exclusive writers wait. Guard loss, cancellation, any failed/partial pass, mismatching deployment identity or incomplete coverage invalidates the whole combined gate and requires a fresh complete rerun. Do not count earlier individual passes after the guard is lost or released. Keep the existing administrator-ignores-fence trust limitation explicit; this plan does not execute production activation.
- Include existing provider-observed StoreId and active runtime epoch in the immutable preflight result and safe success output, in addition to schema compatibility, caller/pair, reviewed owner, runtime count and manifest hash. The retained combined-gate receipt binds those same values, every distinct pair result, exact CLI/provider package identity and the continuous maintenance window; all runtime results must match the reviewed deployment identity. A catalog digest is not required by this chosen procedure and cannot substitute for the continuous guard. Verify identity mismatch, repeated credential, guard loss and an attempted exclusive writer between passes.
- Use one artifact-consuming disposable proof path shared by stable/prerelease candidate and published carriers. Run it for each released matching CLI/provider bundle, regardless of source-path changes; do not reuse an earlier artifact proof for new bytes. Preserve existing 30-minute pack-job and 20-minute source compatibility limits until measured timing justifies an explicitly reviewed adjustment. Retain stage/total duration, safe failure categories and source/run/artifact-bound receipts, owned by the AppSurface release maintainer and assigned to an actual person in the planning record. Missing or failed proof remains a closed gate, never a timeout-based coverage waiver.
- Explain in the canonical operator guide that this command verifies AppSurface-specific runtime role/policy/ACL and credential evidence in addition to existing migration-history compatibility; bespoke SQL and bypass do not establish the supported activation gate. Preserve the CLI verifier plus reused release harness rather than using release-only proof as a replacement for the operator command.
<!-- /autoplan-accepted:ceo -->

### Office-hours metadata history (verbatim)

## Independent Perspective

A fresh `combo/sub`-routed Codex subagent read the structured problem/premises without inherited conversation and recommended the CLI-owned immutable request and parameterized set verifier. It emphasized raw-policy comparison, drift affecting only the second runtime, explicit manifest binding, and the provider status transaction-ownership constraint. We adopted bounded per-runtime diagnostic metadata. We call the result a current structural check rather than an attestation, because file hashing cannot authenticate review. Claude Code outside coverage was unavailable at preflight; the underlying native review model identity was not reported. This opinion is design advice, not implementation or database evidence.

## What I Noticed About How You Think

- Your first A selection kept the approved #823 manifest contract as the starting point instead of reopening role enrollment architecture.
- You confirmed a full artifact/upgrade proof and then selected the smaller internal CLI implementation surface. That separates a complete outcome from a broader public API commitment; it does not reduce the failure cases we must verify.

<!-- gstack:office-hours:concerns:start -->
## Reviewer Concerns

Disposition: COMPLETED

Stop: PASS

No unresolved findings.
<!-- gstack:office-hours:concerns:end -->

### CEO spec review round 1

Independent Einstein (`combo/sub` requested; underlying identity unreported) read both inputs to EOF and scored 7/10. Completeness/Feasibility PASS; two distinct issues affected Consistency/Clarity and one affected Scope. Issue 1: proof-input timing needed explicit planning/candidate/published portions. Disposition: preserve the source's approved pre-implementation manifest/owner and intended values; clarify that actual future artifact bytes are bound after packing. Issue 2: source header still exposed the office-hours approval. Disposition: move the exact provenance/verdict header into this history, leaving source/branch facts in implementation. Re-review pending; fixes are not yet reviewer-confirmed.

| ID and owner | Contract and evidence | Current | Proposed | Status | Exact approval and scope |
|---|---|---|---|---|---|
| CEO-S1 / release | Original Assignment and artifact proof | Intended values before implementation; actual hashes at gate | Explicit staged record in both inputs | approved | Autoplan P1/P5 factual clarification; preserves OH4 and earlier owner/manifest review |
| CEO-S2 / review storage | Blind input excludes old verdict | Old approved header remained | Move header to history | approved | Autoplan P5; no behavioral change |

| 13 | CEO | Clarify staged proof-input timing (S1). | Mechanical | P1, P5 | Preserve intended-value review and bind actual bytes only after they exist. | Weaken earlier owner/manifest review or invent hashes. |
| 14 | CEO | Remove old verdict/provenance from blind input (S2). | Mechanical | P5 | Requirements remain; old approval is historical. | Leave ambiguous APPROVED in current review input. |

### Office-hours header provenance (verbatim)
Generated by /office-hours on 2026-09-30
Branch: `main`
Repo: `forge-trust/AppSurface`
Source inspected: `a15bd0c1bea4fd3cd84a46b51e9aaffbce80f4b2`
Status: APPROVED
Approved: 2026-09-30, user selection D6/A
Mode: Builder

### CEO spec loop terminal outcome and 0H document disposition

Round 2 Beauvoir returned an input failure after reading the summary: it tried `autoplan-ceo-0EkezL` rather than the supplied and verified `autoplan-ceo-0EkezR` path. No grade or complete review was returned. Stop condition: reviewer failure; no third launch. Outcome unavailable, actual launches 2, distinct reported issues 2, reviewer-confirmed fixes 0, reported observations remaining 2, latest quality null; prior valid score 7/10. Parent corrected both issues but does not claim independent spec confirmation. Required analytics metrics were appended. This is missing spec coverage, not a PASS.

0H document approval: automatic A under Autoplan P1/P5/P6. Parent read both corrected inputs fully, reconciled all five scope IDs (2 accepted, 1 existing TODO deferral, 2 skipped), retained OH1–OH4 and all source conditions, and verified the staged Assignment and removal of old verdict metadata. This accepts these document versions for continued review only; global human approval and engineering seam/cleanup decisions remain pending. No known behavior was waived.

| 15 | CEO | Record failed spec pass as unavailable and stop its loop. | Mechanical | P6 | Explicit failure route; latest grade null, earlier findings retained. | Call it a clean review or invent confirmed fixes. |
| 16 | CEO | Approve reconciled scope documents for continued review (0H/A). | Mechanical | P1, P5, P6 | Both documents reflect exact current decisions; failure coverage remains explicit. | Reopen settled requirements or imply final human approval. |

### CEO Step 0I Temporal Interrogation

HOUR 1 foundations: implementer reads the canonical manifest/role recipe and existing CLI/provider status contracts; confirms the pre-implementation planning portion of the proof-input record, knows that migration 0011 bytes are immutable, and sets up deterministic internal synchronization seams. Start with existing command composition, not a new provider API. Human ~2h / CC ~25min.

HOUR 2–3 core logic: strict read-once bytes and owner validation must precede network access. Engineering freezes one caller-owned connection/status seam and session-fence-before-snapshot sequence. Catalog comparisons use all expected runtime OIDs, raw policy arrays and expanded ACLs; null/empty results and second-runtime-only drift fail. Caller identity and safe diagnostics are part of the same observed gate. Human ~8h / CC ~2h, medium risk; timing and complete-set checks are P1 verification before ship.

HOUR 4–5 integration: join existing lane proofs to real CLI credential runs, create an honest test-owned schema-10 two-pair fixture, and consume the frozen candidate bundle rather than repack source. Both release channels must propagate gate failures. A successful owner run cannot cover a runtime, two runs from one credential cannot cover two pairs, and old preview.8 schema-9-on-11 proof cannot cover this upgrade. Human ~8h / CC ~2h, medium risk.

HOUR 6+ polish/tests: prove every input/resource/cleanup branch with internal/public seams, catalog immutability and both controlled concurrency orders; check help, one-pair transition, non-secret examples and guide links; format and resolve introduced warnings. Collect actual candidate identity after packing and public restored identity after publishing, retain receipts and keep activation closed until every condition passes. Human ~6h / CC ~1.5h. Feasibility conditions are mandatory; actual artifacts and production activation are later owned work, never fabricated in this review.

### CEO native result and dispositions

Euclid completed the native CEO review with matching INPUT `ceo ccfcebebeb382f4dae12152056367fde4d96db8bc67daeac79d5ac3d16d4a56d`. Requested model combo/sub; underlying model identity unreported. Six findings: investment/deadline (high), missing deployment inputs (high), two-pass catalog window (high), maintenance owner/budget (medium), alternatives/triggers (medium), workaround threat/value framing (medium). Full native output was published in the conversation before consensus. Fresh outside probe: enabled, not_installed; outside unavailable, not a completed review.

| ID / owner | Contract/evidence | Current | Proposed | Status | Exact authority and scope |
|---|---|---|---|---|---|
| CEO-N1 / release + consumer owner | #845 source limitation; #659 concrete adopter; no live incident/frequency/ROI evidence | Original proof-input confirmation before implementation | Add deadline/need and maintenance-cost confirmation to that same planning record | approved | Autoplan P1/P5; no fabricated customer count or cut to OH3/OH4 |
| CEO-N2 / deployment | Missing actual reviewed inputs already block implementation | Staged Assignment | Preserve blocker; illustrative values do not satisfy it | approved | OH1/OH4 exact earlier requirement; no new change |
| CEO-N3 / deployment/CLI | Separate credential calls can span writer changes | Each command fenced, combined window unspecified | Shared maintenance guard across all results/lane checks/activation; bind StoreId/epoch and existing manifest/artifact identity | approved | Autoplan P1/P2/P5; taste, <5 adjacent files, human ~3h / CC ~40min; no catalog digest/public API |
| CEO-N4 / release maintainer | Existing pack jobs 30m, DB build lane 20m, no new gate timings yet | Two release channels, source-repack harness insufficient | One artifact-consuming proof path, explicit triggers/retention, record duration and failures, assign actual maintainer in planning record | approved | Autoplan P1/P4/P5; no new CI service; preserve existing timeouts until measured data supports a reviewed adjustment |
| CEO-N5 / architecture | Release-only proof cannot repair operator CLI singleton gate | CLI-owned verifier + release harness | Preserve; document comparison and run gate whenever coordinated candidate/published CLI/provider bundle is released | approved | OH2/OH4, P1/P4; no source-change shortcut for artifact proof |
| CEO-N6 / docs | Likely substitute is bespoke SQL or bypass | Package-specific authorization evidence | State value/limits and closed activation gate in canonical guide | approved | Existing docs scope, P5; complements migration history validation |

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 17 | CEO | Confirm deployment need/deadline and release-proof owner/cost in planning record (N1/N4). | Mechanical | P1, P5 | Missing facts are pre-implementation inputs, not invented business evidence. | Assume deadlines or remove approved proof. |
| 18 | CEO | Keep actual deployment inputs as implementation prerequisite (N2). | Mechanical | P1 | Original staged Assignment already requires them. | Treat sample values as the deployment target. |
| 19 | CEO | Use shared maintenance guard and deployment identity binding (N3). | Taste | P1, P2, P5 | Guards the combined activation window without a new catalog-digest contract. | State digest plus guard; digest alone cannot prevent later changes. |
| 20 | CEO | Reuse one proof path for both release channels and every released matching bundle (N5). | Mechanical | P1, P4 | Artifact identity requires proof of these bytes, irrespective of path filters. | Release-only substitute for the operator fix, or skip unchanged-source releases. |
| 21 | CEO | Explain authorization proof vs migration validation and keep activation closed (N6). | Mechanical | P1, P5 | Reduces the incentive to bypass while avoiding broader migration-engine scope. | Generic platform positioning. |

| CEO dimension | Native | Claude Code | Consensus |
|---|---|---|---|
| Premises valid | Real defect; deployment inputs needed | unavailable | N/A |
| Right problem | Complete-set activation proof supported | unavailable | N/A |
| Scope calibration | Investment/maintenance cost concern N1/N4 | unavailable | N/A |
| Alternatives explored | Operational comparison concern N5 | unavailable | N/A |
| Competitive/market risk | Bespoke workaround/bypass concern N6 | unavailable | N/A |
| Six-month trajectory | Maintainer/budget and cross-pass window N3/N4 | unavailable | N/A |

### Section 1: Architecture Review

Current scope is OH1–OH4 plus CEO-P1/P2 and N1–N6 clarifications. P3/P4 are declined, P5 deferred; engineering owns the remaining internal seam/cleanup freeze. The CLI already references Npgsql/provider, so internal status reuse adds no package dependency or public provider API. Proposed boundaries are input validation, owned online observation, catalog verification, safe result formatting and one artifact consumer proof. Cross-window drift is one new finding, resolved by N3; per-operation stale-snapshot risk was already resolved by C1. Runtime writes to Work/Flow/Schedule remain outside this command.

```text
BEFORE: command -> status operation -> provider status transaction
                -> separate structure operation -> singleton catalog query

AFTER: reviewed bytes + reviewed owner -> immutable request -> CLI preflight
       CLI preflight -> owned connection -> shared session fence -> read-only snapshot
       snapshot -> existing provider checksum/status read + caller/OID/set verifier
       -> immutable safe result -> console + deployment receipt

DEPLOYMENT: owner guard connection [shared same package fence held continuously]
            runtime A preflight + runtime B preflight + lane proofs -> activation
            migration/recipe/epoch exclusive writers wait until guard release
RELEASE: frozen package bundle -> clean tool/provider install -> same DB proof -> receipt
         exact public restore -> candidate-byte comparison -> same proof -> receipt
```

Data paths: valid input produces an immutable request; missing path/owner is an input failure before network; empty bytes/pairs fail instead of yielding an empty expected set; upstream DB/role/query/cleanup errors yield nonzero safe output. Only one connection is acquired per command. Database/fence availability is the single point of failure and fails closed under the 30-second contract. At 10x/100x invocations, each process uses one slot and shared locks coexist; at 32 pairs comparisons stay batched. Migration writers may wait for active readers but reader lifetime is bounded. Rollback preserves schema/roles and closes activation; use a corrected tool/package rather than pretending the older singleton preflight proves multi-pair safety. No reverse migration or pair removal is implied.

```text
NEW -> INPUT_VALID -> CONNECTED -> FENCED -> SNAPSHOT -> COMPATIBLE
    -> CALLER_BOUND -> ALL_CHECKS_PASS -> CLEANED_UP -> SUCCESS_OUTPUT
Any phase -> FAILURE -> CLEANUP -> NONZERO
Forbidden: missing input -> CONNECTED; unchecked/null data -> SUCCESS;
           cleanup failure -> SUCCESS; owner diagnostic -> runtime activation proof.
```

### Section 2: Error & Rescue Map

Every below row is planned verification, not a claim of implemented handlers or executed tests. One retry of the whole reviewed command is operator-owned; hidden partial retries must not mix snapshots. Catch typed failures first; a final sanitization boundary may fail closed for unexpected faults but must never swallow, print raw messages, or produce success. That boundary deserves its own regression rather than generic-only logging.

| Codepath | Trigger / exception | Planned rescue and user sees | Verification |
|---|---|---|---|
| Resolve flags/environment | Missing flags/env; CommandException | One safe problem/cause/fix + canonical guide; no DB | Command/help tests; no service call |
| Read manifest once | Missing/unreadable/overlimit; IOException, UnauthorizedAccessException | Input category, no raw path/payload/secrets | Fake I/O branches + real temp bytes |
| Decode/parse manifest | DecoderFallbackException, JsonException | UTF-8/grammar category; no raw JSON | BOM/invalid bytes/depth/duplicates/types/limits |
| Validate owner/roles | InvalidDataException/domain failure | Validated pair/check only; unresolved names never trusted output | 63-byte/Unicode/control/duplicate/alias cases |
| Acquire connection | NpgsqlException, OperationCanceledException | Safe connectivity/cancel/deadline category | Constrained pool, cancellation, denied login |
| Acquire shared fence | Timeout/cancel/NpgsqlException | Retry after maintenance writer; no structural success | Controlled writer, total deadline, lock cleanup |
| Provider status read | InvalidDataException/NpgsqlException/incompatible status | Existing missing/inconsistent/upgrade/too-new and 0011 downtime diagnostics | Existing status + caller seam paths |
| Resolve caller/OIDs | Missing/ambiguous/different session/current identity | Caller/role failure; owner result excluded from runtime proof | All caller classifications + second role |
| Verify catalog sets | Drift/unexpected null/empty shape | Stable object/check and validated pair; unknown OID only | Each raw policy/ACL/role/index branch |
| End transaction/fence/connection | NpgsqlException/IO/cancel during cleanup | Nonzero; abort owned resource; never print success early | Fault seam + actual subsequent lock acquisition |
| Write result | IOException/OperationCanceledException | Process failure; no reusable success receipt | Console fault + no retrying DB observation |
| Artifact install/proof/guard | Hash mismatch, process failure, guard loss | Gate closed; secret-safe retained failure evidence; restart entire gate | Candidate/public bytes + guard-loss/race cases |

Error flow: `typed failure -> bounded diagnostic -> cleanup -> nonzero`; only `all checks + successful cleanup -> success`. No new uncovered error-path finding; the typed registry translates source requirements and N3's guard-loss branch into required verification.

### Section 3: Security & Threat Model

Examined file/path input, names, Unicode, SQL interpolation, role aliases/elevation/membership, default ACL/PUBLIC grants, ownership substitution, credential impersonation, unsafe console/log output and artifact supply. High-impact threats are unauthorized policy/ACL broadening, owner substitution and duplicate-runtime evidence; planned exact raw sets, independently reviewed owner and session/current identity plus distinct pair receipts mitigate them. Input injection likelihood is medium and impact high if names become SQL; typed parameters and exact original-name/OID resolution are mandatory. File hash is byte identity, not authentication. No new dependency, secret store, endpoint, application-row data or mutation is added. No new security finding beyond N3; no claim of security verification before code/tests.

### Section 4: Data Flow & Interaction Edge Cases

```text
INPUT(file + owner + env selector)
 -> VALIDATE(bytes/grammar/limits/names)
 -> TRANSFORM(immutable pairs + SHA)
 -> OBSERVE(one fenced snapshot; no DB persistence)
 -> OUTPUT(safe success/failure + retained external receipt)
Shadow: nil/empty/wrong type -> input fail before OBSERVE
        long/encoding/duplicate -> input fail before OBSERVE
        alias/missing/elevated -> checked role fail, not dropped by join
        stale/partial/timeout/query/cleanup -> no success receipt
        conflict/lock -> bounded wait, then fresh snapshot or nonzero
```

```text
ORDER A: writer owns exclusive | reader awaits shared | writer commits/releases
         reader gets shared -> THEN first consistent snapshot -> sees committed state
ORDER B: reader gets shared -> first snapshot -> checks -> cleanup/releases
         writer requests exclusive -> waits until reader releases -> commits later
COMBINED: deployment guard shared -> A pass -> B pass -> lane proofs -> activation
          exclusive writers blocked across children; guard loss/failure invalidates gate
```

Boundary: relevant package catalog/epoch writers cannot interleave with either command's observation; the deployment procedure separately guards the combined window. Administrators ignoring the package fence remain outside the approved trust model. Controlled pause/release seams must exercise both completion orders and guard loss, not rely on sleeps. Twenty edge categories are covered: missing/empty/oversized/deep/encoding/duplicate/type/name limits, missing/alias/elevated roles, null/empty result, second-runtime-only drift, repeated same caller, assumed caller, cancel/timeout/cleanup failure, stale deployment identity, and rerun after guard loss. Double-click/navigation UI cases are inapplicable; duplicate CLI runs are harmless read-only but cannot fake pair coverage. No unhandled planned category remains after N3.

### Section 5: Code Quality Review

Examined current command/service/diagnostics, provider ReadStatusAsync, manifest recipe and proof scripts. Reuse embedded checksum/status logic and strict System.Text.Json/SHA-256 rather than duplicate parsers, checksum calculators or a new engine. Keep request/result immutable, catalog comparison typed and formatting separate; a method with >5 branches should split by those existing responsibilities, not gain a policy framework. Internal XML docs must explain connection/transaction ownership and cancellation. No new finding: C1 and OH2 already require the narrow seam and complete semantics. Source helpers that repack are reusable for source tests, not silently for the frozen-artifact gate.

### Section 6: Test Review

```text
Manifest/owner loader -> unit: all grammar, byte and name branches; no DB on failure
CLI binder/result/diagnostics -> unit/command: flags, caller labels, safe bounded output, exit
Provider transaction seam -> integration: existing compatibility/checksum and current status behavior
Fenced orchestrator -> integration: both writer orders, cancellation/deadline/cleanup, pool=1
Exact set verifier -> unit shape + real PG: each role/policy/ACL/signature/index branch
Credential evidence -> system: every distinct pair; owner/duplicate/assumed caller rejected as coverage
Lane invariants -> existing role-recipe integration: forwarding Work/Flow/Schedule, Source Work-only
Candidate/public package consumer -> system: exact bytes, 1->2/rerun/10->11, receipt and no mutations
Deployment maintenance guard -> controlled system: blocked writer, loss, rerun, shared identity
Docs/examples -> command/fixture checks: one-pair transition, both profiles, canonical links
```

Observable assertions remain exact: role cardinality equals expected size, not >=1; raw OID arrays are compared before role joins; every runtime has EXECUTE/no grant option and lacks DELETE/TRUNCATE; all owners equal reviewed OID; second-only drift fails under the first credential too. Existing schema-11 preview.8 proof does not cover schema-10 two-pair upgrade. Friday confidence test is the exact packaged one-pair → second pair → identical rerun plus modeled upgrade with both lane/credential proofs. Hostile QA changes only second-runtime privileges or consistently substitutes all owner surfaces. Chaos uses controlled fence/cancel/cleanup fault points and guard loss. Input maxima/32 pairs and unknown ACL cardinality require bounded-output assertions. Unit tests cover branches; fewer real PG tests cover catalog semantics; a few exact-artifact systems cover packaging. No timing guesses/reflection/random-order assertions; no LLM prompt changes/evals. Zero additional uncovered gaps beyond the guard proof accepted in N3.

### Section 7: Performance Review

Examined per-pair loops, data size, catalog queries, caching, jobs and pool pressure. Resolve <=65 reviewed names and compare <=32 runtimes using batched parameters; return only fixed check inventory times manifest size, never uncontrolled ACL/catalog strings. Client input memory is bounded by 64 KiB + small immutable records; do not load unrelated catalog payloads into the client. Fresh proof cannot use cached catalog results. Top slow paths are connection acquisition, fence wait and catalog/status I/O; measured p99 is unavailable and must not be invented, while total online deadline remains 30 seconds. Release proof includes provisioning/install overhead; retain actual durations. No new frequent background job or N+1 requirement, and no new performance finding after existing C1/N4 constraints.

### Section 8: Observability & Debuggability Review

Examine entry/failure/success output, evidence retention, operational response and sensitive logs. Fixed diagnostic family, validated pair/caller, manifest hash, schema version and deployment identity make a failure reproducible; release receipts add source/run/artifact/package/recipe identity and duration. The release maintainer records gate duration/failure category in retained job artifacts, not a new telemetry platform. Input failure points to flags/grammar; structural drift requests reviewed repair and unchanged-manifest rerun; compatibility requires maintenance migration/reconciliation; artifact mismatch closes publication/activation and preserves receipts. No raw arguments/connection/env/provider exception messages. No separate dashboard/alert service is justified for this one-shot command; adopter-owned telemetry remains an existing TODO. Zero new observability gaps once N4 ownership/duration is recorded.

### Section 9: Deployment & Rollout Review

One new risk, N3, resolves separate-credential temporal binding. Deployment sequence is review planning inputs → stop affected hosts → apply existing 0011 if needed → reconcile complete manifest → acquire shared maintenance guard → every runtime preflight + lane proofs → retain matching receipts → application-owned activation → release guard. Per-command reads remain independent point-in-time observations; the procedure supplies the spanning window. Guard loss or any failed/partial result closes activation and restarts all checks. Package rollout is build/pack → exact candidate proof → publish same frozen bytes → exact public restore comparison/proof → consumers' activation gate. No new migration, reverse DDL or implicit startup mutation. Missing required flags intentionally break old command invocations; coordinated package/docs version pinning and one-pair examples explain transition. Old CLI may still provide old status/script/apply, but cannot prove multi-pair preflight. First-five-minute/hour checks are owner review of retained proof, each new host's existing schema readiness and unchanged forwarding/Source boundaries; actual production activation is outside this task.

```text
Failure before publication -> fix candidate -> re-run exact artifact proof -> publish
Failure after publication -> keep activation closed -> diagnose/fix new version -> repeat gates
Already activated regression -> approved owner procedure closes affected activation,
                               preserves roles/schema/store identity, rolls forward corrected tool
Never: omit pair, weaken tests, skip preflight, or erase catalog to make a result pass.
```

### Section 10: Long-Term Trajectory Review

Reversibility 4/5: code/internal seam/docs are reversible; the required-flag transition and retained release contracts need coordinated versions. No new persistent store, public provider API, migration bytes or new dependency. Knowledge is distributed via canonical guides/examples/internal ownership comments and one proof path shared by both channels. Maintenance ownership/duration is a planning prerequisite; don't remove accepted gates to optimize an unmeasured cost. Next doctor/retirement/catalog-registry features require separate adopter evidence and remain deferred. P1/P2 examples/checklist fit the existing workflow; skipped progress/duplicate formatter are not load-bearing. Zero new debt items, with existing lifecycle follow-up retained.

### Section 11: Design & UX Review

SKIPPED (no UI scope). CLI/operator usability is reviewed by the subsequent DX phase.

### NOT in scope

Deferred: role-pair retirement/profile narrowing (CEO-P5), already recorded under `Durable role-pair retirement after #823` in TODOS.md. Keep that item intact; no duplicate TODO. Existing doctor/registry/maintenance-principal/adopter telemetry directions remain outside this fix.

Rejected: new public provider preflight API (OH2), progress spinner (P3), duplicate formatter (P4), new migration engine/migration bytes, catalog-as-intent fallback and proof bypass. Production role changes, publication or activation are not executed by this review.

### What already exists

| Sub-problem | Reuse | Limit |
|---|---|---|
| Manifest/role authority | Durable/configure-postgresql-roles.sql, approved #823 | CLI adds strict byte/JSON admission, not new authority |
| Checksum/compatibility | PostgreSqlDurableRuntimeSchemaManager.ReadStatusAsync | Existing wrapper owns transaction; Eng freezes caller-owned internal seam |
| Command/diagnostics | DurableSchemaCommand.cs and DurableSchemaDiagnostics | Replace singleton/set assumption; preserve safe categories |
| Controlled PG/lane proof | PostgreSqlSchemaIntegrationTests + CLI tests | Join to exact CLI credential runs and honest schema-10 fixture |
| Package extraction/restore | verify-packed-consumers + PackageIndex CLI consumer patterns | Artifact mode must consume frozen/public archives, never repack |
| Release gates | stable/prerelease pack-and-verify + smoke-install | New full Durable proof and candidate/public byte binding |
| Deferrals | Existing TODOS.md lifecycle/telemetry items | No new TODO writing required |

### Dream state delta

This plan turns unsupported valid multi-pair catalogs into reviewed complete-set activation evidence and closes second-role/temporal false-pass paths. The 12-month ideal is routine artifact-bound deployment evidence owned by release/deployment maintainers. It intentionally does not invent a public doctor/registry or claim cryptographic authentication of review.

### Failure Modes Registry

All Y values below mean mandatory planned rescue/tests/output, not executed implementation evidence.

| Codepath | Failure | Rescued? | Test? | User sees | Logged? |
|---|---|---|---|---|---|
| Input | invalid/missing bytes/owner | Y | Y | safe input failure | Y bounded |
| Connection | refused/auth/cancel | Y | Y | safe connectivity category | Y bounded |
| Fence | writer contention/deadline | Y | Y | no success, retry after owner | Y bounded |
| Status | missing/old/inconsistent/new schema | Y | Y | existing compatibility/0011 guidance | Y |
| Caller | owner/assumed/unrelated/duplicate evidence | Y | Y | explicit kind or caller fail; no pair credit | Y |
| Roles | second-only elevation/member/ownership | Y | Y | validated pair/check | Y |
| Policy/ACL | extra/missing/null-default/PUBLIC/grant option | Y | Y | object/set failure | Y |
| Structure | signature/RLS/search path/index drift | Y | Y | stable structural family | Y |
| Cleanup/output | lock/IO failure | Y | Y | nonzero; never success early | Y bounded |
| Combined gate | catalog change/guard loss/identity mismatch | Y | Y | activation closed; full rerun | Y receipt |
| Package proof | source repack/public byte mismatch/fixture failure | Y | Y | publication or activation closed | Y receipt |
| Owner inputs | missing actual deployment/maintenance owner | Y | Y manual checklist | implementation/gate not authorized | Y record |

Critical planned gaps: 0. Actual handlers/tests remain implementation work. Spec coverage unavailable and outside coverage missing remain review limitations, not clean-review evidence.

### Scope Expansion Decisions

Step 0 proposals: 5, accepted 2 (P1 checklist/P2 examples), deferred 1 (P5 existing TODO), skipped 2 (P3/P4). Native N1–N6 add required operational clarifications within accepted scope; N3 guard vs digest is a separate provisional taste choice. None removes OH1–OH4.

### Stale Diagram Audit

Existing role/retention guide ASCII diagrams describe the same store/epoch/lane trust model and do not change. Their deployment sequence prose needs the new flag/combined-window update; check diagrams in every touched guide during implementation. No known incorrect existing ASCII diagram; the current file's architecture/state/error/data/deploy/rollback diagrams are current planning outputs.

### CEO Implementation Tasks

- [ ] **CEO-T1 (P1, human ~4h / CC ~45min)** — CLI/provider — Freeze internal same-connection status seam and fence-before-snapshot sequence; prove both orders/cleanup/pool limits. Files: CLI DurableSchemaCommand.cs, provider schema manager/AssemblyInfo.cs, associated CLI/provider tests. Verify targeted unit/real-PG regressions and no status/apply/script changes. Source: C1, Architecture/Error map.
- [ ] **CEO-T2 (P1, human ~8h / CC ~2h)** — Artifact proof — Consume exact frozen/published CLI/provider archives in one reusable PG proof path, retain full staged receipt and failure propagation in both channels. Files: Durable verification scripts, PackageIndex proof integration, stable/prerelease publishing workflows. Verify one pair/second/rerun/10→11, both credentials/lanes, public byte mismatch blocks activation. Source: N4/N5 and source research.
- [ ] **CEO-T3 (P1, human ~3h / CC ~40min)** — Combined activation proof — Hold shared owner maintenance guard across all required passes/lane proofs; bind results to StoreId/epoch/hash/package/window receipt; guard loss invalidates all results. Files: CLI DurableSchemaCommand.cs/tests, Durable heartbeat-retention-operations.md, artifact proof harness. Verify blocked writer and guard-loss/identity mismatch under controlled seams. Source: N3.
- [ ] **CEO-T4 (P2, human ~2h / CC ~20min)** — Docs/examples — Add non-secret planning/candidate/public checklist, complete manifest examples and explicit one-pair/paired commands; explain application-specific value and repair. Files: existing CLI/provider/Durable/example/root/troubleshooting guides plus example manifest files. Verify grammar/flags/canonical links and owner planning record. Source: P1/P2/N1/N6.

Effort estimates assume substantial reuse; architecture/integration ratios ~5x, docs ~6x, not measured delivery promises. Sections 3/5/7/8/10 have no additional new tasks beyond the reused requirements above. Actual named owners/need/deadline and deployment inputs remain pre-implementation checklist work under the approved Assignment.

### CEO Completion Summary

| Item | Current result |
|---|---|
| Mode | SELECTIVE EXPANSION (Autoplan override) |
| System audit | No code diff; 3 unrelated stashes; source/release overlap and known pool/strict-JSON learnings applied |
| Step 0 | 5 scope proposals, 2 accepted/1 existing deferral/2 skipped; full spec loop terminal unavailable |
| Architecture | 1 cross-pass window issue, N3 resolved in plan; C1 from Step 0 retained |
| Errors | 12 paths mapped, 0 planned gaps |
| Security | 0 new issues, 0 unmitigated high-severity planned threats |
| Data/UX | 20 edge categories, 0 unhandled planned; both writer orders mapped |
| Quality | 0 new issues; reuse and internal seams required |
| Tests | Complete codepath diagram; 1 guard-proof addition incorporated, 0 remaining planned gaps |
| Performance | 0 new issues; measured p99 unavailable, 30s online bound retained |
| Observability | 0 remaining planned gaps; bounded diagnostics and staged receipts |
| Deployment | 1 cross-pass risk incorporated, no new migration |
| Future | Reversibility 4/5, 0 new debt items |
| Design | SKIPPED (no UI scope) |
| NOT in scope / reuse / dream delta | Written |
| Error registry / failure modes | 12 rows each, 0 critical planned gaps |
| TODO updates | 0 new; 1 existing retirement deferral retained |
| CEO archive / tasks | Scope document saved; 4 tasks emitted with full commit binding |
| Outside / native | Claude Code unavailable; native complete, 6 findings with dispositions |
| Spec loop | 2 launches; found 2, confirmed fixed 0, remaining observations 2, latest score null; prior 7/10 |
| Lake score | N/A (no coverage-scored unanswered/answered menu eligible) |
| Diagrams | 6: architecture, state, error, data/shadows, deployment, rollback (plus concurrency schedule) |
| Unresolved CEO decisions | 0; 2 provisional taste choices; implementation inputs/details owned by stated later gates |

Approval readiness: PASS for OH1–OH4, P1–P5, C1/C2, S1/S2, N1–N6 using exact source approvals or per-row Autoplan authority. This is plan-scope readiness, not evidence that code/tests/gates ran, nor final human approval. Missing spec/outside coverage is preserved. Engineering still runs last.


### Phase 2 skip and DX entry

Phase 1 close packet was read 1–182 through EOF and semantically verified against source requirements/decisions/conditions/tests. Parent Phase 1 completion message was sent. Phase 2 SKIPPED (no UI scope), not completed. Phase 2.5 DX driver loaded; DX required by positive term/semantic scope. Native DX not yet dispatched; methodology reads and preliminary DX assessment remain required.

### DX Step 0 evidence and preliminary assessment

Methodology full EOF read: 1–300, 301–600, 601–900, 901–1200, 1201–1500, 1501–1800, 1801–2170, completing all four returned ranges. Current driver 1300–1475 reloaded. Supplementary review-sections.md read all 1051 lines; its older host templates are load-only where the exact installed methodology/Autoplan overrides control.

Product: CLI Tool within a .NET Durable library and operator documentation. Auto-selected persona under Autoplan P6: backend/platform engineer adding a second restricted deployment to an existing PostgreSQL store. Context: reviewed schema/role maintenance; expects noninteractive bounded commands, explicit identity and credible release evidence. Tolerance is inferred, not a user observation: an existing operator accepts a maintenance workflow but must not need to reverse-engineer catalog SQL. README audience is .NET composition builders; Durable/README.md:83–101 narrows this feature to operators. Base main; origin/main...HEAD has no product diff. No live runtime or user timing observed. Brain developer-persona and competitive-intel unavailable after probe; source/approved office-hours decisions supply scope. Prior learnings on shared health connection and strict duplicate-property JSON remain applicable.

Initial plan completeness: 7/10 inferred. Required contracts are unusually detailed; the exact guard vehicle, teachable receipt/output and cold onboarding timing remain verification dependencies. DX POLISH selected by driver; no hosted service or public API expansion.

Developer perspective (inferred narrative, 173 words):
I already run the forwarding store and need to add Source without weakening its restrictions. The root README points me to Durable operations, and the retention guide explains the drained schema-11 window. I expect to reuse my reviewed role file. Today the guide shows a runtime preflight without either new flag, so after this change I will need an obvious one-pair upgrade example as well as the two-pair example. I can follow explicit migration and reconciliation commands, but I cannot tell from a plain Compatible line whether my Source credential was actually tested or an owner session stood in for it. I want the success output to name the caller, complete manifest hash, StoreId and epoch, and a checklist that shows the other required evidence. If a second runtime is missing a grant, I need its validated pair and a safe repair destination, not a raw database exception. I also need to know who keeps the guard connection alive through both checks and activation. These are predicted needs based on inspected commands and approved contracts, not observed user feedback.

Competitive benchmark (current official docs; times unknown, no equivalent human benchmark): Flyway validate (https://documentation.red-gate.com/flyway/reference/commands/validate) checks migration identity and exposes structured failure output; EF Core deployment guidance (https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) recommends reviewable scripts/bundles and warns about production application startup migration; Liquibase validate (https://docs.liquibase.com/secure/reference-guide-5-1/database-inspection-change-tracking-and-utility-commands/validate) validates changelog definitions. Compare explicit commands and actionable failure paths; none of these sources establishes AppSurface-specific two-credential activation proof or an onboarding time.

Clock: from the documented operator start (SDK available, reachable compatible disposable store and separately provisioned reviewed credentials/manifest, no CLI tool installed) through guide reading, clean exact-version tool install, both runtime invocations and understanding which gates remain closed. Auto-selected target 2–5 minutes for this bounded prepared-state journey; current estimate 5–10 minutes, unmeasured. Fresh-machine setup, schema upgrade, artifact proof and lane proof have separate starts and unknown total duration; do not advertise this target as their completion time. Cold setup may exceed 10 minutes and remains adoption risk to measure, not a fabricated benchmark.

Magical moment: existing canonical guide + runnable local example + explicit safe Compatible evidence gives the operator a complete list of distinct runtime passes and what remains before activation. Lowest-effort existing vehicle; no new playground, daemon, progress UI or hosted service.

Initial nine-stage journey: discover root Durable link; evaluate canonical operations; install exact .NET tool; hello world run existing disposable proof; integrate unchanged complete role file; debug fixed check categories; upgrade required flags/schema-10 model; scale to 32 distinct pairs; migrate via closed activation/rollback procedure. Every stage will be traced in passes, with proof rather than predictions used to claim completion.

Autoplan preliminary decisions 22–25: persona/type P6; DX POLISH; prepared-state clock and competitive target (TASTE DECISION: alternate full cold-setup clock remains valid but lacks a measured baseline); canonical-guide/example delivery vehicle P5. These are review auto-decisions pending the parent final gate; no human confirmation or measured execution is inferred. Other original/CEO obligations remain fixed.

### DX review: full primary passes and dispositions

Native reviewer Locke completed with matching INPUT dx cad488e739c4fb2c6afc52d3df7b93b6d73957fc0664b6d30cf46337c6c17a6f; agent 01a0f507-b6d5-7d00-b965-e06bca75459a closed. Six findings: first-run path (high), canonical stage map (high), required-flag help (medium), category recovery (medium), bounds recovery (medium), actual planning inputs (medium). All retained; no false claim that implemented examples or actual inputs exist. Fresh outside probe: enabled, resolveClaudeCommand absent, outside unavailable. Full native report published before primary passes; consensus all six dimensions N/A because no outside voice completed. No cross-model-confirmed User Challenge.

Decision ledger continues the Step 0 five-field list. Evidence/current/proposed/approval/scope for native D1: existing local example and approved CEO example obligation / source path already exists / complete its manifest+owner+preflight transcript and separate starts / original office-hours plus CEO P2 / local proof only, actual package gates fixed. D2: staged approved inputs / scattered pages / one canonical stage map and checklist / CEO P1 and source documentation requirement / no new release policy. D3: source preflight help has neither new flag / old invocation / explicit missing-input problem/cause/fix/link plus before/after / original selected CLI contract / no optional inferred default. D4: fixed failure families already required / general repair instruction / category-specific safe inspection and rerun destinations / original diagnostics obligation / no unsafe auto-repair. D5: approved 64KiB/depth8/32/30s / fixed limits / documented recovery / original reference/pitfall obligation / no bypass. D6: actual names/manifest still absent / conditional gates / preserve planning prerequisite / original assignment+CEO / approval of plan does not fill values.

#### Pass 1: Getting started (7 → 7.5/10)

Hall-of-Fame Pass 1 read independently, including canonical-guide delivery reference. Existing examples/durable-postgresql/README.md:24–47 offers one command, prerequisite checks, pinned PostgreSQL 16.5, bounded 420-second script and disposable cleanup. It source-builds, so it cannot replace a clean candidate/public-feed package proof. README has real manifest/profile walkthrough already; retain and improve it rather than creating another competing tutorial. Installation requires .NET 10; local DB setup additionally needs Docker. No account or credit card gate exists in this local path. No hosted playground is required.

Ten would mean one documented start and a novice independently completing and understanding the intended result within the clock. Current plan predicts a supported route; no novice timing is captured. Prepared operator sequence: (1) read the canonical quickstart and install the exact matching tool in an explicit clean tool path, target 1–2min; (2) inspect reviewed full manifest and owner inputs, target 30–60s; (3) run both runtime preflights and interpret their evidence and remaining gates, target 30–90s plus actual downloads/database wait. These are estimates, not a success claim; all prerequired reviewed values and reachable prepared store are explicit. The one-pair local quickstart includes initial provisioning and recipe and therefore has a separate clock, unknown total and 420s script budget, not a promised 5min complete release.

Required invocation examples are the existing plan commands with --role-pairs-file, --migration-owner-role and one connection-env. The future safe success example must identify compatibility, evidence kind, caller/pair, owner, runtime count, manifest SHA, StoreId and epoch and visibly label placeholders as illustrative. It must say that per-command success is not the full activation gate. D26 auto A (P1/P5 mechanical): complete existing quickstart with prerequisites, owner/recipe/runtime sequence and distinctly named local/candidate/published/deployment endpoints. This follows approved example and docs behavior. Measurement remains pending implementation, score honest.

#### Pass 2: API/CLI/SDK design (8 → 8.5/10)

Hall-of-Fame Pass 2 independently read. DurableSchemaCommand.cs:140–174 currently performs status and structural calls separately and prints a plain compatibility sentence. The source-approved replacement is one immutable request/result, one connection and one deadline; do not expose provider catalog APIs. `durable schema preflight` and --connection-env match existing command grammar. Both newly required flags deliberately lack defaults because independently reviewed authority cannot be inferred. One pair uses the same interface as 32 pairs; complexity is explicit rather than hidden. Escape hatches are explicit manifest pairs/profiles and connection environment-variable name; partial manifests, raw connection args, broadened privileges and bypass are prohibited scope, not missing UX features. Existing offline script/status/apply remain.

Ten means users choose the right credential and immutable input after one example and receive complete machine-safe evidence. D27 auto A (P1 mechanical): command help must say both flags required for every pair count, same full file per invocation, owner diagnostic excluded from credential evidence; missing flags and invalid names provide problem/cause/fix/link before DB access. Preserve generated CliFx help conventions. Machine receipt is proof-internal; no new JSON public-output flag is authorized. Tests exercise actual command help/input failures and exact service request without printing secrets.

#### Pass 3: Errors and debugging (7 → 8.5/10)

Hall-of-Fame Pass 3 independently read. Three actual traced source paths:

| Path/source | Current text/behavior | Required post-change behavior |
| --- | --- | --- |
| Missing connection, command:211 | "Environment variable '{name}' is missing or blank. Set it to a migration-owner or read-only PostgreSQL connection before running this command." | For preflight, name only the validated variable; explain runtime credentials versus owner-diagnostic; problem/cause/fix/canonical link; no variable value. |
| Pending 0011, diagnostics:564–567 | Existing problem/cause/fix downtime text correctly says keep activation closed, drain, apply, reapply roles, rerun. | Retain exact downtime meaning with both required inputs and unchanged full file; successful owner diagnostic cannot clear gate. |
| Structural drift, diagnostics:575–577 | General retention structure failure, failed checks joined, rerun using owner/runtime. | Deterministic categories and validated pair/role or numeric unexpected OID; safe inspection destination, reviewed repair and exact rerun guidance; no blanket assertion that recipe repairs all drift. |

Also source RunOnlineAsync:242–262 maps cancellation, provider/timeout/config errors to safe text but lacks the full four-part format. Complete that format for affected preflight paths while preserving other command semantics. Verbose/raw provider exception output would violate the accepted boundary; no verbose bypass. D28 auto A (P1 mechanical): canonical diagnostics map for input/role/owner/policy/ACL/function/index/caller/identity/resource categories, reachable from errors and guide. D29 auto A (P1): bounds recovery explains keep full file, reduce redundant JSON layout without changing reviewed roles then re-review/hash; never split 33 pairs into passing partial manifests; depth/wrong grammar repaired; locks/readiness diagnosed for timeout; deployments beyond fixed limits need separately reviewed product change. No invented diagnostic IDs; preserve stable families, choose any new constants consistently in implementation. Tests assert no raw JSON/SQL/server/env/credential leaks and bounded ordering.

#### Pass 4: Documentation and learning (7 → 8.5/10)

Hall-of-Fame Pass 4 independently read. Root README already links Durable/retention and CLI; CLI README:50–92 and provider README:367–369 still direct two-pair closure to #795. Replace the obsolete pointer as part of this exact accepted fix. Root navigation, package entry, CLI reference, provider reference, operations, operational assessments, local example and diagnostics need coordinated links; internal APIs need XML shape/ownership/cancellation/constraints as required by AGENTS. One authoritative workflow/checklist lives in heartbeat-retention-operations, with detailed sections linked rather than duplicated. Version label and exact matching package installation belong next to examples. No new docs search product, playground or broad docs redesign.

| Canonical stage | Responsible role (actual person required) | Inputs | Output and gate |
| --- | --- | --- | --- |
| Planning | AppSurface release + Skoolit deployment/certificate owners | reviewed manifest/owner/deployment identity, need/deadline, intended version/source, fixture supplier and maintenance responsibilities | confirmed non-secret planning record before implementation target approved |
| Candidate | AppSurface release/proof maintainer | exact frozen CLI/provider package bundle and staged actual hashes/fixture/image | source/run/artifact-bound complete disposable proof receipt; required before publication |
| Published | release/proof maintainer | clean public-feed bytes compared with candidate | complete matching published receipt; required before activation |
| Deployment | application deployment/certificate owner | actual reviewed credential mapping, continuous guard, every distinct runtime and lane proof | whole combined receipt and application-owned activation authorization, invalidated on guard loss |

Ten means a first reader reaches this map under two minutes and executes examples without guessing. Findability timing unknown. Stage map is approved checklist follow-through, D30 auto A (P1 mechanical), not independent schedule policy.

#### Pass 5: Upgrade and migration (8 → 8.5/10)

Hall-of-Fame Pass 5 independently read. Required flags are an intentional preview CLI change for one-pair consumers; release notes and one-pair before/after instructions must land with behavior. Preserve forward-only migration/history, schema11 downtime, expected pending0011 failure, unchanged current recipe incompatibility with schema10, existing previous-binary proof, and explicit modeled baseline distinction. No codemod is necessary for two additional flags; do not silently infer owner or manifest to reduce friction. Ten would include rehearsed complete package upgrade and rollback with independent operator understanding; not executed in this plan review. D31 auto A (P1 mechanical): stage the old/new invocation in release guidance, show that status/script/apply still use their contracts and why flags are now mandatory; exact package proof is already required. No new tasks beyond the guide and command tasks.

#### Pass 6: Developer environment and tooling (7 → 8/10)

Hall-of-Fame Pass 6 independently read. Existing check-prerequisites.sh names dotnet/docker and safe env names; verify-postgresql.sh offers external connection or pinned Testcontainers, exact test discovery and failure messages; packed-consumer infrastructure supplies isolated restores. Reuse those. Bash local verifier implies Bash on Windows or CI runner; document supported shells and file paths rather than claim native PowerShell script parity. C# internal records and XML provide IDE information; TypeScript language support/LSP additions do not apply. Noninteractive CLI has cancellation/nonzero outcomes; artifact fixture cleanup needs bounded proof. Source tests and clean package receipts prove different boundaries. No new dev container, SDK, mock database or environment wizard. Ten requires verified clean supported environments and measured cold restore/run. D32 auto A (P1): explicit prerequisite/tool-path/package-cache roots and safe cancellation/cleanup instructions in current example/proof help; bounded gate/resource tests are original required verification.

#### Pass 7: Community and ecosystem (7 → 7/10)

Hall-of-Fame Pass 7 independently read. CONTRIBUTING.md provides GitHub issue chooser, docs/DX feedback and private security route; licensing.md links canonical LICENSE and explains public-preview/commercial support separately. Public code is not evidence of a permissive license or guaranteed staffed channel. Reuse those exact links; no new Discord, paid tier, plugins, public telemetry or support SLA. Existing local example is a real Work/Flow/Schedule reference. Ten would require demonstrated response and adoption data that this bug-fix scope does not supply. No issues requiring behavior changes; no new tasks from this pass, no fabricated community measurements. D33 keep existing feedback route (P5).

#### Pass 8: Measurement and feedback (6 → 7.5/10)

Hall-of-Fame Pass 8 independently read. Source existing script budgets and proposed CI job budgets are execution ceilings, not human onboarding measurements. Keep TTHW unknown until post-implementation validation. Use one manual stopwatch/step observation of the complete prepared-state clock from Step0; record guide reading, exact-version install, input inspection, both runtime invocations and correct interpretation of remaining gates. Separately capture cold local-example prerequisite state, build/download time and first useful local proof. Report command/CI durations separately as approved by CEO; no hosted journey analytics or recurring process.

D34 auto A, TASTE DECISION: include one manual first-reader/implementer validation in the existing acceptance checklist (human ~30min / agent ~10min); alternate automated timing only would be cheaper but cannot measure reading/understanding. This is optional verification depth auto-selected under Autoplan and visible at final gate, not already human-approved. Target remains 2–5min prepared-state; a miss is reported for DX adjustment and never waives structural/package gates or imposes a new release-blocking timing SLA. Cold setup >10min risk is unmeasured; flag if observed and recommend improvement. Ten requires real independent novice evidence and longitudinal data; retain honest 7.5.

#### Developer journey: nine stages, post-review

| Stage | Developer does | Friction/disposition | Status |
| --- | --- | --- | --- |
| Discover | root README → Durable operations | existing links reused; new guide endpoint verified | planned |
| Evaluate | reads canonical exact-set/nonclaims and stage map | avoids treating migration validation as activation proof | planned |
| Install | exact CLI/provider bytes in explicit clean roots | SDK/package prerequisites documented, download time unknown | planned |
| Hello world | disposable one-pair example | complete manifest/owner/recipe/preflight transcript, local timing separate | planned |
| Integrate | unchanged complete file for full+work_only pair | credential mapping and distinct coverage explicit | planned |
| Debug | named failure → diagnostic entry → reviewed repair | stable safe categories, no raw details/automatic drift repair | planned |
| Upgrade | adds both flags; candidate modeled10→11 | breaking invocation and downtime shown | planned |
| Scale | up to32 pairs, distinct runtime result each | bounded output/deadline; exceeding limits not partial bypass | planned |
| Migrate | published and deployment receipts → activation | continuous guard/identity and application owner retained | planned |

#### First-time developer confusion report

Simulated timestamps, not observed timing. T+0:00 root README Durable links exist; navigate to canonical guide. T+0:30 existing CLI reference shows bare preflight; new help/one-pair before-after addresses expected confusion. T+1:00 source operations example has one runtime env; completed two-pair guide must enumerate both runtime invocations. T+2:00 current success is plain Compatible; enriched safe identity/count/hash/StoreId/epoch and stage checklist prevent treating it as whole gate. T+3:00 old provider/source warning points to #795; replacing it and linking the complete new gate prevents contradictory directions. No runtime outcomes invented. Each point traced to source and addressed in approved scope.

#### DX scorecard and principle coverage

| Dimension | Initial | Amended inferred plan | What remains to reach10 |
| --- | --- | --- | --- |
| Getting started |7|7.5|measure clean prepared/cold journeys |
| API/CLI/SDK |8|8.5|run generated help and actual outputs |
| Errors |7|8.5|prove every failure map and safe bounded text |
| Documentation |7|8.5|test examples/links and time findability |
| Upgrade |8|8.5|execute both actual package gates and rehearsal |
| Environment |7|8|verify clean supported shell/platform prerequisites |
| Community |7|7|actual support/adoption evidence outside bug scope |
| Measurement |6|7.5|manual observer evidence and post-build results |
| Overall |7.125|8.0|all scores describe plan, not measured DX |

Prior project DX records concern RazorWire MVC forms, not this product journey; dimensions not comparable, prior trend N/A. Latest historical overall8/unresolved0 retained as different scope, not used to clear #845. TTHW current unknown (prepared estimate5–10min); target2–5min, competitive target only; cold/local/whole release timing unknown. Magical moment designed via canonical guide+existing local example. CLI Tool/.NET Library; DX POLISH. Zero-friction covered within prerequisites; learning by doing covered via example; uncertainty covered via evidence/checklist; opinionated/escape covered through explicit files/credentials while bypass excluded; real code context covered; magical outcome designed, execution pending.

#### DX implementation checklist

- [ ] Measure complete prepared-state target2–5min; separately record cold/local first proof, no invented benchmark.
- [ ] Exact-version install one command with SDK/feed prerequisites; first run prints safe meaningful evidence.
- [ ] Complete example manifests/transcript, owner/recipe step, one credential per command, same full reviewed file.
- [ ] Generated help and missing-input diagnostics document mandatory flags for one pair too.
- [ ] Every affected error has problem/cause/fix/verified docs destination; category map and bounds recovery.
- [ ] Canonical stage map/checklist differentiates planning/candidate/published/deployment gates and actual owners.
- [ ] Breaking invocation/migration and forward-only recovery guidance ships with code/release notes.
- [ ] Explicit noninteractive CI roots and cleanup/cancellation verified; safe outputs never include secrets.
- [ ] Existing feedback/security/license links retained; no promised free/commercial tier or support staffing.
- [ ] Docs examples/canonical links exercised against actual package/parser/recipe before claiming runnable.
- N/A: TypeScript, hosted playground/free tier, codemod, new search UI, new community channel; existing capabilities used, no scope expansion.

#### NOT in scope / reuse / TODOS

No hosted quickstart, public provider preflight API, JSON output flag, auto-repair, configurable security bypass, new CLI doctor or telemetry. Community expansion and recurring DX audits do not derive from this bug's accepted scope. No new TODO candidates; role retirement remains the existing deferred item. Reuse current example/prerequisite verifier/full manifest, DurableSchemaDiagnostics, CliFx help, canonical guide, CONTRIBUTING feedback, clean package roots and existing workflow carriers. Original/CEO obligations remain intact.

#### Implementation Tasks

- [ ] **DX-T1 (P1, human ~3h / agent ~30min)** — CLI/docs — Complete required-flag help and all safe diagnostic recovery paths. Surfaced by passes2/3 and native D3/D4/D5. Files: DurableSchemaCommand.cs, DurableSchemaCommandTests.cs, CLI README, troubleshooting/durable-diagnostics.md. Verify actual help/command failure tests, docs links, no raw secret text.
- [ ] **DX-T2 (P1, human ~3h / agent ~30min)** — operator docs/example — Complete one canonical staged gate/checklist and runnable one/two-pair transcript. Surfaced by passes1/4/5/6 and native D1/D2/D6. Files: heartbeat-retention-operations.md, operational-assessments.md, provider/CLI/Durable READMEs, examples/durable-postgresql/README.md and manifests, release guidance/root links. Verify parser/recipe/package-run commands and link consistency.
- [ ] **DX-T3 (P2, human ~30min / agent ~10min)** — acceptance — Run one manual full-clock DX validation and retain honest timing/understanding limitations. Surfaced by pass8/D34. Files: acceptance/proof receipt and existing example/operations docs. Verify documented starting state and end result, separately report automation/cold setup times.

No new tasks from pass7; other tasks intentionally overlap CEO tasks and will be merged by parent aggregation. No human answer fabricated. Final approval pending; actual planning inputs, implementation/tests, novice timing and outside coverage remain concerns, not unresolved auto-review mechanics.

<!-- autoplan-accepted:dx -->
- Complete the existing disposable PostgreSQL example and canonical operations guide with one-pair and full-plus-work_only manifests/transcripts: prerequisite start, owner input, matching packaged recipe invocation, explicit runtime credential per command and unchanged complete file. Clearly distinguish local/source proof, exact candidate/public artifact proof and application-owned deployment gate. Source examples cannot substitute for actual package evidence.
- Make both required preflight flags and their one-pair transition visible in generated command help, safe missing/invalid-input diagnostics, CLI reference and release/upgrade guidance. Preserve explicit independently reviewed manifest/owner and environment-variable-only credentials; no inferred defaults, partial manifest or bypass.
- Map every stable affected failure category to canonical diagnostic guidance with bounded safe pair/role or unexpected numeric OID, problem/cause/fix and verified nearby docs link, supported inspection/reviewed repair and full-manifest rerun. Do not promise that the recipe repairs every drift or expose raw provider errors, JSON, SQL or secret values. Document recovery for 64KiB/depth8/32-pair/30-second limits; exceeding authorized pair count requires separate review, never partial checks or broader grants.
- Keep one authoritative operator workflow/checklist in the schema-11 operations guide mapping planning, candidate, published and deployment stages to responsible actual owners, required non-secret inputs, exact byte/run identities, receipts and closed gates. Cross-link reference/detail/example pages and replace obsolete #795 promises together. Preserve all staged prerequisite and continuous guard/identity rules.
- Validate affected help, examples, parser/recipe grammar, canonical link targets, supported Bash/SDK/container prerequisites, noninteractive clean package roots and safe cancellation/cleanup alongside the existing branch and package proof requirements. Document internal API shape/ownership/defaults/constraints/decision/pitfall content as well as public usage.
- Include one manual DX acceptance observation from the documented prepared operator start through guide reading, exact-version install, both runtime commands and correct interpretation of remaining gates, targeting2–5minutes without claiming measured success. Separately record cold disposable setup and command/CI durations. Report misses and unknowns honestly; this target is not a new timing release gate and cannot waive existing proof. Add no hosted telemetry or recurring process.
<!-- /autoplan-accepted:dx -->

### Engineering Step0: selected target, scope challenge and evidence

Target/report remains the #845 complete-runtime-preflight plan above. Exact current methodology read all2259 lines: 1–300,301–600,601–900,901–1200,1201–1500,1501–1800,1801–2100,2101–2259; all four returned ranges EOF complete. Parent engineering driver1536–1740 loaded. Startup/brain/Aside probes inherited as required; no nested startup or standalone outside fallback.

Existing authored sources map the subproblems: DurableSchemaPreflightCommand and internal service (DurableSchemaCommand.cs:140/294) own the user operation; provider GetStatusAsync(existing connection):145 owns its own transaction and private ReadStatusAsync:466 owns migration checksums and metadata; recipe transaction lock is the same package key; provider epoch initialization/rotation are exclusive writer operations; PostgreSqlSchemaIntegrationTests owns role/policy/lane proof; mixed-version tests cover prior binary; verify-packed-consumers plus PackageIndex publishing/tool-proof helpers own frozen package identities. Existing xUnit/VSTest/coverlet with Testcontainers is authoritative; no new framework/dependency. CLI already references provider and Npgsql. AssemblyInfo.cs exposes only Tests/TestHost, so narrow CLI friend access is an actual required integration change.

Scope counts are estimated proposed work, not files merely read: roughly22–26 files (CLI operation/input types+tests, provider seam/AssemblyInfo+tests, source/package proof scripts and two workflows/tool proof carrier, modeled fixture/example and8+doc surfaces), about4 immutable record types plus1 loader and existing service changes, not4 new public services. Complexity threshold trips. No feature cut proposed (Autoplan P2 never reduces). D35 auto A original arrangement: keep existing CLI/service/provider ownership, add at most focused CLI manifest/request/result file and focused verifier implementation, use existing source/package carriers; do not create a public provider preflight layer or framework. Alternative smaller arrangement would put all new parser/query/DTOs in alreadylarge DurableSchemaCommand.cs and save1–2 files but worsen clarity; both retain all features. This is an arrangement taste choice, not a scope reduction. Pending remedies: exact status seam/fence cleanup and combined guard vehicle; not approved by arrangement selection. Scope accepted as-is/FULL_REVIEW.

Calibrated preliminary findings: S1 P1 confidence9, provider:149 `await using var transaction = await connection.BeginTransactionAsync(cancellationToken)` plus:152 `await AcquireStatusFenceAsync(connection, transaction, cancellationToken)` establishes existing overload cannot participate in caller-owned repeatable-read transaction. Required internal seam remains a design dependency; no product regression claimed. S2 P1 confidence9, CLI:154 `Service.GetStatusAsync` and:166 `Service.VerifyRetentionPreflightAsync` are separate bounded calls, so source is not one snapshot; source plan already authorizes replacement with one operation. S3 P1 confidence8, plan's `dedicated owner guard connection ... all lane proofs and activation` requires an executable fixture/owner procedure, and epoch initializer:287 `await AcquireMigrationLockAsync(connection, cancellationToken)` forbids interpreting activation as an epoch mutation while another shared guard exists; verify semantics and choose vehicle in Architecture. S4 P1 confidence8, existing stable/prerelease smoke-install has help-only proof and source package scripts may repack; exact artifact-consuming carrier is required, not a new release scope. No speculative bugs promoted.

Search check [Layer1]: official PostgreSQL16 explicit-locking and application-consistency docs establish session/shared locks and snapshot-after-lock requirement; Npgsql basic usage/pooling documents data source ownership and that dispose returns pooled sessions. Existing package checksum/status helper and platform catalog arrays/ACL functions are the reuse rungs. No new migration engine. Recent target history: rolepairs #826, retention #825, health #810; no relevant revert found in bounded path history. Full TODOS.md read: role retirement already has complete approved What/Why/Pros/Cons/Context/Depends item; no duplicate needed. Other deferred items are unrelated and stay fixed. Source/public/candidate OS/clean-root distribution remains required in current carriers.

Scope auto-answer record: features unchanged(original OH1–OH4/CEO/DX obligations); structure A via D35/Autoplan P5, exact original ownership and minimal focused files; pending S1/S3 mechanism choices remain pending until Architecture. Readback/semantic comparison performed before native dispatch; selection grants no implementation authority.

### Engineering primary review and decision ledger

Native Hypatia completed INPUT eng 9e0285a6baec49916b3ad9f3aa5d4be79f0bbb2ced9bcb7a8c392b89509e680d; agent01a0f511-cb55-7bc3-81f4-27ec26d713a8 closed. Two findings (P1 undefined guard execution owner/path; P2 per-command versus combined pool limits) were published in full. Fresh outside probe enabled/not_installed for Claude Code; no completed external voice, all six consensus dimensions N/A. Native findings are not primary section counts and not cross-model-confirmed challenges.

#### Section1: Architecture (3 findings)

**A1 [P1] confidence9/10** — provider schema manager:149 `await using var transaction = await connection.BeginTransactionAsync(cancellationToken)` and:152 `await AcquireStatusFenceAsync(connection, transaction, cancellationToken)`. Existing status overload owns its transaction. Proposed CLI unified snapshot must reuse private ReadStatusAsync:466 rather than invoke this overload or copy checksum logic. D36 auto A, mechanical P1/P5 [Layer1]: add `internal ValueTask<DurableRuntimeSchemaStatus> ReadStatusInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)` forwarding to the current reader. Require non-null open connection and active transaction belonging to that connection; document caller ownership/fence/snapshot obligations. No connection acquisition, fence acquisition, transaction begin/commit/rollback or resource disposal inside the seam. Add only CLI friend assembly to existing AssemblyInfo; the CLI already references provider. Existing public and internal status calls continue their current acquisition/transaction behavior and call the same reader. Alternative copying checksums is rejected DRY; public provider preflight scope remains rejected. Tests protect both callers and current status behavior.

**A2 [P1] confidence9/10** — CLI:154 `Service.GetStatusAsync` and:166 `Service.VerifyRetentionPreflightAsync`; current calls cannot produce one coherent result. D37 auto A, TASTE DECISION [Layer1]: use one dedicated physical, nonpooled CLI data-source connection (`Pooling=false`, session-affine endpoint, `Enlist=false`, multiplexing disabled) for the new preflight only. Acquire session shared `pg_advisory_lock_shared` on existing internal MigrationAdvisoryLock before BeginTransaction(RepeatableRead); then `SET TRANSACTION READ ONLY` before any catalog query, reused provider status, caller identity, exact raw catalog checks, result projection, transaction completion, explicit shared unlock and physical disposal. Session lock is chosen because starting repeatable read before a SELECT lock wait can freeze stale metadata; a prior short transaction/session-lock acquisition could work but adds a transaction boundary. Keep current status/script/apply behavior. Never silently honor default_transaction_read_only/isolation or ambient transactions as proof of requested mode.

One monotonic 30-second online budget starts before connection acquisition; linked caller cancellation bounds every work command and lock wait. Reserve up to2seconds of that same total budget for transaction rollback, shared unlock and physical close; work stops by28seconds (earlier caller cancellation still triggers cleanup). Cleanup uses an independent bounded token limited by remaining absolute deadline, not unbounded CancellationToken.None. Check unlock boolean; uncertain/failed cleanup prevents success. Stage success locally and emit only after all owned resource cleanup succeeds. Set connect/command/cancellation timeouts from the remaining budget; never allow connection-string overrides to extend the approved total. On broken transport, dispose the nonpooled physical session and fail closed; a resource probe must demonstrate driver close/cancel behavior fits the total. If it cannot, implementation may not claim the30second contract or ship a success path until repaired. Current migration cleanup is best-effort and is not adopted as a success-producing preflight policy. Alternate tested pooling with proven reset/eviction could preserve behavior but complicates leaked-session proof. No public pool setting added; MaxPoolSize=1 remains a supplied-configuration regression test proving no nested acquisition, while combined pools below are separate. Nonpooled extra connection setup cost must be measured.

Session-scoped locks require a stable backend; transaction/statement poolers are outside this maintenance path. [PgBouncer feature matrix](https://www.pgbouncer.org/features.html) lists session advisory locks unavailable under transaction pooling; [Npgsql basic usage](https://www.npgsql.org/doc/basic-usage.html) explains normal dispose returns a pooled connection. Document direct/session-affine connection prerequisite. Enforce Npgsql settings; endpoint affinity cannot be inferred from a connection string, so reviewed deployment/proof prerequisites and backend-identity probes retain that uncertainty. [PostgreSQL16 application consistency](https://www.postgresql.org/docs/16/applevel-consistency.html) governs snapshot ordering; [advisory locks](https://www.postgresql.org/docs/16/explicit-locking.html#ADVISORY-LOCKS) govern lifetime. No promise to protect privileged administrators ignoring the lock.

**A3 [P1] confidence8/10** — current plan requires guard from first runtime through activation; native N1 identifies missing execution vehicle. Epoch initializer:287 `await AcquireMigrationLockAsync(connection, cancellationToken)` is an exclusive writer, so define activation precisely. D38 auto A, TASTE DECISION: the artifact-consuming disposable consumer controller owns one dedicated nonpooled owner guard connection and a linked cancellation scope. It acquires the same session shared key after all migration, role reconciliation and any explicit epoch bootstrap/rotation; retains that physical session across actual packaged CLI child commands and package consumer lane checks. It verifies guard/backend identity before/after each stage, monitors guard I/O loss with bounded checks while children run, cancels children and invalidates the whole pending receipt on loss, cancellation, timeout, unsafe cleanup or a stage failure. No receipt is activation-authorizing after the guard closes. Test fixture activation is an explicit consumer-owned callback enabling workers on the unchanged already-approved epoch; it must honor guard cancellation, stop/drain any started fixture host on loss during transition, and complete under the guard. Record guard window identity, start/end, each distinct caller, StoreId/epoch, immutable input hash and exact package identity. Only a complete no-gap receipt is finalized; partial receipts are diagnostic-only, never promoted/reused.

Production remains application-owned: the Skoolit deployment/certificate owner must supply and validate the equivalent session-affine coordinator and cancellation/closed-activation procedure before activation approval. Individual CLI commands do not supply this coordinator and cannot carry a guard across invocations. A terminal `psql -c` that disconnects cannot hold it. Do not ship a new public provider guard API, CLI daemon or automatic production activation. The disposable controller lives with existing proof/consumer infrastructure; compiled consumer references exact provider package, installs actual CLI package and uses already-required Npgsql, not source project references. Alternative documenting a wholly manual long-lived session is less verifiable and needs operator supervision; default selects executable proof controller plus explicit adopter-owned production integration. This choice preserves all CEO guard requirements and makes their mechanism reviewable.

D39 auto A (P1 mechanical): per-preflight single connection/MaxPoolSize1 is separate from combined workflow resource limits. Combined controller has1guard connection, one sequential runtime CLI process at a time, and separately bounded lane consumer pools; never share the guard's one-slot data source with runtime credentials. Roles/connections remain distinct. All32 runtime entries are sequential, with one receipt per caller. Guard has the existing proof stage/job total deadline, while each preflight retains30seconds. Do not introduce unbounded global parallelism or a new longer job timeout. A queued exclusive writer may cause a later reader to wait; no fairness guarantee is assumed. The between-pass writer test must prove either all readers safely complete before the writer, or bounded failure invalidates the entire gate, releases guard, lets writer finish, then requires a full rerun. It may never accept stale partial evidence or deadlock indefinitely.

Architecture graph (new proposed files/types remain not available):

```text
Reviewed file + reviewed owner + secret env name
  -> CLI command [flags/validate before DB]
     -> immutable manifest/request loader [<=64KiB, hash exact bytes]
     -> existing IDurableSchemaCommandService new unified operation
        -> owned nonpooled connection + shared session fence
        -> caller-owned RR/read-only transaction
           -> provider ReadStatusInTransaction -> existing ReadStatus/checksums
           -> raw role/policy/ACL/index facts -> universal exact comparisons
           -> safe immutable result [caller,pair,StoreId,epoch,hash,failures]
        -> transaction end / unlock / physical dispose -> emit result or fail

Frozen candidate bundle / clean public restore
  -> existing release carrier -> disposable exact-package consumer controller
     ->1owner guard [shared lock, stable backend, cancellation monitor]
     -> packaged CLI run(runtime1) -> run(runtime2) -> ... -> run(runtimeN)
     -> exact-package forwarding + Source lane consumers
     -> fixture activation on existing epoch + complete receipt under guard
  -> candidate receipt gates publication / published receipt gates deployment
Deployment owner -> equivalent coordinator -> actual activation remains external
```

Security/coupling: manifest, expected owner and candidate bytes are independent authorities; catalog never becomes authority. Parameter arrays/text and OIDs avoid identifier interpolation. Membership/effective permissions are universal and raw unexpected targets remain observable. Restricted roles remain leaves and runtime-owned heartbeat deletion trust stays documented. Provider friend access is a coordinated package build boundary, not public API. No schema migration changes, credential creation in production or public registry. Single database is the existing shared failure domain; no per-lane authorization claim. Failure in any dependent stage closes its own gate and cancels further stages. All3 architecture findings mapped to accepted mechanism/test requirements; no observed implementation success claimed.

#### Section2: Code quality (3 findings)

**Q1 [P1] confidence9** — CLI runtime_role:331 `WHERE cardinality(policy.polroles) = 1` with:337 `HAVING count(*) = 1` is current singleton inference. D40 auto A (original OH complete-set contract): replace it with explicit expected-role parameter rows, raw array cardinality/set checks before any resolving join, universal privileges, and fixed category mapping. A left/exact resolution must preserve missing names; no filtered candidate list, inner join loss, EXISTS over expected roles, OR aggregation, null=success or empty set allowed. Separate catalog fact reading from pure comparison enough to test malformed/null query shapes without private reflection; immutable result protects exact observable behavior. This is root-cause replacement, not a special two-role exception. No public fact API.

**Q2 [P2] confidence8** — proposed parser and status reuse must avoid duplicate contracts and option-heavy helpers. D41 auto A (P1/P5): use strict Utf8JsonReader/System.Text.Json already in .NET, detect duplicate decoded property names at each object scope with ordinal sets before materialization, validate strict UTF8 and reject invalid UTF16 escape results/controls, name byte length and original case. Copy to immutable arrays/records, never expose a mutable List behind IReadOnlyList. Byte SHA computed once from bounded read; no repeated file reads, race-sensitive later hashing or JSON canonicalization. Keep typed profile enum internal mapping with explicit v1 spellings, fixed category order and pair index identity. No new parser dependency. Path/read errors expose categories not exception paths/content. These are exact approved grammar/immutability requirements, not a format extension.

**Q3 [P2] confidence8** — verified shared reuse versus abstractions. Existing provider public/internal status and CLI proposed preflight genuinely share `ReadStatusAsync`'s normalized embedded migration/checksum/metadata contract. Shared seam D36 avoids reimplementing ~170lines with ~15–30lines wrapper/friend/docs; actual total diff grows for tests, deliberately for reliability. No lines removed are counted twice: old reader remains, copy avoided is not claimed removal. Existing PackageIndex CoverageCliConsumerProof.cs and PackagePublishing.cs own separate candidate/public workflows; share only the new artifact-consuming durable proof entry and exact package identity validation, preserving source/feed/timeout differences. Do not merge source-verification scripts that repack with artifact-only mode. Smallest adoption: candidate controller first, then public carrier feeds verified restored bytes to same controller. Proposed caller common contract named; introduced paths unavailable until implementation. Estimated new proof behavior grows total code; no net-savings claim. D42 auto A (P1 mechanical): one carrier with explicit candidate/public inputs, no generic runner or second migration implementation. Targeted regression tests preserve public status/script/apply, existing package smoke and old-binary proof. Current diagrams none in affected CLI; add/update inline owner/transaction diagram where new ordering is complex.

Error/edge checklist: IO limit/encoding/depth/duplicates/name collision, SQL OID completeness, policy PUBLIC/unresolved targets, default ACL/grant option, missing/extra functions/indexes, elevated roles/membership/ownership, caller assumptions, missing/invalid metadata, cancellation/driver cleanup, exact artifact mismatches and partial receipt never succeed. D43 auto A, mechanical regression preservation: keep the provider's existing nullable uninitialized active-epoch status semantics. The immutable preflight projection carries Guid? ActiveRuntimeEpoch and prints a safe explicit uninitialized state; structural compatibility does not initialize an epoch. The combined receipt/activation requires a nonempty already-approved epoch and matching StoreId, and rejects null/empty/mismatch. Bootstrap/rotation occurs before the combined guard, never from its activation callback. A Guid.Empty epoch may not authorize activation. This avoids silently making current schema status/preflight order depend on activation. Required API XML and canonical reference/decision/pitfall docs ship with internals. No schema registry, polymorphic verifier framework, broad service extraction or model reflection. All3 quality findings have necessary accepted proof, no new standalone scope task.

#### Section3: Tests — five steps complete, 24 changed-contract gap groups

Step1 dedicated source/test reads preceded this diagram: complete DurableSchemaCommand.cs; existing CLI tests735–965 and1–125; provider status135–166/466–660; role-pair/lane proof2600–2840; packed-consumer script1–210 and package helper3770–3835; status fence3436–3515 and old binary85–173. The current plan was read in full from the immutable Eng input. Proposed files/functions remain proposed. Framework detected from actual projects: xUnit + Microsoft.NET.Test.Sdk/VSTest, Testcontainers PostgreSQL pinned16.5; no new framework.

Step2 operator flow: read canonical guide -> supply independently reviewed whole file/owner -> help/required-option validation -> bounded local load -> secret environment name validation -> runtime credential connects -> fence -> snapshot -> compatibility -> caller/set/structural checks -> cleanup -> explicit result -> repeat with each distinct runtime -> guarded lane/fixture activation -> retained complete receipt. Missing/invalid options stop before provider work. Interrupted terminal/process, stale reviewed inputs, two simultaneous invocations, slow/blocked connection, duplicate runtime invocation, changed file between invocations, lost guard and unexpected catalog repair are user-visible failure paths. Retry uses the same reviewed complete input and a fresh complete combined window; closing or interrupting a process cannot authorize activation. No browser routes are added.

Step3 baseline search identifies actual qualifying behavior tests: CLI Preflight_fails_with_one_safe_problem_cause_fix_and_docs_block:41; pending0011:61; allpending:83; structuraldrift:102; online failures:193–290; MapRetention shape:328; singlepairowner/runtime:746; signature:795; descendingindex:803; RLS:811; elevatedrole:821; DELETE/TRUNCATE:831. Provider RoleRecipe_ReconcilesFullAndWorkOnlyPairsAndRejectsOmissionAtomically:2607 covers real recipe reconciliation/rerun/omission and forwarding/Source denials, not the new packaged CLI gate. ModifiedMigrationHistory:302, RenamedMigrationHistory:3326, NonContiguousMigrationHistory:3342, SchemaStatus_ClassifiesIncompleteInvalidUpgradeAndUnsupportedStores:3361, statusfence:3454, migrationcancel:3490, EpochActivation_WaitsForTheSchemaAdvisoryLock:3630 cover provider behavior, not caller-owned new RR transaction/cleanup. ExactPreviousPackage_OperatesAfterSchema11AndSupportsBinaryRollback:85 covers exact preview.8 protocol, not a two-pair schema10 starting state. Existing source packing/clean restore bytes are useful package checks but cannot prove consumption of a frozen release bundle. These tests are retained/extended; none alone covers a new-contract group completely. Baseline assertions rank ★★★ where drift/errors are asserted and ★★ for the single-pair pass; help-only package smoke ranks ★ and is not coverage of this gate.

Step4 coverage diagram (branch categories below exhaust planned behavior; no line/branch percentage claimed):

```text
CODE PATHS                                          USER FLOWS
CLI ExecuteAsync                                     Guide/help/install
  flags/console/service guards -> [GAP G01]             required flags/one-pair transition [GAP G01/G24]
  ReadManifestAsync -> [GAP G02–G06]                   File custody/review
    open/read bytes<=64KiB                               unreadable/edited/oversize [GAP G02/G06]
    strict UTF8/no BOM -> parse(depth<=8)                 malformed/unknown/duplicate [GAP G03/G04]
    exact v1/pairs/profile/name -> copy/hash              0/1/32/33 & valid unusual names [GAP G05]
  env name/value -> RunOnlineAsync                     Connection UX
    invalid/missing/config/error/cancel [GAP G07]         safe diagnostics; retry/interrupt [GAP G07/G24]
  UnifiedPreflightAsync -> [→E2E]                      First runtime credential
    connection/open/settings/budget [GAP G08]             slow/open/timeout no success [GAP G08/G19]
    shared SESSION fence BEFORE RR [GAP G09]              preceding writer -> fresh [GAP G09]
    SET TRANSACTION READ ONLY                            later writer waits -> complete [GAP G09]
    ReadStatusInTransaction -> same reader [GAP G10]       pending0011/drift safe fix [GAP G10/G24]
      schema/history/metadata absent/invalid
      checksum/order/name; missing/upgrade/too-new
      compatible; nullable epoch explicit
    session/current caller classify [GAP G11]             owner diagnostic != runtime [GAP G11]
    resolve full names/OIDs/owner [GAP G12]                healthy first + unsafe second [GAP G13–G17]
    runtime attributes/members/ownership [GAP G13]
    raw runtime/owner policies [GAP G14]
    raw/default/effective ACL for EVERY role [GAP G15]
    EVERY runtime DELETE/TRUNCATE [GAP G16]
    function/RLS/index/other policies/health [GAP G17]
    result shapes/order/hash/identity [GAP G18]            changed inputs/identity [GAP G18/G20]
    complete/rollback/unlock/dispose [GAP G19]             cancellation/cleanup uncertainty [GAP G19]
      any uncertain stage -> nonzero; no success
Exact-package controller -> [→E2E]                    Complete guarded window
  validate candidate or public bytes [GAP G22/G23]        frozen feed/cache/bundle mismatch [GAP G22/G23]
  bootstrap before guard -> owner guard [GAP G20]         guard loss invalidates all [GAP G20]
  packaged runtime1..N -> unique evidence [GAP G20]       duplicate caller cannot cover pair [GAP G20]
  one/enroll/rerun/modeled10->11 [GAP G21]                 each recipe/lane/epoch unchanged [GAP G21]
  forwarding & Source provider consumers [GAP G21]        actual denied SQL surfaces [GAP G21]
  fixture activate(existing epoch) [GAP G20]              cancellation stops/drains host [GAP G20]
  finalize receipt / release guard [GAP G20]              receipt not future authorization [GAP G20]
  carrier gates stable + prerelease [GAP G23]             missing proof blocks next gate [GAP G23]

Legend: [GAP] incomplete coverage of changed contract; [→E2E] real integration boundary.
Baseline tests above remain useful; no new implementation/tests have run.
CHANGED-CONTRACT COVERAGE: 0/24 fully covered groups today; 24 mapped gaps.
This is a conservative group inventory, not a measured code-coverage percentage.
LLM/eval: N/A; no product prompts, model calls or tool definitions change.
```

Step5 D44 auto A (mechanical P1/P3): carry forward all exact approved regression contracts at full depth; extend behavior tables/shared fixtures rather than add implementation-mirroring tests. All24 groups below are required work pending consolidated approval, not asserted present. No optional verification-depth or new runtime policy is introduced; Lake kind-choice denominator0. No reflection or test-only public exports. The status seam is needed by the production CLI; malformed result tests use intentionally exposed internal comparison boundary. Cancellation/transport errors are exercised with real PostgreSQL backend termination and subprocess boundaries rather than private hooks.

| Gap | Inputs/branches and observable assertions | Owning tests/type |
|---|---|---|
| G01 CRITICAL | Required file/owner absent, blank, invalid, missing console/service; help names both flags and one-pair requirement. Invalid input makes zero DB calls; direct-command tests provide required valid inputs before testing other guards. | Extend DurableSchemaCommandTests + new DurableSchemaPreflightInputTests; unit/actual CLI help |
| G02 | Missing/unreadable/directory file, pre/mid-read cancellation, exact65536bytes vs65537, stream errors; safe category, no raw path/content/provider call, no partial read accepted. | InputTests; unit file boundary |
| G03 | Invalid UTF8, BOM, malformed JSON, comments/trailing commas, unpaired escaped surrogates, depth8/9; fail before DB. Valid non-ASCII decodes without alteration. | InputTests; table unit |
| G04 | Root/pair duplicate decoded properties (including escaped equivalents), unknown/missing property, wrong numeric/version/string/array/object/null types; exact v1 grammar only. | InputTests; table unit |
| G05 | Pairs0/1/32/33; full/work_only accepted, invalid/missing profile rejected; roles empty/control/63 vs64UTF8 bytes/quotes/spaces/case/nonASCII; repeated/overlapping names across kind/entry; owner same rules/disjointness. | InputTests unit + PG exact-name resolution |
| G06 | Mutate source arrays/file after load; exact byte hash preserved, pair order retained, whitespace/order edit changes hash but set semantics remain unordered; immutable collections resist caller mutation. No second file read. | InputTests; behavior unit |
| G07 CRITICAL | Existing env-only credential name/value/config validation, provider/cancel/timeout errors retained with new required valid inputs; no connection/password/envvalue/raw exception leak. No automatic apply/reconcile. | Extend CLI online error tests; unit + installed CLI negative |
| G08 CRITICAL | Supplied MaxPoolSize1, unsupported multiplexing/ambient enlist defaults overridden, direct/session-affine endpoint prerequisite, one operation session, no nested acquisition; connection failures/slow opens cannot exceed total budget or pass. Verify tx RR/read-only. | New DurableSchemaPreflightIntegrationTests; PG integration |
| G09 CRITICAL | Controlled preceding exclusive writer commits mutation before shared fence -> new snapshot sees it and fails; writer starting after fence blocked until checks/cleanup. Cancel/timeout wait, concurrent shared preflights, recipe/migration/epoch key equality. Do not rely on timing sleeps alone; observe backend/latches. | PreflightIntegrationTests + existing provider fence tests; PG integration |
| G10 CRITICAL | New internal seam null/closed/foreign/completed transaction rejected; successful caller tx remains caller-owned. Same schema missing/partial/metadata absent/duplicate/invalidStoreId/range; contiguous/name/checksum; upgrade/pending0011/too-new semantics through both status and preflight. Null epoch retained for structural status; no epoch mutation. | Provider SchemaIntegrationTests + CLI integration/unit; shared-contract regression |
| G11 | Owner input matches caller -> owner-diagnostic only; every runtime -> its pair index; dispatcher/retention/unknown/missing/SET ROLE identity mismatch -> failure. Assumed-role inputs where possible through actual session config/probe; synthetic unexpected result complements real DB. | PreflightIntegrationTests + comparator; PG/unit |
| G12 | Missing first/second/owner name, quoted-case-sensitive distinct names, exact name vs truncated alias, duplicate/reused OID, runtime/dispatcher/retention/owner overlap; all resolutions retained even on failure. Fully consistent wrong-owner reassignment fails. | PreflightIntegrationTests + catalog comparator; PG/unit |
| G13 | Independently each runtime LOGIN false; each forbidden SUPERUSER/CREATEDB/CREATEROLE/REPLICATION/BYPASSRLS; incoming/outgoing membership even NOINHERIT; database/schema/table/sequence/function ownership. Isolate second runtime while first remains healthy. | Extend CLI role drift table; PG; unconstructible result cases unit |
| G14 | Raw policy missing/extra/duplicate/unresolved/PUBLIC/dispatcher/retention target, correct unordered full set vs omit first/second; command/permissiveness/USING/WITH CHECK, exact2 policies, owner singleton. Raw OID shapes not representable by supported DDL use comparator tests, never unsafe system-catalog writes. | PreflightIntegrationTests + comparator; PG/unit |
| G15 | Per-runtime direct/effective EXECUTE and no grant option; revoke either; extra PUBLIC/dispatcher/retention/unreviewed grantee, grant option either; null ACL resolves PostgreSQL defaults then fails PUBLIC; owner legitimate implicit rights retained. Same rules for due-health. | Extend ACL tests; PG + synthetic malformed facts |
| G16 CRITICAL | DELETE/TRUNCATE direct, PUBLIC or inherited for first then second; deny every runtime, no existential pass. Member-edge rejection preserved even where grants are not inherited. Run owner + both credentials where connectable. | Extend existing effective privilege theories; PG |
| G17 CRITICAL | Missing routine; procedure/wrong argument/result/set-return; owner/securitydefiner/search_path drift; RLS enable/force independent; index missing/invalid/notready/unique/nonbtree/wrongkeys/include/expression/predicate/opclass/order/nullorder; exact other3 policy sets and due-health owner/ACL. Preserve each original guard independently. | Extend structural drift table; PG, constructibility-limited facts unit |
| G18 | Null/empty/unexpected row/result/field/OID; deterministic bounded fixed categories/order; validated names/pair or unexpected numericOID only; safe output includes hash/owner/count/caller/StoreId/nullableepoch. Result mutable inputs copied. Allruntime results must bind same reviewed identity. | Comparator/result + console tests; unit/installed CLI |
| G19 CRITICAL | Cancel/open/lock/query/commit/rollback/unlock/transport/physicalclose; false unlock and uncertain cleanup never print success. Single absolute30sec incl cleanup, work28sec reserve; prove no owned locks/backend/pool slot remain after failure/retry. Driver-close budget probe required; no unbounded fallback. | PG integration/backend terminate + packaged subprocess cancellation |
| G20 CRITICAL | Dedicated guard distinct from CLI/lane pools; owner runtime labels not interchangeable; sequential1..32 evidence, duplicate/missing caller/hash/StoreId/epoch reject; null/empty/unexpected epoch stops activation; allbootstrap beforeguard; guard loss between/during passes/activation cancels children and stops/drains fixture; queued writer either safely completes whole gate or bounded fail/release/writer/fullrerun. Partial/released receipts never reused. | Exact artifact consumer controller tests; real PG/subprocess E2E |
| G21 CRITICAL | Actual packaged one pair; enroll second unchanged identity and lanes before/after; identical rerun stable catalog; modeled two-pair schema10 snapshot before candidate CLI0011apply -> packaged recipe -> both runtime passes/heartbeat/prune/lane tests. Forwarding Work/Flow/Schedule and Source Work + directSQL/Flow/Schedule denies remain. Previous exact preview.8 rollback retained separately. | Extend role-pair/source proofs + new exact-package scenario; PG/package E2E |
| G22 CRITICAL | Candidate exact matching CLI/provider IDs/version/source + individualSHA256/bundleSHA512+recipe/manifest/fixture/image; clean explicit tool/consumer roots, no project refs/local-source substitution/repacking. Wrong package/cache/feed/recipe/missing proof/runidentity rejects. Freeze bundle only after complete receipt. | Shared artifact proof entry + PackageIndex tests + installed tool/consumer E2E |
| G23 CRITICAL | Stable/prerelease always run candidate proof per new bundle; public clean restore byte-compare expected frozen bundle before same proof, mismatched/missing receipt blocks deployment; candidate failure blocks publish. Preserve carrier retry/failure/artifact retention and existing30min pack/20min source/70min smoke budgets. | PackagePublishing/CoverageCliConsumerProof tests + workflow proof; unit/carrier/package E2E |
| G24 | Canonical links/requiredhelp/examples grammar/runorder/SDK10BashDocker/noninteractive roots/cleanup; one full and full+work_only file with both runtime commands. Manual prepared clock target2–5min vs separately observed cold setup; report unknown/miss, no waiver. | Docs/example/actual-help validation + manual DX observation |

Count24 gap groups (some share existing assertions); no arbitrary test-count target. Every group extends the owning behavior fixture unless the new production component needs a new named test file. In all connectable real DB negative cases assert owner plus every affected runtime cannot pass, relevant catalogs unchanged before/after, firstruntime cannot hide secondruntimefailure. Static-only smoke never substitutes for the PG/package boundary.

Tests made obsolete: none retired by default. Single-runtime-only fixture/SQL expectations and no-required-flags command construction are intentionally amended in place to pass full reviewed input while preserving their independent structural/error contract. Singleton acceptance is replaced by cardinality1..32 cases; it is not a license to delete the one-pair test. All public status/script/apply, migration/history, role recipe, old binary and package consumer regressions remain. No runtime prompts/model/eval scope. No new test framework or global test hook. Test requirements are auto-selected exact proof of accepted plan and await the final parent gate; no unresolved section choice.

Test Plan Artifact saved/read back: `/Users/andrew/.gstack/projects/forge-trust-Runnable/andrew-main-eng-review-test-plan-20261001-015000.md`;24 value cards each field <=160UTF8bytes, no missing card. This records required tests, not completed coverage.

#### Section4: Performance (2 findings)

**P1 [P1] confidence8/10 — bounded catalog work without lossy filtering.** D45 auto A, mechanical P1: one bounded file read/hash and immutable manifest allocation (64KiB, depth8, pairs<=32, role bytes<=63); a finite batch of parameterized catalog queries within one RR read-only snapshot, not one acquisition/query tree per runtime. Use expected OID/name arrays to constrain runtime attributes/effective rights and ownership-existence probes; aggregate membership/ownership violations in SQL where possible. Raw policy arrays and ACLs are observed before resolving joins, and SQL proves exact cardinality/no unexpected principal rather than returning an arbitrary capped subset. Unexpected principals/findings can be capped for display, never for acceptance. The user-controlled role count is bounded but a server catalog/ACL/history can be large or adversarial; cancellation/server statement budgets enforce failure, not a claim of constant cost. Provider migration history remains its established contract with shared normalized checksum reader, no cache-based shortcut or skipped rows. Test a large unrelated role/membership catalog and unexpectedly large targets/ACL while proving exact failure still occurs and output remains bounded. Avoid O(P) network round trips; a fixed query batch may still have O(P) effective privilege computation server-side. No new database indices or migration needed for at-most32 expected runtimes.

Memory: retain bounded raw file bytes once, immutable names/OIDs/fixed result inventory; aggregate large unrelated catalogs server-side or stream existence/results without accumulating unbounded unrelated rows. Fail closed on unknown/null shapes. Converting ACL/history into a truncated in-memory success set is forbidden. Hash and evidence outputs are small; secrets stay only in the existing credential lifetime. Query fact/result allocations measured during integration, not a speculative performance rewrite.

Caching: no cross-invocation status/catalog/effective-right cache. Fresh inputs/guard/snapshot are the whole gate; a TTL could hide grant/membership drift. Embedded migration resources may keep their existing cache semantics, manifest digest is computed once per loaded immutable request, pure expected sets prepared once. Receipts are immutable evidence of their completed window, not cache hits authorizing a later deployment. No distributed cache/telemetry/background service.

**P2 [P1] confidence8/10 — slow path and combined window budget.** D46 auto A, mechanical: preserve30second total CLI deadline with28second work/remaining<=2second cleanup reserve; finite lock/connection/driver cancellation budgets and no timeout override expansion. Nonpooled setup may add connection latency: measure at1/2/32roles with direct/session-affine endpoint and guard active.32 sequential commands at30seconds is a16minute upper-bound arithmetic before setup/lane/scenario work, not measured runtime or evidence the30minute pack budget suffices. All4 package scenarios, restore, disposable DB, guard monitor, lanes and cleanup must be timed stage-by-stage in both carriers. Keep existing pack30min, sourcePG20min and smoke-install70min job limits; no budget extension or selective proof waiver now. If measured maximum proof misses, optimize shared fixture setup and batch facts without weakening scenarios/identities; a budget change requires explicit review before adopting it. Monitor bounded guard session only during controller scope; no perpetual watcher or global polling. Writer/readers starvation is handled by totaldeadline/fullgatefailure, not optimistic fairness. Report mean/max stage and total observations with package/run identity; no p95 or capacity claim from one run.

Performance tests derive from G08/G09/G19/G20/G22/G23; large-catalog assertions also augment G12–G17. No separately invented benchmark framework. All2 findings map to necessary correctness/budget work, not optional optimization scope.

#### Failure-mode registry

| New path | Realistic failure | Error/handling and required test | Visibility |
|---|---|---|---|
| Flags/input/env | Whole file missing, malformed or secret-env config invalid | Stop before DB; G01–G07; category/problem/cause/fix/link | Clear safe nonzero |
| Connection/settings | Pool/ambient/multiplexing/backend affinity breaks lifetime | Enforced provider settings + reviewed session-affine prerequisite/probes; G08 | Clear failure; unknown affinity cannot be a pass |
| Fence before snapshot | Writer precedes read, wait freezes stale catalog | Acquire before RR; controlled orders G09 | Clear result from fresh snapshot or bounded fail |
| Shared provider reader | Foreign/completed tx or checksum/meta corruption | Explicit seam ownership and same compatibility logic; G10 | Existing safe incompatible diagnostic |
| Caller/set resolution | Assume role, omitted second name, ownership reassignment | Exact OIDs/owner/session_current equality; G11–G13 | Caller/role category, never implicit coverage |
| Policy/ACL/privileges | Unexpected raw target is lost by join; one unsafe runtime | Raw-before-join and universal comparisons; G14–G17 | Stable bounded failed checks |
| Immutable projection | Malformed row becomes success or output leaks data | No null/unknown success, copied results/categories; G18 | Clear safe failure |
| Cleanup | Rollback/unlock/close interrupted, dead transport | Stage output until checked cleanup, absolutebudget/physicalsession disposal; G19 | No success; retry after resource proof |
| Guard/activation | Transport loss, duplicate caller, queued writer or epoch change | Cancel children/stop-drain fixture/invalidate entire receipt; G20 | Entire gate closes; full rerun |
| Modeled upgrade/lanes | Schema10 seed misrepresented; Source denial lost | Assert starting snapshot and independent lane outcomes; G21 | Proof stage fails |
| Candidate/public bytes | Cached/source-built/mismatched artifact accepted | Exact source/run/byte/recipe receipt, clean roots; G22/G23 | Publish/activation gate fails |
| Guide/help/limits | Operator uses old command or interprets owner pass as approval | Mandatory help + canonical staged guide/manualclock; G24 | Actionable recovery; no hidden bypass |

12paths mapped;0 planned silent critical gaps (every path has error handling plus required tests). This is plan completeness, not proof that current or future code is bug-free. Actual tests24groups remain unimplemented.

#### NOT in scope

- Public provider preflight/guard APIs, database role registry, runtime doctor, new numbered migrations and function-body audit: no additional caller/contract demonstrated; existing focused verifier owns this change.
- Role-pair retirement: existing #823 TODOS item retained; deleting an installed pair remains rejected rather than silently reconciled.
- Production guard implementation, Source activation, real role/secret changes or publication: adopter/release responsibilities need actual reviewed inputs and their separate workflows.
- Hosted playground, recurring DX telemetry, new community/support SLA, progress spinner and duplicate formatter: no accepted need justifies these separate features.
- Catalog digest as substitute for fence, cached passes, inferred manifests/owners, partial role sets and widened privileges: contradict accepted complete independent authority.
- New queue fairness, parallel runtime credential runs or automatic longer proof budgets: no validated benefit; bounded sequential controller preserves honest closed gates.

#### What already exists and what is reused

Reuse [schema manager](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs) ReadStatus/checksum/metadata logic via D36 production-needed internal seam; existing status caller remains. Reuse [CLI service and guards](../../Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs), replacing singleton SQL rather than adding a second verifier. Existing [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) remains the grant/reconcile authority and input grammar; no migration bytes change. Existing [real pair/lane tests](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs), [old binary proof](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlMixedVersionCompatibilityTests.cs), [consumer infrastructure](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-packed-consumers.sh), PackageIndex candidate/public carriers and canonical example are extended, not rebuilt. Q3 quantified avoided duplicate reader and honest net growth; shared contract tests G10 and each migrated caller preserve their differences. Common artifact proof accepts explicit candidate/public inputs, while feed restoration/workflow budgets stay carrier-owned. No generic proof platform.

#### TODOS.md review

Current TODOS.md fully read during Scope Challenge. Existing role-pair retirement item already records motivation/pros/cons/context/dependencies and is retained. No new deferred feature accepted, no current item completed by this review,0 additions proposed and0 mutations. Keep scope cuts/deferred options in this Review record rather than manufacturing duplicate P3 tasks. CEO/DX proposed features were assessed as above; exact proof and documentation work belongs to this branch, not TODO.

#### Worktree parallelization strategy (proposed implementation only)

| Step | Modules touched | Depends on |
|---|---|---|
| S1 freeze internal types/status seam | Cli, Durable PostgreSql provider and provider tests | Reviewed actual planning inputs + consolidated approval |
| S2 input/unified verifier/CLI regression | Cli and Cli.Tests | S1 |
| S3 exact-artifact controller/schema10/lane evidence | Durable consumers and PostgreSql integration tests | S1 interfaces, then S2 packaged CLI |
| S4 candidate/public carrier integration | tools PackageIndex and release workflows | S3 artifact receipt contract |
| S5 canonical guide/help/example validation | Durable docs, Cli docs, examples, root docs | S1 contract, S2 help, S3/S4 final stage grammar |
| S6 final formatting/coverage/artifact review | all affected modules | S2–S5 merged |

3 proposed implementation lanes: A S1→S2 (shared CLI); B S3→S4 (shared durable proof, carries provider tests); C S5 documentation. Launch initial S3 scaffolding and S5 prose after S1 with their disjoint modules while S2 proceeds; await packaged S2 before S3 runtime proof, await S3 before S4; merge all then S6 sequential.2 overlapping windows,4 sequential dependency stages. Provider test module S1/S3 is a conflict: S1 seam tests land first, then B owns new provider integration cases. CLI help/test module S2/S5 is a conflict: S5 docs worker reads output and requests A changes, never edits shared command tests. Documents include examples scripts handled by C only after B hands off proof usage. No worktrees or implementation workers created in Autoplan. Future subagents follow repository combo/sub policy.

#### Implementation Tasks

Synthesized from findings; estimates assume existing .NET10, Docker/PostgreSQL proof infrastructure and reviewed planning inputs, and include integration/debugging rather than mechanically applying default ratios.

- T1 (P1, human ~8h / agent+gstack ~90min): freeze immutable input/result and internal transaction seam, replace singleton inference with complete raw-set/universal checks. Surfaced A1/A2,Q1–Q3,G01–G18. Files: Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs and proposed DurableSchemaPreflightInput.cs/DurableSchemaPreflightVerifier.cs; provider schema manager/AssemblyInfo; affected CLI/provider tests. Verify strict-input/comparison/unit and real schema/role/structural regressions.
- T2 (P1, human ~6h / agent+gstack ~90min): enforce fence-before-snapshot, absolute deadline, checked cleanup and regression/error-resource proof. Surfaced A2/D37,G08–G10/G19,P1/P2. Same owning modules sequenced after T1, not a parallel competing edit. Verify controlled writer orders, real transport termination, MaxPoolSize1, no lock/backend leak and safe no-success outcomes.
- T3 (P1, human ~10h / agent+gstack ~2h): add exact-artifact consumer controller with dedicated guard, honest schema10 fixture and all4 scenarios/lanes/identity receipts. Surfaced A3/D38/D39,G20–G22,P2. Files: existing Durable/consumers and packed consumer proof entry; provider role/lane integration tests; test-owned schema10 fixture. Verify actual installed CLI and exact provider packages, guard-loss/activation/drain/queued-writer cases and existing previous-package regression.
- T4 (P1, human ~5h / agent+gstack ~1h): wire shared proof into candidate and published stable/prerelease carriers, byte-identity receipts and measured budgets/fail propagation. Surfaced Q3/D42,G22/G23,P2. Files: tools/ForgeTrust.AppSurface.PackageIndex, stable/prerelease release workflows. Verify new bundle always runs, clean public restore exact comparison, missing/mismatch/failed proof closes publication or deployment gate.
- T5 (P2, human ~3h30m / agent+gstack ~45min): deliver canonical guide/help/examples/internal API XML/link validation and prepared-clock acceptance. Surfaced G01/G07/G24 and required DX decisions. Files: CLI/provider/root/Durable docs, troubleshooting guide, examples/durable-postgresql. Verify actual help/grammar/example flow/prerequisites/link targets and one manual prepared/cold observation. T5 overlaps CEO/DX tasks; parent aggregation retains these nonidentical rows with possible-duplicate markers. Implement their shared work once and keep the highest priority for any associated ship-blocking instruction.

#### Unresolved decisions and Completion summary

- Step0: scope accepted as-is after auto-selected arrangement D35; no original requirement cut.
- Architecture Review:3 issues found and auto-remedies specified.
- Code Quality Review:3 issues found and auto-remedies specified.
- Test Review: diagram produced,24 gap groups identified; all mapped to required proof, Test Plan saved/read back.
- Performance Review:2 issues found and auto-remedies specified.
- NOT in scope and What already exists: written.
- TODOS.md updates:0 items proposed; existing retirement item retained.
- Failure modes:0 planned critical silent gaps;24groups of implementation test work remain.
- Unresolved decisions:0 within this review; one consolidated human plan approval pending; actual release/deployment planning inputs remain an external conditional gate.
- Native: Hypatia2findings, completed exact matched Eng input. Outside voice: Claude Code unavailable/not_installed; six consensus dimensions N/A;0 cross-model-confirmed UserChallenges.
- Parallelization:3 proposed lanes,2 overlap windows/4 sequential dependency stages; no implementation launched.
- Lake Score:N/A (0coverage-depth choices; tests are exact proof of already accepted contracts, no arbitrary kind-depth selection).
- Eng status:issues_open (32 four-section findings/gap groups =3+3+24+2). Required build work is actionable and unexecuted; do not report clean simply because Autoplan selected remedies. Parent final approval does not mean test success.

<!-- autoplan-accepted:eng -->
- Freeze the narrow internal `ReadStatusInTransactionAsync(connection, transaction, cancellationToken)` provider seam forwarding to the existing schema/checksum reader. Validate open connection and active same-connection transaction; it neither acquires connections/fences nor begins/ends/disposes caller transactions. Add coordinated CLI friend access, document ownership/cancellation/reference/decision/pitfalls, and protect both old status callers and the new preflight with shared-contract tests. Keep public status/script/apply behavior and migration bytes unchanged.
- The unified CLI preflight uses one dedicated nonpooled physical connection with ambient enlistment and multiplexing disabled and a reviewed direct/session-affine endpoint. Acquire the existing migration-key shared session advisory lock before RepeatableRead, set transaction read-only before catalog queries, read compatibility/caller/all exact structural facts in that snapshot, complete the transaction, check explicit unlock success and physically dispose before emitting success. Preserve a single monotonic30second total deadline including connect/wait/cleanup; limit work to28seconds with up-to2seconds of remaining cleanup budget, independent bounded cleanup cancellation, and no connection-string timeout expansion. Any uncertainty/error/cancel/broken transport/cleanup failure is nonzero without success. Prove bounded driver close and no leaked owned locks/backend/pool resources; supplied MaxPoolSize1 must not trigger nested acquisition. Document unsupported transaction/statement pooler lifetime and verify backend affinity in proof; do not infer it solely from a connection string.
- Make the continuous guard executable in the exact-artifact disposable consumer controller: one dedicated nonpooled owner session acquired after all migration/reconciliation/epoch bootstrap, retained across sequential distinct runtime CLI commands, both lane proofs and a cancellable fixture activation callback on the unchanged reviewed epoch. Guard checks/monitoring are bounded by the existing stage/total deadline; loss/cancel/failure/unsafe cleanup invalidates all pending evidence, cancels/drains child processes and stops/drains any activated fixture host. The application deployment owner must supply and validate its equivalent session-affine coordinator before actual activation; individual CLI invocations or disconnected psql commands cannot provide it. Build no public guard API/daemon/production controller. Final receipts bind guard window, every actual runtime caller, manifest/owner, StoreId/epoch and exact artifact/run identity; partial or closed-window evidence cannot authorize future activation.
- Separate resource budgets: controller owns1owner guard connection,1runtime CLI process at a time and separately bounded lane pools; never share the guard one-slot data source with runtime credentials. A queued exclusive writer must lead either to safe whole-window completion or bounded whole-gate failure, guard release, writer completion and fresh complete rerun, never stale partial reuse or an assumed fairness guarantee.
- Preserve nullable uninitialized epoch in provider status and immutable preflight output; structural preflight does not bootstrap it. Combined receipt/activation requires a nonempty matching reviewed epoch and StoreId; bootstrap/rotation is completed before its shared guard. Verify null/empty/mismatch and ensure activation callback performs no exclusive epoch mutation under the guard.
- Implement strict UTF8/JSON duplicate-decoded-property validation and immutable copy/hash-once input/result handling using existing .NET APIs. Replace singleton inference with independent complete expected-name/OID authority and raw-before-join exact policy/default-ACL comparisons and universal effective privilege checks; no empty/null/unknown result may pass. Keep bounded deterministic safe diagnostics and every existing structural guard, including second-runtime-only failures. Reuse provider reader and one explicit artifact-proof entry across candidate/public carriers rather than copying checksum logic or repacking source in artifact mode.
- Implement every G01–G24 required behavior/regression group in the saved engineering Test Plan, using existing xUnit/VSTest/pinned PostgreSQL and actual exact-package subprocess/consumer boundaries. Include strict file/owner grammar/limits/names/hash/immutability; caller and independent owner classification; raw sets/default ACL/every role and ownership; all original structure; shared status seam and both writer orders; read-only/no mutation; timeout/cancel/cleanup/MaxPoolSize1; nullable epoch and guard loss/queued writer/duplicate credential; all4 actual package scenarios with honest modeled schema10 snapshot and both lanes; clean candidate/public byte/carrier failures; help/examples/docs/manual clock. Retain existing status/script/apply, role-recipe/lane, migration/checksum and preview.8 old-binary regressions; amend singleton assertions in place. No reflection, test-only public hooks, blanket test retirement or new framework. Tests must assert observable independent contracts rather than mere existence. Nearly complete changed-branch verification is the goal; report actual coverage/constructibility, do not claim this review measured it.
- Use a finite parameterized catalog-query batch within the single snapshot, bounded raw input/role/result allocations, server-side ownership/membership aggregates or streaming, no per-runtime nested acquisitions and no acceptance based on truncated raw ACL/policy/history data. Test large unrelated catalogs and hostile target/ACL sizes; deadline failures close the gate. No cross-run catalog/permission cache or receipt reuse. Measure nonpooled setup and1/2/32roles plus all scenario/restore/guard/lane/cleanup stage totals for both carriers. Preserve current30minute pack,20minute source compatibility and70minute smoke limits; optimizations cannot drop proof, and a timing change requires separate reviewed evidence. Maintain honest stage/run-bound performance records without unsupported capacity claims.
<!-- /autoplan-accepted:eng -->

### Consolidated Autoplan decision audit (D22–D46)

All auto-selections below are plan amendments under the invoked Autoplan override; taste choices remain provisional until the final parent gate. Earlier D1–D21 tables are preserved.46decision records total:39mechanical auto-decisions,7provisional taste decisions,0confirmed UserChallenges. No new human answer is fabricated.

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 22 | DX | Use prepared backend/platform operator persona and CLI/.NET library type | Mechanical | P6 | Actual existing-store adoption path; no brain persona available | Invent beginner/customer research |
| 23 | DX | Apply DX POLISH to existing command/adoption path | Mechanical | P1/P5 | Existing product and exact requirements | New greenfield service |
| 24 | DX | Define prepared-operator clock with target2–5min | Taste | P2 | Guide+exact install+both commands+interpretation is useful measurable journey | Cold setup clock as primary; still observe separately |
| 25 | DX | Use canonical guide plus existing local example as delivery vehicle | Mechanical | P5 | Existing package/operator path | Hosted playground |
| 26 | DX | Complete first-run prerequisites and exact command transcript | Mechanical | P1/P5 | Original artifact/input gate requires usable run order | Hidden prerequisite or source proof substituted |
| 27 | DX | Expose required flags and one-pair transition in help | Mechanical | P1 | Approved invocation change | Implicit defaults |
| 28 | DX | Map each safe failure category to recovery and canonical link | Mechanical | P1/P5 | Original diagnostic contract | Raw provider error or generic rerun promise |
| 29 | DX | Keep one authoritative staged planning/release/deployment gate map | Mechanical | P4/P5 | Original ownership and byte binding | Duplicated competing guides |
| 30 | DX | Ship explicit forward-only upgrade and package proof guidance | Mechanical | P1 | Original modeled upgrade/candidate/public contract | Claim preview8 proves two-pair schema10 |
| 31 | DX | Verify supported shell/SDK/container/noninteractive roots and cleanup | Mechanical | P1 | Existing deployment/example prerequisites | New bootstrap dependency |
| 32 | DX | Retain existing feedback/security/license links | Mechanical | P5 | Current repository pathways | New support/community promise |
| 33 | DX | Keep all scores/time assumptions inferred until observed | Mechanical | P6 | No measurement performed | Claim timing/success baseline |
| 34 | DX | Include one manual complete-clock observation | Taste | P2 | Reading and gate interpretation need a human observer | Automation-only timing omits comprehension |
| 35 | Eng | Keep existing ownership with1–2 focused CLI files | Taste | P5 | Parser/verifier separation clarifies already-large command file | Put all parser/query/DTOs in one large file |
| 36 | Eng | Add production-needed internal caller-owned status transaction seam | Mechanical | P1/P4 | Reuse checksum logic without nested transaction | Copy170lines or new public API |
| 37 | Eng | Use nonpooled session fence before RR with bounded checked cleanup | Taste | P1/P5 | Simple physical session lifetime prevents reset uncertainty | Pooled implementation with proven eviction/reset has higher proof cost |
| 38 | Eng | Use exact-artifact disposable controller for continuous guard | Taste | P1/P2 | Executable consumer path with activation callback is reviewable | Manual supervised long-lived session has weaker automation proof |
| 39 | Eng | Separate guard/CLI/lane resources and bound queued writer outcome | Mechanical | P1 | No starvation or partial-evidence acceptance | Shared one-slot pool or fairness assumption |
| 40 | Eng | Replace singleton inference with raw exact-set universal checks | Mechanical | P1 | Original complete1–32role contract | Two-pair special case |
| 41 | Eng | Use strict standard JSON parse and immutable hash-once records | Mechanical | P1/P5 | Original exact grammar/custody | New parser dependency or normalization |
| 42 | Eng | Reuse one artifact proof entry while preserving carrier differences | Mechanical | P4 | Exact bytes need common proof; feeds/timeouts differ | Generic runner or source repack substitute |
| 43 | Eng | Keep nullable epoch for structural status; reject it for activation | Mechanical | P1 | Preserve current status/initialization ordering | Implicit bootstrap under shared guard |
| 44 | Eng | Require all24 exact behavior regression groups | Mechanical | P1/P3 | Approved contracts need observable PG/package assertions | Smoke/reflection/blanket retirement |
| 45 | Eng | Batch bounded facts without truncating acceptance or caching passes | Mechanical | P1/P5 | Universal raw set safety within existing deadline | Per-role acquisition or lossy/cached success |
| 46 | Eng | Measure stage budgets and preserve existing timeouts | Mechanical | P1/P6 | No timing measurements justify an expansion | Timeout waiver or drop a scenario |

### Consolidated phase-close verification and cross-phase themes

CEO/DX/Eng methodology reads through EOF, required primary outputs, matching terminal native results, successful artifact writes and complete fresh phase-close packets were checked; visible parent completion announcements preceded each next phase. Design was skipped for no UI scope. All original complete-set/independent-owner/raw-policy-ACL/universal-privilege/caller/strict-input/schema10/artifact/lane/docs constraints remain in the current Implementation plan, supplemented by exact seam/fence/guard/resource/test decisions. Eng is last. Current verified implementation input SHA256 f555921ad6816f734572257826004862065fc3c480bcebf746afe308e5970a0a; close packet `/Users/andrew/.gstack/projects/forge-trust-Runnable/autoplan-eng-QFMxgg/close-packet.md`198lines,43488bytes. Subsequent additions here are Review record metadata/task reporting only, not changed implementation/accepted requirements.

Independent cross-phase themes: CEO and Eng identify the combined credential window/owner gap; CEO/DX/Eng connect exact candidate/public package evidence to staged owners/closed gates; CEO/DX require complete examples and useful safe diagnostics; CEO/Eng require fresh snapshot plus bounded resources. Repeated original constraints are retained and not counted as new independent defects. No native/external consensus or cross-model-confirmed challenge exists because every outside probe was unavailable.

CEO scope:5proposals,2accepted,1deferred,2declined; SELECTIVE_EXPANSION, all applicable sections complete. Independent spec round1 found2issues/quality7; parent clarified staged timing and moved prior verdicts, but round2 used a wrong path and was unavailable. No successful independent recheck of those parent repairs is claimed. Original office-hours PASS9 is a separate source-design review and cannot clear this CEO attempt.

DX inferred plan:7.125→8.0/10; prepared timing unknown (estimate5–10min), target2–5min. No measured improvement or cold-start claim. Eng:3architecture+3quality+24test gap groups+2performance=32issues_open with specified remedies;0planned silent critical gaps,0unanswered internal choices; saved tests are requirements, not executed tests. Final approval remains pending and actual release/deployment planning inputs remain a prerequisite. No code, schema, workflows, production roles/secrets or external activation has been changed by this review.

### Implementation Tasks (aggregated across phases)

Actual jq aggregation filtered main/current recent5commits, kept latest run_id per phase, deduplicated only exact(component, sorted files, title), then ordered priority/phase.12tasks remain:4CEO+5Eng+3DX,0Design. No exact duplicates. Possible semantic overlaps are explicitly marked rather than silently discarded; execute each invariant once in the common owning workstream and preserve its source findings. Do not sum overlapping phase estimates. Latest Eng task partition suggests ~32h30m human / ~6h45m agent+gstack, excluding input collection/approval waits, external production coordinator and unexpected debugging. These are estimates, not duration promises. Future implementation uses the3-lane dependency strategy above.

- [ ] **A1 (P1, human: ~8h / agent+gstack: ~2h) — Artifact proof** — Consume exact candidate and published CLI/provider bytes in reusable proof
  - Surfaced by: ceo-review CEO-T2 — N4/N5; source release carrier research
  - Files: Durable/verify-postgresql.sh, Durable/verify-packed-consumers.sh, tools/ForgeTrust.AppSurface.PackageIndex/PackagePublishing.cs, .github/workflows/nuget-stable-publish.yml, .github/workflows/nuget-prerelease-publish.yml
  - Possible overlap with Eng T1–T4 or docs T5; preserve CEO invariant.
- [ ] **A2 (P1, human: ~4h / agent+gstack: ~45min) — CLI/provider** — Freeze same-connection status seam and fence-before-snapshot proof
  - Surfaced by: ceo-review CEO-T1 — C1; Architecture/Error registry
  - Files: Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql/AssemblyInfo.cs, Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaCommandTests.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs
  - Possible overlap with Eng T1–T4 or docs T5; preserve CEO invariant.
- [ ] **A3 (P1, human: ~3h / agent+gstack: ~40min) — Combined activation proof** — Guard combined runtime/lane window and bind deployment identity
  - Surfaced by: ceo-review CEO-T3 — N3; separate snapshots can differ
  - Files: Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs, Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaCommandTests.cs, Durable/heartbeat-retention-operations.md
  - Possible overlap with Eng T1–T4 or docs T5; preserve CEO invariant.
- [ ] **A4 (P1, human: 8h / agent+gstack: 90min) — CLI and provider internal contract** — Freeze immutable inputs and reuse status in complete-set verifier
  - Surfaced by: eng-review T1 — A1/A2,Q1-Q3,G01-G18
  - Files: Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs, Cli/ForgeTrust.AppSurface.Cli/DurableSchemaPreflightInput.cs, Cli/ForgeTrust.AppSurface.Cli/DurableSchemaPreflightVerifier.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql/AssemblyInfo.cs, Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaCommandTests.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs
  - Possible overlap with CEO/DX rows; Eng supplies the final mechanism and regression matrix.
- [ ] **A5 (P1, human: 5h / agent+gstack: 1h) — Candidate and published release carriers** — Wire byte-bound shared proof into stable and prerelease release gates
  - Surfaced by: eng-review T4 — Q3/D42,G22/G23,P2
  - Files: tools/ForgeTrust.AppSurface.PackageIndex/PackagePublishing.cs, tools/ForgeTrust.AppSurface.PackageIndex/CoverageCliConsumerProof.cs, .github/workflows/nuget-stable-publish.yml, .github/workflows/nuget-prerelease-publish.yml
  - Possible overlap with CEO/DX rows; Eng supplies the final mechanism and regression matrix.
- [ ] **A6 (P1, human: 10h / agent+gstack: 2h) — Exact-artifact durable controller** — Hold guard across all four package scenarios and lane activation evidence
  - Surfaced by: eng-review T3 — A3/D38/D39,G20-G22,P2
  - Files: Durable/verify-packed-consumers.sh, Durable/consumers, Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlMixedVersionCompatibilityTests.cs
  - Possible overlap with CEO/DX rows; Eng supplies the final mechanism and regression matrix.
- [ ] **A7 (P1, human: 6h / agent+gstack: 90min) — Fenced CLI resource lifecycle** — Prove fence-before-snapshot and bounded checked cleanup
  - Surfaced by: eng-review T2 — A2/D37,G08-G10/G19,P1/P2
  - Files: Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs, Cli/ForgeTrust.AppSurface.Cli/DurableSchemaPreflightVerifier.cs, Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaPreflightIntegrationTests.cs, Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs
  - Possible overlap with CEO/DX rows; Eng supplies the final mechanism and regression matrix.
- [ ] **A8 (P1, human: 3h / agent+gstack: 30min) — cli-operator-dx** — Complete required-flag help and safe category recovery
  - Surfaced by: devex-review DX-T1 — DX passes2/3 native D3/D4/D5
  - Files: Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs, Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaCommandTests.cs, Cli/ForgeTrust.AppSurface.Cli/README.md, troubleshooting/durable-diagnostics.md
  - Possible overlap with CEO docs/Eng T5; preserve DX help/recovery/manual acceptance.
- [ ] **A9 (P1, human: 3h / agent+gstack: 30min) — cli-operator-dx** — Complete canonical gate map and runnable one/two-pair transcript
  - Surfaced by: devex-review DX-T2 — DX passes1/4/5/6 native D1/D2/D6
  - Files: Durable/heartbeat-retention-operations.md, Durable/operational-assessments.md, examples/durable-postgresql/README.md
  - Possible overlap with CEO docs/Eng T5; preserve DX help/recovery/manual acceptance.
- [ ] **A10 (P2, human: ~2h / agent+gstack: ~20min) — Docs/examples** — Document staged proof checklist and complete one/two-pair examples
  - Surfaced by: ceo-review CEO-T4 — P1/P2/N1/N6; adoption and proof ownership
  - Files: Cli/ForgeTrust.AppSurface.Cli/README.md, Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md, Durable/heartbeat-retention-operations.md, examples/durable-postgresql/README.md, troubleshooting/durable-diagnostics.md
  - Possible overlap with Eng T1–T4 or docs T5; preserve CEO invariant.
- [ ] **A11 (P2, human: 3h30m / agent+gstack: 45min) — Canonical guide and DX** — Deliver help and complete examples with prepared-clock acceptance
  - Surfaced by: eng-review T5 — G01/G07/G24 and DX required docs decisions
  - Files: Cli/ForgeTrust.AppSurface.Cli/README.md, Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md, Durable/heartbeat-retention-operations.md, Durable/operational-assessments.md, Durable/README.md, examples/durable-postgresql, troubleshooting/durable-diagnostics.md, README.md
  - Possible overlap with CEO/DX rows; Eng supplies the final mechanism and regression matrix.
- [ ] **A12 (P2, human: 30min / agent+gstack: 10min) — cli-operator-dx** — Observe one manual complete-clock DX acceptance
  - Surfaced by: devex-review DX-T3 — DX pass8 D34
  - Files: Durable/heartbeat-retention-operations.md, examples/durable-postgresql/README.md
  - Possible overlap with CEO docs/Eng T5; preserve DX help/recovery/manual acceptance.

### Final gate state

The user selected A: APPROVED AS-IS. All provisional taste choices are accepted without overrides, with the existing actual-input prerequisites retained. No affected-phase rerun is required. Approval applies to the reviewed plan; implementation/tests/publication/production activation remain their separate work and gates. Review logs are now authorized under Autoplan completion.

### Autoplan completion record

Status: **DONE_WITH_CONCERNS**. Human A approval is saved as APPROVED AS-IS; the reviewed Implementation plan and accepted requirements are unchanged. Seven required records were successfully written and read back for run `20261001T015850Z-issue845`: CEO, DX and Eng plus CEO/Design/DX/Eng voice coverage. Eng retains issues_open for32mapped implementation/test findings, not because a plan choice remains unanswered. DX scores and timing remain inferred/unmeasured. Outside Claude Code review and the independent CEO spec recheck remain unavailable. Actual reviewed release/deployment inputs remain prerequisites before implementation; later candidate/public proof and production activation gates remain closed until satisfied.

Operational learning review completed:2nonduplicate project pitfalls saved (`durable-status-reader-owns-transaction`, `durable-epoch-bootstrap-before-shared-guard`); previously saved schema10-baseline and caller-credential insights reused rather than duplicated. Brain calibration write-back is disabled. Default-mode completion stays in this chat. Next implementation uses the approved dependency order after actual planning inputs are supplied, then /ship when ready to create the PR. No new code workflow or deployment was started by the A approval.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|---|---|---|---|---|---|
| CEO Review | /autoplan → /plan-ceo-review | Scope and strategy |105logged total;1current |ISSUES OPEN; plan approved as-is |5proposals,2accepted,1deferred,2skipped; native6; independent spec recheck unavailable |
| Outside Review | Claude Code availability probes, CEO/DX/Eng | Independent second opinion |0completed current /3phase probes |UNAVAILABLE; enabled, CLI not installed |No external findings/consensus; historical external record is different/stale scope |
| Eng Review | /autoplan → /plan-eng-review | Required final architecture/tests |181logged total;1current |ISSUES OPEN; plan approved, required work mapped |3architecture+3quality+24test groups+2performance=32;0planned silent critical gaps; native2 |
| Design Review | /autoplan scope gate | UI/UX |0current;52historical retained |SKIPPED, no UI |Skip recorded; no completion credit or borrowed historical clearance |
| DX Review | /autoplan → /plan-devex-review | CLI/package adoption |107logged total;1current |ISSUES OPEN; plan approved as-is |Inferred7.125→8.0/10; TTHW unknown→target2–5min; native6 |

| Phase | Host | Outside provider/status | Native completion | Native findings | Coverage |
|---|---|---|---|---|---|
| CEO |Codex |Claude Code unavailable |Completed, matched input |6 |Partial: primary/native, no outside |
| Design |Codex |Skipped |Skipped |0 |No UI scope |
| DX |Codex |Claude Code unavailable |Completed, matched input |6 |Partial: primary/native, no outside |
| Eng |Codex |Claude Code unavailable |Completed, matched input |2 |Partial: primary/native, no outside |

- **OUTSIDE COVERAGE:** requested provider Claude Code unavailable in CEO/DX/Eng; Design skipped. All3native reviews completed exact matched inputs. Unknown actual model identity remains unknown; requested native model was combo/sub. All7review/voice records persisted and verified for this approval run.
- **CROSS-MODEL:** N/A.0confirmed UserChallenges/overlaps/disagreements; no outside completion or distinct-model-family claim.
- **VERDICT:** APPROVED AS-IS, DONE_WITH_CONCERNS. Applicable plan reviews are complete and logged. Eng review required: the required final review ran and mapped32findings; dashboard remains ISSUES OPEN until implementation/validation supplies later evidence. Actual planning inputs are still required before implementation; tests/artifact proof/publication/activation have not been performed by this review.
NO UNRESOLVED DECISIONS
