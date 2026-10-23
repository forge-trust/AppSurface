# ForgeTrust.AppSurface.Evidence.Readiness

`ForgeTrust.AppSurface.Evidence.Readiness` is a .NET 10, BCL-only package for a bounded HTTP readiness condition. It contains no Aspire, EvidenceHost bootstrap, planner, producer, browser, or telemetry dependency. Use it in an ordinary test that already owns an `HttpClient`; Aspire integrations compose the same probe with application startup and resource health.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

For a full executable first use, start with the [standalone Evidence.Readiness consumer](https://github.com/forge-trust/AppSurface/tree/main/tests/evidence-readiness-consumer). Its project is the canonical package-only quickstart, including prerequisites, exact coordinated package-version selection, restore/build/run commands, and recorded output. The [typed Aspire consumer](https://github.com/forge-trust/AppSurface/tree/main/tests/evidence-readiness-typed-consumer) is the complete profile-based example. Run both through the [consumer verification script](https://github.com/forge-trust/AppSurface/blob/main/scripts/verify-evidence-readiness-consumers.sh). A controlled handler proves package behavior; it does not prove that a live endpoint or Aspire resource is ready.

Install the exact coordinated package version selected through the [release guidance](#release-guidance):

```bash
dotnet add package ForgeTrust.AppSurface.Evidence.Readiness --version <selected-exact-version>
```

The coordinated release workflow linked in [release guidance](#release-guidance) publishes packages together. Preview versions and their upgrade expectations follow the [pre-1.0 upgrade policy](https://github.com/forge-trust/AppSurface/blob/main/releases/upgrade-policy.md).

## Choose the smallest readiness layer

| Need | Use | Why |
| --- | --- | --- |
| A URL/status check on an already-owned client and endpoint. | `HttpReadinessProbe.WaitAsync` in this package. | It adds one deadline, bounded retry behavior, and safe readiness diagnostics without taking application ownership. |
| An Aspire resource is ready when its native health condition passes. | [Aspire resource notifications](https://learn.microsoft.com/dotnet/aspire/testing/accessing-resources). | No extra HTTP probe is needed if resource health is the full precondition. |
| Aspire's configured endpoint and status health check is the desired condition. | [Aspire HTTP health checks](https://aspire.dev/reference/api/csharp/aspire.hosting/resourcebuilderextensions/methods/). | Keep the condition in Aspire when it already matches the consumer requirement. |
| A profile-based AppHost needs startup, health, endpoint selection, and HTTP status under one budget. | [`ForgeTrust.AppSurface.Aspire.Testing`](https://github.com/forge-trust/AppSurface/blob/main/Aspire/ForgeTrust.AppSurface.Aspire.Testing/README.md#http-readiness-fixture). | Its fixture composes the existing typed profile builder with this package. |
| A declared EvidenceHost resource needs health plus one HTTP condition before a producer runs. | [`ForgeTrust.AppSurface.Evidence.Aspire`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#http-readiness-adapter). | Its adapter uses the same probe through the existing consumer-owned application lease. |

The HTTP condition proves only the configured status predicate. It does not assert response bodies, browser behavior, credentials, business state, or an EvidenceHost obligation. Put those checks in the owning test or producer. A successful HTTP response is not a claim that the complete product is ready.

## `HttpReadinessProbe.WaitAsync`

The public API is:

```csharp
Task HttpReadinessProbe.WaitAsync(
    HttpClient client,
    Uri endpoint,
    string probePath,
    TimeSpan timeout,
    HttpReadinessOptions? options = null,
    CancellationToken cancellationToken = default);
```

`client` is borrowed: the probe neither disposes it nor changes its `BaseAddress`, timeout, headers, handler, or retry policy. `endpoint` must be an absolute HTTP or HTTPS URI. `probePath` must be a nonempty relative or root-relative path. Resolving it against the endpoint must preserve scheme, IDN host, and effective port. Absolute URLs, network-path references, backslashes, fragments, and paths resolving to another origin are rejected before dispatch.

`timeout` is a required, finite, positive, timer-supported total budget. It includes requests and retry delays. The default options are:

### `HttpReadinessOptions`

| `HttpReadinessOptions` property | Default | Behavior |
| --- | --- | --- |
| `RequestTimeout` | 3 seconds | Maximum for one HTTP request, further shortened by the remaining total budget or the client's own shorter timeout. |
| `RetryDelay` | 250 milliseconds | Delay between retryable failures, shortened to fit the remaining total budget. |
| `IsSuccess` | `status => status == HttpStatusCode.OK` | Synchronous status-only predicate; exact HTTP 200 succeeds. Exceptions from the predicate terminate the operation unchanged. |
| `MaxAttempts` | `null` | No request-count cap; the total deadline alone bounds attempts. When set, it must be positive. Reaching it ends immediately with no trailing delay or extra request. |

Options and predicate references are snapshotted before work begins. Invalid duration or a null predicate fails with standard argument exceptions before an HTTP request. Use a custom predicate only when the consumer's real readiness contract accepts another status. A body-dependent health assertion belongs in the caller, which may issue its own request after this status-only gate.

Each attempt sends GET and completes at response headers. It never reads a response body and disposes every response before returning or retrying. Rejected statuses, `HttpRequestException`, and identifiable request-timeout failures are retried within the original budget. Caller cancellation, an expired total deadline, predicate exceptions, unrelated `OperationCanceledException`, and configuration/programming failures are terminal. The caller's cancellation token wins if cancellation and deadline expiry coincide. A shorter [`HttpClient.Timeout`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.timeout?view=net-10.0) remains in force.

Initial URI validation does not constrain redirects. Redirect, proxy, certificate, and handler behavior remain controlled by the supplied client. The package does not mutate a borrowed client or impose transport policy. If the consumer requires different transport configuration, create and own that client at the call site or use the appropriate Aspire adapter's fresh-client factory.

With immediate rejected responses and the default 250 ms fixed delay, an idealized two-minute budget permits about 480 requests. Real request and callback time reduces that count. Use `MaxAttempts` and caller-owned concurrency limits when several tests share a service; this package does not install a global throttle or resilience policy.

## Deadlines and ownership

One monotonic deadline governs each call. The standalone method starts its own deadline; the typed fixture and Evidence.Aspire adapter pass their original deadline into the shared core, so the HTTP phase never restarts the full allowance. Before reporting success, the operation checks caller cancellation and deadline again. Remaining time also limits request timers and retry delays.

Cancellation is cooperative. Synchronous callbacks cannot be preempted, and a transport or cleanup operation that ignores cancellation can continue after its budget expires. A deadline reports that the budget ended; it does not prove that non-cooperative work or cleanup has stopped.

| Caller | Client owner | Application owner | Success result |
| --- | --- | --- | --- |
| Standalone caller | Caller retains the supplied `HttpClient`. | No application is created or owned. | The task completes after the declared status condition passes. |
| Typed Aspire fixture | The fixture disposes a client returned by its optional factory after its probe settles. | The fixture owns the transferred builder and application lifecycle exactly once. | `Application` and selected `Endpoint` are borrowed for assertions. |
| Evidence.Aspire adapter | Each readiness wait creates and disposes its own fresh client. | The existing `EvidenceAspireApplication` lease remains the single application owner. | The Evidence resource becomes ready only after native health and the HTTP condition pass. |

For the typed fixture, a factory must return an exclusive, active, unbuilt builder. Ownership transfers immediately when it returns, including when cancellation is observed just afterward. If a custom factory allocates resources but throws or fails before returning, that factory cleans its own allocations. Startup failure preserves its primary exception while awaiting builder cleanup. Fixture disposal owns the application-before-activation cleanup order; callers must not build or dispose `Application` independently.

For the Evidence.Aspire adapter, each readiness wait has its own deadline and client. Adapter disposal stops new waits, cancels and drains admitted waits and their client cleanup, then releases its own application registration. Each adapter has a separate registration, so disposing one adapter does not stop the application while another adapter remains registered. Dispose every adapter you create, including adapters that were never waited on. Finish readiness work before directly disposing the `EvidenceAspireApplication` owner. This is adapter-local coordination; it does not claim general EvidenceHost cleanup terminalization.

## `ReadinessException`

Generated readiness failures use `ReadinessException : Exception`. Its immutable properties are:

| Property | Meaning |
| --- | --- |
| `Phase` (`ReadinessPhase`) | `Creation`, `Build`, `Start`, `ResourceHealth`, `EndpointResolution`, `Http`, or `Cleanup`. |
| `Category` (`ReadinessFailureCategory`) | `Deadline`, `AttemptLimit`, `StatusRejected`, `TransportFailure`, `RequestTimeout`, `EndpointUnavailable`, or `CleanupFailure`. |
| `LastCause` (`ReadinessFailureCategory?`) | For `Deadline` and `AttemptLimit`, the most recent retryable HTTP cause (`StatusRejected`, `TransportFailure`, or `RequestTimeout`), or null when no request failed. |
| `LastStatusCode` (`HttpStatusCode?`) | The last rejected numeric HTTP status when the retained cause is `StatusRejected`; otherwise null. |
| `LastHttpRequestError` (`HttpRequestError?`) | The safe .NET transport error enum when the retained cause is `TransportFailure`; otherwise null. |
| `Attempts` (`int`) | Number of dispatched requests. |
| `Problem`, `Cause`, `NextAction` (`string`) | Fixed safe diagnostic fields with no request or raw exception content. |
| `DocumentationUrl` (`string`) | Constant URL to this README's [Troubleshooting](#troubleshooting) section. It does not include a request value or vary by category. |

For `Deadline` and `AttemptLimit`, `LastCause`, `LastStatusCode`, and `LastHttpRequestError` describe only the last applicable observation; the status and transport fields are mutually exclusive and both are null if no HTTP failure was observed. `Attempts` remains zero if no request was dispatched. The standalone probe's call-level argument validation uses standard argument exceptions. Caller cancellation propagates as `OperationCanceledException` with the original caller token. Predicate, factory, builder, and other arbitrary caller failures remain their original exceptions.

Generated readiness messages and fields must not include endpoint or path values, query strings, headers, credentials, response bodies, raw transport messages, raw transport inner exceptions, or arbitrary exception data. The API does not automatically log caller exceptions and does not promise that caller-owned exception text is redacted. Do not copy an arbitrary original exception into a safe readiness field.

`ReadinessException.Data` is normally empty. If typed-fixture startup fails and client or builder cleanup also fails, or an Evidence.Aspire readiness wait fails and its owned client cleanup also fails, the original exception remains primary and receives only `Data["AppSurface.Readiness.CleanupFailure"] = ReadinessFailureCategory.CleanupFailure`; the raw secondary exception is not retained. Redaction checks should allow this fixed enum entry on that dual-failure path.

## Troubleshooting

The exception's fixed [`DocumentationUrl`](#readinessexception) always ends in `#troubleshooting`. The category anchors below are additional reference links for people reading this page; they do not alter the generated URL.

### Exact safe diagnostic templates

`Problem`, `Cause`, and `NextAction` contain these fixed safe values. Braced placeholders contain only a phase enum, numeric HTTP status, `HttpRequestError` enum, or fixed allowlisted cleanup type; they never contain request or exception text.

| Phase and category | `Problem` | `Cause` | `NextAction` |
| --- | --- | --- | --- |
| `Creation`, `Build`, or `Start` + `Deadline` | `Startup deadline expired.` | `Startup phase: {Phase}.` | `Check cancellation observation and host-local logs.` |
| `ResourceHealth` + `Deadline` | `Resource deadline expired.` | `The native resource health condition did not complete.` | `Check the explicit resource identity and configured health condition.` |
| `EndpointResolution`, `Http`, or `Cleanup` + `Deadline` | `Readiness deadline expired.` | `The original operation budget was exhausted.` | `Check the failed phase, retained HTTP cause, and explicit timeout.` |
| `Http` + `AttemptLimit` | `Readiness attempt limit reached.` | `Every permitted request failed its condition.` | `Check the last cause and the explicit attempt cap.` |
| `Http` + `StatusRejected` | `HTTP condition rejected.` | `HTTP status: {numeric status}.` | `Check the path and predicate; check caller authentication for HTTP 401 or 403.` |
| `Http` + `TransportFailure` | `HTTP transport failed.` | `HTTP transport category: {HttpRequestError}.` | `Check reachability and caller proxy or certificate policy.` |
| `Http` + `RequestTimeout` | `HTTP request timed out.` | `The request budget or a shorter client timer expired.` | `Check responsiveness and the explicit request or client limits.` |
| `EndpointResolution` + `EndpointUnavailable` | `Named HTTP endpoint unavailable.` | `The explicit resource or endpoint could not be resolved to HTTP(S).` | `Check the resource name, endpoint name, and allocated endpoint.` |
| `Cleanup` + `CleanupFailure` | `Owned cleanup failed.` | `Owned cleanup exception type: {allowlisted cleanup type}.` | `Inspect owner cleanup without claiming readiness.` |

The cleanup type is one of `InvalidOperationException`, `ObjectDisposedException`, `TimeoutException`, `OperationCanceledException`, `HttpRequestException`, or `AggregateException`; all other types map to `Exception`. Arbitrary exception type names are never included.

The base `Message` uses only `Phase`, `Category`, `Attempts`, `LastCause`, numeric `LastStatusCode`, and `LastHttpRequestError` in this shape: `Readiness failed in {Phase}: {Category}; attempts={Attempts}; last cause={LastCause}; status={numeric status or null}; transport={HttpRequestError or null}.` Caller cancellation has no generated readiness fields: it propagates as `OperationCanceledException` carrying the original caller token. The cancellation recovery guidance below is for the caller and is not attached to or logged by the package.

| Phase/category or failure | Safe diagnostic meaning | Next check |
| --- | --- | --- |
| [`Deadline`](#deadline) during creation, build, or start | Startup deadline; cause names the phase. | Check caller token observation and host-local logs. |
| [`Deadline`](#deadline) during native resource health | Resource deadline; cause names the health condition. | Check explicit resource identity and configured health condition. |
| [`EndpointUnavailable`](#endpoint-unavailable) | The named resource/endpoint was missing, unallocated, or not a valid HTTP(S) endpoint. | Check the exact resource and endpoint names and whether Aspire allocated the endpoint in the selected host context. |
| [`StatusRejected`](#status-rejected) or `LastCause = StatusRejected` | The status predicate rejected the numeric status. | Check the configured path and predicate; check caller authentication for 401 or 403. |
| [`TransportFailure`](#transport-failure) or `LastCause = TransportFailure` | The transport reported a safe `HttpRequestError` enum. | Check resource reachability and the caller's proxy/certificate policy. |
| [`RequestTimeout`](#request-timeout) or `LastCause = RequestTimeout` | The request timer or a shorter client timeout expired. | Check resource responsiveness, request budget, and client timeout. |
| [`AttemptLimit`](#attempt-limit) | The configured request cap was reached; `Attempts` equals the cap and no extra request or delay follows. | Check the retained last cause, then decide whether the consumer needs a larger explicit cap or a longer total deadline. |
| [`CleanupFailure`](#cleanup-failure) | Owned cleanup failed after readiness work. | Inspect the owning lifecycle; do not report the resource as ready. |
| [Caller cancellation](#caller-cancellation) | The caller's original token was canceled; this is an `OperationCanceledException`, not `ReadinessException`. | Start a new operation only when the caller intentionally retries. |
| [Validation](#validation) | Invalid arguments/options fail before dispatch. | Check HTTP(S) endpoint, same-origin path, positive timer bounds, and positive `MaxAttempts`. |
| [Null client factory](#client-factory) | A custom factory did not return a usable fresh client. | Return a non-null, newly owned `HttpClient`; the adapter disposes it after the wait. |
| [Invalid builder state](#builder-state) | A fixture factory returned a builder that is built, disposed, preparing, or not exclusively owned. | Return one active, unbuilt builder and let the fixture own it after transfer. |
| [Predicate failure](#predicate) | The synchronous status predicate threw. | Fix the predicate; it is a caller configuration failure and is not retried or wrapped. |
| [Factory/build failure](#factorybuild) | User-supplied creation or build code threw. | Diagnose at the original call site; the exception remains primary and is not automatically logged by the readiness package. |

### Deadline

For an overall deadline, `Category` is `Deadline`. `Phase` identifies where the shared budget expired. Creation, build, and start use the startup-specific template above; native health uses the resource-specific template. Endpoint-resolution, HTTP, and cleanup deadline failures use the general readiness template. When the failure occurred during HTTP readiness, `LastCause` preserves the most recent retryable HTTP cause.

### Attempt limit

`AttemptLimit` is emitted only when the configured positive `MaxAttempts` is reached before the total deadline wins. It retains the last status, transport, or request-timeout cause and its corresponding scalar field. Caller cancellation or deadline expiration takes precedence; there is no request `MaxAttempts + 1` and no final retry delay.

### Status rejected

The default accepts exactly HTTP 200. A custom predicate receives only `HttpStatusCode`; no body is available. The safe status recipe records the numeric status, identifies that the HTTP condition rejected it, and recommends checking the path/predicate and caller authentication for 401/403. It never includes the URI, headers, or response body.

### Transport failure

Transport diagnostics retain only `HttpRequestError`, not the `HttpRequestException`, its message, or inner exception. Check that the resource is reachable from the test host and review the transport policy supplied by the caller, including proxy and certificate configuration.

### Request timeout

The effective request budget is bounded by `RequestTimeout`, the remaining total deadline, and any shorter `HttpClient.Timeout`. A request timeout is retryable only when it is identifiable as a timeout; unrelated `OperationCanceledException` remains terminal. Check resource response time and explicit request/client limits.

### Endpoint unavailable

The Evidence.Aspire adapter resolves the exact named resource and endpoint through Aspire.Hosting under the shared deadline. Missing or ambiguous resource names, resources without endpoint support, missing endpoint names, and invalid or non-HTTP allocated endpoint values fail before an HTTP request. Check the consumer-composed graph and the selected resource/endpoint pair.

### Cleanup failure

Generated cleanup causes include only a fixed BCL type name: `InvalidOperationException`, `ObjectDisposedException`, `TimeoutException`, `OperationCanceledException`, `HttpRequestException`, `AggregateException`, or the fallback `Exception`. Arbitrary exception type names, messages, inner exceptions, and exception instances are never retained.

Explicit fixture or adapter disposal reports an owned cleanup failure to the caller. If startup itself already failed, the startup failure remains primary while cleanup is awaited. Do not replace it with the secondary cleanup error or claim that deadline expiry proved cleanup completed. For Evidence.Aspire, preserve the existing application lease as the sole application owner; [issue #859](https://github.com/forge-trust/AppSurface/issues/859) tracks separate whole-host cleanup coordination.

### Caller cancellation

Caller cancellation is not transformed into a readiness category. It remains `OperationCanceledException` with the original caller token even when the internal deadline is also expiring. A cancellation token used for one wait must not be reused to disguise an overall deadline as caller intent.

### Validation

Validation runs before acquiring or invoking consumer resources. Pass an absolute HTTP(S) endpoint, an unambiguous relative or root-relative path, finite positive timer-supported durations, a non-null predicate, and a positive attempt cap when configured. The path must preserve scheme, IDN host, and effective port. Redirects after the initial request remain controlled by the chosen client.

### Client factory

An Aspire client factory receives the resolved base endpoint and must return a new, non-null `HttpClient`. It is invoked at most once per fixture startup or Evidence.Aspire wait. Do not return a shared client: the adapter owns and disposes the returned instance. Configure proxy, certificate, redirect, and handler behavior in that factory when the consumer needs custom transport policy.

### Builder state

The typed fixture requires a transferred builder in its active, unbuilt state. Do not build it before returning it, retain another disposer, or return a builder already used by another fixture. The fixture calls `BuildAsync` exactly once and owns the returned application's lifecycle.

### Predicate

The predicate is synchronous, receives only the response status, and executes once for each received response. A thrown exception is terminal and propagates unchanged; it is neither retried nor transformed into `ReadinessException`. Keep business logic and response-body assertions outside this predicate.

### Factory/build

The factory runs once; the fixture does not retry application creation. Failures from caller-supplied creation or build code remain primary and are not automatically logged or promised to be redacted. If a custom factory creates a builder but does not return it, that factory still owns and cleans that allocation. Once the builder is returned, the fixture owns it and awaits its cleanup on later failure.

## Alternatives and further reading

[Aspire resource waiting](https://learn.microsoft.com/dotnet/aspire/testing/accessing-resources) and [Aspire HTTP health checks](https://aspire.dev/reference/api/csharp/aspire.hosting/resourcebuilderextensions/methods/) already cover cases where native health or a configured HTTP condition is sufficient. [Testcontainers wait strategies](https://dotnet.testcontainers.org/api/wait_strategies/) describe the same general practice of waiting for a declared condition under a timeout; they do not replace the typed Aspire profile fixture. For resource-backed EvidenceHost work, see [`ForgeTrust.AppSurface.Evidence.Aspire`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md) and the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md). Multi-resource readiness composition is deferred; see the repository's [deferred design item](https://github.com/forge-trust/AppSurface/blob/main/TODOS.md#multi-resource-readiness-composition). This package is associated with [issue #867](https://github.com/forge-trust/AppSurface/issues/867); planned or equivalent consumer proof is not actual adoption.
