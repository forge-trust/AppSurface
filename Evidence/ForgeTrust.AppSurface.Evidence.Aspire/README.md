# ForgeTrust.AppSurface.Evidence.Aspire

`ForgeTrust.AppSurface.Evidence.Aspire` provides the separate, consumer-owned `EvidenceHostBootstrap` lifecycle for resource-backed and browser E2E evidence. It keeps test/evidence code out of the normal application `AppHost`: no application host discovers or invokes it automatically.

Read the [EvidenceHost guide](../../start-here/evidencehost.md) first. `EvidenceAspireApplication.StartAsync(...)` can build and start an explicitly consumer-composed `DistributedApplication` and bind a named Aspire health condition to a declared evidence resource. The package never discovers an application, provisions cloud resources, creates a Docker sandbox, attests an artifact, or operates a dashboard.

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

Register the resulting `CreateHealthReadiness(...)` adapter with the EvidenceHost. It owns a bounded `StopAsync`/`DisposeAsync` lifecycle once the host cleans up; the normal development AppHost remains unaware of it.

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

Read next: [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), [planner](../ForgeTrust.AppSurface.Evidence.Planner/README.md), and the [E2E recipe](../../guides/evidencehost-cookbook.md#resource-backed-browser-e2e).
