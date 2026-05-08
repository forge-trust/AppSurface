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

### Conventional 404 Pages

Runnable Web includes a conventional browser-facing 404 page when MVC views are available through `WebOptions.Mvc.MvcSupportLevel` values such as `MvcSupport.ControllersWithViews` or `MvcSupport.Full`, or when `WebOptions.Errors.NotFoundPageMode` explicitly enables the conventional page. The page keeps the HTTP status code at `404` and gives users recovery actions instead of exposing only framework or maintainer details.

- Missing default docs routes include a documentation search recovery link because stale docs links are the most common browser miss. Apps that mount RazorDocs somewhere else should point users at their configured docs search route.
- Other browser requests include a home recovery link and plain-language copy that explains the route may have moved or may not exist.
- JSON and API-oriented requests do not get the HTML fallback; they keep API-friendly 404 behavior.
- Applications can override the page with `~/Views/Shared/404.cshtml`. Keep a clear recovery path and preserve the `404` response code when replacing the default view.

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
