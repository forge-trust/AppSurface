<!-- /autoplan restore point: "/Users/andrew/.gstack/projects/forge-trust-AppSurface/codex-issue-802-autoplan-autoplan-restore-20260924.md" -->
# Implementation plan: Durable Testing (#802)

Source: [confirmed issue #802](https://github.com/forge-trust/AppSurface/issues/802). Review target: commit `9d185f43` on `main`.

## Implementation plan

### Goal and boundary

Publish `ForgeTrust.AppSurface.Durable.Testing` for .NET 10 so adopters can write deterministic tests for Durable health, pump admission, and typed Work contracts. The package uses the production contracts and contains no PostgreSQL, ASP.NET Core, Testcontainers, assertion-library, or test-framework dependency. It does not emulate persistence, authorization, or provider policy.

### Work sequence

1. **Package and public contract.** Add the Testing project and provider-free test project to the solution and package index. Follow adjacent Durable package metadata, XML documentation, analyzers, API snapshot, and packing conventions. Reference Durable and Durable.Provider. Define the public API before examples and tests.
2. **Valid-by-default builders.** Implement `DurableHealthSnapshotBuilder` with Healthy, NotStarted, Stale, Draining, Incompatible, and Unavailable starting states, named field overrides, checked `Build()`, and explicit `BuildContradictoryForTest()`. Reuse the production snapshot constructor. Add pump request/result builders using production constructors and defaults. Add a typed Work request builder that requires a definition and payload and calls `definition.CreateRequest(...)`. Add the specified native envelope builder through `DurableWorkerEnvelope<T>.CreateNative(...)`, preserving fence identity.
3. **Recording fakes and observations.** Implement `FakeDurableRuntimeHealth`, `RecordingDurableRuntimePump`, and `FakeDurableRuntimeDrainControl` against their respective public Provider interfaces. Capture immutable histories with exact requests, selected surfaces, limits, ordering, overlapping calls, cancellation state, and drain transitions. Support all four production pump attempt kinds plus delegate behavior. Default to a completed empty pass. Propagate caller cancellation and delegate exceptions.
4. **Two-phase host scenario.** Build `DurableHostScenario` from explicit health, pump admission, request, and `TimeProvider` inputs. `AssessHealthAsync` returns a completed health observation. `RunDirectPumpOnceAsync` requires the latest assessed snapshot's production `CanAttemptPump`, invokes admission once, and returns the exact attempt. Use one deadline for each observation and one overall deadline; return completed negative outcomes immediately. Preserve refusal and unavailability as attempt outcomes. No polling, retries, HTTP mapping, or independent readiness policy.
5. **Contract observations.** Add immutable, assertion-free observations for the definition's public identity, codec metadata, safety and retry defaults, exact registry presence, and binding identity. Preserve production missing-registration and codec-failure behavior.
6. **Verification.** Add provider-free unit tests for all named health states and four computed predicates, contradictory states, validation boundaries, request/fingerprint parity, four attempt kinds, call order/concurrency/cancellation, drain transitions, and both deadlines. Extend PostgreSQL tests using shared observation records for real claim, completion, stale-worker recovery, and ambiguous post-permit outcome. Add a consumer compiled against the packed Testing artifact with xUnit assertions and inspect its resolved dependency graph.
7. **Adoption and release docs.** Update the package README, package chooser, Durable start page, API snapshot, release guidance, and examples. Explain defaults, limits, how to assert observations, and when a real-provider test is required. Run formatting, affected tests, package validation, API checks, and practical solution coverage.

### Acceptance evidence

- Each named health state yields the four predicates in [operational assessments](../../Durable/operational-assessments.md); impossible field combinations require the explicit contradictory path.
- Production validation rejects invalid IDs, epochs, surfaces, bounds, payloads, and codec metadata. Typed requests match direct `CreateRequest` fields and fingerprints.
- Fake histories are immutable and prove exact ordering, overlap, cancellation, drain transitions, and attempt kind.
- Scenario tests distinguish NotStarted/Stale admission, Draining/Incompatible refusal, completed empty pass, provider refusal, pre/post-invocation cancellation, observation timeout, and overall timeout.
- Definition, registry, codec, and binding tests cover success, absent and duplicate registration, codec rejection, and exact definition identity.
- PostgreSQL integration proves real provider behaviors; packed consumer compiles and asserts observations; the Testing package brings in no forbidden transitive dependencies.
- Docs, public API snapshot, formatting, package validation, affected tests, and coverage gate pass.

### Exclusions and rollback

Do not add fake Work/Flow/Schedule persistence, an assertion DSL, HTTP adapter, host authorization policy, retry loop, or a second activation policy. Revert the additive package and documentation changes to roll back; no schema migration or persistent state is introduced.


<!-- autoplan-accepted:ceo -->
- Specify observation and overall deadline behavior, distinguish caller cancellation from timeout, and preserve status-unknown evidence after admission starts; prove both with fake-clock and real post-permit tests.
- Serialize completed health-assessment publication and capture the exact assessment used by each pump call; test both completion orders and provider refusal after a positive assessment.
- Add one packed external-host adopter example asserting the exact four-kind admission outcome and package graph.
- Document call-history retention and privacy boundaries without serializing durable payloads.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:dx -->
- Define and document scenario behavior for missing assessment, repeated pump calls after one assessment, concurrently completed assessments, and stale assessment versus authoritative provider admission; test all cases.
- Name observation and overall timeout outcomes and caller-cancellation exceptions in the public contract; retain invocation-started/status-unknown evidence and link to recovery guidance.
- Publish a six-state default-field and valid-override matrix, explicit contradictory-fixture errors, and a complete packed xUnit quick start with install/run commands; measure its TTHW against a five-minute target.
- Specify an atomic immutable call-history snapshot API and a retention/clear mechanism; document default behavior, payload-reference privacy, and customization limits.
- Add API-specific problem/cause/fix/error-link guidance and coordinated preview-package upgrade instructions.
<!-- /autoplan-accepted:dx -->
## Review record

### CEO review (Phase 1, selective expansion)

Input: commit `9d185f43`; issue #802 and its archived spec. The branch starts from main with only this plan added. No #802 implementation or open PR exists. The current #800 design document concerns typed definitions, so #802 itself is the feature-specific design source. `TODOS.md` already defers transport-neutral package extraction until measured adopter need; this plan should not quietly start that extraction.

**0A, premises.** The problem is real: both Provider and PostgreSQL tests repeat health and pump object construction, and a package can make their meanings consistent. The 88/13 construction counts measure repetition but do not prove time saved; the packed consumer must show a concrete host test that is shorter and less error-prone than local fakes. The assumption that all builders, fakes, scenarios, and observations belong in one public package remains a taste decision, since each adds compatibility cost. Doing nothing leaves repeated setup and lets future external activators interpret refusal as an empty pass.

**0B, what already exists.** `DurableRuntimeHealthSnapshot` already computes `WasStoreObserved`, `CanEnableActivation`, `CanAttemptPump`, and `IsReady`; `DurableRuntimePumpAttempt` already validates its four outcomes. `DurableWorkDefinition.CreateRequest` owns typed identity and fingerprint, `DurableWorkerEnvelope.CreateNative` owns native execution identity, and `Durable/verify-packed-consumers.sh` already exercises package artifacts. The plan should wrap these surfaces, not duplicate their validation or make a second policy. Production Durable already references Workers, so an envelope helper can use that transitive project while the Testing project still directly references only Durable and Provider.

**0C, dream state and alternatives.**

```text
Current: repeated local fixtures + ambiguous interpretation
   -> #802: stable production-backed builders, fakes, observations
   -> 12-month ideal: external hosts use one small, versioned test vocabulary;
      provider truth and real PostgreSQL proof remain authoritative.
```

| Approach | Effort (human / agent) | Risk | Strength | Cost |
| --- | --- | --- | --- | --- |
| A: local snippets only | ~1 day / ~1 hour | Low publication risk | Fewest files; proves one adopter journey | Does not remove shared semantic drift; incomplete #802 |
| B: complete #802 package (selected) | ~3 days / ~4–8 hours | Medium API maintenance | Meets approved spec and supports external host reuse | Broad public API requires strong consumer proof |
| C: new cross-package testing platform | >1 week / >1 day | High coupling | Possible later ecosystem reuse | New architecture without adopter evidence |

**0F/0D, scope.** Selected selective expansion and Approach B under the /autoplan completeness principle. Keep the approved package scope. Do not add HTTP adapters, persistence fakes, assertion DSLs, or a new transport-neutral package; these are outside the direct blast radius. Treat the scenario health gate as an unresolved user challenge because the user-confirmed spec states it and the production guide warns against a check-then-act gate. A richer prebuilt host example is a small in-scope improvement to consumer proof. The candidate of widening Testing into a general workflow-testing framework is deferred until an adopter demonstrates need.

**0E, implementation time.** At hour 1 (human), freeze the package graph and state/field matrix; at hours 2–3, define fake-history synchronization and timeout semantics; at hours 4–5, prove the scenario's provider-admission race and packed graph; at hour 6+, compare real PostgreSQL outcomes with observation records, format, and document caveats. The agent effort estimate is ~4–8 hours total, excluding CI/database availability.

**Independent CEO voice.** A `combo/sub` native reviewer read the exact CEO input hash and raised seven concerns: public surface size, adopter evidence, the advisory health gate, timeout meaning, builder state matrix, PostgreSQL proof ownership, and comparison with test-local helpers. The external Codex process is unavailable from a Codex host, so CEO consensus is N/A rather than confirmed. Primary review agrees on the health-gate and deadline concerns; it keeps the confirmed package scope while requiring a concrete packed adopter journey. The native voice's narrower first release remains a taste alternative at the final gate.

#### Section 1: architecture

```text
Host test -> Testing builders/fakes/scenario -> Durable.Provider contracts
          -> Testing typed Work observations -> Durable definitions
          -> native-envelope helper -> Workers (through Durable)
Real-provider conformance -> PostgreSQL -> Durable.Provider + Durable
Packed consumer -> packed Testing + its published dependency graph
```

The dependency arrows are one-way; production packages do not reference Testing. Health assessment and pump admission are separate operations: an empty completed pass has a result; refusal, unavailability, and incompatibility do not. The scenario is stateful because it stores the latest assessment, so concurrent assess and pump calls require a documented ordering rule. At 10x/100x test concurrency, the first risks are mutable history races and unbounded call-history memory, not database load. No production endpoint, credential, or migration is added. Git revert of the additive package is the rollback; published consumers must still respect normal package versioning.

#### Section 2: error and rescue registry

| Codepath | Failure and exception | Action | Caller sees | Proof |
| --- | --- | --- | --- | --- |
| Health builder | invalid enum, ID, epoch, surface, or cross-field state: `ArgumentException` / `ArgumentOutOfRangeException` | Reject at build; never coerce | Exact invalid field | boundary and state matrix |
| Pump request/attempt builder | invalid bounds or result-kind/code pairing: production argument exceptions | Delegate to production constructor | Exact contract error | four kinds and negative cases |
| Typed request builder | codec rejection or invalid payload: production exception | Propagate unchanged | Codec's failure | direct `CreateRequest` parity |
| Health fake / scenario assess | caller cancellation: `OperationCanceledException`; delegate error: original exception | Propagate and retain recorded invocation | Cancellation/error, no synthetic snapshot | before/after invocation |
| Pump fake / scenario run | refusal/unavailable/incompatible: returned `DurableRuntimePumpAttempt` | Preserve kind/code/result nullability | Exact admission outcome | all four kinds |
| Pump fake / scenario run | application/finalization failure: original exception | Propagate, mark invocation started | Potentially ambiguous outcome | delegate and PostgreSQL post-permit |
| Scenario deadline | observation or overall timeout: `TimeoutException` or named cancellation | Distinguish caller cancellation; state that work may continue | Timeout with execution-status unknown | fake-clock boundary tests |
| Packed consumer | restore/compile/package-graph failure | Fail harness with diagnostic | Build failure | packed artifact test |

No generic catch/swallow path is acceptable. The plan needs exact timing semantics before implementation: a timeout cannot claim that a started provider pass did not execute. This is accepted as a clarification of the existing cancellation and ambiguity criteria.

#### Section 3: security and threat model

No new endpoint or production data access is proposed. The public test package can nevertheless capture requests and payload-bearing objects in memory: immutable call histories must avoid logging or serializing payloads and must document test-process retention. Input guards remain production constructors; synthetic contradictory snapshots are deliberately explicit so production-facing examples do not normalize unsafe state. Severity is medium for accidental sensitive test data in histories and low for package dependency sprawl; tests and README must cover both.

#### Section 4: data flow and interaction edges

```text
builder input -> named state + overrides -> cross-field check -> production ctor -> snapshot
 null/invalid -> argument error   empty -> production validation   ctor error -> propagate
health GetAsync -> completed observation -> latest assessment -> admission TryRunOnceAsync
 missing assessment -> reject     empty pass -> Completed(result)   provider refusal -> attempt
```

There is no UI interaction. Overlapping `AssessHealthAsync` calls can complete out of order; define “latest” by completed assessment sequence under one synchronization rule, not call start time. A pump request must capture the assessment it uses, then invoke the provider once. If a newer assessment arrives during admission, the provider's own check decides; tests need both completion orders. A timeout after admission starts leaves status unknown even if local waiting ended.

#### Section 5: code quality

Production constructors and `CreateRequest` are the reuse points. Keep builders thin; a second state-policy engine would drift from the four computed predicates. The fake histories need immutable snapshots rather than exposing a live `List<T>`; a synchronized append/copy is clearer than a generic event bus. Name “observation timeout” separately from “provider time budget” in public docs and API names.

#### Section 6: test diagram

```text
Named health states/contradictions -> provider-free matrix test -> four predicates
Pump request + four outcomes      -> constructor/fake tests      -> exact kind/result/code
Fake overlap and drain            -> gated concurrent tests      -> order + immutable copy
Assessment then admission         -> scenario tests             -> one exact request/call
Both timeout levels               -> FakeTimeProvider tests     -> deadline and unknown status
Typed definition/registry/binding -> contract tests             -> identity/fingerprint/errors
Real claim/recovery/permit        -> PostgreSQL tests           -> authoritative facts
Packed package                   -> consumer restore/build     -> graph and assertions
```

The 2am ship test is a packed external consumer plus a real PostgreSQL ambiguous-outcome test. Hostile tests race assessment completion, cancellation, and provider refusal; no elapsed wall-clock sleeps should decide outcomes. Use .NET's `TimeProvider` and test-only `FakeTimeProvider` for deterministic deadlines, keeping the fake-clock package out of the published Testing dependency graph. [Microsoft's TimeProvider guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/timeprovider-testing) supports that test seam.

