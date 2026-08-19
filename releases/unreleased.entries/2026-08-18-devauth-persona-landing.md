<!-- appsurface:unreleased-entry section="included" -->
### DevAuth persona recovery targets

- [`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth`](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#persona-landing-urls) now lets a seeded local persona declare an optional safe rooted `LandingUrl`. Selection honors an explicit host `returnUrl` first, then the selected persona landing URL, then the existing control-page or marker fallback. Invalid configured landing URLs fail registration with `ASDEV007`; they never silently redirect to `/`. Clear-persona behavior, cookies, endpoints, status JSON, and production-auth boundaries are unchanged.
