# Implementation plan: shared-store Durable PostgreSQL role pairs (#823)

Status: APPROVED — /autoplan review complete; user approved as-is on 2026-09-24.

The [approved design](../designs/issue-823-shared-store-role-pairs.md) is the normative security and behavior contract. This plan turns it into ordered changes and release proof for [#823](https://github.com/forge-trust/AppSurface/issues/823). It preserves the selected complete, versioned role manifest, the existing forwarding pair, one StoreId and epoch, and the source lane's function-only Work dispatcher. The certificate describes a deployed-lane credential boundary, not PostgreSQL row isolation.

## Implementation plan

### 1. Establish baseline and deployment inputs

- Work from the latest reviewed `main` baseline, without carrying unrelated changes from this checkout. Record schema version, packaged recipe location, existing role/policy catalog, PostgreSQL test harness, and current release gates. The local branch is behind `origin/main`; inspect the upstream commit before implementation.
- Collect a reviewed, non-secret manifest proposal with the actual forwarding and source role names and each lane's hosted surfaces. Confirm both lanes use the same StoreId and active epoch. This configuration review creates no roles or connections.
- Keep Source processing closed until the package, role proof, certificate, and operator sequence all pass. A missed prerequisite is a fail-closed rollout stop.

### 2. Define and validate the canonical role-manifest contract

- Change [`configure-postgresql-roles.sql`](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) to accept `:'role_pairs_json'` with exactly `version: 1` and `pairs` (1–32 entries); each entry has exactly `dispatcher`, `runtime`, and explicit `dispatcher_profile` (`full` or `work_only`). Keep migration-owner and retention-operator parameters explicit. Reject unknown fields, missing/aliased/duplicate roles, controls/truncated names, missing role OIDs, and unknown profiles before any grant or policy mutation. Treat manifest values only as data and quote generated identifiers through `%I`.
- Enter a transaction and take the existing migration advisory lock before catalog reads used for validation or mutation. Move the recipe's current pre-transaction checks inside that boundary. Validate restricted service login leaves, distinct owner/retention identities, membership-free posture, ownership, and effective package privileges.
- Read current managed policy targets and package ACL grantees while locked. Reject omission of a previously authorized pair, unexpected extra principals, broader existing privileges, or a profile change that would remove authority. Do not treat omission as retirement. A narrower missing grant for a listed role may be restored. Failed validation must leave the catalog unchanged.

### 3. Reconcile all pairs atomically

- Compute exact policy targets from the full manifest. Put all runtime roles on the four runtime policies; put only `full` dispatchers and the migration owner on Flow/Schedule global-discovery and lease policies; preserve owner-only Work-contract discovery, retention targeting, scope expressions, commands, permissiveness, and forced RLS. Recreate any dropped runtime policy for the complete runtime set inside the transaction.
- Revoke `PUBLIC` on the package schema and its known relations, sequences, and functions. Grant every dispatcher schema `USAGE` and Work-discovery function `EXECUTE`; grant a `full` dispatcher the existing direct `flow_dispatch` `SELECT` and Schedule-claim function `EXECUTE`; grant a `work_only` dispatcher no direct Durable relation/sequence or other function access. Apply the current runtime allowlist to every runtime, and retain the exact owner/retention allowlists.
- Verify policy OIDs/expressions, direct ACLs and grant options, and effective schema/table/column/sequence/function privileges for every pair against an object-specific allowlist. Check inherited authority, memberships, and ownership. Commit only after all assertions pass. Identical reruns must preserve both pairs.

### 4. Prove source Work-only behavior and forwarding continuity

- Use the existing PostgreSQL 16 integration harness. Start with one forwarding `full` pair, then enroll the `work_only` source pair and rerun the full manifest. Run synthetic Flow, Schedule, Work, heartbeat, scoped mutation, health, and epoch-fence checks for the forwarding lane before and after enrollment.
- Configure the source pump and direct pump pass for `DurableRuntimeSurface.Work`, since the provider defaults to `All`. Prove ordinary Work discovery and recovery through `discover_work_dispatch` with its registered contract snapshot, returning routing values without payload. Verify disallowed direct reads, sequences, and Flow/Schedule calls fail under the source dispatcher. A Flow/Schedule or accidental `All` pass must fail closed.
- Exercise duplicate/aliased names, unknown profile, omission of either pair, hostile extra grant/policy target, `PUBLIC` privilege drift, membership drift, and a rerun after forward migration. Snapshot catalog state before and after each rejected recipe run to prove transaction atomicity. Explicitly show runtime credentials alone permit no claim of per-lane row isolation.
- Include the canonical SQL file in package output byte-identically; run the existing integration and packed-consumer gates against the shipped artifact. Keep verification scripts and CI evidence aligned with the manifest form.

### 5. Coordinate schema 11 and document adoption

- Ship #823 against schema 10 first. Before #795's schema 11 is published, replace its single-runtime heartbeat preflight with an exact non-empty set comparison against policy and due-health/retention function allowlists, and prove each runtime's restricted leaf posture. The schema 11 maintenance function grants every authorized runtime and no dispatcher, retention operator, or `PUBLIC` principal. Run a real schema-10-to-11 upgrade and both-pair reproof with the schema-11-capable recipe. If #795 lands first, hold second-pair activation until that combined proof passes.
- Update the [provider guide](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md), [Durable root guide](../../Durable/README.md), [adoption guidance](../../Durable/operational-assessments.md), [local example](../../examples/durable-postgresql/README.md), [CLI schema guide](../../Cli/ForgeTrust.AppSurface.Cli/README.md), SQL comments, and related diagnostics. Document the exact manifest and grant matrix, no profile default, conversion/rerun invocation, omit/refusal behavior, upgrade ordering, Work-selector limits, scope and registration semantics, and retirement as a separate procedure. Explain when a separate store or PostgreSQL-enforced partition would be required.
- Give Skoolit's #659 operator/certificate owner the reviewed package version, manifest template, Work-only host setting, privilege proof, and exact claim: distinct deployed-lane credentials on one store with no per-lane row guarantee. Skoolit-owned `public` schema grants and source activation remain its reviewed deployment work.

### Acceptance and rollback

Release only when the forwarding pair still passes all pre/post enrollment proofs, source Work-only pump succeeds with denied direct access, two-pair identical rerun is catalog-stable, all malformed/drift cases fail atomically, and packaged-consumer/CI gates pass. Apply migrations before role reconciliation; drain as required; run the canonical recipe with the full reviewed manifest and the exact preflight before Source activation. On failure, keep Source closed and repair or roll forward with the complete manifest. Never use a one-pair rerun, broad grant, or silent policy replacement as rollback.

### Not in scope

- PostgreSQL-enforced lane or per-contract row isolation, role-bound scope IDs, a second store, or a new runtime epoch.
- Retiring a role pair or narrowing an existing profile through an omitted manifest entry.
- Migrating the forwarding dispatcher off its current direct payload-free Flow read; a Flow discovery function can be separately reviewed.
- New package channel, application-startup DDL, production data, or source activation as part of this AppSurface change.


<!-- autoplan-accepted:ceo -->
- Inventory and update every canonical role-recipe invocation, test helper, verification script, local example, packaged recipe copy, and package-consumer gate to supply the complete version-1 manifest; verify no old single-pair invocation can silently remove a deployed pair.
- Before Source activation, bind the deployment certificate to exact package and schema versions, reviewed manifest hash and role/profile names, both lanes' hosted surfaces, shared StoreId/epoch confirmation, and pre/post forwarding plus source privilege/pump evidence. Name the AppSurface release owner for recipe/package evidence and the Skoolit deployment/certificate owner for configuration and activation signoff; redact secrets and payloads.
- Treat schema-11 compatibility as the #795 release gate if #823 ships on schema 10 first. The #795 owner must prove a real 10-to-11 upgrade and two-pair exact runtime set before schema-11-capable publication; if #795 ships first, hold second-pair activation until a combined recipe/preflight proof passes.
- Document the chosen boundary through a three-option comparison (shared pair, two pairs on one store, separate stores), including operational cost and the trigger for requiring PostgreSQL row partition or a separate store. State plainly that Work function selectors and runtime scope GUCs are not lane authorization.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:dx -->
- Publish and run a clearly fictional, copyable two-pair PostgreSQL 16 local walkthrough with prerequisites, complete manifest JSON, safe psql invocation, Source `DurableRuntimeSurface.Work` hosted/direct configuration, privilege proof command, expected allowed/denied outcomes, identical rerun, and omitted-pair refusal. Measure primed local time from dependencies-ready to first successful Work-only routing proof; report cold setup separately and do not claim an unmeasured under-five-minute result.
- Make the adoption guide the start-here operator sequence and the provider guide the canonical manifest, profile, and object-specific grant reference. Link the local transcript, upgrade/rollback guidance, and diagnostics from those pages; remove stale one-pair wording in every direct recipe adopter.
- For missing/invalid manifest values, missing/duplicate/aliased roles, omitted installed pair, broader grant/policy drift, and final assertion failures, emit bounded diagnostics with affected manifest entry or package object, problem, likely cause, safe corrective action, and nearby guide link. Assert representative problem/cause/fix messages in PostgreSQL integration tests while keeping credentials, payloads, and connection strings out of output.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Keep the complete reviewed manifest as the deployment authority. Under the advisory lock, reject every omitted role visible in the union of managed policy and package-ACL targets, and fail closed on contradictory or ambiguous catalog state before mutation. Compare the invocation with the prior reviewed release manifest; document that total erasure of a role's catalog traces cannot be detected by catalog inference alone and would need a separately designed registry for database-only proof.
- Hash the exact UTF-8 bytes of the reviewed manifest file used by the recipe for certificate evidence; record its path, digest, and role/profile names. Require re-review when file bytes change, including whitespace or order changes. Reject duplicate JSON properties on the original `json` representation before any `jsonb` conversion.
- Make healthy identical reruns preserve policy OIDs, targets, expressions, ACLs, owners, and effective privileges. Recreate missing runtime policies only with sufficient remaining catalog evidence; reject ambiguous state. Add a forced post-mutation failure test proving complete rollback.
- Use the schema manager's 30-second default as the role-recipe advisory-lock acquisition bound, with a clear nonzero timeout and safe retry guidance; test contention and unchanged catalog. Keep policy-DDL maintenance impact measured and bounded separately.
- Define schema-10 normal migration privilege baseline and object-specific ACL/refusal matrix, including `PUBLIC`, ownership, grant options, and inherited rights. Test missing narrow grant restoration, hostile drift, and both profile-transition directions; require new certificate proof for an explicit reviewed `work_only` to `full` expansion and refuse narrowing in #823.
<!-- /autoplan-accepted:eng -->

## Review record

### Review setup

Autoplan intake on 2026-09-24: GitHub base `main`; local `main` is one unrelated config commit behind `origin/main`. The working-tree design and this plan are the only local changes. UI scope is absent (one incidental “form” match); DX scope is required for the developer-facing PostgreSQL package, recipe, and operator guide. Review mode: **SELECTIVE EXPANSION**. External Codex CLI review is unavailable under this Codex host; independent native `combo/sub` findings are attributed as subagent-only, with no claimed two-provider consensus.

Final gate: the user approved the reviewed plan as-is on 2026-09-24. This approves the implementation plan, not deployment activation or a claim of PostgreSQL-enforced row isolation.

### Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
| --- | --- | --- | --- | --- | --- | --- |
| A01 | Intake | Keep the approved design normative and review this separate implementation plan | Mechanical | P4 | Avoid a second, divergent security contract | Rewriting the approved design |
| A02 | Intake | Skip visual design; run developer-experience review | Mechanical | P3 | Backend recipe and package usage have no screen flow | Scoring nonexistent UI |
| A03 | CEO | Preserve complete manifest and two-pair shared-store scope | Mechanical | P1, prior approval | This directly satisfies the stated deployed-lane boundary | Shared login or separate-store redesign |
| A04 | CEO | Require certificate evidence bound to exact package and manifest, with named owners | Mechanical | P1, P5 | A passing disposable test alone does not prove the deployment uses it | Untied certificate prose |
| A05 | CEO | Audit every recipe caller and packed artifact in the same release | Mechanical | P2, P4 | The recipe input changes from one pair to a manifest | Updating SQL but leaving old invocations |
| A06 | CEO | Make #795 schema-11 compatibility a gate owned by #795 if it ships later | Mechanical | P5 | #823 can ship on schema 10 without claiming future schema 11 proof already exists | Blocking schema-10 release on unpublished migration |
| A07 | CEO | Defer pair retirement and Flow function migration to separately reviewed cases | Taste T1 | P3, P5 | Both change existing authority, while #823 must refuse silent removal | Hiding authority removal inside enrollment |
| A08 | CEO | Include a written comparison of shared pair, two pairs, and separate stores | Mechanical | P5 | Makes the boundary and cost intelligible to certificate owners | Claiming row isolation implicitly |
| A09 | DX | Make the provider guide the canonical manifest/grant reference and adoption guide the operator start page | Mechanical | P5 | Operators need one path and one source of truth for syntax | Repeated divergent examples |
| A10 | DX | Require an executable fictional two-pair local walkthrough with expected proof output | Mechanical | P1, P5 | Existing one-pair tutorial is the closest real entry point | A JSON fragment without invocation or verification |
| A11 | DX | Require problem, cause, safe fix, and guide link for recipe rejection categories | Mechanical | P1 | A nonzero SQL exit alone leaves operators guessing | Raw catalog exceptions as the main guidance |
| A12 | DX | Keep profile and retirement limits explicit without a new CLI | Mechanical | P3, P5 | The manifest is an operator input and omission is intentionally refused | Adding a role-management product to #823 |
| A13 | Eng | Keep the approved catalog-inference design but qualify its evidence limit | Mechanical | P1, P5 | A role whose every policy/ACL trace is deleted cannot be reconstructed from PostgreSQL catalogs; the prior reviewed manifest is the release record | An unplanned registry migration or an impossible unconditional promise |
| A14 | Eng | Hash the exact reviewed UTF-8 manifest file bytes | Mechanical | P5 | Operators can verify the same file was reviewed and invoked; semantic canonicalization adds a second contract without benefit | A new semantic-hash implementation |
| A15 | Eng | Bound the role-recipe advisory-lock wait at 30 seconds | Mechanical | P1, P5 | The existing schema manager uses a 30-second default; bounded failure avoids an indefinite operator run | An unbounded lock wait |
| A16 | Eng | Preserve policy OIDs on healthy reruns and test late rollback | Mechanical | P1, P5 | The current recipe drops four policies every run, so catalog-stable reruns need a defined identity contract | Treating equal final expressions as an unchanged catalog |
| A17 | Eng | Validate original JSON keys before jsonb normalization | Mechanical | P1 | PostgreSQL jsonb collapses duplicate keys; exact-field validation otherwise misses ambiguous input | Accepting last-key-wins input |
| A18 | Eng | Make ACL baseline and profile transitions explicit | Mechanical | P1, P5 | Migration-produced PUBLIC grants and reviewed expansion must be distinguished from hostile drift and authority removal | Blanket revocation or silent profile narrowing |
| A19 | Final gate | Approve reviewed plan as-is | User approval | Explicit final-gate response | The user replied “approved” after the complete review and task summary | Further revision before approval |

### CEO review — strategy and scope

**Premise challenge (0A).** The actual outcome is an independently deployable Source Lifecycle lane with its own secrets and audit identity, while keeping one Durable store and current registration rules. A shared credential would not give the requested deployment identity; a separate store would change StoreId/epoch and coordination semantics. The independent reviewer questioned whether row isolation was the hidden need, but the user and approved design explicitly exclude it. The unresolved real-world fact is whether the deployment owner will attest that the certificate says *deployed-lane boundary*; this is an activation gate, not a reason to rewrite the contract.

**What already exists (0B).** The canonical [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql) already owns object ownership, grants, policy assertions, and the advisory lock, but only for one pair and with pre-lock checks. Migration 0009 already provides `discover_work_dispatch`; the [Work store](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableWorkStore.cs) already calls it. The [Flow processor](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/PostgreSqlDurableFlowProcessor.cs) still selects `flow_dispatch` directly. `HostedSurfaces` defaults to `All`, and the [schema integration tests](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs) already exercise hostile roles, RLS, grants, and lock contention. The [verification script](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-postgresql.sh), mixed-version test, and local example invoke the old variables; they are direct adopters of the recipe input contract.

**Dream state (0C).**

```text
CURRENT: one role pair, one shared store
  -> #823: reviewed full manifest, two restricted pairs, one StoreId/epoch,
           Work-only dispatcher proof and bounded deployment certificate
  -> 12 MONTHS: repeatable pair enrollment/rotation with explicit retirement,
                checked schema upgrades, no accidental policy replacement
```

The current case achieves safe enrollment and repeatable reconciliation. Explicit retirement remains a future lifecycle operation; turning omission into deletion would violate the first boundary. The only competitive risk supported by this evidence is a misleading security claim, not a race with another product.

**Alternatives (0C-bis).** A one-off second-role grant script is small (S, high drift risk) but cannot guarantee reruns preserve both pairs. The selected complete versioned manifest extends the one canonical recipe (M/L, medium SQL complexity) and makes the authorized set reviewable. A database-owned registry could make enrollment persistent (L, more migrations and lifecycle coupling), but conflicts with the schema-11 coordination window and adds no benefit needed for #659. The [approved design](../designs/issue-823-shared-store-role-pairs.md) already chose the complete manifest; this review retains that prior decision.

**Mode and expansion scan (0D).** The plan touches SQL, provider tests, scripts, docs, package, and CI, exceeding eight files because all are direct recipe adopters. The minimum complete change still needs manifest parsing, all-pair policy/grant reconciliation, hostile database proof, invocation updates, and operator docs. Adjacent ideas considered were a catalog diff report, a role enrollment CLI, a database role registry, a Flow discovery function, a role retirement command, and a lane-specific scope claim. Only concise certificate evidence binding and a shared-pair/separate-store decision table belong in this blast radius. The other ideas require new authority or infrastructure and are deferred; none is silently added.

**Temporal interrogation (0E).** At foundation time, the implementer must pin the current schema and every call site of `-v dispatcher_role`/`-v runtime_role`. In core SQL work, they must decide how to distinguish an omitted authorized principal from an unexplained grantee under the advisory lock and how to preserve the full dispatcher’s exact legacy grants. At integration time, the surprise is the default `All` hosted surface and schema-11 preflight’s single-runtime assumption. At verification time, an identical manifest must be idempotent and rejected manifests must leave a byte-equivalent privilege/policy catalog.

**Section 1 — Architecture.** The dependency graph is `reviewed manifest -> psql recipe -> locked catalog reconciliation -> installed policies/ACLs -> provider lanes -> certificate`; migrations precede the recipe, and the provider never runs DDL. Missing/null manifest fails at psql/validation, an empty pair set fails the 1–32 bound, malformed JSON or missing roles aborts before DDL, and SQL/catalog errors abort the transaction. The only new state transition is catalog `one pair -> two pairs`; omission, unauthorized drift, and partial DDL cannot become committed transitions. The one store remains a shared failure domain, so the certificate needs the shared StoreId/epoch and non-isolation statement.

**Section 2 — Error and rescue registry.**

| Codepath | Trigger / exception class | Rescue and visibility | Proof |
| --- | --- | --- | --- |
| psql manifest entry | Missing variable, invalid JSON, wrong fields or bounds; psql/SQL validation error | Exit nonzero before mutation; operator sees bounded field/path reason, no secret value | Invalid-input matrix and catalog equality |
| Role/capability preflight | Missing role, membership, ownership, extra grant/policy; SQL check error | Transaction rollback; name offending role/object/privilege without connection data | Hostile role and drift fixtures |
| Reconciliation DDL | Lock wait, permission denial `42501`, statement failure | Abort transaction; retain prior pair and Source-closed gate | Lock contention and injected failure |
| Work discovery | Dispatcher denied `42501`, invalid contract selector | Provider admission/pass reports existing bounded diagnostic; no broadened SQL grant | Work-only success and Flow/Schedule denial |
| Schema upgrade | Unsupported version or #795 set mismatch | Stop activation; rerun matching packaged recipe after forward migration | 10-to-11 upgrade fixture in #795 |

No catch-all or silent retry is proposed. A lock can wait; use the established drain/maintenance procedure, and report an operator-visible failure rather than claiming zero downtime.

**Section 3 — Security.** Threats are manifest injection (low likelihood, high impact; JSON data and `%I`), stale/broad role authority (medium/high; exact ACL and policy allowlist), `PUBLIC` function access (medium/high; revoke and verify), and certificate overclaim (high operational likelihood/impact; explicit deployed-lane text). Role membership and owner rights are checked because direct grant enumeration alone is insufficient. `work_only` function execution is a selector over caller-provided Work contracts, not per-contract authentication; runtime scope GUC remains caller-supplied. No production secrets or payloads enter the manifest.

**Section 4 — Data and interaction edges.**

```text
manifest -> parse -> role OIDs -> compare locked catalog -> DDL -> assert -> commit
   |         |          |              |              |         |
 missing   malformed   missing      omission/drift   SQL error  mismatch
   +-------------------------- nonzero + rollback ----------------+
```

Two concurrent recipe runs serialize on the existing advisory lock; whichever obtains it second must reread policy/ACL state before deciding. A duplicate invocation with the same manifest is safe; a stale one-pair invocation fails. The operator interaction is review -> migrate -> reconcile -> preflight -> certify -> activate; an interruption before activation leaves Source closed. There are no browser interactions.

**Section 5 — Code quality.** Keep the manifest validator and role-set computation near the canonical recipe instead of duplicating policy ownership in a second script. The existing SQL is long and repetitive; a set-based role relation can reduce single-role loops, but the object-specific allowlist must remain readable and reviewable. Avoid a generic privilege engine or role registry for this case. Update old invocation helpers centrally in tests.

**Section 6 — Test diagram.**

```text
manifest parse/bounds   -> SQL integration: valid 1/2/32, null/empty/33, malformed/unknown
catalog role posture   -> SQL integration: missing, duplicate, membership, ownership, grant options
policy/ACL reconcile   -> SQL integration: full + work_only, PUBLIC, drift, exact rerun
parallel invocation    -> controlled advisory-lock test: both completion orders
provider Work-only     -> pump/recovery integration: routing success, denied Flow/Schedule
forwarding continuity -> synthetic Work/Flow/Schedule/health before and after
distribution          -> packaged recipe byte check + updated script/example/consumer
upgrade               -> forward fixture; #795 owns real schema-10-to-11 proof
```

Shipping confidence is a two-pair pass under the packaged recipe. Hostile QA attempts one-pair omission and inherited access; chaos proof holds the advisory lock while a competing recipe waits, then verifies final exact state. Assertions inspect effective access and catalog targets, not just exit codes. A new UI E2E or wall-clock performance test would not cover this risk.

**Section 7 — Performance.** The bounded manifest caps pair count at 32, making catalog scans and policy target construction finite. The slow paths are catalog ACL enumeration, policy DDL lock acquisition, and the integration fixture; no new hot-path query or connection pool is added to Work discovery. Measure recipe runtime and lock wait in the test harness, but do not promise a p99 without deployment evidence.

**Section 8 — Observability.** The recipe must exit nonzero with a precise validation category and role/object context; it must not print passwords or connection strings. Catalog snapshots and certificate evidence supply the audit trail for enrollment. Existing provider diagnostics report permission denial and admission state; the rollout runbook should map those to the complete-manifest rerun and Source-closed response. A new dashboard/metrics backend is unsupported by this one-time operator action.

**Section 9 — Deployment.** The plan correctly orders migration before reconciliation and keeps Source closed. Add a release record tying package version, schema version, manifest hash, configured role names/surfaces, StoreId/epoch confirmation, and pre/post forwarding plus source proof to the certificate owner. #823 ships on schema 10; if #795 publishes schema 11 later, #795 owns the real upgrade gate. If schema 11 wins the race, the combined compatibility proof precedes second-pair activation. Rollback is stop Source and repair forward using the complete manifest; a one-pair recipe is forbidden.

**Section 10 — Long-term trajectory.** Enrollment is reversible only with a separately reviewed retirement action (2/5 reversibility). That future procedure must prove no deployed lane uses the pair and then remove exact grants/policy targets under lock with post-removal proof; omission remains an error until then. The manifest is simple enough for a new operator if the example and invocation are packaged. The main debt is manual full-set ownership and the pending #795 coordination, not new runtime APIs. Section 11 visual design is skipped: no screens or UI state are changed.

**Architecture, state and operational diagrams.**

```text
package migration SQL -> schema 10 -> canonical role recipe <- reviewed JSON manifest
                                            |                    |
                                       advisory lock       owner/retention names
                                            v
                                     policy + ACL catalog
                                      /           \
                          forwarding full     Source work_only
                          Work/Flow/Sched     discover_work_dispatch only
                                      \           /
                                    one StoreId + epoch

catalog: one-pair --valid complete manifest--> two-pair
         one-pair --bad manifest/DDL failure--> one-pair
         two-pair --same manifest--> two-pair
         two-pair --omitted pair--> REJECT (two-pair retained)
         two-pair --retirement request--> separate reviewed procedure

error flow: validate -> lock -> compare -> reconcile -> assert -> commit
               |        |       |          |         |
               +--------+-------+----------+---------+-> rollback/nonzero
                                                        -> Source stays closed

deploy: review config -> drain -> migrate -> full-manifest recipe
        -> exact preflight -> before/after forwarding proof
        -> Source Work-only pump/recovery + denied-access proof
        -> certificate signoff -> activate Source

rollback: failure? -> keep/return Source closed
                   -> catalog unchanged if recipe failed
                   -> repair forward with same complete manifest
                   -> rerun exact proof -> reconsider activation
```

**Stale diagram audit.** The touched role recipe has no ASCII diagram to revise. The [operational adoption guide](../../Durable/operational-assessments.md) has a schema-9-to-10 rollout table and role command that will become stale when the manifest invocation changes; replace its one-pair examples and update the sequence text. The root guide and local example similarly refer to one pair, so they need the same consistency check.

**CEO completion summary.**

| Area | Result |
| --- | --- |
| Mode/system audit | SELECTIVE EXPANSION; one unrelated upstream commit missing locally; role script and direct callers identified |
| 0A–0F | Deployment identity need confirmed; complete manifest retained; two same-scope improvements accepted |
| 1 Architecture | Shared failure domain and lock ordering made explicit; 1 rollout evidence gap accepted |
| 2 Errors | 5 paths mapped; 0 silent critical gaps after fail-closed obligations |
| 3 Security | 4 threats mapped; no false row-isolation claim |
| 4 Data/UX | Missing, empty, malformed, concurrent and interrupted runs mapped; no UI interactions |
| 5 Quality | Canonical script and set-based validation preferred; no generic registry |
| 6 Tests | 7 paths mapped to integration/package proof, with failure assertions |
| 7 Performance | 32-pair cap; lock wait remains an operational risk |
| 8 Observability | Nonzero recipe diagnostics and catalog/certificate evidence required |
| 9 Deployment | Schema-10 first; schema-11 gate explicitly owned by #795 |
| 10 Future | Reversibility 2/5; retirement tracked in TODOS.md |
| 11 Design | Skipped: no UI scope |
| Scope/tasks | 2 accepted same-scope additions; 1 retirement TODO; 4 CEO tasks |
| Outside voice | Native `combo/sub` completed; Codex CLI unavailable under host; consensus N/A |

### CEO failure modes registry

| Codepath | Failure mode | Rescued? | Test? | Operator sees? | Logged/evidence? |
| --- | --- | --- | --- | --- | --- |
| Manifest input | Omitted existing pair | Yes, reject | Required | Nonzero and omission reason | Catalog before/after |
| SQL transaction | Partial policy/grant change | Yes, rollback | Required | Nonzero | Catalog equality |
| Dispatcher | Unexpected direct table access | Yes, reject/fail certificate | Required | Failed capability proof | Privilege matrix |
| Runtime | Cross-lane row access assumption | Documented limitation | Required explicit negative claim | Certificate wording | Signed deployment review |
| Upgrade | Schema 11 single-role preflight | Gate under #795 | Required in #795 | Activation stop | Upgrade proof |

### CEO completion

Selective expansion retained the approved design. The native CEO reviewer found four concerns; the row-isolation concern is resolved by the user’s explicit requirement, the cost comparison and rollout evidence become same-scope obligations, and retirement remains a separately reviewed follow-up. All ten applicable review sections were examined; one architectural/rollout gap was added to the plan, no unresolved critical gaps remain, and no outside-model consensus is claimed. The six dimensions of CEO dual-voice consensus are N/A because the outside Codex process did not run.

<!-- autoplan-accepted:ceo -->
- Inventory and update every canonical role-recipe invocation, test helper, verification script, local example, packaged recipe copy, and package-consumer gate to supply the complete version-1 manifest; verify no old single-pair invocation can silently remove a deployed pair.
- Before Source activation, bind the deployment certificate to exact package and schema versions, reviewed manifest hash and role/profile names, both lanes' hosted surfaces, shared StoreId/epoch confirmation, and pre/post forwarding plus source privilege/pump evidence. Name the AppSurface release owner for recipe/package evidence and the Skoolit deployment/certificate owner for configuration and activation signoff; redact secrets and payloads.
- Treat schema-11 compatibility as the #795 release gate if #823 ships on schema 10 first. The #795 owner must prove a real 10-to-11 upgrade and two-pair exact runtime set before schema-11-capable publication; if #795 ships first, hold second-pair activation until a combined recipe/preflight proof passes.
- Document the chosen boundary through a three-option comparison (shared pair, two pairs on one store, separate stores), including operational cost and the trigger for requiring PostgreSQL row partition or a separate store. State plainly that Work function selectors and runtime scope GUCs are not lane authorization.
<!-- /autoplan-accepted:ceo -->

### CEO implementation tasks

- [ ] **C1 (P1; human ~2h / agent ~20m):** Update all recipe invocations and package checks for the manifest. Verify with `rg` and packaged-consumer gates.
- [ ] **C2 (P1; human ~2h / agent ~20m):** Produce release/certificate evidence mapping and named owner handoff. Verify exact package, manifest, role, surface, and shared-store fields before activation.
- [ ] **C3 (P1; owned by #795):** Gate schema-11 publication on real upgrade and multi-role preflight proof. Keep Source closed if sequence reverses.
- [ ] **C4 (P2; human ~1h / agent ~10m):** Publish three-option security/operations comparison in provider guidance.

### DX review — developer experience

**Product and persona.** This is a .NET library plus an operator-facing PostgreSQL role recipe and documentation. The primary developer is a platform engineer responsible for a shared Durable store and a source worker deployment. They tolerate deliberate migration and privilege steps, but expect a non-interactive command, exact failure category, a safe rerun, and proof that the existing lane still runs. The repo [provider guide](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md) already has a one-pair command and grant table; the [local example](../../examples/durable-postgresql/README.md) creates four roles and runs a bounded pass. Both will be confusing after the manifest change until updated together.

**Empathy narrative.** I open the Durable provider README because I need a second lane, not a new store. Its deployment section tells me to run `configure-postgresql-roles.sql` with one dispatcher and runtime, and later says to compose exactly one of each. I can see the current grant table, but it does not tell me how to keep the first pair when adding the second. If I copy the old command, I might accidentally target the wrong roles; the new recipe must reject that, and the documentation should show the full JSON and `psql` invocation together. I then find the local example, but it proves only four roles. I need to see both lanes, the source `HostedSurfaces = Work` setting, and the expected allowed/denied results. If the recipe rejects a policy target or a missing role, I need an object and a corrective step, not an unstructured SQL failure. Finally, I must show the certificate owner that the package version, manifest, StoreId/epoch and tested role names match the deployment. This is an operator workflow, so an honest multi-step proof is more useful than a fictional one-command onboarding claim.

**Developer journey map.**

| Stage | Actual current path | #823 resolution |
| --- | --- | --- |
| Discover | Durable README links provider and role recipe | Adoption guide becomes start-here, links canonical provider reference |
| Install | Existing NuGet/package and PostgreSQL 16 setup | No new package; list prerequisites and schema floor |
| Hello world | Local example creates one pair, invokes old variables | Fictional two-pair manifest plus complete local command and expected result |
| Real usage | Provider `HostedSurfaces` defaults to `All` | Show Source `Work` setting in hosted and direct pass examples |
| Debug | Existing `ASDUR103` and SQL errors | Recipe rejection categories explain problem, cause, fix, guide |
| Upgrade | Schema-9-to-10 guidance and role rerun | Complete-manifest rerun and #795 sequence table |
| Scale | Same shared store and bounded manifest | 1–32 pair limit, shared failure domain, no per-lane row claim |
| Migrate | Existing one-pair deployment | Preserve forwarding pair, add source pair, prove before/after |
| Retire | No role-pair retirement flow | Explicit refusal and link to separately reviewed follow-up |

**First-time operator roleplay and TTHW.** At T+0, I find the one-pair recipe in the provider guide. At T+2, I find the local example, but it still has one dispatcher/runtime login. At T+5, I can copy neither a complete two-pair manifest nor a Work-only proof command, so I stop and ask how to preserve the first lane. This is a source-backed friction trace, not a measured user session. The review targets a **primed disposable local environment** where a developer can copy a complete manifest and reach one Work-only routing proof in under five minutes after dependencies are ready. Cold container and .NET setup, real deployment review, and certificate signoff have no honest under-five-minute target; record their elapsed times separately.

**Competitive calibration.** This case is an operational enrollment procedure, not a greenfield orchestration quickstart. [Azure Durable Functions' SQL-provider quickstart](https://learn.microsoft.com/en-us/azure/azure-functions/durable-functions/quickstart-mssql) explicitly separates prerequisites, database setup, run, and deployment; it does not publish a time claim that can be compared to this role-reconciliation proof. The useful benchmark is a complete local path with expected output and safe failure guidance. No competitor timing is asserted. The magical moment is the first Source Work-only discovery succeeding while direct Durable reads fail and the forwarding lane still passes; deliver it through the existing local example and integration harness.

**Eight DX passes and scorecard.**

| Dimension | Initial → planned /10 | Evidence, 10/10 target, disposition |
| --- | --- | --- |
| Getting started | 5 → 8 | One-pair example exists; add complete two-pair walkthrough, expected output, prerequisites and primed time measurement. A 10 would be a proven one-command disposable setup without weakening the review gates. |
| API/CLI/SDK | 7 → 9 | `role_pairs_json` and explicit profiles are consistent with psql data variables; show exact shape and profile choice. A 10 would remove all operator ambiguity while retaining fail-closed inputs. |
| Errors/debugging | 5 → 9 | Existing recipe uses `\\echo` plus forced SQL error; require bounded category, offending role/object, likely cause, corrective action and doc link. A 10 would cover every rejection with exact fixture assertions. |
| Documentation | 6 → 9 | Six guides currently repeat one-pair assumptions; one canonical provider reference and adoption start page reduce drift. A 10 would verify every copied command on all supported platforms. |
| Upgrade | 6 → 9 | Current docs show a 9-to-10 sequence; add manifest conversion, identical rerun, omission refusal, and #795 branches. A 10 would include a tested schema-11 artifact after #795 exists. |
| Developer environment | 7 → 8 | PostgreSQL 16 harness and local script already exist; extend them to two pairs and package bytes. A 10 would measure cross-platform cold setup, which is outside this case. |
| Community/ecosystem | 8 → 8 | Public repo, example, NuGet path and issue handoff already exist; no new channel or SDK language is needed. A 10 would need measured external adopter support beyond #823. |
| Measurement/feedback | 5 → 8 | Measure primed local proof time and capture exact certificate evidence; do not add telemetry. A 10 would require repeated real-operator onboarding observations. |

Overall planned DX: **8.5/10**. The eight-pass scores are review estimates, not measured outcomes. Native `combo/sub` reviewer completed; outside Codex CLI unavailable in this host, so the six DX consensus dimensions are N/A.

**Error examples to require.** A missing role should report the manifest pair index/role name, cause “role not created,” fix “create a restricted login leaf and rerun the unchanged full manifest,” and link to the canonical role reference. An omitted installed pair should identify the missing role, explain that omission is not retirement, show the full-manifest rerun path, and link to the upgrade guide. A broader grant or policy target should identify the package object and extra authority, direct the operator to a reviewed repair, and leave the catalog unchanged. Test the diagnostic text by category without printing secrets, payloads, or connection strings.

**Documentation structure and implementation checklist.** The [adoption guide](../../Durable/operational-assessments.md) is the operator start page: prerequisites -> migration -> manifest review -> recipe -> preflight -> lane proof -> certificate -> activation. The [provider guide](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md) is the canonical manifest/grant/profile reference. The local example owns a complete disposable two-pair transcript, and CLI/troubleshooting pages link to those sources. The implementation must provide a fictional JSON file, a shell-safe `psql -v role_pairs_json=...` invocation, Source Work-only hosted/direct settings, an exact privilege query or proof command, and expected allowed/denied outcomes. Rerunning identical input and an omitted-pair refusal are part of that transcript. Existing one-pair stores remain valid with a one-pair full manifest; profile has no default.

**Not in DX scope.** No hosted playground, new enrollment CLI, dashboard, automatic retirement, or cross-platform timing promise. These would expand the approved operator boundary without evidence. No TypeScript, credit-card, or codemod checklist item applies to this PostgreSQL/.NET role recipe.

### DX implementation tasks

- [ ] **D1 (P1; human ~2h / agent ~20m):** Publish and execute a complete fictional two-pair local transcript. Verify the exact command and expected Work-only/forwarding outcomes in the local harness.
- [ ] **D2 (P1; human ~2h / agent ~20m):** Add actionable rejection diagnostics by category and representative integration assertions; never expose secrets or payloads.
- [ ] **D3 (P2; human ~1h / agent ~10m):** Make adoption guide start-here and provider guide canonical for the manifest; link all supporting pages.

<!-- autoplan-accepted:dx -->
- Publish and run a clearly fictional, copyable two-pair PostgreSQL 16 local walkthrough with prerequisites, complete manifest JSON, safe psql invocation, Source `DurableRuntimeSurface.Work` hosted/direct configuration, privilege proof command, expected allowed/denied outcomes, identical rerun, and omitted-pair refusal. Measure primed local time from dependencies-ready to first successful Work-only routing proof; report cold setup separately and do not claim an unmeasured under-five-minute result.
- Make the adoption guide the start-here operator sequence and the provider guide the canonical manifest, profile, and object-specific grant reference. Link the local transcript, upgrade/rollback guidance, and diagnostics from those pages; remove stale one-pair wording in every direct recipe adopter.
- For missing/invalid manifest values, missing/duplicate/aliased roles, omitted installed pair, broader grant/policy drift, and final assertion failures, emit bounded diagnostics with affected manifest entry or package object, problem, likely cause, safe corrective action, and nearby guide link. Assert representative problem/cause/fix messages in PostgreSQL integration tests while keeping credentials, payloads, and connection strings out of output.
<!-- /autoplan-accepted:dx -->

### Eng review — architecture, code quality, tests, performance

**Step 0 scope challenge.** The plan names more than eight files because the role recipe is a shipped operator input and its old single-pair callers, package copy, tests, examples, and adoption docs must change together. Scope remains the approved complete-manifest design. Existing leverage is the canonical [role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql), schema-manager advisory-lock convention, PostgreSQL 16/xUnit integration harness, Work-discovery `SECURITY DEFINER` function, provider Work surface, and existing NuGet/package-consumer path. No new runtime service, database registry, or distribution channel is justified. `TODOS.md` already tracks separately reviewed pair retirement. An independent `combo/sub` reviewer read the frozen Eng input and completed its full review; the external Codex process remains unavailable under this host. The reviewed input hash was `f8f02a74505b19f3f856b27bb87deb94cb663fabb688df627d24e5d2fade9c55`.

**Section 1 — Architecture.** The complete manifest is the deployment's reviewed authority, and the recipe checks catalog evidence under the migration lock before changing it. The current script reads roles before `BEGIN` and calls `pg_advisory_xact_lock` at line 107; moving role and privilege checks under the lock closes the concurrent-rerun gap. The union of managed policy targets and package ACL grantees can reveal an omitted pair in a normal installation, but no catalog-only algorithm can rediscover a formerly authorized pair after every trace of it has been erased by a more privileged actor. Therefore the implementation must (a) reject every omitted observable principal, unexplained target, and ambiguous/damaged policy or ACL state before DDL; (b) compare the reviewed full manifest against the previous release record during deployment; and (c) state this evidence limit in the guide and certificate. A registry would solve a different durability problem and remains deferred under the approved design. Critical gap before clarification: an unconditional “previously authorized” claim was not provable in a fully erased catalog. The clarified claim is operational and testable.

The rollout graph is:

```text
reviewed manifest + prior release record
          |               |
          v               v
  PostgreSQL 16 schema -> locked recipe validation -> exact policy/ACL reconcile
                                                  -> final assertions -> commit
                                         /                                \
                         forwarding full pair                  Source work_only pair
                         Work/Flow/Schedule                    Work discovery function
                                         \                                /
                                      one StoreId and active epoch
```

Each new integration point has a fail-closed path: malformed input or missing role exits before mutation; a lock timeout exits with the catalog unchanged; drift or omission exits before reconciliation; a late assertion error rolls back the transaction; a Work-only dispatcher attempting Flow/Schedule/direct SQL is denied; schema-11 mismatch blocks publication or activation under #795. The certificate uses SHA-256 over the **exact UTF-8 bytes of the reviewed manifest file passed to the recipe**, with file path and hash recorded beside role/profile names. Whitespace and pair-order edits change that byte hash and require a new review; no semantic canonicalization is promised. This favors a traceable file over a second parser and normalization protocol.

**Section 2 — Code quality.** Keep validation, all-pair grant construction, and assertions in the one canonical recipe, using set-based role OIDs and `%I` only for resolved identifier data. PostgreSQL `json` preserves duplicate keys while `jsonb` does not; inspect the original JSON top-level and each pair for duplicate property names before normalizing. Reject malformed JSON, wrong types, unknown/missing fields, and role-name truncation with bounded, non-secret diagnostics. Separate locked preflight from reconciliation and final assertions so an unexpected target is never silently repaired. The existing script drops/recreates the four runtime or retention policies at lines 162–184 on every invocation. For a healthy matching policy, leave its OID and definition intact; alter only target sets that legitimately change, and recreate a missing runtime policy only when the remaining catalog evidence proves a complete authorized set. Define stable rerun as equality of policy OIDs, expressions, targets, ACLs, owners, and effective privileges after the first successful reconciliation. Sequence/catalog timestamps or command notices need not be byte-identical.

The exact grant matrix must distinguish a normal migrated schema-10 starting state from hostile `PUBLIC` drift. Enumerate canonical migration-produced default privileges eligible for transactional revocation; reject unexpected `PUBLIC` or extra-principal grants before mutation, then assert no `PUBLIC` package capability at commit. Include schema/relation/column/sequence/function ACLs, grant options, ownership, and inherited effective rights. A listed pair's missing narrow grant may be restored. A `full` to `work_only` change refuses authority removal; a reviewed `work_only` to `full` expansion requires explicit manifest change, full grants/proof, and a new deployment certificate before use. No profile change is inferred from omission.

**Section 3 — Test-path diagram and gap audit.** The test framework is xUnit with a PostgreSQL 16 Testcontainers harness in [PostgreSqlSchemaIntegrationTests](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/PostgreSqlSchemaIntegrationTests.cs). Existing tests cover the single-pair recipe, hostile privileges, Work function selection, and lock contention. New tests must exercise actual package bytes and role-authenticated provider behavior rather than only source-text assertions.

```text
CODE PATHS                                                OPERATOR / PROVIDER FLOWS
manifest parse [new]                                      reviewed file -> recipe [new, E2E]
  |-- valid 1, 2, 32 pairs -> exact roles [GAP]              |-- forwarding before/after [GAP]
  |-- 0/33, type/version/field errors -> reject [GAP]        |-- Source Work-only pump/recovery [GAP]
  |-- duplicate JSON keys -> reject [GAP]                    |-- Source direct/Flow/Schedule denial [GAP]
  |-- hostile identifiers -> quote/reject [GAP]              |-- identical rerun + omission refusal [GAP]
role resolve + lock [existing single-role]                 |-- packed recipe byte equality [GAP]
  |-- duplicate/alias/missing/membership -> reject [GAP]     |-- schema 10 -> 11 reproof [#795]
  |-- concurrent order / 30s timeout -> unchanged [GAP]
catalog preflight [existing single-role]
  |-- normal migration PUBLIC baseline -> reconcile [GAP]
  |-- omission/drift/erased evidence -> reject [GAP]
  |-- profile narrowing/expansion -> reject/prove [GAP]
all-pair DDL + assertion [new]
  |-- exact ACL/policies + preserved OIDs on rerun [GAP]
  |-- missing narrow grant restored [GAP]
  |-- forced post-mutation assertion failure -> rollback [GAP]
```

The key regression proof is that adding Source and rerunning cannot remove the forwarding pair's policies, grants, or Work/Flow/Schedule processing. Snapshot relevant `pg_policy`, ACL, owner, membership, and effective-privilege values before each rejection and after success. A preflight-only failure does not prove post-DDL atomicity: inject one late assertion failure after at least one policy or grant statement, then assert the full snapshot is unchanged. Test both lock acquisition orders and a held-lock timeout with bounded SQLSTATE/operator guidance. Test the exact file hash equality with the certificate evidence and its change when bytes change. Assert representative problem/cause/fix/guide diagnostics without leaking a connection string. The executable paths are recorded in this plan's test diagram and exercised by the PostgreSQL schema integration tests.

**Section 4 — Performance.** The 32-pair ceiling bounds package-object privilege scans; no new provider hot-path query or cache is proposed. The expensive event is exclusive advisory-lock or policy-DDL contention, especially during a migration. Mirror the schema manager's 30-second default bound for role-recipe lock acquisition, emit a clear timeout and safe retry instruction, and prove the catalog is unchanged. Keep a separately bounded maintenance window for policy DDL and record observed durations in test/release evidence. The local walkthrough's under-five-minute primed target is a measurement target, not a release gate; cold setup and production certificate time remain separate. No p99 or zero-blocking claim is made.

**What remains outside #823.** Pair retirement and profile narrowing; Flow function migration for the existing forwarding dispatcher; database-owned pair registry; PostgreSQL row partitioning or a second store; production role creation, secret changes, source activation, new package distribution, and future schema-11 implementation owned by #795. The AppSurface package still must prove its schema-10 recipe and coordinate the #795 release gate.

**Failure and rescue register.** Missing/invalid manifest, role drift, omission, profile mismatch, extra grant or policy target, and unknown package object: nonzero before mutation with bounded corrective guidance. Lock timeout: nonzero and unchanged catalog. DDL or late assertion failure: transaction rollback and catalog equality. Work-only Flow/Schedule attempt: permission denied and Source remains closed. Catalog-wide erasure of a prior pair is beyond catalog-only reconstruction; the deployment's prior reviewed manifest and certificate are the independent source for that check. Critical silent gaps after these contracts: zero; the erasure limit must be stated rather than claimed solved by SQL.

**Parallelization.** Three coordinated lanes: SQL contract/recipe followed by database tests; docs/local example after manifest/profile contract freeze; package and #795 coordination after the recipe format and exact role set are settled. A single integration owner reconciles the grant matrix and package bytes. Two lanes can draft concurrently; recipe and its executable proof remain sequential.

**Eng completion summary.** Scope kept; architecture 3 issues, code quality 3, test review 7 gap groups, performance 2 issues (lock contention overlaps architecture); test diagram and QA artifact produced; no UI or eval suite applies; no additional TODO beyond already-recorded retirement. Native reviewer completed with concerns; outside review unavailable under Codex host; cross-provider consensus N/A. The independent reviewer suggested semantic hashing, but exact reviewed-file hashing is chosen for auditability and lower complexity. No unresolved implementation choice remains; certificate owner confirmation is a release gate, not a design ambiguity.

### Eng implementation tasks

- [ ] **E1 (P1; human ~3h / agent ~30m):** Implement complete-set catalog preflight with conservative refusal on ambiguous state; document the catalog-erasure limit and compare against the prior reviewed deployment manifest. Verify omitted-pair and damaged-catalog cases.
- [ ] **E2 (P1; human ~3h / agent ~30m):** Strictly validate original JSON keys, pair bounds, exact fields, role OIDs, and posture under the lock; preserve policy identity on healthy reruns. Verify valid and malformed matrices plus catalog-stable reruns.
- [ ] **E3 (P1; human ~3h / agent ~30m):** Prove all-pair exact grants and transactional rollback after mutation, including profile transitions and migration-produced versus hostile `PUBLIC` privileges. Verify catalog snapshots and Work-only denial.
- [ ] **E4 (P2; human ~1h / agent ~10m):** Bound the recipe lock wait at 30 seconds and report safe retry guidance; verify held-lock timeout and unchanged state.
- [ ] **E5 (P2; human ~1h / agent ~10m):** Bind release evidence to SHA-256 of the exact reviewed manifest file bytes used by psql; verify hash/file match in the disposable walkthrough and deployment checklist.

<!-- autoplan-accepted:eng -->
- Keep the complete reviewed manifest as the deployment authority. Under the advisory lock, reject every omitted role visible in the union of managed policy and package-ACL targets, and fail closed on contradictory or ambiguous catalog state before mutation. Compare the invocation with the prior reviewed release manifest; document that total erasure of a role's catalog traces cannot be detected by catalog inference alone and would need a separately designed registry for database-only proof.
- Hash the exact UTF-8 bytes of the reviewed manifest file used by the recipe for certificate evidence; record its path, digest, and role/profile names. Require re-review when file bytes change, including whitespace or order changes. Reject duplicate JSON properties on the original `json` representation before any `jsonb` conversion.
- Make healthy identical reruns preserve policy OIDs, targets, expressions, ACLs, owners, and effective privileges. Recreate missing runtime policies only with sufficient remaining catalog evidence; reject ambiguous state. Add a forced post-mutation failure test proving complete rollback.
- Use the schema manager's 30-second default as the role-recipe advisory-lock acquisition bound, with a clear nonzero timeout and safe retry guidance; test contention and unchanged catalog. Keep policy-DDL maintenance impact measured and bounded separately.
- Define schema-10 normal migration privilege baseline and object-specific ACL/refusal matrix, including `PUBLIC`, ownership, grant options, and inherited rights. Test missing narrow grant restoration, hostile drift, and both profile-transition directions; require new certificate proof for an explicit reviewed `work_only` to `full` expansion and refuse narrowing in #823.
<!-- /autoplan-accepted:eng -->

### Cross-phase themes and voice coverage

| Theme | Independent phase signals | Disposition |
| --- | --- | --- |
| Keep both pairs on every run | CEO and Eng native reviewers; approved design | Complete manifest, catalog omission check, prior reviewed release record, stable rerun proof |
| Tie proof to the deployed configuration | CEO, DX, and Eng native reviewers | Exact package/schema, manifest file hash, names/profiles, surfaces, StoreId/epoch, and owner signoff |
| Make failure safe and diagnosable | DX and Eng native reviewers | Bounded lock/validation errors, unchanged-catalog proof, Source closed on failure |
| Keep the certificate claim narrow | CEO and Eng native reviewers; user requirement | Deployed-lane credential boundary; no database row-isolation claim |

| Phase | Native `combo/sub` result | External Codex CLI | Consensus |
| --- | --- | --- | --- |
| CEO | Completed; challenged row-isolation language, release proof, cost and retirement | Unavailable under Codex host | N/A; no two-provider agreement claimed |
| Design | Skipped: no UI scope | Skipped | N/A |
| DX | Completed; identified example, diagnostics, docs, rerun and choice-copy gaps | Unavailable under Codex host | N/A; no two-provider agreement claimed |
| Eng | Completed; identified catalog evidence, hash, lock, JSON, rerun, ACL and test gaps | Unavailable under Codex host | N/A; no two-provider agreement claimed |

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
| --- | --- | --- | ---: | --- | --- |
| CEO Review | `/autoplan` CEO phase | Scope and security claim | 1 | Clear | Two same-scope additions; role retirement deferred |
| Outside Review | Codex CLI through `/autoplan` | Independent provider check | 0 | Unavailable | Harness forbids nested Codex process |
| Eng Review | `/autoplan` Eng phase | Architecture and tests | 1 | Clear for planning | 15 review issues/gap groups folded into requirements; 0 critical silent gaps |
| Design Review | `/autoplan` design phase | UI/UX | 0 | Skipped | No UI scope |
| DX Review | `/autoplan` DX phase | Developer experience | 1 | Clear for planning | Estimated 6 to 8.5/10; local proof time unmeasured |

**OUTSIDE COVERAGE:** CEO, DX, and Eng each had a completed native `combo/sub` review. External Codex CLI was unavailable under this Codex host for all three; Design was skipped because there is no UI. No cross-provider consensus is claimed.

**VERDICT:** CEO, DX, and Eng plan reviews complete; user approved the plan as-is on 2026-09-24. The certificate and schema-11 checks remain release gates as specified above.

NO UNRESOLVED DECISIONS
