# ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth

`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` adds fake, local-only ASP.NET Core authentication for AppSurface package consumers who need to try auth-aware endpoints without configuring OIDC, cookies, ASP.NET Identity, or an external identity provider.

DevAuth is development tooling. It is not production authentication, a user store, durable app-user mapping, OIDC, token validation, tenant authority, audit logging, or the Auth.Testing integration-test harness.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when choosing between DevAuth, Auth.Testing, OIDC, and host-owned ASP.NET Core authentication.

## Quickstart

Install the package in an ASP.NET Core app:

```bash
dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth
```

Configure AppSurface auth mapping, seed local personas, require the DevAuth named scheme in policies, and map the control page:

```csharp
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

builder.Services.AddAuthorization(options =>
{
    options.AddAppSurfacePolicy(
        "OperatorsOnly",
        policy => policy
            .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("role", "operator"));
});

builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.Users.Add(
        "admin",
        user => user
            .DisplayName("Local Admin")
            .Subject("admin-1")
            .Claim("role", "operator"));
    dev.Users.Add(
        "viewer",
        user => user
            .DisplayName("Local Viewer")
            .Subject("viewer-1")
            .Claim("role", "viewer"));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (
    HttpContext httpContext,
    IHostEnvironment environment,
    IOptions<AppSurfaceDevAuthOptions> devAuthOptions,
    IDataProtectionProvider dataProtectionProvider) => Results.Content(
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Local proof app</title>
        </head>
        <body>
          <header>Local proof app</header>
          {{AppSurfaceDevAuthMarker.Render(httpContext, environment, devAuthOptions, dataProtectionProvider)}}
          <main>Protected application content</main>
        </body>
        </html>
        """,
        "text/html"));

app.MapGet("/api/auth-proof", () => Results.Json(new { result = "allowed" }))
   .RequireSurfacePolicy("OperatorsOnly");

app.MapAppSurfaceDevAuth();
```

Run in Development:

```bash
DOTNET_ENVIRONMENT=Development dotnet run
```

DevAuth activates only in `Development` by default. For a host-owned proof environment, add the exact environment name explicitly:

```csharp
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.AllowedEnvironmentNames.Add("Staging");
    // personas...
});
```

Only add local or proof environments that may safely expose fake personas. `AllowedEnvironmentNames` is a DevAuth activation allow-list, not a production security boundary.

Open the persona lab:

```text
/_appsurface/dev-auth
```

### Return To The Host Page

To return to a host page after selecting or clearing a persona, open the control page with a URI-encoded local target:

```text
/_appsurface/dev-auth/?returnUrl=%2Fprotected%3Ftab%3Dauth
```

DevAuth carries a safe rooted local path through every select and clear form action, then returns through the existing local redirect after the mutation. An explicit `/` is valid. Missing, blank, non-rooted, absolute, protocol-relative, backslash-containing, or control-character values are omitted from the forms; submitting then leaves the browser on the normal updated DevAuth control response. Rejected targets do not produce a diagnostic because omission is the fail-closed fallback.

The control page lets you select a seeded persona, clear the persona cookie, inspect safe local claims, and copy a visible marker such as `DEV AUTH: Local Admin (AppSurface.DevAuth)`. For a persistent in-app indicator, render `AppSurfaceDevAuthMarker` from your local layout or proof page. The renderer returns an empty string when the current environment is not in `AllowedEnvironmentNames`, so layouts do not need their own `environment.IsDevelopment()` guard. With default styles, the marker is a fixed bottom-right overlay above 640 CSS pixels and participates in normal document flow at widths up to and including 640 CSS pixels. It starts collapsed, keeps the active fake persona visible, and expands to POST-only persona controls that return to the current page after selection.

The host must provide `<meta name="viewport" content="width=device-width, initial-scale=1">` so the 640 CSS-pixel breakpoint tracks the device width. Render the marker after persistent application chrome and before `<main>` (or the equivalent primary content container). At narrow widths, that host-owned location becomes the marker's in-flow position, so opening the disclosure pushes following content rather than covering it. The host also owns outer spacing and the containing layout.

## API Reference

