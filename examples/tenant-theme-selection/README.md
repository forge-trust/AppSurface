# Tenant theme selection proof

This development-only host demonstrates the [AppSurface Web host-owned theme-selection adapter](../../Web/ForgeTrust.AppSurface.Web/README.md#host-owned-theme-selection). It intentionally maps two authorized tenant identities to the same sealed `shared-blue` pair while rendering tenant-distinct body content. That proves why a theme-pair id is not a safe response-cache key.

Run the host from the repository root:

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project examples/tenant-theme-selection/TenantThemeSelectionExample.csproj --urls http://127.0.0.1:5188
```

In a second terminal, make three requests:

```bash
curl -sS -H 'X-Proof-Authorized-Tenant: tenant-a' http://127.0.0.1:5188/
curl -sS -H 'X-Proof-Authorized-Tenant: tenant-b' http://127.0.0.1:5188/
curl -sS http://127.0.0.1:5188/
```

The first two responses both contain `data-as-theme="shared-blue"`, while their body markers are respectively `data-tenant-variant="A"` and `data-tenant-variant="B"`. The third has the configured default `data-as-theme="appsurface"` and `data-tenant-variant="default"`. The proof host explicitly sets `Cache-Control: private, no-store` as its conservative application-owned cache policy.

`X-Proof-Authorized-Tenant` is a proof-only stand-in for a context that production code has already authenticated and authorized. The host refuses to start outside `Development` so this header cannot become a deployment recipe. It is not a recipe for trusting a header, claim, route value, or cookie as a tenant selector. The host's `TenantThemeMap` is constructed during startup and rejects null entries, blank or surrounding-whitespace keys, exact ordinal duplicates, and pairs absent from the sealed registry; it deliberately does not trim, case-fold, or otherwise normalize tenant identifiers. Production hosts own the equivalent canonicalization, authorization, mapping validation, and cache policy. If a production host enables output caching after authorization, it must partition by a stable tenant security boundary and invalidate that boundary when the mapping, authorization, or content changes; never partition only by `shared-blue`.

The host returns `false` from the policy when no proof tenant is present, which gives the adapter's deterministic configured-default fallback. A policy must return `true` only for a registered `AppSurfaceThemeId`; empty and unknown values fail closed. Browser-local preferences are deliberately absent because they are an incompatible v1 document-provider adapter. See the package [diagnostics and provider boundary](../../Web/ForgeTrust.AppSurface.Web/README.md#registration-and-provider-boundary) before composing the feature into a host with custom theming.
