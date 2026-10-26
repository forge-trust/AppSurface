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

After execution cancellation or deadline expiry, `EvidenceHostOptions.ProducerCleanupTimeout` gives producers a separate ten-second grace period to stop their children before registrations are disposed. Set a positive value up to one minute when a producer needs a different cleanup bound. This grace never extends successful execution or changes a timed-out result. If any producer remains active after the grace period, cleanup fails and the host preserves registrations rather than deleting a workspace still in use. The consumer then owns recovery of those resources after the producer has actually stopped.

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

The adapter's disposal closes admission, cancels and drains already admitted waits including their per-wait client cleanup, then releases its registration with the existing application lease. Each health or HTTP adapter retains a separate registration; only the last adapter release stops and disposes the application. Disposing one adapter therefore leaves another adapter's admitted waits and probe clients intact. Dispose every adapter you create, including adapters that were never waited on. Repeated disposal of the final adapter and direct application disposal join the same application cleanup result, including failure. Do not dispose `EvidenceAspireApplication` directly while a readiness wait is active. The deadline remains cooperative: a transport that ignores cancellation and awaited cleanup may remain pending beyond the wait budget. This contract does not claim whole-host timeout terminalization or fold the separate [#859 cleanup coordination](https://github.com/forge-trust/AppSurface/issues/859) into this adapter.

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

## Owned workspaces and command budgets

Use `EvidenceWorkspace.Create(existingParent, options)` for temporary fixtures and installed tools. It allocates a fresh random child and removes only that child on `DisposeAsync`. Keep durable Evidence artifacts outside scratch. The default `EvidenceWorkspaceOptions` allow 512 MiB of logical files, require 256 MiB of free volume space, and bound inspection to 100,000 entries and depth 64. `CheckBudget()` throws `EvidenceWorkspaceBudgetException` on exhausted byte limits and rejects unsupported links or inventory traversal. The exception exposes observed/allowed bytes; it does not grant permission to clean caller-owned files.

Cleanup clears a file's read-only attribute only after verifying the owned root and rejecting links in its bounded inventory. This supports Git objects and installed packages on Windows. Caller-owned files outside that fresh child retain their bytes and attributes; a read-only caller file never grants cleanup authority.

`EvidenceCommandRunner.RunAsync` launches explicit argv with an absolute working directory, optional environment overrides and a positive timeout of at most 4,294,967,294 milliseconds (the cancellation timer limit). Unsupported bounds are rejected before starting a process. Combined stdout/stderr capture defaults to 1 MiB (maximum 256 MiB). `EvidenceCommandResult.OutputTruncated` must prevent assertions that require complete output. Timeout and cancellation use the operating system's process-tree termination through .NET; stable `EvidenceCommandException.Code` values identify start, timeout, budget, capture, drain or cleanup failure without echoing argv, output or environment values. Commands must run in the foreground and keep their children within that lifetime. Deliberately detached daemons are unsupported: once a parent has exited, portable process-tree termination cannot guarantee recovery of escaped descendants. Inherited pipes that remain open fail the bounded drain and cannot produce a complete claim.

Bind a workspace check to the request to enforce capacity while work runs:

```csharp
await using var workspace = EvidenceWorkspace.Create(scratchParent);
var request = new EvidenceCommandRequest(dotnetPath, ["--version"], workspace.RootPath, TimeSpan.FromSeconds(30))
{
    CheckBudget = _ => { workspace.CheckBudget(); return ValueTask.CompletedTask; },
};
var result = await new EvidenceCommandRunner().RunAsync(request, cancellationToken);
```

The runner checks before launch and samples while the process runs with a 250 ms delay between completed checks. Traversal itself takes time; sampling is not a kernel disk quota. A producer must own its workspace until all children stop, implement `IAsyncDisposable`, and let EvidenceHost observe cleanup failure. `GetPath` permits only workspace-relative destinations and rejects existing linked ancestors. Ownership markers and retained metadata detect basic replacement. Linux x64 and ARM64 retain a directory descriptor and compare device/inode identities because its reported creation timestamp can change when children change. Windows retains a no-follow directory handle and compares the volume serial number and all 128 file-ID bits using [FileIdInfo](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info); timestamps and copied marker bytes cannot authorize a different directory object. The internal `EvidenceWorkspaceWindowsIdentity` helper opens existing directories with metadata access, read/write/delete sharing and extended drive/UNC paths; it rejects reparse points, nondirectories and unavailable identity instead of falling back to timestamps. Other platforms retain creation timestamps. Native handles close after successful owned cleanup or failed admission. A rejected cleanup retains its handle so the original can be restored and retried; callers must retain the lease while resolving that failure. Windows can keep an externally deleted directory pending until its handle closes, so replacement tests rename the original before recreating its path. These helpers require trusted workspace writers and are not an adversarial filesystem sandbox. Do not concurrently rename or replace their roots.

Budget inventory can overlap a child deleting workspace descendants. On Windows, the internal `EvidenceWorkspace.MeasureWithRetry` repeats a complete measurement at most three times, with 10 ms between attempts, only for exact access-denied (`0x80070005`), sharing-violation (`0x80070020`) and delete-pending (`0x8007012F`) HRESULTs from filesystem access. Each attempt validates the owned root first and validates it again after a successful measurement. Partial results are discarded; a persistent failure still fails the check. Ownership validation, inventory bounds, byte limits and free-space checks are never retried or waived. Friend assemblies exercise this boundary with explicit measurement and validation delegates; production supplies the actual bounded inventory and operating system. Generic command budget failures retain the stable `budget_check_failed` code and report only a fixed callback phase (`before_start`, `during_execution` or `after_execution`) and numeric HRESULT. Original exception text, custom type names and inner exceptions are not retained. The [packed storage journey](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md) includes that safe message in its failure diagnostic.

`EvidenceCommandProducer` adapts a successful command to an explicitly registered assertion evaluator. Nonzero exit, truncated output, undeclared/empty assertions, timeout and budget failure cannot produce successful assertions. Domain checks remain the evaluator's responsibility: exit zero alone does not prove test execution or coverage. Diagnostics stay bounded and secret-safe, but captured output may contain secrets; review/redact it before writing declared artifacts. See the [native validation recipe](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md) for independent package, file and report assertions.

## Verified coverage fan-in

`EvidenceCoverageFanIn.VerifyAsync(request, cancellationToken)` consumes the explicit capture, report and gate artifacts from the [coverage fan-in workflow](../../guides/coverage-fan-in.md). `EvidenceCoverageFanInRequest` requires trusted expected commit, tree, SDK, configuration, workflow run/attempt, solution test-project inventory, primary capture, three native capture directories, composed reports, both gate reports and expected diff base. Inputs must come from a trusted CI channel; supplying a matching self-authored request is not authentication.

The verifier validates bounded manifest/XML inventories and checksums, rejects duplicate JSON object properties, verifies original collector normalization and full solution execution, and recomputes class/method coverage observations. Primary aggregate and composed patch gates keep their existing 95/85 thresholds, 0.5 tolerance and Codecov patch mode; measurable patch denominators must remain nonempty and cannot decrease. Gate results retain their independent primary measurement. `EvidenceCoverageFanInResult` exposes verified counters and manifest hashes; invalid evidence raises `InvalidDataException` and cannot close an obligation. The native [acceptance tool](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#verified-native-coverage-fan-in) registers this check as an explicit producer.

Read next: [Evidence contracts](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md), [planner](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md), and the [E2E recipe](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md#resource-backed-browser-e2e).

If a readiness wait fails and disposing its owned probe client also fails nonfatally, the original readiness exception stays primary. Only the fixed `Data["AppSurface.Readiness.CleanupFailure"] = ReadinessFailureCategory.CleanupFailure` marker is added on a best-effort basis; no raw secondary exception is retained. A nonfatal client cleanup failure after an otherwise successful condition produces a safe `Cleanup` / `CleanupFailure` diagnostic. Fatal client cleanup or diagnostic-storage failures (`OutOfMemoryException`, `StackOverflowException`, `AccessViolationException`, or `AppDomainUnloadedException`) propagate unchanged after per-wait bookkeeping is restored, following the host's existing fatal-exception policy. Explicit adapter disposal still joins and reports the existing lease's original cleanup failure. See the [diagnostic contract](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#readinessexception).

During explicit HTTP adapter disposal, admitted waits and clients drain before application-registration release. If cancellation callbacks or registration cleanup contain a process-fatal failure, including inside a nested `AggregateException`, every disposal caller observes the same original fatal exception. Multiple nonfatal cleanup failures retain their aggregate result. This is adapter-local failure handling; it does not change the host-wide cleanup contract.

The same nested-aggregate classification applies when a per-wait client is disposed or secondary diagnostic storage throws: an original fatal leaf takes precedence over ordinary readiness failures and safe cleanup summaries. The shared [neutral readiness diagnostic contract](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#readinessexception) retains the adapter's existing fatal-type policy.

The internal `EvidenceBoundedReadStream` shares cumulative read-size and cancellation enforcement between fan-in JSON and XML readers. It owns and disposes its input, supports synchronous and asynchronous reads, and rejects seeking or writing. File-size preflight alone is insufficient because a file may grow after inspection; each read rechecks the same limit. This helper is an internal verification seam, not an additional public API.

Workspace admission tests use the internal `EvidenceWorkspace.CreateWithAdmissionHooksForTests` factory to inject a failure after fresh-root allocation or after retaining its identity, before marker creation. It executes the same creation and rollback path as `Create`; its callbacks never replace filesystem observations or cleanup decisions. Use this seam only in friend-assembly tests that verify the original exception, owned-empty-root cleanup, and preservation of unknown entries or partial markers. Application code uses `Create` and supplies no hooks.

Fan-in JSON rejects duplicate property names at every object depth, including objects nested in arrays. Ambiguous policy, identity, receipt or gate fields cannot be accepted by choosing one occurrence. This applies to all untrusted JSON evidence parsed by the verifier.
