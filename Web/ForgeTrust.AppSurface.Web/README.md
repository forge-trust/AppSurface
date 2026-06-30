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

### PWA Install Metadata

AppSurface Web can serve the baseline Progressive Web App install contract from `WebOptions.Pwa`: a manifest endpoint, MVC/Razor head tags, development diagnostics, and an explicit starter offline service worker. PWA support is disabled by default, and offline caching stays off until the app configures an offline strategy.

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
appsurface pwa verify --url https://app.example.com
```

AppSurface maps `/manifest.webmanifest` with `application/manifest+json`, exposes development-only diagnostics at `/_appsurface/pwa`, and emits no service worker unless `options.Pwa.Offline.Enabled` is true. Minimal API and custom-layout apps can copy the exact generated tags from `/_appsurface/pwa` instead of using the TagHelper.

For the full API shape, browser caveats, offline pitfalls, and CLI proof flow, see [PWA install support](Docs/pwa-install.md). For executable proof, run [examples/web-pwa-install](../../examples/web-pwa-install/README.md).

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
