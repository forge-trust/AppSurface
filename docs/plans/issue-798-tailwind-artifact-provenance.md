<!-- /autoplan restore point: /Users/andrew/.gstack/projects/forge-trust-Runnable/HEAD-autoplan-restore-20260915-052948.md -->
# Implementation Plan: #798 Tailwind Artifact Provenance

Status: APPROVED
Approved: 2026-09-18 — user selected A (approve as-is)
Review date: 2026-09-15
Source: [approved design](../designs/issue-798-tailwind-artifact-provenance.md)
Reviewed source base: `e0618ac8dcc3b5903517e9a711f42959534b7fb5` (`origin/main`).
Planning checkout: `188bc3e4d2a3f23c27bff376a7525842b4e8820f` (detached HEAD). Implementation must branch from the reviewed main base and carry the design and plan forward.

Read the [test matrix](issue-798-tailwind-artifact-provenance-test-plan.md) for acceptance and the [aggregated task list](issue-798-tailwind-artifact-provenance-tasks.md) for implementation ownership. The final review report records approval state.

The approved design remains the requirement contract. This plan adds implementation decisions and review evidence. No product implementation is part of this review.

<!-- AUTONOMOUS DECISION LOG -->
## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|---|---|---|---|---|---|
| 1 | Intake | Review #798 against current main; preserve approved design and add a separate plan | Mechanical | P6 | The detached source predates #791 and current main; a plan must name its actual base | Reviewing stale checkout as current |
| 2 | Intake | CEO → DX → Eng; omit visual design | Mechanical | P3 | No rendered product UI; maintainer CLI and release workflow are developer-facing | Treating CLI flags as screen design |
| 3 | Intake | Defer optional gstack upgrade for this review | Mechanical | P3 | Automatic upgrades are disabled; preserve loaded methodology and avoid unrelated installation changes | Upgrade toolchain mid-review |
| 4 | CEO | Keep selected typed verifier and SELECTIVE EXPANSION posture | Mechanical | P4/P6 | Reuse the existing release system and preserve explicit user decisions | Reopen shell-only approach A |
| 5 | CEO | Add a concise producer-to-publish summary to existing reports | Mechanical | P2 | Release operator needs one identity and recovery action, within existing renderer | New dashboard/service |
| 6 | CEO | Classify recovery failures with specific next steps | Mechanical | P1/P5 | Missing history and missing evidence have different recovery actions | Generic verification-failed message |
| 7 | CEO | Keep credential-free substitution/retry fixture as acceptance | Mechanical | P4 | Already required by approved assignment; strengthen existing tests | New standalone demo product |
| 8 | CEO | Specify bounded parsing, hashing and diagnostic capture | Taste | P1/P3 | Existing buffered runner and ZIP readers do not provide these limits | Unbounded reads; broad runner rewrite |
| 9 | CEO | Defer portable signed attestations and long-lived external storage | Mechanical | P3 | Outside the approved trusted-CI boundary and requires new policy/infrastructure | Add signing service now |
| 10 | CEO | Preserve full original-candidate recovery; order its tests after the core mutation proof | Mechanical | P1/P6 | Independent reviewer suggested staging hardening; accepted retry requirements remain release-blocking | Defer approved recovery guarantees |
| 11 | CEO | Name graph authority, projection version, and exact-source tool invocation | Mechanical | P5 | Native review exposed handoff ambiguity without changing the byte guarantee | Host-defined closure or separately installed verifier |
| 12 | CEO | Use configured retention and report its actual earliest expiry | Mechanical | P3 | No evidence supports inventing a second minimum retention policy | Arbitrary new minimum-days policy |
| 13 | CEO | Bound retry guidance by platform rerun limits as well as artifact expiry | Mechanical | P1 | Official Actions docs limit reruns to 30 days and 50 attempts | Treat configured retention as a rerun guarantee |
| 14 | CEO | Queue both reviewers' staged-scope proposal; preserve original full release guarantee pending user decision | User Challenge | User sovereignty | Both voices would defer some approved recovery/projection work | Silently weaken the accepted launch gate |
| 15 | DX | Infer release maintainer and OSS contributor persona; use DX POLISH | Mechanical | P6 | Existing README and protected workflow identify the users | Consumer onboarding redesign |
| 16 | DX | Use focused real-archive tests as first useful local proof | Mechanical | P4/P5 | Reuses xUnit and recording process seams without a public demo command | Hosted playground or real-token quick start |
| 17 | DX | Keep explicit trusted identity flags, with wrapper/help/examples for humans | Taste | P5 | Transparent authority fits current workflow conventions | New context-file parser |
| 18 | DX | Preserve wrapper usage exits and restrict it to local mode | Mechanical | P1 | Compatibility includes current flags and exit 2 for usage errors | Silent release/local fallback |
| 19 | DX | Add stable diagnostic codes, exact rerun guidance and migration table | Mechanical | P1 | Operators need both cause and permitted recovery | Generic error text or relabeled v1 evidence |
| 20 | DX | Measure local fixture and real native release separately | Mechanical | P3 | A fast simulated proof is not a five-host acceptance run | Report invented first-run timings |

| 21 | DX | Fix report filenames, versioned diagnostics and output/exit behavior | Mechanical | P1/P5 | Both voices found missing operator contracts | Scrape prose or leave partial success files |
| 22 | DX | Put host prerequisites inside the consumer stage and retain typed .NET operations | Mechanical | P3/P4 | Distinguishes environment failures without a new health command | Preserve obsolete shell utility dependencies |
| 23 | DX | Define opt-in retained smoke reports and exact legacy adapter mapping | Mechanical | P1 | Makes the first result reproducible while retaining local-only semantics | Promise native readiness from a fixture |

| 24 | Eng | Define explicit producer context and immutable typed validation results | Mechanical | P1/P5 | Breaks no local behavior and avoids circular identity or hidden authority | Infer release identity from files |
| 25 | Eng | Add credential provider and validate start/evidence before first read | Mechanical | P1 | Ordering is verified directly through the existing publisher path | Require an API key merely to reject invalid evidence |
| 26 | Eng | Extract bounded workflow resolver with complete history fixtures | Mechanical | P1/P4 | Actions owns trusted transport and historical context | C# GitHub client or latest-name selection |
| 27 | Eng | Reuse .NET strict JSON and focused raw-path validation | Mechanical | P3/P4 | Runtime supports duplicate rejection; raw normalization currently loses evidence | Handwritten generic parser or broad validator rewrite |
| 28 | Eng | Bound capture through opt-in existing runner request policy | Mechanical | P1/P4 | Streams can exceed memory limits and must drain without blocking | Duplicate process runner |
| 29 | Eng | Require full branch matrix and manual-only five-host rehearsal | Mechanical | P1/P4 | Mocked receipts cannot prove NuGet extraction on actual hosts | Weaken native acceptance or run trusted hosts for every PR |
| 30 | Eng | Use two ownership lanes after contracts freeze | Mechanical | P3 | Typed proof and workflow transport are separable; shared API edits are not | Many parallel agents editing the same module |

| 31 | Eng | Re-emit complete bindings on reuse and enumerate host artifacts by exact IDs | Mechanical | P1/P5 | Native review exposed skipped upload outputs and matrix transport ambiguity | Embed future upload ID in receipt |
| 32 | Eng | Pin source to triggering event commit before tag outputs | Mechanical | P1 | A mutable fetched tag can differ from the triggering source | Trust the current tag name alone |
| 33 | Eng | Add hosted matrix configuration validation and best-effort failed receipts | Mechanical | P1 | Runner scheduling can fail before the typed command runs | Treat absent evidence as a completed failure report |
| 34 | Eng | Make projection asset groups finite and harden regular-file publication staging | Mechanical | P1/P5 | Every interpreted asset needs coverage; read-only staging detects accidental change | Claim hostile same-user race resistance |

| 35 | Eng | Freeze private wire shapes and explicit host transport map | Mechanical | P5 | Prevent circular self-hashes and unspecified aggregation fields | Schema interpretation inferred during implementation |

| 36 | Eng | Specify caller/callee permissions and main-only manual rehearsal guard | Mechanical | P1 | CLI found effective grants and exact trusted-ref condition unspecified | Rely on sibling permissions or prose-only trust |
| 37 | Eng | Retain actual global-cache archive check, verified against NuGet source | Mechanical | P1/P6 | Upstream v3 save mode retains the archive; copying producer bytes is insufficient | Replace restored evidence due to incorrect absence premise |
| 38 | Eng | Check release checkout and verifier build stamp; unify failure finalization | Mechanical | P1/P4 | Source flags and native-only cancellation handling leave other entrypoints ambiguous | Trust arbitrary stale DLL or cancelled-token report writes |
| 39 | Eng | Specify conservative finite projection set without NuGet RID re-resolution | Mechanical | P1/P3 | All protected roots plus group-path union have deterministic membership | Implement a second asset-selection engine |

| 40 | Eng | Use distinct fresh aggregate, preflight and publisher report directories | Mechanical | P1 | Reusing the same path conflicts with create-new output safety | Examples that collide across stages |
| 41 | Eng | Serialize bound summaries before their containing receipt and publish self-digests separately | Mechanical | P5 | A summary containing its own parent digest creates a hash cycle | Circular receipt/report hash inventory |

| 42 | Final gate | Approve as-is; retain full scope and both recommended choices | User decision | User sovereignty | User selected A on 2026-09-18 | Reduce recovery scope or override the recommended interfaces/limits |

## Review basis and requirement trace

