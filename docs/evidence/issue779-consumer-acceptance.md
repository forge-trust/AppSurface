# EvidenceHost #779 consumer acceptance

Status: **not admitted**. This record contains observed prerequisites and missing proofs; it does not issue runtime admission. The [approved design](../designs/issue-779-evidencehost-trust-boundary.md) and [acceptance test plan](../plans/issue-779-evidencehost-test-plan.md) define the required outcome. The release maintainer owns this record and the corresponding versioned provider/platform allowlist.

## Candidate and ownership

The confirmed first consumer is `forge-trust/AppSurface` on GitHub Actions. Andrew, the requesting repository maintainer, confirmed option **A — Use AppSurface's GitHub Actions workflows; I'm the CI owner** on 2026-09-30. Andrew owns the reviewed protected/fork map and release acceptance of this first integration. No external consumer is known from this execution pass. This confirms ownership, not successful isolation/supervision/platform proof or runtime admission.

Protected tool revision inspected: `5dc141db81009348538d3f6b7634a99ccb34cd99`. Candidate changes are developed on `codex/make-it-so-evidencehost-779`; that branch is not a protected base and cannot supply production Trusted authority.

## Observed workflow map

| Fact | Existing AppSurface workflow | Admission consequence |
| --- | --- | --- |
| Definition | [build.yml](../../.github/workflows/build.yml) runs on `push` to `main`, `pull_request` to `main`, and `workflow_dispatch`. | A PR job executing its PR-supplied workflow/tooling is an observation of subject code, not an independently protected verifier. |
| Checkout | The `build` job uses pinned checkout, `persist-credentials: false`, no separate protected tool checkout, and PR fetch depth 2. | The checkout includes PR code. Separate pinned base tooling is absent. |
| First subject execution | `dotnet restore ForgeTrust.AppSurface.slnx --locked-mode` can evaluate subject MSBuild inputs. Later package/snippet/assets/coverage commands also execute subject tooling. | Admission and isolated subject ownership cannot be placed only before the later coverage command. |
| Permissions | Workflow-level `contents: read`; the `build` job does not override it. Other export/deploy jobs have separate permissions. | A read-only token does not prove worker/subject isolation. The supported evidence worker and restricted child require an explicitly sanitized environment. |
| Protected values | The build job exposes only the Boolean `CODECOV_TOKEN_PRESENT`; actual Codecov token usage is in the uploader step. No EvidenceHost-specific secret projection is currently registered here. | Inspect exact per-step injection and remove protected credentials from the execution worker/subject. Do not infer secret absence from a verifier-shaped environment variable. |
| Output | Coverage and test artifacts use checkout-relative and runner temporary paths, with no enforced subject denial of the final EvidenceHost output root. | Fresh protected parent/handle binding, exclusive no-follow allocation and denied subject writes remain unproved. |
| Subject identity | Subject commands run as the same runner identity as tool commands. | Two directories would not establish denied component loads or output writes. Existing jobs are rejected for Trusted. |
| Supervision | A 45-minute job timeout surrounds the `build` job; current host waits and process ownership do not expose the required independently armed worker/descendant exit contract. | Outer job timeout alone does not satisfy the bounded stop/join/fatal rule. |
| Fork behavior | `pull_request` includes fork events. Its source builds can run without repository secret values under GitHub's fork policy, but no separate admitted Observation profile/supervisor exists in the current code. | Add an actual dependency-free, secret-free supervised Observation proof. Do not relabel the existing full build as Observation admission. |

The main-branch protection endpoint returned `404 Branch not protected`. The effective rules endpoint additionally reported an **active default-branch ruleset** requiring pull requests, one approval, linear history and no force pushes. Organization administrators may bypass it. These are distinct endpoint observations; the absence of legacy branch protection does not establish that `main` is unprotected. Admission must bind the reviewed immutable base revision and protected workflow rather than infer protection from either an environment Boolean or this prose record.

## Required consumer proof before API freeze

1. Confirm the named consumer and CI owner. Record their reviewed protected/fork event/ref map, immutable workflow/base identity and release acceptance responsibility.
2. Demonstrate a consumer-owned independent supervisor that starts before every configurable callback and terminates blocked workers without waiting for their callback thread. Prove owned descendant and pump exit, cooperative stop and unsuccessful fatal stop with no disposal/finalization marker.
3. Run subject code under a separately enforced identity/filesystem boundary. A negative fixture must fail to read/load the pinned verifier/registration or write final output, while a neighboring declared subject input succeeds.
4. Demonstrate exclusive fresh directory/file allocation under the admitted protected parent, preserve handle identity for writing/hashing, and reject collision, alias, substituted parent and changed ownership on each admitted platform.
5. Exercise the actual protected downstream gate with the expected current run/base identities: supported positive, fork Observation, forged pass, missing manifest, stale/mismatched acceptance proof and both cancellation/allocation orders.
6. Record immutable run/revision/artifact identifiers and the matching provider/platform/build entries. Missing, failed or stale entries keep that combination excluded from Trusted registration.

