# RazorWire MVC Example

This sample is the concrete proof behind the RazorWire package README. It shows how returned Razor fragments, islands, and SSE fit into a normal ASP.NET Core MVC app without a separate client rendering stack.

## Start Here: Return Razor Fragments

1. Run the application from the repository root:

   ```bash
   dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj
   ```

   This is the repo-local path while the public `v0.1` package install flow is being finalized. It assumes you are in a clone of this repository with the .NET 10 SDK installed.

   If you `cd examples/razorwire-mvc` first, `dotnet run` also works from there.

2. Open the URL printed in the console and navigate to `/Reactivity`.
3. Wait for the `Permanent Island` sidebar to load.
4. Click the `+` button in the counter widget.
5. Watch `Instance Score` and `Session Score` update in place without a full page reload.

That is the core RazorWire workflow in one interaction: a normal MVC form posts, the controller returns targeted Razor fragments, and the UI updates only where it needs to.

To inspect failed-submission conventions, navigate to `/Reactivity/FormFailures`. That page intentionally triggers validation, anti-forgery, authorization, malformed request, and server failures so you can compare server-handled errors with the default runtime fallback.

### Form Loading Feedback

To inspect form-loading conventions, navigate to `/Reactivity/FormLoading`. It demonstrates a form-local status, a section-shared status, the package fallback on a form with `data-rw-form-failure="off"`, and a five-second Turbo Frame submission with a local status. The fallback case shows that loading remains enabled when failure UX is opted out. The [Form Loading Feedback guide](../../Web/ForgeTrust.RazorWire/Docs/form-loading.md) covers configuration, indicator ownership, CSP, and the remaining manual and consumer proof.

To inspect same-page navigation conventions, navigate to `/Navigation/PageNavigation`. That page is a brochure-style proof for active section links, initial hash state, optional mobile panel close, and Bootstrap scrollspy replacement without custom page JavaScript.

To inspect the default runtime-sourcing contract without unrelated network traffic, navigate to `/Reactivity/DeterministicRuntime`. Its dedicated layout contains no external fonts or other remote assets, loads the package-owned Turbo 8.0.23 runtime from the app origin, and links to a second state through Turbo Drive. The integration tests record external HTTP requests, preserve a window sentinel across visits, and cover the hash, frame, and stream risks selected by the [Turbo 8.0.23 upgrade review](../../Web/ForgeTrust.RazorWire/Docs/turbo-8.0.23-upgrade-review.md).

## What Just Happened

```text
/Reactivity
  -> loads the Permanent Island from /Reactivity/Sidebar
  -> renders the Counter view component inside that island
  -> posts the counter form to ReactivityController.IncrementCounter
  -> returns a RazorWire stream with targeted updates
  -> updates the two counters and replaces the hidden input for the next click
```

## Files Behind the Hero Flow

- `examples/razorwire-mvc/Views/Reactivity/Index.cshtml` loads the permanent island with `src="/Reactivity/Sidebar"`.
- `examples/razorwire-mvc/Views/Shared/_Sidebar.cshtml` hosts the island content and invokes the `Counter` view component.
- `examples/razorwire-mvc/Views/Shared/Components/Counter/Default.cshtml` renders the counter values plus the `IncrementCounter` form.
- `examples/razorwire-mvc/Controllers/ReactivityController.cs` returns the targeted stream updates.
- `examples/razorwire-mvc/Views/Reactivity/_CounterInput.cshtml` replaces the hidden `clientCount` input after each click.

## Proof Slice

`examples/razorwire-mvc/Views/Shared/Components/Counter/Default.cshtml`

```cshtml
<div id="instance-score-value" class="text-2xl font-black text-indigo-600 tabular-nums">@Model</div>
<div id="session-score-value" class="text-2xl font-black text-indigo-400 tabular-nums">0</div>

<form asp-controller="Reactivity" asp-action="IncrementCounter" method="post" rw-active="true" data-counter-form>
    <input type="hidden" name="clientCount" id="client-count-input" value="0" />
    <button type="submit" aria-label="Increment counter">+</button>
    <button type="submit" name="openDialog" value="true">Increment and show result</button>
</form>
```

