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
