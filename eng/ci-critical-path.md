# AppSurface CI Critical Path

This note is the CI-000 baseline for build-time work. Historical timing samples below describe the runs taken at the dates shown; the current coverage decision path is governed by the shared [evidence policy](https://github.com/forge-trust/AppSurface/blob/main/.github/evidence.policy.json) and the tracked workflows.

## Current PR validation path

AppSurface currently runs these workflows for ordinary pull requests to `main`:

- `build.yml`: changed-path planning; when native coverage is selected, C# native coverage captures on Linux, Windows, and macOS followed by full-solution coverage and C# fan-in composition/verification; plus generated verification, web assets, Codecov reporting, and AppSurface Docs export. Test-only coverage selections run full-solution coverage with the direct patch gate and skip the native matrix.
- `package-gate.yml`: policy-scoped package checks, including isolated package validation when selected.
- `package-artifacts.yml`: package artifact verification and upload.
- `code-quality.yml`: locked restore, release build, generated verification, and `dotnet format --verify-no-changes`.
- `release-contract.yml`: PR title and unreleased-entry contract.
- `release-prep.yml`: release-tooling or release-artifact review only for release-related paths.
- `vcs-ignore-parity.yml`: docs VCS ignore parity tests only for docs/package-lock related paths.

GitHub returned `Branch not protected` for `main` on 2026-06-02, so there is no GitHub-enforced required-check set at this snapshot. The trusted PR decision path is therefore the practical all-green convention across the workflows above, not a branch-protection rule.

## Baseline timing sample

Recent PR runs from 2026-06-02 showed this shape:

- `build.yml` was the long tail at roughly 9-10 minutes end to end when docs export was included; the old serialized `Run tests` step was roughly 6m13s to 6m20s.
- `package-gate.yml`, `package-artifacts.yml`, and `code-quality.yml` generally completed in about 2-4 minutes.
- `release-contract.yml`, `release-prep.yml`, and `vcs-ignore-parity.yml` were short checks when they ran, but they still contribute to the all-green moment.
- The docs export job repeated generated package chooser, Markdown snippet, and web asset verification after `build` already completed those checks, costing roughly 41-55 seconds in the sampled runs.

Future CI performance changes should compare:

- p50 and p95 time from push to all trusted PR checks green.
- `build.yml` total time and test lane time.
- Codecov coverage upload and test-result upload success.
- Total runner minutes, because matrix fan-out can trade wall-clock time for capacity.
- Stale-run cancellation behavior for superseded commits on the same PR branch.

## v1 policy

- Keep all current PR workflows active.
- Keep coverage as PR confidence unless a measured follow-up proves Codecov coverage and test-result reporting still work under a different policy.
- Use `concurrency.cancel-in-progress` on long PR workflows so superseded commits stop consuming runner capacity.
- Keep full-solution coverage and its direct patch gate for every selected coverage profile. Select the three-OS native evidence matrix only when root policy selection includes coverage and at least one other producer; fan-in must use captures from the same workflow run and attempt. Preserve the direct gate's documented no-measurable-patch behavior and the native fan-in's strict nonempty-denominator contract.
- Keep the shared evidence policy as the source of truth for local and CI producer selection. The current `build.yml` coverage path uses the C# validation DLL and writes evidence under `TestResults/coverage-evidence/coverage/`, with `coverage-fanin/` (containing `primary/`, `native-*`, `composed/`, and `accepted/`) alongside it. The historical `coverage-merged/` layout remains relevant to the unconditional compatibility scripts, not this workflow path.

## NuGet cache policy

Issue #479 treats NuGet package caching as a measured CI hygiene change, not as the primary fix for the solution coverage long pole. Caching is enabled only for jobs that repeatedly restore the shared locked dependency graph:

- `build.yml` / `build`
- `build.yml` / `export-appsurface-docs`
- `code-quality.yml` / `dotnet-format`

Each cache-enabled job sets `NUGET_PACKAGES: ${{ github.workspace }}/.nuget/packages` at the job level before `actions/setup-dotnet`, then uses `setup-dotnet` with `cache: true` and cache dependency paths for both `**/packages.lock.json` and `**/packages.*.lock.json`. The broad lock-file paths are intentional for this first rollout because the selected solution-shaped jobs restore project graphs spread across the repository, including RID-specific AppHost lock files. A dependency or lock-file change anywhere in that graph can invalidate the cache; that is expected and safer than silently omitting a restored project from the cache key. If future timing data shows lock-file churn dominates cache misses, split the cache dependency paths by job/project scope in a follow-up.

Cache-enabled build jobs should restore explicitly before later .NET commands. The current `build.yml` coverage path resolves event-bound changed paths with the C# validation tool, then runs its `solution-coverage` producer for full-solution Release collection and the aggregate gate. `selection.json` exposes `requiresNativeCoverage`: source-code selections that include coverage and package checks request native evidence on PRs, while test-only coverage selections use the direct patch gate. PRs with native evidence capture the selected Linux, Windows, and macOS shards and use the [installed native fan-in commands](../guides/coverage-fan-in.md) for composition, acceptance and independent verification. Aggregate and applicable patch thresholds remain 95% line and 85% branch with 0.5 percentage-point tolerance; direct patch gates retain the CLI's documented no-measurable-points behavior, while native fan-in continues to require measurable patch denominators. Main-branch runs have no patch baseline. Existing ignored local artifacts may still affect package checks if they are in paths those checks inspect; ignore status is not a package-check skip. `scripts/coverage-solution.sh` and `scripts/coverage-gate-fanin.sh` remain unconditional compatibility entry points; the fan-in wrapper invokes the installed native CLI. The legacy Python fan-in producer and its mapped test suite are retired after matching native execution, acceptance and independent verification.

Package-sensitive workflows stay uncached unless a separate issue evaluates their trust boundary. In particular, `package-gate.yml` must keep its isolated `${{ runner.temp }}/nuget-packages` restore and `NuGet.package-gate.config` source policy. Publish, smoke-restore, trusted-publishing, and package validation workflows should not inherit the shared NuGet cache by convention.

## Tailwind provenance release path (issue #798)

The approved [artifact provenance contract](../docs/tailwind-artifact-provenance.md) defines the release dependency path for Tailwind packages; the tracked workflows remain executable authority. For a Tailwind release, validated source and package plan feed one `pack-and-verify` producer and immutable bundle. The five native jobs depend on that bundle and prove the actual restore/build inputs against it; aggregation validates exactly five current-invocation successes; publish preflight validates the original producer and aggregate before credentials, and the publisher revalidates before push. This serial dependency adds producer time to the native critical path. Keep unrelated release validation parallel.

The exact hosts remain `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64`; configured protected native runners must report actual OS/process architecture. A matrix member rerun alone creates an incomplete invocation. Rerun all five hosts together against the frozen producer; never combine receipts from separate attempts. Preserve #790 host CLI caching and its Windows Arm64 x64-under-emulation policy. See the [release operator recovery rules](https://github.com/forge-trust/AppSurface/blob/main/.github/release-ops.md#tailwind-artifact-provenance-issue-798).

Native proof uses fresh isolated NuGet caches, HTTP cache and CLI home, so shared package caches are not an optimization opportunity for this gate. It verifies raw restored `.nupkg` SHA-512 and extracted payload, then builds without restore and checks the existing CSS/cache/no-companion/no-native-output assertions. Cache or runner setup failures are proof failures, not permission to omit a host. Upload bounded diagnostics on success and failure without credentials, environment dumps or whole CLI homes. Keep candidate, host evidence, aggregate and start-receipt retention aligned to validated `github.retention_days`; report the earliest required-artifact expiry and 30-day/50-rerun platform limits. Do not treat artifact retention as extending workflow reruns.

Because five isolated native proofs now follow one pack, record the added release critical-path duration, host failure/rerun count, retained artifact bytes and cold/warm local-fixture timing for the first three candidate runs. These are measured acceptance observations, not grounds to skip a required stage or use a shared cache. A local typed proof is expressly local-only and does not reduce five-host release evidence.

## Tailwind host-scoped CLI in CI

The main Tailwind package has no native runtime-package dependency. A normal build
resolves exactly one standalone CLI for the machine running MSBuild and stores it in a
verified, version/RID-scoped cache. Do not set a consumer RuntimeIdentifier to select
the CLI: it describes the application target, not the build host.

| Workflow/job | Required Tailwind posture |
| --- | --- |
| `build.yml` / ordinary build | Leave Tailwind enabled when generated CSS is part of the project; it uses the host cache. |
| `build.yml` / coverage-security-platform | Keep `TailwindEnabled=false` because the test intentionally avoids generated CSS. Do not use the removed runtime-resolution switch. |
| `vcs-ignore-parity.yml` / compatibility smoke | The direct companion package may be built independently, but it is not evidence for the main-package consumer graph. |
| `package-gate.yml` and package artifacts | Keep the isolated NuGet cache; `verify-packages` inspects the main archive and runs the real packed-consumer proof. |
| `tailwind-native-host-evidence.yml` | Runs the exact five-host matrix with repository-configured protected runners and emits an aggregate, tag-bound evidence artifact. |
| release publishing | Require `verify-packages`, manifest byte-identity proof, packed-consumer proof, and the complete five-host evidence set. |

For a durable CI cache, restore the Tailwind cache before `dotnet build` and key it by
the manifest version and actual native host, for example
`appsurface-tailwind-4.1.18-linux-x64`. A fresh offline cache must fail before process
start; a prewarmed verified entry succeeds without network access. An explicit
`TailwindCliPath` is the documented alternative for air-gapped or custom-tool jobs.

The supported-native evidence set is Linux x64, Linux Arm64, macOS x64, macOS Arm64,
and Windows x64. Windows Arm64 uses the Windows x64 asset under emulation. CI may not
claim five-host coverage from a cross-RID run. If a hosted runner is unavailable for a
RID, use a trusted native self-hosted runner and persist the archive, nuspec, assets
file, restore binlog, cache identity, and output listing as release evidence.

For release proof, prefer the package artifact workflow over hand-packing:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify-packages \
  --package-version 0.0.0-ci.local \
  --artifacts-output artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --report artifacts/package-validation-report.md
```

Publishing calls `tailwind-native-host-evidence.yml` directly and cannot reach the
NuGet job until its aggregate evidence validates one successful packed consumer for
each of `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64`. Configure
the repository variable `TAILWIND_NATIVE_HOST_RUNNERS` before enabling that gate. It
must be a JSON object whose values are protected runner-label arrays, for example:

```json
{
  "linux-x64": ["self-hosted", "appsurface-tailwind-linux-x64"],
  "linux-arm64": ["self-hosted", "appsurface-tailwind-linux-arm64"],
  "osx-x64": ["self-hosted", "appsurface-tailwind-osx-x64"],
  "osx-arm64": ["self-hosted", "appsurface-tailwind-osx-arm64"],
  "win-x64": ["self-hosted", "appsurface-tailwind-win-x64"]
}
```

The gate fails closed when the variable is missing, a runner is unavailable, a host
reports a different RID, or a record cannot be bound to the exact tag commit, package
version, and release-manifest digest. Do not substitute cross-RID execution or reuse a
successful artifact from an earlier workflow run.

Every selected native runner must provide Bash, `jq`, and either `sha256sum` or
`shasum`. Treat a missing prerequisite as a host-configuration failure and install the
tool before enabling the runner label in `TAILWIND_NATIVE_HOST_RUNNERS`.
The native consumer proof must pass `restore` or `build` as the first `dotnet` argument;
a project path alone is interpreted as a command name. On Windows runners using Git
Bash, use the `-p:SourceRevisionId=...` MSBuild switch when stamping the verifier.
Git Bash rewrites the leading slash in `/p:...`, leaving MSBuild with an extra project
argument.
The checked-in Tailwind release manifest is pinned to LF by [`.gitattributes`](https://github.com/forge-trust/AppSurface/blob/main/.gitattributes),
including on Windows. Its packed copy must byte-match the source during native proof;
Git checkout conversion to CRLF would reject an otherwise unchanged producer.

Cache misses are normal after dependency updates, lock-file updates, or cache eviction. The cache is only useful if warm runs reduce selected workflow time or runner minutes without regressing the all-green decision path. For cache experiments, record at least:

| Run | Cache state | Setup .NET | Locked restore | Build/tests/docs | Workflow total | Notes |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Baseline after #476 | none | | | | | |
| First #479 run | miss/populate | | | | | |
| Warm #479 run | hit | | | | | |

Use the GitHub UI for action step duration and the job summary for cache-hit, package path, package folder size, and locked restore duration. Merge cache changes only when warm-cache evidence shows at least a 30 second net improvement in one selected workflow or at least a 5% total runner-minute/all-green improvement without regression. If the threshold is not met, remove the cache workflow changes and keep only the measurement notes.

Common restore failures:

- A locked restore failure usually means package references, central package versions, or RID-specific AppHost inputs changed without updating `packages.lock.json` or `packages.*.lock.json`. Run `dotnet restore --force-evaluate`, commit the updated lock files, and rerun CI.
- `NU1403` after enabling setup-dotnet caching can happen because the action restores only the global packages folder. If CI hits this, add or test `DisableImplicitNuGetFallbackFolder` centrally before widening the cache rollout.

Rollback is per job: remove `cache: true`, remove `cache-dependency-path`, and remove job-level `NUGET_PACKAGES` from the affected cache-enabled job. Leave package-gate's isolated temp `NUGET_PACKAGES` in place.
