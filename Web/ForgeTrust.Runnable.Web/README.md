# Runnable Web

The **ForgeTrust.Runnable.Web** package provides the bootstrapping logic for building ASP.NET Core applications using the Runnable module system. It sits on top of the compilation concepts defined in `ForgeTrust.Runnable.Core`.

## Overview

The easiest way to get started is by using the `WebApp` static entry point. This provides a default setup that works for most applications.

```csharp
await WebApp<MyRootModule>.RunAsync(args);
```

For more advanced use cases where you need to customize the startup lifecycle beyond what the options provide, you can extend `WebStartup<TModule>`.

## Key Abstractions

### `WebApp`

The primary entry point for web applications. It handles creating the internal startup class and running the application. It provides a generic overload `WebApp<TModule>` for standard usage and `WebApp<TStartup, TModule>` if you have a custom startup class.

### `IRunnableWebModule`

Modules that want to participate in the web startup lifecycle should implement this interface. It extends `IRunnableHostModule` and adds web-specific hooks:

*   **`ConfigureWebOptions`**: Modify the global `WebOptions` (e.g., enable MVC, configure CORS).
*   **`ConfigureWebApplication`**: Register middleware using `IApplicationBuilder` (e.g., `app.UseAuthentication()`).
*   **`ConfigureEndpoints`**: Map endpoints using `IEndpointRouteBuilder` (e.g., `endpoints.MapGet("/", ...)`).

### `WebStartup`

The base class for the application bootstrapping logic. While `WebApp` uses a generic version of this internally, you can extend it if you need deep customization of the host builder or service configuration logic.

## Features

### MVC and Controllers

Support for MVC approaches can be configured via `WebOptions`:

*   **None**: For pure Minimal APIs (default).
*   **Controllers**: For Web APIs using controllers (`AddControllers`).
*   **ControllersWithViews**: For traditional MVC apps with views.
*   **Full**: Full MVC support.

### CORS
Built-in support for CORS configuration:
*   **Enforced Origin Safety**: When `EnableCors` is true, you MUST specify at least one origin in `AllowedOrigins`, unless running in Development with `EnableAllOriginsInDevelopment` enabled (the default). If `AllowedOrigins` is empty in production or when `EnableAllOriginsInDevelopment` is disabled, the application will throw a startup exception to prevent unintended security openness (verified by tests `EmptyOrigins_WithEnableCors_ThrowsException` and `EnableAllOriginsInDevelopment_AllowsAnyOrigin`).
*   **Development Convenience**: `EnableAllOriginsInDevelopment` (enabled by default) automatically allows any origin when the environment is `Development`, simplifying local testing without compromising production security.
*   **Default Policy**: Configures a policy named "DefaultCorsPolicy" (configurable) and automatically registers the CORS middleware.

### Endpoint Routing

Modules can define their own endpoints, making it easy to slice features vertically ("Vertical Slice Architecture").

### Browser Status Pages

Runnable Web includes conventional browser-facing pages for empty `401`, `403`, and `404` status responses. The feature is designed for human browser requests: it keeps the original HTTP status code, shows recovery-oriented HTML, and leaves JSON/API responses alone.

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
| `401` | `/_runnable/errors/401` |
| `403` | `/_runnable/errors/403` |
| `404` | `/_runnable/errors/404` |

Override any page with the conventional app or shared Razor Class Library path:

| Status | Override path |
|--------|---------------|
| `401` | `~/Views/Shared/401.cshtml` |
| `403` | `~/Views/Shared/403.cshtml` |
| `404` | `~/Views/Shared/404.cshtml` |

Use `BrowserStatusPageModel` in overrides:

```cshtml
@model ForgeTrust.Runnable.Web.BrowserStatusPageModel

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
- Missing `/docs/...` `404` routes include a documentation search recovery link because stale docs links are the most common browser miss.
- Static export remains conservative: RazorWire CLI probes `/_runnable/errors/404` and writes only `404.html`; it does not emit `401.html` or `403.html`.
- Production exception-page support for conventional `500` pages is intentionally separate and tracked in [issue #224](https://github.com/forge-trust/Runnable/issues/224). This feature uses ASP.NET Core status-code pages, not exception-handler middleware.

### Configuration and Port Overrides

The web application supports standard ASP.NET Core configuration sources (command-line arguments, environment variables, and `appsettings.json`).

#### Deterministic Development Port Default

When a Runnable web application starts in `Development` without explicit endpoint configuration, Runnable Web chooses a deterministic localhost-only fallback URL based on the current workspace path. That gives each local worktree a stable URL instead of every development environment fighting for the same hard-coded dev port.

- Use this default for local `dotnet run` convenience when you do not care about a specific port ahead of time.
- Override it any time with `--port`, `--urls`, `ASPNETCORE_URLS`/`URLS`, `ASPNETCORE_HTTP_PORTS`/`DOTNET_HTTP_PORTS`/`HTTP_PORTS`, `ASPNETCORE_HTTPS_PORTS`/`DOTNET_HTTPS_PORTS`/`HTTPS_PORTS`, `urls`/`http_ports`/`https_ports` in appsettings, or `Kestrel:Endpoints` in appsettings/environment variables.
- Treat the startup log as the source of truth for the selected local URL.
- The automatic fallback binds only `http://localhost:{port}`. Use `--port` or an explicit wildcard URL when you intentionally need LAN/container access.

#### Port Overrides

You can override the application's listening port using several methods:

1.  **Command-Line**: Use `--port` (shortcut) or `--urls`.
    ```bash
    dotnet run -- --port 5001
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
> The `--port` flag is a convenience shortcut that maps to `http://localhost:{port};http://*:{port}`. This ensures the application is accessible on all interfaces while logging a clickable `localhost` URL in the console. If both `--port` and `--urls` are provided, `--port` takes precedence.
> [!TIP]
> If you rely on the deterministic development-port fallback, different worktrees on the same machine will get different stable ports. If you need a predictable shared URL for docs, QA, or CI instructions, pass `--port` or `--urls` explicitly instead of depending on the fallback.

---
[📂 Back to Web List](../README.md) | [🏠 Back to Root](../../README.md)