The September 15 live GitHub main ref is `e0618ac8dcc3b5903517e9a711f42959534b7fb5`.
Between the approved design base `0a9a4213` and that commit, the scoped release,
Tailwind, and PackageIndex files are unchanged. The additional main commit is
Durable work (#810); it does not supply an alternative #798 implementation.
The large working-tree-versus-main diff is ancestry drift, not this task's edits.
The only intentional review outputs are this plan and related documentation.

The [approved design](../designs/issue-798-tailwind-artifact-provenance.md) is
incorporated as the normative contract, including every schema field and flag
group. Its four requirements map to implementation work as follows:

| Requirement | Owner | Acceptance evidence |
|---|---|---|
| Same producer and push bytes | Producer subject, shared validator, publisher | Same-version substitution causes zero build/push calls |
| Actual restored bytes on five hosts | Typed consumer proof, strict payload projection | Archive and extracted-file mutations rejected on all actual host families |
| Complete, current evidence and frozen retry | Workflow resolver, aggregator, publication-start receipt | Missing/mixed receipts fail; interrupted publication recovers original IDs |
| Preserve #790 and existing CI boundary | Script adapter, native host matrix, existing local proof | Existing CSS/cache/no-native-output assertions and five hosts remain |

## CEO premise challenge and alternatives

**Right problem.** The native workflow still runs `dotnet pack` twice per host
(`tailwind-native-host-evidence.yml:59-60`), while release packing uploads another
bundle (`nuget-prerelease-publish.yml:227-231`). Doing nothing permits green native
tests of different bytes. The outcome is an operator being able to identify the
tested candidate before irreversible publishing, not merely obtaining more hashes.

**Premises.** Byte identity is appropriate for the input to NuGet publishing;
NuGet repository signing makes remote raw equality a different claim. Five actual
hosts are required by the accepted #790 boundary, not an inferred wish for broader
platform support. An authorized runner remains trusted: document hashes do not
authenticate a fabricated report from a compromised runner. Original-candidate
recovery is reasonable because one coordinated release can partially publish;
it must not silently become permission to regenerate an expired candidate.

**Alternatives.** These comparisons do not reopen the user's selected B.

| Approach | Effort / risk | Pros | Cons | Reuse / disposition |
|---|---|---|---|---|
| A: extend shell guards | Human 4-6 days / agent 1-2 days; high drift risk | Small initial diff; familiar CI commands | Duplicates schema and path rules; direct publisher remains separate | Existing jq/script; previously rejected |
| B: shared typed verification | Human 6-10 days / agent 2-4 days; medium integration risk | One meaning of evidence; injected no-push tests; explicit stages | Several contracts and workflow handoffs must align | Existing PackageIndex; selected |
| C: signed portable attestation platform | Human several weeks / agent several days; high scope risk | Independent verification outside Actions; long-term portability | Does not itself prove restored payload identity; new trust/retention policy | Platform attestations; defer |

[GitHub's attestation verification](https://cli.github.com/manual/gh_attestation_verify)
verifies signed provenance claims. It complements the native consumer proof and
cannot replace checking which expanded files the build consumed. No commercial
competitive pressure changes this internal release-tool decision.

```text
CURRENT                         THIS PLAN                      12-MONTH IDEAL
5 independent packs +           1 frozen producer ->          Every release can be
separate publish pack   --->    5 byte-bound native proofs ->  explained and replayed
same version, different bytes   exact pre-push candidate       from retained evidence
```

The dream-state delta is retained evidence inside the current trust boundary.
Portable external verification and retention beyond Actions remain separate work.
Complexity exceeds eight files, but the minimum complete change spans producer,
consumer, aggregation, publisher, both release workflows, tests, and docs. Reduce
moving parts by sharing pure validators and keeping GitHub API calls in a small
workflow resolver; do not invent a generic proof framework or C# GitHub client.

Five adjacent opportunities were evaluated: an identity summary (accept), actionable
recovery classifications (accept), a credential-free demonstration (already in
scope), bounded diagnostics (accept, Choice 1), and portable signed receipts
(defer). Platform potential exists in the pure archive/receipt helpers; extraction
into a reusable library waits for a second real caller.

| Implementation moment | Decision fixed before coding |
|---|---|
| Hour 1 foundations | Implement from current main; schemas remain private, manifest v1 remains stable |
| Hours 2-3 core | Return local proof closure before cleanup; reject unsafe paths before normalization |
| Hours 4-5 integration | Same producer output feeds action and verifier; resolve original start receipt before selecting retry evidence |
| Hour 6+ validation | Mutation fixtures, current-attempt completeness, cancellation and output limits are acceptance paths |

These are sequencing checkpoints, not a six-hour delivery promise. The total
effort estimate includes native runner feedback and coordinated workflow checks.

## What already exists

Source references below are at the reviewed main commit, available with
`git show e0618ac8:<path>`; working links may show the older planning checkout.

| Subproblem | Existing implementation | Reuse and required change |
|---|---|---|
| Package identity | [PackagePublishing.cs](../../tools/ForgeTrust.AppSurface.PackageIndex/PackagePublishing.cs), `PackageHash`, manifest reader and plan validator | Retain lowercase raw SHA-512 and plan matching; guard duplicate/null input and bound reads |
| Internal Tailwind contract | [PackageArtifactValidation.cs](../../tools/ForgeTrust.AppSurface.PackageIndex/PackageArtifactValidation.cs), `ValidateTailwindMainPackageContract` | Extract focused shared validator, retain source-byte and five-RID rules |
| Pack-time proof | [PackageArtifactWorkflow.cs](../../tools/ForgeTrust.AppSurface.PackageIndex/PackageArtifactWorkflow.cs):174-198 | Capture immutable closure report before successful workspace deletion; emit subject after all local proofs |
| Process execution and reports | [DocsPackageConsumerProof.cs](../../tools/ForgeTrust.AppSurface.PackageIndex/DocsPackageConsumerProof.cs), `IExternalCommandRunner` | Reuse request/result seam; add bounded capture for new proof commands |
| Workspace safety | [PackageProofWorkDirectory.cs](../../tools/ForgeTrust.AppSurface.PackageIndex/PackageProofWorkDirectory.cs) | Reuse lexical disjoint checks; add release-specific create-new and ancestor link checks |
| Archive path checks | `NormalizePackagePathStrict`, PackageArtifactValidation:1500 | It currently replaces backslashes and trims slashes; reject raw invalid names before normalization in the new shared verifier |
| Publish retry and ledger | `PackagePublishWorkflow.RunAsync`:294, `RunPushAsync`:389 | Move credential read after evidence checks; copy/recheck push files; retain original identity and duplicate-reported semantics |
| Scheduling | Existing [stable](../../.github/workflows/nuget-stable-publish.yml) and [prerelease](../../.github/workflows/nuget-prerelease-publish.yml) flows | Preserve protected environments, per-tag concurrency, readiness checks, stable release evidence and smoke install |

## CEO review sections

### 1. Architecture

One typed verifier owns interpretation; Actions owns artifact transport and scheduling.
The current parallel native/pack jobs become a dependency chain, adding producer
latency to the native critical path. Keep other independent release validation
parallel, and do not let optional report rendering authorize publication.

```text
trusted tag/source/plan
    |
    v
producer [pack + existing local proofs + subject] -> immutable upload ID
    |                         |
    |                         +--> workflow recovery resolver (Actions API)
    v
native x5 [fresh restore -> archive/payload checks -> build -> recheck]
    |
    v
aggregator [exact producer + exact invocation + five complete receipts]
    |
    v
publish preflight -> isolated push files -> uploaded start receipt
    |
    v
token -> shared publisher validation -> per-file hash -> nuget push -> ledger
```

The producer is a deliberate single candidate authority. Actions artifact availability
and five native runners are operational dependencies, so outages delay publishing.
At 10x releases the largest cost is repeated full downloads and builds; at 100x
runner capacity and storage dominate, not validator dispatch. No new public endpoint,
database, or shared mutable service is introduced.

### 2. Error and rescue map

The registry below names failure handling at each boundary. A failed verification
is terminal for that attempt; bounded retry belongs to workflow transport where
the same immutable ID is retained. Cancellation cannot be converted into success,
and a diagnostic-write failure cannot replace the original stage failure.

| Method/codepath | Failure / exception class | Rescued? / action | Operator sees |
|---|---|---|---|
| Parse subject/receipt | `JsonException`, `PackageIndexException`; null/duplicate/missing/oversize | Yes, terminal contract failure | Document, field, expected schema, correction link |
| Read/hash archive | `IOException`, `UnauthorizedAccessException`, `InvalidDataException` | Yes, terminal integrity/I/O result | Package/file, expected vs actual hash or read failure |
| Project assets and payload | `PackageIndexException`, invalid graph/path/link | Yes, terminal before build | Graph member or relative path and source contract |
| Create workspace | `IOException`, access denial, pre-existing directory, unsafe overlap | Yes, terminal before any deletion | Safe fresh directory requirement and untouched inputs |
| Restore/build | Nonzero result, `Win32Exception`, timeout | Yes, bounded report; no success receipt | Stage, exit, bounded log, retry guidance |
| Cancellation | `OperationCanceledException` | Propagate cancellation; best-effort bounded diagnostics | Cancelled, never proved |
| Resolve/download artifact | HTTP 403/404/410/429/5xx, incomplete page/history | Retry only bounded transient transport; otherwise stop | Missing authority vs transient service failure |
| Upload start/aggregate | Failure or ambiguous completion | Stop; next attempt performs exact lookup | Zero push authorization; recovery lookup required |
| Publish/copy/recheck | Changed hash, I/O or nonzero push | Stop further pushes; preserve frozen start receipt | Failed/unknown/pushed/duplicate-reported per package |
| Render/upload diagnostics | I/O failure, output limit, upload failure | Keep primary failure; mark secondary diagnostic problem | Evidence incomplete and original cause |

### 3. Security and trust boundary

All new IDs are data strings, and workflow expressions enter quoted environment
arguments. Validate filenames, raw ZIP names and every filesystem ancestor before
opening payload files. The existing normalizer is insufficient for the stronger
contract; reusing it unchanged would accept rooted/backslash input (medium likelihood,
high impact if it escapes the intended evidence directory).

No new credentials are needed. Keep API-key acquisition after preflight/start upload,
and prevent logs from containing the key, CLI home, full environment, or arbitrary
restore configuration. Compromised authorized runners and post-upload NuGet signing
are explicitly outside the guarantee; do not market hashes as authenticated attestations.

### 4. Data flows and edge cases

```text
producer bytes -> parse/validate -> frozen subject -> upload -> trusted ID
  nil/empty ----> fail contract       I/O error ---> no candidate output

trusted ID -> download -> fresh restore -> archive/payload -> build -> receipt
  missing ------> fail    cache exists ---> fail    mismatch ---> no success

five directories -> receipt validation -> aggregate -> start receipt -> push
  zero/partial ------> failed summary     conflict ------> exact-ID recovery
  duplicate/foreign -> reject            cancelled -----> frozen unknown result
```

Repeated invocation must select the original producer, not overwrite it. Empty
evidence is a failure with diagnostics; a single native retry cannot import four
older receipts. Partial publication stops on the first failed push, and a crash
before ledger persistence retains the start receipt as the candidate authority.

### 5. Code quality

Add small internal contract records, a pure validator, a consumer workflow, and
renderers consistent with PackageIndex. Do not place another large branching
workflow into `PackagePublishing.cs`; call a focused service through an explicit
request. Reuse process-runner injection, but do not mistake its current catch-all
and `ExecuteBufferedAsync` for bounded or specific failure handling.

### 6. Test review

```text
new contracts -> unit fixtures (invalid types, duplicates, hashes, graph)
new path projection -> filesystem/ZIP integration (links, case, traversal)
new consumer stages -> recording runner + real restore/build on five hosts
new workflow edges -> parsed YAML/expression policy + synthetic artifact run
new retry state -> fake Actions responses and interrupted publisher fixtures
new CLI/report -> entrypoint tests, stable diagnostics, no credential reads
```

The Friday-night confidence test is a complete candidate followed by a same-version
substitution that reaches zero push calls. Hostile QA mutates expanded targets while
leaving the archive valid; chaos testing cancels between push and ledger write, then
retries the original candidate. Many pure tests and a smaller actual-host acceptance
suite avoid an inverted pyramid; no LLM features or prompt evals are involved.

### 7. Performance

The top three waits are repository pack/proofs, five cold restores/builds, and
artifact transfer. Their p99 durations are unknown until measured; retain existing
30-minute producer/native job limits initially and record stage elapsed times.
Stream archive and payload hashing with fixed buffers, never cache trust across
invocations, and set explicit parser/archive/log limits before constructing objects.

Choice 1 recommends initial private constants: JSON document 16 MiB, archive
1 GiB, 100,000 entries, protected expanded bytes 4 GiB per archive, and diagnostic
capture 4 MiB per stream with a truncation marker. Count actual streamed bytes as
well as declared ZIP sizes, reject limit violations, and use bounded concurrency
(one package at a time per host). Validate these ceilings against real fixtures;
changes require a reviewed constant/test update, not a caller bypass. A valid
alternative is lower package-specific limits after first measuring representative
release artifacts; the trust guarantee is unchanged.

### 8. Observability

The report starts with status, producer ID, source/version, five host outcomes,
and the next permitted action. Include stage duration, expected/actual digests,
receipt and aggregate IDs, and earliest retained-artifact expiry. Existing Actions
job failures and summaries supply notification; no separate dashboard, alert
service, or background monitor is warranted.

### 9. Deployment and rollback

Build contract helpers and negative fixtures first, wire the complete producer-to-
publisher chain, then run a nonpublishing candidate through all five hosts before
enabling a real release. Both release workflows must switch together; v1 evidence
cannot satisfy the new gate. Keep validation-environment, coordinated package
readiness, stable source/release checks and smoke installs intact.

```text
helpers/tests -> full workflow wiring -> no-publish native candidate -> release
failure before push -> fix/retry original artifacts or abandon candidate
failure after any push -> freeze original inputs -> repair forward/retry original
code regression -> revert implementation for future candidates; never rewrite receipt
```

A workflow revert does not roll back NuGet acceptance. Within the first release,
inspect the identity summary and both broken-evidence/no-push fixtures; within the
first hour inspect actual runner timings and artifact inventories. No schema
migration or new feature flag is needed; no bypass flag may authorize weak evidence.

### 10. Long-term trajectory

Private versioned records and ordinary files fit .NET and GitHub Actions. The new
coupling is deliberate: publishing now depends on the same candidate's five-host
proof, so artifact expiry becomes a documented recovery boundary. Reversibility
is 4/5 for code, 1/5 for a package already accepted by NuGet; retain this distinction
in the runbook rather than promising a rollback the system cannot perform.

### 11. Visual design applicability

No screens, layouts or interactive rendering are introduced. Markdown summaries
and terminal output are evaluated in DX, so the visual design phase is omitted.

## Failure modes registry

| Codepath | Failure | Handling | Planned test | User visibility / logging |
|---|---|---|---|---|
| Producer | Local proof fails or closure lost before cleanup | No subject/upload | Capture-before-cleanup and failed proof | Failed stage report |
| Bundle | Same version, wrong bytes | Reject before restore | Substituted archive | Expected/actual hash |
| Restore | Cache/source/fallback substitutes dependency | Reject graph/archive | Prepopulation and wrong Core | Dependency identity report |
| Payload | Modified extracted target or link/case collision | Reject before/after build | Real archive/path fixtures | Relative path reason |
| Native | Wrong observed host, failure, cancellation | No eligible receipt | Host identity and process fixtures | Per-host failed record |
| Aggregate | Missing/duplicate/mixed invocation | Failed summary, no publish output | Five-host set permutations | Missing/extra RID list |
| Recovery | Deleted artifact or ambiguous upload/history | Stop, never repack uncertain candidate | Paginated API state fixtures | Exact recovery classification |
| Start receipt | Upload failed/conflict | No credential acquisition/push | Process ordering and retry | Original receipt ID or blocked |
| Publish | Mutation after preflight or failure partway | Per-file rehash, stop; frozen candidate | Injected mutation/crash | Ledger + start receipt |
| Diagnostics | Logs exceed bound or storage fails | Bounded capture, preserve primary failure | Limit and I/O failure | Truncation/secondary error |

No critical gap remains in these planned outcomes. None is claimed as an executed
test result; engineering review will refine the exact coverage matrix.

## NOT in scope

- Portable signing/attestation and storage outside Actions: separate trust and retention policy.
- Consumer Tailwind behavior, additional RIDs, native payload packages: #790 remains settled.
- General CI framework, all-package native testing, or public verifier SDK: no current need.
- Remote NuGet archive equality or repacking expired candidates: different guarantees.
- Unrelated Durable #810 changes and existing deferred TODOs: no #798 dependency.

## CEO completion summary

| Item | Result |
|---|---|
| Mode / system audit | SELECTIVE EXPANSION; current main preserves the known native/publish gap |
| Step 0 | Existing B retained; five bounded opportunities evaluated |
| Architecture / errors | One ordering clarification; ten error boundaries mapped, zero unhandled planned outcomes |
| Security / data | Raw path normalizer gap addressed; four shadow flows and retry boundaries mapped |
| Quality / tests | Focused services and bounded execution; codepath/test diagram written |
| Performance / observability | Unknown timings declared; one limit choice; existing reports extended |
| Deployment / future | Pre-push rehearsal, both release modes, original-candidate recovery; reversibility 4/5 before push |
| Visual design | Omitted: no UI scope |
| Required outputs | Existing-code map, ten-row failure registry, scope exclusions and dream delta written |
| Scope decisions | Five proposals: three accepted, one duplicate retained, one deferred |
| Independent voices | Native combo/sub + Codex CLI combo/sub completed; one user challenge queued |

## CEO contract clarifications

The local assets graph is the source for which packages the fixed consumer actually
resolved. The validated package plan and manifest define authorized first-party
identities and bytes. For each assets `libraries` node of type `package`, match
ID/version to the target graph; classify known inventory IDs as first party and
reject any other `ForgeTrust.*` ID. Walk dependencies from the consumer's Tailwind
root, reject first-party project nodes and missing targets, and require every
first-party node to agree with manifest/nuspec versions. Freeze the resulting sorted
set before workspace cleanup. The graph does not choose its own trusted hashes.

Add `payloadProjectionVersion: 1` to the new subject and receipt contract; v1
denotes exactly the protected directories and graph-selected managed assets in
the approved design. Receipts record the selected relative paths and hashes, so a
future layout change produces an inspectable difference. Unknown projection versions
fail; a later rule change requires an explicit schema/projection update.

Build PackageIndex from the exact validated source checkout before proof execution;
invoke its built DLL with `dotnet` and `--repo-root` pointing to that checkout.
Do not use a globally installed verifier or recursively invoke `dotnet run` from
the pack-time consumer adapter. Restore/build the maintainer tool before proof
cache isolation, using the repository lockfile policy; building this tool does
not repack the product.

The configured Actions retention is the supported maximum, reduced to the earliest
expiry of the original required artifacts and GitHub's workflow-rerun window. Report that deadline; do not promise a
minimum number of days unsupported by repository policy. Record failure rate and
stage durations on the first candidate; interpret timing targets as measured
acceptance goals rather than an excuse to omit a host or validation stage.

## Candidate and proof state machines

```text
NoCandidate --first attempt/proven never uploaded--> LocalCandidate
LocalCandidate --all local proofs + upload succeeds--> FrozenCandidate
FrozenCandidate --five current native successes--> CompleteEvidence
CompleteEvidence --preflight + start upload succeeds--> PublicationStarted
PublicationStarted --all pushes observed success/duplicate--> PublicationObserved
PublicationStarted --failure/cancellation--> PublicationUnknownOrPartial
PublicationUnknownOrPartial --same frozen IDs--> PublicationStarted

Forbidden: FrozenCandidate -> repacked replacement
Forbidden: partial/mixed native receipts -> CompleteEvidence
Forbidden: local start file or failed upload -> PublicationStarted
Forbidden: missing original artifact -> replacement under same start receipt
```

Each transition is guarded by validated bytes plus protected workflow context.
An expired/deleted authority enters `RecoveryBlocked`, whose only output is an
operator diagnostic; it has no edge to a new candidate for that release run.
A local-only consumer proof can never transition into a release-native receipt.

### Recovery platform limits

[GitHub reruns](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)
are available for 30 days after the original run and have a maximum of 50 reruns.
Therefore the operator's retry deadline is the earlier of the required artifact
expiries and the run's 30-day deadline, with remaining rerun count also displayed.
Longer artifact retention preserves audit evidence but does not extend rerun
eligibility. Exhaustion blocks same-run recovery; do not silently start a new run
and publish a regenerated package under the old candidate identity.

### CEO implementation tasks

- [ ] **CEO-T1 (P1, human: 6h / agent: 45min):** capture the first-party graph in the typed local proof result before cleanup; assert identity and dependency agreement against the producer inventory. Files: PackageArtifactWorkflow and TailwindConsumerProof. Verify with capture-before-cleanup and wrong-Core fixtures.
- [ ] **CEO-T2 (P1, human: 8h / agent: 1h):** implement strict raw-path and bounded I/O contracts in TailwindProofContracts/Validation. Verify every limit boundary, traversal, symlink and output-overflow case.
- [ ] **CEO-T3 (P2, human: 3h / agent: 30min):** add identity/recovery summaries and the actual deadline to maintainer and release documentation. Verify sample reports and each recovery action against workflow fixtures.

The touched maintainer/release guides contain no existing architecture ASCII
diagrams to repair. The existing CI timing tables and Tailwind host matrix must
be updated with the new dependency edge; source diagrams in unrelated Durable
files remain outside scope.

### Recovery lookup implementation boundary

The workflow resolver identifies the producer-upload and protected-publication
steps using stable checked-in job/step names and the documented per-attempt job
response. Missing expected jobs/steps, incomplete pagination, renamed/unrecognized
states, cancelled upload, or API denial produce `history-unknown`. Only complete
prior attempts with explicit pre-upload failure and no publication start may
authorize a first upload on a later attempt. The initial implementation needs no
new C# GitHub client; resolver logic is shared by the two existing release flows
and exercised against captured synthetic API responses.

## CEO independent voices and consensus

The native review scored the early plan 8/10 and raised release cost plus four
implementation ambiguities. The CLI review completed successfully (exit 0),
raising three strategic concerns and three implementation clarifications. Both
ran with `combo/sub` per the user's routing policy; model-family diversity is
not established. Their fresh contexts provide independent review, not evidence
that distinct model families agreed. The initial CLI sandbox startup failed;
the authorized runtime-access retry succeeded with a read-only reviewer.

| Dimension | Native reviewer | Codex CLI | Consensus |
|---|---|---|---|
| Premises valid | Yes; current independent packs leave a real gap | Yes | Confirmed |
| Right problem | Exact tested bytes must bind to publish | Same | Confirmed |
| Scope calibration | Stage core gate before unusual recovery | Defer receipts/history/projection hardening | Confirmed concern; User Challenge 1 |
| Alternatives | B sound; archive-only insufficient | B sound; built-in attestation complementary | Confirmed |
| Platform risk | Keep contract replaceable and private | Avoid claiming authenticated provenance | Confirmed |
| Six-month trajectory | Measure cost/flake burden | Measure cost and require real second caller | Confirmed |

Six of six dimensions have concordant conclusions, including concerns; this is
not six positive scores or an implementation approval. Graph authority, projection
version, exact-source invocation and actual retry limits were clarified. The
CLI's reference to old consumer packaging wording came from the older checkout;
that statement is excluded from review conclusions.

### User Challenge 1: stage the launch guarantee

**Resolution, 2026-09-18:** Approval A retains the original full guarantee.
The proposed scope reduction is not adopted.

**What the user said:** Approve one pack, all five restored-byte proofs, and safe
original-candidate recovery before publishing.

**What both reviewers recommend:** Ship a narrower byte-identity gate first and
defer some historical recovery/publication-start machinery. The CLI also proposes
deferring expanded-payload checks; the native reviewer explicitly considers those
checks necessary for the requested guarantee.

**Why:** They expect a lower initial maintenance and release-latency burden.

**What we might be missing:** Actual frequency/cost of failed releases, existing
partial-publish incidents, and the user's willingness to trade release assurance
for delivery speed. No measured data currently supports a scope cut.

**If the recommendation is wrong:** The launch can again publish after a retry
without proving the original candidate, and archive-only verification can miss
changed expanded build files. This would lose part of the already approved guarantee.

**Default retained for engineering review:** Implement and test in increments,
but require the full approved guarantee before real publishing. Nothing is
deferred by this challenge until the user explicitly changes the design.

### Strategy acceptance and full voice artifacts

The first real release must retain the complete candidate identity summary, pass
the zero-push substitution exercise, and show original-ID partial retry recovery.
For the first three candidate runs, record added critical-path time and native
proof failures; a later six-month review can decide whether a demonstrated second
caller justifies extraction. Reuse and signed external verification are not launch
success conditions.

Full independent outputs are saved in the private project artifacts as
`issue798-ceo-native-review.md` and `issue798-ceo-codex-review.md`. The native
reviewer's follow-up confirmed the graph, projection, invocation and retention
ambiguities resolved in the amended plan. All CEO outputs are complete.

## DX investigation

Mode: **DX POLISH**. Product type: private maintainer CLI and release workflow.
The primary developer is a release maintainer diagnosing a blocked coordinated
NuGet release; the secondary developer is an OSS contributor reproducing the
verification rules locally. An application developer consuming Tailwind has no
new installation or migration step.

Persona card: the maintainer arrives with .NET 10, the checkout and a failed
Actions run. They expect noninteractive commands, a concrete reason, preserved
evidence, and a safe next action in one terminal session. Their tolerance target
is under two minutes to find the correct guide and under five minutes to run a
small local proof after the SDK is installed; a full five-host release proof has
a separate duration budget. These are targets to measure, not observed timings.

### Developer perspective

I open the PackageIndex maintainer guide. Its first paragraph points me to the
package chooser, and the first workflow teaches me to change release-guidance
variants. That is useful for package policy, but I arrived because a release
failed and I want to know whether the tested package is the one being published.
I find `verify-packages` farther down and see that it packs packages and runs
proofs. I still cannot tell which artifact ID a native runner used, because that
relationship is the missing feature this change is meant to add.

I open the release operations guide and find stable and prerelease publishing,
the manifest, the ledger, and retry instructions. I need those instructions to
tell me whether a failed host means rerun all five, whether publication already
started, and whether the original evidence can still be recovered. I do not
want a command that quietly changes the candidate while reusing its version.

As a contributor, I want one small test command that demonstrates both a valid
candidate and a substituted one without giving the tool a NuGet key. Once that
works, I can read the full flags and schemas. A clear result should tell me what
was proved, where the report is, and which recovery action is still permitted.

This is a source-grounded roleplay, not a user interview or a measured session.

### Comparable developer workflows

| Reference | Useful first-result shape | Published timing | Lesson for this plan |
|---|---|---|---|
| [GitHub CLI attestation verify](https://cli.github.com/manual/gh_attestation_verify) | One verification command with repository identity; JSON output | Not stated | Separate expected identity from the submitted artifact |
| [dotnet nuget verify](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify) | One package-path command; signatures and diagnostic content hash | Not stated | Familiar verify naming, explicit scope of proof |
| [Cosign blob verification](https://docs.sigstore.dev/cosign/verifying/verify/) | Blob plus bundle and expected identity/issuer | Not stated | Bundle convenience must retain explicit expected identity |
| #798 local fixture | One filtered test command after checkout/SDK | Target under five minutes, unmeasured | Demonstrate valid/substituted candidates without credentials |

These are comparable CLI interactions, not identical security guarantees or
measured competitors. A verification command cannot replace NuGet restore and
five real host builds. The first useful moment is seeing a valid fixture reach
the injected push boundary and the substituted fixture produce zero push calls.
Use the existing test runner as the delivery vehicle, with no new public demo tool.

### Nine-stage developer journey

| Stage | Developer action | Existing friction / resolution in plan |
|---|---|---|
| Discover | Read maintainer README and release-ops | Add an early linked “Tailwind release evidence” entry |
| Evaluate | Read concise proof scope and sample summary | State pre-push bytes and trusted-runner boundary first |
| Install | Checkout the intended commit, install .NET 10 | Document SDK and Bash needs; no global verifier installation |
| Hello world | Run the focused credential-free fixture | Add one copyable test command and expected pass/failure assertions |
| Integrate | Inspect the real workflow invocation | Full flag examples projected from trusted job outputs |
| Debug | Open report, bound logs and failed stage | Problem/cause/fix/docs plus expected and observed identities |
| Upgrade | Switch workflows and verifier together | v1 historical only; preserve local script flags/exit conventions |
| Scale | Run larger package set or multiple releases | Bounded I/O and fixed five-host scheduling; measure added cost |
| Recover/migrate | Resume original candidate or abandon | Explicit original IDs, expiry/rerun limits and fix-forward guidance |

### First-time contributor confusion report

| Time | Source-grounded observation | Planned resolution |
|---|---|---|
| T+0:00 | README starts with package-story policy, not evidence | Early evidence link with release-maintainer audience |
| T+0:30 | `verify-packages` means a full pack, not a quick rule demonstration | Focused fixture first; full proof named separately |
| T+1:00 | CLI help has global options and no new command mode examples | Command-specific help and complete mode examples |
| T+2:00 | Missing native evidence can be confused with a retryable upload | Stable reason code and permitted next action |
| T+3:00 | Historical v1 report can look like current proof | Visible schema/release-eligibility label and migration table |

All five confusion points are addressed in the plan. The timestamps represent
a roleplay sequence; none is a stopwatch measurement.

## DX pass 1: getting started

Initial specification score: 6/10. The approved design lists flags and negative
tests but does not give a short local entrypoint. The existing README's
`verify-packages` example incurs full packing and network/host work; adding a
new hosted playground would not serve this maintainer task.

The first-run guide has three steps: obtain the exact source checkout with .NET
10 installed; run the focused fixture below; inspect its test result and emitted
sample evidence. The fixture is a new test class, and this command is a required
implementation deliverable, not an executable feature already present:

```bash
dotnet test tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj --filter FullyQualifiedName~TailwindArtifactProvenanceTests
```

The test creates real temporary archives and uses recording process/credential
seams. It includes valid, same-version substitution and partial-retry cases;
expected assertions are one original producer ID, permitted valid push invocation,
and zero push calls for the invalid candidate. It cannot issue a real NuGet push.
Target: under five minutes including test-project restore on a prepared SDK host,
under two minutes warm. Report actual cold/warm times separately; if cold restore
exceeds the target, surface that measured limitation and improve the test setup.
Full native evidence remains mandatory and is never replaced by this local fixture.

Required specification after this pass: 10/10; measured DX remains pending.

## DX pass 2: CLI ergonomics

Initial specification score: 7/10. Keep the approved command names and mandatory
identity fields: local convenience must not infer expected authority from the
submitted evidence. Add command-scoped help for `verify-tailwind-consumer` and
`verify-tailwind-evidence`, including mutually exclusive modes, required flag
groups, directory constraints and output files. Missing mode is an error.

The workflow builds the verifier once and invokes its DLL. Its complete release
consumer command is the following future interface; the workflow sets every
environment variable from the validated tag/source and exact producer outputs:

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

Documentation must provide equivalent complete examples for aggregate,
publish-preflight, and both publisher commands; all share the design's bundle
and producer flags. Use environment projection plus quoted arguments, never
inline shell evaluation. Paths may be configured; trust checks, required hosts,
archive matching, projection rules and freshness have no bypass flag.

**Choice 2:** retain explicit release flags in workflow invocations (recommended)
instead of introducing a new context-file shortcut. A generated context file
would shorten invocations but add a parser and obscure which values are trusted.
The existing wrapper and command-scoped help provide the simpler human entrypoint.
Keep the .NET CLI's 0 success / 1 failure convention. Preserve the old Bash
wrapper's 2 for argument errors, 1 for proof failures, and 0 for success;
missing option values must show usage instead of an unbound-variable crash.
Specification target after this pass: 10/10, pending implementation validation.

## DX pass 3: errors and debugging

Initial specification score: 7/10. The existing publisher says an artifact “does
not match the manifest SHA-512 hash,” and the aggregate's early shell `test`
provides no explanation of missing hosts. The design requires diagnostics, but
does not name a stable machine-readable error shape.

Use a private error record with `code`, `stage`, `message`, `expected`, `observed`,
`nextAction`, `docsUrl`, and optional relative `evidencePath`. Keep the code stable
within the schema version; human wording may improve. Emit problem/cause/fix
before log details and preserve the failed stage if rendering itself fails.

| Trace | Current behavior | Required diagnostic and action |
|---|---|---|
| Same-version package swapped | Generic manifest SHA-512 mismatch | `artifact-hash-mismatch`: name package and both digests; recover the exact producer ID, never repack |
| One native job failed | Aggregator stops at `test "$MATRIX_RESULT" = success` | `native-set-incomplete`: list missing/failed/current invocation RIDs; rerun all native jobs/all release jobs using frozen producer |
| Original artifact expired | Generic download failure | `recovery-authority-unavailable`: identify missing original ID and earliest deadline; stop same-run publishing and follow fix-forward/abandonment runbook |

Also define `input-invalid`, `payload-mismatch`, `host-mismatch`, `history-unknown`,
`command-failed`, `cancelled`, and `limit-exceeded`. Do not classify a failed
diagnostic upload as an integrity mismatch. Reports use relative paths and
whitelisted values; raw credentials/environment dumps never enter verbose output.
Specification target: 10/10 after these cases and CLI entrypoint tests exist.

## DX pass 4: documentation and learning

Initial specification score: 7/10. The maintainer README currently teaches
package-guidance reconciliation first, while release-ops contains the existing
publish and retry path. Keep those canonical owners, add a short early evidence
link, and provide distinct “local rule demonstration,” “native release proof,”
and “recover an existing release” sections so the reader can find the right
task within two minutes.

Each new internal request, result, schema field, default, limit and error code
needs reference, decision and pitfall documentation per AGENTS.md. Include all
mode examples and one failed report with the exact next action, anchored links
to the runbook, and the .NET/Bash prerequisites. The copied examples must be
covered by meaningful command parsing or workflow fixtures; no hosted playground
or video is required for this maintainer CLI. Specification target: 10/10.

## DX pass 5: upgrades and migration

Initial specification score: 8/10. The design already rejects v1 receipts at the
new gate and preserves #790 product behavior. Clarify that old artifacts retain
historical diagnostic value but cannot be upgraded by relabeling their schema
or supplying missing hashes from another candidate.

| Existing caller/artifact | Upgrade behavior |
|---|---|
| Local script with original four flags | Preserve argument names/report location and exit conventions; delegate to typed local mode |
| Pack-time consumer workflow | Use in-process typed service and capture result before cleanup; never recurse through wrapper |
| v1 native/aggregate artifacts | Historical only; create a complete v2 proof against an eligible frozen producer |
| Existing stable/prerelease publisher | Native evidence/start receipt mandatory whenever the plan contains Tailwind |
| Non-Tailwind publish plan | Existing manifest/plan checks remain; no accidental requirement for Tailwind evidence |
| Partial old release | Do not synthesize missing original authority; follow existing fix-forward policy |

No consumer package API changes or codemod is warranted. Add an unreleased entry
and migration/runbook notes before enabling the new workflows. Specification
target: 10/10; actual compatibility requires tests.

## DX pass 6: environment and tooling

Initial specification score: 8/10. Existing .NET 10, xUnit, InternalsVisibleTo,
CliWrap, and YamlDotNet are sufficient; add no public SDK or test framework.
The first local rule fixture is cross-platform and credential-free. An actual
consumer proof still requires network access for reviewed third-party packages
and Tailwind downloads, plus the native runner for the platform it claims.

The local adapter keeps this exact interface (run from the repository root,
after producing packages and building the maintainer tool):

```bash
bash scripts/verify-tailwind-package-consumer.sh \
  --artifacts "$PACKAGE_ARTIFACTS" --package-version "$PACKAGE_VERSION" \
  --work-directory "$FRESH_WORK_DIRECTORY" \
  --report-path "$FRESH_WORK_DIRECTORY/tailwind-package-consumer-proof.md"
```

Its output is visibly `local-only`: current-host consumer behavior, no eligible
native release receipt. The adapter accepts only its existing flags; release
identity flags are rejected as usage errors. The typed release command must
validate complete authority before restore/build. Document PowerShell invocation
of the built DLL separately; native workflows may retain Git Bash where the
runner contract requires it. No global tool install, paid account, hosted sandbox,
TypeScript types, language server, or custom editor integration is needed.
Specification target: 10/10; actual five-host portability must be exercised.

## DX pass 7: contribution and ecosystem

Initial specification score: 8/10. The repository already provides an issue tracker,
CONTRIBUTING guidance, release notes and source-visible tests. Keep those routes;
there is no need for a new community channel or plugin system for a private tool.
Add a small issue-report checklist requesting schema, stage, original artifact IDs,
sanitized expected/observed values and a retained report link; never request keys
or whole environment dumps. This makes a contributor's report actionable without
requiring them to understand every evidence field. Specification target: 10/10.

## DX pass 8: measurement and feedback

Initial specification score: 5/10. The design mandates acceptance but did not
separate contributor onboarding time from real release duration. Record cold/warm
fixture elapsed time, time to locate the relevant runbook section, added release
critical-path duration, native failure/retry count and artifact bytes for the
first three candidate runs. Reports already own stage durations and IDs, so reuse
them instead of collecting new user analytics.

Target one prepared-host terminal session under five minutes for the local
fixture, under two minutes to locate remediation, and a full candidate completing
within the retained workflow timeouts. Do not infer success from those targets:
the first actual run supplies a baseline. Feedback goes through the existing
GitHub issue template; repeat the DX audit when a real failure shows confusion.
Specification target: 10/10; execution measurements are outstanding acceptance work.

## DX operational command recipes

The examples below are implementation contracts. The docs must define each variable
next to the example and test that its value comes from the named trusted output.
`PACKAGE_INDEX_DLL` is built from `SOURCE_COMMIT`; `SOURCE_CHECKOUT` is that checkout.
`PRODUCER_DIRECTORY` and `AGGREGATE_DIRECTORY` are the corresponding download
action's `download-path`; all IDs/hashes below come from protected producer or
aggregate outputs, not from unverified receipt contents. `REPORT_DIRECTORY` is
a fresh caller-selected output directory disjoint from every input.

Use distinct fresh `AGGREGATE_REPORT_DIRECTORY`, `PREFLIGHT_REPORT_DIRECTORY`
and `PUBLISH_REPORT_DIRECTORY` paths. The authoritative start receipt path is
resolved from its successful upload/recovery, even when preflight also writes a
local draft. For Bash workflow steps, construct a shared argument array explicitly:

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
dotnet "$PACKAGE_INDEX_DLL" verify-tailwind-evidence "${bundle_args[@]}" \
  --mode aggregate --evidence-input "$HOST_EVIDENCE_DIRECTORY" \
  --host-artifacts-map "$HOST_ARTIFACTS_MAP" \
  --native-invocation-id "$NATIVE_INVOCATION_ID" \
  --report-directory "$AGGREGATE_REPORT_DIRECTORY"
```

Preflight uses the downloaded successful aggregate and prepares push inputs:

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

After a successful start-receipt upload/recovery and token acquisition, the
publisher receives the same arguments plus the authoritative receipt:

```bash
dotnet "$PACKAGE_INDEX_DLL" publish-prerelease "${bundle_args[@]}" \
  "${aggregate_args[@]}" --publication-directory "$PUBLICATION_DIRECTORY" \
  --publication-start-receipt "$START_RECEIPT_PATH" \
  --publication-start-artifact-id "$START_ARTIFACT_ID" \
  --publish-log "$PUBLISH_LEDGER_PATH" --report-directory "$PUBLISH_REPORT_DIRECTORY" \
  --api-key-env NUGET_API_KEY
```

The stable example replaces only `publish-prerelease` with `publish-stable` and
uses the existing stable plan/version/source validation. These recipes are for
protected workflows; the local quick start never requests credentials.

Before first release, verify the five `TAILWIND_NATIVE_HOST_RUNNERS` mappings,
actual OS/architecture reporting, .NET/Bash prerequisites, artifact permissions,
retention, original-run rerun window, and protected NuGet environment. Include
the exact run's “Re-run all jobs” link/instruction for `native-set-incomplete`;
[the CLI equivalent](https://cli.github.com/manual/gh_run_rerun) is
`gh run rerun "$RUN_ID" --repo forge-trust/AppSurface`. The `--failed` option
may leave an incomplete new native invocation, so the diagnostic does not suggest
it. Publishing retries recover the existing start receipt first. Do not delete
old artifacts as a migration step, create a replacement under an old run, or
attempt to convert v1 receipts to v2.

## DX scorecard and implementation checklist

Scores below assess plan specification coverage, not delivered user experience.
No previous #798 DX review exists to provide a measured trend. Native reviewer
scores reflected the pre-amendment document and are preserved in its report.

| Dimension | Initial plan | Required specification after amendments | Evidence still required |
|---|---:|---:|---|
| Getting started | 6 | 10 | Execute the exact filtered fixture, record cold/warm times |
| CLI | 7 | 10 | Parse/help tests and full protected workflow examples |
| Errors | 7 | 10 | Three traced failures and every stable code in command tests |
| Documentation | 7 | 10 | Copyable examples and linked navigation test |
| Upgrade | 8 | 10 | Old wrapper flags/exits and v1 rejection matrix |
| Environment | 8 | 10 | Five actual hosts and local-only boundary |
| Community | 8 | 10 | Contributor issue checklist, existing channels |
| Measurement | 5 | 10 | First three candidate reports, no fabricated baseline |
| Overall | 7 | 10 specified | Delivered DX is unmeasured |

- [ ] Prepared-host local fixture under five minutes; warm under two minutes.
- [ ] Exact-source build and one fixture command; output proves valid and invalid cases.
- [ ] Each failure contains problem, cause, fix, docs and bounded evidence.
- [ ] Help and all four command modes explain required fields, defaults and constraints.
- [ ] Identity requirements have no bypass; configurable paths remain safe/disjoint.
- [ ] Complete Bash examples and Windows DLL invocation instructions are tested.
- [ ] v1 artifacts and local reports visibly cannot authorize release publishing.
- [ ] First-release runner/retention/permissions checklist and original-candidate runbook.
- [ ] API/XML docs, migration entry, error reference and cross-links land with code.
- [ ] Measure first three candidate runs and use existing GitHub issues for feedback.

TypeScript, public SDK installation, a hosted free tier, custom editor tooling,
new community service, video tutorials, and automatic codemods are not applicable
to this private .NET maintainer-tool enhancement. Existing docs, xUnit, typed
command requests and repository contribution guidance are reused. No additional
DX debt is deferred to TODOS.md; the portable-verification follow-up remains the
only new deferred item.

### DX implementation tasks

- [ ] **DX-T1 (P1, human: 4h / agent: 30min):** add command-scoped help and complete native/aggregate/preflight/publish recipes. Files: Program.cs, PackageIndex README, release-ops. Verify parsing and trusted output mapping, including missing/unknown/mixed-mode options.
- [ ] **DX-T2 (P1, human: 4h / agent: 30min):** preserve local wrapper flags, usage exits and report mapping; add v1/local/release migration matrix. Files: script, Program.cs, CLI tests. Verify old invocation and strict mode separation.
- [ ] **DX-T3 (P2, human: 3h / agent: 30min):** implement reason-code summaries and measurable local/release documentation. Files: renderers, README, release-ops, ci-critical-path. Verify diagnostic examples, safe rerun action and recorded elapsed times.

## DX independent reviews and final contract amendments

Both fresh-context voices ran successfully with `combo/sub`; routing does not
establish different model families. The native review initially raised five
issues. The CLI review raised six concerns and exited 0. Its claim that only the
consumer recipe existed refers to the document before the concurrent recipe
amendment; all four protected recipes are now present above. A native follow-up
confirmed that improvement and identified the remaining contracts below.

| Dimension | Native reviewer | Codex CLI | Consensus and disposition |
|---|---|---|---|
| First useful result | Needs explicit fixture and expected output | Separate smoke test from actual native proof | Confirmed; local contract smoke test only |
| Naming and CLI | Full command matrix required | Many identity flags need safe replay guidance | Confirmed; explicit authority plus help/output matrix |
| Errors | Actionable next step per failure | Version, channel and multi-error semantics missing | Confirmed; diagnostic contract below |
| Documentation | Complete recipes and recovery transcript | Same; immutable IDs must be visible | Confirmed; checked examples are acceptance work |
| Upgrade | Preserve wrapper/report behavior and v1 boundary | Same; test exits 0/1/2 | Confirmed; exact adapter map below |
| Environment | Centralized prerequisites and native expectations | Preflight should distinguish host failure | Confirmed; automatic prerequisite stage below |

Six of six dimensions agree on required improvements; none is an executed
acceptance test. This is a plan review, so “10 specified” in the scorecard means
all eight passes have concrete acceptance work, not measured product quality.

### Output and diagnostic contract

All filenames below are fixed relative to the validated proof-owned report
root. Every JSON success file is atomically renamed into place only after its
last prerequisite; failed stages cannot leave a stale success file.

| Command/mode | Output |
|---|---|
| Consumer release | `tailwind-native-host-proof.json` (v2); `diagnostics.json`; `summary.md`; `commands/`; `evidence/` |
| Consumer local | `tailwind-local-proof.json` (`releaseEligible: false`); same diagnostics; existing Markdown report path |
| Evidence aggregate | `tailwind-native-aggregate.json`; `hosts/<rid>/` containing the exact bound files; diagnostics and summary |
| Evidence publish-preflight | `publication-start-receipt.json` (not authoritative until upload returns ID); diagnostics and summary; prepared archives in a separate publication directory |
| Existing publisher | Existing publish ledger path, extended identity fields; diagnostics and summary in explicit report directory |

`diagnostics.json` uses schema `appsurface-tailwind-diagnostic-v1`, required
`status` (`succeeded`, `failed`, `cancelled`), `stage`, `releaseEligible`, and an
ordered `errors` array. Each error has `code`, `message`, `expected`, `observed`,
`nextAction`, `docsUrl`, and optional confined `relativeEvidencePath`. Unknown
values are JSON null rather than guessed. At most 100 errors are serialized;
`errorsTruncated` identifies omitted entries and never converts failure to success.
Validation gathers independent schema/host-record errors after safe parsing;
stop immediately on unsafe paths, invalid authority, or a failed process
prerequisite. A fixed stage order gives deterministic primary error selection.

Human summaries and the report path go to stdout; concise failures go to stderr.
Machine consumers read the file, never scrape prose. If the requested report
location is unsafe, print a bounded diagnostic to stderr and write nothing there.
Dotnet commands return 0 only for success/help, 1 for validation, prerequisite,
process, or cancellation failures. The legacy Bash adapter returns 2 for argument
usage errors, 1 for proof failures, 0 for success. No success receipt is written
on cancellation or partial execution. Add `prerequisite-unavailable` and
`native-invocation-mismatch` to the stable codes already listed.

Render recovery commands from trusted validated workflow context, using an argv
representation and shell-specific quoting, never evaluating a report string.
An incomplete invocation can display the known repository/run `gh run rerun`
command above. Missing/deleted authority displays the original run link plus the
fix-forward runbook, not a command that repacks. A hash mismatch recommends a
fresh download of the same immutable ID; it does not copy observed identity into
expected flags. Reports redact tokens, environment secrets and credential URLs.

### Prerequisites, smoke output, and compatibility

The consumer command begins with an automatic `prerequisites` stage before
restore: .NET 10 SDK availability/version, actual OS/process architecture,
expected RID support, executable availability, and writable isolated temp space.
Report the selected SDK and each failed requirement together. Network/source
errors are a separate restore-stage diagnostic; a preliminary network ping cannot
prove restore availability and must not become a new external health dependency.
The typed proof owns ZIP, hash, filesystem and JSON work through .NET APIs; it no
longer requires the old script's `unzip`, `find`, `grep`, `tr`, or hash utilities.
The workflow resolver uses `gh` and `jq`; Bash remains a workflow/adapter
prerequisite. Document .NET 10.x, Windows Git Bash/MSYS argument conversion, and
the #790 Windows ARM64-to-win-x64 exception without misreporting architecture.
No new prerequisite-only public command or service is needed.

The **local contract smoke test** writes sanitized `sample-evidence.json` and
`summary.md` under an optional `ISSUE798_SMOKE_OUTPUT_DIRECTORY`, required to be a
new safe directory when supplied. Otherwise use a unique temp directory and print
its absolute path. Never overwrite an existing directory; fixtures clean private
scratch on success but keep the opt-in sample reports until the user removes them.
Examples visibly say `releaseEligible: false`. Print cold/warm elapsed time and
expected assertions; no real native or NuGet publication claim follows.

| Existing Bash flag | New local request mapping |
|---|---|
| `--artifacts` | `ArtifactsInput`/`--artifacts-input` |
| `--package-version` | `PackageVersion`/`--package-version` |
| `--work-directory` | Local proof workspace; preserve documented safe recreation policy |
| `--report-path` | Legacy Markdown destination; default `<work>/tailwind-package-consumer-proof.md` |

Keep the existing Markdown headings and consumer assertions, add a visible
local-only label and link to adjacent structured diagnostics. Usage and malformed
values fail before launching the verifier. The adapter invokes the exact-checkout
built tool, building the maintainer tool if needed under the existing documented
entrypoint; it never packs product packages. Tests cover omitted optional report,
paths with spaces, missing values, unknown options, invalid version, success and
failure. Native CI invokes the built DLL directly and never routes through the
local compatibility adapter.

DX completion: eight passes complete; nine-stage journey, empathy narrative,
roleplay, command recipes, scorecard and ten acceptance checklist items written.
Two voices, six concordant dimensions, no unresolved DX scope challenge. Three
implementation tasks are recorded; all concrete findings are assigned to those
tasks and the engineering contracts/tests. No extra DX infrastructure deferred.

## Engineering scope challenge and evidence

Scope accepted in full. The approved B implementation is larger than a small
patch, but each boundary already has a release-system owner. Eight-plus affected
files are justified by the producer, consumer, aggregate, publisher, reusable
workflow, two release callers, tests and documentation. Keep one private typed
implementation in PackageIndex and one small workflow transport resolver; do not
split it into an SDK, generic attestation framework or new service.

This review read current-main `PackageArtifactWorkflow`, `PackagePublishing`,
`Program`, `PackageProofWorkDirectory`, the archive validator, actual xUnit tests,
and all three release/native workflows. Current tests live mainly in
`PackageArtifactValidationTests.cs`; there is no `PackagePublishingTests.cs`.
The project uses xUnit, Microsoft.NET.Test.Sdk and net10.0. No CLAUDE.md or root
`global.json` supplies an alternate test/toolchain contract. Current central
versions are CliWrap 3.10.1 and YamlDotNet 18.0.0; no dependency upgrade is needed.

| Finding | Verified motivating evidence | Decision |
|---|---|---|
| ENG-A1 P1, confidence 9/10: producer context needs an explicit handoff | Approved design:95-96 requires `repositoryId`, `sourceCommit`, `producerRunId`, `producerAttempt`; Program:171 only calls `options.CreatePackageArtifactRequest()` | Extend existing producer request and verify-packages flags, validate the complete release context before packing |
| ENG-A2 P1, confidence 10/10: credential ordering needs a test seam | PackagePublishing:302 reads `Environment.GetEnvironmentVariable(request.ApiKeyEnvironmentVariable)` before artifact/evidence validation | Inject a minimal credential provider; move its first read after all Tailwind checks and authoritative start receipt validation |
| ENG-A3 P1, confidence 9/10: recovery is a workflow state machine, not a name lookup | Approved design:387-395 requires complete prior job/step history; current native workflow:146 runs `test "$MATRIX_RESULT" = success` before download | Implement/test complete resolver states; gather available diagnostics before reporting aggregate failure |
| ENG-Q1 P1, confidence 10/10: normalizing first hides invalid raw names | PackageArtifactValidation:1507: `entryPath.Replace('\\', '/').Trim('/')` | Raw-path validation precedes canonicalization; share focused Tailwind validator without changing unrelated package semantics blindly |
| ENG-Q2 P1, confidence 10/10: buffered capture contradicts bounded reports | PackagePublishing:36: `command.ExecuteBufferedAsync(timeoutCts.Token)` | Add opt-in bounded capture through existing request/runner seam, drain streams after truncation |
| ENG-T1 P1, confidence 9/10: existing tests do not exercise the new guarantee | PackageArtifactValidationTests:8193 tests manifest-order publishing; native workflow:216 uploads only aggregate JSON | Add real-archive mutation, no-credential/no-push, complete evidence and exact original retry tests |
| ENG-T2 P1, confidence 9/10: simulated host records cannot validate restore projection | Approved design:258: “Test this projection against real NuGet restores on all five hosts.” | Add a manual-only nonpublishing rehearsal through the existing package-artifacts workflow, plus native mutation probes |
| ENG-P1 P2, confidence 8/10: complete evidence creates repeat I/O cost | Approved design:355-357 requires aggregate JSON and five bound proof directories | Stream hashes, copy only required evidence, measure candidate/aggregate bytes and stage durations |

These are implementation risks and missing plan details, not claims that proposed
code already regressed. No finding relies on reflection or an assumed absent field.
The source base is pinned so line references remain auditable after main moves.

## Engineering section 1: architecture

```text
Program / exact-checkout DLL
  | parse command-specific request; validate required authority
  +--> PackageArtifactWorkflow (existing pack/local proofs)
  |      +--> TailwindConsumerProof(local) -> immutable closure snapshot
  |      +--> manifest writer -> TailwindProofSubject writer
  +--> TailwindConsumerProof(release)
  |      +--> TailwindProofValidation [pure contracts/archive/graph/payload]
  |      +--> IExternalCommandRunner [restore, locked restore, build]
  |      +--> TailwindProofReportRenderer [bounded diagnostics/evidence]
  +--> TailwindEvidenceWorkflow(aggregate / publish-preflight)
  |      +--> same TailwindProofValidation
  |      +--> prepared publication files + non-authoritative start JSON
  +--> PackagePublishWorkflow
         +--> existing plan/readiness/manifest validation
         +--> same evidence validation + authoritative start identity
         +--> IReleaseCredentialProvider.Read (first credential access)
         +--> per-file rehash -> existing runner push -> ledger

GitHub workflow resolver [gh + jq; separately fixture-tested]
  | resolve original start -> original producer/aggregate IDs, or safe first pack
  | exact-ID downloads -> typed commands -> immutable upload IDs/digests
  +--> never grants authority from a local JSON file or job conclusion alone
```

Internal types remain in `ForgeTrust.AppSurface.PackageIndex`: value records in
`TailwindProofContracts.cs`; pure parsers/checkers in
`TailwindProofValidation.cs`; process choreography in `TailwindConsumerProof.cs`;
aggregation/preflight in `TailwindEvidenceWorkflow.cs`; diagnostic rendering in
`TailwindProofReportRenderer.cs`. These are focused files, not separate assemblies.
Use internal APIs already exposed to the test assembly. Do not add a generic
filesystem abstraction: real temporary files/ZIPs test paths; injectable runner,
credential provider, clock and workflow API fixtures cover external boundaries.

`ValidatedProducerBundle`, `ValidatedNativeSet`, and `PreparedPublication` are
immutable results created only by validation services. They contain confined
paths and parsed identity values, not permission to skip later byte rechecks.
Persist the corresponding private schema JSON, never serialized object caches.
Producer graph capture is an in-process result returned before local cleanup;
no recursive CLI invocation or manifest/local-proof dependency cycle is added.

For release producers, extend existing `verify-packages` with
`--repository-id`, `--producer-run-id`, `--producer-attempt`, `--source-commit`.
All four are required together and are validated before any pack command. The
workflow always passes them from validated context. With none supplied, preserve
local verify-packages behavior, producing no release-eligible subject. Partial
context is invalid; local consumers cannot fill it in. The subject output is the
fixed adjacent `tailwind-proof-subject.json`, written only after the manifest and
all existing package proofs succeed. The workflow computes/exports its exact
file SHA-256 before upload; the artifact ID arrives only after upload.

```bash
dotnet "$PACKAGE_INDEX_DLL" verify-packages \
  --repo-root "$SOURCE_CHECKOUT" --package-version "$PACKAGE_VERSION" \
  --artifacts-output "$PRODUCER_DIRECTORY" \
  --artifact-manifest "$PRODUCER_DIRECTORY/package-artifact-manifest.json" \
  --report "$PRODUCER_DIRECTORY/package-validation-report.md" \
  --repository-id "$EXPECTED_REPOSITORY_ID" \
  --producer-run-id "$PRODUCER_RUN_ID" --producer-attempt "$PRODUCER_ATTEMPT" \
  --source-commit "$SOURCE_COMMIT"
```

Preflight is repeatable only into a new disjoint publication/report workspace.
It returns the prepared file inventory and start JSON; a successful immutable
upload/recovery supplies the separate trusted start artifact ID. The publisher
revalidates the original bundle, complete aggregate inventory, prepared files and
start binding before credential access. Add optional `IReleaseCredentialProvider`
to the existing construction path with the environment reader as production
implementation; tests record reads and fail if invalid evidence reaches it.
The Tailwind requirement comes from the resolved plan, so hiding a CLI flag or
omitting Tailwind from a caller-provided manifest does not disable it.

Extract the resolver into a checked-in Bash script with narrow operations
`resolve-producer`, `resolve-publication`, and `resolve-native-evidence`.
It accepts validated numeric IDs/run attempts and repository context, uses `gh`
argument arrays and jq, and writes fixed output keys for Actions. Tests substitute
`gh` through a controlled PATH fixture; production uses the installed executable.
No remote mutations are performed by these resolver operations. Freeze step IDs
and job-name matching rules in one tested table; every prior attempt is queried,
all pages consumed, rate limits bounded, and unknown/truncated history blocks.
The initial attempt with no artifact may pack. A later no-artifact attempt may
pack only after all prior producer uploads are proven not to have succeeded and
publication never started. A started/cancelled/unknown publication job blocks.
When a start receipt exists, resolve it first and follow its exact original IDs.

Per-host evidence names include run, native invocation and RID. `nativeInvocationId`
is the caller run ID plus run attempt plus a fixed reusable-call-site identifier;
it is passed explicitly and matched as an opaque bounded string. Do not equate it
with producerAttempt. Before a start receipt, select only the explicit successful
aggregate output for that invocation. After a start receipt, its aggregate wins.
Job outputs never infer an artifact ID from a JSON field or download-path output.

No new public API, package, endpoint or authentication scheme is introduced.
The new documents travel in existing immutable Actions artifacts and are updated
atomically with the exact-source verifier/workflows. This preserves the trusted
CI boundary; injected credentials and typed results improve ordering tests, not
protection against an authorized runner forging arbitrary workflow context.

## Engineering section 2: code quality

The current validator and test file are large; add cohesive files above and reuse
existing package hashes, plan validation, release environment, source-manifest
comparison, and recording runner. Extract the Tailwind-specific contract once,
then route pack-time and native checks through it. Do not duplicate the current
shell proof in C#, retain jq interpretation of proof schemas, or add reflection
access to private methods. Keep schema parsing separate from command execution
so malformed JSON cannot accidentally trigger a restore while gathering errors.

For strict JSON, reuse .NET 10's
[`JsonDocumentOptions.AllowDuplicateProperties`](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Document/JsonDocumentOptions.cs)
with false, MaxDepth 64, comments/trailing commas disabled and the byte limit
already specified. Then check required properties/types/finite enums using
ordinal schema names; reject unknown fields for these private versioned schemas.
Do not rely on permissive serializer defaults or implement a duplicate-property
tokenizer that the runtime already supplies. Preserve manifest v1's existing
shape while validating its identities strictly at the new release boundary.

Raw ZIP/evidence paths reject absolute paths, backslashes, empty/dot/traversal
segments, drive/UNC prefixes, NUL, colon/alternate streams, Windows reserved names,
trailing-dot/space aliases, duplicate normalized names and case collisions across
all hosts. Permit structural directory entries only when their clean relative
name matches child paths; do not include them as payload bytes. Reject symlink
ZIP types and filesystem symlink/junction/reparse ancestors before following them.
Use a confined, non-link directory for output and atomic same-directory rename.
Workflow paths must use the physical canonical runner-temp root (for example,
resolve a trusted macOS temp alias before selecting child workspaces); never
resolve an evidence-controlled symlink to make it pass containment checks.
The threat model does not promise race resistance against another malicious
process running as the same authorized CI user; check again after each external
build and before publishing to detect accidental mutations.

For bounded capture, extend `ExternalCommandRequest` with an optional capture
policy and `ExternalCommandResult` with defaulted truncation/timing metadata so
existing callers/tests remain valid. New proof requests always set the policy.
Use CliWrap stream pipes and `ExecuteAsync`, retaining at most the stated byte
limit while draining the rest; a callback that accumulates whole unbounded lines
is insufficient. Preserve cancellation versus timeout distinction. Truncated logs
can accompany success if all required structured evidence remains complete;
missing/truncated required evidence always fails. XML-doc the new behavior and
exercise the actual runner with a short test subprocess, not only mocks.

Unexpected exceptions are classified at the CLI boundary after cleanup/evidence
attempts. Diagnostic-write failure never masks the original stage/exit status.
Use a short independent cleanup token after cancellation; never start another
restore/build/push. Credentials and child-process arguments are redacted before
any report serialization. Failure reports retain only bounded proof-owned data.
Update reference docs with every internal request/result, defaults, limits,
ordering rule and when local versus release mode is appropriate.

## Engineering section 3: test review

The complete [test plan](issue-798-tailwind-artifact-provenance-test-plan.md)
contains the branch-by-branch matrix, actual test-framework evidence, existing
coverage versus required additions, acceptance steps, and retained artifacts.
This review adds no tests to the current product checkout; every new case is an
implementation requirement. Existing tests cover archive/source contracts,
manifest-order publishing, duplicate reporting, readiness rejection, stopped
publication, secret redaction and local proof assertions. They do not establish
same-producer restore, v2 evidence completeness, or frozen retry authority.

```text
NEW CLI/local UX -> C01-C07 + O04-O05 [integration, help/docs/adapter]
producer local proof -> graph snapshot -> subject -> P01-P05 [real ZIP + runner]
release consumer -> prerequisites -> restore/lock -> R01-R04 [integration + native]
  -> raw path/archive/payload/source -> R05-R11 [real filesystem/ZIP + native]
  -> build + #790 behavior + postcheck -> R12-R13 [integration + native]
aggregate -> complete same-invocation five -> A01-A07 [unit/integration/workflow]
publish -> plan gate -> prepare -> uploaded start -> F01-F04 [zero-read/zero-push]
  -> rehash each -> push/ledger/unknown crash -> F05-F09 [injected publisher]
resolver -> initial / safe preupload retry / frozen original -> H01-H09 [gh fixtures]
all stages -> timeout/cancel/bounded error output -> O01-O03 [real runner + injected]
actual five-host + mutation + interrupted replay -> N01-N03 [native acceptance]
```

Every identified gap is added to the test matrix rather than deferred. Tests
operate through public or intentionally internal APIs. Real temp ZIP/filesystem
fixtures prove raw bytes, paths and expanded payload. Recording runner and
credential seams prove call order; a short real subprocess proves bounded
capture and cancellation. Workflow tests must inspect parsed YAML relationships
and run the actual resolver with fixture API responses, including pagination.
Do not regard a string containing `--producer-artifact-id` as sufficient dataflow
coverage. Negative fixtures explicitly assert no later build, credential read,
success receipt or push as appropriate to their failure boundary.

For native acceptance, extend the existing manual `package-artifacts` workflow
path to export a subject/producer ID and invoke the same five-host workflow. Gate
that reusable call to explicit `workflow_dispatch` on a trusted branch; ordinary
PR runs do not gain access to self-hosted native runners. It is a rehearsal of
producer/consumer/aggregate, with no NuGet login/token/push job. Native mutation
probes run against isolated copies after valid evidence is frozen. The existing
xUnit fixture replays the complete aggregate into a recording publisher to
exercise preflight and interrupted publication. A human must observe actual
host reports before the first protected release; fake RIDs cannot meet N01.

Regression requirements include old script flags and exit codes, existing
Tailwind consumer assertions, non-Tailwind publication, stable readiness/release
evidence and post-publish smoke install. No LLM/prompt change means no eval suite.
No UI change means no browser flow. Target nearly complete changed branch
verification, with explicit rationale for any environmental failure impossible
to reproduce; solution coverage remains part of implementation validation.

## Engineering section 4: performance

The dominant cost is one full producer pack followed by five parallel restore/
build proofs and aggregate revalidation. The publisher repeats validation to
protect a direct invocation; that deliberate repetition is part of the guarantee.
Do not cache a validated result across processes or artifacts. Within one command,
reuse an immutable parsed manifest/archive index after checking it once, and
rehash bytes whenever the lifecycle requires a new observation.

Stream archive and file hashes, bound JSON/logs as already specified, and avoid
materializing whole expanded payloads in memory. Sort only bounded path/digest
inventories. Work is linear in archive/payload bytes plus sorting entry metadata;
there is no database or N+1 query problem. Native jobs use fresh private caches,
so a warm global NuGet cache is not an optimization option. Reuse downloaded
producer bytes within a job, never packages from a previous proof workspace.

The trusted source plan bounds coordinated package count; reject unplanned
artifacts and graph members before allocating work for them. The documented
per-archive limits apply to actual streamed bytes, not only ZIP headers. Apply
one overall command timeout and cancellation to every loop. The workflow resolver
requests 100 jobs per page, follows all pages, and has a five-minute total deadline
and 500-request ceiling across all attempts; exceeding either reports unknown
history. Retries for transient API failures are bounded by the same deadline.
This is a conservative ceiling under Choice 1, not permission to accept truncated
history. Report expected/observed limit and actionable cause.

Retain only exact bound evidence required for revalidation, including every
first-party consumed archive/protected projection and lock/assets files. Do not
upload the entire third-party package cache. Full native evidence is necessarily
larger than today's JSON-only aggregate. Measure producer, per-host and aggregate
artifact bytes, stage durations, copy/hash durations and failed native reruns for
the first three candidates. Compare release critical path separately from the
local smoke test target. No hard release-latency SLO is invented without data.

Keep existing 30-minute native and 10-minute aggregate ceilings initially; make
new command-level timeouts leave time for failure diagnostics/upload. At 10x
candidate rate, per-tag concurrency still serializes each candidate; independent
versions contend for the configured five runner pools. At 10x artifact size,
streaming limits provide a clear bounded failure rather than memory exhaustion.
Record resource-limit failure as failed evidence; operators cannot override it
with a skip-validation option. If measurement requires changing a ceiling,
review the documented constant and boundary tests together.

## Engineering deployment and failure review

The earlier failure registry remains applicable. All additional critical paths
now have a defined outcome and a test assignment:

| Failure boundary | Safe outcome | Test reference | Critical gap after specification |
|---|---|---|---|
| Producer/local proof/subject fails | No reusable successful subject; no native start | P01-P04 | None; not implemented |
| Malformed/archive/path/host/restore/payload | Failed diagnostics; no next build/success receipt | C04-C06,R01-R13 | None; actual native proof pending |
| Missing/stale/foreign host evidence | Gather available diagnostics, no aggregate success | A01-A06 | None; not implemented |
| Missing or ambiguous original authority | No replacement candidate/token/push | H01-H08 | None; API fixture and real rerun pending |
| Publication copy/hash/start upload fails | Zero credential reads/pushes | F01-F04,H06 | None; not implemented |
| Mid-push mutation/crash/ledger failure | Freeze original receipt; stop; record known/unknown outcomes | F05-F09 | None; injected exercise pending |
| Timeout/cancel/report write/limit | Bounded failure; no success artifact; original error retained | O01-O03 | None; real subprocess pending |

Implementation lands verifier, producer, adapter and all workflow callers as one
coherent change; do not enable publishing against a half-migrated v1/v2 contract.
Keep per-tag concurrency with cancel-in-progress false, protected environments,
readiness evidence, token permissions and smoke install. Only recovery steps need
`actions: read`; native restore/build jobs do not gain release secrets. Rehearsal
must prove schema, output, action-ID and runtime behavior before publishing.
If rollout breaks before any push, stop publishing and fix forward in a reviewed
change or restore the complete prior workflow/verifier pair for a new candidate.
After any package is accepted, retain original-candidate evidence and follow
existing fix-forward policy; never silently relax the gate to recover a release.

## Engineering parallelization and implementation order

Two directory-level lanes are available after a short sequential contract freeze:

| Lane | Owns | Dependency and integration |
|---|---|---|
| A: typed proof | PackageIndex source/tests and local adapter | Sequential internal services, then publisher integration; owns contracts and generated examples |
| B: workflow transport | `.github/workflows/`, resolver and its fixtures | Starts after schema/CLI/ID output freeze; merges after typed commands pass |
| Final integration | docs/release-ops, README, CI timing docs, native rehearsal | Main integrator owns consistency; starts after both lanes |

Lane A must not be split into simultaneous edits of Program, the large existing
test file and shared publisher contracts. Lane B may author fixtures against
frozen responses while typed implementation proceeds. One final integrator checks
all flags, output names, schemas, links and base-commit changes. No parallel branch
can redefine the same identity field. Use isolated worktrees for actual delegated
implementation; this planning review creates none.

## Engineering independent-review amendments

The native reviewer produced seven evidence-backed concerns. The first six are
P1 handoff/validation gaps; the staging concern is P2 within the trusted-runner
boundary. The amendments below preserve the approved scope.

1. **Fresh and reused outputs are identical contracts.** A final `resolved-binding`
   step runs for both upload and recovery, emitting producer artifact ID, subject
   SHA-256, repository/run/original attempt, source commit and actual expiry. A
   corresponding resolved aggregate step emits aggregate ID/JSON SHA-256/native
   invocation and expiry. Job outputs point to these resolver steps rather than
   skipped upload steps. Tests cover a reuse path with no upload-step outputs.
2. **Host transport is explicit.** The workflow resolver lists every page of the
   exact run's artifacts, matches the exact invocation-qualified names, and
   resolves at most one ID for each canonical RID. There is no invented artifact
   API filter for run attempt: attempt separation comes from the validated name
   and serialized invocation/run/attempt fields. Download available hosts by
   their individual immutable IDs into separate RID directories; preserve missing
   slots for diagnostics and require all five for success. The aggregate binds
   each resolved host artifact ID and exact receipt/file digests. A host receipt
   cannot contain its own future upload ID; do not create that circular contract.
   Names are used only by this trusted recovery/transport resolver, never as a
   replacement for content validation. The [Actions artifact API](https://docs.github.com/en/rest/actions/artifacts#list-workflow-run-artifacts)
   is the canonical transport reference.
3. **Pin the triggering source.** Both release workflows compare the annotated
   tag's resolved commit with the immutable triggering `github.sha` commit and
   fail on mismatch before producing source outputs. If git object peeling is
   needed, peel the exact event object, never a fresh mutable ref. The same event
   commit drives source CI, tool checkout, pack, subject and all native proofs.
   The [push event contract](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#push)
   identifies the triggering tip commit. Keep existing annotated-tag and main
   reachability rules. Test moved tag, missing event object and valid annotated
   tag; a re-fetch cannot redefine the event identity.
4. **Failed evidence survives ordinary nonzero exits.** The typed CLI owns failed
   or cancelled receipt creation before returning, using bounded cleanup writes.
   A subsequent `always()` upload includes those files even when the shell step
   fails; success creation is not in a later shell step that `set -e` skips.
   Runner loss or hard cancellation can prevent all cleanup: aggregate reports
   missing/cancelled evidence and fails, never fabricates completed commands.
   Structured command logs are flushed as stages complete. Test nonzero/timeout,
   cooperative cancellation and no-file hard-stop fixtures.
5. **Validate runner configuration before scheduling.** Add a small hosted
   prerequisite job that parses `TAILWIND_NATIVE_HOST_RUNNERS` with jq, requires
   exactly the five canonical RIDs and valid nonempty string/label-array values,
   and emits the full validated matrix with RID, labels and binary name. The
   matrix job depends on that success and uses its output, avoiding direct
   `fromJSON(vars...)` evaluation on malformed input. Aggregation can report a
   configuration failure even when no native job started. Unavailable/offline
   runners remain a separate scheduling failure; preflight cannot prove capacity.
6. **Projection v1 has a finite asset rule.** Protect every regular file under
   `build`, `buildTransitive`, `buildMultiTargeting`, `lib`, `ref`, `analyzers`,
   `tools`, `tasks`, `runtimes`, and `contentFiles`. In the fixed assets target,
   enumerate file-valued `compile`, `runtime`, `native`, `resource`, `build`,
   `buildMultiTargeting`, `contentFiles`, and `runtimeTargets` groups; add any
   selected archive-relative file outside those roots. `_._` is a placeholder,
   not an executable asset, but remains compared when present in a protected root.
   Metadata-only dependency/framework groups are explicitly allowlisted in the
   parser. Any other first-party asset group with unrecognized semantics fails
   as `unsupported-asset-group`; do not silently ignore it or trace arbitrary
   MSBuild imports dynamically. Both producer and hosts enforce this versioned
   rule. Add one mutation/rejection fixture per group and update the schema docs.
7. **Publication staging is confined.** Prepared archives are regular non-link
   files copied into a fresh disjoint directory, with link/reparse checks before
   and after copy. Mark them read-only where supported; validate the entire
   inventory and rehash each immediately before the command runner boundary.
   Test link substitution and mutation injected before this final recheck.
   A hostile process modifying bytes after the last check is outside the existing
   trusted-runner boundary; read-only bits and path checks do not claim to solve
   that adversary. Do not promise an impossible cross-platform atomic hash plus
   external-process open with the current `dotnet nuget push` path API.

Add tests C07 (scheduler preflight), P05 (event/tag pin), A07 (host-ID transport),
H09 (re-emitted recovery outputs), and extend R05/F06/O03 for asset groups, link
staging and hard-stop diagnostics. These are required in the linked test plan.

## Private wire-contract freeze

Implement these finite record families together and check in valid/invalid JSON
fixtures with their reference docs. This table fixes names and shapes that the
approved design described semantically; every digest is over exact file bytes,
not reserialized JSON. Common binding fields are serialized directly on release
records: `repositoryId`, `sourceCommit`, `producerRunId`, `producerAttempt`,
`producerArtifactId`, `subjectSha256`, `artifactManifestSha256`, `packageVersion`,
`payloadProjectionVersion`. The producer subject omits its future artifact ID
and uses the original subject field names in the approved design.

| Document / schema | Required shape beyond the common binding |
|---|---|
| `tailwind-proof-subject.json` / `appsurface-tailwind-proof-subject-v1` | All approved subject fields plus `payloadProjectionVersion: 1`; sorted firstPartyPackages entries `{packageId, packageVersion, artifactFileName, packageSha512}` |
| `tailwind-native-host-proof.json` / `appsurface-tailwind-native-host-proof-v2` | `status`, `nativeInvocationId`, `nativeRunId`, `nativeAttempt`, `expectedRid`, `observedRid`, `hostOs`, `osArchitecture`, `processArchitecture`, `runnerLabel`, `sdkVersion`, `firstPartyPackages`, `tailwindManifestSha256`, `restoredTailwindManifestSha256`, `binaryName`, `binarySha256`, `checks`, `files`, `diagnosticPath` |
| `tailwind-native-aggregate.json` / `appsurface-tailwind-native-host-evidence-v2` | `status: succeeded`, `nativeInvocationId`, exact `requiredRids`, five `hosts` entries `{rid, hostArtifactId, receiptPath, receiptSha256}`, and full `files` inventory |
| `publication-start-receipt.json` / `appsurface-tailwind-publication-start-v1` | `aggregateArtifactId`, `aggregateSha256`, `nativeInvocationId`, `packages` inventory `{packageId, packageVersion, artifactFileName, packageSha512}`; no self upload ID |
| `tailwind-local-proof.json` / `appsurface-tailwind-local-proof-v1` | `status`, `releaseEligible: false`, `packageVersion`, observed host, local firstPartyPackages/checks/files and diagnosticPath; no fabricated release authority |

Native `firstPartyPackages` entries contain package ID/version, producer and
restored SHA-512, confined archive path, `payloadVerified`, and sorted
`payloadFiles` entries `{packageRelativePath, evidencePath, sha256}`. `checks` has
required booleans `generatedCss`, `hostCacheBinary`, `noRuntimeCompanionDependency`,
`noNativeConsumerOutput`, and `postBuildPayloadUnchanged`; only all true can
succeed. `files` entries are `{path, sha256}` relative to that record's artifact
root. Exclude the containing receipt itself from its own inventory; the aggregate
hashes each receipt. Exclude aggregate JSON from its own files inventory; its
trusted job output supplies the digest. Bind command reports, diagnostics,
lock/assets files, consumed archives, protected payload copies and generated CSS.
Generate bound summaries before receipt/aggregate serialization and omit their
containing document's digest from those summaries. Print the newly computed
receipt/aggregate digest to the job summary/output afterward, outside the bound
artifact. This prevents a summary-to-receipt circular hash.

Failed/cancelled native records retain the same schema and known identity fields;
unobserved result values are null, never true/default RID. Success validation
requires every mandatory value and the exact inventory. Failure records are
read for diagnostics only and cannot occupy an aggregate success slot. Reports
must fit the JSON and evidence bounds; surplus unknown files at the aggregate
release boundary fail rather than being silently dropped. Markdown summaries
are useful but cannot supply a missing required JSON/evidence value.

`hostArtifactId` and the start receipt's upload ID enter only after immutable
upload succeeds. The native CLI does not guess its artifact ID. The aggregator
receives the resolver's host-ID map separately as `--host-artifacts-map`, a
confined JSON file of zero to five distinct `{rid, artifactId, directory}` entries with RID
allowlist and relative paths under the evidence root; success requires all five. It is a trusted workflow transport
input, not an additional package identity authority or a human recovery context
file. The action download ID, resolver map and aggregate host ID must agree in
workflow tests. Add this required flag to aggregate help and examples.

## Engineering implementation tasks

These tasks derive from ENG-A1–P1 and the seven native-review amendments. They
complement the CEO/DX tasks above; overlapping descriptions are cross-phase
coverage, not additional product scope or additive effort estimates.

- [ ] **ENG-T1 (P1, human: 12h / agent: 2h): typed proof** — implement strict contracts, graph capture, raw archive/payload checks and bounded consumer execution. Files: proposed TailwindProofContracts.cs, TailwindProofValidation.cs, TailwindConsumerProof.cs, existing PackageArtifactWorkflow.cs and command runner. Verify P01-P05, R01-R13, C04-C06 and O02-O03.
- [ ] **ENG-T2 (P1, human: 6h / agent: 1h): evidence gate** — implement exact-five aggregation, host transport map, complete file inventory and preflight preparation. Files: proposed TailwindEvidenceWorkflow.cs, TailwindProofReportRenderer.cs, Program.cs. Verify A01-A07 and F01-F04.
- [ ] **ENG-T3 (P1, human: 8h / agent: 1h): publisher** — integrate credential provider, original start binding, per-file rehash and observed/unknown ledger outcomes. Files: PackagePublishing.cs, Program.cs and new publisher tests. Verify F01-F09, H05-H06 and existing non-Tailwind publish behavior.
- [ ] **ENG-T4 (P1, human: 10h / agent: 2h): workflow transport** — implement `.github/scripts/resolve-tailwind-release-artifacts.sh`, event/tag pin, scheduler preflight, full binding outputs and exact-ID downloads in native/stable/prerelease callers. Verify C07, P05, A04-A07 and H01-H09 through parsed YAML and executable API fixtures.
- [ ] **ENG-T5 (P1, human: 12h / agent: 2h plus runner time): acceptance** — implement the 58-group test matrix, manual-only package-artifacts native rehearsal and recording-publisher recovery exercise. Files: new focused PackageIndex test files, existing regression tests, package-artifacts workflow. Verify complete PackageIndex/affected Tailwind/solution suites, formatting, branch coverage and N01-N03 on actual hosts.

The implementation estimate remains roughly six to ten human development days
or two to four agent working days plus runner feedback. Per-phase estimates
include overlapping work and must not be summed as independent commitments.

## Cross-phase themes

**Original-candidate recovery must be understandable and mechanically reliable.**
CEO voices questioned its operational cost; DX voices needed copyable recovery
and failure guidance; the independent engineering voice found missing reuse
outputs and host transport details. This repeated concern supports both a clear
runbook and executable recovery fixtures. It does not authorize reducing scope.

**Explicit identity and bounded evidence need one implementation.** CEO and
engineering reviewers independently identified graph/projection ambiguity and
trusted-CI limits; DX reviewers found that those rules also need named inputs,
outputs and error codes. Keep one typed validator, explicit workflow authority
and versioned private records so human guidance matches enforcement.

**A usable release gate needs observable failure behavior.** DX reviewers asked
for environment checks and structured errors; engineering found failures before
native scheduling and after nonzero commands. Producer, host and aggregate
reports must distinguish configuration, incomplete invocation and missing
recovery authority. A successful local fixture does not substitute for real host
execution or measured release reliability.

## Suppressed findings and rejected remedies

- A compromised authorized runner or malicious same-user process can fabricate
  records or mutate a file after a final hash. Treating the new JSON hashes or
  read-only file bit as authentication/race-proofing would be false. Confidence
  4/10 as a newly introduced vulnerability; outside the approved trusted-CI threat
  model. Regular-file staging and pre-command rechecks remain required.
- Embedding a native artifact's own immutable upload ID before upload is an
  impossible remedy, not an accepted plan change. The verified transport concern
  is addressed by the aggregate's post-upload host-ID map instead.
- Reviewer claims that future commands/tests are absent from the current source
  are implementation status, not observed regressions. Existing-code comparisons
  use the pinned current-main source; stale working-checkout conclusions were
  excluded.

The task aggregator produced [eleven actionable tasks](issue-798-tailwind-artifact-provenance-tasks.md).
It selected the latest per-phase run on this detached branch and the full current
commit in the recent-commit window, then applied exact-match deduplication.
Nonidentical overlaps are marked explicitly; per-phase time estimates overlap.
The only deferred scope is portable verification/storage beyond Actions in
[TODOS.md](../../TODOS.md#release-provenance-follow-up-798).

## Engineering CLI-review resolutions

The CLI voice completed successfully, scored architecture/tests 8/10 and
performance/security/errors/deployment 7/10 before these final clarifications,
and raised six concerns. Five need explicit implementation contracts; the cache
archive concern requires correcting a factual premise and documenting resolution.

### Effective permissions and trusted rehearsal

[Reusable workflow permissions cannot be elevated by the callee](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows#nesting-reusable-workflows).
Set explicit permissions at every caller and called job; do not depend on a
sibling source-CI job's grants.

| Job / caller | Effective token permissions |
|---|---|
| Stable/prerelease native reusable call; manual rehearsal native call | `contents: read`, `actions: read`; no id-token |
| Hosted matrix-config prerequisite and native build jobs | `contents: read`; no NuGet secrets or id-token |
| Producer recovery and native aggregate/resolver jobs | `contents: read`, `actions: read`; no id-token |
| Protected stable/prerelease publish job with original-artifact recovery | Existing `contents: read`, `id-token: write`, plus `actions: read`; preserve existing protected environment |
| Manual package-artifacts rehearsal producer/resolver | `contents: read`, `actions: read`; no id-token or NuGet environment |

Upload uses the pinned artifact action's runtime authorization; do not invent an
`actions: write` requirement merely to upload. API lookup needs `actions: read`.
Native jobs that only download same-run artifacts can keep the narrower token;
any job that performs an explicit API lookup must use the resolver grant.
Test effective caller-to-callee permission reduction and the actual job dependency
chain, including the negative missing-grant fixture and real rehearsal transport.

The manual native-call job condition is exactly:
`github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && github.repository == 'forge-trust/AppSurface'`.
The source checkout remains the dispatch's immutable `github.sha`, not a later
main tip. Producer jobs for ordinary PR CI remain as today; only this allowlisted
manual path can schedule the rehearsal native matrix. Test non-main dispatch,
PR, fork/repository mismatch, and valid main dispatch. The rehearsal graph has no
NuGet login, publish command, id-token grant or secret inheritance. After merging
the implementation, run this rehearsal on main before creating a release tag or
approving its protected publish environment. Do not weaken the guard to test an
arbitrary feature branch on trusted runners.

### Restored archive resolution: verified NuGet behavior

The suggestion to replace the cache archive with a producer copy is rejected.
NuGet's PackageReference
[path resolver](https://raw.githubusercontent.com/NuGet/NuGet.Client/dev/src/NuGet.Core/NuGet.Packaging/VersionFolderPathResolver.cs)
defines the global-cache archive as `<package-folder>/<lower-id>.<normalized-lower-version>.nupkg`.
Its [package extractor](https://raw.githubusercontent.com/NuGet/NuGet.Client/dev/src/NuGet.Core/NuGet.Packaging/PackageExtractor.cs)
retains the downloaded archive at that path when Nupkg save mode is enabled.
The [default v3 save mode](https://raw.githubusercontent.com/NuGet/NuGet.Client/dev/src/NuGet.Core/NuGet.Packaging/PackageExtraction/PackageSaveMode.cs)
includes both expanded files and the archive. Expanded PackageReference use does
not imply the cached archive is absent. This was verified in upstream source;
actual .NET 10 host restores remain required acceptance evidence.

Operationally: read the single validated private `packageFolders` root and each
first-party `libraries[id/version].path` from assets; require package type,
identity/version and containment; locate the canonical regular `.nupkg` inside
that resolved directory; hash its bytes and validate nuspec identity. The fresh
restore from the producer-only first-party source establishes how it arrived.
The expanded-file comparison then proves that the build's protected files equal
that archive. Require the cache archive to exist: altered save mode or a missing
archive fails, with no fallback to a copied producer package, HTTP-cache guess,
`.nupkg.sha512`, contentHash, or metadata source claim. Add a golden path fixture
and a real cache snapshot per actual host to R03/N01.

### Source and verifier identity

At the start of every release consumer, aggregate, preflight and Tailwind publish
command, assert `git rev-parse HEAD` in `--repo-root` equals `--source-commit` and
relevant tracked tool, shared dependency/lock and Tailwind source inputs are clean.
Producer release mode does the same before pack. Build the tool after locked
restore into a new output directory with a source-commit assembly metadata stamp;
invoke that exact DLL, with no PATH-based tool lookup or reuse of prior bin output.
At runtime the metadata stamp must match the expected commit. Record actual DLL,
application dependency, deps/runtimeconfig hashes and SDK version in diagnostics.
A hash record is diagnostic provenance within trusted CI, not authentication of
an arbitrary executable. Test wrong HEAD, dirty relevant source, wrong/missing
assembly stamp and stale/global invocation; workflow fixtures verify clean build
output selection. No unverifiable “fresh DLL” assertion substitutes for this order.

### Common failure finalization

Use a focused internal `TailwindProofExecution` helper shared by producer, native,
aggregate and preflight stages. It records completed commands/stages and handles
success, `OperationCanceledException`, known validation failures and unexpected
I/O/process exceptions. A short independent cleanup token (five seconds) permits
best-effort atomic diagnostics after cancellation; cleanup never launches a new
proof or push. Publisher finalization uses the same diagnostic helper while its
already-uploaded start receipt remains the authority if ledger writing fails.
The shell resolver returns structured failure categories to its job summary,
without manufacturing a typed success record. Test pre-cancel, mid-command cancel,
timeout, diagnostic-write failure and hard runner loss separately in O03. Hard
loss can prevent reporting; missing evidence remains a failed release gate.

### Deterministic projection algorithm

Projection v1 is a conservative byte set, not a reimplementation of NuGet's
runtime asset-selection engine:

1. Require the generated consumer's single `net10.0` target, no `RuntimeIdentifier`
   and no RID-qualified target sections. Reject an unsupported graph shape before
   build. The fixed generated project declares its exact package references and
   does not accept caller-supplied IncludeAssets/ExcludeAssets overrides.
2. For each first-party archive, include **all** regular files under the ten
   protected roots listed above, regardless of whether a specific host selects
   that file. Validate ZIP uniqueness before computing this set.
3. Walk every package node in the fixed target. For the finite file-valued groups
   listed above, union all path keys. `runtimeTargets` paths are included for all
   RIDs/recognized `assetType` values (`runtime`, `native`, `resources`), with no
   host-RID filtering; unknown type or malformed metadata fails. `contentFiles`
   paths are included regardless of copy/build-action metadata. Their metadata
   does not change membership. This intentionally protects a superset.
4. Metadata-only target-node keys are exactly `type`, `framework`, `dependencies`,
   `frameworkAssemblies`, `frameworkReferences`, and `compileOnly`: `type` and
   `framework` are strings, dependencies maps package IDs to version strings,
   framework lists are string arrays, and compileOnly is boolean when present.
   Fixture these shapes and reject null/wrong types. File-valued group keys are exactly the
   list above. Unknown keys/groups fail; a new NuGet shape requires a reviewed
   parser/projection update, not silent tolerance.
5. A repeated identical path across compile/runtime/groups is one union member;
   case-only aliases, different canonical interpretations or missing archive paths
   fail. There is no first-group-wins precedence. Compare the sorted set's raw
   expanded bytes and protected-directory membership before and after build.

Golden assets fixtures cover all groups, shared compile/runtime paths, RID/TFM
variants, contentFiles metadata and excluded-asset attempts. Real host snapshots
validate the supported shape. This resolves the algorithm gap without adding a
new resolver dependency or weakening the full expanded-payload guarantee.

## Engineering voices and completion summary

**Native subagent:** seven concerns, six P1 and one P2, with a sound B architecture.
**Codex CLI:** six concerns, successful exit 0; initial dimension scores 8/8/7/7/7/7
for architecture/tests/performance/security/errors/deployment. Five concerns were
incorporated as explicit rules; the archive-absence premise was corrected using
NuGet source while retaining the useful request for an exact path mechanism.
These scores precede the final resolutions; no invented post-amendment score or
executed-test approval is reported.

| Dimension | Native review | Codex CLI | Consensus / final disposition |
|---|---|---|---|
| Architecture | Sound; recovery outputs and transport incomplete | Sound; source/tool and archive binding need detail | Confirmed concern; resolved bindings, actual cache path and build stamp specified |
| Tests | Add edge, matrix/retry and mutation fixtures | Broad matrix; missing executable contract checks | Confirmed concern; 58 groups plus explicit permission/source/ref variants |
| Performance | Follow-up: bounds/timing useful; cold hosts cost expected | Bounds reasonable; duplication/cold restore costly | Aligned on follow-up only; not independent confirmation |
| Security | Source tag, projection and staging need precise boundaries | Effective grants, source/verifier and trusted-ref guard missing | Confirmed concern; finite projection, permission matrix and exact guard specified |
| Error paths | Nonzero/cancelled host must retain evidence | Finalization must cover all workflows | Confirmed concern; common stage helper and bounded cleanup specified |
| Deployment | Scheduler/recovery transport need enforcement | Permissions and native rehearsal are release blockers | Confirmed concern; main-only rehearsal before protected release |

Five of six dimensions have independent overlapping concerns; performance was
explicitly assessed by the native voice only in follow-up after the CLI report.
It is not counted as independent confirmation. No unresolved engineering taste
disagreement remains. Both calls used `combo/sub` per user policy; distinct model
families are not established. Full reports remain in private project artifacts
as `issue798-eng-native-review.md` and `issue798-eng-codex-review.md`.

The main reviewer checked the final amendments against the identified findings,
source interfaces and official platform references. Final clarifications were
not put through a second complete outside-review cycle. This is a reviewed
implementation plan, with implementation and all native acceptance still pending.

| Required completion item | Result |
|---|---|
| Scope challenge | Accepted full approved scope; current source inspected; no reduction |
| Architecture | Eight grouped issues resolved in the specification: producer context, credentials, recovery, host transport, source/tool, scheduling, permissions, rehearsal trust |
| Code quality | Five grouped issues resolved: raw paths, bounded capture, projection, failure finalization, private wire contracts |
| Tests | Diagram plus 58 required coverage groups, existing/gap mapping, no deferred gap; test artifact written to repo and private skill paths |
| Performance | Two cost areas examined: streamed evidence/copying and bounded historical API work; measurements pending |
| NOT in scope / reuse | Both written; no new public SDK, service, signing system or package dependency |
| TODOS | One P3 portable-evidence/retention follow-up; no accepted guarantee deferred |
| Failure modes | Seven engineering boundary groups plus original registry; zero unhandled planned critical outcomes; implementation remains unverified |
| Outside voices | Native and CLI completed; 7 and 6 observations, respectively, with overlap and one corrected premise |
| Parallelization | Two directory-ownership lanes after sequential contract freeze; final integration and native rehearsal sequential |
| Lake score | 58/58 identified acceptance groups retained as required implementation work |
| Validation performed in this review | Source/platform checks, artifact/task selection, local links, Markdown structure, whitespace and decision/task consistency |
| Validation not performed | Product tests, native workflows, publication or empirical performance measurements |

## Pre-gate verification

CEO: premise challenge, all ten applicable sections, error/rescue and failure
registries, scope/reuse, dream-state delta, completion, two voices and consensus
are present. Visual review was deliberately skipped because there is no UI.
DX: all eight scored passes, journey, empathy narrative, first-result target,
checklist, two voices and consensus are present. Engineering: actual-source scope
challenge, architecture and coverage diagrams, test artifact, reuse/scope,
failure review, completion, two voices and consensus are present. Cross-phase
themes and the complete decision audit are written. Task aggregation succeeded
with eleven current-run tasks; no required output is missing.

Two durable source-backed learnings were retained: .NET 10 can reject duplicate
JSON properties directly, and a PackageReference cache can retain the actual
NuGet archive alongside expanded files. The user approved this implementation
plan as-is on 2026-09-18. The original office-hours design remains approved and
unchanged. Clean review metadata applies to plan completeness and approval;
product implementation, tests and native acceptance remain outstanding.

## Approval record

The user selected **A — Approve as-is** on 2026-09-18. The full same-byte,
five-host and original-candidate recovery scope is retained. Choice 1 accepts
the documented processing/history limits; Choice 2 accepts explicit identity
flags with help and examples. The reviewers' proposed recovery-scope reduction
is not adopted. No further decision is required for this reviewed plan.

| Phase | Host | Outside provider/status | Native completion | Findings disposition |
|---|---|---|---|---|
| CEO | Codex | Codex CLI / completed | Completed | Six-dimension concordance; full scope retained by user |
| Design | Codex | None / skipped | Skipped | No UI scope |
| DX | Codex | Codex CLI / completed | Completed | Six-dimension concordance; eight passes specified |
| Engineering | Codex | Codex CLI / completed | Completed | Seven native/six CLI observations addressed or corrected; five independent overlapping dimensions, qualified performance follow-up |

All reviewer calls used the requested `combo/sub` routing; underlying model
families remain unknown. Approval does not imply empirical validation.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|---|---|---|---:|---|---|
| CEO | autoplan / plan-ceo-review | Strategy and scope | 1 | CLEAR — plan approved | Five adjacent proposals evaluated; full guarantee retained |
| Visual design | Conditional design review | Product UI | 0 | Skipped | No UI scope |
| DX | autoplan / plan-devex-review | Maintainer workflow | 1 | CLEAR — plan approved | Eight dimensions specified; 7/10 initial, delivered DX unmeasured |
| Engineering | autoplan / plan-eng-review | Architecture, failure paths and tests | 1 | CLEAR — plan approved | 58 acceptance groups; final contracts resolve actionable review findings |
| Independent voices | autoplan native + Codex CLI | Fresh-context challenge | 3 pairs | Completed | CEO 6/6 and DX 6/6 concordance; Eng 5/6 independent overlap, performance follow-up qualified |

**VERDICT:** CEO + DX + ENG CLEARED at the plan tier. User approved as-is on
2026-09-18. The full byte/host/recovery guarantee and recommended choices are
accepted. Ready for implementation from the reviewed source base; product tests,
five-host acceptance and publishing have not run.

NO UNRESOLVED DECISIONS
