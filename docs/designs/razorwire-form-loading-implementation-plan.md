# Implementation plan: RazorWire form loading feedback (#827)

## Implementation plan

Source design: [approved form loading design](razorwire-form-loading-feedback.md). This plan implements its agreed form scope and leaves navigation and arbitrary requests for later. It is the input to `/autoplan` and may be amended by its reviews.

## Outcome and boundaries

An accepted remote `rw-active` form submission displays an app-authored loading indicator by the next paint, including while a lazy anti-forgery token request or cold server is pending. The indicator remains until that submission ends or is canceled. If no applicable app indicator exists, a package-owned accessible top bar appears by default. Same-form duplicate submissions are blocked by default. The package contract and one affected consumer application both need proof. The exact consumer form remains to be identified before claiming its production experience is fixed.

Loading is independent from failed-form UX. Browser validation rejection and a canceled Turbo confirmation create no loading activity. Native no-JavaScript form submission, failure events, server-side idempotency, and Turbo navigation behavior remain valid. Do not add public generic request or navigation APIs in this release.

## Work packages

0. **Consumer baseline before package design.** Identify the affected form route, Turbo Frame target, existing loading signal, lazy-token path, and click-to-visible timing under a controlled delay. Use the result to validate the private hook and adoption recipe before fixing the final package diff; retain the approved form-only public contract.
1. **Server contract and configuration.** Add `RazorWireFormOptions.Loading` with `Enabled`, `ShowFallbackBar`, and `PreventDuplicateSubmissions`, each defaulting to `true`. The Tag Helper emits a distinct loading ownership marker for enabled `rw-active` forms, even if `EnableFailureUx=false` or `data-rw-form-failure="off"`; preserve current failure marker and hidden-field semantics. `data-rw-loading="off"` overrides loading for one form. `data-rw-loading-lock="true|false"` overrides only duplicate prevention; invalid values use the configured default. Serialize global configuration through the existing RazorWire runtime configuration path. Add XML/API docs and C# tests for global and per-form precedence, disabled forms, and failure-UX independence.
2. **Activity lifecycle and lazy-token repair.** Add a private activity ledger in `assets/src/razorwire.ts`, separate from `FormFailureManager`, with per-attempt identity, form, selected boundary/indicator, and idempotent settlement. Begin on `turbo:before-fetch-request`, after accepted form submit and before the lazy anti-forgery wait. Avoid event-listener-order dependence. Settle on `turbo:submit-end`, network error, token failure, cancellation, DOM/form/frame replacement, and page departure. Repair the existing rejected lazy-token path so it cancels the paused Turbo request, clears state, allows retry, and cannot send a POST without a token. Keep failure UI responsible for explanatory errors. Use Turbo's own `aria-busy` and initiating-submitter behavior where reliable; only restore control states RazorWire itself changes.
3. **Indicator ownership and fallback.** Support presence-only `data-rw-loading-boundary`, app-owned `data-rw-loading-indicator`, runtime `data-rw-loading-state="pending"`, and styled `data-rw-loading-fallback`. Resolve the first indicator owned by each ancestor boundary, searching from the form outward; body is a shared boundary only when marked and containing the form. A selected app indicator suppresses fallback. Snapshot and restore each form/boundary state attribute and indicator hidden state exactly at zero-to-one and one-to-zero transitions. Reference counts keep shared indicators visible until all associated attempts settle, including overlapping requests on one unlocked form. Add an accessible polite status to the fallback, reduced-motion styling, and documented CSS hooks. Avoid layout shift where practical and do not move focus.
4. **Duplicate guard and Turbo bar arbitration.** For a pending form with effective locking enabled, cancel subsequent `submit` events in capture phase before Turbo sees them, including Enter, another or external associated submitter, `requestSubmit()`, and `click()`. Do not alter the first activity or other forms. `form.submit()` remains outside the contract. Coordinate the package fallback and Turbo's delayed form bar so full-page and frame submissions do not show duplicate bars. When a separate Turbo visit overlaps a form, preserve an existing visible signal and allow a visit bar when only an app indicator covers the form. A form response may hand off to navigation. Verify canceled and failed visits against installed Turbo 8.0.23.
5. **Public artifacts and adoption.** Build generated assets; update the public contract manifest, `Web/ForgeTrust.RazorWire/README.md`, a focused loading guide, generated docs metadata, repository discoverability, and the working RazorWire MVC example. Document API shape, defaults, ownership, accessibility, constraints, when to use the local indicator versus fallback, and the pitfall that this does not replace server-side idempotency. Document the actual package-feed/source-build and asset-build requirements without claiming a public NuGet release. After package verification, return to the identified consumer form, add an app-designed local indicator, and verify site-wide fallback on a second form. Record click-to-visible-feedback time under controlled delay and whether the form targets a Turbo Frame. Coordinate cross-repository adoption as a separate deliverable if that app is outside this checkout.

## Verification

- Add a normal Playwright regression test with the installed Turbo runtime: hold a lazy token response, assert feedback before any form POST; reject token refresh, assert loading clears and no form POST; retry with a good token and assert one POST. This is a failing feature test if broken, not a separate release gate.
- Browser-test next-paint feedback on five-second delay; continued visibility at ten seconds; browser invalid form and canceled confirmation; local/site-wide/fallback precedence; no duplicate Turbo bar after 500 ms; frame and full-page forms; app indicator accessibility and fallback reduced-motion behavior.
- Verify two rapid physical actions and later `requestSubmit()`/submitter `click()`, a second independent form, per-form lock off and shared-boundary counts; success, handled and unhandled HTTP failure, network failure, cancellation, replacement, and page departure. Assert idempotent cleanup and preservation of pre-disabled controls.
- Add C# and JavaScript contract tests for option defaults, precedence, marker independence, and event-path edge cases. Run formatting, TypeScript typecheck, RazorWire package tests, generated-asset verification, focused .NET tests, and solution coverage when practical.


<!-- autoplan-accepted:ceo -->
- Establish and document the installed Turbo 8.0.23 sequence: browser validation and confirmation precede Turbo's intercepted submit, which dispatches `turbo:before-fetch-request` before `turbo:submit-start` and before the lazy token listener pauses the request. The loading listener must run during that same event dispatch and show feedback by the next paint. Verify this order in a real-browser test, including canceled confirmation and invalid forms.
- On lazy token failure, resume Turbo's paused continuation only with an already-aborted fetch signal (or an equally proven no-network cancellation mechanism) so Turbo reaches its terminal cleanup without sending an unprotected POST. Prove the exact mechanism with held-token/failure/retry Playwright assertions against the installed runtime before building other lifecycle branches.
- Detect disconnected forms, frames and selected indicators with a document `MutationObserver`; settle affected attempts idempotently and restore owned state on still-connected nodes. Also settle on actual page departure, and verify frame replacement, body replacement, cancellation, and pagehide without relying on a timer or a canceled before-render event.
- Track accepted visits through Turbo navigation events separately from form activity solely for bar arbitration. Suppress Turbo's delayed form bar while a managed form is pending; if a distinct visit overlaps an app indicator, allow the visit bar, and if package fallback is visible, retain it until settlement before allowing an active visit bar. Verify event ordering and canceled/failed visit cleanup against Turbo 8.0.23 rather than assuming event names imply terminal behavior.
- Treat the four CEO additions as implementation detail and proof for existing approved form behavior, not new feature scope. The consumer app adoption proof is required for completion even when it needs a separate repository change; identify the form and access path before claiming the feature finished.
- Use a controlled browser `requestAnimationFrame` after accepted submit, while the token or form response is held, to assert that exactly one applicable indicator has computed visible style by that frame. Assert that the fixed-position package bar does not shift page layout; advise app authors to reserve local indicator space. At 600 ms assert the unrelated Turbo form bar is not simultaneously visible. Record consumer click-to-visible time without inventing a cold-start performance threshold.
- On token failure, immediately settle the corresponding ledger attempt idempotently and set an aborted fetch signal before invoking Turbo's paused `resume`; if Turbo then emits `turbo:submit-end`, it is a no-op for ledger cleanup. The first browser regression must prove no POST and successful retry. If the installed runtime does not honor this path or the observed event order, stop dependent implementation and revise the internal hook while keeping the approved user-visible contract.
- If only a selected indicator or boundary disconnects while its form stays connected, atomically release its reference count and re-resolve the same live attempt to the nearest applicable connected indicator or fallback; keep visible feedback. If the form disconnects, settle that attempt. A removed shared boundary must not settle other forms that remain connected; each live form rebinds independently. Verify these transitions with two forms and exact state restoration.
- Prototype bar arbitration against Turbo 8.0.23 before completing the runtime: use document state and a scoped CSS rule for `.turbo-progress-bar`, plus accepted `turbo:visit` and actual terminal/abort signals to distinguish a separate visit from form-only progress. A visit canceled before acceptance adds no state; a visit that starts and then fails, aborts, reloads, or loads must release arbitration state. If Turbo's shared bar disappears when a form finishes during a still-active visit, keep the package bar visible as the handoff until that visit settles. Assert one visible signal at every overlap transition; do not rely on an untested private Turbo adapter API.
- Apply the approved option precedence exactly: `Loading.Enabled=false` disables loading markers, indicators, fallback and the loading-layer duplicate guard; form opt-out does the same for that form. With loading enabled but `ShowFallbackBar=false` and no applicable app indicator, the package intentionally shows no loading visual, although Turbo's existing behavior may still apply outside the loading layer. Document this deliberate configuration and its risk of silent delay.
- Distinguish form-only progress from an accepted separate visit with document state: suppress `.turbo-progress-bar` while a managed form is pending and no visit is active, or whenever the package fallback is visible. When an accepted visit overlaps a form using an app indicator, allow Turbo's visit bar. If the form ends before that visit, show the package fallback synchronously at `turbo:submit-end` as a handoff before Turbo hides its shared bar; keep Turbo's bar suppressed until the visit terminates. Browser-test both event orders and the 500 ms transition so there is no visible gap or duplicate bar.
- Make the first delivery checkpoint the affected consumer form: identify its route, Turbo Frame target, current loading signal and controlled-delay click-to-visible timing before deciding the final package diff. Compare Turbo alone, an app-only indicator, and the approved package-plus-app approach against that case. Turbo's 500 ms page bar and absent frame bar cannot meet next-paint feedback; app-only markup does not cover the lazy token hold and reusable default. The approved package default and duplicate guard remain, with documented opt-outs.
- Make the installed Turbo held-token/failure/no-POST/retry Playwright test the first implementation test before broader ledger and bar work. If event order or aborted-signal cleanup differs, revise the internal design before extending it. Keep the supported Turbo version explicit and rerun this proof on Turbo upgrades.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:design -->
- Put consumer discovery and controlled-delay baseline first in the work-package order. Record the route, frame/full-page target, existing indicator, click-to-visible timing, and whether lazy antiforgery is active. Those facts may change the private event hook or adoption recipe, but not the approved form-only public scope without a new decision. Return to that same form after package verification for the adoption proof.
- Define the app indicator as app-authored, initially hidden, with a short visible action/status message such as “Saving…” and an accessible `role="status"`/polite announcement. The package removes and restores only its `hidden` state, never rewrites app copy, moves focus, or styles app markup. Document how to reserve space and avoid spinner-only feedback. Verify that the pending message appears in the accessibility tree, test an actual screen-reader announcement in the MVC example, and avoid double announcement when the fallback is suppressed.
- Define indicator selection as a connected form's ancestor-boundary walk, nearest first. Within each boundary, inspect owned connected indicators in document order, excluding those whose nearest boundary is nested elsewhere; select the first. An indicator already pending may be shared by another form through a reference count. On disconnection re-resolve live attempts atomically before considering fallback. Test multiple indicators, nested boundaries, a disconnected selected indicator, and two shared forms.
- Specify user-visible states and overlaps: an accepted form shows one local, site-wide, or fallback signal; explicit global/form/fallback opt-outs may show no package signal and must be documented as such. A separate accepted Turbo visit may show its own navigation bar alongside an app form indicator; fallback and Turbo bars must not duplicate each other. On form completion during a visit, preserve one progress signal through handoff. Test both overlap orders and all opt-outs.
- Keep pending visible without a timeout while a request remains live. Browser cancellation, form/frame removal, and page departure settle it; a form with no cancellation path can remain pending indefinitely if the network never terminates. Explain this pitfall and the host application's recovery choices in the guide; do not imply a fake progress percentage or invent a cold-start timer.
- The fallback is a fixed, compact top line using documented RazorWire `data-rw-ui`/`data-rw-loading-fallback` selectors and `--rw-ui-accent` plus narrowly named loading properties. It uses a polite text status, sufficient default non-text contrast, no layout shift, safe-area placement on narrow screens, and a static reduced-motion/forced-colors treatment. Verify 375px and desktop rendering, contrast, focus retention, and no duplicate announcement. Preserve host app styling for local indicators.
<!-- /autoplan-accepted:design -->