#### Section 7: performance

There are no new production queries or connections. The slowest paths are PostgreSQL integration, packed restore, and solution coverage, all validation-only. The fake history grows with calls, so document test-local lifetime and avoid static/global retention; add a bounded stress test for concurrent append/copy correctness rather than speculative production benchmarking.

#### Section 8: observability and debugging

For a test package, immutable observations and call histories are the diagnostic surface. Expose exact attempt kind, problem code, request, elapsed time, and whether invocation started, without logging payloads or inventing application telemetry. No new production dashboard or alert is justified; the canonical [operational assessment guide](../../Durable/operational-assessments.md) already covers runtime alerts. A test failure three weeks later should report a specific state transition and request rather than an opaque timeout.

#### Section 9: deployment and rollout

The release is additive and requires no schema migration. Pack the library, compile the consumer against that artifact, inspect its dependency graph, then publish alongside matching Durable and Provider versions. If the package is withdrawn, remove its package-index/docs entries and revert the release; existing released binaries still require normal support/versioning. Test rollout risk is mismatched package versions or a source-project reference accidentally masking an invalid packed graph.

#### Section 10: long-term trajectory

Reversibility is 4/5 before publication and lower after third-party adoption. The main debt is public API breadth, especially scenario policy and deadline semantics. Document when to use local fakes and when to require a real provider, then revisit API additions after external host #804 supplies usage evidence. The package supports the one-year path only if it remains provider-neutral and does not become a simulated persistence layer.

