# Release contract maintainer notes

This file lives under `.github/` on purpose so AppSurface Docs does not publish it as public documentation.

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
- publishable .NET tool artifacts such as `ForgeTrust.AppSurface.Cli`

CLI docs that show `dnx`, `dotnet tool execute`, or `dotnet tool install` assume
either a NuGet-published prerelease package or an explicit local package source.
`ForgeTrust.AppSurface.Cli` is the public `appsurface` tool; RazorWire-specific
export workflows remain deferred to the separate `razorwire` tool.

Tracked follow-up: #161, "Automate coordinated monorepo releases from the public release contract".

## Package artifact dry run

The pack-only release slice uses `packages/package-index.yml` as the single package
contract. Maintain `publish_decision`, `publish_reason`, and
`expected_dependency_package_ids` there instead of creating a second release package
list. Review [`packages/readiness.md`](../packages/readiness.md) before release
review when deciding whether package-index evidence is complete enough for the
package artifact workflow. That dashboard is generated maintainer evidence from
the package index, not live NuGet publish, artifact, or smoke-install status.

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
informational versions, .NET tool command settings, and Tailwind runtime binary
payloads. It intentionally rejects stable versions and SemVer build metadata
because this path is for prerelease package identity that NuGet will preserve
exactly.

`verify-packages` is the primary package-proof path for Tailwind runtime packages.
It forces `TailwindRuntimeBinaryResolutionEnabled=true` during restore, build, and
pack so runtime `.nupkg` files cannot be created without their native Tailwind
binary payload. Do not set `TailwindRuntimeBinaryResolutionEnabled=false` for
release package validation; that switch is only for non-package CI restore,
build, and test jobs.

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

The workflow force-fetches the exact remote tag ref before validation, resolves
annotated tags to the tagged commit with `refs/tags/<tag>^{commit}`, verifies that
commit is reachable from `origin/main`, and then checks that `build.yml` and
`package-gate.yml` have successful completed runs for that exact commit before any
protected publish job can start.

Publishing is gated by the GitHub Environment named `nuget-prerelease` and by a
nuget.org Trusted Publishing policy. The publish job requests a short-lived NuGet
API key through GitHub Actions OIDC immediately before it runs
`publish-prerelease`; do not create or store a long-lived `NUGET_API_KEY` secret
at the repository, organization, or environment level.

Before creating the first prerelease tag, create the GitHub environments used by
the workflow. Create `nuget-prerelease` with required reviewers and prevent
self-review enabled. Create `nuget-prerelease-smoke` with a 25-minute wait timer
and no required reviewers so NuGet validation and indexing can settle before the
smoke-install runner starts. Add `NUGET_USER` as an environment or repository
variable containing the nuget.org profile name, not an email address. On
nuget.org, add a Trusted Publishing policy for the package owner with these
GitHub Actions details:

- Repository Owner: `forge-trust`
- Repository: `AppSurface`
- Workflow File: `nuget-prerelease-publish.yml`
- Environment: `nuget-prerelease`

NuGet's policy UI expects only the workflow file name, not the
`.github/workflows/` path. Keep the policy environment-scoped so a token exchange
is valid only after the GitHub environment approval gate has passed. The workflow
fails closed if `nuget-prerelease` is missing, has no required-reviewer
protection, or does not prevent self-review. It also fails closed if
`nuget-prerelease-smoke` is missing, lacks the 25-minute wait timer, or has
required reviewers configured.

The PackageIndex tool owns the package contract for the workflow:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify
```

`verify` confirms both the adopter-facing package chooser and the maintainer
readiness dashboard are current. The readiness dashboard checks package-index
evidence such as release metadata, docs links, packability, tool command shape,
and expected first-party package dependencies before publish artifacts are built.

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
flag, declared tool command name for .NET tools, and SHA-512 hash for every
`publish` and `support_publish` package selected from `packages/package-index.yml`.
The `tool_command_name` value must be one file-name-safe command token, not a
path: no whitespace, path separators, reserved `.`/`..` segments, control
characters, trailing periods, Windows reserved device names or dotted aliases,
or Windows-invalid file-name characters.

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
that clears inherited sources and points only at nuget.org. It restores all direct
non-tool `publish` packages from `packages/package-index.yml` in one aggregate
smoke project with a shared fresh `NUGET_PACKAGES` directory, isolated
`DOTNET_CLI_HOME`, and retry/backoff for NuGet indexing delay. This keeps the
post-publish check representative of user installs without multiplying restore
timeouts by the number of public library packages. Tool packages install with
`dotnet tool install --tool-path`, then the workflow resolves the declared
`tool_command_name` shim and runs `<command> --help`. The smoke job references
the `nuget-prerelease-smoke` environment, which must keep a 25-minute wait timer
so NuGet can finish validation and indexing before the runner starts. The job
sets a 70-minute timeout so the 25-minute environment wait still leaves up to
45 minutes for active smoke install work. The
smoke step fails if the installed command exits non-zero or if help output does
not identify the command users are expected to type.
