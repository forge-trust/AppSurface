# ForgeTrust.AppSurface.Web.Push

`ForgeTrust.AppSurface.Web.Push` is the optional safe rail between AppSurface Web's [shared PWA worker](../ForgeTrust.AppSurface.Web/Docs/pwa-install.md) and an application-owned notification product. It handles bounded browser subscription intake, VAPID key custody, [RFC 8291](https://www.rfc-editor.org/rfc/rfc8291) encryption, one-attempt push-service requests, safe response classification, and conditional stale-subscription cleanup. The host retains identity, tenants, preferences, persistence, recipients, send timing, and retry policy.

Use this package for direct standards-based Web Push when the app will own subscription storage and notification policy. Prefer a hosted provider for managed audiences, multi-channel orchestration, analytics, fan-out, or retry operations. Use an app-owned protocol implementation only when its extra control justifies owning encryption, VAPID, SSRF protection, and push-service failure semantics.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## Five-minute local proof

The canonical [web-pwa-install example](../../examples/web-pwa-install/README.md) contains an explicit example-only key generator. It never generates keys at startup and is not package API.

```bash
dotnet run --project examples/web-pwa-install -- --generate-vapid-keys
dotnet user-secrets init --project examples/web-pwa-install
dotnet user-secrets set --project examples/web-pwa-install WebPush:Keys:Primary:PublicKey "<public>"
dotnet user-secrets set --project examples/web-pwa-install WebPush:Keys:Primary:PrivateKey "<private>"
dotnet user-secrets set --project examples/web-pwa-install WebPush:Keys:Primary:Subject "mailto:push@example.test"
dotnet user-secrets set --project examples/web-pwa-install WebPush:AllowedPushServiceOrigins:0 "https://fcm.googleapis.com"
DOTNET_ENVIRONMENT=Development dotnet run --project examples/web-pwa-install
```

Open the sample, select the Push Admin DevAuth persona, register the worker, and choose **Enable notifications**. The Viewer persona is deliberately forbidden. The protected host action runs the package sender against a Development-only proof transport that returns HTTP 201 without making a network request, and always leaves **Push delivery** at **Not proven**.

The sample registers `AddAppSurfaceWebPushDevelopmentProofTransport(environment)` after the normal package registration. This public proof seam is restricted to `Development`, replaces only the outbound transport, and retains package validation, encryption, single-attempt behavior, and response classification. It throws outside Development. Use it only in canonical examples or local integration proofs; never treat its HTTP 201 as push-service or browser-delivery evidence.

Choose the exact origin for the browser under test: Chromium commonly uses `https://fcm.googleapis.com`, Firefox uses `https://updates.push.services.mozilla.com`, and Safari uses `https://web.push.apple.com`. These are explicit starting values, not wildcards; if a browser vendor changes its endpoint origin, add that reviewed exact origin before intake rather than weakening validation.

## Registration and protected mapping

Install this optional package and enable the existing shared worker separately:

```csharp
services.AddAppSurfaceWebPush(options =>
{
    options.ActiveVapidKeyId = "primary-2026";
    options.VapidKeys["primary-2026"] = new AppSurfaceWebPushVapidKeyOptions
    {
        Subject = "mailto:push@example.test",
        PublicKey = configuration["WebPush:Keys:Primary:PublicKey"],
        PrivateKey = configuration["WebPush:Keys:Primary:PrivateKey"],
    };
    options.AllowedPushServiceOrigins.Add("https://updates.push.services.mozilla.com");
});
services.AddScoped<IAppSurfaceWebPushSubscriptionCustody, AppPushSubscriptionCustody>();
services.AddScoped<IAppSurfaceWebPushBearerTokenValidator, AppPushBearerTokenValidator>();
webOptions.Pwa.Push.Enabled = true;
```

`AllowedPushServiceOrigins` is required and accepts 1-16 exact normalized HTTPS default-port origins. Wildcards, paths, queries, fragments, userinfo, custom ports, percent escapes, and an arbitrary-HTTPS fallback are rejected. The allowlist is checked at startup, intake, and immediately before send.

Service registration maps nothing. Choose one protection mode:

