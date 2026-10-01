# ForgeTrust.AppSurface.Evidence.Planner

`ForgeTrust.AppSurface.Evidence.Planner` turns an explicit diff and a checked-in `EvidencePolicy` into one deterministic `EvidencePlan`. It is the boundary that makes a coverage or E2E gate explainable before work begins.

Begin with the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md). The policy resolver owns planning only: it does not invoke Git, start Aspire, run tests, create containers, or decide that missing evidence is acceptable. The separate `EvidenceGitChangeCapture` API does invoke Git to capture and verify exact commit-pair changes.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Minimal use

```csharp
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

var planner = new EvidencePlanner();
var plan = planner.Resolve(policy, [new NormalizedDiffPath("src/Orders/SubmitOrder.cs")]);
```

`Resolve` normalizes and sorts paths, applies the most-specific matching rules, and falls back to `ConservativeProfileId` for a path that has no rule. If equally specific rules choose different profiles, it throws `EvidencePlanningException` instead of silently taking a lower-risk route. `EvidenceUnifiedDiffReader.Read(...)` accepts a CI-provided Git-formatted unified diff when callers do not want to depend on a local Git checkout. Hunked diffs must include `diff --git` file headers; use explicit paths when only a non-Git diff is available.

## Pull-request gate planning

Use `ResolveForGate` to build a plan for a PR gate. It opts into stricter validation before selecting paths:

```csharp
var plan = new EvidencePlanner().ResolveForGate(policy, changedPaths);
```

In addition to the ordinary v1 policy checks, the gate path checks that the conservative profile includes every requirement from every rule-selected targeted profile that could be combined in one diff. It preserves resource declarations and producer resource dependencies; producer kind, version, assertions, and coverage requirements; artifact-slot roots, media types, required status, and byte bounds; and obligation identity, risk class, rationale, required assertion, and required producers. The conservative profile may add resources, producers, assertions, artifact slots, and obligations. Resource readiness modes must match, and resource or producer deadlines may be shortened. Coverage thresholds may be strengthened. A required artifact slot cannot become optional, and its byte limit may stay the same or become smaller.

An empty targeted profile is valid for an explicit docs-only rule: it contributes no requirements to the superset check and can still resolve to `NoEvidenceRequired`. PR rules and their conservative profile must use `Targeted` scope. Release profiles require separate validation in their trusted release context.

`ValidateGatePolicy(policy)` exposes the same check for policy preflight. `Resolve` and `ValidatePolicy` deliberately retain local v1 behavior and do not perform cross-profile validation. Local planning therefore remains compatible, but its result alone does not prove that a policy is safe for a PR gate. Gate callers should use `ResolveForGate` with the same base-owned policy snapshot used for the run. A failed superset check reports `ASEVD129` before a plan is produced.

## Revision-bound Git change capture

For a revision-bound gate, [EvidenceGitChangeCapture](EvidenceGitChangeCapture.cs) captures the source diff and the path-selection stream from an explicit commit pair:

```csharp
var snapshot = await EvidenceGitChangeCapture.CaptureAsync(
    trustedObjectStorePath,
    baseCommitId,
    headCommitId,
    cancellationToken);
var plan = EvidenceRevisionPlanBuilder.ResolveForPullRequest(new EvidencePlanner(), policy, snapshot);
// Run the evidence producers for `plan` here.
await EvidenceRevisionPlanBuilder.VerifyAsync(
    new EvidencePlanner(), trustedBaseOwnedPolicy, trustedObjectStorePath, plan, cancellationToken);
// Separately recheck the PR's live base/head before issuing the verdict.
```

`baseCommitId` and `headCommitId` must be complete lower-case commit object IDs, either 40 or 64 hexadecimal characters, in the same object format. They must resolve to commit objects in a trusted bare or object-only Git checkout. The capture implementation invokes Git with fixed diff options and neutralized ambient Git configuration; the gate runner remains responsible for supplying that trusted object store, pinning the Git version, fetching the exact commits, and checking its trust boundary.

The snapshot keeps two representations for different purposes. `SourceDiff` contains the exact bounded `git diff --binary` bytes and `SourceDiffDigest` binds those bytes. `NameStatus` contains the separate `git diff --name-status -z` byte stream; its NUL-delimited records are parsed into `ChangedPaths`, including both old and new paths for renames and copies. Policy selection uses those parsed status paths, so binary, mode, symlink, and submodule changes are not lost just because they have no text hunk. `NameStatusDigest` binds the exact status bytes. Do not parse paths from source-diff text as a substitute for the status stream.

The initial gate bounds are a 20 MiB source diff, 10,000 changed records, 4 KiB of UTF-8 bytes per path, and 120 seconds for the complete capture operation (`MaximumCaptureSeconds`). A separate bounded read protects the status stream before parsing. Exceeding a bound or receiving malformed, truncated, unsafe, or invalid-UTF-8 status data fails closed; callers must not fall back to an empty or text-hunk-only path list.

`EvidenceRevisionPlanBuilder.ResolveForPullRequest` emits contract version `2.0` and binds both revisions plus source/status digests to the normalized path selection and policy. Its `VerifyAsync` independently regenerates both Git representations and resolves the plan from a separately trusted policy. It rejects a substituted policy or plan but does not query GitHub or prove current-base/head freshness. The trusted controller must independently recheck the remote PR's current base and head immediately before issuing a gate verdict, as described in the [issue #777 gate design](../../docs/designs/issue-777-policy-driven-ci-evidence-gate.md).