<!-- autoplan-accepted:dx -->
- Make the feature adoption clock explicit: for a developer with an existing RazorWire package/source build and `rw-active` form, measure time from opening the loading guide to the first visibly pending delayed submission. Target under five minutes with a three-step guide; record the actual human time during the MVC and consumer proof. Fresh clone, .NET 10 installation and unavailable public NuGet publishing are separate documented setup dependencies, not claimed to fit this clock.
- Before final package implementation, identify the affected consumer form route, repository/access path, adoption owner, frame target and current signal. If the app is outside this checkout, name the separate change and owner in the delivery record. Do not mark #827 complete until the package plus real consumer proof runs; the first checkpoint may adjust private hook details while preserving approved public form behavior.
- Put a copy-paste-complete loading guide within two links of the RazorWire README quickstart. It must show a working `rw-active` form, initially hidden app-authored `role="status"` indicator with visible words, an optional boundary, global C# defaults/overrides, per-form loading and lock opt-outs, site-wide versus local precedence, fallback-off consequences, and exact script/asset prerequisites. Use the MVC example as the live proof and update snippets/contract metadata together.
- Reuse the existing RazorWire problem/cause/fix/docs diagnostic pattern. In the guide, trace token refresh failure (what the user sees, why, retry path, and no-POST guarantee), missing loading marker or stale generated asset, hidden indicator styling, and the intentionally silent `ShowFallbackBar=false` combination. Link to antiforgery and runtime-contract guidance; verify the example and troubleshooting text against real browser behavior. When failed-form UX is off, document the host's responsibility to explain preparation failures.
- Preserve a CSP-compatible default fallback. Do not depend on a nonce-less dynamically injected or inline style for its visibility; use a packaged stylesheet or an equivalent proven CSP-safe path, document the asset and theme overrides, and browser-test a strict `style-src` policy. Document that app CSS must not force a `[hidden]` indicator visible and demonstrate a selector that styles only the revealed state.
- Keep the default path production-useful with no added setup: a managed form without an app indicator gets the fallback automatically, while a local indicator needs only markup/CSS and no new JavaScript. Measure whether the first delayed form shows next-paint feedback and whether the developer can find, customize, debug and roll back the behavior. No new hosted playground, generic navigation API, telemetry service or public NuGet availability claim is part of this DX polish pass.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Keep the approved feature list and use the smaller file arrangement: one private form-activity coordinator in the authored runtime, the existing options/Tag Helpers, one packaged CSP-safe stylesheet, existing contract metadata, and focused tests/docs. Do not add a public request manager, standalone navigation service, or generic event bus. The implementation spans more than eight files because package source, generated output, tests, sample and docs are required, not because it needs additional runtime layers.
- Treat each `turbo:before-fetch-request` for a loading-owned form as one accepted attempt, keyed to that request's fetch-options/signal identity and its Turbo form-submission identity once available; only RazorWire's antiforgery handler owns `preventDefault`/`resume`. The loading coordinator observes the same synchronous event and never resumes it. On token rejection or cancellation while token preparation is paused, set an already-aborted signal on that event's fetch options before calling its `resume` once; the installed Turbo 8.0.23 path then enters `requestStarted` and `requestFinished` without a POST. Prove this order first in a real browser, including retries, out-of-order unlocked attempts, and foreign listeners that only observe the event. Do not promise correct behavior if a third-party listener independently resumes or replaces RazorWire's paused continuation.
- Preserve the specific “could not prepare this form” error across Turbo's later `submit-start`/`submit-end` sequence on an aborted token request. Coordinate the existing `FormFailureManager` so its later start does not erase the preparation error and its terminal handling does not replace that error with a generic network message; emit one terminal outcome and leave controls retryable. Verify both `EnableFailureUx=true` and `false`, where the host owns explanatory text.
- Make indicator transfer a single idempotent transaction for each live attempt: release old indicator/boundary ownership, restore saved state only at zero references on connected nodes, resolve the nearest connected replacement, then acquire its ownership or fallback. Settlement during transfer is a no-op after the first release. Observe only while attempts are pending and inspect affected attempts rather than rescanning every form after every mutation.
- Implement bar arbitration as a small private state machine with explicit form-indicator and visit ownership. For an app indicator or package fallback, suppress Turbo's delayed **form** bar; with fallback disabled and no app indicator, leave Turbo's native delayed page-form bar alone (frame forms may remain intentionally silent). A separate visit may show Turbo's bar beside an app indicator while the form remains pending. If the form finishes before that visit, reveal or retain the package fallback synchronously as the handoff and suppress Turbo's shared bar until the visit settles; this prevents a gap when Turbo hides its bar on form completion. If the visit finishes first, settle visit ownership and retain only the live form's indicator/fallback. A disabled loading layer or per-form opt-out leaves Turbo untouched. Test both completion orders, canceled/failed/reload visits, and mixed forms.
- Make package completion and issue completion separate evidence states. The first checkpoint records the consumer route, repository/access path, owner, target, current signal, token path, and delay measurement. If access or cross-repository adoption is unavailable, name the separate deliverable and owner; the package can be ready, but #827 remains open until the real form and a second fallback form are proved. Verify the emitted package and generated assets, strict CSP, and no-JavaScript behavior, not just TypeScript source.
<!-- /autoplan-accepted:eng -->
## Review record

<!-- autoplan-accepted:eng -->
- Keep the approved feature list and use the smaller file arrangement: one private form-activity coordinator in the authored runtime, the existing options/Tag Helpers, one packaged CSP-safe stylesheet, existing contract metadata, and focused tests/docs. Do not add a public request manager, standalone navigation service, or generic event bus. The implementation spans more than eight files because package source, generated output, tests, sample and docs are required, not because it needs additional runtime layers.
- Treat each `turbo:before-fetch-request` for a loading-owned form as one accepted attempt, keyed to that request's fetch-options/signal identity and its Turbo form-submission identity once available; only RazorWire's antiforgery handler owns `preventDefault`/`resume`. The loading coordinator observes the same synchronous event and never resumes it. On token rejection or cancellation while token preparation is paused, set an already-aborted signal on that event's fetch options before calling its `resume` once; the installed Turbo 8.0.23 path then enters `requestStarted` and `requestFinished` without a POST. Prove this order first in a real browser, including retries, out-of-order unlocked attempts, and foreign listeners that only observe the event. Do not promise correct behavior if a third-party listener independently resumes or replaces RazorWire's paused continuation.
- Preserve the specific “could not prepare this form” error across Turbo's later `submit-start`/`submit-end` sequence on an aborted token request. Coordinate the existing `FormFailureManager` so its later start does not erase the preparation error and its terminal handling does not replace that error with a generic network message; emit one terminal outcome and leave controls retryable. Verify both `EnableFailureUx=true` and `false`, where the host owns explanatory text.
- Make indicator transfer a single idempotent transaction for each live attempt: release old indicator/boundary ownership, restore saved state only at zero references on connected nodes, resolve the nearest connected replacement, then acquire its ownership or fallback. Settlement during transfer is a no-op after the first release. Observe only while attempts are pending and inspect affected attempts rather than rescanning every form after every mutation.
- Implement bar arbitration as a small private state machine with explicit form-indicator and visit ownership. For an app indicator or package fallback, suppress Turbo's delayed **form** bar; with fallback disabled and no app indicator, leave Turbo's native delayed page-form bar alone (frame forms may remain intentionally silent). A separate visit may show Turbo's bar beside an app indicator while the form remains pending. If the form finishes before that visit, reveal or retain the package fallback synchronously as the handoff and suppress Turbo's shared bar until the visit settles; this prevents a gap when Turbo hides its bar on form completion. If the visit finishes first, settle visit ownership and retain only the live form's indicator/fallback. A disabled loading layer or per-form opt-out leaves Turbo untouched. Test both completion orders, canceled/failed/reload visits, and mixed forms.
- Make package completion and issue completion separate evidence states. The first checkpoint records the consumer route, repository/access path, owner, target, current signal, token path, and delay measurement. If access or cross-repository adoption is unavailable, name the separate deliverable and owner; the package can be ready, but #827 remains open until the real form and a second fallback form are proved. Verify the emitted package and generated assets, strict CSP, and no-JavaScript behavior, not just TypeScript source.
<!-- /autoplan-accepted:eng -->

