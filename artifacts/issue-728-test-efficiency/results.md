# Issue #728 coverage-efficiency results

Use this page to publish the implementation result. A safe no-change ceiling is a complete outcome.

## Before / after evidence

| State | Commit SHA | Evidence workflow run | Artifact URL | Runner/runtime fingerprint matches baseline? | Exact coverage-step median seconds | Relative spread | Actual serial-set attribution | Verdict |
| --- | --- | --- | --- | --- | ---: | ---: | --- | --- |
| baseline cold | pending | pending | pending | pending | pending | pending | pending | pending |
| baseline warm | pending | pending | pending | pending | pending | pending | pending | pending |
| post-change cold | pending | pending | pending | pending | pending | pending | pending | pending |
| post-change warm | pending | pending | pending | pending | pending | pending | pending | pending |

## Claim rules

- The primary metric is exact coverage-step wall-clock time from the retained manual workflow artifact.
- A candidate needs a five-sample median reduction of at least 15% and at least five seconds in the same comparable cold or warm class.
- Project/lifecycle attribution explains the result; it never replaces the step-time claim.
- One successful ordinary PR coverage run is required to merge a sharing change.
- Two comparable baseline and two comparable post-change evidence runs are required before claiming the issue-level 240-second result or closing #728.

## Safety protocol record

| Candidate | Targeted ten-run command and seed | Full-gate runs | Failure-injection command | Captured identities | Absence assertions | Result |
| --- | --- | --- | --- | --- | --- |
| pending | pending | pending | pending | pending | pending | pending |

## Ceiling or follow-up decision

- Outcome: pending.
- Best safe observed reduction: pending.
- Why additional sharing is unsafe or unproven: pending.
- Follow-up owner and focused issue/PR scope: pending.
- Re-evaluate on **2027-02-11**, or earlier after a material test-topology, runner-image, or runtime change.
