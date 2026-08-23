# Test Plan: Issue #782 Local Keycloak Seed Extension Points

Status: Feasibility spike in progress; public seed API intentionally withheld
Design: [Ordered Local Seed Extension Points](auth-aspire-keycloak-local-seeds.md)

## Test Layers

| Layer | Purpose | Primary location |
| --- | --- | --- |
| Public-Aspire spike | Prove lifecycle, binding, manifest, and execution-context semantics using supported APIs only. | New focused test AppHost/sample, before public API implementation. |
| Hosting unit tests | Verify wrapper-local validation, immutable handles, diagnostics, and no registry records on failure. | `Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests` |
| AppHost integration tests | Observe ordered process launch, completion, failure, cancellation, and publish exclusion. | Focused AppHost test project introduced by the spike. |
| Runnable sample proof | Verify consumer-owned identity/fixture convergence and output hygiene against persistent local data. | New #782 sample and verifier. |
| Package/docs verification | Preserve dependency isolation and links across the package index, README, sample guide, and release notes. | Existing repository package/doc verification suites. |

## Feasibility Spike Cases

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

Implementation may add the public `RealmReady` and `WithLocalSeed` surface only when every feasibility-spike case passes on documented public APIs. Any unavailable or inconclusive case returns the work to the design instead of substituting a callback runner or a broader secret-binding mechanism.

## Current Spike Evidence

The first #782 pull request implements only the isolated finite-project spike. It adds a private sample worker at
[`examples/auth-aspire-keycloak-readiness-gate`](../../examples/auth-aspire-keycloak-readiness-gate) and places it
between Keycloak health and the existing web proof with Aspire's public `WaitFor` and `WaitForCompletion` annotations.
The worker reconstructs the existing public readiness probe from safe local values and never accepts an administrator
credential or a generic configuration object.

The deterministic test suite captures these results without a container runtime:

| Evidence | Result |
| --- | --- |
| Completion graph | A Keycloak dependency uses `WaitFor`; first and second finite projects each use a successful-exit `WaitForCompletion` dependency. |
| Gate outcomes | The worker returns `0` after an injected successful probe, `1` for a named safe diagnostic or invalid input, and `124` after cancellation. |
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
a container-orchestration prerequisite failure, not evidence that the finite gate completed or failed.

The public `RealmReady` and `WithLocalSeed` contract remains withheld until a supported local Aspire runtime completes
the baseline and the remaining lifecycle cases. The runner, annotations, deterministic manifest test, and this
inconclusive status are the complete scope of the isolated first spike pull request.
