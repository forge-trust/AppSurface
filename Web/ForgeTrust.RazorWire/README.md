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

Use that contract when adding or styling RazorWire-generated nodes such as form feedback, stream status affordances, or package-owned fallback UI. It defines the scope boundary, data-attribute and CSS custom-property styling surface, accessibility baseline, override model, and anti-patterns. It does not apply to app-authored forms, partials, layouts, or RazorDocs chrome.

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
    options.Forms.FailureMode = RazorWireFormFailureMode.Auto;
    options.Forms.DefaultFailureMessage = "We could not submit this form. Check your input and try again.";
});
```

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

### Form Enhancement

Standard HTML forms can return targeted stream updates instead of full reloads or redirect-first flows. The counter example above is the smallest version of that story: submit a normal MVC form, return RazorWire updates, and change only the DOM you care about.

When `EnableFailureUx` is enabled, `form[rw-active]` also marks enhanced form posts with `X-RazorWire-Form: true` and `__RazorWireForm=1`. That gives the runtime and server adapters enough context to render useful failed-submission UX without every controller hand-rolling client glue.

## Security & Anti-Forgery

Handling anti-forgery tokens correctly is critical when updating forms via Turbo Streams. See [Security & Anti-Forgery](Docs/antiforgery.md) for the detailed patterns and recommendations.

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

### `this.RazorWireStream()` (controller extension)

- `Append(target, content)` adds content to the end of the target element.
- `Prepend(target, content)` adds content to the beginning.
- `Replace(target, content)` replaces the target element entirely.
- `Update(target, content)` replaces the inner content of the target.
- `Remove(target)` removes the target element.
- `FormError(target, title, message)` updates the target with an encoded generated error block and marks the response handled.
- `FormValidationErrors(target, ModelState, title, maxErrors, message)` updates the target with a stable MVC validation summary and marks the response handled.
- `BuildResult(statusCode)` returns the stream and optionally sets the HTTP status code.

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

```html
<rw:stream-source id="rw-stream-reactivity" channel="reactivity" permanent="true"></rw:stream-source>
```

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

## Static Export

RazorWire can generate CDN-ready static output with the installable `razorwire`
.NET tool, or with the short-lived `dnx` tool execution path. CDN mode is the
default: extensionless internal routes such as `/about` are emitted as files such
as `about.html`, and exporter-managed links, frames, scripts, stylesheets, images,
`<img>` and `<source>` `srcset` candidates, and CSS `url(...)` references are
rewritten to the generated artifact URLs. When the conventional
`/_appsurface/errors/404` route is available, it emits `404.html` through the same
validation and rewrite path. Use `--mode hybrid` when the exported directory will
still be served behind infrastructure that resolves application-style extensionless
URLs.

CDN export validates the dependencies it can discover while crawling. Missing
frame routes, unsafe query-bearing frame sources, missing internal assets, and
managed URLs that cannot be rewritten fail the export with `RWEXPORT###`
diagnostics instead of producing a broken folder. The validation boundary is
deliberate: app-authored JavaScript fetches, form posts, Server-Sent Events, import
maps, and other runtime behavior outside markup/CSS references are not proven static
by the exporter.

Those package-based commands require a published package or an explicit local package
source; public package publishing is still manual until the coordinated release
automation tracked in #161 lands.

For installation, `dnx`, local-package, and source-run examples, see the
[RazorWire CLI](../../Web/ForgeTrust.RazorWire.Cli/README.md).

## Examples

- [Focused proof path: return Razor fragments](../../examples/razorwire-mvc/README.md#start-here-return-razor-fragments)
- [Full RazorWire MVC example](../../examples/razorwire-mvc/README.md)
- [Failed Form UX guide](Docs/form-failures.md)
- [Security & Anti-Forgery](Docs/antiforgery.md)
