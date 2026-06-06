# AppSurface Release Tool

`./eng/release` is the repository-owned release cockpit for coordinated AppSurface releases. It prepares the release pull request from the living unreleased note, validates the tagged release state, and emits structured data for GitHub Actions. Release evidence is consistency evidence for repository release artifacts; it is not a signature, hosted-build attestation, or SLSA/Sigstore provenance.

## Quickstart

```bash
./eng/release check --version 0.1.0-preview.1
./eng/release prepare --version 0.1.0-preview.1 --dry-run
./eng/release prepare --version 0.1.0-preview.1 --date 2026-05-25
./eng/release publish --version 0.1.0-preview.1 --tag v0.1.0-preview.1 --dry-run
```

Use `--version` without a leading `v`. Use `--tag v<version>` only for `publish`, where the tag must already exist and must be annotated.

## Check

`check` validates the release inputs without mutating the repository. It verifies required release files, target versioned artifacts, package publishing policy, package manifest shape, release evidence bundles, and warning IDs that the review workflow can enforce with `--fail-on-warnings`. The release-prep review workflow also passes `--allow-existing-targets` because it reviews the versioned artifacts that the preparation workflow intentionally generated.

`--fail-on-warnings` and `--allow-existing-targets` are intentionally check-only options. `prepare` and `publish` reject them so a maintainer does not think warning policy or target-collision policy changed for a mutating or publishing command.

For prereleases, `check` warns when the version cannot trigger protected NuGet prerelease publishing. The protected workflow currently accepts `preview`, `alpha`, `beta`, and `rc` labels with a positive numeric suffix, for example `0.1.0-preview.1`.

## Prepare

`prepare` creates the release PR payload:

- `releases/v{version}.md`
- `releases/v{version}.md.yml`
- `releases/v{version}.release.json`
- `releases/v{version}.evidence.json`
- `CHANGELOG.md` compact rollover entries
- `packages/package-index.yml` release note paths for every `classification: public` plus `publish_decision: publish` package
- reset `releases/unreleased.md` and `releases/unreleased.md.yml`

The changelog is a compact ledger, not the detailed release narrative. During preparation, the detailed `CHANGELOG.md` `Unreleased`
body is reset to the standard pointer list while the full story moves from `releases/unreleased.md` into the generated tagged release
note.

`--dry-run` prints the readiness report, release evidence bundle summary, manual review gate, and planned file list without changing repository files. `--date` is parsed as invariant `YYYY-MM-DD`; malformed sidecar YAML fails with the standard diagnostic envelope instead of a raw parser exception.

Non-dry-run preparation writes files sequentially. If a local write fails partway through, inspect `git status` and remove or revert the partial generated files before retrying; otherwise the create-only target checks may report `release-target-exists` for the partially written versioned artifacts.

Release preparation ends at a pull request. Maintainers must manually review and merge release PRs before any annotated tag is created
or any publish workflow is started; automation and coding agents should stop at the ready-for-review PR unless a maintainer gives an
explicit post-review instruction to continue.

## Publish

`publish` is create-only. It validates that the supplied tag is annotated, resolves to a commit reachable from `origin/main`, has a successful prerelease NuGet publish run for prerelease tags, has no existing GitHub Release, and contains the versioned release note, release manifest, and matching release evidence bundle at the tag commit before GitHub Release creation. The command writes `version`, `tag`, `tag_commit`, `note_path`, `notes_file`, `release_classification`, `evidence_path`, `evidence_subject_sha256`, `evidence_tag_commit`, `docs_release_manifest_sha256`, and `prerelease` outputs when `--github-output` is supplied. `--github-output` must be a file path, not a root directory; in GitHub Actions, pass the `GITHUB_OUTPUT` file supplied by the runner.

## Release Evidence Bundle

`releases/v{version}.evidence.json` uses schema `appsurface-release-evidence-bundle-v1`. The release JSON describes the coordinated release metadata and generated release files; the release evidence bundle proves the generated release artifacts agree. Draft evidence is validated during release-prep pull request review. Tag-bound evidence is validated by `publish` against the resolved annotated tag commit.

The bundle records release identity, release note and sidecar paths, the release JSON digest, public package release-note paths, optional AppSurface Docs archive catalog fields, split commit identities, generator metadata, and a deterministic subject SHA-256. The subject digest excludes the generated timestamp so maintainers can review generation time without churning the proof. Optional GitHub artifact attestations are not required in v1; default workflows must not request attestation permissions unless a future explicit attestation mode is added.

The AppSurface Docs `.appsurface-docs-release-manifest.json` remains the exact-tree byte manifest produced by docs export. Runtime archive mounting still trusts only the version catalog's `releaseManifestSha256` pin plus local archive verification; release evidence only connects that catalog/archive identity to the repository release artifacts when those docs archive fields are present.

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

Common codes include `release-version-leading-v`, `release-version-invalid`, `release-target-exists`, `release-sidecar-invalid`, `release-stable-package-policy-missing`, `release-prerelease-label-unprotected`, `release-tag-lightweight`, `release-tag-unreachable-from-main`, `release-github-output-path-invalid`, `release-github-release-exists`, `release-evidence-missing`, `release-evidence-duplicate`, `release-evidence-schema-invalid`, `release-evidence-version-mismatch`, `release-evidence-artifact-digest-mismatch`, `release-evidence-subject-digest-mismatch`, `release-evidence-docs-manifest-digest-mismatch`, `release-evidence-catalog-entry-mismatch`, and `release-evidence-tag-commit-mismatch`.
