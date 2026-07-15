# ForgeTrust.AppSurface.Durable

> **Source-only public preview:** this package is intentionally excluded from all repository publish plans until the
> PostgreSQL provider milestone supplies conformance and restore evidence. It installs no runtime and starts no hosted
> service.

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

## Public API by audience

Every public type in this package belongs to one of these adopter-facing families. The
[member-level API snapshot](PublicAPI.Shipped.txt) is the canonical inventory; public types added to the corresponding
source families inherit the audience and compatibility policy shown here.

| Audience | Public types | Contract role |
|---|---|---|
| All adopters | `DurableScopeId`, `DurableWorkId`, `DurableCommandId`, `DurableProblem`, `DurableOperationResult<T>`, `DurableProblemCodes` | Opaque identity and safe diagnostics |
| Serialization authors | `DurableDataClassification`, `DurableEncodedPayload`, `IDurablePayloadCodec`, `IDurablePayloadCodec<T>`, `SystemTextJsonDurablePayloadCodec<T>`, registry types | Explicit, versioned, policy-approved payload bytes |
| Work authors | `DurableProviderSafety`, retry/state/request/acceptance types, `IDurableWorkClient`, execution/prepared-work/registration/registry types, `DurableServiceCollectionExtensions` | Declare, enqueue, and execute typed Work through a provider adapter |
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
| Mutation semantics or canonical bytes | Requires a new fingerprint schema id |
| Payload bytes | Requires a new application contract version |
| Flow executable behavior | Requires a new implementation version or explicit migration |
| Schedule dialect semantics | Requires a new dialect/version, never reinterpret persisted occurrences |
| Provider SPI behavior | Requires provider conformance evidence before adoption |

Diagnostics available now cover contract validation and semantic conflicts. Storage, schema, activation, heartbeat,
drain, and restore diagnostics are reserved until a provider implements and tests them. See the
[`ASDURxxx` catalog](../../troubleshooting/durable-diagnostics.md).

## Release Guidance

Use the [package chooser](../../packages/README.md) for the machine-enforced publication hold. Versioned publication
evidence and policy live in the [release hub](../../releases/README.md).
