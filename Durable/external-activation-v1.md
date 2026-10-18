# External Durable activation v1

This is the canonical reference for the provider-neutral external activation API, its outcome and cancellation rules,
operator response, and telemetry contract. It is for hosts that receive a payload-free wake from HTTP, a scheduler, a
webhook, or another external signal and then ask the configured Durable provider to attempt one bounded pass. The
[authenticated ASP.NET Core reference-host contract](../examples/durable-external-activation/README.md) describes one
transport; this service has no HTTP dependency.

Use the [typed Work guide](migrations/typed-work-definitions-v1.md) to define and accept Work, the
[operational-assessment guide](operational-assessments.md) for provider health predicates and direct admission, and the
[Work protocol](work-protocol-v1.md) for persisted execution facts and typed exits. A wake selects no Work and carries
no Work payload, scope, lane, caller-selected pump limits, or effect identity.

## Ownership worksheet: billing lifecycle

The pinned [billing-lifecycle adopter evidence](evidence/executable-contract-adoption.md) measured a representative
host and test at commit `8177fb438c1c11b04248d320ee46390c3cb2791b`. That evidence is a design baseline, not proof of this
implementation. The service closes only the reusable activation decision; the adoption boundary stays explicit:

| Decision or fact | Owner | Porting rule |
|---|---|---|
| Work name/version, codecs, executor, retry defaults, and application request construction | Application | Keep the typed definition and acceptance contract with the billing domain. |
| Accepting Work, idempotency, and retaining the acceptance receipt and authorized scope | Application | Save `DurableWorkAcceptance.WorkId` with its trusted scope context outside the wake request. |
| Mapping a generic wake to a bounded provider health observation and one admission call | Service | Replace repeated activation orchestration with `IDurableExternalActivationService`; the provider remains admission authority. |
| Route, authentication/authorization, empty-body policy, HTTP mapping, transport deadlines, burst limits, and platform probe choice | Host | Keep the host's existing endpoint and policy; the package adds no route or limiter. |
| Domain reconciliation, materialization, continuation dispatch, and cleanup | Application | Keep these outside the activation service and outside the pump result. |
| Schema, runtime epoch, StoreId, PostgreSQL roles, and deployment enablement | Host/deployment | Provision and review these out of band; service registration and host startup do not apply DDL or grants. |
| Persisted Work/effect inspection and any later application retry | Application | Use an existing authorized read/recovery path and the retained acceptance receipt; a wake result is not a Work receipt. |

The reference lifecycle sequence is: accept one typed Work item, preserve its receipt, send an authorized empty wake,
verify the persisted terminal Work fact, observe readiness become true, and confirm export of the activation activity.
It does not claim that an overlapping generic wake processed one particular accepted Work item.

## Public API and constraints

The four public types live in `ForgeTrust.AppSurface.Durable.Provider`:

| Type | V1 shape |
|---|---|
| `DurableExternalActivationRequest` | Sealed record constructed as `(DurableRuntimePumpRequest pumpRequest, TimeSpan requestBudget)`; get-only `PumpRequest` and `RequestBudget`. |
| `DurableExternalActivationOutcomeKind` | Closed values and numeric assignments shown below. |
| `DurableExternalActivationResult` | Sealed record constructed as `(kind, observedHealthState, problemCode, pumpResult)`; get-only validated properties. |
| `IDurableExternalActivationService` | `ValueTask<DurableExternalActivationResult> ActivateAsync(DurableExternalActivationRequest request, CancellationToken callerCancellation = default)`. |

`pumpRequest` is required and preserved by reference. `requestBudget` must be positive and no greater than
`TimeSpan.FromMilliseconds(uint.MaxValue - 1)`, the timer range supported by the pinned .NET 10 runtime. Zero, negative,
infinite, and larger values throw during request construction. Timer precision below one millisecond is platform-dependent
and grants no minimum execution time. The request budget is independent of `DurableRuntimePumpRequest.TimeBudget`; it
includes service setup and the health read. A null service request throws `ArgumentNullException` before instrumentation
or operational result mapping.

