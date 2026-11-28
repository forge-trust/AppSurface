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
| `ASEVD137` | A `test` producer has no `test` block or an empty `test.projects`. | Declare 1 to 32 projects in the [test declaration](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#test-producer-declaration-preview). |
| `ASEVD138` | A `test` producer declares more than 32 projects. | Split the projects across more than one `test` producer. |
| `ASEVD139` | A `test.projects` path is not a repository-relative forward-slash path of at most 512 characters ending in `.csproj`, `.fsproj` or `.vbproj`, or it has a `.`/`..` segment, a segment starting with `-`, a `:` or a control character. | Correct the path; it is passed to `dotnet` as an argument, so a leading `-` is refused. |
| `ASEVD140` | A `test` producer declares the same project path twice, compared without regard to case. | Declare each project once and combine filters with `\|` in one entry. |
| `ASEVD141` | A `filter` is blank, longer than 1024 characters, or holds a control character or line break. | Use a one-line VSTest filter, or omit `filter` to select every test. |
| `ASEVD142` | `minimumTests` is below 1. | Use 1 or greater, or omit it for the default of 1. |
| `ASEVD143` | A producer of another kind declares a `test` block. | Remove the block or set `kind` to `test`. |
| `ASEVD144` | A `test` producer declares `coverageGate`, `coverageExecution` or a non-default `coverageArtifactRetention`. | Remove the coverage fields; a `test` producer makes no coverage claim. Use kind `coverage` for coverage evidence. |
| `ASEVD145` | A `test` producer's `assertionIds` is not exactly `["appsurface/test/discovery-complete@1"]`. | Paste the array printed in the message. |
| `ASEVD146` | A `test` producer does not declare exactly one required `test-receipt` slot with media type `application/json` and `relativeRoot` `test`. | Paste the `artifactSlots` value printed in the message. |
| `ASEVD147` | The `test-receipt` slot's `maximumBytes` is below 4096 plus, per project, 512 plus six bytes per character of its path (the worst case of the receipt's JSON escaping). | Set `maximumBytes` to at least the minimum printed in the message. |
| `ASEVD148` | A `test` producer's `timeoutSeconds` is below 30. | Use 30 or more so the producer keeps time to write its receipt. |
| `ASEVD149` | A `test` producer id is not one safe path segment (`A-Z a-z 0-9 . _ -`, and not `.` or `..`). | Rename the producer; its id names the artifact directory. Other kinds keep the general identifier rule (`ASEVD121`). |
| `ASEVD150` | A producer of a kind other than `coverage` declares a `coverageScope` block. | Remove the block or set `kind` to `coverage`. |
| `ASEVD151` | A scoped coverage producer id is not one safe path segment. | Rename the producer; its id names the artifact and `coverage-scoped/<id>` directories. |
| `ASEVD152` | A producer declares both `coverageScope` and `coverageGate`. | Use two producers: `coverageGate` for the repository-wide claim, `coverageScope` for the scoped patch claim. |
| `ASEVD153` | `coverageScope.projects` is empty, has more than 32 entries, repeats a path (compared without case), or holds a path that `ASEVD139` would reject. | Declare 1 to 32 unique repository-relative test project paths. |
| `ASEVD154` | `coverageScope.sources` is empty, has more than 32 entries, repeats a pattern, or holds an invalid pattern or assembly name. | Use the producer `inputs` pattern grammar, at most 512 characters, and an assembly name of letters, digits, `.`, `_` and `-`, at most 256 characters. |
| `ASEVD155` | No patch threshold is declared, a threshold or the tolerance is outside 0 to 100, or `patchLineMode` is not `measurable` or `codecov`. | Declare `minPatchLinePercent`, `minPatchBranchPercent` or both. |
| `ASEVD156` | A scoped producer's `assertionIds` is not exactly `["appsurface/coverage/patch-scoped@1"]`. | Paste the array printed in the message. A scoped producer never closes a repository-wide coverage assertion. |
| `ASEVD157` | A producer without `coverageScope` declares `appsurface/coverage/patch-scoped@1`. | Add the `coverageScope` block or remove the assertion id. |
| `ASEVD158` | A scoped producer does not declare exactly one required `scoped-coverage-receipt` slot with media type `application/json`, `relativeRoot` `coverage-scope` and no `maximumFiles`. | Paste the `artifactSlots` value printed in the message. |
| `ASEVD159` | The receipt slot's `maximumBytes` is below the minimum derived from the declaration. | Set `maximumBytes` to at least the minimum printed in the message. |
| `ASEVD160` | A scoped producer is in a profile whose scope is not `Targeted`. | Move it to a `Targeted` profile; a release profile needs a repository-wide coverage producer. |
| `ASEVD161` | Two source entries with different assemblies can match the same path. | Give each assembly patterns under its own directory. |

