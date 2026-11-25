Execution update (2026-11-11): the user authorized `/make-it-so` implementation and a draft PR using this approved plan and the completed card. Merge and hosted rehearsal dispatch remain prohibited. Review-time planning-only statements below describe the earlier approval; they do not supersede the current implementation authorization. Keep #910 open pending separately authorized hosted acceptance.

## URGENT, outside this plan

None.

<!-- Historical /autoplan restore point: local planning scratch, issue910-20261107/restore.md. -->
## Implementation plan
# Plan: #910 exact-ID artifact verification for Tailwind rehearsal

Status: APPROVED on 2026-11-10 by the user (`rec`, gate revision 2). Approval scope: planning only; implementation remains deferred.
Issue: https://github.com/forge-trust/AppSurface/issues/910
Approved design: [exact-ID verification](../designs/issue-910-exact-artifact-verification.md), approved by the user on 2026-11-07, original approved-snapshot SHA-256 `cf11d7754fd457b1524f186607b58a34620c06d818ecfbbfa8e52adc37873be2`. The design now also records completion of its card/owner assignment on 2026-11-10.
Planning base: `ed93a55aad1bf86ea0cae47cd8ddd4ae9fb3e9c9` on `main`; implementation must start from an isolated current-main worktree after separate authorization.

## Outcome and authority

Verify and consume one current-attempt producer bundle by exact GitHub Actions artifact ID in the existing protected, manual, nonpublishing Tailwind rehearsal. Preserve the selected internal verifier plus thin Tailwind adapter architecture. The issue contract and the approved design below are binding, including all diagnostic categories, budgets and tests. The three owner clarifications take precedence over the historical review excerpts: human estimate 2.5–3 engineer-days; every attempt conflict uses `artifact-attempt-mismatch`; any `Retry-After` header on HTTP 403 is a rate-limit signal.

This review authorizes planning artifacts only. It does not authorize source changes, workflow dispatch, publication, a PR, or issue-body edits. No current code or hosted acceptance has been tested by writing this plan. The implementation entry gate requires a named maintainer and a sanitized acceptance card from one retained protected rehearsal, recording exact artifact/run/attempt/source/upload-digest values and the protected source of each. If retained evidence has expired or lacks upload-output authority, record that limit and keep implementation authorization pending rather than substituting a public artifact or inventing authority.

