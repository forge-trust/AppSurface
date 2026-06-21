# AppSurface Auth ASP.NET Core OIDC Example

This example proves `ForgeTrust.AppSurface.Auth.AspNetCore.Oidc` can register named cookie and OIDC schemes without owning host defaults or requiring a live identity provider for local diagnostics.

Run it:

```bash
dotnet run --project examples/auth-aspnetcore-oidc --urls http://127.0.0.1:5058
```

From another terminal:

```bash
curl -s http://127.0.0.1:5058/
curl -s http://127.0.0.1:5058/diagnostics/oidc-options
```

Expected diagnostic shape:

```json
{
  "cookieScheme": "AppSurface.Cookies",
  "oidcScheme": "AppSurface.Oidc",
  "subjectClaim": "sub",
  "callbackPath": "/signin-appsurface-oidc",
  "signedOutCallbackPath": "/signout-callback-appsurface-oidc",
  "saveTokens": false,
  "hasAuthority": true,
  "hasClientId": true,
  "hasClientSecret": true
}
```

The placeholder authority and client settings are for local registration proof only. Real applications should supply provider values through configuration, register callback URLs with the provider, keep secrets out of source control, and keep `UseAuthentication()` before `UseAuthorization()`.

The example calls `AddAuthorization()` because authorization services and middleware remain host-owned ASP.NET Core setup. The OIDC convenience package does not insert middleware or create authorization policies for the host.
