# Cloud Tasks activation

This guide documents the optional `ForgeTrust.AppSurface.Durable.CloudTasks` adapter for submitting one named, empty Google Cloud Tasks wake to an existing Durable external-activation host. It covers the public API, identity boundary, request and response limits, result meanings, recovery, and known pitfalls.

The adapter is a transport for task creation. It does not own accepted Work, worker hosting, application outboxes, scheduling, queue administration, or business recovery. Start with the [external activation reference](external-activation-v1.md) for the worker-side contract and the [Cloud Tasks package README](ForgeTrust.AppSurface.Durable.CloudTasks/README.md) for installation, registration, and the local proof command.
The [Cloud Tasks caller example](../examples/durable-external-activation/CloudTasksCallerExample.md) shows both explicit Cloud Run token-source registration and the simulated reference-host delivery boundary.

## Decision

Use this package when an application already uses an external Durable activation host and wants a shared, explicitly credentialed Google Cloud Tasks creation path. The task contains no application payload; its stable task ID is chosen by the caller. This keeps the queue transport from becoming a second Work store or business command channel.

Use the official Google Cloud Tasks client inside this opt-in GCP package. The Google SDK types and dependencies stay out of the base Durable, Provider, PostgreSQL, Testing, ASP.NET, CLI, and neutral worker packages. The adapter uses an explicitly supplied OAuth access token, not application-default credentials or environment discovery. The task's OIDC worker identity is a separate identity configured on the target.

This package is not the right choice when the application needs queue scheduling, arbitrary request bodies or headers, queue-level retry configuration, dynamic routing, a provider-neutral dispatcher, or automatic recovery. Keep those decisions in the application or choose a transport designed for them. `AlreadyExists` only means the name conflicts; it is not proof of equivalent work or completed work.

## Reference

### Public types

All public types are in namespace `ForgeTrust.AppSurface.Durable.CloudTasks`.

| Type | Contract |
| --- | --- |
| `CloudTasksActivationTarget` | Immutable, constructor-validated project, location, queue, HTTPS endpoint, worker OIDC service-account email, audience, and dispatch deadline. `QueueName` exposes the canonical queue resource name. |
| `CloudTasksActivationRequest` | Immutable target and caller-selected `TaskId`; `TaskName` is derived from the validated queue and ID. It has no payload, schedule, header, or retry-policy fields. |
| `CloudTasksDispatchDisposition` | Closed outcomes: `Created`, `AlreadyExists`, `Rejected`, and `Indeterminate`. |
| `CloudTasksDispatchReceipt` | Expected task name from request intent, disposition, and canonical diagnostic code. The name is never copied from an untrusted provider response. |
| `ICloudTasksAccessTokenSource` | `ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)` supplies a bare OAuth access token for task creation; implementations must be thread-safe singletons that honor cancellation. |
| `ICloudTasksActivationClient` | `DispatchAsync` performs one logical submission and returns bounded creation evidence or propagates caller cancellation. |
| `CloudTasksActivationClientOptions` | Requires a positive finite `SubmissionTimeout`; no implicit value is selected. It covers token acquisition and creation together. |
| `CloudRunMetadataAccessTokenOptions` | Requires a positive finite `RequestTimeout` for the optional Cloud Run metadata source. |

The target constructor is:

```csharp
new CloudTasksActivationTarget(
    projectId,
    locationId,
    queueId,
    workerEndpoint,
    oidcServiceAccountEmail,
    audience,
    dispatchDeadline);
```

The request is constructed with `new CloudTasksActivationRequest(target, taskId)`. Use opaque, uniformly distributed task IDs. Do not put sensitive domain data in resource names.

### Validation

Validation throws an `ArgumentException` family exception with a safe `ParamName`, `Data["CloudTasksValidationCode"]`, and a closed `Data["CloudTasksConstraint"]`. Messages explain the repair and link here without echoing the supplied input or storing it as `ActualValue`. Constructors cannot be bypassed with mutable setters or record updates.

<a id="asctv001"></a>
#### ASCTV001 — Required value

Provide the required target/request/option or a nonblank field. Check the safe `ParamName` and closed constraint; no token lookup or Cloud Tasks invocation has occurred.

<a id="asctv002"></a>
#### ASCTV002 — Syntax

