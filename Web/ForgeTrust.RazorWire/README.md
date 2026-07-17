# RazorWire

RazorWire lets ASP.NET Core MVC apps update UI by returning Razor fragments from the server instead of building a separate JSON endpoint and client-state rendering loop.

## 60-Second Quickstart

AppSurface has not published the public `v0.1` package set yet, so the copy-paste path today is repo-local:

1. Clone this repository and use the .NET 10 SDK.
1. Run the MVC sample:

```bash
dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj
```

1. Open the URL printed in the console and navigate to `/Reactivity`.

Wait for the `Permanent Island` card to load, then click the `+` button. The `Instance Score` and `Session Score` update in place without a full-page reload.

When consuming package builds from a configured feed, reference `ForgeTrust.RazorWire` first and then continue at [Add the Module](#add-the-module). Public NuGet install commands will replace this note when the `v0.1` publishing path is live.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## Hero Proof

`examples/razorwire-mvc/Views/Shared/Components/Counter/Default.cshtml`

<!-- appsurface:snippet id="razorwire-counter" file="examples/razorwire-mvc/Views/Shared/Components/Counter/Default.cshtml" marker="razorwire-counter" lang="cshtml" -->
```cshtml
<div id="counter-widget" class="p-4 bg-white border border-slate-100 rounded-xl shadow-sm flex items-center justify-between group">
    <div class="flex gap-6">
        <div class="space-y-0.5">
            <span class="text-[10px] font-bold text-slate-400 uppercase tracking-widest">Instance Score</span>
            <div id="instance-score-value" class="text-2xl font-black text-indigo-600 tabular-nums">@Model</div>
        </div>
        <div class="space-y-0.5">
            <span class="text-[10px] font-bold text-slate-400 uppercase tracking-widest">Session Score</span>
            <div id="session-score-value" class="text-2xl font-black text-indigo-400 tabular-nums">0</div>
        </div>
    </div>

    <form asp-controller="Reactivity" asp-action="IncrementCounter" method="post" rw-active="true" data-counter-form>
        <input type="hidden" name="clientCount" id="client-count-input" value="0" />
        <button type="submit" aria-label="Increment counter" class="h-10 w-10 bg-indigo-600 text-white rounded-lg flex items-center justify-center hover:bg-indigo-700 active:scale-90 transition-all shadow-sm shadow-indigo-100">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 4v16m8-8H4"></path></svg>
        </button>
    </form>
</div>
```
<!-- /appsurface:snippet -->

`examples/razorwire-mvc/Controllers/ReactivityController.cs`

<!-- appsurface:snippet id="razorwire-increment-counter" file="examples/razorwire-mvc/Controllers/ReactivityController.cs" marker="razorwire-increment-counter" lang="csharp" -->
```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public IActionResult IncrementCounter([FromForm] int clientCount)
{
    CounterViewComponent.Increment();
    clientCount++;

    if (Request.IsTurboRequest())
    {
        return this.RazorWireStream()
            .Update(
                "instance-score-value",
                CounterViewComponent.Count.ToString())
            .Update("session-score-value", clientCount.ToString())
            .ReplacePartial(
                "client-count-input",
                "_CounterInput",
                clientCount)
            .BuildResult();
    }

    // Safe redirect
    var referer = Request.Headers["Referer"].ToString();

    return Url.IsLocalUrl(referer) ? Redirect(referer) : RedirectToAction(nameof(Index));
}
```
<!-- /appsurface:snippet -->

`examples/razorwire-mvc/Views/Reactivity/_CounterInput.cshtml`

<!-- appsurface:snippet id="razorwire-counter-input" file="examples/razorwire-mvc/Views/Reactivity/_CounterInput.cshtml" marker="razorwire-counter-input" lang="cshtml" -->
```cshtml
<input type='hidden' name='clientCount' id='client-count-input' value='@Model' />
```
<!-- /appsurface:snippet -->

Read the [focused proof path](../../examples/razorwire-mvc/README.md#start-here-return-razor-fragments) for the file-by-file walkthrough. If copying this pattern gives you a bare `400 Bad Request`, anti-forgery is the first thing to check. See [Security & Anti-Forgery](Docs/antiforgery.md).

The source-backed snippets in this README are generated from `docs:snippet` markers in the sample app. After changing marked sample code, run:

```bash
# From the repository root:
dotnet run --project tools/ForgeTrust.AppSurface.MarkdownSnippets/ForgeTrust.AppSurface.MarkdownSnippets.csproj -- generate
```

For failed submissions, RazorWire also ships a convention-based form UX stack: default form-local fallbacks for unhandled failures, server helpers for validation errors, anti-forgery diagnostics in development, and styling/event hooks for consumers. See [Failed Form UX](Docs/form-failures.md) or run the sample and visit `/Reactivity/FormFailures`.

## Behavior Kit in 3 Minutes

Use RazorWire behavior kit when app-owned JavaScript needs to enhance server-rendered DOM without duplicate document listeners after Turbo visits, frame replacement, partial updates, or repeated bundle evaluation.

```cshtml
<rw:scripts behavior-kit="true" />
<script src="~/js/page-behaviors.js" asp-append-version="true"></script>
```

```js
window.RazorWire.behaviors.register({
  name: "demo.preview",
  selector: "[data-demo-preview]",
  connect(root, context) {
    const button = context.query("[data-demo-preview-button]");
    const output = context.query("[data-demo-preview-output]");
    if (!button || !output) return;

    button.addEventListener("click", () => {
      output.textContent = new Date().toLocaleTimeString();
    }, { signal: context.signal });
  }
});
```

Behavior kit is eager-only in v1. Plain `<rw:scripts />` lazy-loads built-in page navigation, section copy, and form interactions when their markup is present; it does not infer app behavior registration from generic markers. Use built-in managers for package-owned behavior, islands for component/module hydration, and behavior kit for small lifecycle-safe progressive enhancement on replaceable server DOM. See the full contract in [Behavior Kit](Docs/behavior-kit.md).

## Form Interactions in 3 Minutes

Use RazorWire form interactions when a server-rendered form needs conditional fields or one-dimensional model-bound collection rows without page-local JavaScript.

```cshtml
<input type="checkbox" name="ExpectedNoAction" rw-form-toggle="draft-action" rw-form-toggle-invert="true" />

<fieldset rw-form-toggle-target="draft-action" rw-form-toggle-disable-when-hidden="true">
    <input type="hidden" name="Actions.index" value="0" />
    <label for="Actions_0__Title">Title</label>
    <input name="Actions[0].Title" id="Actions_0__Title" />
</fieldset>

<div rw-form-collection="Actions" rw-form-collection-label="action">
    <fieldset rw-form-collection-row rw-form-index="0">
        <input type="hidden" name="Actions.index" value="0" />
        <input name="Actions[0].Title" id="Actions_0__Title" />
        <button rw-form-collection-duplicate>Duplicate</button>
        <button rw-form-collection-remove>Remove</button>
    </fieldset>

    <template rw-form-collection-template>
        <fieldset rw-form-collection-row>
            <input type="hidden" name="Actions.index" value="__index__" />
            <input name="Actions[__index__].Title" id="Actions___index____Title" />
        </fieldset>
    </template>

    <button rw-form-collection-add>Add action</button>
</div>
```

RazorWire owns local behavior, state attributes, sparse `.index` allocation, and accessibility status hooks. Your app owns fields, labels, layout, styling, persistence, and server validation. See the full contract in [Form Interactions](Docs/form-interactions.md).

## Behavior Kit Reference

Use RazorWire Behavior Kit when an app needs small local JavaScript that follows RazorWire's lifecycle without becoming a frontend framework. Root behaviors enhance replaceable DOM roots; lifecycle behaviors run once per logical browser visit.

Behavior Kit is explicit in v1:

```cshtml
<rw:scripts behavior-kit="true" />
```

Root-scoped behaviors bind app-owned controls and clean up with `AbortSignal`:

```html
<section data-install-card>
    <button type="button" data-install-button>Install app</button>
</section>

<script>
window.RazorWire.behaviors.register({
  name: "app.install-card",
  selector: "[data-install-card]",
  connect(root, context) {
    context.query("[data-install-button]")?.addEventListener("click", () => {
      root.setAttribute("data-install-requested", "true");
    }, { signal: context.signal });
  }
});
</script>
```

Lifecycle behaviors cover page-owned browser signals that should not depend on a fake root selector:

```html
<script>
window.RazorWire.behaviors.registerLifecycle({
  name: "app.pwa-display-mode",
  connect(context) {
    const installed =
      window.matchMedia("(display-mode: standalone)").matches ||
      window.matchMedia("(display-mode: fullscreen)").matches ||
      window.matchMedia("(display-mode: minimal-ui)").matches ||
      window.navigator.standalone === true;

    document.dispatchEvent(new CustomEvent("app:pwa-display-mode-seen", {
      detail: {
        displayMode: installed ? "installed" : "browser",
        renderKind: context.renderKind
      }
    }));
  }
});
</script>
```

Use built-in RazorWire managers for package-owned page navigation, section copy, and form interactions. Use Behavior Kit for app-owned root behaviors or logical-visit instrumentation. Use islands for component/module hydration. See [Behavior Kit](Docs/behavior-kit.md) for diagnostics, lifecycle events, and troubleshooting.

## Page Navigation in 3 Minutes

Use RazorWire page navigation when a server-rendered page needs same-page section links, active state, and optional mobile-panel close behavior without app-specific JavaScript.

```cshtml
<nav rw-page-nav aria-label="Page sections">
    <button rw-page-nav-toggle="page-sections-panel" aria-expanded="true">Sections</button>
    <div id="page-sections-panel" rw-page-nav-panel>
        <a rw-page-nav-link href="#overview">Overview</a>
        <a rw-page-nav-link href="#pricing">Pricing</a>
    </div>
</nav>

<section id="overview" style="scroll-margin-top: 6rem">...</section>
<section id="pricing" style="scroll-margin-top: 6rem">...</section>
```

RazorWire renders stable `data-rw-page-nav*` attributes, updates `aria-current="location"` on the active link, reveals the active link inside visible overflowing vertical nav surfaces, mirrors panel state through `data-rw-page-nav-panel-state`, and leaves all layout and styling to the host application. This replaces common brochure-site hooks such as `.page-scroll`, `data-bs-spy`, active classes, and `.navbar-collapse` close scripts. See the full contract and migration table in [Page Navigation](Docs/page-navigation.md), or run the MVC sample and visit `/Navigation/PageNavigation`.

## Section Copy in 3 Minutes

Use RazorWire section copy when long-form pages need "copy link to this section" behavior without host-specific clipboard JavaScript.

```cshtml
<h2 id="install" data-rw-section-copy-target="true">Install</h2>

<h2 id="usage">Usage</h2>
<button type="button" data-rw-section-copy="usage" data-rw-section-copy-title="Usage">
    Copy link
</button>
```

RazorWire resolves ids document-wide, writes copied/fallback state through stable `data-rw-section-copy*` hooks, announces feedback through an optional live status region, and renders an inline readonly-input fallback when clipboard writes are unavailable. See the full contract in [Section Copy](Docs/section-copy.md).

## Generated UI Design Contract

RazorWire should feel like a quiet enhancement inside the host application, not like a separate visual product placed on top of it. Package-owned generated UI follows the [RazorWire generated UI design contract](DESIGN.md).

Use that contract when adding or styling RazorWire-generated nodes such as form feedback, stream status affordances, or package-owned fallback UI. It defines the scope boundary, data-attribute and CSS custom-property styling surface, accessibility baseline, override model, and anti-patterns. It does not apply to app-authored forms, partials, layouts, or AppSurface Docs chrome.

## Add the Module

Once you already reference the RazorWire package in your app, add `RazorWireWebModule` to your root module:

```csharp
public class MyRootModule : IAppSurfaceWebModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<RazorWireWebModule>();
    }
}
```

## Enable TagHelpers and Scripts

RazorWire markup only lights up when your views import the package TagHelpers and your shared layout renders the client scripts once. Without this step, `rw:island`, `rw:stream-source`, and `rw-active` forms fall back to plain HTML behavior.

`examples/razorwire-mvc/Views/_ViewImports.cshtml`

<!-- appsurface:snippet id="razorwire-view-imports" file="examples/razorwire-mvc/Views/_ViewImports.cshtml" marker="razorwire-view-imports" lang="cshtml" -->
```cshtml
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@addTagHelper *, ForgeTrust.RazorWire
```
<!-- /appsurface:snippet -->

`examples/razorwire-mvc/Views/Shared/_Layout.cshtml`

<!-- appsurface:snippet id="razorwire-scripts" file="examples/razorwire-mvc/Views/Shared/_Layout.cshtml" marker="razorwire-scripts" lang="cshtml" -->
```cshtml
<rw:scripts/>
```
<!-- /appsurface:snippet -->

Plain `<rw:scripts/>` is enough for page navigation, section copy, and form interactions. RazorWire emits small detectors that load `page-navigation.js` only when the rendered page contains `rw-page-nav` / `data-rw-page-nav` markup, `section-copy.js` only when it contains `data-rw-section-copy` / `data-rw-section-copy-target` markup, and `form-interactions.js` only when it contains `data-rw-form-toggle` or `data-rw-form-collection` markup, including after Turbo page or frame renders. The optional `page-navigation="true"`, `section-copy="true"`, and `form-interactions="true"` attributes are eager-load escape hatches, but they are not required for normal adoption. Behavior Kit is explicit in v1; set `behavior-kit="true"` when the page registers `window.RazorWire.behaviors`.

App-authored behavior kit registration is different: use `<rw:scripts behavior-kit="true" />` when app bundles call `window.RazorWire.behaviors.register(...)` or `window.RazorWire.behaviors.registerLifecycle(...)`. Behavior kit has no v1 lazy marker or synthetic static-export reference.

### Choose who supplies Turbo

RazorWire defaults to `RazorWireTurboRuntimeMode.Bundled`: `<rw:scripts />` emits the package's exact Turbo 8.0.12 UMD build from the same origin before the RazorWire scripts. This is the recommended mode because a normal package install works without an external CDN request, additional CSP origin, or host-managed load-order step.

Use `Custom` only when the app publishes a compatible Turbo build as its own same-origin static asset:

```csharp
services.AddRazorWire(options =>
{
    options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.Custom;
    options.Turbo.CustomPath = "/js/turbo.es2017-umd.js";
});
```

`CustomPath` must be an app-absolute same-origin path. RazorWire rejects query strings, fragments, percent encoding, dot segments, whitespace, protocol-relative paths, and HTML-sensitive characters, then applies the current `PathBase` and ASP.NET Core static-asset versioning. Do not use `Custom` for a CDN URL.

Use `HostManaged` when the host must own a cross-origin URL, subresource integrity, CSP metadata, or other script attributes:

```csharp
services.AddRazorWire(options =>
{
    options.Turbo.RuntimeMode = RazorWireTurboRuntimeMode.HostManaged;
});
```

In host-managed mode, RazorWire emits no Turbo script. Load the host's Turbo script before `<rw:scripts />` without `async` or `defer`; `window.Turbo` must already exist when RazorWire executes. RazorWire verifies compatibility with Turbo 8.0.12 only. Other versions are host-owned compatibility experiments and should be covered by the app's browser tests.

When migrating from the former CDN-backed default, remove any duplicate Turbo tag and keep the default bundled mode. A CSP can then allow the runtime through `'self'` rather than `https://cdn.jsdelivr.net`. Choose one owner—bundled, custom, or host-managed—because loading Turbo twice can register duplicate navigation listeners and produce nondeterministic page visits.

## Configure Services (Optional)

You can customize RazorWire behavior via `RazorWireOptions`:

```csharp
services.AddRazorWire(options =>
{
    options.Streams.BasePath = "/custom-stream-path";
    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.DenyAll;
    options.Streams.MaxChannelNameLength = 128;
    options.Streams.MaxLiveChannels = 64;
    options.Streams.MaxLiveSubscriptions = 256;
    options.Streams.MaxLiveSubscriptionsPerChannel = 32;
    options.Forms.FailureMode = RazorWireFormFailureMode.Auto;
    options.Forms.DefaultFailureMessage = "We could not submit this form. Check your input and try again.";
});
```

Stream subscriptions are denied by default. Choose `AllowAll` only for public/demo streams with finite channel names and
explicit per-process limits:

```csharp
services.AddRazorWire(options =>
{
    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
    options.Streams.MaxLiveChannels = 8;
    options.Streams.MaxLiveSubscriptions = 100;
    options.Streams.MaxLiveSubscriptionsPerChannel = 25;
});
```

These limits are admission guardrails for one ASP.NET Core application process. They are not tenant quotas, user quotas,
IP fairness, load-balancer limits, or cluster-wide counters. Use ASP.NET Core rate limiting, reverse-proxy connection
limits, SignalR, or managed pub/sub when public traffic needs client fairness, distributed fanout, groups, durable
delivery, or cross-node capacity planning.

For user, tenant, or workflow-specific streams, prefer a result-bearing stream authorizer:

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

services.AddSingleton<IRazorWireStreamAuthorizer, TenantStreamAuthorizer>();
services.AddRazorWire();
```

Add one stream source that uses the same finite channel scheme:

```razor
<rw:stream-source channel="tenant:@Model.TenantId:updates" replay="true" />
```

Run the app, sign in as a user with a matching `tenant_id` claim, and open the page. Allowed requests open the
Server-Sent Events stream. Unauthenticated requests return `401`, forbidden tenant mismatches return `403`, setup
failures return `500`, unsafe navigation returns `400`, and stale sessions return `401` before any SSE headers, hub
subscription, or admission lease. In Development, RazorWire writes safe `Problem`, `Cause`, `Fix`, and `Docs` text; in
Production, denial bodies stay empty.

Legacy bool authorizers still work and are useful for simple allow/deny compatibility:

```csharp
public sealed class TenantBoolStreamAuthorizer : IRazorWireChannelAuthorizer
{
    public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        var tenantId = context.User.FindFirst("tenant_id")?.Value;
        return new ValueTask<bool>(tenantId is not null && channel == $"tenant:{tenantId}:updates");
    }
}

services.AddSingleton<IRazorWireChannelAuthorizer, TenantBoolStreamAuthorizer>();
services.AddRazorWire();
```

Use `IRazorWireStreamAuthorizer` when diagnostics need unauthenticated, forbidden, stale-session, unsafe-navigation, or
setup-failure outcomes. Use `IRazorWireChannelAuthorizer` only when a plain allow/deny answer is enough. Registration
order follows Microsoft DI single-service resolution:

| Registration shape | Effective stream authorization |
|---|---|
| No custom authorizer | Built-in bool authorizer selected by `RazorWireOptions.Streams.AuthorizationMode`, adapted to results. |
| `IRazorWireChannelAuthorizer` before or after `AddRazorWire` | Legacy bool authorizer is adapted to `Allowed` or `Forbidden`. |
| `IRazorWireStreamAuthorizer` before `AddRazorWire` | Result authorizer suppresses the bool adapter and wins. |
| `IRazorWireStreamAuthorizer` after `AddRazorWire` | Result authorizer wins through last-registration behavior. |
| Both result and bool authorizers | The result authorizer wins unless it explicitly delegates to a bool authorizer. |
| `AddAppSurfaceDocs` | Docs installs a result-aware harvest wrapper and a legacy bool facade over the same decision. |
| Custom result or bool authorizer after `AddAppSurfaceDocs` | Advanced replacement mode; the host owns harvest-channel safety. |

For ASP.NET Core host policies, keep policy evaluation in the host or `ForgeTrust.AppSurface.Auth.AspNetCore`, then
return the resulting `AppSurfaceAuthResult` from `IRazorWireStreamAuthorizer`. RazorWire does not call
`ChallengeAsync`, `ForbidAsync`, redirect, mutate cookies, register schemes, or evaluate policies itself.

Package-owned sensitive streams may impose stricter rules than the global RazorWire `AllowAll` shortcut. For example, AppSurface Docs harvest progress requires a custom host authorizer outside Development even when the host exposes docs harvest health routes.

## Also Possible

- Keep sidebars and other regions independent with `rw:island`, including lazy loading and `permanent="true"` persistence across page transitions.
- Push live updates to connected clients with `IRazorWireStreamHub` and `rw:stream-source`.
- Return form updates from normal MVC controllers with `this.RazorWireStream()`, not a separate JSON API.
- See the broader [RazorWire MVC Example](../../examples/razorwire-mvc/README.md) for registration, message publishing, islands, and SSE.
- See [Failed Form UX](Docs/form-failures.md) for server failure conventions, customization, and diagnostics.
- See [Security & Anti-Forgery](Docs/antiforgery.md) for the form-update patterns that matter in production.

## Core Concepts

### Islands

Islands are isolated regions of a page that can load, reload, or update independently. RazorWire renders them as Turbo Frames, so you can decompose a page into smaller Razor-backed units without introducing a separate frontend app.

### Streams and SSE

RazorWire can push Turbo Stream updates to one or more clients over Server-Sent Events. That makes it a good fit for counters, feeds, presence lists, and other UI that should update live while staying server-rendered.

RazorWire can also send a narrow same-origin visit command with `Visit(...)`. Visit streams are one-shot navigation commands, not replayable state. Use them for active subscribers only, and keep normal links or retained state available when late subscribers or no-JavaScript users need to continue.

The in-memory stream hub keeps live subscription tracking separate from opt-in replay retention. Empty live channel tracking is released after the last subscriber disconnects or publish-time cleanup prunes stale writers. Retained replay buffers are not deleted by live disconnects; they stay bounded by the replay retention policy.

Before a request reaches the hub, the RazorWire endpoint applies single-process admission guardrails:

- Channel names, after URL decoding, may contain only ASCII letters, digits, `.`, `_`, `-`, and `:` and must fit `MaxChannelNameLength`.
- Invalid or overlong channels return `400` before custom authorizers run.
- Authorization denials return `403` and do not consume admission capacity.
- Capacity denials return `429` before SSE headers are written and before `IRazorWireStreamHub.Subscribe(...)` is called.
- `MaxLiveChannels`, `MaxLiveSubscriptions`, and `MaxLiveSubscriptionsPerChannel` are per-process limits. One browser tab or live SSE request consumes one subscription slot.

### Text vs Trusted HTML in Stream Templates

RazorWire stream templates have a deliberate trust boundary:

| API | Template content handling | Use when |
|---|---|---|
| `Append`, `Prepend`, `Replace`, `Update` | Treats `content` as plain text and HTML-encodes it. `null` renders as empty text. | Updating counters, status labels, validation targets, and any caller-supplied text. |
| `AppendHtml`, `PrependHtml`, `ReplaceHtml`, `UpdateHtml` | Writes `trustedHtml` directly. RazorWire does not encode or sanitize it. | Inserting small server-authored fragments where every user value has already been encoded. |
| `AppendPartial`, `PrependPartial`, `ReplacePartial`, `UpdatePartial` | Renders a Razor partial through MVC. | Returning app markup that belongs in a `.cshtml` partial. |
| `AppendComponent`, `PrependComponent`, `ReplaceComponent`, `UpdateComponent` | Renders a view component through MVC. | Returning reusable app markup with component logic. |
| `new RazorWireStreamResult(rawContent)` and `IRazorWireStreamHub.PublishAsync(channel, content)` | Raw whole-stream boundaries. Content is transported as-is. | Advanced integrations that already built trusted Turbo Stream markup. |

Prefer the text APIs by default:

```csharp
return this.RazorWireStream()
    .Update("status", displayName)
    .BuildResult();
```

If `displayName` is `<b>Ada</b>`, the browser receives text, not markup:

```html
<template>&lt;b&gt;Ada&lt;/b&gt;</template>
```

For app-authored markup, prefer Razor-rendered helpers:

```csharp
return this.RazorWireStream()
    .ReplacePartial("profile-card", "_ProfileCard", model)
    .BuildResult();
```

Use `*Html` only when the string is already trusted and caller-owned. Encode user values before composing the fragment:

```csharp
var encodedName = System.Net.WebUtility.HtmlEncode(displayName);

return this.RazorWireStream()
    .UpdateHtml("status", $"<p>Saved {encodedName}.</p>")
    .BuildResult();
```

### Form Enhancement

Standard HTML forms can return targeted stream updates instead of full reloads or redirect-first flows. The counter example above is the smallest version of that story: submit a normal MVC form, return RazorWire updates, and change only the DOM you care about.

When `EnableFailureUx` is enabled, `form[rw-active]` also marks enhanced form posts with `X-RazorWire-Form: true` and `__RazorWireForm=1`. That gives the runtime and server adapters enough context to render useful failed-submission UX without every controller hand-rolling client glue.

## Security & Anti-Forgery

Handling anti-forgery tokens correctly is critical when updating forms via Turbo Streams. See [Security & Anti-Forgery](Docs/antiforgery.md) for the detailed patterns and recommendations.

RazorWire stream subscriptions are also safe by default: `RazorWireOptions.Streams.AuthorizationMode` starts at `RazorWireStreamAuthorizationMode.DenyAll`, so `rw:stream-source` receives `401` for anonymous callers or `403` for authenticated callers until the app either opts into `RazorWireStreamAuthorizationMode.AllowAll` for public/demo channels, registers `IRazorWireStreamAuthorizer`, or keeps a legacy `IRazorWireChannelAuthorizer` for simple allow/deny compatibility. Development responses include safe plain-text diagnostics for `401`, `403`, `500`, `400`, and admission `429`; production denials stay generic and logs avoid raw channel names, user identifiers, and claim values.

Native `EventSource` does not expose failed HTTP response bodies or status codes to application JavaScript. RazorWire dispatches `razorwire:stream:error` from registered `rw-stream-source` elements when the browser reports a stream error. Use that event for client-side diagnostics, and use the browser Network tab plus server log event `13700 StreamSubscriptionDenied` for authorization denials (`401`/`403`/`500`/`400`) and `13701 StreamAdmissionRejected` for validation and capacity rejections (`400`/`429`) during development.

RazorWire also emits low-cardinality `System.Diagnostics.Metrics` instruments for stream admission: `razorwire.stream.live_subscriptions`, `razorwire.stream.live_channels`, and `razorwire.stream.admission.rejections` tagged by rejection reason. These metrics are process-local diagnostics for tuning limits; they are not an abuse-mitigation layer.

Stream builder text APIs encode template content by default. The `*Html` builder methods, `RazorWireStreamResult(string?)`, and `IRazorWireStreamHub.PublishAsync(channel, content)` are trusted boundaries: they do not encode or sanitize raw markup. Keep user-controlled values in text APIs, Razor partials, or view components unless you explicitly encode them before composing trusted HTML.

Development anti-forgery failures from RazorWire forms are rewritten into helpful form-local diagnostics when possible. Production responses stay safe and generic. See [Failed Form UX](Docs/form-failures.md#development-diagnostics).

## Development Experience

RazorWire is designed for a fast feedback loop during development:

- Razor Runtime Compilation is automatically enabled in `Development`, so you can edit `.cshtml` files and refresh without rebuilding.
- Local scripts and styles automatically receive version hashes for cache busting, even without `asp-append-version="true"`.

## API Reference

### `RazorWireBridge`

- `Frame(controller, id, viewName, model)` returns a partial view wrapped in a `<turbo-frame>` with the specified ID.
- `FrameComponent(controller, id, componentName)` renders a view component inside a `<turbo-frame>`.

### `IRazorWireStreamHub`

- `PublishAsync(channel, content)` broadcasts a trusted Turbo Stream fragment to every subscriber on a channel. The hub transports `content` as-is; it does not encode or sanitize template content.
- `PublishAsync(channel, content, new RazorWireStreamPublishOptions { Replay = true })` broadcasts the trusted fragment and retains it in the channel's bounded replay buffer.
- `Subscribe(channel)` receives only live messages published after subscription.
- `Subscribe(channel, new RazorWireStreamSubscribeOptions { Replay = true })` receives retained replay messages first, then continues with live messages.

Replay is opt-in and intentionally small. The in-memory hub keeps up to 25 retained fragments per replay channel and prunes inactive replay channels when more than 256 replay channels are retained, dropping the oldest retained fragments and inactive replay channels first. Replay subscriptions to channels with no retained messages do not allocate durable per-channel replay metadata. Use replay for idempotent state snapshots, progress indicators, and other "latest known UI" streams where a late subscriber should catch up. Do not use replay for one-time commands, sensitive personal data, secrets, or unbounded event logs.

Endpoint admission is separate from hub delivery. The default endpoint validates channel names, authorizes with
`IRazorWireStreamAuthorizer`, and admits a live subscription before calling `Subscribe`. Apps that call a custom
`IRazorWireStreamHub` directly do not get endpoint admission automatically.

### `RazorWireStreamOptions`

- `BasePath`: endpoint base path; defaults to `/_rw/streams`. It must be an absolute path without a trailing slash, route tokens, query string, fragment, whitespace, or control characters.
- `AuthorizationMode`: defaults to `DenyAll`.
- `MaxChannelNameLength`: defaults to `128` decoded characters.
- `MaxLiveChannels`: defaults to `64` live channel names per process.
- `MaxLiveSubscriptions`: defaults to `256` live SSE requests per process.
- `MaxLiveSubscriptionsPerChannel`: defaults to `32` live SSE requests for one channel per process.

All numeric admission limits must be greater than zero and are validated at startup. Raising them increases connection,
memory, and request-slot pressure in the current process; it does not create distributed fairness.

### `RazorWireTurboOptions`

`RazorWireOptions.Turbo` controls the Turbo script emitted by `<rw:scripts />`:

- `RuntimeMode` defaults to `RazorWireTurboRuntimeMode.Bundled` (`0`). `Custom` is `1`; `HostManaged` is `2`.
- `CustomPath` is required only for `Custom` and must otherwise be `null`.
- Invalid modes, paths, and mode/path combinations fail during options validation at startup.

See [Choose who supplies Turbo](#choose-who-supplies-turbo) for configuration, ownership guidance, ordering requirements, and migration pitfalls.

### `IRazorWireStreamAuthorizer`

- `AuthorizeAsync(RazorWireStreamAuthorizationContext)` returns a passive `AppSurfaceAuthResult`.
- `Allowed` continues to admission and SSE; `Challenge` maps to `401`; `Forbid` maps to `403`; `SetupFailure` maps to
  `500`; `UnsafeNavigation` maps to `400`; `StaleOrUnknownSession` maps to `401`.
- RazorWire treats app-supplied `AppSurfaceAuthResult.Message` and `Metadata` as untrusted. Development diagnostics are
  synthesized from safe enum/status/configuration facts; production bodies stay empty.
- The response mapper is not an extension point in v1. Customize authorization decisions by registering a different
  `IRazorWireStreamAuthorizer`.
- See [Stream Authorization](Docs/stream-authorization.md) for the full status matrix, DI precedence, logging caveats,
  and host-policy recipe.

### `IRazorWireStreamAuthorizationFilter`

- `AuthorizeAsync(RazorWireStreamAuthorizationContext)` runs before the active stream authorizer.
- Return `null` when the filter does not apply to the requested channel.
- Return a denial or setup-failure result to stop the subscription before SSE; return `Allowed` to let later filters and
  the active `IRazorWireStreamAuthorizer` continue.
- Use filters for package-owned or reserved channels that need a non-bypassable gate while still allowing the host
  stream authorizer to narrow access.

### `IRazorWireChannelAuthorizer`

- `CanSubscribeAsync(HttpContext, channel)` decides whether the current request may subscribe to a stream channel.
- The built-in `DenyAllRazorWireChannelAuthorizer` is selected by default through `RazorWireOptions.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.DenyAll`.
- `AllowAllRazorWireChannelAuthorizer` is selected by `RazorWireOptions.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll` and should only be used for public/demo streams.
- Register a custom implementation only when a plain allow/deny answer is enough. New user, tenant, or workflow streams
  should generally use `IRazorWireStreamAuthorizer` for richer denial semantics.

### `RazorWireStreamAuthorizationMode`

- `DenyAll = 0`: default; every subscription returns `403` unless a custom `IRazorWireStreamAuthorizer` or legacy
  `IRazorWireChannelAuthorizer` is registered.
- `AllowAll = 1`: permits every subscription; intended for public/demo streams only.
- Unknown enum values fail with a clear configuration exception instead of falling through to an unsafe mode.

### `this.RazorWireStream()` (controller extension)

- `Append(target, content)` adds HTML-encoded text to the end of the target element.
- `Prepend(target, content)` adds HTML-encoded text to the beginning.
- `Replace(target, content)` replaces the target element entirely with HTML-encoded text.
- `Update(target, content)` replaces the inner content of the target with HTML-encoded text.
- `AppendHtml(target, trustedHtml)`, `PrependHtml(target, trustedHtml)`, `ReplaceHtml(target, trustedHtml)`, and `UpdateHtml(target, trustedHtml)` insert trusted HTML without encoding or sanitizing. Use partials/components for app markup when practical, and encode user values before composing trusted fragments.
- `Remove(target)` removes the target element.
- `FormError(target, title, message)` updates the target with an encoded generated error block and marks the response handled.
- `FormValidationErrors(target, ModelState, title, maxErrors, message)` updates the target with a stable MVC validation summary and marks the response handled.
- `Visit(url)` emits `<turbo-stream action="rw-visit" url="..." visit-action="advance"></turbo-stream>` and asks the browser to run a same-origin Turbo visit that advances history.
- `Visit(url, RazorWireVisitAction.Replace)` emits the same command with `visit-action="replace"` so Turbo replaces the current history entry.
- `BuildResult(statusCode)` returns the stream and optionally sets the HTTP status code.

`Visit(...)` accepts relative URLs such as `/docs/next`, `?tab=done`, `#summary`, `./next`, `../next`, and same-origin absolute URLs. The server rejects blank URLs and ASCII control characters before rendering; the browser runtime rejects `~/` URLs, protocol-relative URLs, external origins, `javascript:`, `data:`, backslash-prefixed values, and malformed input before calling Turbo. Do not retain or replay `rw-visit` streams through `IRazorWireStreamHub`; publish a separate idempotent state stream when late subscribers need context.

## TagHelpers

### `rw:island`

Wraps content in a `<turbo-frame>`.

- `id`: unique identifier for the island.
- `src`: URL to load content from.
- `loading`: load strategy such as `lazy`.
- `permanent`: persists the element across Turbo page transitions.
- `swr`: enables stale-while-revalidate behavior.
- `client-module`: client module specifier to mount for hybrid islands. Use a relative path, root-relative path, same-origin URL, explicit HTTPS URL, or bare import-map specifier.
- `client-strategy`: mount timing such as `load`, `visible`, or `idle`.
- `client-props`: JSON payload passed to the client module's mount function.

```html
<rw:island id="sidebar" src="/Reactivity/Sidebar" loading="lazy" permanent="true">
    <p>Loading sidebar...</p>
</rw:island>
```

### `form[rw-active]`

Enhances a normal form so Turbo handles the submission and optional frame targeting.

- `rw-active="true"` enables RazorWire form handling.
- `rw-target` sets the target frame when you want to constrain the response.
- `data-rw-form-failure-target` points failed-submission UI at a local error container by simple element ID, optionally prefixed with `#`; selector-like values are ignored.
- `data-rw-form-failure="auto"` uses the default fallback UI, `manual` only dispatches events, and `off` disables the failure convention for that form.
- Generated hidden fields `__RazorWireForm` and, when possible, `__RazorWireFormFailureTarget` help server-side adapters identify and localize form failures.

```html
<form asp-controller="Reactivity" asp-action="IncrementCounter" method="post" rw-active="true">
    <input type="hidden" name="clientCount" value="0" />
    <button type="submit" aria-label="Increment counter">+</button>
</form>
```

### `rw:stream-source`

Subscribes the page to a RazorWire stream channel.

- `channel`: required channel name.
- `permanent`: keeps the stream source alive across Turbo visits.
- Stream endpoints deny subscriptions by default; configure `RazorWireStreamAuthorizationMode.AllowAll` only for public/demo channels, provide a custom `IRazorWireStreamAuthorizer` for result-aware authorization, or keep `IRazorWireChannelAuthorizer` only for simple legacy allow/deny compatibility.
- Channel names, after URL decoding, may contain only ASCII letters, digits, `.`, `_`, `-`, and `:`. The tag helper URL-encodes the generated path segment. Direct requests whose channel path segment decodes to spaces, slashes, query/hash characters, control characters, Unicode, or another invalid channel character are rejected with `400`; unencoded extra path segments can miss the stream route and return `404`, while query strings and fragments are not part of the channel route value.
- `replay`: when `true`, appends `?replay=1` to the stream endpoint so the page receives retained channel messages before live updates. The in-memory hub retains at most 25 messages per replay channel and prunes inactive replay channels when more than 256 replay channels are retained.
- Listen for `razorwire:stream:error` on the element when you need client-side diagnostics for failed native `EventSource` connections. The event detail includes `channel`, `source`, `state`, `readyState`, and `src`; it intentionally does not include server response bodies.

```html
<rw:stream-source id="rw-stream-reactivity" channel="reactivity" permanent="true"></rw:stream-source>
```

Use `replay="true"` when the source powers resumable UI state, such as a build or harvest progress surface. Leave it off for purely live feeds where old messages would be confusing.

### `requires-stream`

Marks an element as inactive until a named stream is connected.

```html
<button type="submit" requires-stream="reactivity">Send</button>
```

### `<time rw-type="local">`

Localizes UTC timestamps on the client with the browser's `Intl` APIs.

- `rw-display`: `time`, `date`, `datetime`, or `relative`.
- `rw-format`: `short`, `medium`, `long`, or `full`.

```html
<time datetime="@Model.Timestamp" rw-type="local" rw-display="relative"></time>
```

### `rw:scripts`

Injects the client scripts RazorWire needs. By default this includes the same-origin bundled Turbo 8.0.12 runtime followed by the RazorWire assets; `RazorWireOptions.Turbo` can select a custom same-origin runtime or host-managed ownership.

```html
<rw:scripts />
```

The script tag also carries failed-form runtime configuration derived from `RazorWireOptions.Forms`; no inline configuration script is required.

### `rw:auth-view`

Projects a passive auth result into one server-rendered slot. Child slots include `rw:auth-allowed`,
`rw:auth-anonymous`, `rw:auth-forbidden`, `rw:auth-setup-failure`, `rw:auth-unsafe-navigation`, `rw:auth-stale`, and
`rw:auth-unknown`.

```html
<rw:auth-view policy="docs.publish">
  <rw:auth-allowed>Allowed content</rw:auth-allowed>
  <rw:auth-anonymous>Sign in prompt</rw:auth-anonymous>
</rw:auth-view>
```

Static export forces auth projection into a public anonymous/fallback state and never writes protected allowed content to
disk. Exported protected views need an explicit `rw:auth-anonymous` fallback. See
[Static Auth Projection](Docs/static-auth-projection.md).

### `rw:auth-gate` and `rw:permission-gate`

Conditionally render child content when a projected policy reaches the requested state. `rw:permission-gate` is an
allowed-state alias for policy-oriented markup.

```html
<rw:auth-gate policy="docs.publish" state="forbidden">
  You do not have permission.
</rw:auth-gate>
```

### `rw:login-link` and `rw:logout-button`

Render host-owned sign-in and sign-out routes without executing auth side effects. `return-url-policy="current-path"`
adds the current local path as `returnUrl`. `rw:logout-button` emits a POST form and includes an ASP.NET Core
anti-forgery hidden input for local or same-origin actions when `IAntiforgery` is available from the host.

```html
<rw:login-link href="/login" return-url-policy="current-path">Sign in</rw:login-link>
<rw:logout-button action="/logout" return-url-policy="current-path">Sign out</rw:logout-button>
```

## Utilities

### `StringUtils`

- `ToSafeId(input, appendHash)` sanitizes values for DOM IDs or anchors and can append a deterministic hash for uniqueness.

## Client-Side Interop

RazorWire also supports hybrid islands where a server-rendered region mounts a client module:

```html
<rw:island id="interactive-chart"
           client-module="ChartComponent"
           client-strategy="visible"
           client-props='{ "data": [1, 2, 3] }'>
</rw:island>
```

`client-module` can be a relative path, root-relative path, same-origin URL, explicit HTTPS URL, or bare import-map specifier. Hosts that prefer logical names can set `window.RazorWireIslandModules = { ChartComponent: "/js/chart-component.js" }` before hydration; RazorWire resolves the rendered `data-rw-module` name through that host-provided specifier map before calling dynamic `import()`. The manifest is not a security allowlist and does not allow inline module bytes.

```js
// /wwwroot/js/chart-component.js
export function mount(root, props) {
  root.textContent = `chart points: ${props.data.length}`;
}
```

## Project Auth Results Into UI

RazorWire auth helpers render passive UI states from `ForgeTrust.AppSurface.Auth` results. They do not sign users in,
sign users out, select authentication schemes, mutate cookies, challenge, forbid, redirect, or protect endpoints. Use
them when a page should show the same host-owned policy result that an endpoint enforces.

Register the ASP.NET Core adapter package in hosts that want RazorWire to evaluate AppSurface-shaped ASP.NET Core
policy results:

```csharp
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.RazorWire.Auth.AspNetCore;

builder.Services.AddRazorWire();
builder.Services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim("sub"));
builder.Services.AddRazorWireAspNetCoreAuth();
```

Project the host policy in Razor:

```cshtml
<rw:auth-view policy="docs.publish">
    <rw:auth-allowed>
        <button type="submit">Publish</button>
    </rw:auth-allowed>
    <rw:auth-anonymous>
        <rw:login-link href="/login" return-url-policy="current-path">Sign in</rw:login-link>
    </rw:auth-anonymous>
    <rw:auth-forbidden>
        You do not have permission to publish this page.
    </rw:auth-forbidden>
    <rw:auth-setup-failure>
        Publishing is unavailable right now.
    </rw:auth-setup-failure>
</rw:auth-view>
```

Enforce the same policy on the endpoint or action:

```csharp
app.MapPost("/docs/publish", PublishAsync)
   .RequireSurfacePolicy("docs.publish");
```

`rw:auth-view` emits safe default hooks such as `data-rw-auth-state`, `data-rw-auth-outcome`, and
`data-rw-auth-helper`. Policy names and reason details are emitted only when `include-diagnostics="true"` is set. Raw
`AppSurfaceAuthResult.Message`, arbitrary metadata, persona names, subjects, schemes, and DevAuth state are not rendered
by default.

Static export is stricter: the exporter sends `X-RazorWire-Static-Export: auth-anonymous-v1`, renders only explicit
anonymous fallback output, rejects allowed gates and logout forms, strips outcome details from static auth markers, and
fails with `RWEXPORT010` before writing unsafe text artifacts. See
[Static Auth Projection](Docs/static-auth-projection.md).

Available states are `allowed`, `anonymous`, `forbidden`, `setup-failure`, `unsafe-navigation`,
`stale-or-unknown-session`, and `unknown`. `ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth` remains separate local
tooling; render `AppSurfaceDevAuthMarker` beside a proof page when fake personas should stay visible during
Development.

| Specifier form | Direct `client-module` / `data-rw-module` | `window.RazorWireIslandModules` value |
| --- | --- | --- |
| `./chart-component.js` or `../charts/chart.js` | Allowed | Allowed |
| `/js/chart-component.js` | Allowed | Allowed |
| `https://app.example/js/chart.js` when same-origin, or any explicit `https://cdn.example/chart.js` | Allowed | Allowed |
| `ChartComponent` resolved by an import map | Allowed | Allowed |
| `data:text/javascript,...` | Blocked | Blocked |
| `javascript:`, `blob:`, or `file:` | Blocked | Blocked |
| `//cdn.example/chart.js` | Blocked; use explicit `https://cdn.example/chart.js` | Blocked; use explicit `https://cdn.example/chart.js` |
| Empty or non-string values | Blocked | Blocked |

If an older prototype or test used inline module bytes, move the code into a served module file:

```js
// Before: inline module bytes are blocked.
window.RazorWireIslandModules = {
  ChartComponent: "data:text/javascript,export function mount(root, props) { root.textContent = props.title; }"
};

// After: host the module as a normal static asset.
window.RazorWireIslandModules = {
  ChartComponent: "/js/chart-component.js"
};
```

```js
// /wwwroot/js/chart-component.js
export function mount(root, props) {
  root.textContent = props.title;
}
```

RazorWire serves `/_content/ForgeTrust.RazorWire/razorwire/turbo.es2017-umd.js`,
`razorwire.js`, `razorwire.islands.js`, `behavior-kit.js`, and the package demo assets as normal Razor Class Library
static web assets when the host has a static-web-assets manifest. The same files
are also embedded into the `ForgeTrust.RazorWire` assembly and mapped as endpoint
fallbacks by `RazorWireWebModule`, so packaged command-line hosts can serve the
runtime even when only compiled assemblies are present.

The runtime and island loader are authored under `assets/src` and generated into
the committed `wwwroot/razorwire/*.js` package outputs. Public consumers should
continue loading the same script URLs or `<rw:scripts />`; the TypeScript asset
pipeline is maintainer-only and does not require application code changes. The
docs-only `assets/contracts/razorwire-public-contracts.js` file preserves the
AppSurface Docs JavaScript API harvest, while `exampleJsInterop.js` stays
hand-authored and demo-only. For build commands, diagnostics, the pack-time
freshness guard, and the emergency bypass property, see the
[Runtime Contract Pipeline](Docs/runtime-contract-pipeline.md).

Maintainer note: AppSurface Docs `VerifyEventDispatches` checks only direct
literal `dispatchEvent(new CustomEvent("event:name", ...))` calls in configured
`.js` harvest inputs. The docs-only contract manifest contains public doclet
evidence, not runtime dispatch evidence, and authored RazorWire runtime source
under `assets/src/*.ts` plus helper-dispatched events are outside that v1
verifier boundary.

## Static Export

RazorWire can generate CDN-ready static output with the installable `razorwire`
.NET tool, or with the short-lived `dnx` tool execution path. CDN mode is the
default: extensionless internal routes such as `/about` are emitted as files such
as `about.html`, and exporter-managed links, frames, scripts, stylesheets, images,
`<img>` and `<source>` `srcset` candidates, CSS `url(...)` references, and
string-form CSS `@import "..."` dependencies are rewritten to the generated
artifact URLs. When the conventional
`/_appsurface/errors/404` route is available, it emits `404.html` through the same
validation and rewrite path. Use `--mode hybrid` when the exported directory will
still be served behind infrastructure that resolves application-style extensionless
URLs, live frames, forms, streams, or islands.

Hybrid export can serve RazorWire endpoints through same-origin backend passthrough. Safe RazorWire forms with static anti-forgery tokens are converted to lazy runtime token refresh so the backend wakes only when the user starts interacting with the form or submits it. For split-origin hybrid sites, add `--live-origin https://api.example.com`; RazorWire export then also rewrites managed live surfaces to that origin without requiring per-form flags: stream sources, live island frames, and RazorWire forms with static anti-forgery tokens. Unsafe anti-forgery fails early with `RWEXPORT006` when the form is unmanaged, opts out with `rw-antiforgery="off"`, posts to an external action, or split-origin credentials are omitted. CDN mode fails any anti-forgery surface because a plain static host cannot mint runtime tokens. See [Hybrid Hosting With Cloud Run](Docs/hybrid-hosting.md) for the local proof path, Cloud Run live-origin recipe, CORS setup, and AppSurface Docs export example.

CDN export validates the static references it can discover while crawling.
Missing frame routes, unsafe query-bearing frame sources, missing internal assets,
and managed URLs that cannot be rewritten fail the export with `RWEXPORT###`
diagnostics instead of producing a broken folder. Hybrid export keeps page and
live-behavior routes live, but it still fails missing browser-delivered assets
such as scripts, stylesheets, module preloads, icons, images, `srcset` candidates, CSS `url(...)`,
CSS `@import`, and asset-shaped preload or prefetch hints. The diagnostics include the
HTML element/attribute or CSS token that produced the reference and the normalized
path the exporter attempted to prove. The validation boundary is deliberate:
app-authored JavaScript fetches, form posts, Server-Sent Events, import maps, and
other runtime behavior outside markup/CSS references are not proven static by the
exporter.

Parser-backed discovery may find valid references that older exporter versions
missed. Treat new export validation failures after upgrading as potentially correct:
export the missing route or asset, fix path casing, mark authoring-only anchors
with `data-rw-export-ignore`, or choose `--mode hybrid` when live infrastructure
owns the dependency.

Those package-based commands require a published package or an explicit local
package source. The package chooser excludes `ForgeTrust.RazorWire.Cli` until
issue #171 lands stable public .NET tool packaging.

For installation, `dnx`, local-package, and source-run examples, see the
[RazorWire CLI](../../Web/ForgeTrust.RazorWire.Cli/README.md).

## Examples

- [Focused proof path: return Razor fragments](../../examples/razorwire-mvc/README.md#start-here-return-razor-fragments)
- [Full RazorWire MVC example](../../examples/razorwire-mvc/README.md)
- [Failed Form UX guide](Docs/form-failures.md)
- [Runtime Contract Pipeline](Docs/runtime-contract-pipeline.md)
- [Security & Anti-Forgery](Docs/antiforgery.md)
