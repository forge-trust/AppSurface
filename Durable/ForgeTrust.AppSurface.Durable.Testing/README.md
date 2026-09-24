# ForgeTrust.AppSurface.Durable.Testing

`ForgeTrust.AppSurface.Durable.Testing` is a .NET 10 test-support package for consumers of
[`ForgeTrust.AppSurface.Durable`](../ForgeTrust.AppSurface.Durable/README.md) and
[`ForgeTrust.AppSurface.Durable.Provider`](../ForgeTrust.AppSurface.Durable.Provider/README.md). It provides deterministic,
production-backed fixtures for health, pump admission, drain control, and typed Work contracts. Its builders call the
production constructors and preserve their validation; it does not emulate a database or provider policy.

Use this package in module and host tests where the question is about request construction, health interpretation,
call ordering, or how a host handles provider admission outcomes. Use the
[PostgreSQL conformance suite](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md) when the claim concerns persisted
state, leases, crash recovery, transactions, or an ambiguous external effect. The
[operational-assessment guide](../operational-assessments.md) defines the production meanings of health and admission.

## Quick start

The package has no test-framework or assertion-library dependency. Start with an xUnit project (or use an existing
test project), add Testing, and save the following test as `DurableHostTests.cs`:

```bash
dotnet new xunit -f net10.0 -n DurableHost.Tests
cd DurableHost.Tests
dotnet package add ForgeTrust.AppSurface.Durable.Testing
```

Keep `ForgeTrust.AppSurface.Durable.Testing`, `ForgeTrust.AppSurface.Durable`, and
`ForgeTrust.AppSurface.Durable.Provider` on the same coordinated preview version. When pinning previews, use the
identical version on each direct package reference.

```csharp
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;
using Xunit;

public sealed class DurableHostTests
{
    [Fact]
    public async Task Host_observes_health_then_uses_authoritative_admission()
    {
        var health = new FakeDurableRuntimeHealth(
            new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.Healthy).Build());
        var pump = new RecordingDurableRuntimePump();
        var scenario = new DurableHostScenario(
            health,
            pump,
            new DurableRuntimePumpRequestBuilder().Build());

        var assessment = await scenario.AssessHealthAsync();
        var attempt = await scenario.RunDirectPumpOnceAsync();

        Assert.True(assessment.Snapshot.IsReady);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, attempt.Kind);
        Assert.Single(pump.History);
        Assert.Same(assessment, scenario.PumpInvocations[0].Assessment);
    }
}
```

Run `dotnet test` from the test-project directory. For a repository-local package feed, use the
[packed consumer verifier](../verify-packed-consumers.sh) as the working example of restore, graph inspection, and
four-kind admission assertions against a freshly packed artifact.

The default recording pump returns `Completed` with an empty result. That means this invocation completed and reported
zero counts; it does not prove that a real store has no eligible work. Configure `RecordingDurableRuntimePump.Admission`
to return any production `DurableRuntimePumpAttempt`, including `Refused`, `Unavailable`, or `Incompatible`, when
testing host handling of those outcomes. Assert the exact `Kind`; never translate refusal or provider unavailability
into an empty successful pass.

## Reference: builders and fakes

| API | Default and behavior | Use it for |
| --- | --- | --- |
| `DurableHealthSnapshotBuilder` | Starts at `Healthy`; named state supplies representative defaults. Field overrides are applied before the production snapshot constructor validates them. | Health consumers and boundary validation. |
| `BuildContradictoryForTest()` | Explicitly opts into otherwise rejected inconsistent state/field combinations for defensive consumer tests. | Testing behavior when upstream evidence is contradictory. |
| `DurableRuntimePumpRequestBuilder` | 32 maximum items, all surfaces, production request defaults for time budget. | Bounded pump requests. |
| `DurableRuntimePumpResultBuilder` | Zero counts, no more-work flag, zero elapsed time, no next due time. | Exact result assertions. |
| `DurableRuntimePumpAttemptBuilder` | `Completed` with a zero-count result. Production attempt constructor validates kind/result/problem-code combinations. | Exhaustive four-kind outcome handling. |
| `DurableWorkRequestBuilder<TWork,TResult>` | Requires a definition and an explicitly supplied payload; default test scope/command values and idempotency key `test-key`. Calls the definition's `CreateRequest`. | Typed request parity and production codec validation. |
| `DurableWorkerEnvelopeBuilder<TPayload>` | Uses native envelope creation and requires correlation and execution/fence identity. | Worker projection assertions that preserve native identity. |
| `FakeDurableRuntimeHealth` | Returns the configured snapshot unchanged; observes cancellation before returning. | Deterministic health reads. |
| `RecordingDurableRuntimePump` | Implements admission-aware and bounded pump interfaces. Captures invocation order, exact request reference, selected surfaces, limits, and completion. Defaults to an empty completed pass. | Call ordering, cancellation, overlap, and outcome handling. |
| `FakeDurableRuntimeDrainControl` | Applies begin/resume requests and retains immutable, ordered transition history. | Drain-control consumer behavior. |

Use builder overrides for a single field at a time where possible. Production constructors remain the validation
authority, so malformed identifiers, bounds, surfaces, counts, codec metadata, or identity still fail during `Build()`.
The contradictory path is intentionally conspicuous; ordinary `Build()` does not silently create impossible fixtures.

