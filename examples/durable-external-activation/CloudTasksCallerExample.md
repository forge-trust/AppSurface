# Cloud Tasks caller example

This example shows how a GCP-targeted caller can compose the optional [Cloud Tasks activation package](../../Durable/ForgeTrust.AppSurface.Durable.CloudTasks/README.md) with an existing external activation host. The sample host and its test project stay provider-neutral; they do not reference the Cloud Tasks SDK or package. For the full contract, diagnostics, bounds, registration ordering, and recovery guidance, see the [Cloud Tasks activation reference](../../Durable/cloudtasks-activation.md).

## Production caller setup

Register the metadata token source explicitly before the activation client. The metadata source obtains the caller's Cloud Run OAuth token for creating the Cloud Task. It is separate from the OIDC identity that Cloud Tasks will use when it dispatches to the worker.

```csharp
using ForgeTrust.AppSurface.Durable.CloudTasks;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddCloudRunMetadataAccessTokenSource(
    new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(2)));
services.AddCloudTasksActivation(
    new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(20)));

using var provider = services.BuildServiceProvider();
var dispatcher = provider.GetRequiredService<ICloudTasksActivationClient>();

var target = new CloudTasksActivationTarget(
    projectId: "123456789",
    locationId: "us-central1",
    queueId: "worker-wakes",
    workerEndpoint: new Uri("https://worker.example/private/durable/activate"),
    oidcServiceAccountEmail: "task-dispatcher@123456789.iam.gserviceaccount.com",
    audience: "https://worker.example",
    dispatchDeadline: TimeSpan.FromSeconds(30));

var request = new CloudTasksActivationRequest(target, taskId: "wake_01JEXAMPLE");
using var cancellation = new CancellationTokenSource();
var receipt = await dispatcher.DispatchAsync(request, cancellation.Token);
```

Use a caller-generated task ID and retain it with the application's outbox or recovery record. A `Created` receipt means that Cloud Tasks returned the expected task name. `AlreadyExists` does not establish task equivalence or current queue presence. `Indeterminate` means the task may have been accepted; keep the task ID and use the application's existing authorized recovery process before deciding whether to submit again. No outcome proves delivery, Work selection, or Work completion.

Production host authentication remains host-owned. The worker endpoint must accept the configured Google-issued OIDC identity through its existing authentication and authorization policy, or a deliberately configured platform-ingress bridge must translate that authenticated identity without bypassing the host's authorization contract.

## Local reference-host simulation

The repository's reference host requires its existing `Authorization: Bearer ...` development credential. The [delivery simulation test](../durable-external-activation.tests/CloudTasksDeliverySimulationTests.cs) takes a recorded task intent, sends an empty `POST` to the real route, and supplies the host's local test bearer credential. It verifies that host authentication, empty-body validation, and the activation service are exercised.

That bridge is **simulated delivery**. It does not validate a Google-issued token, run Cloud Tasks, or prove effective queue configuration or delivery. The existing `Authorized_first_wake_completes_persisted_Work_becomes_ready_and_exports_activation` PostgreSQL lifecycle test separately verifies persisted Work state in [PostgreSqlActivationLifecycleTests.cs](../durable-external-activation.tests/PostgreSqlActivationLifecycleTests.cs). The local test's recording service does not make that persisted-Work assertion.

To run the neutral host and its existing test suite, use the [reference-host README](./README.md). The Cloud Tasks package is added only to a GCP-targeted caller. The approved local Cloud Tasks proof entry point is `scripts/verify-cloudtasks-activation.sh`; it runs the SDK fixtures and packed GCP and neutral consumer graph checks without contacting Google or publishing a package.
