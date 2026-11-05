# Repository EvidenceHost acceptance

This repository tool uses the public [EvidenceHost lifecycle](../../start-here/evidencehost.md) to validate AppSurface itself. It runs explicit producers against a supplied CLI package, checks published behavior independently, and emits a canonical plan and manifest. Use it when changing [coverage storage](../../guides/coverage-storage.md), package installation, or the native Evidence execution facilities.

## Local development

Run the same checked-in [evidence policy](https://github.com/forge-trust/AppSurface/blob/main/.github/evidence.policy.json) locally before a push:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- --operation local-plan
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- --operation local
```

`local-plan` explains the selected profile, producers and obligations without packing or running tests.
`local` executes that plan on the current OS and verifies its manifest and artifacts. Both default to the
current checkout, `.github/evidence.policy.json`, and `--base origin/main`. Use `--base COMMIT_OR_REF` for
another comparison, `--repository /absolute/checkout` from elsewhere, or `--output /absolute/fresh-output`
to retain results in a chosen location. Default output is a fresh ignored directory beneath
`TestResults/repository-evidence/`; each invocation prints its path. After building the tool once, invoke
its DLL directly or use `dotnet run --no-build --no-restore` with the same configuration to avoid rebuilding
the driver. Keep the driver current when changing evidence code.

Local collection combines the merge-base-to-HEAD diff, staged changes, unstaged changes and untracked
nonignored files. It retains both rename names and includes binary, mode-only and deleted paths. Resolve
merge conflicts first. Nothing needs to be committed to select evidence. The collector resolves the base
once without fetching; fetch explicitly when an updated remote comparison is needed. Missing base history
fails with no claim. An empty local inventory exits successfully without creating an evidence manifest;
this is distinct from a reviewed documentation change selecting `NoEvidenceRequired`.

Planning happens before the CLI candidate build, full solution coverage and package validation.
Reviewed documentation-only changes avoid all four producers. Test-only changes collect full solution
coverage and use its direct Codecov-mode patch gate without native fan-in. Ordinary source changes collect
coverage and package checks; on CI pull requests this producer union also selects the native coverage
supplement. Package metadata changes collect package checks.
Native CLI and shared infrastructure changes select the conservative set of all four producers.
`local` creates a fresh package only when required, using locked restore, then calls the same
[`RepositoryEvidenceWorkflow`](./RepositoryEvidenceWorkflow.cs) and producers as CI. Optional
`--dotnet /absolute/dotnet` selects the host used for packing and producer commands; otherwise the current
SDK host is discovered. No result cache or historical manifest is used to skip required work.

Use `--scope coverage`, `--scope packages`, or `--scope native` on either local operation to execute one
lane of the resolved plan. The default `--scope all` runs every selected producer. A scoped pass closes
only that lane; use `all` for a complete local policy result. A package-only or coverage-only plan does
not build the separate native CLI candidate. Scopes never reclassify changed paths: the full policy is
validated before a deterministic projection is bound into that lane's plan and manifest.
The projection applies its already selected profile to the complete inventory through one catch-all
rule. Actual paths remain in the canonical plan and diff digest; long names are not converted into
policy rule patterns with their shorter identifier limit.

The [C# Git collector](./RepositoryGitChanges.cs) records source content and index state in
`source-state.json` alongside `changed-paths.json`. Its `sourceDigest` is included in the derived
`local-policy.json` policy ID, so the canonical plan and resulting manifest bind the exact local source
state while preserving the checked-in rules and producer contracts. Local execution rechecks source and policy before the
candidate build, after it, and after producer completion. Edits during execution fail the local gate; if
producers already returned a manifest, its claim is downgraded to `ObservationOnly`. Keep output under
ignored `TestResults` or outside the checkout: writing output into an unignored source directory changes
the inventory and correctly invalidates the run. A local pass covers only the current OS; CI still owns
its selected native supplement. The recorded source digest describes dirty local inputs; package source metadata
retains HEAD and does not claim those edits were committed or independently attested.

## Shared policy and CI

The [native acceptance workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/native-evidence-validation.yml) uses the checked-in
[evidence policy](https://github.com/forge-trust/AppSurface/blob/main/.github/evidence.policy.json) and real changed paths to decide which native journeys
each Ubuntu, Windows and macOS lane must complete. Its final `Evidence manifest gate` requires successful
verification in all three lanes. Configure that check as required in repository branch protection when
adopting this workflow; a workflow file cannot configure branch protection itself.

| Selected profile | Required producers | Selection |
| --- | --- | --- |
| `no-evidence` | None | Only explicitly reviewed Markdown paths in the policy. |
| `coverage` | `solution-coverage` | Test projects and shared test support. |
| `packages` | `package-checks` | Package metadata/tooling, packaged READMEs and release entries. |
| `code` | Coverage and package checks | Ordinary C# source, project files and web implementation. |
| `storage` | `packed-storage` and coverage | The packed storage producer implementation. |
| `consumer` | `installed-consumer` and coverage | The installed consumer producer, options, or checked-in fixture. |
| `native-acceptance` | All four | Unmatched files, policy/workflow/shared infrastructure and native coverage/CLI implementation. |

The [planner](../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md) considers both names in a
rename. This policy opts into `combineProfiles`: mixed changes select the union of their required
producers and obligations. Identical declarations are deduplicated; conflicting IDs fail planning.
Documentation plus a test edit therefore requires coverage without unrelated package checks; test-only
coverage does not select native fan-in. The source-code profile combines coverage and package checks and
does select native fan-in on CI pull requests.
Unmatched files still select the conservative profile, and policy edits cannot exempt themselves.
New exceptions deserve the same review as test exemptions. Packaged README changes require package
documentation verification; the root README and explicitly listed authored guides are exempt.

`solution-coverage` invokes the existing collector over every solution test project, then reuses the built
CLI for its gates. It verifies the complete
successful timing inventory, and requires 95% line / 85% branch coverage with 0.5-point tolerance. Applicable
patch gates retain 95/85 and Codecov mode. The [CLI patch gate](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate)
documents that a patch metric with no measurable changed lines or branches reports 100%. Native fan-in
keeps its strict nonempty-denominator verification. Local patch snapshots include current tracked and
untracked content, so uncommitted code participates. CI PRs whose root policy selection includes coverage
and another producer retain native Linux/Windows/macOS supplementation, independent aggregate acceptance
and nondecreasing measurable patch denominators through the C#
[fan-in capture/composition/verifier](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md).
Main baseline runs enforce aggregate coverage without inventing a PR patch. Reports and diagnostics live
under `coverage/`, with optional `coverage-fanin/` and `coverage-fanin-composed/` children. Test-only PRs
run the direct patch gate and do not produce those fan-in directories; source-code-plus-package PRs require
the three native captures and fan-in verification.
CI coverage pins source identity and requires a clean, unchanged checkout before collection, after
collection and after gate verification, including direct runs without native fan-in. Supply a pinned
Git base for native fan-in; a local patch override cannot be combined with native CI captures. Dirty
local runs use their source digest and captured patch, with the local workflow checking that digest
around execution instead of requiring a clean checkout. The local workflow supplies that digest check
to each coverage checkpoint through an in-process callback. Direct `run --diff-file` calls fail closed;
use `--operation local` to collect and verify a dirty working tree.

`package-checks` runs the existing [PackageIndex](../ForgeTrust.AppSurface.PackageIndex/README.md)
`gate`, `verify` and `verify-packages` commands, using a fresh exact version and an isolated NuGet cache.
The last command already restores, builds, packs and exercises installed consumers; the adapter avoids
the old duplicate solution restore/build and reuses the PackageIndex driver after the first command.
It verifies the archive inventory and SHA-512 hashes before
closing its assertions, and retains the manifest, report and command transcript. Package consumer
proofs require their normal SDK, pnpm/Node and applicable platform prerequisites; missing capabilities
fail evidence rather than skipping requirements. Ignore status affects changed-path selection, not what
the existing PackageIndex checks inspect: ignored local artifacts in inspected paths can still affect or
fail package checks. Such failures are evidence failures, not an optional skip. Diagnostics are under
`package-checks/`.
Run installed-consumer proofs on a host with the required Docker, PostgreSQL and listener capabilities.
The Durable template's [test builders](../../Durable/ForgeTrust.AppSurface.Durable.Templates/content/durable-worker/README.md)
disable configuration file reload before creating default configuration sources. Without that setting,
native file watchers can stall builder construction in a restricted process after restore, build and
formatting succeed, leaving a timed-out `authored-tests` phase with no TRX observation. Keep the existing
180-second test budget; removing unused test-fixture watchers addresses the stall while preserving
application configuration defaults. The [PackageIndex proof reference](../ForgeTrust.AppSurface.PackageIndex/README.md#durable-worker-template-proof-806)
explains the phase receipt and its limits; missing test results never authorize a successful claim.
Both repository producers propagate the selected `--dotnet` host through `DOTNET_HOST_PATH` and the
child process search path, so nested SDK commands resolve the same host.

The [build workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/build.yml) plans coverage before native collection and emits
a verified empty-lane manifest when coverage is unselected. The
[package gate](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/package-gate.yml) plans before web/package prerequisites and uses
the shared package producer. Existing artifact production, release policy and publishing workflows keep
their delivery responsibilities; this policy controls validation evidence, not publication.

The internal `RepositoryEvidenceWorkflow` adapter exposes three operations through this executable:

```bash
dotnet tools/ForgeTrust.AppSurface.Evidence.Validation/bin/Release/net10.0/ForgeTrust.AppSurface.Evidence.Validation.dll \
  --operation plan --policy .github/evidence.policy.json \
  --changed-paths /absolute/changed-paths.json --output /absolute/fresh-plan

dotnet tools/ForgeTrust.AppSurface.Evidence.Validation/bin/Release/net10.0/ForgeTrust.AppSurface.Evidence.Validation.dll \
  --operation run --policy .github/evidence.policy.json \
  --changed-paths /absolute/changed-paths.json --plan /absolute/fresh-plan/evidence-plan.json \
  --output /absolute/fresh-proof --package /absolute/feed/ForgeTrust.AppSurface.Cli.VERSION.nupkg \
  --version VERSION --source-commit COMMIT --dotnet /absolute/dotnet

dotnet tools/ForgeTrust.AppSurface.Evidence.Validation/bin/Release/net10.0/ForgeTrust.AppSurface.Evidence.Validation.dll \
  --operation verify --policy .github/evidence.policy.json \
  --changed-paths /absolute/changed-paths.json --plan /absolute/fresh-plan/evidence-plan.json \
  --manifest /absolute/fresh-proof/evidence-manifest.json --artifact-root /absolute/fresh-proof/artifacts
```

`plan` writes `evidence-plan.json`, `selection.json` (`profileId`, `requiresPackage`, `hasProducers`,
`requiresCoverage`, `requiresNativeCoverage`, `requiresPackageChecks`, `producerIds`) and
`evidence-summary.md` into a fresh directory. `requiresNativeCoverage` reflects the root policy selection:
it is true when `solution-coverage` and at least one other producer are selected. CI uses it to select the
native matrix and PR fan-in; a test-only coverage selection stays on the direct patch gate. `run` requires
that exact plan and independently resolves
the policy and paths again before executing only its selected registrations through EvidenceHost. It
writes the same plan/selection/summary plus `evidence-manifest.json` and declared artifacts. For an explicit
empty profile, omit the package, version, source commit, dotnet and scratch options: no package is built,
no producer runs, and the host emits `NoEvidenceRequired`.

`verify` re-resolves the current policy/paths and requires the original plan to match, validates canonical
manifest binding and PR gate eligibility, and rehashes every retained artifact beneath its producer
directory. Missing/altered artifacts, links, failed cleanup, incomplete or observation claims fail the
gate. Both `TargetedComplete` and an explicitly selected `NoEvidenceRequired` may pass. JSON inputs are
capped at 16 MiB and empty changed-path inventories fail closed. Cancellation and invalid arguments return
nonzero. Failed execution retains a manifest when the host returned one; planning/preflight failures may
have no manifest and cannot pass the gate.

These operations accept the four producer declarations above with their exact implementation
contracts and require an obligation for every declared assertion. Unknown IDs, weakened declarations,
release profiles and resource declarations are rejected. `run` accepts the existing `--repository` and
`--scratch-parent` locations but keeps default workspace budgets and the checked-in consumer; `--mode`,
external consumer overrides and fan-in are available only in the separate explicit journey recipe below.

The adapter requires the conservative profile to retain all four producers and rejects a policy-file
change (including its rename) if it selects a narrower profile. A policy edit cannot exempt itself from
the full repository acceptance baseline. The executable and workflow remain reviewed repository code; this
is not a security boundary against a PR that rewrites the gate implementation itself.

The [C# changed-path collector](./RepositoryGitChanges.cs) reads the event file and obtains a
NUL-delimited Git inventory, including binary and mode-only files and rename provenance. PR runs use the
PR head with the merge base of the exact event base/head commits; the synthetic merge commit is not the
test source. Pushes use the event's before/after commits, and manual runs use the complete tracked file
inventory. Missing history and unrepresentable paths fail planning. CI invokes the same executable:

```bash
dotnet tools/ForgeTrust.AppSurface.Evidence.Validation/bin/Release/net10.0/ForgeTrust.AppSurface.Evidence.Validation.dll \
  --operation collect --repository /absolute/checkout --event-file /absolute/event.json \
  --event-name pull_request --workflow-sha WORKFLOW_SHA --output /absolute/fresh-changes
```

`collect` writes `changed-paths.json` and `source-state.json`; it never selects a synthetic fallback path
for an empty CI diff. The [C# collector tests](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/RepositoryGitChangesTests.cs)
exercise real Git repositories and malformed inputs, and the
[workflow contract tests](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/RepositoryEvidenceWorkflowContractTests.cs)
verify the CI integration using the existing .NET test project. This path requires only Git and .NET.

`LocalEvidenceWorkflow` has internal package-command and execution delegates for deterministic orchestration
tests. The execution delegate replaces only the `run` operation: policy planning, source snapshots and the
final canonical manifest/artifact verification remain real. A nonzero delegate result fails the workflow;
source drift after a completed execution downgrades its manifest to `ObservationOnly`. These seams do not
change the executable's command-line inputs or permit production callers to supply their own claims.

Plans bind policy and normalized changed paths, not independent commit attestation. The workflow obtains
the checkout, event inputs, candidate package and manifests within the current run; verification detects
inconsistency, not artifact origin. It does not aggregate manifests across jobs or replace existing
[coverage fan-in](../../guides/coverage-fan-in.md).

## Run the acceptance journeys

Build a candidate CLI package with its real source commit in NuGet repository metadata. Select an absolute .NET SDK host path. Invoke the tool from the checkout:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- \
  --package /absolute/feed/ForgeTrust.AppSurface.Cli.VERSION.nupkg \
  --version VERSION --source-commit COMMIT \
  --dotnet /absolute/dotnet --output /absolute/fresh-evidence
```

On Windows, supply absolute Windows paths to the same options. The tool passes arguments directly to processes and does not interpolate a shell command. `--help` lists all options. `--mode storage` selects the packed storage journey, `--mode consumer` selects the installed consumer, and the default `all` requires both to pass.

The storage producer extracts the package into an owned workspace and verifies its package ID, exact version and source metadata. It then runs read-only status, first and repeated merge, invalid input, maintenance preview, pin, restore, overwrite refusal, unpin, and explicit expiry. Hashes of the published original-file inventory must match restored bytes; current and restored outputs must survive maintenance. Regenerated metadata is checked under its documented contract rather than treated as an original archived file.

The consumer producer installs the supplied package in an isolated tool directory and cache, verifies installed bytes, and collects fresh Release coverage and JUnit results from the checked-in two-project consumer. Every required project and test fragment must execute successfully. Overall coverage must pass 95% line and 85% branch with zero tolerance. Report, manifest, log and timing readers must still work, and source/settings integrity must remain unchanged. This fixture proves the repeatable consumer recipe; it does not claim to have executed an arbitrary external application's tests.

## External consumer profile

Pass `--consumer-profile /absolute/profile.json` to run a consumer you own. A profile supplies these fields:

```json
{
  "sourceDirectory": "/absolute/consumer-repository",
  "expectedCommit": "PINNED_COMMIT",
  "solutionPath": "Consumer.slnx",
  "testProjects": [],
  "expectedRequiredTestFragments": ["CriticalBehaviorTests"],
  "permittedSkipPatterns": {
    "KnownPlatformOnlyTest": "Requires unavailable native platform capability on this runner"
  },
  "diffBase": "PINNED_DIFF_BASE",
  "environment": {}
}
```

The source repository is read for identity/integrity and cloned into scratch. The original remains caller-owned. External profiles must provide a nonblank `expectedCommit`; the producer compares source HEAD with this explicit pin before cloning. The built-in fixture does not require this field. An explicit project list must contain the complete intended acceptance inventory; otherwise the solution determines it. `testProjects` is a scoped acceptance declaration, so it must list every intended test project for that profile; it does not silently claim full repository coverage. `.sln` and `.slnx` inputs are parsed before execution and must resolve to at least one test project. When `diffBase` is supplied, the consumer also requires 95/85 patch coverage with zero tolerance. Provide required test fragments so a successful exit cannot hide a missing critical suite; required fragments must be present in non-skipped, passing JUnit cases. Skips are rejected by default. External profiles may explicitly allow a skip only when `permittedSkipPatterns` contains a matching testcase attribute fragment and a nonempty rationale; those skips remain part of the declared profile scope. The checked-in fixture has no permitted skips. JUnit acceptance reads exactly one top-level `junit-*.xml` report per declared project, capped at 16 MiB per report, 64 MiB total and 100,000 testcases. Oversized evidence fails acceptance; it never silently drops tests. DTDs are prohibited, and retained testcase summaries omit output bodies and parent-document references to bound memory. For larger applications, compose separate Evidence obligations that together retain the full intended test inventory. These report bounds never waive the repository coverage gate or allow a missing required suite to pass. Resource-backed applications should compose explicit readiness probes through the [Aspire Evidence package](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md); this tool does not infer or provision their databases. Keep secrets out of profile files and durable evidence.

## Storage and lifetime

`--maximum-scratch-bytes` defaults to 2 GiB per producer and `--minimum-free-bytes` to 1 GiB. The native [workspace budget](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#owned-workspaces-and-command-budgets) checks before commands and during execution. Measurements are sampled, so a child can allocate between checks; this is not an operating-system disk quota. A budget failure must stop the command and prevent a complete claim. Entry/depth limits also bound filesystem inspection.

Candidate JSON readers also enforce cumulative byte limits: 1 MiB for the consumer's `coverage-gate.json` and 16 MiB for the storage run's `.appsurface-coverage-run.json`. Missing, empty, linked, oversized or invalid JSON fails acceptance. These bounds apply before parsing and throughout reading, with cancellation checks; they do not trim evidence or relax coverage requirements.

`--scratch-parent` must already exist and defaults to the platform temporary directory. Each producer owns a fresh random child. Cleanup removes only that child after checking its ownership and rejecting links. Durable `--output` must be fresh and outside scratch, and remains caller-owned. If cleanup cannot complete, EvidenceHost records an incomplete result. Use trusted local workspaces; path checks do not provide an adversarial filesystem sandbox.

## Evidence and claims

The output contains `evidence-plan.json`, `evidence-manifest.json` and bounded declared artifacts beneath `artifacts/PRODUCER/`. The manifest includes artifact hashes, producer assertions, lifecycle timing and cleanup status. Verify it through [the native contracts](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md) or `appsurface evidence verify`. Verification detects internal inconsistency; obtain artifacts and trusted inputs through an appropriate CI channel because hashes are not independent attestation.

Only `TargetedComplete` returns exit zero. Missing prerequisites, failed assertions, incomplete output capture, timeouts and failed cleanup prevent success. Cancellation returns a nonzero exit. Each journey retains its scope: passing the fixture is not a replacement for the full solution or an external consumer's required gates.

## Verified native coverage fan-in

`--fan-in-request /absolute/trusted-request.json` adds a required producer using [native fan-in verification](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#verified-coverage-fan-in). Supply the trusted expected source commit/tree, SDK, Release/Debug configuration, workflow run and attempt, full solution project inventory, primary capture, exactly three native captures, composed reports and gate JSON. The request is trusted configuration, not an automatically authenticated attestation.

The verifier checks every input hash and recomputes composition. The independently required full-solution 95/85 aggregate gate and 95/85 patch gate retain the repository's 0.5-point tolerance, Codecov patch mode and nonempty, nondecreasing measurable patch denominators. Native-only points cannot be discarded. Restored or historical reports cannot be relabeled as current evidence. The existing [coverage fan-in collection workflow](../../guides/coverage-fan-in.md) remains the producer of these inputs.

The packed-storage fixture writes its explicit retention policy with owner-only access: mode `0600` on Unix and a protected current-user ACL on Windows. This satisfies the existing [private storage contract](../../guides/coverage-storage.md); it does not relax policy-file access validation. Failure artifacts redact the owned workspace path and retain at most 16 KiB of UTF-8 bytes per captured output stream, keeping the most recent complete UTF-8 text. External consumer output remains withheld. A size cap is not general secret redaction: do not pass secrets to fixture commands, because other failure details can appear in CI logs. Installed CLI assembly identity is checked only beneath the installed tool path private `.store`; at least one candidate must exist and every matching assembly must match the package archive hash, so workspace decoys cannot mask a mismatch.

The internal `PackedStorageProducer.WritePrivateRetentionPolicyAsync` seam writes that policy only inside freshly owned journey scratch, before maintenance reads it; it is not a consumer-policy repair API. The [native coverage regression](../../Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageArtifactFixturePolicyTests.cs) checks policy bytes, Unix permissions or Windows ownership and protected access rules, cancellation, and unrelated-file preservation. Its CLI test-project reference and friend access let the existing [native coverage selection](../../guides/coverage-fan-in.md) instrument the same implementation as the acceptance journey without changing filters or coverage policy.
