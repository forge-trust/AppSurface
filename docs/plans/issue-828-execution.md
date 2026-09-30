# #828 implementation progress

Branch: `codex/make-it-so-dialog-responses`. Approved scope: [implementation plan](issue-828-dialog-responses.md). Goal: validated draft PR.

## Protocol proof

2026-09-30: ran `/private/tmp/issue828-turbo-proof.cjs` in Chromium with the package's unmodified Turbo 8.0.23. Passed GET/POST request-header propagation, success and handled 422 response hooks, awaited stream render wrapper, actual update of a target inserted by a preceding asynchronous action, and both response completion orders. Turbo stream GET links create hidden temporary forms, so provenance and focus must retain the initiating link through `turbo:click`/`turbo:before-visit` rather than treating that form as the origin control. This is a tested implementation concern within the approved protocol.

## Shared wire contract

- Request headers: `X-RazorWire-Request` (UUID), `X-RazorWire-Order` (positive safe integer), optional `X-RazorWire-Flow` (UUID for requests from live dialog).
- Stream metadata attributes: `data-rw-request`, `data-rw-order`, optional `data-rw-flow`, `data-rw-dialog-phase` (`origin`, `new`, or `closed`). All package-authored target actions in a correlated response carry these attributes; opaque custom actions remain outside implicit scoping.
- Final action: `action="rw-dialog"`, `dialog-command="open|close"`, `dialog-title="encoded title"` on open, `<template>` with final body. No raw HTML dialog overload.
- `origin`: before dialog slot, or response without dialog command. `new`: ordinary actions after final open slot. `closed`: ordinary actions after final close slot. Ordinary page targets always remain eligible; dialog targets require the resolved live flow. A rejected open must not confer eligibility on its following dialog actions.
- One-use request token for final dialog command; server metadata validates UUIDs, positive order <= JS safe integer, single header values. Missing metadata rejects dialog-containing results. Page-only responses without correlation preserve existing rendering.
- Render dialog-containing results sequentially into a complete buffer before antiforgery/header/body writes. One render lane avoids shared MVC helper context races. Snapshot final slot at BuildResult and RenderAsync.

## Remaining work

Live MVC preliminary verification on 2026-09-30 passed GET status open/ordered body update, Escape returning to the GET link, POST Save open, handled 422 validation focusing `Name`, and successful explicit close with page result and trigger focus. `/Reactivity/IncrementCounter` retains inline default; its explicit `openDialog=true` submit button opens the response without scrolling. Host Tailwind reset exposed missing native centering (1280×720 viewport: shell x=0/y=0, 512×159). Adding shell `margin: auto` fixed it after rebuilding embedded assets (x=384/y=280.5); integration centering assertion requested. Screenshots retained in `/private/tmp/issue828-adoption{,-fixed}.png`. These are implementation checks; Standard QA follows the clean baseline commit.

Programmatic form provenance red proof: a freshly cloned outside Save form submitted while the in-dialog `Name` field held focus emitted a flow header although its request target was outside the shell. Request ownership now uses actual form containment; GET stream-link capture still supplies the original link's provenance. A null submitter clears the previous submitter, and an active control is used only when it belongs to that form. Browser regression requested; green proof pending final rebuilt app/tests.

Runtime smoke: package scripts in Chromium passed one shell, server open and dependent target, first field focus, handled 422 validation focus, Escape returning to original GET link, and rejecting an old outside open after newer open. Typecheck/build passed. Existing asset suites passed 108 tests before final runtime refinements; rerun scheduled. Narrow internal form-failure seam suppresses stale unhandled fallback before it can resolve a reused target in a newer dialog.

Coverage gate: `./scripts/coverage-solution.sh` unchanged, full solution plus gate at 95% line/85% branch and 95% patch line/85% patch branch against `origin/main`. Must run unsandboxed with host/container prerequisites and repeat on final code before draft PR.

Server builder/result and tests; browser manager and styles/package delivery; MVC sample/forms/fallback; integration/browser tests; docs; enhance; clean baseline commit; Standard browser QA; exact repository coverage gate; ship draft PR.

Pre-existing changes belong to #828 planning. Preserve review scratch directories without committing them.

## Integrated validation progress — 2026-09-30

