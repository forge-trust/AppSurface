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

## Typed Work definitions

Define one static, immutable contract and use it for registration and request creation. The definition captures the Work
name/version, input and result codec identities, provider safety, and default retry policy once. Callers still choose
the scope, command, duplicate-submission key, input, explicit retry override, and due time for each request.

The [complete compiled source](../packed-consumers/Adopter/TypedWorkDefinitionProof.cs), including its using directives, is the copyable entry point. The example below includes three distinct Work/input/result identities, ordinary, reconciled, and
exit-aware bindings, generated JSON metadata, executor implementations, a direct request comparison, and a passive Flow
composition. Its output proves contract registration and request parity only; it does not accept Work, start a worker, or
claim terminal processing. The source is linked into the focused test so the documentation and test exercise one body.

<!-- appsurface:snippet id="durable-typed-work-definition" file="Durable/packed-consumers/Adopter/TypedWorkDefinitionProof.cs" marker="durable-typed-work-definition" lang="csharp" -->
```csharp
internal static class TypedWorkDefinitionProof
{
    internal static readonly DurableWorkDefinition<InvoiceWork, InvoiceResult> OrdinaryDefinition =
        DurableWork.Define(
            workName: "examples.invoice.send",
            workVersion: "v1",
            workCodec: TypedWorkCodecs.InvoiceWorkCodec,
            resultCodec: TypedWorkCodecs.InvoiceResultCodec,
            providerSafety: DurableProviderSafety.Idempotent,
            defaultRetryPolicy: DurableWorkRetryPolicy.Default);

    internal static readonly DurableWorkDefinition<LedgerWork, LedgerResult> ReconciledDefinition =
        DurableWork.Define(
            workName: "examples.ledger.reconcile",
            workVersion: "v1",
            workCodec: TypedWorkCodecs.LedgerWorkCodec,
            resultCodec: TypedWorkCodecs.LedgerResultCodec,
            providerSafety: DurableProviderSafety.ReconcileBeforeRetry,
            defaultRetryPolicy: DurableWorkRetryPolicy.Default);

    internal static readonly DurableWorkDefinition<NotificationWork, NotificationResult> ExitDefinition =
        DurableWork.Define(
            workName: "examples.notification.send",
            workVersion: "v1",
            workCodec: TypedWorkCodecs.NotificationWorkCodec,
            resultCodec: TypedWorkCodecs.NotificationResultCodec,
            providerSafety: DurableProviderSafety.ProviderKeyed,
            defaultRetryPolicy: DurableWorkRetryPolicy.Default);

    private static readonly IDurablePayloadCodec<FlowContext> FlowContextCodec =
        new SystemTextJsonDurablePayloadCodec<FlowContext>(
            "examples.flow.context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            TypedWorkJsonContext.Default.FlowContext,
            static _ => true);

    private static readonly DurableWorkRetryPolicy ExplicitRetry = new(
        maximumAttempts: 4,
        maximumElapsedTime: TimeSpan.FromHours(1),
        initialRetryDelay: TimeSpan.FromSeconds(2),
        maximumRetryDelay: TimeSpan.FromMinutes(5),
        leaseDuration: TimeSpan.FromMinutes(1),
        renewalCadence: TimeSpan.FromSeconds(15),
        maximumLeaseLifetime: TimeSpan.FromMinutes(5),
        backoffAlgorithm: "linear-v1");

    internal static void Run()
    {
        var services = new ServiceCollection();
        new AppSurfaceDurableModule().ConfigureServices(
            new StartupContext([], new PassiveHostModule()),
            services);

        var ordinaryBinding = OrdinaryDefinition.ExecutedBy<InvoiceExecutor>();
        var reconciledBinding = ReconciledDefinition.ExecutedBy<LedgerExecutor>()
            .ReconciledBy<LedgerReconciler>();
        var exitBinding = ExitDefinition.ExecutedByExit<NotificationExitExecutor>();

        services.AddDurableWork(ordinaryBinding);
        services.AddDurableWork(reconciledBinding);
        services.AddDurableWork(exitBinding);
        services.AddSingleton<IDurablePayloadCodec>(FlowContextCodec);
        services.AddSingleton<DurableFlowRegistration>(provider =>
        {
            var workRegistration = provider.GetRequiredService<IDurableWorkRegistry>()
                .GetRequired(OrdinaryDefinition.WorkName, OrdinaryDefinition.WorkVersion);
            var callsite = new FlowActivityCallsite<InvoiceWork, InvoiceResult>("send-invoice", 1, 1);
            var flowDefinition = FlowGraphBuilder<FlowContext>
                .Create("examples.invoice-flow", "v1")
                .AddNode("send", new InvoiceActivityNode(callsite))
                .StartAt("send")
                .Build();
            var activityBinding = new DurableFlowActivityBinding<FlowContext, InvoiceWork, InvoiceResult>(
                callsite,
                workRegistration,
                OrdinaryDefinition.WorkCodec,
                OrdinaryDefinition.ResultCodec);
            return new DurableFlowRegistration<FlowContext>(
                flowDefinition,
                FlowContextCodec,
                "typed-work-definition-proof-v1",
                new FlowTransitionEvaluator<FlowContext>(),
                [activityBinding]);
        });

        var scope = new DurableScopeId("typed-proof-scope");
        var command = new DurableCommandId("typed-proof-command");
        var due = new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.FromHours(-4));
        var input = new InvoiceWork("invoice-1001");
        var actual = OrdinaryDefinition.CreateRequest(
            scope,
            command,
            "typed-proof-key",
            input,
            retryPolicy: ExplicitRetry,
            dueAtUtc: due);
        var expected = new DurableWorkRequest(
            scope,
            command,
            "typed-proof-key",
            OrdinaryDefinition.WorkName,
            OrdinaryDefinition.WorkVersion,
            OrdinaryDefinition.WorkCodec.Encode(input),
            OrdinaryDefinition.ProviderSafety,
            ExplicitRetry,
            due);
        AssertRequestParity(expected, actual);
        var defaultRequest = OrdinaryDefinition.CreateRequest(scope, command, "default-key", input);
        AssertRequestParity(new DurableWorkRequest(scope, command, "default-key", OrdinaryDefinition.WorkName,
            OrdinaryDefinition.WorkVersion, TypedWorkCodecs.InvoiceWorkCodec.Encode(input), OrdinaryDefinition.ProviderSafety,
            OrdinaryDefinition.DefaultRetryPolicy), defaultRequest);
        TypedWorkMigrationSnippets.Ordinary(scope, command, "ordinary-migration", input, ExplicitRetry, due);
        TypedWorkMigrationSnippets.Reconciled(scope, command, "reconciled-migration", new LedgerWork("entry-1"), ExplicitRetry, due);
        TypedWorkMigrationSnippets.ExitAware(scope, command, "exit-migration", new NotificationWork("recipient-1"), ExplicitRetry, due);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IDurablePayloadCodecRegistry>();
        var workRegistry = provider.GetRequiredService<IDurableWorkRegistry>();
        var flow = provider.GetRequiredService<IDurableFlowRegistry>().GetRequired("examples.invoice-flow", "v1");
        var registeredWork = workRegistry.GetRequired(OrdinaryDefinition.WorkName, OrdinaryDefinition.WorkVersion);
        if (!ReferenceEquals(flow.ActivityWorkRegistrations.Single(), registeredWork)
            || !ReferenceEquals(registeredWork.WorkCodec, OrdinaryDefinition.WorkCodec)
            || !ReferenceEquals(registeredWork.ResultCodec, OrdinaryDefinition.ResultCodec))
        {
            throw new InvalidOperationException("Flow composition did not retain the exact defined Work registration and codec views.");
        }
        if (provider.GetService<IDurableWorkClient>() is not null
            || provider.GetService<IDurableFlowClient>() is not null
            || provider.GetService<IDurableScheduleClient>() is not null
            || provider.GetServices<IHostedService>().Any())
        {
            throw new InvalidOperationException("Typed Work proof unexpectedly installed a runtime.");
        }

        Console.WriteLine("typed Work contracts registered; request parity verified; no runtime installed");
    }

    internal static void AssertRequestParity(DurableWorkRequest expected, DurableWorkRequest actual)
    {
        if (expected.ScopeId != actual.ScopeId
            || expected.CommandId != actual.CommandId
            || expected.IdempotencyKey != actual.IdempotencyKey
            || expected.WorkName != actual.WorkName
            || expected.WorkVersion != actual.WorkVersion
            || expected.Payload != actual.Payload
            || expected.ProviderSafety != actual.ProviderSafety
            || expected.RetryPolicy != actual.RetryPolicy
            || expected.DueAtUtc != actual.DueAtUtc
            || expected.Fingerprint != actual.Fingerprint)
        {
            throw new InvalidOperationException("Typed Work request does not match equivalent direct construction.");
        }
    }

    private sealed class InvoiceActivityNode(FlowActivityCallsite<InvoiceWork, InvoiceResult> callsite)
        : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                FlowNodeOutcome<FlowContext>.Activity(callsite, new InvoiceWork("invoice-from-flow"), context.State));
    }

    internal sealed class InvoiceExecutor : IDurableWorkerExecutor<InvoiceWork, InvoiceResult>
    {
        public ValueTask<InvoiceResult> ExecuteAsync(
            DurableWorkerEnvelope<InvoiceWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new InvoiceResult(work.Payload!.InvoiceId));
    }

    internal sealed class LedgerExecutor : IDurableWorkerExecutor<LedgerWork, LedgerResult>
    {
        public ValueTask<LedgerResult> ExecuteAsync(
            DurableWorkerEnvelope<LedgerWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LedgerResult(work.Payload!.EntryId));
    }

    internal sealed class LedgerReconciler : IDurableEffectReconciler<LedgerWork, LedgerResult>
    {
        public ValueTask<DurableEffectReconciliation<LedgerResult>> ReconcileAsync(
            DurableWorkerEnvelope<LedgerWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableEffectReconciliation<LedgerResult>.NotApplied());
    }

    internal sealed class NotificationExitExecutor : IDurableWorkExitExecutor<NotificationWork, NotificationResult>
    {
        public ValueTask<DurableWorkExit<NotificationResult>> ExecuteAsync(
            DurableWorkerEnvelope<NotificationWork> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DurableWorkExit<NotificationResult>.Succeeded(
                new NotificationResult(work.Payload!.Recipient)));
    }

    private sealed class PassiveHostModule : IAppSurfaceHostModule
    {
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureServices(StartupContext context, IServiceCollection services) { }
        public void RegisterDependentModules(ModuleDependencyBuilder builder) { }
    }
}

internal sealed record InvoiceWork(string InvoiceId);
internal sealed record InvoiceResult(string InvoiceId);
internal sealed record LedgerWork(string EntryId);
internal sealed record LedgerResult(string EntryId);
internal sealed record NotificationWork(string Recipient);
internal sealed record NotificationResult(string Recipient);
internal sealed record FlowContext(string InvoiceId);

internal static class TypedWorkCodecs
{
    internal static readonly IDurablePayloadCodec<InvoiceWork> InvoiceWorkCodec =
        new SystemTextJsonDurablePayloadCodec<InvoiceWork>("examples.invoice.request", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.InvoiceWork, static _ => true);
    internal static readonly IDurablePayloadCodec<InvoiceResult> InvoiceResultCodec =
        new SystemTextJsonDurablePayloadCodec<InvoiceResult>("examples.invoice.result", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.InvoiceResult, static _ => true);
    internal static readonly IDurablePayloadCodec<LedgerWork> LedgerWorkCodec =
        new SystemTextJsonDurablePayloadCodec<LedgerWork>("examples.ledger.request", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.LedgerWork, static _ => true);
    internal static readonly IDurablePayloadCodec<LedgerResult> LedgerResultCodec =
        new SystemTextJsonDurablePayloadCodec<LedgerResult>("examples.ledger.result", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.LedgerResult, static _ => true);
    internal static readonly IDurablePayloadCodec<NotificationWork> NotificationWorkCodec =
        new SystemTextJsonDurablePayloadCodec<NotificationWork>("examples.notification.request", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.NotificationWork, static _ => true);
    internal static readonly IDurablePayloadCodec<NotificationResult> NotificationResultCodec =
        new SystemTextJsonDurablePayloadCodec<NotificationResult>("examples.notification.result", "v1",
            DurableDataClassification.ApprovedApplication, TypedWorkJsonContext.Default.NotificationResult, static _ => true);
}

[JsonSerializable(typeof(InvoiceWork))]
[JsonSerializable(typeof(InvoiceResult))]
[JsonSerializable(typeof(LedgerWork))]
[JsonSerializable(typeof(LedgerResult))]
[JsonSerializable(typeof(NotificationWork))]
[JsonSerializable(typeof(NotificationResult))]
[JsonSerializable(typeof(FlowContext))]
internal sealed partial class TypedWorkJsonContext : JsonSerializerContext;
```
<!-- /appsurface:snippet -->