Section 11 skipped: no screens, components, frontend state, or visual interaction is in the plan. Existing diagrams in the touched production files are unaffected because this review proposes no production-code edits.

### CEO required registries and scope

**NOT in scope:** fake storage (would counterfeit provider guarantees); HTTP/authorization adapters (application-owned); assertion DSL and framework packages (consumer decides); transport-neutral contract extraction (deferred pending measured adopter need); dashboard/telemetry (no production runtime added).

**Dream-state delta:** the package moves repeated contract setup into one versioned library but does not prove application business readiness or replace PostgreSQL crash proof. External host #804 should be the first named adopter of the packed observations.

| Failure mode | Rescued? | Test? | Caller impact | Visible? |
| --- | --- | --- | --- | --- |
| contradictory health snapshot accepted by ordinary builder | Must reject | matrix | false readiness test | exception |
| stale health precheck treated as authoritative admission | unresolved challenge | race | lost or blocked pass | provider result needed |
| completed empty pass recoded as refusal | Must prevent | four-kind matrix | wrong host status | observation kind |
| timeout interpreted as no execution | Must prevent | fake clock + real post-permit | unsafe retry | status unknown |
| overlapping fake calls lose order | Must prevent | gated concurrency | flaky assertions | call sequence |
| packed graph silently imports Npgsql/test framework | Must prevent | graph inspection | adopter coupling | harness failure |

