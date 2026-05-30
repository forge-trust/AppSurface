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

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 1 release note](../../releases/v0.1.0-rc.1.md) for current release risk, migration guidance, and package readiness.

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

## Configure Services (Optional)

You can customize RazorWire behavior via `RazorWireOptions`:

```csharp
services.AddRazorWire(options =>
{
    options.Streams.BasePath = "/custom-stream-path";
    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.DenyAll;
    options.Forms.FailureMode = RazorWireFormFailureMode.Auto;
    options.Forms.DefaultFailureMessage = "We could not submit this form. Check your input and try again.";
});
```

Stream subscriptions are denied by default. Choose `AllowAll` only for public/demo streams:

```csharp
services.AddRazorWire(options =>
{
    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
});
```

For user, tenant, or workflow-specific streams, register a custom authorizer instead:

```csharp
public sealed class TenantStreamAuthorizer : IRazorWireChannelAuthorizer
{
    public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        var tenantId = context.User.FindFirst("tenant_id")?.Value;
        return new ValueTask<bool>(tenantId is not null && channel == $"tenant:{tenantId}:updates");
    }
}

services.AddSingleton<IRazorWireChannelAuthorizer, TenantStreamAuthorizer>();
services.AddRazorWire();
```

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

### Form Enhancement

Standard HTML forms can return targeted stream updates instead of full reloads or redirect-first flows. The counter example above is the smallest version of that story: submit a normal MVC form, return RazorWire updates, and change only the DOM you care about.

When `EnableFailureUx` is enabled, `form[rw-active]` also marks enhanced form posts with `X-RazorWire-Form: true` and `__RazorWireForm=1`. That gives the runtime and server adapters enough context to render useful failed-submission UX without every controller hand-rolling client glue.

## Security & Anti-Forgery

Handling anti-forgery tokens correctly is critical when updating forms via Turbo Streams. See [Security & Anti-Forgery](Docs/antiforgery.md) for the detailed patterns and recommendations.

RazorWire stream subscriptions are also safe by default: `RazorWireOptions.Streams.AuthorizationMode` starts at `RazorWireStreamAuthorizationMode.DenyAll`, so `rw:stream-source` receives `403` until the app either opts into `RazorWireStreamAuthorizationMode.AllowAll` for public/demo channels or registers `IRazorWireChannelAuthorizer`. Development responses include a safe plain-text diagnostic; production denials stay generic and logs avoid raw channel names, user identifiers, and claim values.

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

- `PublishAsync(channel, content)` broadcasts a Turbo Stream fragment to every subscriber on a channel.
- `PublishAsync(channel, content, new RazorWireStreamPublishOptions { Replay = true })` broadcasts the fragment and retains it in the channel's bounded replay buffer.
- `Subscribe(channel)` receives only live messages published after subscription.
- `Subscribe(channel, new RazorWireStreamSubscribeOptions { Replay = true })` receives retained replay messages first, then continues with live messages.

Replay is opt-in and intentionally small. The in-memory hub keeps a bounded per-channel buffer and drops the oldest retained fragments first. Use replay for idempotent state snapshots, progress indicators, and other "latest known UI" streams where a late subscriber should catch up. Do not use replay for one-time commands, sensitive personal data, secrets, or unbounded event logs.

### `IRazorWireChannelAuthorizer`

- `CanSubscribeAsync(HttpContext, channel)` decides whether the current request may subscribe to a stream channel.
- The built-in `DenyAllRazorWireChannelAuthorizer` is selected by default through `RazorWireOptions.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.DenyAll`.
- `AllowAllRazorWireChannelAuthorizer` is selected by `RazorWireOptions.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll` and should only be used for public/demo streams.
- Register a custom implementation for auth-context-aware decisions based on `HttpContext.User`, claims, tenant membership, workflow state, or route data.

### `RazorWireStreamAuthorizationMode`

- `DenyAll = 0`: default; every subscription returns `403` unless a custom `IRazorWireChannelAuthorizer` is registered.
- `AllowAll = 1`: permits every subscription; intended for public/demo streams only.
- Unknown enum values fail with a clear configuration exception instead of falling through to an unsafe mode.

### `this.RazorWireStream()` (controller extension)

- `Append(target, content)` adds content to the end of the target element.
- `Prepend(target, content)` adds content to the beginning.
- `Replace(target, content)` replaces the target element entirely.
- `Update(target, content)` replaces the inner content of the target.
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
- `client-module`: client module path or name to mount for hybrid islands.
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
- Stream endpoints deny subscriptions by default; configure `RazorWireStreamAuthorizationMode.AllowAll` for public/demo channels or provide a custom `IRazorWireChannelAuthorizer`.
- `replay`: when `true`, appends `?replay=1` to the stream endpoint so the page receives retained channel messages before live updates.

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

Injects the client scripts RazorWire needs, including Turbo and the RazorWire assets.

```html
<rw:scripts />
```

The script tag also carries failed-form runtime configuration derived from `RazorWireOptions.Forms`; no inline configuration script is required.

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

`client-module` can be a relative path, root-relative path, same-origin URL, HTTPS URL, or bare import-map specifier. Hosts that prefer logical names can set `window.RazorWireIslandModules = { ChartComponent: "/js/chart-component.js" }` before hydration; RazorWire resolves the rendered `data-rw-module` name through that manifest before calling dynamic `import()`. Direct `javascript:`, `blob:`, `file:`, and unapproved `data:` module specifiers are rejected.

RazorWire serves `/_content/ForgeTrust.RazorWire/razorwire/razorwire.js`,
`razorwire.islands.js`, and the package demo assets as normal Razor Class Library
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
URLs.

CDN export validates the static references it can discover while crawling.
Missing frame routes, unsafe query-bearing frame sources, missing internal assets,
and managed URLs that cannot be rewritten fail the export with `RWEXPORT###`
diagnostics instead of producing a broken folder. The diagnostics include the
HTML element/attribute or CSS token that produced the reference and the normalized
path the exporter attempted to prove. The validation boundary is deliberate:
app-authored JavaScript fetches, form posts, Server-Sent Events, import maps, and
other runtime behavior outside markup/CSS references are not proven static by the
exporter.

Parser-backed discovery may find valid references that older exporter versions
missed. Treat new CDN validation failures after upgrading as potentially correct:
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
