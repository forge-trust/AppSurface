# Release contract maintainer notes

This file lives under `.github/` on purpose so RazorDocs does not publish it as public documentation.

## `no-unreleased-entry` label

Use `no-unreleased-entry` only when a pull request does not belong in the public release story, such as:

- repository administration
- workflow-only cleanup
- maintainer ergonomics that do not affect adopters

Do not use it for package behavior, CLI behavior, examples, docs-facing behavior, or release policy changes.

## Deferred automation

The current workflow establishes the contracts that future release automation will consume:

- Conventional Commits PR titles
- one public unreleased proof artifact
- tagged release notes plus a compact changelog
- verified prerelease `.nupkg` artifacts from the manifest-backed package artifact workflow
- packageable CLI artifacts such as `ForgeTrust.RazorWire.Cli`

Until that automation lands, package docs that show `dnx`, `dotnet tool execute`,
or `dotnet tool install` assume either a manually published package source or an
explicit local package source.

Tracked follow-up: #161, "Automate coordinated monorepo releases from the public release contract".

## Package artifact dry run

The pack-only release slice uses `packages/package-index.yml` as the single package
contract. Maintain `publish_decision`, `publish_reason`, and
`expected_dependency_package_ids` there instead of creating a second release package
list.

Run the package artifact verifier with an exact prerelease version:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify-packages \
  --package-version 0.0.0-ci.local \
  --artifacts-output artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --report artifacts/package-validation-report.md
```

The verifier restores and builds the solution once, packs manifest-selected packages
with `--no-restore --no-build`, inspects each `.nupkg`, and writes a markdown report.
It checks package metadata, expected same-version dependencies, first-party DLL
informational versions, and Tailwind runtime binary payloads. It intentionally
rejects stable versions and SemVer build metadata because this path is for
prerelease package identity that NuGet will preserve exactly.

Tracked follow-up for actual publishing: #253, "Add protected NuGet prerelease publish workflow after rename pass".

## Protected NuGet prerelease publish

The prerelease publish slice lives in `.github/workflows/nuget-prerelease-publish.yml`.
It has no `pull_request`, `pull_request_target`, or `workflow_dispatch` entry point.
It runs only when the main repository receives an annotated prerelease tag that
matches:

```text
v<major>.<minor>.<patch>-<preview|alpha|beta|rc>.<positive-number>
```

Examples: `v0.4.0-preview.1`, `v0.4.0-rc.2`.

The workflow resolves annotated tags to the tagged commit with
`refs/tags/<tag>^{commit}`, verifies that commit is reachable from `origin/main`,
and then checks that `build.yml` and `package-gate.yml` have successful completed
runs for that exact commit before any protected publish job can start.

Publishing is gated by the GitHub Environment named `nuget-prerelease`. Store the
NuGet API token only as that environment's `NUGET_API_KEY` secret. The publish job
passes the key through the environment variable consumed by the PackageIndex tool;
do not add a repository-level NuGet API secret and do not pass the key as an input
or workflow-dispatch parameter.

Before creating the first prerelease tag, create the `nuget-prerelease` environment
in repository settings with required reviewers and prevent self-review enabled.
Add `NUGET_API_KEY` only as an environment secret there. Verify that no repository
or organization `NUGET_API_KEY` secret exists, because a repo/org secret with the
same name would weaken the intended environment-only secret boundary. The workflow
fails closed if the environment is missing, has no required-reviewer protection, or
does not prevent self-review.

The PackageIndex tool owns the package contract for the workflow:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify-packages \
  --package-version 0.4.0-preview.1 \
  --artifacts-output artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --report artifacts/packages/package-validation-report.md
```

`verify-packages` writes both the markdown validation report and
`package-artifact-manifest.json`. The JSON manifest records the prerelease version,
manifest order, package id, project path, publish decision, artifact file name, tool
flag, and SHA-512 hash for every `publish` and `support_publish` package selected
from `packages/package-index.yml`.

The protected publish job downloads that exact artifact bundle and runs:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  publish-prerelease \
  --artifacts-input artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --publish-log artifacts/packages/package-publish-log.md
```

`publish-prerelease` re-reads `packages/package-index.yml`, verifies that the JSON
artifact manifest exactly matches the current package plan and SHA-512 hashes, then
runs `dotnet nuget push --skip-duplicate` in manifest order. Captured publish
output is redacted before it is written to the uploaded ledger artifact, and the
ledger is rewritten after every package attempt so partial-publish evidence survives
later command failures. The publish ledger uses these statuses:

- `pushed`: NuGet accepted the package.
- `duplicate-reported`: `--skip-duplicate` reported that the package already exists.
- `failed`: the package push failed.
- `skipped-after-failure`: the package was not attempted because an earlier package
  failed.

Partial publish recovery uses the same tag and the same package version. Re-run the
failed workflow after fixing transient NuGet or environment problems. Already
published packages should become `duplicate-reported`, and the first package that
previously failed should be the first new `pushed` entry. Do not retag or create a
new package version for a transient partial publish.

Content or metadata defects require a new prerelease version. Once any package has
been accepted by NuGet, treat the entire coordinated package version as immutable.
Fix the defect in source, create the next prerelease tag, and let the workflow
publish the next package family.

After publishing, the workflow runs `smoke-install` from a fresh NuGet configuration
that clears inherited sources and points only at nuget.org. It restores every direct
`publish` package from `packages/package-index.yml` with a shared fresh
`NUGET_PACKAGES` directory, isolated `DOTNET_CLI_HOME`, and retry/backoff for NuGet
indexing delay. Tool packages, when they become publishable, use an isolated
`dotnet tool install --tool-path` smoke path instead of a project restore.
