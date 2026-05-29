# ForgeTrust.AppSurface.Auth

This package does not authenticate users today.

`ForgeTrust.AppSurface.Auth` is a boundary-preview package for AppSurface module composition. It gives future AppSurface auth contracts a surface-neutral home without depending on ASP.NET Core authentication, authorization policies, identity providers, middleware, endpoint filters, cookies, bearer tokens, or UI.

Use this package only when you are authoring AppSurface modules that need to compose against the future auth boundary. Applications must still configure their host-owned security stack directly, such as ASP.NET Core authentication and authorization in an ASP.NET Core host.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 1 release note](../../releases/v0.1.0-rc.1.md) for current release risk, migration guidance, and package readiness.

## What the package includes

- `AppSurfaceAuthModule`
- `AppSurfaceAuthOptions`
- Microsoft Options registration for `AppSurfaceAuthOptions`

## What the package does not include

- Authentication schemes or handlers
- Cookies, JWT bearer, OAuth, OIDC, or ASP.NET Identity integration
- Authorization policies or policy evaluation
- Middleware, endpoint filters, challenges, or forbids
- User, session, principal, tenant, or authorization-result contracts
- RazorWire, web, or UI behavior

## Composition

Register `AppSurfaceAuthModule` from another AppSurface module when you need the boundary present in the module graph:

```csharp
public void RegisterDependentModules(ModuleDependencyBuilder builder)
{
    builder.AddModule<AppSurfaceAuthModule>();
}
```

That registration only composes the boundary and registers `AppSurfaceAuthOptions`. It has no runtime request effect.

## Why this exists now

AppSurface packages need a shared place for future auth-sensitive contracts that can be used from web, console, docs, RazorWire, and other module families without making the core auth boundary an ASP.NET Core implementation. This package establishes that package and namespace boundary before #416 adds real user, session, or result contracts.

Choose host-specific security packages and platform tools when you need request handling, identity-provider integration, policy enforcement, or route access rules.
