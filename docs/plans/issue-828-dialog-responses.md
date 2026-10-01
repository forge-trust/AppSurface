<!-- Historical local /autoplan restore artifact: main-autoplan-restore-20260930-issue828.md -->
## Implementation plan
# Plan: Server-selected RazorWire dialog responses (#828)

Source: [approved design](../designs/issue-828-server-selected-dialogs.md) and [issue #828](https://github.com/forge-trust/AppSurface/issues/828). The design's latest protocol revision is approved by the user but its independent review is recorded as unreviewed.

**Status: APPROVED as-is (2026-09-30).** The final `/autoplan` decision retains the approved outside-response opener-order rule and immediate Close/Escape/replacement behavior. The two response-order races below are accepted limitations of this plan; neither proposed freshness barrier nor a dirty-form guard is authorized. The Turbo hook and DOM-order proof remains the first implementation gate.

## Goal and constraints

A Save or Check status response can explicitly present a titled modal dialog even when the existing response target is far from the initiating control. The body may contain an app-owned form. RazorWire supplies one accessible shell, handles focus and lifecycle, and leaves ordinary responses inline. Server-initiated push dialogs and stacked dialogs are outside this change. The same endpoints return usable HTML pages without JavaScript.

The builder owns one mutable dialog slot per response. `OpenDialog*` sets it to open, `ReplaceDialog*` overwrites its pending payload, and `CloseDialog()` sets it to close. The final result emits at most one dialog stream action. `HasActiveDialog` reports whether the builder's pending final state is open. A second plain open while an open is pending fails immediately. Close-then-open yields one final open; open-then-close yields one final close. Overwritten partials or components are not rendered. Ordinary page actions retain their relative order and the dialog slot retains the position of the first dialog operation.

## Implementation sequence

1. **Prove Turbo's request and render hooks.** With the bundled Turbo 8.0.23 runtime, verify `turbo:before-fetch-request` can attach a unique request token and tab-local monotonic start order to supported stream GET links and forms. Only a request originating inside the live shell also carries its origin flow token; an outside request has no origin flow token. Verify response stream actions can be filtered before DOM mutation for `422` and success responses. Exercise out-of-order requests and a page action after the final dialog slot. Capture the exact event objects and any limitations before choosing the runtime interception path. If hooks cannot preserve the approved stale-response behavior, amend the protocol and bring the affected decision back for review before implementing it.
2. **Build the server API and serialization.** Extend `RazorWireStreamBuilder` with the approved `OpenDialog*`, `ReplaceDialog*`, `CloseDialog()`, and read-only `HasActiveDialog` surface. Keep one mutable dialog slot, validate titles and invalid sequences at method call time, and snapshot it at `BuildResult()` or `RenderAsync`. `Build()` must reject dialog commands because it has no request context. Extend `RazorWireStreamResult` to read bounded correlation headers, encode metadata on the final dialog action and scoped in-dialog page actions, and preserve output order even when partial rendering runs in parallel. Continue normal antiforgery and handled-form headers. Do not add raw HTML overloads or use the tokens for authorization.
3. **Implement the browser shell and flow gating.** Register the package's dialog action alongside the existing `rw-visit` integration. Create one native `<dialog>` shell on demand, with accessible title, visible Close control, focus management, and same-origin scoped CSS variables for surface, text, border, spacing, size, backdrop, and focus ring. Every accepted final open, whether from an outside or inside response, creates a fresh opaque flow token, binds it to the shell, and tombstones the previous flow; a pending response from that prior flow cannot change the new dialog. `ReplaceDialog*` only changes the unsent builder slot and creates no intermediate browser flow. For an outside response, accept its dialog command only if its request start order is newer than the current or last-dismissed flow opener; a close affects only an eligible current dialog. For an inside response, require its echoed origin flow to match the live flow and, among overlapping submissions within that flow, require the most recently started request. A dismissed or superseded flow must not be resurrected by a late response. Ignore stale dialog commands and dialog-target actions, including a target ID reused in a newer dialog; preserve ordinary page-target actions from the same response. Clear the shell before Turbo caches or navigates away.
4. **Handle forms and fallback.** Support a form in a partial or component: pending state, handled `422` validation inside the dialog that focuses the first invalid field or error summary according to the existing form contract, unhandled network/HTTP fallback using the existing form failure contract, success that remains open unless the server closes it, and dismissal while pending. Coordinate loading feedback with #827 without duplicate indicators. Add status GET and save POST routes to `examples/razorwire-mvc`; each returns Turbo Streams only when accepted, otherwise a usable full HTML page and form. Set `Vary: Accept` for GET negotiation and appropriate private caching for user-specific output.
5. **Document and verify adoption.** Add public API reference and usage guidance to the RazorWire package README and a focused dialog guide; update package-level index, design guidance, XML docs, and MVC example with canonical links. Explain when dialog attention is useful, when inline feedback is better, buffer state versus browser state, flow tokens, stale responses, fallback, forms, caching, and CSP. Package the built script and stylesheet for static and embedded serving. Trial one real existing save or status action whose response target is far from the button, recording route, target, desired body, and whether the result is visible without scrolling.

## Verification gates

- **Server unit tests:** API shape and chaining; title and message encoding; null body; partial/component rendering and missing view failures; `HasActiveDialog` across every transition; repeated close; immediate error on second open or replace without pending open; open/replace/close normalization to one frame; overwritten renderer never invoked; page action order; malformed/missing correlation metadata; injection resistance.
- **Browser tests:** one shell; accepted outside and inside opens replacing the current shell with a fresh flow; final buffered `ReplaceDialog*` without an intermediate dialog or flow; out-of-order outside GET responses with no flow token; inside requests with matching, dismissed, and superseded origin flow tokens; an older in-dialog response arriving after an outside replacement; overlapping in-dialog form submissions; dismissal and replacement while a response is pending; duplicate token; stale dialog-target action with reused ID; page-target action from a stale response; focus on first invalid field or error summary after handled `422`; focus and fallback when trigger is gone; Escape, Close, navigation/cache cleanup; narrow viewport, reduced motion, accessible name, customized CSS variables, and CSP without inline styles.
- **Integration tests:** stream GET status result, POST save with page update and open, dialog form handled `422`, success with explicit close, unhandled failure/retry, and GET/POST no-JavaScript HTML fallback. Include `Vary: Accept` and user-specific cache behavior.
- **Project gates:** build and type-check RazorWire assets; run focused .NET and Playwright suites; verify static/hybrid export and embedded asset fallback; format touched C#; resolve introduced warnings; run `./scripts/coverage-solution.sh` when practical. Record any coverage limitation rather than claiming completion.

## Risks to resolve during implementation

- The approved design has an unverified review disposition. Its older review concerns about multi-command flow transitions are now reduced by the one-slot buffer, but the current revision has not had an independent verdict.
- Turbo 8.0.23 may schedule individual stream element rendering asynchronously even when server markup is ordered. Prove and enforce the ordering required when a page action targets the newly opened dialog.
- Request and flow tokens only control presentation. They do not prevent two server writes from racing. The guide must say when applications need concurrency or idempotency protection.
- The issue's original 24–31 hour estimate predates flow ownership and race testing. Re-estimate after the protocol proof.

<!-- autoplan-accepted:ceo -->
- Preserve explicit server-selected open, replace, and close; `HasActiveDialog` reports only pending builder state. Test second-open failure, replace-without-open failure, repeated close, snapshot immutability, one final dialog frame, and original slot ordering.
- Preserve flow-scoped stale-response protection for outside and inside requests, including concurrent form submits, dismissal, replacement, duplicate/unknown tokens, and dialog targets with reused IDs; page targets outside the dialog remain eligible. Every accepted final open creates a fresh flow and tombstones the prior one, while buffered `ReplaceDialog*` creates no intermediate flow. Prove both completion orders in browser tests and reject an older in-dialog response after outside replacement.
- Assign a unique request token and tab-local start order to each supported stream GET or form request; only inside requests carry the current origin-flow token. Outside dialog actions compare start order with the current or last-dismissed opener; inside actions require the live matching flow and newest submission. Test both paths and stale command/target filtering.
- Preserve encoded plain text title/message, trusted Razor-rendered partial/component body, bounded metadata, and existing antiforgery/handled-form headers. Test malicious attributes/content and malformed metadata; do not treat presentation tokens as authorization or idempotency.
- Preserve one native accessible shell, visible Close, Escape, focus entry/return and disconnected-trigger fallback, narrow viewport, reduced motion, cache/navigation cleanup, and same-origin themeable CSS variables under strict CSP. Test custom variables, keyboard, and browser lifecycle paths.
- Preserve dialog forms with handled 422 validation focusing the first invalid field or error summary, unhandled failure fallback, explicit server close on success, and no duplicate loading or failure UI. Test pending, error, retry, success, and dismissal-while-pending states.
- Preserve GET status and POST save examples with full HTML GET/POST fallbacks, `Vary: Accept`, private/no-store user-specific caching, and one real adoption proof whose result is visible without scrolling.
- Prove Turbo 8.0.23 request headers and pre-render hooks and action ordering before implementation. If the installed runtime cannot enforce flow guarantees, return to the user-approved protocol decision instead of silently weakening it.
- Document external and internal API shape, defaults, constraints, when dialogs are useful, inline alternatives, request/flow token limits, fallback, forms, caching, CSP, and adoption links. Build/package assets; run focused server/browser/integration tests, formatting, warnings, and practical coverage.
- Before implementing the public API, identify one existing distant-result route, compare inline relocation versus explicit dialog attention, and record whether users notice and can act on the result without undue interruption. Then run a go/no-go proof of bundled Turbo 8.0.23 hooks and both response orders; retain approved feature scope unless the user changes it.
- For dialog-containing results, validate bounded correlation metadata and render the complete ordered action sequence before committing headers or bytes; preserve page-only streaming behavior. Test a missing partial after an earlier page action and assert no partial dialog response is applied.
- Expose target/flow metadata through a narrow internal action seam rather than parsing serialized HTML. Keep browser token/tombstone tracking bounded while rejecting duplicate, stale, and reused-target actions; verify long-lived-tab behavior.
- Document the exact bundled Turbo version proof and compatibility expectations for custom/host-managed runtimes. In the save example, determine whether overlapping writes are possible and use or document the application's concurrency/idempotency rule; presentation tokens do not order stored writes.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:design -->
- Preserve the approved native one-shell UX and low-fidelity interaction sketch. Make the package-owned header (title and visible Close) persistent and discoverable above a scrollable host-owned body; title + Close is a valid minimal dialog when the plain message is null. Do not add an app-styled footer or restyle host form controls.
- Specify modest responsive defaults (roughly 32rem desktop maximum width, 2rem viewport gutters, bounded dynamic viewport height), long-content scrolling, 320px/375px and 200% zoom behavior, visible focus, 44px package-owned touch targets, and default package-owned contrast checks. Respect host overrides and reduced motion.
- Specify exact focus: first meaningful field or heading/content on open; first invalid field or summary on handled validation with scroll into view; connected trigger or safe page landmark after close; no focus return between accepted cross-response replacement. Escape and Close dismiss immediately; backdrop does not.
- Document and test visible trigger waiting and failure before an outside open, retained in-dialog values and pending state, handled 422, local unhandled failure/retry, server-owned success close, null-body dialog, stale response with no shell flash, and eligible page updates. Coordinate with #827 and the existing form-failure contract without duplicate generated UI.
- Document the dialog-use criterion: prefer an explicit dialog when a result needs attention or follow-up and relocating it inline would still be easy to miss; keep routine acknowledgements and recoverable inline form feedback near their controls. Trial one real distant-result route using that criterion.
- Document that immediate Close/Escape, navigation, or accepted replacement can discard unsaved visible form input while a server mutation may continue. Preserve the approved immediate behavior; a dirty-form guard is an unresolved product choice, not an accepted feature.
<!-- /autoplan-accepted:design -->

<!-- autoplan-accepted:dx -->
- Keep DX POLISH for the primary MVC developer persona. Provide a short source-backed path to the first visible server-selected dialog: exact .NET 10 prerequisite, sample run command, documented route, initiating GET link, server stream/HTML branches, app partial/form, expected visible result, and retry/422/close behavior. Target under five minutes from a prepared app; measure and separately report a clean checkout without claiming an unobserved time.
- Preserve and document every approved public overload by exact name and signature from the #828 design, including all `OpenDialog*` and `ReplaceDialog*` text/partial/generic/named component forms, `CloseDialog()`, `HasActiveDialog`, fluent return, title/null-body constraints, single pending slot, and `BuildResult()`/`RenderAsync` requirement. Include copyable examples for each body type and page-update-plus-open; do not add raw HTML or change fixed interaction defaults.
- Make invalid sequence, absent pending dialog, unscoped `Build()`, missing view, and malformed correlation failures actionable with problem, cause, fix, and canonical guide pointer. Explain intentional stale browser skips and presentation-only token limits. Test error content and recovery path through public APIs.
- Cross-link README, focused dialog guide, package index, XML/API reference, design guidance, and MVC sample. Show GET stream opt-in, handled 422 validation, explicit success close, no-JavaScript GET/POST HTML fallback, cache headers, CSP, bundled Turbo 8.0.23 proof, and host-managed compatibility expectations.
- Record the adoption trial's route, steps, observed first-result time, visibility without scrolling, and whether inline relocation would suffice. Do not add telemetry or an automated release gate. State that focus, Escape, Close, backdrop, and immediate replacement follow the approved fixed shell contract; interaction override is unresolved scope, not accepted DX work.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Preserve the one-slot builder API and one-shell browser contract from the approved #828 design. In dialog-containing results, validate request metadata and fully render ordered actions before committing headers or bytes; keep page-only streaming behavior. Verify shared `ViewContext` safety before any parallel dialog render; otherwise render that path sequentially, with cancellation and bounded concurrency.
- Make the bundled Turbo proof assert a real DOM dependency: a same-response action targeting the dialog body applies only after shell/body insertion. Test both response completion orders, inside/outside provenance, stale target ID reuse, preserved ordinary page actions, and navigation/cache cleanup. If hooks cannot establish the approved behavior, return to the user-approved protocol decision.
- Use structured action target metadata for package-authored actions; document the boundary for opaque custom `IRazorWireStreamAction` results. Keep the dialog shell and form failure ownership separate, coordinating request headers and handled 422 focus with the existing form contract.
- Package dialog styles and runtime through the generated asset, static, and embedded paths; verify strict CSP and host-managed Turbo compatibility. Measure representative concurrent dialog rendering and document that no dialog body size cap is introduced.
- Add stream and ordinary HTML POST tests with missing or invalid antiforgery tokens. Assert rejection before any dialog or page action writes, preserving the existing failure contract. Extend server, browser, MVC integration, and package suites for the accepted behavior; retire no existing tests.
- Do not implement a dismissal-time outside-request watermark (UC828-1), a server-close freshness barrier (UC828-2), or a dirty-form guard without the final user decision. Keep the approved opener-order behavior visible as the current baseline and state its two discovered races.
<!-- /autoplan-accepted:eng -->
## Review record

### CEO Step 0 — scope and premise (2026-09-30)

Mode: **SELECTIVE EXPANSION**, selected by `/autoplan`'s phase override. Review depth: implementation ready. Source authority: the user-approved [#828 design](../designs/issue-828-server-selected-dialogs.md) and the [issue acceptance criteria](https://github.com/forge-trust/AppSurface/issues/828). No change to an approved dialog behavior is authorized by this mode.

**0A, premise:** A save or status response can land outside the user's viewport. The desired outcome is that the server can draw attention to a specific result and keep an optional follow-up form in one accessible shell. The plan addresses that pain directly. The cost of doing nothing is missed results and each host application repeating dialog, focus, and failure wiring.

**0B, reuse:** `RazorWireStreamBuilder` already composes ordered page updates and `rw-visit`; `RazorWireStreamResult` renders MVC partials/components with request context; `razorwire.ts` registers package actions; `FormFailureManager` owns unhandled form fallbacks; `RazorWireScriptsTagHelper` and the asset pipeline package scripts and styles. Reuse these rather than adding a parallel response family. The result's parallel render enumerator may preserve output order, but Turbo's per-element asynchronous render still needs a browser proof for dialog-target actions after an open slot.

**0C, trajectory:** Current: distant inline result and application-authored attention UI → this plan: explicit one-shell server response, usable forms, fallback, and flow ownership → 12-month ideal: a predictable server-composed interaction system with documented response attention patterns and measured adoption. The plan advances that direction without requiring live push dialogs or a generic overlay framework.

**Scope scan:** Standard shell theme tokens, focus return, handled validation focus, and an MVC adoption example are already in the approved design, so they are duplicates rather than additions. Server-pushed dialogs are deferred as the approved design says; prefetching dialog content and automatically opening dialogs for form responses are skipped because they lack a current use case and would change behavior. #827 owns click-to-response loading feedback. No new expansion was accepted, and no approved #828 work was cut. The smallest correct work still includes flow gating, HTML fallback, browser order proof, forms, accessibility, docs, and tests.

**Timeline:** Hour 1 proves bundled Turbo request headers, stream hooks, and action ordering with controlled out-of-order responses. Hours 2–3 define the action metadata seam, one-slot snapshot, and client flow state. Hours 4–5 integrate shell, forms, and MVC HTML fallback. Hour 6 onward verifies races, focus, packaging, docs, and one real consumer flow. Re-estimate after the proof; the issue's 24–31 hours predates flow ownership. Human team: roughly L; CC + gstack: roughly M, contingent on the Turbo hook proof.

**Decision ledger:**

| ID and owner | Contract and evidence | Current | Proposed | Status | Exact approval and scope |
|---|---|---|---|---|---|
| D828-1, user | Explicit server choice; issue criteria 1–3 and approved design | `OpenDialog*` only | No change | approved | User approved design; applies to all response kinds with stream/HTML negotiation |
| D828-2, user | One final buffered frame; latest user clarification and design Public server API | Open, replace, close, `HasActiveDialog` | No change | approved | User approved design; no multi-frame dialog chain |
| D828-3, user | Flow ownership; approved design Flow protocol | Stale dialog commands and in-dialog targets ignored | No change | approved | User chose flow-scoped option B; ordinary page targets remain eligible |
| D828-4, CEO review | Turbo ordering and metadata seam; bundled runtime and builder/result inspection | Unproven browser order and target metadata access | Verify hooks, add explicit action metadata seam if needed | approved as directly required proof | Existing plan's Step 1 and source design require this proof before implementation; no contract change |
| D828-5, CEO review | Adjacent additions | Approved design covers shell/focus/forms/sample | Prefetch, automatic opening, live push | skipped or deferred | `/autoplan` P3/P4; only live push remains a follow-up, no new feature added to #828 |
| UC828-1, spec review | Dismissal race; approved design Flow protocol and spec pass 3 | Outside command compares request order with last flow opener | Also compare against a dismissal-time request watermark | pending user challenge | Reviewer found a concrete late-response schedule; present exact change at final gate before amending approved eligibility |

**Premise challenge:** The product value is server-selected attention, not modal rendering by itself. An app still decides whether a result merits interrupting the user. Documentation must recommend inline feedback for routine acknowledgments and show the dialog for actionable results; the API remains unconstrained as the user requested.

**Taste decisions:** None at Step 0. **User challenges:** UC828-1 is pending after spec pass 3; the original feature direction and single-slot contract remain approved.

**Spec review pass 1:** `combo/sub` reviewed the complete CEO scope summary and exported implementation input. Quality 7/10, overall FAIL: themeability was missing from the plan, validation focus was unspecified, and outside versus inside flow metadata was unclear. All three are already approved requirements in the source design; the implementation plan now names the CSS variables and customization test, first-invalid-field/error-summary focus and test, and request-token versus origin-flow rules with stale-response cases. These are corrections to the written plan, not new scope. The second pass confirmed those three corrections; it found one new gap described below.

**Spec review pass 2:** `combo/sub` read both complete inputs. Quality 8/10, overall FAIL: the plan did not explicitly say that a visible dialog replacement starts a fresh flow and makes the previous flow stale. The approved design already states this for every accepted final open and distinguishes it from unsent `ReplaceDialog*`. The plan and browser tests now spell out both paths. Reviewer confirmation is pending a third and final pass.

**Spec review pass 3:** `combo/sub` read both complete inputs. Quality 8/10, overall FAIL. It confirmed the replacement-flow correction, then identified a remaining race: outside request 51 can start after flow opener 50, the user dismisses 50, and response 51 can still open under the approved opener-order rule. The reviewer proposes a separate dismissal watermark that rejects any dialog command from a request already started when the user dismissed the dialog; a request started after dismissal remains eligible. This would strengthen the approved design's dismissal promise but changes its written outside-response eligibility rule. Record as **UC828-1, pending user challenge at /autoplan final gate**; do not count the proposed watermark as approved behavior. Test both sides of the race if approved. The spec loop stopped at its three-review cap with one unresolved issue.

### CEO dual voices and disposition

The independent `combo/sub` CEO reviewer read implementation SHA `e01ea6229219e62ce10546880d8c3ece8f5c9f15a68e7307112ddadce6e1d570` and raised six concerns. The configured Codex outside pass was unavailable because this task is already running under Codex; no second provider ran. The native review is complete, but cross-model confirmation is unavailable.

| Dimension | Native reviewer | Outside Codex | Consensus |
|---|---|---|---|
| Premises valid? | Visibility pain is clear; modal value should be checked on a real route | unavailable | N/A |
| Right problem? | Keep response visibility primary | unavailable | N/A |
| Scope calibration? | Suggested a narrower slice | unavailable | N/A |
| Alternatives? | Compare inline, relocated target, host-owned dialog | unavailable | N/A |
| Competitive risk? | The value is coordinated behavior, not the open command | unavailable | N/A |
| Six-month trajectory? | Turbo compatibility and maintenance cost need evidence | unavailable | N/A |

**Disposition of native findings:** The user's approved #828 design establishes the modal capability and forms, so the review does not cut them. Move the adoption route selection and inline-versus-dialog baseline to the first gate, then run the Turbo hook proof before committing the public API. Carry forward a concise alternative comparison, a supported-runtime contract, and a save-concurrency check for the sample. Those are proof and documentation steps within the approved scope. The request to hold or narrow the entire feature is a strategic recommendation; without a completed outside voice or a new user instruction it does not replace the approved design. UC828-1 remains the only unresolved protocol decision.

### Section 1 — Architecture Review

The dependency path is `MVC endpoint → RazorWireStreamBuilder → immutable action snapshot → RazorWireStreamResult → Turbo stream HTML → bundled Turbo hooks → RazorWire dialog manager → one native dialog`. The new manager must not parse rendered HTML to discover target ownership; expose an internal target/action metadata seam from the builder's action types and serialize bounded metadata in the result. This keeps routing decisions at the action boundary and prevents stale updates from reaching a reused dialog ID. The shell and flow state are page-local; navigation and `turbo:before-cache` clear them. No database or new network endpoint is introduced.

```text
GET/POST trigger --request/order/optional origin--> MVC controller
        MVC controller --page actions + one dialog slot--> Builder snapshot
        Builder snapshot --rendered, encoded metadata--> StreamResult
        StreamResult --ordered stream frames--> Turbo 8.0.23
        Turbo --before-stream-render gate--> page DOM + RazorWire <dialog>
        No stream Accept --> full HTML page + working form
```

Happy path: valid request metadata, page actions, final dialog frame, visible shell. Nil metadata with a dialog command: reject before writing stream bytes and expose a clear request failure; ordinary page-only streams remain valid. Empty message: valid titled shell with empty body. Render error (missing partial/component): fail before any dialog-containing response bytes are sent so the browser does not apply a partial command sequence. These paths are planned checks, not verified behavior. At 10x usage the shared script and action metadata overhead matter more than server CPU; at 100x concurrent requests the first failure is stale ordering if Turbo hooks differ from the pinned version. Rollback is a package/code revert with no migration, but existing consumers of a published API would need a release note.

State machine: `closed --accepted open--> open(flow F) --accepted open--> open(flow G, F tombstoned) --accepted close/Escape/Close--> closed(tombstone)`. Invalid or duplicated request tokens and stale flow ownership leave state unchanged. `ReplaceDialog*` is solely a builder-buffer transition, never a second browser state transition. The exact outside-response eligibility after a manual dismissal remains UC828-1.

### Section 2 — Error & Rescue Map

| Codepath | Failure | Expected handling and user result | Proof |
|---|---|---|---|
| `OpenDialog*` / `ReplaceDialog*` | blank title, second open, replace without open | `ArgumentException` or `InvalidOperationException` at the call; developer gets actionable error, no response mutation | server unit |
| `Build()` | dialog command lacks request context | `InvalidOperationException` directing to `BuildResult()` or `RenderAsync` | server unit |
| `BuildResult()` / result metadata | missing, oversized, or malformed correlation headers | reject dialog command before output; form failure path stays user visible | server + integration |
| partial/component render | view missing or renderer fails | preserve exception context and avoid a half-written dialog response; user sees normal request failure | server + integration |
| request hook | unsupported event/header shape | fail the protocol proof gate; no silent unscoped dialog action | browser proof |
| stream render gate | stale/duplicate token or old flow | skip only dialog command and dialog target; preserve eligible page update | controlled browser races |
| native `showModal()` / focus | detached shell, missing trigger, unsupported element | report diagnostic, keep page usable, focus safe landmark when possible | browser lifecycle |
| enhanced form request | handled 422, network failure, or unhandled HTTP error | keep validation in shell; use existing form-local failure UI for unhandled errors | integration |

The current `RazorWireStreamResult.ExecuteResultAsync` writes each action while rendering the sequence. For responses containing a dialog command, pre-render and validate the complete ordered sequence before committing headers or bytes; otherwise a later partial failure could leave a successful partial stream in the browser. Preserve normal streaming behavior for page-only results. This is an accepted correctness repair within #828, with a missing-view regression test. Do not catch every exception and continue silently.

### Section 3 — Security & Threat Model

The new inputs are title, plain message, Razor view/component identity, target ID, and request/flow headers. Treat title and message as data; encode content and attributes, reject invalid or oversized metadata, and never use the token for authorization, CSRF protection, or write ordering. The endpoint's existing authorization and antiforgery requirements still govern save operations. Risk: an attacker supplied header reflected into a stream attribute could become script-capable markup if encoding is missed (medium likelihood, high impact); the plan requires malformed and injection tests. No secrets, database, or new privileged endpoint are introduced. The browser shell renders only app-owned Razor markup and package-owned controls; a raw HTML dialog overload remains excluded.

### Section 4 — Data Flow & Interaction Edge Cases

```text
trigger -> Turbo request hook -> bounded headers -> MVC validation/auth
        -> builder snapshot -> Razor render -> stream metadata -> browser gate -> DOM
          nil/wrong header -> reject dialog; empty message -> empty body;
          bad title -> call-time error; renderer failure -> no partial dialog stream;
          duplicate/stale token -> no dialog mutation; page target -> normal render
```

The critical schedule is `A starts → B starts → B opens flow G → A returns`, where A's dialog command is ignored. Reverse completion order allows A to open first and B to replace it; B creates a new flow and A's later in-dialog updates are stale. Inside one flow, older form submission results cannot overwrite newer validation state. Manual dismissal while an outside request is pending is UC828-1; the approved opener-order rule fails that schedule, so no implementation should claim the dismissal guarantee until the challenge is resolved. Double-click, navigation, request timeout, zero-length body, reused target IDs, and detached triggers each have a named browser or integration test.

### Section 5 — Code Quality Review

Reuse `IRazorWireStreamAction` and current partial/component rendering rather than creating a second response stack. The builder's dialog slot should be a small explicit state type with one original list position, not a series of emitted frames to clean up later. Snapshot immutably at result construction; overwritten partials must never render. Expose action target metadata through a narrow internal interface rather than parsing HTML or accessing private members from tests. Keep flow decisions in one browser manager, with helper methods that separate token validation, eligibility, shell mutation, and focus; avoid a single branch-heavy event handler.

### Section 6 — Test Review

```text
builder state/validation -> C# unit + property transition cases
result metadata/render failure -> C# unit + MVC integration
request hook/order/gating -> browser tests with paused responses in both orders
shell/focus/forms -> Playwright keyboard, 422, network failure, retry
HTML fallback/cache/assets -> MVC integration + packaged-consumer checks
```

The Friday-night proof is a real browser test where a form validation response arrives after its dialog was replaced, and it cannot mutate the newer shell even with the same target ID. A hostile QA test submits twice, dismisses, navigates back, then releases both responses. A bounded chaos test permutes only the relevant two-request completion orders, without relying on wall-clock sleeps. The plan already names most cases; add a missing-view atomic-response regression, custom CSS override assertion, and a no-JavaScript POST validation check. Helper-only coverage does not prove the controller and Turbo path.

### Section 7 — Performance Review

There are no new database queries, indexes, caches, or jobs. Dialog-containing responses should be buffered before write, so bound the practical body size to the existing server response budget and test a large partial without unbounded intermediate copies. Browser tombstones and consumed-token tracking must not grow forever on a long-lived tab; prefer bounded state and demonstrate duplicate rejection after pruning. The three likely slow paths are MVC partial rendering, a form POST, and the first dialog asset load; measure them in the example rather than inventing p99 numbers. No new connection pool is added.

### Section 8 — Observability & Debuggability Review

Record which route and result kind emitted an invalid dialog command or failed rendering in server diagnostics, without logging token values or form data. Existing form-failure events and HTTP status reporting remain the first user-visible failure path. Expected stale responses are normal and should not flood logs; development diagnostics should distinguish malformed metadata from a legitimate stale skip. For a bug report, collect request order, whether the request originated inside a dialog, action kind, and eligibility outcome without persisting opaque tokens. This library does not own a production dashboard or alert target; the first adopting app should monitor request errors and failed forms through its existing telemetry.

### Section 9 — Deployment & Rollout Review

No migration or background job is needed. Add assets and server APIs in one package change, then enable them only at routes that call `OpenDialog*`; existing inline responses remain dormant. Verify script ordering with bundled Turbo 8.0.23, static and embedded serving, CSP, and a host-managed/custom Turbo compatibility note before adoption. Smoke test one GET dialog, POST validation and close, and one no-JavaScript fallback in staging. If broken, revert the package and route calls; HTML fallback remains available. The Turbo proof is a go/no-go gate before the public API is considered ready.

### Section 10 — Long-Term Trajectory Review

The one-slot builder and one shell keep the contract explainable. Public API, event semantics, and docs are durable maintenance obligations even if the first release is pre-v0.1; reversibility is 4/5 before public publication and lower afterward. The 12-month risk is coupling to undocumented Turbo implementation details; use public hooks and a pinned bundled-version proof, and document custom runtime requirements. Server-pushed dialogs stay in [TODOS.md](../../TODOS.md) until a separate ownership/replay design exists. A new engineer should find the public guide, protocol constraints, and example from the package index without tracing runtime source.

### Section 11 — Design & UX Review

The user presses Save or Check status, sees normal pending feedback, then an app-authored body in a package-owned titled shell only when the server opts in. For a form, handled 422 keeps the shell open and moves focus to an invalid field or error summary; unhandled failure stays local, and success closes only on an explicit server command. Empty body still has a meaningful heading and Close. The shell fits a narrow viewport, preserves visible focus, supports Escape, and does not auto-dismiss on backdrop click. The interaction sketch is specific enough for structural review; a rendered design audit is still needed after implementation.

| State | Loading | Empty | Error | Success | Partial/stale |
|---|---|---|---|---|---|
| Status dialog | trigger feedback from #827 | title and empty body | existing form/request failure path | title, body, Close | stale open ignored |
| Form dialog | retain submitted form | validation summary fallback | handled 422 or local failure | page update; explicit close or remain | old flow cannot change new shell |

```text
Page trigger -> pending -> server-selected open -> dialog body
                                  form submit -> 422 errors -> focus invalid field
                                              -> success -> server close -> page focus
                  Escape/Close -> tombstone -> page focus
                  navigation/cache -> clear shell
```

### CEO required outputs

**NOT in scope:** Server-pushed dialogs are deferred to [TODOS.md](../../TODOS.md); stacked dialogs, implicit opening, prefetch, drag/resize, and backdrop dismissal are excluded by the approved design or lack of use case. **What already exists:** builder page actions, `rw-visit`, MVC partial/component rendering, form failure UI, Turbo 8.0.23, and package asset delivery all have reusable paths in `Web/ForgeTrust.RazorWire`. **Dream state delta:** the package gains an explicit attention primitive, while app-specific decision of when to interrupt, data-write concurrency, and live-push ownership remain with their proper owners.

| Failure mode | Rescued? | Planned test | User sees | Logged/diagnosable |
|---|---|---|---|---|
| invalid title or dialog transition | fail early | unit | request error; no partial dialog | exception context |
| missing/invalid correlation | fail early | unit/integration | normal failure path | safe diagnostic |
| missing partial after earlier page action | buffer before write | integration | request failure, no half-update | exception context |
| stale inside/outside command | skip dialog target only | browser race | current dialog unchanged | development diagnostic |
| handled 422 / unhandled failure | keep shell / fallback | Playwright | errors in dialog | existing form telemetry |
| manual dismissal with pending outside open | **GAP, UC828-1** | pending challenge | may reopen unexpectedly | not settled |

Diagrams above cover architecture, data flow, state machine, and user flow. Deployment sequence is `hook proof → server/client/assets → package test → sample/adoption trial → publish`; rollback is `disable route use → revert package → restore previous asset version`, with no schema step. No existing ASCII diagram in the touched files has been found stale from this plan review.

### CEO implementation tasks

- [ ] **T1 (P1, human: ~3h / CC: ~45min)** — protocol — Select a real distant-result route, record its current visibility and interruption risk, then prove Turbo 8.0.23 hooks and both response orders before public API implementation. Files: `docs/plans/issue-828-dialog-responses.md`, browser test to be determined. Verify: controlled browser proof and documented go/no-go result.
- [ ] **T2 (P1, human: ~5h / CC: ~1h)** — server — Implement the single-slot immutable snapshot, target metadata seam, bounded correlation validation, and prebuffered dialog response rendering. Files: `Web/ForgeTrust.RazorWire/Bridge/RazorWireStreamBuilder.cs`, `Web/ForgeTrust.RazorWire/Bridge/RazorWireStreamResult.cs`, new internal action type to be determined. Verify: builder/result tests including missing view and one final frame.
- [ ] **T3 (P1, human: ~8h / CC: ~2h)** — browser — Add one shell, flow state, stale filtering, focus, and cache cleanup for accepted protocol behavior. Files: `Web/ForgeTrust.RazorWire/assets/src/razorwire.ts`, dialog CSS and tests to be determined. Verify: Playwright request-order, form, keyboard, and CSP cases; UC828-1 must be resolved before the dismissal guarantee can pass.
- [ ] **T4 (P1, human: ~5h / CC: ~1h)** — adoption — Add GET and POST sample with full HTML fallback, check save concurrency, and trial a real distant-result flow. Files: `examples/razorwire-mvc/`, integration tests to be determined. Verify: stream and no-JavaScript cases, user-specific caching, and result visibility without scrolling.
- [ ] **T5 (P2, human: ~3h / CC: ~40min)** — documentation — Publish API, protocol, compatibility, and usage guidance with canonical links. Files: `Web/ForgeTrust.RazorWire/README.md`, `Web/ForgeTrust.RazorWire/DESIGN.md`, `Web/ForgeTrust.RazorWire/Docs/dialog-responses.md`, `packages/README.md`. Verify: docs links and API signatures against build output.

**CEO Completion Summary:** SELECTIVE EXPANSION; 0 additions accepted, 1 deferred; 11 review sections evaluated; native CEO review complete with 6 findings, outside Codex unavailable, consensus N/A; spec loop 3 passes, final 8/10 with 1 unresolved dismissal race; 1 user challenge and 1 critical UX state gap; no feature code changed. The plan is reviewable but not implementation-cleared until UC828-1 is decided and the Turbo hook proof passes.

### NOT in scope

The approved design excludes stacked dialogs, implicit form-response opening, drag/resize, backdrop dismissal, and live-push commands. The live-push follow-up is recorded in [TODOS.md](../../TODOS.md); prefetch has no current adopter evidence. These exclusions do not weaken fallback, form support, or stale-response protection for request-owned dialogs.

### What already exists

`RazorWireStreamBuilder` provides ordered page actions and `rw-visit`; `RazorWireStreamResult` has MVC request context and parallel rendering; `razorwire.ts` registers the existing custom action; `FormFailureManager` handles unhandled form failures; and the package serves bundled Turbo and first-party assets. The new work extends those seams and reuses the current form contract.

### Dream state delta

The plan gives RazorWire one explicit attention mechanism for server responses. It does not make the framework decide which result merits interruption, order application writes, or own server-pushed notifications; those boundaries preserve an understandable 12-month API.

### Error, deployment, and rollback diagrams

```text
request -> metadata valid? --no--> reject dialog result before write -> form/request failure UI
                      yes -> render all dialog actions? --no--> exception/HTTP failure, no partial stream
                                                    yes -> Turbo pre-render gate
                                                            stale -> skip dialog targets, keep page targets
                                                            current -> shell/page update

Turbo hook proof -> package build -> server/browser tests -> sample + HTML fallback
                 -> custom-runtime compatibility note -> adopter trial -> release

bad release? -> disable route OpenDialog calls -> use full HTML/inline result
             -> revert package assets/API change -> rerun smoke tests
             -> no migration or stored dialog state to roll back
```

### Completion Summary

```text
+====================================================================+
|            MEGA PLAN REVIEW — COMPLETION SUMMARY                   |
+====================================================================+
| Mode selected        | SELECTIVE EXPANSION                        |
| System Audit         | Reuse builder/result/runtime/form seams    |
| Step 0               | Approved #828 scope kept; UC828-1 pending  |
| Section 1  (Arch)    | 3 issues found                             |
| Section 2  (Errors)  | 8 error paths mapped, 1 GAP                |
| Section 3  (Security)| 1 issue found, 1 high impact               |
| Section 4  (Data/UX) | 8 edge cases mapped, 1 unhandled          |
| Section 5  (Quality) | 3 issues found                             |
| Section 6  (Tests)   | Diagram produced, 3 gaps addressed in plan |
| Section 7  (Perf)    | 2 issues found                             |
| Section 8  (Observ)  | 1 gap found                                |
| Section 9  (Deploy)  | 1 compatibility risk flagged              |
| Section 10 (Future)  | Reversibility: 4/5, debt items: 2         |
| Section 11 (Design)  | 1 unresolved dismissal UX issue           |
+--------------------------------------------------------------------+
| NOT in scope         | written (6 classes)                        |
| What already exists  | written                                    |
| Dream state delta    | written                                    |
| Error/rescue registry| 8 rows, 1 unresolved GAP                  |
| Failure modes        | 6 total, 1 CRITICAL GAP                    |
| TODOS.md updates     | 1 item (server push)                       |
| Scope proposals      | 6 proposed, 0 accepted                    |
| CEO plan             | written in local gstack state            |
| Outside voice        | Codex unavailable; native completed       |
| Lake Score           | N/A                                       |
| Diagrams produced    | 6 types (arch/data/state/error/deploy/RB) |
| Stale diagrams found | 0 identified                              |
| Unresolved decisions | 1 (UC828-1)                              |
+====================================================================+
```

<!-- AUTONOMOUS DECISION LOG -->
## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|-------|----------|----------------|-----------|-----------|----------|
| 1 | CEO Step 0 | Keep the approved one-slot, flow-scoped dialog design intact | Settled contract | User approval | This is the exact behavior approved for #828 | Unscoped last-response-wins dialog |
| 2 | CEO Step 0 | Prove Turbo stream ordering and expose action target metadata without parsing rendered HTML | Required implementation proof | P1 + P5 | The bundled runtime renders stream elements asynchronously and actions hide targets behind private fields | Assuming wire order alone proves DOM order |
| 3 | CEO Step 0 | Defer live push; skip prefetch and automatic opening | Scope disposition | P3 + P4 | Live push is explicitly deferred in the approved design; the others lack a current use case and automatic opening contradicts opt-in | Adding optional interaction modes to #828 |
| 4 | CEO Sections 1–2 | Pre-render and validate dialog-containing results before output | Correctness repair | P1 | Current `ExecuteResultAsync` can emit earlier frames before a later partial fails | Continuing partial dialog streams |
| 5 | CEO Sections 1–5 | Add an internal action target metadata seam | Correctness repair | P5 | Existing action types hide targets and HTML parsing is fragile | Parsing rendered stream markup |
| 6 | CEO native + Sections 6–9 | Move adoption baseline and Turbo proof before public API implementation | Sequencing | P1 | Validates real visibility pain and event compatibility before durable API work | Building full package before first adopter proof |
| 7 | CEO Sections 7–9 | Bound browser flow bookkeeping and state the sample write-concurrency rule | Correctness and operations | P1 | Long-lived tabs and overlapping saves must not imply false freshness guarantees | Unbounded tombstones or claiming tokens order writes |
| 8 | Design passes 1–6 | Specify a persistent header, scrollable body, exact focus destinations, and responsive checks | Approved UX detail | Accessibility and existing design contract | These make the already-approved one-shell interaction testable without taking ownership of the app's form | Styling host controls or adding a package footer |
| 9 | DX passes 1–8 | Document exact overloads, actionable errors, a copyable MVC journey, and measured first-result time | Adoption detail | Executable developer contract | An opt-in server API needs a reproducible path from controller response to visible dialog and full HTML fallback | Claiming unmeasured onboarding time or adding interaction hooks |
| 10 | Engineering review | Require real DOM-order proof, bounded atomic dialog rendering, packaging verification, and negative antiforgery tests | Implementation and verification | Correctness and security | Ordered markup alone cannot prove Turbo render order, and a later render failure must not leak earlier dialog frames | Relying on stream text order or treating presentation tokens as write/authorization controls |

<!-- autoplan-baseline-edits:ceo {"sourceSha256":"0f9992b609386e572985d141b0b50043e4c0fa8484dd8ad9f7f879b1bd4522c0","replacements":[{"oldText":"1. **Prove Turbo's request and render hooks.** With the bundled Turbo 8.0.23 runtime, verify `turbo:before-fetch-request` can attach a request token and origin flow token to stream GET links and forms; verify response stream actions can be filtered before DOM mutation for `422` and success responses. Exercise out-of-order requests and a page action after the final dialog slot. Capture the exact event objects and any limitations before choosing the runtime interception path. If hooks cannot preserve the approved stale-response behavior, amend the protocol and bring the affected decision back for review before implementing it.","newText":"1. **Prove Turbo's request and render hooks.** With the bundled Turbo 8.0.23 runtime, verify `turbo:before-fetch-request` can attach a unique request token and tab-local monotonic start order to supported stream GET links and forms. Only a request originating inside the live shell also carries its origin flow token; an outside request has no origin flow token. Verify response stream actions can be filtered before DOM mutation for `422` and success responses. Exercise out-of-order requests and a page action after the final dialog slot. Capture the exact event objects and any limitations before choosing the runtime interception path. If hooks cannot preserve the approved stale-response behavior, amend the protocol and bring the affected decision back for review before implementing it."},{"oldText":"3. **Implement the browser shell and flow gating.** Register the package's dialog action alongside the existing `rw-visit` integration. Create one native `<dialog>` shell on demand, with accessible title, visible Close control, focus management, and same-origin scoped CSS. Generate one opaque flow token per accepted final open, carry it on requests originating inside the shell, and ignore stale opens, closes, and dialog-target actions. A dismissed or superseded flow must not be resurrected by a late response. Preserve page-target actions from the same stale response where safe. Clear the shell before Turbo caches or navigates away.\n4. **Handle forms and fallback.** Support a form in a partial or component: pending state, handled `422` validation inside the dialog, unhandled network/HTTP fallback using the existing form failure contract, success that remains open unless the server closes it, and dismissal while pending. Coordinate loading feedback with #827 without duplicate indicators. Add status GET and save POST routes to `examples/razorwire-mvc`; each returns Turbo Streams only when accepted, otherwise a usable full HTML page and form. Set `Vary: Accept` for GET negotiation and appropriate private caching for user-specific output.","newText":"3. **Implement the browser shell and flow gating.** Register the package's dialog action alongside the existing `rw-visit` integration. Create one native `<dialog>` shell on demand, with accessible title, visible Close control, focus management, and same-origin scoped CSS variables for surface, text, border, spacing, size, backdrop, and focus ring. Every accepted final open, whether from an outside or inside response, creates a fresh opaque flow token, binds it to the shell, and tombstones the previous flow; a pending response from that prior flow cannot change the new dialog. `ReplaceDialog*` only changes the unsent builder slot and creates no intermediate browser flow. For an outside response, accept its dialog command only if its request start order is newer than the current or last-dismissed flow opener; a close affects only an eligible current dialog. For an inside response, require its echoed origin flow to match the live flow and, among overlapping submissions within that flow, require the most recently started request. A dismissed or superseded flow must not be resurrected by a late response. Ignore stale dialog commands and dialog-target actions, including a target ID reused in a newer dialog; preserve ordinary page-target actions from the same response. Clear the shell before Turbo caches or navigates away.\n4. **Handle forms and fallback.** Support a form in a partial or component: pending state, handled `422` validation inside the dialog that focuses the first invalid field or error summary according to the existing form contract, unhandled network/HTTP fallback using the existing form failure contract, success that remains open unless the server closes it, and dismissal while pending. Coordinate loading feedback with #827 without duplicate indicators. Add status GET and save POST routes to `examples/razorwire-mvc`; each returns Turbo Streams only when accepted, otherwise a usable full HTML page and form. Set `Vary: Accept` for GET negotiation and appropriate private caching for user-specific output."},{"oldText":"- **Browser tests:** one shell; open and replace across responses; final buffered replacement without an intermediate dialog; out-of-order GET responses; overlapping in-dialog form submissions; dismissal and replacement while a response is pending; duplicate token; stale dialog-target action with reused ID; page-target action from a stale response; focus and fallback when trigger is gone; Escape, Close, navigation/cache cleanup; narrow viewport, reduced motion, accessible name, and CSP without inline styles.","newText":"- **Browser tests:** one shell; accepted outside and inside opens replacing the current shell with a fresh flow; final buffered `ReplaceDialog*` without an intermediate dialog or flow; out-of-order outside GET responses with no flow token; inside requests with matching, dismissed, and superseded origin flow tokens; an older in-dialog response arriving after an outside replacement; overlapping in-dialog form submissions; dismissal and replacement while a response is pending; duplicate token; stale dialog-target action with reused ID; page-target action from a stale response; focus on first invalid field or error summary after handled `422`; focus and fallback when trigger is gone; Escape, Close, navigation/cache cleanup; narrow viewport, reduced motion, accessible name, customized CSS variables, and CSP without inline styles."}]} -->

<!-- autoplan-accepted:ceo -->
- Preserve explicit server-selected open, replace, and close; `HasActiveDialog` reports only pending builder state. Test second-open failure, replace-without-open failure, repeated close, snapshot immutability, one final dialog frame, and original slot ordering.
- Preserve flow-scoped stale-response protection for outside and inside requests, including concurrent form submits, dismissal, replacement, duplicate/unknown tokens, and dialog targets with reused IDs; page targets outside the dialog remain eligible. Every accepted final open creates a fresh flow and tombstones the prior one, while buffered `ReplaceDialog*` creates no intermediate flow. Prove both completion orders in browser tests and reject an older in-dialog response after outside replacement.
- Assign a unique request token and tab-local start order to each supported stream GET or form request; only inside requests carry the current origin-flow token. Outside dialog actions compare start order with the current or last-dismissed opener; inside actions require the live matching flow and newest submission. Test both paths and stale command/target filtering.
- Preserve encoded plain text title/message, trusted Razor-rendered partial/component body, bounded metadata, and existing antiforgery/handled-form headers. Test malicious attributes/content and malformed metadata; do not treat presentation tokens as authorization or idempotency.
- Preserve one native accessible shell, visible Close, Escape, focus entry/return and disconnected-trigger fallback, narrow viewport, reduced motion, cache/navigation cleanup, and same-origin themeable CSS variables under strict CSP. Test custom variables, keyboard, and browser lifecycle paths.
- Preserve dialog forms with handled 422 validation focusing the first invalid field or error summary, unhandled failure fallback, explicit server close on success, and no duplicate loading or failure UI. Test pending, error, retry, success, and dismissal-while-pending states.
- Preserve GET status and POST save examples with full HTML GET/POST fallbacks, `Vary: Accept`, private/no-store user-specific caching, and one real adoption proof whose result is visible without scrolling.
- Prove Turbo 8.0.23 request headers and pre-render hooks and action ordering before implementation. If the installed runtime cannot enforce flow guarantees, return to the user-approved protocol decision instead of silently weakening it.
- Document external and internal API shape, defaults, constraints, when dialogs are useful, inline alternatives, request/flow token limits, fallback, forms, caching, CSP, and adoption links. Build/package assets; run focused server/browser/integration tests, formatting, warnings, and practical coverage.
- Before implementing the public API, identify one existing distant-result route, compare inline relocation versus explicit dialog attention, and record whether users notice and can act on the result without undue interruption. Then run a go/no-go proof of bundled Turbo 8.0.23 hooks and both response orders; retain approved feature scope unless the user changes it.
- For dialog-containing results, validate bounded correlation metadata and render the complete ordered action sequence before committing headers or bytes; preserve page-only streaming behavior. Test a missing partial after an earlier page action and assert no partial dialog response is applied.
- Expose target/flow metadata through a narrow internal action seam rather than parsing serialized HTML. Keep browser token/tombstone tracking bounded while rejecting duplicate, stale, and reused-target actions; verify long-lived-tab behavior.
- Document the exact bundled Turbo version proof and compatibility expectations for custom/host-managed runtimes. In the save example, determine whether overlapping writes are possible and use or document the application's concurrency/idempotency rule; presentation tokens do not order stored writes.
<!-- /autoplan-accepted:ceo -->

### Design review — dialog experience (2026-09-30)

**System audit and Step 0.** UI scope is the RazorWire-owned dialog shell and its focus and lifecycle; the page and form body remain host-owned. [RazorWire's design contract](../../Web/ForgeTrust.RazorWire/DESIGN.md) calls for calm, compact, inheriting generated UI with `data-rw-*` hooks and custom properties. The approved [interaction sketch](https://github.com/forge-trust/AppSurface/blob/f597e7ad2062379720a835826183fa33fa9a3327/docs/designs/issue-828-dialog-wireframe.png) already establishes trigger → dialog form → handled validation. Existing [form failure guidance](../../Web/ForgeTrust.RazorWire/Docs/form-failures.md) supplies form-local recovery and `aria-live` behavior. Initial design completeness: **7/10**. A complete plan specifies the shell's hierarchy and geometry, every visible waiting and failure state, focus destinations, and the use criterion for an interrupting dialog.

**Dual voices.** Outside Codex review: **unavailable** on the Codex host; no second-model consensus is claimed. The native `combo/sub` reviewer read the fixed design input (`c9c84c9f66429ed08fa8842266090ca84dc5d7c70f18af941b191a6e72444467`) and reported five findings: unsaved edits can disappear on replacement; pending and failure feedback is vague; focus and dismissal destinations need detail; shell hierarchy and responsive geometry need defaults; and the adoption trial needs an attention criterion. The first finding preserves a real tradeoff in the approved immediate replacement contract. No guard or confirmation is silently added.

**Visual evidence.** The approved low-fidelity sketch remains the reference for shell ownership and flow. Three generated variants were examined in the local exploration artifact directory `issue-828-dialog-20260930/`; the automated image checks reported pass on all three, but none is adopted. Each over-specifies host page and form styling, introduces card-heavy app layouts or invented tokens, and would imply RazorWire owns more than its design contract allows. They are exploration, not approved mockups. No new aesthetic direction is selected.

**Pass 1 — information architecture, 8/10 → 9/10.** The core order is present in the sketch. Make the package-owned hierarchy explicit:

```text
host page: initiating control and any inline status
  → one modal shell: title + visible Close in a persistent header
    → optional plain message or app-rendered body/form in a scrollable region
      → app-owned field errors and actions, or title + Close for a null body
  → on server close or user dismissal: return focus to trigger or safe page landmark
```

The title or Close must remain discoverable when a long app body scrolls; the package does not invent a footer or reorder app form controls.

**Pass 2 — interaction states, 6/10 → 8/10.** These describe user-visible behavior, not only protocol state:

| Surface | Waiting | Empty | Error | Success | Partial/stale |
|---|---|---|---|---|---|
| Outside status/save trigger | No dialog until a valid server open; use existing trigger-adjacent busy feedback or the #827 form-loading affordance when available | If server elects no dialog, retain the ordinary inline response | Before an open, keep the dialog closed and show a host-owned failure near the trigger in the example | Server-selected title and actionable result appear in the modal | A stale dialog command does not flash a shell; eligible ordinary page updates can still appear |
| Dialog content/form | Keep the current form and values visible with its existing Turbo pending state; no duplicate package spinner | A null message yields an intentionally minimal title and Close, without a fake empty-state panel | Handled `422` keeps server-rendered errors and submitted values in the dialog; unhandled HTTP/network failure uses the documented form-local fallback with retry | Stay open unless the server sends an eligible close; then show eligible page updates and restore focus | Dismissal or replacement discards the old visible flow; late errors cannot appear in a new dialog |

For a failed outside GET, the package cannot fabricate a server result. The MVC example should show a nearby host-owned error and retry path. For `422`, the app must return the submitted values in the rerendered form. Tests should assert visible text, retained values, absence of duplicate failure/loading UI, and no shell flash for stale responses.

**Pass 3 — journey, 7/10 → 8/10.** The approved three-step storyboard is expanded for recovery:

| Step | User does | User sees/feels | Plan support |
|---|---|---|---|
| 1 | Presses Check status or Save | Response is pending near the trigger, without a premature modal | Existing host/#827 waiting feedback |
| 2 | Receives actionable server result | One clearly named dialog brings the result into view | Server-selected open, accessible heading, initial focus |
| 3 | Enters or corrects form values | Values stay visible; validation is local and focused | Handled `422` and form-local fallback |
| 4 | Retries or completes | Either recovers in place or sees explicit server close and page update | Retry and server-owned lifecycle |
| 5 | Dismisses or navigates | Focus returns predictably; a pending server mutation is not canceled | Flow tombstone, focus return, documented write semantics |

At five seconds the result is noticed without hunting down-page; at five minutes a validation failure is recoverable in place; over repeated use, routine acknowledgements remain inline so the interruption earns its cost. A newer outside open or immediate Close/Escape can discard unsaved input; this follows the approved lifecycle and remains a visible product tradeoff for the final gate.

**Pass 4 — specificity / AI-slop risk, 8/10 → 8/10.** Classifier: **OPERATE**. Hard rejection hits in the approved sketch: none. Litmus: product/surface identity **YES** (host page remains visible); visual anchor **YES** (one dialog); scan hierarchy **YES** (heading then content); one job per region **YES**; decorative cards needed **NO**; motion adds value **NO** (do not require animation); works without shadows **YES**. The generated exploratory variants are rejected for invented host chrome, decorative cards, and unsupported token palettes. The package should use restrained native behavior, inherited type/colors, visible focus, and no ornamental motion.

**Pass 5 — design-system alignment, 7/10 → 9/10.** Reuse the canonical `--rw-ui-*` color, type, border, radius, and gap defaults where appropriate; document only a small dialog-specific set for width, height, backdrop, and focus. Mark generated nodes with stable `data-rw-ui` attributes and avoid styling host-authored form fields. The design contract discourages modal takeover for recoverable inline feedback; #828 is explicit for results needing user attention or follow-up. The documentation must state that distinction. No variant's invented `--rw-color-*` palette becomes a package API.

**Pass 6 — responsive and accessibility, 6/10 → 8/10.** Default shell geometry should fit within `calc(100vw - 2rem)` on narrow screens, use a modest desktop maximum width (about `32rem`), and cap height below the dynamic viewport so the body scrolls while the named header and Close remain accessible. Verify 320px and 375px widths, 200% zoom, long title/body, and keyboard-only operation. Generated Close and other package-owned controls need at least a 44px touch target; package-owned text and control contrast should meet 4.5:1 with default tokens and show a visible `:focus-visible` ring. Native `showModal()` provides modal semantics; bind the visible heading as accessible name. Focus the first meaningful field on open, otherwise the heading/content; after handled errors, scroll/focus the first invalid field or error summary; on close, focus the connected trigger or a safe page landmark. Escape and Close dismiss immediately, backdrop does not. The host remains responsible for contrast, labels, and field styling in its own body.

**Pass 7 — unresolved decisions.** One product tradeoff remains: should the package add a cancellable dirty-form dismissal/replacement guard, or retain the approved immediate Close/Escape/replacement behavior and document that unsaved input can be lost? The approved behavior remains in force; a guard would change it and is not included in implementation tasks. The CEO's separate UC828-1 dismissal-watermark challenge also remains open. No other design choice needs a new user decision.

**NOT in scope.** App typography/layout/theme, a generic alert/toast system, an automatic modal for validation, package-owned form actions, and visual redesign of the host page are excluded because the host owns them. Live server-pushed dialogs remain deferred by the approved design. No new design debt is added to `TODOS.md`; required layout, state, and accessibility verification belongs in the #828 plan.

**What already exists.** The [approved sketch](https://github.com/forge-trust/AppSurface/blob/f597e7ad2062379720a835826183fa33fa9a3327/docs/designs/issue-828-dialog-wireframe.png), [RazorWire generated-UI contract](../../Web/ForgeTrust.RazorWire/DESIGN.md), [form failure contract](../../Web/ForgeTrust.RazorWire/Docs/form-failures.md), current Turbo pending markers, and the [#827 loading-feedback issue](https://github.com/forge-trust/AppSurface/issues/827) supply the relevant starting points.

**Implementation Tasks**

- [ ] **D1 (P1, human: ~3h / CC: ~40min)** — Dialog shell — Specify and implement stable header, scrollable body, responsive geometry, touch target, and focus destinations.
  - Surfaced by: Passes 1 and 6; native findings 3–4.
  - Files: `Web/ForgeTrust.RazorWire/assets/src/razorwire.ts`, RazorWire stylesheet, browser tests.
  - Verify: Playwright at 320px/375px, 200% zoom, long content, keyboard/Escape, and disconnected trigger.
- [ ] **D2 (P1, human: ~2h / CC: ~30min)** — Dialog form states — Show local pending/422/unhandled failure/retry without duplicate feedback and retain submitted values.
  - Surfaced by: Pass 2; native finding 2.
  - Files: `examples/razorwire-mvc`, `Web/ForgeTrust.RazorWire/Docs/form-failures.md`, browser/integration tests.
  - Verify: pending, invalid, network/HTTP failure, retry, success close, and dismissal races.
- [ ] **D3 (P2, human: ~1h / CC: ~15min)** — Adoption guidance — State when interruption beats inline feedback and document loss of unsaved input on immediate dismissal or replacement.
  - Surfaced by: Passes 3 and 5; native findings 1 and 5.
  - Files: RazorWire dialog guide, README, MVC example.
  - Verify: one real route trial and guide review against DESIGN.md.

**Design completion summary.** Step 0: 7/10. Passes 1–6: 8→9, 6→8, 7→8, 8→8, 7→9, 6→8. Overall (lowest rated pass): **6/10 → 8/10** after accepted structural clarifications. Pass 7: zero new decisions resolved, one dirty-form tradeoff deferred; one CEO challenge remains. New generated mockups approved: zero; existing approved sketch retained. Review status: **issues open** because the dirty-form tradeoff remains explicit. Outside coverage: unavailable; native design review complete.

<!-- autoplan-accepted:design -->
- Preserve the approved native one-shell UX and low-fidelity interaction sketch. Make the package-owned header (title and visible Close) persistent and discoverable above a scrollable host-owned body; title + Close is a valid minimal dialog when the plain message is null. Do not add an app-styled footer or restyle host form controls.
- Specify modest responsive defaults (roughly 32rem desktop maximum width, 2rem viewport gutters, bounded dynamic viewport height), long-content scrolling, 320px/375px and 200% zoom behavior, visible focus, 44px package-owned touch targets, and default package-owned contrast checks. Respect host overrides and reduced motion.
- Specify exact focus: first meaningful field or heading/content on open; first invalid field or summary on handled validation with scroll into view; connected trigger or safe page landmark after close; no focus return between accepted cross-response replacement. Escape and Close dismiss immediately; backdrop does not.
- Document and test visible trigger waiting and failure before an outside open, retained in-dialog values and pending state, handled 422, local unhandled failure/retry, server-owned success close, null-body dialog, stale response with no shell flash, and eligible page updates. Coordinate with #827 and the existing form-failure contract without duplicate generated UI.
- Document the dialog-use criterion: prefer an explicit dialog when a result needs attention or follow-up and relocating it inline would still be easy to miss; keep routine acknowledgements and recoverable inline form feedback near their controls. Trial one real distant-result route using that criterion.
- Document that immediate Close/Escape, navigation, or accepted replacement can discard unsaved visible form input while a server mutation may continue. Preserve the approved immediate behavior; a dirty-form guard is an unresolved product choice, not an accepted feature.
<!-- /autoplan-accepted:design -->

### DX review — MVC adopter path (2026-09-30)

**Step 0 — product, persona, and clock.** Product type: ASP.NET Core MVC library with documentation and an executable sample. Mode: **DX POLISH**. Primary developer: an MVC developer already returning RazorWire streams who wants a Save or Check status result to appear next to the user's attention rather than at a distant target. They know controllers and Razor views, have limited patience for browser lifecycle code, and expect one sample that works with the bundled Turbo runtime. The current package [README quickstart](../../Web/ForgeTrust.RazorWire/README.md#60-second-quickstart) starts the repository MVC example with the .NET 10 SDK; the public v0.1 feed is not yet live. **TTHW for #828 is unmeasured** because the feature has not been implemented. The review target is under five minutes from a prepared RazorWire MVC app to the first working server-selected dialog. Also time a clean checkout to the sample result and report it separately; do not present a warm run as cold onboarding.

**Developer perspective (inferred, to verify).** I already return `this.RazorWireStream().Update(...).BuildResult()` from a controller. My status result lands in a region below the form, so users miss it. I want to keep the server view and add one explicit choice that shows an actionable result in a dialog. I search the RazorWire README for streams, find the builder reference, and expect a complete controller branch and link or form markup. I am unsure whether `OpenDialogPartial` needs a page target, how GET opts into streams, and what the same route returns without JavaScript. I copy the example, click Check status, and want to see a named dialog immediately after the response arrives. Then I submit its form with bad data and expect to remain inside it with my input intact. If I accidentally queue two opens, I want the exception to tell me to use `ReplaceDialog*`. If I use `Build()` or a custom Turbo build, I need to know which context or compatibility guarantee is missing. These are predicted friction points from the current API and docs, not observations of a shipped #828 feature.

**Competitive DX benchmark.** Compare choices, not unmeasured timings. [Turbo Streams](https://turbo.hotwired.dev/handbook/streams) documents custom actions and `data-turbo-stream` for GET links; the application must still supply modal shell and lifecycle behavior. [htmx's custom modal example](https://htmx.org/examples/modal-custom/) shows a server-loaded dialog with app-side CSS and JavaScript. RazorWire's proposed advantage is a package-owned shell and fluent server response with no app-authored dialog JavaScript, while preserving ordinary HTML fallback. Neither peer source gives a comparable cold-start time for this precise MVC workflow. The target is therefore an internal usability goal, not a claim of competitive timing parity.

**Magical moment and smallest delivery vehicle.** In the source-backed MVC sample, a developer runs `dotnet run --project examples/razorwire-mvc/RazorWireWebExample.csproj`, visits a documented `/Reactivity/DialogResponses` route, clicks Check status, and sees a server-selected partial and follow-up form in one modal. The same route has a Save POST that updates the page, handles `422` inside the dialog, and explicitly closes on success. The quickstart should show the initiating `data-turbo-stream` GET link, controller stream/HTML branches, Razor partial, and form as copyable pieces. No new playground or hosting service is needed.

**Nine-stage developer journey.**

| Stage | Developer does | Current evidence / friction | Required #828 follow-through |
|---|---|---|---|
| Discover | Reads package README or namespace index | Streams documented; dialog absent until shipped | Link first meaningful dialog mention to focused guide |
| Evaluate | Compares inline update with dialog | No shipped outcome yet | Show when interruption is useful and when inline is better |
| Install | Uses configured feed or source-backed example | Public v0.1 package unavailable | State source/feed path honestly; avoid premature NuGet command |
| Hello world | Runs MVC sample and clicks Check status | Existing sample command works for RazorWire, not yet #828 | Add exact route, command, expected visible result, and timed proof |
| Integrate | Adds `OpenDialog*` to a controller and HTML fallback | Request metadata and GET stream opt-in are easy to miss | Copyable GET/POST branches and `data-turbo-stream` link |
| Debug | Handles invalid sequence, missing partial, 422, or stale response | Existing `Build()` error explains async actions; #828 errors not specified | Problem + cause + fix + guide link, with intentional stale-ignore explanation |
| Upgrade | Uses bundled or host-managed Turbo | README specifies bundled 8.0.23 and host responsibility | State tested version and compatibility tests for custom runtime |
| Scale | Reuses shell and handles overlapping writes | Presentation tokens do not serialize server mutations | Explain app concurrency/idempotency and cache headers |
| Migrate | Converts one distant inline result | No automatic migration needed | Show before/after controller shape and preserve HTML route |

**First-time developer roleplay (predicted, not measured).** T+0:00: finds the README's 60-second source-backed sample. T+0:30: starts the MVC app and navigates to the #828 route. T+1:00: sees Check status but may miss `data-turbo-stream` if copying only controller code. T+2:00: adds `OpenDialogPartial` and gets a visible modal in the prepared sample; if `Build()` is copied, an actionable exception directs them to `BuildResult()`. T+3:00: submits invalid data and sees server errors in the same dialog. These timestamps are a walkthrough target; a clean checkout and a prepared-app trial must measure actual elapsed time and record prerequisites and deviations.

**Dual voices.** Outside Codex DX voice: **unavailable** on the Codex host. Native `combo/sub` reviewed fixed input `bad1c3a9a7fd6d7b40ae50a237aa9e4b013557aecbd4d11681b19e00670a3f14` and reported five gaps: unknown first-run/TTHW, shorthand public signatures, unspecified actionable developer errors, missing copyable cross-linked flows, and unclear interaction override policy. The first four are in-scope documentation and verification work. The fifth is a taste/scope choice: the approved design fixes Escape, Close, backdrop, focus, and immediate replacement; adding an extension point would be a new public API. Keep those behaviors fixed and document that boundary. **Consensus table:** getting started, naming, errors, docs, upgrade, and environment all **N/A** for cross-model agreement because outside coverage is missing; only the native findings inform this pass.

**Passes 1–8, before → after plan completeness.**

| Pass | Score | Evidence and disposition |
|---|---|---|
| 1. Getting started | 5 → 8 | Existing README supplies `dotnet run`; add the #828 route, exact click result, copyable path, and separate cold/prepared timers. Target under five minutes is unverified. |
| 2. API design | 7 → 9 | Import the approved exact `OpenDialog`, `OpenDialogPartial`, generic/name component, and matching `ReplaceDialog` overloads; `CloseDialog()` and `HasActiveDialog` retain fluent/buffer semantics. Document `Build()` failure and null text. No raw HTML overload. |
| 3. Errors/debugging | 5 → 8 | Specify immediate second-open, replace-without-open, `Build()` without request context, missing partial, and malformed metadata as problem/cause/fix cases. A stale browser action is intentionally skipped; describe how to reproduce and diagnose it. |
| 4. Documentation | 5 → 8 | README, focused guide, API reference/XML docs, namespace index, MVC sample, and design contract must cross-link. Include plain text, partial/component, page-update-plus-open, handled 422, close-on-success, and no-JS HTML branches. |
| 5. Upgrade | 6 → 8 | No migration from current stream APIs is required; new opt-in methods preserve old behavior. Test bundled Turbo 8.0.23; host-managed/custom modes need explicit compatibility note and browser proof. |
| 6. Environment/tooling | 7 → 8 | Current source-backed app and script packaging exist. Verify static and embedded assets, strict CSP, TypeScript build, .NET tests, and browser tests; include a one-command focused sample run. |
| 7. Community/ecosystem | 7 → 7 | Existing source repo, sample, package index, and feedback channels remain the discovery path. Public v0.1 publishing is separate; no #828-specific community program is justified. |
| 8. Measurement | 4 → 8 | Measure the approved first useful dialog from a prepared app and a clean checkout separately; record steps, duration, first-run failures, and whether a real distant-result adopter sees the outcome. No telemetry or automated release gate is added. |

**Three error contracts to implement and test.** Exact wording may follow repository conventions, but each message must name the problem, cause, fix, and canonical dialog guide. (1) Second `OpenDialog*` while `HasActiveDialog` is true: one dialog is already queued; use `ReplaceDialog*` or close first. (2) `ReplaceDialog*` without a pending open: no active buffer payload exists; call `OpenDialog*` or branch on `HasActiveDialog`. (3) `Build()` with a dialog: request correlation is unavailable; return `BuildResult()` from the controller or render with a request-aware context. Missing partial names should identify the failing view and MVC lookup context; malformed correlation metadata must fail the dialog action safely before output, with a developer-diagnosable cause. Browser stale commands are ignored by design, not reported as a server failure.

**DX scorecard.** Getting started 8, API 9, errors 8, docs 8, upgrade 8, environment 8, community 7, measurement 8. Mean **8.0/10**, up from an initial **5.8/10** plan assessment; this scores written coverage, not shipped behavior. TTHW observed: **unmeasured**; target: **under five minutes from prepared app**, clean checkout recorded separately. Competitive rank: target competitive, not yet measured. Magical moment: designed via source-backed MVC route. Zero friction and learn-by-doing: planned; uncertainty: error contracts planned; opinionated defaults and theme escape: planned; code in context: planned; no new interaction escape hatch authorized.

**DX implementation checklist.** Verify: exact sample command and route; copied GET/POST HTML/stream branches; first modal result and `422` form; error messages with problem/cause/fix/docs; stable overload and `HasActiveDialog` reference; README → guide → API → sample links; version/CSP/static-asset notes; two TTHW clocks with observed values; one real distant-result route. The existing public package release remains a separate release process. No new `TODOS.md` entry is needed for in-scope DX work.

**NOT in scope.** Hosted playground, NuGet publishing before v0.1, migration codemod, runtime telemetry, new interaction customization hooks, and a new modal framework are excluded from DX POLISH. **What already exists.** [RazorWire README](../../Web/ForgeTrust.RazorWire/README.md), [MVC sample](../../examples/razorwire-mvc/README.md), [form failure guide](../../Web/ForgeTrust.RazorWire/Docs/form-failures.md), builder XML documentation, bundled Turbo and packaging tests.

**Implementation Tasks**

- [ ] **X1 (P1, human: ~2h / CC: ~30min)** — Quickstart — Add a complete #828 MVC route walkthrough and time the first result from prepared app and clean checkout.
  - Surfaced by: DX pass 1 and native finding 1.
  - Files: RazorWire README, focused dialog guide, `examples/razorwire-mvc`.
  - Verify: execute documented command and both timed journeys, recording prerequisites and result.
- [ ] **X2 (P1, human: ~2h / CC: ~25min)** — API and errors — Document exact overloads and actionable invalid-sequence, context, rendering, and metadata failures.
  - Surfaced by: DX passes 2–3 and native findings 2–3.
  - Files: `RazorWireStreamBuilder.cs`, `RazorWireStreamResult.cs`, guide and unit tests.
  - Verify: public API tests and assertions for problem/cause/fix information.
- [ ] **X3 (P2, human: ~1h / CC: ~15min)** — Docs navigation — Cross-link copyable text/partial/component, form 422/close, HTML fallback, and runtime compatibility examples.
  - Surfaced by: DX passes 4–5 and native finding 4.
  - Files: README, package index, focused guide, MVC sample.
  - Verify: run snippets and check canonical links.

**Unresolved DX decisions.** No new DX commitment is approved beyond the user-approved interaction contract. A package-level interaction override remains a taste/scope proposal, tied to the design phase's dirty-form guard question; it is not an implementation task. UC828-1 remains the separate CEO protocol challenge.

<!-- autoplan-accepted:dx -->
- Keep DX POLISH for the primary MVC developer persona. Provide a short source-backed path to the first visible server-selected dialog: exact .NET 10 prerequisite, sample run command, documented route, initiating GET link, server stream/HTML branches, app partial/form, expected visible result, and retry/422/close behavior. Target under five minutes from a prepared app; measure and separately report a clean checkout without claiming an unobserved time.
- Preserve and document every approved public overload by exact name and signature from the #828 design, including all `OpenDialog*` and `ReplaceDialog*` text/partial/generic/named component forms, `CloseDialog()`, `HasActiveDialog`, fluent return, title/null-body constraints, single pending slot, and `BuildResult()`/`RenderAsync` requirement. Include copyable examples for each body type and page-update-plus-open; do not add raw HTML or change fixed interaction defaults.
- Make invalid sequence, absent pending dialog, unscoped `Build()`, missing view, and malformed correlation failures actionable with problem, cause, fix, and canonical guide pointer. Explain intentional stale browser skips and presentation-only token limits. Test error content and recovery path through public APIs.
- Cross-link README, focused dialog guide, package index, XML/API reference, design guidance, and MVC sample. Show GET stream opt-in, handled 422 validation, explicit success close, no-JavaScript GET/POST HTML fallback, cache headers, CSP, bundled Turbo 8.0.23 proof, and host-managed compatibility expectations.
- Record the adoption trial's route, steps, observed first-result time, visibility without scrolling, and whether inline relocation would suffice. Do not add telemetry or an automated release gate. State that focus, Escape, Close, backdrop, and immediate replacement follow the approved fixed shell contract; interaction override is unresolved scope, not accepted DX work.
<!-- /autoplan-accepted:dx -->

### Engineering review — implementation and verification (2026-09-30)

**Scope challenge:** Keep the approved #828 feature set. The proposed work spans the builder, result, browser runtime, package assets, MVC example, tests, and documentation, so this is a multi-module change. Use an explicit dialog command value and a narrow internal action metadata seam. Keep page-only streaming behavior. No feature cut is authorized by this review. The existing `RazorWireStreamBuilder`, `RazorWireStreamResult`, `rw-visit` registration, `FormFailureManager`, and MVC Playwright fixture are the reuse points. Proposed dialog classes and routes do not exist yet.

**Architecture and dependency graph:**

```text
MVC route (GET status / POST save / dialog form)
  ├─ ordinary HTML branch → full page/form for non-stream Accept
  └─ stream branch → RazorWireStreamBuilder
       ├─ ordinary page actions (existing)
       └─ one mutable dialog slot → immutable BuildResult snapshot
            ↓ bounded request metadata + action target metadata
       RazorWireStreamResult → antiforgery + handled-form headers
            ├─ page-only: existing ordered streaming
            └─ dialog-containing: render all actions before any bytes
                 ↓ ordered Turbo Stream response
       bundled Turbo 8.0.23 → before-stream-render eligibility gate
            ├─ page target: apply eligible page action
            ├─ stale dialog target/command: ignore
            └─ eligible command: one native <dialog> shell + new flow token
                 └─ form submit → origin-flow header → 422 / success / failure
```

**Architecture findings and dispositions:**

1. [P1] (confidence: 9/10) `Web/ForgeTrust.RazorWire/Bridge/RazorWireStreamResult.cs:93` uses `ParallelSelectAsyncEnumerable(... maxDegreeOfParallelism: 64)` and line 98 writes each result as it arrives. The approved plan already requires atomic rendering for a dialog-containing result. Implement that in a distinct result path that completes validation and rendering before headers or bytes; retain the page-only streaming path. Because actions currently share one `ViewContext` (line 91), verify whether parallel Razor rendering is safe before reusing it in the atomic path. Default to ordered sequential rendering there if the proof is absent. This is an implementation choice for the approved atomicity contract, not a new public behavior.
2. [P1] (confidence: 9/10) Bundled Turbo's `connectedCallback()` awaits each element's own `render()` (`wwwroot/razorwire/turbo.es2017-umd.js:7020`), and each render waits for repaint before invoking the action (`:7035`). Wire order therefore needs a real browser proof for a page action targeting the just-opened dialog. The plan's first gate and Playwright ordering assertion remain mandatory; if the hook cannot enforce the ordering, reopen the affected protocol decision.
3. [P2] (confidence: 9/10) `Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj:29` explicitly enumerates embedded assets, while `RazorWireScriptsTagHelper.cs:172` emits package scripts. Add any dialog stylesheet to the embedded manifest and a stable package loading path; verify strict CSP, static, hybrid, and embedded modes. The existing asset verifier and size budget in `assets/scripts/build.mjs:13` must account for the generated runtime change. This is distribution proof for the approved shell, not optional polish.
4. [P2] (confidence: 8/10) `IRazorWireStreamAction` exposes only `RenderAsync` (`Bridge/IRazorWireStreamAction.cs:8`), so a result cannot classify an action's target without a typed seam. Give package-authored target actions structured metadata and define how the public `RazorWireStreamResult(IEnumerable<IRazorWireStreamAction>)` treats opaque custom actions in dialog responses. Keep raw/manual actions outside implicit dialog scoping; document that limit and verify it through the public result API.

**Independent engineering voice:** `combo/sub` completed a full snapshot read and returned `INPUT: eng 37dd7db7b1c4b8c84926ce54a92aced310fb5b65208011a6dfe277f2c9bbf6bd` with four findings: a delayed outside open after a newer server close; browser action order requiring a real DOM dependency test; full-response buffering and concurrent render pressure; and negative antiforgery coverage for both stream and HTML POSTs. Codex outside execution is unavailable from this Codex host, so there is no cross-model consensus claim. The reviewer recommended changes before implementation sign-off.

**Independent finding dispositions:** The Turbo ordering finding is covered by architecture finding 2, but sharpen the gate: a dialog-target update must be visible after the body is inserted, not merely present in ordered markup or event logs. The resource finding is covered by the atomic-path performance review: bound rendering concurrency, propagate request cancellation, test representative concurrent load, and document the lack of a dialog body size cap rather than silently imposing a new product limit. Negative antiforgery tests are required regression proof of the approved fallback and existing antiforgery contract; add stream and ordinary HTML POST cases asserting rejection before a write and no dialog command. The newer-close race conflicts with the approved opener-order comparison. Record it as **UC828-2** pending final user challenge: should an accepted outside server close advance the per-tab freshness barrier so an earlier-started delayed open cannot reopen the dialog? UC828-1 remains the independent manual-dismissal barrier choice. No barrier change is counted as approved yet.

**Engineering consensus table:**

| Dimension | Native `combo/sub` | Outside Codex | Consensus |
|---|---|---|---|
| Architecture sound? | Conditional; close race and ordering gate | Unavailable | N/A |
| Test coverage sufficient? | No; negative antiforgery and DOM order | Unavailable | N/A |
| Performance addressed? | Needs bounded render/load proof | Unavailable | N/A |
| Security threats covered? | Token boundary sound; antiforgery proof missing | Unavailable | N/A |
| Error paths handled? | Close race pending; POST rejection test missing | Unavailable | N/A |
| Deployment risk manageable? | No new finding | Unavailable | N/A |

**Code quality review:** Use one internal dialog payload/transition implementation behind the eight approved open/replace overloads, with the same validation path for each body type. Keep the browser flow manager separate from `FormFailureManager`'s existing submit/fallback ownership; integrate through documented events or narrow calls. The present `razorwire.ts` already handles `turbo:before-fetch-request` at line 819 for form headers, so coordinate header edits there instead of adding competing listeners with hidden ordering assumptions. No speculative shared-code extraction is justified by two verified callers yet. No current diagram in touched code is known to be stale.

**Test review:** Existing builder tests prove page-action encoding, visit commands, and output order; result tests prove raw output and antiforgery; MVC Playwright tests prove page-local handled and unhandled form failures. All dialog rows below are proposed paths, so none is counted as implemented coverage. Extend those suites and add dialog browser coverage; preserve page-only behavior and the `rw-visit` and form-failure contracts.

```text
CODE PATHS                                           USER FLOWS
[+] Builder action queue                             [+] Check status
  ├─ [GAP] open / second open / replace / close         ├─ [GAP →E2E] stream GET → visible modal
  ├─ [GAP] HasActiveDialog + final slot                 └─ [GAP →E2E] normal GET → full page
  ├─ [GAP] text/partial/component + bad title        [+] Save
  └─ [GAP] Build rejection + snapshot immutability     ├─ [GAP →E2E] POST → page update + open
[+] Result                                            └─ [GAP →E2E] normal/invalid POST → full page
  ├─ [★★ TESTED] page-only output + antiforgery       [+] Dialog form
  ├─ [GAP] metadata valid/missing/malformed            ├─ [GAP →E2E] submit → pending → handled 422
  ├─ [GAP] atomic render / later partial failure       ├─ [GAP →E2E] retry → explicit close
  ├─ [GAP] invalid antiforgery stream/HTML POST        ├─ [GAP →E2E] invalid token → no dialog
  └─ [GAP] page-only regression after dialog path      └─ [GAP →E2E] network/500 → local retry
[+] Browser provenance + shell                       [+] Interruptions
  ├─ [GAP] outside/inside header/start order           ├─ [GAP →E2E] rapid requests in both orders
  ├─ [GAP] stale command + reused dialog target        ├─ [GAP →E2E] Escape/Close while pending
  ├─ [GAP] delayed open after newer server close       ├─ [GAP →E2E] close then older open (UC828-2)
  ├─ [GAP] ordered stream application                  └─ [GAP →E2E] navigation/cache/focus return
  ├─ [GAP] one shell + focus + CSP/theme               [+] Packaging
  └─ [GAP] bounded token bookkeeping                  └─ [GAP →E2E] static/embedded/custom runtime

Legend: ★★ = current happy-path test; GAP = test required for planned behavior.
New dialog paths: 0 currently covered; the existing baseline must keep passing.
```

**Test disposition:** Required proofs are already covered by the user's approved dialog, fallback, and regression contracts, so add them to builder/result unit tests, browser tests, and MVC Playwright integration tests without another scope question. Each assertion should fail for a credible behavior regression; extend current suites where appropriate. Include a same-result missing partial after an earlier page action, both out-of-order completion directions, a dialog-target update visibly applied after body insertion, the late inside response after outside replacement, stale dialog target ID reuse, a page target from that stale response, handled 422 focus after rendering, negative antiforgery for both stream and HTML POST, and static/embedded packaging. The historical local QA artifact `andrew-main-eng-review-test-plan-20260930-0125.md` records routes and value cards; the [test review](#section-6--test-review) preserves the repository's planned coverage. No existing test should be retired. The manual-dismissal watermark assertion remains pending UC828-1 and the newer-server-close barrier assertion remains pending UC828-2; dirty-form and interaction override assertions remain outside accepted scope.

**Performance review:** The atomic dialog path trades streaming latency and memory for no half-applied dialog response. Bound render concurrency in that path and propagate request cancellation. Measure a representative long partial/component body under concurrent requests after the Turbo proof; document the absence of a dialog body size cap rather than imposing one without a product decision. Preserve the existing 64-way page-only path unless tests reveal a current defect. Keep request/flow bookkeeping bounded for long-lived tabs. There are no new database queries in the package; the sample save route must state its concurrency/idempotency rule.

**Failure modes:**

| New path | Failure and user effect | Planned coverage/handling | Critical gap |
|---|---|---|---|
| Builder transition | Invalid second open or replace sequence | Immediate actionable exception; unit tests | No |
| Result rendering | Later missing partial after earlier page action | Pre-render before bytes; integration test | No after implementation |
| Turbo ordering | Dialog target action runs before shell exists | Browser go/no-go proof and E2E ordering test | No after proof |
| Flow correlation | Old response mutates current shell | Suppress stale command/target; race tests | No after implementation |
| Form response | Handled 422 duplicates fallback or focus is lost | Existing form contract plus dialog E2E | No after implementation |
| Dismissal | Earlier-started outside request reopens | UC828-1 pending, explicit final challenge | **Yes until decided** |
| Server close | Delayed earlier open reopens after newer close | UC828-2 pending, explicit final challenge | **Yes until decided** |
| Antiforgery rejection | Invalid POST reaches dialog result | Negative stream/HTML integration tests | No after implementation |
| Packaging | CSS/script missing in embedded mode | Asset manifest and package tests | No after implementation |

**NOT in scope:** server-initiated push, stacked dialogs, implicit form-to-dialog conversion, prefetch, backdrop dismissal, and package-level interaction overrides. The dirty-form guard is a pending UX choice; UC828-1 and UC828-2 are pending protocol challenges. **What already exists:** ordered page-action builder, result renderer, `rw-visit`, form failure manager, Turbo bundle, asset build, static and embedded serving, and MVC Playwright fixture; the dialog slot, shell, flow gate, and dialog tests are new.

**Parallelization:** The Turbo hook proof and adopter baseline are sequential prerequisites to durable public API work. After that, server builder/result and browser shell/assets can proceed in separate module lanes. The MVC example and docs can be drafted alongside them, then integrated after both contracts settle. Shared browser runtime and package manifest edits should be owned by one lane to avoid conflicting asset builds.

| Step | Modules touched | Depends on |
|---|---|---|
| A: route baseline and Turbo proof | examples, RazorWire integration tests | — |
| B: server slot/result | RazorWire Bridge and unit tests | A |
| C: browser shell/flow/assets | RazorWire assets and browser tests | A |
| D: sample, docs, end-to-end verification | examples, Docs, package indexes, integration tests | B + C |

**Engineering implementation tasks:**

- [ ] **E1 (P1, human: ~3h / CC: ~40min)** — result rendering — Make dialog-containing results atomic, validate metadata before writes, and prove safe rendering order. Surfaced by architecture finding 1. Files: `Bridge/RazorWireStreamResult.cs`, result tests. Verify: later missing partial yields no earlier applied frame; page-only streaming regression passes.
- [ ] **E2 (P1, human: ~4h / CC: ~50min)** — Turbo bridge — Prove request provenance and element render ordering, then implement eligible command/target filtering. Surfaced by architecture finding 2. Files: `assets/src/razorwire.ts`, browser and Playwright tests. Verify: both request completion orders and dialog-target action after open.
- [ ] **E3 (P2, human: ~2h / CC: ~25min)** — package delivery — Include shell CSS and runtime in static/embedded assets with CSP-safe loading. Surfaced by architecture finding 3. Files: project manifest, tag helper, asset build, asset tests. Verify: package build and static/embedded consumer smoke.
- [ ] **E4 (P2, human: ~2h / CC: ~25min)** — target metadata — Define typed metadata for built-in actions and document opaque custom action behavior. Surfaced by architecture finding 4. Files: Bridge action types, result tests, dialog guide. Verify: public result API and stale target regression.
- [ ] **E5 (P2, human: ~1h / CC: ~15min)** — antiforgery regression — Add invalid/missing-token stream and HTML POST cases with no dialog output. Surfaced by independent engineering finding 4. Files: MVC Playwright/integration tests. Verify: both modes reject before result write and retain the existing failure UX.

**Engineering completion summary:** Scope accepted as-is; architecture 4 issues, code quality 0 independent issues, test diagram produced with new dialog paths currently uncovered and negative antiforgery added, performance 1 tradeoff to measure; NOT in scope and What already exists recorded; 2 critical unresolved flow gaps (UC828-1 and UC828-2), plus the previously recorded dirty-form taste choice; outside Codex unavailable on this host, native engineering review completed with 4 findings. Test plan artifact saved; no feature code or proposed tests implemented.

**Engineering approval readiness:** PASS for the accepted #828 contract and required implementation proof (user-approved design; `/autoplan` P1 complete behavior, P5 explicit architecture). The written outside-response eligibility changes in UC828-1 and UC828-2, and the optional dirty-form guard, remain pending; no selected option is inferred from this review. Code and tests are still proposed work.

<!-- autoplan-accepted:eng -->
- Preserve the one-slot builder API and one-shell browser contract from the approved #828 design. In dialog-containing results, validate request metadata and fully render ordered actions before committing headers or bytes; keep page-only streaming behavior. Verify shared `ViewContext` safety before any parallel dialog render; otherwise render that path sequentially, with cancellation and bounded concurrency.
- Make the bundled Turbo proof assert a real DOM dependency: a same-response action targeting the dialog body applies only after shell/body insertion. Test both response completion orders, inside/outside provenance, stale target ID reuse, preserved ordinary page actions, and navigation/cache cleanup. If hooks cannot establish the approved behavior, return to the user-approved protocol decision.
- Use structured action target metadata for package-authored actions; document the boundary for opaque custom `IRazorWireStreamAction` results. Keep the dialog shell and form failure ownership separate, coordinating request headers and handled 422 focus with the existing form contract.
- Package dialog styles and runtime through the generated asset, static, and embedded paths; verify strict CSP and host-managed Turbo compatibility. Measure representative concurrent dialog rendering and document that no dialog body size cap is introduced.
- Add stream and ordinary HTML POST tests with missing or invalid antiforgery tokens. Assert rejection before any dialog or page action writes, preserving the existing failure contract. Extend server, browser, MVC integration, and package suites for the accepted behavior; retire no existing tests.
- Do not implement a dismissal-time outside-request watermark (UC828-1), a server-close freshness barrier (UC828-2), or a dirty-form guard without the final user decision. Keep the approved opener-order behavior visible as the current baseline and state its two discovered races.
<!-- /autoplan-accepted:eng -->

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` via `/autoplan` | Scope and strategy | 1 current plan | Issues open | UC828-1 dismissal race accepted as a known limitation at final gate |
| Outside Review | Codex outside voice | Independent second opinion | 0 current plan | Unavailable | Codex host could not launch an independent outside process |
| Eng Review | `/plan-eng-review` via `/autoplan` | Architecture and tests | 1 current plan | Issues open | 10 mapped findings; 2 flow races accepted as known limitations; Turbo proof still required |
| Design Review | `/plan-design-review` via `/autoplan` | Dialog interaction and accessibility | 1 current plan | Issues open | 6/10 → 8/10; immediate dismissal/replacement retained at final gate |
| DX Review | `/plan-devex-review` via `/autoplan` | Developer adoption | 1 current plan | Issues open | 5.8/10 → 8.0/10; TTHW unmeasured, target under five minutes from prepared app |

**OUTSIDE COVERAGE:** CEO, Design, DX, and Eng phases: Codex outside voice unavailable on this Codex host. Native `combo/sub` completed with 6, 5, 5, and 4 findings respectively. No cross-model confirmation is claimed.

**VERDICT:** **APPROVED as-is by the user on 2026-09-30.** CEO, Design, DX, and Engineering plan reviews are complete. The user chose to retain the existing opener-order protocol and immediate dialog dismissal/replacement. The Turbo hook and DOM-order proof remains an implementation gate; if it fails, return for protocol review as the plan requires.

**FINAL GATE DISPOSITION — A, approve as-is:**
- UC828-1: retain opener-order eligibility. A request started before user dismissal may later open a dialog when its order is newer than the prior opener. Do not add a dismissal-time watermark.
- UC828-2: retain opener-order eligibility. A delayed outside open may arrive after a newer accepted server close. Do not add a server-close freshness barrier.
- Design/DX: retain immediate Close, Escape, and replacement, with documented potential loss of unsaved visible input. Do not add a dirty-form guard or interaction override.

These are explicit accepted limitations of the plan, not claims that the race scenarios cannot occur. The native review findings and unavailable outside review remain recorded above.