`examples/razorwire-mvc/Controllers/ReactivityController.cs`

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public IActionResult IncrementCounter([FromForm] int clientCount, [FromForm] bool openDialog = false)
{
    CounterViewComponent.Increment();
    clientCount++;

    if (Request.IsTurboRequest())
    {
        var stream = this.RazorWireStream()
            .Update("instance-score-value", CounterViewComponent.Count.ToString())
            .Update("session-score-value", clientCount.ToString())
            .ReplacePartial("client-count-input", "_CounterInput", clientCount);

        if (openDialog)
        {
            var result = $"Counter updated to {CounterViewComponent.Count} in the local in-memory sample; " +
                "the separate Reactivity page result was updated too.";
            stream.Update("counter-distant-result", result);
            stream.OpenDialog("Counter updated", result);
        }

        return stream.BuildResult();
    }

    var referer = Request.Headers["Referer"].ToString();
    return Url.IsLocalUrl(referer) ? Redirect(referer) : RedirectToAction(nameof(Index));
}
```

`examples/razorwire-mvc/Views/Reactivity/_CounterInput.cshtml`

```cshtml
<input type='hidden' name='clientCount' id='client-count-input' value='@Model' />
```

## If Your Result Differs

- If the page loads on a different port, use the URL printed by `dotnet run`.
- If clicking `+` gives you a bare `400 Bad Request`, check the package docs for [Security & Anti-Forgery](../../Web/ForgeTrust.RazorWire/Docs/antiforgery.md). That is the first thing to verify when you copy this pattern into another page or app.
- If the form does not update in place, check the same anti-forgery guidance first, then confirm you are still posting with `rw-active="true"` and returning a RazorWire stream from `IncrementCounter`.
- If you want the broader sample context instead of the focused proof, continue below.

## Server-Selected Dialogs

Run the sample, open `/Reactivity/DialogResponses`, and try the enhanced responses. The dialog body is an app-owned Razor partial; RazorWire supplies the accessible shell. The same actions render a complete HTML page with the equivalent status or form when Turbo is not requesting a stream.

| Route | Trigger and enhanced response | Full HTML behavior |
|---|---|---|
| `GET /Reactivity/DialogResponses` | Opens the sample page. The response is private/no-store and varies by `Accept`. | Renders the same page shell. |
| `GET /Reactivity/DialogStatus` | **Check status** is an ordinary link with `data-turbo-stream`. The private/no-store response varies by `Accept`, opens `_DialogStatus`, then updates `#dialog-proof-after` to `Ordered update applied`. This later action proves the stream applies a target update after its dialog body is inserted. | Renders the status partial inline on the sample page. |
| `POST /Reactivity/SaveDialog` | The outside **Save and continue** form includes the normal MVC anti-forgery token. The response updates `#dialog-result`, then opens `_DialogForm` in the shell. | Renders the result and the same form inline. |
| `POST /Reactivity/CompleteDialog` | The in-dialog form posts its `Name`. Invalid input returns a handled `422`, replaces `#dialog-form`, and renders the validation summary in `#dialog-errors` while retaining the entered value. Success updates `#dialog-result` and explicitly calls `CloseDialog()`. | Renders the same form and errors in the page with status `422`; a valid submission renders the completed result in the page. |

`Name` is required, must be at least two characters, and is limited to 40 characters; whitespace-only names are rejected after trimming. The sample does not persist the submitted value. Escape, the visible Close control, or navigation can dismiss a dialog and discard unsaved visible input while an already-submitted server operation continues. Request/flow metadata orders presentation only; it does not order or undo durable application writes. See the [server-selected dialog guide](../../Web/ForgeTrust.RazorWire/Docs/dialog-responses.md) for the full API, protocol, cache, CSP, and recovery contract.

Both GET routes send `Vary: Accept` and `Cache-Control: private, no-store`. The POST response variants also send `Cache-Control: private, no-store`.

