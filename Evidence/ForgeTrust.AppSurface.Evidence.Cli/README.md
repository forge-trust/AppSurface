# ForgeTrust.AppSurface.Evidence.Cli

`ForgeTrust.AppSurface.Evidence.Cli` supplies the internal workflow used by the public `appsurface evidence` commands. Most adopters install the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md), not this package directly.

Start with the [EvidenceHost guide](../../start-here/evidencehost.md). The workflow reads only explicit policy and diff inputs; it does not scan consumer assemblies, discover tests, provision third-party services, or report outbound usage telemetry.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Command workflow

```bash
appsurface evidence init --sample
appsurface evidence doctor --path src/Orders/SubmitOrder.cs
appsurface evidence explain --path src/Orders/SubmitOrder.cs
appsurface evidence run --diff-file artifacts/changed.patch --solution App.slnx
appsurface evidence verify TestResults/evidence/evidence-manifest.json
```

| Command | Behavior |
| --- | --- |
| `init --sample` | Creates a marked, non-overwriting policy, host skeleton, and local README. `--force` may replace only previously marked starter files. |
| `doctor` | Resolves policy and reports policy, diff, Docker, browser, and release-envelope prerequisites without starting anything. |
| `explain` | Writes the resolved plan and a human-readable summary without running producers. |
| `run` | Runs selected built-in coverage evidence, writes a canonical plan/manifest/summary, and writes a GitHub step summary when available. Consumer-owned browser/E2E or resource-backed producers run through the separate Aspire EvidenceHost package. |
| `verify` | Recomputes binding and digest verification without running any producer. |

## Incomplete profiles are not complete evidence

An intentionally selected `no-evidence` profile may close a gate. A skipped test project, filtered test suite, unavailable browser, missing Docker runtime, or unsupported producer cannot. `run` returns a failing command and a manifest with `ClaimKind.None` for those cases, preserving the diagnostic and next action in `evidence-summary.json`.

The built-in coverage producer is an in-process bridge to the existing `appsurface coverage run` and `appsurface coverage gate` workflows. Its policy declaration carries the exact overall and optional patch thresholds, tolerance, and patch-line mode; the resolved plan binds those values before collection begins. It does not silently convert a partial test selection into a full-profile claim.

## Pitfalls

- Do not run a repository-wide gate after intentionally filtering out required tests and expect a complete claim.
- Do not treat `doctor`'s `ready_with_external_prerequisites` status as a pass; it describes what the consumer CI image must provide.
- Do not set `--observation-only` on a job that must satisfy a PR or release gate.
- Do not hand-edit generated artifacts; use `verify`.

Read next: the [CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md), [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), and the [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md).
