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
Issue [#624](https://github.com/forge-trust/AppSurface/issues/624) adds a privacy-safe evidence envelope,
[#625](https://github.com/forge-trust/AppSurface/issues/625) adds caller-side polling, and
[#626](https://github.com/forge-trust/AppSurface/issues/626) adds a neutral end-to-end example. Until that operator rail is
proved, this API remains preview. A separate [aggregate snapshot follow-up](https://github.com/forge-trust/AppSurface/issues/645)
will compile multiple checks with bounded concurrency and deadlines after the safe envelope exists; #623 evaluates one
registered name per request.

#### 5-minute named canary path

This path assumes the host already has authentication. If it does not, first choose a host-owned scheme and policy using
the [AppSurface Auth adoption ladder](../../Auth/ForgeTrust.AppSurface.Auth/README.md). Named canaries never install an
authentication scheme or define who an operator is.

Start from this compile-only evaluator skeleton. Replace its placeholder result with an application-owned lookup that
uses `context.Marker` and `context.FreshSince`; the evaluator must inspect existing proof and must not trigger the workflow:

<!-- appsurface:snippet id="appsurface-canary-evaluator" file="Web/ForgeTrust.AppSurface.Web.Tests.SharedErrorPagesFixture/NamedCanaryPublicApiFixture.cs" marker="appsurface-canary-evaluator" lang="csharp" -->
```csharp
using ForgeTrust.AppSurface.Web;

public sealed class ForwardingCanaryEvaluator : IAppSurfaceCanaryEvaluator
{
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
the proof dependency is intentionally unavailable.

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
        });
}
```
<!-- /appsurface:snippet -->

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

The response is status-only in #623:

```json
{"name":"forwarding.alpha-evidence","status":"pending"}
```

#### Status and HTTP contract

`MapAppSurfaceCanaries` maps one GET route, `/_appsurface/canaries/{name}`, for the whole named registry. It is excluded
from API Explorer/OpenAPI and every package-owned response sets `Cache-Control: no-store` and `Pragma: no-cache`.

| Evaluator status | Meaning for a caller | Default HTTP status |
|---|---|---:|
| `pass` | Current proof is acceptable. | `200` |
| `pending` | Acceptable proof may still arrive. | `503` |
| `fail` | Current proof demonstrates failure. | `503` |
| `stale` | Proof exists but predates the requested boundary. | `503` |
| `not-configured` | The registered evaluator cannot inspect an intentionally unavailable proof dependency. | `503` |

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
use `status` as the decision field, and ignore unknown properties so the #624 envelope can grow additively.

#### Registration and request reference

- Names are 1-128 lowercase characters in dot-separated segments. A segment starts and ends with a letter or digit and
  may contain internal hyphens, for example `forwarding.alpha-evidence`. Matching is exact and ordinal.
- `DisplayName` defaults to the registered name, `Description` defaults to `null` and is limited to 512 characters, and
  `Tags` is an ordinal set of 1-64 character lowercase letter/digit/internal-hyphen values. These values are immutable
  registry metadata and are not returned by the #623 endpoint.
- `RequireMarker()` and `RequireFreshSince()` are idempotent. Optional inputs still reach the evaluator when supplied.
- Marker values are opaque: AppSurface preserves the value delivered by ASP.NET Core, including internal whitespace,
  but HTTP clients and servers may normalize leading or trailing field-value whitespace. Avoid whitespace-significant
  markers. Control characters and repeated values are rejected, the maximum is 256 UTF-8 bytes, and a blank optional
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
| `ASCAN101` | Invalid registration | Name, display metadata, description, or tag grammar is invalid. | Correct the registration callback before building the host. |
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
| `ASCAN301` | Evaluation failed | Activation failed, the evaluator threw/canceled independently, or returned null. | Inspect host-local evaluator diagnostics; caller retry policy remains external. |

### PWA Install and Push-Worker Foundation

AppSurface Web provides independent [PWA install, offline, and push-worker capabilities](Docs/pwa-install.md) from `WebOptions.Pwa`: a manifest endpoint, MVC/Razor head tags, development diagnostics, an explicit starter offline strategy, safe default notification handlers, and an inert registration helper. Every capability is disabled by default. Enabling push does not request permission, create a subscription, choose recipients, or send a notification.

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
