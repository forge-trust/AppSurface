Execution update (2026-11-11): the user subsequently authorized `/make-it-so` implementation and a draft PR. This completed historical card remains the planning authority and maintainer assignment; it is not an operational receipt. Statements below that implementation was deferred describe the card collection on 2026-11-10. No merge or hosted rehearsal dispatch is authorized.

# Protected acceptance card: #910 Tailwind artifact verification

Completed: 2026-11-10.
Status: retained evidence selected, protected field sources recorded, responsible maintainer assigned. The pre-implementation card/owner assignment is complete; implementation and a draft PR were authorized on 2026-11-11.
Responsible maintainer: **Andrew Krock ([`@akrock`](https://github.com/akrock))**, explicitly assigned by the user in this chat on 2026-11-10.
Scope: the approved [#910 design](../designs/issue-910-exact-artifact-verification.md) and [implementation plan](issue-910-exact-artifact-verification.md), limited to the same-attempt, nonpublishing Tailwind rehearsal.

## Retained rehearsal and authority

Use [run 36547070332, attempt 1](https://github.com/forge-trust/AppSurface/actions/runs/36547070332/attempts/1), created on 2026-09-29. GitHub reports `workflow_dispatch`, branch `main`, repository/head-repository `forge-trust/AppSurface` with ID `1035215851`, workflow `.github/workflows/package-artifacts.yml`, source `10d375c6942fc650cc64f64e0748add77540d47c`, and a successful completed attempt. The [producer job](https://github.com/forge-trust/AppSurface/actions/runs/36547070332/job/109335915393) and [nonpublishing consumer job](https://github.com/forge-trust/AppSurface/actions/runs/36547070332/job/109339841837) both succeeded in attempt 1.

The authority is the retained protected workflow execution and its upload/context outputs. The [workflow at that exact commit](https://github.com/forge-trust/AppSurface/blob/10d375c6942fc650cc64f64e0748add77540d47c/.github/workflows/package-artifacts.yml#L128-L158) supplies source, attempt, repository and run from GitHub context, and the artifact ID from the upload step. Its manual/main/own-repository guards execute the post-upload typed producer validation and nonpublishing rehearsal. This records the workflow's actual guards; it makes no new assertion about repository branch-protection settings or the trustworthiness of a compromised authorized producer.

The new v1 receipt did not exist in this historical run. The normalized record below is a planning transcription of protected retained evidence, **not** a newly emitted `needs` output, signed attestation, or operational receipt to feed a future invocation. The future implementation must still obtain its receipt only from the successful named producer's protected outputs, and independent current context from that future invocation.

## Exact values and protected sources

Selected log lines, timestamps, original line numbers, immutable workflow excerpts, and fresh API metadata are preserved in the [sanitized evidence snapshot](https://github.com/forge-trust/AppSurface/blob/3d1a69e1db4dfea8eacf4927e34e6440f9dde6f1/docs/plans/evidence/issue-910/retained-rehearsal.json). References to source lines below mean the original decoded job log recorded there; signed URLs and full logs are excluded.

| Field | Exact value | Protected source | Corroboration |
|---|---|---|---|
| Repository | `forge-trust/AppSurface` | Fixed guarded workflow policy; producer download step 10, log line 760 | Exact-attempt API repository and head-repository |
| Repository ID | `1035215851` | Producer step 11, log line 808: `REPOSITORY_ID`, supplied from `github.repository_id` at workflow line 136 | Attempt, artifact and consumer context agree |
| Artifact ID | `11023576342` | Producer **step 9**, `upload-package-artifacts`: finalization/upload log lines 752–753 at `2026-09-29T09:13:17Z`; step 11 receives it from that step's `artifact-id` output (workflow line 134) | Producer step 10 uses the exact ID; consumer receives the existing job output, not a name lookup |
| Run ID | `36547070332` | Producer step 11, log line 809: `PRODUCER_RUN_ID`, supplied from `github.run_id` at workflow line 137 | Exact attempt and both jobs name the same run |
| Producer attempt | `1` | Producer step 11, log line 805: `PRODUCER_ATTEMPT`, supplied from `github.run_attempt` at workflow line 133 | Exact-attempt API and producer/consumer job payloads report attempt 1; artifact metadata itself has no attempt field |
| Source commit | `10d375c6942fc650cc64f64e0748add77540d47c` | Producer step 11, log line 804: `SOURCE_COMMIT`, supplied from `github.sha` at workflow line 132; step 7 builds with that source stamp | Attempt/job/artifact `head_sha` and consumer context agree |
| Upload archive SHA-256 | `sha256:27151d76524a22cd8126f5dc658295c16cf6d869930ccaf8a3f97d97e14be6fd` | Producer **step 9**, upload log line 750 at `2026-09-29T09:13:17.4530104Z`; the same upload group finalizes artifact `11023576342` two lines later | Artifact API digest, producer step 10 downloaded digest (line 776), and consumer producer-download digest (line 201) all match |
| Expected Tailwind subject SHA-256 | `45a52636f54ea4e2e50713af73025bf4c38f1b4ef5c6e62d1bc312c47f954b01` | Producer step 11, log line 807: `EXPECTED_SUBJECT_SHA256`, sourced from step `producer-binding`; successful validation at line 811 reexports the checked value | Consumer receives the job's `expected_subject_sha256` output; this is a subject digest, not the archive digest |

The upload group is specifically **Upload package artifacts**, step 9, pinned to `actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`. Do not confuse it with step 8's validation-diagnostics artifact `11023690381` or its digest. The [job-output declarations](https://github.com/forge-trust/AppSurface/blob/10d375c6942fc650cc64f64e0748add77540d47c/.github/workflows/package-artifacts.yml#L28-L32) bind `producer_artifact_id` to the upload and `expected_subject_sha256` to post-upload validation. The [consumer context](https://github.com/forge-trust/AppSurface/blob/10d375c6942fc650cc64f64e0748add77540d47c/.github/workflows/package-artifacts.yml#L175-L203) consumes those outputs and GitHub context under the same protected policy.

Workflow path `.github/workflows/package-artifacts.yml`, event `workflow_dispatch`, and producer job `package-artifacts` are fixed policy fields corroborated by the exact attempt/job and immutable source. Neither the artifact name nor its timestamp is selection or attempt authority. Receipt/upload/API digest agreement records consistency within this producer authority; it is not independent producer authentication.

## Normalized historical reference record

This is the intended v1 receipt shape populated from the retained evidence. Its planning provenance is this card and the linked snapshot. The upload logged a bare lowercase 64-hex digest; the transcription prefixes it once with `sha256:`. All identities remain strings.

```json
{
  "schema": "appsurface-github-actions-producer-receipt-v1",
  "repository": "forge-trust/AppSurface",
  "repositoryId": "1035215851",
  "workflowPath": ".github/workflows/package-artifacts.yml",
  "event": "workflow_dispatch",
  "producerJob": "package-artifacts",
  "runId": "36547070332",
  "runAttempt": "1",
  "sourceCommit": "10d375c6942fc650cc64f64e0748add77540d47c",
  "artifactId": "11023576342",
  "archiveSha256": "sha256:27151d76524a22cd8126f5dc658295c16cf6d869930ccaf8a3f97d97e14be6fd"
}
```

The producer's post-upload download logged the same recomputed archive digest and completed successfully, then its typed content binding succeeded. The nonpublishing consumer independently logged the same downloaded digest and completed its preflight/replay steps. These are historical observations of the existing path. The new `artifact-transport` mode, direct REST verifier, refusal tests, safe extraction and success marker have **not** been implemented or executed.

## Three acceptance outcomes

These are required future results from the [approved test plan](issue-910-exact-artifact-verification-test-plan.md), not new passing-test claims. Fixtures must execute the real adapter and content checks through the approved internal seams; they do not acquire live producer authority by copying this record.

| Case | Inputs and controlled change | Required observable result | Observed for new mode? |
|---|---|---|---|
| Matching same-attempt evidence | Retained identity above, expected attempt `1`, matching receipt/API/archive/subject fixtures. The reference archive has 581,350,422 bytes. | Exit `0`; verified bundle, final report-directory `verified-artifact.json`, and downstream preflight consumes those exact bytes. `releaseEligible` stays `false`. | No; implementation deferred |
| Earlier producer receipt | Model a later current attempt **`2`** with the retained attempt-`1` receipt. Stop at the pre-HTTP receipt comparison; no attempt-2 API response or historical run is invented. | Exit `1`, `artifact-attempt-mismatch`, **zero HTTP calls**, no success marker or eligible bundle. | No; implementation deferred |
| One changed archive byte | Keep expected attempt `1`, all identity and both expected digests unchanged; flip exactly one byte in a local archive fixture without repacking. | Exit `1`, `artifact-bytes-digest-mismatch` **before extraction**, no success marker or eligible bundle. | No; implementation deferred |

Attempt 1 has no earlier positive attempt number. Do not use `0` as an “old attempt”: it is noncanonical input and would test another refusal. The second row is a simulated partial-rerun boundary, not a claim that attempt 2 occurred. If a test specifically changes *only the receipt attempt* from a matching baseline, build a separately labeled controlled attempt-2 fixture (expected context, receipt, attempt response and subject all `2`), then change only its receipt to `1`; keep its synthetic status explicit. The retained historical reference remains attempt 1. No cross-attempt recovery is authorized.

Value: `protects=trusted exact identity and pre-extraction bytes; fails_when=old attempt or altered bytes become eligible; no_claim=this planning card is not execution of the new verifier, a real future receipt, hosted GITHUB_TOKEN proof, or an explanation of Skoolit's historical incident`.

## Responsible maintainer and remaining authorization

Andrew Krock (`@akrock`) is responsible for authorizing the first future protected-main, manual **nonpublishing** hosted trial after implementation is separately authorized, reviewed and merged. Andrew owns stopping on a refusal, retaining safe reports, reconciling partial owned output, and deciding whether to revert the consumer wiring. No automatic rerun, credential expansion, candidate substitution, repacking under an old identity, publishing, or stable/prerelease enforcement follows from this assignment.

The card/owner assignment from the approved design is complete. The user subsequently authorized implementation and a draft PR on 2026-11-11. After implementation/merge, separately authorized hosted acceptance must prove the new REST token, streamed ZIP/digest, resource fit, safe outputs and downstream verified-byte consumption. Keep #910 open until that required evidence exists.

## Retention and evidence limits

Fresh exact-ID metadata captured on 2026-11-10 reports `expired: false`, `size_in_bytes: 581350422`, and expiry `2026-12-28T09:07:08Z` (04:07:08 America/New_York). The snapshot records the precise UTC collection times. This is an observed retention state, not a promise that evidence cannot be deleted or will remain accessible. The small sanitized source/log/API snapshot is retained with this card; the 581 MB producer ZIP and raw logs were not copied into the repository.

No product code, workflow definition, test implementation, issue, PR, publication or deployment was changed to collect this card. Only existing source, job logs and exact run/artifact metadata were read. No artifact archive was downloaded or rehashed in this task; the equal download digests are historical log observations. Current direct API reads use the operator's account, not a hosted rehearsal `GITHUB_TOKEN`. The archived evidence establishes the planning inputs and ownership while preserving all remaining implementation and hosted verification requirements.
