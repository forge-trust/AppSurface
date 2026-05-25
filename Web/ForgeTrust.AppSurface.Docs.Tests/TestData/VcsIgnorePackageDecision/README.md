# AppSurface Docs VCS Ignore Package Decision

Issue: https://github.com/forge-trust/AppSurface/issues/347

Evidence refreshed: 2026-05-24.

## Decision

Keep the AppSurface-owned VCS ignore policy as the production source-harvest boundary for now.

Do not add a runtime `.gitignore` parser package for #347. Package work may continue as a spike only after the structured Git oracle fixture matrix, AppSurface policy fixtures, cross-platform parity workflow, diagnostics docs, and performance baselines are in place. A package can be adopted later only if it preserves AppSurface's policy contract and lowers long-term risk without adding native assets, machine-local Git state, or dual-parser drift.

## Why Keep The Local Runtime Matcher

AppSurface Docs is not only asking "does this Git pattern ignore this path?" The source-harvest policy also owns:

- repository-owned `.gitignore` scope, excluding `.git/info/exclude` and global excludes
- snapshot-scoped lazy nested `.gitignore` loading
- source `.gitignore` path, line number, and raw pattern provenance
- AppSurface include/default-exclusion/configured-exclude precedence
- `VcsIgnore:AllowGlobs` restoration using AppSurface glob syntax
- safe pruning when ignored directories might contain reachable negations or allowed docs
- source-kind exclusion counts, capped samples, warnings, and client-safe health redaction

Most candidate packages can help with matching semantics, but none has yet proven this full policy boundary.

## Hard Gates For Runtime Adoption

Any follow-up adapter PR must pass all of these gates before adding a production package reference:

1. Current AppSurface VCS-ignore tests plus the structured Git oracle fixtures pass unchanged.
2. Linux, macOS, and Windows parity fixtures pass with the documented ordinal VCS-ignore case contract.
3. Diagnostics preserve decision code, source path, line number, raw pattern, samples, counts, warnings, and redaction.
4. AppSurface default exclusions and configured excludes still win after Git negation and `VcsIgnore:AllowGlobs`.
5. Safe pruning keeps ignored subtrees fast without hiding descendants reachable by negations or AppSurface allow globs.
6. Runtime and allocation cost stay within the accepted threshold on broad, deep, nested-ignore, broad-negation, broad-allow, and concurrent-harvester fixtures.
7. Supply-chain review accepts license, source availability, owner/maintenance posture, vulnerability/deprecation status, dependency graph, native assets, package size, and removal cost.
8. Any hybrid approach avoids dual-parser drift. If local parsing remains for provenance while a package matches paths, divergence tests are mandatory.

## Candidate Matrix

| Candidate | Current evidence | Runtime fit | Decision |
| --- | --- | --- | --- |
| `GitignoreParserNet` 0.2.0.15 | NuGet lists .NET 8.0 and .NET Standard 2.0 targets, Apache-2.0 license, dependency-free package shape, 17.5K total downloads, and last update 2025-11-15. | Best first spike candidate because it is managed and current enough to inspect. Provenance and prune-hint fit still unproven. | Spike only after harness gates. |
| `Ignore` 0.2.1 | NuGet lists .NET 8.0 and .NET Standard 2.0 targets, no dependencies, 503.6K total downloads, and last update 2024-07-18. It describes itself as a `.gitignore` parser according to the Git spec. | Healthy usage signal and managed package shape. Need API/provenance review and parity run before runtime consideration. | Compare, possible spike if `GitignoreParserNet` fails API fit. |
| `MAB.DotIgnore` 3.0.2 | NuGet lists .NET Standard 1.3 and .NET Framework 4.0 targets, no direct runtime dependency for those targets beyond old framework support, 223.6K total downloads, and last update 2020-10-18. | Older/stale baseline. Useful as a semantics comparison, but maintenance age is a supply-chain concern for a safety boundary. | Do not adopt unless newer candidates fail and review explicitly accepts age risk. |
| `LibGit2Sharp` 0.31.0 | NuGet lists .NET 8.0 and .NET Framework 4.7.2 targets, very broad usage, and a `LibGit2Sharp.NativeBinaries` dependency. | Good oracle/prototype aid for Git behavior, but native binaries and broader Git state make it wrong for AppSurface Docs runtime harvest policy. | Test/prototype oracle only; no runtime package adoption. |

Source references: [GitignoreParserNet](https://www.nuget.org/packages/GitignoreParserNet/), [Ignore](https://www.nuget.org/packages/Ignore/), [MAB.DotIgnore](https://www.nuget.org/packages/MAB.DotIgnore/), [LibGit2Sharp](https://www.nuget.org/packages/LibGit2Sharp/), [gitignore](https://git-scm.com/docs/gitignore), and [git-check-ignore](https://git-scm.com/docs/git-check-ignore).

## AppSurface Policy Divergence From Git

| Topic | Git behavior | AppSurface Docs behavior |
| --- | --- | --- |
| `.gitignore` files | Git reads `.gitignore` files, `.git/info/exclude`, and global excludes according to precedence. | Production harvest reads repository-owned `.gitignore` files under the source root only. |
| Tracked files | Git ignore rules do not affect already tracked files. `git check-ignore --no-index` is needed to debug tracked paths. | AppSurface Docs ignores matching source candidates regardless of whether Git tracks them, because the docs harvest is a publication boundary. |
| Case sensitivity | Git can be affected by platform and repository configuration such as `core.ignoreCase`. | AppSurface VCS-ignore matching is ordinal and reproducible; configured AppSurface globs remain case-insensitive. |
| Local developer state | Git can include local excludes. | Local excludes are intentionally ignored so CI, static export, packaged hosts, and developer machines agree. |
| Restoring ignored docs | Git negation can re-include paths only when excluded parent directories are not pruned. | Git negation is honored inside VCS-ignore semantics; `VcsIgnore:AllowGlobs` can also restore selected VCS-only exclusions. |
| Final AppSurface policy | Git does not know AppSurface include/default/configured-exclude rules. | Configured AppSurface excludes and default exclusions still win after VCS-ignore restoration. |

## Probe Commands

Focused parity and policy tests:

```bash
dotnet test Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj --filter AppSurfaceDocsHarvestVcsIgnorePolicyTests
```

Focused traversal and snapshot policy tests:

```bash
dotnet test Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj --filter AppSurfaceDocsHarvestPathPolicySnapshotTests
```

CI parity workflow:

```text
.github/workflows/vcs-ignore-parity.yml
```

## Structured Git Oracle Shape

The test oracle uses:

```bash
git -c core.excludesFile=<empty-file> -c core.ignoreCase=false \
  check-ignore --verbose --non-matching -z --stdin --no-index
```

The important pieces are:

- `--verbose` gives source, line, pattern, and path provenance.
- `--non-matching` makes nonignored paths explicit instead of silently absent.
- `-z --stdin` makes paths with spaces and punctuation parseable.
- `--no-index` keeps tracked-file divergence visible.
- `core.excludesFile=<empty-file>` and an empty `.git/info/exclude` isolate machine-local ignore state.
- `core.ignoreCase=false` pins the oracle to AppSurface's ordinal VCS-ignore case contract.

## Revisit Triggers

Reopen runtime package adoption only when:

- a candidate exposes enough API surface to preserve provenance without duplicating parser semantics, or
- the expanded fixtures reveal local matcher drift that a package handles cleanly, or
- maintenance cost of local matcher changes becomes higher than the package supply-chain and adapter cost.

Until then, package comparison improves confidence by hardening tests and docs, not by adding a dependency.