Use `ExecutedBy<TExecutor>()` for ordinary `Idempotent`, `ProviderKeyed`, or `ManualResolution` Work. Complete a
`ReconcileBeforeRetry` binding with `ReconciledBy<TReconciler>()`. Use `ExecutedByExit<TExecutor>()` only for
`ProviderKeyed` Work when the executor can return an honest typed exit fact. Register one binding per Work identity;
adding a legacy and definition registration beside each other with the same name/version produces the existing
duplicate Work error.

The definition is passive and safe to reuse across concurrent request calls when the codec and its captured dependencies
are safe for concurrent use. Metadata is captured during definition creation. Later mutable codec behavior is not frozen;
incompatible encoded metadata is rejected at the codec boundary. The caller owns concurrent use of its codec and any
application state it closes over.

The focused passive proof can be run with:

```bash
dotnet test Durable/ForgeTrust.AppSurface.Durable.Tests/ForgeTrust.AppSurface.Durable.Tests.csproj \
  --filter TypedWorkDefinitionProofTests --no-restore
```

For accepted Work and actual terminal success, use the existing [PostgreSQL reference workload](../slice3-reference-workload.md)
and its [provider acceptance proof](../ForgeTrust.AppSurface.Durable.PostgreSql/README.md#accept-work).
The typed proof intentionally reports no terminal outcome.

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
`0001`–`0009` migration flow, canonical role recipe, registry-scoped Work-discovery rollout, recovery posture, and the
implemented [`durable schema` CLI commands](../../Cli/ForgeTrust.AppSurface.Cli/README.md#durable-postgresql-schema-commands).

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