## Trusted PR verdict boundary

For a nonempty profile, the manifest must declare `ValidatedNotAttested`, and the fresh authority snapshot must separately set `SubjectEnvelopeAttested` only after trusted isolation inspection. The manifest's enum is a structural claim, not proof of the runner envelope; a missing independent attestation returns `ASEVG007`. An empty profile instead requires `NotRequired`.

[`EvidencePullRequestGateVerifier.VerifyAsync`](EvidencePullRequestGateVerifier.cs) is the final library seam for a PR verdict. Supply the base-owned policy and trusted Git object store separately from the candidate v2 plan and manifest. `EvidencePullRequestGateExpectedIdentity` must come from controller-owned workflow context: repository, event, workflow and subject-job IDs, plus numeric repository, head-repository, PR, run and attempt IDs and target branch in `EvidencePullRequestRunIdentity`. Do not copy expected identity from the plan itself. The verifier recreates the plan from Git, checks the manifest and every selected obligation, then calls an `IEvidencePullRequestGateAuthorityProvider` to reread current PR base/head and subject-job provenance immediately before it returns. A missing, stale, fork, wrong-job, or observational result is ineligible.

For a nonempty profile, also pass a controller-owned extracted-artifact handoff root and an `IEvidencePullRequestGateArtifactVerifier`. The supplied [`EvidencePullRequestGateNoFollowArtifactVerifier`](EvidencePullRequestGateVerifier.cs) requires Linux x64 or arm64 with `openat2` and `statx`; it opens each declared file beneath that root without following links and verifies type, single-link identity, length, slot, and SHA-256 from the bytes read. Directory and no-follow open flags are selected for the running Linux architecture, so an arm64 runner uses the same fail-closed path checks as x64. It rejects missing, moved, duplicate or forged artifacts. A missing verifier or root returns `ASEVG009`; a byte mismatch returns `ASEVG010`. An explicit empty targeted profile has no artifact handoff to verify and may yield `NoEvidenceRequired` after the same revision and authority checks. The host's own success is not a substitute for this final verification.

`EvidencePullRequestGateVerificationResult` exposes a fixed diagnostic code and bounded `Summary` for terminal, GitHub summary, and machine rendering. The summary carries profile/rule rationale, short revisions, and selected, closed, and missing obligation IDs; it does not carry producer logs or arbitrary artifact contents. A calling CI command may issue a passing check only for `IsEligible == true` (`ASEVG000`). `ASEVG001`–`ASEVG008` cover invalid trusted identity, v2 plan or manifest mismatch, out-of-scope claim, unavailable or mismatched fresh authority, and cancellation. A verifier implementation alone does not provide a GitHub API authority provider or isolate the subject job; those remain requirements of the [AppSurface rollout](../../docs/evidence-gate-rollout.md).

## Policy design

Keep the policy small and explicit:

- map genuinely non-behavioral files to an empty `no-evidence` profile;
- map behavior-sensitive paths to producers that make a real assertion, such as coverage or browser E2E;
- choose a conservative non-empty fallback profile; and
- give every obligation one named risk rationale and assertion id.

The planner does not classify C# semantics or infer that a getter, constructor, or generated line is low value. That belongs in a future, separately versioned behavior classifier; v1 refuses to pretend an unimplemented heuristic is trustworthy.

## Failure and recovery

| Diagnostic | Meaning | Recovery |
| --- | --- | --- |
| `ASEVD105` | Conservative fallback points at an empty profile. | Choose a non-empty profile that genuinely mediates unknown changes. |
| `ASEVD117` | Same-precedence rules selected different profiles. | Add an explicit precedence or remove the overlap. |
| `ASEVD118` | A supplied changed path is not normalized. | Use a repository-relative forward-slash path without `.` or `..` segments. |
| `ASEVD121` | An identifier is empty or exceeds 128 characters. | Use a stable identifier up to 128 characters. |
| `ASEVD111`, `ASEVD124` | A producer or resource requires an undeclared resource. | Declare every required resource in the same profile. |
| `ASEVD128` | A hunked unified diff does not include Git file headers. | Supply a Git-formatted diff or explicit changed paths. |
| `ASEVD129` | A PR gate's conservative profile does not preserve a targeted profile requirement. | Add the required declaration to the conservative profile or use an explicit combined profile. |
| `ASEVD130`–`ASEVD136` | Exact Git revisions, object-store reads, bounded status parsing, or capture verification failed. | Fetch full commit objects into the trusted object store and recapture with the pinned Git runner; do not downgrade to path-only selection. |
| `ASEVD137` | Exact Git change capture exceeded its 120-second deadline. | Retry in the pinned runner or reduce the change before a reviewed deadline increase. |
| `ASEVD138` | A pull-request plan selected a release-only profile. | Map PR paths to a targeted profile and reserve release selection for the protected release event. |
| `ASEVD139` | A v2 plan did not match the trusted policy and exact Git commit pair. | Discard it and replan from the trusted base-owned policy and fetched objects. |

Read next: [contracts](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md), [CLI workflow](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md), and the [policy cookbook](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md).