- `AddAppSurfaceDevAuth(IHostEnvironment environment, Action<AppSurfaceDevAuthOptions> configure)` registers the named DevAuth authentication scheme and startup safety validation. The `configure` callback is evaluated once during registration, and the same validated options are used for both scheme registration and runtime DevAuth behavior.
- `MapAppSurfaceDevAuth(this IEndpointRouteBuilder endpoints)` maps the local-only control page, status JSON, select persona endpoint, and clear persona endpoint. The control-page GET accepts an optional safe rooted local `returnUrl`, carries it through every select and clear form action, and returns through a local redirect after successful mutation. Missing or rejected targets are omitted, so mutations render the updated control page normally. Control and mutation endpoints return not found when the active environment is not allowed; status remains read-only and reports `enabled: false`. The control page root always includes a static-auditable DevAuth control-page marker attribute so static export audits can reject DevAuth UI before it is written to disk.
- `AppSurfaceDevAuthMarker.Render(HttpContext, IHostEnvironment, IOptions<AppSurfaceDevAuthOptions>, IDataProtectionProvider, Action<AppSurfaceDevAuthMarkerOptions>?)` returns safe HTML for an explicit in-app DevAuth state marker. It returns `string.Empty` when the active environment is not allowed. With default styles, the marker is fixed above 640 CSS pixels and in flow at or below 640 CSS pixels. The marker root always includes a static-auditable DevAuth marker attribute so static export audits can reject DevAuth UI even when the CSS class prefix is customized.
- `AppSurfaceDevAuthDefaults.AuthenticationScheme` is `AppSurface.DevAuth`.
- `AppSurfaceDevAuthDefaults.PathPrefix` is `/_appsurface/dev-auth`.
- `AppSurfaceDevAuthDefaults.CookieName` is `.AppSurface.DevAuth.Persona`.
- `AppSurfaceDevAuthDefaults.SubjectClaimType` is `sub`.
- `AppSurfaceDevAuthOptions.Users` contains seeded local personas.
- `AppSurfaceDevAuthOptions.SchemeName` overrides the registered authentication scheme. It defaults to `AppSurfaceDevAuthDefaults.AuthenticationScheme`.
- `AppSurfaceDevAuthOptions.PathPrefix` overrides the local control-page and status endpoint path prefix. It defaults to `AppSurfaceDevAuthDefaults.PathPrefix`.
- `AppSurfaceDevAuthOptions.CookieName` overrides the selected-persona cookie name. It defaults to `AppSurfaceDevAuthDefaults.CookieName`.
- `AppSurfaceDevAuthOptions.AllowedEnvironmentNames` controls where DevAuth may activate. It defaults to `Development`; names are compared case-insensitively after trimming for comparison. The set must contain at least one non-blank value.
- `AppSurfaceDevAuthOptions.UseAsDefaultSchemeForLocalProof` is off by default. Enable it only for throwaway local proof hosts where DevAuth intentionally owns the whole auth stack.
- `AppSurfaceDevAuthOptions.AllowDevAuthOverrideForLocalProof` is off by default. Enable it only when a local proof intentionally composes DevAuth with other registered auth schemes.
- `AppSurfaceDevAuthOptions.RequireLoopbackControlRequests` is on by default.
- `AppSurfaceDevAuthOptions.DisplayClaimTypes` controls which issued claims may appear in the local HTML preview. It defaults to `sub`, `role`, and `tenant`.
- `AppSurfaceDevAuthMarkerOptions.CssClassPrefix` changes the CSS class prefix for marker elements. The default is the package-owned DevAuth marker prefix.
- `AppSurfaceDevAuthMarkerOptions.AdditionalCssClass` appends host-owned classes to the marker root.
- `AppSurfaceDevAuthMarkerOptions.IncludeDefaultStyles` is on by default. Disable it to skin and position the marker entirely with host CSS.
- `AppSurfaceDevAuthMarkerOptions.ShowPersonaControls` is on by default. Disable it when a page should show state but send persona changes through the full control page.
- `AppSurfaceDevAuthMarkerOptions.StartExpanded` is off by default to keep the fixed desktop overlay compact. Enable it when a proof page should show controls immediately; at narrow widths, the default-styled expanded marker remains in flow.
- `AppSurfaceDevAuthMarkerOptions.ReturnUrl` overrides the local page that marker POSTs return to. When unset, DevAuth returns to the current request path and query.

Persona IDs must be route-safe local identifiers containing only ASCII letters, digits, `.`, `_`, or `-`. The dot-segment IDs `.` and `..` are not allowed, and ids that look like tokens, secrets, passwords, keys, credentials, or emails are rejected. Persona IDs are used in the selection endpoint path and stored as the protected cookie payload.

Persona state is stored in a protected, HttpOnly, SameSite=Strict cookie that contains only the persona id. DevAuth adds the `Secure` cookie attribute on HTTPS requests and omits it on plain HTTP localhost so browser-based local proof works. Blank, unknown, stale, reset, or tampered cookie state authenticates as no result.

The authentication handler issues every seeded persona claim, but the control page does not display every issued claim. Claims are rendered only when their type is in `DisplayClaimTypes`, their value is short, and neither the type nor the value looks like a token, secret, password, key, credential, or email. Display names and subjects that look sensitive are redacted from HTML and status JSON. Hidden claims are counted without showing their values.

