# Tailwind artifact provenance (#798)

This is the operator and private-tool reference for the approved [#798 design](designs/issue-798-tailwind-artifact-provenance.md) and [implementation plan](plans/issue-798-tailwind-artifact-provenance.md). The flags, records and workflow behavior below describe the release provenance implementation. There is no consumer-facing package API change.

## Trust chain and authorities

The validated tag checkout, repository identity, full source commit and coordinated package plan are the source authority. The producer runs `pack-and-verify` once, performs the existing local Linux consumer proof, and emits a package inventory (`package-artifact-manifest.json` v1) plus `tailwind-proof-subject.json` (`appsurface-tailwind-proof-subject-v1`). The subject freezes package version, producer run and attempt, source/repository, exact manifest SHA-256, Tailwind package ID/file/raw SHA-512, exact internal Tailwind release-manifest SHA-256, `net10.0` consumer framework, payload projection version 1, and sorted first-party closure (at the reviewed base, Core and Tailwind), each with ID/version/file/raw SHA-512.

The producer uploads that bundle once. The immutable upload artifact ID and SHA-256 of the exact serialized subject are separate protected producer outputs: the artifact ID cannot be embedded in the subject because upload assigns it afterward. The binding is expected repository/run + producer artifact ID + expected subject digest; that subject then binds manifest and package bytes. `download-artifact` receives the exact ID, and its `download-path` is the verifier's `--artifacts-input`. Do not select by latest name, merge host directories, or take expected IDs/hashes from receipt contents. The action's download path is transport context; the typed verifier recomputes subject, manifest, archive and payload digests. The download action does not return an artifact ID or digest output.

The source/plan defines what may be packed. Producer subject and manifest bind the candidate's bytes. The actual resolved `project.assets.json` package graph defines what the consumer selected, constrained to the producer's frozen first-party inventory and locked third-party graph. The restored `.nupkg` archives are opened from the fresh private NuGet cache resolved by assets metadata and hashed directly; `.nupkg.sha512`, `contentHash`, or source metadata are supporting diagnostics, never archive-hash substitutes. Extracted protected files are compared separately with archive entries before and after build. The aggregate is authoritative only for one invocation with exactly five successful receipts and all bound host artifacts. A publication-start receipt becomes authority only after its immutable upload succeeds and returns an ID. On retry, its original producer and aggregate IDs win.

This is evidence inside the protected repository/workflow/producer/native-runner trust boundary; it does not authenticate a compromised authorized runner. It proves the raw archives supplied to the push boundary, not raw equality to NuGet.org's repository-signed downloads. It does not create portable attestations.

## Wire records and validation

The existing package manifest remains v1. Producer subject schema is `appsurface-tailwind-proof-subject-v1`; native receipt schema is `appsurface-tailwind-native-host-proof-v2`; publication-start schema is `appsurface-tailwind-publication-start-v1`. Native receipt v1 is historical diagnostic data and cannot satisfy the new gate. IDs are decimal strings, not JSON numbers. SHA-512 values are lowercase 128-character hexadecimal; SHA-256 values are lowercase 64-character hexadecimal. Validate strict JSON schemas, types, required fields, duplicate JSON fields and identities, unique package identities, safe basename artifact filenames, and confined relative paths. Never infer omitted data as success.

The v2 native receipt binds repository, source, producer run/attempt and artifact ID, subject and manifest SHA-256, version, `nativeInvocationId`, native run/attempt, expected and observed RID, OS/process architecture and runner label. It records every frozen first-party package's identity, producer SHA-512, recomputed restored archive SHA-512 and payload result; both internal/restored Tailwind release-manifest hashes; selected CLI binary and recomputed SHA-256; and generated CSS, no companion dependency, no native consumer output. It also indexes relative paths and hashes for lock/assets data, consumed archives, relevant extracted payload, CSS and bounded structured command/failure reports. Cache absolute paths are diagnostics only and never aggregator path authority.

Projection version 1 protects every regular-file entry beneath all ten roots: `build/`, `buildTransitive/`, `buildMultiTargeting/`, `lib/`, `ref/`, `analyzers/`, `tools/`, `tasks/`, `runtimes/`, and `contentFiles/`. It also includes file-valued assets selected by the fixed consumer's `project.assets.json` graph even when their archive-relative path is outside those roots. The finite recognized file groups are `compile`, `runtime`, `native`, `resource`, `build`, `buildMultiTargeting`, `contentFiles`, and `runtimeTargets`; `_._` is a placeholder rather than an executable asset, but if it appears under a protected root it remains part of the byte comparison. Metadata-only dependency/framework groups must be explicitly allowlisted by the parser. An unrecognized first-party asset group fails as `unsupported-asset-group`; do not silently ignore it or dynamically trace arbitrary MSBuild imports. Both producer and native consumers enforce the same projection version.

Projection rejects unsafe/rooted/traversing/backslash paths, ambiguous and case-colliding entries, symlinks/reparse points, cache escapes, unknown projection versions and unexpected first-party graph nodes. It compares decompressed file bytes, not ZIP compression metadata. NuGet-generated package metadata and ZIP packaging metadata are outside the extracted payload comparison; the raw archive is still separately hashed. File membership and matching use ordinal-ignore-case consistently across hosts, so case-only collisions fail even on Unix.

The fixed output contract is `tailwind-native-host-proof.json` for release consumer, `tailwind-local-proof.json` for local mode (`releaseEligible: false`), `tailwind-native-aggregate.json` for aggregate, and `publication-start-receipt.json` as the preflight draft. Each mode also writes `diagnostics.json` and `summary.md`; consumer output includes `commands/` and `evidence/`, while aggregate includes `hosts/<rid>/` with the exact bound files. Publisher keeps its existing ledger path and adds diagnostics/summary in its explicit report directory. Success JSON is atomically installed only after all required checks pass; failure/cancellation must not leave stale success. `diagnostics.json` uses `appsurface-tailwind-diagnostic-v1`, status `succeeded|failed|cancelled`, stage, release eligibility, and ordered errors with code/message/expected/observed/nextAction/docs URL and optional confined relative evidence path. Unknown values are null. At most 100 errors are serialized; `errorsTruncated` marks omitted errors. Machine callers read JSON, never scrape Markdown or stdout. The receipt's containing document is excluded from its own file inventory; aggregate binds each receipt, while its own expected digest is supplied separately by the protected workflow output.

Approved initial resource limits are 16 MiB per JSON document, 1 GiB per archive, 100,000 archive entries, 4 GiB protected expanded bytes per archive, and 4 MiB captured stdout and stderr per stream (with truncation marker, while continuing to drain). Count actual streamed bytes, not only declared ZIP sizes. Native jobs have a 30-minute ceiling and aggregation 10 minutes; leave time for failure report and artifact upload. Workflow history resolution requests 100 jobs per page, follows every page, and has a five-minute total deadline and 500-request ceiling across attempts; either limit or incomplete pagination yields `history-unknown`, never partial authority. These are implementation limits from the approved plan, not operator override flags.

The native matrix must observe the exact five hosts `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64`, using #790's actual-host mapping (Windows Arm64 runs the x64 asset under emulation, not a sixth host). An input RID alone does not prove host execution. Each invocation is identified by producer artifact ID, workflow run ID, current `github.run_attempt`, and fixed native workflow job key. Producer attempt identifies packaging; native attempt identifies proof execution; do not compare them for equality. Exactly one `status: succeeded` record per RID is required. Failed/cancelled diagnostics remain available but cannot fill a success slot or be mixed with another invocation.

## Commands and API shape

The publisher's internal `TailwindPublicationRequest` carries the checkout, producer bundle/manifest/subject, numeric repository and run identity, exact aggregate ID/hash, prepared archive directory, uploaded start receipt ID/path, and a fresh report directory. It has no default authority values. Relative aggregate, publication, start-receipt, and report paths passed to the publication commands resolve against `--repo-root`; the producer subject remains relative to the downloaded bundle. `PackagePublishRequest.TailwindEvidence` is optional only for package plans without Tailwind; the resolved plan makes it mandatory otherwise. `ITailwindPublicationEvidenceValidator` returns confined prepared package paths after the shared validator checks the original records. `IReleaseCredentialProvider` reads the named NuGet key only after those checks; direct tool callers cannot bypass the order by omitting a CLI flag. The publish ledger records the original producer, subject, aggregate and start artifact IDs/hashes, including after a partial push. A `duplicate-reported` entry remains an observed NuGet response, not proof of remote byte identity.

Proof command execution uses `ExternalCapturePolicy` with a four MiB cap for stdout and independently for stderr. `CliWrapCommandRunner` drains bytes beyond each cap and appends an explicit truncation marker; `ExternalCommandResult.StandardOutputTruncated` and `.StandardErrorTruncated` report that condition. An omitted policy preserves ordinary buffered command behavior for existing non-proof callers. Truncated diagnostics may accompany a successful process exit, but structured proof files must still be complete and validated; command text, credentials and unrelated environment values are not serialized into proof evidence.

`ForgeTrust.AppSurface.PackageIndex` remains a non-packable maintainer executable. Its proof commands are `verify-tailwind-consumer` and `verify-tailwind-evidence`; command help describes their modes and flags. The release verifier is built from the exact validated source checkout, then invoked by DLL with `dotnet`. It asserts the repository HEAD matches `--source-commit` and relevant tracked inputs are clean. Existing `--repo-root` defaults to the current checkout. Workflow expressions are projected through environment variables and passed as quoted arguments, never evaluated as assembled shell text.

### Shared bundle and producer identity

Consumer release mode, evidence aggregate/preflight, and Tailwind publisher commands require all of:

```text
--artifacts-input <exact-producer-download-path>
--artifact-manifest <path-inside-bundle>/package-artifact-manifest.json
--producer-subject <path-inside-bundle>/tailwind-proof-subject.json
--producer-artifact-id <protected-producer-output>
--expected-subject-sha256 <protected-producer-output>
--repository-id <validated-repository-id>
--producer-run-id <frozen-producer-run-id>
--source-commit <validated-full-commit>
```

The manifest and subject must reside in the exact bundle directory returned by the ID-based download. Identity arguments are trusted workflow outputs/context, not values copied out of the evidence. Producer attempt is read from the subject only after the expected subject hash has been verified.

### Consumer: local and release modes

`verify-tailwind-consumer` requires an explicit `--mode local` or `--mode release`. Local mode is for the existing pack-time proof and compatibility adapter; it produces an explicitly local-only report and cannot emit a native release receipt. It is not a five-host claim. The Bash adapter preserves its existing flags and exit meanings: 0 success, 1 proof failure, 2 usage error; release identity flags are rejected by the adapter.

Release mode requires the shared flags plus:

```text
--native-invocation-id <producer/run/current-attempt/job-key identity>
--expected-rid <one-required-rid>
--work-directory <new-proof-owned-directory>
--report-directory <directory-under-proof-workspace>
```

There are no defaults for invocation ID or these directories. A native workflow must explicitly request release mode; missing release inputs fail rather than falling back to local. Each host uses isolated package/HTTP caches, CLI home and consumer workspace; clears inherited package sources and fallback folders; restores locked dependencies; checks resolved graph and actual archive bytes; verifies extracted payload; builds with `--no-restore`; then rechecks archives/payload and existing CSS/cache/no-companion/no-native-output behavior.

The release consumer seeds `packages.lock.json` from the [reviewed native consumer lock](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.PackageIndex/tailwind-native-consumer.lock.json) **before its first restore**. That tracked `net10.0` NuGet graph fixes every third-party package's version, content hash, and dependency edges. The verifier changes only the Core and Tailwind entries to the producer subject's package version and raw archive SHA-512 (encoded as NuGet's Base64 `contentHash`), plus their first-party dependency references. Both restores use `--locked-mode`; a changed or missing lock after either restore fails before build. The lock is copied into host evidence. A new first-party dependency, third-party version, or graph edge requires a reviewed template update; a routine release version does not. The producer subject and raw restored archive checks remain the authority for first-party bytes, while NuGet's lock constrains third-party resolution.

To update the template intentionally, restore the fixed `net10.0` consumer project against a validated local package bundle in a disposable directory, with the same [source mapping and isolated cache policy](../tools/ForgeTrust.AppSurface.PackageIndex/TailwindNativeConsumerWorkflow.cs). Review the complete generated NuGet lock diff, including package hashes and transitive edges, then commit the new template with the package changes. Do not run `--force-evaluate` in the release verifier: it would accept a newly resolved graph before the checked-in contract can constrain it. The repository's [NuGet lock guidance](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies) explains why application entrypoints commit their lock and use locked mode. A failed locked restore is a prompt to inspect the candidate package dependencies and update the reviewed graph if warranted, not to regenerate the lock inside CI.

### Aggregate mode

`verify-tailwind-evidence --mode aggregate` accepts the shared flags and:

```text
--evidence-input <available-host-records-directory>
--host-artifacts-map <trusted-RID-to-artifact-ID-map>
--native-invocation-id <expected-invocation>
--report-directory <fresh-aggregate-report-directory>
```

The workflow resolves available host evidence by exact artifact IDs for this repository/run/invocation and passes an explicit map. The aggregator validates every record and referenced relative file, captures missing/failed/cancelled hosts in its diagnostic summary, and writes a successful aggregate only if the exact five-host set passes. It does not follow absolute cache paths. The uploaded aggregate gets its own immutable artifact ID and SHA-256 of aggregate JSON; the latter is not the artifact transport archive digest.

### Publish preflight and publisher

`verify-tailwind-evidence --mode publish-preflight` accepts shared flags plus:

```text
--aggregate-input <exact-aggregate-download-path>
--aggregate-artifact-id <protected-aggregate-output>
--expected-aggregate-sha256 <protected-aggregate-output>
--publication-directory <fresh-push-input-directory>
--report-directory <fresh-preflight-report-directory>
```

It validates the original producer bundle and aggregate, then prepares and rechecks the package files in an isolated directory before any credential is requested. Inputs and outputs must be disjoint; report directories are fresh and separate for aggregate, preflight and publish.

After the publication-start receipt has been uploaded or recovered by immutable ID, the workflow downloads that ID and invokes `verify-tailwind-evidence --mode validate-publication-start` before `NuGet/login`. Supply the shared bundle flags, aggregate flags above, the prepared `--publication-directory`, the downloaded `--publication-start-receipt`, `--publication-start-artifact-id`, and a separate `--report-directory`. This mode revalidates the original aggregate, the stored receipt, and every prepared archive without restaging or rewriting the receipt. Missing or changed original evidence stops credential acquisition; the publisher repeats the same validation immediately before pushing.

When the selected publish plan contains Tailwind, `publish-prerelease` and `publish-stable` require the shared flags, aggregate inputs, prepared `--publication-directory`, and:

```text
--publication-start-receipt <authoritative-uploaded-or-recovered-receipt>
--publication-start-artifact-id <successful-upload-or-recovered-ID>
```

They revalidate the same evidence inside the publishing workflow before the first push and rehash each selected file immediately before pushing. The plan determines whether Tailwind evidence is mandatory; no caller bypass flag exists. Non-Tailwind plans keep existing manifest/plan checks without acquiring an unintended Tailwind gate. The API key/environment is read only after validation and start-receipt authority. `duplicate-reported` does not prove remote byte equality.

All commands fail nonzero on missing/invalid input before the relevant restore/build/push. The .NET convention is 0 success and 1 failure. Successful commands write structured JSON and Markdown summaries. The diagnostic envelope records `stage`; each error uses stable `code`, `message`, `expected`, `observed`, `nextAction`, `docsUrl`, and optional confined relative `evidencePath`. Output is bounded and redacted; do not capture secrets, full environments, whole CLI homes, or unrelated caches. Missing, duplicate, stale, foreign-run, wrong-source, v1, malformed or mismatched evidence is a failure.

## Operator recipes

In the workflow examples below, `PACKAGE_INDEX_DLL` is built from `SOURCE_COMMIT`; `SOURCE_CHECKOUT` is that checkout; `PRODUCER_DIRECTORY` and `AGGREGATE_DIRECTORY` are action-returned download paths; producer and aggregate IDs/digests come from protected outputs; repository/run/source values come from validated workflow context. Evidence does not supply its own expected values. Every report directory is fresh, caller-selected and disjoint from inputs. Pass these values via environment variables and quoted shell arguments.

Release consumer invocation:

```bash
dotnet "$PACKAGE_INDEX_DLL" verify-tailwind-consumer \
  --repo-root "$SOURCE_CHECKOUT" --mode release \
  --artifacts-input "$PRODUCER_DIRECTORY" \
  --artifact-manifest "$PRODUCER_DIRECTORY/package-artifact-manifest.json" \
  --producer-subject "$PRODUCER_DIRECTORY/tailwind-proof-subject.json" \
  --producer-artifact-id "$PRODUCER_ARTIFACT_ID" \
  --expected-subject-sha256 "$EXPECTED_SUBJECT_SHA256" \
  --repository-id "$EXPECTED_REPOSITORY_ID" \
  --producer-run-id "$PRODUCER_RUN_ID" --source-commit "$SOURCE_COMMIT" \
  --native-invocation-id "$NATIVE_INVOCATION_ID" --expected-rid "$EXPECTED_RID" \
  --work-directory "$FRESH_WORK_DIRECTORY" \
  --report-directory "$FRESH_WORK_DIRECTORY/reports"
```

The protected native workflow uses this contract; local developers use the compatibility script:

```bash
bash scripts/verify-tailwind-package-consumer.sh \
  --artifacts "$PACKAGE_ARTIFACTS" --package-version "$PACKAGE_VERSION" \
  --work-directory "$FRESH_WORK_DIRECTORY" \
  --report-path "$FRESH_WORK_DIRECTORY/tailwind-package-consumer-proof.md"
```

That report is `local-only`; it cannot authorize publication. This is a rule-demonstration path, not a substitute for actual native release acceptance.

For shell workflow steps, create a common argument array explicitly:

```bash
bundle_args=(
  --repo-root "$SOURCE_CHECKOUT"
  --artifacts-input "$PRODUCER_DIRECTORY"
  --artifact-manifest "$PRODUCER_DIRECTORY/package-artifact-manifest.json"
  --producer-subject "$PRODUCER_DIRECTORY/tailwind-proof-subject.json"
  --producer-artifact-id "$PRODUCER_ARTIFACT_ID"
  --expected-subject-sha256 "$EXPECTED_SUBJECT_SHA256"
  --repository-id "$EXPECTED_REPOSITORY_ID"
  --producer-run-id "$PRODUCER_RUN_ID" --source-commit "$SOURCE_COMMIT"
)
```

Aggregate invocation (host artifact map and IDs come from the trusted workflow resolver):

```bash
dotnet "$PACKAGE_INDEX_DLL" verify-tailwind-evidence "${bundle_args[@]}" \
  --mode aggregate --evidence-input "$HOST_EVIDENCE_DIRECTORY" \
  --host-artifacts-map "$HOST_ARTIFACTS_MAP" \
  --native-invocation-id "$NATIVE_INVOCATION_ID" \
  --report-directory "$AGGREGATE_REPORT_DIRECTORY"
```

Preflight uses a downloaded successful aggregate and its own fresh report directory:

```bash
aggregate_args=(
  --aggregate-input "$AGGREGATE_DIRECTORY"
  --aggregate-artifact-id "$AGGREGATE_ARTIFACT_ID"
  --expected-aggregate-sha256 "$EXPECTED_AGGREGATE_SHA256"
)
dotnet "$PACKAGE_INDEX_DLL" verify-tailwind-evidence "${bundle_args[@]}" \
  "${aggregate_args[@]}" --mode publish-preflight \
  --publication-directory "$PUBLICATION_DIRECTORY" \
  --report-directory "$PREFLIGHT_REPORT_DIRECTORY"
```

After a successful publication-start receipt upload or exact recovery, the prerelease publisher receives the same producer and aggregate bindings:

```bash
dotnet "$PACKAGE_INDEX_DLL" publish-prerelease "${bundle_args[@]}" \
  "${aggregate_args[@]}" --publication-directory "$PUBLICATION_DIRECTORY" \
  --publication-start-receipt "$START_RECEIPT_PATH" \
  --publication-start-artifact-id "$START_ARTIFACT_ID" \
  --publish-log "$PUBLISH_LEDGER_PATH" --report-directory "$PUBLISH_REPORT_DIRECTORY" \
  --api-key-env NUGET_API_KEY
```

Stable publication uses the same evidence arguments and substitutes `publish-stable`, retaining its stable source/version checks. These are protected-workflow recipes, not credentialed local quick starts.

## Failure and recovery

| Diagnostic code | Meaning and next action |
|---|---|
| `input-invalid` | Required flag/schema/path is invalid; correct the named input. |
| `artifact-hash-mismatch` | Bundle or restored archive differs from its producer hash; recover the exact original producer ID, never repack. |
| `payload-mismatch` | Restored/extracted protected payload differs; preserve evidence and investigate package/restore integrity. |
| `host-mismatch` | Observed host differs from expected RID mapping; fix runner configuration and rerun the complete native invocation. |
| `native-set-incomplete` | One or more current-invocation hosts failed, cancelled or are missing; rerun all five native jobs/all release jobs with the frozen producer. Do not use a failed-jobs-only rerun to assemble mixed attempts. |
| `history-unknown` | Workflow history is incomplete or unavailable; stop because absence is not proof that upload/publication did not happen. |
| `command-failed` / `cancelled` | Preserve stage report and command context; repair transient environment/runner issue, then use the permitted full rerun. |
| `limit-exceeded` | Bounded input/capture limit reached; inspect named evidence and address the source rather than raising limits ad hoc. |
| `recovery-authority-unavailable` | Original receipt or one of its bound artifacts is missing/expired/unreadable; same-run repack or replacement is forbidden. Stop and follow fix-forward/abandonment policy. |

Before a publication-start receipt exists, a new full five-host invocation may use the frozen producer if its authority remains valid. A new producer upload always requires fresh native proofs. A publish-only retry may reuse the original successful aggregate. Once the start receipt exists, it freezes producer, aggregate, invocation and prepared package hash inventory; recover and validate those exact IDs first, even if newer valid native evidence exists. If a push may have occurred before its ledger was written, its result is unknown until observed on retry. `--skip-duplicate` may make retry possible but does not establish remote byte identity. Content defects after any coordinated package is accepted require fix-forward to a new version, not retagging or replacement under the old receipt.

Upload every candidate, host-evidence, aggregate, start-receipt and ledger artifact with configured `github.retention_days`, validated as a positive decimal within the permitted limit; never silently choose another lifetime. Report the effective retry deadline: the earliest expiry among artifacts required by the authoritative start receipt and the original run's rerun deadline. GitHub reruns are available for 30 days and capped at 50 reruns, so the operative deadline is earlier of required-artifact expiry and 30 days, with remaining reruns reported. Longer artifact retention does not extend rerun eligibility. Before a start receipt, the producer must still be recoverable; available newer host artifacts cannot extend an expired producer.

The first native failure requires a complete all-five-host rerun in one invocation, reusing the original producer. A successful publish-only retry reuses the same original candidate/aggregate/start receipt. Missing original history or evidence blocks same-run recovery and must not cause silent repacking. See the [release operations guide](https://github.com/forge-trust/AppSurface/blob/main/.github/release-ops.md#tailwind-artifact-provenance-issue-798) for the protected operator path and [CI critical path](../eng/ci-critical-path.md#tailwind-provenance-release-path-issue-798) for runner and latency implications.

## Decision and pitfalls

Use this verifier for release provenance and the existing wrapper for local compatibility. Local success only exercises the current machine; it cannot be promoted to a release receipt. Native hosts restore from the same producer bundle and use a fresh isolated cache; a cache hit, source label, version match, NuGet `.nupkg.sha512`, or assets `contentHash` is not raw archive proof. The graph determines resolved package locations and membership observations, while the producer subject/manifest determine trusted identities and hashes. Do not let the graph rewrite the expected closure.

Keep original artifact uploads immutable and non-overwriting. New producer identity means new native proofs. Native proof retry means all five hosts in one invocation. Publish-only retry may reuse complete prior evidence only under original IDs. After start-receipt upload, that receipt governs even if a newer aggregate exists. Failed uploads, absent receipts, incomplete API history or missing artifacts do not establish that publication never started. V1 records cannot be upgraded by changing a schema label or filling fields from a different candidate. The guarantee ends at the push input; NuGet.org applies repository signing after upload.

Related references: the [PackageIndex maintainer guide](../tools/ForgeTrust.AppSurface.PackageIndex/README.md#tailwind-artifact-provenance-798), [Tailwind package guide](../Web/ForgeTrust.AppSurface.Web.Tailwind/README.md), [release operations](https://github.com/forge-trust/AppSurface/blob/main/.github/release-ops.md#tailwind-artifact-provenance-issue-798), and [CI critical path](../eng/ci-critical-path.md#tailwind-provenance-release-path-issue-798).