### CEO Step 0 scope audit

Mode: SELECTIVE EXPANSION, selected by `/autoplan` for a new capability within the approved form boundary. Review depth: implementation ready. Base branch: `main`; no branch diff. The approved design and issue #827 settle the approach, so no new approach decision is needed.

Premise: the problem is lack of visible acknowledgement after an accepted form submit, especially during serverless wake and lazy token refresh. The proposed state hook addresses that directly. The do-nothing cost is repeated user actions and abandonment; the design has no measured cold-start duration, so the consumer proof must record one instead of asserting a target.

What already exists: Turbo 8.0.23 supplies form busy state, initiating-submitter disablement, confirmation and delayed full-page progress; frame submissions have no Turbo bar in the installed runtime. `RazorWireFormTagHelper` emits `data-rw-form` for lazy antiforgery or failure UX, while `FormFailureManager` owns token refresh and failure events. The new loading marker stays independent of failure markers and uses the existing runtime configuration path and build pipeline.

Twelve-month direction: a consistent activity signal across forms, navigation and other remote actions, with app-owned visuals. This plan establishes a private per-attempt ledger and only the tested form contract. Public generic activity APIs and navigation loading remain outside this release.

Landscape: official Turbo guidance supplies a delayed page bar; htmx exposes app-authored request indicators; Livewire exposes form loading and duplicate prevention. The differentiated need here is RazorWire's pre-POST lazy token wait and consistent frame/full-page behavior. These patterns support the chosen narrow extension rather than a new public abstraction.

Selective expansion scan: five small UX opportunities are contextual button copy, layout-stable indicator placement, accessible status text, reduced-motion fallback, and a second-form site-wide proof. They are already covered by the approved design's ownership and verification, so no new scope was added. Larger adjacent work (generic navigation loading, request progress percentages, an app telemetry dashboard) is outside the form blast radius and remains for later evidence. The plan already names consumer adoption as a separate deliverable if outside this checkout.

Temporal check: Hour 1 establishes the distinct loading marker and runtime config. Hours 2–3 must prove Turbo accepts the submission before activity starts and that a rejected lazy token can cancel the paused request. Hours 4–5 must settle replacement, overlap and visit-bar ownership without orphaning state. Hour 6+ verifies real browser timing, generated assets, docs, and the actual consumer form. Estimates are planning only: human team 2–4 days; CC plus review/QA roughly 4–8 hours, with the cross-repository consumer work dependent on access.

<!-- autoplan-accepted:ceo -->
- Establish and document the installed Turbo 8.0.23 sequence: browser validation and confirmation precede Turbo's intercepted submit, which dispatches `turbo:before-fetch-request` before `turbo:submit-start` and before the lazy token listener pauses the request. The loading listener must run during that same event dispatch and show feedback by the next paint. Verify this order in a real-browser test, including canceled confirmation and invalid forms.
- On lazy token failure, resume Turbo's paused continuation only with an already-aborted fetch signal (or an equally proven no-network cancellation mechanism) so Turbo reaches its terminal cleanup without sending an unprotected POST. Prove the exact mechanism with held-token/failure/retry Playwright assertions against the installed runtime before building other lifecycle branches.
- Detect disconnected forms, frames and selected indicators with a document `MutationObserver`; settle affected attempts idempotently and restore owned state on still-connected nodes. Also settle on actual page departure, and verify frame replacement, body replacement, cancellation, and pagehide without relying on a timer or a canceled before-render event.
- Track accepted visits through Turbo navigation events separately from form activity solely for bar arbitration. Suppress Turbo's delayed form bar while a managed form is pending; if a distinct visit overlaps an app indicator, allow the visit bar, and if package fallback is visible, retain it until settlement before allowing an active visit bar. Verify event ordering and canceled/failed visit cleanup against Turbo 8.0.23 rather than assuming event names imply terminal behavior.
- Treat the four CEO additions as implementation detail and proof for existing approved form behavior, not new feature scope. The consumer app adoption proof is required for completion even when it needs a separate repository change; identify the form and access path before claiming the feature finished.
- Use a controlled browser `requestAnimationFrame` after accepted submit, while the token or form response is held, to assert that exactly one applicable indicator has computed visible style by that frame. Assert that the fixed-position package bar does not shift page layout; advise app authors to reserve local indicator space. At 600 ms assert the unrelated Turbo form bar is not simultaneously visible. Record consumer click-to-visible time without inventing a cold-start performance threshold.
- On token failure, immediately settle the corresponding ledger attempt idempotently and set an aborted fetch signal before invoking Turbo's paused `resume`; if Turbo then emits `turbo:submit-end`, it is a no-op for ledger cleanup. The first browser regression must prove no POST and successful retry. If the installed runtime does not honor this path or the observed event order, stop dependent implementation and revise the internal hook while keeping the approved user-visible contract.
- If only a selected indicator or boundary disconnects while its form stays connected, atomically release its reference count and re-resolve the same live attempt to the nearest applicable connected indicator or fallback; keep visible feedback. If the form disconnects, settle that attempt. A removed shared boundary must not settle other forms that remain connected; each live form rebinds independently. Verify these transitions with two forms and exact state restoration.
- Prototype bar arbitration against Turbo 8.0.23 before completing the runtime: use document state and a scoped CSS rule for `.turbo-progress-bar`, plus accepted `turbo:visit` and actual terminal/abort signals to distinguish a separate visit from form-only progress. A visit canceled before acceptance adds no state; a visit that starts and then fails, aborts, reloads, or loads must release arbitration state. If Turbo's shared bar disappears when a form finishes during a still-active visit, keep the package bar visible as the handoff until that visit settles. Assert one visible signal at every overlap transition; do not rely on an untested private Turbo adapter API.
- Apply the approved option precedence exactly: `Loading.Enabled=false` disables loading markers, indicators, fallback and the loading-layer duplicate guard; form opt-out does the same for that form. With loading enabled but `ShowFallbackBar=false` and no applicable app indicator, the package intentionally shows no loading visual, although Turbo's existing behavior may still apply outside the loading layer. Document this deliberate configuration and its risk of silent delay.
- Distinguish form-only progress from an accepted separate visit with document state: suppress `.turbo-progress-bar` while a managed form is pending and no visit is active, or whenever the package fallback is visible. When an accepted visit overlaps a form using an app indicator, allow Turbo's visit bar. If the form ends before that visit, show the package fallback synchronously at `turbo:submit-end` as a handoff before Turbo hides its shared bar; keep Turbo's bar suppressed until the visit terminates. Browser-test both event orders and the 500 ms transition so there is no visible gap or duplicate bar.
- Make the first delivery checkpoint the affected consumer form: identify its route, Turbo Frame target, current loading signal and controlled-delay click-to-visible timing before deciding the final package diff. Compare Turbo alone, an app-only indicator, and the approved package-plus-app approach against that case. Turbo's 500 ms page bar and absent frame bar cannot meet next-paint feedback; app-only markup does not cover the lazy token hold and reusable default. The approved package default and duplicate guard remain, with documented opt-outs.
- Make the installed Turbo held-token/failure/no-POST/retry Playwright test the first implementation test before broader ledger and bar work. If event order or aborted-signal cleanup differs, revise the internal design before extending it. Keep the supported Turbo version explicit and rerun this proof on Turbo upgrades.
<!-- /autoplan-accepted:ceo -->

<!-- AUTONOMOUS DECISION LOG -->
## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|-------|----------|----------------|-----------|-----------|----------|
| 1 | CEO | Keep the approved form-first scope and defer generic request/navigation UI | Mechanical | Explicit over clever | The accepted design limits this release to forms while the private ledger leaves room to extend after evidence. | Publishing a generic API now |
| 2 | CEO | Specify the Turbo cancellation proof and replacement cleanup before implementation | Mechanical | Choose completeness | The spec review identified ambiguity in the lifecycle that could leave a stale lock or send an unprotected request. | Leaving terminal behavior implicit |
| 3 | CEO | Preserve feedback through indicator removal and visit overlap | Mechanical | Choose completeness | The second spec review exposed disconnected indicators and Turbo's shared bar as ways feedback could disappear during an accepted request. | Treating DOM/bar loss as silent completion |
| 4 | CEO | Fix option precedence and visit handoff policy | Mechanical | Reuse the approved design | The final spec review found missing semantics already decided in the approved design and a bar overlap ambiguity with a concrete transition path. | Leaving fallback-off and global-off undefined |
| 5 | CEO | Identify the consumer form and prove lazy-token cancellation before the larger runtime | Mechanical | Explicit over clever | The independent strategy review showed that these two facts determine whether the approved package design is feasible and proportionate. | Building all lifecycle machinery before validating the observed case |

### CEO review: strategy and scope

Mode: SELECTIVE EXPANSION. The independent in-host CEO voice reviewed implementation hash `9f4c026fd0860f7354fdd7edbae9fb73b70584b476f0ff95cfbf435dfdbbe45a` and found one high and four medium concerns. The outside Codex CLI was unavailable because this review already runs under Codex; there is no independent external-provider consensus. Its strongest concern is that the affected consumer form is still unidentified. The approved product direction remains in force, with evidence gathering and the lazy-token test moved to the front.

#### CEO Step 0 disposition

The target is the first accepted click, not cold-start detection. Existing Turbo feedback is delayed for full-page forms, absent for frame forms in the installed runtime, and begins after the lazy-token hold; an app-only spinner would fix one page but not the reusable package contract the user approved. The chosen package-plus-consumer approach is still the best fit; generic request/navigation activity, percentage progress, and a telemetry dashboard are not in scope. An earlier scope review recorded the 10x check and three spec reviews. The last spec review scored 7/10 and left four findings; option precedence and bar handoff were specified afterward and have not had a fourth spec pass.

#### Section 1: Architecture review

The new boundary is a form-only browser activity ledger receiving accepted Turbo request events; it does not own server requests or failed-form rendering. The server Tag Helper and scripts output determine which forms participate and pass three loading defaults; the ledger resolves an app indicator or fallback and reference-counts shared DOM state. This reuses the existing form and asset pipeline while keeping navigation use of the ledger private.

