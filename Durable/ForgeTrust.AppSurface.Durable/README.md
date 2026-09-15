# ForgeTrust.AppSurface.Durable

> **Public preview:** the [PostgreSQL provider](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md) supplies the
> current conformance path. This package installs no runtime and starts no hosted service.

`ForgeTrust.AppSurface.Durable` is the adopter-facing contract package for durable Work, resumable
[AppSurface Flow](../../Flow/ForgeTrust.AppSurface.Flow/README.md), schedules, serialization, registration, and clients.
Runtime-provider and operator APIs live in
[`ForgeTrust.AppSurface.Durable.Provider`](../ForgeTrust.AppSurface.Durable.Provider/README.md).

## Choose this package when

- a reusable module needs to describe durable work without choosing storage;
- a typed Flow must resume at explicit, persisted transition boundaries;
- a host needs `At`, `After`, `Every`, or Cron schedule intent; or
- an application needs stable command fingerprints for retry/conflict comparison.

Do not choose it for arbitrary replayable code, exactly-once external effects, child workflows, unbounded fan-out, a
message bus, storage, or worker hosting.

## Passive registration proof

`AppSurfaceDurableModule` registers only payload, Work, and Flow registries. This complete source consumer verifies that
registration resolves those registries and leaves `IHostedService` empty:

<!-- appsurface:snippet id="durable-passive-registration" file="Durable/ForgeTrust.AppSurface.Durable.Tests/PassiveRegistrationProof.cs" marker="durable-passive-registration" lang="csharp" -->
```csharp
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Durable.Examples;

internal static class PassiveRegistrationProof
{
    internal static void Run()
    {
        var services = new ServiceCollection();
        new AppSurfaceDurableModule().ConfigureServices(
            new StartupContext([], new PassiveHostModule()),
            services);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IDurablePayloadCodecRegistry>();
        _ = provider.GetRequiredService<IDurableWorkRegistry>();
        _ = provider.GetRequiredService<IDurableFlowRegistry>();
        if (provider.GetService<IDurableWorkClient>() is not null
            || provider.GetService<IDurableFlowClient>() is not null
            || provider.GetService<IDurableScheduleClient>() is not null
            || provider.GetServices<IHostedService>().Any())
        {
            throw new InvalidOperationException("Durable contract registration must remain passive.");
        }

        Console.WriteLine("contracts registered; no runtime installed");
    }

    private sealed class PassiveHostModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }
    }
}
```
<!-- /appsurface:snippet -->

From the repository root, the compile-and-run proof is one command:

```bash
dotnet test Durable/ForgeTrust.AppSurface.Durable.Tests/ForgeTrust.AppSurface.Durable.Tests.csproj \
  --filter PassiveRegistrationProof --artifacts-path /tmp/appsurface-durable-passive
```

Expected result: the named proof passes; no runtime, network call, DDL, poller, or hosted service starts.
The proof emits `contracts registered; no runtime installed`.

## Exit-aware Work for a proven pre-effect retry

Use `IDurableWorkExitExecutor<TWork,TResult>` only when an executor can state a fact about its own provider I/O.
It is additive: [`IDurableWorkerExecutor<TWork,TResult>`](../../Workers/ForgeTrust.AppSurface.Workers/IDurableWorkerExecutor.cs)
remains the right contract for ordinary success-or-exception Work. V1 is deliberately limited to the PostgreSQL
provider and `ProviderKeyed` Work; `AddDurableWorkExit` fixes that safety class so a caller cannot accidentally apply
the contract to an idempotent, reconciliation, or manual-resolution registration.

This five-line registration is compiled from the packed adopter proof in
[`GmailBackfillExitExample.cs`](../packed-consumers/Adopter/GmailBackfillExitExample.cs):

<!-- appsurface:snippet id="durable-work-exit-gmail" file="Durable/packed-consumers/Adopter/GmailBackfillExitExample.cs" marker="durable-work-exit-gmail" lang="csharp" -->
```csharp
internal static void Register(IServiceCollection services) =>
    services.AddDurableWorkExit<GmailBackfillRequest, GmailBackfillResult, GmailBackfillExecutor>(
        "gmail.sender-list-backfill",
        "v2",
        new SystemTextJsonDurablePayloadCodec<GmailBackfillRequest>(
            "consumer.gmail-backfill.request",
            "v1",
            DurableDataClassification.ApprovedApplication,
            GmailBackfillJsonContext.Default.GmailBackfillRequest,
            static _ => true),
        new SystemTextJsonDurablePayloadCodec<GmailBackfillResult>(
            "consumer.gmail-backfill.result",
            "v1",
            DurableDataClassification.ApprovedApplication,
            GmailBackfillJsonContext.Default.GmailBackfillResult,
            static _ => true));
```
<!-- /appsurface:snippet -->

