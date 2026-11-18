# ForgeTrust.AppSurface.Evidence.Planner

`ForgeTrust.AppSurface.Evidence.Planner` turns an explicit diff and a checked-in `EvidencePolicy` into one deterministic `EvidencePlan`. It is the boundary that makes a coverage or E2E gate explainable before work begins.

Begin with the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md). This package owns planning only: it does not invoke Git, start Aspire, run tests, create containers, or decide that missing evidence is acceptable.

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

For the [Coverage EvidenceHost producer](../ForgeTrust.AppSurface.Evidence.Aspire/README.md#coverage-fan-in-producer), use `ResolvePatch(policy, patchBytes, additionalPaths)` with the exact UTF-8 patch captured for planning (up to 20 MiB). The planner copies the bytes, strictly decodes the entire snapshot before parsing paths or selecting policy, and rejects malformed UTF-8 with `ASEVD136`. Valid Unicode, including a literal U+FFFD character, remains valid; the patch SHA-256 is computed from the exact copied bytes and recorded separately from the normalized path digest. The Evidence CLI binds the snapshot automatically when you pass `--diff-file`. A path-only plan still resolves normally but cannot close a patch coverage obligation with this producer.

Strict decoding includes hunk content, not only file headers. A text patch containing legacy-encoded source or fixture bytes fails with `ASEVD136` even when its paths are valid UTF-8. Establish the repository's source/fixture encoding policy before generating the planning patch. Do not transcode, truncate or omit bytes after capturing its identity, or mark changed source as binary merely to bypass a coverage obligation.

`EvidenceUnifiedDiffReader.Read(string)` receives already-decoded text and cannot detect earlier lossy decoding. Callers using it with `Resolve` must decode their original bytes strictly themselves; prefer `ResolvePatch` when byte identity and patch coverage matter. A legitimate U+FFFD character must not be rejected as a substitute for validating the original encoding.

Generate the planning patch from the repository root with the same path prefixes and options as the coverage gate. Use the same `DIFF_BASE` in the acceptance policy:

```sh
git --no-pager --no-replace-objects diff --unified=0 --no-ext-diff --no-textconv --src-prefix=a/ --dst-prefix=b/ --relative "${DIFF_BASE}...HEAD" -- > "$PATCH_FILE"
```

Choose a patch output path outside the checkout so the source remains clean. Pass the file's exact bytes to `ResolvePatch` or `--diff-file`.

## Canonical v3 ResolvePatch recipe

Use this recipe only for a v3 report-based patch obligation. The shell example above is the existing planner workflow; it does not implement the stricter `appsurface-git-patch-v1` policy. Follow the [canonical v3 Git policy, environment, caps and recovery rules](../../guides/coverage-fan-in.md#deterministic-git-policy-appsurface-git-patch-v1). The trusted adapter resolves the requested base once to a full lowercase commit ID, confirms source/head identity and complete local history, and requires `merge-base --all` to return exactly one base. Git 2.46 or later is required. No fetch, rebase, shell interpolation, alternate diff, or post-capture byte normalization is allowed.

The patch command is an argument vector run from the validated repository root with the exact isolated environment and common Git prefix in the guide. Its fixed diff arguments are:

```text
diff --no-color --no-ext-diff --no-textconv --no-relative
--no-renames --find-renames=50% --no-rename-empty -l1000
--diff-algorithm=myers --no-indent-heuristic
--ignore-submodules=none --submodule=short
--patch --unified=0 --inter-hunk-context=0 --full-index
--src-prefix=a/ --dst-prefix=b/
--output-indicator-new=+ --output-indicator-old=-
--output-indicator-context=<one literal space>
<merge-base> <head> --
```

The last option's value is one literal space in one argv item. Retain the exact stdout bytes (including any final newline), enforce the 20 MiB patch cap, and pass that unchanged snapshot to `ResolvePatch`. Strict UTF-8 decoding and exact-byte SHA-256 binding happen inside the planner:

```csharp
var plan = planner.ResolvePatch(policy, patchBytes, additionalPaths);
var expectedPatchSha256 = plan.PatchSha256;
```

Pass `plan.PatchSha256` as the v3 Coverage request's `ExpectedPatchSha256`; the Evidence coverage producer obtains that value from the selected plan. The acceptance request also pins `ContractVersion = 3` and `ReportProfileId = "coverlet-collector-cobertura-v1"`. Use fresh homogeneous v3 captures and keep the complete four-metric applicability result with the expected contract version. Valid N/A is accepted by v3 without another flag.

`ResolvePatch` rejects an empty patch or a patch with no normalized changed path (`ASEVD101`); the path-only `Resolve` overload likewise cannot bind patch bytes. Exact-empty v3 diff acceptance is a separate Coverage API case that requires the SHA-256 of the exact empty byte sequence and independently verified empty Git metadata. The Evidence planner does not manufacture an obligation for that case: do not add a synthetic path or edit a digest to make one.

Set `EvidencePolicy.CombineProfiles = true` to union distinct profiles selected by matched rules. The planner sorts source profile ids, declaration ids, paths, and matched rule ids before producing a stable `combined-<digest>` profile. Repeated resource, producer, or obligation ids must have identical canonical declarations; a conflict fails closed with `ASEVD133`. Profiles must share one scope or resolution fails with `ASEVD134`. An empty profile contributes no declarations. Paths with no matching rule still select the configured conservative profile. With the default `false`, any multi-profile selection keeps the existing conservative fallback behavior. The option is serialized only when true, preserving the canonical bytes of existing policies.

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
| `ASEVD133` | Selected profiles declare conflicting values for one resource, producer, or obligation id. | Make same-id declarations identical or keep the profiles separate. |
| `ASEVD134` | Selected profiles have different scopes. | Select profiles with a shared scope or disable profile combination. |
| `ASEVD118` | A supplied changed path is not normalized. | Use a repository-relative forward-slash path without `.` or `..` segments. |
| `ASEVD121` | An identifier is empty or exceeds 128 characters. | Use a stable identifier up to 128 characters. |
| `ASEVD111`, `ASEVD124` | A producer or resource requires an undeclared resource. | Declare every required resource in the same profile. |
| `ASEVD128` | A hunked unified diff does not include Git file headers. | Supply a Git-formatted diff or explicit changed paths. |
| `ASEVD129` | A coverage execution configuration is empty, longer than 32 characters, or contains unsupported characters. | Use letters, digits, `.`, `-`, or `_` in a simple configuration name. |
| `ASEVD130` | Coverage execution parallelism is outside 1 through 32. | Choose a bounded project concurrency from 1 through 32. |
| `ASEVD131` | A coverage execution test-result format is unsupported. | Select `none` or `junit` in the [coverage declaration](../ForgeTrust.AppSurface.Evidence.Contracts/README.md). |
| `ASEVD132` | A coverage producer has an unsupported `coverageArtifactRetention` value. | Use `managed` (the default) or `full`; this setting does not change coverage gate requirements. |
| `ASEVD135` | A Git patch path header is empty or contains invalid quoting or Git path escapes. | Supply an unmodified Git unified patch with UTF-8 paths. |
| `ASEVD136` | The exact patch snapshot contains malformed UTF-8 anywhere in its bytes. | Supply a valid UTF-8 unified patch; the planner rejects it before policy selection. |

Read next: [contracts](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md), [CLI workflow](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md), and the [policy cookbook](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md).