Use the field's documented character set and shape. Remove controls, resource separators, URL credentials/fragments, invalid email syntax, unsupported enum values or invalid receipt/code combinations. A worker deadline must have whole-millisecond precision. The exception does not echo the rejected value.

<a id="asctv003"></a>
#### ASCTV003 — Range

Choose a value within the field bounds below. Both timeout options must be greater than zero and no more than 4,294,967,294 milliseconds, the supported .NET timer range. There is no implicit timeout default.

<a id="asctv004"></a>
#### ASCTV004 — Registration conflict

Register exactly one unkeyed singleton `ICloudTasksAccessTokenSource`, then call `AddCloudTasksActivation` once. Use the metadata extension once before activation as an alternative to a custom source. Missing-source registration remains passive; explicit client resolution fails before credential/provider I/O. Remove duplicate, scoped or transient descriptors and repeated extension calls. This uses safe `InvalidOperationException` plus the same diagnostic metadata keys; supplied configuration values are absent.

Validation rejects blank or oversized values, control characters, segment separators, malformed email syntax, non-HTTPS or malformed endpoints, endpoint user-info or fragments, and invalid task IDs before token lookup or network I/O. Project IDs support documented textual and numeric forms up to 128 characters; queue IDs allow ASCII letters, digits, and hyphens up to 100 characters (see the [queue name contract](https://docs.cloud.google.com/tasks/docs/reference/rest/v2/projects.locations.queues)); locations to 63 characters; task IDs allow ASCII letters, digits, hyphen, and underscore up to 500 characters. The explicit audience is limited to 2,048 characters and the service-account email to 254 ASCII characters. The absolute HTTPS endpoint has a 2,083-character encoded limit, a host, no user-info, and no fragment. A query, if used, is fixed host configuration and must not encode Work data, a scope, tenant, or credential. These are package syntax policies, not live provider checks. They do not prove a location or queue exists, identity ownership, permissions, or effective queue routing. In particular, Google requires the OIDC service account to belong to the queue project and task creation permission including `iam.serviceAccounts.actAs`; compare neither string fragments nor email text to infer ownership. See Google's [OIDC contract](https://docs.cloud.google.com/tasks/docs/reference/rest/v2/OidcToken).

The dispatch deadline is between 15 seconds and 30 minutes inclusive with whole-millisecond precision. The caller owns choosing a unique task ID and retaining it for recovery.

<a id="registration"></a>
### Registration and lifetimes

Register exactly one unkeyed singleton `ICloudTasksAccessTokenSource` before adding the activation client. Or opt into the built-in Cloud Run metadata source, which registers the same singleton contract. Then add the activation client with explicit immutable options:

```csharp
using ForgeTrust.AppSurface.Durable.CloudTasks;

services.AddCloudRunMetadataAccessTokenSource(
    new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(2)));
services.AddCloudTasksActivation(
    new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(20)));
```

For a host-owned source, register it first:

```csharp
services.AddSingleton<ICloudTasksAccessTokenSource, WorkloadTokenSource>();
services.AddCloudTasksActivation(
    new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(20)));
```

The exact supported extension signatures are documented on the package XML API; examples should use the package version being consumed. Registration is opt-in and does not read credentials or contact Google. The activation client and its private reusable SDK channel/HTTP transport are singletons; the metadata source and its separate HTTP client/handler are a singleton pair. The host container owns disposal of package-created services. An application-registered instance token source remains owned by its registering application, following normal Microsoft DI rules. Do not register or dispose the private transport separately. Await in-flight dispatches before disposing the service provider.

Registration must happen once and in order: exactly one unkeyed singleton token source, then one `AddCloudTasksActivation` call. A missing source is passive at registration and fails safely on explicit activation-client resolution. Multiple descriptors, duplicate implementations or instances, scoped/transient sources, repeated metadata registration, and repeated activation registration are rejected before adding package descriptors. A late extra source descriptor fails the resolution guard before any token or provider I/O; do not call `BuildServiceProvider` to validate registrations. These rules make the credential source explicit and deterministic.

### Caller token versus worker OIDC identity

There are two credentials with different purposes:

1. The token source returns the OAuth bearer token authorizing this process to call the Cloud Tasks creation API. Its value is invocation-local, syntax-checked, bounded to 8 KiB, and never logged. The built-in source, when explicitly selected, calls only the documented Cloud Run metadata endpoint with `Metadata-Flavor: Google`; it does not consult files, cache credentials, or switch sources.
2. The target's OIDC service-account email and audience become the identity configuration on the worker request. Cloud Tasks mints the task's OIDC token when delivering the task. This package does not validate that token, establish endpoint authorization, or prove the worker accepted it.

Tokens returned by any source must be 1 to 8,192 ASCII characters matching the [RFC 6750 bearer-token grammar](https://datatracker.ietf.org/doc/html/rfc6750#section-2.1): letters, digits, `-`, `.`, `_`, `~`, `+`, and `/`, followed only by optional trailing `=` padding. Reject whitespace, controls, non-ASCII, embedded padding, and an already prefixed `Bearer ` value. This syntax check does not authenticate a token or prove its expiry. A custom source owns its token lifetime and validity and must cooperate with the linked caller/submission cancellation; the client does not detach a source that ignores cancellation.

For the built-in metadata source, the bounded JSON response must contain one `access_token`, one `token_type` equal to `Bearer` ignoring ASCII case, and one integer `expires_in` from 1 through `Int32.MaxValue`. Duplicate fields, wrong types, malformed JSON, or insufficient lifetime are rejected without a Cloud Tasks request. Elapsed metadata request and read time is subtracted conservatively from `expires_in`; remaining lifetime must be strictly greater than the remaining submission budget plus a five-second safety margin. There is no token cache, credential-file fallback, source switching, or configurable production metadata URI. The metadata request uses the earlier of its configured request timeout and the remaining submission budget.

The worker host retains its own authentication and authorization policy. A local bearer-authenticated fixture does not prove acceptance of Google-issued OIDC. Production composition must validate the Google token in the host or establish a documented platform-ingress identity bridge that satisfies the host policy; ingress by itself does not replace the application's authorization contract.

### Request projection and transport behavior

The adapter projects one validated intent into one Cloud Tasks `CreateTask` request: explicit queue and task names, `POST`, the fixed HTTPS endpoint, the configured OIDC service account and audience, and the dispatch deadline. It leaves the HTTP body unset and requests the `BASIC` response view. It does not set a schedule, task retry policy, arbitrary headers, payload, or queue settings.

The client uses the official generated request model and a reusable TLS `GrpcChannel` to `https://cloudtasks.googleapis.com`, with a package-owned bounded transport and explicitly supplied per-call authorization metadata. There is no ADC lookup, credential builder, emulator auto-discovery, configurable production endpoint, app-level retry, or GAX retry/hedging policy. Each dispatch uses a fresh linked cancellation/deadline and authorization header; shared client state contains no caller token.

The gRPC transport can transparently replay a request only before acceptance when HTTP/2 reports `REFUSED_STREAM` or GOAWAY declares the request stream unprocessed. That pre-acceptance transport behavior is permitted; the client still makes one SDK call, and the replay is neither a GAX retry nor an application retry. As a result, a local fixture may observe multiple HTTP/2 request streams for one SDK invocation while the provider accepts at most one task. This is distinct from retrying after an ambiguous accepted submission, which the adapter never does. See [Google's SDK behavior and the package fixture results](ForgeTrust.AppSurface.Durable.CloudTasks/README.md#transport-replay-and-bounds). The local fixture observes SDK RPC invocations with an interceptor on the real SDK call invoker, separately from HTTP-handler sends and complete wire requests; its counters do not infer SDK calls from the number of dispatches.

The package bounds metadata JSON to 16 KiB, access tokens to 8 KiB, gRPC received protobuf messages to 64 KiB, and aggregate response headers/trailers to 16 KiB. Metadata JSON is read with limit-plus-one detection and a maximum JSON depth of four. Gzip responses are decoded under a 64 KiB limit. Redirects, cookies, proxy use, and automatic content decompression for metadata are disabled. Oversized responses are not buffered as fallback HTML or provider error text. Diagnostics never contain token values, provider messages, exception objects, or raw response metadata.

### Dispositions and diagnostic catalog

A receipt describes whether task creation was accepted or what bounded evidence was available. It never says which Work ran or whether the Work completed. Each code below has a permanent lowercase fragment identifier for stable references. The `package.version` diagnostic tag and activity-source version preserve the NuGet version, including its prerelease suffix; build/source-revision metadata is omitted. They do not use the fixed assembly compatibility version.

<a id="asct001"></a>
#### ASCT001 — Created

- **Operation:** One Cloud Tasks `CreateTask` SDK invocation returned successfully with the exact expected task name.
- **Safe context:** The receipt contains the expected caller-derived task name, `Created`, and this code. It excludes provider message text and task contents.
- **Effect certainty:** Creation acceptance is confirmed. Delivery, Work selection, and Work completion are unknown.
- **Next action:** Observe Work through the application's normal persisted state and recovery process. Do not resubmit the task.
- **Retry policy:** No automatic retry. No further submission is implied by this receipt.

<a id="asct002"></a>
#### ASCT002 — AlreadyExists

- **Operation:** The provider rejected creation because the caller-selected task name already exists or remains reserved.
- **Safe context:** Only the expected task name and this code are returned; the existing task is not fetched or compared.
- **Effect certainty:** This request did not create a new task. The existing or reserved task's equivalence, queue presence, delivery, and Work effect remain unknown.
- **Next action:** Retain the same ID and inspect authoritative application Work state before any manually authorized action.
- **Retry policy:** No automatic retry. A new task ID must not be used to bypass uncertainty.

<a id="asct101"></a>
#### ASCT101 — Token unavailable

- **Operation:** The explicitly selected token source failed, returned an unsuccessful metadata response, or canceled without a caller or budget signal before the Cloud Tasks SDK invocation. Budget expiry is classified separately as [ASCT103](#asct103).
- **Safe context:** Diagnostic event fields, when emitted, contain only operation `token`, stage `acquisition`, a package-owned cause/code, package version, and fixed phase sequence. No token, provider request, or raw exception is exposed.
- **Effect certainty:** No Cloud Tasks request was invoked by this dispatch; this code means task creation was not attempted.
- **Next action:** Repair the configured source or runtime identity, then check whether the application still needs a wake for the retained Work.
- **Retry policy:** No automatic retry. If a wake is still needed after repair, the application may manually dispatch the same retained task ID.

<a id="asct102"></a>
#### ASCT102 — Invalid token

- **Operation:** Token syntax/size validation failed, or built-in metadata token fields, duplicates, types, or remaining lifetime were invalid.
- **Safe context:** Token contents are never copied to diagnostics or exceptions. Cloud Tasks was not invoked.
- **Effect certainty:** No Cloud Tasks request was invoked by this dispatch; task creation was not attempted.
- **Next action:** Correct the explicit token source or metadata identity configuration and verify its bearer token contract.
- **Retry policy:** No automatic retry. After repair and application state review, manually reuse the retained task ID if it is still needed.

<a id="asct103"></a>
#### ASCT103 — Budget expired before submission

- **Operation:** The overall monotonic submission budget expired before the SDK invocation boundary, including while acquiring a token.
- **Safe context:** The receipt reports only the expected task name, `Rejected`, and this code; no request was invoked.
- **Effect certainty:** Cloud Tasks was not called and no task was created by this dispatch.
- **Next action:** Review token-source latency and the caller's configured submission budget, then confirm the pending Work still requires a wake.
- **Retry policy:** No automatic retry. If manually resubmitted after review, use the same retained task ID.

<a id="asct201"></a>
#### ASCT201 — InvalidArgument

- **Operation:** Cloud Tasks returned the definitive RPC status `InvalidArgument` for the creation request.
- **Safe context:** Only the expected task name and stable code are surfaced; provider status details and response metadata are omitted.
- **Effect certainty:** The provider definitively rejected this create request; it did not accept a task from this invocation.
- **Next action:** Correct the target or request configuration and revalidate it locally.
- **Retry policy:** No automatic retry. After correction, manually resubmit with the same retained task ID if the application still requires the wake.

<a id="asct202"></a>
#### ASCT202 — Unauthenticated

- **Operation:** Cloud Tasks returned the definitive RPC status `Unauthenticated`.
- **Safe context:** Provider detail strings, OAuth token values, and headers are not returned.
- **Effect certainty:** The provider definitively rejected this create request.
- **Next action:** Repair the creator OAuth token source and its authorization context; distinguish it from the worker OIDC identity.
- **Retry policy:** No automatic retry. After repair and Work-state review, manually reuse the same task ID if needed.

<a id="asct203"></a>
#### ASCT203 — PermissionDenied

- **Operation:** Cloud Tasks returned the definitive RPC status `PermissionDenied`.
- **Safe context:** Only a stable code is surfaced; IAM details and provider text are not copied.
- **Effect certainty:** The provider definitively rejected this create request.
- **Next action:** Review creator permissions, including `iam.serviceAccounts.actAs`, and the worker service-account project ownership.
- **Retry policy:** No automatic retry. After permission repair and Work-state review, manually reuse the same task ID if needed.

<a id="asct204"></a>
#### ASCT204 — NotFound

- **Operation:** Cloud Tasks returned the definitive RPC status `NotFound`.
- **Safe context:** The receipt retains the caller-derived expected name and omits provider response details.
- **Effect certainty:** The provider definitively rejected this create request.
- **Next action:** Check the configured project, location, and queue, including whether the queue exists and is available.
- **Retry policy:** No automatic retry. After correction and Work-state review, manually reuse the same task ID if needed.

<a id="asct205"></a>
#### ASCT205 — FailedPrecondition

- **Operation:** Cloud Tasks returned the definitive RPC status `FailedPrecondition`.
- **Safe context:** Only the stable code is surfaced; no provider explanation or response metadata is returned.
- **Effect certainty:** The provider definitively rejected this create request.
- **Next action:** Review queue and service-account configuration and satisfy the provider's preconditions.
- **Retry policy:** No automatic retry. After correction and Work-state review, manually reuse the same task ID if needed.

<a id="asct206"></a>
#### ASCT206 — OutOfRange

- **Operation:** Cloud Tasks returned the definitive RPC status `OutOfRange`.
- **Safe context:** Only the expected task identity and stable code are returned.
- **Effect certainty:** The provider definitively rejected this create request.
- **Next action:** Correct the rejected value or provider limit mismatch; do not infer the field from redacted provider text.
- **Retry policy:** No automatic retry. After correction and Work-state review, manually reuse the same task ID if needed.

<a id="asct207"></a>
#### ASCT207 — Unimplemented

- **Operation:** Cloud Tasks returned the definitive RPC status `Unimplemented`.
- **Safe context:** Provider messages and response metadata are omitted.
- **Effect certainty:** The provider definitively rejected this create request.
- **Next action:** Review API endpoint and service compatibility for the deployed provider.
- **Retry policy:** No automatic retry. After compatibility repair and Work-state review, manually reuse the same task ID if needed.

<a id="asct301"></a>
#### ASCT301 — Deadline or budget after invocation

- **Operation:** The SDK call exceeded its RPC deadline, or the overall budget expired after the SDK invocation boundary.
- **Safe context:** The expected task name and package-owned deadline code are returned; provider details are not included.
- **Effect certainty:** Creation is indeterminate because the deadline can occur after the provider commits the mutation.
- **Next action:** Keep the original task ID and reconcile using the application's authorized Work recovery process.
- **Retry policy:** No automatic retry or new task ID. Only after manual reconciliation may the application decide whether to resubmit the same ID.

<a id="asct302"></a>
#### ASCT302 — Unavailable or connection loss

- **Operation:** The provider returned `Unavailable` or the connection was lost after the SDK invocation boundary.
- **Safe context:** The receipt omits provider status text, trailers, and exception details.
- **Effect certainty:** Creation is indeterminate; the request may have been accepted before the connection failed.
- **Next action:** Retain the original task ID and reconcile against authoritative application Work state.
- **Retry policy:** No automatic retry. Any later submission requires application-owned manual recovery and the same ID.

<a id="asct303"></a>
#### ASCT303 — Ambiguous ResourceExhausted

- **Operation:** RPC status `ResourceExhausted` could represent quota or a local response receive limit without reliable provenance.
- **Safe context:** Provider messages are not inspected to guess the source; only the expected identity and stable code are exposed.
- **Effect certainty:** Creation is indeterminate because the error cannot safely establish whether provider acceptance occurred.
- **Next action:** Check bounded application and provider-side evidence without treating exception text as proof.
- **Retry policy:** No automatic retry. Reconcile manually before considering the same retained ID; do not switch to a fresh ID.

<a id="asct304"></a>
#### ASCT304 — Response limit exceeded

- **Operation:** A local response body, protobuf, header, or trailer limit was exceeded while reading the SDK result.
- **Safe context:** No oversized response content or provider detail is returned.
- **Effect certainty:** Creation is indeterminate; the provider may have accepted the task before the response exceeded a local bound.
- **Next action:** Review the bound-specific fixture/transport evidence and provider response shape without weakening limits automatically.
- **Retry policy:** No automatic retry. Reconcile manually with the same retained ID before another dispatch decision.

<a id="asct305"></a>
#### ASCT305 — Other uncertain failure

- **Operation:** A nonfatal, unmapped, `Unknown`, `Aborted`, `Internal`, or `DataLoss` RPC/transport failure occurred after the SDK invocation boundary; protocol-bounded GOAWAY exhaustion also maps here.
- **Safe context:** When the unexpected-failure event is emitted, it contains only package-owned operation, stage, cause, code, package version, and phase sequence. Raw exception, response metadata, and correlation values are not attached.
- **Effect certainty:** Creation is indeterminate; the SDK may have reached the provider or the provider may have committed before the failure.
- **Next action:** Preserve the original task ID and reconcile using application-authorized Work evidence.
- **Retry policy:** No automatic retry. Only after manual reconciliation may the application decide whether to submit the same ID again.

<a id="asct306"></a>
#### ASCT306 — Success response malformed or mismatched

- **Operation:** The SDK returned a missing/malformed success object or a task name different from the expected request-derived name.
- **Safe context:** The receipt never copies the returned name or provider body; it includes only the expected task name and this code.
- **Effect certainty:** Creation is indeterminate; a task may exist even though its response could not be validated.
- **Next action:** Keep the expected task ID and reconcile with the application's authorized Work state.
- **Retry policy:** No automatic retry. Reconcile manually before deciding about another submission with the same ID.

<a id="asct307"></a>
#### ASCT307 — Unrequested RPC cancellation

- **Operation:** The RPC was cancelled after the SDK invocation boundary without a signaled caller token or submission budget. Unrequested token-source cancellation before that boundary is [Rejected/ASCT101](#asct101).
- **Safe context:** Caller-requested cancellation instead propagates a sanitized `OperationCanceledException`; this receipt is only for unrequested cancellation. No provider or exception text is exposed.
- **Effect certainty:** The SDK invocation began; task creation is indeterminate and may have occurred. This code does not describe token acquisition failure before invocation.
- **Next action:** Repair the unexpected RPC cancellation source, retain the same task ID, and reconcile Work state before authorizing another dispatch.
- **Retry policy:** No automatic retry. Preserve the task ID; any later dispatch is manually authorized after reconciliation.

Caller-requested cancellation before or during token/RPC work propagates a sanitized `OperationCanceledException` carrying the caller token, with no receipt code or provider message. A definitive provider rejection or complete validated SDK return wins over cancellation observed only afterward. Cancellation after the SDK invocation boundary is not evidence of non-acceptance. Deadlines and connection loss can occur after provider mutation commit. No disposition automatically resubmits. For uncertain results, the application retains the same task ID and manually reconciles before deciding whether to submit again.

### Activity diagnostics

The package emits an optional `System.Diagnostics.ActivitySource` named `ForgeTrust.AppSurface.Durable.CloudTasks`. Subscribe with an `ActivityListener`; ordinary callback failures are ignored by the adapter, while fatal process exceptions propagate. The activity is `cloudtasks.dispatch.failure` and its event is `cloudtasks.failure`. Package-owned event tags are `operation`, `stage`, `cause`, `code`, `package.version`, and `phase.sequence`. Current safe values include `operation=token|create`, `stage=acquisition|submission`, and `cause=source|rpc|transport`. The phase sequence is `validate>token>failure` for a token-source failure or `validate>token>create>failure` for a create-stage failure. There are no raw exceptions, exception messages, tokens, URLs, task IDs, response metadata, or caller/work correlation tags.

```csharp
using System.Diagnostics;

using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == "ForgeTrust.AppSurface.Durable.CloudTasks",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity =>
    {
        foreach (var item in activity.Events.Where(static item => item.Name == "cloudtasks.failure"))
        {
            var safeFields = item.Tags.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString());
            // Send only this package-owned allowlist to the application's diagnostics sink.
            Console.WriteLine($"Cloud Tasks failure: {safeFields.GetValueOrDefault("code")}");
        }
    }
};
ActivitySource.AddActivityListener(listener);
```

The package currently emits this event for unrequested token-source, RPC, and transport failures. Normal `Created`, `AlreadyExists`, and definitive provider rejection receipts do not produce the unexpected-failure event. Applications should use the stable diagnostic code as a remediation key, not treat the activity as a retry signal.

### Recovery and task IDs

On `Indeterminate`, retain the original task ID and follow the application's existing authorized recovery process before deciding whether to submit again. Do not generate a fresh ID just to bypass an uncertain response, and do not interpret task-name conflict as proof of the intended payload. Cloud Tasks can reserve names after deletion or execution, so stable IDs should be chosen deliberately and sensitive values kept out of IDs.

This adapter does not own an outbox, inspect persisted Work, delete a task, poll for delivery, or resubmit. The application decides whether a pending accepted Work still requires a wake and how it safely proceeds. Duplicate delivery remains possible; application admission and effect safety remain authoritative.

### Pitfalls and limits of evidence

- Syntax validation does not check project, queue, region, identity ownership, IAM, or the queue's effective configuration.
- Queue-level settings may override the requested URI, method, headers, and identity. The receipt reports requested intent and provider acceptance; operators must review queue configuration separately. See Google's [queue HTTP target overrides](https://docs.cloud.google.com/tasks/docs/reference/rest/v2/projects.locations.queues#HttpTarget).
- `Created` is not delivered, executed, or completed. `AlreadyExists` is not content equivalence. `Indeterminate` is not rollback evidence.
- A simulated local delivery fixture proves request composition against the reference host and its test authentication only. It does not prove Google-issued OIDC acceptance, live IAM, Cloud Run metadata availability, or real queue delivery.
- Do not widen response limits or change to handwritten REST if the pinned SDK proof fails. Report the exact failed property before proposing a changed design.
- The package is optional and has no runtime dependency from neutral Durable consumers. Keep Cloud Tasks references out of those graphs.

### Packed package proof and first-party reproducibility

The owner-approved packed-proof contract compares baseline and candidate neutral dependency graphs and framework/dependency contracts. Only the coordinated first-party package version may be normalized; external package IDs, versions, content hashes, and edges remain exact. Its bounded binary normalization permits byte differences only in Core, Flow, Workers, and Durable after proving their authored sources and project files, Flow.Generators sources/projects, and shared build inputs unchanged from the pinned baseline. The proof must retain both raw archive SHA-512 values and library SHA-256 values, require exact library entry sets, and bind each resolved first-party package to its exact validated local archive. It must also retain the complete pinned Cloud Tasks SDK closure, neutral no-provider graph and direct-reference checks, public API leak scans, artifact provenance, and negative controls. A failure withholds successful artifact output. The gate is implemented and the local orchestration passed on 2026-10-27 for `0.2.0-cloudtasksproof.1`: 52 artifacts validated; baseline and candidate neutral graphs each contained 33 nodes; the GCP graph contained 23. The proof report records the raw source, archive and library hashes. This local evidence is tied to those artifacts; each future candidate must pass independently, and no publication approval follows.


Ordinary verification and publication select the candidate committed source snapshot as the paired neutral baseline,
so later committed changes to neutral libraries or shared build inputs do not freeze releases. The source/build-input
parity check still runs against that snapshot; dirty neutral edits fail. This establishes current artifact isolation,
not a comparison with a historical release. For strict historical #892 acceptance, pass a fetched target ref as
`scripts/verify-cloudtasks-activation.sh /tmp/cloudtasks-acceptance origin/main` or use the package tool's
`--cloudtasks-baseline-ref` option. The unique target/candidate merge-base must precede the candidate; missing,
ambiguous, or self-referential history fails without substitution. Reports retain the full baseline commit and mode.
The [package-tool lifecycle reference](../tools/ForgeTrust.AppSurface.PackageIndex/README.md#cloud-tasks-packed-isolation-proof-892)
describes main/tag behavior, explicit historical replay, and the two allowlisted central SDK declarations whose
versions must exactly match the candidate lockfile's direct roots. These baseline choices never remove packed
consumer, graph, contract, raw-hash, source-parity, provenance, or SDK-isolation gates.