The marker renderer uses the same safe display rules as the control page and status JSON. It does not render arbitrary claims. Marker select and clear buttons call the same POST-only mutation endpoints as the control page and use only safe local return URLs, so external `returnUrl` values do not redirect.

## Responsive Placement And Customization

The package default deliberately changes placement rather than visibility: above 640 CSS pixels the marker remains the existing fixed bottom-right development overlay; at 640 CSS pixels or below it becomes an ordinary in-flow element. The host render location is therefore visually significant on narrow screens. The default non-obstruction guarantee applies when the marker is rendered in an ordinary document-flow container after persistent application chrome and before main content.

The host owns viewport metadata and outer spacing. Add the standard viewport tag to the document `<head>`, then use `AdditionalCssClass` for local spacing or a higher-specificity placement override:

```csharp
var marker = AppSurfaceDevAuthMarker.Render(
    httpContext,
    environment,
    devAuthOptions,
    dataProtectionProvider,
    options => options.AdditionalCssClass = "local-dev-auth");
```

```css
@media (max-width: 640px) {
  body > .local-dev-auth {
    margin: 12px 16px;
  }
}
```

Use `CssClassPrefix` when integrating the existing hierarchy with host CSS. Set `IncludeDefaultStyles = false` only when the host will provide the complete visual and responsive placement contract; no package CSS is emitted in that mode. Custom CSS can intentionally override package placement, but the host then owns overlap prevention.

For a complete host-owned skin, change the prefix and disable package styles together:

```csharp
var marker = AppSurfaceDevAuthMarker.Render(
    httpContext,
    environment,
    devAuthOptions,
    dataProtectionProvider,
    options =>
    {
        options.CssClassPrefix = "local-dev-auth";
        options.IncludeDefaultStyles = false;
    });
```

The host must then style the emitted `local-dev-auth` hierarchy and own both desktop and narrow-screen placement. This minimal starting point keeps the warning visible, preserves the desktop overlay, and reserves mobile layout space:

```css
.local-dev-auth {
  color: #111827;
  background: #fff;
  border: 2px solid #b91c1c;
}

@media (min-width: 641px) {
  .local-dev-auth {
    position: fixed;
    right: 16px;
    bottom: 16px;
    z-index: 2147483647;
    max-width: min(360px, calc(100vw - 32px));
  }
}

@media (max-width: 640px) {
  .local-dev-auth {
    position: static;
    max-width: none;
  }

  .local-dev-auth__actions form,
  .local-dev-auth__button {
    min-width: 0;
    max-width: 100%;
    overflow-wrap: anywhere;
  }
}
```

## Marker Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| The marker still behaves like a desktop overlay on a phone. | The host document omits the viewport meta tag, so the browser uses a wider layout viewport. | Add `<meta name="viewport" content="width=device-width, initial-scale=1">` to `<head>`. |
| The narrow marker covers or clips application content. | The marker is inside a fixed, absolutely positioned, clipped, or otherwise overlapping host container, or host CSS overrides the package placement. | Render it in ordinary flow after persistent application chrome and before main content; remove the conflicting container or own placement with custom CSS. |
| The marker is below the fold on a narrow screen. | Normal flow reserves space but does not keep the marker pinned to the viewport; its visibility depends on the host render location. | Render it after persistent application chrome and before main content. Use a host-owned fixed or sticky override only when persistent visibility is more important than package-guaranteed non-obstruction. |
| The in-flow marker touches the viewport or adjacent content. | AppSurface does not choose host-specific outer spacing. | Add a host class with `AdditionalCssClass` and apply narrow-screen margin in host CSS. |
| A custom-skinned marker does not switch placement at 640 pixels. | `IncludeDefaultStyles = false` removes all package CSS, including the responsive rule. | Add the host's own media query and overlap-prevention behavior, or re-enable package styles. |

## Contributor Test Loop

The fast loop skips browser integration tests:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests.csproj --filter "Category!=Integration"
```

Run only the responsive browser contract while iterating on marker layout or keyboard behavior:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests.csproj --filter "Category=Integration"
```

Run the complete focused project before landing:

```bash
dotnet test Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests.csproj
```

The integration fixture installs Playwright Chromium automatically. The first integration or full run may download the browser; later runs reuse the installed browser.

Persona mutation endpoints are loopback-only by default and reject cross-site browser POSTs when `Origin`, `Referer`, or Fetch Metadata identifies another origin. This keeps arbitrary websites from silently changing the fake persona in a developer's local browser. Command-line local tooling without browser origin headers can still post to the endpoints from loopback.

## DevAuth Versus Other Auth Packages

