# AppSurface ASP.NET Core DevAuth Example

This example proves `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` can give a local package consumer a fake, visible persona lab without configuring an external identity provider. DevAuth activates only in `Development` by default and can opt in additional local/proof environments through `AllowedEnvironmentNames`.

DevAuth is local tooling. It is not production authentication, OIDC, ASP.NET Identity, a user store, a durable app-user mapping layer, or the Auth.Testing integration-test harness.

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether this local persona proof, Auth.Testing, OIDC, or raw ASP.NET Core authentication is the right next step.

The root page renders `AppSurfaceDevAuthMarker` as a persistent in-app overlay without wrapping it in an environment check. The renderer returns an empty string outside allowed environments, so host layouts can call it unconditionally. Selecting `Local Admin`, `Local Viewer`, or `Clear` from the overlay posts to the DevAuth control endpoints and returns to the same page, so the fake auth state remains visible while you use the app. Consumers can skin that marker by passing `AppSurfaceDevAuthMarkerOptions`, including `CssClassPrefix`, `AdditionalCssClass`, and `IncludeDefaultStyles = false`.

To opt in a host-owned proof environment, configure the package explicitly:

```csharp
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.AllowedEnvironmentNames.Add("Staging");
    // personas...
});
```

Run it:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project examples/auth-aspnetcore-dev-auth -- --urls http://127.0.0.1:5058
```

Open the control page:

```bash
# macOS
open http://127.0.0.1:5058/_appsurface/dev-auth
# Linux
xdg-open http://127.0.0.1:5058/_appsurface/dev-auth
# Windows (PowerShell)
Start-Process http://127.0.0.1:5058/_appsurface/dev-auth
```

Or start from the app page and use the overlay:

```text
http://127.0.0.1:5058/
```

Or prove the flow with curl:

```bash
cookie="$(curl -is -X POST -L http://127.0.0.1:5058/_appsurface/dev-auth/select/admin | awk 'tolower($0) ~ /^set-cookie: \.appsurface\.devauth\.persona=/{sub(/^[^:]*: /,""); sub(/;.*/,""); print; exit}')"
curl -s -H "Cookie: $cookie" http://127.0.0.1:5058/api/auth-proof
cookie="$(curl -is -X POST -L http://127.0.0.1:5058/_appsurface/dev-auth/select/viewer | awk 'tolower($0) ~ /^set-cookie: \.appsurface\.devauth\.persona=/{sub(/^[^:]*: /,""); sub(/;.*/,""); print; exit}')"
curl -s -H "Cookie: $cookie" http://127.0.0.1:5058/api/auth-proof
```

Expected outcomes:

| State | `/api/auth-proof` outcome |
| --- | --- |
| `admin` selected | HTTP 200 with `{"result":"allowed","subject":"admin-1"}` |
| `viewer` selected | HTTP 403 AppSurface ProblemDetails with `Forbid` / `Forbidden` |
| persona cleared | HTTP 401 AppSurface ProblemDetails with `Challenge` / `Unauthenticated` |

Run the verifier:

```bash
bash examples/auth-aspnetcore-dev-auth/verify.sh
```
