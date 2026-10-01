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
| `doctor` | Resolves policy and reports structural policy/diff and Docker/browser prerequisites without starting anything. Protected execution-envelope facts remain `unverified` for every profile, including when `GITHUB_ACTIONS=true`. |
| `explain` | Writes the resolved plan and a human-readable summary without running producers. |
| `run` | Runs selected built-in coverage evidence, writes a canonical plan/manifest/summary, and writes a GitHub step summary when available. Consumer-owned browser/E2E or resource-backed producers run through the separate Aspire EvidenceHost package. |
| `verify` | Recomputes binding and digest verification without running any producer. |

Policy, plan and manifest JSON use the shared [bounded JSON reader](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input): at most 20 MiB of UTF-8 input per file, counted as bytes arrive before parsing. A file length check does not establish this bound. Duplicate or case-colliding properties, unknown or numeric enum values and unsupported plan/manifest versions are rejected; safe additive properties remain compatible. Unreadable, oversized or invalid policy input returns `ASEVD205`; equivalent generated input failures return `ASEVD209`. Diagnostics contain no JSON content or raw reader exception. Structural verification checks consistency and does not authenticate origin.

## Incomplete profiles are not complete evidence

An intentionally selected `no-evidence` profile may close a gate. A skipped test project, filtered test suite, unavailable browser, missing Docker runtime, or unsupported producer cannot. `run` returns a failing command and a manifest with `ClaimKind.None` for those cases, preserving the diagnostic and next action in `evidence-summary.json`.

The built-in coverage producer is an in-process adapter over the private [`ForgeTrust.AppSurface.Evidence.Coverage`](../ForgeTrust.AppSurface.Evidence.Coverage/README.md) engine shared with `appsurface coverage run` and `appsurface coverage gate`. Its policy declaration carries the exact overall and optional patch thresholds, tolerance, and patch-line mode; the resolved plan binds those values before collection begins. When a patch gate is selected, `evidence run` captures the bounded `--diff-file` bytes during planning and reuses that immutable snapshot for gate evaluation, so replacing the file mid-run cannot change the measured input. It does not silently convert a partial test selection into a full-profile claim.

Coverage execution uses the same fail-mode [watchdog and no-dump VSTest hang policy](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) as `coverage run`. Its `TimeoutSeconds` producer deadline includes discovery and build time and can further limit the automatic VSTest timer at each test launch. Evidence also fixes the no-progress watchdog at 10 minutes, which caps the automatic VSTest timeout at 9 minutes even when `TimeoutSeconds` is increased. Evidence has no watchdog or test-argument opt-out for disabling automatic blame; use `coverage run` when those controls are needed. An expired producer deadline maps to `TimedOut` without assertions. After a timeout, the Evidence result may point to `coverage/timings.json` when that owned artifact was written; it never copies test names or sequence XML into the result. The [#815 migration guide](../../releases/issue-815-vstest-hang.md#migration-choices) explains the timing limits and actor-specific outcomes.

## Pitfalls

- Do not run a repository-wide gate after intentionally filtering out required tests and expect a complete claim.
- Do not treat `doctor`'s `ready_with_external_prerequisites` status as a pass; it describes prerequisites and grants no admission. The `trusted-envelope` check stays `unverified` until the registered verifier runs under an independently armed supervisor. Consult the [consumer acceptance record](../../docs/evidence/issue779-consumer-acceptance.md); setting a CI environment flag cannot substitute for that proof.
- Do not set `--observation-only` on a job that must satisfy a PR or release gate.
- Do not hand-edit generated artifacts; use `verify`.

Read next: the [CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md), [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), and the [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md).

Input reader error categories are determined when opening the file: a missing file or directory reports `ASEVD204` for policy and `ASEVD208` for plan/manifest. Empty paths that reach the reader and paths rejected by the file API also report those codes with a generic message that omits the invalid path. Command-level argument validation runs first; for example, an empty `evidence verify` manifest argument reports `ASEVD212`. Access denial and other read failures report `ASEVD205` and `ASEVD209`, respectively; path existence metadata does not override the actual open result. See the [CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence) and [bounded input contract](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input).