Use `ForgeTrust.AppSurface.Auth` when reusable modules need surface-neutral auth vocabulary, auth results, prompts, audit event descriptions, or durable external-subject to app-user-id mapping contracts.

Use `ForgeTrust.AppSurface.Auth.AspNetCore` when an ASP.NET Core host already owns authentication and authorization, but AppSurface modules need mapped request context or named host-policy results.

Use DevAuth only when you need fake local personas in Development, or in an explicitly opted-in local/proof environment, so a package consumer can try AppSurface auth-aware endpoints without an identity provider.

Use OIDC or native ASP.NET Core authentication packages for real sign-in, cookies, external identity providers, redirects, token validation, and production auth flows.

Use `ForgeTrust.AppSurface.Auth.Testing` for deterministic automated auth scenarios. DevAuth is for local developer interaction, not browser automation authority.

## What Not To Copy To Production

- Do not enable DevAuth outside configured allowed environments, and add only local/proof environment names that keep fake personas visible.
- Do not treat persona claims as production identity, tenant authority, or permission truth.
- Do not put tokens, passwords, secrets, raw emails, or production identity payloads into seeded personas.
- Do not add sensitive claim types to `DisplayClaimTypes`; the control page still refuses common secret/token/email shapes.
- Do not use the DevAuth persona cookie as production session management.
- Do not hide real auth scheme conflicts with `AllowDevAuthOverrideForLocalProof`.

## Diagnostics

DevAuth diagnostics use `Problem:`, `Cause:`, `Fix:`, and `Docs:` wording and the safe metadata key `appsurface.devauth.diagnostic_code`.

| Code | Meaning |
| --- | --- |
| `ASDEV001` | DevAuth was enabled in an environment that is not in `AllowedEnvironmentNames`. |
| `ASDEV002` | DevAuth detected an existing real authentication scheme or default. |
| `ASDEV003` | DevAuth was enabled without seeded personas. |
| `ASDEV004` | A selected persona did not contain the configured subject claim. |
| `ASDEV005` | The reserved path prefix was invalid or conflicted with the local control surface. |
| `ASDEV006` | A persona id was invalid, unknown, stale, duplicated, or tampered. |

Diagnostics, HTML, and status JSON do not include raw tokens, secrets, passwords, raw emails, or unbounded identity-provider payloads.

## Pitfalls

- Call `UseAuthentication()` before endpoints that depend on selected personas.
- Call `UseAuthorization()` before AppSurface policy-protected endpoints when your host uses normal ASP.NET Core authorization middleware.
- Call `MapAppSurfaceDevAuth()` so the persona lab and status JSON exist.
- Add `AppSurfaceDevAuthDefaults.AuthenticationScheme` to policies that should evaluate DevAuth personas.
- Use simple route-safe persona IDs such as `admin`, `viewer`, or `qa.local_1`; dot segments, sensitive-looking ids, query strings, fragments, encoded slashes, spaces, and other punctuation are rejected with `ASDEV006`.
- Call `Subject(...)` for every persona and keep it aligned with `AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim(...))`.
- Keep the DevAuth marker visible in local sample pages so fake auth is impossible to miss.
- Prefer `AppSurfaceDevAuthMarker.Render(...)` over copying the generated control-page HTML. Use `StartExpanded = true` when the marker should show controls immediately, and use `IncludeDefaultStyles = false`, `CssClassPrefix`, and `AdditionalCssClass` when the marker needs to match a consumer app.
- Include the standard viewport meta tag and render the marker after persistent application chrome and before main content. The package cannot reserve safe space when a host puts the marker inside a fixed, absolute, clipped, or overlapping container.
- DevAuth does not automatically inject a marker into arbitrary responses. Add it explicitly to the pages or local layout where the fake-auth state should be visible; the renderer self-suppresses outside allowed environments.
- If persona selection returns a same-origin 403, make sure custom local UI posts from the same scheme, host, and port as the mapped DevAuth endpoints.
- If persona selection leaves you in the persona lab, verify that the initial control-page URL contained a URI-encoded, rooted local `returnUrl`. Inspect the rendered form action when debugging; rejected values are intentionally omitted rather than diagnosed or redirected to `/`.

## Upgrade And Removal

Remove DevAuth before deploying a host:

1. Remove the `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` package reference.
2. Remove `AddAppSurfaceDevAuth(...)`.
3. Remove `MapAppSurfaceDevAuth()`.
4. Remove DevAuth scheme references from policies.
5. Configure real ASP.NET Core authentication and keep `ForgeTrust.AppSurface.Auth.AspNetCore` only for AppSurface result mapping.

For a working proof, see [the DevAuth example](../../examples/auth-aspnetcore-dev-auth/README.md).
