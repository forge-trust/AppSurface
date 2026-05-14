# Failed Form UX

RazorWire-enhanced forms now have a convention for server failures. A form with `rw-active="true"` gets request markers, a default form-local fallback UI for unhandled failures, server-side helpers for high-quality validation errors, and development diagnostics for anti-forgery failures.

AppSurface has not published the public `v0.1` package set yet, so the one-copy path today is the in-repo MVC sample:

```bash
dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj
```

Open the printed URL and visit `/Reactivity/FormFailures`.

When consuming package builds from a configured feed, reference `ForgeTrust.RazorWire` first and then continue with the form markup below. Public NuGet install commands will replace this repo-local note when publishing is live.

## Quickstart

Add a local error target to any enhanced form:

```cshtml
<form asp-action="Save"
      method="post"
      rw-active="true"
      data-rw-form-failure-target="profile-errors">
    <div id="profile-errors" data-rw-form-errors></div>
    <input name="displayName" />
    <button type="submit">Save</button>
</form>
```

When the server returns an unhandled `400`, `401`, `403`, `422`, or `500` response, the runtime renders a scoped fallback block inside `#profile-errors`. The block is tagged with `data-rw-form-error-generated="true"`, gets an accessible live-region role, and is removed on the next submit or successful submit.

For validation failures, prefer a server-handled stream:

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public IActionResult Save([FromForm] string? displayName)
{
    if (string.IsNullOrWhiteSpace(displayName))
    {
        ModelState.AddModelError(nameof(displayName), "Display name is required.");
    }

    if (!ModelState.IsValid)
    {
        return this.RazorWireStream()
            .FormValidationErrors("profile-errors", ModelState)
            .BuildResult(StatusCodes.Status422UnprocessableEntity);
    }

    return this.RazorWireStream()
        .Update("profile-errors", string.Empty)
        .BuildResult();
}
```

`FormValidationErrors` marks the response as handled by setting `X-RazorWire-Form-Handled: true`, so the runtime will not duplicate the server-rendered error UI.

## Customize The Default Styling

The default fallback CSS is injected once by the runtime and only targets generated RazorWire form errors. Consumers can tune it with CSS variables on a form, a containing section, or global stylesheet:

```css
.account-form {
  --rw-form-error-border: #0f766e;
  --rw-form-error-bg: #f0fdfa;
  --rw-form-error-title: #115e59;
  --rw-form-error-text: #134e4a;
  --rw-form-error-radius: 4px;
  --rw-form-error-spacing: 1rem;
}
```

Use `data-rw-form-failure="manual"` when the app wants to render everything itself:

```cshtml
<form asp-action="Save"
      method="post"
      rw-active="true"
      data-rw-form-failure="manual"
      data-account-errors="#account-errors">
    <div id="account-errors"></div>
    <button type="submit">Save</button>
