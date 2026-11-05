# Verified cross-platform coverage in this repository

The repository's current coverage path is selected by the shared [evidence policy](https://github.com/forge-trust/AppSurface/blob/main/.github/evidence.policy.json) and executed by the C# [repository evidence validation tool](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md). The workflow's `coverage` scope projects the root policy selection to solution coverage. Test-only changes select only that producer and use its direct Codecov-mode patch gate. Source-code changes select coverage and package checks; on CI pull requests that producer union also requires native fan-in. Local `local` and `local-plan` support `all`, `native`, `coverage`, and `packages` scopes; a scoped run closes only its lane, and `all` is the complete local policy selection.

## Current CI path

In [build.yml](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/build.yml), the coverage planning job collects event-bound changed paths and resolves the policy. Its `selection.json` exposes `requiresNativeCoverage`, true when root selection includes `solution-coverage` and at least one other producer. Only that selection starts the Linux, Windows, and macOS security-platform jobs, downloads their captures, and enables PR fan-in. Each capture records its actual source, SDK, platform, test scope, coverage policy, report hashes, and workflow run identity. Test-only coverage skips this native matrix and runs the existing direct patch gate against the solution report.

The build job runs the full solution coverage producer through the validation DLL. It verifies the solution test-project inventory and timing records and evaluates the full-solution aggregate gate. When native evidence is selected, it captures the solution report and uses the public `EvidenceCoverageFanIn` API to compose and verify the three same-run native captures for the patch gate. Otherwise it evaluates the patch gate directly on the solution report. This path does not invoke Python. The previous `coverage-solution.sh`, `coverage_fanin.py`, and gate-helper scripts remain available as unconditional compatibility workflows; they are not the current `build.yml` evidence path.

Coverage acceptance preserves the repository's full-solution aggregate minimums of 95% line and 85% branch, plus the Codecov-mode patch minimums of 95% line and 85% branch where measurable. Both gates retain 0.5 percentage-point tolerance. As documented by the [CLI patch gate](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate), a patch metric with no measurable changed lines or branches reports 100%; this existing direct-gate behavior is used for test-only PRs. Native fan-in retains its strict nonempty measurable-denominator checks and does not weaken them. Main-branch runs have no patch baseline and therefore evaluate the aggregate gate without a patch gate. PRs selecting native evidence use the collected event base and require exactly one Linux, Windows, and macOS capture from the same workflow run and attempt. Native-only measurable coverage points may not be discarded from the composition.

The artifact layout under `TestResults/coverage-evidence/` is:

- `coverage/`: full-solution collector output, timing and test diagnostics, and aggregate/patch gate reports.
- `coverage-fanin/`: captured full-solution primary input, when native fan-in is selected.
- `coverage-fanin-composed/`: composed Cobertura report, primary-path report, and verification receipt, when native fan-in is selected.
- `evidence-manifest.json`, `evidence-summary.md`, and `artifacts/`: the evidence host's retained manifest and producer artifacts.

The workflow uploads these outputs as coverage diagnostics and gate reports. The full-solution aggregate gate remains independently required; the composed report's aggregate percentage is informational. Codecov receives the composed report on PRs selecting native coverage, otherwise the full-solution report. Main runs without a patch baseline also report the full-solution report.

## Local commands

Plan the selected evidence without running producers, then execute it on the current OS:

```sh
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- --operation local-plan
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- --operation local
```

Both commands compare local changes with `origin/main` by default. Add `--base <revision>` to select another base and `--scope all|native|coverage|packages` to select a lane. The `coverage` scope runs full-solution coverage against the local diff; local collection cannot substitute for any selected three-OS CI matrix. The local output defaults under ignored `TestResults/repository-evidence/`; see the tool guide for source-drift behavior, options, and evidence details.

## Evidence and troubleshooting

The C# [EvidenceCoverageFanIn API](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#verified-coverage-fan-in) verifies input manifests and hashes, source and SDK identity, test inventory, workflow provenance, report composition, and both gate outputs. Its `CaptureAsync`, `ComposeAsync`, and `VerifyAsync` methods are used by the repository validation workflow; they do not add a public command to replay the whole repository workflow. The validation tool's separate `capture-native` operation creates a native capture. For API contracts and integration details, see the [Evidence.Aspire guide](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#verified-coverage-fan-in).

When a coverage lane fails, download the `coverage-gate-reports` and `test-result-diagnostics` artifacts from the same workflow run. Inspect the full-solution timings and test logs under `coverage/`, the evidence summary and manifest, and, when selected, the fan-in verification receipt under `coverage-fanin-composed/`. Missing projects, required native captures, mismatched provenance or report hashes, incomplete measurable patch denominators in native fan-in, and failed applicable thresholds are hard failures. A direct patch metric with no measurable points follows the CLI's documented 100% behavior. Regenerate evidence from the current source and workflow run rather than relabeling old reports.

The historical compatibility scripts remain documented in their source and are useful for callers that explicitly continue to use them. Their artifact layout and Python test commands do not describe the current workflow path.
