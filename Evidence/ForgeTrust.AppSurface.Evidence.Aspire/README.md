# ForgeTrust.AppSurface.Evidence.Aspire

`ForgeTrust.AppSurface.Evidence.Aspire` provides the separate, consumer-owned `EvidenceHostBootstrap` lifecycle for resource-backed and browser E2E evidence. It keeps test/evidence code out of the normal application `AppHost`: no application host discovers or invokes it automatically.

Read the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md) first. `EvidenceAspireApplication.StartAsync(...)` can build and start an explicitly consumer-composed `DistributedApplication` and bind a named Aspire health condition to a declared evidence resource. The package never discovers an application, provisions cloud resources, creates a Docker sandbox, attests an artifact, or operates a dashboard.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

This AppHost-oriented package follows the coordinated AppSurface release policy.
Before using a prerelease build in an AppHost, development, or test environment,
check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md) for publication status,
compatibility guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Explicit bootstrap

```csharp
using ForgeTrust.AppSurface.Evidence.Aspire;

await using var host = EvidenceHostBootstrap.Create(plan, registration =>
{
    registration.AddResource(evidenceApplication.CreateHealthReadiness("postgres", "postgres"));
    registration.AddProducer(browserE2eProducer);
    registration.SetEnvelopeVerifier(githubActionsEnvelopeVerifier);
});

var manifest = await host.RunAsync();
```

The plan decides which registrations are required. `EvidenceHostBootstrap` rejects missing resources/producers, waits for declared readiness in dependency order, applies resource and producer deadlines, catches producer failures, closes the manifest, and cleans owned disposable registrations in reverse registration order. It executes exactly once.

Use a separate, explicit app lease when resources must be started for this evidence run:

```csharp
var builder = DistributedApplication.CreateBuilder(args);
// Consumer-owned AddPostgres, projects, fixtures, and resource configuration go here.
await using var evidenceApplication = await EvidenceAspireApplication.StartAsync(builder, cancellationToken);
```

Register a readiness adapter with the EvidenceHost. `CreateHealthReadiness(...)` waits for native Aspire health alone. When that condition is insufficient and a declared HTTP status must also pass, use [`CreateHttpReadiness(...)`](#http-readiness-adapter). The adapter owns the probe clients it creates; the existing `EvidenceAspireApplication` remains the sole owner of the `DistributedApplication`, and the normal development AppHost remains unaware of the evidence run.

## HTTP Readiness Adapter

The HTTP adapter composes one declared EvidenceHost resource with native Aspire health, one explicitly named endpoint, and the shared status-only HTTP probe. It does not move `EvidenceHostBootstrap` or its readiness contracts to another assembly, and it does not require `Aspire.Hosting.Testing`. Use it when a resource becoming healthy is necessary but the producer also requires one HTTP status condition before running. Keep browser, response-body, credential, and business assertions in the producer.

```csharp
IEvidenceResourceReadiness EvidenceAspireApplication.CreateHttpReadiness(
    string evidenceResourceId,
    string resourceName,
    string endpointName,
    string probePath,
    TimeSpan timeout,
    EvidenceAspireHttpReadinessOptions? options = null);
```

`evidenceResourceId` must match a declared EvidenceHost resource. `resourceName` and `endpointName` select the exact Aspire resource and endpoint; no ambient lookup occurs. `probePath` is a relative or root-relative path which must resolve to the endpoint's same HTTP(S) scheme, IDN host, and effective port. `timeout` is an explicit finite positive readiness budget. The EvidenceHost cancellation token is also honored, and its resource deadline may shorten this adapter budget.

`EvidenceAspireHttpReadinessOptions.Http` defaults to a new [`HttpReadinessOptions`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#httpreadinessoptions) instance. Its request timeout defaults to three seconds, retry delay to 250 ms, success predicate to exact HTTP 200, and `MaxAttempts` to null (deadline-only). A configured attempt cap must be positive. `CreateProbeClient` is an optional `Func<Uri, HttpClient>`; null selects the adapter's owned native default. A supplied factory is called at most once for each individual readiness wait, after native health and endpoint resolution. It must return a fresh client; the adapter owns and disposes that client before that wait settles. A null result is terminal invalid configuration. Concurrent and sequential waits do not share client or deadline state.

The adapter starts one monotonic deadline for the wait, resolves the named endpoint asynchronously inside its remaining budget, and calls the shared probe core without restarting that budget. Requests use GET with headers-only completion and never read response bodies. Every response is disposed. Retryable conditions are a rejected status, `HttpRequestException`, and an identifiable request or shorter client timeout; caller cancellation, overall deadline expiry, predicate errors, and programming/configuration failures are terminal. When `MaxAttempts` is reached, no extra delay or request is made. The default success predicate accepts exact 200, so select another status only when the producer's declared precondition requires it.

The adapter's disposal closes admission, cancels and drains already admitted waits including their per-wait client cleanup, then delegates once to its existing application lease. All explicit disposal callers join that cleanup result. Do not dispose `EvidenceAspireApplication` directly while a readiness wait is active. The deadline remains cooperative: a transport that ignores cancellation and awaited cleanup may remain pending beyond the wait budget. This contract does not claim whole-host timeout terminalization or fold the separate [#859 cleanup coordination](https://github.com/forge-trust/AppSurface/issues/859) into this adapter.

The canonical [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md#aspire-http-readiness), [readiness cookbook](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md#http-readiness-in-an-ordinary-aspire-test), [neutral probe reference](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md), and the existing lease's preparation record for [issue #778](https://github.com/forge-trust/AppSurface/issues/778) explain which layer owns startup, transport, and evidence policy.

## Public extension points

| Type | Consumer responsibility |
| --- | --- |
| `IEvidenceResourceReadiness` | Bind a declared resource id to an Aspire health/readiness condition. Never report ready merely because a container was created. |
| `IEvidenceProducer` | Perform one typed coverage, API, or browser E2E assertion and return only its declared assertion ids. |
| `IEvidenceExecutionEnvelopeVerifier` | Validate protected CI inputs without putting secret values into a plan or manifest. |
| `EvidenceHostRegistration` | Register each resource, producer, and verifier directly in code. Duplicate and ambient registration are rejected. |
| `EvidenceHostOptions` | Require an envelope for a targeted run when the consumer's policy needs it. |

For release scope, an accepted v1 envelope is represented as `ValidatedNotAttested`; that is intentionally weaker than independent artifact attestation and must be described honestly downstream.

## Pitfalls

- Do not add the EvidenceHost to a normal AppHost or production startup path.
- Do not use a started process as readiness. Wait for the resource condition the producer actually needs.
- Do not swallow cleanup errors. They invalidate the collected evidence rather than leaving a passing claim behind.
- Do not use reflection or assembly scanning to find producers; explicit registration is the trust boundary.

Read next: [Evidence contracts](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md), [planner](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md), and the [E2E recipe](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md#resource-backed-browser-e2e).

If a readiness wait fails and disposing its owned probe client also fails, the original readiness exception stays primary. Only the fixed `Data["AppSurface.Readiness.CleanupFailure"] = ReadinessFailureCategory.CleanupFailure` marker is added on a best-effort basis; no raw secondary exception is retained. A client cleanup failure after an otherwise successful condition produces a safe `Cleanup` / `CleanupFailure` diagnostic. Explicit adapter disposal still joins and reports the existing lease's original cleanup failure. See the [diagnostic contract](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#readinessexception).