```csharp
app.MapAppSurfaceWebPushSubscriptions(
    "/account/push-subscriptions",
    authorizationPolicy: "push.manage",
    rateLimiterPolicy: "push.subscription-writes");

// Token-only alternative; ambient cookies cannot satisfy this rail.
app.MapAppSurfaceWebPushBearerSubscriptions(
    "/api/push-subscriptions",
    authorizationPolicy: "push.manage",
    rateLimiterPolicy: "push.subscription-writes");
```

Map these fixed application endpoints on the application-root `WebApplication` builder. The mapping methods
reject `RouteGroupBuilder` instances because a group prefix would move the package-owned client asset away
from its documented fixed path.

Both methods return `void`. Every handler requires an authenticated principal and directly evaluates the named policy before configuration, antiforgery, parsing, or custody; inherited `AllowAnonymous` cannot bypass it. The cookie policy must declare exactly one authentication scheme, which the package evaluates before issuing antiforgery tokens or accepting writes.

Bearer mapping requires a nonblank HTTP `Authorization: Bearer` credential and exactly one app registration of `IAppSurfaceWebPushBearerTokenValidator`. The package parses the header, passes only the sensitive token value to `ValidateAsync`, requires the returned principal to be authenticated, and evaluates the named policy against that principal. Implement the validator with the app's JWT, opaque-token, or equivalent validation library; validate issuer, audience, signature, lifetime, and revocation as appropriate. Return `null` for rejected credentials, propagate caller cancellation, and never log the token. The validator must not fall back to cookies, `HttpContext.User`, or any other ambient credential. Missing or failing validation and missing policies fail closed with `ASPUSH108`; rejected or malformed credentials return 401.

The mapped base path must be a literal app-root-relative path. Route parameters, catch-alls, `.` or `..` traversal segments, encoded paths, query strings, and fragments are rejected. Reserved-space and duplicate checks are case-insensitive, matching ASP.NET Core routing; map separate literal rails when a host needs more than one custody boundary.

The optional `rateLimiterPolicy` is a host-owned named ASP.NET Core rate-limiter policy and is applied internally to configuration, PUT, and DELETE. Register the policy with `AddRateLimiter` and enable `UseRateLimiter`; omit the argument when the host deliberately provides no endpoint limiter. Rate-limiter middleware runs before the package's direct handler authentication. Bearer policies must therefore use pre-authentication inputs such as remote address and route, unless the host has already established that exact bearer principal before `UseRateLimiter`. Never partition on subscription material, and do not treat rate limiting as a replacement for authorization or antiforgery.

Render the external CSP-compatible client (`script-src 'self'`) and prepare without prompting:

```razor
<appsurface:web-push-client />
```

```javascript
const preparation = await window.AppSurface.Pwa.prepare({
  endpoint: "/account/push-subscriptions"
});

button.addEventListener("click", () => {
  if (preparation.status === "prepared") {
    // Keep this call directly in the gesture handler.
    void window.AppSurface.Pwa.subscribe({ prepared: preparation.handle });
  }
});
```

The endpoint is relative to the application root, not the origin root. When the host uses an ASP.NET Core
`PathBase`, render the value through `Url.Content("~/account/push-subscriptions")` or an equivalent
server-owned path-base helper before passing it to the client.

Bearer hosts pass the same callback to both preparation and unsubscription; the callback may refresh a token and receives the operation signal:

```javascript
const authorization = async ({ signal }) => `Bearer ${await tokens.getAccessToken({ signal })}`;
const preparation = await window.AppSurface.Pwa.prepare({
  endpoint: "/api/push-subscriptions",
  authorization
});
await window.AppSurface.Pwa.unsubscribe({
  endpoint: "/api/push-subscriptions",
  authorization
});
```

`prepare()` never requests permission. Its opaque handle expires after five minutes and is consumed once. `subscribe()` calls `PushManager.subscribe()` before its first await. A key mismatch returns `vapid-key-migration-required`: offer disable, prepare again, then a second explicit enable action. `unsubscribe()` removes server custody before browser unsubscription.

## Custody and sending

`IAppSurfaceWebPushSubscriptionCustody` receives the authenticated `ClaimsPrincipal`; derive app user and tenant keys there. Never reassign another principal's endpoint. `MarkTerminalAsync` receives the complete subscription snapshot and must compare-and-mark so a stale 404/410 cannot retire a replacement.

