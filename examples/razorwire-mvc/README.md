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
</form>
```

`examples/razorwire-mvc/Controllers/ReactivityController.cs`

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
            .Update("instance-score-value", CounterViewComponent.Count.ToString())
            .Update("session-score-value", clientCount.ToString())
            .ReplacePartial("client-count-input", "_CounterInput", clientCount)
            .BuildResult();
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

## Broader Sample Features

### Islands

The sample uses `rw:island` to load and persist independent UI regions.

- `ReactivityController.Sidebar()` returns the permanent sidebar island.
- `ReactivityController.UserList()` returns the `UserList` view component inside its own island.
- `Views/Home/Index.cshtml`, `Views/Reactivity/Index.cshtml`, and `Views/Navigation/Index.cshtml` all reuse the same `permanent-island` so it can persist across page transitions.

### Live Updates over SSE

The sample also demonstrates live multi-client updates.

- `Views/Reactivity/Index.cshtml` includes `<rw:stream-source id="rw-stream-reactivity" channel="reactivity" permanent="true" />`.
- `ReactivityController.PublishMessage()` pushes new messages to every connected client.
- `ReactivityController.BroadcastUserPresenceAsync()` updates the user list and online count across sessions.

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

## Project Structure

- `Controllers/ReactivityController.cs`: main demo controller for islands, form posts, and stream responses.
- `Views/Reactivity/`: reactivity page plus registration, message, and counter partials.
- `Views/Shared/`: shared island and view component rendering.
- `ViewComponents/`: view component entry points such as `Counter` and `UserList`.
- `Services/`: in-memory sample services such as `UserPresenceService` and `MessageStore`.

## Development Notes

To enable Razor Runtime Compilation and live static asset updates in the sample, run in the `Development` environment, for example with `ASPNETCORE_ENVIRONMENT=Development`.

Local assets such as `site.js` and `site.css` automatically receive version hashes for cache busting. You can still use `asp-append-version="true"` explicitly if you want to make that behavior obvious in markup.