- Core implementation committed as `4feb8d94`; current main integrated as `c474a423` (base `a15bd0c1`, includes #827 form loading). Additive merge preserves both stylesheet paths, loading ownership and dialog stale-form fallback guards. Generated assets rebuilt.
- Enhance cycle 1: six supported fixes (link provenance bound to actual temporary forms rather than URL FIFO, retired shell identity guard, final Turbo renderer wrapping after synchronous extension dispatch, current-response-only validation focus, `RenderAsync` response-token echo, corresponding docs). Cycle 2 independent verdict pending. Accepted UC828 opener races remain documented.
- Asset validation after integration: `npm test` passed 21 generated-asset and 130 runtime tests. Authored runtime harness now bundles the module graph through esbuild.
- Preliminary UI check on local integrated app `http://localhost:5829`: status dialog/ordered update, Escape and exact initiating-link focus, save dialog/first Name field, handled empty-name validation/Name focus, successful completion/page result/server close and Save trigger focus all passed. Standard QA follows clean integration-test baseline.
- Browser regression run outside sandbox executed 23 cases (12 pass, 11 fail). Actual test setup/assertion corrections in progress: hidden input value assignment, phase expectations, status-vs-save opening, deliberate overlap setup, no-JavaScript navigation wait. No browser-test passing claim yet.
- Review scratch moved to `/private/tmp/issue828-preserved-design-reviews`; retained without publication.
- Required gates pending: full server suite terminal result; final Playwright cases; clean review; Standard QA; exact `./scripts/coverage-solution.sh` aggregate95/85 and patch95/85 against integrated origin/main; draft PR.

## Browser baseline ready

- Full server/host suite: 526 passed, 0 failed (host run log `/private/tmp/issue828-server-host.log`).
- Enhance cycle 2 independent review: `NO_FINDINGS`; stopped before checkpoint after six fixes in cycle 1.
- Final asset run: 21 generated-asset checks and 130 runtime tests passed; TypeScript typecheck passed.
- Dialog Playwright suite: 27 passed, 0 failed, including cancelled confirmation provenance, retired shell close event, extension render ordering, validation focus ownership, 422/success/fallback/antiforgery, both response orders, reused targets, stale failures and responsive/CSP checks.
- Corrected fixture duplicate IDs, observed Turbo completion on document for detached forms, asserted existing handled antiforgery response, and awaited asynchronous Turbo render before checking replacement flow.
- Isolated `TMPDIR=/private/tmp/issue828-native-tmp` resolves a transient verified Tailwind native-library loading stall; no gate or product behavior was changed.
- Standard QA, fresh full coverage gate and ship draft PR remain.

## Final failure-path revision

- Standard live QA complete at `/private/tmp/issue828-qa-standard/report.md`: status/open/order/Escape focus, save/422/name focus, valid completion/explicit close/trigger focus, inline counter default and explicit centered modal all passed, no console errors. Scoped provisional score100; links, performance, speech and browser chrome focus cycling unscored. No confirmed QA defects.
- A real delayed antiforgery400 reproduced a flow-ownership gap: its package filter emitted uncorrelated targeted streams. Fixed filter metadata echo; invalid correlation keeps400 as plaintext with no mutation. Selector actions matching both page and dialog now preserve page effects and exclude stale dialog matches. Docs and unit tests cover the response contract.
- Reproduction: `LateRealAntiforgeryFailure_CannotRewriteReplacementDialogErrorTarget` failed with current error target overwritten; passed after fix and explicit MVC binary rebuild. Base control unavailable because main has no dialog API.
- Added current500 retry,200 remaining open until explicit server close, and shared selector target partition verification. Final server531/531; dialog Playwright30/30; assets21+130 and typecheck pass; samplebuild0warnings/errors.
- Coverage attempt1 stopped gracefully after production correction made it stale; partial run is not a coverage pass. Exact command unchanged for next attempt.

## Plan audit closure and full-gate triage

- Added copyable plain text, partial, generic component, named component, parameterized component, and pending-slot replacement examples to the canonical guide.
- Added regression checks for default shell/Close text contrast and keyboard focus contrast, plus 640×360 CSS viewport reflow (the effective viewport of a 1280×720 desktop at 200% zoom). This is equivalent layout testing; the in-app browser did not implement keyboard page zoom. Live equivalent-viewport shell/Close bounds passed.
- Prepared running-app interaction: Check status to visible titled dialog/Close took347ms; ordered body update visible without scrolling. Default text/Close contrast14.89:1; keyboard Close focus2px solid#2563eb. This is response timing, not developer setup time.
- Coverage attempt2 runs the unchanged exact gate at `e837dbad`; additional test/docs audit edits make its evidence diagnostic, not final. Auth verifier child-launch timeouts, durable watchdog missing child PID, fake Git startup timeouts, documentation TOC screenshot changes, and one registration SSE timeout require triage. No gate waiver or threshold change.

## Coverage recovery and adoption evidence

- Invalid presentation metadata in the antiforgery filter now emits `X-RazorWire-Form-Handled: false` with its plaintext400 diagnostic, letting only the current form show local retry UI. Unit and live-browser regressions verify that it remains an antiforgery rejection and cannot rewrite a newer dialog.
- Final dialog plus registration browser slice:33/33 pass. Refreshed documentation detail/release screenshots for new TOC links and current release; excluded home pixel noise and search focus/caret captures from the baseline update.
- Enhance coverage cycle1: added semantic typed/named component correlation and discovery, MVC ambiguity, missing-partial diagnostics, cancellation, invalid input and internal stream contract tests. Server543/543 pass. Focused diagnostic patch coverage98.30% line/97.32% branch; focused aggregate is not a solution gate pass.
- All three initially failing process test projects passed full collector reruns sequentially on the host: AuthAspNetCoreDevAuthExample61, DurablePostgreSqlLocalExample55, Durable.AdoptionMetrics57. Original timeout cause remains unproven; no thresholds, watchdogs or product semantics changed.
- Independent prepared-app adoption on one clean export took242.254s from action/link edits to HTTP readiness. Parent browser click subsequently confirmed a visible titled dialog/body/Close in289ms. Initial clean-source extraction/startup took11.312s (0.673s extraction,10.638s run); global NuGet cache warm and host permissions required. Evidence screenshot `/private/tmp/issue828-prepared-adoption.jpg`.
- Refreshed main adds only the web-push timeout assertion stabilization (#841). Merge it before the final unchanged full coverage run. Standard QA is complete; final coverage/review and draft PR remain.