```csharp
var result = await sender.SendAsync(new AppSurfaceWebPushSendRequest(
    subscription,
    new AppSurfaceWebPushNotification(
        "Invoice ready",
        body: "Open the account to review it.",
        destinationPath: "/account/invoices"),
    new AppSurfaceWebPushSendOptions(
        timeToLiveSeconds: 3600,
        urgency: AppSurfaceWebPushUrgency.Normal,
        topic: "invoice")), cancellationToken);
```

One call makes at most one request. `Accepted` means exactly HTTP 201; it is not delivery/display proof. Only 404/410 produce `TerminalSubscription` and cleanup. HTTP 408, 429, 5xx, timeout, and network failure are transient. Other 4xx responses are rejected without cleanup. Redirects, unexpected 2xx, and protocol failures are `ProtocolFailure`. Missing retained keys return `VapidKeyUnavailable`; disallowed origins return `PushServiceNotAllowed` without network. The host owns retry policy.

Payload version 1 matches the worker: title 1-256 characters; body up to 2,048; tag up to 128; safe app-root-relative icon, badge, and destination paths; and at most 3,993 UTF-8 bytes. TTL is 1 second through 28 days. Topic is 1-32 URL-safe base64 characters.

## Rotation, privacy, and pitfalls

Add and retain the new key, deploy, switch `ActiveVapidKeyId`, migrate users through explicit disable plus a second enable action, and remove the old key only when no records reference it. Rollback changes only the active ID while both pairs remain retained.

- Never log VAPID private keys, endpoints, `p256dh`, `auth`, bearer tokens, payloads, or response bodies. Sensitive public models redact `ToString()`.
- The authenticated 16 KiB PUT body is the one intentional serialization of subscription material.
- Store keys in user-secrets, environment configuration, or a production secret provider. Never commit them.
- iOS/iPadOS require an installed Home Screen app and a direct gesture. Never prompt on page load or preparation.
- Acceptance does not prove receipt or display. This package has no database, audience, preference UI, campaign, scheduler, fan-out, retry loop, readiness score, or telemetry.

HTTP problems use `ASPUSH100`-`ASPUSH109`; configuration uses `ASPUSHCFG`; sending uses `ASPUSHSEND`; browser invariants use `ASPUSHJS`. Correct the named configuration, validator, or policy; refresh preparation after key changes; or reconcile custody. Never recover by weakening token validation, authorization, antiforgery, or the origin allowlist.

### Browser and HTTP contract

`GET {base}/configuration` returns schema version 1, the active public VAPID key and ID, plus either antiforgery metadata or `requestProtection: "bearer"`. `PUT {base}` accepts exactly `{ schemaVersion, endpoint, keys: { p256dh, auth }, vapidKeyId }`; `DELETE {base}` accepts exactly `{ schemaVersion, endpoint }`. Writes are limited to 16 KiB. Successful writes return 204. Problems are safe `ProblemDetails`: `ASPUSH100` malformed JSON, `101` invalid subscription, `102` media type, `103` body size, `104` antiforgery, `105` custody rejected, `106` endpoint ownership conflict, `107` custody unavailable, `108` configuration or authentication/authorization services unavailable, and `109` stale VAPID key.

Stable preparation results are `prepared`, `vapid-key-migration-required`, `unsupported`, `unauthorized`, `forbidden`, `authorization-failed`, `worker-registration-failed`, `browser-subscription-failed`, `network-failed`, `configuration-failed`, and `invalid-response`. Mutation results add `subscribed`, `already-subscribed`, `unsubscribed`, `already-unsubscribed`, `permission-denied`, `permission-dismissed`, `browser-unsubscribe-failed`, `vapid-key-stale`, `antiforgery-failed`, `custody-conflict`, `custody-failed`, and `operation-in-progress`. Every result contains a `retryable` boolean; hosts should branch on `status`, never exception text.

## Dependency decision

The package uses `Lib.Net.Http.WebPush` 3.3.1 behind an internal adapter for RFC 8291 `aes128gcm` encryption and [RFC 8292](https://www.rfc-editor.org/rfc/rfc8292) `vapid` signing. AppSurface disables automatic 429 retries and redirects, strips/disposes response content without reading it, and exposes no third-party types or exceptions. See [third-party notices](THIRD-PARTY-NOTICES.md).
