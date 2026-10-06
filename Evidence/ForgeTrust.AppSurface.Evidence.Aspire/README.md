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
using ForgeTrust.AppSurface.Evidence.Contracts;

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
using Aspire.Hosting;

static IDistributedApplicationBuilder CreateConsumerBuilder()
{
    var builder = DistributedApplication.CreateBuilder(args: []);
    // Add consumer-owned resources and configuration here.
    return builder;
}
```

The restricted resource path selects an immutable definition from the [closed application catalogue](../ForgeTrust.AppSurface.Evidence.Planner/README.md#internal-closed-application-catalogue-prerequisite). It retains a pending local application lease before application-start I/O, checks complete registrations, then asks the authenticated root broker to start the exact application ID and entry digest. It never executes a public builder factory. The production catalogue and acceptance registry remain empty, so matching metadata cannot enable this path.

`AddAspireHealthResource` supplies declaration and resource-name metadata for that selected definition. Root readiness adapters are captured before start and execute only inside their actual bounded Resource stage. Each resource has one attempt; the adapter links cancellation to that stage and tracks the entire root request even if its returned task is ignored. A public call without the internal stage binding is rejected. Readiness requires the typed root receipt for the prior application lease, resource, UID and cgroup; a caller notification or arbitrary health callback supplies no authority. Direct `AddResource(declaration, probe)` remains available for metadata and the synthetic lifecycle tests but cannot substitute for a closed restricted adapter.

Restricted producers use [the shared coverage factory](../ForgeTrust.AppSurface.Evidence.Coverage/README.md#fixed-restricted-coverage-registration):

```csharp
using ForgeTrust.AppSurface.Evidence.Coverage;

registration.AddProducer(producerDeclaration,
    EvidenceRestrictedCoverageProducerFactory.Create(producerDeclaration));
```

The factory returns inspectable sealed metadata. The host captures and validates those exact references against complete selected declarations before startup. Public resource and producer maps are read-only views; later registrations cannot replace a captured adapter. Public same-declaration producer substitutes and legacy id-only registrations fail the restricted audit. Every actual Producer callback binds the same protected writer, admitted plan, captured diff bytes, shared process-output quota and lifecycle to the factory's one-attempt internal lease. Neither matching metadata nor a local artifact writer can issue it.

Root stop/wait must establish physical exit of application and producer groups and completion of output pumps before local disposal or artifact collection. Local application disposal checks the lifecycle's joined pre-disposal phase; it adds no independent grace or timer. `OwnWorkStopped` is a final predicate that includes registered disposer tasks and must not be checked from inside one of those same tasks. Premature local disposal stays failed on retry. The [shared lifecycle contract](../ForgeTrust.AppSurface.Evidence.Contracts/README.md) describes stopping, cleanup and collection reserves. Startup failure follows that same stop/join path before disposal.

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
