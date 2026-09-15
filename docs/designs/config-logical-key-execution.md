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
[normative matrix](https://github.com/forge-trust/AppSurface/blob/codex/config-logical-key-contract/tests/config-key-contract-matrix.json)
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
Core/Config outputs while another test build copied them. The current local runner
`python3 /tmp/config-validation-lock.py dotnet ...` serializes validation through an
exclusive file lock. Provider edits can proceed independently; a missing in-progress
helper or fixture is a build failure, never a test pass.

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
[clean packed consumer](https://github.com/forge-trust/AppSurface/blob/codex/config-logical-key-contract/tests/config-package-consumer/README.md); compatibility
fixtures; solution build and formatting; documentation checks; and
`./scripts/coverage-solution.sh`. The required kernel/parser/projection/index/codecs
reach 100% branches and changed provider orchestration exceeds 95%. All scoped review
findings are closed and affected gates have been rerun. No threshold or tolerance was
changed. The local implementation commit is followed by the committed patch gate
against the same collected coverage before the task is reported complete.
