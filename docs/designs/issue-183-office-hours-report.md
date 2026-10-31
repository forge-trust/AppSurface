# Office-hours report: AppSurface #183

Design: [Preserve the guide concept when switching versions](issue-183-concept-preserving-version-switching.md)
Case: [#183](https://github.com/forge-trust/AppSurface/issues/183)
Wireframe: [image](issue-183-version-switch-wireframe.png) · [HTML](issue-183-version-switch-wireframe.html)

## Decisions

The user selected stable concept IDs for new guides with explicit mappings for legacy releases, and accepted the three-state wireframe. The design documents identity precedence, honest absence and uncertainty, locale preservation, archive integrity, and hosted/static publication. The user approved the design on 2026-10-27 (D8/A), including carrying the minor fallback concern into engineering review.

## Review Evidence

The requested `combo/sub` model was unavailable on five attempts. Following the user's fallback policy, the independent review ran with `gpt-6-luna`. The first round reviewed the whole draft; the second reviewed the locale clarification and prior findings. Original evidence is retained beside the private design:

- [Round 1 verdict](/Users/andrew/.gstack/projects/forge-trust-Runnable/andrew-main-design-20261027-230354-issue183.md.review.yK1g2K/round-1.json)
- [Round 2 verdict](/Users/andrew/.gstack/projects/forge-trust-Runnable/andrew-main-design-20261027-230354-issue183.md.review.yK1g2K/round-2.json)

The review helper validated both sealed receipts and installed the authoritative findings in the design. Local relative-link checks and whitespace validation passed. The accepted sketch was rendered and visually checked. No implementation tests ran because this session produced planning artifacts only.

## The Assignment

Before building, choose two real published releases and one guide that moved or changed title. Record their canonical routes and pinned release manifests, write the smallest explicit bridge that links them, and identify one genuinely absent or unknown counterpart. Walk those cases through the accepted wireframe and check that every fallback names an existing destination in the selected release. This tests the authoring burden and wording against real archives.

## Coaching

“Keep me on the same concept even if the page moved” identifies reader intent as the continuity requirement. “Say so and offer a useful fallback” couples honest uncertainty with recovery. Both statements drive the selected design.

## Approval and Handoff

Design approved on 2026-10-27 (D8/A). The user explicitly requested `/plan-eng-review` of this design, carrying R2-1 forward. Engineering review will confirm schema/API compatibility, resource bounds, publication integration, and the recorded fallback concern. No implementation or PR is authorized by this report.

<!-- gstack:office-hours:report:start -->
## Spec Review

Disposition: COMPLETED

Stop: PASS

No blocking findings remain. The 1 open minor finding(s) below are recorded for the user, not fixed.

| Round | Blocking findings | Minor findings | Prior findings confirmed resolved | Quality score |
|---|---:|---:|---:|---:|
| 1 | 1 | 1 | 0 | 8/10 |
| 2 | 0 | 1 | 1 | 8/10 |

Findings reported across rounds: 3 (sum of round inventories; recurrences count again).

Confirmed resolutions: 1 (sum of explicit later-reviewer resolved statuses).

Unresolved findings in the last completed inventory: 1 (0 blocking, 1 minor).

Completed fix-and-review transitions: 1 (rounds, not edits).

### R2-1 — clarity (minor)

**Problem**

> The recovery rule still says a nearest canonical ancestor landing page may be offered without defining what qualifies as a landing page or how to choose among qualifying ancestors.

**Remedy**

> Clarify the deterministic landing-page eligibility and selection rule, or state that recovery omits this action unless an explicitly authored section is available.

### Prior finding evidence

**R1-2 → R2-1 (persisting)**

> The recovery paragraph still says 'otherwise a nearest existing canonical ancestor landing page may be offered' without an eligibility or selection rule.
<!-- gstack:office-hours:report:end -->
