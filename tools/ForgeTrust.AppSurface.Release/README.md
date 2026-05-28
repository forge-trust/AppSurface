# AppSurface Release Tool

`./eng/release` is the repository-owned release cockpit for coordinated AppSurface releases. It prepares the release pull request from the living unreleased note, validates the tagged release state, and emits structured data for GitHub Actions.

## Quickstart

```bash
./eng/release check --version 0.1.0-preview.1
./eng/release prepare --version 0.1.0-preview.1 --dry-run
./eng/release prepare --version 0.1.0-preview.1 --date 2026-05-25
./eng/release publish --version 0.1.0-preview.1 --tag v0.1.0-preview.1 --dry-run
```

Use `--version` without a leading `v`. Use `--tag v<version>` only for `publish`, where the tag must already exist and must be annotated.

## Check

`check` validates the release inputs without mutating the repository. It verifies required release files, target versioned artifacts, package publishing policy, package manifest shape, and warning IDs that the review workflow can enforce with `--fail-on-warnings`. The release-prep review workflow also passes `--allow-existing-targets` because it reviews the versioned artifacts that the preparation workflow intentionally generated.

`--fail-on-warnings` and `--allow-existing-targets` are intentionally check-only options. `prepare` and `publish` reject them so a maintainer does not think warning policy or target-collision policy changed for a mutating or publishing command.

## Prepare

`prepare` creates the release PR payload:

- `releases/v{version}.md`
- `releases/v{version}.md.yml`
- `releases/v{version}.release.json`
- `CHANGELOG.md` rollover entries
- `packages/package-index.yml` release note paths for every `classification: public` plus `publish_decision: publish` package
- reset `releases/unreleased.md` and `releases/unreleased.md.yml`

`--dry-run` prints the readiness report and planned file list without changing repository files. `--date` is parsed as invariant `YYYY-MM-DD`; malformed sidecar YAML fails with the standard diagnostic envelope instead of a raw parser exception.

## Publish

`publish` is create-only. It validates that the supplied tag is annotated, resolves to a commit reachable from `origin/main`, has a successful prerelease NuGet publish run for prerelease tags, has no existing GitHub Release, and contains the versioned release note at the tag commit. The command writes `version`, `tag`, `tag_commit`, `note_path`, `notes_file`, `release_classification`, and `prerelease` outputs when `--github-output` is supplied. `--github-output` must be a file path, not a root directory; in GitHub Actions, pass the `GITHUB_OUTPUT` file supplied by the runner.

## Stable Release Policy

Stable GitHub Releases are blocked until this repository has a protected stable NuGet publish workflow and the release cockpit can verify that workflow before GitHub Release creation. This prevents `v0.1.0` from becoming a GitHub-only release while public packages remain unpublished. Prerelease publishing uses the existing protected `nuget-prerelease` path and `publish` requires a successful `nuget-prerelease-publish.yml` run for the requested tag commit. To enable stable releases, add and protect a stable package publish path, wire it to stable package validation and publishing, teach this tool how to verify it, and keep environment review enabled.

## Diagnostics

Every failure uses the same envelope:

- `Severity`
- `Code`
- `Problem`
- `Cause`
- `Fix`
- `Docs`

Common codes include `release-version-leading-v`, `release-version-invalid`, `release-target-exists`, `release-sidecar-invalid`, `release-stable-package-policy-missing`, `release-tag-lightweight`, `release-tag-unreachable-from-main`, `release-github-output-path-invalid`, and `release-github-release-exists`.