The unresolved health-gate row is a critical gap until the final gate. All other rows have a planned test and explicit caller outcome.

**CEO completion summary:** selective expansion; 2 named premise risks; 2 architecture issues (health gate, assessment ordering); 8 error paths mapped; 1 security documentation gap; 3 async edge cases; 1 quality risk (second policy engine); 7 test layers; 1 performance risk (history growth); 1 debugging gap (timeout ambiguity); 1 rollout risk (packed graph); reversibility 4/5. CEO voice: native completed with 7 concerns; outside unavailable; consensus N/A. No #802 TODO was added; the existing transport-neutral extraction TODO remains deferred. One user challenge and one taste choice proceed to the final gate.

### CEO Implementation Tasks

- [ ] **C1 (P1, human: ~2h / agent: ~30min):** Define timeout and cancellation semantics, including invocation-started and status-unknown evidence. Verify with fake time and post-permit tests.
- [ ] **C2 (P1, human: ~2h / agent: ~30min):** Define latest-assessment ordering and both race schedules. Verify with gated concurrent scenario tests.
- [ ] **C3 (P2, human: ~1h / agent: ~15min):** Add a packed adopter journey that compares the package to local fixture code and asserts exact admission outcomes.
- [ ] **C4 (P2, human: ~1h / agent: ~15min):** Document test-process history retention and forbid payload serialization in diagnostics.

