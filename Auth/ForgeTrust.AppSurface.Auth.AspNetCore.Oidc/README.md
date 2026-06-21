# ForgeTrust.AppSurface.Auth.AspNetCore.Oidc

`ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` registers named ASP.NET Core cookie and OpenID Connect schemes with AppSurface-safe defaults.

Use this package when an ASP.NET Core web app already has an identity provider selected and wants a shorter, safer cookie + OIDC setup for AppSurface modules. The package composes `ForgeTrust.AppSurface.Auth.AspNetCore` so request principals and host-owned policy decisions still flow through the existing AppSurface auth adapter.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md), the [unreleased proof artifact](../../releases/unreleased.md), and the current [package chooser](../../packages/README.md) for release risk and package readiness.

## Quickstart: Register Named Cookie And OIDC Schemes

Install the package:

```bash
dotnet package add ForgeTrust.AppSurface.Auth.AspNetCore.Oidc
```

Register AppSurface OIDC auth:

```csharp
using ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

builder.Services.AddAppSurfaceOidcAuth(options =>
{
    options.ConfigureOpenIdConnect(oidc =>
    {
        oidc.Authority = builder.Configuration["Authentication:Oidc:Authority"];
        oidc.ClientId = builder.Configuration["Authentication:Oidc:ClientId"];
        oidc.ClientSecret = builder.Configuration["Authentication:Oidc:ClientSecret"];
    });
});
builder.Services.AddAuthorization();
```

Run the host auth middleware in the normal ASP.NET Core order:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

The default schemes are:

| Scheme | Default |
| --- | --- |
| Cookie | `AppSurface.Cookies` |
| OIDC | `AppSurface.Oidc` |

The package does not set global default schemes. If the host wants ASP.NET Core defaults, configure them explicitly with the normal `AddAuthentication(options => ...)` API before or after adding this package.

## What The Package Includes

- `AddAppSurfaceOidcAuth(...)`
- `AppSurfaceOidcAuthOptions`
- named cookie and OIDC scheme registration
- OIDC authorization code flow default
- OIDC sign-in scheme wired to the AppSurface cookie scheme
- `SaveTokens = false` by default
- `sub` subject mapping through `ForgeTrust.AppSurface.Auth.AspNetCore`
- safe local-only passive login/logout prompt helpers
- stable OIDC diagnostic codes
- host event chaining for OIDC diagnostics

## What The Package Does Not Include

- user stores, app-user provisioning, or ASP.NET Identity replacement
- identity-provider hosting or OAuth/OIDC server behavior
- provider SDKs such as Microsoft.Identity.Web, Auth0, Okta, or Keycloak packages
- EF Core, persistence, tenant authority, or permission systems
- middleware insertion
- challenge, redirect, sign-in, or sign-out execution
- silent default scheme takeover
- token storage by default
- Aspire, Keycloak management, or Dev Auth test harness behavior

## Defaults And Overrides

| Setting | Default | Override |
| --- | --- | --- |
| Cookie scheme | `AppSurface.Cookies` | `options.CookieScheme = "..."` |
| OIDC scheme | `AppSurface.Oidc` | `options.OidcScheme = "..."` |
| Callback path | `/signin-appsurface-oidc` | `options.CallbackPath = "..."` |
| Signed-out callback path | `/signout-callback-appsurface-oidc` | `options.SignedOutCallbackPath = "..."` |
| Subject claim | `sub` | `options.SubjectClaim = "..."` |
| Save tokens | `false` | `options.SaveTokens = true` |
| Client secret validation | required | `options.RequireClientSecret = false` |

Use `ConfigureOpenIdConnect(...)` for provider settings:

```csharp
builder.Services.AddAppSurfaceOidcAuth(options =>
{
    options.ConfigureOpenIdConnect(oidc =>
    {
        oidc.Authority = "https://login.example";
        oidc.ClientId = "appsurface-web";
        oidc.ClientSecret = builder.Configuration["Authentication:Oidc:ClientSecret"];
        oidc.Scope.Add("profile");
    });
});
```

Client secrets are applied to ASP.NET Core `OpenIdConnectOptions`; AppSurface diagnostics never copy them into metadata.

## Login And Logout Prompts

`CreateLoginPrompt(...)` and `CreateLogoutPrompt(...)` create passive AppSurface prompt objects only:

```csharp
var prompt = oidcOptions.CreateLoginPrompt("/dashboard", "Sign in");
```

Prompt targets must be local app-relative paths such as `/dashboard`. External, protocol-relative, backslash-containing, or control-character paths are rejected. The helpers do not call `ChallengeAsync`, `SignInAsync`, `SignOutAsync`, or `Redirect`.

## Diagnostics

Setup diagnostics use stable codes and safe metadata:

| Code | Problem | Fix |
| --- | --- | --- |
| `ASOIDC001` | Missing authority | Configure `oidc.Authority`. |
| `ASOIDC002` | Missing client id | Configure `oidc.ClientId`. |
| `ASOIDC003` | Missing client secret while required | Configure `oidc.ClientSecret` or set `RequireClientSecret = false`. |
| `ASOIDC004` | Remote OIDC failure | Check provider callback/signout URLs and provider logs. |
| `ASOIDC005` | Missing subject claim | Issue the configured subject claim or change `SubjectClaim`. |
| `ASOIDC006` | Token persistence enabled | Confirm the host accepts the cookie-size and token-storage tradeoff. |

Diagnostics must not include raw tokens, raw claims, email addresses, display names, client secrets, ID-token payloads, or provider response bodies.

Middleware ordering symptoms are documented, not perfectly runtime-detected. If authentication never appears to run, verify `UseAuthentication()` is before `UseAuthorization()` and before endpoints that need `HttpContext.User`.

## Provider Guidance

Use this package when you want recognizable ASP.NET Core cookie + OIDC handlers with AppSurface naming, subject mapping, return-url guardrails, and safe diagnostics.

Use raw ASP.NET Core OIDC when the host already has a detailed provider setup and only needs complete handler control.

Use Microsoft.Identity.Web, Auth0, Okta, Keycloak, or another provider SDK when the app needs provider-specific token acquisition, management APIs, tenant helpers, or SDK-specific conventions.

Durable external-subject-to-app-user mapping belongs to the AppSurface app-user mapping contract path, not this package.

## Local Proof

For a no-secret demonstration of the registration surface, see [the ASP.NET Core OIDC example](../../examples/auth-aspnetcore-oidc/README.md).

## Pitfalls

- Register real provider values before resolving the named OIDC options.
- Configure callback and signout callback URLs in the identity provider when using the defaults.
- Do not enable `SaveTokens` unless the host accepts larger cookies and stored token material.
- Do not treat the external OIDC subject as an AppSurface app-user record.
- Do not expect this package to insert middleware or execute login/logout redirects.
