# RazorWire Stream Authorization

RazorWire stream endpoints are safe by default. A request must pass channel validation, stream authorization, and
single-process admission before the endpoint writes `text/event-stream`, subscribes to the hub, or consumes an admission
lease.

Use `IRazorWireStreamAuthorizer` for new user, tenant, or workflow-sensitive streams:

```csharp
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.RazorWire.Streams;

public sealed class TenantStreamAuthorizer : IRazorWireStreamAuthorizer
{
    public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
    {
        var tenantId = context.HttpContext.User.FindFirst("tenant_id")?.Value;

        if (tenantId is null)
        {
            return new ValueTask<AppSurfaceAuthResult>(AppSurfaceAuthResult.Unauthenticated());
        }

        return context.Channel == $"tenant:{tenantId}:updates"
            ? new ValueTask<AppSurfaceAuthResult>(AppSurfaceAuthResult.Allowed())
            : new ValueTask<AppSurfaceAuthResult>(AppSurfaceAuthResult.Forbidden());
    }
}
```

Register it before or after `AddRazorWire`:

```csharp
services.AddSingleton<IRazorWireStreamAuthorizer, TenantStreamAuthorizer>();
services.AddRazorWire();
```

## Result Mapping

| Result outcome | HTTP status before SSE | Development body | Production body |
|---|---:|---|---|
| `Allowed` | none; continues to admission and SSE | none | none |
| `Challenge` / `Unauthenticated` | `401` | Safe `Problem`, `Cause`, `Fix`, `Docs` text | empty |
| `Forbid` / `Forbidden` | `403` | Safe `Problem`, `Cause`, `Fix`, `Docs` text | empty |
| `SetupFailure` / `MissingPolicy` / `MissingServices` / `MissingSubject` | `500` | Safe setup-failure text | empty |
| `UnsafeNavigation` / `UnsafeReturnUrl` | `400` | Safe unsafe-navigation text | empty |
| `StaleOrUnknownSession` | `401` | Safe stale-session text | empty |

`null` results, authorizer exceptions, and non-request cancellations fail closed as setup failures. Request-aborted
cancellation stops quietly without producing a false denial log.

## Trust Boundary

RazorWire treats `AppSurfaceAuthResult.Message`, arbitrary `Metadata`, claims, channel names, return URLs, and exception
messages as untrusted. Development diagnostics and logs are synthesized from safe enum/status/configuration facts such as
outcome, reason, status code, configured authorization mode, authenticated flag, authorizer type, exception type, and
channel length. Production denial responses are empty.

The response mapper is intentionally not host-overridable in v1. Register a different `IRazorWireStreamAuthorizer` to
change authorization decisions while keeping non-leaky status mapping.

## DI Precedence

| Registration shape | Effective stream authorization |
|---|---|
| No custom authorizer | Built-in bool authorizer selected by `RazorWireOptions.Streams.AuthorizationMode`, adapted to results. |
| `IRazorWireChannelAuthorizer` before or after `AddRazorWire` | Legacy bool authorizer is adapted to `Allowed` or `Forbidden`. |
| `IRazorWireStreamAuthorizer` before `AddRazorWire` | Result authorizer suppresses the bool adapter and wins. |
| `IRazorWireStreamAuthorizer` after `AddRazorWire` | Result authorizer wins through last-registration behavior. |
| Both result and bool authorizers | The result authorizer wins unless it explicitly delegates. |
| `AddAppSurfaceDocs` | Docs installs a result-aware harvest wrapper and a legacy bool facade over the same decision. |
| Custom result or bool authorizer after `AddAppSurfaceDocs` | Advanced replacement mode; the host owns harvest-channel safety. |

## Host Policy Recipe

RazorWire does not call `ChallengeAsync`, `ForbidAsync`, redirect, mutate cookies, register authentication schemes, or
evaluate ASP.NET Core authorization policies. Keep host-policy evaluation in the host or in
`ForgeTrust.AppSurface.Auth.AspNetCore`, then return the resulting `AppSurfaceAuthResult` from
`IRazorWireStreamAuthorizer`.

If host policy services, policy names, or subject mapping are missing, return a setup-failure result such as
`AppSurfaceAuthResult.MissingPolicy()`, `MissingServices()`, or `MissingSubject()`. RazorWire maps those to `500` before
SSE so the failure is visible during development and non-leaky in production.

## EventSource Retry Caveat

Native `EventSource` does not expose failed response status codes or bodies to application JavaScript, and browsers may
retry mounted stream sources after `401`, `403`, or `500` responses. Use the browser Network tab plus server log event
`13700 StreamSubscriptionDenied` during development. RazorWire logs low-cardinality denial facts and omits raw channel
names, claim values, return URLs, app messages, metadata, and exception messages.