**Entry assignment completed 2026-11-10:** the [protected acceptance card](issue-910-protected-acceptance-card.md) binds retained run `36547070332`, attempt `1`, artifact `11023576342`, source and upload digest to the protected workflow and exact upload/validation step logs. The user assigned Andrew Krock ([`@akrock`](https://github.com/akrock)) as responsible maintainer. The linked sanitized evidence includes fresh exact-ID retention metadata. This completes the card/owner assignment; implementation remains explicitly deferred, and the new-mode hosted trial is still future acceptance work. Historical review-time pending statements below are preserved as history.

## Execution sequence after separate authorization

### 1. Capture contracts and existing seams

Use the canonical receipt and command contracts reproduced below. Add internal immutable expected-context, parsed-receipt, verified-archive and typed-failure records in `GitHubActionsArtifactVerifier.cs`, beside a concrete internal verifier. Keep the API independent of Tailwind and environment discovery. The caller supplies GitHub.com identity, credentials, fixed producer policy, cancellation and owned scratch location. Inject `HttpMessageHandler` and `TimeProvider` through intentionally testable internal constructors; reuse the existing test assembly's internal visibility. Do not introduce a service container, generic plugin framework or package dependency.

Use `TailwindEvidenceWorkflow.ParseStrict`, bounded hashing and create-new report writing where semantics match. Its private path and exact-field helpers are not assumed public: expose the smallest internal shared helper only if the adapter needs identical behavior; retain tests for old consumers. Inventory `TailwindProofSubjectService` and `TailwindPublicationRequest` before extending options so the subject digest is never confused with the ZIP digest. New schema/parser failures must become transport codes, not generic unclassified exceptions. Keep full decimal strings without floating-point or lossy intermediate conversion, including API numeric tokens.

### 2. Implement direct identity and HTTP verification

Before sending any HTTP, validate inputs, receipt schema, source-stamped checkout and fresh output-path ownership. Compare receipt against independent consumer context; current-attempt disagreement fails before any handler invocation. Construct only the direct artifact, exact-attempt and artifact-ZIP paths from validated IDs and repository. Do not accept a receipt URL or follow metadata's supplied download URL. Check API fields in the issue's defined order and tolerate additive fields only on API responses.

Use a single overall deadline and explicit per-response header and stream-inactivity timeouts with controlled clock tests. Disable automatic redirects and retries. Count every sent request against the shared six-request budget; the first three are artifact metadata, exact-attempt metadata and ZIP. Permit at most three HTTPS archive redirect follow-ups, without Authorization or other token-bearing headers. Read bounded error JSON only to classify the known primary/secondary limit message; never serialize it. Apply the approved 403 predicate before generic permission errors. Distinguish caller cancellation, elapsed budget, malformed metadata, API failure, redirect failure and redirected-storage transfer failure.

Stream the actual ZIP bytes into an invocation-owned create-new temporary file, incrementally count and hash them, and require equality with both the receipt and already-validated API digest before opening the ZIP. Reject unsupported digest formats. Dispose response, stream and temporary handles on all exits; do not buffer an entire archive. Leave an explicit state transition between verified archive and extraction so tests can prove a byte mismatch never reaches extraction.

### 3. Implement the Tailwind adapter and transactional outputs

Add `TailwindArtifactTransportWorkflow` and `artifact-transport` dispatch in the existing `verify-tailwind-evidence` command. Add only the required mode inputs to `TailwindCommandOptions`; update help and option/mode validation. Read `GH_TOKEN` once in the adapter, pass it explicitly into the core and never include it in records or logs. Preserve all existing mode defaults and source-stamp validation.

Resolve work/report/bundle paths against `--repo-root`; require fresh, disjoint owned directories, disjoint from concrete receipt/payload input files and their containing evidence directories (including ancestor/descendant overlaps); --repo-root is the resolution/source context, not itself an evidence-input directory, so a fresh dedicated output below the checkout is allowed if it cannot overlap those concrete inputs, with no link/reparse ancestors. Never delete a pre-existing path. Use an owned staging directory on the same volume as the final bundle. Validate entry names and types and actual expanded byte counts before installation; reject traversal, rooted/backslash names, links, duplicates, case collisions, file/directory collisions, corruption and unsupported entries. Check cancellation/deadline during extraction and existing binding validation as well as HTTP.

Reuse `TailwindEvidenceWorkflow.ValidateProducerBindingAsync` for subject, manifest and package checks. Require the validated subject attempt separately, classifying its conflict as `artifact-attempt-mismatch`; preserve safe underlying producer diagnostics. Move the verified staging directory with `Directory.Move`; no cross-volume copy fallback. Write diagnostics and summary, then atomically install `verified-artifact.json` last. The caller must require exit 0 and the final record before consuming output. Failures remove only owned work/staging/installed paths best effort and never leave a success marker; cleanup/report failure must not replace the first typed failure. A crash can leave a directory or no report; neither is eligible without exit 0 and marker.

### 4. Wire one protected rehearsal caller

In `.github/workflows/package-artifacts.yml`, emit the receipt from successful upload outputs and protected GitHub context after the existing producer-binding step, under the current manual/main/own-repository guard. Export one new named receipt job output through `$GITHUB_OUTPUT`; preserve the existing producer_artifact_id job output sourced directly from upload-package-artifacts.outputs.artifact-id. Pass that existing needs.package-artifacts.outputs.producer_artifact_id independently as --producer-artifact-id and compare it with the receipt; this is same-producer consistency, not independent producer authentication. Missing/malformed upload ID or digest means no receipt. Keep the producer's existing upload/download/content-validation behavior and outputs. Materialize the consumer's receipt exclusively from `needs.package-artifacts.outputs`, using environment variables and quoted arguments rather than interpolating expressions into shell.

Require successful `needs.package-artifacts.result` explicitly. Preserve the consumer's `actions: read`, `contents: read`, protected checkout and existing native/aggregate guards. Move the existing locked restore and source-stamped build ahead of producer consumption, then replace only the consumer's producer download with `artifact-transport`. Pass the independent current `github.run_attempt`, not an attempt copied out of the receipt. Use the same verified bundle path in all existing publication-preflight, original-receipt, replay and recording-publisher checks; do not download a replacement producer after verification. Keep aggregate/start-receipt/native downloads and stable/prerelease resolution behavior. Retain only safe transport reports through an `always()` upload.

### 5. Add branch verification and maintainers' documentation

Add deterministic `GitHubActionsArtifactVerifierTests` for strict receipt parsing, each identity field, direct endpoint sequence, every HTTP classification, redirect isolation, all stream/time/request bounds and ZIP digest equality. Add `TailwindArtifactTransportTests` for dispatch/options/source stamp, safe extraction, current-subject attempt, owned outputs, cancellation/report failure and success-record lifecycle. Use fake handlers and clocks plus real temporary ZIPs; no reflection. Parameterize each specified refusal variant rather than merely checking any failure. Each test carries `protects`, `fails_when`, `no_claim` beside it.

Extend `.github/tests/tailwind_workflow_transport_tests.py` to execute the actual receipt run block with controlled upload/context inputs, including large IDs, bad/missing outputs and malicious expression-like values. Assert successful-producer dependency, permissions/guards, current-attempt propagation, source-stamped build order, new command success gating and verified-byte consumption, unchanged publishing enforcement and `always()` safe-report handling. Follow the existing YAML-run-block test pattern; no receipt script solely for testability.

Update `docs/tailwind-artifact-provenance.md` as canonical reference for receipt, command, trust, bounds, all codes, output eligibility, unsigned-producer trust and partial-rerun behavior. Update PackageIndex README discovery/usage and `.github/release-ops.md` with the exact recovery instruction: “re-run all jobs so the producer emits a receipt for the current attempt.” Provide copy-paste complete credential-free fixture instructions, explain that live GH_TOKEN is workflow-supplied, and link the canonical reference from first meaningful mentions. Document internal contract shapes and constraints with XML comments and reference content.

### 6. Validate, review and retain hosted acceptance as a separate gate

Run PackageIndex tests, `python3 .github/tests/tailwind_workflow_transport_tests.py`, repository formatting, relevant analyzers and documentation checks. Aim for nearly 100% changed-branch coverage; run `./scripts/coverage-solution.sh` when practical and report observed coverage or its precise execution limit. Re-run existing Tailwind evidence/publishing tests to catch reused-helper regression. This plan run executes no implementation tests.

After merge and separate hosted authorization, run one protected-main manual nonpublishing rehearsal and retain genuine receipt, safe transport reports, verified digest/byte count and downstream consumption evidence. Compare the three acceptance-card outcomes without fabricating a hosted old-attempt or byte-mutation run; those negative cases can remain fixtures. Record actual hosted token, archive digest and resource observations separately. Keep #910 open until required hosted acceptance is complete. Roll back the new consumer wiring if necessary; no alternative candidate or repacking under the same identity is recovery.

## Expected change footprint and effort

Eleven primary files: two new implementation files, two new test files, existing command/options files, one workflow, one workflow test suite and three docs. A minimal shared-helper visibility change in `TailwindEvidenceWorkflow.cs` is allowed only if needed for exact reuse; this makes the upper estimate twelve files. The existing #798 validator remains the content authority. Estimated human effort 2.5–3 engineer-days; agent-assisted 5–8 focused hours, excluding hosted-run availability and review turnaround. These are planning estimates, not measured performance gains.

## Deferred work

Stable/prerelease enforcement, cross-attempt recovery, public packages, generic plugin/CLI frameworks, Skoolit adoption before #1441 settles, signatures/attestations, broad release resolver rewrites and a permanent shadow-probe command remain outside this plan. Preserve the uncertainty about the historical Skoolit incident. No new telemetry, dashboard service or scheduling is required: safe retained reports and a maintainer runbook are the scoped operational interface.

## Required acceptance card

The [completed protected card](issue-910-protected-acceptance-card.md) is the concrete evidence/owner record. Its normalized historical receipt is a planning transcription, not a workflow-emitted v1 receipt. The attempt-1 reference uses a clearly labeled simulated current attempt `2` for the old-receipt negative case; it never claims an actual attempt 2 or uses invalid attempt `0`. The underlying same-attempt refusal contract is unchanged.

Matching current-attempt receipt/API/archive/subject yields exit 0, a verified bundle and the final success record; downstream preflight consumes those bytes. Mutating only receipt attempt yields `artifact-attempt-mismatch`, exit 1 and zero HTTP. Mutating one archive byte while retaining digests yields `artifact-bytes-digest-mismatch` before extraction and no eligible bundle. The complete refusal table and coverage variants below are required alongside this card.


## Binding issue contract, captured for implementation

## Context

Release maintainers need to verify that a supplied GitHub Actions artifact is the exact bundle produced by the intended repository, source commit, workflow run, and attempt. Refusals must identify the failed check so operators can distinguish an identity mismatch from expired evidence, denied access, or a failed download.

There are two relevant callers: AppSurface’s [Tailwind artifact resolver](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/.github/scripts/resolve-tailwind-release-artifacts.sh) and Skoolit’s [temporal-proof reader](https://github.com/forge-trust/skoolit/issues/1446). **V1 covers one producer bundle in AppSurface’s nonpublishing Tailwind rehearsal, using the current run and attempt.** Stable/prerelease enforcement, cross-attempt recovery, and Skoolit adoption are deferred.

This is a verification capability. Skoolit’s historical failure cause remains unknown, and this issue does not claim to repair it.

## Current State

Verified November 7, 2026 against AppSurface commit `adeb7903b550a42de48ae5260ff31fccd9de0b0b`.

| Component | Existing behavior | V1 treatment |
|---|---|---|
| [Tailwind resolver](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/.github/scripts/resolve-tailwind-release-artifacts.sh#L67-L89) | Lists run artifacts, resolves names to IDs, checks uniqueness and expiry. | Preserve its release and recovery behavior. |
| [Package producer](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/.github/workflows/package-artifacts.yml#L139-L185) | Uploads a bundle, passes its exact ID to the download action, and revalidates its Tailwind subject. | Emit a workflow-bound transport receipt from successful upload outputs. |
| [Nonpublishing publisher rehearsal](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/.github/workflows/package-artifacts.yml#L202-L263) | Downloads the producer by ID before validating the aggregate and preparing publication inputs. | Verify and download the producer directly by ID; consume those verified bytes. |
| [Tailwind evidence verifier](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/tools/ForgeTrust.AppSurface.PackageIndex/TailwindEvidenceWorkflow.cs#L35-L75) | Validates source checkout, subject, manifest, and package bindings. | Reuse these checks after transport verification. |
| Skoolit reader | One error code covers several discovery failures; historical failing predicates are unestablished. | Revisit after [PR #1441](https://github.com/forge-trust/skoolit/pull/1441) settles. It was open for review when checked. |

GitHub’s [artifact response](https://docs.github.com/en/rest/actions/artifacts#get-an-artifact) exposes artifact/run/repository/source/digest metadata, but does not expose the producing attempt. The receipt supplies that binding through trusted workflow outputs. The [attempt-specific API](https://docs.github.com/en/rest/actions/workflow-runs#get-a-workflow-run-attempt) corroborates the named attempt’s identity; it does not independently associate an artifact with that attempt.

The pinned [`download-artifact` action](https://github.com/actions/download-artifact/blob/v8.0.1/src/download-artifact.ts#L102-L133) lists artifacts before filtering supplied IDs. V1’s verifier uses direct artifact endpoints.

No AppSurface incident rate or latency improvement has been established. Success is measured by the acceptance and refusal behaviors below.

## Proposed Change

Add an internal `GitHubActionsArtifactVerifier` to the existing, non-packable [PackageIndex maintainer tool](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/tools/ForgeTrust.AppSurface.PackageIndex/README.md#tailwind-artifact-provenance-798).

Expose its first adapter as:

```text
verify-tailwind-evidence --mode artifact-transport
```

The adapter verifies the producer archive, safely extracts it, and runs the existing Tailwind producer-binding checks. The nonpublishing publisher rehearsal then consumes that extracted bundle.

The reusable internal verifier handles GitHub artifact identity and transport. Tailwind-specific extraction and subject validation remain in the adapter. V1 adds no public NuGet API or dependency.

### Trusted producer receipt

The protected `package-artifacts` job emits this versioned JSON record as a job output after successful upload and existing producer-binding validation:

```json
{
  "schema": "appsurface-github-actions-producer-receipt-v1",
  "repository": "forge-trust/AppSurface",
  "repositoryId": "123456",
  "workflowPath": ".github/workflows/package-artifacts.yml",
  "event": "workflow_dispatch",
  "producerJob": "package-artifacts",
  "runId": "123456789",
  "runAttempt": "1",
  "sourceCommit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "artifactId": "987654321",
  "archiveSha256": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
}
```

Values above illustrate the format.

Contract:

- Repository, workflow, run, attempt, and source come from protected GitHub context.
- Artifact ID and archive digest come directly from the successful upload action’s outputs. Normalize its lowercase hexadecimal digest to the `sha256:<64 lowercase hex>` form when constructing the receipt.
- Missing or malformed upload outputs prevent receipt emission.
- IDs are positive canonical decimal strings, with no leading zeros. Source commits are full lowercase 40-character hexadecimal SHAs.
- Reject unsupported schemas, duplicate properties, missing/extra fields, and incorrect types.
- Receipt authority comes from the named successful producer’s `needs` outputs. The JSON is unsigned. Receipt contents inside the downloaded artifact, operator-supplied URLs, artifact names, timestamps, and API metadata cannot establish this authority.
- The archive digest is separate from the existing protected Tailwind subject digest.

The consumer materializes only the named producer output into its receipt file. Receipt and expected values travel through environment variables and quoted arguments, following the existing workflow conventions.

### Command contract

Required inputs:

```text
verify-tailwind-evidence --mode artifact-transport
  --repo-root <source checkout>
  --repository <owner/repository>
  --repository-id <expected numeric repository ID>
  --producer-run-id <current run ID>
  --producer-attempt <current attempt>
  --source-commit <expected source SHA>
  --producer-artifact-id <exact artifact ID>
  --producer-receipt <trusted receipt JSON file>
  --expected-subject-sha256 <existing protected subject hash>
  --work-directory <fresh scratch directory>
  --artifacts-output <new verified bundle directory>
  --report-directory <fresh report directory>
```

Authentication uses `GH_TOKEN` supplied from the rehearsal’s existing read-only `GITHUB_TOKEN`. V1 supports GitHub.com. API host, workflow path, event, and producer-job policy are fixed to the rehearsal contract.

Exit codes follow the existing tool convention: `0` succeeds, `1` refuses or fails. Every refusal has a stable diagnostic code; callers read JSON rather than parsing stdout.

### Verification sequence

Fail at the first failed stage:

1. Validate arguments, receipt schema, output-path ownership, and source checkout identity.
2. Compare receipt identity with independently supplied expected repository, artifact, source, run, and **current attempt**. Refuse earlier-attempt receipts before network access.
3. Fetch `GET /repos/{repository}/actions/artifacts/{artifactId}` directly. Verify returned artifact ID, run ID, repository ID, head-repository ID, source SHA, expiry, and SHA-256 digest.
4. Fetch `GET /repos/{repository}/actions/runs/{runId}/attempts/{attempt}`. Verify run ID, attempt, repository/head-repository IDs, source SHA, workflow path, and `workflow_dispatch` event.
5. Download `GET /repos/{repository}/actions/artifacts/{artifactId}/zip`. Hash the streamed archive bytes and require equality with both the trusted receipt and artifact metadata.
6. Safely extract those verified bytes into an owned staging directory.
7. Reuse the existing Tailwind producer-binding validator. Additionally require the validated subject’s `producerAttempt` to equal the expected current attempt.
8. Install the verified bundle and write the success record last. Subsequent rehearsal steps consume this bundle.

The workflow requires `needs.package-artifacts.result == 'success'`. Whole-run success is not required: the consumer is executing within that run.

The direct verifier performs no artifact listing, name lookup, latest selection, fallback selection, or automatic retry. Artifact metadata’s missing attempt field must remain explicit in the documentation and tests.

### Transport and archive limits

Reuse the existing [Tailwind limits and safety conventions](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/docs/tailwind-artifact-provenance.md#wire-records-and-validation):

- 16 MiB per JSON document; maximum JSON depth 64.
- 1 GiB downloaded archive; 100,000 ZIP entries; 4 GiB total expanded bytes.
- Five-minute deadline for the new verification mode.
- Thirty-second response-header and stream-inactivity limits.
- At most six HTTP requests, including at most three archive redirect hops.

Count actual streamed/expanded bytes, even when declared lengths are absent or incorrect.

Use manual redirect handling. Only authenticated API requests receive the token. Archive redirects require HTTPS and omit authorization; signed URLs and their query strings never enter reports or logs.

Extraction rejects traversal, rooted/backslash paths, links, duplicate or case-colliding names, file/directory collisions, and unsupported/corrupt archives. Output directories must be new, disjoint from inputs and reports, and free of link/reparse-point ancestors. Reuse appropriate existing validation helpers.

### Diagnostics and output lifecycle

Reuse `diagnostics.json`, `summary.md`, and the existing Tailwind diagnostic record shape, with:

- `stage: "artifact-transport"`
- `status: succeeded|failed|cancelled`
- `releaseEligible: false`
- Stable error code, safe expected/observed values, next action, and canonical documentation link.

Required refusal categories:

| Code | Condition |
|---|---|
| `artifact-input-invalid` | Invalid caller arguments or missing required authority. |
| `artifact-receipt-invalid` | Invalid receipt schema, properties, or types. |
| `artifact-receipt-binding-mismatch` | Receipt conflicts with expected identity; identify the field. |
| `artifact-attempt-mismatch` | Receipt, attempt response, or validated subject conflicts with the expected attempt. |
| `artifact-repository-mismatch` | Artifact/run repository or head-repository differs. |
| `artifact-run-mismatch` | Returned run identity differs. |
| `artifact-workflow-mismatch` | Workflow path or event differs. |
| `artifact-source-mismatch` | Source identity differs. |
| `artifact-not-found-or-inaccessible` | HTTP 404; preserve its ambiguity. |
| `artifact-expired` | Expired metadata or HTTP 410. |
| `artifact-permission-denied` | Authentication/authorization refusal. |
| `artifact-rate-limited` | Explicit rate-limit response. |
| `artifact-api-unavailable` | Network or server failure. |
| `artifact-api-response-invalid` | Malformed response or missing/invalid required metadata, including unsupported digest format. |
| `artifact-metadata-digest-mismatch` | API digest differs from the receipt. |
| `artifact-bytes-digest-mismatch` | Downloaded bytes differ from the expected digest. |
| `artifact-download-failed` | Archive transfer fails or is incomplete. |
| `artifact-redirect-invalid` | Invalid redirect status, destination, or hop count. |
| `artifact-budget-exceeded` | A time, request, document, archive, entry, or expansion limit is exceeded; identify the limit. |
| `artifact-archive-unsafe` | Archive is corrupt or violates extraction rules. |
| `artifact-producer-binding-invalid` | Existing Tailwind content verification fails; retain its safe underlying diagnostic. |
| `artifact-output-failed` | Required output cannot be created or installed. |
| `artifact-cancelled` | Caller cancellation. |

Use fixed safe messages and allowlisted values. Preserve HTTP status and stage where useful; omit tokens, headers, raw response bodies, signed URLs, and unrelated environment data. Diagnostic writing is best effort and preserves the original failure.

Success writes `verified-artifact.json` with schema `appsurface-github-actions-verified-artifact-v1`, all receipt identity fields, the verified archive digest, `archiveBytes`, `status: "succeeded"`, and `releaseEligible: false`.

Failure or handled cancellation leaves no success marker or usable output bundle. Cleanup touches only invocation-owned paths. Pre-existing paths are refused rather than deleted. Process termination before diagnostics can be written remains an explicit observability limit.

### Workflow integration

Modify only the rehearsal path in `package-artifacts.yml`:

1. Emit the producer receipt under the existing protected manual-rehearsal guard.
2. In `tailwind-publish-rehearsal`, restore/build the source-stamped verifier before producer consumption.
3. Replace that job’s producer download with the new mode.
4. Keep existing aggregate, publication-preflight, receipt-replay, and recording-publisher checks.
5. Upload only the new safe reports with `if: always()`.

Keep the existing manual/main/repository guards and read-only permissions.

## Acceptance Criteria

1. A valid same-attempt receipt and matching API/archive/subject evidence produce the verified bundle and success record.
2. Earlier-attempt receipts fail before network access, including partial reruns that retain earlier producer outputs.
3. The verifier uses direct artifact endpoints and never calls an artifact-list endpoint.
4. Every identity, expiry, digest, access, transport, and budget failure above produces its specified code and no success output.
5. The downloaded archive SHA-256 is recomputed before extraction; subject hashing remains an independent check.
6. The rehearsal consumes the verified bundle bytes, without downloading a replacement producer afterward.
7. Existing Tailwind package, aggregate, native-host, and publication checks continue to run.
8. New tests cover the changed branches through supported seams, without reflection. Each test states `protects`, `fails_when`, and `no_claim`.
9. Formatting, relevant tests, analyzers, and documentation checks pass without newly introduced warnings.
10. The implementing report distinguishes fixture validation, hosted validation, and remaining verification limits.

## Testing Plan

Use injected `HttpMessageHandler` and `TimeProvider` seams for deterministic transport and deadline tests.

| Layer | Required coverage |
|---|---|
| Unit | Strict receipt parsing; every identity field; missing upload outputs; large IDs; digest formats; current-attempt checks. |
| HTTP integration fixtures | Valid direct lookup/download; mismatched metadata/attempt; 401/403/404/410/429/5xx; malformed JSON; redirect handling; token isolation; no retries/list calls. |
| Stream/archive integration | Digest mutations; truncation; byte/time/request bounds; unsafe ZIP variants; staged output cleanup; cancellation. |
| CLI | Real command dispatch, required options, source binding, exit status, typed diagnostics, success-marker lifecycle. |
| Workflow | Execute receipt construction with fixture outputs; assert successful-producer dependency, current-context binding, verified-bundle consumption, report retention, and unchanged publishing enforcement. |
| Hosted | One authorized protected-main nonpublishing rehearsal after merge, retaining the receipt and safe verification reports under `contents: read` and `actions: read`. |

Every listed variant requires its own parameterized case or test. Example test cards:

- Receipt/identity tests: `protects=exact trusted identity; fails_when=a conflicting identity is accepted; no_claim=fixtures do not authenticate real workflow-output provenance`.
- Transport tests: `protects=verified archive bytes and credential isolation; fails_when=wrong bytes pass or credentials reach redirected storage; no_claim=mock HTTP does not establish hosted token permissions`.
- Workflow tests: `protects=rehearsal-only enforcement and verified-bundle consumption; fails_when=the consumer bypasses verification or publishing policy changes; no_claim=source/fixture checks do not execute a hosted rehearsal`.
- Hosted rehearsal: `protects=the real read-only producer-to-consumer path; fails_when=the expected candidate cannot be verified or consumed; no_claim=one run does not reproduce Skoolit’s historical failure`.

Run the PackageIndex test project and:

```text
python3 .github/tests/tailwind_workflow_transport_tests.py
```

Run `./scripts/coverage-solution.sh` when practical and report its observed result or explicit execution limit. Hosted rehearsal validation remains pending until its authorized run; preserve the main-branch guard.

## Files Reference

Paths are relative to the AppSurface repository root.

| File | Change |
|---|---|
| `tools/ForgeTrust.AppSurface.PackageIndex/GitHubActionsArtifactVerifier.cs` | New internal contracts, direct REST/download verification, budgets, and typed failures. |
| `tools/ForgeTrust.AppSurface.PackageIndex/TailwindArtifactTransportWorkflow.cs` | New rehearsal adapter, extraction, existing producer validation, and output lifecycle. |
| `tools/ForgeTrust.AppSurface.PackageIndex/Program.cs` | Dispatch and command help. |
| `tools/ForgeTrust.AppSurface.PackageIndex/TailwindCommandOptions.cs` | Parse the new mode’s receipt/repository inputs. |
| `tools/ForgeTrust.AppSurface.PackageIndex.Tests/GitHubActionsArtifactVerifierTests.cs` | New deterministic identity and transport tests. |
| `tools/ForgeTrust.AppSurface.PackageIndex.Tests/TailwindArtifactTransportTests.cs` | New adapter, CLI, extraction, and cleanup tests. |
| `.github/workflows/package-artifacts.yml` | Protected receipt output and rehearsal consumer integration. |
| `.github/tests/tailwind_workflow_transport_tests.py` | Executable receipt and workflow regression coverage. |
| `docs/tailwind-artifact-provenance.md` | Canonical trust, schema, command, diagnostic, and rerun reference. |
| `tools/ForgeTrust.AppSurface.PackageIndex/README.md` | Maintainer entry point and usage links. |
| `.github/release-ops.md` | Rehearsal operation, refusal handling, and deferred recovery guidance. |

## Rollback Plan

Revert the new rehearsal integration to restore its prior producer-download path. The new internal mode can remain unused or be reverted with it. The receipt is additive; this change performs no data migration or publication.

During a failed rollout, stop the rehearsal and retain diagnostics. Refusal does not authorize substituting another artifact or repacking under an existing identity.

## Effort Estimate

Approximately **2.5–3 engineer-days**:

- Direct-ID contracts, transport, and diagnostics: 1 day.
- Tailwind adapter and workflow integration: 0.5 day.
- Tests and branch verification: 0.75–1 day.
- Documentation and review corrections: 0.25–0.5 day.

Hosted rehearsal time depends on the existing package/native workflow and is not estimated as an improvement.

## Out of Scope

- Stable/prerelease enforcement or changes to their recovery policies.
- Cross-attempt reuse, recovery, stabilization, or fallback.
- Skoolit changes or claims about its historical root cause.
- New verification of native-host, aggregate, or publication-start transport.
- Portable signed receipts, attestations, or verification of a compromised authorized producer.
- Public package APIs, GitHub Enterprise support, credential changes, publishing, or deployments.

## Rules for the Implementing Agent

1. Deliver real implementation and regression tests together.
2. Preserve the trust boundaries and approved scope.
3. Report each test, validator, timeout, threshold, snapshot, or suppression change with its justification.
4. Report commands with observed results; distinguish verification performed, evidence reused, and checks still pending.
5. Keep the issue open while required hosted acceptance remains pending.

## Related

- [AppSurface #798 — Tailwind artifact provenance](https://github.com/forge-trust/AppSurface/issues/798).
- [Skoolit #1446 — opaque intermittent temporal-proof refusal](https://github.com/forge-trust/skoolit/issues/1446).
- [Skoolit #1441 — immutable artifact acceptance and recovery](https://github.com/forge-trust/skoolit/pull/1441).


## Approved design obligations, captured for implementation



Release maintainers need one predictable answer to a narrow question: does this exact GitHub Actions artifact match the protected producer receipt for this repository, source, run and attempt, and do its downloaded bytes match the expected archive digest?

[Issue #910](https://github.com/forge-trust/AppSurface/issues/910) is the canonical receipt schema, command, resource-limit and refusal-code specification. This design selects its implementation boundary and resolves operational details. V1 verifies one producer bundle before the existing nonpublishing Tailwind publisher rehearsal consumes it. Skoolit's [historical temporal-proof failure](https://github.com/forge-trust/skoolit/issues/1446) remains unexplained; revisit adoption after [#1441](https://github.com/forge-trust/skoolit/pull/1441) settles.

The immediate useful result is an actionable refusal such as “producer receipt attempt 2; current rehearsal attempt 3,” or an archive digest mismatch caught before extraction. A later contributor could turn a safe refusal record into a reproducible fixture without deployment credentials. General replay tooling is future work.

## Confirmed Premises and Boundaries

1. Retrieve and verify this one producer bundle directly by ID, with specific refusal reasons. Preserve the uncertainty about Skoolit's historical cause.
2. Attempt ownership comes from protected producer outputs. The API corroborates the named run and attempt but cannot independently associate an artifact with that attempt.
3. Use the current nonpublishing rehearsal as the integration trial. Public packaging, cross-attempt recovery, stable/prerelease enforcement and Skoolit adoption remain deferred.

The user confirmed all three premises and selected **B: internal transport verifier plus Tailwind adapter**. Existing Tailwind package, subject, manifest, native-host, aggregate and publication validation remains required. A successful transport record always has `releaseEligible: false`.

## Existing Evidence and Independent Perspective

At the design base, the [rehearsal consumer](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/.github/workflows/package-artifacts.yml#L202-L263) downloads the producer by ID, then runs the existing typed publication preflight. The [pinned download action](https://github.com/actions/download-artifact/blob/v8.0.1/src/download-artifact.ts#L102-L133) first lists artifacts and filters IDs; its default digest policy already rejects byte mismatches. #910 adds direct retrieval and receipt-bound diagnostics to this one consumption path. Other rehearsal downloads retain their existing behavior.

The independent Claude Code cold read completed with captured CLI exit 0. It retained all agreed premises and recommended the internal verifier/adapter split. It emphasized the shared authority of producer ID and digest, rate-limit classification, partial-rerun instructions and hosted evidence. Its High permission finding came from an incomplete excerpt: the actual consumer already declares [`actions: read` and `contents: read`](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/.github/workflows/package-artifacts.yml#L208-L210). Preserve those declarations and assert them in workflow tests; static declarations do not establish the actual hosted token's capabilities.

The review proposed a permanent shadow-probe mode. This design uses the existing manual, nonpublishing rehearsal for acceptance and adds no permanent probe command. No new generalized landscape search was performed; the evidence is repository code and the primary GitHub contracts referenced here and in #910.

## Chosen Architecture

**Internal `GitHubActionsArtifactVerifier`:** own strict receipt parsing, comparison with trusted expected context, direct artifact and exact-attempt requests, bounded archive download, digest verification and typed failures. Inputs carry expected repository/run/attempt/source/artifact identity and the protected receipt. Supply `HttpMessageHandler` and `TimeProvider` seams for deterministic tests. Return verified identity, digest, byte count and an owned temporary ZIP path. Keep the implementation concrete and internal to the existing non-packable [PackageIndex tool](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/tools/ForgeTrust.AppSurface.PackageIndex/README.md#tailwind-artifact-provenance-798).

**Thin `TailwindArtifactTransportWorkflow`:** implement `verify-tailwind-evidence --mode artifact-transport`; obtain the token from `GH_TOKEN`, validate source checkout/build identity, invoke the core, safely extract the verified ZIP, and reuse [producer-binding validation](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/tools/ForgeTrust.AppSurface.PackageIndex/TailwindEvidenceWorkflow.cs#L35-L75). Require the validated subject's producer attempt to equal the current expected attempt. Own bundle installation and the final reports.

The generic core receives explicit context and credentials through its caller; it does not discover authority from environment variables, artifact names or receipt URLs. The adapter supplies the fixed workflow/event/producer-job policy defined in #910. Both layers reuse existing strict JSON, hashing, path and report helpers where applicable. No public package, plugin system or new dependency is required.

Alternative A, a combined Tailwind HTTP/orchestration component, was rejected because it couples the transport tests and Tailwind semantics. B keeps the full same-attempt contract while placing the seam between different responsibilities.

## Authority and Acceptance Order

The protected producer creates the receipt only after successful upload and existing producer-binding validation. Repository/run/attempt/source comes from GitHub context; artifact ID and archive digest come from that upload's outputs. The consumer materializes only the named successful producer's `needs` output.

Receipt ID and digest share producer authority. Comparing them with another output from that producer establishes consistency. The independent consumer context supplies expected repository/run/current-attempt/source constraints. The unsigned receipt does not authenticate a compromised authorized producer.

Use this order:

1. Validate required arguments, receipt schema and output ownership, and require the exact source-stamped verifier checkout.
2. Match the receipt against expected context, including the current attempt. An earlier-attempt receipt fails before HTTP access.
3. Read the exact artifact endpoint and the exact attempt endpoint; verify the identity, workflow, event, source, expiry and digest fields specified by #910.
4. Download that exact ID's ZIP and hash actual streamed bytes against the protected digest and API digest before extraction.
5. Extract into owned staging, run existing Tailwind producer-binding checks, and compare the subject's attempt.
6. Move staging to the requested bundle output, then atomically install the success record last. The rehearsal consumes this bundle without a replacement producer download.

The producer job must have succeeded. The overall attempt may be `in_progress`; do not require whole-run success. Earlier producer outputs carried into a partial rerun are intentionally refused. The diagnostic's next action and release-operations guide must say **“re-run all jobs so the producer emits a receipt for the current attempt.”**

## Operational Decisions

| Area | Required behavior |
|---|---|
| Receipt and API JSON | Reuse the existing [duplicate-property check](https://github.com/forge-trust/AppSurface/blob/adeb7903b550a42de48ae5260ff31fccd9de0b0b/tools/ForgeTrust.AppSurface.PackageIndex/TailwindEvidenceWorkflow.cs#L795-L800). Receipt fields are exact/versioned; tolerate additive API fields while validating required fields and types. Compare canonical decimal identities without floating-point conversion. |
| HTTP setup | Use `https://api.github.com`, User-Agent `AppSurface-ArtifactVerifier`, Accept `application/vnd.github+json` and `X-GitHub-Api-Version: 2026-03-10`. Construct API paths from validated expected identity. |
| Credentials and redirects | Authenticate only the original GitHub API requests. Follow archive redirects manually over HTTPS, stripping Authorization at the storage boundary. Keep signed URLs, query strings, headers and raw bodies out of diagnostics. |
| Budgets | Retain #910's 16 MiB JSON/depth-64, 1 GiB ZIP, 100,000-entry, 4 GiB expanded, five-minute overall and 30-second header/inactivity limits. Count actual bytes. Six total requests cover two metadata calls, the ZIP request and up to three redirect follow-ups; there are no automatic retries or extra discovery requests. |
| HTTP refusals | Classify 429 as `artifact-rate-limited`. For 403, classify as `artifact-rate-limited` first when `x-ratelimit-remaining` is zero, any `Retry-After` header is present (regardless of its value), or the bounded GitHub error message explicitly identifies a primary/secondary limit. Other API 401/403 responses use `artifact-permission-denied`. Preserve 404 as `artifact-not-found-or-inaccessible`; 410 is `artifact-expired`; server/network failure is `artifact-api-unavailable`. A failed redirected storage transfer uses `artifact-download-failed`. Never serialize the inspected error body. |
| Code precedence | Invalid caller context is `artifact-input-invalid`; malformed receipt is `artifact-receipt-invalid`. An attempt-number conflict from the receipt, exact-attempt API response or validated subject uses `artifact-attempt-mismatch`. Other receipt identity conflicts use `artifact-receipt-binding-mismatch` with the field named. Other API identity conflicts use `artifact-repository-mismatch`, `artifact-run-mismatch`, `artifact-source-mismatch` or `artifact-workflow-mismatch`. Metadata digest disagreement uses `artifact-metadata-digest-mismatch`; downloaded byte disagreement uses `artifact-bytes-digest-mismatch`. |
| Reports | Write `diagnostics.json`, `summary.md` and the final `verified-artifact.json` in `--report-directory`. The bundle lives only in `--artifacts-output`; scratch ZIP/staging lives in `--work-directory`. Resolve relative paths against `--repo-root`; require fresh, disjoint owned paths without link ancestors. |
| Installation | Use a same-volume directory move from staging. A cross-volume move fails as `artifact-output-failed`; do not copy after validation. Unsupported/unsafe ZIP entries fail before publication of the bundle. |
| Failure/cancellation | Exit 1 for every refusal, operational failure or handled cancellation; success is 0. Publish no success marker for a failed invocation. Remove owned staged/installed output on failure as best effort; a remaining directory is ineligible for consumption without command success and the final marker. Cleanup/report writing does not replace the original typed failure. A crash before report writing can leave diagnostics absent. |

The HTTP classification follows GitHub's [documented rate-limit signals](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api#rate-limit-errors). Preserve the complete #910 refusal taxonomy and safe expected/observed/next-action fields. If the report path is invalid or unwritable, return the failure with a fixed safe stderr code; reporting failure never becomes success. The caller requires both command success and the final success record before consuming the bundle.

## Verification and Success Criteria

The three-case acceptance card is:

| Case | Observable result |
|---|---|
| Matching current-attempt receipt, metadata, ZIP and Tailwind subject | Exit 0; verified bundle and report-directory success marker; downstream preflight uses those bytes. |
| Change only receipt attempt to an earlier attempt | Exit 1 with `artifact-attempt-mismatch`; zero HTTP calls; no usable output. |
| Change one ZIP byte while retaining expected digests | Exit 1 with `artifact-bytes-digest-mismatch` before extraction; no usable output. |

Extend that card to every listed refusal branch using fake HTTP responses, controlled clocks, real temporary ZIPs and public/intentionally exposed internal seams. Cover 403 rate limits including secondary limits without a zero remaining header, 404 ambiguity, redirect credential isolation, malformed/duplicate JSON, time/byte/request limits, unsafe entries, cancellation, same-volume installation failures and report-write failures. No reflection in tests.

Include separate fixtures for an exact-attempt API response with a different attempt number and for a validated subject with a different producer attempt; both must return `artifact-attempt-mismatch`. For 403 classification, a response with any `Retry-After` header and no zero remaining header must return `artifact-rate-limited`, including empty or malformed header values. A 403 with no `Retry-After` header, no zero remaining header and no primary/secondary rate-limit message must return `artifact-permission-denied`.

Each test states `protects`, `fails_when` and `no_claim` in a nearby test comment. Workflow tests execute the relevant YAML run block with fixtures, following the existing transport-suite pattern; no separate receipt script is required solely for testing. They assert existing permissions and guards, receipt provenance, current-attempt propagation, verified-bundle consumption and safe `always()` reports.

The implementing change runs PackageIndex tests and `python3 .github/tests/tailwind_workflow_transport_tests.py`, formatting and relevant analyzers/documentation checks. Run solution coverage when practical and record the actual result or execution limit. Fixtures establish behavior under controlled inputs; they do not establish real hosted permissions, archive transport fidelity or the historical incident cause.

After merge, one separately authorized protected-main manual rehearsal must retain the genuine producer receipt and safe reports proving the REST token/ZIP/digest path and downstream consumption. Keep #910 open while that required hosted acceptance is pending. This design does not authorize dispatching it now.

## Distribution, Sequence and Effort

Distribution remains the repository-local PackageIndex executable, built from the exact checkout with locked dependencies and its source stamp. Existing CI runs the C# and Python test suites; the current manual package-artifacts workflow supplies hosted acceptance. Stable/prerelease workflows and the shared native-host workflow retain their enforcement behavior.

Implementation sequence, after separate authorization:

1. Add internal receipt/context/result contracts and deterministic identity/HTTP refusal tests.
2. Add bounded direct download, redirect isolation and archive hashing, then the Tailwind extraction/binding adapter and output lifecycle.
3. Emit the protected receipt and wire only the nonpublishing consumer; update the canonical reference, maintainer guide and release-operations instructions together.
4. Validate locally and in CI; retain the issue for the first authorized hosted acceptance run.

Planning estimate: human 2.5–3 engineer-days, aligned with issue #910, or agent-assisted 5–8 focused hours, excluding hosted-run availability, review turnaround and any unexpected archive compatibility issue. These are estimates, not measured speed claims. Reverting the rehearsal wiring restores its previous download path; retain failed-run reports, and never substitute another candidate as recovery.

## The Assignment

Before authorizing implementation, choose one retained protected Tailwind rehearsal as the source for a sanitized acceptance card: exact artifact ID, run ID, producer attempt, source SHA and upload digest, with the protected source of each recorded. Use the three approved expected outcomes above and name the maintainer who will authorize the first hosted run. The result is a concrete fixture authority and owner; no producer rerun, publication or deployment is needed for this assignment.



<!-- autoplan-accepted:ceo -->
- Preserve the existing producer_artifact_id output sourced directly from the successful upload; pass it separately from the receipt as --producer-artifact-id, compare them and test both disagreement and no independent-authority claim.
- Use exact artifact-prefixed diagnostic codes from the canonical refusal table in all precedence text, reports and parameterized test expectations; shorthand never defines another code.
- Treat --repo-root as source/resolution context rather than a concrete evidence input for output-overlap checks. Permit fresh dedicated checkout-relative output if disjoint from actual receipt/payload inputs and evidence directories, all output/work/report paths and their ancestor/descendant overlaps; keep no-link and no-preexisting-delete rules and add allowed/disallowed path fixtures.
- Complete the HTTP status partition: metadata endpoints require 200; known 401/403/404/410/429 and 5xx classifications take precedence, metadata redirects use artifact-redirect-invalid, and every other final metadata/API status (including unexpected 2xx and other 4xx) uses artifact-api-response-invalid. ZIP GET/follow-ups accept 200 bytes or 301/302/303/307/308 with a valid HTTPS Location inside hop/request limits; unsupported or malformed redirects use artifact-redirect-invalid. On the authenticated ZIP API request use the same known API failure classifications; any other non-success API status uses artifact-api-response-invalid. Once following storage, non-success/non-supported-redirect transfer statuses use artifact-download-failed. Cover each partition and boundary, with no retries.
- After content validation and installation, dispose and remove invocation-owned temporary ZIP and scratch staging before publishing the final success marker. Do not retain ZIPs on success or upload them as reports. A required scratch-removal failure before success becomes artifact-output-failed and triggers the existing failure cleanup without a marker; once a typed failure exists, subsequent cleanup cannot replace it. Test normal success cleanup, size-boundary streamed ZIP cleanup and injected cleanup failure while retaining the no-preexisting-delete rule.
- Document the action-plus-current-attempt alternative explicitly: it retains existing action ID/digest behavior and adds subject-attempt comparison, but still lists/filter IDs and cannot satisfy this approved direct metadata/attempt/refusal contract. Do not imply list/filter selected wrong bytes or claim an observed integrity incident; measure v1 by its defined acceptance/refusal behaviors.
- State that the first protected-main transport trial occurs after merge and can fail on real API/token/ZIP compatibility. The named maintainer required by the implementation entry gate owns stopping, retaining safe reports and reverting only consumer wiring. Do not dispatch the run or add publishing enforcement as part of this plan review.
- Keep the provider-specific boundary internal. At a future relevant upstream dependency update, reconsider keeping this custom transport if supported tooling meets the complete direct/current-attempt/refusal contract; no scheduled review, adoption promise or new publishing milestone is introduced.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:dx -->
- Preserve every prior CEO/source requirement. Missing or malformed upload artifact-id or artifact-digest must fail the guarded producer receipt run block nonzero with a fixed safe message naming the output; never let the producer succeed with a silently empty receipt. Test missing/empty/leading-zero ID and digest variants: bare lowercase 64-hex is accepted and prefixed once; already-prefixed, uppercase and short upload digest outputs are rejected under the pinned output contract. Do not change the receipt schema to accept an upstream output change silently.
- Before any publish-preflight or later bundle use, the workflow must check command exit 0 and jq -e verify the final record's schema, succeeded status, releaseEligible false, repository/name+ID, run, current attempt, source, exact artifact ID and verified digest against its trusted context/receipt. A missing, malformed or conflicting marker blocks consumption; no replacement download. Execute fixture run blocks and remove/break the marker as regression cases.
- The new mode requires its complete existing command contract including --producer-artifact-id from the separate existing producer job output; it never derives expected ID solely from the receipt. Refuse inapplicable input-selection options such as --artifacts-input or --producer-subject with artifact-input-invalid naming only the option; accept documented common help/options, preserve other modes, and require GH_TOKEN without ever printing its value. Parameterize these cases and mode/help consistency.
- Complete the credential-free recipe in the existing provenance reference and link it from the PackageIndex README. From a prepared checkout with .NET 10, run dotnet restore tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj --locked-mode, then dotnet test tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~TailwindArtifactTransportTests.AcceptanceCard. Implement that stable parameterized test as exactly three cases, expected Passed: 3; the matching invocation exits 0 and has its marker, the old-attempt invocation exits 1 with zero HTTP, and the changed-byte invocation exits 1 before extraction. The suite succeeds by asserting both expected refusals, not by converting their command exits to success. Use generated temporary fixture inputs/checkouts and intentionally internal test seams, never the maintainer's dirty working tree as evidence or production GH_TOKEN. No test-only CLI flag or environment bypass is allowed. Keep a real production source-stamp/default-validator regression outside the mocked card; the card's no_claim explicitly excludes genuine producer authority and hosted source/token proof.
- Normal transport refusals emit a safe first-line stderr prefix artifact-transport failed: <canonical-code>, followed by safe stage/context and an authorized final-line next action; exit is 1. Report-path failure uses exactly that fixed one-line prefix with the original canonical code and no untrusted path/value. Keep structured diagnostics.json and summary.md; never fall through to the existing raw exception-message formatter for the new mode. Use canonical per-code anchors in docs/tailwind-artifact-provenance.md for docsUrl, not issue #798; verify each emitted code's anchor exists.
- Report safe context for environment/dependency/partial/unexpected paths in the existing diagnostic channels: source-stamped verifier revision, fixed API version, operation/stage, HTTP status when present, validated canonical identity and limit name, relevant variable name GH_TOKEN without its value, and allowlisted underlying cause kind. Usage validation before work needs only operation/constraint, safe fix, code, redaction and failure channel. For unexpected local failures, include compact allowlisted PackageIndex type/method frames in the existing report/message, with no arguments, locals, filesystem source paths, raw exception/inner-exception messages, signed URLs, headers or bodies. No separate raw trace artifact, verbose flag or public schema/package is added; document any internal diagnostics shape changes fully and preserve old-mode output behavior.
- Keep the existing refusal taxonomy. Known exceptions map at their owning boundary: invalid path/argument before work to artifact-input-invalid, malformed API content to artifact-api-response-invalid, transfer/HTTP to the defined API/download code, invalid/unsupported ZIP to artifact-archive-unsafe, binding to artifact-producer-binding-invalid, and output IO to artifact-output-failed. A last-resort unexpected local operational failure that prevents producing required verified output uses artifact-output-failed with safe cause kind unexpected-local and maintainer escalation, never misreported as denied permission or corrupt remote input. Preserve caller cancellation and the first typed failure separately; process-fatal termination remains the documented no-report limit. Add injected unexpected-exception and double-failure fixtures through internal supported seams.
- For every failure family, scan diagnostics.json, summary.md, stdout, stderr, success record if present and all report-directory files for sentinels representing a token, signed query, Location value, raw body and nested exception text; none may occur. Distinguish this emitted-output redaction proof from redirect-header isolation. Assert error context, next action and docs anchor as well as code and output eligibility.
- Recovery is bounded by zero automatic retries and zero unattended new hosted runs. Every hosted rerun requires the existing maintainer's authorization and fresh owned paths; after any refusal stop again rather than loop. Before a new invocation, reconcile the prior command exit and final marker plus any invocation-owned leftover directory. Before work, bundle side effects are none; after staging/install, side effects may be partial and the bundle remains ineligible without exit 0 plus marker; after crash/failed cleanup they are unknown until reconciled. Reports are diagnostic side effects, never release authority.
- artifact-input-invalid next action: correct the named argument or GH_TOKEN binding without printing secrets; ask the maintainer before a new full rehearsal.
- artifact-receipt-invalid next action: repair protected producer receipt emission/schema; never edit artifact-contained data to manufacture authority; ask the maintainer before a new full rehearsal.
- artifact-receipt-binding-mismatch next action: investigate the named protected receipt/context field; do not substitute an ID; ask the maintainer before a new full rehearsal.
- artifact-attempt-mismatch next action: ask the maintainer to authorize re-running all jobs; re-run all jobs so the producer emits a receipt for the current attempt. Re-running failed jobs keeps the older receipt and will refuse again.
- artifact-repository-mismatch next action: stop and verify protected repository/head-repository authority; no cross-repository fallback; ask the maintainer before a new full rehearsal.
- artifact-run-mismatch next action: stop and verify protected run authority; no latest-run selection; ask the maintainer before a new full rehearsal.
- artifact-workflow-mismatch next action: stop and verify protected workflow path/event; do not relax the manual-main policy; ask the maintainer before a new full rehearsal.
- artifact-source-mismatch next action: restore/build the exact authorized source and investigate conflicting metadata; never relabel a package; ask the maintainer before a new full rehearsal.
- artifact-not-found-or-inaccessible next action: ask the maintainer to establish access and retention for the exact ID; preserve missing-versus-inaccessible ambiguity and do not choose another candidate.
- artifact-expired next action: stop and ask the maintainer for a new authorized full rehearsal; do not recover with a different artifact under the old receipt.
- artifact-permission-denied next action: ask the maintainer to verify GH_TOKEN wiring and read permissions; never print credentials or broaden them automatically.
- artifact-rate-limited next action: ask the maintainer to establish that GitHub's limit has cleared before authorizing a new full rehearsal; re-run all jobs so the producer emits a current-attempt receipt, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-api-unavailable next action: ask the maintainer to confirm API/network recovery and reconcile local output before authorizing a new full rehearsal; re-run all jobs, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-api-response-invalid next action: preserve safe metadata context and ask the maintainer to investigate the pinned API contract; no retry of malformed content or guessed API version.
- artifact-metadata-digest-mismatch next action: stop and investigate the original upload/API digest authority; no digest rewrite, alternate ID or repack under the same identity.
- artifact-bytes-digest-mismatch next action: stop and investigate transfer/archive integrity using safe identity/digest evidence; no extraction or replacement candidate.
- artifact-download-failed next action: ask the maintainer to establish transfer recovery and reconcile owned scratch before authorizing a new full rehearsal; re-run all jobs, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-redirect-invalid next action: stop and ask the maintainer to investigate the exact artifact download path using safe stage/status only; never follow an unsafe destination or log its signed URL.
- artifact-budget-exceeded next action: ask the maintainer to inspect the named byte/time/request/entry/depth/expansion bound and cause; do not raise it ad hoc. If a new rehearsal is authorized, re-run all jobs, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-archive-unsafe next action: stop and ask the maintainer to investigate the protected archive structure; no extraction recovery, path rewriting or repack under the old identity.
- artifact-producer-binding-invalid next action: preserve the safe underlying diagnostic and ask the maintainer to investigate subject/manifest/package binding; no replacement producer download.
- artifact-output-failed next action: reconcile invocation-owned partial output and inspect safe cause kind; ask the maintainer to repair disk/access/same-volume configuration for expected IO or investigate the stamped tool for unexpected-local failure. Any authorized new rehearsal must re-run all jobs with fresh paths.
- artifact-cancelled next action: respect caller intent, reconcile owned output eligibility, and ask the maintainer before authorizing a new full rehearsal; no automatic restart.
- Document work/staging and bundle destination same-volume requirements in help and the existing pitfall guide, using safe root descriptions rather than raw untrusted path values in errors. Retain the cross-volume refusal and no-copy fallback.
- During later authorized implementation verification, record one README-to-understood-card elapsed observation including reading/restore/build, supported SDK/hardware and cache state. The target is under five minutes from the declared prepared-checkout start, not a measured guarantee; report limits/causes if missed without weakening tests. SDK installation and genuine protected hosted acceptance remain separate observations; no telemetry, recurring process or new timer release gate is introduced.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Retain all prior source/CEO/DX obligations and the four preliminary engineering requirements below. Approval remains planning only. Before implementation authorization, the sanitized protected acceptance card must record exact artifact/run/attempt/source/upload-digest values and each protected source, and a named maintainer must own the first hosted run. That assignment is completed on 2026-11-10 in [the protected card](issue-910-protected-acceptance-card.md): run 36547070332, attempt 1, artifact 11023576342, source 10d375c6942fc650cc64f64e0748add77540d47c, repository ID 1035215851, archive digest sha256:27151d76524a22cd8126f5dc658295c16cf6d869930ccaf8a3f97d97e14be6fd, with protected context and exact upload-step log sources. Andrew Krock (@akrock) is explicitly assigned by the user. Current exact-ID metadata reports retained/unexpired; it is corroboration, not attempt authority. The normalized card record is a historical planning transcription, not a newly emitted v1 needs output. This completes evidence/owner selection only; implementation and the future hosted trial remain separately authorized.
- For new-mode path validation, inspect the nearest existing ancestor and every existing ancestor for links/reparse points before creating any parent/output; refuse overlap and pre-existing targets first. Track each path this invocation actually creates for owned cleanup. Do not copy the older private helper's create-parent-before-check ordering or change unrelated callers' behavior. Test a linked ancestor with a missing child and assert no outside path is created. Checkout-relative outputs are allowed only outside the existing TailwindSourceIdentity watched input paths; refuse work, report, staging or bundle locations inside those paths as artifact-input-invalid before HTTP. Keep legitimate artifacts/ or runner-temp outputs, concrete-input disjointness and repeated real source validation. Reuse TailwindTestPaths.TemporaryRoot for fixture paths, including its existing macOS /private alias handling; do not weaken production no-link checks or silently resolve operator-supplied symlinks.
- Before ZipArchive.Entries materialization, read only bounded EOCD and, when present, ZIP64 envelope metadata to reject declared entry counts beyond 100,000 and invalid/split/out-of-archive offsets. Keep the envelope check small and internal to the adapter; use .NET ZipArchive for structure/decompression, retain its count consistency checks, and count actual extracted entries/bytes. Do not add a dependency, a full archive parser or a new resource limit. Cover standard ZIP and ZIP64 valid cases, exact/over entry bound, malformed/missing/truncated envelope, misleading declared count and unsupported multi-volume archives. A preflight count bound uses artifact-budget-exceeded; malformed/unsupported structure uses artifact-archive-unsafe.
- Configure production transport for manual redirects, no ambient/default credentials or cookies, no automatic decompression, and per-request Authorization only on the validated API requests; do not put Authorization in shared DefaultRequestHeaders. Read raw response bytes with ResponseHeadersRead and explicit header/body-inactivity deadlines under the single overall TimeProvider-backed budget. Count actual bytes, ensure storage follow-ups carry no credentials and classify caller cancellation separately from budget cancellation. Pass the overall deadline through extraction, source/content validation and output installation; keep cleanup/reporting best effort without changing the first failure. Test header stall, body stall/dribble, caller/deadline race, raw encoding/hash fidelity, no-token redirects and disposal/cancellation paths. The test-only manual TimeProvider must implement CreateTimer and support manual advance; first prove that advancing it fires a CTS-backed timeout. Do not add a TimeProvider package reference merely because a central version exists. Reject a declared Content-Length above the applicable JSON/archive limit early, while still enforcing actual bytes when the declaration is absent, false or smaller.
- Define one portable ZIP relative-name representation before path resolution/collision checks. Permit a single trailing slash only as a directory marker, then require nonempty forward-slash-separated segments; reject rooted names, backslashes, NUL, colon, empty/dot/dot-dot segments, non-NFC spellings, Windows-invalid/reserved segments and trailing dot/space aliases. Do not rewrite unsafe names into safe ones. Use the validated NFC relative name and OrdinalIgnoreCase collision keys, tracking file/directory ancestor relationships; require the resolved target to stay beneath staging and reject any unexpected normalization change. Test normalized aliases, case/Unicode aliases, reserved names, file/directory collisions and valid explicit directory markers on supported platforms. This is safe extraction policy, not a full general ZIP parser.
- Add only intentionally internal adapter seams needed for deterministic verification: a source-identity delegate defaulting to TailwindSourceIdentity.RequireAsync, and narrow owned-file create/move/delete/report-write operations defaulting to real filesystem calls. Thread the source delegate through a minimal internal overload of ValidateProducerBindingAsync so the real subject/manifest/package validator still executes in the three-case card. Existing callers and every production entry use the real default; no CLI/environment bypass, reflection, alternate authority or replaceable content validator. Keep real clean-checkout/stamped-build success and dirty/wrong/missing-stamp failures outside the card. Use the existing Program.RunAsync workflow injection convention only for dispatch wiring tests, not to fake the adapter acceptance card. Test real filesystem success as well as injected cross-volume move, cleanup, scratch removal, report/marker write and double failures. Document each seam and no_claim: injected IO does not establish OS behavior; the card's controlled source check does not establish hosted/source authority.
- Inventory CommandLineOptions in Program.cs as well as TailwindCommandOptions. For artifact-transport only, validate raw argument names/presence before applying general defaults or path resolution: require all twelve documented options, reject duplicates and inapplicable selection options with artifact-input-invalid naming only the option, and route parse failures through the new safe stderr boundary. Then reuse existing parsing/path resolution for accepted values. Defaults on CommandLineOptions must not manufacture required authority or conceal that an option was explicitly supplied. Preserve every other mode's parsing/defaults; add real dispatch coverage for presence versus default and malformed raw values.
- Retain the existing producer-binding validator, but never forward arbitrary PackageIndexException.Message or inner-exception text. At the source-check boundary map failures to fixed safe source messages; at subject/manifest/package boundaries map to fixed safe binding messages plus allowlisted cause kind and validated canonical identity/field names only. Preserve an existing safe underlying diagnostic code when actually available; otherwise emit the safe boundary cause, not a manufactured code or raw message. Test local paths, signed queries and secret sentinels in reused exception messages across all emitted channels. Existing old-mode diagnostic behavior is unchanged.
- The core owns and best-effort deletes an incomplete temporary ZIP on internal failure. A successful verified-archive result transfers that ZIP's ownership to the adapter; the adapter disposes handles and removes the entire invocation-created work directory, including ZIP, before writing the last success marker. Retain first-error precedence if later cleanup/reporting also fails. Keep same-volume installation with Directory.Move and no copy fallback. Document the configuration before invoking, but add no pre-HTTP filesystem rename probe or extra output creation; a real/injected cross-volume move remains artifact-output-failed. This owner-approved D3 choice avoids an additional mutation path while preserving the approved refusal and all safety coverage; an early owned rename probe is the equally safe alternative, with more setup/cleanup behavior and earlier feedback.
- Emit the receipt after the protected step with id validate-producer-binding, not the earlier producer-binding step. Move setup-dotnet together with locked restore and source-stamped build before consumer verification. Use the fixed workflowPath .github/workflows/package-artifacts.yml from this protected workflow's policy; the API path check corroborates it and existing repository/main/manual guards remain required. Include these exact ordering/policy cases in the executable workflow suite. Document that receipt/API digest agreement is consistency and not an independent signature or producer authentication; recomputing downloaded bytes proves equality to that trusted digest. Retain the supported 2026-03-10 API pin, verified against GitHub's supported-versions documentation. No new hosted dispatch is authorized here, and one required acceptance run is a minimum evidence requirement, not a rule preventing separately authorized investigation after a refusal.
<!-- /autoplan-accepted:eng -->
## Review record

Historical phase outputs below retain their review-time status. The owner approved the plan and D1a/D2a/D3a on 2026-11-10; see the final owner-approval record and terminal report for current status.

### CEO Step 0: scope, evidence and decisions

Review depth is implementation-ready, mode SELECTIVE EXPANSION by /autoplan override. The output is a plan, not implementation. The real pain is ambiguous refusal and artifact discovery in a narrow rehearsal path; leaving it alone preserves list/filter behavior and limited diagnostics. There is no measured incident frequency or latency baseline and no established Skoolit root cause.

| ID and owner | Contract and evidence | Current | Proposed | Status | Exact approval and scope |
|---|---|---|---|---|---|
| S1, owner | Approved design Confirmed Premises | One nonpublishing Tailwind current-attempt bundle | Same | approved | User: nonpublishing rehearsal; same-attempt only; defer publishing/recovery/Skoolit |
| A1, owner | Approved design Chosen Architecture | Internal verifier + thin adapter | Same | approved | User selected B at D4; no public package/framework |
| D1, owner | Latest user instruction | Plan/review only | Same | approved | “Run /autoplan ... Don’t start implementation yet.” |
| C1, owner | Approved design Owner-Requested Clarifications | 2.5–3 days; all attempt conflicts; any Retry-After on 403 | Same | approved | User requested all three minor fixes and design approval |
| P1, implementer + maintainer | Approved design The Assignment | Acceptance-card authority and hosted owner not yet recorded | Keep as entry gate before implementation authorization | approved | Existing design requirement; this planning run cannot invent owner or evidence |

Prior branch decision #892 concerns Cloud Tasks and is unrelated. No branch default overrides #910's explicit approvals. The local old #781 branch and its dirty state are not the implementation baseline. Current remote main is ed93a55aad1bf86ea0cae47cd8ddd4ae9fb3e9c9; nine named existing integration/source files were read via GitHub at that SHA (manifest retained); three speculative helper filenames returned 404 and are not dependencies. Existing relevant workflow and verifier bytes match the earlier local origin/main snapshot. README changed outside the specific Tailwind behavior. No implementation of artifact-transport or GitHubActionsArtifactVerifier was found in canonical command registry/source. Current CLI lacks the proposed mode by design, so its addition/help tests are explicit tasks. No fetch/merge was used to mutate this dirty branch; pinned GitHub API source is the freshness evidence.

Taste references: reuse TailwindEvidenceWorkflow.ParseStrict, HashFileBoundedAsync and ValidateProducerBindingAsync; use the executable YAML-run-block tests as the workflow pattern. Avoid copying the producer's download-action list/filter path into the new consumer, and avoid another resolver or receipt-only testing script. Do not assume private helpers are already reusable APIs.

Premises: the artifact metadata has no producing-attempt field, so protected upload outputs remain the attempt authority. The exact-attempt endpoint corroborates context only. Digest comparison and subject validation establish consistency within that trusted-producer model. A compromised authorized producer is outside this trust model; docs must say so. A read-only API token is required in the hosted workflow; the existing permission declaration is static evidence, not hosted capability proof.

Landscape: Aside is not installed; official GitHub REST/action docs were searched instead. Layer 1 is pinned download-artifact and existing typed content checks. Layer 2 is official artifact/attempt endpoint contracts and upload-artifact's SHA-256 output; newer action functionality does not supply the approved receipt-bound refusal contract. Layer 3: an API without artifact-attempt association cannot independently reconstruct it, making a protected same-attempt receipt the narrow valid authority. No new dependency or action upgrade is justified.

10x vision: after independent caller evidence, one reliable transport contract could support other producer readers. This slice moves from list/filter plus semantic preflight to exact identity + actual archive hash + specific reasons. The 12-month ideal would include separately designed recovery and independently reviewed adopters; the v1 plan deliberately makes no promise to deliver it now.

Complexity challenge: 11 primary changed files estimated (4 new source/test files, 4 integration files, 3 docs), at most 12 if minimal helper visibility changes. Most are tests/docs required by the contract, not independent runtime components. Keep the two concrete responsibilities selected by the user; combining them reduces a file but couples generic transport to Tailwind semantics. One PR, no new infrastructure; hosted acceptance remains a post-merge issue gate, not another implementation PR.

Delight/expansion scan, individually dispositioned under prior approvals:

| Proposal | Effort, risk | Disposition | Why |
|---|---|---|---|
| Safe expected/observed/next-action reports | S, low | already required | Issue diagnostics contract; no scope addition |
| Exact partial-rerun instruction | S, low | already required | Approved design explicitly defines recovery text |
| Credential-free three-case acceptance card | S, low | already required | Approved design/test contract; protects operators and agents |
| Copy-paste canonical command/help links | S, low | already required | AGENTS documentation and issue command contract |
| No-success-marker proof in failure fixtures | S, low | already required | Issue lifecycle and acceptance criteria |
| Permanent shadow probe or public shared package | M, medium | declined/deferred | Approved design explicitly excludes both now |

No accepted expansion assumes cross-attempt recovery, stable publishing or Skoolit adoption. No new scope proposal is accepted; no new TODO proposal is needed. Carry already deferred work in the plan's Deferred work list and chat. Existing TODOS.md #798 external attestation/retention follow-up remains separate and unchanged.

Temporal interrogation: Hour 1 fixes exact contracts, helper availability and source stamp (human ~0.5d / agent ~1h); hours 2–3 build direct identity, deadlines, redirect isolation and hashing (human ~1d / agent ~2–3h); hours 4–5 adapter/extraction/output and workflow integration (human ~0.5d / agent ~1–2h); hour 6+ hostile fixtures, docs/format/analyzers and review (human ~0.5–1d / agent ~1–2h). Estimates overlap by activity; total remains human 2.5–3d and agent 5–8h, not their maxima added. Hosted time is unestimated and separately authorized. Open evidence gate: retained acceptance-card authority plus maintainer before implementation authorization; token/digest/resource reality requires the later hosted run.

<!-- AUTONOMOUS DECISION LOG -->
### Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| M1 | CEO | Review in SELECTIVE EXPANSION, preserve settled scope | Mechanical | P1,P6 | /autoplan override with exact prior approvals | Reopening architecture or expanding callers |
| M2 | CEO | Keep all diagnostics/tests/docs and one PR | Mechanical | P1,P4,P5 | Complete approved contract, reuse existing typed validator | Reducing failure coverage to reduce file count |
| M3 | CEO | Keep acceptance-card/maintainer and hosted evidence as distinct gates | Mechanical | P1,P5 | Required design assignment and real-host acceptance cannot be fabricated | Treating fixtures/static permissions as hosted proof |

<!-- autoplan-accepted:ceo -->
- Preserve the existing producer_artifact_id output sourced directly from the successful upload; pass it separately from the receipt as --producer-artifact-id, compare them and test both disagreement and no independent-authority claim.
- Use exact artifact-prefixed diagnostic codes from the canonical refusal table in all precedence text, reports and parameterized test expectations; shorthand never defines another code.
- Treat --repo-root as source/resolution context rather than a concrete evidence input for output-overlap checks. Permit fresh dedicated checkout-relative output if disjoint from actual receipt/payload inputs and evidence directories, all output/work/report paths and their ancestor/descendant overlaps; keep no-link and no-preexisting-delete rules and add allowed/disallowed path fixtures.
- Complete the HTTP status partition: metadata endpoints require 200; known 401/403/404/410/429 and 5xx classifications take precedence, metadata redirects use artifact-redirect-invalid, and every other final metadata/API status (including unexpected 2xx and other 4xx) uses artifact-api-response-invalid. ZIP GET/follow-ups accept 200 bytes or 301/302/303/307/308 with a valid HTTPS Location inside hop/request limits; unsupported or malformed redirects use artifact-redirect-invalid. On the authenticated ZIP API request use the same known API failure classifications; any other non-success API status uses artifact-api-response-invalid. Once following storage, non-success/non-supported-redirect transfer statuses use artifact-download-failed. Cover each partition and boundary, with no retries.
- After content validation and installation, dispose and remove invocation-owned temporary ZIP and scratch staging before publishing the final success marker. Do not retain ZIPs on success or upload them as reports. A required scratch-removal failure before success becomes artifact-output-failed and triggers the existing failure cleanup without a marker; once a typed failure exists, subsequent cleanup cannot replace it. Test normal success cleanup, size-boundary streamed ZIP cleanup and injected cleanup failure while retaining the no-preexisting-delete rule.
- Document the action-plus-current-attempt alternative explicitly: it retains existing action ID/digest behavior and adds subject-attempt comparison, but still lists/filter IDs and cannot satisfy this approved direct metadata/attempt/refusal contract. Do not imply list/filter selected wrong bytes or claim an observed integrity incident; measure v1 by its defined acceptance/refusal behaviors.
- State that the first protected-main transport trial occurs after merge and can fail on real API/token/ZIP compatibility. The named maintainer required by the implementation entry gate owns stopping, retaining safe reports and reverting only consumer wiring. Do not dispatch the run or add publishing enforcement as part of this plan review.
- Keep the provider-specific boundary internal. At a future relevant upstream dependency update, reconsider keeping this custom transport if supported tooling meets the complete direct/current-attempt/refusal contract; no scheduled review, adoption promise or new publishing milestone is introduced.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-baseline-edits:ceo {"sourceSha256":"72c0968766d974a23a694d64b572b433040a1e618e730b03a59dc632a9c1c0ee","replacements":[{"oldText":"disjoint from all inputs and their ancestors/descendants","newText":"disjoint from concrete receipt/payload input files and their containing evidence directories (including ancestor/descendant overlaps); --repo-root is the resolution/source context, not itself an evidence-input directory, so a fresh dedicated output below the checkout is allowed if it cannot overlap those concrete inputs"},{"oldText":"Export one named job output through `$GITHUB_OUTPUT`; missing/malformed upload ID or digest means no receipt.","newText":"Export one new named receipt job output through `$GITHUB_OUTPUT`; preserve the existing producer_artifact_id job output sourced directly from upload-package-artifacts.outputs.artifact-id. Pass that existing needs.package-artifacts.outputs.producer_artifact_id independently as --producer-artifact-id and compare it with the receipt; this is same-producer consistency, not independent producer authentication. Missing/malformed upload ID or digest means no receipt."},{"oldText":"Invalid caller context is input-invalid; malformed receipt is receipt-invalid.","newText":"Invalid caller context is `artifact-input-invalid`; malformed receipt is `artifact-receipt-invalid`."},{"oldText":"Other receipt identity conflicts use receipt-binding-mismatch with the field named.","newText":"Other receipt identity conflicts use `artifact-receipt-binding-mismatch` with the field named."},{"oldText":"Classify 429 as rate-limited.","newText":"Classify 429 as `artifact-rate-limited`."},{"oldText":"Preserve 404 as missing-or-inaccessible; 410 is expired; server/network failure is API-unavailable. A failed redirected storage transfer uses download-failed.","newText":"Preserve 404 as `artifact-not-found-or-inaccessible`; 410 is `artifact-expired`; server/network failure is `artifact-api-unavailable`. A failed redirected storage transfer uses `artifact-download-failed`."},{"oldText":"Other API identity conflicts use their repository/run/source/workflow codes. Metadata-digest and downloaded-bytes-digest mismatches remain separate.","newText":"Other API identity conflicts use `artifact-repository-mismatch`, `artifact-run-mismatch`, `artifact-source-mismatch` or `artifact-workflow-mismatch`. Metadata digest disagreement uses `artifact-metadata-digest-mismatch`; downloaded byte disagreement uses `artifact-bytes-digest-mismatch`."},{"oldText":"A cross-volume move fails as output-failed;","newText":"A cross-volume move fails as `artifact-output-failed`;"},{"oldText":"Exit 1 with attempt-mismatch; zero HTTP calls; no usable output.","newText":"Exit 1 with `artifact-attempt-mismatch`; zero HTTP calls; no usable output."},{"oldText":"Exit 1 with bytes-digest-mismatch before extraction; no usable output.","newText":"Exit 1 with `artifact-bytes-digest-mismatch` before extraction; no usable output."}]} -->

### Spec review round 1 disposition
Native combo/sub completed both inputs: 7/10, three findings, no wholly omitted required behavior. All three are mechanically clarified under P1/P5: retain existing ID output rather than add another authority; use exact canonical code spellings; exclude context root alone from concrete-input overlap checks. Coverage is unchanged and the approved design stays immutable. Reviewer-confirmed fixes: 0 until a later result. Decision rows M4–M6: each mechanical, completeness/explicitness, rejected ambiguous wording.

### Spec review round 2 disposition and loop stop
Round 2 native combo/sub: 8/10, three issues (complete HTTP-status partition, success scratch policy, residual diagnostic shorthand). Prior ID-output and repo-root issues were reviewer-confirmed resolved; code spelling was partially clarified. The same code consistency requirement recurred, so stop the loop at its repeated-issue condition after two launches, retain the 8/10 NOT PASS verdict and apply the three edits without claiming an independent re-review. Applied edits verified: 3 of 3 against the current amendment readback. Spec not re-verified is an explicit final-gate concern. Decision M7: mechanical P1/P5 total status partition; M8: Taste P3/P5 remove disposable scratch before marker (alternative retain archive for debugging costs disk and broadens retention); M9: mechanical P5 exact full code names. No product scope or limit changes.

### CEO review evidence and voice disposition

Full methodology loaded through EOF: combined 2,192 lines, ranges 1–600, 601–1200, 1201–1800 and 1801–2192; the truncated middle was repaired with 1410–1580. Review-specific sections were applied after Step 0 and the spec loop. Scope-document approval is auto-decided under /autoplan for consistency with settled decisions, not user approval of this plan or implementation. Step 0I's implementation sequence and uncertainty gates remain binding.

Native CEO completed with matching INPUT `ceo 4d149785662b32091316a543cfe495dd37e1da1b009f5b45b12ce857c6f3b996`: five findings, two High and three Medium. Claude CLI exit 0 returned eleven concerns and a 4/10 score, but validator exit 4 (`untagged_review`) makes outside coverage unavailable/unverified. Its generated result, session and all modelUsage are retained; provider reports `claude-fable-5-1`, not the model from the earlier design review. These are unverified concerns, not eligible cross-model consensus. The five native findings are separately dispositioned below.

| Native finding | Disposition and verification |
|---|---|
| Value under-evidenced | Clarify capability, never infer a prior integrity incident. Existing action already selects ID and checks digest; v1 adds the owner's direct-endpoint/no-list contract, current-attempt gate and stable refusal distinctions. Existing ValidateProducerBindingAsync accepts repository/run/source/ID but no expected current attempt (TailwindEvidenceWorkflow.cs:35–61). This supports a static contract gap, not a proven harmful release. |
| Smallest alternative omitted | Add comparison: action + subject-attempt check supplies digest and current-attempt refusal after action execution, but still lists/filter IDs and lacks the specified direct metadata/attempt HTTP taxonomy. It is simpler, but changing to it removes approved outcomes. Retain B under the user's exact approval; expose that tradeoff without asserting list/filter chooses wrong bytes. |
| First hosted transport after merge | Accept the risk statement and named maintainer rollback decision. One PR/manual nonpublishing trial matches the approved design; do not silently introduce another workflow mode or activate publishing. First run can fail on archive/API compatibility; issue remains open and reports retained. |
| Upstream replacement | Keep provider boundary internal and narrow. Revisit at a future dependency update if supported upstream tooling satisfies direct retrieval, current-attempt authority and the refusal contract; no scheduled review process or graduation deadline is added. |
| Card/owner gate missing | Confirm open entry gate rather than waive it. No qualifying card or owner has been recorded by this planning run; implementation authorization stays pending independently of plan approval. |

Unverified Claude concerns: value/alternative/first-run/entry-gate points overlap the native review and are analyzed on source evidence above. Attestations, reducing the taxonomy, relaxing same-attempt policy, requiring publishing graduation and editing Skoolit would change settled scope and are not accepted. The claim that API version 2026-03-10 cannot be confirmed is contradicted by current official REST examples. The effort figures remain explicit estimates, not binding delivery ceilings; no cuts follow from the reviewer's unsupported alternative estimate. A historical flake-rate measurement is not required to redefine the user's chosen same-attempt behavior; document rerun cost without inventing its rate. No Critical defect is established from this unverified output.

### CEO consensus table

| Dimension | Native CEO | Claude Code | Consensus |
|---|---|---|---|
| Premises valid? | Capability valid, outcome claims need precision | Unverified critique | N/A |
| Right problem? | Value and simpler alternative challenged | Unverified critique | N/A |
| Scope calibrated? | Maintain approved scope pending owner | Unverified critique | N/A |
| Alternatives explored? | Add action-plus-attempt comparison | Unverified critique | N/A |
| Market risk? | Upstream may replace contract | Unverified critique | N/A |
| Six-month trajectory? | Need entry gate and first-run owner | Unverified critique | N/A |

### Section 1: architecture

The approved seam follows different authorities: the core validates GitHub transport; the adapter validates Tailwind content and owns installation. The new report is consumed by maintainers for refusal/recovery and the success record by the workflow for eligibility; it is the smallest persistent proof for this manual trial and is retained with that run, not a new process dashboard. Runtime has no new public listener, DB, cache or background service. At higher concurrent workflow volume, request/disk budgets and runner capacity fail first; each invocation has fresh disjoint paths, with no shared output state to serialize.

```text
Protected upload + producer binding
     | needs receipt + separate existing ID + subject hash
     v
Independent consumer context -> Tailwind adapter -> internal GitHub verifier
                                  |                  | artifact / exact attempt / ZIP
                                  |                  v
                                  |              verified raw ZIP
                                  v
                         safe staging -> existing producer binding
                                  | move / reports / success marker LAST
                                  v
                         same bundle -> existing preflight/replay/publisher rehearsal
```

### Section 2: error and rescue

Use typed failures at boundaries and fixed messages; raw exception messages cannot become diagnostics because they may carry a signed URL or response content. Specifically catch parse/IO/access/HTTP/cancellation/unsupported-archive exceptions at their owning boundary and preserve the first typed failure during best-effort cleanup. Do not silently retry, find another artifact or catch everything as permission failure. Required code partitions are in the implementation contract and must be verified through parameterized cases.

| Method/codepath | Failure / exception class | Rescue and user result | Planned proof |
|---|---|---|---|
| Options/context/source validation | PackageIndexException, invalid caller identity/source | artifact-input-invalid; no HTTP; safe actionable refusal | CLI required/source fixtures |
| Receipt parse | JsonException / bounded strict-schema failure | artifact-receipt-invalid | duplicate/type/schema/size/depth cases |
| Receipt binding | Typed identity/attempt conflict | binding-mismatch or attempt-mismatch; no HTTP | each field, earlier attempt zero sends |
| Artifact/attempt metadata | HttpRequestException, JsonException, known HTTP response | exact identity/access/expiry/rate/API codes; preserve 404 ambiguity | every field and status partition |
| Headers/stream/request counter | Deadline or bound exceeded | artifact-budget-exceeded identifies bound; caller cancellation distinct | controlled clocks and boundaries |
| Archive request/redirect | bad Location/status/TLS/hop or interrupted transfer | artifact-redirect-invalid or artifact-download-failed | redirect and token-isolation cases |
| Stream digest | Typed metadata/byte digest disagreement | distinct metadata-digest/bytes-digest code before ZIP open | expected digest and one-byte mutation |
| ZIP scan/extract | InvalidDataException, unsafe name/type or bound | archive-unsafe or budget-exceeded; staging ineligible | malicious real ZIP inventory |
| Tailwind binding | PackageIndexException / safe underlying diagnostic | artifact-producer-binding-invalid; subject attempt separately attempt-mismatch | existing validator plus subject-attempt cases |
| Move/report/success scratch cleanup | IOException, UnauthorizedAccessException | artifact-output-failed; no marker; no copy fallback | locks/access/move/report/cleanup injection |
| Caller cancellation | OperationCanceledException with caller token | artifact-cancelled/status cancelled; exit 1 | cancel at stage boundaries |
| Failure cleanup/report fallback | IO/access while a failure exists | preserve original code; fixed safe stderr if report unavailable | double-failure case |

### Section 3: security and trust

Threats are malicious receipt/API/archive input, credential forwarding, output overwrite, and overclaiming producer authority. Receipt/API parsing and exact comparison mitigate high-impact identity substitution; raw-byte hashing before extraction protects archive integrity under the trusted producer digest. HTTPS redirects with fresh unauthenticated requests mitigate token forwarding; path/type/inventory rejection and owned fresh directories mitigate traversal and overwrite. Compromised authorized producer and concurrent malicious writes by the same authorized runner are outside the approved trust model; do not add claims about them. No new package or credential type is introduced.

### Section 4: data flow and interaction edge cases

```text
INPUT -> strict VALIDATION -> direct HTTP/hash -> STAGING -> binding -> INSTALL -> marker
  | nil/empty/wrong type       | timeout/403/404 | unsafe      | mismatch | IO     |
  +---------------------------+----------------+-------------+----------+--------+
                              typed failure -> owned cleanup -> safe report, exit 1
```

Nil receipt/token/required argument and empty JSON/ZIP fail at their boundary. Duplicate/case-colliding entries, huge streams, stale attempt outputs, corrupt transfer and interrupted reports remain refusal cases. A retry is a new invocation with fresh paths; a partial rerun with an older producer receipt refuses before network and instructs re-running all jobs. There is no user navigation or click UI. Two invocations cannot intentionally share output: an existing target is refused, and create-new/move without overwrite prevents the second install. Within an invocation, marker publication follows move and scratch cleanup; test cancellation before/after each await and failure after installation to prove no eligible partial state.

```text
Order A: move succeeds -> cancellation -> remove owned bundle -> exit 1, no marker
Order B: cancellation -> move never starts -> cleanup staging -> exit 1, no marker
Fresh consumer runs only after command exit 0 + final marker; neither schedule admits partial output.
```

### Section 5: code quality

Keep two concrete runtime responsibilities rather than generalizing a transport framework for imagined callers. Reuse ParseStrict, bounded hashing and the intentionally internal binding API; expose only identical private helper behavior if needed and keep old callers' regressions. Name stage-specific operations so parsing, classification, download, extraction and installation do not become a single branching method. No implementation complexity score is claimed before code exists; future code review must inspect the actual branches.

### Section 6: tests

The approved test matrix maps every new boundary and every refusal variant to a supported seam; no extra behavioral assertion is waived. The Friday confidence check is the real command through matching receipt/API/ZIP/subject followed by actual verified-bundle preflight. Hostile QA mutates only a ZIP byte, an attempt, a redirect token destination or a case-colliding path. Chaos checks cancel or fail IO at stage handoffs and combine primary/report cleanup failures; none uses wall-clock sleeps or external credentials. This is a new implementation, so these tests are planned, not passing today.

```text
receipt + expected context -> unit strict fields/each identity/large numeric tokens
direct requests -> fake-handler sequence + no-list/no-retry + exact API fields/status
deadline/streams -> fake-clock + boundary counter + interrupted/oversize actual bytes
ZIP/hash -> real ZIP mutation before extraction + entry safety and expansion bounds
adapter -> internal seam + CLI dispatch/options/source + marker/owned-path assertions
workflow -> execute YAML run block + guard/permissions/current attempt/verified-byte use
host -> authorized protected-main rehearsal; proves real API/token/digest path only then
```

### Section 7: performance

There is no throughput/latency claim or hot service path. The largest persistent structures are the bounded metadata document, streamed ZIP on disk and entry-name set; archive bytes must never be held in memory as a whole. The approved limits, one deadline and inactivity/header budgets are the performance contract; controlled limit fixtures and later hosted byte/time/disk observations test them. No database/index/cache applies. Do not cache context or ZIP across attempts because it would add recovery semantics.

### Section 8: observability

The diagnostic record contains safe operation/stage, stable code, expected/observed identity, authorized next action and canonical docs; run/attempt/artifact/source identify the workflow correlation without another trace service. The manual workflow's retained safe reports and maintainer runbook are the launch operational surface. API bodies, token values, signed URL/query and unrelated environment are excluded, including stderr. A crash before reports is an explicit limit, and absence of a report never implies success. No alert service, dashboard UI or broad telemetry is justified for one manually invoked trial.

### Section 9: rollout and rollback

There is no data migration. One additive producer output and one consumer replacement land together behind existing protected manual/nonpublishing guards; old aggregate/native/stable/prerelease paths continue. The first hosted execution can expose unmodeled real REST/ZIP behavior after merge. Its named maintainer must stop on refusal, retain safe reports and own reverting only the new consumer wiring rather than substitute another candidate. First-run authorization and issue closure remain separate from this plan review; do not dispatch now.

```text
separate implementation authorization -> one reviewed PR -> merge
    -> separate protected-main trial authorization -> manual rehearsal
          success: retain real evidence; decide issue acceptance
          refusal: retain safe reports -> stop -> maintainer revert consumer wiring
```

### Section 10: trajectory

Reversibility is high: restoring the previous consumer wiring has no data migration or publication side effect. The maintenance debt is owning HTTP/ZIP/refusal contracts; keep it internal and document the actual trust boundary so another caller cannot assume stronger proof. Reconsider at an upstream dependency update when native tooling supplies the whole required contract; do not add an adoption promise, scheduled compatibility review or deadline to expand into publishing. Already deferred Skoolit work remains tied to its recovery design settling, not to a claimed root-cause fix here.

### Section 11: design

SKIPPED (no UI scope). This is a maintainer CLI and guarded workflow with machine-readable reports; scope detection found no rendered UI file or visual evidence.

### Failure modes registry

| Codepath | Failure | Rescued? | Test planned? | User sees / logged |
|---|---|---|---|---|
| Input/receipt | Missing authority, invalid schema, conflicting identity | Yes | Yes, each category | typed code; safe fields |
| Metadata | Wrong artifact/run/attempt/repository/source/workflow or digest | Yes | Yes, each field | specific refusal |
| HTTP | denied/404/expired/rate/server/other status | Yes | Yes, all partitions | stage/status/code, no raw body |
| Budgets | JSON/depth/bytes/time/requests/entries/expansion | Yes | Yes, each limit | named bound |
| Redirect/transfer | unsafe destination/hops/auth forwarding/truncation | Yes | Yes | redirect/download code |
| Archive | corrupt/traversal/link/collision | Yes | Yes, real ZIPs | archive-unsafe |
| Binding | package/manifest/subject or current attempt mismatch | Yes | Yes | safe nested detail / attempt code |
| Output | report/move/scratch cleanup failure | Yes | Yes | output-failed, no marker |
| Cancellation | caller abort around stage handoffs | Yes | Yes | cancelled/exit 1 |
| Crash | termination before report/cleanup | Process cannot rescue | Eligibility gate prevents consumption | no eligible bundle; reports may be absent |

No unrescued silent critical gap is accepted. Tests remain obligations, not implementation evidence.

### NOT in scope

Deferred by the user's prior answers: stable/prerelease enforcement, cross-attempt recovery, public package and Skoolit adoption after #1441 settles. Signatures/attestations remain separate #798 retention/provenance follow-up. Rejected now: generic plugin framework, permanent shadow-probe mode, resolver rewrite, new native/aggregate transport and guessed historical root cause. No new TODO is created from unverified proposals or speculative graduation metrics.

### What already exists

| Subproblem | Existing surface | Treatment |
|---|---|---|
| Protected identity/ID/hash outputs | package-artifacts.yml producer and consumer env | reuse; add receipt output only |
| Source/tool identity | TailwindSourceIdentity + locked source-stamped build | reuse before HTTP |
| Strict/bounded JSON and hashing | TailwindEvidenceWorkflow ParseStrict / hashing | reuse exact applicable semantics |
| Subject/manifest/package binding | ValidateProducerBindingAsync | reuse; additionally compare current subject attempt |
| Action ID selection/digest | pinned download-artifact | retain other callers; replace one consumer for approved no-list contract |
| Rehearsal and publishing checks | aggregate/preflight/start/replay/recording publisher | consume same verified bytes; keep all gates |
| Workflow execution fixtures | tailwind_workflow_transport_tests.py | extend actual YAML run-block tests |

### Dream state delta and diagrams audit

Current action-based exact-ID selection/digest plus typed content validation moves to one directly retrieved, receipt-bound current-attempt bundle with specific refusal reasons. The later ideal is evidence-backed adoption or retirement of a useful internal contract, not compulsory publishing graduation. New architecture/data/error/state/deployment/rollback diagrams above cover this slice; existing #798 diagrams remain accurate except the one producer transport edge in the rehearsal and the new receipt edge, which the canonical docs must update with implementation.

### CEO completion summary

| Field | Result |
|---|---|
| Mode / audit | SELECTIVE EXPANSION; old dirty branch preserved, pinned main source read |
| Step 0 / scope proposals | settled S1/A1/C1/D1; no new expansion; five delight candidates already required |
| Architecture | narrow split retained; alternative comparison and honest value wording required |
| Errors | twelve boundary paths mapped; no accepted critical gaps |
| Security | four threat classes mapped; no new credential/dependency; no producer-compromise claim |
| Data/interaction | nil/empty/stale/collision/cancel/install ordering mapped; no accepted unhandled path |
| Quality | reuse seams retained; actual branch complexity awaits implementation |
| Tests | diagram produced; no coverage waivers; tests not run in planning |
| Performance | explicit limits retained; actual hosted resource fit unverified |
| Observability | safe reports/runbook; crash-before-report limit explicit |
| Deployment | first hosted run risk and rollback ownership explicit |
| Future | reversible consumer wiring; provider maintenance debt acknowledged |
| UI | skipped, no UI scope |
| Registries | twelve error/rescue rows; ten failure rows; no accepted silent critical gap |
| Artifacts | CEO summary and plan saved; test/task artifacts below; original design unchanged |
| Outside/native | native five findings; outside unavailable/unverified, no consensus credit |
| Spec loop | two rounds; latest 8/10 NOT PASS; two fixes confirmed, three final edits text-verified, not re-reviewed |
| Lake score | N/A, no coverage-scored user choices |
| Unresolved | plan approval pending; scratch-cleanup taste choice at final gate; implementation card/owner evidence pending |

### CEO decision audit continuation

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| M10 | CEO | Retain approved direct/no-list contract and document action-plus-attempt alternative | Mechanical | P1,P5 | Different coverage; no incident claim needed to state capability | Imply old action lacked digest checking |
| M11 | CEO | State first-run risk and maintainer-owned wiring rollback | Mechanical | P1,P6 | Existing trial and named-owner gate already required | Silent extra activation workflow or publishing |
| M12 | CEO | Keep upstream reconsideration conditional, no graduation deadline | Mechanical | P3,P4 | Avoid dead code without expanding into publishing | New scheduled process / guaranteed adoption |
| M13 | CEO | Preserve unverified external result as missing coverage | Mechanical | P5 | Exit 4 cannot clear a phase | Fabricated cross-model confirmation |
| D1 | CEO | Remove disposable success scratch before final marker | Taste, approved 2026-11-10 | P3,P5 | Limits disk/retention; retaining it could help debugging but broadens artifacts | Retain ZIP by default |

Approval readiness: PASS for scope retention and auto-decided mechanical amendments; final plan approval and taste ratification remain pending. No current user direction has been changed.

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| M4 | CEO | Retain and pass existing separate upload ID | Mechanical | P1,P5 | Clarifies existing authority wire and comparison tests | Another invented authority |
| M5 | CEO | Use canonical full code names throughout | Mechanical | P5 | Exact taxonomy and assertions | Ambiguous shorthand |
| M6 | CEO | Exclude context root from concrete-input overlap | Mechanical | P1,P5 | Preserve relative output semantics and actual input protection | Ban all checkout-relative output |
| M7 | CEO | Define all HTTP status partitions and tests | Mechanical | P1,P5 | Every response must refuse predictably | Unspecified fallback |
| M9 | CEO | Apply final text corrections without spec PASS claim | Mechanical | P5 | Repeated-issue stop; later phase reviews remain separate | Invented re-review |
| M14 | CEO | Auto-accept consistent scope documents for continued review | Mechanical | P6 | /autoplan intermediate document decision only | Claim user approved plan/implementation |

### CEO implementation tasks

- [ ] **CEO1 (P1, human: 2h / agent: 30min)** — Cover every HTTP status partition with exact diagnostic codes. Verify: targeted transport/adapter tests and canonical-document readback.
- [ ] **CEO2 (P1, human: 2h / agent: 30min)** — Verify concrete-input overlap and success scratch cleanup before the marker. Verify: targeted transport/adapter tests and canonical-document readback.
- [ ] **CEO3 (P2, human: 1h / agent: 20min)** — Explain the action alternative, trusted-producer limit and first-trial rollback ownership. Verify: targeted transport/adapter tests and canonical-document readback.

Estimates overlap with the full implementation tasks; do not add them twice. Other reviewed sections emitted no independent new task beyond the source contract. Prior #892 task lists are unrelated work on this reused branch and remain intact in a separate artifact scope.

### Premise receipts

| Premise | Claim | Receipt | Verdict |
|---|---|---|---|
| Existing selection | Current rehearsal passes exact upload ID to download action | .gstack/tmp/autoplan/issue910-20261107/source/.github/workflows/package-artifacts.yml:231 | verified statically |
| Existing content boundary | Existing binding API lacks expected current-attempt argument | .gstack/tmp/autoplan/issue910-20261107/source/tools/ForgeTrust.AppSurface.PackageIndex/TailwindEvidenceWorkflow.cs:35 | verified statically |
| Proposed capability | Direct no-list transport and typed refusals are approved v1 requirements | docs/designs/issue-910-exact-artifact-verification.md:24 | prior user approval |
| Hosted reality | Actual read token capability, archive digest equality and resource fit | docs/designs/issue-910-exact-artifact-verification.md:109 | unverified; collect hosted evidence |
| Implementation entry | Protected retained card and named maintainer | docs/designs/issue-910-exact-artifact-verification.md:117 | unverified; entry gate pending |

REALITY: premise-table pass static and approved claims separated from hosted/entry unknowns; unverified=2 docs/designs/issue-910-exact-artifact-verification.md:109
REALITY: already-done pass pinned current-main source inspected through GitHub API; no artifact-transport implementation, unrelated work preserved .gstack/tmp/autoplan/issue910-20261107/source-manifest.json:2
REALITY: surface-check pass existing verify-tailwind-evidence registered; new mode absent and planned; direct routes checked against official REST docs .gstack/tmp/autoplan/issue910-20261107/source/tools/ForgeTrust.AppSurface.PackageIndex/Program.cs:358
REALITY: binding-decisions pass approved same-attempt/rehearsal/B split and implementation deferral retained; branch CloudTasks decision unrelated docs/designs/issue-910-exact-artifact-verification.md:24
REALITY: numbers pass resource limits are quoted contract values, estimates remain estimates; no performance or incident-rate measurement claimed docs/designs/issue-910-exact-artifact-verification.md:75
REALITY: deferred-asks pass no v1 issue request dropped; prior non-v1 deferrals retained in source plan docs/plans/issue-910-exact-artifact-verification.md:64
REALITY: pr-count pass no ship-policy file exists; default one PR, no staged PR split accepted .gstack/tmp/autoplan/issue910-20261107/ceo-step0.md:23
REALITY: prior-art pass diverge from download action only for approved no-list/typed-refusal contract, not a claimed digest deficiency docs/designs/issue-910-exact-artifact-verification.md:33
REALITY: contract-assumptions pass known action digest and REST API contracts are primary-source checked; hosted compatibility remains pending .gstack/tmp/autoplan/issue910-20261107/source-manifest.json:2
REALITY: falsification-box pass capability verification, not incident diagnosis; no historical Skoolit causal hypothesis accepted docs/designs/issue-910-exact-artifact-verification.md:18
REALITY: stop-reasons pass all refusal categories and required fixtures retained in the binding issue contract docs/plans/issue-910-exact-artifact-verification.md:215
REALITY: reporter-environment pass historical incident class not confirmed; fixture and hosted evidence kept distinct docs/designs/issue-910-exact-artifact-verification.md:109
REALITY: units pass no units/accuracy retuning; byte/time limits retained verbatim docs/designs/issue-910-exact-artifact-verification.md:75
REALITY: perf-table pass no speed or service-load claim; controlled budget tests plus actual hosted resource observations required docs/designs/issue-910-exact-artifact-verification.md:75
REALITY: dependency-fallback pass existing validator stays content authority; no public package/Skoolit predecessor required docs/designs/issue-910-exact-artifact-verification.md:42
REALITY: obligation-tiers pass must-ship full v1 tests/docs, no independent next PR; broader callers deferred docs/designs/issue-910-exact-artifact-verification.md:24

Incident falsification: no causal hypothesis is asserted; diagnosis has no work budget in v1. New predicate evidence would reopen that separate work after Skoolit #1441 settles; absence of evidence leaves the cause unknown. Environment reproduction is class-not-confirmed, never ruled out.

Performance evidence: machine/engine/data size/N/p50/p95/base-branch comparison/pathological/steady-state samples are not measured in this planning run. No units changed and no consuming threshold is retuned. Resource limits are contractual ceilings; the authorized hosted run later records actual runner/version, archive/expanded sizes, elapsed time and disk fit.

### Phase 2 disposition
Design skipped: no UI scope detected. This is not a completed design review.

### DX investigation before scoring

Primary product: repository-local maintainer CLI and workflow; not a public SDK or skill. Mode DX POLISH by /autoplan override. Persona: release/platform maintainer familiar with .NET and GitHub Actions, encountering a blocked nonpublishing Tailwind rehearsal. An AI coding agent is a second reader of structured refusals. The maintainer expects an exact next step and stable authority, tolerates prerequisite setup, and should not reconstruct a signed URL or guess another candidate.

Developer perspective (inferred from inspected docs, not an observed interview): I open the PackageIndex maintainer guide because a rehearsal has stopped. It already points me to Tailwind provenance, whose reference explains protected outputs and why I must not repack an old identity. I can find the source and the existing verification modes, but the new mode does not exist yet. I want to know whether this refusal is an attempt mismatch, unavailable evidence or failed transport before deciding whether a full rerun is authorized. I do not want a successful local test to be described as permission to publish. On my prepared .NET machine, I want a small credential-free card that exercises matching evidence, an earlier attempt and one changed archive byte. It should tell me what each result proves and what still needs a protected hosted run. For real usage I expect the workflow to supply the token and context, not copy a secret into an argument. If my receipt belongs to an earlier attempt, I need a clear instruction to re-run all jobs, with the cost and preserved scope stated. I also need to know whether any bundle was installed before a failure and whether its marker makes it eligible. These are predicted questions from the documented contract; current onboarding times and user confusion have not been measured.

Benchmark clock: prepared supported checkout with .NET 10 already installed, starting at the new README recipe, through reading, locked test-project restore/build and the first understood three-case fixture result. Include uncached restore/build in the measured interval and record caches/hardware; SDK installation is a separately reported prerequisite. This is not a hosted-acceptance clock or pure command timer. Current time is unknown. Auto-decided target: competitive, under five minutes, an unmeasured target rather than release SLO. If actual verification exceeds it, record why; do not weaken required checks to hit it.

| Tool | Start → useful result | Time / evidence | DX choice | Source |
|---|---|---|---|---|
| AppSurface #910 | Prepared checkout/recipe → understand three fixture outcomes, including restore/build | Unknown; target under five minutes | One credential-free focused card, separate hosted evidence | Approved design Verification; current maintainer README Tailwind section |
| download-artifact | Configured Actions workflow/permissions → extracted artifact | Unknown; docs/source only, not comparable human onboarding | Exact-ID input and digest option; workflow handles runtime | https://github.com/actions/download-artifact/tree/v8.0.1 |
| gh run download | Installed/authenticated CLI and named run → downloaded artifacts | Unknown; docs only; result omits this receipt/attempt contract | Familiar command/help and directory output | https://cli.github.com/manual/gh_run_download |
| GitHub REST | Authenticated request and IDs → direct metadata/ZIP response | Unknown; docs only, incomplete verification endpoint | Explicit request identity and status | https://docs.github.com/en/rest/actions/artifacts |

No peer timing claim is imported. Aside readiness was unavailable earlier; primary-source web research supplied these choices. Prior branch DX reviews concern #865/#892, not this feature; their scores are not a trend baseline for #910 (one also used a different/absent rubric).

Magical moment/vehicle: the already approved three-case card makes a refusal understandable before using credentials. Reuse the planned test project and existing command-dispatch seams, not a new probe command, hosted playground, generator or service. Documentation includes a locked restore command followed by a filtered test command and the three exact expected outcomes. This does not replace the genuine protected acceptance-card/owner entry gate or later hosted acceptance.

| Stage | Maintainer does | Evidence / friction disposition |
|---|---|---|
| Discover | PackageIndex README → canonical Tailwind reference | Existing link at README Tailwind section; add new mode/recipe under existing entry |
| Install | Uses supported .NET SDK, locked restore and exact source | Existing non-packable net10.0 tool; document prerequisites and source stamp |
| Hello world | Runs focused three-case fixture card | New tests are planned; no token/hosted authority claim; include exact filter/output count |
| Real usage | Protected workflow supplies receipt, current context and GH_TOKEN | Current guards/permissions exist; add one mode call and retain no-replacement producer consumption |
| Debug | Reads safe code/expected/observed/nextAction and summary | Required full taxonomy; make partial output and authorized recovery explicit |
| Upgrade | Updates repository-local tool from same checkout | No public migration; other modes and publishing enforcement unchanged; document rehearsal-only refusal difference |

First-time confusion report (simulated timestamps, not measurements): T+0:00 open maintainer README; its Tailwind section already links the reference. T+0:30 choose the new rehearsal-only fixture recipe; it must distinguish fixture card from a genuine receipt. T+1:00 read prerequisites and run locked restore; elapsed completion is unknown until measured. T+2:00 run the focused card and read the three named expected results, if build/setup has finished. T+3:00 understand the earlier-attempt instruction and hosted evidence limit; this is a target scenario, not an observed success. No current runtime output is invented.

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| M15 | DX | Maintainer CLI persona | Mechanical | P6 | Existing tool and approved caller establish the audience | Public SDK personas |
| M16 | DX | Prepared-checkout under-five-minute target, unmeasured | Mechanical | P1,P3 | Include reading/restore/build and report actual result later | Invented observed timing |
| M17 | DX | Existing three-case card and complete recipe | Mechanical | P4,P5 | Reuse the approved test vehicle | New probe/sample service |
| M18 | DX | DX POLISH | Mechanical | P5 | Autoplan override | Scope expansion |

### DX review passes and decisions

Methodology loaded through EOF: 1–600, 601–1200, 1201–1800, 1801–1855; Hall of Fame sections loaded separately at each pass. Scores below judge the amended plan, not running code or measured onboarding. No prior #910 DX score exists; unrelated #865/#892 reviews are not a trend.

Native review completed matching INPUT dx 4f5fbbc88eb6d1f06fd2258cb4b5112401400951b0159a85ea793f814495eec7. It reported five findings: four High and one Medium. Three High error-contract gaps are accepted; one High required-ID omission is rejected against command contract line 159, where the option is present. The Medium fixture recipe gap is accepted within the already approved credential-free instructions. No Critical finding. Outside DX completed: CLI exit 0, validator exit 3 (findings), quality 6/10, twelve findings: three P1, six P2, three P3. Raw session/modelUsage retained, model claude-fable-5-1. No result is inferred from CEO coverage.

### Pass 1: getting started, 7/10 before amendment

The existing maintainer README already links the canonical Tailwind reference, and the non-packable net10.0 tool/test assembly provide the local vehicle. The plan required a recipe but did not name its exact focused test filter or expected terminal evidence. Make the approved three-case acceptance card a named test with a locked restore/test sequence, no credentials and explicit outcomes; this is completion of existing documentation/testing scope, not a public probe. A 10 would require a measured fresh maintainer journey reaching the understood outcomes within the declared clock; it remains unmeasured, so the revised planning score is 8/10.

Planned recipe, to be verified once implemented from a source checkout with .NET 10 installed:

```text
dotnet restore tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj --locked-mode
dotnet test tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~TailwindArtifactTransportTests.AcceptanceCard
```

The `AcceptanceCard` named test covers the matching bundle/marker, earlier receipt attempt with zero sends, and one-byte mutation before extraction. It prints three safe named outcome lines through the test framework and asserts exit/status/code/marker state through existing command seams; it uses ephemeral fixture paths and removes them, so it does not create a fake retained producer receipt. Exact fixture builders live in the two planned test files. Document expected success for the test suite separately from the negative CLI invocation exits. Restore/build duration is included in any later TTHW measurement; SDK install, hosted token and protected receipt provenance are not claimed.

### Pass 2: CLI/API, 8/10 before and after

The approved mode and flags extend an existing command consistently. All identity inputs are explicit because authority cannot come from guessed names, latest selection or a mutable environment default. Fixed host/workflow/event/producer policy and budgets are policy, not missing escape hatches; no unsafe override is added to raise a score. The separate `--producer-artifact-id` is already in the full command contract and tied to its existing job output; reject the native omission claim with source receipt. A 10 would include verified help and copy-paste examples from implemented code; the plan names those checks but cannot establish them yet.

### Pass 3: errors/debugging, 6/10 before amendments

Native CAP6 recovery/context/trace findings are valid: stable codes and a generic nextAction do not define safe recovery for every environment/partial-work path. Complete them using the existing diagnostic record, summary and stderr surface, without another trace artifact or raw stack dump. Every code's canonical docs anchor must explain what failed, its safe first/last-line next action, allowed recovery and known output state. The normal CLI failure prints the same safe code, stage, relevant allowlisted identity, report path and next action to stderr with exit 1. Invalid report paths use the already approved fixed safe fallback; diagnostic-channel failure never changes the original result.

| Error path | Class | what | fix | recovery | code | context | trace | redaction | channel | Before cap |
|---|---|---|---|---|---|---|---|---|---|---|
| Earlier receipt attempt | preflight validation | pass | pass, exact all-jobs instruction | N/A before work | pass | N/A before work | N/A expected | pass | pass | none |
| API 403/429 | dependency | pass | generic, incomplete | fail | pass | fail | N/A expected | pass | pass | CAP6 recovery/context |
| Output move/report failure | partial-work | pass | generic, incomplete | fail | pass | fail | N/A expected IO | pass | pass | CAP6 recovery/context |
| Unexpected classified exception | unexpected | pass | generic, incomplete | fail | pass | fail | fail | pass | pass | CAP6 recovery/context/trace |

Proposed corrected example lines are not captured runtime output:

```text
artifact-attempt-mismatch: receipt attempt 2 differs from expected 3; no HTTP sent. Next: re-run all jobs so the producer emits a receipt for the current attempt.
artifact-rate-limited: artifact metadata refused by GitHub API (HTTP 403); no bundle installed. Next: maintainer must observe GitHub's rate-limit reset before authorizing a new full rehearsal; no automatic retry.
artifact-output-failed: verified bundle installation failed; success marker absent. Next: inspect invocation-owned output state and repair local access/disk configuration before requesting a new full rehearsal.
artifact-api-unavailable: unexpected local transfer failure; safe frames GitHubActionsArtifactVerifier.DownloadAsync -> TailwindArtifactTransportWorkflow.RunAsync. Next: provide the safe report and run/attempt/artifact identity to the maintainer; do not retry until output state is reconciled.
```

Safe context in existing reports/summary: verifier source stamp, fixed GitHub API version, stage/operation, HTTP status if present, safe bound identity, limit name for budget failure, environment variable name `GH_TOKEN` without value, and allowlisted cause kind such as network, timeout, malformed-json, io, access, invalid-archive or cancellation. Do not copy HttpRequestException.Message, arbitrary nested messages, URLs, headers, response bodies, raw path values or remote stack details. Unexpected local failures add compact allowlisted PackageIndex type/method frames to the existing message/report; no source paths, arguments, locals or unfiltered exception strings. A dedicated raw stack log or verbose transport mode is not introduced. Test injected secret/URL-bearing exception and inner-exception sentinels against every report/stderr channel.

Recovery policy is an instruction/escalation, never new retry automation. Every hosted refusal has zero automatic retries and zero unattended manual reruns; the maintainer must inspect the diagnostic before authorizing a fresh full rehearsal with new owned directories. Input/receipt/binding/identity/digest/archive problems require correction or investigation of the named trusted source; no alternate ID, repack or relaxed limit is suggested. Rate-limit/server/network/download problems require maintainer confirmation that the external condition has cleared. Permission/404/expired evidence require the maintainer to establish access or supply authorized valid evidence; the tool cannot distinguish 404 absence from concealment. Path/output failures require local ownership/disk/access repair and reconciliation of invocation-owned leftovers. Cancellation requires caller intent before restarting. Unknown/crash state requires inspecting exit and final marker plus owned output eligibility; no report means no success proof. After any new failure, stop again rather than blindly loop. This deliberately bounds automated action at zero and escalates policy to the existing maintainer authority.

Side effects: before output creation, none. Once staging exists, partial local invocation-owned scratch may remain on failed cleanup. If installed but no success marker/exit 0, the directory is ineligible; identify it as partial/ineligible, not usable. A killed process has unknown cleanup state and must reconcile marker/exit before any consumption. No remote write or publication occurs in this mode. These distinctions are required in the existing safe diagnostic fields/message, not a second reporting schema. Revised error planning score 8/10 after complete mapping and tests; real implementation wording/redaction remains to verify.

### Pass 4: documentation, 7/10 before, 8/10 after

Canonical trust/schema/command/refusal reference, maintainer discovery and release-ops recovery are the right existing destinations. Add the exact fixture recipe and separate examples for matching/attempt/byte failure; distinguish a test-suite pass from an individual command refusal. Name source identity, no-public-package boundary and the unknown hosted token/archive evidence in those pages. A 10 requires tested examples and link validation; these are implementation checks, not proven by this document. No docs site redesign or interactive playground is warranted.

### Pass 5: upgrade, 9/10 before and after

This is repository-local and additive; no NuGet consumer migration or codemod applies. Existing command modes keep their behavior, and the one protected rehearsal changes its producer transport and same-attempt refusal behavior. The runbook must scope the all-jobs instruction to this v1 mode so maintainers do not apply it as a rewrite of stable/prerelease recovery. A 10 would require actual upgrade/regression execution; planned mode/permission tests cover that without inventing another compatibility promise.

### Pass 6: environment, 8/10 before and after

Locked source-stamped build, HttpMessageHandler/TimeProvider seams and actual temporary ZIP fixtures fit the repository and avoid hosted credentials for local tests. Linux is the integration caller; archive case/link rules are cross-platform conservative, with no claim of new native-host transport coverage. The tool does not need editor integration, TypeScript types, a watch service or Docker for this unit/adapter path. A 10 requires checking supported test hosts and the real protected token path; those remain implementation/hosted evidence.

### Pass 7: community, 8/10 before and after

The existing repo, issue #910 and maintainer docs are sufficient discovery and feedback for one internal caller. No public package, extension registry, new support channel or pricing surface is added; those would cross the selected scope. A 10 for this slice means another maintainer can reproduce a safe card and file a useful report without secrets, which the planned recipe and diagnostic context enable but have not yet demonstrated. No additional task is emitted from this pass.

### Pass 8: measurement, 7/10 before, 8/10 after

Keep the declared README-to-understood-three-case clock with restore/build included and cache state recorded. The implementer should record one actual local observation with hardware/SDK/cache/result and explicitly call it one observation, not an adoption or latency improvement. The separately authorized hosted run records genuine API/token/digest/resource behavior; neither replaces the other. No recurring process, telemetry or release-gate timer is added. A 10 needs repeated human evidence rather than another dashboard; revised 8/10 reflects the specified measurement method only.

## DX Review Scorecard

| Dimension | Before | Amended plan | Prior/trend |
|---|---|---|---|
| Getting started | 7 | 8 | No comparable #910 baseline |
| API/CLI/SDK | 8 | 8 | No comparable #910 baseline |
| Error messages | 6 | 8 | agent-ready-v1; implemented text unverified |
| Documentation | 7 | 8 | Recipe specified, not executed |
| Upgrade path | 9 | 9 | No public migration |
| Developer environment | 8 | 8 | Hosted capability pending |
| Community | 8 | 8 | Internal caller scope |
| Measurement | 7 | 8 | Human timing unmeasured |
| Overall arithmetic mean | 7.5 | 8.125 | Plan judgment only |

TTHW: observed unknown → target under five minutes on declared prepared-checkout clock. Competitive rank is a target only. Magical moment designed via the existing three-case fixture card. Product maintainer CLI/workflow, mode DX POLISH. Zero friction is conditional on supported SDK; learn-by-doing, uncertainty, code-in-context and agent reader are specified; fixed policy has no unsafe override escape hatch.

### DX implementation checklist

- [ ] Add and verify the exact credential-free `AcceptanceCard` recipe and three named outcomes.
- [ ] Measure the declared local clock, including restore/build, and report cache/SDK/hardware and limits.
- [ ] Keep all required identity flags, source stamp, noninteractive exit and help coherent.
- [ ] Map every refusal to safe next action, recovery/escalation, output state, context and docs anchor.
- [ ] Add allowlisted compact local frames for unexpected failures in existing safe reports; test redaction and first-failure preservation.
- [ ] Preserve other modes and stable/prerelease recovery; scope new rerun guidance to the rehearsal.
- [ ] Verify README/reference/release-ops links and examples together.
- [ ] Record hosted acceptance separately after authorization; keep issue open while pending.

No public install/free-tier/types/codemod/community-channel changes apply to this internal CLI. They are not missing product commitments. No new TODO proposal is justified; all accepted gaps are within existing v1 tests/docs and diagnostic obligations.

### DX decisions

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| M19 | DX | Verify existing artifact-ID flag and reject omission claim | Mechanical | P5 | Actual command already includes the ID flag | Duplicate flags or false finding |
| M20 | DX | Complete per-code recovery/output-state/context on existing channels | Mechanical | P1,P5 | Every refusal must guide bounded authorized recovery | Unbounded agent retry and generic nextAction |
| D2 | DX | Use allowlisted compact frames in existing safe report for unexpected errors | Taste, approved 2026-11-10 | P1,P5 | Compact safe context completes existing debugging channels | Separate trace artifact increases retention/redaction surface |
| M21 | DX | Name AcceptanceCard and complete the existing fixture recipe | Mechanical | P4,P5 | Maintainers need copy-paste commands and expected outcomes | New probe command/public sample |
| M22 | DX | Record one local timing observation without a new telemetry gate | Mechanical | P3,P5 | One later observation is sufficient for the declared target | Invent measured onboarding speed |
### Outside DX dispositions and consensus

| Finding | Disposition |
|---|---|
| P1 transient rerun trap | Accept: every authorized hosted recovery is a full all-jobs rerun; failed-jobs-only will mismatch. Keep no automatic retries. |
| P1 silent empty receipt | Accept: receipt construction fails producer job nonzero on invalid upload outputs. |
| P1 emitted redaction | Accept: scan all report/stdout/stderr channels for token/URL/header/body/inner-exception sentinels. |
| P2 unknown exception/fallback | Accept within existing taxonomy: boundary mapping plus last-resort artifact-output-failed with unexpected-local cause and safe escalation; fixed stderr prefix. Eng must inspect the boundary, not mask arbitrary errors as permissions. |
| P2 docsUrl | Accept per-code canonical anchors for new mode; preserve old-mode output. |
| P2 Actions visibility | Resolve through mandatory stderr failure channel visible in job logs. Additional annotations/step-summary automation are not needed for that declared outcome and are not added. |
| P2 marker validation | Accept explicit jq schema/status/noneligible/identity/digest check before consumption. |
| P2 inapplicable options | Accept reject ignored input selectors, require token by name only; keep common help/options. |
| P2 local source stamp collision | Accept documented fixture checkout/internal test seams with explicit no_claim; real default source guard regression remains required and no CLI/environment bypass added. |
| P3 target observation | Accept one local observation as approved fixture verification evidence, no recurring telemetry or timing gate. |
| P3 volumes | Accept help/pitfall explanation and safe cause, no raw roots that could leak input. |
| P3 upload normalization | Accept explicit fixtures: bare lowercase hex accepted; already-prefixed/uppercase/short rejected under pinned upload output contract. |

| DX dimension | Native | Claude | Consensus |
|---|---|---|---|
| First local result | Missing exact recipe | Missing exact recipe/clock seam | CONFIRMED gap, amended |
| Naming/interface | Consistent; missing-ID claim false | Mode options need explicit rejection | N/A, distinct findings |
| Actionable errors | Recovery/context/trace gaps | Rerun/fallback/redaction gaps | CONFIRMED gap, amended |
| Findable complete docs | Planned reference needs exact example | Reference/anchor/recipe gaps | CONFIRMED gap, amended |
| Upgrade safety | Narrow policy appropriate | No explicit dimension verdict | N/A |
| Environment friction | Not fully assessed independently | Fixture/source-stamp uncertainty | N/A |

Consensus confirms three review gaps, not running-code correctness or measured onboarding. No User Challenge changes the owner's direction. The three High outside gaps and three valid High native gaps are addressed in the current plan obligations; they have not been independently re-reviewed as DX, and Eng will review the final amended plan last.

| M23 | Define boundary-specific exception mapping and safe last-resort output refusal | Mechanical | P1,P5 | Preserve taxonomy and unexpected-local cause, not a new internal-error code |
| M24 | Fail invalid receipt emission and explicitly gate marker/irrelevant options | Mechanical | P1,P5 | Complete approved authority/consumption contract |
| M25 | Require emitted redaction, per-code anchors and pinned digest fixtures | Mechanical | P1,P5 | Verify approved secrecy/schema contract at outputs |
| M26 | Use stderr visibility, retain simple report uploads | Mechanical | P3,P4 | Do not add duplicate annotations to fix an already visible failure channel |

<!-- autoplan-accepted:dx -->
- Preserve every prior CEO/source requirement. Missing or malformed upload artifact-id or artifact-digest must fail the guarded producer receipt run block nonzero with a fixed safe message naming the output; never let the producer succeed with a silently empty receipt. Test missing/empty/leading-zero ID and digest variants: bare lowercase 64-hex is accepted and prefixed once; already-prefixed, uppercase and short upload digest outputs are rejected under the pinned output contract. Do not change the receipt schema to accept an upstream output change silently.
- Before any publish-preflight or later bundle use, the workflow must check command exit 0 and jq -e verify the final record's schema, succeeded status, releaseEligible false, repository/name+ID, run, current attempt, source, exact artifact ID and verified digest against its trusted context/receipt. A missing, malformed or conflicting marker blocks consumption; no replacement download. Execute fixture run blocks and remove/break the marker as regression cases.
- The new mode requires its complete existing command contract including --producer-artifact-id from the separate existing producer job output; it never derives expected ID solely from the receipt. Refuse inapplicable input-selection options such as --artifacts-input or --producer-subject with artifact-input-invalid naming only the option; accept documented common help/options, preserve other modes, and require GH_TOKEN without ever printing its value. Parameterize these cases and mode/help consistency.
- Complete the credential-free recipe in the existing provenance reference and link it from the PackageIndex README. From a prepared checkout with .NET 10, run dotnet restore tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj --locked-mode, then dotnet test tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~TailwindArtifactTransportTests.AcceptanceCard. Implement that stable parameterized test as exactly three cases, expected Passed: 3; the matching invocation exits 0 and has its marker, the old-attempt invocation exits 1 with zero HTTP, and the changed-byte invocation exits 1 before extraction. The suite succeeds by asserting both expected refusals, not by converting their command exits to success. Use generated temporary fixture inputs/checkouts and intentionally internal test seams, never the maintainer's dirty working tree as evidence or production GH_TOKEN. No test-only CLI flag or environment bypass is allowed. Keep a real production source-stamp/default-validator regression outside the mocked card; the card's no_claim explicitly excludes genuine producer authority and hosted source/token proof.
- Normal transport refusals emit a safe first-line stderr prefix artifact-transport failed: <canonical-code>, followed by safe stage/context and an authorized final-line next action; exit is 1. Report-path failure uses exactly that fixed one-line prefix with the original canonical code and no untrusted path/value. Keep structured diagnostics.json and summary.md; never fall through to the existing raw exception-message formatter for the new mode. Use canonical per-code anchors in docs/tailwind-artifact-provenance.md for docsUrl, not issue #798; verify each emitted code's anchor exists.
- Report safe context for environment/dependency/partial/unexpected paths in the existing diagnostic channels: source-stamped verifier revision, fixed API version, operation/stage, HTTP status when present, validated canonical identity and limit name, relevant variable name GH_TOKEN without its value, and allowlisted underlying cause kind. Usage validation before work needs only operation/constraint, safe fix, code, redaction and failure channel. For unexpected local failures, include compact allowlisted PackageIndex type/method frames in the existing report/message, with no arguments, locals, filesystem source paths, raw exception/inner-exception messages, signed URLs, headers or bodies. No separate raw trace artifact, verbose flag or public schema/package is added; document any internal diagnostics shape changes fully and preserve old-mode output behavior.
- Keep the existing refusal taxonomy. Known exceptions map at their owning boundary: invalid path/argument before work to artifact-input-invalid, malformed API content to artifact-api-response-invalid, transfer/HTTP to the defined API/download code, invalid/unsupported ZIP to artifact-archive-unsafe, binding to artifact-producer-binding-invalid, and output IO to artifact-output-failed. A last-resort unexpected local operational failure that prevents producing required verified output uses artifact-output-failed with safe cause kind unexpected-local and maintainer escalation, never misreported as denied permission or corrupt remote input. Preserve caller cancellation and the first typed failure separately; process-fatal termination remains the documented no-report limit. Add injected unexpected-exception and double-failure fixtures through internal supported seams.
- For every failure family, scan diagnostics.json, summary.md, stdout, stderr, success record if present and all report-directory files for sentinels representing a token, signed query, Location value, raw body and nested exception text; none may occur. Distinguish this emitted-output redaction proof from redirect-header isolation. Assert error context, next action and docs anchor as well as code and output eligibility.
- Recovery is bounded by zero automatic retries and zero unattended new hosted runs. Every hosted rerun requires the existing maintainer's authorization and fresh owned paths; after any refusal stop again rather than loop. Before a new invocation, reconcile the prior command exit and final marker plus any invocation-owned leftover directory. Before work, bundle side effects are none; after staging/install, side effects may be partial and the bundle remains ineligible without exit 0 plus marker; after crash/failed cleanup they are unknown until reconciled. Reports are diagnostic side effects, never release authority.
- artifact-input-invalid next action: correct the named argument or GH_TOKEN binding without printing secrets; ask the maintainer before a new full rehearsal.
- artifact-receipt-invalid next action: repair protected producer receipt emission/schema; never edit artifact-contained data to manufacture authority; ask the maintainer before a new full rehearsal.
- artifact-receipt-binding-mismatch next action: investigate the named protected receipt/context field; do not substitute an ID; ask the maintainer before a new full rehearsal.
- artifact-attempt-mismatch next action: ask the maintainer to authorize re-running all jobs; re-run all jobs so the producer emits a receipt for the current attempt. Re-running failed jobs keeps the older receipt and will refuse again.
- artifact-repository-mismatch next action: stop and verify protected repository/head-repository authority; no cross-repository fallback; ask the maintainer before a new full rehearsal.
- artifact-run-mismatch next action: stop and verify protected run authority; no latest-run selection; ask the maintainer before a new full rehearsal.
- artifact-workflow-mismatch next action: stop and verify protected workflow path/event; do not relax the manual-main policy; ask the maintainer before a new full rehearsal.
- artifact-source-mismatch next action: restore/build the exact authorized source and investigate conflicting metadata; never relabel a package; ask the maintainer before a new full rehearsal.
- artifact-not-found-or-inaccessible next action: ask the maintainer to establish access and retention for the exact ID; preserve missing-versus-inaccessible ambiguity and do not choose another candidate.
- artifact-expired next action: stop and ask the maintainer for a new authorized full rehearsal; do not recover with a different artifact under the old receipt.
- artifact-permission-denied next action: ask the maintainer to verify GH_TOKEN wiring and read permissions; never print credentials or broaden them automatically.
- artifact-rate-limited next action: ask the maintainer to establish that GitHub's limit has cleared before authorizing a new full rehearsal; re-run all jobs so the producer emits a current-attempt receipt, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-api-unavailable next action: ask the maintainer to confirm API/network recovery and reconcile local output before authorizing a new full rehearsal; re-run all jobs, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-api-response-invalid next action: preserve safe metadata context and ask the maintainer to investigate the pinned API contract; no retry of malformed content or guessed API version.
- artifact-metadata-digest-mismatch next action: stop and investigate the original upload/API digest authority; no digest rewrite, alternate ID or repack under the same identity.
- artifact-bytes-digest-mismatch next action: stop and investigate transfer/archive integrity using safe identity/digest evidence; no extraction or replacement candidate.
- artifact-download-failed next action: ask the maintainer to establish transfer recovery and reconcile owned scratch before authorizing a new full rehearsal; re-run all jobs, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-redirect-invalid next action: stop and ask the maintainer to investigate the exact artifact download path using safe stage/status only; never follow an unsafe destination or log its signed URL.
- artifact-budget-exceeded next action: ask the maintainer to inspect the named byte/time/request/entry/depth/expansion bound and cause; do not raise it ad hoc. If a new rehearsal is authorized, re-run all jobs, because failed-jobs-only retry will cause artifact-attempt-mismatch.
- artifact-archive-unsafe next action: stop and ask the maintainer to investigate the protected archive structure; no extraction recovery, path rewriting or repack under the old identity.
- artifact-producer-binding-invalid next action: preserve the safe underlying diagnostic and ask the maintainer to investigate subject/manifest/package binding; no replacement producer download.
- artifact-output-failed next action: reconcile invocation-owned partial output and inspect safe cause kind; ask the maintainer to repair disk/access/same-volume configuration for expected IO or investigate the stamped tool for unexpected-local failure. Any authorized new rehearsal must re-run all jobs with fresh paths.
- artifact-cancelled next action: respect caller intent, reconcile owned output eligibility, and ask the maintainer before authorizing a new full rehearsal; no automatic restart.
- Document work/staging and bundle destination same-volume requirements in help and the existing pitfall guide, using safe root descriptions rather than raw untrusted path values in errors. Retain the cross-volume refusal and no-copy fallback.
- During later authorized implementation verification, record one README-to-understood-card elapsed observation including reading/restore/build, supported SDK/hardware and cache state. The target is under five minutes from the declared prepared-checkout start, not a measured guarantee; report limits/causes if missed without weakening tests. SDK installation and genuine protected hosted acceptance remain separate observations; no telemetry, recurring process or new timer release gate is introduced.
<!-- /autoplan-accepted:dx -->

### DX implementation tasks

- [ ] **DX1 (P1, human: 2h / agent: 30min)** — Complete safe recovery, context, local trace and emitted-channel redaction; verify with focused C#/workflow fixtures, report sentinel scans and canonical recipe readback.
- [ ] **DX2 (P1, human: 1h / agent: 20min)** — Fail invalid receipt emission and check the exact success marker before consumption; verify with focused C#/workflow fixtures, report sentinel scans and canonical recipe readback.
- [ ] **DX3 (P2, human: 1h / agent: 20min)** — Pin the credential-free three-case card, help/options and scoped rerun recipes; verify with focused C#/workflow fixtures, report sentinel scans and canonical recipe readback.

Task estimates overlap the source implementation estimate and CEO work; do not add twice. No independent task from upgrade/community passes.

### DX post-outside verification
After consuming the completed outside review, all eight passes were rechecked against the current accepted DX obligations. Getting started now names the fixture filter, three case results, source-fixture limits and prepared-checkout clock. CLI preserves the actual artifact-ID flag and adds explicit irrelevant-option refusal. Error recovery/context/trace and emitted redaction have per-code obligations and escalation bounds without new retry automation. Docs require code anchors and a single canonical recipe, while upgrade rules retain stable/prerelease behavior. Environment/test seams preserve real production defaults and separate mocked source proof. Community remains the existing maintainer repo/issue surface. Measurement remains one disclosed observation with no new telemetry gate. Current report scores describe this amended plan; no implementation test or hosted evidence is claimed.

### Engineering scope challenge and preparation

Target is the #910 implementation plan at docs/plans/issue-910-exact-artifact-verification.md, based on the approved design and amended by CEO/DX. This is FULL_REVIEW, scope accepted as-is; no implementation or new test suite is built in this review. Current source/test reads include Program mode routing and its injected-publisher pattern, TailwindCommandOptions, TailwindEvidenceWorkflow parsing/path/writer/binding helpers, TailwindSourceIdentity, TailwindArtifactProvenance, actual TailwindCliDispatchTests/TailwindEvidenceWorkflowTests and the executable YAML run-block suite. XUnit and Python unittest are established; no new test dependency is needed. Relevant source history includes #821's package-byte binding fix, with no selected-path revert found in the inspected history.

Complexity selector: original arrangement is two concrete runtime components (generic transport verifier and Tailwind adapter), four new implementation/test files plus seven integration/docs files; a minimal shared-helper change may add another existing file. Smaller arrangement combines transport and Tailwind policy in one component, reducing one implementation file at equal behavioral coverage but coupling tests and authorities. User D4 already chose the split, and no new evidence invalidates that choice. Auto-resolve the structural gate by retaining original arrangement under that exact approval and P5. All features, bounds, diagnostic fixes and tests remain fixed; no pending feature-cut remedy. No additional public package, service, environment override or plugin abstraction is introduced.

Source-grounded findings before architecture review:

1. [INFORMATIONAL] (confidence: 9/10) TailwindEvidenceWorkflow.cs:994–1005 has a private fresh-directory helper that calls `Directory.CreateDirectory(parent)` before walking reparse ancestors. The new mode must validate all existing ancestors before it creates any parent or output, rather than copying this ordering. This is a proposed caller constraint, not a claim that the current protected workflow was exploited. Disposition: accept exact new-mode validation/owned-cleanup requirement, retain old callers' behavior and tests.
2. [INFORMATIONAL] (confidence: 9/10) The runtime's ZIP entry collection materializes central-directory entries; checking Entries.Count afterward does not enforce the v1 entry allocation bound before materialization. .NET 10 ReadCentralDirectory adds an entry before checking it against the declared expected count. Preflight bounded EOCD/ZIP64 count/offset metadata before materializing Entries, then rely on runtime structural checks plus counted extraction; no general ZIP parser or dependency is needed. Disposition: accept a small envelope preflight in the adapter with boundary, ZIP64 and corrupt-count fixtures.
3. [INFORMATIONAL] (confidence: 9/10) ResponseHeadersRead completes before the content body and HttpClient.Timeout does not cover later body reads. The approved header/inactivity/overall limits therefore require explicit linked stage tokens and read-time activity deadlines, not only HttpClient.Timeout. Disposition: retain the existing approved limits with explicit mechanism and controlled-clock header/body/extraction tests.
4. [INFORMATIONAL] (confidence: 8/10) Source identity is a real stamp/clean-checkout guard, not just an argument parser. The credential-free card must use controlled fixture checkouts with matching build identity or intentionally supported boundaries, preserve a real default-validator regression and never add a CLI/environment source bypass. Program already supports internal injected workflow delegates for dispatch tests. Disposition: use that established pattern for dispatch wiring, real validator coverage separately; no new source-authentication guarantee is claimed by mocks.

Primary references: https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0 ; https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/ZipArchive.cs ; https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices . Search ran through primary-source web fallback because Aside was unavailable. Layer 1: HttpClient, ZipArchive, SHA256 and existing source/content validators. Layer 2: official timeout/archive guidance. Layer 3: enforce the declared application budgets before library work that could materialize over-budget input. Do not infer hosted compatibility or incident causality from these reads.

What already exists: producer needs outputs, protected workflow guards/permissions, source-stamped locked build, strict JSON/hash/create-new writer helpers, typed subject/manifest/package binding and CLI/workflow test patterns. Reuse identical behavior; do not extract similar-looking policies with different contracts. Existing TODOS #798 retention/attestation work remains deferred and does not block this internal trial. Distribution is the same non-packable tool from exact source, not another release channel. Hosted acceptance and the card/maintainer entry gate remain open evidence, not new predecessors implemented here.

Scope record: feature answers S1/A1/C1/D1 retained from user; structure original arrangement B retained from D4 and /autoplan P5; accepted scope complete current-attempt rehearsal contract; no feature cut; pending implementation evidence card/owner and later hosted trial. The plan can be reviewed without pretending those evidence gates are satisfied.

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| M27 | Eng | Retain original verifier/adapter arrangement | Mechanical, prior approval | P5 | User D4 already chose the meaningful responsibility seam | Reopen combined component without new evidence |
| M28 | Eng | Validate existing ancestors before output-parent creation | Mechanical | P1,P5 | Required no-link/no-unowned-write contract | Blindly copy private helper ordering |
| M29 | Eng | Preflight bounded ZIP count envelope before Entries | Mechanical | P1,P3 | Enforce existing entry bound before allocation | New archive dependency/full parser |
| M30 | Eng | Explicit linked header/body/inactivity/overall cancellation | Mechanical | P1,P5 | Required stream budgets exceed HttpClient header scope | Rely on one HttpClient timeout |
| M31 | Eng | Preserve real source guard and use existing internal dispatch pattern | Mechanical | P1,P4 | Fixture success must not create a production bypass | Test-only CLI/environment override |

<!-- autoplan-accepted:eng -->
- Retain all prior source/CEO/DX obligations and the four preliminary engineering requirements below. Approval remains planning only. Before implementation authorization, the sanitized protected acceptance card must record exact artifact/run/attempt/source/upload-digest values and each protected source, and a named maintainer must own the first hosted run. That assignment is completed on 2026-11-10 in [the protected card](issue-910-protected-acceptance-card.md): run 36547070332, attempt 1, artifact 11023576342, source 10d375c6942fc650cc64f64e0748add77540d47c, repository ID 1035215851, archive digest sha256:27151d76524a22cd8126f5dc658295c16cf6d869930ccaf8a3f97d97e14be6fd, with protected context and exact upload-step log sources. Andrew Krock (@akrock) is explicitly assigned by the user. Current exact-ID metadata reports retained/unexpired; it is corroboration, not attempt authority. The normalized card record is a historical planning transcription, not a newly emitted v1 needs output. This completes evidence/owner selection only; implementation and the future hosted trial remain separately authorized.
- For new-mode path validation, inspect the nearest existing ancestor and every existing ancestor for links/reparse points before creating any parent/output; refuse overlap and pre-existing targets first. Track each path this invocation actually creates for owned cleanup. Do not copy the older private helper's create-parent-before-check ordering or change unrelated callers' behavior. Test a linked ancestor with a missing child and assert no outside path is created. Checkout-relative outputs are allowed only outside the existing TailwindSourceIdentity watched input paths; refuse work, report, staging or bundle locations inside those paths as artifact-input-invalid before HTTP. Keep legitimate artifacts/ or runner-temp outputs, concrete-input disjointness and repeated real source validation. Reuse TailwindTestPaths.TemporaryRoot for fixture paths, including its existing macOS /private alias handling; do not weaken production no-link checks or silently resolve operator-supplied symlinks.
- Before ZipArchive.Entries materialization, read only bounded EOCD and, when present, ZIP64 envelope metadata to reject declared entry counts beyond 100,000 and invalid/split/out-of-archive offsets. Keep the envelope check small and internal to the adapter; use .NET ZipArchive for structure/decompression, retain its count consistency checks, and count actual extracted entries/bytes. Do not add a dependency, a full archive parser or a new resource limit. Cover standard ZIP and ZIP64 valid cases, exact/over entry bound, malformed/missing/truncated envelope, misleading declared count and unsupported multi-volume archives. A preflight count bound uses artifact-budget-exceeded; malformed/unsupported structure uses artifact-archive-unsafe.
- Configure production transport for manual redirects, no ambient/default credentials or cookies, no automatic decompression, and per-request Authorization only on the validated API requests; do not put Authorization in shared DefaultRequestHeaders. Read raw response bytes with ResponseHeadersRead and explicit header/body-inactivity deadlines under the single overall TimeProvider-backed budget. Count actual bytes, ensure storage follow-ups carry no credentials and classify caller cancellation separately from budget cancellation. Pass the overall deadline through extraction, source/content validation and output installation; keep cleanup/reporting best effort without changing the first failure. Test header stall, body stall/dribble, caller/deadline race, raw encoding/hash fidelity, no-token redirects and disposal/cancellation paths. The test-only manual TimeProvider must implement CreateTimer and support manual advance; first prove that advancing it fires a CTS-backed timeout. Do not add a TimeProvider package reference merely because a central version exists. Reject a declared Content-Length above the applicable JSON/archive limit early, while still enforcing actual bytes when the declaration is absent, false or smaller.
- Define one portable ZIP relative-name representation before path resolution/collision checks. Permit a single trailing slash only as a directory marker, then require nonempty forward-slash-separated segments; reject rooted names, backslashes, NUL, colon, empty/dot/dot-dot segments, non-NFC spellings, Windows-invalid/reserved segments and trailing dot/space aliases. Do not rewrite unsafe names into safe ones. Use the validated NFC relative name and OrdinalIgnoreCase collision keys, tracking file/directory ancestor relationships; require the resolved target to stay beneath staging and reject any unexpected normalization change. Test normalized aliases, case/Unicode aliases, reserved names, file/directory collisions and valid explicit directory markers on supported platforms. This is safe extraction policy, not a full general ZIP parser.
- Add only intentionally internal adapter seams needed for deterministic verification: a source-identity delegate defaulting to TailwindSourceIdentity.RequireAsync, and narrow owned-file create/move/delete/report-write operations defaulting to real filesystem calls. Thread the source delegate through a minimal internal overload of ValidateProducerBindingAsync so the real subject/manifest/package validator still executes in the three-case card. Existing callers and every production entry use the real default; no CLI/environment bypass, reflection, alternate authority or replaceable content validator. Keep real clean-checkout/stamped-build success and dirty/wrong/missing-stamp failures outside the card. Use the existing Program.RunAsync workflow injection convention only for dispatch wiring tests, not to fake the adapter acceptance card. Test real filesystem success as well as injected cross-volume move, cleanup, scratch removal, report/marker write and double failures. Document each seam and no_claim: injected IO does not establish OS behavior; the card's controlled source check does not establish hosted/source authority.
- Inventory CommandLineOptions in Program.cs as well as TailwindCommandOptions. For artifact-transport only, validate raw argument names/presence before applying general defaults or path resolution: require all twelve documented options, reject duplicates and inapplicable selection options with artifact-input-invalid naming only the option, and route parse failures through the new safe stderr boundary. Then reuse existing parsing/path resolution for accepted values. Defaults on CommandLineOptions must not manufacture required authority or conceal that an option was explicitly supplied. Preserve every other mode's parsing/defaults; add real dispatch coverage for presence versus default and malformed raw values.
- Retain the existing producer-binding validator, but never forward arbitrary PackageIndexException.Message or inner-exception text. At the source-check boundary map failures to fixed safe source messages; at subject/manifest/package boundaries map to fixed safe binding messages plus allowlisted cause kind and validated canonical identity/field names only. Preserve an existing safe underlying diagnostic code when actually available; otherwise emit the safe boundary cause, not a manufactured code or raw message. Test local paths, signed queries and secret sentinels in reused exception messages across all emitted channels. Existing old-mode diagnostic behavior is unchanged.
- The core owns and best-effort deletes an incomplete temporary ZIP on internal failure. A successful verified-archive result transfers that ZIP's ownership to the adapter; the adapter disposes handles and removes the entire invocation-created work directory, including ZIP, before writing the last success marker. Retain first-error precedence if later cleanup/reporting also fails. Keep same-volume installation with Directory.Move and no copy fallback. Document the configuration before invoking, but add no pre-HTTP filesystem rename probe or extra output creation; a real/injected cross-volume move remains artifact-output-failed. This owner-approved D3 choice avoids an additional mutation path while preserving the approved refusal and all safety coverage; an early owned rename probe is the equally safe alternative, with more setup/cleanup behavior and earlier feedback.
- Emit the receipt after the protected step with id validate-producer-binding, not the earlier producer-binding step. Move setup-dotnet together with locked restore and source-stamped build before consumer verification. Use the fixed workflowPath .github/workflows/package-artifacts.yml from this protected workflow's policy; the API path check corroborates it and existing repository/main/manual guards remain required. Include these exact ordering/policy cases in the executable workflow suite. Document that receipt/API digest agreement is consistency and not an independent signature or producer authentication; recomputing downloaded bytes proves equality to that trusted digest. Retain the supported 2026-03-10 API pin, verified against GitHub's supported-versions documentation. No new hosted dispatch is authorized here, and one required acceptance run is a minimum evidence requirement, not a rule preventing separately authorized investigation after a refusal.
<!-- /autoplan-accepted:eng -->


### Engineering independent reviews and dispositions

Review input: eng b19445ac754da3abdb213d1a92ea1ade620895de2298517a44dc0a5fbd95590c. Native combo/sub completed against the exact snapshot, with three findings (one P1, two P2). Actual routed model identity is unknown. Claude Code completed with CLI exit 0, validator exit 3, session 0fc19ad8-0a9c-4012-a0a8-117e07b41cb7, actual modelUsage claude-fable-5-1. Its 14 findings comprise three P1, six P2 and five P3; quality judgment 7/10. This is planning review, not running-code coverage. Full external output and modelUsage are retained in eng-outside.txt and eng-outside-result.json under this run's scratch directory. Native required completion marker matched the snapshot. No single-voice Critical/P0 finding was reported.

Codex (in-host) SUBAGENT (eng, independent review): P1 acceptance card/owner missing; P2 canonical ZIP name representation missing; P2 deterministic filesystem fault seam missing. Retain the card/owner as an unsatisfied implementation-entry condition, add canonical extraction rules and the narrow IO seam. The original design's acceptance card is not replaced with a mocked fixture or API metadata.

Claude Code SAYS (eng, architecture challenge): the card needs a source-validation seam; the API pin allegedly fails; temp aliases need handling; general option parsing obscures presence; outputs may dirty watched source; reused messages may leak; fake timers need working timers; early cross-volume feedback is desirable; filesystem failures need seams; workflow step IDs/build setup need precision; digest authority must be honest; Content-Length needs early refusal; ZIP ownership needs an explicit transfer; estimates should remain estimates.

| Outside finding | Disposition and evidence |
|---|---|
| E1 P1 source fixture | Accept a narrower remedy: inject source identity through an internal binding overload, preserve real content validation. A whole mocked workflow does not satisfy the card. Program.cs's existing delegate pattern is only dispatch coverage. TailwindEvidenceWorkflow.cs:43 calls the static source check before subject validation. |
| E2 P1 API pin | Reject factual premise. GitHub's supported API versions page explicitly lists 2026-03-10. Native independently confirmed it. Actual hosted GITHUB_TOKEN compatibility remains pending; a docs check is not a hosted result. |
| E3 P1 macOS alias | Existing remedy found: TailwindTestPaths.cs:12–17 already maps /var and /tmp aliases to /private. Reuse it, document operator pitfall, preserve no-link refusal. Do not introduce arbitrary operator-path resolution. |
| E4 P2 general parser | Accept raw presence/name validation before CommandLineOptions defaults, only in the new mode. Program.cs:662–664 defaults input to output and manifest to an artifacts path. |
| E5 P2 watched outputs | Accept excluding watched source-input subtrees from writable outputs. Keep allowed checkout-relative artifacts/ outputs; repeated real source checks remain. TailwindSourceIdentity.cs:23–36 names the watched inputs. |
| E6 P2 reused messages | Accept safe boundary mapping, not arbitrary PackageIndexException.Message passthrough. TailwindEvidenceWorkflow.RequireFreshDirectory and SourceIdentity.RunGitAsync demonstrate raw path/message content. |
| E7 P2 timer fake | Accept a small test-only manual provider implementing timers, plus a CTS timer proof. The central TimeProvider.Testing version exists, but this project has no package reference; retain no new dependency. |
| E8 P2 early same-volume probe | D3 taste: retain documented same-volume configuration plus atomic move and typed output failure; no pre-HTTP rename probe. Extra probe mutations/cleanup are optional feedback optimization. Alternative is safe but expands setup. Refusal coverage is equivalent. |
| E9 P2 filesystem seam | Accept; independently agrees with native's IO test gap. Local internal operations only, real defaults plus real filesystem success coverage. |
| E10 P3 workflow precision | Accept exact validate-producer-binding ID, setup-dotnet move, fixed workflow policy path and retained guards. Current workflow lines 139/156 distinguish upload and post-upload validation. |
| E11 P3 digest authority | Accept explicit consistency wording. No independent-signature claim; actual downloaded bytes must still hash correctly. |
| E12 P3 Content-Length | Accept early declared over-limit refusal and keep actual-byte bounds. Understated or absent lengths never bypass limits. |
| E13 P3 ownership | Accept explicit core-to-adapter ZIP ownership transfer and entire owned work-directory removal before marker. This completes D1, not a second retention policy. |
| E14 P3 estimate | Retain the owner's 2.5–3 engineer-days / agent 5–8h estimate. Record actual effort later; it is no cap or measured result. |

### ENG DUAL VOICES consensus

Confirmed means both completed independent voices assessed the same matter. An unmentioned dimension is not assumed agreement.

| Dimension | Native | Claude Code | Consensus |
|---|---|---|---|
| Architecture sound? | Split sound; entry gate incomplete | Split retained; fixture boundary incomplete | CONFIRMED responsibility split; distinct readiness/test-seam findings |
| Test coverage sufficient? | IO seam gap, canonical-path gap | IO/source/timer fixture gaps | CONFIRMED IO coverage gap; remaining findings distinct |
| Perf risks + budget/bench named? | Limits reviewed, no finding | Early length/probe/ownership findings | N/A for a joint performance verdict; no measured benchmark |
| Security threats covered? | Canonical-path gap | Safe-message and watched-output gaps | N/A for a joint security verdict; different concrete gaps |
| Error paths handled? | Fault injection incomplete | Same deterministic IO gap | CONFIRMED IO failure-proof gap |
| Deployment risk manageable? | Entry card/owner pending | Hosted token still unproven; API pin concern false | N/A for joint readiness; entry and hosted evidence remain pending |

Three of six dimensions contain a specifically shared conclusion/gap; one valid disagreement about early cross-volume probing goes to D3. There is no joint recommendation to change the user's architecture or v1 scope, hence no User Challenge. The API-pin objection is a corrected factual error, not an unresolved security disagreement. Engineering amendments below are source-grounded parent decisions; neither reviewer has re-reviewed those final bytes.

### Engineering Section 1: architecture

The existing verifier is a maintainer tool, not a new deployed service. Distribution stays the existing locked net10.0 build stamped with exact source; no package publication, schema migration or public compatibility promise. The worst permitted side effect is a partial invocation-owned diagnostic or ineligible directory. A protected producer is still trusted; hostile API/archive bytes do not establish authority. Handler authentication applies per original API request, never through shared default headers, redirects or reports. There is one consumer and one in-flight archive, so no fan-out/scaling service is introduced.

Two shapes were compared. Selected shape B has `GitHubActionsArtifactVerifier.VerifyAsync(expected, receipt, token, ownedScratch, cancellation)` returning an owned verified archive; `TailwindArtifactTransportWorkflow.RunAsync(request)` owns source checks, safe extraction, real content validation and final outputs. Alternative A is one `TailwindArtifactTransportWorkflow.RunAsync(request)` owning HTTP as well as Tailwind policy. Both cover every behavior. B hides HTTP parsing, budgets and credential transitions behind one internal operation, while keeping filesystem/content policy visible at its actual owner; user D4 already selected it. Internal result/exception records must not export HttpResponseMessage or let callers advance transport stages independently.

```text
protected workflow: producer job SUCCESS + upload outputs + GitHub context
        | versioned unsigned receipt + separate exact artifact ID
        v
Program --mode artifact-transport   independently supplied current context
        | raw required-option presence -> safe boundary -> parsed request
        v
TailwindArtifactTransportWorkflow [owns paths, source, install, reports]
        | fresh/disjoint/no-link/watched-input checks; real source guard
        v
GitHubActionsArtifactVerifier [owns API, token, HTTP/time/byte limits]
        | receipt binding -> direct artifact -> exact attempt -> exact ZIP
        | raw streamed hash == receipt == API digest
        +-- fail: core owns/deletes incomplete ZIP -> typed safe failure
        v
verified-archive result transfers ZIP ownership to adapter
        | bounded ZIP envelope -> canonical entries -> owned staging
        v
ValidateProducerBindingAsync [real subject + manifest + package validator]
        | real source default again -> current subject attempt
        v
Directory.Move(staging, bundle), diagnostics + summary, remove owned work
        v
verified-artifact.json LAST + process exit 0
        | caller checks marker identity and exit, then existing preflight
        v
aggregate / original receipt / replay / recording publisher (nonpublishing)

ANY failure/cancel -> retain first code -> only owned cleanup -> safe report
                  -> exit 1; no eligible marker/bundle; no retries/fallback
crash -> cleanup/report unknown; no authority without exit 0 AND marker
```

Eight shape flags: (1) shallow module avoided by one deep core operation; (2) information leakage avoided by parsed immutable identities and no raw HTTP objects; (3) split follows trust knowledge, not arbitrary load/validate/save stages; (4) no empty pass-through layer or DI framework; (5) adapter alone owns bundle/reports, core transfers ZIP once; (6) only this consumer's producer download changes, other transport paths serve separately deferred scopes; (7) new types remain internal with existing test visibility, no public imports; (8) canonical diagnostic table owns docs and parameterized checks, workflow literal policy is asserted against command expectations. None requires another runtime component. Add concise inline state/ownership ASCII diagrams to the two new source files and update nearby documentation diagrams when implemented.

Findings/dispositions: [P2, confidence 9/10] source/watch-path interaction follows the quoted `await TailwindSourceIdentity.RequireAsync(...)` and `git status --porcelain --untracked-files=all` call; refuse writable output under watched inputs before HTTP (M32). [P2, confidence 9/10] raw option defaults `resolvedArtifactsInputPath = ResolvePath(..., resolvedArtifactsOutputPath)` conceal suppliedness; validate raw options before defaults (M33). Both are accepted exact-contract completion, no scope cut. Existing protected guards, producer success and independent attempt are required and already in the approved plan.

### Engineering Section 2: code quality and reuse

Use existing strict JSON duplicate checks, bounded hashing, create-new writer and actual subject/manifest/package validation where behavior matches. The current `RequireFreshDirectory` creates its parent before testing links; copying it would violate the new mode's pre-side-effect requirement. Keep the new mode's stricter ownership validation local rather than altering unrelated callers. Decimal IDs need positive canonical raw decimal handling; existing `RequireDecimal` permits zero, so it is not the new contract unchanged. Report redaction belongs at each boundary, not a catch that prints arbitrary exceptions.

Shared-code rubric: existing authored caller `ValidateAndWriteProducerBindingAsync` at TailwindEvidenceWorkflow.cs:65 calls `ValidateProducerBindingAsync`; the proposed adapter is the second caller and must be labeled proposed. Both need identical subject/manifest/package checking. Add only an internal overload/source delegate while preserving the existing default entry; do not rebuild content validation. Estimated implementation removal 0–5 lines, addition 5–20, savings -20 to 0; including integration/tests removal 0–5, addition 40–100, savings -100 to -35, deliberately net growth for reliable reuse. These are planning ranges, not a measured diff. Both existing-caller/default-source and new-adapter integration tests are required. No generic filesystem, general archive parser or common policy base class is justified. The generic core is reusable internally but only one transport caller is implemented; Skoolit is not claimed as a second implemented caller.

Findings/dispositions: [P2, confidence 9/10] source-boundary testability needs a narrow delegate; the existing static source call makes a made-up fixture commit insufficient. Accept source-only injection through the existing binding helper while retaining real content checks (M34). [P2, confidence 9/10] existing exceptions contain raw paths and git stderr (`throw new PackageIndexException($"Exact-source git check failed: {error.Trim()}")`); accept fixed boundary messages and allowlisted causes, never raw passthrough (M35). [P2, confidence 8/10] existing `Directory.Move`/`Delete` calls cannot provide deterministic double-failure coverage via platform permissions; accept local internal IO operations with real defaults (M36). These add intentional testing seams under AGENTS.md, not public hooks or authority bypasses.

### Engineering Section 3: tests, every branch and user flow

Concrete reads before this diagram: Program.cs option/default/routing code; TailwindCommandOptions.cs; TailwindEvidenceWorkflow.cs validation/parser/report/path code; TailwindSourceIdentity.cs; TailwindArtifactProvenance.cs; existing TailwindCliDispatchTests and TailwindEvidenceWorkflowTests; TailwindTestPaths.cs; actual workflow and Python executable-run-block tests. Frameworks: existing XUnit and Python unittest, no framework installation. No implementation tests run in this planning review.

```text
MAINTAINER opens README -> locked restore -> AcceptanceCard (three cases)
  | setup absent/SDK wrong -> prerequisite guidance, not a false success
  +-- match -> actual adapter transport/extraction/content -> exit0 + marker
  +-- old attempt -> exit1 attempt mismatch, HTTP sends0
  +-- byte mutation -> exit1 bytes mismatch, extraction not entered
  no_claim: controlled source check/fake API do not prove protected authority

PRODUCER run block [execute actual YAML body]
  upload absent/bad ID/digest -> nonzero fixed safe error; no job receipt
  successful upload + validate-producer-binding -> exact fields/needs output
  large decimal / expression-like strings -> quoted JSON values, no shell eval

CONSUMER -> raw options [missing/duplicate/inapplicable/unknown/help]
  -> source check [correct stamp/clean; dirty/wrong/missing stamp/git failure]
  -> paths [fresh/disjoint/link/watched input; zero sends on preflight refusal]
  -> receipt [missing/extra/duplicate/type/schema/ID/SHA/digest/depth/size]
  -> binding [each expected field; attempt before HTTP]
  -> artifact API [required fields, additive fields, expiry, identity, digest]
  -> exact-attempt API [run/attempt/repositories/source/path/event]
  -> ZIP API + manual hops [HTTPS/status/Location/count/no credentials]
  -> raw stream [success/truncation/network/header/body/overall/byte limit]
  -> digest equality [API vs receipt; bytes vs both; no extraction on mismatch]
  -> envelope [ZIP/ZIP64/count/offset/split/truncated/corrupt]
  -> entry [canonical path/type/collision/link; entry and expanded-byte counts]
  -> real binding [subject/hash/manifest/package; attempt separate]
  -> move/install [same volume; injected move failure, no copy fallback]
  -> report + scratch removal [failure precedence/redaction]
  -> final marker [create-new atomic last; absent/broken/conflicting -> no use]
  -> downstream exact verified bundle [no replacement producer download]
       [->E2E protected hosted run only after merge + separate authorization]
ALL branches: correct code+stage+safe context+next action+docs anchor, exit,
  ownership/no marker, no retry/list/fallback and all-channel sentinel scan.
CALLER cancel at each stage -> exit1 cancelled; deadline -> named budget;
  double failure preserves first; crash has documented unobservable limit.
RERUN/concurrency: stale receipt refuses; second invocation cannot overwrite
  paths/marker; operator reconciles and authorizes a fresh full rehearsal.
```

| Branch family | Existing coverage | Required verification and gap disposition |
|---|---|---|
| Receipt/identity and decimal precision | No new transport exists; existing subject tests have different authority | Parameterize each field/type/size/depth/duplicate/canonical/large-ID case in GitHubActionsArtifactVerifierTests. Unit plus HTTP boundary; add, M37. |
| Direct endpoint order, all statuses, rate signals | Existing downloader workflow tests do not execute this HTTP path | Fake handler asserts no list/retries, direct fixed paths, each 401/403/404/410/429/5xx and unexpected status. 403 includes any Retry-After present, remaining=0, safe recognized limit JSON and no-signal denial. Add, M37. |
| Digest/stream/resource/redirect | Existing binding hashes files, not this ZIP stream | Real fixture ZIP bytes, fake stream stalls/truncation, exact/over bounds, raw encoding, all redirect statuses/Location/credential isolation/disposal, manual clock timers. Add, M37. |
| Canonical extraction and owned lifecycle | Existing path helpers serve other shapes, no general ZIP safety proof | Real temporary ZIPs and directories plus internal fault injection; each alias/type/collision, count/expansion bound, final move and marker ordering. Add, M38. |
| CLI/source/content integration | TailwindCliDispatchTests and TailwindEvidenceWorkflowTests cover existing modes | Extend existing tables for help/default regressions; new mode tests run real adapter with internal source seam and real content checks, plus separate real default-source success/failures. Add, M39. |
| Workflow guard/receipt/verified consumption | Python suite already extracts/executes run blocks | Extend actual bodies, include jq marker missing/malformed/conflict, setup/build order, existing guards/permissions and unchanged stable/prerelease/native/aggregate/start paths. Add, M40. |
| Redaction/refusal usability | Existing shape differs; new all-channel guarantee absent | Table-driven sentinel scan, code/context/next-action/anchor/exit assertions across every family; safe report-write fallback and unexpected exceptions. Add, M41. |
| Genuine service/token/runner behavior | Source fixtures cannot establish it | One authorized protected-main nonpublishing run after merge, retain real receipt/report/digest/consumed bytes; issue stays open. Planned hosted acceptance, not replaced or claimed, M42. |

Each parameterized case carries `protects`, `fails_when`, `no_claim`; the standalone test plan also supplies `why_new` and seam. Prefer adding rows to established fixture tables over repeating near-identical tests. Full v1 has no LLM/prompt behavior, so LLM evals are N/A. No existing tests retire: native, aggregate, publication and old CLI modes still guard distinct behavior. Real default content/source validation must not be reduced to a dispatch mock. Manual timer proof is test infrastructure correctness needed by real timeout regressions, not a production test hook. macOS root handling reuses TailwindTestPaths rather than a second path policy. Every new diagram family is currently a planned gap; none is counted as passing coverage.

Test review findings: six gaps, grouping receipt/HTTP/stream together, archive/lifecycle, CLI/source, workflow, safe refusal channels, and hosted acceptance. All accepted into existing v1 verification; hosted proof remains pending. The test artifact is written at the canonical project root and linked in the plan. Coverage goal is nearly 100% changed branches; record measured coverage later or explicit execution limits, and run solution coverage when practical. Plan-only validation cannot produce a numeric code-coverage score.

### Engineering Section 4: performance

No DB, N+1 query, cache, new service or request fan-out. One archive flows through three direct requests plus at most three HTTPS storage hops, bounded by the shared request counter and five-minute overall budget. Metadata buffers are bounded to 16 MiB each and depth 64; dispose each document/response rather than retaining arbitrary HTTP objects. ZIP bytes stream to disk with a fixed-size buffer, not a 1 GiB managed array. Entry metadata/collision keys are bounded by the 100,000-entry envelope check and runtime declared-count checks. Actual extraction counts enforce 4 GiB even for false declared lengths. Disk peak includes both raw archive and staged/installed bundle (contractual maximum near 5 GiB plus small reports), not a measured runner requirement. Real resource fit is unknown until hosted observation. No cache should weaken same-attempt identity or fresh output ownership.

Findings/dispositions: two accepted budget mechanisms. [P2, confidence 9/10] Entries materialization precedes an after-the-fact Count check; preflight the small EOCD/ZIP64 envelope and preserve runtime structure checks (M29). [P2, confidence 9/10] ResponseHeadersRead excludes later body waits from HttpClient.Timeout; enforce linked header/inactivity/overall timers including extraction/install, early declared length rejection plus actual counts (M30/M43). The canonical primary .NET references are in engineering Step 0. Fake-time exact/over boundary tests are budget tests, not throughput benchmarks. Later hosted report must name SDK/runner, byte/entry counts, elapsed duration, peak/available disk observations and any limit; p50/p95 or speedup remains unmeasured. D3 explicitly accepts possible late cross-volume feedback without relaxing the move failure.

### Engineering failure modes registry

| Path | Realistic failure | Handling/test | User visibility | Critical silent gap? |
|---|---|---|---|---|
| Receipt emission | Upload output absent or malformed | Producer exits nonzero; executable YAML fixtures | Fixed output name and producer failure | No |
| Raw CLI/source/paths | Defaults hide authority or output dirties source | Presence/watch-input preflight; real source and old-mode regressions | Typed input/source refusal, zero HTTP | No |
| Receipt comparison | Old producer survives partial rerun | Current-attempt check before HTTP | attempt mismatch; authorize all-jobs rerun | No |
| Metadata/attempt APIs | 404 ambiguity, rate limit, wrong identity, additive schema | Distinct status/field fixtures and first failed predicate | Exact stable code and safe context | No |
| Redirect/download | Token forwarded or signed URL leaks, stall/truncation | Per-request headers, raw hashing, timer/stream/redaction tests | Safe redirect/transfer/budget refusal | No |
| ZIP envelope/extraction | Resource exhaustion or normalized alias escapes | Bounded envelope, canonical names, actual byte/count tests | Budget or archive-unsafe before install | No |
| Tailwind binding | Valid ZIP contains wrong package/attempt | Existing real validator and separate subject attempt | Binding or attempt refusal; safe cause | No |
| Install/output | Cross-volume, report IO, cleanup also fails | Atomic move, narrow IO injection, first-error precedence | Output code, no eligible marker | No |
| Workflow consumption | Ignores exit or uses replacement producer | Execute marker check; assert downstream bundle path | Job refuses before preflight | No |
| Cancellation/crash | Cancel during body/install or kill before report | Cancel fixtures; crash eligibility contract | Cancel code when handled; missing report explicitly possible | No handled silent gap; process termination is an explicit limit |
| Hosted path | REST token/archive compatibility differs from fixtures | Separate authorized real run, retained evidence, rollback wiring | Issue remains open until verified | No claimed proof; acceptance pending |

### Engineering NOT in scope and existing solutions

Stable/prerelease transport enforcement and resolver changes remain deferred because only the nonpublishing rehearsal is the authorized trial. Cross-attempt recovery/fallback remains deferred because v1 requires current protected outputs. Skoolit adoption and historical diagnosis wait for #1441; no causal claim is made. Public packaging, signatures/attestations, GitHub Enterprise, native/aggregate/start transport rewrites, automatic retries, dashboards and a permanent probe command remain deferred. Existing #798 portable-evidence TODO remains canonical for signatures/retention. The #910 planning-only TODO section records adoption triggers without opening implementation scope.

Existing source reused: protected producer outputs/guards/read permissions in package-artifacts.yml; strict JSON and create-new writers in TailwindEvidenceWorkflow; real source stamp in TailwindSourceIdentity; subject/manifest/package validator in TailwindArtifactProvenance; XUnit fixture roots and Program dispatch injection; Python run-block execution harness. Transport verification is new, not a relabeling of subject hashing. No existing runtime path is deleted except the targeted consumer's producer download wiring after authorization.

Parallelization: Sequential implementation, no parallelization opportunity. PackageIndex's shared mode/options/contracts and tests are one primary module; freeze contracts then core, adapter, workflow and docs. Tests evolve with each boundary. Optional independent doc drafting is not a safe independent merge lane for this plan. One eventual PR on isolated current main; this dirty planning branch is not an implementation base.

### Engineering decision audit continuation

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| M32 | Eng | Exclude watched inputs from writable output roots | Mechanical | P1,P5 | Repeated source check must remain real | Suppress clean-source check |
| M33 | Eng | Validate raw required-option presence before defaults | Mechanical | P1,P5 | General defaults are not protected authority | Derive presence from resolved paths |
| M34 | Eng | Narrow source delegate, real content validator and default-source regressions | Mechanical | P1,P4 | Make card honest without a production bypass | Whole mocked adapter/card |
| M35 | Eng | Safe fixed boundary messages and allowlisted causes | Mechanical | P1,P5 | Reused raw exceptions can contain paths/secrets | Arbitrary exception-message passthrough |
| M36 | Eng | Local IO operation seams with real defaults | Mechanical | P1,P3 | Deterministic failure precedence and ownership proof | Platform-permission tricks/general filesystem framework |
| M37 | Eng | Full receipt/HTTP/stream parameter matrices | Mechanical | P1 | Each refusal/budget is an independent approved contract | Any-failure assertions |
| M38 | Eng | Canonical ZIP names and lifecycle fixtures | Mechanical | P1,P5 | Avoid aliases and unsafe installs | Rewrite unsafe entry names |
| M39 | Eng | Real adapter/content plus separate default source integration | Mechanical | P1,P4 | Dispatch injection alone hides production behavior | New CLI source bypass |
| M40 | Eng | Actual workflow run-block tests and marker gating | Mechanical | P1,P4 | One consumer must use exactly verified bytes | Separate receipt script solely for tests |
| M41 | Eng | Every refusal scans all emitted channels and docs anchors | Mechanical | P1 | Safe actionable refusals are the v1 outcome | Raw nested exception trace |
| M42 | Eng | Keep entry card/owner and hosted proof pending | Mechanical, prior approval | P1,P6 | Planning approval grants no implementation or dispatch | Treat metadata/mock as producer authority |
| M43 | Eng | Working manual timers and early length bounds | Mechanical | P1,P3 | Enforce approved limits without a dependency | Timeout fake that never fires |
| M44 | Eng | Reuse existing macOS fixture root helper | Mechanical | P4 | Existing authored helper already resolves system temp aliases | New root resolver/weakened no-link rule |
| M45 | Eng | Exact receipt step/setup ordering and policy literal | Mechanical | P5 | Remove ambiguous integration instruction | Wrong earlier binding step/context string parsing |
| M46 | Eng | Explicit ZIP ownership and entire work removal | Mechanical | P1,P5 | D1 must cover core failure and adapter success | Leaked scratch ownership |
| M47 | Eng | Keep verified API pin and honest digest/effort claims | Mechanical | P3,P6 | Primary docs and owner estimate govern | Unverified version failure claim |
| D3 | Eng | No early filesystem rename probe; retain typed move refusal | Taste, approved 2026-11-10 | P3,P5 | Less setup mutation at equal safety coverage; possible late feedback | Early owned probe and extra cleanup |

Approval readiness: PASS for planning remedies M27–M47 under /autoplan's explicit auto-decision principles and prior D4. D1–D3 remain provisional for the final single gate. No User Challenge. The implementation entry condition and future hosted evidence are not satisfied by this PASS. Pending decisions: final plan approval and three taste choices; named maintainer is an implementation entry assignment, not a silently assigned owner.

### Engineering completion summary

| Item | Result |
|---|---|
| Scope challenge | Scope accepted as-is, FULL_REVIEW; original arrangement retained |
| Reality rows | See validated PLAN_REALITY below; two unverified premise groups: entry authority/owner and hosted fit |
| Architecture | Two source-grounded issues, accepted remedies |
| Code quality | Three issues, accepted remedies |
| Test review | Full diagram produced, six planned gaps mapped to required verification |
| Performance | Two mechanism issues, accepted remedies; no benchmark claim |
| Four-section issues_found | 13, including resolved planning findings; outside/scope counts separate |
| NOT in scope / existing code | Written |
| TODOS | Existing #798 reused; #910 deferral triggers recorded, no scope expansion |
| Failure modes | Zero critical unhandled-and-untested silent gaps in the amended plan; implementation coverage unmeasured |
| Unresolved decisions | Final approval plus D1–D3 taste choices; entry assignment still open |
| Outside voice | Completed Claude Code, claude-fable-5-1, 14 findings; API-version premise corrected |
| Parallelization | Zero parallel lanes, sequential primary module |
| Lake Score | N/A; no feature-coverage option was reduced or scored |

### Engineering implementation tasks

- [ ] **ENG1 (P1, human: 1–1.5d / agent: 2–3h)** — PackageIndex — Implement direct transport and bounded time/byte/credential verification with the full refusal matrix. Derives from M29/M30/M37/M43 and the approved core contract. Files: the two core implementation/test files; verify deterministic XUnit matrix and no list/retry calls.
- [ ] **ENG2 (P1, human: 1d / agent: 2–3h)** — Tailwind adapter — Implement canonical extraction, real source/content binding and atomic owned outputs with safe diagnostics and fault seams. Derives from M28/M32–M36/M38/M39/M41/M46. Files: adapter/tests, Program/options and minimal existing binding overload; verify real default source and fault matrices.
- [ ] **ENG3 (P1, human: 0.5–1d / agent: 1–2h)** — Rehearsal integration — Wire exact protected receipt and consumer marker checks, finish canonical documentation, then retain authorized hosted acceptance after entry approval/merge. Derives from M40/M42/M45/M47. Files: workflow, executable Python suite and three docs; verify old enforcement unchanged and real trial separately.

Task estimates overlap CEO/DX tasks and each other; they are not additive delivery estimates. Overall owner estimate remains 2.5–3 engineer-days / agent 5–8h plus external availability. No implementation task has started.


### Engineering verification artifact

See [#910 test plan](issue-910-exact-artifact-verification-test-plan.md). A byte-identical canonical copy is retained at the gstack project root after the successful write below.

### Claude Code engineering full response

```tool-output
Review of the #910 implementation plan against the supplied issue, design, workflow excerpt, and PackageIndex source. No tools were used; everything below is derived from the material in the prompt.

[P1] High. The acceptance card's exit-0 case cannot be reached without a seam the plan does not name. Plan step 3 reuses `TailwindEvidenceWorkflow.ValidateProducerBindingAsync`, which calls `TailwindSourceIdentity.RequireAsync` statically. That check requires git HEAD to equal the source commit, a clean status over the watched paths, and the executing assembly's informational-version stamp to equal the same commit. A generated temporary fixture checkout cannot produce a HEAD that matches the test assembly's compile-time stamp, and the plan forbids test-only flags, environment bypasses, and reflection. The only approved seams listed are `HttpMessageHandler`, `TimeProvider`, and `Program.RunAsync` workflow injection. Injecting a whole controlled workflow through `Program.RunAsync` would make the card a test of a mock rather than of the adapter. Remedy: specify an internal adapter constructor parameter for source-identity and producer-binding validation with the production implementation as default, state that the card uses it, keep the separate real stamped-build regression the plan already requires, and record this seam in the card's `no_claim`.

[P1] High. The pinned API version header is unverified and would fail every hosted call. The design fixes `X-GitHub-Api-Version: 2026-03-10`. GitHub rejects unknown versions with HTTP 400, which the plan's partition maps to `artifact-api-response-invalid`. Fixture tests assert the header value and would pass regardless, so the first protected rehearsal would be the first place the error surfaces, consuming the single hosted trial. Note also that the existing same-run `download-artifact` step uses the runtime token and results service rather than the REST path, so there is no in-repo hosted precedent for REST artifact retrieval with `GITHUB_TOKEN`. Remedy: before implementation authorization, confirm the version against GitHub's published versions list and record the verified value in the plan, or pin the documented current stable version.

[P1] High. Credential-free fixtures will refuse on macOS because of the no-link-ancestor rule. The plan's path validation refuses any link or reparse-point ancestor, matching `RequireNoReparseAncestors`. On macOS, `Path.GetTempPath()` returns a path under `/var/folders`, and `/var` is a symlink to `/private/var`. The maintainer's platform is darwin, and the under-five-minute README-to-card target assumes `dotnet test` works from a prepared local checkout. Remedy: require fixture roots to be link-resolved before use, for example via `DirectoryInfo.ResolveLinkTarget` on the temp root, document this in the credential-free recipe, and add a pitfall note for operators passing `TMPDIR`-based paths.

[P2] Medium. The new mode's option surface collides with `CommandLineOptions`, which the plan does not inventory. Program.cs lines 300–364 call `CommandLineOptions.Parse(tailwindOptions.RemainingArguments, ...)` before mode dispatch, and `--repo-root`, `--artifacts-input`, `--artifact-manifest`, and `--artifacts-output` are not in `TailwindCommandOptions.Keys`, so they are parsed there. The plan requires refusing `--artifacts-input` with `artifact-input-invalid` naming only the option, requires `--artifacts-output` to be fresh, and takes `--repo-root` from the same parser. If `CommandLineOptions.Parse` applies defaults for artifacts-input or manifest, presence cannot be detected, and if it requires them, the new mode cannot omit them. Remedy: inventory `CommandLineOptions` in step 1 alongside the two Tailwind types already listed, and state whether the new mode parses a raw token list or extends `CommandLineOptions` with an explicit presence flag.

[P2] Medium. Checkout-relative outputs can break the second source-identity check. Step 1 validates the source checkout before any output is created, then step 3 reuses `ValidateProducerBindingAsync`, which runs `TailwindSourceIdentity.RequireAsync` again after the work directory, temporary ZIP, and staging exist. `RequireAsync` runs `git status --porcelain --untracked-files=all` over the watched paths. The plan explicitly permits fresh checkout-relative outputs. An output under `tools/ForgeTrust.AppSurface.PackageIndex` would pass the first check and fail the second with an exact-source message, which the adapter would misclassify as `artifact-source-mismatch` or `artifact-producer-binding-invalid`. Remedy: refuse outputs beneath any path in the watched list as `artifact-input-invalid`, or document and test that the allowed checkout-relative region excludes them.

[P2] Medium. "Safe underlying diagnostic" is undefined for reused exceptions. `artifact-producer-binding-invalid` must retain the underlying diagnostic, but existing `PackageIndexException` messages include full filesystem paths, for example `ReadBounded` and `RequireRegularFileWithoutLinks`, and `TailwindSourceIdentity` messages include the observed HEAD. The plan's redaction scan forbids nested exception text and filesystem source paths for unexpected failures but says nothing about which `PackageIndexException` messages are allowlisted. All reused validators throw the same exception type, so the adapter cannot classify by type. Remedy: define per-boundary message handling in the plan, either pass through `PackageIndexException.Message` as trusted for binding failures and say so, or map to fixed messages and carry the original only in the `observed` field after a path-stripping allowlist.

[P2] Medium. Deterministic time tests need a fake `TimeProvider` with working timers, and the plan forbids new dependencies. Header and inactivity timeouts implemented through `CancellationTokenSource(TimeSpan, TimeProvider)` or `Task.WaitAsync(TimeSpan, TimeProvider)` require the fake to fire timers on manual advance. `Microsoft.Extensions.TimeProvider.Testing` provides this but is a package reference. Remedy: state whether that package is already in `Directory.Packages.props` or specify a hand-rolled test `TimeProvider` that implements `CreateTimer` with manual advance, and add a test proving the fake fires a CTS-backed timeout.

[P2] Medium. Same-volume failure is detected only after a full download. The design places staging in `--work-directory` and the bundle in `--artifacts-output` and requires `Directory.Move` with no copy fallback. A cross-volume configuration fails as `artifact-output-failed` after up to 1 GiB has been streamed and validated. The plan already does pre-output validation before HTTP. Remedy: during pre-output validation, create both owned directories and rename a probe file from work to output parent, refusing cross-volume configuration as `artifact-input-invalid` before any request is sent.

[P2] Medium. Injected failure seams are listed incompletely. The plan requires tests for cross-volume move failure, scratch-removal failure, report-write failure, and double failure, but `Directory.Move` and `Directory.Delete` have no injection point and a real cross-volume setup is not deterministic in CI. Remedy: add an internal filesystem-operations seam on the adapter with a production default, list it with the other approved seams, and state its `no_claim`.

[P3] Low. Workflow wiring details are underspecified. The plan says to emit the receipt "after the existing producer-binding step" but both `producer-binding` at line 88 and `validate-producer-binding` at line 156 exist; the design means the latter. Moving restore and build ahead of consumption also requires moving the `Set up .NET SDK` step at line 236. The receipt `workflowPath` is described as coming from GitHub context, but `github.workflow_ref` needs parsing; since policy is fixed, a constant compared against the API `path` field is simpler. Remedy: name the step ID, include setup-dotnet in the move, and use a constant for `workflowPath`.

[P3] Low. The API digest shares producer authority, which the plan does not state. The artifact `digest` field is populated from the upload-artifact client's finalize request, so receipt-versus-API digest agreement is consistency, not independent corroboration. The download-bytes check remains the real integrity check. Remedy: say this in the canonical reference beside the existing "same-producer consistency" statement for the artifact ID.

[P3] Low. Early declared-length rejection is missing. Counting actual bytes is required, but a `Content-Length` above 1 GiB should fail as `artifact-budget-exceeded` before streaming begins. Remedy: add the early check while retaining actual-byte counting.

[P3] Low. Temporary ZIP ownership transfer and work-directory removal are unspecified. The core returns an owned ZIP path; the plan says dispose on all exits but does not say who deletes the ZIP when the core fails before returning versus after, nor whether the work directory itself is removed on success. Remedy: state that the core deletes on internal failure, ownership passes to the adapter on success, and the invocation-created work directory is removed entirely.

[P3] Low. Effort estimate is a judgment call, not a contradiction. Twenty-three refusal codes, each with parameterized cases, redaction scans, and anchor checks, plus ZIP64 preflight, manual timeout handling, and workflow run-block execution tests, is substantial for 2.5–3 engineer-days under AGENTS.md's near-100% coverage rule. The owner clarification governs. Flagging only so the implementing report records actual versus estimated.

Quality score: 7/10. The plan is unusually complete on trust boundaries, refusal taxonomy, and output lifecycle, and it correctly reads the workflow and source excerpts. It is weak on the mechanics of its own tests: the exit-0 card is not achievable with the seams listed, fixture paths will refuse on the maintainer's platform, and the pinned API version is unverified.

Recommendation: revise before authorizing implementation because the three P1 items would either block the acceptance card or burn the single hosted rehearsal on an avoidable 400, and each is fixable in the plan text without changing approved direction.
```

### Engineering premise receipts

| Premise | Claim | Receipt | Verdict |
|---|---|---|---|
| Existing selection | Current rehearsal passes exact upload ID to download action | .gstack/tmp/autoplan/issue910-20261107/source/.github/workflows/package-artifacts.yml:231 | verified statically |
| Existing content boundary | Existing binding API lacks expected current-attempt argument | .gstack/tmp/autoplan/issue910-20261107/source/tools/ForgeTrust.AppSurface.PackageIndex/TailwindEvidenceWorkflow.cs:35 | verified statically |
| Proposed capability | Direct no-list transport and typed refusals are approved v1 requirements | docs/designs/issue-910-exact-artifact-verification.md:24 | prior user approval |
| Hosted reality | Actual read token capability, archive digest equality and resource fit | docs/designs/issue-910-exact-artifact-verification.md:109 | unverified; collect hosted evidence |
| Implementation entry | Protected retained card and named maintainer | docs/designs/issue-910-exact-artifact-verification.md:117 | unverified; entry gate pending |

REALITY: premise-table pass static and approved claims separated from hosted/entry unknowns; unverified=2 docs/designs/issue-910-exact-artifact-verification.md:109
REALITY: already-done pass pinned current-main source inspected through GitHub API; no artifact-transport implementation, unrelated work preserved .gstack/tmp/autoplan/issue910-20261107/source-manifest.json:2
REALITY: surface-check pass existing verify-tailwind-evidence registered; new mode absent and planned; direct routes checked against official REST docs .gstack/tmp/autoplan/issue910-20261107/source/tools/ForgeTrust.AppSurface.PackageIndex/Program.cs:358
REALITY: binding-decisions pass approved same-attempt/rehearsal/B split and implementation deferral retained; branch CloudTasks decision unrelated docs/designs/issue-910-exact-artifact-verification.md:24
REALITY: numbers pass resource limits are quoted contract values, estimates remain estimates; no performance or incident-rate measurement claimed docs/designs/issue-910-exact-artifact-verification.md:75
REALITY: deferred-asks pass no v1 issue request dropped; prior non-v1 deferrals retained in source plan docs/plans/issue-910-exact-artifact-verification.md:64
REALITY: pr-count pass no ship-policy file exists; default one PR, no staged PR split accepted .gstack/tmp/autoplan/issue910-20261107/ceo-step0.md:23
REALITY: prior-art pass diverge from download action only for approved no-list/typed-refusal contract, not a claimed digest deficiency docs/designs/issue-910-exact-artifact-verification.md:33
REALITY: contract-assumptions pass known action digest and REST API contracts are primary-source checked; hosted compatibility remains pending .gstack/tmp/autoplan/issue910-20261107/source-manifest.json:2
REALITY: falsification-box pass capability verification, not incident diagnosis; no historical Skoolit causal hypothesis accepted docs/designs/issue-910-exact-artifact-verification.md:18
REALITY: stop-reasons pass all refusal categories and required fixtures retained in the binding issue contract docs/plans/issue-910-exact-artifact-verification.md:215
REALITY: reporter-environment pass historical incident class not confirmed; fixture and hosted evidence kept distinct docs/designs/issue-910-exact-artifact-verification.md:109
REALITY: units pass no units/accuracy retuning; byte/time limits retained verbatim docs/designs/issue-910-exact-artifact-verification.md:75
REALITY: perf-table pass no speed or service-load claim; controlled budget tests plus actual hosted resource observations required docs/designs/issue-910-exact-artifact-verification.md:75
REALITY: dependency-fallback pass existing validator stays content authority; no public package/Skoolit predecessor required docs/designs/issue-910-exact-artifact-verification.md:42
REALITY: obligation-tiers pass must-ship full v1 tests/docs, no independent next PR; broader callers deferred docs/designs/issue-910-exact-artifact-verification.md:24

Incident falsification: no causal hypothesis is asserted; diagnosis has no work budget in v1. New predicate evidence would reopen that separate work after Skoolit #1441 settles; absence of evidence leaves the cause unknown. Environment reproduction is class-not-confirmed, never ruled out.

Performance evidence: machine/engine/data size/N/p50/p95/base-branch comparison/pathological/steady-state samples are not measured in this planning run. No units changed and no consuming threshold is retuned. Resource limits are contractual ceilings; the authorized hosted run later records actual runner/version, archive/expanded sizes, elapsed time and disk fit.


Engineering reality result: `PLAN_REALITY: phase=eng rows=16/16 missing=none unverified=2 verdict=complete`. Canonical test plan was saved in the local planning archive; the [repository test-plan copy](issue-910-exact-artifact-verification-test-plan.md) above has identical bytes to that historical snapshot. Tasks ENG1–ENG3 validated and archived; none executed.


### Final autoplan reconciliation

Status: REVIEW COMPLETE, FINAL PLAN APPROVAL PENDING. Implementation remains deferred. Phase order was CEO → visual design skipped (no UI) → DX → engineering last, followed by the fixed-checkpoint readback. The engineering close packet was read through EOF with missing middle ranges repaired; source/receipt/attempt/digest/resource/unsafe-archive/refusal/owned-output/workflow requirements remain present. Independent voices reviewed their captured snapshots; accepted final amendments received parent semantic verification, not an invented independent re-review.

Pre-gate verification: CEO includes named premise receipts, all ten applicable sections, error/rescue and failure registries, diagrams, scope/existing-code/dream-state and summary. DX includes eight dimension scores, journey/empathy, declared clock/target, checklist and consensus. Engineering includes source-grounded scope challenge, both shapes/eight flags, ASCII dependency/test diagrams, all four full sections, canonical test artifact, scope/existing-code/failure registry and summary. CEO and engineering reality checks both report 16/16 complete with two unverified premise groups. Those checks establish planning evidence hygiene, not runtime truth. No applicable required output remains missing.

Coverage limits: CEO external review is unavailable/unverified (validator 4), not cross-model agreement. The two-round Spec loop ended at 8/10 NOT PASS under its repeated-issue stop rule; two fixes were independently confirmed, three final corrections were applied and text-verified without another Spec PASS. DX and engineering external outputs completed, including false positives corrected from actual code/primary docs. Final amendments were not independently re-reviewed after integration. The protected acceptance card/owner and future real hosted resource/token/ZIP proof remain pending. There was no implementation test run or numerical branch-coverage result.

49 unique decisions: 46 mechanical auto-decisions, three provisional taste choices, zero User Challenges. M8 in early prose is an alias for D1, not an additional decision. Decision rows are review history; accepted behavior appears in the Implementation plan's applied obligations. D1–D3 and plan approval are the single owner gate, revision 2. Its final item uses plan1 because the installed validator requires a numbered id; the skill's literal plan id is rejected. Reply rec selects all recommendations. The approval changes planning status only.

Nine validated tasks aggregate CEO/DX/engineering findings; exact dedupe preserved distinct tasks, including overlapping requirements. Do not add their effort figures together. The shared branch also has unrelated #865/#892 work: aggregation used only the three validated #910 lists plus branch/recent-commit filtering. Their files and historical review records were preserved.

Deferred scope was written to TODOS.md as three #910 adoption/recovery follow-ups, each with the existing owner deferral and a trigger; portable attestation/retention still points to #798. No follow-up was converted into v1 implementation. No urgent security/data-loss issue outside the plan was established.

Implementation-model recommendation is unavailable under this custom OpenAI endpoint: no smart implementation model is configured. If a later implementation is authorized, name the served model explicitly or set gstack-config model_smart_openai to the endpoint's model ID. No model/configuration was changed and no implementation agent was spawned.

### Owner approval — 2026-11-10

The user replied `rec` to gate revision 2. The installed gate parser returned valid=true, no pending choices and no unparsed tokens, selecting D1a, D2a, D3a and PLAN1a. The close helper reported zero unresolved draft directions. Approval applies to this amended plan and its test requirements only; no implementation, hosted dispatch, PR, issue edit or publication is authorized.

The selected choices are: remove owned scratch before the final marker; keep compact allowlisted local frames in the existing safe diagnostics; and retain final atomic-move cross-volume refusal without a pre-HTTP probe or copy fallback. No requirement or architecture was changed by this approval, so no affected-phase rerun is needed. The three taste choices are settled.

Pre-approval implementation bytes still matched the engineering close source SHA-256 f785d3f07697e1f9783e856592ad2ed11fc714b76cc2494149a8dbe11a007ef5. The current renderer computes a different recommendation hash after the tooling update, but the saved gate revision, item texts, recommendations and selected four choices are unchanged from the presented gate. The reply was accepted against that unchanged decision content; no stale or unparsed choice was guessed. Immutable review snapshots remain intact.

Evidence limits remain unchanged: CEO outside review is unverified; the Spec loop ended at 8/10 without PASS; final amendments were not independently re-reviewed. The protected acceptance card and responsible maintainer must be completed before implementation authorization. Later genuine hosted token/archive/digest/resource acceptance remains pending. Existing candidate metadata is not a protected receipt and is not claimed revalidated on the approval date.

### Protected acceptance-card assignment completed — 2026-11-10

The user requested completion from the retained rehearsal and explicitly named themselves as responsible maintainer while withholding implementation authorization. The [completed card](issue-910-protected-acceptance-card.md) and [sanitized evidence](https://github.com/forge-trust/AppSurface/blob/3d1a69e1db4dfea8eacf4927e34e6440f9dde6f1/docs/plans/evidence/issue-910/retained-rehearsal.json) bind every required value to the exact producer upload/context step, pinned historical workflow, and corroborating attempt/job/artifact responses. The responsible maintainer is Andrew Krock (`@akrock`), confirmed through the connected GitHub identity and assigned by the user's instruction.

Run `36547070332`, attempt `1`, succeeded; producer job `109335915393` and nonpublishing consumer `109339841837` succeeded. Step 9's upload log ties artifact `11023576342` to archive SHA-256 `27151d76524a22cd8126f5dc658295c16cf6d869930ccaf8a3f97d97e14be6fd`. Step 11 logs the GitHub-context source/run/attempt/repository and subject hash, and its typed binding succeeded. The existing producer and consumer download logs record the same archive digest. This task did not download/recompute the archive or execute the proposed mode.

Fresh metadata reports the artifact retained/unexpired through `2026-12-28T09:07:08Z`; the evidence snapshot records exact collection times and does not promise future retention. The historical run predates v1 receipt emission. The card's normalized record is a provenance-labeled transcription, not an operational receipt. Attempt 1 has no earlier positive attempt, so the negative fixture explicitly models current attempt 2 with old receipt 1 and refuses before HTTP; no real rerun is invented. The three required outcomes remain future tests.

This evidence/ownership update completes the design assignment and resolves the corresponding review-time entry gap. It changes no runtime behavior, architecture, refusal taxonomy, budgets, or approved scope. No implementation, workflow dispatch, issue/PR write, publication, deployment or build/test run occurred. Separate implementation authorization and later new-mode hosted acceptance remain required. Historical CEO outside/Spec review limits remain recorded without claiming a new review PASS.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|---|---|---|---|---|---|
| CEO Review | /autoplan CEO | Scope and strategy | 1 current #910 run | APPROVED PLAN, COVERAGE CONCERNS | Native 5; Spec latest 8/10 without PASS; outside unverified |
| Outside Review | Claude Code plan-review | Independent challenge | 3 phase attempts | CEO UNVERIFIED; DX/ENG COMPLETED | DX 12; Eng 14; modelUsage retained; final amendments not re-reviewed |
| Eng Review | /autoplan engineering | Architecture, branches and tests | 1 current #910 run | APPROVED PLAN, IMPLEMENTATION DEFERRED | 13 parent section issues with planned remedies; native 3; zero critical silent gaps in plan; runtime unverified |
| Design Review | Scope detection | No UI | 0 | SKIPPED | Not a completed design review |
| DX Review | /autoplan developer experience | Maintainer CLI and workflow | 1 current #910 run | APPROVED PLAN | Plan score 7.5 → 8.1/10; native 5 (1 false positive); outside 12; timing unmeasured |

**OUTSIDE COVERAGE:** CEO unavailable/unverified; design skipped; DX and engineering completed with captured CLI 0/validator 3. Actual outside model claude-fable-5-1; requested native model combo/sub, actual routed model unknown. Prior design reviews do not substitute for missing coverage.

**CROSS-MODEL:** DX has 3/6 specifically shared gaps; engineering has 3/6 dimensions with shared conclusions/gaps. D3's valid probe disagreement is resolved by the owner's selection of D3a. Other cells remain N/A or distinct, not implied agreement.

**VERDICT:** Amended plan APPROVED by user on 2026-11-10 with D1a, D2a and D3a. Implementation remains deferred. The [protected card/maintainer assignment](issue-910-protected-acceptance-card.md) is completed on 2026-11-10 with Andrew Krock (@akrock) responsible. Separate implementation authorization and later hosted acceptance remain outstanding; no new-mode runtime verification or formal Spec PASS is claimed. Any later implementation requires separate authorization and review of its actual diff.

NO UNRESOLVED DECISIONS