<!-- autoplan-accepted:ceo -->
- Specify observation and overall deadline behavior, distinguish caller cancellation from timeout, and preserve status-unknown evidence after admission starts; prove both with fake-clock and real post-permit tests.
- Serialize completed health-assessment publication and capture the exact assessment used by each pump call; test both completion orders and provider refusal after a positive assessment.
- Add one packed external-host adopter example asserting the exact four-kind admission outcome and package graph.
- Document call-history retention and privacy boundaries without serializing durable payloads.
<!-- /autoplan-accepted:ceo -->

### DX review (Phase 2.5, DX polish)

**Scope and persona.** This is a .NET library and package for backend or platform engineers testing external Durable activators. They know xUnit and the production Provider contracts, but should not need a live database to assert a host's response to health and admission. They expect one install step, a complete first test, API reference, deterministic time, and a separate real-provider proof path. The initial DX completeness is 5/10: package discovery is planned, but no concrete first-use sequence or exception table exists.

**Developer perspective.** “I find the Durable package chooser and see separate adopter, Provider, and PostgreSQL packages. The Provider README gives me a useful warning: health is advisory and admission is authoritative. I want to test the small activation wrapper in my app, so I reach for a Testing package. Today that package is absent and I have to assemble a twenty-argument health snapshot and remember which refusal outcomes have null results. A broad Testing API could solve that, but only if I can install it and copy one test that compiles against the packed artifact. I will start with a healthy assessment, then make the pump refuse the call, and assert that no empty pass is invented. If the sample instead requires a large scenario setup or hides the refusal behind a timeout, I will write my own fake. When I later test a real claim or a post-permit failure, I need the docs to tell me clearly that the fake cannot prove those storage facts and point me to PostgreSQL conformance. I also need errors to say whether my fixture is invalid, the caller canceled, or provider execution is unknown.”

