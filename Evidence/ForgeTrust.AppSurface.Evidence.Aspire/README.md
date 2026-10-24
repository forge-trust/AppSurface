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

After execution cancellation or deadline expiry, `EvidenceHostOptions.ProducerCleanupTimeout` gives producers a separate ten-second grace period to stop their children before registrations are disposed. Set a positive value up to one minute when a producer needs a different cleanup bound. This grace never extends successful execution or changes a timed-out result. If any producer remains active after the grace period, cleanup fails and the host preserves registrations rather than deleting a workspace still in use. The consumer then owns recovery of those resources after the producer has actually stopped.

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

## Owned workspaces and command budgets

Use `EvidenceWorkspace.Create(existingParent, options)` for temporary fixtures and installed tools. It allocates a fresh random child and removes only that child on `DisposeAsync`. Keep durable Evidence artifacts outside scratch. The default `EvidenceWorkspaceOptions` allow 512 MiB of logical files, require 256 MiB of free volume space, and bound inspection to 100,000 entries and depth 64. `CheckBudget()` throws `EvidenceWorkspaceBudgetException` on exhausted byte limits and rejects unsupported links or inventory traversal. The exception exposes observed/allowed bytes; it does not grant permission to clean caller-owned files.

`EvidenceCommandRunner.RunAsync` launches explicit argv with an absolute working directory, optional environment overrides and a finite timeout. Combined stdout/stderr capture defaults to 1 MiB (maximum 256 MiB). `EvidenceCommandResult.OutputTruncated` must prevent assertions that require complete output. Timeout and cancellation terminate the owned process tree; stable `EvidenceCommandException.Code` values identify start, timeout, budget, capture, drain or cleanup failure without echoing argv, output or environment values.

Bind a workspace check to the request to enforce capacity while work runs:

```csharp
await using var workspace = EvidenceWorkspace.Create(scratchParent);
var request = new EvidenceCommandRequest(dotnetPath, ["--version"], workspace.RootPath, TimeSpan.FromSeconds(30))
{
    CheckBudget = _ => { workspace.CheckBudget(); return ValueTask.CompletedTask; },
};
var result = await new EvidenceCommandRunner().RunAsync(request, cancellationToken);
```

The runner checks before launch and samples while the process runs with a 250 ms delay between completed checks. Traversal itself takes time; sampling is not a kernel disk quota. A producer must own its workspace until all children stop, implement `IAsyncDisposable`, and let EvidenceHost observe cleanup failure. `GetPath` permits only workspace-relative destinations and rejects existing linked ancestors. Ownership markers and retained timestamps detect basic replacement; these helpers require trusted workspace writers and are not an adversarial filesystem sandbox. Do not concurrently rename or replace their roots.

`EvidenceCommandProducer` adapts a successful command to an explicitly registered assertion evaluator. Nonzero exit, truncated output, undeclared/empty assertions, timeout and budget failure cannot produce successful assertions. Domain checks remain the evaluator's responsibility: exit zero alone does not prove test execution or coverage. Diagnostics stay bounded and secret-safe, but captured output may contain secrets; review/redact it before writing declared artifacts. See the [native validation recipe](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md) for independent package, file and report assertions.

## Verified coverage fan-in

`EvidenceCoverageFanIn.VerifyAsync(request, cancellationToken)` consumes the explicit capture, report and gate artifacts from the [coverage fan-in workflow](../../guides/coverage-fan-in.md). `EvidenceCoverageFanInRequest` requires trusted expected commit, tree, SDK, configuration, workflow run/attempt, solution test-project inventory, primary capture, three native capture directories, composed reports, both gate reports and expected diff base. Inputs must come from a trusted CI channel; supplying a matching self-authored request is not authentication.

The verifier validates bounded manifest/XML inventories and checksums, original collector normalization, full solution execution, and recomputed class/method coverage observations. Primary aggregate and composed patch gates keep their existing 95/85 thresholds, 0.5 tolerance and Codecov patch mode; measurable patch denominators must remain nonempty and cannot decrease. Gate results retain their independent primary measurement. `EvidenceCoverageFanInResult` exposes verified counters and manifest hashes; invalid evidence raises `InvalidDataException` and cannot close an obligation. The native [acceptance tool](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#verified-native-coverage-fan-in) registers this check as an explicit producer.

Read next: [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), [planner](../ForgeTrust.AppSurface.Evidence.Planner/README.md), and the [E2E recipe](../../guides/evidencehost-cookbook.md#resource-backed-browser-e2e).