The full HTML view passes `DialogIdPrefix = "inline-"` through partial view data. `_DialogStatus` and `_DialogForm` apply that prefix to their inline target and field IDs, including the label's `for` and the form's failure target. Dialog stream targets retain `dialog-proof-after`, `dialog-form`, `dialog-errors`, and `Name`; the bound field name remains `Name` in both variants. This keeps a direct HTML visit followed by an enhanced response from creating duplicate IDs or updating the wrong copy. See [fallback target ownership](../../Web/ForgeTrust.RazorWire/Docs/dialog-responses.md#get-post-and-handled-validation).

**Developer adoption trial (2026-09-30): 242.254 seconds from editing a prepared sample through HTTP readiness.** An independent worker added a server action and enhanced link in a clean source export, then rebuilt and started that same export. A browser click subsequently opened the titled **Adoption trial** dialog with its body and Close control in 289 ms. Setup and the browser check were measured separately; the browser check was delayed by review work and an app restart. The prepared-app setup meets the under-five-minute target. This trial used the warm global NuGet cache and host execution permissions; it is not a first-ever machine setup measurement.

**Clean-source startup trial: 11.312 seconds** from export extraction through HTTP readiness: extraction 0.673 seconds, then `dotnet run` 10.638 seconds. The global NuGet cache was warm. The initial sandbox startup failed; the successful run used host execution permissions. No prebuilt project binaries were copied into the export.

The existing **Check status** interaction separately took 347 ms from clicking to a visible titled dialog and Close control in the already-running sample; its ordered body update was visible without scrolling. These interaction timings describe response presentation after setup.

### Real Adoption Proof: IncrementCounter

The existing counter action is the sample's opt-in adoption proof. On `/Reactivity`, the counter trigger lives in the **Permanent Island** in the left column. Its existing `+` button posts `POST /Reactivity/IncrementCounter` and keeps the ordinary response inline: `#instance-score-value` and `#session-score-value` change beside the button, and `#client-count-input` is replaced for the next click. That default is unchanged.

The adjacent **Increment and show result** submit button opts into the same action's dialog branch. The response also updates `#counter-distant-result` in the main/right column, then opens a plain-text dialog titled **Counter updated** with this body (where `{count}` is the new in-memory counter value):

```text
Counter updated to {count} in the local in-memory sample; the separate Reactivity page result was updated too.
```

This makes the route, trigger, distant target, and body concrete: the trigger is in the left sidebar island, while the additional page target is in the main column. The dialog is opt-in because this result is separated from the trigger; routine increments continue to update their nearby values without interrupting the user. The dialog branch also preserves the existing counter updates. This is sample-only in-memory state, not a durable save workflow.

## Broader Sample Features

### Islands

The sample uses `rw:island` to load and persist independent UI regions.

- `ReactivityController.Sidebar()` returns the permanent sidebar island.
- `ReactivityController.UserList()` returns the `UserList` view component inside its own island.
- `Views/Home/Index.cshtml`, `Views/Reactivity/Index.cshtml`, and `Views/Navigation/Index.cshtml` all reuse the same `permanent-island` so it can persist across page transitions.

Hybrid client islands should point at normal served JavaScript modules rather
than inline module bytes. The Playwright proof uses
`wwwroot/js/playwright-client-island.js` as a same-origin module fixture:

```js
export function mount(root, props) {
  root.textContent = `client:${props.label}`;
  root.dataset.clientMounted = 'true';
}
```

Hosts can reference that shape directly with `client-module="/js/my-island.js"`
or indirectly with `window.RazorWireIslandModules = { MyIsland: "/js/my-island.js" }`.
RazorWire rejects `data:` module content, `file:`,
`blob:`, `javascript:`, and protocol-relative `//...` specifiers before
calling dynamic `import()`.

### Live Updates over SSE

The sample also demonstrates live multi-client updates.

- `Views/Reactivity/Index.cshtml` includes `<rw:stream-source id="rw-stream-reactivity" channel="reactivity" permanent="true" />`.
- `ReactivityController.PublishMessage()` pushes new messages to every connected client.
- `ReactivityController.BroadcastUserPresenceAsync()` updates the user list and online count across sessions.

RazorWire stream subscriptions are denied by default. This sample explicitly sets `RazorWireOptions.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll` in `RazorWireExampleModule.ConfigureServices` because the `reactivity` channel is a public demo channel. Production apps should register `IRazorWireChannelAuthorizer` when channels depend on the current user, tenant, or workflow state.

When you intentionally expose public/demo streams, keep channel names finite and namespaced instead of accepting arbitrary request values. RazorWire now admits public streams through per-process guardrails: channel names must be segment-safe, invalid channels return `400`, authorization denials return `403`, and capacity denials return `429` before the stream hub allocates subscriber state. A browser may surface a rejected native `EventSource` only as a stream error, so use the Network tab, `razorwire:stream:error`, server log event `13700 StreamSubscriptionDenied` for authorization denials, and `13701 StreamAdmissionRejected` for validation or capacity rejections while debugging.

For a public demo, keep the limits visible near the `AllowAll` decision:

```csharp
services.AddRazorWire(options =>
{
    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
    options.Streams.MaxLiveChannels = 8;
    options.Streams.MaxLiveSubscriptions = 100;
    options.Streams.MaxLiveSubscriptionsPerChannel = 25;
});
```

The limits above apply to one ASP.NET Core process. Raise them for intentional high-fanout demos, but use ASP.NET Core rate limiting, proxy limits, SignalR, or managed pub/sub when you need per-client fairness, cross-node fanout, groups, or durable delivery.

### Registration and Message Publishing

The reactivity page includes two additional form flows:

- `Views/Reactivity/_UserRegistration.cshtml` posts to `RegisterUser` and swaps the register and message forms.
- `Views/Reactivity/_MessageForm.cshtml` posts to `PublishMessage` and prepends messages into the live feed.

`RegisterUser` stores the display name in a `razorwire-username` cookie with `Secure`, `HttpOnly`, and `SameSite=Lax` set. Keep that shape when copying the sample into an application: the cookie is only a convenience for the demo identity, but browsers can otherwise send it over cleartext HTTP. Outside localhost-style development, serve the flow over HTTPS before depending on the cookie. During local development, use the printed `localhost` URL rather than swapping in `127.0.0.1`; browsers treat those as different cookie hosts, and some will reject `Secure` cookies from an HTTP loopback IP.

Those flows are richer than the counter demo, but the counter is the cleanest first proof because it does not depend on stream-hub context to feel convincing.

### Failed Form UX

The sample includes a dedicated `/Reactivity/FormFailures` page that demonstrates:

- `FormValidationErrors` returning a `422` validation stream with `X-RazorWire-Form-Handled: true`.
- Development anti-forgery diagnostics for a raw form that intentionally omits `__RequestVerificationToken`.
- Default runtime fallbacks for unhandled `400`, `403`, and `500` responses.
- Consumer customization with CSS variables and `razorwire:form:failure` in manual mode.

See the package guide for the API contract and troubleshooting notes: [Failed Form UX](../../Web/ForgeTrust.RazorWire/Docs/form-failures.md).

### Page Navigation

The sample includes a dedicated `/Navigation/PageNavigation` page that demonstrates:

- `rw-page-nav` on a normal navigation root.
- `rw-page-nav-link` on ordinary section anchors.
- `rw-page-nav-toggle` and `rw-page-nav-panel` for an optional narrow-viewport panel.
- Active-state styling through `data-rw-page-nav-active="true"`.
- Sticky-header offset through app-owned `scroll-margin-top`.

The page is intentionally free of custom JavaScript. It is the migration path for brochure sites that previously needed `.page-scroll`, `data-bs-spy`, active class scripts, or Bootstrap navbar-collapse hooks. See the package guide for the full contract and migration table: [Page Navigation](../../Web/ForgeTrust.RazorWire/Docs/page-navigation.md).

## Project Structure

- `Controllers/ReactivityController.cs`: main demo controller for islands, form posts, and stream responses.
- `RazorWireExampleModule.cs`: AppSurface module registration, including the demo-only `AllowAll` stream authorization mode.
- `Views/Reactivity/`: reactivity page plus registration, message, and counter partials.
- `Views/Shared/`: shared island and view component rendering.
- `ViewComponents/`: view component entry points such as `Counter` and `UserList`.
- `Services/`: in-memory sample services such as `UserPresenceService` and `MessageStore`.

## Development Notes

To enable Razor Runtime Compilation and live static asset updates in the sample, run in the `Development` environment, for example with `ASPNETCORE_ENVIRONMENT=Development`.

Local assets such as `site.js` and `site.css` automatically receive version hashes for cache busting. You can still use `asp-append-version="true"` explicitly if you want to make that behavior obvious in markup.
