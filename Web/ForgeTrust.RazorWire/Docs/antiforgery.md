# Security & Anti-Forgery

When using RazorWire Turbo Streams to replace or update parts of a page that contain forms, the original Anti-Forgery token hidden input may be lost. To prevent `400 Bad Request` errors on subsequent form submissions, ensure the token is included in your updated HTML.

RazorWire's failed-form convention improves the development experience around this failure. Enhanced forms are marked with `X-RazorWire-Form: true` and `__RazorWireForm=1`; when anti-forgery validation fails for one of those requests, RazorWire can rewrite the failure into a helpful `400` response with safe production text or detailed development diagnostics. See [Failed Form UX](./form-failures.md).

## Recommended: Use the `<form>` TagHelper with `replace`

If your partial view contains the entire `<form>` element, the ASP.NET Core TagHelper automatically injects the hidden anti-forgery token:

```cshtml
<!-- _MyForm.cshtml -->
<form asp-action="Submit" method="post" id="my-form">
    <input type="text" name="data" />
    <button type="submit">Submit</button>
</form>
```

**Important:** This approach requires using the **`replace`** Turbo Stream action (or `ReplacePartial` server-side helper). The `replace` action replaces the *entire* element, so when the partial is rendered, the TagHelper runs and emits a fresh token.

```csharp
// Controller
return this.RazorWireStream()
    .ReplacePartial("my-form", "_MyForm", model)
    .BuildResult();
```

## Fallback: Explicit Token for `update` Actions

The **`update`** Turbo Stream action replaces only the *inner HTML* of the target element—it does **not** replace the element itself. If your outer `<form>` tag remains in the DOM and only its contents are swapped, the TagHelper won't run again, and the hidden token will be stripped.

In this scenario, your partial should explicitly include the token:

```cshtml
<!-- _FormFields.cshtml -->
@Html.AntiForgeryToken()
<input type="text" name="data" />
<button type="submit">Submit</button>
```

```csharp
// Controller
return this.RazorWireStream()
    .UpdatePartial("my-form", "_FormFields", model)
    .BuildResult();
```

### Avoiding Duplicate Tokens

If `_FormFields.cshtml` is rendered inside an outer `<form asp-action="..." method="post">` on initial page load, the TagHelper will also inject a token. To avoid duplicate tokens or mixed patterns:

1. **Option A (Recommended):** Set `asp-antiforgery="false"` on the outer `<form>` and rely solely on `@Html.AntiForgeryToken()` in your fragment:
   ```cshtml
   <form asp-action="Submit" method="post" asp-antiforgery="false" id="my-form">
       @await Html.PartialAsync("_FormFields")
   </form>
   ```

2. **Option B:** Use `replace` instead of `update` so the entire `<form>` (with TagHelper) is replaced consistently.

## Summary

| Stream Action | What It Does | Token Strategy |
|---------------|--------------|----------------|
| `replace` / `ReplacePartial` | Replaces entire element | Use `<form>` TagHelper (automatic) |
| `update` / `UpdatePartial` | Replaces inner HTML only | Use `@Html.AntiForgeryToken()` explicitly |

Both methods provide the same security protection when used correctly.

## Static and Hybrid Export

Anti-forgery tokens minted during export are not valid for the eventual browser user. Plain CDN exports fail early when a page contains any anti-forgery surface because there is no backend endpoint available to mint a fresh token for the browser user.

In hybrid export, RazorWire handles the safe case automatically:

```bash
razorwire export --mode hybrid --live-origin https://api.example.com --project ./MyApp.csproj
```

When export sees a RazorWire-managed form with static `__RequestVerificationToken` inputs, including valid HTML form-associated token inputs that use `form="..."` outside the form element, it rewrites the form action to the live origin, removes the baked token inputs, and marks the form with `data-rw-antiforgery="lazy"`. The browser then fetches a fresh token from `/_rw/antiforgery/token` on first form intent or just before Turbo submits the form. Managed live calls include credentials by default when `--live-origin` is set. If the crawled app uses a path base, split-origin export normalizes `data-rw-antiforgery-endpoint` to the live app route before the runtime prefixes `--live-origin`; avoid hard-coding the crawl path base into custom live-origin token endpoints.

Hybrid export without `--live-origin` is also supported for deployments where the CDN passes RazorWire endpoints back to the backend on the same public origin. In that mode, export removes safe static token inputs and marks the form for lazy refresh while preserving an app-owned same-origin action.

You may write `rw-antiforgery="lazy"` on a form as an explicit assertion, but it is not required for safe static-token conversion. Do not use `rw-antiforgery="off"` on exported forms that contain anti-forgery tokens unless the form is loaded from a live endpoint instead of static HTML.

Export fails with `RWEXPORT006` when anti-forgery cannot be made safe: any anti-forgery marker in CDN mode, unmanaged forms, external form actions, omitted split-origin hybrid credentials, or explicit anti-forgery opt-out. This is intentionally early and noisy so stale tokens do not ship to production.

## Symptom: The First Submit Works, The Second Submit Returns 400

The most common cause is a stale or missing anti-forgery token after a stream update. Replace the whole form, or render `@Html.AntiForgeryToken()` inside the updated fragment.

## Symptom: The Anti-Forgery Error Is Helpful In Development But Generic In Production

That is intentional. Development diagnostics are controlled by `RazorWireOptions.Forms.EnableDevelopmentDiagnostics` and `IWebHostEnvironment.IsDevelopment()`. Production responses avoid exposing token details to end users.
