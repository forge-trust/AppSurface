# #772 host-specific browser QA checklist

This planned proof belongs to the [persona fixture activation design](../designs/issue-772-persona-scoped-fixture-activation.md). The checklist was written before implementation. Measured results are recorded below, separately from the planned checks.

Use the [existing DevAuth example](../../examples/auth-aspnetcore-dev-auth/README.md) in Development on loopback. Scenario key: `candidate-review-demo-v1`. The host owns one process-local synthetic candidate, separate role readiness and separate completed work; restarting resets the store. Use the persistent fake-auth marker and explicit POST selection, with a safe local destination. No provider or seed endpoint participates.

Planned route contract (to be implemented and documented in the example): open `/_appsurface/dev-auth/`; POST `/_appsurface/dev-auth/select/labeler?returnUrl=/candidate/label` or `/_appsurface/dev-auth/select/reviewer?returnUrl=/candidate/review`. The persona LandingUrls are `/candidate/label` and `/candidate/review`. Labeling completes through POST `/candidate/label/complete`, review through POST `/candidate/review/complete`; each successful product action returns a local HTTP 303 to its own page. These actions never prepare fixtures. Record the actual loopback origin used by the implemented host; no port is prescribed by this planning artifact.

At each ready page assert the visible heading, selected persona, `Fixtures ready`, stable candidate ID and text states `Labeling: pending/completed` and `Review: pending/completed`. A not-ready page shows `Fixtures not ready` plus `Reselect labeler` or `Reselect reviewer`, an explicit POST form to the corresponding selection route with its safe return URL. The browser waits for selection without a custom spinner; selection does not report success while activation is pending. The fixed HTTP 500 body explains that fixtures are not ready and directs the operator back to the DevAuth control URL. An identity marker alone never proves readiness.

1. Begin with an empty fresh host. Select labeler; wait for its landing page. Record the candidate ID and both progress states. There must be exactly one candidate, labeler ready and neither stage completed.
2. Complete labeling using the host's product action. Select reviewer. Verify the same candidate ID and preserved labeling completion; complete review. Reselect each persona twice. Both completed stages and the original ID must remain.
3. Use two browser profiles with independent cookies. Select labeler and reviewer concurrently. Check each resulting identity and page readiness, then confirm exactly one candidate and preserved work. Deterministic HTTP tests will gate both completion orders; this manual step does not substitute for them.
4. In the test-host-only one-shot fault run, successfully label first. The substituted scoped reviewer handler ensures the same candidate, then throws before readiness. The host returns safe HTTP 500 with its queued cookie preserved, no Location and no exception details. This is the sample's policy; the framework does not guarantee cookie preservation or rollback.
5. Navigate to the reviewer page after failure. Expect HTTP 409 with visible “Fixtures not ready” and a local explicit POST reselection action. Verify no candidate is added, no work is reset and no review is completed by the GET.
6. Reselect reviewer with the one-shot fault exhausted. Expect awaited success, the same candidate and preserved labeling. Complete review, repeat and overlap selections, then confirm exactly one candidate and both completed stages.
7. Confirm anonymous challenge and wrong-role forbid, clear-persona behavior, safe return URL precedence, keyboard access to selection/retry forms, 375px layout without horizontal scroll and text labels that distinguish persona identity from readiness.

Record candidate IDs, stage states, response status and request order in the test result; exclude cookies, real identities and raw error payloads. The existing real-socket verifier retains private jars, disabled redirects/proxies, finite timeouts, child-owned listening evidence and bounded sanitized failure artifacts.

Execution result template (leave unmeasured until the feature exists): record the tester persona, .NET SDK/version, checkout, actual origin, docs read and exact commands, cold/warm dependency state, wall-clock start/end and first understood same-candidate result. The prepared-developer target is at most five minutes, including reading, restore/build/launch and labeler → labeling → reviewer; record SDK installation and checkout acquisition separately. Record stuck steps and recovery results, actual keyboard/375px/768px/200% zoom checks, and any target overrun. Keep automated verifier duration separate from human onboarding time. The execution record below supplies the measured observations.


## Execution record, 2026-10-01

The Standard browser pass ran against owned Development loopback hosts using Chromium. The normal sample was `http://127.0.0.1:61259`; a temporary real-Kestrel test host at `http://127.0.0.1:64562` replaced only the scoped activation service to fail the first reviewer selection after ensure. No production fault switch was added. Production and test source matched commit `e1bca718`; the only subsequent change at this point corrected a planning word rejected by the package brand gate.

| Required check | Observed result |
| --- | --- |
| First labeler selection | HTTP 302 then ready 200, one `synthetic-candidate-001`, both work stages pending |
| Labeling and reviewer switch | Product POST 303; reviewer 302 then 200 with the same ID, labeling completed and review pending |
| Review and repeated selections | Product POST 303; both stages completed; four further alternating selections preserved the ID and work |
| Independent browser identities | Two separate Chromium contexts submitted their native labeler/reviewer forms concurrently; each retained its selected persona and rendered the same candidate with both stages completed |
| Partial activation failure | First reviewer POST returned fixed safe 500 with no success navigation; subsequent authorized reviewer GET returned 409, with the reviewer marker and explicit reselection form |
| Explicit retry | Keyboard Tab reached the 44px `Reselect reviewer` button with a 3px outline/offset; Enter submitted the native POST, followed 302 to ready 200, original ID and completed labeling; review completion then preserved both stages |
| Adjacent auth behavior | Anonymous 401, wrong-role 403, clear 200, admin/viewer pages 200, safe explicit return URL override and matching status identity |
| Layout and accessibility | One h1/main and named navigation; 44px product/retry/navigation targets; 375px and 768px without horizontal overflow; 200% CSS content zoom at 768px without clipping; measured text contrast 16.96:1 and link contrast 6.41:1 |
| Console and links | No unexpected console errors in the normal journey; deliberate 401/403/500/409 probes produced expected browser resource errors. Actual control navigation returned 200; its unsupported HEAD probe returned 405 |

Screenshots of labeler, 375px, 768px, 200% zoom, 409 recovery and recovered completion were visually inspected. No actionable QA finding or repair was recorded. The provisional selected-surface score was 100/100; this is not a screen-reader or operating-system zoom certification. Local artifacts are retained in the ignored `.gstack/qa-reports/772-standard/` directory, with ordered checkpoint/probe files and a report.

An optional reload after the recovery proof returned connection refused because closing its worker stopped the worker-owned host. This was a fixture lifecycle error; the completed recovery evidence remains valid. A final selection on the normal host returned 200 with the original ID and both work stages completed. Hosts/tabs opened for QA were owned and are cleaned up by the workflow.

### Prepared-developer measurement

A fresh archive of `e1bca718` was acquired in 0.842 seconds, separately from the measured journey. The .NET 10.0.102 SDK and NuGet cache were already available; SDK acquisition was unnecessary and excluded. This was an agent-run prepared-developer proxy, not an observed human onboarding session.

The documentation read → restore → build → launch → labeler → complete labeling → reviewer → understand same-candidate result ran from 08:18:42.571 to 08:25:58.962 UTC, totaling **7m16.391s**, exceeding the five-minute target. Restore took 2.800s, build 2.606s with zero warnings/errors, and the successful HTTP journey 3.780s. The first launch stalled before reporting a listening address; retrying with the sample-documented reload setting started an ephemeral loopback listener. Documentation reading and startup recovery account for the remaining time. The owned measurement host was stopped. This overrun is an onboarding observation, not an automated release gate; no human-speed claim is inferred.
