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
  --report artifacts/package-validation-report.md
```

The verifier restores and builds the solution once, packs manifest-selected packages
with `--no-restore --no-build`, inspects each `.nupkg`, and writes a markdown report.
It checks package metadata, expected same-version dependencies, first-party DLL
informational versions, and Tailwind runtime binary payloads. It intentionally
rejects stable versions and SemVer build metadata because this path is for
prerelease package identity that NuGet will preserve exactly.

Tracked follow-up for actual publishing: #253, "Add protected NuGet prerelease publish workflow after rename pass".
