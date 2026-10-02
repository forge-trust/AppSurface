# ForgeTrust.AppSurface.Evidence.Aspire

`ForgeTrust.AppSurface.Evidence.Aspire` provides the separate, consumer-owned `EvidenceHostBootstrap` lifecycle for resource-backed and browser E2E evidence. It keeps test/evidence code out of the normal application `AppHost`: no application host discovers or invokes it automatically.

Read the [EvidenceHost guide](../../start-here/evidencehost.md) first. The package never discovers an application, provisions cloud resources, creates a Docker sandbox, attests an artifact, or operates a dashboard. Aspire execution uses the protected Linux worker and shared admission/lifecycle path. Its support limits and migration order are described below.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

This AppHost-oriented package follows the coordinated AppSurface release policy.
Before using a prerelease build in an AppHost, development, or test environment,
check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md) for publication status,
compatibility guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Explicit supervised bootstrap

The factory registration below describes the API shape for a future accepted resource lane. It currently
fails with `ASEVD407` before factory invocation: no restricted Aspire child or resource capability map has
been proved. A resource-bearing profile also fails Observation admission with `ASEVD406`. See the
[EvidenceHost execution guide](../../start-here/evidencehost.md) for the dependency-free Observation candidate
and the [migration guide](../../docs/evidence/evidencehost-migration.md) for rejected legacy calls. This example
is not an executable quickstart for resource-backed work.

```csharp
using ForgeTrust.AppSurface.Evidence.Aspire;

await using var host = EvidenceHostBootstrap.Create(plan, registration =>
{
    registration.AddAspireHealthResource(resourceDeclaration, "postgres");
    registration.AddProducer(producerDeclaration, browserE2eProducer);
    registration.SetApplicationFactory(CreateConsumerBuilder);
});

var manifest = await host.RunAsync(
    new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, protectedControlSocket),
    cancellationToken);
```

The protected launcher authenticates the worker before the host invokes registration or verifier callbacks. Every resource and producer is paired with its complete protected declaration; extra, missing, or changed declarations fail admission. Configuration runs through the shared bounded lifecycle. Builder creation, build, and start require the separately restricted Aspire child; the current production path rejects them before invocation. The internal test lane verifies resource readiness in dependency order, producer deadlines, joined asynchronous work, artifact checks after cleanup and bounded manifest collection. Each host permits one execution attempt, claimed before authentication. Authentication or admission failure consumes the attempt even if `State` remains `Created`; create a new host for another run. A null request and cancellation before acquiring execution ownership leave the attempt available.

The old `RunAsync(bool observationOnly = false)` overload remains for source compatibility but always fails before configuration: omitted/false returns `ASEVD401`; true returns `ASEVD402` because it has no authenticated supervisor. `EvidenceAspireApplication.StartAsync(IDistributedApplicationBuilder)` returns `ASEVD400` before building a preconfigured builder. A deferred factory can be registered with the host, but the current production lane rejects its execution:

```csharp
static IDistributedApplicationBuilder CreateConsumerBuilder()
{
    var builder = DistributedApplication.CreateBuilder(args: []);
    // Add consumer-owned resources and configuration here.
    return builder;
}
```

The intended resource lane creates readiness adapters for `AddAspireHealthResource` after its admitted application starts, then owns its bounded stop/dispose lifecycle. Application ownership must be retained immediately after build, before start can fail. Startup failure is stopped and joined by the shared lifecycle before disposal; the start callback must not dispose the lease itself. Direct readiness probes use `AddResource(declaration, probe)`. Producers use `AddProducer(declaration, producer)`. Legacy id-only registration overloads remain source compatible but cannot satisfy the exact admitted declaration check.

Observation accepts only a protected dependency-free targeted profile and issues informational evidence. The registered verifier API and envelope-shaped values cannot enable Trusted runtime admission until a full protected consumer acceptance proof exists; current production context deliberately rejects Trusted with `ASEVD407`. The control-channel request alone grants no authority. The protected Linux launcher is the current candidate mechanism; full runtime acceptance is pending. macOS, Windows, direct in-process execution, and unrelated long-lived host processes are unsupported.

## Public extension points

| Type | Consumer responsibility |
| --- | --- |
| `IEvidenceResourceReadiness` | Bind a declared resource id to an Aspire health/readiness condition. Never report ready merely because a container was created. |
| `IEvidenceProducer` | Perform one typed coverage, API, or browser E2E assertion and return only its declared assertion ids. |
| `IEvidenceExecutionEnvelopeVerifier` | Legacy structural callback shape; it does not independently admit Trusted execution. Protected admission owns verifier invocation. |
| `EvidenceHostRegistration` | Register each complete resource/producer declaration, optional deferred application factory, and verifier directly in code. Duplicate and ambient registration are rejected. |
| `EvidenceHostOptions` | Retained for source compatibility; it cannot grant execution admission or change protected budgets. |

For release scope, an accepted v1 envelope is represented as `ValidatedNotAttested`; that is intentionally weaker than independent artifact attestation and must be described honestly downstream. Runtime support is not available on a platform without the protected Linux launcher.

## Pitfalls

- Do not add the EvidenceHost to a normal AppHost or production startup path.
- Do not use a started process as readiness. Wait for the resource condition the producer actually needs.
- Do not swallow cleanup errors. They invalidate the collected evidence rather than leaving a passing claim behind.
- Do not use reflection or assembly scanning to find producers; explicit registration is the trust boundary.

Read next: [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), [planner](../ForgeTrust.AppSurface.Evidence.Planner/README.md), and the [E2E recipe](../../guides/evidencehost-cookbook.md#resource-backed-browser-e2e).