```text
RazorWireFormOptions.Loading -> FormTagHelper marker --+
                         -> ScriptsTagHelper config ---+--> browser runtime
Turbo accepted submit -> before-fetch-request -> activity ledger -> nearest owned indicator
         |                       |                       \-> package fallback if allowed
         |                       +-> FormFailureManager lazy token hold
         +-> submit-end / error / removal / pagehide -> idempotent settle
separate Turbo visit -> private bar arbiter -> Turbo bar or fallback handoff
```

Happy path: marker and config are present, accepted event creates one activity, a selected indicator becomes visible, and terminal event restores prior values. Nil path: missing form marker means no RazorWire loading layer; Turbo retains native behavior. Empty path: a boundary without an owned indicator is skipped in the outward search, then fallback policy applies. Error path: token/network failure settles the attempt and lets existing failure UX explain the problem. The ledger state is `absent -> pending -> settled`; each attempt may settle once, and duplicate terminal events are no-ops. A shared boundary count moves `0 -> N -> 0`, with snapshot/restore only at the outer transitions. At 10x forms, the event listener cost is constant per event and mutation checks should iterate active attempts only; at 100x active attempts, an unbounded whole-document scan would become the first bottleneck, so keep DOM observation proportional to pending forms. No new auth or data endpoint is introduced. Rollback is the `Loading.Enabled=false` switch plus the prior package asset version; verify the old and new package/asset pair in a consumer before rollout.

#### Section 2: Error and rescue map

| Codepath | Failure / class | Rescue | User result and proof |
|---|---|---|---|
| `FormFailureManager` token refresh | fetch rejection, non-OK response, missing token, invalid JSON | Existing failure event plus abort-before-resume and idempotent loading settle | Failure UI says preparation failed; Playwright proves zero form POST and later retry. |
| Loading event intake | missing or opted-out form; malformed lock value | Ignore absent/disabled form; use global lock default for malformed value | Native Turbo behavior, no stale marker; option tests. |
| Boundary resolution | empty boundary or disconnected indicator | Search outward; atomically rebind a live attempt or fallback | Visible status remains; DOM replacement test. |
| Activity termination | duplicate end/error, removed form, pagehide | Idempotent settle, restore only state RazorWire owned | No stuck spinner or unlocked other form; event-path tests. |
| Turbo bar arbitration | visit failure/abort or shared bar hidden on form finish | Release visit state on terminal signal; fallback handoff while visit lives | At least one progress signal; overlap browser test. |

No catch-all should silently dismiss an accepted activity. A token failure requires a user-visible explanation from failed-form UX when that mode is enabled; with failure UX disabled, the loading layer may only clear, so the host remains responsible for an explanatory error. The proposed aborted-signal mechanism is a hypothesis until the first real-browser test proves Turbo reaches `submit-end` without a POST.

#### Section 3: Security and threat model

No new endpoint, secret, database read, or authorization decision is added. The primary risk is accidentally resuming a lazy-token form without a token; the no-POST browser assertion is therefore a required P1 proof. Attribute values are finite flags or presence selectors, and app indicator text is app-authored markup rather than inserted unsanitized server content. A loading lock is only client-side duplicate prevention; the docs must retain server idempotency advice because direct `form.submit()`, disabled JavaScript, and retries can still submit.

#### Section 4: Data flow and interaction edge cases

```text
rw-active + loading config -> marker/flag parsing -> accepted Turbo event
  -> token hold or fetch -> unique activity -> DOM state -> settlement
  missing/invalid flag -> default or no layer; disconnected DOM -> rebind/settle
  token error -> aborted signal before resume -> no POST -> retry possible
  duplicate submit -> capture guard -> no second activity; lock-off -> two IDs
```

Two completion orders matter: token failure may precede or follow a form removal, and `submit-end` may precede or follow an overlapping visit's terminal event. Idempotent attempt settlement handles both token/removal orders; counts belong to activity identities rather than forms, so two unlocked submissions can settle in either order. The visit state and fallback handoff must be tested in both orders because Turbo's form completion can hide its shared bar. Invalid form and canceled confirmation have no activity, an external associated submitter is guarded, other forms stay usable, and a disconnected indicator rebinds while a disconnected form settles. A ten-second server wait must not trigger any time-based cleanup.

#### Section 5: Code quality review

Keep loading ownership independent of `data-rw-form`, whose current meaning includes failure UX and lazy antiforgery. Put the private ledger beside the existing `FormFailureManager` rather than extending that class's already mixed token, failure, and styling duties. Use one idempotent `settle` operation for every terminal path, one boundary resolver for initial selection and rebinding, and one effective-lock parser for global/per-form precedence. Avoid a generic public request manager, arbitrary event bus, or hidden Turbo adapter calls until an actual adopter requires them.

#### Section 6: Test review

```text
TagHelper/options ---- C# defaults, precedence, failure-off and disabled-form tests
accepted Turbo event -- Playwright invalid/confirm, next-frame, token hold/fail/retry
ledger + indicators - JS counts, restoration, opt-out, DOM replacement tests
duplicate guard ---- Playwright double click, Enter, external submitter, requestSubmit
bar arbitration ---- Playwright frame/page, 600-ms Turbo bar, visit order/error
distribution ------- generated-assets verification + MVC example + consumer proof
```

The Friday-night shipping test is the held lazy-token failure with zero POST and a successful one-POST retry; it rejects the highest-risk silent and unsafe failure. Hostile QA should remove a shared indicator while two forms are pending, then finish them in reverse order and verify exact prior state restoration. Chaos proof should abort a visit while a slow form is pending and verify no stale bar suppression. Prefer deterministic request interception and `requestAnimationFrame` over fixed sleeps; the 600 ms observation exists only to cross Turbo's configured 500 ms bar delay. No prompt or LLM evaluation suite applies.

#### Section 7: Performance review

The new runtime state is bounded by currently pending form attempts and selected DOM nodes, not all forms ever rendered. Boundary search is ancestor traversal plus owned-indicator lookup; cache only while connected, because Turbo can replace markup. No database query, index, worker, connection pool, or server cache is introduced. The slowest paths are initial boundary search, mutation rebinding, and visit-state coordination; browser timing should confirm they do not delay the next paint on the MVC example.

#### Section 8: Observability and debuggability review

The public observable contract is `data-rw-loading-state` on form/boundary and the selected indicator or fallback; the app can inspect it without a new telemetry API. Record the controlled consumer click-to-visible timing and selected path (local, site-wide, fallback) in the acceptance proof. A package-wide dashboard, per-request metrics, and trace propagation would add an operator contract without a stated owner; there is no cross-service transaction introduced by the loading layer. The focused guide should include a short troubleshooting table for missing marker, hidden indicator, fallback disabled, failed token refresh, and lingering pending state.

#### Section 9: Deployment and rollout review

No migration or server release order is required. The package runtime, generated asset, and public contract manifest must be verified together before the example and consumer update; an old asset with new markup would silently omit feedback. Start with the MVC delayed-form proof, then install the verified package/source build in the affected application and measure its form. If regression appears, set `Loading.Enabled=false` or revert the package/asset pair; this is reversible without data migration. Post-deploy smoke checks must cover a slow form, token failure/retry, and a second form without a local indicator.

#### Section 10: Long-term trajectory review

The private activity identity can later accept navigation sources, but this release publishes only form semantics. The main debt risk is coupling to Turbo 8.0.23 event order and shared progress-bar behavior; document that version and rerun browser proofs on upgrade. Reversibility is 4/5 because the global switch and package rollback exist, but consumer markup may depend on the new attributes. The 12-month direction is one activity model for several remote actions only after form usage supplies evidence; it is not a public API promise today.

#### Section 11: Design and UX review

The first visible state after accepted submit is app-authored form or site status, otherwise the fixed package bar. During a long wait it remains stable; on error it clears and the existing failure UX explains recovery; on success it clears as the response renders. Empty/no-indicator with fallback disabled is an explicit app choice, not an accidental broken state. On mobile, the top bar stays fixed without moving content; app indicators own responsive placement. Keyboard and screen-reader users retain form focus, Turbo's busy semantics, and a polite status; reduced motion uses a steady fallback line. After implementation, inspect the rendered example and affected app for contrast, layout shift, repeated announcements and loading/failure transitions.

```text
Ready -> accepted submit -> local/site/fallback pending -> success response
   \-> invalid/confirmation canceled -> Ready
Pending -> token/network failure -> indicator clears + failure message -> Retry
Pending -> separate visit -> Turbo bar or fallback handoff -> visit terminal
```

#### CEO required outputs and disposition

**NOT in scope:** Public generic request/navigation APIs (deferred by the approved design until a non-form adopter proves semantics); percentage progress (rejected because serverless wake has no honest completion fraction); package telemetry dashboard (no operator owner). No new TODO was written because the generic API is already explicitly deferred in the approved design and no independently approved backlog item arose in this review.

**What already exists:** Turbo provides confirmation, `aria-busy`, initiating-submitter disablement and a delayed page bar; RazorWire provides a Tag Helper, runtime config, token refresh, failed-form UX, generated assets, MVC example, and integration test harness. The new layer reuses these surfaces and fills the early feedback and frame gap.

**Dream state delta:** Current forms can look inert through token prep and server wake. This plan gives a verified form activity contract and real consumer proof. The one-year ideal is cross-action loading only after navigation and other request semantics have been observed.

| Error & Rescue class | Trigger | Rescue | User sees | Verification |
|---|---|---|---|---|
| Token refresh failure | Network/HTTP/malformed token | Abort paused request, settle, existing failure handler | Preparation error if failure UX on | Held token failure/retry Playwright |
| DOM disconnect | Form/indicator/boundary removed | Settle form or rebind indicator | Status stays or clears on departure | Two-form replacement browser test |
| Visit failure | Aborted/failed separate visit | Release arbitration state | Form status or handoff bar remains | Overlap order browser test |

| Failure mode | Rescued? | Test? | User sees? | Logged? |
|---|---:|---:|---|---|
| Token failure while Turbo continuation paused | Planned | Planned | Existing failure UX or host-owned message | No new telemetry |
| Shared indicator removed while another form remains | Planned | Planned | Rebound indicator or fallback | State hook |
| Separate visit ends before form | Planned | Planned | Form indicator remains | State hook |
| Form ends before separate visit | Planned | Planned | Turbo bar then fallback handoff | State hook |

Critical gaps in the written plan: 0, conditional on the first browser proof. Evidence dependency: exact consumer form remains unknown; the implementation cannot be declared complete until it is identified and measured. The Turbo overlap behavior remains a prototype to verify, not a claimed tested fact.

