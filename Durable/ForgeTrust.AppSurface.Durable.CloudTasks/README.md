# ForgeTrust.AppSurface.Durable.CloudTasks

Optional Google Cloud Tasks transport for one named, payload-free wake to an existing Durable external-activation host. This package owns task construction and a bounded creation receipt. It does not own Work, host authentication, queue administration, outboxes, recovery, or business decisions.

This public preview package depends on the official Google Cloud Tasks client and is intended only for applications that already use Google Cloud and the Durable external-activation contract. It must not be added to neutral Durable, Provider, PostgreSQL, Testing, ASP.NET, CLI, or worker-template projects. See the [Durable package family guide](https://github.com/forge-trust/AppSurface/blob/main/Durable/README.md) and the [canonical Cloud Tasks activation reference](https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md) for when to use this package, complete behavior, diagnostics, decisions, and pitfalls.

## Proof-first quickstart

Use the repository's .NET SDK 10.0.402 with the .NET 10.0.13 runtime. The local SDK transport cases were also verified with .NET 10.0.2. The two-case local fixture proof below exercises a valid successful creation and a response lost after simulated acceptance. It requires no Docker and makes no Google API calls:

```bash
dotnet restore Durable/ForgeTrust.AppSurface.Durable.CloudTasks.Tests/ForgeTrust.AppSurface.Durable.CloudTasks.Tests.csproj --locked-mode
dotnet test Durable/ForgeTrust.AppSurface.Durable.CloudTasks.Tests/ForgeTrust.AppSurface.Durable.CloudTasks.Tests.csproj --no-restore --filter 'FullyQualifiedName~SdkHttp2Tests.OfficialSdkRespectsBoundedTransport'
```

The fixture asserts this synthetic task name: `projects/proof-project/locations/us-central1/queues/proof-queue/tasks/proof-task`.

| Case | Token calls / SDK calls / wire requests | Fixture acceptances | Receipt |
| --- | --- | --- | --- |
| `success` | 1 / 1 / 1 | 1 | `Created/ASCT001` |
| `response-lost` | 1 / 1 / 1 | 1 | `Indeterminate/ASCT305` |

That filtered theory also checks response bounds and protocol-declared pre-acceptance replays. Its assertions verify the counts, exact expected name, and safe receipt without printing credentials. A human should be able to complete this local two-case proof in under five minutes; that is an unmeasured usability target, not a result or release gate.

Run `scripts/verify-cloudtasks-activation.sh` for the broader proof, including reference-host and persisted PostgreSQL Work composition plus packed GCP and neutral consumers. That full path requires Docker for the PostgreSQL host fixture. It is still local-only and does not call Google or publish.

## Install and configure

The supported public namespace is `ForgeTrust.AppSurface.Durable.CloudTasks`. Register one singleton token source before the activation client. For Cloud Run, explicitly select the metadata source:

```csharp
using ForgeTrust.AppSurface.Durable.CloudTasks;

services.AddCloudRunMetadataAccessTokenSource(
    new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(2)));
services.AddCloudTasksActivation(
    new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(20)));
```

For another runtime identity mechanism, implement `ICloudTasksAccessTokenSource` and register exactly one singleton before activation:

```csharp
services.AddSingleton<ICloudTasksAccessTokenSource, WorkloadTokenSource>();
services.AddCloudTasksActivation(
    new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(20)));
```

Custom sources must be thread-safe, honor cancellation, return a bare OAuth access token, and avoid logging it. The adapter validates token syntax and size but does not cryptographically validate a custom token or its lifetime. It never discovers credentials from ambient files or environment state.

Construct explicit immutable target/request values and dispatch through the interface:

```csharp
var target = new CloudTasksActivationTarget(
    projectId: "my-project",
    locationId: "us-central1",
    queueId: "durable-wake",
    workerEndpoint: new Uri("https://worker.example.test/activate"),
    oidcServiceAccountEmail: "tasks-invoker@my-project.iam.gserviceaccount.com",
    audience: "https://worker.example.test/",
    dispatchDeadline: TimeSpan.FromMinutes(2));
var request = new CloudTasksActivationRequest(target, taskId: taskId);
var receipt = await client.DispatchAsync(request, cancellationToken);
```

The caller selects and retains `taskId`. Keep business data out of it. The endpoint audience is explicit and need not equal the endpoint URI. Configure the Cloud Tasks creator OAuth identity separately from the task's worker OIDC identity. The worker host must preserve its own authentication/authorization policy; see the [identity separation and production host requirements](https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#caller-token-versus-worker-oidc-identity).

For a complete GCP-caller and local reference-host simulation example, see the [Cloud Tasks caller example](https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/CloudTasksCallerExample.md). The fixture's development bearer token is test authentication; the fixture does not validate Google's worker OIDC identity.

Registration is singleton and strict. Register exactly one unkeyed singleton source before calling activation. A missing source is allowed during passive `AddCloudTasksActivation` registration but fails clearly when the activation client is explicitly resolved; resolution still does no credential lookup or provider call. Multiple source descriptors, duplicate implementations or instances, scoped/transient sources, repeated metadata or activation extension calls, and a late source registration or source replacement are conflicts. A resolution guard checks that the sole registered source descriptor is still the same one captured at activation registration. Call extensions once and in order; the package does not build a second service provider to validate the graph. The host container disposes package-owned client/transport and metadata HTTP resources; application-provided token-source instances remain application-owned. Allow in-flight calls to finish before disposing the host provider.

## Receipt and recovery

`Created` means the provider returned the exact expected task name. `AlreadyExists` means the name conflicts. Neither proves delivery or completed Work. Rejected results have a definitive no-submission or provider-rejection code. An `Indeterminate` result means a submission may have been accepted; keep the task ID and use the application's authorized recovery process before deciding on another dispatch. Caller cancellation propagates as cancellation and is not evidence that the task was not accepted.

See the [full diagnostic table and recovery guidance](https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#dispositions-and-diagnostic-catalog). Google may accept a mutation even when the caller later observes a deadline or lost response. The adapter has no application-level retries or automatic resubmission.

## Transport replay and bounds

The client makes one SDK invocation and configures no GAX/application retry or hedging. The supported gRPC transport can replay after a protocol-declared HTTP/2 `REFUSED_STREAM`, which guarantees the stream was not accepted. This is a pre-acceptance transport replay only. A fixture observes two request streams for one refusal followed by success (one accepted task), and four request streams for repeated refusal (zero accepted tasks). A response lost after provider acceptance produces `Indeterminate/ASCT305` after one request and one acceptance; it is never retried. A GOAWAY with last-stream-id zero produces four requests, zero acceptances, and `Indeterminate/ASCT305`. These local results were observed with Google.Cloud.Tasks.V2 3.7.0, GAX 4.13.1, Grpc.Net.Client 2.80.0, .NET SDK 10.0.402 with runtime 10.0.13, and separately runtime 10.0.2. They establish the fixture cases, not live Google behavior.

The transport caps received protobuf messages at 64 KiB, aggregate response headers plus trailers at 16 KiB, metadata JSON at 16 KiB, and tokens at 8 KiB. A bounded gzip decoder enforces the decoded protobuf size. Redirects, cookies, ambient credentials, proxy use for metadata, and fallback error-body buffering are disabled. See the [canonical bounds and transport reference](https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#request-projection-and-transport-behavior).

## Local proof

The local HTTP/2 fixture exercises the official client, TLS, explicit per-call token, success, retry-eligible pre-acceptance refusal, repeated refusal, provider unavailability, a lost response after acceptance, GOAWAY, compression bounds, header/trailer bounds, and request projection. The example's fixture delivery is simulated with the reference host's existing test authentication. The broader integration test exercises the actual captured SDK request against the host and separately inspects persisted PostgreSQL Work; its receipt still claims creation evidence only.

Use the repository orchestration script `scripts/verify-cloudtasks-activation.sh` for focused tests and packed consumer proofs. It runs local proofs only and does not call Google or publish packages. Its GCP package consumer is designed to validate the public API and dependency closure. A separate neutral Durable consumer is designed to prove that its restored graph gains no Google Cloud Tasks, GAX, or gRPC dependency, alongside the unaffected baseline graph. These packed proofs remain release gates until their run completes successfully. Package construction and local fixtures do not establish public NuGet availability, live queue IAM, Cloud Run metadata identity, worker OIDC acceptance, effective queue configuration, or real delivery. No live-provider evidence is claimed.

## Adoption and release

This package is opt-in and can be removed by restoring the caller's prior transport while keeping task IDs and application recovery state. Its lifecycle and package-index readiness follow the [coordinated release guidance](#release-guidance). Publication is separate from local package and consumer proof. Keep the generated release-guidance section at the end as the package README's only direct link to the canonical chooser and release hub.

For API shapes, all diagnostic codes, ownership boundaries, unsupported uses, and operator pitfalls, use the [Cloud Tasks activation reference](https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md).

<!-- appsurface-release-guidance: begin -->
## Release Guidance

This package has an explicitly experimental or publication-held contract. Do not
treat it as a normal prerelease install; use the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and
[release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md) for the current publication decision, proof requirements, and
migration guidance.
<!-- appsurface-release-guidance: end -->
