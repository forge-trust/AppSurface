# AppSurface Web

The **ForgeTrust.AppSurface.Web** package provides the bootstrapping logic for building ASP.NET Core applications using the AppSurface module system. It sits on top of the compilation concepts defined in `ForgeTrust.AppSurface.Core`.

## Overview

The easiest way to get started is by using the `WebApp` static entry point. This provides a default setup that works for most applications.

```csharp
await WebApp<MyRootModule>.RunAsync(args);
```

For more advanced use cases where you need to customize the startup lifecycle beyond what the options provide, you can extend `WebStartup<TModule>`.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## Key Abstractions

### `WebApp`

The primary entry point for web applications. It handles creating the internal startup class and running the application. It provides a generic overload `WebApp<TModule>` for standard usage and `WebApp<TStartup, TModule>` if you have a custom startup class.

### `IAppSurfaceWebModule`

Modules that want to participate in the web startup lifecycle should implement this interface. It extends `IAppSurfaceHostModule` and adds web-specific hooks:

*   **`ConfigureWebOptions`**: Modify the global `WebOptions` (e.g., enable MVC, configure CORS).
*   **`ConfigureWebApplication`**: Register pre-routing middleware using `IApplicationBuilder` (e.g., exception handling or package-owned static-file middleware that must run before routing).
*   **`ConfigureEndpointAwareMiddleware`**: Register middleware that must run after routing has selected an endpoint and before endpoints execute (e.g., root/host-owned `app.UseAuthentication()` and `app.UseAuthorization()`).
*   **`ConfigureEndpoints`**: Map endpoints using `IEndpointRouteBuilder` (e.g., `endpoints.MapGet("/", ...)`).

### `WebStartup`

The base class for the application bootstrapping logic. While `WebApp` uses a generic version of this internally, you can extend it if you need deep customization of the host builder or service configuration logic.

## Features

### PWA application badging

AppSurface Web includes a default-off, privacy-safe browser rail for application-icon badge requests:

```csharp
options.Pwa.Badging.Enabled = true;
```

With `<appsurface:pwa-head />` in the page head, applications can call `AppSurface.Pwa.badging.set(count)` or `.clear()`. Successful calls resolve to `"accepted"` or `"unsupported"`; invalid counts and native failures reject with sanitized `ASPWAJS040`–`ASPWAJS042` errors. `"accepted"` means only that the native request resolved and never proves a visible icon badge. When offline or push already activates the shared worker, the same API is installed in that worker after normal service-worker activation. Badging alone does not create a worker, own an attention count, request permission, change the push payload, or add CLI verification.

