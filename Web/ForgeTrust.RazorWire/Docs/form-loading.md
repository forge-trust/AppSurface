# Form Loading Feedback

RazorWire form loading gives an accepted Turbo form submission visible feedback while the browser waits for token preparation or the server. It applies to forms enhanced with `rw-active="true"`; it does not add a general request or navigation loading API. The app can supply a nearby status message, and RazorWire shows its accessible top bar when no app indicator applies.

For the local, section-shared, fallback-with-failure-UX-off, and five-second Turbo Frame examples, run the [RazorWire MVC example](../../examples/razorwire-mvc/README.md#form-loading-feedback). This guide describes the package contract and its current proof status.

## Quick start

1. Keep `<rw:scripts />` in the shared layout. It loads RazorWire and, when form loading is enabled (the default), the versioned loading stylesheet from the package. The default stylesheet URL is `/_content/ForgeTrust.RazorWire/razorwire/razorwire.loading.css`.
2. Add an initially hidden, app-authored status indicator to a boundary containing the form:

```cshtml
<form method="post" action="/orders/save"
      rw-active="true"
      data-rw-loading-boundary>
    @Html.AntiForgeryToken()
    <button type="submit">Save order</button>
    <span data-rw-loading-indicator hidden role="status" aria-live="polite">
        Saving your order…
    </span>
</form>
```

3. Style only the revealed state and preserve the HTML `hidden` behavior:

```css
[data-rw-loading-indicator][hidden] {
  display: none !important;
}

[data-rw-loading-indicator]:not([hidden]) {
  display: inline-flex;
  align-items: center;
  margin-inline-start: 0.5rem;
}
```

The visible words communicate the action without relying on a spinner. Reserve room for the message if showing it should not move nearby content. RazorWire toggles only the indicator's `hidden` state; the app owns its text, semantics, placement, and styling. The package does not move focus.

## Configuration and precedence

All three global options default to `true`:

```csharp
builder.Services.AddRazorWire(options =>
{
    options.Forms.Loading.Enabled = true;
    options.Forms.Loading.ShowFallbackBar = true;
    options.Forms.Loading.PreventDuplicateSubmissions = true;
});
```

`<rw:scripts />` serializes these settings as lowercase Boolean attributes on the RazorWire runtime script:

| C# option | Script attribute |
|---|---|
| `Forms.Loading.Enabled` | `data-rw-form-loading-enabled` |
| `Forms.Loading.ShowFallbackBar` | `data-rw-form-loading-show-fallback-bar` |
| `Forms.Loading.PreventDuplicateSubmissions` | `data-rw-form-loading-prevent-duplicate-submissions` |

Let `<rw:scripts />` render these configuration attributes; they are package configuration, not per-form switches. The form Tag Helper emits `data-rw-loading="true"` on enabled `rw-active` forms. Set `data-rw-loading="off"` in the form markup to opt out.

The effective behavior follows this order:

1. `Forms.Loading.Enabled = false` disables the loading layer for every form: the Tag Helper does not add the enabled `data-rw-loading="true"` marker, and RazorWire shows no app indicator or package fallback and applies no loading-layer duplicate guard. Failed-form UX is unchanged.
2. `data-rw-loading="off"` opts one `rw-active` form out of that entire layer.
3. For an enabled form, RazorWire searches its ancestor boundaries from nearest to farthest. The first connected indicator owned by a boundary is selected.
4. A selected app indicator suppresses the package fallback. If none applies, `ShowFallbackBar = true` shows the package top bar. With `ShowFallbackBar = false`, there is intentionally no RazorWire loading visual.

`data-rw-loading-boundary` is presence-only: any value, including an empty value or `false`, declares a boundary. An indicator belongs to its nearest boundary. Within a boundary, the first owned indicator in document order is used; indicators inside a nested boundary belong to that nested boundary. Put the form and its intended indicator inside the same boundary. A boundary on `body` can provide a site-wide indicator only to forms inside that body.

For example, a form can have its own boundary and message inside a larger site boundary. The form uses its nearby message; other forms in the outer boundary can use that boundary's message. An indicator in an unrelated part of the document is not a site-wide indicator unless it belongs to an ancestor boundary of the submitting form.

When loading is enabled, `data-rw-loading-lock="true"` or `"false"` overrides only `PreventDuplicateSubmissions` for that form. Other values use the global setting. The default blocks a second submission of the same pending form, including normal submit events from Enter, submit buttons, external associated controls, and `requestSubmit()`. While the guard is active, RazorWire disables enabled submit controls associated with that form and restores only controls it disabled; controls that were already disabled stay disabled. It does not block other forms. Setting the global option to `false` or this form attribute to `false` permits overlapping submissions. Opting the form out with `data-rw-loading="off"` disables the guard with the rest of the loading layer. Direct `form.submit()` does not dispatch the submit event and bypasses Turbo loading tracking and this guard. These choices can allow duplicate side effects; server-side idempotency remains necessary.

Turning the lock off does not turn off loading feedback. Multiple accepted submissions may overlap; a shared indicator and boundary remain pending until all of their associated submissions settle. The browser-side guard is a user-experience aid, not server-side idempotency. Protect side-effecting endpoints on the server as well.

If the fallback is disabled and a form has no applicable app indicator, the user may see no immediate RazorWire feedback. Turbo may still show its delayed page progress bar for a full-page form; a Turbo Frame form has no equivalent native bar in the supported runtime. Choose this setting only when the app supplies another visible signal or accepts that delay.

## Ownership, lifecycle, and failure behavior

RazorWire adds `data-rw-loading="true"` to enabled `rw-active` forms unless the form opts out. This loading marker is independent of `data-rw-form` and failed-form settings. Disabling `Forms.EnableFailureUx` or setting `data-rw-form-failure="off"` does not disable loading. Conversely, disabling loading does not change failure handling. See [Failed Form UX](form-failures.md) for the separate failure contract.

Loading begins only after Turbo accepts the submission. Browser constraint-validation rejection and a canceled Turbo confirmation do not show pending feedback. The status remains visible until the request settles, is canceled, or the page actually departs. There is no artificial timeout or percentage. If a request never reaches a terminal state and the user cannot cancel it, the form can remain pending; the host app should provide a real recovery path for that workflow.

RazorWire snapshots and restores the previous `hidden` value on an app indicator and the previous `data-rw-loading-state` value on the form and selected boundary. Reference counting keeps shared UI pending while any associated request remains active. The package owns `data-rw-loading-state="pending"`; do not set that runtime state in app markup. If a selected indicator or boundary is replaced while its form remains connected, the live activity is rebound to the nearest applicable indicator or fallback. Removing the form settles its activity.

Lazy anti-forgery token preparation is inside the loading interval. If token refresh fails, RazorWire cancels the paused form submission without sending an unprotected POST, clears the loading state, and leaves the form retryable. The existing failed-form path owns the explanation. If failed-form UX is disabled, the host app owns that explanation. Aborting a pending token preparation or leaving the page also resumes Turbo without a POST, but does not show a failure message for that deliberate cancellation; this applies to aborts even when loading feedback is opted out. A page restored from the back-forward cache does not submit a token request that was pending when it left. See [Security & Anti-Forgery](antiforgery.md) for token setup and recovery details.

Loading applies to form submissions, not arbitrary requests or navigation. A separate accepted Turbo visit can use Turbo's navigation progress bar alongside an app-owned form indicator. While a package fallback is active, it remains the single top-level signal. If the form finishes before the visit, the fallback carries feedback until the visit finishes, including when the form had used an app indicator. A canceled visit does not change the form signal. No loading-specific public events or navigation API are added.

With JavaScript unavailable, the browser submits the ordinary HTML form and the initially hidden indicator stays hidden. The server remains responsible for authorization, validation, anti-forgery, and idempotency; client-side loading and duplicate prevention do not replace those protections.

## Styling the package fallback

The package-owned fallback is a fixed top bar marked with `[data-rw-loading-fallback]`. It includes polite status text, does not move page content or intercept clicks beneath it, observes safe-area insets, and uses a static treatment for reduced-motion and forced-colors preferences. Its focus behavior does not interrupt the submitter. These generated-node ownership rules follow [RazorWire's UI design contract](../DESIGN.md).

The packaged stylesheet defines these override points:

| CSS property | Default | Purpose |
|---|---:|---|
| `--rw-ui-accent` | `#2563eb` | Shared RazorWire accent used for the fallback's accent and progress strip. |
| `--rw-loading-fallback-z-index` | `1000` | Stacking order. |
| `--rw-loading-fallback-block-size` | `2.5rem` | Main bar block size, before the safe-area inset. |
| `--rw-loading-fallback-surface` | `#fff` | Bar background. |
| `--rw-loading-fallback-color` | `#111827` | Status text color. |
| `--rw-loading-fallback-border` | `rgb(17 24 39 / 18%)` | Bottom border color. |
| `--rw-loading-fallback-shadow` | `0 0.125rem 0.5rem rgb(17 24 39 / 14%)` | Bar shadow. |
| `--rw-loading-fallback-progress-size` | `0.1875rem` | Decorative strip thickness. |

Set these on `:root`, `body`, or an app shell to match the host theme. The animated strip is indeterminate decoration, not a percentage or completion estimate. App-owned indicators are not styled by the package.

### Strict Content Security Policy

When `Forms.Loading.Enabled` is true, `<rw:scripts />` emits a normal same-origin stylesheet link for `/_content/ForgeTrust.RazorWire/razorwire/razorwire.loading.css` (with the app path base and static-file versioning applied). Disabling form loading omits the link. The host's Content Security Policy must authorize that resource under `style-src`; for a same-origin package asset, `style-src 'self'` is sufficient. A script nonce does not authorize an external stylesheet under a nonce-only `style-src` policy. The package fallback does not rely on a nonce-less inline style block or style attribute. If the stylesheet is missing or stale, rebuild and verify the generated assets as described in the [runtime contract pipeline](runtime-contract-pipeline.md).

## Troubleshooting

### No indicator appears

Check that the form has `rw-active="true"`, the global `Forms.Loading.Enabled` option is enabled, and the form does not have `data-rw-loading="off"`. Check that the intended indicator is inside an applicable ancestor boundary. If there is no app indicator, enable `ShowFallbackBar` to use the package fallback.

### The form looks inert with the fallback turned off

With `ShowFallbackBar = false`, RazorWire intentionally shows no loading visual when no app indicator applies. Add an app-owned indicator or restore the fallback option. A Turbo Frame form may otherwise have no native progress signal.

### The local message stays hidden or appears before submission

Start the indicator with the `hidden` attribute, and make display styling match only the revealed state, as in the quick-start CSS. An unconditional `display` rule on `[data-rw-loading-indicator]` can override the browser's hidden presentation. RazorWire restores the indicator's original hidden state after its last associated request.

### A lazy anti-forgery refresh fails

The form POST must not be sent without its token. Loading clears and the form can be retried; use the existing failure UI for the explanation. If `EnableFailureUx` is off, add host-owned explanatory and recovery UI. See [Security & Anti-Forgery](antiforgery.md) and [Failed Form UX](form-failures.md).

### Source and packaged stylesheet differ

The stylesheet source is `Web/ForgeTrust.RazorWire/assets/src/razorwire.loading.css`; the generated package asset is `Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.loading.css`. From the repository root, rebuild and verify it with:

```bash
pnpm --dir Web install --frozen-lockfile
pnpm --dir Web run assets:razorwire:build
pnpm --dir Web run assets:razorwire:verify
dotnet pack Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj
```

The pack target verifies generated assets. For a stale-output failure, see [Runtime Contract Pipeline](runtime-contract-pipeline.md#pack-guard). These are repository/source-build instructions; they do not claim a public NuGet release.

## Verification status

The guide and MVC route make the approved contract discoverable, but they are not evidence that the full interaction has shipped. The manual screen-reader announcement check in the MVC example and the affected consumer application's adoption proof remain pending. Consumer proof must identify the route and Turbo Frame target, record the existing signal and lazy-token path, measure click-to-visible feedback under a controlled delay, then verify a second form using the site-wide fallback. Do not treat package documentation or an accessibility-tree assertion alone as completion of those checks.