```text
Deployment: option/TagHelper -> built asset -> package tests -> MVC browser proof
         -> consumer package install -> delayed form proof -> release notes
Rollback: regression? -> Loading.Enabled=false -> verify old Turbo form behavior
         -> revert package/asset pair if needed -> rerun smoke test
```

No preexisting ASCII diagram in the touched RazorWire code/docs was identified as needing an update; the new diagrams above describe the proposed flow only.

CEO dual voices, outside provider unavailable:

| Dimension | In-host CEO reviewer | Outside Codex | Consensus |
|---|---|---|---|
| Premises valid? | Consumer pain credible; target form unknown | Unavailable | N/A |
| Right problem? | Yes, after case confirmation | Unavailable | N/A |
| Scope calibration? | Maintenance breadth risk | Unavailable | N/A |
| Alternatives explored? | Needs target-case comparison | Unavailable | N/A |
| Competitive risk? | Must demonstrate earlier visible feedback | Unavailable | N/A |
| Six-month trajectory? | Keep public API narrow and Turbo proof current | Unavailable | N/A |

CEO Completion Summary: mode SELECTIVE EXPANSION; 5 in-host strategy concerns, all mapped to sequencing or already-approved scope; 3 spec launches, latest 7/10 with post-review corrections not independently rechecked; 11 sections examined; 5 failure paths mapped and 0 written-plan critical gaps; outside Codex unavailable due harness mismatch; consumer form evidence remains open; no new feature expansion accepted. Approval readiness for CEO-only amendments: PASS against approved design and audit rows 1–5. Phase status: issues_open because consumer evidence and Turbo prototype are still unverified.

## Implementation Tasks

- [ ] **T1 (P1, human: ~2h / CC: ~30min)** — Consumer evidence — Identify the affected form, target, current signal, and controlled delay before final package sizing.
  - Surfaced by: CEO premise challenge — exact form still unknown.
  - Files: affected consumer repository to be determined.
  - Verify: recorded click-to-visible timing and a note of frame/full-page target.
- [ ] **T2 (P1, human: ~4h / CC: ~30min)** — Lazy token path — Prove held-token feedback, zero POST on failure, and one POST after retry on installed Turbo 8.0.23.
  - Surfaced by: CEO feasibility and Error & Rescue map — paused continuation may not settle safely.
  - Files: `Web/ForgeTrust.RazorWire/assets/src/razorwire.ts`, focused browser test to be determined.
  - Verify: Playwright test passes with controlled token responses and observed event order.
- [ ] **T3 (P1, human: ~1d / CC: ~2h)** — Loading runtime — Implement ledger, boundary transfer, fallback, duplicate guard, and tested bar handoff.
  - Surfaced by: CEO architecture and data-flow review — missing early feedback and overlap cleanup.
  - Files: `Web/ForgeTrust.RazorWire/assets/src/razorwire.ts`, Tag Helpers, options, and tests.
  - Verify: C# and browser matrix above, including two forms and opposite completion orders.
- [ ] **T4 (P2, human: ~1d / CC: ~2h)** — Adoption and docs — Build assets, document the public contract, update the MVC example, then prove the consumer form.
  - Surfaced by: CEO deployment and trajectory review — package and asset skew can silently remove feedback.
  - Files: RazorWire README, loading guide, contract manifest, generated assets, MVC example, consumer repo to be determined.
  - Verify: generated-asset check, focused package tests, delayed MVC and consumer form checks.

<!-- autoplan-accepted:design -->
- Put consumer discovery and controlled-delay baseline first in the work-package order. Record the route, frame/full-page target, existing indicator, click-to-visible timing, and whether lazy antiforgery is active. Those facts may change the private event hook or adoption recipe, but not the approved form-only public scope without a new decision. Return to that same form after package verification for the adoption proof.
- Define the app indicator as app-authored, initially hidden, with a short visible action/status message such as “Saving…” and an accessible `role="status"`/polite announcement. The package removes and restores only its `hidden` state, never rewrites app copy, moves focus, or styles app markup. Document how to reserve space and avoid spinner-only feedback. Verify that the pending message appears in the accessibility tree, test an actual screen-reader announcement in the MVC example, and avoid double announcement when the fallback is suppressed.
- Define indicator selection as a connected form's ancestor-boundary walk, nearest first. Within each boundary, inspect owned connected indicators in document order, excluding those whose nearest boundary is nested elsewhere; select the first. An indicator already pending may be shared by another form through a reference count. On disconnection re-resolve live attempts atomically before considering fallback. Test multiple indicators, nested boundaries, a disconnected selected indicator, and two shared forms.
- Specify user-visible states and overlaps: an accepted form shows one local, site-wide, or fallback signal; explicit global/form/fallback opt-outs may show no package signal and must be documented as such. A separate accepted Turbo visit may show its own navigation bar alongside an app form indicator; fallback and Turbo bars must not duplicate each other. On form completion during a visit, preserve one progress signal through handoff. Test both overlap orders and all opt-outs.
- Keep pending visible without a timeout while a request remains live. Browser cancellation, form/frame removal, and page departure settle it; a form with no cancellation path can remain pending indefinitely if the network never terminates. Explain this pitfall and the host application's recovery choices in the guide; do not imply a fake progress percentage or invent a cold-start timer.
- The fallback is a fixed, compact top line using documented RazorWire `data-rw-ui`/`data-rw-loading-fallback` selectors and `--rw-ui-accent` plus narrowly named loading properties. It uses a polite text status, sufficient default non-text contrast, no layout shift, safe-area placement on narrow screens, and a static reduced-motion/forced-colors treatment. Verify 375px and desktop rendering, contrast, focus retention, and no duplicate announcement. Preserve host app styling for local indicators.
<!-- /autoplan-accepted:design -->

<!-- autoplan-baseline-edits:design {"sourceSha256":"7d87ab6896bacee7e36bf2a8292f5ebc47a52c3d276cf68b1c4b1b7bc596b4e7","replacements":[{"oldText":"## Work packages\n\n1.","newText":"## Work packages\n\n0. **Consumer baseline before package design.** Identify the affected form route, Turbo Frame target, existing loading signal, lazy-token path, and click-to-visible timing under a controlled delay. Use the result to validate the private hook and adoption recipe before fixing the final package diff; retain the approved form-only public contract.\n1."},{"oldText":"After package verification, identify one affected consumer form, add an app-designed local indicator, and verify site-wide fallback on a second form.","newText":"After package verification, return to the identified consumer form, add an app-designed local indicator, and verify site-wide fallback on a second form."}]} -->

### Design review: form feedback contract

Step 0 scope: 7/10. This is an OPERATE interaction in a host application's form, not a new RazorWire page. The package owns only the fallback line and state hooks under [RazorWire's generated UI contract](../../Web/ForgeTrust.RazorWire/DESIGN.md); the application owns fields, button, local indicator, type and layout. Existing patterns are Turbo's delayed page bar, RazorWire's failure status, and the app's own form UI. The native design reviewer inspected implementation snapshot `5f53a0773d724099692738681f3a0f062aedf964364df959ec4399aeee251391` and found two high and three medium gaps. Outside Codex review was unavailable in this Codex harness; consensus is N/A.

| Pass | Before → after | Finding and disposition |
|---|---:|---|
| 1. Information architecture | 7 → 9 | The first visible information is acceptance/progress at the form or top edge, then a terminal response or error. Consumer discovery is the first delivery checkpoint; the package does not impose a page hierarchy. |
| 2. Interaction states | 6 → 9 | Added explicit empty/opt-out, pending, error, success, overlap and handoff rules. No fabricated percentage or timer. |
| 3. User journey | 7 → 9 | The first click receives a next-paint acknowledgement; long waits remain calm and stable; failure clears loading and presents the existing failure message or host recovery. A hung request with no cancel path remains pending. |
| 4. AI slop risk | 7 → 9 | Rejected all three exploratory mockups: A makes the fallback a thick banner, B imposes a branded form theme, and C invents 42% progress. The approved direction is a thin app-inheriting line and app-owned local UI. |
| 5. Design system alignment | 7 → 9 | Reused `--rw-ui-accent`, `data-rw-ui` and compact generated UI rules from `DESIGN.md`; no package styling on app markup. |
| 6. Responsive and accessibility | 6 → 9 | Specified visible words with local spinner, status semantics, fixed bar safe-area behavior, contrast, reduced motion, focus retention, and real screen-reader check. |
| 7. Decisions register | 5 resolved, 0 deferred | Ordering, local status, boundary tie-break, overlap visibility and indefinite pending are resolved in accepted obligations. The exact consumer route remains an evidence task, not a design preference. |

The information hierarchy for this feature is deliberately small:

```text
Form action -> nearby app status (if owned) -> submitted result or error
            -> site status (if owned)
            -> package top line (fallback only)
Separate visit -> Turbo navigation bar, except while fallback is the visible shared signal
```

| Feature | Loading | Empty / opt-out | Error | Success | Partial / overlap |
|---|---|---|---|---|---|
| App indicator | Visible text plus optional spinner, polite status | Hidden; if none applies search outward | Clears, then existing failure UX or app error | Clears as response renders | Shared until last form settles; visit may have its own bar |
| Package fallback | Thin fixed line and polite “Processing…” | Hidden when app indicator wins, or disabled by setting | Clears; error belongs to existing UX | Clears or hands off to navigation | Stays through visit handoff if Turbo bar would disappear |
| Form controls | Turbo busy state and guarded duplicate submit | Native controls if feature opted out or JavaScript unavailable | Retry allowed after settle | Response owns next state | Independent forms remain usable |

| Step | User does | User feels / sees | Plan response |
|---|---|---|---|
| 1 | Finds a form and presses Save | Expects acknowledgement | Existing app layout and control labels remain familiar. |
| 2 | Waits through token preparation or server wake | Might doubt the click | One visible applicable status appears by the next paint; focus remains. |
| 3 | Waits five or ten seconds | Wants reassurance without false precision | Status persists, copy stays factual, no fake percentage. |
| 4 | Receives result or failure | Needs closure or recovery | State clears once; response or existing failure UX takes over; retry works. |
| 5 | Uses the app later | Trusts a repeatable interaction | The same contract works on another form and a frame form. |

At five seconds the interaction must still feel active, at five minutes a truly hung request must be recoverable through browser/navigation or an app-provided cancel path, and over repeated use the feedback must stay consistent without pretending to know server progress.

