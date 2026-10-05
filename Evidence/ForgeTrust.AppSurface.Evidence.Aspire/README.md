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

The plan decides which registrations are required. `EvidenceHostBootstrap` rejects missing resources/producers, waits for declared readiness in dependency order, applies resource and producer deadlines, catches producer failures, closes the manifest, and cleans producers in reverse registration order and resources in reverse dependency order. It executes exactly once. Read [execution and cleanup budgets](#execution-and-cleanup-budgets) before registering custom callbacks.

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
| `EvidenceHostOptions` | Set the trusted-envelope requirement, artifact root, total execution budget, and total cleanup allowance. |
| `IEvidenceExecutionLifetime` | Supply a safe-to-overlap stop-and-join capability when a registration owns active work or external processes. |
| `EvidenceProcessLifetime` | Force-terminate and join an explicitly enrolled set of already-started process handles. |

For release scope, an accepted v1 envelope is represented as `ValidatedNotAttested`; that is intentionally weaker than independent artifact attestation and must be described honestly downstream.

## Execution and cleanup budgets

```csharp
var options = new EvidenceHostOptions(RequireTrustedEnvelope: true)
{
    ExecutionTimeout = TimeSpan.FromMinutes(60),
    CleanupTimeout = TimeSpan.FromSeconds(30),
};
await using var host = EvidenceHostBootstrap.Create(plan, register, options);
var manifest = await host.RunAsync(cancellationToken: cancellationToken);
```

`ExecutionTimeout` defaults to one hour and bounds envelope validation, resource readiness, and producer execution together. Plan resource and producer deadlines still apply inside that budget. `CleanupTimeout` defaults to 30 seconds and is a separate total allowance for stopping, joining, and disposing registrations. Both values must be positive and no greater than 1,073,741,823 milliseconds. The supplied `TimeProvider` drives these deadlines as well as elapsed budget accounting.

Cleanup proceeds in reverse producer order, then reverse resource dependency order, with the envelope verifier last. Each remaining owner receives an equal share of the remaining allowance, so a stalled owner leaves time for unrelated cleanup. Each owner is processed once. Cleanup does not race ordinary disposal against a tracked callback: the callback must settle first. A failed or unsettled owner retains resources it depends on, including transitive dependencies, to avoid disposing resources still in use. Unrelated registrations still receive cleanup attempts.

The host invokes extension callbacks on the thread pool to keep a synchronously blocking method from blocking the host's deadline waiter. This isolates the wait; it does **not** kill managed code. A callback or disposal method that outlives its budget may remain active. Late task faults are observed; no terminal pass or cleanup success is inferred from abandoning a wait. Use a separate process and an independent supervisor when code cannot cooperate, and leave the outer CI timeout in place.

Implement `IEvidenceExecutionLifetime.StopAsync(CancellationToken)` when an owner needs an explicit stop while its execution callback is active. The implementation must tolerate that overlap, stop all owned work, and return only after joining it. For external commands, termination and exit verification must cover owned descendants as well as the leader. Cancellation or a leader exit alone does not establish quiescence. `EvidenceProcessLifetime` supplies this capability for already-started, consumer-owned `Process` handles. Pass the leader and every descendant that can survive independently; retain their handles until stop finishes. It force-terminates enrolled processes and their currently discoverable trees, then verifies exit of every enrolled handle, including a descendant whose leader already exited. It neither discovers detached descendants after parent exit nor proves completeness of enrollment. Use OS-level containment and an independent outer supervisor when complete enrollment is unavailable. Repeated stop calls share the first attempt; cancellation or termination errors require forward recovery, not a success claim.

The host bounds this operation, joins tracked execution callbacks, then invokes ordinary disposal. A stop failure invalidates cleanup and prevents ordinary disposal from racing active work.

On timeout or cleanup failure, the manifest retains producer outcomes and reports `CleanupCompleted = false`, a bounded identifier/type diagnostic, and no complete or release claim. The host does not include exception messages or callback payloads. `DisposeAsync()` cancels and joins an active run; disposal without a run throws `EvidenceHostException` with `ASEVD306` if cleanup fails. When `RunAsync()` already returned its cleanup failure in a manifest, subsequent disposal does not replace that outcome with another exception. If execution throws before a manifest exists, the original exception remains primary and `CleanupDiagnostic` exposes any cleanup failure on the host without copying exception messages or payloads. A null diagnostic before cleanup finishes is not proof of cleanup completion. Repeated disposal does not repeat cleanup. A direct-disposal failure remains a failure on subsequent calls; only a failure already reported by a run is suppressed during later disposal.

The total host budget begins at `RunAsync()`. Consumer-owned startup before that call, artifact upload after it, and an unresponsive runner require CI-level supervision. [The cookbook](../../guides/evidencehost-cookbook.md#bounded-host-cleanup) shows the adoption boundary. Verify the shipped package with the [packed-consumer fixture](../../tests/evidencehost-cleanup-consumer/README.md).

## Pitfalls

- Do not add the EvidenceHost to a normal AppHost or production startup path.
- Do not use a started process as readiness. Wait for the resource condition the producer actually needs.
- Do not swallow cleanup errors. They invalidate the collected evidence rather than leaving a passing claim behind.
- Do not use reflection or assembly scanning to find producers; explicit registration is the trust boundary.

Read next: [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), [planner](../ForgeTrust.AppSurface.Evidence.Planner/README.md), and the [E2E recipe](../../guides/evidencehost-cookbook.md#resource-backed-browser-e2e).
