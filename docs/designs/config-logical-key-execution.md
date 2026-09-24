# Logical-key contract implementation evidence

Approved source: [logical-key plan](config-logical-key-contract.md), commit `540b59f7`.
Execution branch: `codex/config-logical-key-contract`.

The final approval governs the complete train-1 provider implementation, coordinated
binary break, terminal collisions, automatic dot translation, terminal fallback
suppression with transactional child rescue, explicit Google legacy mappings,
`GetValue<T>`, and one LocalSecrets maintenance lease. The subsequent MakeItSo
invocation authorizes completing review, validation, and a draft pull request for
this scope. Package publication and merging the pull request remain outside this
execution.

## Implementation baseline — September 15

This evidence was captured immediately before the local implementation commit.
Implementation and scoped review are complete. The final solution run and the
working-implementation coverage gate both passed; the committed patch is checked
after the commit using the same coverage artifact. Historical focused passes below
identify their exact scope and are supplemented by the all-green final solution run. The
[normative matrix](https://github.com/forge-trust/AppSurface/blob/main/tests/config-key-contract-matrix.json)
records exact test methods and distinguishes complete assertion evidence from gaps.
Its latest bounded source review found all 47 rows covered, with 176 exact named-method
references verified. Source assertions
are not substitutes for executing the tests or resolving later review findings.
The final focused evidence join matches all 176 class/method references to passing
TRX results across Config, LocalSecrets, Google and CLI: all 47 rows have executed
named-method evidence, with no missing or non-passing methods.

| Area | Final evidence | Validation scope / limits |
| --- | --- | --- |
| Key/parser/projection/snapshot | Final unfiltered Config suite: 896 passed, zero warnings/errors; assembly 98.96% line / 96.44% branch; key/parser/projection/snapshot/declarations/catalog/metrics 100% branches | Included in the final all-green solution run |
| Manager | Final unfiltered Config suite: all 46/46 orchestration branches covered | Included in the final all-green solution run |
| Registry/wrappers | 139 passed, zero warnings; full branch coverage for registry, descriptors, startup validator and wrappers; nested translated-attribute provenance and 64-declaration concurrent completeness verified | Included in the final all-green solution run |
| Environment | 250 focused tests and final Config suite passed; final provider 240/244 branches (98.36%), codec/reverse claims/options 100%; all review fixes independently verified | Included in the final all-green solution run |
| File/audit | Audit 387 and subsequent file 336 focused tests passed, including 16 file review regressions; final Config capture: file 290/300 branches (96.67%), reporter 257/270 (95.19%), token/shared projection 100% | Included in the final all-green solution run |
| Audit/diff output | Final diff 54 passed and included in final Config suite; mixed visible/redacted/HMAC dictionary generations covered; all changed traversal branches and sequence points 100% | Included in the final all-green solution run |
| LocalSecrets | Final 631 passed, zero warnings/errors; provider/coordinator/exact decoder/three migration backends/journal helper 100% line and branch; including lease/durable replacement primitives 277/286 branches (96.85%); independent review clear | Included in the final all-green solution run |
| Google | Final 200 passed, zero warnings; provider 96.09% branches, codec/options/declaration/inventory 100%; independent review clear | Included in the final all-green solution run |
| CLI | Full pre-follow-up suite 1,434 passed; final migration subset 22 passed, zero warnings, including escaped/bounded output and selected-backend metadata | Included in the final all-green solution run |
| Docs | Full suite 2,973 passed, zero compiler warnings; final scoped formatting passed; 19 changed Markdown files, 478 local targets and 49 anchors checked with zero broken links/anchors | Included in the final all-green solution run |
| Visuals | Five release captures differ only in the new release-note TOC heading; final solution RazorWire integration suite passed in 204 seconds; six incidental home/search refreshes restored | Included in the final all-green solution run |
| Source consumer | Final post-format fresh build and strengthened proof passed 10/10; build plus proof about 5.2 seconds, proof execution 215 ms, zero compiler warnings/errors; source hashes stable | None; under the 120-second budget |
| Previous/candidate binaries | Core 168 passed; final old-plugin probe rejects before all three startup callbacks, four independently mixed-family probes reject, unguarded control throws the expected loader failure | Compatibility source unchanged since these proofs |
| Packed consumer | Final post-format fresh Core/Config/Testing pack and isolated consumer passed both runs, including two shared cases per run and IConfiguration coexistence; about 9.8 seconds, zero compiler warnings/errors; three AppSurface package dependencies and zero project dependencies; source hashes stable | Under the 300-second budget; Linux/Windows workflow configured but not run remotely |
| Full solution coverage | Actual unchanged script passed: 52 projects, 12,903 passed, zero failed/skipped, zero compiler/analyzer warnings/errors; 124,430/131,332 lines (94.7446%) and 39,930/45,117 branches (88.5032%) | Configured 95%/85%, existing tolerance 0.5 points, effective 94.5%/84.5%; no settings changed |
| Implementation patch | Working-diff gate passed in Codecov mode: 3,718/3,829 lines (97.1011%) and 3,668/3,772 branches (97.2428%); nonempty implementation diff, 28,014 changed lines | Same configured thresholds and existing tolerance; committed gate follows the local commit |

## Local validation procedure

Use `DOTNET_USE_POLLING_FILE_WATCHER=1` for sandboxed macOS validation: native file
watching previously stalled Generic Host construction, while the same consumer with
polling passed. This is a validation-process setting, not a library behavior change.
Use `NUGET_HTTP_CACHE_PATH=/tmp/config-final-http` for writable NuGet audit caching;
forced Core/Config restores cleared prior cached NU1900 warnings without disabling
audit or suppressing diagnostics.

Use `--disable-build-servers -p:UseSharedCompilation=false` for direct validation
commands. For the unchanged solution script, set `MSBUILDDISABLENODEREUSE=1`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0`, and `UseSharedCompilation=false` in its environment.
These avoid the observed shared compiler-server output access failure without
changing test selection, coverage thresholds, or the unrestricted-run guard.

Shared-project builds must run serially. Concurrent `Rebuild` previously removed
Core/Config outputs while another test build copied them. This evidence run used
the local, untracked `/tmp/config-validation-lock.py` wrapper to serialize validation
through an exclusive file lock; maintainers can run the same `dotnet` commands
serially without that wrapper. Provider edits can proceed independently; a missing
in-progress helper or fixture is a build failure, never a test pass.

The coverage CLI's `--diff-base` uses the committed merge-base-to-HEAD diff. Staging
alone does not include the implementation in that patch gate. Before committing,
also run the same 95% line / 85% branch patch thresholds against a zero-context
working-tree diff supplied through `--diff-file`, with new files staged so Git includes
them. After the local commit, rerun the committed patch gate against the same collected
coverage; do not describe a plan-only committed diff as implementation coverage.
The existing coverage CLI defaults to a 0.5-percentage-point tolerance. Thus the
unchanged configured 95% line / 85% branch thresholds evaluate against 94.5% / 84.5%,
for both aggregate and patch gates. Report the observed percentages and this existing
tolerance explicitly; do not describe a nominal-threshold comparison as the gate's
actual result or change the tolerance to obtain a pass.

Final solution and patch evidence:

- `/tmp/config-full-solution-final.log` and `/tmp/config-full-solution-final-report.json`: actual script exit 0, all 52 projects green, 672 seconds total, 14.79-second build, four-second merge, no slow-test diagnostic warnings.
- `TestResults/coverage-merged/coverage.cobertura.xml` and `timings.json`: merged coverage from all 52 projects, with one JUnit artifact per project.
- `/tmp/config-implementation-working-gate/coverage-gate.json`: unchanged gate settings and implementation patch results. Input diff SHA-256 `87875d228eb18a9d54e338688ee4d14d6f6793cd6aa914870eadd7b54d12778c`; 1,995,632 bytes.
- `/tmp/config-final-independent-verification.json`: independent Main verification of all JUnit results, configured/effective thresholds, tolerance, and source hashes.
- `/tmp/config-final-source-manifest.json`: 210 changed paths; production and tests remained unchanged during the final collection and working-diff gate. This final evidence-only ledger update follows collection.

Supporting local evidence:

- `/tmp/config-full-coordinated2.log`, `/tmp/config-full-coordinated2-report.md`, and
  `/tmp/config-full-coordinated2-results/config-full.trx`: final unfiltered Config
  capture, with all 115 source/project hashes stable during the run.
- `/tmp/config-google-review-verified.log` and `/tmp/config-google-alias-report.md`:
  final Google run and independent confirmation that both reviewed P2s are closed.
- `/tmp/local-sept15-final-approved.log`, `/tmp/local-sept15-final-approved/local.trx`,
  and `/tmp/local-sept15-matrix-evidence.json`: final LocalSecrets run, exact branch
  gaps, matrix methods and source hashes. The nine primitive branch gaps are native
  Windows/Unix failure and lease exception/disposal alternatives; generated P/Invoke
  coverage is recorded separately.
- `/tmp/config-final-suites-20260915-012702/cli.log` and `docs.log`.
- `/tmp/config-source-manager-notices-build.log` and `/tmp/config-source-manager-notices.log`.
- `/tmp/config-baseline-review-sept15/report.md` and `pixel-stats.json`.
- `/tmp/config-matrix-gaps.md`: assertion-level gaps and file ownership.
- `/tmp/config-matrix-executed-final-focused.json`: exact class/method joins for all
  176 references across the final focused TRX files, covering every matrix row.
- `/tmp/config-final-proofs-sept15/source/validation-summary.json` and
  `/tmp/config-final-proofs-sept15/packed/validation-summary.json`: final source and
  package-only proofs. The deliberately returned provider notice appears once per
  consumer run and is recorded separately from compiler warnings.
- `/tmp/config-final-doc-links-sept15.md`: final local documentation link/anchor check,
  excluding the approved plan baseline.
- `/tmp/config-full-solution-initial-failed`: preserved complete initial solution
  artifacts. Its only failures queried `IEnumerable<ConfigAuditKnownEntry>` directly
  after declarations had moved to the finalized registry; the repaired Docs test
  verifies all five keys, logical paths, declared types and missing states through
  `IConfigAuditReporter` in both parser modes.
- `/tmp/config-final-format-summary.md` and `/tmp/config-final-format-validation.json`:
  157 C# files and 10 project files checked; formatting-only changes in seven C# files
  and one project file, with XML content preserved and no-change verification green.

## Completion audit

Every matrix row has direct executed evidence, including registration/two-host
lifecycle, native codec properties, concurrency, terminal fallback suppression,
transactional rescue, file raw-token duplicates and shape replacement, LocalSecrets
transition/mutation/writer failure injection, Google claims/cache/cancellation/limits,
diagnostic trust/redaction, and previous/candidate package compatibility.

Validation includes the focused Config, LocalSecrets, Google and Config.Testing suites; the
[source proof](../../examples/config-key-contract/README.md); the
[clean packed consumer](https://github.com/forge-trust/AppSurface/blob/main/tests/config-package-consumer/README.md); compatibility
fixtures; solution build and formatting; documentation checks; and
`./scripts/coverage-solution.sh`. The required kernel/parser/projection/index/codecs
reach 100% branches and changed provider orchestration exceeds 95%. All scoped review
findings are closed and affected gates have been rerun. No threshold or tolerance was
changed. The local implementation commit is followed by the committed patch gate
against the same collected coverage before the task is reported complete.

## MakeItSo validation after merging main — September 15

This section supersedes the 52-project implementation baseline above. The final
collection validated clean commit `980ada8b74030f112cfe1ee1d060c3b7663b0b3c` after
merging `origin/main` at `e0618ac8dcc3b5903517e9a711f42959534b7fb5`. All 2,379
tracked/input hashes remained unchanged through the source, package, CLI,
compatibility, and full-solution checks. This ledger update records evidence only.

The unchanged [solution coverage script](https://github.com/forge-trust/AppSurface/blob/main/scripts/coverage-solution.sh) and
its committed patch gate both exited 0. The exact invocation was:

```sh
MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false BUILD_CONFIGURATION=Debug BUILD_NO_RESTORE=true COVERAGE_PARALLELISM=1 COVERAGE_GATE_DIFF_BASE=origin/main python3 /tmp/config-validation-lock.py bash scripts/coverage-solution.sh
```

The `/tmp/config-validation-lock.py` wrapper was local to this evidence run and is
not part of the repository. To reproduce the coverage check, run the linked
`scripts/coverage-solution.sh` serially with the same environment variables,
omitting `python3 /tmp/config-validation-lock.py` from the command above.

The run discovered 53 test projects and recorded **13,081 total tests: 13,080
passed, zero failures or errors, and one skipped**. Compiler/analyzer warnings and
errors were zero. The skipped
`PostgreSqlMixedVersionCompatibilityTests.ExactV020Preview8Package_OperatesAfterSchema10AndSupportsBinaryRollback`
is unchanged upstream and belongs to the explicit PostgreSQL release-proof lane
enabled by `APPSURFACE_REQUIRE_V020_RELEASE_PROOF=true`; this run did not opt in.
The solution build took 42.10 seconds, collection took 720 seconds, and the complete
evidence wrapper and gate took 761.6 seconds.

| Coverage | Covered / measurable | Observed |
| --- | --- | --- |
| Aggregate lines | 126,309 / 133,405 | 94.6809% |
| Aggregate branches | 40,414 / 45,660 | 88.5107% |
| Committed patch lines | 3,759 / 3,877 | 96.9564% |
| Committed patch branches | 3,690 / 3,816 | 96.6981% |

Both aggregate and patch gates retain configured 95% line / 85% branch thresholds,
the existing 0.5-percentage-point tolerance, and effective 94.5% / 84.5% thresholds.
Patch mode is Codecov against `origin/main`. No thresholds, exclusions, or test
selection were relaxed.

The key, parser, projection, provider result, environment snapshot, declaration
registry, manager, environment codec, claims, and file-token projection each reach
100% line and branch coverage. Provider branch coverage is Environment 261/266
(98.1203%), File 290/300 (96.6667%), Google 123/128 (96.0938%), and Local 58/58
(100%). `ConfigResolutionScope` remains separately measured at 23/24 branches
(95.8333%); `EnvironmentObjectClone` is 70/72 (97.2222%). These results do not imply
100% coverage for every configuration class.

Fresh public proofs passed: source 10/10; two isolated packed-consumer runs, each
with conformance 2/2 and `IConfiguration` coexistence; five isolated file-store CLI
flows; and ten compatibility scenarios using four freshly built candidate packages.
Current builds were warning-free. The packed consumers intentionally emitted two
provider notices in total. Historical compatibility packages produced one MSB3277
warning family and four missing-README advisories; these are distinct from current
candidate build diagnostics.

Release-page Playwright baseline regeneration and normal verification each passed
2/2; the full browser integration suite also passed. Strict CDN documentation
export exited 0 with 815 manifest entries and no RWEXPORT004 warnings. Four
source-only documentation links were repaired. Documentation health returned
HTTP 200 with the existing lossy-slug-normalization runtime warning.

Enhance completed in two cycles. Specialist, adversarial, red-team, and independent
fix review left no actionable scoped P1/P2 findings. Local migration now rejects
case-colliding destinations without deleting source data. Google convention
prefixes are explicit, and the ignored fail-open option was removed with migration
guidance. The plan audit verified all 47 matrix rows and 176 method references. A
separate bounded assertion audit traced 30 representative flows to 106 test methods
with no demonstrated gap; that source assessment is not instrumented coverage.

Final local artifacts:

- `/tmp/config-make-it-so-final-report.json` and `.md`, with preserved evidence in
  `/tmp/config-make-it-so-final-evidence`.
- `/tmp/config-make-it-so-full.log` and `/tmp/config-make-it-so-full-report.json`.
- `/tmp/config-make-it-so-independent-verification.json`: independent JUnit counts,
  sole-skip identity, coverage counters, hashes, and clean-tree verification.
- `/tmp/config-make-it-so-qa-report.json` and
  `/tmp/config-make-it-so-kernel-providers.json`.
- `/tmp/config-make-it-so-evidence-check.log`: exact-command freshness check passed.
- Merged coverage SHA-256:
  `8cf5e130ef2c63d39efb70bed38d05d4b7f354f418d3554b2b79ad6f45fa1a92`.
- Canonical source-map SHA-256:
  `71dc9a1d4e30726f6dc7807e0d54a8d4d3c4defceb35262b08627e9acd96654d`.

The original 52-project coverage remains preserved in
`/tmp/config-baseline-work/testresults-before.tar`. The final evidence-only commit
is followed by a committed-gate-only check against the same coverage. Package
publication, PR merge, credentialed Google service proof, and the separately
planned dotted-key analyzer remain outside this draft-PR workflow.