The result constructor rejects undefined enum values, noncanonical or contradictory problem codes, and unlisted
combinations. Only `Completed` carries a non-null pump result. It preserves the exact provider result reference and every
aggregate field, including `Failed`, `HasMore`, `NextDueAtUtc`, and elapsed time. `Completed` means that one provider
sweep and its terminal bookkeeping completed; it does not mean each Work item succeeded.

## Health observation and admission

`IDurableRuntimeHealth.GetAsync` provides a current observation, not a reservation. `CanAttemptPump` is advisory and
must never be cached as permission for a later call. For a compatible `NotStarted`, `Healthy`, or `Stale` observation,
the service invokes `IDurableRuntimePumpAdmission.TryRunOnceAsync` exactly once. Admission is authoritative. Any
expected precheck rejection invokes it zero times. There is no service queue, retry loop, semaphore, cached readiness
gate, or fallback to `IDurableRuntimePump.RunOnceAsync`.

The PostgreSQL health reader can report `NotStarted` with `ASDUR404` before the first heartbeat exists. This is a valid
initial assessment: compatibility may be true while `IsReady` is false. A host health-assessment response preserves that
state and code as observed. The service accepts this combination, but does not copy `ASDUR404` into an activation
result for `NotStarted`; activation results permit `ASDUR404` or `ASDUR405` only when the observed state is `Stale`.
This keeps “first heartbeat not yet observed” distinct from a retained stale-state diagnostic without changing provider
health semantics.

Health validation treats activation-enabled snapshots separately from unavailable or compatibility-blocked snapshots.
For a snapshot whose `CanEnableActivation` is true, the state, drain flag, and problem code must match this table:

| State | `IsDraining` | Accepted health `ProblemCode` |
|---|---:|---|
| `NotStarted` | `false` | `null` or `ASDUR404` |
| `Healthy` | `false` | `null` |
| `Draining` | `true` | `null` |
| `Stale` | `false` or `true` | `null`, `ASDUR404`, or `ASDUR405` |

`Unavailable` requires `ASDUR103` and both compatibility flags false. Any other snapshot with
`CanEnableActivation == false` must carry one of `ASDUR108` or `ASDUR400`–`ASDUR403`; the activation-disabled
compatibility branch validates that diagnostic and does not apply the enabled-state drain-flag checks above. Thus the
enabled `NotStarted` and `Healthy` cases require `IsDraining == false`, enabled `Draining` requires `true`, and enabled
`Stale` allows either value. Invalid enum values are rejected when a health snapshot is constructed.

The service retains a defined health state after a successful read. A health read that throws, returns null, or supplies
evidence that fails the validation rules below maps to `ActivationFailed/ASDUR407` without invoking admission, unless
caller or request-budget cancellation wins at a pre-admission check. An observed `Unavailable` maps to
`Unavailable/ASDUR103`; incompatible evidence maps to `Incompatible` with `ASDUR108` or `ASDUR400`–`ASDUR403`; a
compatible `Draining` observation maps to `Draining` without admission.

## Passive registration and host startup

`AddDurableExternalActivation()` returns the same `IServiceCollection` and repeat-safely adds the singleton service,
`TimeProvider.System`, and standard logging registration. Existing service and clock registrations win. Health and
admission can be registered before or after the extension. The service constructor exposes
`IDurableRuntimeHealth`, `IDurableRuntimePumpAdmission`, `TimeProvider`, and
`ILogger<DurableExternalActivationService>` to dependency injection.

Registration does not build a service provider, open a database connection, apply schema or role changes, start a timer,
install a hosted worker, map a route, replace an `ActivitySource`, or construct a tracer provider. The default service is
a singleton and its health/admission dependencies must have compatible lifetimes and concurrency behavior. Custom
containers must apply their own validation rules.

The reference ASP.NET host enables `ValidateOnBuild` and `ValidateScopes` in every environment, then resolves
`IDurableExternalActivationService` before listening. This catches missing constructor dependencies at graph validation
and verifies opaque factories at resolution. Build validation is conditional in the default container and does not run
arbitrary factory bodies; an `IServiceCollection` extension cannot guarantee validation in every consumer's container.

