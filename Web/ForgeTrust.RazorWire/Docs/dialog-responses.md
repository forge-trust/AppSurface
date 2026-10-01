# Server-selected dialog responses

RazorWire dialogs let an MVC action explicitly present a response in one accessible, package-owned native dialog shell. The application owns the Razor body and decides which response needs attention. Start with the [MVC dialog sample](../../../examples/razorwire-mvc/README.md#server-selected-dialogs) and the [RazorWire package guide](../README.md#server-selected-dialog-responses).

Use a dialog when a result needs the user's attention or a follow-up action and moving it next to the trigger would still leave it easy to miss. Keep routine acknowledgements and recoverable feedback beside their fields. The [sample's IncrementCounter adoption proof](../../../examples/razorwire-mvc/README.md#real-adoption-proof-incrementcounter) shows an explicit opt-in for a distant counter result while preserving its inline default.

## Builder API

All methods below are on `RazorWireStreamBuilder`. They return the same builder and can be chained with ordinary page-target actions.

```csharp
RazorWireStreamBuilder OpenDialog(string title, string? message)
RazorWireStreamBuilder OpenDialogPartial(string title, string viewName, object? model = null)
RazorWireStreamBuilder OpenDialogComponent<T>(string title, object? arguments = null)
    where T : ViewComponent
RazorWireStreamBuilder OpenDialogComponent(string title, string componentName, object? arguments = null)

RazorWireStreamBuilder ReplaceDialog(string title, string? message)
RazorWireStreamBuilder ReplaceDialogPartial(string title, string viewName, object? model = null)
RazorWireStreamBuilder ReplaceDialogComponent<T>(string title, object? arguments = null)
    where T : ViewComponent
RazorWireStreamBuilder ReplaceDialogComponent(string title, string componentName, object? arguments = null)

RazorWireStreamBuilder CloseDialog()
bool HasActiveDialog { get; }
```

- `OpenDialog` supplies encoded plain text. `null` produces an empty body with the package-owned title and Close control; it does not accept HTML.
- `OpenDialogPartial` renders a named MVC partial with an optional model. `OpenDialogComponent<T>` uses a component type, and `OpenDialogComponent` uses its registered name with optional arguments. These use the existing trusted Razor rendering boundary.
- `title` must be nonblank. `viewName` and the named `componentName` must also be nonblank. The generic component overloads require `T : ViewComponent`.
- Each `ReplaceDialog*` overload replaces an already queued open payload, including its title and rendering source. The generic and named component forms mirror their `OpenDialogComponent` counterparts.
- `CloseDialog()` queues an explicit close. It does not infer success from a status code or form submission.
- `HasActiveDialog` reports only whether this builder's final pending slot is an open payload. It is false initially and after `CloseDialog`; it does not inspect the browser or report whether a user currently sees a dialog.

The builder has one dialog slot. A second `OpenDialog*` while an open is pending throws immediately; use `ReplaceDialog*` when replacing is intentional. `ReplaceDialog*` without a pending open throws. Repeated replacements retain only the last payload, and overwritten partials/components are not rendered. Repeated closes are idempotent. Open then close leaves one final close; close then open leaves one final open. `HasActiveDialog` tracks those transitions. Ordinary page actions keep their relative order, and the dialog slot stays where its first dialog operation was queued. Therefore an action aimed at the new body must be added after the open operation.

The dialog API needs request context. Return `BuildResult()` from the MVC request or call `RenderAsync(viewContext)`; `Build()` fails for a builder with a dialog command because it has no request headers to correlate. Its `InvalidOperationException` tells the developer to use `BuildResult()` from a controller action or `RenderAsync(viewContext)` with the current request context. The package README links this API and recovery guide alongside that diagnostic; the exception does not fabricate a stream response. The final response contains at most one dialog action. There is no raw-HTML dialog overload. Manually authored raw streams and `IRazorWireStreamHub` replay/live-push are outside the scoped-dialog guarantees.

### Copyable body examples

Inside a controller's enhanced response branch, a plain-text response can update the page and draw attention to the result:

```csharp
return this.RazorWireStream()
    .Update("dialog-result", "Check complete.")
    .OpenDialog("Service status", "The service is ready for your next request.")
    .BuildResult();
```

A partial uses the same MVC view discovery and model conventions as the [sample status partial](../../../examples/razorwire-mvc/Views/Reactivity/_DialogStatus.cshtml):

```csharp
return this.RazorWireStream()
    .OpenDialogPartial("Service status", "_DialogStatus", model)
    .BuildResult();
```

For a view component, these generic and named calls both use the sample's existing [Counter component](../../../examples/razorwire-mvc/ViewComponents/CounterViewComponent.cs). The generic form requires `using RazorWireWebExample.ViewComponents;` in that sample:

```csharp
return this.RazorWireStream()
    .OpenDialogComponent<CounterViewComponent>("Current counter")
    .BuildResult();
```

```csharp
return this.RazorWireStream()
    .OpenDialogComponent("Current counter", "Counter")
    .BuildResult();
```

Components that accept parameters receive an anonymous object matching their `Invoke` or `InvokeAsync` arguments. For example, the sample's [UserList component](../../../examples/razorwire-mvc/ViewComponents/UserListViewComponent.cs) accepts `users`:

```csharp
return this.RazorWireStream()
    .OpenDialogComponent("Active users", "UserList", new { users })
    .BuildResult();
```

When multiple helpers contribute to one response, inspect the pending slot before adding an open. An intentional replacement retains one final payload and does not render the overwritten component:

```csharp
var response = this.RazorWireStream()
    .OpenDialogComponent<CounterViewComponent>("Current counter");

if (response.HasActiveDialog)
{
    response.ReplaceDialog("Counter result", "The counter check is complete.");
}

return response.BuildResult();
```

Use `ReplaceDialogPartial` or either `ReplaceDialogComponent` overload in the same position when the final body needs Razor rendering. These snippets belong in an `IsTurboRequest()` branch with a full HTML counterpart, as shown below.

## GET, POST, and handled validation

Use the ordinary endpoint and `Accept` negotiation for the enhanced and no-JavaScript paths. A stream GET link must opt in with `data-turbo-stream`; Turbo forms already request a stream. Keep a complete HTML view and working form for ordinary requests.

If that full HTML view can later open the same body in a dialog, give its inline targets and field IDs distinct values. Turbo resolves `target` IDs across the document; duplicate IDs can update the inline copy instead of the dialog. Keep each label's `for` and each form's failure-target attribute aligned with its own IDs, while preserving field `name` values for model binding. The [sample fallback](../../../examples/razorwire-mvc/README.md#server-selected-dialogs) uses an `inline-` prefix for those IDs.

```csharp
public IActionResult DialogStatus()
{
    SetDialogResponseHeaders(); // Vary: Accept; Cache-Control: private, no-store

    if (Request.IsTurboRequest())
    {
        return this.RazorWireStream()
            .OpenDialogPartial("Service status", "_DialogStatus", GetStatus())
            .BuildResult();
    }

    return View("DialogStatus", GetStatus());
}

[HttpPost]
[ValidateAntiForgeryToken]
public IActionResult CompleteDialog(DialogFormModel model)
{
    if (!ModelState.IsValid)
    {
        if (Request.IsTurboRequest())
        {
            return this.RazorWireStream()
                .ReplacePartial("dialog-form", "_CompleteDialogForm", model)
                .FormValidationErrors("dialog-errors", ModelState)
                .BuildResult(StatusCodes.Status422UnprocessableEntity);
        }

        return View("DialogPage", model);
    }

    // Apply application-owned validation and persistence rules here.
    if (Request.IsTurboRequest())
    {
        return this.RazorWireStream()
            .Update("dialog-result", "Request completed.")
            .CloseDialog()
            .BuildResult();
    }

    return RedirectToAction(nameof(DialogPage));
}
```

This sketch names the flow: the status response opens a Razor partial, invalid dialog form data returns a handled `422` with a rerendered form and validation summary, and success updates a page target then explicitly closes. `ReplacePartial` updates the existing body in the browser; `ReplaceDialogPartial` only replaces an unsent open payload in the current builder. See the [complete sample action and views](../../../examples/razorwire-mvc/README.md#server-selected-dialogs) for compilable endpoints and full HTML counterparts.

Mark form actions with the normal ASP.NET Core `[ValidateAntiForgeryToken]` policy and render the regular MVC anti-forgery field. Enhanced Turbo requests do not relax authorization or anti-forgery. A stream GET link looks like:

```html
<a href="/Reactivity/DialogStatus" data-turbo-stream>Check status</a>
```

For GET negotiation, emit `Vary: Accept`. Dialog bodies can be user-specific; use `Cache-Control: private, no-store` (or a deliberate equivalent) on both negotiated variants so shared caches cannot reuse one user's output. The same endpoint should return a complete HTML page/form when Turbo Stream is not accepted. A Turbo stream response is not a no-JavaScript fallback.

## Ordering, flows, and writes

The browser assigns supported Turbo requests an opaque request ID and tab-local start order. Requests started inside a live dialog also carry that flow's opaque ID. The request headers are:

| Header | Value | Meaning |
|---|---|---|
| `X-RazorWire-Request` | UUID | Unique, one-use presentation correlation for a supported request. |
| `X-RazorWire-Order` | Positive JavaScript safe integer, up to `9007199254740991` | Tab-local request start order. |
| `X-RazorWire-Flow` | Optional UUID, runtime-generated | Present only for a request originating in a live dialog. |

The runtime supplies these headers; application code should not mint or forward them. Turbo stream GET links and `data-turbo-method` links are internally submitted by Turbo through a temporary form, so RazorWire retains the initiating link as the request origin for correlation and focus. Non-GET method links request streams without an additional `data-turbo-stream` marker; GET links require that explicit marker. The original link's flow is captured before that temporary form is created; the temporary form is not mistaken for a user-authored form inside the dialog. Prefer ordinary forms for mutations so the endpoint also works without JavaScript.

These values are bounded UI-presentation metadata, never authentication, CSRF protection, idempotency keys, or a database write-ordering mechanism. Every correlated stream response echoes the request, order, and optional flow values on its package-authored stream actions as `data-rw-request`, `data-rw-order`, and optional `data-rw-flow`, plus the action's `data-rw-dialog-phase` (`origin`, `new`, or `closed`). Both `BuildResult()` and `RenderAsync(viewContext)` echo the matching request token in the HTTP response header `X-RazorWire-Request` for correlated responses. `RenderAsync` leaves headers unchanged once the response has started; render before writing response bytes when relying on the validation echo, or return `BuildResult()` so RazorWire prepares the headers. The browser consults that header for `422` responses to associate validation with its originating submission and focus after the matching dialog update renders. The header does not change success handling. Page-only responses without correlation keep their existing behavior. Package-generated handled antiforgery failures also echo correlation on their stream actions, so a late 400 response cannot overwrite a reused form-local error target in a newer dialog. Invalid presentation headers preserve the antiforgery 400 as a plain-text diagnostic without stream mutation and mark it unhandled, allowing the current form's local failure/retry UI to present the rejection. A correlated selector action matching both page and stale dialog diagnostics keeps its page effects and excludes the stale dialog targets.

An accepted open creates a new flow and replaces the current shell. Stale outside opens and responses from a dismissed, replaced, or no-longer-live inside flow cannot change the current dialog. Older overlapping submissions in one flow cannot overwrite newer validation state. For package-authored target actions, RazorWire applies this gating only when the action's actual target is inside the live dialog shell. An action carrying unknown or malformed correlation is skipped only when its target is inside the shell; an outside-page target remains eligible. A `rw-dialog` command must independently have a known, well-formed, unused request token to be accepted. Actions with no `data-rw-request`, including opaque/raw custom actions, are left to Turbo's normal behavior and remain the action author's responsibility. They are outside the scoped-dialog guarantee. Do not use an uncorrelated custom action to mutate dialog content when relying on flow isolation.

The approved opener-order rule has two accepted races: an outside request that began before manual dismissal can arrive later and reopen a dialog, and an earlier-started open can arrive after a newer server close. Those UC828-1/UC828-2 freshness barriers were explicitly not added. Escape, Close, navigation, or accepted replacement can also discard unsaved visible form values while the already-submitted server mutation continues. A presentation token cannot undo that write. If writes can conflict, use application-owned optimistic concurrency, idempotency, or serialization independently of RazorWire.

For a response containing a dialog command, RazorWire renders and buffers the complete ordered stream before writing headers or bytes. This avoids a partial page update followed by a rendering failure, but uses memory proportional to the response and does not impose a dialog body-size cap. Page-only streams retain their normal streaming path. Propagate cancellation and apply sensible application limits to large user-controlled bodies.

## Errors and recovery

| Symptom | Cause | Fix |
|---|---|---|
| A second `OpenDialog*` throws | This builder already has an open payload. | Use `ReplaceDialog*` for an intentional replacement, or close the slot before opening. |
| `ReplaceDialog*` throws | No open payload is pending in this builder. | Open first, or return an ordinary page response. |
| Dialog `Build()` fails | `Build()` has no MVC request context, so it cannot read required correlation headers. Its `InvalidOperationException` includes the concrete replacement calls. | Return `BuildResult()` from the controller action or use `RenderAsync(viewContext)`; follow the linked [builder API guide](#builder-api). |
| The server returns an error before a dialog stream body is written | A dialog command was built without required request/order metadata, or a supplied correlation header is duplicated, oversized, or malformed. The request-aware result validates this on the server and throws before committing headers or bytes. | Return the dialog builder through `BuildResult()` or `RenderAsync(viewContext)` from a supported correlated Turbo request. Check the server exception and request headers. Do not treat this as a browser stale-response skip. |
| The browser skips a dialog command or an in-shell target | Server metadata was valid, but the action's echoed token is unknown, malformed, already used, or stale for the current request/flow. Scoped actions targeting the page remain eligible. | Inspect the response's `data-rw-request`, `data-rw-order`, optional `data-rw-flow`, and dialog phase against the initiating request. Retry from the current page or dialog. |
| An uncorrelated custom action changes dialog content | A raw/custom stream action has no `data-rw-request`; it is intentionally left to Turbo's default processing and outside flow gating. | Use a package-authored targeted builder action for dialog content, or provide equivalent correlation/gating within the custom action. |
| Partial/component rendering fails | The view name/type is unavailable or rendering threw. | Check MVC view discovery and the server exception. Dialog responses are buffered, so the browser should not receive a partial stream from that result. |
| A late response does not update an open form | Its request belongs to a stale flow or an older submission. | This is intentional presentation gating. Retry from the current dialog; separately inspect whether the server write already happened. |
| Invalid form appears outside the dialog | The response did not use the handled `422` stream path, or the request was ordinary HTML. | For enhanced requests return status 422 with a dialog-target update and the existing handled-form response contract; keep the equivalent full HTML error view for fallback. |

An unhandled network or HTTP failure remains under the existing [form failure UX](form-failures.md), rather than being converted into a server-selected dialog. Preserve that local retry path and avoid duplicate loading/error UI. The `X-RazorWire-Request`, `X-RazorWire-Order`, and optional `X-RazorWire-Flow` values are transient browser-presentation buffers; they do not persist response state and are not a durable server queue.

## Shell and accessibility

RazorWire owns one native `<dialog>` shell, its accessible title, visible Close control, Escape handling, focus entry/return, and lifecycle cleanup. The app owns the body and its field layout. Focus enters the first meaningful control or the heading/content, moves to the first invalid field or error summary for handled validation, and returns to the connected trigger or a safe page landmark on close. A missing or detached trigger has a safe landmark fallback. Backdrop clicks do not dismiss. There is no new public runtime manager to configure.

The package dialog stylesheet is included in the generated package assets. `<rw:scripts />` always emits its same-origin versioned stylesheet link; the package's embedded-asset fallback serves the same CSS when static package assets are unavailable. The shell exposes these host overrides:

| Custom property | Controls | Default |
|---|---|---|
| `--rw-dialog-text` | Shell text color | `var(--rw-ui-text, #27272a)` |
| `--rw-dialog-surface` | Shell surface | `var(--rw-ui-surface, #fff)` |
| `--rw-dialog-border` | Shell and header border | `var(--rw-ui-border, #d4d4d8)` |
| `--rw-dialog-width` | Maximum preferred shell width | `32rem` |
| `--rw-dialog-max-height` | Shell maximum block size | `calc(100dvh - 2rem)` |
| `--rw-dialog-backdrop` | Native dialog backdrop | `rgb(0 0 0 / .45)` |
| `--rw-dialog-spacing` | Header and body padding | `1rem` |
| `--rw-dialog-focus` | Package-owned focus outline | `#2563eb` |

The shell's border radius uses canonical `--rw-ui-radius` with a `.5rem` fallback; header spacing uses canonical `--rw-ui-gap` with a `.75rem` fallback. These variables affect only the package-owned shell and controls. They do not restyle app-authored fields or the dialog body.

### CSP and Turbo compatibility

The bundled and verified runtime is Turbo **8.0.23**. The supported origin hooks were proved with that version: GET stream-link and non-GET method-link provenance survives Turbo's temporary form, forms preserve request-header propagation, handled `422` responses expose the echoed request token for validation focus, and the awaited stream-render hook filters before DOM mutation and applies a target update after an earlier action inserts it. Both response completion orders were exercised. RazorWire does not promise compatibility with other host-managed Turbo versions. If selecting `Custom` or `HostManaged`, the host owns script loading and CSP, and must browser-test the originating-link hooks, request-header propagation, success and `422` hooks, stale filtering, and target ordering for its exact version.

The dialog shell and stylesheet require no inline script or inline style injection. Under a strict CSP, allow the same-origin package script and stylesheet (typically `'self'`) and retain the host's normal nonce/hash policy for its own code. Do not add a second Turbo runtime. See [Turbo sourcing](../README.md#choose-who-supplies-turbo), the [generated UI design contract](../DESIGN.md), and [anti-forgery guidance](antiforgery.md).

## Try the sample

From the repository root, run:

```bash
dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj
```

Visit `/Reactivity/DialogResponses`, click **Check status**, then **Save and continue**. Submit a one-character or whitespace-only name to see handled validation stay in the dialog; submit a valid name to see the page result update and the server explicitly close the dialog. Disable JavaScript and use the same full-page forms. The [sample README](../../../examples/razorwire-mvc/README.md#server-selected-dialogs) records exact routes, targets, response bodies, and the prepared-app interaction timing; developer setup and clean-source startup are separate measurements.
