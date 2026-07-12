# AppSurface Auth Aspire Keycloak Web Proof

This web app uses `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` directly. It does not reference `ForgeTrust.AppSurface.Auth.Aspire.Keycloak`; the AppHost package only supplies local provider configuration.

Run through the paired AppHost:

```bash
aspire run --apphost examples/auth-aspire-keycloak-apphost/AuthAspireKeycloakAppHost.csproj -- local
```

Open `http://localhost:5059`, sign in with local Keycloak, and view `/auth/proof/result`.

Probe endpoints:

- `/auth/proof/status` returns unauthenticated JSON before login and authenticated JSON after login.
- `/auth/proof/protected` returns an OIDC challenge before login.
- `/auth/proof/admin` requires the seeded `admin` user's `appsurface_role` claim.

Seeded local-only users:

| User | Password |
| --- | --- |
| `admin` | `appsurface-admin-local-only` |
| `viewer` | `appsurface-viewer-local-only` |
