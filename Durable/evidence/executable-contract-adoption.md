# Executable-contract adoption evidence

## Runtime-doctor operator comparison

The [runtime doctor](../runtime-doctor.md) addresses the repeated health
interpretation recorded below. Previously, an operator combined schema status,
[runtime health assessments](../operational-assessments.md#health-predicates),
process logs and application tests to decide which prerequisite failed. Doctor
returns the connected store's diagnosis and its next action in one report;
[deployment preflight](../heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist)
and application verification still require their own evidence.

On October 3, 2026 UTC, the
[operator workflow test](../../Cli/ForgeTrust.AppSurface.Cli.Tests/DurableDoctorIntegrationTests.cs)
executed two isolated workflows through `AppSurfaceCliApp.RunAsync` against one
disposable PostgreSQL store. It used the same runtime connection throughout,
selected by `ISSUE801_PRIVATE_CONNECTION`, and the same configured epoch variable,
`ISSUE801_PRIVATE_RUNTIME_EPOCH`. Each command's before/after state fingerprint
matched, and a pruning-function trap was never invoked.

| Workflow | Observed diagnosis and next action | Recovery evidence | Measured time |
| --- | --- | --- | --- |
| Store epoch mismatch | Exit 2, `ASDUR108`; retry doctor with the original selected environment names | The fixture restored its active epoch on the same StoreId; JSON and text doctor reruns returned exit 0 | 17:01:10.928387–17:01:11.079591 UTC; 151.20 ms including fixture changes, 49.65 ms across CLI calls |
| Incompatible schema | Exit 2, `ASDUR401`; `appsurface durable schema status --connection-env ISSUE801_PRIVATE_CONNECTION` | Status reported installed 10 / required 11. The fixture restored its captured schema-11 history and metadata on the same StoreId; JSON and text reruns returned exit 0 | 17:01:11.085856–17:01:11.268244 UTC; 182.39 ms including fixture changes, 53.47 ms across CLI calls |

These timings cover commands on an existing host with .NET and Docker already
available. Fixture provisioning, package installation, builds and cold-host setup
were outside the measured intervals. The implementation-task clock began at
14:41:51 UTC; that elapsed engineering time is not adopter onboarding time.
Recovery used synthetic fixture edits, not a claim that a deployment migration or
epoch-rotation procedure was executed. The test can write a fresh local timing
record when `APPSURFACE_DOCTOR_WORKFLOW_EVIDENCE_PATH` names an owned output file;
timing values are observations, not performance thresholds.

The maintainer inspected the actual clean text: it says “Store/runtime checks
passed,” states that worker observation was not requested, and asks for the
consumer's composition verifier command. The displayed boundary explicitly
excludes deployment authorization, process ownership, future readiness and
successful application execution. This records the intended interpretation of
observed output; it is not a human comprehension study. Cold-host time to first
diagnosis, adopter comprehension and a comparison of elapsed manual-triage time
remain unmeasured.

## Original adoption-rail filing evidence

This evidence closes the filing gate in
[case #793](https://github.com/forge-trust/AppSurface/issues/793). It compares
one representative Skoolit durable-worker lane with the three-touchpoint
AppSurface adoption rail proposed in the
[approved design](../../docs/designs/durable-executable-contract-adoption-rail.md).
It is design evidence, not proof that the proposed APIs already exist.

## Baseline

- Consumer: [forge-trust/skoolit](https://github.com/forge-trust/skoolit)
- Commit: `8177fb438c1c11b04248d320ee46390c3cb2791b`
- Representative lane: billing lifecycle
- Measured host:
  `src/Skoolit.DurableWorker/BillingLifecycleDurableWorkerHost.cs`
- Measured lifecycle test:
  `tests/Skoolit.Web.Tests/BillingLifecycleDurableWorkerHostEndpointTests.cs`

The measurement tool verifies the exact checkout commit and rejects changes to
either selected consumer file. The wider checkout may contain unrelated local
work; it cannot change the measured regions.

Billing lifecycle is representative because it has passive Durable
registration, externally activated direct pumping, health, application
reconciliation, continuation dispatch, and endpoint tests. The seven selector
lanes observed in the worker host are calendar, source lifecycle, digest email,
billing lifecycle, Gmail content backfill, onboarding forwarding binding, and
forwarding. Seven is the selector-lane count, not the total durable-registration
count: account deletion, Gmail scoped backfill, and Web-module registrations
also exist.

## Behavioral baseline

Time to first successful real Durable completion is **unmeasured**. The selected
endpoint test runs against `TestServer` with stub health, pump, dispatcher, and
reconciler services. Its one-millisecond pump duration is fixture data, not an
elapsed adoption measurement. It does not start PostgreSQL, accept real Work,
execute a real codec/provider path, or prove an exported activation activity.
This evidence therefore makes no cold-start or time-to-first-success claim.
Those clocks belong to the generated-template evidence in case 9.

The representative production path repeats five definition-owned facts during
registration: Work name, Work version, provider safety, Work codec, and result
codec. Request construction repeats Work name, Work version, provider safety,
the Work codec, and retry policy. The selected production host and contract
contain nine direct references to these contract members; the selected endpoint
test adds one more. Across the worker host, six files register Work directly and
seven construct a direct pump path. Seven worker-host files also reconstruct
the operational decision from component flags.

The current endpoint test reproduces these production-relevant rows without a
real provider:

| Row | Current evidence | Gap exposed by the rail |
| --- | --- | --- |
| Compatible `Healthy` | Health route returns 200 and pump/recovery order is asserted | 200 is based on schema and epoch flags, not `IsReady` |
| Incompatible schema/epoch | Health route returns 503 | Unavailability is not distinguished from observed incompatibility |
| Empty pump | No continuation pump is dispatched | No canonical completed-empty attempt shape |
| Disabled revision | Pump, dispatch, and reconciliation are skipped | Revision admission remains application-owned |
| Route override | Configured private routes exist and defaults return 404 | Route naming remains host-owned |

`NotStarted`, `Stale`, `Draining`, store-unavailable, process-local overlap,
caller cancellation, and deadline cancellation are not exercised by this
endpoint test. The baseline also passes the request cancellation token directly
into the pump, so it does not prove that the host request deadline and pump
discovery budget are distinct.

## Measured comparison

The source regions and exact boundary tokens are recorded in
[the measurement specification](https://github.com/forge-trust/AppSurface/blob/main/Durable/evidence/executable-contract-adoption.measurements.json);
the deterministic output is checked in as
[the result](https://github.com/forge-trust/AppSurface/blob/main/Durable/evidence/executable-contract-adoption.results.json). Token lines are
excluded and every nonblank physical line between them is counted, including
comments and braces.

| Touchpoint | Baseline | Proposed | Limit | Interpretation |
| --- | ---: | ---: | ---: | --- |
| Registration | 6 | 10 | 25 | One immutable definition closes Work facts and feeds one binding |
| External-activation mapping | 15 | 24 | 25 | The sketch keeps readiness separate, calls authoritative admission directly, and makes the request deadline, pump budget, four outcomes, and authorization explicit |
| Primary lifecycle test | 25 | 15 | 25 | One scenario removes hand-built health and pump fixtures while asserting the readiness transition |

All proposed regions pass the 25-line readability guardrail. The external
mapping intentionally grows by nine lines: the baseline omits several safety
decisions that the proposal makes visible. The readiness route is a separate
observation; the activation route calls authoritative admission directly and
handles its four closed outcomes without a health check-then-act race. Line
count is secondary; a shorter mapping that hides the deadline, admission,
outcomes, or authorization policy would fail this evidence.

## Ownership boundary

The sketch genericises only three reusable touchpoints:

1. `DurableWorkDefinition<TWork, TResult>` owns immutable registration and
   request defaults.
2. Canonical health assessments and admission-aware pumping expose safe host
   decisions while the direct route keeps transport policy visible.
3. `DurableHostScenario` owns deterministic Durable health/pump test fixtures.

The following remain explicitly application-owned and outside the measured
proposed regions:

- domain execution, aggregate transitions, persistence, and projection rules;
- acceptance, idempotency identity, outbox rows, and materialization;
- Cloud Tasks, OIDC claims, route names, and authorization implementation;
- revision admission, deployment configuration, and restart policy;
- reconciliation ordering and continuation dispatch.

That boundary rejects a provider-neutral wake dispatcher, Skoolit lane
abstractions, application outbox types, or domain lifecycle APIs as upstream
surface.

## Verdict

The three-touchpoint proposal passes the filing gate with
`overallPassed: true`. It is narrow enough to upstream without absorbing
Skoolit policy, and it makes the missing safety decisions more explicit.
The evidence supports filing the dependency-eligible Track B and Track C cases;
it does not bypass their separate dependency or second-adopter gates.

Reproduce from the AppSurface repository root with a Skoolit checkout at the
pinned commit:

```console
dotnet run --project tools/ForgeTrust.AppSurface.Durable.AdoptionMetrics -- \
  --spec Durable/evidence/executable-contract-adoption.measurements.json \
  --consumer-root /path/to/skoolit \
  --output Durable/evidence/executable-contract-adoption.results.json
```
