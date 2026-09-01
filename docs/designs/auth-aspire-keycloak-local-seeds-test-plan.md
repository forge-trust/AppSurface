# Test Plan: Issue #782 Local Keycloak Seed Extension Points

Status: Implemented and verified on the supported public Aspire surface
Design: [Ordered Local Seed Extension Points](auth-aspire-keycloak-local-seeds.md)

## Test Layers

| Layer | Purpose | Primary location |
| --- | --- | --- |
| Public-Aspire proof | Preserve the supported lifecycle, binding, manifest, and execution-context semantics used by the public API. | Focused package tests and the executable AppHost/sample. |
| Hosting unit tests | Verify wrapper-local validation, immutable handles, diagnostics, and no registry records on failure. | `Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests` |
| AppHost integration tests | Observe ordered process launch, completion, failure, cancellation, and publish exclusion. | Focused AppHost test project introduced by the spike. |
| Executable sample proof | Verify consumer-owned identity/fixture convergence and output hygiene against persistent local data. | New #782 sample and verifier. |
| Package/docs verification | Preserve dependency isolation and links across the package index, README, sample guide, and release notes. | Existing repository package/doc verification suites. |

## Contract Verification Cases

| Case | Setup | Assertion |
| --- | --- | --- |
| Baseline completion | Valid Keycloak baseline and a finite project that exits `0`. | The seed project launches only after the realm-ready gate succeeds. |
| Failed baseline | Metadata, generated import, or authorization challenge is invalid. | Gate emits the existing redacted diagnostic; no seed or web process launches. |
| Ordered completion | Register two finite projects in a linear chain. | Seed two starts only after seed one exits successfully; web starts only after seed two. |
| Nonzero seed exit | First or final seed returns nonzero. | Later resources never receive successful completion and remain unlaunched. |
| Cancellation and hang | Cancel AppHost during each stage; separately leave a worker running past its own timeout. | Capture actual public resource states; hang is non-completed until cancellation, never silently treated as success. |
| Restart behavior | Exercise success, nonzero exit, timeout, and cancellation under the supported Aspire runtime. | Record observed restart/no-restart behavior without asserting an unsupported framework contract. |
| Safe context values | Factory binds authority, realm, and public client ID to the returned project. | Manifest and environment annotations show values only on that project and never on web. |
| Secret parameter | Bind one and then multiple required `ParameterResource` values. | Manifest has a value-free reference only on the declared project; no web projection, framework diagnostic, or evidence contains the sentinel. |
| Registration failure | Blank/duplicate name, invalid immediate predecessor, cross-builder/wrapper handle, thrown/null/wrong-name factory return. | Stable `ASKEYC` diagnostic before `Build()` and no wrapper-registry entry. |
| Local-only policy | Exercise `Run`, `Publish`, other operations, default allow-list, custom allow-list, blank, and unknown environment. | Only `Run` plus allowed local/test environment invokes the factory; publish artifacts contain no seed resource. |

## Consumer Sample Cases

| Case | Fault point | Exact postcondition |
| --- | --- | --- |
| Normal two-seed run | None. | One broker alias, one `founder -> subject-founder-001` record, and one `candidate:founder` fixture record; web proof launches. |
| Optional broker disabled | Do not register identity seed. | No broker mutation; fixture is the first/last seed and the web proof waits for it. |
| Partial mutation then rerun | Exit nonzero after identity worker has converged broker alias and subject map, immediately before fixture upsert. | Rerun produces exactly one broker alias, one subject map, and one natural-key fixture record. |
| Capability failure | Consumer admin capability check rejects credentials. | Named seed failure, no fixture/web launch, no provider text parsed by AppSurface. |
| Output hygiene | Set `LOCAL_TEST_SECRET_SENTINEL` in required secret input. | Sentinel absent from framework manifests/projections/diagnostics/evidence and from every worker/dashboard capture available to the sample test. |

## Required Commands and Evidence

1. Record the exact Aspire and AppSurface package versions used by the spike.
2. Capture redacted generated manifests and a timestamped resource-state timeline for each lifecycle case.
3. Run the focused test project, then the repository's relevant package/documentation verification and formatting checks.
4. Run `./scripts/coverage-solution.sh` when the added AppHost tests can participate in the solution coverage run.
5. Store no secret, raw provider response, source-machine path, or generated credential in committed test evidence.

## Exit Criteria

`RealmReady()` and `WithLocalSeed(...)` are now implemented only because the supported public APIs proved the required
completion, typed-parameter, and manifest behavior. The regression suite must preserve that behavior. Any future
Aspire incompatibility returns the feature to this design; it must not be replaced with a package callback runner or a
broader secret-binding mechanism.

## Current Spike Evidence