Local Docker availability is useful only for a Linux validation environment. It is not a selected EvidenceHost Docker containment runtime, proof of runner provenance, or CI acceptance evidence. The separately scoped Docker pilot remains unchanged.

## Versioned support allowlist

Allowlist revision: `issue779-v1-draft`.

| Provider / platform | CI owner | Supervision / allocation mechanism | Immutable proof runs and artifacts | State |
| --- | --- | --- | --- | --- |
| GitHub Actions / Linux | Andrew | provisional systemd transient services + separate worker/subject Unix identities + retained-descriptor `openat2` allocation | [mechanism run 36988778445](https://github.com/forge-trust/AppSurface/actions/runs/36988778445), artifact `11218159613`; mechanism only | excluded |
| GitHub Actions / Windows | Andrew | not proved | none | excluded |
| GitHub Actions / macOS | Andrew | not proved | none | excluded |

The documentation record cannot authenticate facts or mint a lease. The protected release fixture must verify matching immutable proof entries against its build, and runtime admission must validate its exact registered protected context.

## Provisional Linux mechanism preparation

The [consumer fixture](../../tests/evidencehost-consumer/README.md) and
[candidate workflow](../../.github/workflows/evidencehost-mechanism-proof.yml) now make the proposed mechanism
reviewable. PID 1 owns transient services with a deadline armed before callbacks, control-group termination,
and a bounded final kill. Separate non-login accounts deny subject access to simulated protected tooling,
worker state and output. Root owns tooling; the worker group has read access. The allocation probe tests
exclusive retained-descriptor allocation and identity rechecking. None of these files issues an admission lease.

Local evidence on 2026-09-30:

- The isolated .NET 10 worker built with warnings as errors: exit 0, zero warnings and errors.
- Real local worker processes observed normal completion (exit 0), cooperative stop after join (exit 2),
  and nonreturning `Environment.FailFast` (signal exit -6). Fatal unwind/disposal/manifest markers were absent.
  Report: `/private/tmp/issue779-worker-controls.json`. These are entry mechanism checks on macOS, not a
  supported provider or full host lifecycle proof.
- A Linux validation container with distinct UIDs, removed capabilities and no-new-privileges ran the real
  subject fixture. All twelve allowed-input/denial/environment assertions passed (exit 0). This proves only
  the fixture's UID checks in that environment. It does not supply independent systemd supervision,
  GitHub runner provenance, production-worker/subject integration or platform admission.
- The actual Linux supervision probe rejects this macOS host (exit 1) with
  `requires-root-on-disposable-linux-vm`; the combined launcher rejects the host (exit 2).
  No skipped run or failed host is credited as a positive proof.
- Linux allocation C compiler and probe both exited 0 in the disposable validation container. All 14
  allocation scenarios were observed and matched their expected outcomes. The probe checks bounded
  content equality; it does not implement cryptographic writer/manifest hashing or real CI acceptance.
- Python syntax, shell syntax, C# formatting and `git diff --check` passed for the prepared fixture.

The preliminary workflow produced a real systemd/cgroup-v2 mechanism observation described below. Full consumer and downstream acceptance remain required before public provider API freeze. The new workflow cannot be
manually dispatched until it exists on the default branch under
[GitHub's dispatch rules](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).
The user selected the staged proof route on 2026-10-01: create a preliminary draft mechanism-proof PR after
the unchanged coverage gate passes, obtain its real runner observations, then continue the full #779
implementation. The branch is published as [draft PR #850](https://github.com/forge-trust/AppSurface/pull/850). Trusted remains excluded until the actual
protected integration and immutable acceptance evidence are complete. The preliminary PR references #779
without closing it; final implementation and acceptance own closure. No coverage or release exception has
been inferred.

## Actual Ubuntu mechanism observation (2026-10-02)

The candidate [mechanism workflow run 36988778445](https://github.com/forge-trust/AppSurface/actions/runs/36988778445) succeeded on attempt 1 at source revision `4dd992ec1bc2df8220c73149115c5b478edb0085`. Its merge checkout was `d0f6acd18c90d4c7950e00f428bf9de8f2b88522`. GitHub API job metadata identified `ubuntu-24.04`, runner ID `1000061870` and the GitHub Actions runner group; repository runner inventory contained zero entries. These metadata are observations, not runtime authentication.

The parent verified the downloaded artifact ZIP, GitHub artifact digest, internal file index and source/run/attempt bindings. Artifact `11218159613` had SHA-256 `0dabd2ffa925d4f8031264ce8d952b5592311ba5f9559ea4a35bf7bb99e4bf9f`. The reports recorded systemd `255.4-1ubuntu8.17`, kernel `6.17.0-1022-azure`, x86_64, nine worker lifecycle cases, twelve subject-boundary assertions and fourteen allocation cases. Every required case was observed and passed; the artifact still states `admission: none`.

This permits implementation against the demonstrated internal Linux mechanism. It does not establish protected-base workflow execution, production CLI/Aspire integration, the downstream gate, fork Observation execution, release acceptance, or all 57 acceptance groups. Linux, Windows and macOS remain excluded from Trusted registration. The broader build for the preliminary head failed; that failure is being diagnosed independently of the successful mechanism job.

## Current candidate validation observations (2026-10-02)

The separate native-validation branch captures the shared implementation without
updating the preliminary feature PR. Its source snapshot
`43e826b11ece70845ca1eabdec79679a9865d797` is candidate code, not protected base
tooling. [Native run 37024942137](https://github.com/forge-trust/AppSurface/actions/runs/37024942137)
verified 2707 captured files before and after execution. The CLI Evidence filter
passed 336 cases and failed one test lookup that omitted the producer directory.
The broker positive case itself produced a Passed Observation manifest with
completed cleanup and 100% numerical line and branch coverage. Artifact
`11235321545` retains the results. The synthetic broker cgroup does not establish
actual systemd supervision, and the Aspire selection and unchanged solution
coverage wrapper were not reached. Later source fixes require a new snapshot.

[Systemd run 37025006612](https://github.com/forge-trust/AppSurface/actions/runs/37025006612)
built the same snapshot with zero warnings or errors, then failed at the
production Observation launcher with a generic host failure. Artifact `11234757444`
contains build and console logs; no completed `runtime-proof.json` exists. This
is a failed current runtime observation. It adds no entry to the Trusted allowlist
and does not satisfy protected workflow, fork or downstream acceptance. See the
[execution record](../plans/issue-779-execution.md#native-candidate-results-and-invalid-outcome-regression-2026-10-02)
for the focused fixes and remaining gates.

### Native solution gate and systemd startup result (2026-10-02)

[Native run 37028100295](https://github.com/forge-trust/AppSurface/actions/runs/37028100295)
verified all 2707 source files at private snapshot
`ce8375688cc62621c6de6259494ecea63dd91fd4` before and after execution. Its CLI
Evidence selection passed **338/338** and Aspire selection passed **81/81**,
with no failures or skips. The exact `./scripts/coverage-solution.sh` then
reported **15266 passed / 1 failed / 2 skipped** across 54 test projects and
exited **1** with `ASCOV120`. The remaining failure was the repository test-path
policy: the physical report lookup needed `TestPathUtils.PathUnder` for a dynamic
relative path. The two existing skips were the PostgreSQL previous-package
rollback case and manual candidate-publication replay. Neither was introduced
to accommodate this change.

Merged coverage measured **94.88% line / 88.52% branch**. The wrapper stopped
after the test failure, so aggregate threshold evaluation and patch coverage
were **not reached**. The required 95/85 aggregate and patch thresholds, base
revision and sandbox checks remain unchanged. Artifact `11238035677` has verified
ZIP SHA-256 `c658c5e4a3908518f49dcb681e11610d28d8d3dc55801118f15e80c0fda732ef`.
These results do not validate later source changes or the new standalone
[protected gate consumer](../../tests/evidencehost-consumer/ProtectedGateConsumer/README.md)
and [restricted Aspire child fixture](../../tests/evidencehost-consumer/AspireChild/README.md).

[Systemd run 37032279319](https://github.com/forge-trust/AppSurface/actions/runs/37032279319)
used snapshot `3a3083ce47695189cfb9c8950b96a10bb021363c`, including the bounded,
hash-pinned six-file runtime-subject projection. All 2707 source hashes matched;
subject and CLI builds reported zero warnings and errors. The worker's
`systemd-run` submission/start returned **1**. Its closed diagnostic category was
`systemd-operation-failed`, with operation `systemd-run`. No numeric worker status,
completed runtime proof, typed manifest or structural-verification result was
produced. Artifact `11237732048` has verified ZIP SHA-256
`d42feb161195e48bed07dc70c8f2d937b4b04c37f902a4988ace7f200fa55d39`.
Bounded startup status and the unit's private temporary-directory visibility are
the next diagnosis; the exit alone establishes neither cause. Trusted remains
excluded for every provider/platform entry above.

### Full snapshot startup diagnosis (2026-10-02)

The next immutable candidate, `cdadf95bb91e46c21d46dc72a8317950e9fd5da4`,
contains 2725 source files, including the standalone gate and Aspire fixtures.
[Systemd run 37036246639](https://github.com/forge-trust/AppSurface/actions/runs/37036246639)
verified that binding and retained all six warning-free build/publish streams.
The worker unit was loaded but failed with `ExecMainCode=1`,
`ExecMainStatus=226` (`EXIT_NAMESPACE`) and `Result=exit-code`. This narrows the
failure to namespace setup; the individual failing operation and path were not
captured. Moving the fresh protected workspace outside private temporary mounts
requires another native observation with the existing namespace guards retained.
No completed Observation manifest or runtime proof was produced. Artifact
`11240576268` has verified ZIP SHA-256
`1ba681224d99a58b92ffd1db11fadce4ab2fab8a43188d75058084510638f11f`.

[Aspire mechanism run 37036436702](https://github.com/forge-trust/AppSurface/actions/runs/37036436702)
used the same full source binding on Ubuntu 24.04 x64, systemd 255 and .NET
10.0.401. Locked restore and build exited zero with no warning diagnostics. The
pinned Aspire 13.4.4 DCP payload was checked as an executable Linux x64 ELF;
that file check does not establish DCP execution. The factory-stall control
passed. The other four controls failed before any DCP or resource PID was
observed: Aspire attempted to create its store beneath the embedded build
`AspireChild/obj/.aspire` path under the protected home directory.

All five controls confirmed their actual nonroot identity/cgroup, watchdog
acknowledgement and clean watchdog exit, complete error-free pump EOF and exact
byte accounting, and empty owned cgroups before cleanup. These successful
teardown observations do not supply the missing resource or access-denial proof.
The [Aspire child fixture](../../tests/evidencehost-consumer/AspireChild/README.md)
records the pinned SDK store configuration and the next narrow scratch-path
correction. Neither native result proves the shared Aspire host, protected-base
execution, Trusted admission or downstream acceptance. Every provider/platform
entry remains excluded.

### Full-source gate and next startup failures (2026-10-02)

[Native gate run 37037592365](https://github.com/forge-trust/AppSurface/actions/runs/37037592365)
used the same 2725-file candidate and reached the unchanged
[`coverage-solution.sh` gate](../../scripts/coverage-solution.sh). All 54 test
projects completed with 15269 passing tests, zero failures and two existing
skips. The aggregate report measured 94.88% lines and 88.522% branches, within
the configured gate's existing tolerance. Patch coverage measured 82.852%
lines and 76.6374% branches; the gate exited **1** with `ASCOV020`. This is a
failed required gate. The next work adds meaningful tests for the measured
uncovered worker, budget, protected producer and host paths. The thresholds,
comparison base, patch mode, selections and exclusions remain unchanged.

[Systemd run 37039914626](https://github.com/forge-trust/AppSurface/actions/runs/37039914626)
used the next complete snapshot, `2b7da72fa2eba5d15300052efc5818fb868d7f9d`.
The namespace failure was gone, but the worker exited with `ExecMainCode=1`
and `ExecMainStatus=1`. The closed cause was `worker-protocol-incomplete`;
the retained receipt did not distinguish missing readiness from a missing
terminal acknowledgement. There was no completed runtime proof or manifest.
The next bounded root diagnostic records those lifecycle checkpoints and
the unit's private journal without publishing raw subject output.

[Aspire mechanism run 37039916803](https://github.com/forge-trust/AppSurface/actions/runs/37039916803)
used that same frozen source. Locked native restore and build again exited
zero with no warnings or errors. **Zero of five** controls passed. Four
startup cases got past the store failure, then tried the embedded NuGet DCP
path beneath protected home: the fixture supplied `Dcp:CliPath`, while
Aspire 13.4.4 reads `DcpPublisher:CliPath`. No DCP or resource PID was observed.
The factory-stall case failed before process observations were published;
its exact identity rejection facts were not retained, so no specific kernel
race is established. All five cases confirmed empty owned cgroups and
complete error-free pump accounting before cleanup.

The [child fixture](../../tests/evidencehost-consumer/AspireChild/README.md)
now supplies the pinned SDK's correct DCP option section and records a
bounded, root-only identity diagnostic when the unchanged UID/cgroup guard
rejects a process. Its scoped build is warning-free and all forty portable
controls pass. These source checks require a new exact native run; they do
not turn either earlier failure into acceptance. Shared Aspire execution,
protected downstream acceptance and all remaining acceptance groups are
still required, and Trusted support remains excluded.

### Frozen-source allocation and resource startup failures (2026-10-02)

The next complete snapshot, `8a964188e85561d57119133d26b552f9412e2445`,
contains 2725 source files. Both dispatched workflows, the Aspire wrapper and
binding, and every frozen source hash were independently verified. Downloaded
artifact digests matched GitHub's recorded digests.

[Systemd run 37047555055](https://github.com/forge-trust/AppSurface/actions/runs/37047555055)
exited **1** after warning-free subject and CLI builds. The worker reached
readiness and completed its wait, with no active handlers or subject runs,
but did not send the terminal protocol acknowledgement. Its root-owned
private journal contained `ASEVD409`, output allocation or activation failure.
The journal was retained as one bounded regular member with mode 0600; raw
journal bytes were excluded from the public diagnostic. The allocation stage
currently discards its internal exception when reporting that code, so the
specific filesystem, cancellation or activation cause remains unverified.
No completed runtime proof, manifest or coverage measurement was produced.

[Aspire mechanism run 37047560583](https://github.com/forge-trust/AppSurface/actions/runs/37047560583)
exited **1**, with **zero of five** controls passing. Locked restore and native
build exited zero without warning diagnostics. The four HTTP cases observed
AppHost, DCP and resource processes, then resource startup failed while creating
threads. Task-limit exhaustion versus memory exhaustion remains unresolved;
the retained receipts lack the necessary cgroup counters. No healthy readiness
or authenticated HTTP 503 control was established.

The factory-stall identity diagnostic recorded candidate/MainPID 3568, expected
UID 999, actual UID tuple `[0,0,0,0]`, matching cgroup membership and active unit
state. The unchanged identity guard rejected it. These facts do not establish
that the factory callback ran or prove a particular startup race. All five cases
reported empty owned groups, completed cleanup, clean watchdog acknowledgement
and exit, and both error-free pump EOFs. Successful teardown does not validate
the failed positive or negative controls.

Further diagnosis retains closed allocation operation and stage facts, and
checks completed executable startup plus bounded task/memory counters in the
[child fixture](../../tests/evidencehost-consumer/AspireChild/README.md).
Namespace, ownership, admission and gate requirements still apply. Shared Aspire
execution, protected downstream acceptance and all 57 required groups remain
incomplete; every production Trusted support entry remains excluded.

### Frozen-source fixture-path policy failure (2026-10-02)

The [unchanged coverage run 37047553718](https://github.com/forge-trust/AppSurface/actions/runs/37047553718)
used the same complete frozen snapshot `8a964188e85561d57119133d26b552f9412e2445`.
The dispatched workflow and script, before/after source receipts, all 2725
hashes, preserved comparison base and downloaded artifact digest matched.
It exited **1** with `ASCOV120`: the repository fixture-path policy rejected
`Path.Join` with `artifact.RelativePath` in the
[Bootstrap artifact test](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs).

Focused CLI tests passed **403/403** and Aspire tests passed **143/143**.
The solution recorded **15,393 passed / 1 failed / 2 existing skips** across
54 projects, with 53 project exits zero and no compiler warnings or errors.
Measured aggregate coverage was **94.98% line / 88.79% branch**. The patch
gate was not reached, so this run provides no current patch percentage or
green coverage gate.

The source correction uses the existing
[`TestPathUtils.PathUnder` policy](../../tests/ForgeTrust.AppSurface.Testing.Tests/TestFixturePathPolicyTests.cs).
The corrected test and the closed-catalogue metadata prerequisite require a
fresh complete source capture and unchanged native gate. These changes do
not resolve the separate runtime allocation or Aspire resource startup
failures above or create admission authority.

The exact existing repository policy test passed locally from source **1/1**,
exit **0**, with no warnings, after the correction. Scoped formatting and
source-built [Bootstrap tests](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceHostBootstrapTests.cs)
passed **42/42**. The new
[closed catalogue prerequisite](../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md#closed-application-catalogue-prerequisite)
passed **50/50** metadata controls. These macOS results validate the source
repair and pure metadata checks; the native gate and shared execution remain
required. The production catalogue remains empty and rejects with `ASEVD407`.