**Competitive benchmark and target.** A general-purpose test-local fake has no new package install but repeats contract knowledge. A mocking library can stub `IDurableRuntimeHealth` and `IDurableRuntimePumpAdmission`, but the developer still must create valid production snapshots and distinguish four attempt kinds. The planned Testing package wins only if its first assertion is easier and contract-correct. No reliable comparable product-specific TTHW measurements were found; do not borrow Stripe/Vercel times as evidence. Current TTHW is unmeasured because this package does not yet exist. Target: a new xUnit test project reaches one meaningful health-plus-admission assertion within five minutes from package installation, measured in a packed consumer trial.

**Magical moment.** A copyable test makes a healthy snapshot, records one exact request, returns `Refused`, and shows `Result == null` while a completed empty pass produces a non-null result. Delivery vehicle: a three-step README path (install, copy one test, run `dotnet test`) compiled as the packed consumer. The first sample must keep health advisory until the final user challenge is resolved.

| Journey stage | Developer action and current evidence | Friction / planned repair |
| --- | --- | --- |
| 1 Discover | Opens `packages/README.md` or `Durable/README.md` | Add a Testing row and a start-here link |
| 2 Evaluate | Reads Provider's operational assessment warning | Explain fake limits and real-provider boundary |
| 3 Install | Adds `ForgeTrust.AppSurface.Durable.Testing` to a .NET 10 test project | Show exact NuGet command and compatible version |
| 4 First assertion | Builds health and asks fake pump to refuse | Provide one complete, compiled xUnit example |
| 5 Integrate | Passes explicit interfaces and request to own host tests | Explain customization and scenario state rules |
| 6 Debug | Sees invalid fixture, timeout, or propagated provider error | Add problem/cause/fix table and exact exception policy |
| 7 Verify real provider | Runs PostgreSQL claim/recovery/permitted-effect tests | Link to real-provider conformance; no fake-storage claims |
| 8 Upgrade | Updates coordinated Durable/Provider/Testing preview versions | Show package compatibility and release note |
| 9 Extend | Adds own delegate and `TimeProvider` | Document overrides, immutable history access, retention |

**First-time confusion report.** T+0:00 the engineer finds Provider but no Testing row. T+0:30 they see a twenty-argument snapshot constructor. T+1:00 they inspect `DurableRuntimePumpAttempt` to learn that refused has no result. T+2:00 they search for a copyable host test and find only provider-oriented operational prose. The planned quick start addresses this; its actual completion time remains to be measured after the package exists.

**Native DX voice.** The `combo/sub` reviewer read the exact DX input hash and found seven issues: unspecified scenario call order/reuse, timeout evidence, state defaults, history access/retention, quick start, API error guidance, and escape-hatch docs. The outside Codex pass is unavailable from this host, so all six DX dual-voice consensus cells are N/A. The primary review accepts the documentation and error-shape remedies; the advisory health gate remains the CEO user challenge.

| DX pass | Initial / plan after remedies | Evidence and specific remedy |
| --- | --- | --- |
| 1 Getting started | 4/10 → 8/10 | Package absent today; add a compiled three-step path and time it with the packed consumer. |
| 2 API design | 5/10 → 8/10 | Six named states are useful; define state/default matrix, missing-assessment behavior, assessment reuse, and history snapshot API. |
| 3 Errors | 4/10 → 8/10 | Production constructor errors exist; document builder, scenario, deadline, cancellation, and provider exceptions with causes and fixes. |
| 4 Documentation | 5/10 → 8/10 | Durable guide is detailed; add start-here and real-provider cross-links plus complete examples. |
| 5 Upgrade | 6/10 → 8/10 | Additive preview package; show coordinated version and release guidance. |
| 6 Environment | 7/10 → 8/10 | Existing packed harness and xUnit are reusable; add Testing to the harness and keep fake clock test-only. |
| 7 Ecosystem | 6/10 → 7/10 | Existing repo/package chooser offers discovery; no separate community channel or SDK language expansion is justified. |
| 8 Measurement | 3/10 → 7/10 | Constructor counts are not adoption data; time one clean packed-consumer journey and retain the result. |

