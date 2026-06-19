# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- `ForgeTrust.AppSurface.Auth` now defines durable external-subject to app-user-id mapping contracts without taking on user-store, ASP.NET Core, OIDC, EF Core, Aspire, or tenant-authority responsibilities.

## Included in the next coordinated version

### Release and docs surface

- Added `ExternalSubject`, `AppUserId`, `IAppSurfaceUserIdentityResolver`, `AppSurfaceUserIdentityResolutionContext`, `AppSurfaceUserIdentityResult`, and `AppSurfaceUserIdentityStatus`, with README guidance for uniqueness, idempotency, cancellation, concurrency, PII-safe diagnostics, and ASP.NET Core adapter integration planning.

## Migration watch

- `AppSurfaceUser.Id` remains a host-owned subject identifier in the existing ASP.NET Core adapter. Consumers that need durable app-owned users should resolve that subject through the new identity resolver contract instead of treating the mapped subject claim as an app user id.
