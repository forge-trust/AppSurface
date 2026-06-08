# AppSurface Auth ASP.NET Core Bridge Example

This example proves `ForgeTrust.AppSurface.Auth.AspNetCore` can consume a normal ASP.NET Core authentication and authorization setup without owning it.

The host still configures:

- `AddAuthentication(...)`
- `AddAuthorization(...)`
- `UseAuthentication()`
- `UseAuthorization()`
- the named `OperatorsOnly` policy

The AppSurface adapter adds only:

- `AddAppSurfaceAspNetCoreAuth(...)`
- `IAppSurfaceAspNetCorePolicyEvaluator`
- AppSurface auth context/result mapping

Run it:

```bash
dotnet run --project examples/auth-aspnetcore-bridge --urls http://127.0.0.1:5057
```

From another terminal:

```bash
curl -s -H 'X-Proof-User: operator' http://127.0.0.1:5057/allowed
curl -s -H 'X-Proof-User: viewer' http://127.0.0.1:5057/forbidden
curl -s http://127.0.0.1:5057/unauthenticated
curl -s -H 'X-Proof-User: operator' http://127.0.0.1:5057/missing-policy
curl -s -H 'X-Proof-User: nosub' http://127.0.0.1:5057/missing-subject
curl -s http://127.0.0.1:5057/missing-services
```

Expected outcomes:

| Request | Outcome | Reason |
| --- | --- | --- |
| `/allowed` with `X-Proof-User: operator` | `Allowed` | `None` |
| `/forbidden` with `X-Proof-User: viewer` | `Forbid` | `Forbidden` |
| `/unauthenticated` without a header | `Challenge` | `Unauthenticated` |
| `/missing-policy` | `SetupFailure` | `MissingPolicy` |
| `/missing-subject` with `X-Proof-User: nosub` | `SetupFailure` | `MissingSubject` |
| `/missing-services` | `SetupFailure` | `MissingServices` |

The fake `X-Proof-User` authentication scheme is only for local proof. Real applications should keep using their existing ASP.NET Core authentication handlers and policies.