Start with the [PWA badging quick start](Docs/pwa-install.md#badging-only), then use the [executable accessible proof](../../examples/web-pwa-install/README.md). The full guide documents configuration, sanitized `ASPWAJS040`–`042` failures, PathBase, activation lag, privacy boundaries, and unsupported-browser behavior.

### Health and Readiness Probes

AppSurface Web maps public platform probe endpoints by default:

- `/health` runs every registered ASP.NET Core health check.
- `/ready` runs only checks tagged with `AppSurfaceHealthCheckTags.Ready`.

Both endpoints return minimal plain-text aggregate status (`Healthy`, `Degraded`, or `Unhealthy`), set no-store headers,
and return `200` only for `Healthy`. `Degraded` and `Unhealthy` return `503` so Kubernetes, Aspire, Docker, and similar
platforms can remove an instance from traffic. The endpoints are excluded from API Explorer/OpenAPI by default because
they are platform probes, not application API operations.

If no checks are tagged with `AppSurfaceHealthCheckTags.Ready`, `/ready` reports `Healthy` once the web app has started.
Add ready-tagged checks for dependencies that must be available before the app receives traffic.

#### 3-minute health and readiness path

Register normal ASP.NET Core health checks from your module or host. Tag startup-critical checks with
`AppSurfaceHealthCheckTags.Ready`:

```csharp
using ForgeTrust.AppSurface.Web;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class MyRootModule : IAppSurfaceWebModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck(
                "database",
                () => HealthCheckResult.Healthy("Database connection accepted."),
                tags: [AppSurfaceHealthCheckTags.Ready]);
    }
}
```

Run the app and verify the default routes:

```bash
curl -i http://127.0.0.1:5000/health
curl -i http://127.0.0.1:5000/ready
curl -I http://127.0.0.1:5000/ready
```

Use the endpoints as platform probes:

```yaml
livenessProbe:
  httpGet:
    path: /health
    port: http
  periodSeconds: 10
  timeoutSeconds: 2
readinessProbe:
  httpGet:
    path: /ready
    port: http
  periodSeconds: 5
  timeoutSeconds: 2
```

Customize or disable the routes through `WebOptions.Health`:

```csharp
await WebApp<MyRootModule>.RunAsync(
    args,
    options =>
    {
        options.Health.HealthPath = "/internal/live";
        options.Health.ReadyPath = "/internal/ready";
        // options.Health.Enabled = false; // Use this when the host owns probe endpoints directly.
    });
```

Important behavior:

- Probe paths must be app-root-relative paths without route parameters, query strings, fragments, whitespace, or
  traversal segments.
- `HealthPath` and `ReadyPath` must be distinct after normalization.
- AppSurface fails startup if a module or `WebOptions.MapEndpoints` already mapped the configured probe path. Move the
  existing endpoint, change `WebOptions.Health` paths, or disable AppSurface health endpoints.
- The endpoints are intentionally public and unauthenticated so infrastructure probes can reach them. Keep them behind
  normal platform controls such as cluster-local networking, ingress allowlists, or rate limiting when they are exposed
  beyond private service traffic.
- Public probes expose only aggregate status. Use authenticated support surfaces, such as Config audit diagnostics, for
  detailed operator evidence.
- The product-readiness lab's `/readiness` endpoint is an example-local report endpoint. It is separate from the reusable
  AppSurface Web `/ready` platform probe contract.

### Named Canary Endpoints

Named canaries are a preview primitive for protected, application-owned deploy evidence. They are not liveness,
readiness, general diagnostics, dogfood approval, customer readiness, or launch approval. Keep platform traffic decisions
on the [`/health` and `/ready` probes](#health-and-readiness-probes). A named canary answers one narrower question once:
does the application currently hold acceptable proof for this registered workflow, deploy marker, and freshness boundary?

AppSurface owns registration, the fixed protected route, request validation, and status mapping. Your application owns
the proof query. The deploy caller triggers synthetic work before evaluation and owns polling, retries, and total timeout.
Issue [#624](https://github.com/forge-trust/AppSurface/issues/624) adds a bounded evidence envelope and fixed
completion telemetry. It constrains shape and exposure; it does not classify or redact application-authored text. Issue
[#625](https://github.com/forge-trust/AppSurface/issues/625) adds caller-side polling, and
[#626](https://github.com/forge-trust/AppSurface/issues/626) adds a neutral end-to-end example. Until that operator rail is
proved, this API remains preview. A separate [aggregate snapshot follow-up](https://github.com/forge-trust/AppSurface/issues/645)
will compile multiple checks with bounded concurrency and deadlines after the safe envelope exists; #623 evaluates one
registered name per request.

Choose the surface by the question you need to answer:

| Use | Question | Lifetime and audience |
|---|---|---|
| ASP.NET Core health checks through `/health` or `/ready` | Is this process and its required dependencies healthy enough for infrastructure traffic decisions? | Ongoing, public/minimal platform probes. |
| AppSurface named canary | Does this protected, registered workflow have acceptable proof for this marker and freshness boundary right now? | One authenticated evaluation for deploy operators or automation. |

Named canaries do not replace ASP.NET Core health checks, do not affect `/ready`, and ship no health-check adapter. Do not
point a liveness or readiness probe at the canary route.

#### Named canary time-to-first-success paths

Budget **under 5 minutes** when the host already has authentication and an operator policy. Budget **under 15 minutes**
for the cold path where the Web host must first add its host-owned scheme and policy using the
[AppSurface Auth adoption ladder](../../Auth/ForgeTrust.AppSurface.Auth/README.md). Named canaries never install an
authentication scheme or define who an operator is. Both clocks assume the application already has a proof reader; the
server primitive evaluates existing proof and never triggers the workflow.

Start from this compile-only evaluator skeleton. Replace its placeholder result with an application-owned lookup that
uses `context.Marker` and `context.FreshSince`; the evaluator must inspect existing proof and must not trigger the workflow:

<!-- appsurface:snippet id="appsurface-canary-evaluator" file="Web/ForgeTrust.AppSurface.Web.Tests.SharedErrorPagesFixture/NamedCanaryPublicApiFixture.cs" marker="appsurface-canary-evaluator" lang="csharp" -->
```csharp
using ForgeTrust.AppSurface.Web;

public sealed class ForwardingCanaryEvaluator : IAppSurfaceCanaryEvaluator
{
    public const string ProofKindDetailKey = "proof.kind";

    public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken)
    {
        // Compile-only placeholder: query application-owned proof using context.Marker and context.FreshSince.
        return ValueTask.FromResult(
            new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pending));
    }
}
```
<!-- /appsurface:snippet -->

The placeholder always returns `Pending` and is not a working deploy proof. Replace it with your application-owned query:
return `Pass` when matching proof at or after the requested freshness boundary is acceptable, `Pending` while matching proof
may still arrive, `Fail` for a completed negative outcome, `Stale` for proof older than the boundary, and `NotConfigured` when
the proof dependency is intentionally unavailable. This complete fixture shows the application boundary: `IForwardingProofReader`
reads existing proof and returns already-classified, bounded evidence; the evaluator only translates that decision into the
package contract.

<!-- appsurface:snippet id="appsurface-canary-forwarding-complete" file="Web/ForgeTrust.AppSurface.Web.Tests.CanaryConsumerFixture/CanaryConsumerFixture.cs" marker="appsurface-canary-forwarding-complete" lang="csharp" -->
```csharp
/// <summary>Describes application-owned forwarding proof returned by a protected proof store.</summary>
/// <param name="Status">The named-canary status derived by the application.</param>
/// <param name="ObservedAt">The time the proof was observed, when meaningful.</param>
/// <param name="MatchedCount">The number of matching proof records.</param>
/// <param name="ReasonCode">The stable, operator-safe reason code.</param>
/// <param name="Summary">The bounded, operator-safe summary.</param>
/// <param name="CorrelationId">The non-secret application correlation identifier.</param>
public sealed record ForwardingProofSnapshot(
    AppSurfaceCanaryStatus Status,
    DateTimeOffset? ObservedAt,
    int MatchedCount,
    string ReasonCode,
    string Summary,
    string CorrelationId);

/// <summary>Reads existing forwarding proof without triggering the workflow under evaluation.</summary>
public interface IForwardingProofReader
{
    /// <summary>Finds proof for the exact marker and freshness boundary supplied by the deploy caller.</summary>
    /// <param name="marker">The required opaque, non-secret deploy marker.</param>
    /// <param name="freshSince">The required proof freshness boundary.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The application-owned proof decision and bounded evidence.</returns>
    ValueTask<ForwardingProofSnapshot> ReadAsync(
        string marker,
        DateTimeOffset freshSince,
        CancellationToken cancellationToken);
}

/// <summary>Evaluates existing forwarding proof without triggering forwarding itself.</summary>
public sealed class CompleteForwardingCanaryEvaluator(IForwardingProofReader proofReader) : IAppSurfaceCanaryEvaluator
{
    /// <summary>The application-owned detail key shared by registration and result construction.</summary>
    public const string ProofKindDetailKey = "proof.kind";

    /// <inheritdoc />
    public async ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken)
    {
        var proof = await proofReader.ReadAsync(
            context.Marker!,
            context.FreshSince!.Value,
            cancellationToken);

        return new AppSurfaceCanaryResult(
            proof.Status,
            result =>
            {
                result.ObservedAt = proof.ObservedAt;
                result.MatchedCount = proof.MatchedCount;
                result.ReasonCode = proof.ReasonCode;
                result.Summary = proof.Summary;
                result.CorrelationId = proof.CorrelationId;
                result.AddDetail(ProofKindDetailKey, "forwarding");
            });
    }
}
```
<!-- /appsurface:snippet -->

The existing `new AppSurfaceCanaryResult(status)` constructor remains valid and produces no optional evaluator evidence.
The callback overload is construction-only. AppSurface validates it and snapshots the options and details before the result becomes
observable; later mutations cannot change the result. Prefer the typed fields. Use details only for a small, stable,
operator-safe token that does not fit a typed field. Keep raw provider payloads and deep diagnostics in an application-owned,
protected system.

A separate public consumer fixture exercised by endpoint tests also uses a contrasting non-message case to keep forwarding-specific
concepts out of the typed contract:

```csharp
public sealed class MigrationCompletionCanaryEvaluator : IAppSurfaceCanaryEvaluator
{
    public const string MigrationKindDetailKey = "migration.kind";

    public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            new AppSurfaceCanaryResult(
                AppSurfaceCanaryStatus.Pass,
                result =>
                {
                    result.ObservedAt = DateTimeOffset.UtcNow;
                    result.MatchedCount = 1;
                    result.ReasonCode = "migration-complete";
                    result.Summary = "The expected schema migration is complete.";
                    result.AddDetail(MigrationKindDetailKey, "schema");
                }));
}
```

Register the evaluator and require the inputs your proof query needs. Registration alone exposes no route:

<!-- appsurface:snippet id="appsurface-canary-registration" file="Web/ForgeTrust.AppSurface.Web.Tests.SharedErrorPagesFixture/NamedCanaryPublicApiFixture.cs" marker="appsurface-canary-registration" lang="csharp" -->
```csharp
public void ConfigureServices(StartupContext context, IServiceCollection services)
{
    services.AddAuthorization(options =>
        options.AddPolicy("DeployOperators", policy => policy.RequireAuthenticatedUser()));

    services.AddAppSurfaceCanary<ForwardingCanaryEvaluator>(
        "forwarding.alpha-evidence",
        canary =>
        {
            canary.RequireMarker();
            canary.RequireFreshSince();
            canary.AllowedDetailKeys.Add(ForwardingCanaryEvaluator.ProofKindDetailKey);
        });
}
```
<!-- /appsurface:snippet -->

After registering the application's `IForwardingProofReader` implementation with its normal host-owned lifetime, register
the complete evaluator and declare every custom result key in that same callback:

```csharp
services.AddAppSurfaceCanary<CompleteForwardingCanaryEvaluator>(
    "forwarding.alpha-evidence",
    canary =>
    {
        canary.RequireMarker();
        canary.RequireFreshSince();
        canary.AllowedDetailKeys.Add(CompleteForwardingCanaryEvaluator.ProofKindDetailKey);
    });
```

Declarations are isolated per canary and snapshotted after registration. Duplicate declarations are idempotent. An
undeclared returned key fails the evaluation closed as `ASCAN301`; AppSurface never silently drops it.

Run host-owned authentication and authorization in the endpoint-aware phase, then map the fixed route family explicitly:

<!-- appsurface:snippet id="appsurface-canary-mapping" file="Web/ForgeTrust.AppSurface.Web.Tests.SharedErrorPagesFixture/NamedCanaryPublicApiFixture.cs" marker="appsurface-canary-mapping" lang="csharp" -->
```csharp
public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
{
    endpoints.MapAppSurfaceCanaries("DeployOperators");
}
```
<!-- /appsurface:snippet -->

Evaluate one registered name with the dedicated headers and an operator credential:

```bash
curl --fail-with-body \
  -H "Authorization: Bearer $DEPLOY_OPERATOR_TOKEN" \
  -H "X-AppSurface-Canary-Marker: deploy-42" \
  -H "X-AppSurface-Canary-Fresh-Since: 2026-07-12T12:00:00Z" \
  https://app.example.com/_appsurface/canaries/forwarding.alpha-evidence
```

The completed response contains a required compatibility core and omits optional evidence that is absent:

```json
{
  "name": "forwarding.alpha-evidence",
  "ready": false,
  "status": "pending",
  "markerFingerprint": "sha256:8f3e39850c0f5251fc6d36845f3d69a4cb12963e96e9ee95c935b33b68d893f6",
  "freshSince": "2026-07-12T12:00:00.0000000Z",
  "matchedCount": 0,
  "reasonCode": "proof-not-observed",
  "summary": "No fresh matching forwarding proof observed yet.",
  "details": {
    "proof.kind": "forwarding"
  },
  "correlationId": "deploy-20260716-004006"
}
```

`name`, `ready`, and `status` are always present. `ready` is only a projection of `status == "pass"`; it does not mean
the process is ready for traffic. `markerFingerprint` is present only when a validated marker was supplied and is
`sha256:` plus the lowercase SHA-256 digest of the exact UTF-8 marker. It is a correlation aid, not anonymization: a
low-entropy marker can be guessed offline. Never put a secret, token, email address, personal identifier, or private
content in a marker.

`freshSince` comes from the validated request. `observedAt` comes from the evaluator and does not default to request time.
Both are normalized to UTC and use exactly seven fractional digits. The other evaluator-owned fields and `details` are
omitted when absent; an empty details collection is omitted.

#### Status and HTTP contract

`MapAppSurfaceCanaries` maps one GET route, `/_appsurface/canaries/{name}`, for the whole named registry. It is excluded
from API Explorer/OpenAPI and every package-owned response sets `Cache-Control: no-store` and `Pragma: no-cache`.

| Evaluator status | Meaning for a caller | Typical caller action | Default HTTP status |
|---|---|---|---:|
| `pass` | Current proof is acceptable. | Continue the deploy decision. | `200` |
| `pending` | Acceptable proof may still arrive. | Wait and retry within the caller-owned deadline. | `503` |
| `fail` | Current proof demonstrates failure. | Stop and surface the bounded reason; do not retry blindly. | `503` |
| `stale` | Proof exists but predates the requested boundary. | Check the trigger/marker boundary before retrying. | `503` |
| `not-configured` | The registered evaluator cannot inspect an intentionally unavailable proof dependency. | Fix host configuration or deliberately skip this workflow outside the server primitive. | `503` |

The default `AppSurfaceCanaryCompletedResponseMode.StatusCode` makes deployment checks work without parsing JSON.
Authenticated diagnostic/reporting consumers can explicitly opt into `AlwaysOk`; they must then parse `status` because
every completed result returns `200`:

<!-- appsurface:snippet id="appsurface-canary-always-ok" file="Web/ForgeTrust.AppSurface.Web.Tests.SharedErrorPagesFixture/NamedCanaryPublicApiFixture.cs" marker="appsurface-canary-always-ok" lang="csharp" -->
```csharp
public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
{
    endpoints.MapAppSurfaceCanaries(
        "DeployOperators",
        options => options.CompletedResponseMode = AppSurfaceCanaryCompletedResponseMode.AlwaysOk);
}
```
<!-- /appsurface:snippet -->

This option changes only the HTTP status. It never changes the evaluator contract or JSON. Clients should parse JSON,
use `status` as the decision field, and apply these preview compatibility rules:

- Require valid `name`, `ready`, and `status` fields. They are the compatibility core.
- Accept any optional field being absent and ignore fields added by a future package version.
- Parse by property name. The package writes deterministic output for reproducibility, but property and detail order are
  not a wire compatibility guarantee.
- Do not infer `ready` from HTTP status when `AlwaysOk` is enabled, and do not reinterpret `ready` as platform readiness.

This additive contract is intentionally unversioned while preview. The [#625 caller](https://github.com/forge-trust/AppSurface/issues/625)
must follow these rules and will prove the server-to-operator polling path before the named-canary API leaves preview.

#### Parse the envelope and choose an action

Use `System.Text.Json` to validate the required core by property name. This public-fixture parser accepts reordered JSON,
absent optional fields, and future unknown fields. It rejects missing or malformed `name`, `ready`, or `status` fields and
rejects a `ready` projection that disagrees with `status`.

<!-- appsurface:snippet id="appsurface-canary-consumer" file="Web/ForgeTrust.AppSurface.Web.Tests.CanaryConsumerFixture/CanaryConsumerFixture.cs" marker="appsurface-canary-consumer" lang="csharp" -->
```csharp
using System.Text.Json;

namespace ForgeTrust.AppSurface.Web.Tests.CanaryConsumerFixture;

/// <summary>Lists the next operator action selected from a semantic named-canary envelope.</summary>
public enum CanaryOperatorAction
{
    /// <summary>Continue the deployment decision after acceptable proof.</summary>
    Continue,

    /// <summary>Wait for proof that may still arrive.</summary>
    Wait,

    /// <summary>Refresh stale proof or its trigger boundary.</summary>
    Refresh,

    /// <summary>Configure the unavailable proof dependency.</summary>
    Configure,

    /// <summary>Investigate a completed negative outcome.</summary>
    Investigate,

    /// <summary>Roll back after a known migration-integrity failure.</summary>
    RollBack,
}

/// <summary>Represents the required compatibility core and selected operator action parsed by a public consumer.</summary>
/// <param name="Name">The exact registered canary name.</param>
/// <param name="Status">The defined lowercase wire status.</param>
/// <param name="Ready">The compatibility projection of whether <paramref name="Status"/> is <c>pass</c>.</param>
/// <param name="ReasonCode">The optional response-only machine reason.</param>
/// <param name="Action">The operator action selected from the semantic envelope.</param>
public sealed record CanaryConsumerResult(
    string Name,
    string Status,
    bool Ready,
    string? ReasonCode,
    CanaryOperatorAction Action);

/// <summary>Parses named-canary JSON by semantic field name without depending on property order or optional fields.</summary>
public static class CanaryEnvelopeConsumer
{
    private static readonly HashSet<string> DefinedStatuses =
    [
        "pass",
        "pending",
        "fail",
        "stale",
        "not-configured",
    ];

    /// <summary>Parses and validates the required named-canary compatibility core.</summary>
    /// <param name="json">The non-null JSON envelope to parse.</param>
    /// <returns>The validated core fields and selected operator action.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">A required field is absent, invalid, or semantically inconsistent.</exception>
    public static CanaryConsumerResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The named-canary envelope must be a JSON object.");
        }

        var name = ReadRequiredString(root, "name");
        var status = ReadRequiredString(root, "status");
        if (!DefinedStatuses.Contains(status))
        {
            throw new JsonException("The named-canary status is not recognized.");
        }

        if (!root.TryGetProperty("ready", out var readyProperty)
            || readyProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new JsonException("The named-canary ready projection is required and must be Boolean.");
        }

        var ready = readyProperty.GetBoolean();
        if (ready != string.Equals(status, "pass", StringComparison.Ordinal))
        {
            throw new JsonException("The named-canary ready projection does not match status.");
        }

        var reasonCode = root.TryGetProperty("reasonCode", out var reasonProperty)
            && reasonProperty.ValueKind == JsonValueKind.String
                ? reasonProperty.GetString()
                : null;

        return new CanaryConsumerResult(name, status, ready, reasonCode, SelectAction(status, reasonCode));
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new JsonException($"The named-canary {propertyName} is required and must be a nonblank string.");
        }

        return property.GetString()!;
    }

    private static CanaryOperatorAction SelectAction(string status, string? reasonCode) => status switch
    {
        "pass" => CanaryOperatorAction.Continue,
        "pending" => CanaryOperatorAction.Wait,
        "stale" => CanaryOperatorAction.Refresh,
        "not-configured" => CanaryOperatorAction.Configure,
        "fail" when string.Equals(reasonCode, "checksum-mismatch", StringComparison.Ordinal) =>
            CanaryOperatorAction.RollBack,
        "fail" => CanaryOperatorAction.Investigate,
        _ => throw new JsonException("The named-canary status is not recognized."),
    };
}
```
<!-- /appsurface:snippet -->

Shell automation can apply the same semantic checks with `jq`; property order and unknown fields do not affect this filter:

```bash
curl --silent --show-error \
  -H "Authorization: Bearer $DEPLOY_OPERATOR_TOKEN" \
  -H "X-AppSurface-Canary-Marker: deploy-42" \
  -H "X-AppSurface-Canary-Fresh-Since: 2026-07-12T12:00:00Z" \
  https://app.example.com/_appsurface/canaries/forwarding.alpha-evidence |
jq -er '
  if (.name | type) != "string" or (.name | length) == 0 then error("invalid name")
  elif (.ready | type) != "boolean" then error("invalid ready")
  elif (.status | type) != "string"
    or (.status as $status | ["pass", "pending", "fail", "stale", "not-configured"] | index($status)) == null
    then error("invalid status")
  elif .ready != (.status == "pass") then error("ready does not match status")
  else {
    name,
    status,
    reasonCode: (.reasonCode // null),
    action: (
      if .status == "pass" then "continue"
      elif .status == "pending" then "wait"
      elif .status == "stale" then "refresh"
      elif .status == "not-configured" then "configure"
      elif .reasonCode == "checksum-mismatch" then "roll-back"
      else "investigate"
      end)
  }
  end'
```

With the default response mode, do not add `--fail` when the script needs to parse non-pass envelopes because their `503`
status is intentional. Use `curl --fail-with-body` only when HTTP status alone should fail the deploy step.

#### Upgrade from the #623 status-only envelope

Existing evaluators that return `new AppSurfaceCanaryResult(status)` still compile unchanged. The successful response is
additive: #624 adds required `ready` and may add bounded optional evidence, while `name` and `status` keep their meaning.

| #623 response | #624 response and consumer change |
|---|---|
| `{ "name": "forwarding.alpha-evidence", "status": "pending" }` | `{ "name": "forwarding.alpha-evidence", "ready": false, "status": "pending" }` plus optional evidence when supplied. |
| Read `name` and `status`. | Require `name`, `ready`, and `status`; verify `ready == (status == "pass")`. |
| No additive evidence existed. | Tolerate absent optional fields, ignore unknown fields, and never depend on property order. |

No registration, route, or evaluator migration is required for status-only producers. Consumers must make the required-core
update before reading #624 responses. The API remains preview until the [#625 caller](https://github.com/forge-trust/AppSurface/issues/625)
proves this contract across the operator path.

#### Result evidence reference

`AppSurfaceCanaryResult.Status` is required. The construction callback can set these optional evaluator-owned fields:

| Field | Contract | Intended use |
|---|---|---|
| `ObservedAt` | Any representable `DateTimeOffset`; normalized to UTC. | Time the proof itself was observed. Leave absent when no meaningful observation time exists. |
| `MatchedCount` | Non-negative integer. | Number of candidate proofs considered or matched. |
| `ReasonCode` | 1-64 lowercase ASCII letters, digits, or internal hyphens; starts and ends alphanumeric. | Stable machine branching such as `proof-not-observed`. |
| `Summary` | Nonblank, at most 256 UTF-8 bytes, well-formed Unicode, no Unicode control (`Cc`) scalar. | Short operator-safe explanation. |
| `CorrelationId` | 1-128 ASCII characters from letters, digits, `.`, `_`, `:`, or `-`; starts and ends alphanumeric. | Non-secret application/deploy correlation token. |
| `Details` | Up to 16 declared string pairs, exposed as an immutable, ordinally sorted dictionary. | Small application-specific operator tokens that do not belong in the typed fields. |

`AddDetail(key, value)` validates each key and value immediately. Keys are 1-64 lowercase ASCII characters in
dot-separated segments; segments start and end alphanumeric and may contain internal hyphens. Values are nonblank, at
most 128 UTF-8 bytes, well-formed Unicode, and contain no Unicode control (`Cc`) scalar. Duplicate result keys or a 17th
unique result detail throw `InvalidOperationException`. Null callbacks, keys, or values throw `ArgumentNullException`.
Invalid keys or values throw `ArgumentException` for `key` or `value`; invalid scalar options discovered after the callback
throw `ArgumentException` for `configure`. Exceptions thrown by application callback code propagate unchanged and no
partial result is created.

`AllowedDetailKeys` accepts at most 16 unique keys with the same key grammar. Invalid declarations, including a 17th
unique key, make registration fail atomically with `ASCAN101` as an `ArgumentException` for `configure`; the service
collection is not partially changed. Returning a syntactically valid key that this canary did not declare is different:
construction succeeds, then descriptor-specific validation rejects the evaluation as safe `ASCAN301` with no partial
envelope.

Bounds make transport size and log behavior predictable; they do not make text semantically safe. Never place email
addresses, provider identifiers or URLs, credentials or tokens, prompts, model output, source text, child names, parent
emails, private generated content, exception messages, or raw provider/domain payloads in `Summary`, `CorrelationId`, or
`Details`. `AllowedDetailKeys` is a pre-authorized shape, not classification, redaction, or data-loss prevention.

#### Authoring-time validation rescue

These failures happen when application code constructs a result directly during authoring or startup, or registers a
canary. They are distinct from endpoint diagnostics because no endpoint evaluation is running. The same construction
failure thrown inside `EvaluateAsync` occurs under the runtime boundary and is reported as safe `ASCAN301` instead.

| Failure | Exception and parameter | Cause | Fix |
|---|---|---|---|
| Missing result callback | `ArgumentNullException`, `configure` | The callback overload received `null`. | Pass a non-null construction callback or use the status-only constructor. |
| Invalid scalar option | `ArgumentException`, `configure` | A count is negative, a reason/correlation value violates its grammar, or summary/Unicode bounds are invalid. | Correct the value using the limits in the result evidence reference. |
| Invalid detail key or value | `ArgumentNullException` or `ArgumentException`, `key`/`value` | A key/value is null, blank, malformed, contains a control scalar, or exceeds its UTF-8 bound. | Use a declared 1-64 character key and bounded nonblank value of at most 128 UTF-8 bytes. |
| Duplicate or 17th result detail | `InvalidOperationException` | Result construction reused a key or exceeded 16 unique details. | Return each declared key once and keep at most 16 details. |
| Invalid allowed-key declaration | `ArgumentException`, `configure`; diagnostic `ASCAN101` | Registration declared an invalid key or exceeded 16 unique keys. | Correct the registration callback; registration remains atomic. |

A syntactically valid result key that was not declared is a runtime, canary-specific mismatch. Result construction succeeds,
then evaluation fails closed as `ASCAN301` with no partial envelope. Declare the exact shared constant in that canary's
`AllowedDetailKeys` or remove the returned detail.

#### Completion telemetry

After one evaluator result passes validation and before response serialization, AppSurface emits one source-generated
`Information` event: ID `62401`, name `AppSurfaceCanaryEvaluationCompleted`. It contains only the fixed typed fields
`CanaryName`, `CanaryStatus`, `Ready`, `ObservedAt`, `FreshSince`, `MatchedCount`, `ElapsedMilliseconds`,
`ApplicationName`, and `EnvironmentName`. Elapsed time covers evaluator activation, invocation, and result validation.
Missing or blank host application/environment names are normalized to `unknown` and do not prevent route mapping.

The event never contains the raw marker, marker fingerprint, reason code, summary, correlation ID, detail keys or values,
exception message, response body, or evaluator domain payload. The response-only fields stay response-only even when a
logging provider could accept them. Use ambient logging scopes and OpenTelemetry resource attributes for revision/build
metadata, and ambient `Activity` context for request correlation; #624 does not define a build identity API.

Evaluation activation, invocation, null-result, result-validation, or undeclared-key failures continue through redacted
event `62301` and `ASCAN301`, with no completion event. Request-abort cancellation emits neither event. If response writing
fails after a valid completion, the completion event remains accurate and the write exception flows to host diagnostics.

#### Registration and request reference

- Names are 1-128 lowercase characters in dot-separated segments. A segment starts and ends with a letter or digit and
  may contain internal hyphens, for example `forwarding.alpha-evidence`. Matching is exact and ordinal.
- `DisplayName` defaults to the registered name, `Description` defaults to `null` and is limited to 512 characters, and
  `Tags` is an ordinal set of 1-64 character lowercase letter/digit/internal-hyphen values. These values are immutable
  registry metadata and are not returned by the endpoint. `AllowedDetailKeys` is also immutable registry metadata, but
  it declares response shape rather than describing the registration.
- `RequireMarker()` and `RequireFreshSince()` are idempotent. Optional inputs still reach the evaluator when supplied.
- Marker values are opaque: AppSurface preserves the value delivered by ASP.NET Core, including internal whitespace,
  but HTTP clients and servers may normalize leading or trailing field-value whitespace. Avoid whitespace-significant
  markers. Malformed Unicode, control characters, and repeated values are rejected, the maximum is 256 UTF-8 bytes, and a blank optional
  marker becomes `null`.
- Freshness accepts strict RFC 3339 timestamps with seconds, a `Z` or numeric offset, and zero to seven fractional digits.
  It rejects surrounding whitespace, offset-free values, and invalid calendar or offset values, then normalizes to UTC.
- `AddAppSurfaceCanary<TEvaluator>` uses a host registration of the concrete evaluator when one already exists. Otherwise
  it adds the evaluator as transient. Singleton, scoped, and transient overrides are supported; one request resolves the
  concrete evaluator exactly once from its request scope.
- An unknown route name returns `404`. `not-configured` is different: the name is registered and its evaluator ran.
- One request performs one evaluation. Request abort cancellation propagates. The package adds no evaluator timeout,
  retry, polling loop, cache, fan-out, trigger, or readiness effect.

#### Authorization and route sharp edges

Mapping requires a nonblank host-owned policy and registered ASP.NET Core authorization services. Native policy
resolution remains authoritative, including dynamic policy providers. Authentication and `UseAuthorization()` must run
after `UseRouting()` and before the endpoint executes; AppSurface's
[`ConfigureEndpointAwareMiddleware`](#key-abstractions) hook is the intended phase. A missing policy or missing/misordered
authorization middleware fails closed with `500` before evaluator invocation. Native schemes still own challenges,
forbids, and cookie redirects.

Never append `AllowAnonymous` to the returned `RouteHandlerBuilder` or remove its required named-policy metadata. The
adapter detects either weakening and returns a safe `ASCAN113` `500` before name lookup or evaluation. Map named canaries
at the application root, not through a route group: the route must remain exactly `/_appsurface/canaries/{name}`. The
entire `/_appsurface/canaries` namespace is reserved while the mapper is active; startup fails with `ASCAN115` if the
framework route is relocated or a literal, parameterized, constrained, or controller route exists at that path or below
it. Routes outside that exact namespace, such as `/_appsurface/canaries-extra`, are unaffected.

#### Diagnostic rescue table

Problem responses contain only fixed `title`, `status`, `code`, `problem`, `cause`, `fix`, and `docsLink` fields. The
canary adapter uses package-owned JSON settings rather than the host-global ProblemDetails customizer, so host callbacks
cannot append trace identifiers or request-derived fields. Responses never echo marker/freshness values, registered-name
inventories, exception messages, stacks, or trace identifiers. Host-local logs for evaluation failures contain only a
stable event, canary name, diagnostic code, and exception type.

| Code | Problem | Likely cause | Fix |
|---|---|---|---|
| `ASCAN101` | Invalid registration | Name, display metadata, description, tag grammar, allowed-detail grammar, or the 16-key declaration limit is invalid. | Correct the registration callback before building the host. |
| `ASCAN102` | Duplicate name | More than one registration used the same exact name. | Keep one registration per name. |
| `ASCAN111` | Invalid mapping policy | The supplied policy name is blank. | Pass a nonblank host-owned policy name. |
| `ASCAN112` | No registrations | The route was mapped without any named canaries. | Register at least one typed evaluator first. |
| `ASCAN113` | Unsafe/missing authorization | Authorization services are absent, anonymous metadata bypassed the policy, or the required named-policy metadata was removed. | Register authorization, remove `AllowAnonymous`, and retain the mapped named policy. |
| `ASCAN114` | Repeated mapping | The fixed route family was mapped more than once. | Map it exactly once. |
| `ASCAN115` | Fixed/reserved route conflict | Mapping through a route group relocated the fixed route, or a host endpoint overlaps the canary namespace. | Map on the application root and move host routes outside `/_appsurface/canaries`. |
| `ASCAN116` | Invalid response mode | The mapper callback assigned an undefined enum value. | Choose `StatusCode` or `AlwaysOk`. |
| `ASCAN201` | Required header missing | A registration-required marker or freshness value is blank/absent. | Supply the named header and retry. |
| `ASCAN202` | Header invalid | A header is repeated, malformed, unsafe, or too large. | Follow the marker or strict freshness rules above. |
| `ASCAN203` | Canary not found | The exact route name is not registered. | Register it or correct the lowercase name. |
| `ASCAN301` | Evaluation failed | Activation failed, the evaluator threw/canceled independently, returned null, returned invalid result state, or returned a detail key not declared for this canary. | Correct the evaluator or declare the bounded key; inspect host-local evaluator diagnostics because rejected values remain redacted. Caller retry policy remains external. |

### PWA Install and Push-Worker Foundation

AppSurface Web provides independent [PWA install, offline, and push-worker capabilities](Docs/pwa-install.md) from `WebOptions.Pwa`: a manifest endpoint, MVC/Razor head tags, development diagnostics, an explicit starter offline strategy, safe default notification handlers, and an inert registration helper. Every capability is disabled by default. Enabling push does not request permission, create a subscription, choose recipients, or send a notification. Add the optional [`ForgeTrust.AppSurface.Web.Push`](../ForgeTrust.AppSurface.Web.Push/README.md) package for protected subscription intake, VAPID, encrypted one-attempt sending, and stale-subscription cleanup while retaining app-owned custody and product policy.

#### 3-minute PWA install path

Configure install metadata in the existing Web package:

```csharp
await WebApp<MyRootModule>.RunAsync(
    args,
    options =>
    {
        options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
        options.Pwa.Enabled = true;
        options.Pwa.Name = "Contoso Field Notes";
        options.Pwa.ShortName = "Field Notes";
        options.Pwa.ThemeColor = "#2563eb";
        options.Pwa.BackgroundColor = "#ffffff";
        options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-192.png", Sizes = "192x192", Type = "image/png" });
        options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-512.png", Sizes = "512x512", Type = "image/png" });
    });
```

Add the head helper to your MVC layout:

```cshtml
<appsurface:pwa-head />
```

Run the app on HTTPS or localhost, then verify the install contract:

```bash
appsurface pwa verify --base-url https://app.example.com --entry-path /account/resume --expect-display standalone --expect-icon 192x192 --expect-icon 512x512
```

`Pwa.Enabled` controls install metadata only. `Pwa.Offline.Enabled` and `Pwa.Push.Enabled` independently activate one shared worker, so an existing PWA can adopt push handlers without replacing its install experience. Push-only mode installs no fetch listener and creates no cache. When push is enabled, the head helper loads an external script exposing `window.AppSurface.Pwa.register()`; the application decides when to call it.

For push-only and combined quick starts, the complete option/payload/helper contract, ownership boundaries, migration guidance, browser caveats, and CLI proof flow, see [PWA install and push-worker support](Docs/pwa-install.md). For executable combined proof, run [examples/web-pwa-install](../../examples/web-pwa-install/README.md).

### Middleware Lifecycle

AppSurface Web keeps middleware and endpoint mapping in three explicit phases:

1. `ConfigureWebApplication` runs before static files, routing, AppSurface CORS, and endpoint execution. Use it for middleware that does not need endpoint metadata.
2. `ConfigureEndpointAwareMiddleware` runs after `UseRouting()` and AppSurface-managed CORS, before endpoint execution. At request time, middleware here can inspect `HttpContext.GetEndpoint()`, route values, and endpoint metadata for matched endpoints.
3. `ConfigureEndpoints` maps module endpoints. `WebOptions.MapEndpoints` maps direct host endpoints in the same endpoint-routing phase.

Global ASP.NET Core authentication and authorization middleware should be registered once from the root or host integration module:

```csharp
public sealed class MyRootModule : IAppSurfaceWebModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddAuthentication(/* scheme configuration */);
        services.AddAuthorization();
    }

    public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
```

The root/host module's endpoint-aware middleware runs before dependency feature modules, so feature middleware can rely on root-owned authentication having already run. Feature modules should only register endpoint-aware middleware they own; they should not each install global authentication or authorization middleware. Apps that need bespoke pipeline policy beyond AppSurface's module hooks can use a custom `WebStartup<TModule>`.

Endpoint-aware middleware must tolerate `HttpContext.GetEndpoint()` being `null` for unmatched requests. Do not call `UseRouting`, `UseCors`, `UseEndpoints`, or map endpoints from this hook.

### MVC and Controllers

Support for MVC approaches can be configured via `WebOptions`:

*   **None**: For pure Minimal APIs (default).
*   **Controllers**: For Web APIs using controllers (`AddControllers`).
*   **ControllersWithViews**: For traditional MVC apps with views.
*   **Full**: Full MVC support.

### CORS
Built-in support for CORS configuration:
*   **Enforced Origin Safety**: When `EnableCors` is true, you MUST specify at least one origin in `AllowedOrigins`, unless running in Development with `EnableAllOriginsInDevelopment` enabled (the default). If `AllowedOrigins` is empty in production or when `EnableAllOriginsInDevelopment` is disabled, the application will throw a startup exception to prevent unintended security openness (verified by tests `EmptyOrigins_WithEnableCors_ThrowsException` and `EnableAllOriginsInDevelopment_AllowsAnyOrigin`). A literal origin wildcard (`["*"]`) is also rejected outside Development for AppSurface-managed CORS; use explicit browser origins such as `["https://app.example.com"]`.
*   **Development Convenience**: `EnableAllOriginsInDevelopment` (enabled by default) automatically allows any origin when the environment is `Development`. When `AllowedHeaders` and `AllowedMethods` are empty or omitted, the `DefaultCorsPolicy` also allows any header and method for local convenience; configured values keep those header and method restrictions enforced even in Development.
*   **Origin Wildcards Are Not All The Same**: `AllowedOrigins = ["*"]` means every origin and is only available through the development compatibility path. `AllowedOrigins = ["https://*.example.com"]` is ASP.NET Core wildcard-subdomain matching and remains supported. Host binding wildcards such as `http://*:5000` decide which network interfaces Kestrel listens on; they do not make browser CORS responses public.
*   **Header and Method Control**: `AllowedHeaders` and `AllowedMethods` default to empty arrays in production, so AppSurface does not silently allow every preflight header and method once CORS is enabled. Set each collection to the browser contract the app actually supports, for example `["Content-Type", "X-Request-Id"]` and `[HttpMethods.Get, HttpMethods.Post]`. Use `["*"]` only when a production app intentionally wants `AllowAnyHeader()` or `AllowAnyMethod()`.
*   **Default Policy**: Configures a policy named "DefaultCorsPolicy" (configurable) and automatically registers the CORS middleware.

```csharp
await WebApp<MyRootModule>.RunAsync(
    args,
    options =>
    {
        options.Cors.EnableCors = true;
        options.Cors.AllowedOrigins = ["https://app.example.com"];
        options.Cors.AllowedHeaders = ["Content-Type", "X-Request-Id"];
        options.Cors.AllowedMethods = [HttpMethods.Get, HttpMethods.Post];
    });
```

The production default is deliberately stricter than older AppSurface previews: `AllowedOrigins` decides which browser
frontends may read cross-origin responses, while `AllowedHeaders` and `AllowedMethods` decide which preflighted browser
requests are accepted from those origins. Keep them explicit for production APIs, especially when credentials are
allowed. Migrate older permissive production configuration from:

```csharp
options.Cors.EnableCors = true;
options.Cors.AllowedOrigins = ["*"];
```

to:

```csharp
options.Cors.EnableCors = true;
options.Cors.AllowedOrigins = ["https://app.example.com"];
```

For an intentionally public API that must own wildcard CORS in production, do not use the AppSurface-managed policy for
that surface. Register and apply the host's ASP.NET Core CORS policy directly so the public contract is visible in the
application's own startup code.

### Endpoint Routing

Modules can define their own endpoints, making it easy to slice features vertically ("Vertical Slice Architecture").

### Config Audit HTTP Diagnostics

`MapAppSurfaceConfigAuditDiagnostics` maps an authenticated `GET` endpoint that returns the active host's sanitized
`ConfigAuditReport` JSON. The endpoint is support-sensitive operator evidence capture: it can expose provider names,
configuration keys, source paths, diagnostics, and redaction metadata even though values are already sanitized by
`ForgeTrust.AppSurface.Config`.

The endpoint is never mapped automatically. The default route is
`/_appsurface/config/audit` (`AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute`), and hosts can pass a custom route
when their operations model keeps diagnostics under a different path.

#### Config Audit HTTP in 5 minutes

Add the Config module, configure normal ASP.NET Core authentication and authorization, install the authorization
middleware, then opt in to the endpoint:

```csharp
public sealed class MyRootModule : IAppSurfaceWebModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
    }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddAuthentication(/* host scheme */);
        services.AddAuthorization(options =>
        {
            options.AddPolicy("ConfigAuditRead", policy => policy.RequireAuthenticatedUser());
        });
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAppSurfaceConfigAuditDiagnostics("ConfigAuditRead");
    }
}
```

Capture the report only under your support policy:

```bash
curl -H "Authorization: Bearer $SUPPORT_TOKEN" \
  https://app.example.com/_appsurface/config/audit \
  > production.config-audit.json
```

Compare captured host snapshots later with the Config package's captured-snapshot diff workflow:
[Config diff in 10 minutes](../../Config/ForgeTrust.AppSurface.Config/README.md#config-diff-in-10-minutes).

Important behavior:

- Mapping validates a non-null endpoint builder plus non-blank route and policy names at startup.
- The endpoint maps `GET` only, applies `RequireAuthorization(policyName)`, and is hidden from API Explorer/OpenAPI by
  default with `ExcludeFromDescription()`.
- Successful responses and AppSurface-owned setup/runtime failures set `Cache-Control: no-store` and `Pragma: no-cache`.
- Missing Config services, missing environment services, blank environment names, reporter failures, and report JSON
  failures return safe `500` ProblemDetails with `problem`, `cause`, `fix`, and `docsLink` extensions.
- Native ASP.NET Core authorization still owns challenge and forbid behavior. Cookie hosts may redirect on unauthorized
  requests unless the host's auth scheme is configured for API-style `401`/`403` responses.
- Missing authorization policies or missing authorization middleware fail closed through ASP.NET Core's own runtime
  behavior before the handler runs.
- AppSurface Web does not add rate limiting. Put host-owned rate limiting, network controls, audit logging, or break-glass
  workflow around this route when support access requires it.

For custom JSON options, ProblemDetails shape, discovery behavior, rate limiting, or auth response formatting, write a
host-owned `MapGet` endpoint over `IConfigAuditReporter` instead of using this package mapper.

### Browser Status Pages

AppSurface Web includes conventional browser-facing pages for empty `401`, `403`, and `404` status responses. The feature is designed for human browser requests: it keeps the original HTTP status code, shows recovery-oriented HTML, and leaves JSON/API responses alone.

#### Browser status pages in 2 minutes

If your app already uses MVC views, keep the default `Auto` mode:

```csharp
public void ConfigureWebOptions(StartupContext context, WebOptions options)
{
    options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
}
```

If your app starts with controllers only but still wants the browser pages, opt in explicitly:

```csharp
public void ConfigureWebOptions(StartupContext context, WebOptions options)
{
    options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.Controllers };
    options.Errors.UseConventionalBrowserStatusPages();
}
```

Preview the built-in pages while the app is running:

| Status | Preview URL |
|--------|-------------|
| `401` | `/_appsurface/errors/401` |
| `403` | `/_appsurface/errors/403` |
| `404` | `/_appsurface/errors/404` |

Override any page with the conventional app or shared Razor Class Library path:

| Status | Override path |
|--------|---------------|
| `401` | `~/Views/Shared/401.cshtml` |
| `403` | `~/Views/Shared/403.cshtml` |
| `404` | `~/Views/Shared/404.cshtml` |

Use `BrowserStatusPageModel` in overrides:

```cshtml
@model ForgeTrust.AppSurface.Web.BrowserStatusPageModel

<h1>HTTP @Model.StatusCode</h1>
<p>@Model.OriginalPath</p>
```

Mode behavior:

| Mode | Behavior |
|------|----------|
| `BrowserStatusPageMode.Auto` | Enables browser status pages only when MVC support already includes views. |
| `BrowserStatusPageMode.Enabled` | Always enables browser status pages and upgrades MVC support to controllers with views when needed. |
| `BrowserStatusPageMode.Disabled` | Leaves status-code handling fully to the app or other middleware. |

Important behavior:

- Only empty `401`, `403`, and `404` responses from `GET` or `HEAD` requests that accept `text/html` or `application/xhtml+xml` are re-executed.
- JSON, non-HTML, non-empty, and non-GET/HEAD responses keep their original API-friendly behavior.
- The framework fallback is app-agnostic. It includes a generic home recovery link and does not inspect product-specific route families such as `/docs`.
- Apps that need domain-specific recovery, such as documentation search for stale docs links, should add `~/Views/Shared/404.cshtml` and render links from their own route contracts.
- Static export remains conservative: RazorWire CLI probes `/_appsurface/errors/404` and writes only `404.html`; it does not emit `401.html` or `403.html`. In CDN mode, that `404.html` page is validated and rewritten with the rest of the static output. The fallback `Return home` link is marked `data-rw-export-ignore` so apps that do not export `/` can still publish a valid conventional `404.html`.
- Production `500` exception pages are intentionally separate from browser status pages and must be enabled with `UseConventionalExceptionPage()`.

### Conventional Production 500 Pages

AppSurface Web can also own a safe browser-facing production 500 page for unhandled exceptions. This is off by default because exception handling is usually application policy. Opt in through `WebOptions.Errors.UseConventionalExceptionPage()` when a normal MVC-style app wants a generic HTML failure page without writing exception middleware by hand.

```csharp
await WebApp<MyRootModule>.RunAsync(
    args,
    options => options.Errors.UseConventionalExceptionPage());
```

The conventional exception page uses ASP.NET Core exception handling, not status-code pages. That distinction matters: status-code pages can render empty `404` or `500` responses, but they do not catch thrown exceptions. AppSurface registers the exception handler early enough to catch module middleware and endpoint failures, then renders a Razor view only for requests that accept `text/html` or `application/xhtml+xml`.

- The default view returns HTTP `500`, generic recovery copy, a home link, and a request id that operators can correlate with logs.
- The default `ExceptionPageModel` contains only `StatusCode` and `RequestId`; it does not expose exception messages, stack traces, headers, cookies, route values, or form fields.
- Applications can override the page with `~/Views/Shared/500.cshtml`. Keep the page generic and support-oriented. Do not read `IExceptionHandlerFeature`, request headers, cookies, route values, or form values unless the app has a deliberate reviewed disclosure policy.
- Development keeps its existing developer exception behavior. AppSurface does not install the conventional production handler when `StartupContext.IsDevelopment` is true.
- API-only apps, JSON problem-details APIs, tenant-specific error pages, or apps with telemetry-first exception middleware should leave this disabled and register their own exception handling.
- Once ASP.NET Core has started a response, exception handling cannot replace it with the conventional page. Design streaming endpoints so failures are reported through the stream protocol rather than relying on a late 500 page.

### Executable error-page proof

Use the focused [web error-page proof](../../examples/web-error-pages/README.md) when you want executable evidence rather than API reference prose:

```bash
bash examples/web-error-pages/verify.sh
```

The proof starts a local production-mode app, verifies browser HTML for empty `401`, `403`, `404`, and thrown `500` paths, verifies API requests do not receive browser HTML, and checks that synthetic request sentinels are absent from the production `500` response body.

### Configuration and Port Overrides

The web application supports standard ASP.NET Core configuration sources (command-line arguments, environment variables, and `appsettings.json`).

#### Deterministic Development Port Default

When an AppSurface web application starts in `Development` without explicit endpoint configuration, AppSurface Web chooses a deterministic localhost-only fallback URL based on the current workspace path. That gives each local worktree a stable URL instead of every development environment fighting for the same hard-coded dev port.

- Use this default for local `dotnet run` convenience when you do not care about a specific port ahead of time.
- AppSurface treats `--environment Development`, `ASPNETCORE_ENVIRONMENT=Development`, and `DOTNET_ENVIRONMENT=Development` as development for both the deterministic port resolver and module-level `StartupContext.IsDevelopment` decisions. Command-line environment parsing is shared with `DefaultEnvironmentProvider`, so duplicate `--environment` keys use the last valid value.
- Override it any time with `--port`, `--urls`, `ASPNETCORE_URLS`/`URLS`, `ASPNETCORE_HTTP_PORTS`/`DOTNET_HTTP_PORTS`/`HTTP_PORTS`, `ASPNETCORE_HTTPS_PORTS`/`DOTNET_HTTPS_PORTS`/`HTTPS_PORTS`, `urls`/`http_ports`/`https_ports` in appsettings, or `Kestrel:Endpoints` in appsettings/environment variables.
- Treat the startup log as the source of truth for the selected local URL.
- The automatic fallback and `--port` shortcut bind only `http://localhost:{port}`. Add `--all-hosts` to the `--port` shortcut, or pass an explicit wildcard URL, only when you intentionally need LAN/container access.

#### Port Overrides

You can override the application's listening port using several methods:

1.  **Command-Line**: Use `--port` (localhost-only shortcut), `--port` with `--all-hosts`, or `--urls`.

    ```bash
    dotnet run -- --port 5001
    # OR
    dotnet run -- --port 5001 --all-hosts
    # OR
    dotnet run -- --urls "http://localhost:5001"
    ```

2.  **Environment Variables**: Set `ASPNETCORE_URLS`.

    ```bash
    export ASPNETCORE_URLS="http://localhost:5001"
    dotnet run
    ```

3.  **App Settings**: Configure `urls` in `appsettings.json`.

    ```json
    {
      "urls": "http://localhost:5001"
    }
    ```

4.  **Kestrel Endpoints**: Configure named endpoints when you need protocol, certificate, or endpoint-specific settings.

    ```json
    {
      "Kestrel": {
        "Endpoints": {
          "Http": {
            "Url": "http://localhost:5001"
          }
        }
      }
    }
    ```

> [!NOTE]
> The `--port` flag is a convenience shortcut that maps to `http://localhost:{port}`. Add `--all-hosts` to map the same port to `http://localhost:{port};http://*:{port}` when all-interface access is intentional. The `*` host is a wildcard binding and can expose the preview host beyond the local machine. If both `--port` and `--urls` are provided, `--port` takes precedence.
> [!TIP]
> If you rely on the deterministic development-port fallback, different worktrees on the same machine will get different stable ports. If you need a predictable shared URL for docs, QA, or CI instructions, pass `--port` or `--urls` explicitly instead of depending on the fallback.

### Startup Watchdog

AppSurface Web fails fast when a host does not complete startup within `WebOptions.StartupTimeout`. The default is 10 seconds. This catches pre-bind stalls where the process is alive but Kestrel has not started listening, including sandbox restrictions, package layout issues, static web asset discovery hangs, and hosted services that block startup.

When the watchdog fires, AppSurface logs the observed startup phase, current directory, application base directory, static web asset mode, endpoint-related startup arguments, and any known Codex sandbox markers such as `CODEX_SANDBOX`. If a Codex sandbox is detected, try the same command outside the sandbox or with the runner's approved unsandboxed/escalated permission before debugging package layout or hosted-service startup.

Configure or disable the watchdog through `WebOptions`:

```csharp
await WebApp<MyRootModule>.RunAsync(
    args,
    options => options.StartupTimeout = TimeSpan.FromSeconds(60));
```

Set `StartupTimeout` to `null` only when the host intentionally performs long-running pre-bind work. Values at or below zero are invalid; use `null` instead of `TimeSpan.Zero` when disabling the guard. The watchdog stops checking once startup completes, so it does not limit normal request processing or long-running background work after the host is listening.

---
[📂 Back to Web List](../README.md) | [🏠 Back to Root](../../README.md)
