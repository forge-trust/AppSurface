# Verified cross-platform coverage in this repository

AppSurface's repository validates its native filesystem code on Linux, Windows and macOS. A path implemented using Windows APIs needs actual Windows execution to establish coverage. The [coverage solution wrapper](https://github.com/forge-trust/AppSurface/blob/main/scripts/coverage-solution.sh) keeps the existing complete solution collection and 95% line / 85% branch aggregate gate. When CI supplies verified native evidence, a separate report extends that collection for the existing 95% line / 85% branch patch gate in Codecov mode. Both gates retain the existing 0.5 percentage-point tolerance. No test projects, source filters or instrumentation exclusions are removed.

This repository workflow is separate from the [consumer storage defaults](coverage-storage.md). Consumers get managed artifact retention by upgrading; they do not need to copy a platform matrix unless they have platform-specific code and want to combine its execution evidence.

## Producers and acceptance

The [build workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/build.yml) selects SDK **10.0.402** and Release configuration for the complete solution and each native shard. Each producer attests its actual selected SDK, Git commit and tree, source checkout root, platform, instrumentation policy, test scope, workflow run and attempt, and SHA-256 hashes of its report bytes. Evidence is published only after its test command succeeds. The complete solution evidence additionally includes the existing timing/project inventory; acceptance requires every solution test project exactly once, successful completion, a coverage artifact for each project, and successful merge.

The repository-only [fan-in script](https://github.com/forge-trust/AppSurface/blob/main/scripts/coverage_fanin.py) rejects a dirty tracked tree, untracked source files, mismatched commits, trees, SDKs, configurations, exclusions, test filters, hashes, scopes or workflow provenance. It requires exactly one native shard for each OS from the same workflow run and attempt as the full solution. These manifests describe a trusted CI producer's observations; they are not cryptographic signatures or permission to trust arbitrary downloaded reports.

Native collector reports pass through the same existing `appsurface coverage merge` normalization as the full solution. Each shard retains and hashes both the original collector bytes and the normalized report. Capture and acceptance verify that normalization keeps every original file/line point and observed hit, and does not reduce measurable branch totals. This aligns compiler-generated class identities without filtering native-only source points.

Files named by Cobertura are normalized using the producer's attested checkout prefix. Foreign absolute paths and parent traversal are rejected. Class and source line identities retain their package, class name and repository-relative path. Every primary class and method line record survives, and native-only measurable records are added. Methods retain their name and signature; overloads and repeated records within one observation remain distinct. The existing patch reader visits both method and class line collections and sums their branch counts, so dropping either collection would reduce its measurement scope. Root aggregate counters continue to describe class lines, as in the normalized producer report; they do not count method copies again.

Line hits combine actual execution. Partial branch observations are not added together: two 50% reports cannot establish 100%. A matching class or method line's branch counts are promoted only when another producer observed every branch with the same denominator. Otherwise its original branch counts remain. Distinct classes and method overloads sharing a source line remain distinct, matching the existing gate's condition-counting behavior. This conservative handling can undercount execution; add an honest test or investigate incompatible sequence points if a gate remains red.

The aggregate gate always evaluates the complete solution report with its original counts and records; only its filename prefixes are normalized for the current checkout. The original producer bytes are retained separately. The patch gate evaluates the composed report. Acceptance additionally compares their patch targets to ensure neither measurable line nor branch denominators shrink, requires the same Git diff and Codecov mode, and rejects an empty measurable patch. The canonical full solution report remains the input for the existing Codecov upload.

## Commands and evidence

The ordinary local command remains:

```sh
./scripts/coverage-solution.sh
```

It collects all tests and enforces aggregate and patch thresholds using local execution. CI sets `COVERAGE_FANIN_NATIVE` to the directory containing the three same-run attested native artifact directories. `BUILD_CONFIGURATION=Release` selects their matching configuration. The wrapper collects the full solution, captures `coverage-merged/fanin-primary`, composes the sibling `coverage-fanin` directory, and calls the [gate helper](https://github.com/forge-trust/AppSurface/blob/main/scripts/coverage-gate-fanin.sh). Keeping its input outside the gate's output preserves the existing output safety checks and coordinated report publication. The helper verifies inputs again immediately before evaluation and compares the regenerated bytes to the published composition.

After downloading the complete attested primary, composed report and all three native artifacts from one successful workflow run, the same checks can be repeated locally from its exact clean source commit using its exact SDK:

```sh
BUILD_CONFIGURATION=Release \
COVERAGE_DIRECTORY=/path/to/downloaded/coverage-merged \
COVERAGE_FANIN_NATIVE=/path/to/downloaded/native-inputs \
COVERAGE_GATE_DIFF_BASE=origin/main \
./scripts/coverage-gate-fanin.sh
```

This helper re-evaluates evidence; it does not rerun tests. The primary timing inventory, input manifests and hashes identify what actually ran. A source or SDK mismatch requires new producer runs rather than relabeling old XML. A different Git base intentionally evaluates a different patch and is recorded by the gate.

Before composing at its default location, the wrapper retires a previous composition only when its three known files match their saved hashes and policy. Unknown files, changed reports and links are preserved and cause a failure. Cleanup retains no-follow directory handles so a parent replacement cannot redirect its deletions; a host lacking these filesystem capabilities preserves existing output and fails rather than using an unsafe fallback. An explicit `COVERAGE_FANIN_REPORT` destination must be fresh and is never prepared automatically. This bounds repeated default runs without deleting caller-owned evidence.

Repeated collection also replaces a prior `coverage-merged/fanin-primary` capture only after validating the new producer's source, report and complete timing inventory. The old directory must contain exactly `manifest.json`, `coverage.cobertura.xml` and `timings.json`, with the solution scope, supported policy and matching saved file hashes. Unknown, modified or linked material prevents replacement. The same retained directory-handle protections apply through every parent. Previous capture identity authorizes only retirement of those known bytes; acceptance still requires fresh matching-source evidence. Custom primary and native capture destinations remain fresh-only.

`coverage-merged/coverage-gate.json` and patch targets describe the composed patch gate. `coverage-merged/aggregate-gate/coverage-gate.json` describes the original full solution aggregate gate and its patch denominator reference. `coverage-fanin/verification.json` records input manifests, hashes, retained point counts and the composed report hash. `COVERAGE_FANIN_REPORT` can select another fresh composition directory outside `COVERAGE_DIRECTORY`; its default is the `coverage-fanin` sibling. Keep that sibling layout when downloading artifacts for local replay. Read both gate reports when assessing acceptance; the composed report's aggregate percentage is informational and does not replace the full solution aggregate requirement. All are uploaded in the existing coverage artifact with bounded CI retention.

## Updating or troubleshooting

Update the exact SDK in the workflow for every producer together. A manifest records the SDK actually selected, so installation alone cannot silently accept another preinstalled SDK. The workflow selects its SDK through a `global.json` outside the checkout to keep the source tree clean.

Keep the native filter and instrumentation flags in the workflow aligned with the policy constants in the fan-in script. Run `python3 -m unittest discover -s scripts/tests -p 'test_coverage_fanin.py'` and the [script contracts](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli.Tests/CoverageSolutionScriptTests.cs) after changes. Missing native artifacts, checksum differences, incomplete primary scope or zero patch denominators are hard failures. Never reuse historical artifacts under a new manifest to resolve one.
