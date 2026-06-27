# ForgeTrust.RazorWire.Auth.AspNetCore

`ForgeTrust.RazorWire.Auth.AspNetCore` connects RazorWire auth projection helpers to host-owned ASP.NET Core policies through `ForgeTrust.AppSurface.Auth.AspNetCore`.

Use this package when an ASP.NET Core host already configured authentication, authorization policies, AppSurface auth mapping, and RazorWire, and you want `rw:auth-view` or `rw:auth-gate` to render passive UI states from those host policy results.

This package is not authentication, sign-in UI, sign-out UI, OIDC, cookies, ASP.NET Identity, policy creation, middleware insertion, redirects, challenges, forbids, or DevAuth. It only registers an `IRazorWireAuthResultProvider` that delegates to `IAppSurfaceAspNetCorePolicyEvaluator`.

## Release Guidance

AppSurface publishes coordinated `v0.1.0` release candidates. Before installing this package from a prerelease feed, read the [v0.1.0 RC 4 release note](../../releases/v0.1.0-rc.4.md) for current release risk, migration guidance, and package readiness.

## Quickstart

```csharp
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Auth.AspNetCore;

builder.Services.AddRazorWire();
builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));
builder.Services.AddRazorWireAspNetCoreAuth();

builder.Services.AddAuthorization(options =>
{
    options.AddAppSurfacePolicy(
        "docs.publish",
        policy => policy.RequireAuthenticatedUser().RequireClaim("role", "publisher"));
});
```

Then project the same host policy in a Razor view:

```cshtml
<rw:auth-view policy="docs.publish">
    <rw:auth-allowed>
        <button type="submit">Publish</button>
    </rw:auth-allowed>
    <rw:auth-anonymous>
        <rw:login-link href="/login" return-url-policy="current-path">Sign in</rw:login-link>
    </rw:auth-anonymous>
    <rw:auth-forbidden>
        You do not have permission to publish this page.
    </rw:auth-forbidden>
</rw:auth-view>
```

Always enforce the policy on the endpoint or action too:

```csharp
app.MapPost("/docs/publish", PublishAsync)
   .RequireSurfacePolicy("docs.publish");
```

## API Reference

- `AddRazorWireAspNetCoreAuth()` registers `IRazorWireAuthResultProvider` as a scoped provider backed by `IAppSurfaceAspNetCorePolicyEvaluator`.

## Pitfalls

- UI projection is not authorization. Hidden UI does not protect endpoints.
- Register host authentication, authorization, AppSurface auth mapping, and RazorWire before relying on projected states.
- Use `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` only for local Development proof. Render `AppSurfaceDevAuthMarker` separately from `rw:auth-view`.
- Do not put production identity payloads, secrets, tokens, raw emails, or arbitrary auth metadata in projected UI.
