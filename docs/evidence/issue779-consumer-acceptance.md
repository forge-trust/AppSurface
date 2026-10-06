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
[closed catalogue prerequisite](../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md#internal-closed-application-catalogue-prerequisite)
passed **50/50** metadata controls. These macOS results validate the source
repair and pure metadata checks; the native gate and shared execution remain
required. The production catalogue remains empty and rejects with `ASEVD407`.

A subsequent catalogue review found that a later null bundle path could
throw before returning the fixed invalid-metadata diagnostic. Validation now
checks all paths before comparing prefixes. The final scoped catalogue suite
passed **52/52**, including candidate and observed-inventory regressions,
with no warnings. This repair preserves the empty production registration.

### Frozen-source native patch gate and syscall compatibility (2026-10-02)

[Native run 37054363272](https://github.com/forge-trust/AppSurface/actions/runs/37054363272)
tested frozen source `02ca14024d3f7d051c186925a69151786705702e`, with all
2727 SHA-256 bindings and remote Git blob identities verified. The dispatched
workflow, full manifest, before/after source checks and artifact `11248639375`
matched. Its ZIP SHA-256 is
`e91baa72b5e38ae8be772e3cd825b5f05f5795e42fdd4e6f8d37a0687a0d4eec`.
All 54 solution test projects exited zero: **15,454 passed / 0 failed / 2
existing skips**. Focused native tests passed **CLI 463/463** and **Aspire
143/143**. Eight build summaries recorded zero warnings and zero errors.

The unchanged [solution coverage gate](../../scripts/coverage-solution.sh)
reached patch evaluation and exited **1**, `ASCOV020`:

| Dimension | Measured | Effective threshold | Result |
| --- | ---: | ---: | --- |
| Aggregate line | 94.9807% | 94.5% | Met |
| Aggregate branch | 88.7247% | 84.5% | Met |
| Patch line | 88.7301% | 94.5% | Failed |
| Patch branch | 83.8465% | 84.5% | Failed |

Configured thresholds remain 95% line / 85% branch for aggregate and patch,
with the existing 0.5 percentage-point tolerance and Codecov patch semantics.
The diagnostic comparison base remains
`4aa8329c76f4279ad319747f9f3c14b9b8de51ef`. The primary `origin/main` had
advanced to `ec5d927e38ef89758699406926f0b09207f1605d` independently of the
frozen source. Latest-main integration and a fresh final gate remain required;
this historical-base result does not cover subsequent application-protocol
or policy edits.

The same frozen source's
[systemd Observation run 37054367100](https://github.com/forge-trust/AppSurface/actions/runs/37054367100)
failed before opening the output parent. Its retained private allocation frame
was `Allocation / OpenFilesystemRoot / Unsupported`, with `nativeErrno: null`.
That receipt establishes neither a successful allocation nor its exact errno.
The current diagnostic correction retains a direct known errno for supported
unsupported-syscall exceptions, as described in the
[private allocation diagnostic reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#private-allocation-diagnostics).

The independent
[native compatibility probe 37058052231](https://github.com/forge-trust/AppSurface/actions/runs/37058052231)
then passed both controls under identical worker properties except
`RestrictSUIDSGID`: `yes` returned errno **38**, and `no` successfully opened
the filesystem root with `openat2`. Both verified actual UID 999/GID 987,
`NoNewPrivs=1`, zero effective capabilities, exact kernel cgroup membership,
and empty owned groups after stop. Its artifact `11249356111` has verified
ZIP SHA-256
`532668381b3f02795040eb548e4851f3450e8250f9fe6eb7253a7596458ee307`.
The resulting worker-only policy correction and retained subject restriction
are documented in the
[execution broker reference](../../tests/evidencehost-consumer/ExecutionBroker-README.md#production-worker-syscall-compatibility).
A fresh production Observation run is still required.

### Aspire startup remains unproved (2026-10-02)

[Aspire mechanism run 37054371143](https://github.com/forge-trust/AppSurface/actions/runs/37054371143)
restored and built pinned Aspire/DCP 13.4.4 successfully on native Ubuntu
24.04/systemd 255, with zero warnings/errors, but passed **0/5 controls**.
All five reported startup failure followed by unavailable watchdog cleanup;
they quarantined output and left physical exit unconfirmed. The wrapper later
confirmed empty groups. No AppHost/DCP/resource identity, HTTP denial proof
or task/memory counter evidence was retained. The next controller correction
must preserve the first failure, capture bounded startup-query facts, and
discover the selected unit's actual cgroup during teardown. No task or memory
allowance increase follows from these missing observations.

The production catalogue and consumer-proof resolver remain closed. The
root v2 handler, separately restricted shared Aspire execution, protected
downstream gate and all 57 required acceptance groups remain prerequisites
for release acceptance.

### Shared producer and host integration checkpoint (2026-10-02)

The [fixed restricted coverage registration](../../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md#fixed-restricted-coverage-registration)
now contains the single shared procedure used by the CLI and the
[supervised Aspire bootstrap](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#explicit-supervised-bootstrap).
The public sealed registration exposes immutable declaration metadata. Execution
requires the protected writer's internal, single-attempt callback binding to the
actual supervisor, admission, protected plan, copied diff, shared output quota
and current stage token. The binding tracks the whole procedure before returning
its task and is invalidated when the callback ends. No friend assembly, public
transport factory or consumer-proof authority was added.

Focused macOS source validation passed **457 distinct cases across the recorded
runs**: catalogue 207, coverage procedure 45, coverage failures 10, protected CLI
22, factory 31, worker lifecycle 73, host bootstrap 42 and restricted host
registration 27. Locked Aspire restore and scoped formatting exited zero; the
successful test commands had zero failures, skips or compiler diagnostics.
The initial obsolete assertion and subsequent ten factory fixture setup failures
were retained in their failed receipts and corrected before the final factory
31/31 and Aspire 142/142 runs. These checks verify metadata, procedure and shared
lifecycle behavior on macOS. They do not measure new coverage or exercise a
native application start.

The host validates captured concrete producer references before application
start and exposes read-only registration maps. Readiness work consumes a private
binding to the current resource stage. Application-local cleanup requires the
[joined cleanup phase](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#shared-lifecycle-and-output-limits),
which is established after physical stop and pre-disposer work join; the final
global completion predicate also includes the currently running disposer.
Regression controls cover premature cleanup and the registered disposer ordering.
The compiled application catalogue and consumer-proof resolver remain empty.

### Frozen v11 native outcomes and next corrections (2026-10-02)

The complete frozen source `da4dddd1ae4b7437b4fc0990f22bc8bc5e572935`
contains **2730 files**. The archive, submitted workflow and source bindings
were checked against every source hash and executable mode; the independent
Aspire review additionally matched the untruncated remote source tree and all
2730 Git blobs. This establishes the tested source identity, without admitting
that source as protected tooling.

[Production Observation run 37063146983](https://github.com/forge-trust/AppSurface/actions/runs/37063146983)
failed at the first control-protocol handshake with `ASEVD402`. Artifact
`11251278044` retained the failure log. The production CLI, allocation and
Observation manifest were not reached. The fixture still used the old run/path
grammar and output identities; its subsequent correction passed seven portable
controls. A new native run must exercise the corrected fixture and the production
path.

[Aspire mechanism run 37063150354](https://github.com/forge-trust/AppSurface/actions/runs/37063150354)
restored and built pinned Aspire/DCP 13.4.4 with zero warnings/errors, then failed
**all five controls**. Artifacts `11252135026` and `11251695436` were downloaded
within fixed bounds and their GitHub digests verified. Each startup diagnostic
reported a loaded `Type=exec` unit that was inactive/dead with `MainPID=0`; each
launcher exited zero while the controller retained startup failure. Child stdout
and stderr were empty. No AppHost/DCP/resource identity or HTTP denial proof was
observed. Controller-owned exit and watchdog cleanup were unconfirmed; output
was quarantined, and the wrapper later confirmed empty groups. The retained
facts do not explain why the unit was inactive and provide no basis for raising
task or memory limits.

The next [Aspire child diagnostic](../../tests/evidencehost-consumer/AspireChild/README.md#native-proof-controls-parent-ci-only)
adds bounded private `Result`, main-exit and queued-job facts. Its **82/82**
portable controls passed, while the thirteen startup/identity/stop/pump/watchdog
guard functions remained unchanged. Separately, the
[subject completion correction](../../tests/evidencehost-consumer/ExecutionBroker-README.md#production-subject-command-completion)
captures terminal main-process facts before explicit unit stop, then joins the
launcher and both pumps and checks the generated cgroup is empty. Its **79/79**
portable controls passed. Both changes require fresh native validation. Neither
the portable controls nor the failed v11 runs satisfy protected/shared execution,
the downstream gate, the unchanged solution coverage gate or all 57 acceptance
groups.

### Restricted Aspire mechanism and production failure diagnostics (2026-10-03)

[Aspire mechanism run 37080727793](https://github.com/forge-trust/AppSurface/actions/runs/37080727793)
tested immutable source `a9476ee70eeda3eaf114639fb6266ef54c7f3b36` under harness
`e39a91356d0ab418d70b4c1e3b45293497f1ae09`. All **five controls passed**:
normal, readiness failure, factory stall, cancellation and a stuck descendant.
The parent verified both artifact digests, the submitted workflow and wrapper,
all 2736 source Git blobs and modes, and each private controller receipt.
Normal and cancellation observed healthy readiness and cooperative exit zero;
the factory stall and stuck descendant required forced termination. Every case
confirmed owned exit, cleanup, a joined watchdog with exit zero, two error-free
EOF pumps with exact byte counts, and an empty owned cgroup.

The [child fixture](../../tests/evidencehost-consumer/AspireChild/README.md)
uses pinned Aspire/DCP 13.4.4, a finite 128-task limit and the existing 1 GiB
memory limit. Its earlier 64-task run recorded kernel PID-limit events; this
passing run verifies the selected 128-task mechanism. AppHost, DCP and resource
still share one restricted application identity. This proves that fixture's
mechanism and controls, without proving the production root application module,
shared producer admission, protected consumer or downstream acceptance.

[Production Observation run 37080725733](https://github.com/forge-trust/AppSurface/actions/runs/37080725733)
tested source `3efba1d29ff929d63466d6f64cece24dd4213d07` and failed with
`worker-unsuccessful`: main exit code/status were both one. The bounded root
journal retained `ASEVD211`, meaning the production worker returned an incomplete
manifest. The broker had confirmed exit and completed wait with no active
handlers or runs. Artifact `11259205336` and its canonical private journal were
digest verified. The journal did not identify the producer failure; retaining
the bounded private failure artifacts is the next diagnosis. No passing
Observation or exact Trusted-rejection result is credited to this run.

The matching catalogue and application protocol now accept a declared task
limit of 128 and reject 129. Scoped macOS source verification passed **209
catalogue cases and 129 protocol cases**, with no failures, skips or compiler
diagnostics. Those metadata controls do not establish Linux execution. The
production compiled catalogue and accepted-consumer-proof registry remain empty;
the unchanged solution coverage gate and all required consumer proofs remain
required before Trusted support can be enabled.

## Frozen v13 actual results and pending corrections (2026-10-03)

[Aspire mechanism-v9 run 37083994959](https://github.com/forge-trust/AppSurface/actions/runs/37083994959)
tested source `6518b17e1bd587143314cfa2ea1a73e8113ab9cf` under harness
`ada7aac782f331734024e6330e4589ba6a9f90b5`. The parent verified both artifact
digests, the complete 2740-file source binding, and the five private control
receipts. **All five controls passed**, with owned exit and cleanup confirmed:
normal, readiness failure, factory stall, cancellation and stuck descendant.
Normal, readiness failure and cancellation stopped cooperatively with process
exit zero; factory stall and stuck descendant required forced termination.
All controls joined their watchdogs and error-free EOF pumps and confirmed the
protected output probe was absent.

This is evidence for the [restricted Aspire child mechanism](../../tests/evidencehost-consumer/AspireChild/README.md)
with pinned Aspire/DCP 13.4.4 and the selected finite 128-task limit. It does
not complete positive acceptance for the [shared CLI caller](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md),
[shared Aspire caller](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md),
[production root application module](../../tests/evidencehost-consumer/LinuxApplication-README.md)
or Trusted execution. The production compiled catalogue and accepted-proof
registry remain closed.

[Native-v11 run 37083997761](https://github.com/forge-trust/AppSurface/actions/runs/37083997761),
harness `34ff9f3b31c777c00724c39aa670d9236ff1e52f`, tested the same source.
The launcher, proof and broker portable suites passed **91, 46 and 4 controls**,
respectively. The root portable watchdog-oversize control then failed on a Linux
connection reset. Focused C# verification and the
[unchanged solution coverage gate](../../scripts/coverage-solution.sh) were not
reached; this run supplies no result for those gates.

[Runtime-v15 run 37083999238](https://github.com/forge-trust/AppSurface/actions/runs/37083999238),
harness `14047384137c380675a140253b57e0e40c3c58a4`, also tested that source and
failed closed with `worker-unsuccessful`. Its retained manifest reported
`ProducerOutcome.Failed` after approximately 10 ms with
`EvidenceAdmissionException`, `ClaimKind.None`, `Eligibility.None` and
`ExecutionVerdict.Incomplete`; cleanup was confirmed. A fresh-results directory
ownership defect was separately confirmed from source inspection. The native
inner cause was not measured, so this manifest does not establish that defect
as the cause of this failure. No passing shared coverage procedure or
[protected downstream gate](../../tests/evidencehost-consumer/ProtectedGateConsumer/README.md)
is credited to this run.

### Native-v12 and Runtime-v16 outcomes

Correction source `9894c62c525724b0d62c9f4859faa20b464ac737` contains the Linux
connection-reset test correction and the fresh-results ownership fix with tests
and documentation. [Native-v12 run 37087508180](https://github.com/forge-trust/AppSurface/actions/runs/37087508180)
passed all **310 controls in eight preceding Python suites**, including all 39
root-module controls, and recorded seven builds with zero warnings and errors.
The CLI selection passed **914 of 915** tests. Its sole failure expected the
root-peer diagnostic but received the generic descriptor diagnostic. The
solution coverage gate was not reached; source verification passed again after
the failed selection.

[Runtime-v16 run 37087509927](https://github.com/forge-trust/AppSurface/actions/runs/37087509927)
tested the same source. Its artifact digest and canonical private failure
archive matched. The retained producer result reported exit code **1** after
**95,410 ms**, with no report artifact. Cleanup remained confirmed and the
manifest remained None/None/Incomplete. This establishes a completed nonzero
restricted-process result; its subprocess output was not retained and its inner
cause remains unmeasured. It does not establish a successful coverage procedure.

The [handshake failure normalizer](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#authenticated-outer-worker-descriptor)
preserves host-selected `ASEVD402` errors before the general exception family.
Two scoped formatters and a macOS source build passed **124/124** selected cases
with zero compiler diagnostics. Linux peer branches were not executed by that
local run; the existing real-socket control still requires zero request bytes
after root-peer rejection. A private latest-main source includes this fix and
verified upstream revision `416b30919e6c4d03b634d9d3fe558884b02f0b91`.
Its unchanged coverage gate is pending. The [acceptance map](issue779-acceptance-status.md)
and protected consumer requirements remain open.

## Current validation checkpoint (2026-10-03 19:58 UTC)

[Native-v16 run 37143380879](https://github.com/forge-trust/AppSurface/actions/runs/37143380879)
tested frozen source `610489dbd1165ea7980faced55969cfe32c06b69` against base
`416b30919e6c4d03b634d9d3fe558884b02f0b91`. The unchanged wrapper exited
**1 / ASCOV120** after an existing console-sink test observed zero writes where
its timing-dependent assertion expected one. Raw merged coverage was
**94.930930% line / 88.841319% branch**. Numerical and patch gates were not
reached; see the [coverage audit](issue779-coverage-gate.md#current-frozen-source-coverage-checkpoint-2026-10-03).

The subsequent test-only console correction passed its **one selected test**.
Seven new pure-protocol rows passed across bounded selections: two IOException
controls in the initial run and five parser rows after correcting their message
assertions. Two new worker lifecycle facts passed their focused source build.
Final selected commands exited zero with no compiler diagnostic records. These
results establish the selected controls; a full-suite rerun and measured coverage
gain remain pending. Earlier failed attempts remain recorded. The five tested
files are mirrored in the primary worktree, whose recorded HEAD remains
`4dd992ec1bc2df8220c73149115c5b478edb0085`; [preliminary PR #850](https://github.com/forge-trust/AppSurface/pull/850)
has not received a product push.

[Private qualification V4 run 37145635575](https://github.com/forge-trust/AppSurface/actions/runs/37145635575)
tested source `7d94e66bf47ecf8c39e17046bc7e6d40eeb1b7c8` and failed with
**Claim None / Eligibility None**, no completed CLI/Host entry, and worker main
code **3**, status **6**. The broker reported ready and work closed, but wait
completion and exit were false. Its 4,096-byte journal was truncated without a
closed diagnostic frame. A private diagnostic-only correction increased the
raw journal limit to 16 KiB and passed **14 targeted portable controls**;
functional lifecycle and controller AST comparisons remained unchanged.

[Private qualification V5 run 37148541526](https://github.com/forge-trust/AppSurface/actions/runs/37148541526)
tested correction source `a9d9ec2a35932f671f697ccfd184b8df83cb9e76` and again
failed with no completed entry and **Claim None / Eligibility None**. Its
verified 11,946-byte journal remained below the byte cap and contained the fixed
fatal message for a faulted owned-exit acknowledgement task. The initial marker
scan missed that message. Its stack is consistent with a negative `owned_exit`
acknowledgement; response bytes were not retained. The root application-join or
ownership check that rejected exit remains unknown. The numeric exit signal
alone does not identify that cause.

[Native-v17 run 37147414478](https://github.com/forge-trust/AppSurface/actions/runs/37147414478)
completed with **16,737 .NET tests passed, zero failed and two existing skips**,
plus **345 Python controls passed**. Eight build summaries reported zero warnings
and errors. It reached the unchanged gate: aggregate **94.94% line / 88.85%
branch** passed, and patch branch **86.07%** passed. Patch line **87.44%** failed
the effective **94.5%** requirement, producing exit **1 / ASCOV020**. These are
the rounded job-log measurements; artifact numerator verification remains
pending. The run binds source
`085b302fedc37c30932122be27b3efc20ff9f5b0`; the later two-worker-fact capture
`ef1a6b9ec4141a96f0af8f2fd583cd05cff65ea5` is separate. The complete
[57-group acceptance set](issue779-acceptance-status.md) remains unfinished.
The production support catalogue and accepted-proof registry remain closed;
these observations enable no Trusted support.
