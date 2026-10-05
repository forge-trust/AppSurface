# ForgeTrust.AppSurface.Evidence.Cli

`ForgeTrust.AppSurface.Evidence.Cli` supplies the internal workflow used by the public `appsurface evidence` commands. Most adopters install the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md), not this package directly.

Start with the [EvidenceHost guide](../../start-here/evidencehost.md). The workflow reads explicit policy and diff inputs; revision-bound mode also invokes fixed-options Git against the supplied object store. It does not scan consumer assemblies, discover tests, provision third-party services, or report outbound usage telemetry.

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
| `verify` | Reads at most 4 MiB per canonical plan or manifest, then recomputes binding and digests without running a producer. v2 also requires a separately trusted `--policy` and `--repository` to regenerate the exact Git change. |

Revision-bound `doctor`, `explain`, and `run` require `--base-revision`, `--head-revision`, and `--diff-file` together, with no `--path`. `--repository` points at the trusted object store and defaults to the current directory for these planning commands. `--gate-mode` rejects local path-only planning. A trusted controller may add `--pr-run-identity` with canonical PR/run identity JSON; it is not accepted for a local-only plan. A mismatch between the supplied source diff and the fixed-options Git diff fails with `ASEVD231`; partial input fails with `ASEVD230`; identity without revisions fails with `ASEVD233`. The [cookbook rehearsal](../../guides/evidencehost-cookbook.md#revision-bound-pr-rehearsal) shows a copyable command sequence. Built-in `run` treats v2 results as `ObservationOnly` because the local CLI cannot authenticate CI job provenance or subject isolation; gate mode exits nonzero on that observation. External gate authorization requires the [base-owned verifier](../../docs/designs/issue-777-policy-driven-ci-evidence-gate.md) to check current PR identity and execution provenance.

## Incomplete profiles are not complete evidence

An intentionally selected `no-evidence` profile may close a gate. A skipped test project, filtered test suite, unavailable browser, missing Docker runtime, or unsupported producer cannot. `run` returns a failing command and a manifest with `ClaimKind.None` for those cases, preserving the diagnostic and next action in `evidence-summary.json`.

The built-in coverage producer is an in-process adapter over the private [`ForgeTrust.AppSurface.Evidence.Coverage`](../ForgeTrust.AppSurface.Evidence.Coverage/README.md) engine shared with `appsurface coverage run` and `appsurface coverage gate`. Its policy declaration carries the exact overall and optional patch thresholds, tolerance, and patch-line mode; the resolved plan binds those values before collection begins. When a patch gate is selected, `evidence run` captures the bounded `--diff-file` bytes during planning and reuses that immutable snapshot for gate evaluation, so replacing the file mid-run cannot change the measured input. It does not silently convert a partial test selection into a full-profile claim.

Coverage execution uses the same fail-mode [watchdog and no-dump VSTest hang policy](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) as `coverage run`. Its `TimeoutSeconds` producer deadline includes discovery and build time and can further limit the automatic VSTest timer at each test launch. Evidence also fixes the no-progress watchdog at 10 minutes, which caps the automatic VSTest timeout at 9 minutes even when `TimeoutSeconds` is increased. Evidence has no watchdog or test-argument opt-out for disabling automatic blame; use `coverage run` when those controls are needed. An expired producer deadline maps to `TimedOut` without assertions. After a timeout, the Evidence result may point to `coverage/timings.json` when that owned artifact was written; it never copies test names or sequence XML into the result. The [#815 migration guide](../../releases/issue-815-vstest-hang.md#migration-choices) explains the timing limits and actor-specific outcomes.

## Pitfalls

- Do not run a repository-wide gate after intentionally filtering out required tests and expect a complete claim.
- Do not treat `doctor`'s `ready_with_external_prerequisites` status as a pass; it describes what the consumer CI image must provide.
- Do not set `--observation-only` on a job that must satisfy a PR or release gate.
- Do not hand-edit generated artifacts; use `verify`.

Read next: the [CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md), [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), and the [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md).