Design litmus (OPERATE surface): (1) Brand in first screen: NO as a package requirement, because the host app owns brand; (2) one visual anchor: YES, the chosen local status or fallback line; (3) scan by headlines: NO, this interaction introduces no headings; (4) one job per section: YES, status only acknowledges work; (5) cards necessary: NO, no cards; (6) motion improves hierarchy: NO, motion is optional and steady state conveys the message; (7) premium without shadows: YES, no decorative shadows are needed. No approved plan contains a hard-rejection card/grid/hero pattern. The three generated images passed the automatic image check but were manually rejected for contract violations, so none is an implementation reference.

**NOT in scope:** app theme, page composition, generic navigation UI, percentage completion, a package cancel button, and a new timeout. The app controls its own status placement and recovery action; the package owns the form lifecycle and fallback. **What already exists:** Turbo busy semantics and delayed page bar, RazorWire form failure status, the generated UI design contract, and an MVC example for proof. No new design TODO was accepted: the remaining consumer-form identification and screen-reader proof are in-scope delivery tasks.

#### Design decision audit additions

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 6 | Design | Move consumer baseline ahead of package implementation | Mechanical | Explicit over clever | The first consumer case must be known before finalizing the private hook and adoption recipe. | Discovering it only after the package is built |
| 7 | Design | Require visible words and accessible status for app indicators | Mechanical | Choose completeness | Spinner-only markup can look active but communicate nothing to assistive technology. | Visual-only spinner contract |
| 8 | Design | Specify connected, owned indicator selection and rebind | Mechanical | Explicit over clever | Nested boundaries and shared forms otherwise choose different indicators in different implementations. | Implicit first-match behavior |
| 9 | Design | Keep the fallback compact and reject the generated concepts | Mechanical | Reuse the design contract | All generated variants either impose host UI or imply false progress. | Adopting a generated full-form theme |
| 10 | Design | Document indefinite pending and overlap signals | Mechanical | Choose completeness | A no-timeout contract needs an honest recovery explanation and one signal per activity. | Timer-based disappearance |

#### Design implementation tasks

- [ ] **D1 (P1, human: ~2h / CC: ~30min)** — Consumer baseline — Identify the form and measure controlled-delay feedback before final package sizing. Verify its route, target, lazy token path and current visual response.
- [ ] **D2 (P1, human: ~4h / CC: ~1h)** — Indicator contract — Implement exact nested ownership, shared counts and rebind. Verify multiple indicators, two forms and disconnected nodes in browser tests.
- [ ] **D3 (P1, human: ~3h / CC: ~45min)** — Accessible feedback — Specify and test app status semantics and compact fallback across 375px, desktop, reduced motion, contrast and screen-reader announcement.
- [ ] **D4 (P2, human: ~1h / CC: ~20min)** — Guide — Document opt-outs, honest indefinite pending, recovery responsibility and overlap behavior with app-owned examples.

Design completion summary: Step 0 7/10; overall 6/10 → 9/10 (minimum of six scored passes), five auto-decisions, zero taste choices and zero unresolved design decisions. Three mockups generated, zero approved. The implementation plan is design-complete as a specification; rendered MVC and consumer visual QA remain required during implementation.

<!-- autoplan-accepted:dx -->
- Make the feature adoption clock explicit: for a developer with an existing RazorWire package/source build and `rw-active` form, measure time from opening the loading guide to the first visibly pending delayed submission. Target under five minutes with a three-step guide; record the actual human time during the MVC and consumer proof. Fresh clone, .NET 10 installation and unavailable public NuGet publishing are separate documented setup dependencies, not claimed to fit this clock.
- Before final package implementation, identify the affected consumer form route, repository/access path, adoption owner, frame target and current signal. If the app is outside this checkout, name the separate change and owner in the delivery record. Do not mark #827 complete until the package plus real consumer proof runs; the first checkpoint may adjust private hook details while preserving approved public form behavior.
- Put a copy-paste-complete loading guide within two links of the RazorWire README quickstart. It must show a working `rw-active` form, initially hidden app-authored `role="status"` indicator with visible words, an optional boundary, global C# defaults/overrides, per-form loading and lock opt-outs, site-wide versus local precedence, fallback-off consequences, and exact script/asset prerequisites. Use the MVC example as the live proof and update snippets/contract metadata together.
- Reuse the existing RazorWire problem/cause/fix/docs diagnostic pattern. In the guide, trace token refresh failure (what the user sees, why, retry path, and no-POST guarantee), missing loading marker or stale generated asset, hidden indicator styling, and the intentionally silent `ShowFallbackBar=false` combination. Link to antiforgery and runtime-contract guidance; verify the example and troubleshooting text against real browser behavior. When failed-form UX is off, document the host's responsibility to explain preparation failures.
- Preserve a CSP-compatible default fallback. Do not depend on a nonce-less dynamically injected or inline style for its visibility; use a packaged stylesheet or an equivalent proven CSP-safe path, document the asset and theme overrides, and browser-test a strict `style-src` policy. Document that app CSS must not force a `[hidden]` indicator visible and demonstrate a selector that styles only the revealed state.
- Keep the default path production-useful with no added setup: a managed form without an app indicator gets the fallback automatically, while a local indicator needs only markup/CSS and no new JavaScript. Measure whether the first delayed form shows next-paint feedback and whether the developer can find, customize, debug and roll back the behavior. No new hosted playground, generic navigation API, telemetry service or public NuGet availability claim is part of this DX polish pass.
<!-- /autoplan-accepted:dx -->

### DX review: package adoption

Step 0 classification: DX POLISH for an existing ASP.NET Core MVC library and its loading guide. The primary developer is an MVC application builder already using RazorWire forms. They expect a useful default, then a small markup change for app-owned status; they can tolerate a few minutes for one form, but a package-feed/bootstrap step is a separate setup path. The current feature time to hello world is **unmeasured** because the loading feature does not exist yet. Target: under five human minutes from opening the guide in an app with the RazorWire package/source build and an `rw-active` form to a visibly pending delayed submission. A fresh repository clone or public NuGet install is not included because the public `v0.1` path is not live; document and measure those separately rather than calling a warm demonstration a cold-start result.

**Developer perspective.** I already have a RazorWire MVC form that posts a normal server action. I know its view and I can run the app, but I cannot tell a user whether the click was accepted while the server wakes. I open the package README and expect one short path to a safe default, then a local “Saving…” message I can style with my app's CSS. I should not need a new JavaScript registration, a separate spinner framework, or knowledge of Turbo's private adapter. When I paste the example, I expect the message to stay hidden before submission, become visible after the first accepted click, and clear on every result or cancellation. If my form uses a lazy antiforgery token, I need to know that preparation failure sends no POST and that the user can retry. If I disable the fallback, I need the docs to tell me that a form without an app indicator may look inert. I also need to know where the generated asset comes from and what to verify if source and output differ. My existing failure UI should continue to explain errors. This is a predicted journey derived from the current README, form-failure guide and runtime contract, not an observed human onboarding session.

