# AppSurface Auth Web/RazorWire Proof

This sample is the five-minute proof for `ForgeTrust.AppSurface.Auth.AspNetCore`: one host-owned ASP.NET Core policy flows through AppSurface into both a Minimal API response and a RazorWire-facing rendered state.

Your host owns auth. This sample only changes the local proof persona.

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether this ASP.NET Core adapter proof, DevAuth, OIDC, Auth.Testing, or raw ASP.NET Core authentication is the right next step.

## Run The Proof

From the repository root:

```bash
dotnet run --project examples/auth-web-razorwire-proof/AuthWebRazorWireProofExample.csproj --urls http://127.0.0.1:5058
```

Open <http://127.0.0.1:5058/> and use the persona switch:

| Persona | API status | AppSurface outcome | AppSurface reason | RazorWire-facing state |
| --- | ---: | --- | --- | --- |
| `anonymous` | `401` | `Challenge` | `Unauthenticated` | unauthenticated |
| `viewer` | `403` | `Forbid` | `Forbidden` | forbidden |
| `operator` | `200` | `Allowed` | `None` | allowed |

The two panels should agree for every persona:

- `Minimal API` shows the JSON-facing status, outcome, reason, and subject.
- `RazorWire-facing state` renders the same policy result as page state.

## Curl Parity

The browser switch keeps the selected persona in URL-local proof state. Curl can use `X-Proof-User`; the header wins over URL state so command-line checks stay deterministic.

```bash
curl -i http://127.0.0.1:5058/api/auth-proof
curl -i -H 'X-Proof-User: viewer' http://127.0.0.1:5058/api/auth-proof
curl -i -H 'X-Proof-User: operator' http://127.0.0.1:5058/api/auth-proof
```

Expected response states:

- No header: `401`, `Challenge`, `Unauthenticated`, no subject.
- `viewer`: `403`, `Forbid`, `Forbidden`, subject `viewer-1`.
- `operator`: `200`, `Allowed`, `None`, subject `operator-1`.

Unsupported proof users behave like anonymous requests.

## What This Sample Proves

- The host registers the local proof authentication handler.
- The host owns the `OperatorsOnly` authorization policy and registers it with `AddAppSurfacePolicy(...)`.
- `AddAppSurfaceAspNetCoreAuth(...)` maps the evaluated ASP.NET Core policy result into `AppSurfaceAuthResult`.
- The Minimal API endpoint and RazorWire-facing page use the same `IAppSurfaceAspNetCorePolicyEvaluator.AuthorizeAsync("OperatorsOnly")` decision path.

For production Minimal API endpoints that only need to enforce a host policy and return API-safe auth failures, prefer `RequireSurfacePolicy(...)`. This sample calls the evaluator directly so `/api/auth-proof` can return the same canonical outcome matrix that the RazorWire-facing page renders.

## What This Sample Is Not

- Not production authentication.
- Not OAuth, OIDC, JWT, cookies, ASP.NET Identity, login, or logout guidance.
- Not challenge, forbid, redirect, sign-in, or sign-out execution.
- Not a replacement for `RequireSurfacePolicy(...)` on production Minimal API endpoints.
- Not an `AuthGate`, `AuthView`, `PermissionGate`, or result-bearing RazorWire auth adapter.

Keep the proof-only `ProofAuthenticationHandler`, URL-local proof state, and persona switch inside this sample. Real applications should keep their existing ASP.NET Core authentication handlers and policies, then let AppSurface observe the populated request principal and named host-policy result.

## If Your Result Differs

- Confirm you ran the command from the repository root.
- Confirm the app is listening on `http://127.0.0.1:5058`.
- Confirm the host calls `UseAuthentication()` before `UseAuthorization()`.
- Confirm authenticated proof users have a stable `sub` claim. Missing subjects are setup failures, not forbidden results.
- Confirm the policy name is `OperatorsOnly`; missing policy names are setup failures.

For lower-level adapter diagnostics such as missing policy, missing services, and missing subject, see the [ASP.NET Core auth bridge example](../auth-aspnetcore-bridge/README.md).