</form>
```

```js
document.addEventListener('razorwire:form:failure', event => {
  const form = event.target;
  const target = document.querySelector(form.getAttribute('data-account-errors'));
  if (!target) return;

  event.preventDefault();
  target.textContent = event.detail.message;
});
```

## Development Diagnostics

In `Development`, RazorWire rewrites anti-forgery validation failures for RazorWire form requests into a helpful `400` response. The diagnostic explains that the token is missing or stale, gives fixes, and links back to [Security & Anti-Forgery](./antiforgery.md). In production, the response remains safe for users and does not expose implementation details.

The adapter recognizes RazorWire form requests by:

- `X-RazorWire-Form: true`, which the runtime adds to enhanced form submissions.
- `__RazorWireForm=1`, which the `form[rw-active]` TagHelper emits as a hidden marker for fallback server detection.

If the form declares `data-rw-form-failure-target="profile-errors"`, the TagHelper also emits `__RazorWireFormFailureTarget=profile-errors`. The anti-forgery adapter uses that value to stream diagnostics into the form-local target when the request can be read safely. It does not parse multipart bodies for classification or target discovery.

## Options

Configure failed-form behavior through `RazorWireOptions.Forms`:

```csharp
services.AddRazorWire(options =>
{
    options.Forms.EnableFailureUx = true;
    options.Forms.FailureMode = RazorWireFormFailureMode.Auto;
    options.Forms.EnableDevelopmentDiagnostics = true;
    options.Forms.DefaultFailureMessage = "We could not submit this form. Check your input and try again.";
});
```

- `EnableFailureUx` disables all failed-form request markers, events, fallback rendering, runtime handling, and anti-forgery rewriting when set to `false`.
- `FailureMode.Auto` renders the default fallback UI for unhandled failures.
- `FailureMode.Manual` dispatches failure events but does not render fallback UI.
- `FailureMode.Off` disables the failure convention by default.
- `EnableDevelopmentDiagnostics` controls detailed diagnostics in development only.
- `DefaultFailureMessage` is the generic fallback message for unhandled client-rendered failures. Null, empty, or whitespace-only values fall back to RazorWire's safe default.

When `EnableFailureUx` is `true`, per-form `data-rw-form-failure="auto"`, `manual`, or `off` overrides the global failure mode.

## Server API

`this.RazorWireStream()` includes two form-focused helpers:

- `FormError(target, title, message)` renders a form-local error block and marks the response handled.
- `FormValidationErrors(target, ModelState, title, maxErrors, message)` renders a stable ModelState summary, caps long lists, and marks the response handled.

`BuildResult(statusCode)` can set the HTTP status while returning a Turbo Stream. Use `422` for validation responses, `400` for malformed requests, and keep unexpected `500` failures unhandled unless the app has a specific recovery UI.

## Runtime Events

The runtime dispatches:

- `razorwire:form:submit-start`
- `razorwire:form:failure`
- `razorwire:form:diagnostic`
- `razorwire:form:submit-end`

`razorwire:form:failure` is cancelable. Call `event.preventDefault()` to suppress the default fallback while still using `FailureMode.Auto`.

The event detail includes `form`, `target`, `message`, and optional `developmentDiagnostic`.

## Test Workflow

Runtime tests live beside the RazorWire static asset:

```bash
node --test Web/ForgeTrust.RazorWire/test/*.test.mjs
```

CI runs the same tests with `npm ci --prefix Web/ForgeTrust.RazorWire` followed by `npm test --prefix Web/ForgeTrust.RazorWire`.

Server behavior is covered by the RazorWire unit tests:

```bash
dotnet test Web/ForgeTrust.RazorWire.Tests/ForgeTrust.RazorWire.Tests.csproj
```

Browser-level sample behavior is covered by the RazorWire integration tests:

```bash
dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj
```

## Symptom: A Failed Form Shows A Full Error Page

Confirm the form has `rw-active="true"` and that `<rw:scripts />` is rendered once in the page layout. Without the runtime, the browser falls back to normal form navigation.

## Symptom: The Error Appears In The Wrong Place

Add `data-rw-form-failure-target="your-errors-id"` and make sure the target exists before submit. Use a simple id for server-handled anti-forgery diagnostics. The client can resolve ids or CSS selectors, but server-generated Turbo Streams target ids.

## Symptom: A Validation Error Appears Twice

Use `FormError` or `FormValidationErrors` for server-rendered failure UI. Those helpers set `X-RazorWire-Form-Handled: true`, which tells the runtime not to render the default fallback.

## Symptom: Anti-Forgery Fails After A Partial Update

Refresh the token whenever a stream replaces form contents. Prefer `ReplacePartial` for the whole `<form>` so the MVC Form TagHelper emits a fresh token, or include `@Html.AntiForgeryToken()` inside updated form fields. See [Security & Anti-Forgery](./antiforgery.md).

## Safe Reporting

Production users should see short recovery messages. Detailed diagnostics should stay in development or server logs. When reporting failed-form bugs, include the action route, HTTP status, response content type, whether `X-RazorWire-Form-Handled` was present, and whether the form had `data-rw-form-failure-target`.