**Competitive benchmark.** Time boundaries are deliberately comparable only for the RazorWire target above; peer docs describe syntax and defaults but do not provide measured human onboarding times. [Turbo's handbook](https://turbo.hotwired.dev/handbook/drive) documents a page bar after 500 ms, [htmx indicators](https://htmx.org/attributes/hx-indicator/) expose an inherited indicator hook, and [Livewire loading](https://livewire.laravel.com/docs/3.x/wire-loading) uses element markup with request-driven visibility. The RazorWire advantage under test is next-paint feedback through lazy-token preparation and consistent frame/full-page forms, with a default fallback. No competitive time claim is made.

| Stage | Developer does | Evidence and friction | Planned response |
|---|---|---|---|
| Discover | Finds RazorWire in repository/package README | README has a 60-second quickstart but no loading entry yet | Link the new loading guide from form and quickstart navigation. |
| Evaluate | Compares Turbo's bar and app-owned indicators | Turbo bar is delayed and absent on frame submissions in installed runtime | State the exact gap and show default/local paths. |
| Install | Uses existing source build or configured package feed | Public NuGet v0.1 is not live | Document the actual source/feed path and asset pair. |
| Hello World | Runs MVC example and delayed form | Current example proves failures, not new loading | Add a delayed form proving fallback and local status. |
| Integrate | Adds local boundary/indicator to an existing form | Selector ownership and `hidden` CSS can confuse | Provide complete markup/CSS/C# snippets and precedence table. |
| Debug | Checks token or missing-indicator issue | Runtime already has failure diagnostics and asset verifier; no loading guide | Add problem/cause/fix/docs troubleshooting for three paths. |
| Upgrade | Takes new package and generated assets together | Stale runtime silently omits hooks | Keep pack verify and versioned asset guidance. |
| Scale | Reuses a site-wide boundary across forms | Shared state and visit overlap are unfamiliar | Document scope and counts through example and tests. |
| Migrate | Rolls back or opts a form out | Global/per-form switches exist in proposed API | Show exact switches and preserve native/Turbo behavior. |

The magical moment is a controlled slow `rw-active` form showing the package fallback by the next paint with no app markup, followed by a two-element local “Saving…” example taking precedence. The vehicle is the existing MVC sample plus one focused, copyable guide. The proposed three-step adoption path is: verify package and asset version; run the sample delayed form; paste a local indicator into one existing form and observe it in a deliberately delayed request. This is a target to test with a developer, not a measured duration.

First-time confusion log, inferred from actual docs: T+0:00 the developer opens `Web/ForgeTrust.RazorWire/README.md` and sees the 60-second quickstart. T+0:30 they learn the public v0.1 feed is not yet available and choose the source-backed sample. T+1:00 they run the sample and locate `/Reactivity/FormFailures`, which currently demonstrates failures rather than loading. T+2:00 they search for a loading recipe and have no page to copy yet. T+3:00 they may write a local spinner, but that would miss the reusable fallback and lazy-token hold. The new guide, example and link are the planned fixes; these timestamps are a walkthrough, not measured user behavior.

DX dual voices: the native combo/sub reviewer evaluated input `babe30054eb65fda932fd402fd65eb71b4ed2520633e8eea34369ebcaeea82f4` and found one high and three medium issues. The outside Codex CLI is unavailable under this Codex harness; each consensus cell is N/A, not confirmation.

| Dimension | Native reviewer | Outside Codex | Consensus |
|---|---|---|---|
| Getting started under five minutes | Consumer path and owner unknown | Unavailable | N/A |
| API naming and examples | Copyable local and opt-out example missing | Unavailable | N/A |
| Error messages actionable | Token failure/retry needs linked path | Unavailable | N/A |
| Docs findable and complete | Loading guide required | Unavailable | N/A |
| Upgrade path safe | No new concern; verify asset pair | Unavailable | N/A |
| Dev environment friction | Source/feed dependency remains | Unavailable | N/A |

DX pass scorecard (before → plan after accepted obligations; scores assess the plan, not shipped behavior):

| Pass | Score | Concrete assessment |
|---|---:|---|
| 1. Getting started | 5 → 8 | Existing quickstart helps, but no loading path yet. A three-step guide and sample target the first useful result; cold setup remains separate. |
| 2. API/SDK | 6 → 9 | `Loading.Enabled`, `ShowFallbackBar`, `PreventDuplicateSubmissions` and per-form flags have clear defaults; examples must show their precedence and `hidden` styling. |
| 3. Errors/debugging | 5 → 8 | Token failure, missing marker/stale asset, and silent fallback-off need problem/cause/fix/docs trails; preserve existing form-failure diagnostics. |
| 4. Documentation | 5 → 9 | The new loading guide will sit beside form failures, with working markup, option reference, examples and pitfalls linked from README. |
| 5. Upgrade | 6 → 8 | Package and generated asset must move together; use existing pack guard and document fallback-off/rollback. No breaking migration is proposed. |
| 6. Developer environment | 6 → 8 | MVC sample, TypeScript checks and asset verifier already exist; add a strict-CSP browser proof for the bar and source/output guidance. |
| 7. Community/ecosystem | 7 → 7 | Existing repository examples and issue #827 are the support path; a new community program is outside this feature. |
| 8. Measurement | 5 → 8 | Record the complete warm-app human clock and consumer click-to-visible timing; no telemetry service or release gate is needed. |

Overall DX plan score: 5/10 → 8.1/10 (mean of eight passes). TTHW: **unmeasured → target under five minutes** for the defined warm-app path; a future `/devex-review` must time a real developer. The community pass stays 7 because no new ecosystem work is justified by this scoped feature. The highest adoption risk is still the unknown affected consumer route/access path; it is the first implementation checkpoint and a completion dependency.

Three error paths and expected developer guidance:

| Path | Current evidence | Required developer response |
|---|---|---|
| Lazy token refresh fails | Existing runtime message says it could not prepare the form, marks antiforgery failed and can emit a development diagnostic when failed-form UX is on | Explain token endpoint/network cause, no form POST, retry after connection recovers, and link [antiforgery](../../Web/ForgeTrust.RazorWire/Docs/antiforgery.md); host owns visible error when failure UX is off. |
| Marker or asset missing | `rw-active`/Tag Helper and `<rw:scripts />` are prerequisites; generated asset verifier exists | Inspect rendered loading marker and script version, rebuild/verify asset, and link [runtime contract](../../Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md). |
| Fallback suppressed with no indicator | `ShowFallbackBar=false` intentionally produces no package visual | Restore fallback or add a local/site-wide indicator; show this exact combination in troubleshooting. |

**DX implementation checklist:** [ ] default fallback appears on a managed form without app markup; [ ] full copyable local example works; [ ] target warm-app adoption time is measured; [ ] token failure sends no POST and guide explains retry; [ ] missing marker and stale asset have a diagnostic path; [ ] every option/attribute/default/precedence is documented; [ ] generated assets and manifest verify together; [ ] strict CSP preserves bar visibility; [ ] README and form-failure guide link to loading; [ ] real affected form is identified and exercised; [ ] rollback switch and package version pairing are documented. The feature has no new CLI, credential, pricing or hosted service requirement; those generic checklist items are not applicable.

**NOT in scope:** a hosted playground, new public request API, package telemetry dashboard, new community channel, or a public NuGet claim before publishing. **What already exists:** README quickstart, brochure starter, MVC source sample, form-failure diagnostics, `RWASSET`/`RWPACK` verification and runtime-contract guide. A new TODO was not opened because every accepted DX fix is direct feature work; fresh-install public package onboarding remains tied to the existing publishing path.

#### DX decision audit additions

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 11 | DX | Define a warm-app adoption clock and measure it | Mechanical | Explicit over clever | A public feed is unavailable, so a claimed cold five-minute path would be misleading. | Unbounded “easy to adopt” claim |
| 12 | DX | Ship complete local/fallback/opt-out examples | Mechanical | Choose completeness | The public attributes need a copyable path to the first visible result. | Reference-only docs |
| 13 | DX | Trace token, marker and silent-config failures | Mechanical | Fight uncertainty | Each is a concrete failure a form author can encounter. | Generic troubleshooting prose |
| 14 | DX | Preserve CSP-compatible fallback styling | Mechanical | Reuse package conventions | A blocked style would make the default failsafe invisible in strict CSP hosts. | Relying on a nonce-less injected style |

#### DX implementation tasks

- [ ] **X1 (P1, human: ~2h / CC: ~30min)** — Consumer proof — Identify the app route, owner and access path; record warm adoption and delayed-form timing before final package sizing. Verify frame target and antiforgery path.
- [ ] **X2 (P1, human: ~3h / CC: ~45min)** — Guide and sample — Add a three-step loading quickstart with full markup, CSS, options and opt-outs, linked from package README; run it in MVC.
- [ ] **X3 (P1, human: ~3h / CC: ~45min)** — CSP fallback — Package styling through a CSP-safe path and prove the default bar remains visible with strict `style-src`.
- [ ] **X4 (P2, human: ~2h / CC: ~30min)** — Troubleshooting — Give problem/cause/fix/docs trails for token failure, missing marker/asset and silent opt-out, with real behavior verification.

### Engineering review: architecture, code paths, and verification

**Step 0 scope challenge.** Keep the approved form-first scope. The real consumer route remains unidentified, so the first work package is evidence gathering rather than a claim that #827 is already fixed. Existing Turbo 8.0.23 owns browser validation, confirmation, busy semantics, the initiating submitter and a delayed page bar. Existing RazorWire owns `rw-active` Tag Helper output, lazy antiforgery, failure UX, generated assets, the public manifest and MVC Playwright tests. The missing behavior is accepted-request feedback before token preparation and cold-server wait, with exact cleanup. Reuse these paths; no new endpoint, database, package dependency, generic request API or navigation product is warranted.

The plan touches more than eight files because its options, Tag Helpers, TypeScript source, generated JS/CSS, manifest, tests, guide and MVC example ship as one package contract. A smaller runtime arrangement preserves all approved behavior: one private coordinator beside `FormFailureManager`, one CSP-safe stylesheet, and the existing server/asset pipeline. No standalone navigation service, event bus or public manager is needed. This is the autoplan structure choice; the approved feature list is unchanged. The first installed-Turbo browser test remains a feasibility checkpoint, not a separate release gate. Official [Turbo events](https://turbo.hotwired.dev/reference/events) describe the interception/resume and form events, while [AbortSignal.abort()](https://developer.mozilla.org/en-US/docs/Web/API/AbortSignal/abort_static) supplies an already-aborted fetch signal; the packaged 8.0.23 source determines the actual event order and cleanup behavior.

**Eng dual voices.** The native `combo/sub` reviewer completed against snapshot `417841ea6d77b29e023a4402eb7c6d3830f3ccae5f589ae5572e72854b93dec9`, reporting three P1 and three P2 concerns. Codex CLI outside review is unavailable in this Codex host, so there is no independent outside-provider consensus.

| Dimension | Native reviewer | Outside Codex | Consensus |
|---|---|---|---|
| Architecture sound? | Conditional on bounded private state machines | Unavailable | N/A |
| Test coverage sufficient? | Planned breadth good; first token proof essential | Unavailable | N/A |
| Performance risks addressed? | Bound observer/rebinding work | Unavailable | N/A |
| Security threats covered? | No-POST proof is mandatory | Unavailable | N/A |
| Error paths handled? | Prep-error ordering and bar matrix needed | Unavailable | N/A |
| Deployment risk manageable? | Consumer owner/access and built assets must be explicit | Unavailable | N/A |

**Section 1 — architecture.** `RazorWireFormTagHelper` emits a loading marker independently of `data-rw-form`; `RazorWireScriptsTagHelper` serializes defaults and links the CSP-safe asset. The private coordinator observes accepted Turbo request events and owns only loading references, duplicate guard and bar arbitration. `FormFailureManager` alone owns lazy-token pause/resume and explanatory failure UI. Shared state is limited to the attempt identity and terminal signal. A request's fetch-options/signal identity must be correlated with the later `formSubmission`; never settle all attempts on a form merely because one ends. When a form disappears during the paused token request, cancellation must abort and release the continuation as well as clear the visual state. The exact correlation is a pinned-runtime prototype obligation.

```text
RazorWireFormOptions.Loading -> FormTagHelper marker --+-> emitted MVC markup
                              -> ScriptsTagHelper config + stylesheet
Turbo accepted request -> private form-activity coordinator -> owned indicator / fallback
         |                              |                   -> private bar arbiter <- Turbo visit
         +-> FormFailureManager token hold / failure UI
         +-> Turbo submit-end / abort / pagehide -> idempotent attempt settle
MutationObserver (only while pending) -> transactional rebind or settle
```

Nil path: a disabled/non-RazorWire form is ignored and Turbo keeps its native behavior. Empty indicator path: search outward, then default fallback, then intentional no-package-signal if disabled. Error path: token failure must reach one specific preparation message and no POST; other response/network failures use existing failed-form UX and clear loading. No auth boundary changes or new server input are introduced. The duplicate guard is only a browser convenience; server idempotency still protects side effects.

**Bar state table.** “Turbo form bar” means its delayed page-form bar; a Turbo Frame has no equivalent bar in this build. A separate visit is tracked only to preserve navigation feedback. All entries require browser proof against Turbo 8.0.23.

| Form state | No separate visit | Separate visit while form pending | Form ends before visit |
|---|---|---|---|
| App indicator selected | App status visible; Turbo form bar suppressed | App status plus Turbo visit bar when Turbo shows it | App status clears; package fallback appears synchronously for the live visit until it settles |
| Package fallback selected | Fallback visible; Turbo form bar suppressed | Fallback remains the shared top signal; Turbo bar suppressed | Retain fallback until the visit settles; no gap |
| Fallback off, no app indicator | No package visual; native delayed Turbo page-form bar remains possible | Native Turbo visit/form behavior untouched | Native Turbo visit behavior remains |
| Global loading disabled or per-form off | Native Turbo behavior untouched | Native Turbo behavior untouched | Native Turbo behavior untouched |

When mixed forms are pending, fallback wins the top-bar arbitration if any attempt requires fallback; app indicators remain independently visible. Visit cancellation/failure/reload releases visit ownership. `turbo:before-render` is not a terminal event if prevented, so cleanup uses proven terminal/departure paths.

**Section 2 — code quality.** `FormFailureManager` already mixes token, failure UI and dynamically injected CSS; adding loading references inside it would make cleanup and opt-out semantics harder to reason about. Keep one private loading coordinator and one `settle(attempt)` path. Reuse a single boundary resolver for initial selection and rebinding, and a single effective-lock parser for global/per-form precedence. Maintain a map of active attempts, per-node reference counts, and exact prior attribute/hidden values at zero-to-one transitions. Rebinding is release → restore old node only at zero → resolve → acquire, with idempotent settlement between steps. Watch only while work is pending and inspect affected attempts. Keep generated output generated; do not patch the bundled Turbo file. A dynamic nonce-less `<style>` already exists for failure UX, so the new fallback must not copy that pattern under strict CSP.

**Section 3 — test review.** Read authored runtime, bundled Turbo, Tag Helpers and their tests, Node form-failure tests, MVC Playwright tests, and the asset pipeline. Current Node tests prove token handler behavior with a fake event; they do not run Turbo's held continuation. Current MVC tests cover failures and navigation but no loading lifecycle. A separate engineering test plan was an implementation artifact, not a passing-test report.

```text
Options/Tag Helpers -> C# defaults, marker independence, opt-outs, emitted config/CSS
Accepted event -> Playwright invalid/confirm/next-paint/5s/10s/frame/page
Token hold -> Playwright aborted-no-POST/prep-error/retry; Node identity/settle branches
Indicators -> Node resolver/count/restore + Playwright nested/shared/replacement/accessibility
Duplicate guard -> Playwright rapid clicks/Enter/external submitter/requestSubmit/lock-off
Bar arbiter -> Playwright all table states, both completion orders, abort/failure/reload
Distribution -> generated assets/pack, strict CSP, no-JS, MVC and consumer proof
```

Every listed new path gets a test; none is deferred for token cancellation, duplicate prevention or cleanup. Use deterministic held responses and event assertions rather than sleep-based success checks; a deliberate 600 ms check crosses Turbo's 500 ms native bar delay. Screen-reader announcement remains a recorded manual MVC check alongside accessibility-tree assertions. No LLM/prompt evaluation applies. The lazy-token Playwright case is a normal integration test, as the user chose, not an extra release gate.

**Section 4 — performance.** No query, index, server cache, worker or connection pool changes. Memory should be proportional to live attempts and selected nodes, with references released on settlement/page departure. Ancestor traversal happens on attempt start or affected-node replacement; a document-wide mutation scan on every DOM edit would be wasteful and risks repeated work at 100 pending forms. Test two shared forms and out-of-order unlocked attempts; optionally time next-paint on the MVC sample to catch work that delays status visibility. CSS/style asset adds one versioned package resource; verify its load under path base and CSP.

**Failure modes registry.** These are planned rescues and proofs; implementation has not run.

| Failure | Rescue / user result | Proof | Critical gap now? |
|---|---|---|---|
| Token request rejects while Turbo is paused | Abort fetch options, resume once, settle, preserve specific prep error, allow retry; host explains error when failure UX off | First real-browser held-token/no-POST/retry test | No written-plan gap; high-risk feasibility proof pending |
| Form removed during token hold | Abort/resume paused continuation, settle once; no stale signal | Frame/form replacement Playwright | No |
| Shared indicator disconnected or two requests finish out of order | Transactional rebind/count restore; fallback if needed | Node and browser shared-form tests | No |
| Separate visit ends before/after form, or reloads | State machine releases only its owner; signal remains for live work | Visit overlap matrix in browser | No |
| CSP blocks inline styles | Packaged stylesheet keeps fallback visible | Strict-CSP browser test | No |
| Consumer route unavailable | Package may be ready; issue remains open with named owner/deliverable | Consumer evidence record | No, but completion dependency |

**NOT in scope:** generic navigation/request loading API (no non-form adopter yet), percentage progress (no truthful serverless fraction), package cancel button or arbitrary timeout (could dismiss live work), telemetry dashboard (no operator owner), and public NuGet availability claim (publishing path is separate). Direct `form.submit()` bypasses Turbo and is documented outside duplicate prevention. Third-party listeners that take ownership of RazorWire's paused continuation are outside the supported coordination contract.

**What already exists:** Turbo 8.0.23 validation, confirmation, submitter/busy lifecycle and delayed page bar; RazorWire's form Tag Helper, runtime config, token refresh, failed-form UX, generated-asset verifier/pack guard, MVC sample and Playwright harness. Reuse those surfaces, while keeping loading independent of the failure-only `data-rw-form` meaning.

**Deployment and workstreams.** The consumer baseline and Turbo token prototype are sequential prerequisites. C# marker/config and authored runtime can then be developed in separate worktrees only with an agreed marker/config interface; CSS/docs/sample can follow the runtime contract, and generated output plus package proof must be integrated once. Dependency order: consumer baseline → token proof → server/runtime implementation → generated assets and MVC tests → real consumer proof. The shared RazorWire module and generated outputs are conflict flags; merge those sequentially. Rollback uses `Loading.Enabled=false` or the prior matching package/asset pair. No migration is required.

**TODOS.md disposition.** Reviewed the existing deferred-work ledger. The previously approved navigation/general-request idea is already explicitly deferred in the design and no new independent backlog item was accepted; no `TODOS.md` entry is added. All six engineering findings are obligations of this feature, not deferred cleanup.

#### Eng decision audit additions

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 15 | Eng | Keep approved scope in a smaller runtime arrangement | Mechanical | Explicit over clever | Broad file count comes from distribution, not a need for new public layers | Generic manager/service |
| 16 | Eng | Own pause/resume in antiforgery handler and correlate attempts | Mechanical | Choose completeness | Prevent unprotected POST and wrong settlement with lock off | Form-wide boolean |
| 17 | Eng | Preserve preparation error through Turbo's aborted cleanup | Mechanical | Fight uncertainty | Installed event order otherwise risks erasing the useful failure message | Generic network error replacing prep cause |
| 18 | Eng | Specify bar states including fallback-off/native Turbo | Mechanical | Explicit over clever | Silent package configuration must have predictable native behavior | Blanket Turbo-bar suppression |
| 19 | Eng | Transfer connected indicator ownership transactionally | Mechanical | Choose completeness | Shared counts and exact restoration require ordered release/acquire | Re-scan and overwrite |
| 20 | Eng | Separate package-ready from issue-complete consumer proof | Mechanical | Honor approved outcome | Unknown consumer access cannot be hidden in a package-only claim | Closing #827 on library tests alone |

#### Eng implementation tasks

- [ ] **E1 (P1, human: ~4h / CC: ~1h)** — Token continuation — Build the first Turbo 8.0.23 Playwright held-token/no-POST/retry test; implement abort-before-resume and preserve the preparation error through later Turbo events. Surfaced by native P1 and installed runtime ordering. Files: `assets/src/razorwire.ts`, MVC integration tests. Verify one failed attempt sends zero POSTs and retry sends one.
- [ ] **E2 (P1, human: ~4h / CC: ~1h)** — Bar arbitration — Implement and browser-test the state table, including fallback-off/native behavior and canceled/failed/reload visits. Surfaced by native P1/P2. Files: authored runtime, packaged stylesheet, MVC integration tests. Verify no duplicate or missing signal.
- [ ] **E3 (P1, human: ~2h / CC: ~40min)** — Consumer evidence — Record route, access, owner, frame target, token path and baseline/final timing; keep #827 open without real proof. Surfaced by native P1. Files: consumer app and delivery record to be identified. Verify controlled-delay form and second fallback form.
- [ ] **E4 (P2, human: ~3h / CC: ~45min)** — State restoration — Use attempt identity and transactional rebind with shared counts; test reverse completion and removal. Surfaced by native P2. Files: authored runtime, Node/browser tests. Verify exact prior DOM state.
- [ ] **E5 (P2, human: ~2h / CC: ~30min)** — Artifact boundaries — Verify option/marker independence, emitted CSS/script assets, strict CSP and no-JavaScript forms. Surfaced by native P2. Files: Tag Helper/options tests, generated-asset tests, MVC integration tests. Verify `RWPACK001` and browser behavior.

**Eng completion summary.** Step 0: scope accepted as-is, smaller runtime arrangement chosen. Architecture: 3 concerns (token correlation, bar ownership, consumer completion). Code quality: 2 concerns (avoid expanding `FormFailureManager`, transactional reference state). Test review: diagram produced; 14 branch families and 5 high-priority proof clusters in the linked artifact. Performance: 1 bounded-observer concern. NOT in scope and What already exists: written. TODOS.md updates: 0. Failure modes: 0 unhandled silent written-plan gaps, with token/no-POST prototype and consumer proof still outstanding. Unresolved design choices: 0; implementation evidence dependencies: 2. Outside voice: Codex CLI unavailable under Codex harness. Parallelization: two potential lanes after sequential baseline/prototype, integrated sequentially. Lake Score: N/A (no user coverage choices in this review). Status: issues_open until the planned proofs run; this is plan review, not implementation or passing QA.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope and strategy | 1 this run | Complete | Consumer evidence and lazy-token proof moved first. |
| Outside Review | Codex CLI | Independent second opinion | 0 this run | Unavailable | Codex harness mismatch; no outside voice. |
| Eng Review | `/plan-eng-review` | Architecture and tests | 1 this run | Complete, proofs pending | Six native concerns mapped to obligations; token continuation and bar matrix first. |
| Design Review | `/plan-design-review` | Form interaction and generated UI | 1 this run | Complete | 6/10 → 9/10; five mechanical decisions. |
| DX Review | `/plan-devex-review` | Adoption and API usability | 1 this run | Complete | 5/10 → 8.1/10; four mechanical decisions. |

**OUTSIDE COVERAGE:** Codex outside CLI unavailable for CEO, design, DX and Eng under this Codex harness; native reviewer findings are not cross-model consensus.

**VERDICT:** APPROVED as-is by the user (option A). All four autoplan review phases completed. Turbo feasibility and the actual consumer form remain implementation proof obligations; this approval does not claim that the feature has shipped.

NO UNRESOLVED DECISIONS
