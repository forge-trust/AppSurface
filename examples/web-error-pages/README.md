# Web error-page proof

This example is a confidence proof for the `ForgeTrust.AppSurface.Web` error-page conventions. It does not add new framework behavior. It runs a small app and verifies that browser users get conventional HTML status and exception pages while API clients avoid surprise browser HTML.

Run the proof from the repository root:

```bash
bash examples/web-error-pages/verify.sh
```

The verifier requires Bash, `curl`, and the .NET SDK. It starts the app once in local production mode on `127.0.0.1`, sends explicit `Accept` headers, checks response status and content markers, and stops the app before it exits. Set `APP_SURFACE_WEB_ERROR_PAGES_PORT` if the default proof port is already in use:

```bash
APP_SURFACE_WEB_ERROR_PAGES_PORT=61250 bash examples/web-error-pages/verify.sh
```

## What this proves

- Empty browser `401`, `403`, and `404` responses render conventional AppSurface HTML pages while preserving the original status code.
- JSON/API routes do not receive browser status-page HTML.
- Production browser exceptions render a generic `500` page.
- JSON/API exceptions keep a `500` response without receiving the browser exception page copy.
- Synthetic route, header, cookie, form, and exception-message sentinels are absent from the production `500` response body.

The sentinel checks cover response body output only. They do not prove anything about shell history, URLs, logs, reverse proxies, telemetry, browser extensions, or other systems that may observe request data.

## AppSurface configuration

The proof app uses only `ForgeTrust.AppSurface.Web` and enables the browser status and production exception pages through `WebOptions.Errors`.

<!-- appsurface:snippet id="web-error-page-options" file="examples/web-error-pages/Program.cs" marker="web-error-page-options" lang="csharp" -->
```csharp
options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.Controllers };
options.Errors.UseConventionalBrowserStatusPages();
options.Errors.UseConventionalExceptionPage();
```
<!-- /appsurface:snippet -->

`UseConventionalBrowserStatusPages()` handles empty browser-oriented status responses. `UseConventionalExceptionPage()` handles thrown exceptions in production. Keep them separate when reasoning about behavior: status-code pages do not catch thrown exceptions.

## Using this in your app

Install or reference `ForgeTrust.AppSurface.Web`, set MVC support to at least controllers, and opt into the two error-page conventions in the same `WebOptions` configuration path your app already uses. Keep API routes explicit about their response bodies and `Accept` expectations; AppSurface only renders the conventional browser pages for requests that prefer HTML.

## Manual curl checks

Start the app in one terminal:

```bash
dotnet run --project examples/web-error-pages/WebErrorPagesExample.csproj -- --port 61249 --environment Production
```

Then run these from another terminal. The commands intentionally use `curl -i -sS` without `--fail` because the expected proof responses include `4xx` and `5xx` status codes.

```bash
curl -i -sS -H "Accept: text/html" http://127.0.0.1:61249/empty-401
curl -i -sS -H "Accept: text/html" http://127.0.0.1:61249/empty-403
curl -i -sS -H "Accept: text/html" http://127.0.0.1:61249/empty-404
curl -i -sS -H "Accept: application/json" http://127.0.0.1:61249/api/not-found
curl -i -sS -H "Accept: text/html" http://127.0.0.1:61249/throws
curl -i -sS -H "Accept: application/json" http://127.0.0.1:61249/api/throws
curl -i -sS -X POST \
  -H "Accept: text/html" \
  -H "X-Proof-Sentinel: synthetic-header-proof-249" \
  -H "Cookie: proof-cookie=synthetic-cookie-proof-249" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data "proof-form=synthetic-form-proof-249" \
  http://127.0.0.1:61249/throws/synthetic-route-proof-249
```

PowerShell users can run the same app command, then use `Invoke-WebRequest` with explicit headers:

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:61249/empty-404" -Headers @{ Accept = "text/html" } -SkipHttpErrorCheck
Invoke-WebRequest -Uri "http://127.0.0.1:61249/api/not-found" -Headers @{ Accept = "application/json" } -SkipHttpErrorCheck
```

## When not to use this

Do not enable these conventions for API-only applications that must always return JSON Problem Details, tenant-specific error envelopes, or telemetry-first exception middleware. In those apps, leave AppSurface's conventional pages disabled and register the app's own ASP.NET Core error-handling policy.

## Override or disable

Applications can override browser status pages with `~/Views/Shared/401.cshtml`, `~/Views/Shared/403.cshtml`, and `~/Views/Shared/404.cshtml`. They can override the production exception page with `~/Views/Shared/500.cshtml`.

Disable the conventions when another middleware owns the behavior:

```csharp
options.Errors.DisableBrowserStatusPages();
options.Errors.DisableConventionalExceptionPage();
```

For the full API shape, defaults, constraints, and pitfalls, see the [AppSurface Web README](../../Web/ForgeTrust.AppSurface.Web/README.md).

## Snippet verification

The configuration snippet above is source-owned by `Program.cs`. Verify it from the repository root:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.MarkdownSnippets/ForgeTrust.AppSurface.MarkdownSnippets.csproj -- verify --document examples/web-error-pages/README.md
```