Overall plan score: 5/10 initially, 8/10 after the accepted DX obligations are implemented. These are review scores, not a measured product evaluation. TTHW: unmeasured now, target ≤5 minutes. No additional community platform, video, playground, or telemetry service is in scope; the published package and compiled example are the appropriate delivery path.

**Error examples.** Invalid `Build()` state: name the contradictory fields and suggest `BuildContradictoryForTest()` only for deliberate contradiction tests. Missing assessment: an explicit `InvalidOperationException` with “call AssessHealthAsync first”; document whether one assessment may back multiple pump calls. Observation timeout: distinct from caller `OperationCanceledException`, with invocation-started/unknown-status facts and a link to [operational assessment recovery](../../Durable/operational-assessments.md#operational-triage). Provider exception: propagate unchanged and document that retry is host policy. Messages must contain no durable payload values.

**DX implementation checklist.**

- [ ] A .NET 10 test project installs Testing in one package command and runs a complete xUnit assertion within five measured minutes.
- [ ] The package README shows exact commands, a compiled refusal-versus-empty-pass test, defaults, overrides, and limits.
- [ ] Public XML docs and the README define missing assessment, assessment reuse, timeout, cancellation, and provider-error behavior.
- [ ] Every new error description gives problem, cause, fix, and the best local docs link without serializing payloads.
- [ ] The state/default matrix and `BuildContradictoryForTest()` escape are documented and tested.
- [ ] Fake history snapshots are atomic immutable copies with an explicit test-process retention/clear story.
- [ ] The package chooser, start page, version guidance, packed proof, and real-provider conformance guide link together.
- [ ] The packed consumer captures TTHW evidence and proves the forbidden transitive packages are absent.

**DX tasks.** D1 (P1): specify public scenario call-order/reuse and timeout exceptions before API snapshot. D2 (P1): compile a three-step packed quick start and record TTHW. D3 (P2): add state/default and API error tables. D4 (P2): specify atomic history snapshot and retention/clear behavior. No new TODO is required; the scope remains the confirmed package.

<!-- autoplan-accepted:dx -->
- Define and document scenario behavior for missing assessment, repeated pump calls after one assessment, concurrently completed assessments, and stale assessment versus authoritative provider admission; test all cases.
- Name observation and overall timeout outcomes and caller-cancellation exceptions in the public contract; retain invocation-started/status-unknown evidence and link to recovery guidance.
- Publish a six-state default-field and valid-override matrix, explicit contradictory-fixture errors, and a complete packed xUnit quick start with install/run commands; measure its TTHW against a five-minute target.
- Specify an atomic immutable call-history snapshot API and a retention/clear mechanism; document default behavior, payload-reference privacy, and customization limits.
- Add API-specific problem/cause/fix/error-link guidance and coordinated preview-package upgrade instructions.
<!-- /autoplan-accepted:dx -->

<!-- AUTONOMOUS DECISION LOG -->
## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | CEO | Keep the confirmed full #802 package scope provisionally | Taste | Completeness | Required for the approved external-host proof; narrower staging stays viable | release only local snippets |
| 2 | CEO | Define timeout status as unknown after invocation starts | Mechanical | Explicit over clever | Production admission cannot certify that a timed-out pass never executed | map timeout to refused |
| 3 | CEO | Serialize latest-assessment publication | Mechanical | Completeness | Makes concurrent scenario observations deterministic | unsynchronized last-writer field |
| 4 | CEO | Add packed adopter comparison and privacy documentation | Mechanical | Boil lakes | Tests real consumer value and prevents payload-bearing diagnostic leakage | count constructors as proof |
| 5 | CEO | Carry advisory-health-gate contradiction to final approval | User Challenge | Explicit over clever | Confirmed spec conflicts with canonical provider admission guidance | silently change the spec |
| 6 | DX | Target a measured five-minute packed first assertion | Mechanical | Pragmatic | A compiled consumer proves the package can be adopted with one install | unmeasured speed claim |
| 7 | DX | Specify scenario errors, assessment reuse, and history snapshot API | Mechanical | Completeness | New public types must be predictable under cancellation and concurrency | document only happy path |
| 8 | DX | Keep the copyable xUnit test as the first-use vehicle | Mechanical | Explicit over clever | Reuses the existing packed harness without new hosted infrastructure | playground or video |
