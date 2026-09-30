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