The initial #782 spike added a private finite gate in
[`examples/auth-aspire-keycloak-readiness-gate`](https://github.com/forge-trust/AppSurface/tree/main/examples/auth-aspire-keycloak-readiness-gate).
Its public-API
findings are now embodied by the package-owned `RealmReady()` executable and the two consumer-owned projects in the
[sample AppHost](../../examples/auth-aspire-keycloak-apphost). The package gate reconstructs the existing readiness
probe from safe values and never accepts an administrator credential or arbitrary consumer configuration.

The deterministic test suite captures these results without a container runtime:

| Evidence | Result |
| --- | --- |
| Completion graph | The AppHost wires Keycloak through `WaitFor` to the readiness gate, then through successful-exit `WaitForCompletion` dependencies to a finite consumer-style worker and the web proof. |
| Gate outcomes | The worker returns `0` after an injected successful probe, `1` for a named safe diagnostic or invalid input, and `124` after cancellation. |
| Consumer-worker outcomes | The private lifecycle worker deterministically returns `0` for success, `1` for failure or its bounded timeout mode, and `124` when a hanging test worker is cancelled. |
| Output hygiene | An injected `LOCAL_TEST_SECRET_SENTINEL` inside a probe exception is absent from worker output. |
| Parameter binding | A secret `ParameterResource` produces only its value-free reference in the declared project's publish manifest; an unrelated web project receives neither the variable nor the reference. |

The project pins `Aspire.Hosting` and `Aspire.Hosting.AppHost` at `13.4.4`, and `Aspire.Hosting.Keycloak` at
`13.4.4-preview.1.26314.3` in [Directory.Packages.props](../../Directory.Packages.props). The local Aspire CLI may be
newer; live proof evidence records the CLI version separately. No generated manifest, source-machine path, secret, or
provider response is committed as evidence.

### Live-runtime status

On Aspire CLI `13.4.6`, the first local `verify` run was inconclusive: DCP created the local container network but did
not allocate the Keycloak service port before the profile's existing five-minute timeout. Keycloak, the gate, the web
project, and the verifier therefore remained unlaunched; no `ASKEYC` diagnostic or worker output was produced. This is
recorded as an infrastructure observation, not gate evidence.

Follow-up runs on the same pinned AppHost packages established two public-Aspire observations without exposing a
secret or retaining a generated artifact:

| Lifecycle case | Observed result |
| --- | --- |
| Isolated randomized-port run | Keycloak reached healthy state, then the gate started and finished with its fixed-authority probe unable to use the randomized provider port. The web proof remained blocked and transitioned to failed start; no dependent verifier launched. This demonstrates nonzero finite completion does not release the dependent web project. |
| Fixed-port normal run | Keycloak became healthy, the finite gate completed, the web proof started, the verifier started, and the `verify` profile exited `0` with its success message. The web endpoint explicitly pins both public and target ports to the realm's registered local redirect port and opts out of DCP proxying, preventing a dynamically allocated redirect URI. |
| Finite consumer worker success | Keycloak, gate, private lifecycle worker, web proof, and verifier completed in that order; the `verify` profile exited `0`. |
| Finite consumer worker failure | The worker transitioned `Running -> Finished` after its nonzero failure mode. The web proof transitioned `Waiting -> FailedToStart`; the verifier never launched. No automatic worker restart was observed before AppHost cancellation. |
| Finite consumer timeout | The worker's consumer-enforced timeout mode transitioned `Running -> Finished` with the same blocked web result. No automatic worker restart was observed before AppHost cancellation. |
| Finite consumer hang and cancellation | The worker remained `Running` while web and verifier remained `Waiting`. Cancelling the owned AppHost session transitioned web and verifier to `FailedToStart`; the DCP executor stopped its resource watchers during shutdown. |

### Implemented-contract evidence

The public `RealmReady` and `WithLocalSeed` contract is intentionally narrow and additive. The focused tests verify
cached realm-ready registration, the linear `WaitForCompletion` chain, operation-first local-only denial, factory and
predecessor validation, redacted typed-secret manifest binding, no secret reuse, and absence of a seed from unrelated
web configuration. The consumer-store tests verify idempotent natural-key replacement, malformed-state rejection,
atomic-update recovery, and concurrent read consistency.

Live Aspire CLI `13.4.6` runs against the pinned `13.4.4` hosting packages recorded these final sample outcomes without
committing generated state or credential values:

| Lifecycle case | Observed result |
| --- | --- |
| Normal two-seed run | Keycloak became healthy; `RealmReady`, identity bootstrap, candidate fixture, web proof, and verifier completed in order. The verifier confirmed one broker alias, one founder mapping, and one fixture. |
| Persistent normal rerun | The same graph completed with the same exact three record counts, proving the consumer upserts did not duplicate state. |
| Injected fixture failure | Identity bootstrap converged the broker and founder mapping; the candidate fixture exited nonzero before its upsert. The web proof remained blocked/failed to start and no candidate fixture was recorded. |
| Recovery run | Removing the injected failure and rerunning converged the partial state to exactly one record of each kind. |

The observed Aspire runtime does not promise automatic restart of a failed finite project. A consumer therefore owns
its bounded timeout and rerun-safe idempotence; dependents receive no successful completion signal after a failure or
while a worker is hung.