## Invocation phases, cancellation, and deadlines

Each call owns its monotonic start timestamp, linked caller/budget token, timer, activity, and phase. Setup time is
subtracted from the original budget; the health read does not reset it. The service checks elapsed time synchronously,
so delayed timer callback delivery cannot admit work after the deadline. Cancellation callbacks only signal their
sources and do not change phase or manufacture a result.

The phases are `PreAdmission`, `PumpInvoked`, and `Completed`. `PumpInvoked` is set immediately before the one call to
`TryRunOnceAsync`; it means the interface was invoked, not that the provider admitted a pass, claimed Work, or began an
application effect. `Completed` is set only after a completed attempt returns.

Before any operational pre-admission return, caller cancellation takes precedence over an elapsed request budget. This
applies before entry work, during health observation, after a health read, and immediately before admission. A health
reader that ignores its token is checked again when it returns. Caller cancellation maps to
`CanceledBeforeAdmission`; budget cancellation maps to `RequestBudgetExceeded`; both mean zero admission calls.

After `PumpInvoked`, an `OperationCanceledException` maps to `PumpCanceled` if either caller or budget cancellation is
signaled, regardless of which token appears on the exception. An unrelated cancellation exception maps to
`ActivationFailed/ASDUR407` before invocation or `PumpFailed/ASDUR407` after it. A non-cancellation exception remains a
failure even if a cancellation source is also signaled. A returned provider attempt is authoritative: do not rewrite a
completed or expected non-completed attempt because cancellation arrived afterward. Fatal
`StackOverflowException`, `OutOfMemoryException`, and `AccessViolationException` propagate.

The budget is cooperative. The service never races unfinished pump work with `Task.WhenAny` or `WaitAsync`, and never
detaches it as fire-and-forget. Provider execution and terminal cleanup may outlast the request budget; transport and
infrastructure limits must allow their independent execution and cleanup bounds. A timeout or HTTP disconnect is not
evidence that no durable or external effect occurred.

## Closed outcomes and host status mapping

The result constructor accepts only these state/code/result tuples. `Eligible` in the table means the exact set
`NotStarted`, `Healthy`, or `Stale`; `CompatibilityCode` means `ASDUR108` or `ASDUR400`–`ASDUR403`; `StaleCode` means
`ASDUR404` or `ASDUR405` and is valid only with observed `Stale`.

| Value | Outcome | Required observed state | Required problem code | Pump result | Reference-host HTTP status |
|---:|---|---|---|---|---:|
| 0 | `Unavailable` | `Unavailable` | `ASDUR103` | `null` | 503 |
| 1 | `Incompatible` | Any defined state except `Unavailable` | `CompatibilityCode` | `null` | 503 |
| 2 | `Draining` | `Draining` | `null` | `null` | 503 |
| 3 | `Busy` | Eligible | `null`, or `StaleCode` only for `Stale` | `null` | 409 |
| 4 | `CanceledBeforeAdmission` | `null` or any defined state | `null`, or `StaleCode` only for `Stale` | `null` | 408 |
| 5 | `RequestBudgetExceeded` | `null` or any defined state | `null`, or `StaleCode` only for `Stale` | `null` | 504 |
| 6 | `Completed` | Eligible | `null`, or `StaleCode` only for `Stale` | Exact non-null provider aggregate | 200 |
| 7 | `ActivationFailed` | `null` or any defined state | `ASDUR407` | `null` | 500 |
| 8 | `PumpCanceled` | Eligible | `null`, or `StaleCode` only for `Stale` | `null` | 408 |
| 9 | `PumpFailed` | Eligible | `ASDUR103`, `CompatibilityCode`, or `ASDUR407` | `null` | 503 for `ASDUR103`/`CompatibilityCode`; otherwise 500 |

