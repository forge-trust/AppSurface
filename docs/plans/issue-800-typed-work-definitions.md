<!-- /autoplan restore point: /Users/andrew/.gstack/projects/forge-trust-AppSurface/main-autoplan-restore-20260910-093630.md -->
# Implementation plan: typed Durable Work definitions (#800)

Status: APPROVED — user approved the reviewed plan and all recommendations on 2026-09-10.

The [approved design](../designs/issue-800-typed-work-definitions.md) is the normative public API and compatibility contract. This plan supplies implementation tasks and review amendments for [#800](https://github.com/forge-trust/AppSurface/issues/800); it preserves the user's selected shared internal snapshot (approach B), all three binding forms, and explicit request choices.

Implementation baseline: `48c6db61ac5f742bea9f1ee434860daf72df791c` (`origin/main`, includes Work exits). At intake this checkout was two documentation commits ahead and eight commits behind that baseline. Implementation must start on a branch containing the baseline; this review does not change the branch or implement the API. Companion deliverables: [test plan](issue-800-typed-work-definitions-test-plan.md) and [aggregated implementation tasks](issue-800-typed-work-definitions-tasks.md).

## Review setup

CEO selective expansion → visual design skipped (no UI) → full SDK/library DX review → full engineering review last. Independent review uses native `combo/sub` agents under the user's model policy. The skill's separate Codex CLI and Claude-labelled outside voices are not invoked; outside-voice coverage is reported as `subagent-only`, without claiming independent model-family consensus. Main Codex review and the routed subagent are identified separately.

The autoplan preamble returned protocol 1 and interactive mode. Its update check failed and its analytics write was denied by the filesystem sandbox; neither is a review result. Continuous local checkpoints are enabled, with push disabled and artifact sync off. No successful telemetry upload is claimed.

## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
| --- | --- | --- | --- | --- | --- | --- |
| A01 | Intake | Preserve the approved design as source; review a separate plan | Mechanical | P4 | Prevent competing public API designs; keep a restore point | Rewriting the approved design silently |
| A02 | Intake | No visual design phase; run SDK/library DX | Mechanical | P3 | Codec views and binding forms are API concepts; no user-interface surface | Scoring nonexistent screens |
| A03 | Intake | Use native combo/sub outside reviewers; report subagent-only coverage | Mechanical | User policy | Honor explicit model/transport rules and record review limits | Claiming Claude plus Codex CLI consensus |
| A04 | CEO | Selective expansion; retain approved B and all compatibility work | Mechanical | P1, P4 | Existing Flow reference checks make compatibility work necessary | Removing regression protection to shrink the diff |
| A05 | CEO | Add a syntax-migration versus semantic-migration rollout matrix | Mechanical | P1 | Existing exit rollout rules must remain visible to adopters | Treating all package upgrades as safe executor-style changes |
| A06 | CEO | Extend the representative PostgreSQL proof through terminal success | Mechanical | P1, P2 | Fits the existing integration fixture without new infrastructure | Calling acceptance alone durable completion |
| A07 | CEO | Keep real-user timing and competitor experiments outside the #800 release gate | Taste T1 | P3, P6 | The parent owns broader adoption evidence; #800 can prove correctness without invented timing | Blocking this API on an external adopter study |
| A08 | CEO | Tie every provenance change to an existing-code regression witness | Mechanical | P4, P5 | Bounds shared snapshot complexity to demonstrated compatibility seams | General-purpose registry or discovery framework |
| A09 | CEO | Record an adoption review trigger in TODOS | Mechanical | P3 | Limits future convenience APIs to demonstrated need | New telemetry or a scheduled task |
| A10 | DX | SDK/library persona; DX POLISH | Mechanical | P3, P6 | Existing .NET module authors need precise adoption guidance | New-product UI or hosted-platform scope |
| A11 | DX | Lead with a complete passive definition example and explicit output | Mechanical | P1, P5 | First feedback must prove registration/request consistency without requiring storage | Calling a passive request proof durable completion |
| A12 | DX | Keep complete old/new examples in compiled packed source and render managed snippets | Mechanical | P4 | Existing snippet tool prevents docs from drifting from package syntax | Hand-maintained parallel examples |
| A13 | DX | Add fixed corrective language for new guards; map existing errors to a migration table | Mechanical | P1 | Actionable errors can preserve legacy duplicate messages and privacy bounds | Arbitrary codec values or new provider codes in exceptions |
| A14 | DX | Set a primed passive-proof target under five minutes; report all measured durations honestly | Mechanical | P3 | Separate quick contract feedback from cold setup and terminal completion | Unmeasured champion ranking |
| A15 | Eng | Keep all approved compatibility seams; use one internal entry/capture model | Mechanical | P4, P5 | Current Work/Flow and registry reference checks require these changes | Removing cases or adding public snapshot extension points |
| A16 | Eng | Make manual registry insert/upgrade atomic across both indexes | Mechanical | P1 | Thread-safe registry must not expose stale or duplicate type entries | Updating only the contract dictionary |
| A17 | Eng | Read back every accepted request field; distinguish changed-default conflict from equivalent override replay | Mechanical | P1 | Duplicate identity alone does not prove persisted facts | Incorrectly expecting a changed fingerprint to deduplicate |
| A18 | Eng | Use indexed source/contract aggregation and count-based scalability checks | Mechanical | P3, P5 | Avoid quadratic startup without brittle wall-clock budgets | New global cache or arbitrary performance claim |
| A19 | Eng | Use compiler SARIF and fixture-specific exact diagnostics with positive controls | Mechanical | P1 | Negative builds must fail for the intended public API constraint | Broad error-code-family or any-failure acceptance |
| A20 | Eng | Link the packed example source into the focused proof test | Mechanical | P4 | One compiled example body must own the documentation proof | Parallel drifting test-only example |

## Review amendments

This plan adds executable sequencing and evidence to the approved design. Public API names, constraints, request argument order, required retry default, codec snapshot semantics, and custom-registry ownership remain governed by that design.

## CEO review — scope and strategy

### System audit and premise challenge

The working tree started clean. The merge-base diff contains only the approved design; the large two-dot diff against `origin/main` reflects eight missing upstream commits, not proposed deletions. Four unrelated stashes were observed and left alone. There is no root `CLAUDE.md`; [AGENTS.md](../../AGENTS.md) requires documentation and nearly complete branch verification. [TODOS.md](../../TODOS.md) contains no prerequisite that blocks #800. Recent Durable changes include scoped discovery, public-preview distribution, and the upstream typed-exit contract, so compatibility and packaging are the high-risk seams.

| Premise | Evidence and assessment | Decision |
| --- | --- | --- |
| Repeated contract facts cause avoidable authoring risk | [#793](https://github.com/forge-trust/AppSurface/issues/793) reports seven adopter lanes; existing registration and request constructors separately accept Work facts | Valid problem; remove repeated facts and prove request equality |
| A shorter registration is the product outcome | Parent evidence records 6 → 10 lines; it explicitly makes line count secondary | Reject this proxy; report eliminated facts and correct completion |
| Static definitions can freeze arbitrary application behavior | Metadata capture freezes facts, but codecs and approval predicates remain caller code | Preserve the approved, limited concurrency guarantee |
| Registry and Flow work is optional polish | Current registry coalesces identical references; Flow activity, context, and event checks require exact references | Necessary regression work once definitions expose guarded views |
| The existing runtime and distribution can be reused | Existing request fingerprint, passive registries, packed consumers, PostgreSQL fixture, package workflows | Reuse; no new runtime, package, or database migration |
| Typed bindings deliver faster first completion | No measured evidence supplied; #793's end-to-end template clocks belong to later cases | Unproven; do not advertise a time reduction |

**Landscape:** conventional .NET durable-work SDKs already offer typed invocation. [Temporal's SDK](https://github.com/temporalio/sdk-dotnet/blob/main/README.md) supports typed workflow calls and dynamic alternatives. The inference for this plan is that type safety alone is not a unique market claim: AppSurface's useful distinction here is explicit immutable contract facts within its existing native protocol. A local application helper is the smallest alternative, but leaves every adopter maintaining the same consistency rules. No market-share or competitor-speed conclusion was measured.

### What already exists

All code evidence below was read at the pinned implementation baseline; relative links become current after implementation starts on that baseline.

| Subproblem | Existing implementation | Planned reuse / regression witness |
| --- | --- | --- |
| Identifier validation, payload bounds and byte immutability | [DurablePayload.cs](../../Durable/ForgeTrust.AppSurface.Durable/DurablePayload.cs), `DurableIdentifier` | Reuse bounds and payload object; no alternate hash/serializer |
| Accepted policy and canonical fingerprint | [DurableWorkContracts.cs](../../Durable/ForgeTrust.AppSurface.Durable/DurableWorkContracts.cs) | Factory calls existing request constructor; compare all fields as well as hash |
| Ordinary, reconciled and exit invocation | [DurableWorkRegistration.cs](../../Durable/ForgeTrust.AppSurface.Durable/DurableWorkRegistration.cs) | Common builder feeds existing prepared invocation; preserve exit compatibility boundary |
| Registry duplicate behavior | `DurableWorkRegistry` and `DurablePayloadCodecRegistry` | Preserve Work duplicates, same-source coalescing, exact codec identity lookup |
| Flow codec compatibility | [DurableFlowRegistration.cs](../../Durable/ForgeTrust.AppSurface.Durable/DurableFlowRegistration.cs) | Replace only codec reference checks with package provenance; keep exact Work-registration check |
| Passive module composition | [AppSurfaceDurableModule.cs](../../Durable/ForgeTrust.AppSurface.Durable/AppSurfaceDurableModule.cs) | One default installation helper and per-provider catalog; no host activation |
| Package proof | [verify-packed-consumers.sh](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-packed-consumers.sh) | Retain executable consumers and add typed/negative fixtures |
| Database execution and rollout | [Work protocol](../../Durable/work-protocol-v1.md), [PostgreSQL verification](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-postgresql.sh) | Bounded fixture, same acceptance and runtime authority |

The payload object and explicit prepared invocation are style references: small immutable values and clear effect boundaries. Repeated `TryAddSingleton` registry setup across module/Work/Flow is the duplication to remove. Growing the already large contract test class or hiding selection in single-service generic DI lookup would make maintenance worse.

### Dream state and alternatives

```text
CURRENT                       #800                         12-MONTH IDEAL
repeat Work facts       ->    one typed immutable     ->    adopters compose tested
at registration/enqueue       definition, explicit          Work contracts, testing,
custom helper per app         request policy, same          activation and diagnostics
                              accepted protocol             through the parent rail
```

The broader opportunity is a complete durable lifecycle that is easy to demonstrate. This case supplies the contract source of truth; later [adoption-rail cases](https://github.com/forge-trust/AppSurface/issues/793) own testing, activation, diagnostics, and templates. That is the dream-state delta, not scope silently imported into #800.

| Approach | Effort / risk | Strength | Cost / outcome |
| --- | --- | --- | --- |
| Minimal: app-owned helper and examples | S / low framework risk | Reuses every current API | Does not deliver the requested library contract; consistency logic remains adopter-owned |
| A: adapters as primary definition model | M / medium compatibility risk | Smaller initial definition implementation | Multiple identity/capture paths; rejected by the user during office hours |
| B: shared internal snapshot, thin views | L / medium compatibility risk | One closed fact set across definition, binding, registration and contributions | Requires precise provenance and Flow integration; chosen and retained |

Effort categories are planning estimates, not measured delivery promises: B is approximately 3–5 human engineering days or 1–2 focused agent-assisted days including reviews and environment-dependent verification. The smallest complete implementation still needs all three bindings, legacy composition, registry and Flow compatibility, package proofs and docs. The >8-file complexity trigger is justified by those obligations; no generic contract framework is added.

Five adjacent opportunities were considered: (1) a replacement-versus-double-registration migration example, accepted as required documentation; (2) expected proof output, accepted in DX; (3) terminal completion in the existing PostgreSQL proof, accepted; (4) a provenance-to-regression map, accepted; (5) a six-month adopter review, deferred to TODOS. A hosted playground, telemetry collection, code generator, or new diagnostic command would add separate infrastructure or duplicate the parent rail and is outside this case.

### Temporal interrogation

| Stage | Decision resolved before implementation |
| --- | --- |
| Hour 1 — foundations | Start from the pinned exit-capable baseline. Keep snapshot/provenance types internal and use existing validators. |
| Hours 2–3 — core | Reuse request constructor; capture metadata once; ordinary reconciler-required binding may be intermediate but cannot register incomplete. |
| Hours 4–5 — integration | Catalog must validate Work identity before codec enumeration; Flow must execute the codec it validated. |
| Hour 6+ — verification | Diagnostic-specific negative builds must not pass on restore failure; terminal completion and rollback need explicit evidence. |

These are human-team decision stages, not a six-hour schedule. Agent assistance shortens edits and repetitive verification, but database setup and review remain real elapsed work.

### 1. Architecture

The definition is passive configuration, while the existing request and provider retain execution authority. New coupling is limited to package-internal metadata/provenance helpers consumed by Work registration and the existing Flow adapter. Both default registries share a validated catalog; the codec factory must never resolve the public Work registry because a custom implementation can depend on codecs and create a cycle.

```text
Define -> Work snapshot -> definition -> CreateRequest -> existing request -> provider
                    |          |
                    |          +-> ordinary/reconciled/exit binding
                    v                        |
legacy facts -> common closure/builder <-----+
                    |
             registration values -> catalog -> default Work registry
                    |                 |
              codec contributions <---+ -> default codec registry
                                                 |
Flow registration -> selected Work + codec registries -> evaluate locally
```

The single-point risks are custom codec behavior and registry construction errors, both explicit failures rather than background recovery systems. Configuration size scales with registered contracts; request creation has no DI lookup, database query, or new network hop. Preserve the existing immutable contract identities during syntax-only migration, and use the rollout matrix for semantic changes.

### 2. Error and rescue registry

Library authoring failures should surface immediately to the caller. They must not be swallowed or retried by a definition; provider failures remain governed by the existing [diagnostics catalog](../../troubleshooting/durable-diagnostics.md). User codec exceptions propagate, so library documentation must warn callers that arbitrary custom exception messages are not guaranteed privacy-safe.

| Method/path | Failure and exception | Rescued? / action | Developer sees / proof |
| --- | --- | --- | --- |
| `Define` | Null codecs/default, invalid identifiers or metadata: argument exceptions; undefined enums: `ArgumentOutOfRangeException` | No; correct configuration | Parameter-specific error; boundary tests |
| Codec metadata capture | Custom getter throws its original exception | No; fix custom codec | Original cause; one-read/throwing-getter test |
| `CreateRequest` | Invalid scope/command/key/null Work: argument exception before encode | No; correct call | Caller argument and stack; codec not called |
| Guarded encode | Policy refusal/size: existing `ArgumentException`; serializer: original exception; null/mismatched metadata: `InvalidOperationException` | No; reject, never relabel bytes | Fixed bounded mismatch explanation; field-by-field tests |
| Guarded decode | Incompatible metadata or null/wrong untyped output: `InvalidOperationException`; malformed source JSON: `JsonException` | No; reject before unsafe decode where possible | Correct codec/version remediation; counter tests |
| Binding/register | Wrong safety/missing reconciler: existing argument conventions; no partial descriptors | No; select valid binding | Binding matrix plus collection-unchanged assertion |
| Work catalog | Duplicate `(WorkName,WorkVersion)`: existing `InvalidOperationException` | No; replace old registration | Work duplicate wins before incidental codec conflict |
| Codec aggregation / `Register` | Same source conflicting snapshot or unrelated source same codec identity: `InvalidOperationException` | No; fix source/version ownership | Existing duplicate message or fixed snapshot conflict text |
| Exact/type-only codec lookup | Missing/wrong type/ambiguous versions: `InvalidOperationException` | No; choose exact type/name/version | Existing exact-lookup guidance |
| Flow constructor / registry / evaluation | Incompatible provenance: existing argument/invariant exception boundary | No fallback to stored raw codec | Registration failure or visible evaluation failure |
| Executor/reconciler/exit invocation | Original failures and existing exit compatibility exception | Existing provider policy only | Existing Work outcome/diagnostic; landed exit regression suite |
| Packed negative harness | Restore failure, unexpected error, no exact marked diagnostic, positive control fails | Fail harness; no false success | Fixture-specific build log and nonzero proof exit |

### 3. Security and threat model

The API adds no remote endpoint, authority, secret, SQL command, or payload logging. Metadata mismatch is a medium-likelihood/high-impact integrity risk if a view merely relabels incompatible bytes; exact guards and immutable payload reuse prevent that path. A consumer-controlled codec can execute arbitrary application code already, so source provenance is compatibility evidence rather than a security sandbox; private package provenance must not be forgeable through public metadata equality.

Scope and command remain caller-owned and validated by existing rules. Frozen classification/retention must be checked on both encode and decode, including Flow context/event paths, to avoid a guard bypass. Fixed library-generated errors contain no payload bytes or request keys; any logging or telemetry expansion is rejected as unnecessary exposure.

### 4. Data flow and interaction edges

```text
facts -> validate -> capture once -> definition -> bind -> contribution -> provider catalog
  |          |             |                     |           |               |
 null     empty/invalid   getter throws         bad safety   duplicate      fail closed

request input -> validate -> guarded encode -> existing request -> existing transaction
  |                 |              |                 |                    |
 null/default      empty key     refusal/mismatch   bytes copied        Duplicate/conflict
                                                                            |
                                                                      claim -> terminal
```

Empty identifiers are rejected; empty payload bytes remain codec policy, rather than gaining a blanket rejection. A failed bind/register call must leave its immutable source and service collection unchanged. Repeated API submission remains subject to existing command/idempotency semantics, concurrent factory calls share no mutable request state, and partial acceptance follows the existing transaction rollback boundary.

```text
untrusted facts --valid--> immutable definition --bind--> immutable binding
       |                         |                          |
     throw                    no setters                 register once per identity
                                                          |
                                                     duplicate -> throw

request created -> accepted [existing state machine] -> existing claim/outcome states
```

There is no new runtime state machine. Browser double-click/navigation rules do not apply; the relevant interactions are repeated registration, repeated submission, package migration and concurrent calls.

### 5. Code quality

Keep one validator/capture routine, one source-provenance comparison and one default-registry installer. The new snapshot should contain contract facts, not become a plugin interface, reflection map or application policy engine. Split definition/binding tests into focused files and keep constructors readable; a large aggregator should be decomposed around capture, conflict validation and publication, with no duplicated authoritative maps that can diverge.

### 6. Tests

```text
new definition/request -> unit: boundaries, one capture, exact fields/fingerprint
new bindings          -> unit: safety matrix + package compiler constraints
new registry views    -> unit: source/order/custom-registry/lifetime matrix
Flow compatibility    -> integration: activity + context + wait/resume use guards
accepted Work         -> PostgreSQL: replay/conflict/immutable fields + terminal success
distribution          -> packed: legacy + 3 new forms + precise negative fixtures
```

The shipping-confidence test uses a static definition in two providers, shared Flow codecs and unchanged accepted Work facts. The hostile test changes each captured metadata field after construction and returns incompatible payload metadata, while the chaos test uses concurrent factory calls and deterministic order permutations. Avoid wall-clock sleeps and global mutable test state; PostgreSQL tests reuse the bounded fixture and fail if required database tests are skipped.

### 7. Performance

The three new costs are metadata capture during construction, registry aggregation during first resolution, and constant metadata guards around codec invocation. No new database indexes, polling or connection pools are justified because request persistence is unchanged. Use operation-count tests to ensure one capture per source and one encode per request; keep registry building linear in contributions and type lookup indexed, and do not invent p99 measurements before code exists.

### 8. Observability and debugging

This passive API is best diagnosed through precise exceptions and a migration troubleshooting table. Adding per-request entry/exit logs or a new metrics backend would impose overhead and duplicate existing provider observability. The representative proof must report accepted, duplicate and terminal-success outcomes separately, and documentation must map construction/registration failures to corrective examples while leaving existing provider diagnostics intact.

### 9. Deployment and rollout

| Change | Same Work version? | Mixed deployment / rollback rule |
| --- | --- | --- |
| Syntax-only legacy → definition, identical codec behavior, safety, executor style and request facts | Yes | Replace the old registration in each host. Old/new binaries may coexist only while they honor identical contract behavior. |
| Register both forms for one identity in one provider | No | Duplicate Work error, irrespective of registration order; never select a winner. |
| Change definition retry default only | Existing version can create different future requests | Accepted records keep old policy; intentional request overrides remain explicit. Existing duplicate keys may conflict if semantics differ. |
| Change codec semantics, safety or execution/binding style | Use existing version-rollout discipline; new immutable Work version where required | Do not reinterpret historical Work. Validate new version before accepting it; retain workers for old versions. |
| Introduce exit-aware Work to a fleet lacking exit capability | New Work version after capability deployment | Follow [typed exit rules](../../Durable/work-protocol-v1.md#typed-executor-exits); all eligible workers must support the final provider path before new acceptance. |
| Use invalid mutable codec behavior that older code happened to tolerate | Unsupported behavior, not guaranteed migration compatibility | Repair the codec/version; guards fail closed and do not silently convert bytes. |

```text
pack + proof -> update one representative host -> validate registries
            -> unchanged-version enqueue/replay -> terminal success -> wider adoption
failure -> stop new submissions -> retain capable workers for accepted versions
        -> revert composition/package only if old workers honor those accepted contracts
```

A syntax-only migration needs no database schema migration or runtime feature flag, but still requires the documented operational rollout and rollback checks. In the first representative run verify registry resolution and expected request facts; during the bounded processing run verify terminal success and existing diagnostics. A rollback that removes the only workers capable of accepted new-version Work is unsafe even though this API changes no database schema.

### 10. Long-term trajectory

The snapshot's internal visibility keeps it refactorable (4/5 reversibility), while published generic bindings and their behavior create a lasting compatibility obligation (2/5). Later rail cases may consume the public definition, but should not receive internal snapshot/provenance as an extension API. Review adopter migrations and friction after six months or two independent migrations, whichever comes first, before proposing additional convenience surfaces; that is a documented follow-up, not an automation.

### NOT in scope

1. New runtime/testing/doctor/template/activation products — owned by [#793's other cases](https://github.com/forge-trust/AppSurface/issues/793).
2. Deadline or persisted attempt-plan changes — owned by [#765](https://github.com/forge-trust/AppSurface/issues/765).
3. New public Flow definition/registration helper — existing low-level composition can preserve exact Work registration identity.
4. Hosted playground, onboarding telemetry or competitor timing program — needs separate infrastructure/adopter evidence.
5. Codemod or deprecation of legacy APIs — explicit application facts cannot be safely inferred.
6. Database migration or new package — existing protocol and coordinated release process suffice.

### Failure modes registry

| Path | Failure | Rescue / visibility | Required test | Logging | Critical gap after amendments |
| --- | --- | --- | --- | --- | --- |
| Capture | Mutable metadata reused accidentally | Throw on conflicting closed contributions | Getter count + same-source conflicting snapshots | No payload logs | None planned |
| Encode/decode | Guard validates one codec but executes another | Reject; use validated guarded view | Source/view counters and bad outputs | Caller exception | None planned |
| DI | Duplicate Work masked by codec conflict or cycle | Catalog first; acyclic factories | Both orders, custom registry cases | Caller exception | None planned |
| Flow | Shared raw source no longer compares equal | Provenance-aware codec checks only | Context/event/activity paths; retain Work identity check | Existing evaluation diagnostic | None planned |
| Submission | Definition change rewrites accepted facts | Existing immutable request and store | Replay/conflict/store-field assertions | Existing provider diagnostics | None planned |
| Rollout | Old worker cannot execute accepted exit Work | Capability-first rollout; retain capable workers | Landed exit cases + documented matrix | Existing compatibility diagnostic | None planned |
| Package proof | Broken infrastructure masquerades as compile rejection | Harness must fail | Valid restore + exact line/code + positive control | Fixture output | None planned |
| Adoption proof | Acceptance reported as completion | Require a terminal-success assertion | Bounded PostgreSQL execution | Separate outcomes | None planned |

### CEO outside review and resolution

The native `combo/sub` reviewer raised five concerns and scored the starting specification 8.2/10 (subjective). Mixed rollout is addressed above; representative terminal completion is accepted; the actual-code map justifies every registry/Flow seam. The call for a new real-user/competitor benchmark as a release gate is T1: keep correctness evidence mandatory, retain timing as follow-up evidence. The reviewer counted four public types, but the approved table contains five including the static factory; the accepted surface remains that table.

| Dimension | Primary Codex assessment | Routed outside reviewer | Codex CLI / Claude-labelled voice consensus |
| --- | --- | --- | --- |
| Premises | Repetition valid; speed unmeasured | Same concern | N/A — named dual transport not run |
| Right problem | Contract consistency | Agrees; requests stronger adoption proof | N/A |
| Scope | Flow compatibility necessary from actual checks | Concern about machinery dominating value | N/A — resolved with regression map |
| Alternatives | Preserve approved B | Preserve approved B | N/A |
| Competitive risk | No performance/market claim | Requires evidence before claims | N/A |
| Trajectory | Internal snapshot; follow-up on real adoption | Six-month review requested | N/A |

No dual-model consensus is claimed (0/6 CONFIRMED in the skill's named-voice matrix). There is one surfaced judgment difference, T1, and no proposal supported by both reviewers to reverse a settled user choice.

A fresh native specification reviewer then checked the CEO amendments against the approved design: PASS, subjective 9.3/10. Its one wording clarification distinguished a database schema migration from the still-required operational rollout; applied above. No contradiction remained.

### CEO completion summary

| Review element | Result |
| --- | --- |
| Mode / system audit / Step 0 | Selective expansion; pinned upstream baseline; approved B retained |
| Architecture | 1 explicit regression-map amendment |
| Errors | 12 failure paths; no unnamed rescue obligation |
| Security | Metadata integrity and custom-code boundary assessed; no new external surface |
| Data / interaction | Null, empty, error, duplicate, concurrent and rolling-upgrade cases mapped |
| Quality | Existing helper duplication addressed in shared installer |
| Tests | Diagram produced; terminal-completion proof added |
| Performance | Three cost centers; linear construction and constant hot-path guards |
| Observability | Expected proof outcomes and corrective documentation; no new backend |
| Deployment | 6-row rollout matrix and safe rollback flow |
| Future | Internal 4/5, public 2/5 reversibility; one follow-up item |
| Visual design | Skipped — no UI scope |
| Required artifacts | NOT in scope, reuse, dream delta, error table and 8-row failure registry written |
| Open items | T1 recommendation approved; no unresolved plan decisions or critical design gaps; implementation unverified |

## Developer experience review

**Product / mode:** Library/SDK, DX POLISH. **Persona:** a .NET backend or reusable-module author who already understands DI and needs durable Work without importing host policy. They encounter the package through the [package chooser](../../packages/README.md) and [Durable overview](../../Durable/README.md). A migrating adopter expects familiar executor contracts, a complete source example, useful IntelliSense and no surprise change to accepted Work. A five-minute primed contract-proof goal is a review target, not an observed tolerance or measured result.

### Developer perspective

I open the package README and see “Choose this package when.” It tells me this package describes durable Work and that it starts no runtime. That matches my module: I want to publish a contract without choosing a database for every application that uses it. The next useful heading is “Passive registration proof.” It gives me a complete class and a test command, and its output clearly says that no runtime was installed. I can understand that success without mistaking it for a completed job.

Now I want to define my own Work. The existing exit-aware example is concrete, but it makes me choose an execution style before I have seen the ordinary typed-definition path. I need the new page to show the payload types, generated JSON context, codec instances, definition, executor and request together. If I copy only the short sketch, I will still be hunting for those missing pieces.

When I migrate, my instinct is to add the new registration beside the old one and test it. The documentation should stop that mistake explicitly: replace the old registration for the same identity. If I hit a codec mismatch, I need to learn which contract boundary rejected it and how to fix the codec. I do not need internal snapshot terminology until I deliberately reuse codecs across Work and Flow. This narrative is a source-grounded roleplay, not a recorded user session.

### Competitive benchmark and first useful result

| Tool | Observed authoring choice from primary docs | Measured TTHW in this review |
| --- | --- | --- |
| [Temporal .NET](https://github.com/temporalio/sdk-dotnet/blob/main/README.md) | Typed workflow lambdas alongside dynamic calls | Not measured |
| [MassTransit job consumers](https://masstransit.massient.com/concepts/job-consumers) | Typed job interface, executable sample, explicit runtime prerequisites | Not measured |
| [Hangfire](https://docs.hangfire.io/en/latest/background-methods/calling-methods-in-background.html) | Concise enqueue call; docs distinguish queuing from worker execution | Not measured |
| AppSurface #800 | Explicit definition + typed binding + request, using current passive package | Not measured; target <5 min for a primed contract proof |

These are comparable authoring choices, not interchangeable execution models or performance rankings. The selected delivery vehicle is the existing compiled source consumer and a copyable focused test command. No playground, network account, or added package is needed to demonstrate that a definition produces the same immutable request as direct construction.

**First useful-result specification:** add `TypedWorkDefinitionProof.cs` beside the packed Adopter's existing examples. Its `Run` method composes ordinary/reconciled/exit examples, creates direct and factory requests, checks exact field/fingerprint equality and passive DI, and is called by the existing `Program.cs`. It prints a fixed summary such as `typed Work contracts registered; request parity verified; no runtime installed`. If an assertion fails it must exit nonzero. The README must explicitly direct readers to the existing PostgreSQL example/proof for actual acceptance and terminal completion.

The source example must include `using` directives, payload/result records, source-generated JSON metadata, source codec fields, definition, executor/reconciler implementations, binding selection, provider construction/disposal and visible scope/command/key/retry/due decisions. Avoid service-provider construction inside a DI factory. The Flow example must resolve the exact global Work registration in its existing factory-based composition path.

### Journey map

| Stage | Developer action | Evidence / friction and planned result |
| --- | --- | --- |
| 1. Discover | Open package chooser and Durable overview | Link directly to the new typed Work section; leave audience boundaries clear |
| 2. Evaluate | Read ordinary definition/request example | Lead with contract consistency; show expected passive output and link real execution |
| 3. Install | Add existing `ForgeTrust.AppSurface.Durable` package | Document .NET 10 and the actual coordinated preview version after release; no invented version |
| 4. Hello world | Run the named passive proof | Full source + command, no Docker; separate package restore from execution time |
| 5. Integrate | Pick ordinary, reconciled or exit binding | Compact safety/binding table; preserve explicit per-request choices |
| 6. Debug | Follow a validation/duplicate/codec failure | Problem/cause/fix table with safe, stable explanations |
| 7. Upgrade | Replace legacy registration with equivalent binding | Side-by-side examples for all three forms; no obsolete annotation/codemod claim |
| 8. Scale | Reuse static definitions across providers and concurrent requests | Document codec author's thread safety; single-service generic DI does not select a Work identity |
| 9. Migrate / leave | Use legacy direct request or custom registry | Preserve documented escape hatches; apply existing Work-version rollout discipline |

**First-time confusion trace (illustrative timestamps, not measured):** T+0:00 package README establishes passive scope; T+0:30 ordinary typed example should precede specialized exits; T+1:00 missing payload/JSON context would stop a copied sketch; T+2:00 adding instead of replacing registration would produce the existing duplicate error; T+3:00 a passing request proof still has no storage/worker. A11–A13 address these by complete source, expected output, prominent replacement guidance and a separate actual-completion path.

### 1. Getting started

Current design completeness is 7/10: it requires compiled examples but leaves the entry point and first visible result implicit. Add the source-owned passive proof, link its ordinary case first and keep the existing explicit runtime boundary above it. A 10/10 lived experience would require a new developer successfully following this on a primed machine; the amended plan scores 9/10 on specification completeness, with that observation still unmeasured.

The documented primed sequence is three steps: (1) open the complete example and verify .NET 10/project prerequisites, budget 1 minute; (2) run `dotnet test Durable/ForgeTrust.AppSurface.Durable.Tests/ForgeTrust.AppSurface.Durable.Tests.csproj --filter TypedWorkDefinitionProof`, budget 3 minutes; (3) inspect the equality/passivity success message and follow the real-processing link, budget 1 minute. During implementation the named test must call the same compiled proof source or equivalent managed fixture; the command is proposed until that test exists. Restore/network waits and cold SDK/Docker setup are reported separately, never removed from end-to-end measurements by implication.

### 2. API design

The approved names form a readable progression: define, bind an executor, optionally reconcile, register, then create requests. The required retry default remains explicit because it is a contract decision; `CreateRequest` keeps optional override and due time in the exact approved order. Ordinary `ReconcileBeforeRetry` binding is intentionally intermediate, so the binding table and XML must tell developers that registration rejects it until `ReconciledBy` completes the contract.

Initial score 9/10, amended 9/10: compile-positive/negative package examples resolve generic inference concerns, but usability has not been observed. Do not add a `With...` builder, second inference facade or implicit Work-selection rule. A 10 would be observed first-use success across all three bindings without consulting implementation internals.

### 3. Error messages and debugging

Initial score 7/10, amended 9/10. The approved design preserves duplicate messages and separates provider outcomes from argument errors; the missing piece was a precise correction path. A 10 would mean users reliably recover from these errors in a live walkthrough, which this planning session cannot establish.

| Error path | Existing / proposed failure | Required correction guidance |
| --- | --- | --- |
| Legacy + new binding for one Work identity | Existing `InvalidOperationException`: Work is registered more than once | Preserve text; migration troubleshooting says replace the prior registration, or use a distinct immutable version for changed semantics |
| Definition codec emits mismatched metadata | New fixed `InvalidOperationException`; design only promised a bounded explanation | Explain that encoded metadata disagrees with the definition and that the codec must preserve its captured contract; never include arbitrary values or relabel bytes |
| Incomplete reconciler-required binding | Registration-time argument error before DI mutation | State that `ReconcileBeforeRetry` requires `ReconciledBy<TReconciler>()`; link the binding example from XML remarks/migration table |

Use parameter names for construction errors, new fixed corrective sentences for guard failures, and nearby documentation links for all three paths. Keep exact existing duplicate diagnostics unchanged. A custom codec can throw arbitrary messages; the library must not add payload context or promise redaction of caller-authored exception content.

### 4. Documentation and learning

Initial score 8/10, amended 9/10. The package already uses managed `appsurface:snippet` blocks and the [Markdown snippet tool](../../tools/ForgeTrust.AppSurface.MarkdownSnippets/Program.cs); reuse it so the README and migration examples render from compiled source. Add reference, decision and pitfall content to the package README, Durable overview, Work protocol, migration guide, diagnostics and XML, with links from the package chooser/root Durable entry.

Keep advanced static reuse, source/view distinctions, exact registry lookup and factory-based Flow composition after the ordinary example. Searchable headings should use the public API names and actual failure concepts, not invented jargon. A 10 requires successful search/findability and copy/paste verification against the released package; implementation must run snippet verification and the packed proof.

### 5. Upgrade and migration

Initial score 8/10, amended 9/10 with the CEO rollout matrix. Migration must show ordinary, reconciled and exit-aware before/after forms, explicitly replacing old registration while retaining scope/key/command/due/retry choices. Distinguish syntax-only migration from new codec/executor semantics and preserve the final exit-capability rollout contract.

The `typed-work-definitions-v1.md` suffix identifies guidance, not a SQL migration or release number. Legacy APIs stay available and not obsolete; a codemod is intentionally absent because it cannot infer application policy. A 10 requires a representative migration exercised against the package and accepted Work, addressed by the implementation proofs rather than a score claim today.

### 6. Environment and tooling

Initial score 8/10, amended 9/10. The existing projects target `net10.0`, use xUnit and package API-baseline tests, and the packed proof isolates a local feed and temporary consumers. Require no new SDK, public test package, global tool or runtime dependency for #800; use XML and generic constraints for editor feedback.

The passive example runs without PostgreSQL; database proofs require the existing Docker fixture or explicitly configured external test database. Keep the shell harness's macOS/Linux expectations visible and document equivalent `dotnet` commands for consumers using Windows; do not claim Windows shell-harness validation without evidence. A 10 requires cross-environment verified runs, so portability claims must follow actual CI evidence.

### 7. Community and ecosystem

Initial score 8/10, amended 8/10. Reuse repository source, issue reporting, examples and coordinated release notes, and link the relevant package rather than starting a new support channel. Nothing in this API requires a new plugin ecosystem or community site.

No response-time, adoption, pricing or support-level promise was inferred from repository availability. A 10 would require real contributor/adopter feedback and maintained support experience over time; that evidence belongs in the TODO trigger, not manufactured review points. The same branch still must ship the examples and migration links that make useful feedback possible.

### 8. Measurement and feedback

Initial score 6/10, amended 8/10. Record exact duplicated contract facts removed, application decisions retained, proof commit/package version, outcome (request equality versus accepted versus terminal), and any measured elapsed time with prerequisites. The measurement is local proof output and documentation; no usage collection is introduced.

The parent issue refers to adoption-evidence files that are not present at this pinned baseline. Implementation must resolve the pinned adopter evidence from the parent case when available and label any fallback representative fixture as such; it must not claim to have rerun missing measurements. A 10 requires independent adopter evidence, captured in the existing TODO rather than becoming an unverifiable #800 claim.

### DX scorecard and implementation checklist

| Dimension | Initial plan | Amended plan | Remaining evidence |
| --- | --- | --- | --- |
| Getting started | 7 | 9 | Primed walkthrough timing |
| API/SDK | 9 | 9 | Observed use of all binding forms |
| Errors | 7 | 9 | Recovery from real invalid examples |
| Documentation | 8 | 9 | Package build and snippet/findability checks |
| Upgrade | 8 | 9 | Representative package migration |
| Environment | 8 | 9 | CI and platform runs |
| Community | 8 | 8 | Maintainer/adopter feedback |
| Measurement | 6 | 8 | Independent adoption data |
| Mean | 7.6/10 | 8.8/10 | Scores describe the plan, not released-product usability |

TTHW current: unmeasured. Target: <5 minutes for primed passive contract feedback; actual durable completion and cold setup reported separately. Competitive rank is unknown. The library's meaningful first result is designed through compiled source and an explicit proof command; no UI or Claude-skill checklist applies.

- [ ] Complete ordinary example leads the typed Work docs; specialized forms follow.
- [ ] Same-source managed snippets compile from the packed package and match rendered docs.
- [ ] Proposed named passive test exists, prints a truthful result and stays free of runtime activation.
- [ ] New errors explain problem/cause/fix; preserved errors link to corrective examples.
- [ ] Migration replaces rather than duplicates registrations and includes all three forms.
- [ ] XML documents defaults, boundaries, thread safety and escape hatches.
- [ ] CLI/compiler negative fixture errors are tied to exact deliberate source lines.
- [ ] Package/CI/snippet checks pass with no new warnings; actual timing is labelled by environment.
- [ ] README links actual acceptance/terminal proof and existing support/release routes.

**DX not in scope / reuse:** reuse the CEO exclusions and existing package/proof/snippet tools. No new playground, SDK language, codemod, hosted support surface, global tool or telemetry is needed. The adoption-evidence TODO is the only new follow-up; unmeasured product scores do not justify speculative product expansion.

### DX outside review and completion

The fresh `combo/sub` DX reviewer reported five documentation gaps: a primary typed entry point, complete examples, binding failure boundaries, construction-error remediation, and migration/escape-hatch guidance. These are requirements for the upcoming implementation, not bugs in nonexistent definition code. A11–A13 and the sections above cover all five; there is no new taste decision.

| Dimension | Primary assessment after amendments | Independent DX voice | Named dual-voice consensus |
| --- | --- | --- | --- |
| Getting started <5 minutes | Target only; full passive proof specified | Entry point/examples required | N/A |
| API naming | Approved names coherent; matrix explains intermediate binding | Boundary table required | N/A |
| Errors actionable | Fixed guard text + corrective docs | Construction-error table required | N/A |
| Docs complete/findable | Managed source snippets and entry links required | All three complete examples required | N/A |
| Upgrade safe | Replacement and rollout matrix required | Same guidance required | N/A |
| Environment clear | Passive proof separate from runtime; codec obligations explicit | Static reuse/concurrency guidance required | N/A |

DX mean 7.6 → 8.8/10 is a subjective planning score. Actual TTHW and runtime behavior remain unmeasured. All eight dimensions, nine journey stages, empathy narrative, first-use roleplay, competitor sources and implementation checklist are written. Five outside findings are covered; zero named dual-voice consensus claims, zero critical unresolved design gaps and no additional deferred scope.

## Engineering review — final amended plan

### Scope challenge and reuse check

This is a multi-file feature, not a thin fluent wrapper. The [approved design](../designs/issue-800-typed-work-definitions.md#codec-identity-and-registry-compatibility) says registry entries must use captured metadata, and the baseline `DurablePayloadCodecRegistry.Register` both appends to `_byType` and assigns `_byContract`. The baseline Flow adapter has codec reference checks at activity construction, registry construction and wait evaluation; those are real compatibility seams that a definition-only patch would break. The existing-code map in the CEO section remains the engineering reuse map.

The minimum complete change is snapshot/capture + definition/request + three bindings + common registration/DI + bounded Flow compatibility + public/package/DB proofs and docs. Splitting these into sequential implementation checkpoints reduces review difficulty; shipping only the first wrapper is not the approved contract. The selected internal entry model can use normal dictionaries, the existing registry lock and standard DI descriptors; no reflection, interning cache or new dependency is justified.

[Microsoft's DI guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview) supplies the ordinary service registration/lifetime model. The domain-specific source/snapshot conflict rules are not a new DI container. [Compiler `ErrorLog`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/errors-warnings#errorlog) emits SARIF, so the negative fixture harness can use structured diagnostics without adding a compiler library. The applicable prior learning is `durable-codec-sharing-flow-reference-boundary`: codec views must preserve source provenance, and Flow must execute the guarded codec it validated.

### 1. Architecture and concrete findings

```text
STATIC / CALLER-OWNED              SINGLETON CONTRIBUTIONS         PER PROVIDER
source codecs -> closed snapshots -> definition + binding           |
                         |          -> Work registration values -----+-> catalog
                         +---------> codec contributions ------------+    |
                                                                            +-> default Work registry
public raw codec descriptors --> resolve AFTER catalog validation ----------+-> codec aggregator
                                                                                |
                                                                      canonical entry + view
                                                                      /                   \
                                                               contract index           type index

Flow registry -> chosen public Work registry + chosen public codec registry
evaluation -> exact context/event lookup -> provenance check -> local selected codec
activity -> exact global Work registration -> its guarded input/result codecs

CreateRequest -> guarded input Encode -> existing request/fingerprint -> existing provider
```

**E1 [P1], confidence 9/10 — atomic registry upgrade.** The approved design at line 195 permits a raw entry to become a canonical frozen entry; baseline `DurablePayloadCodecRegistry.Register` at lines 400–401 performs `codecs.Add(codec);` and `_byContract[key] = codec;`. Reusing that insert logic for an upgrade would leave a duplicate or stale type entry. Use one internal entry per source/contract and replace its codec projection consistently in both indexes under the existing gate; validate the candidate before mutation and keep failure atomic. U11 verifies raw → frozen → raw, type and exact lookup, conflicts and concurrency.

Do not expose internal snapshot types through the existing protected public-registration constructor. Preserve external subclass compatibility and all old method signatures; package-owned paths can use internal/private-protected construction helpers. Default codec construction resolves the private catalog, never a custom public Work registry, so package code does not add a custom-registry dependency cycle. The approved catalog-first order still applies before descriptor factories/getters run.

The four production failure families remain explicit: invalid authoring inputs fail before mutation; codec disagreement fails before unsafe decode or after encode without relabelling; registry/Flow incompatibility fails closed; provider failures retain existing Work outcomes. There is no new authentication, database authority, cache owner or network path. All canonical registry views are per provider, while definitions/bindings and registration contribution values may be shared immutable instances.

### 2. Code quality and organization

Use small internal helpers for capture/validation, provenance comparison, codec entry aggregation and default registry installation. Keep typed definition/binding public classes sealed and get-only; expose only the five approved new public types and their required members. Store typed invocation codecs separately from preserved legacy raw public properties so the new guards do not accidentally rewrite legacy reference contracts.

**E2 [P2], confidence 8/10 — one executable example body.** The DX section requires a named test and a packed example to prove the same syntax. Link the packed `TypedWorkDefinitionProof.cs` into the test project and invoke its `Run` method from a focused test class, following the existing shared-source pattern; use managed snippets from that same file. Do not create two independently maintained copies merely to make the commands work.

Inline ASCII comments should explain the catalog-before-codec dependency, provenance upgrade and Flow codec-selection boundaries, where an implementer could otherwise reintroduce a cycle or unchecked raw call. No existing inline diagram in the inspected C# seams describes the new design; update relevant Work-protocol and README diagrams when touching them, without changing persisted state-machine meanings. The test monolith remains a regression baseline; new definition/view tests get focused files. No reflection-based test access, broad warning suppression, new serializer or generic plugin framework is warranted.

### 3. Test review and evidence

The separate [test plan](issue-800-typed-work-definitions-test-plan.md) contains the full codepath diagram and 20 verification groups: U01–U15, I01–I02, P01–P02 and D01. Existing xUnit contract tests already assert raw codec identity, type-only version ambiguity, passive registration, fingerprints, Flow reference rules and deterministic evaluation; the new definition and frozen-view branches are planned gaps until implemented. Each group specifies happy/error/boundary cases and an owner file; none is marked tested merely because a requirement was written.

**E3 [P1], confidence 9/10 — accepted-state readback.** The approved design at line 264 requires that “a different definition/default cannot alter the accepted policy/payload/fingerprint.” Existing `PostgreSqlDurableWorkStoreTests.TransactionWriter_UsesCallerTransactionWithoutOwningIt_AndDeduplicatesExactRequest` demonstrates acceptance/deduplication and transaction ownership, but acceptance results do not return all persisted request facts. I01 therefore reads the stored Work row after initial acceptance, equivalent direct replay, changed-default conflict, and an explicit override restoring the original policy; compare bytes, metadata, safety, every retry/lease field, due time and fingerprint against the original request. A changed default is **Conflict** when it changes the accepted fingerprint; an explicit override yielding equivalent facts is **Duplicate**. Do not modify the public acceptance result just to make a test convenient.

**E4 [P2], confidence 9/10 — exact negative-build evidence.** The approved design line 270 already allows a justified fixture-specific diagnostic allowlist. Use SARIF 2.1 with exact file/line markers, a clean output path for every fixture, successful restore and a matching positive control; record the observed compiler SDK. Keep CS0311/CS0452/CS1061 as designed initial expectations. If final method lookup legitimately emits another code, first isolate the source shape, then document an exact per-fixture allowance from observed output; do not pre-approve CS1929 or a whole family based on speculation. The harness must also detect infrastructure/MSBuild failures outside compiler SARIF and cannot accept an intended error plus unrelated build failure.

Tests U09–U14 protect the hardest cross-component paths: Work duplicate precedence even when codec resolution starts first; custom DI descriptor identity/selection; atomic manual registry upgrades; exact Work registration identity; context/event use of the selected compatible codec without registration mutation; and shared codec versions in both orders. Runtime I02 extends the existing bounded fixture through terminal success. No LLM/prompt eval, browser QA or new background job applies, and unit tests alone do not substitute for the real database proof.

### 4. Performance

**E5 [P2], confidence 8/10 — indexed aggregation.** The design line 189 requires indexing by original source reference and retaining conflicting captures. Implement source groups with reference-identity dictionaries and contract groups with ordinal name/version keys, with a single captured entry reused by validation, view creation and both indexes. Avoid repeated scans of contribution lists or extra source getter reads; sort only conflict output/registered identity presentation when needed. Expected construction is O(n) aggregation plus any O(k log k) presentation sort, with O(n) configuration memory.

The request hot path remains one source encode plus constant metadata comparisons and the existing payload copy/hash/fingerprint. Definition lookups do not touch DI, and there is no new N+1 query, database connection, polling loop or external request. Prefer count-based construction tests at 1, 10 and 100 distinct contracts plus shared-source cases; use wall-clock/allocation benchmarks only if implementation evidence reveals a real regression. Arbitrary custom codecs remain the dominant variable and must not run under a new global lock.

### Work decomposition and verification order

| Step | Module/directory ownership | Depends on | Deliverable / exit condition |
| --- | --- | --- | --- |
| E1 | Durable contract package + contract tests | Pinned baseline | Snapshot/guards/definition/request; U01–U05 and unchanged fingerprint tests |
| E2 | Same package/tests | E1 | Three bindings/common builder/legacy API; U06–U08/U15 |
| E3 | Same package/tests | E2 | Catalog/aggregator/manual atomic upgrade/Flow selection; U09–U14 |
| E4 | Packed consumers + package verification | E3 | Positive, negative, shared-source docs example; P01/P02 |
| E5 | PostgreSQL tests | E3 | Persisted-field parity/conflict/rollback + terminal success; I01/I02 |
| E6 | Package docs/API/release + snippet tooling invocation | E4/E5 | D01; linked migration and generated snippets; no warnings |

E1 → E2 → E3 is one sequential lane: all share the contract module and authority. After E3, packed-proof and PostgreSQL-test lanes can run independently; documentation can be drafted alongside them, but final snippets/API/release verification waits for both. The package test project that links the proof source is owned by the packed-proof lane after core tests stabilize. No overlapping parallel edits to the core registry/Flow files are planned. Overall estimates are the CEO range; per-task estimates in the aggregate overlap and must not be summed as a calendar promise.

Begin implementation on a `codex/` branch containing `48c6db61`; do not treat the current behind-main checkout as the implementation baseline. Finish formatting, affected test/API/analyzer checks, packed proof and managed snippets; run solution coverage when practical and record any actual environment limitation. The release-note fragment links [#800](https://github.com/forge-trust/AppSurface/issues/800) and uses the existing [coordinated release process](../../releases/README.md), with no new artifact or publishing channel.

### Engineering outside review and resolution

The fresh native reviewer raised four concerns: dual-index upgrade, stored-field readback, quadratic aggregation and compiler diagnostic brittleness. E1/E3/E5 make the first three explicit. E4 preserves the design's already-required exact diagnostic escape hatch, while suppressing an unobserved alternative diagnostic as a speculative implementation outcome. The reviewer suggested that a changed default should yield Duplicate; that conflicts with the existing fingerprint contract and was corrected to conflict unless an explicit override restores the original facts.

Its final recheck confirmed these four concerns were covered and requested that P02 say the diagnostic codes are initial expectations, with any exact allowance justified by observed SARIF. That wording is now aligned with E4. The engineering review covers the final CEO and DX amendments plus the completed test plan; implementation evidence remains outstanding by design.

| Dimension | Primary after amendments | Independent reviewer | Named dual-voice consensus |
| --- | --- | --- | --- |
| Architecture | Atomic canonical entry/index update specified | Requested dual-index replacement | N/A |
| Test coverage | Persisted-field readback + 20 groups specified | Requested direct stored-field proof | N/A |
| Performance | Indexed capture; count-based verification | Requested dictionaries/no repeated scans | N/A |
| Security | Source provenance, bounded errors, existing authority | No additional gap | N/A |
| Errors | Exact observed compiler diagnostics with positive controls | Warned about lookup-dependent code | N/A |
| Deployment | Existing package/protocol and capability rollout | No additional blocker | N/A |

**Suppressed/rejected findings:** compiler CS1929 is not demonstrated for the final API, so it is a contingency rather than a new allowed error; confidence 4/10 until observed. A changed-default request automatically deduplicating is rejected because the request fingerprint includes retry policy. Broad removal of Flow compatibility would violate the approved design and is not proposed. No current implementation bug is claimed for code that has not yet been written.

### Engineering completion summary

| Required output | Result |
| --- | --- |
| Scope challenge | Full approved scope retained; actual reference/DI seams inspected |
| Architecture | 1 concrete atomic-upgrade clarification; diagram written |
| Code quality | 1 shared-source proof clarification; focused helper/test organization |
| Tests | Complete diagram and 20 groups; readback and diagnostic clarifications; all future checks labelled planned |
| Performance | 1 indexed-aggregation clarification; no claimed benchmark |
| NOT in scope / reuse | CEO exclusions and existing-code map remain current |
| Failure modes | CEO registry + U01–D01 covers visible failure and required proof; 0 unaddressed critical design gaps |
| TODOs | One adoption-evidence follow-up; no new dependency blocking this plan |
| Outside voice | Native combo/sub; named Codex CLI/Claude consensus unavailable by transport choice |
| Parallelization | Core sequential; 2 verification lanes after core stabilization; final docs integration |
| Completeness | All 5 engineering recommendations included; no implementation shortcut approved |

## Cross-phase themes

**Prove outcomes, not proxies:** CEO requested terminal completion; DX separates passive proof from actual execution; engineering requires stored-field readback. Together these prevent a passing compilation or Duplicate response from being advertised as stronger evidence than it is.

**Preserve identity across migration:** CEO calls for replacement and mixed-version rollout; DX requires corrective examples; engineering protects both registry indexes and exact Flow Work-registration identity. The same source/provenance rules must connect all three rather than becoming separate ad hoc fixes.

**Bound the machinery:** all phases preserve the existing protocol, package and execution boundary. Independent routed reviewers provide fresh-context support for these themes; their backing model families are unknown, so this is cross-phase agreement, not verified cross-model consensus.

## Implementation tasks and final choice

The [task list](issue-800-typed-work-definitions-tasks.md) aggregates 11 distinct records from the latest CEO, DX and engineering phase artifacts for this branch/commit window. It retains possible overlaps explicitly: C2/D2 are one migration/documentation effort, and C3 supplies scenarios for G3/G4. Implement the dependency-ordered work decomposition above; aggregation priority order is not build order.

**T1 — required correctness proof versus a required external timing study.** Approved recommendation (2026-09-10): ship #800 with direct/factory equivalence, real PostgreSQL terminal completion, migration examples and preserved accepted-state evidence; keep real-user and competitor timing as follow-up evidence. The alternative was to wait for an external adopter study before accepting the API; it offers stronger usability evidence but depends on participant availability and work owned by the broader adoption rail. Neither choice authorizes claiming a speed improvement without measurements. This was the only taste choice; no settled public API or shared-snapshot decision was challenged.

## Pre-gate verification

- CEO: named premises, reuse map, alternatives, dream delta, temporal decisions, all ten applicable sections, error/rescue table, failure registry, rollout diagram and completion summary are written. Independent strategy review and a fresh specification check completed.
- Visual design: skipped because there is no UI surface. Codec views and binding forms are not screens/forms.
- DX: all eight dimensions, nine-stage journey, persona/narrative, first-use roleplay, primary competitor sources, explicit TTHW target, checklist, outside review and completion summary are written.
- Engineering: actual baseline code inspected, scope challenge, dependency diagram, complete test artifact, failure cases, ownership sequence, outside review/recheck and completion summary are written.
- Decision trail: 20 decisions, 19 mechanical and one taste recommendation; zero user challenges. One adoption-evidence follow-up is recorded in TODOS.
- Artifact checks: all relative repository links resolve against the working tree or pinned baseline; code fences balance; whitespace checks pass. The approved design is unchanged. No implementation code, tests, package publication or issue mutation was performed.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
| --- | --- | --- | --- | --- | --- |
| CEO | autoplan selective expansion | Strategy/scope | 1 + specification check | Approved | 5 outside concerns addressed or resolved by approved T1; final spec score 9.3/10, subjective |
| DX | autoplan DX POLISH | Developer adoption | 1 | Approved | 5 outside requirements covered; plan score 7.6 → 8.8/10; TTHW unmeasured |
| Engineering | autoplan FULL_REVIEW | Architecture and verification | 1 + final recheck | Approved | 4 outside concerns covered; 5 primary clarifications; 20 planned verification groups |
| Visual design | UI applicability check | Screens/interactions | 0 | Not applicable | No UI scope |
| Outside voice transport | Native combo/sub | Fresh independent context | Per applicable phase | Subagent-only | No separate Codex CLI/Claude pass or verified cross-model consensus |

**VERDICT:** APPROVED by the user on 2026-09-10, accepting all review recommendations including T1. No unresolved plan decisions or critical design findings remain. Implementation and its tests have not run; this approval records the reviewed implementation plan.

**NO UNRESOLVED DECISIONS.**