### Test producer rules (preview)

`ASEVD137` through `ASEVD149` apply only to producers of kind `test`, except `ASEVD143`, which keeps a `test` block off every other kind. The planner checks them in this order and reports the first failure: `ASEVD143`, `ASEVD149`, `ASEVD137`, `ASEVD138`, then per project `ASEVD139` to `ASEVD142`, then `ASEVD144` to `ASEVD148`. The general producer rules (`ASEVD110`, `ASEVD121`, `ASEVD125`, `ASEVD126`) run first.

Why these rules exist: a `test` producer claims only that the tests it selected all ran and passed. `ASEVD144` and `ASEVD145` stop that claim from standing in for a coverage assertion, and `ASEVD146` and `ASEVD147` guarantee the producer can always write the receipt a gate reads. The kind, its assertion id and these codes are preview and may change without a compatibility step. Pitfall: an older planner ignores an unknown `test` block instead of rejecting it, so upgrade every CLI that plans or verifies before adding one to a shared policy.

### Scoped coverage diagnostics

`ASEVD150` through `ASEVD161` guard the [scoped patch coverage declaration](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#scoped-patch-coverage-declaration). The planner reports the first failure in this order: `ASEVD150`, `ASEVD157`, then for a producer with a `coverageScope` block `ASEVD151` to `ASEVD156`, `ASEVD158`, `ASEVD159`, `ASEVD160` and `ASEVD161`. The general producer rules and the test producer rules run first.

Why these rules exist: a scoped claim answers only for its declared sources. `ASEVD152`, `ASEVD156` and `ASEVD157` keep it from standing in for a repository-wide coverage assertion, so an obligation that names a scoped producer for a repository-wide assertion is rejected by `ASEVD115`. `ASEVD158` and `ASEVD159` guarantee the receipt can always be written. `ASEVD161` keeps every in-scope path tied to exactly one assembly.

`ASEVD161` is conservative. Two patterns are treated as overlapping unless their literal leading segments differ, so `src/A/*.cs` and `src/A/*.txt` with different assemblies are rejected even though they cannot match one path. Split such sources by directory.

Pitfall: the planner does not check routing. A rule broader than a scoped producer's sources can select it for changes it does not answer for; the producer then reports `NothingInScope` or counts the files as out of scope. See the [CLI pitfalls](../ForgeTrust.AppSurface.Evidence.Cli/README.md#scoped-coverage-pitfalls).

Read next: [contracts](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md), [CLI workflow](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md), and the [policy cookbook](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md).

## Declared producer inputs

Set the nullable init-only `EvidenceProducerDeclaration.Inputs` to `new EvidenceProducerInputs(include, exclude)` to declare complete reusable file inputs; use an empty exclude list when nothing is excluded. Omission leaves the producer ineligible for reuse. See the [reuse contract](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#local-producer-reuse-contract-candidate-11) for invocation opt-in, callback lifetime, claims and compatibility limits.

Patterns are repository-relative forward-slash paths: exact names, `*` within a segment, and `**` as a whole segment across zero or more segments. For example, `src/**/*.cs` includes C# files below `src`, and `src/**/Generated/**` can exclude generated trees. Includes must be nonempty; both lists must be non-null. Absolute/drive paths, backslashes, empty segments, `.`/`..`, control characters, embedded `**` and unsupported `?`, bracket or brace syntax fail planning with `ASEVD137`. Literal filename whitespace is preserved. No filesystem is consulted: a syntactically valid declaration may match no files, which preparation must reject later. Declare build inputs, configuration, tools and fixtures explicitly; matching source files alone does not establish a complete execution identity.

Any policy profile containing `Inputs`, an explicit artifact `MaximumFiles`, or `CooperativeChildLifetime=true` makes the resolved contract version `1.1`, even if that profile is unselected or execution will be fresh. Omission of all three preserves `1.0` bytes and digests. `MaximumFiles` must be omitted or 1 through 128; invalid values report `ASEVD126`. Slot byte limits are aggregate across files, and the producer-wide limit remains 256 MiB. See [multi-file slot semantics](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#multi-file-slots-and-versioning).

These additions remain candidates pending the [released-reader and artifact feasibility proofs](../../docs/designs/issue-917-producer-reuse.md#contract-and-compatibility); current-source validation does not freeze compatibility with released packages.
