# Static Auth Projection

RazorWire auth helpers are passive UI projection. They do not authorize endpoints, sign users in, sign users out, or
turn build-time identity into runtime identity. Static export treats those helpers as public artifact boundaries:
protected `rw:auth-allowed` content is never written to CDN or hybrid output.

During export, RazorWire sends `X-RazorWire-Static-Export: auth-anonymous-v1` to the crawled app. RazorWire auth helpers
use that non-secret request marker only to downgrade output into a static-safe anonymous projection. The marker never
grants access.

## Safe `rw:auth-view`

Use `rw:auth-view` when the exported page can show a generic public fallback. The fallback must be explicit.

```cshtml
<rw:auth-view policy="docs.publish">
    <rw:auth-allowed>
        <button type="submit">Publish</button>
    </rw:auth-allowed>
    <rw:auth-anonymous>
        <rw:login-link href="/login" return-url-policy="current-path">Sign in</rw:login-link>
    </rw:auth-anonymous>
</rw:auth-view>
```

Static export emits the anonymous fallback and blocks the allowed slot. Built-in default helper text does not count as an
explicit static fallback for protected views.

## Gates

`rw:permission-gate` and default `rw:auth-gate` are allowed-only gates. In v0 they fail static export because they have
no explicit fallback child shape.

Replace allowed gates on exported routes with `rw:auth-view` plus an explicit `rw:auth-anonymous` fallback, keep the
protected UI live/server-rendered only, or remove the route from static export.

## Login And Logout

`rw:login-link` can appear in static output only when its target is a host-owned safe local or same-origin URL.
`rw:logout-button` is rejected in static export v0 because it renders a runtime POST form and may include anti-forgery or
return-url data.

## Choosing A Hosting Shape

| Goal | Use |
| --- | --- |
| Public static page with a sign-in prompt | `rw:auth-view` with explicit `rw:auth-anonymous` fallback |
| Protected UI that must render for signed-in users | Live/server-rendered route; do not export the protected route |
| Static shell with live RazorWire behavior | `--mode hybrid`, with the same static auth artifact checks |
| Private site behind infrastructure auth | Put access control in front of the dynamic app; do not rely on static HTML hiding |

Hybrid mode preserves live behavior for supported runtime surfaces, but it does not permit private HTML or sensitive auth
metadata on disk.

## Diagnostics

Static auth projection failures use `RWEXPORT010` with a stable reason label. Diagnostics name the route, artifact kind,
helper kind when available, and a fix. They do not print policy names, subjects, claims, persona names, provider metadata,
auth messages, or DevAuth evidence by default.

### auth-missing-fallback

The exporter found a protected `rw:auth-view` without an explicit static anonymous fallback.

Fix: add a generic `rw:auth-anonymous` fallback, move protected UI behind live rendering, or remove the route from static
export.

### auth-private-content

The exporter found allowed-only auth UI that would become public static output.

Fix: replace allowed gates with `rw:auth-view` plus an explicit anonymous fallback, keep the content server-rendered only,
or do not export the route.

### auth-unsafe-metadata

The exporter found evaluated auth outcome or provider metadata in a static artifact.

Fix: remove auth metadata from rendered markup and let RazorWire emit only static-safe auth markers.

### auth-diagnostics

The exporter found auth diagnostics such as policy or reason attributes in static output.

Fix: disable auth diagnostics for exported routes.

### auth-artifact-leak

The exporter found development, test persona, or auth marker content in a generated text artifact.

Fix: disable DevAuth or test persona UI for exported routes, or exclude the route from static export.