The executor returns one of four closed facts: `Succeeded(result)`, `RetryBeforeEffect(code)`,
`FailedTerminal(code)`, or `AmbiguousExternalOutcome(code)`. `RetryBeforeEffect` means the executor can prove it did
not begin an external provider mutation; read-only provider I/O is allowed. It does not mean that no Durable permit
was committed. The provider still owns permit,
claim, cancellation, retry-delay, deadline, and fencing decisions; an exit has no direct state API, custom retry
delay, hook, metadata envelope, or exception classifier.

Use application-owned, privacy-safe codes such as `app.gmail.sender_list_transient`. Codes use the normal Durable
identifier alphabet and a 120-character maximum. The reserved `ASDUR` prefix is rejected case-insensitively, so do
not reuse any provider diagnostic code: those explain provider/runtime facts, while an application exit code explains
the executor fact. See the [Work protocol's typed
exit rules](../work-protocol-v1.md#typed-executor-exits) and the [diagnostics catalog](../../troubleshooting/durable-diagnostics.md#exit-aware-work-codes).

Before registering the new immutable Work version, deploy exit-capable Provider/PostgreSQL binaries, confirm every
worker that can discover the contract has restarted with that capability, then accept the new version. Never convert
an accepted version in place. A legacy provider that reaches an exit-aware Work through `InvokeAsync` fails safely
with `DurableWorkExitCompatibilityException`; keep exit-capable workers running until all accepted new-version Work is
terminal or intentionally suspended before rolling back.

## Slice 7 discovery boundary

This package is a public preview. Registration is intentionally passive: it installs contract registries, not storage
operations or worker hosting.
PostgreSQL storage registration remains passive as well; continuous processing requires the explicit
[`AddWorkerHost()` opt-in](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md#run-a-worker-host).

When a host starts the opted-in worker, startup validates schema compatibility and the active runtime epoch. It fails
closed when those values are incompatible and never applies DDL or advances migration history. See the
[Slice 7 discovery and reconciliation guide](../README.md#slice-7-discovery-and-reconciliation) for the ordered
`0001`–`0010` migration flow, canonical role recipe, registry-scoped Work-discovery rollout, recovery posture, and the
implemented [`durable schema` CLI commands](../../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands).

When an external activator needs execution certainty, use the [operational-assessment adoption guide](../operational-assessments.md)
and the Provider package's `IDurableRuntimePumpAdmission`. `CanAttemptPump` is an advisory precheck; it must not be
used as a check-then-act gate. The admission-aware call owns authoritative admission and returns `Completed`,
`Refused`, `Unavailable`, or `Incompatible`. A completed result certifies pass bookkeeping, not item-level success.

## Public API by audience

Every public type in this package belongs to one of these adopter-facing families. The
[member-level API snapshot](https://github.com/forge-trust/AppSurface/blob/main/Durable/ForgeTrust.AppSurface.Durable/PublicAPI.Shipped.txt) is the canonical inventory; public types added to the corresponding
source families inherit the audience and compatibility policy shown here.

| Audience | Public types | Contract role |
|---|---|---|
| All adopters | `DurableScopeId`, `DurableWorkId`, `DurableCommandId`, `DurableProblem`, `DurableOperationResult<T>`, `DurableProblemCodes` | Opaque identity and safe diagnostics |
| Serialization authors | `DurableDataClassification`, `DurableEncodedPayload`, `IDurablePayloadCodec`, `IDurablePayloadCodec<T>`, `SystemTextJsonDurablePayloadCodec<T>`, registry types | Explicit, versioned, policy-approved payload bytes |
| Work authors | `DurableProviderSafety`, retry/state/request/acceptance types, `IDurableWorkClient`, execution/prepared-work/registration/registry types, `DurableWorkExitKind`, `DurableWorkExit<T>`, `DurableEncodedWorkExit`, `IDurableWorkExitExecutor<TWork,TResult>`, `DurableWorkExitCompatibilityException`, and `DurableServiceCollectionExtensions` | Declare, enqueue, and execute typed Work through a provider adapter |
| Flow authors | Flow identifiers, state/request/result/snapshot/client types; evaluation, activity, event, registration, registry, and determinism-verifier types | Persist one explicit Flow transition at a time |
| Schedule authors | Schedule shapes/policies/targets, schedule request/result/snapshot/list/explain types, `IDurableScheduleClient`, `DurableScheduleProblemCodes` | Author and inspect versioned schedule intent |
| Retry-aware clients and providers | `DurableCommandFingerprint`, `DurableCommandFingerprintMatch` | Compare canonical semantic command bytes without treating unknown schemas as equal |
| Effect-reconciliation authors | `DurableEffectReconciliationKind`, reconciliation result types, `IDurableEffectReconciler<TWork,TResult>` | Declare side-effect-free reconciliation for ambiguous provider outcomes |
| Composition roots | `AppSurfaceDurableModule` | Register passive contract registries only |

The application surface intentionally excludes runtime pump, claim, health, drain, scope-control, and Work operator
types. Those are Provider SPI.

## Command fingerprints

Every command-bearing mutation exposes a computed `DurableCommandFingerprint` with a versioned schema id and SHA-256
digest. Fingerprints cover Work enqueue; Flow start, event, cancel, and recovery release; schedule create, update, pause,
resume, delete, and recovery release. Provider operator commands have their own schemas in the Provider package.

Use `Compare` before treating a repeated command identity as equivalent. `Exact` means the schema and digest agree;
`Conflict` means the known schema agrees but semantic bytes differ; `UnsupportedSchema` means the caller must not guess.
Never persist a caller-supplied digest as authoritative.

Schedule targets are encoded by their registered codec when the target is constructed. Mutating the caller's input
object afterward cannot change the fingerprint or the bytes a provider receives.

## Identifiers, results, and limits

All request and result constructors reject default opaque identifiers at the public boundary. Collection-bearing
results defensively copy caller collections. `DurableWorkerExecutionIdentity` can only be created through validated
factories and advanced through its monotonic transition API; its provider key is the immutable activity id.

### Durable identifier alphabet and bounds

Fields documented as durable identifiers accept only ASCII letters (`A-Z`, `a-z`), digits (`0-9`), hyphens (`-`),
underscores (`_`), periods (`.`), and colons (`:`). They reject null, empty, or whitespace-only values, control
characters, and every other character. The shared rule keeps persisted identity values ordinal, privacy-safe, and
portable across providers.

Work and Flow names and other registered names are limited to 200 characters; immutable Work and Flow versions and
other registered versions are limited to 100 characters. Provider health and inventory contracts apply the same
alphabet with a 200-character worker-id limit and 120-character terminal/problem-code limit. These rules apply only to
fields documented as durable identifiers. Human-readable labels, Cron expressions, time-zone ids, and encoded payloads
have their own validation rules and must not be inferred from this alphabet.

Cron expression text is limited to 512 characters and the IANA time-zone id to 128 characters. These are contract
limits, not proof that a string is valid Cronos grammar or a known time zone; a provider performs grammar and zone
validation before persistence.

## Preview compatibility

| Change | Preview policy |
|---|---|
| Additive request/result member | Allowed only with a documented default and fingerprint review |
| Mutation semantics or canonical bytes | Before either independent boundary—the first supported publication or any persisted deployment—v1 may be corrected in place with updated test vectors; after either, requires a new fingerprint schema id |
| Payload bytes | Requires a new application contract version |
| Flow executable behavior | Requires a new implementation version or explicit migration |
| Schedule dialect semantics | Requires a new dialect/version, never reinterpret persisted occurrences |
| Provider SPI behavior | Requires provider conformance evidence before adoption |

Diagnostics available now cover contract validation, semantic conflicts, and PostgreSQL Work storage, schema,
activation, and restore failures. Heartbeat, drain, and hosted-runtime diagnostics are provider-owned. See the
[`ASDURxxx` catalog](../../troubleshooting/durable-diagnostics.md).

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->
