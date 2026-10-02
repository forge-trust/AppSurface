# ForgeTrust.AppSurface.Evidence.Cli

`ForgeTrust.AppSurface.Evidence.Cli` supplies the internal workflow used by the public `appsurface evidence` commands. Most adopters install the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md), not this package directly.

Start with the [EvidenceHost guide](../../start-here/evidencehost.md) and its [migration and support reference](../../docs/evidence/evidencehost-migration.md). The workflow reads only explicit policy and diff inputs; it does not scan consumer assemblies, discover tests, provision third-party services, or report outbound usage telemetry.

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
```

| Command | Behavior |
| --- | --- |
| `init --sample` | Creates a marked, non-overwriting policy, host skeleton, and local README. `--force` may replace only previously marked starter files. |
| `doctor` | Resolves policy and reports structural policy/diff and Docker/browser prerequisites without starting anything. Protected execution-envelope facts remain `unverified` for every profile; `GITHUB_ACTIONS=true` grants no admission. |
| `explain` | Writes the resolved plan and a human-readable planning summary without running producers. |
| `run` | Requires explicit `--mode trusted` or `--mode observation` and a `--control` socket from the independently armed Linux launcher. It runs only after protected admission. Trusted gate admission remains closed pending full consumer and production-broker CI proof. |
| `verify` | Recomputes plan/manifest structural binding and digests without running any producer or authenticating provenance. |

The quickstart above covers planning and structural verification. The `run` command requires a supported protected worker. Observation still requires an independent protected worker and a declared dependency-free profile. See the [canonical migration reference](../../docs/evidence/evidencehost-migration.md#api-migration) before changing an invocation.

Policy, plan and manifest JSON use the shared [bounded JSON reader](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input): at most 20 MiB of UTF-8 input per file, counted as bytes arrive before parsing. A file length check does not establish this bound. Duplicate or case-colliding properties, unknown or numeric enum values and unsupported plan/manifest versions are rejected; safe additive properties remain compatible. Unreadable, oversized or invalid policy input returns `ASEVD205`; equivalent generated input failures return `ASEVD209`. Diagnostics contain no JSON content or raw reader exception. Structural verification checks consistency and does not authenticate origin.

## Incomplete profiles are not complete evidence

An intentionally selected `no-evidence` profile may produce the structural `NoEvidenceRequired` claim. No Trusted claim is currently admitted for a protected PR or release gate on any provider or platform; see the [consumer acceptance record](../../docs/evidence/issue779-consumer-acceptance.md). A skipped test project, filtered test suite, unavailable browser, missing Docker runtime, or unsupported producer cannot produce complete evidence. A normal producer failure can yield an incomplete manifest; a fatal stop without confirmed owned-process exit yields no final manifest or summary and must be quarantined.

## Restricted coverage adapter and support boundary

The coverage adapter is implemented in the protected CLI path. It launches the fixed `dotnet test` command through the independent restricted broker, collects bounded broker-retained Cobertura reports only after the subject child and output pumps exit, validates them, merges them through the shared [`ForgeTrust.AppSurface.Evidence.Coverage`](../ForgeTrust.AppSurface.Evidence.Coverage/README.md) workflow, and evaluates the exact thresholds and optional patch rules from the resolved plan. It emits its declared coverage assertion only when the restricted procedure and numeric gate both pass; a successful test exit or log text alone is not evidence.

The protected merger loads `ReportGenerator.dll` from `reportgenerator/net10.0/` under the authenticated tool root, with no package-cache fallback. The AppSurface CLI tool-pack path includes the ReportGenerator payload; a plain `dotnet publish` output does not include it by default. If the protected tool root lacks the payload, the run fails with `ASEVD404`. Subject and reporter output share the run-wide process-output quota, while reports and emitted artifacts remain bounded by the artifact quota. See the [migration reference](../../docs/evidence/evidencehost-migration.md#restricted-coverage-path-and-packaging) for limits and recovery.

Implementation is not equivalent to an admitted supported gate. Linux x86_64 systemd/cgroup mechanism behavior was observed in CI on October 2, 2026, but the updated production broker and complete consumer integration have not been validated in CI. Windows and macOS have no accepted mechanism. Keep Trusted gate proof closed until the complete protected consumer and platform proof is accepted.

For supported numeric coverage enforcement today, use the separate [`appsurface coverage run` and `coverage gate`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) workflow; it does not depend on EvidenceHost admission. The [#815 migration guide](../../releases/issue-815-vstest-hang.md#migration-choices) explains timing limits in that workflow.

## Pitfalls

- Do not run a repository-wide gate after intentionally filtering out required tests and expect a complete claim.
- Do not treat `doctor`'s `ready_with_external_prerequisites` status as a pass; it describes planning prerequisites and grants no admission. The `trusted-envelope` check is `unverified`; `doctor` does not invoke the protected verifier. Consult the [consumer acceptance record](../../docs/evidence/issue779-consumer-acceptance.md); setting a CI environment flag cannot substitute for that proof.
- Do not set `--observation-only` on a job that must satisfy a PR or release gate.
- Do not hand-edit generated artifacts; use `verify`.

Read next: the [CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md), [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), and the [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md).

Input reader error categories are determined when opening the file: a missing file or directory reports `ASEVD204` for policy and `ASEVD208` for plan/manifest. Empty paths that reach the reader and paths rejected by the file API also report those codes with a generic message that omits the invalid path. Command-level argument validation runs first; for example, an empty `evidence verify` manifest argument reports `ASEVD212`. Access denial and other read failures report `ASEVD205` and `ASEVD209`, respectively; path existence metadata does not override the actual open result. See the [CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence) and [bounded input contract](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input).
