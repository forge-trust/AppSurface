# AppSurface ASP.NET Core DevAuth Example

This example proves `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` can give a local package consumer a fake, visible persona lab without configuring an external identity provider. DevAuth activates only in `Development` by default and can opt in additional local/proof environments through `AllowedEnvironmentNames`.

DevAuth is local tooling. It is not production authentication, OIDC, ASP.NET Identity, a user store, a durable app-user mapping layer, or the Auth.Testing integration-test harness.

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether this local persona proof, Auth.Testing, OIDC, or raw ASP.NET Core authentication is the right next step.

The root page renders `AppSurfaceDevAuthMarker` without wrapping it in an environment check. The renderer returns an empty string outside allowed environments, so host layouts can call it unconditionally. With default styles, the marker is a fixed bottom-right overlay above 640 CSS pixels and participates in normal document flow at widths up to and including 640 CSS pixels. It starts collapsed, then expands when you need to select `Local Admin`, `Local Viewer`, or `Clear`. Those controls post to the DevAuth control endpoints and return to the same page, so the fake auth state remains visible while you use the app.

The example supplies the host-owned viewport meta tag and renders the marker after the page header but before `<main>`. That location lets the narrow-screen marker reserve space and push application content instead of covering it. The example also supplies narrow-screen outer margin through `AdditionalCssClass`; AppSurface cannot choose spacing that fits every host layout. Consumers can use the same option for a higher-specificity placement override, or set `IncludeDefaultStyles = false` and own the complete skin and responsive behavior. Fixed, absolutely positioned, clipped, or overriding host containers are outside the default non-obstruction guarantee. See the package's [responsive placement and customization guidance](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#responsive-placement-and-customization) for recipes and troubleshooting.

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

Or start from the app page and use the marker:

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

The verifier checks the viewport tag, the recommended header-marker-main ordering, the package responsive declarations, and the DevAuth persona flow.