## Six health-state defaults

The builder's named states create valid representative snapshots. The four predicate columns are computed by the
production `DurableRuntimeHealthSnapshot`, not by the builder. `Observed` means `WasStoreObserved`.

| State | Schema / epoch defaults | Started / heartbeat defaults | Draining | Observed | Can enable activation | Can attempt pump | Ready |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Healthy` | compatible / compatible | present / present | no | yes | yes | yes | yes |
| `NotStarted` | compatible / compatible | absent / absent | no | yes | yes | yes | no |
| `Stale` | compatible / compatible | present / present (representative timestamps) | no | yes | yes | yes | no |
| `Draining` | compatible / compatible | present / present | yes | yes | yes | no | no |
| `Incompatible` | incompatible / incompatible | present / present | no | yes | no | no | no |
| `Unavailable` | incompatible / incompatible | start / heartbeat absent; active epoch absent | no | no | no | no | no |

`Stale` represents the state, not an elapsed-time calculation performed by this test builder. Override timestamps or
compatibility fields to exercise a particular consumer case; the production constructor validates the resulting
combination. An unavailable assessment means compatibility was not observed, not that incompatibility was proven.

## Reference: host scenario and deadlines

`DurableHostScenario` takes explicit health and admission interfaces, one bounded request, and optional `TimeProvider`,
observation timeout, and overall timeout. Defaults are `TimeProvider.System`, 30 seconds per observation, and two
minutes overall. Budgets use monotonic timestamps. The overall budget begins with the first health assessment, is shared
by later assessments and pump calls, and does not reset; construct a new scenario for a new budget. Custom providers
must supply monotonic timestamps.

Call `AssessHealthAsync` before `RunDirectPumpOnceAsync`. Missing assessment throws `InvalidOperationException`. A
successful assessment is published in completion order, even if it started before another successful read; a later
pump atomically captures the latest published one.
Failed, canceled, or timed-out reads do not replace it. A published assessment may be reused for multiple pump calls.
The snapshot is advisory: every in-budget pump call invokes authoritative admission exactly once even when
`CanAttemptPump` is false. The exact provider attempt is returned unchanged.

`DurableScenarioTimeoutException` names the `Phase` (`Health` or `Pump`) and `Reason` (`Observation` or `Overall`). A
timeout before admission has no invocation. If the pump call has started, `Invocation` carries its sequence, exact
request, captured assessment, and repeat-awaitable `Completion` task. `InvocationStarted` and
`ExecutionStatusUnknown` are true in that case. The timeout ends only the scenario's wait; it does not cancel provider
work or convert it to refusal, unavailability, or an empty pass. Await `Invocation.Completion` to observe the eventual
attempt, cancellation, or original exception. A fault after a permit may still require provider recovery to establish
whether an external effect occurred. Only the caller's cancellation token is passed to provider methods.

The terminal decision gives an already-completed result priority, then observed caller cancellation, then overall
deadline, then observation deadline. A caller cancellation observed before completion/timeout propagates as
`OperationCanceledException`; provider exceptions propagate without being synthesized into attempts. Never silently
retry a timed-out admission call.

## Contract observations

`DurableWorkDefinitionObservation.Capture` snapshots definition identity, codec metadata, classifications, retention
policy IDs, provider-safety declaration, and default retry policy. `DurableWorkRegistryObservation.Capture` performs the
exact production registry lookup; missing-registration errors propagate. `DurableWorkBindingObservation.Capture`
records the exact definition reference and identity without resolving services or running work. These are observation
records, not assertion helpers: use your test framework to make the assertions appropriate to your consumer.

## History, retention, and privacy

Pump `History` and scenario `PumpInvocations` return atomic immutable snapshots ordered by invocation start. Entries
retain exact request and result references, so their metadata/order is frozen but payload-bearing references are
shallow. Do not mutate payload objects while asserting captured requests. The pump retains history for the fake
instance's lifetime until `ClearHistory()`; clearing advances a generation so an older in-flight call cannot repopulate
the cleared history. The scenario retains invocation handles for its lifetime; `ClearCompletedPumpInvocations()` only
removes terminal handles, leaving in-flight work reachable. There is no automatic truncation or disposal that hides
in-flight calls. Clear completed history when retaining a long-lived fixture, and await timed-out handles before
releasing provider/test resources.

Durable payload values are never formatted or serialized by these helpers. Keep assertions and test output focused on
safe scalar facts such as state, attempt kind, sequence, count, and stable problem code. A test framework may display
objects passed to a failing assertion, so avoid asserting whole payload-bearing records when that could disclose
sensitive fixture data.

## Package coordination and proof boundary

This package references only Durable and Durable.Provider as product packages. It intentionally adds no PostgreSQL,
ASP.NET Core, Testcontainers, test framework, or assertion library. Install it only in test projects. Keep the three
Durable package versions aligned during preview upgrades and review their coordinated notes in the
[release hub](../../releases/README.md). Package installation does not establish database behavior: retain real
PostgreSQL tests for acceptance, claim, completion, stale-worker recovery, and ambiguous post-permit outcomes. See the
[Durable package chooser](../../packages/README.md) for package selection and the
[typed Work migration guide](../migrations/typed-work-definitions-v1.md) for authoring contracts.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