`Completed` alone carries a pump result and preserves its exact reference and aggregate fields. It means one admitted
sweep and terminal bookkeeping returned; it does not mean every Work item succeeded. `Busy` means authoritative
admission refused before application execution. `CanceledBeforeAdmission` and `RequestBudgetExceeded` mean zero
admission calls. `ActivationFailed` is an unexpected nonfatal setup/health failure before invocation; `PumpCanceled` and
`PumpFailed` happen after the admission interface was invoked and require persisted-effect inspection before retry.
The sample maps these service outcomes to HTTP as shown; other hosts own their own status and body policy.

Pre-admission cancellation may retain any successfully read defined state, because cancellation wins before the
subsequent health classification. `ActivationFailed` may carry no state or any defined observed state. Stale diagnostics
are retained only for observed `Stale`; the initial health assessment `NotStarted + ASDUR404` remains visible to the
host-owned probe but its code is omitted from an activation result. Unexpected health codes and contradictory evidence
fail closed as `ActivationFailed/ASDUR407`.

The sample's exact JSON bodies, authentication status, body guard, and nullable-field rules are in the
[reference-host contract](../examples/durable-external-activation/README.md#http-contract). Transport framing failures
do not become fabricated activation outcomes. Authentication and authorization run before body validation or service
resolution. The sample rejects a nonempty wake body without calling health or admission.

## Operator actions and recovery

The service performs no retries. Use the result as evidence about this invocation only, then follow the existing
[Durable diagnostic catalog](../troubleshooting/durable-diagnostics.md), [operational assessments](operational-assessments.md),
and [typed Work exits and recovery protocol](work-protocol-v1.md) for their respective domains.

| Outcome or probe failure | Operator action |
|---|---|
| `Unavailable` | Diagnose PostgreSQL connectivity, selected data source, pool pressure, and `ASDUR103`; restore observation before another host-policy-controlled wake. |
| `Incompatible` | Review StoreId, schema version/history, active epoch, compatibility code, and the reviewed migration/role recipe. Startup does not apply DDL or grants. |
| `Draining` | Follow the operator's drain and shutdown controls; do not force another pass through a different API. |
| `Busy` | Inspect current local/store admission and the active runtime; apply application scheduling policy after the refusal cause clears. |
| `CanceledBeforeAdmission` | Confirm zero admission calls and inspect the caller/transport cancellation owner. Reissue only under host policy. |
| `RequestBudgetExceeded` | Confirm zero admission calls; inspect setup/health elapsed time and the configured cooperative budget before changing it. |
| `Completed` | Read the complete aggregate and inspect persisted item exits. A positive `failed` count is still a completed sweep; use item-level diagnostics and recovery guidance instead of repeating a wake to erase the count. |
| `ActivationFailed/ASDUR407` | Inspect the bounded phase/code and service configuration or failed health observation. Admission was not invoked. Never substitute exception text for the safe code. |
| `PumpCanceled` | Admission was invoked. Inspect persisted Work, effect-permit state, and application ownership before any caller retry; cancellation does not prove an effect was absent. |
| `PumpFailed/ASDUR103`, `/ASDUR108`, or `/ASDUR400`–`/ASDUR403` | Diagnose store availability or compatibility and inspect persisted Work/effects before retrying. |
| `PumpFailed/ASDUR407` | Inspect provider execution or terminal-bookkeeping failure and persisted Work/effect facts before retrying. |
| `ProbeFailed/ASDUR407` | No health assessment was obtained. Inspect probe dependency/configuration; do not invent `Unavailable` evidence. |
| `ProbeCanceled` | Inspect request cancellation or transport state. Reissue the observational probe only when appropriate. |

For a billing-lifecycle wake, retain the original `DurableWorkAcceptance.WorkId` and authorized scope context as an
application-owned acceptance receipt before sending the wake. If the activation response is lost, use the existing
authorized application/operator read path with those two values as separate, parameterized lookup inputs. Inspect the
persisted Work state and its effect-permit/recovery evidence, then follow the Work protocol before deciding whether an
application retry is safe. The existing public Work snapshot query accepts the retained scope and Work identity as
separate typed inputs:

```csharp
var workRead = await workControlClient.GetAsync(
    new DurableWorkGetRequest(authorizedScopeId, acceptance.WorkId),
    cancellationToken);
```

This reads the current Work snapshot through `IDurableWorkControlClient`; it does not expose an activation identifier or
add a public status API. Effect-permit inspection stays on the application's existing authorized recovery path, using
separately parameterized scope and Work values where that path reads persisted provider evidence. A request trace
identifies the host invocation and its activation outcome, not which Work a generic wake selected. Overlapping wakes
likewise prove no one-to-one relationship between a response and an acceptance receipt. Never infer “no effect” from a
missing response, timeout, `PumpCanceled`, or `PumpFailed`.

## Telemetry contract v1

The service emits operation `appsurface.durable.runtime.activation`, with `ActivityKind.Internal`, through the
process-shared `AppSurfaceActivitySources.Instance` whose source name is `ForgeTrust.AppSurface`. One operational call
creates at most one activity, and only if a listener enables it. Argument validation precedes activity creation. The
service does not build or flush a tracer provider and never disposes the shared source.

| Exact tag | Value or omission rule |
|---|---|
| `appsurface.durable.activation.contract_version` | Integer `1`, always on an emitted activity. |
| `appsurface.durable.activation.phase` | `pre_admission`, `pump_invoked`, or `completed`, reflecting the final phase. |
| `appsurface.durable.activation.outcome` | Exact outcome enum name for a returned operational result; omitted when a fatal exception propagates. |
| `appsurface.durable.activation.health_state` | Exact defined health-state name after a successful read; omitted before a read. |
| `appsurface.durable.activation.problem_code` | Final validated code when present; otherwise omitted, never an empty/null sentinel. |

Returned `ActivationFailed` and `PumpFailed` set error status without a description. Do not attach events, stack traces,
request or Work data, scope, worker identity, route, principal, pump counts, arbitrary baggage, or arbitrary exception
text to this activity. Safe service logs contain only phase, outcome, observed state, and bounded code. An emitted
activity or an activity observed by a listener does not prove that an exporter delivered it.

For an exporter receipt, configure all four controls: source listening, sampling, processing, and export. The activation
host must run in the **same process** as the test-owned SDK, tracer provider, and in-memory exporter below.
`ActivitySource` listeners observe only activities emitted in their own process. A remote client's in-memory exporter
cannot observe a separately launched or remote host's activation activity merely by sending an HTTP request. For a
separate host process, configure export in that host and inspect delivery at its configured collector.

### Activation activity export proof

The source-owned recipe in
[InProcessActivationExportExample.cs](../examples/durable-external-activation.tests/InProcessActivationExportExample.cs)
uses the same in-process `ActivationTestHost` fixture as the
[exporter controls](../examples/durable-external-activation.tests/ActivationExporterTests.cs). It starts the authorized
reference endpoint with the production activation service and controlled test health/admission dependencies. This
recipe proves SDK export, not PostgreSQL persistence. The
[PostgreSQL lifecycle proof](../examples/durable-external-activation.tests/PostgreSqlActivationLifecycleTests.cs)
separately checks persisted Work, readiness, and export with the real provider.

These explicit imports accompany the compiled .NET 10 exporter sample. The fixture and its
local authorization token are test-owned helpers from that project, so this snippet is an executable repository-test
recipe rather than standalone remote-client code:

<!-- appsurface:snippet id="durable-external-activation-export-imports" file="examples/durable-external-activation.tests/InProcessActivationExportExample.cs" marker="durable-external-activation-export-imports" lang="csharp" -->
```csharp
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using ForgeTrust.AppSurface.Core;
using OpenTelemetry;
using OpenTelemetry.Trace;
```
<!-- /appsurface:snippet -->

<!-- appsurface:snippet id="durable-external-activation-in-process-export" file="examples/durable-external-activation.tests/InProcessActivationExportExample.cs" marker="durable-external-activation-in-process-export" lang="csharp" -->
```csharp
/// <summary>Proves export from the source-owned test host running in the same process as the OpenTelemetry SDK.</summary>
internal static class InProcessActivationExportExample
{
    /// <summary>Starts the in-process test host and sends an authorized empty wake through its real service.</summary>
    /// <param name="callerCancellation">Cancellation for the test-owned HTTP request.</param>
    /// <returns>The activation received by the attached exporter after a successful SDK flush.</returns>
    /// <remarks>The fixture uses controlled health/admission dependencies; this recipe alone proves no persistence.</remarks>
    internal static async Task<Activity> RequireExportAsync(CancellationToken callerCancellation)
    {
        var exported = new ConcurrentQueue<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(AppSurfaceActivitySources.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new SimpleActivityExportProcessor(new RecordingActivityExporter(exported)))
            .Build();
        await using var host = await ActivationTestHost.StartAsync(new ActivationHostOptions
        {
            UseProductionActivationService = true,
        }).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/private/durable/activate")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ActivationTestHost.ValidToken);
        using var response = await host.Client.SendAsync(request, callerCancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _ = await response.Content.ReadAsStringAsync(callerCancellation).ConfigureAwait(false);

        if (!tracerProvider.ForceFlush(30_000))
        {
            throw new InvalidOperationException("The activation exporter did not flush.");
        }

        return exported.Single(activity =>
            activity.Source.Name == AppSurfaceActivitySources.ActivitySourceName
            && activity.OperationName == "appsurface.durable.runtime.activation");
    }

    /// <summary>Collects activities delivered through the SDK's export processor for this test only.</summary>
    /// <param name="exported">Test-owned receipt collection.</param>
    private sealed class RecordingActivityExporter(ConcurrentQueue<Activity> exported) : BaseExporter<Activity>
    {
        /// <inheritdoc />
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exported.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }
}
```
<!-- /appsurface:snippet -->

The returned `Activity` lets the test inspect the real service operation after response completion and successful
`ForceFlush`; the code does not manufacture a synthetic activation. The existing exporter tests additionally check the
exact bounded tag allowlist, zero exception events, flush failure, and an unattached-exporter negative control. A
listener can observe an activation while the exporter collection remains empty. Those tests serialize access to the
process-shared source; the documentation recipe joins the same collection. An emitted or listener-observed activity
alone is not an export receipt.

## Existing-host migration

Keep the existing passive provider composition, host route and authorization policy, body handling, schema and role
deployment, application acceptance, and PostgreSQL finalization. Add the service registration next to the existing
provider registration, validate and resolve it before listening, then replace the endpoint's duplicated health/direct
admission/result mapping with one explicit service request. A pre-existing direct-admission integration may remain
when the caller intentionally owns and tests that lower-level provider result contract; legacy
`IDurableRuntimePump.RunOnceAsync` callers remain supported and are not silently redirected.

The before/after samples below are application-owned helpers in the
[existing-host sample source](../examples/durable-external-activation.tests/ExistingHostActivationExample.cs),
not new package APIs. Copy the adoption imports and helper into the existing ASP.NET host. Its provider, health,
admission, authentication, authorization, empty-body guard, and response projection remain the host's existing setup:

<!-- appsurface:snippet id="durable-external-activation-adoption-imports" file="examples/durable-external-activation.tests/ExistingHostActivationExample.cs" marker="durable-external-activation-adoption-imports" lang="csharp" -->
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
```
<!-- /appsurface:snippet -->

Before, the existing endpoint might have obtained advisory health and invoked direct admission itself, then applied its
own outcome policy. These two lower-level calls alone do not implement the service's complete phase, deadline, and
exception rules. `provider` is the existing host's actual service provider, such as `app.Services`:

<!-- appsurface:snippet id="durable-external-activation-direct-admission" file="examples/durable-external-activation.tests/ExistingHostActivationExample.cs" marker="durable-external-activation-direct-admission" lang="csharp" -->
```csharp
/// <summary>Shows the lower-level calls retained by hosts that intentionally own direct-admission orchestration.</summary>
internal static class ExistingHostDirectAdmissionExample
{
    /// <summary>Obtains advisory health and asks the provider for an authoritative attempt.</summary>
    /// <param name="provider">The existing host's actual service provider.</param>
    /// <param name="pumpRequest">Existing host-configured provider discovery limits.</param>
    /// <param name="callerCancellation">The existing caller token.</param>
    /// <returns>Observed health and the authoritative attempt for the existing host's own outcome policy.</returns>
    /// <remarks>These two calls alone do not supply the service's phase, deadline, or exception policy.</remarks>
    internal static async ValueTask<(DurableRuntimeHealthSnapshot Health, DurableRuntimePumpAttempt Attempt)> RunAsync(
        IServiceProvider provider,
        DurableRuntimePumpRequest pumpRequest,
        CancellationToken callerCancellation)
    {
        var health = await provider.GetRequiredService<IDurableRuntimeHealth>()
            .GetAsync(callerCancellation).ConfigureAwait(false);
        var attempt = await provider.GetRequiredService<IDurableRuntimePumpAdmission>()
            .TryRunOnceAsync(pumpRequest, callerCancellation).ConfigureAwait(false);
        return (health, attempt);
    }
}
```
<!-- /appsurface:snippet -->

After, pass the already configured host builder to `BuildBeforeListeningAsync`. It adds registration, builds and
validates that same provider, and resolves the service before listening. Its returned `App` remains owned by the
application, which maps its existing endpoint and starts/disposes it normally. Within that endpoint, run authentication,
authorization, and the existing bounded empty-body guard before calling `ActivateAsync` with the returned `Service` and
the caller/transport token, such as `HttpContext.RequestAborted`.

The helper explicitly separates a two-second pump discovery budget, a ten-second service budget, and the caller token.
Preserve or adjust these illustrative limits using the host's configuration policy. The exhaustive switch handles all
ten outcomes with the reference host's statuses and throws for an unsupported future outcome. Return the unchanged
`Result` through the host's existing activation-envelope projection; the `(StatusCode, Result)` tuple is an application
decision, not a new wire schema:

<!-- appsurface:snippet id="durable-external-activation-adoption" file="examples/durable-external-activation.tests/ExistingHostActivationExample.cs" marker="durable-external-activation-adoption" lang="csharp" -->
```csharp
/// <summary>Demonstrates adopting the service inside an existing host without adding a package API or route.</summary>
internal static class ExistingHostActivationExample
{
    /// <summary>Builds and eagerly resolves the actual host, disposing it if a dependency factory fails.</summary>
    /// <param name="builder">Existing host builder with its provider and host policy already configured.</param>
    /// <returns>The built application and activation service, without starting a listener.</returns>
    internal static async ValueTask<(WebApplication App, IDurableExternalActivationService Service)> BuildBeforeListeningAsync(
        WebApplicationBuilder builder)
    {
        Register(builder);
        var app = builder.Build();
        try
        {
            return (app, ResolveBeforeListening(app));
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Adds passive activation registration and validates the host's actual container in every environment.</summary>
    /// <param name="builder">Existing host builder, retaining its provider, authentication, and authorization setup.</param>
    internal static void Register(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddDurableExternalActivation();
        builder.Host.UseDefaultServiceProvider((_, options) =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
    }

    /// <summary>Resolves the service before listening so opaque dependency factories are checked as well.</summary>
    /// <param name="app">Existing application built from the same host builder.</param>
    /// <returns>The host's registered activation service.</returns>
    internal static IDurableExternalActivationService ResolveBeforeListening(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services.GetRequiredService<IDurableExternalActivationService>();
    }

    /// <summary>Invokes the service after the existing endpoint has authorized the caller and validated an empty body.</summary>
    /// <param name="service">Eagerly resolved service from the existing host.</param>
    /// <param name="callerCancellation">Caller or transport token, separate from both configured budgets.</param>
    /// <returns>The example's HTTP status and unchanged result for the host's existing response projection.</returns>
    internal static async ValueTask<(int StatusCode, DurableExternalActivationResult Result)> ActivateAsync(
        IDurableExternalActivationService service,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(service);
        var pumpRequest = new DurableRuntimePumpRequest(
            maximumItems: 32,
            timeBudget: TimeSpan.FromSeconds(2),
            surfaces: DurableRuntimeSurface.Work);
        var request = new DurableExternalActivationRequest(
            pumpRequest,
            requestBudget: TimeSpan.FromSeconds(10));

        var result = await service.ActivateAsync(request, callerCancellation).ConfigureAwait(false);
        return (GetStatusCode(result), result);
    }

    /// <summary>Handles every v1 outcome explicitly, preserving the reference host's chosen status policy.</summary>
    /// <param name="result">Validated service result, including its observed state, code, and aggregate.</param>
    /// <returns>The reference host's status without rewriting a returned result after late caller cancellation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An outcome outside the handled v1 contract is supplied.</exception>
    internal static int GetStatusCode(DurableExternalActivationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Kind switch
        {
            DurableExternalActivationOutcomeKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.Incompatible => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.Draining => StatusCodes.Status503ServiceUnavailable,
            DurableExternalActivationOutcomeKind.Busy => StatusCodes.Status409Conflict,
            DurableExternalActivationOutcomeKind.CanceledBeforeAdmission => StatusCodes.Status408RequestTimeout,
            DurableExternalActivationOutcomeKind.RequestBudgetExceeded => StatusCodes.Status504GatewayTimeout,
            DurableExternalActivationOutcomeKind.Completed => StatusCodes.Status200OK,
            DurableExternalActivationOutcomeKind.ActivationFailed => StatusCodes.Status500InternalServerError,
            DurableExternalActivationOutcomeKind.PumpCanceled => StatusCodes.Status408RequestTimeout,
            DurableExternalActivationOutcomeKind.PumpFailed => result.ProblemCode is
                DurableProblemCodes.StoreUnavailable
                or DurableProblemCodes.RecoveryEpochRequired
                or DurableProblemCodes.SchemaMissing
                or DurableProblemCodes.SchemaUpgradeRequired
                or DurableProblemCodes.SchemaVersionUnsupported
                or DurableProblemCodes.SchemaInconsistent
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status500InternalServerError,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Kind, "Unsupported activation outcome."),
        };
    }
}
```
<!-- /appsurface:snippet -->

The documentation-example tests exercise constructor-validated examples of all ten outcomes and each allowed
`PumpFailed` diagnostic. They retain the exact result and `Completed` aggregate, including a positive failed-item count
and `HasMore`, when the caller cancels just before the service returns. They also check the distinct budgets and caller
token, passive registration, and eager factory validation. The
[packed-consumer verifier](https://github.com/forge-trust/AppSurface/blob/ecd81417065982d1803d26000fc7618a7c06108e/Durable/verify-packed-consumers.sh) copies these same sources into its package-reference test
project, so the recipes participate in compilation and execution against the exact restored package APIs. Managed
fences are tied to these source markers by the
[Markdown snippet verifier](../tools/ForgeTrust.AppSurface.MarkdownSnippets/README.md).

`Completed` carries the authoritative sweep aggregate, including a possibly positive failed-item count. A returned
expected non-completed admission attempt is authoritative; a later cancellation check must not rewrite it. The request
budget and pump discovery budget remain distinct, and a cooperative timeout may occur after the provider has begun
execution. Authentication, body policy, acceptance, schema/grants, and provider-owned finalization remain with their
existing owners. Disabling the application-owned endpoint or removing activation registration is the rollback path;
keep accepted Work and persisted recovery evidence for the existing operator path. Direct-admission and legacy pump
callers remain supported.

## Deployment limits

The service contains no rate limiter, concurrency gate, or database-capacity controller. Concurrent calls may each
perform their own health read; authoritative provider overlap refusal occurs at admission. The host/deployment owner
chooses ingress wake and probe limits, request/read deadlines, connection-pool sizing, and database capacity policy.
Those controls do not create new service outcomes, and a controlled burst test is not a scalability or database-pressure
measurement.
