# AppSurface Release Tool

`./eng/release` is the repository-owned release cockpit for coordinated AppSurface releases. It prepares the release pull request from the living unreleased note, validates the tagged release state, and emits structured data for GitHub Actions. Release evidence is consistency evidence for repository release artifacts; it is not a signature, hosted-build attestation, or SLSA/Sigstore provenance.

## Quickstart

```bash
./eng/release check --version 0.1.0-preview.1
./eng/release prepare --version 0.1.0-preview.1 --dry-run
./eng/release prepare --version 0.1.0-preview.1 --date 2026-05-25
./eng/release publish --version 0.1.0-preview.1 --tag v0.1.0-preview.1 --dry-run
./eng/release check --version 0.1.0 --allow-existing-targets --fail-on-warnings --docs-catalog ./dist/docs/versions.json --docs-trusted-release-root ./dist/docs
./eng/release publish --version 0.1.0 --tag v0.1.0 --base-ref release/0.1.0 --dry-run
./eng/release docs-publication --version 0.1.0 --tag v0.1.0 --docs-exact-tree ./dist/docs --archive-output ./artifacts/appsurface-docs-v0.1.0.tar.gz --pages-staging-root /tmp/appsurface-pages --plan-output ./artifacts/docs-publication-plan.json --expected-release-manifest-sha256 <sha256>
```

Use `--version` without a leading `v`. Use `--tag v<version>` only for `publish`, where the tag must already exist and must be annotated. `publish` defaults `--base-ref` to `main`; pass the release branch, such as `--base-ref release/0.1.0`, when publishing from a maintained release branch. `--base-ref` accepts branch names plus `origin/<branch>`, `refs/heads/<branch>`, and `refs/remotes/origin/<branch>` refs, then normalizes them before checking reachability from `origin/<branch>`. Tags, full object IDs, empty branch names, and unsupported refs are rejected because the publish gate must prove protected branch reachability.

## Check

`check` validates the release inputs without mutating the repository. It verifies required release files, target versioned artifacts, package publishing policy, package manifest shape, release evidence bundles, optional stable docs archive inputs, and warning IDs that the review workflow can enforce with `--fail-on-warnings`. The release-prep review workflow also passes `--allow-existing-targets` because it reviews the versioned artifacts that the preparation workflow intentionally generated.

`--fail-on-warnings` and `--allow-existing-targets` are intentionally check-only options. `--docs-catalog` and `--docs-trusted-release-root` are stable docs evidence review options for `check` and optional publish-time diagnostics. `prepare` rejects all of these options so a maintainer does not think warning policy, target-collision policy, or docs archive selection changed while generating release artifacts.

For prereleases, `check` warns when the version cannot trigger protected NuGet prerelease publishing. The protected workflow currently accepts `preview`, `alpha`, `beta`, and `rc` labels with a positive numeric suffix, for example `0.1.0-preview.1`.

For stable releases, `check` also validates prepared release evidence and verifies the AppSurface Docs catalog entry and exact archive tree recorded there. Pass `--docs-catalog <path>` to the staged `versions.json` and `--docs-trusted-release-root <path>` to the directory that contains the catalog exact trees. When `--docs-catalog` is omitted, `check` uses `dist/docs/versions.json` only as a local review fallback if it exists. Use `--allow-existing-targets` for the release-prep review pass so generated target files are treated as the prepared artifact set instead of stale collisions.

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

`publish` validates that the supplied tag is annotated, resolves to a commit reachable from `origin/<base-ref>`, has a successful protected NuGet publish run for the tag's release classification, is not already public, and contains the versioned release note, sidecar, release manifest, and matching release evidence bundle at the tag commit before GitHub Release publication. Prerelease tags require `nuget-prerelease-publish.yml` proof. Stable tags require `nuget-stable-publish.yml` proof before the public GitHub Release can be promoted, so `v0.1.0` cannot become a GitHub-only release. Existing draft releases may be reused by the workflow when they still point at the same tag; already-public releases remain no-clobber by default. The command writes `version`, `tag`, `tag_commit`, `note_path`, `notes_file`, `release_classification`, `evidence_path`, `evidence_subject_sha256`, `evidence_tag_commit`, `docs_release_manifest_sha256`, and `prerelease` outputs when `--github-output` is supplied. `--github-output` must be a file path, not a root directory; in GitHub Actions, pass the `GITHUB_OUTPUT` file supplied by the runner.

Stable release docs publication is handled by the `docs-publication` command and the `release-publish.yml` workflow after package publish proof exists. The workflow exports docs from the annotated tag commit, creates the deterministic docs archive and `.sha256`, stages `versions.json` plus `releases/{version}/`, verifies the staged archive, deploys Pages, fetches the public catalog and exact-tree manifest, verifies the uploaded release asset digest, and only then promotes the draft GitHub Release.

## Docs Publication

`docs-publication` is the release-owned planner for the public AppSurface Docs trust path. It takes the exported exact tree for the tag and produces:

- `appsurface-docs-v{version}.tar.gz` with deterministic file ordering, normalized file modes, zero tar mtimes, and a paired `.sha256`
- `docs-publication-plan.json` with archive digest, catalog entry, exact tree path, release manifest digest, retry policy, and recovery summary path
- a Pages staging root containing the current docs payload, `versions.json`, and `releases/{version}/`
- a maintainer recovery summary with resume, publish, and abort commands for partial failures

Stable publication rejects release-manifest digest mismatches and recommended-version downgrades. GitHub Release assets are policy-immutable rather than platform-immutable: draft assets may be replaced during recovery for the same tag, but public release assets are no-clobber and require manual recovery or a fix-forward release.

`--pages-staging-root` is reset before the verified Pages payload is copied into it. Use a disposable directory outside the repository and outside the exported exact tree, existing Pages root, archive output, plan output, and recovery summary output. The command rejects root paths and overlapping staging paths before deleting anything.

## Release Evidence Bundle

`releases/v{version}.evidence.json` uses schema `appsurface-release-evidence-bundle-v1`. The release JSON describes the coordinated release metadata and generated release files; the release evidence bundle proves the generated release artifacts agree. Draft evidence is validated during release-prep pull request review. Tag-bound evidence is validated by `publish` against the resolved annotated tag commit.

The bundle records release identity, release note and sidecar paths, the release JSON digest, public package release-note paths, optional AppSurface Docs archive catalog fields, split commit identities, generator metadata, and a deterministic subject SHA-256. The subject digest excludes the generated timestamp so maintainers can review generation time without churning the proof. Optional GitHub artifact attestations are not required in v1; default workflows must not request attestation permissions unless a future explicit attestation mode is added.

The AppSurface Docs `.appsurface-docs-release-manifest.json` remains the exact-tree byte manifest produced by docs export. Runtime archive mounting still trusts only the version catalog's `releaseManifestSha256` pin plus local archive verification; release evidence connects that catalog/archive identity to the repository release artifacts when those docs archive fields are present.

Stable evidence must record a configured docs archive: `docsArchive.exactTreePath`, `docsArchive.releaseManifestSha256`, `docsArchive.catalogEntry.exactTreePath`, and `docsArchive.catalogEntry.releaseManifestSha256`. The catalog-entry fields must mirror the top-level docs archive fields. Stable `notConfigured` evidence is an error; prerelease `notConfigured` evidence remains allowed because prerelease docs archives may still be staged out-of-band.

## Stable Docs Evidence

Use the docs exporter and verifier before asking the release tool to validate a stable release:

```bash
appsurface docs export --repo . --output ./dist/docs --strict
appsurface docs verify-archive --catalog ./dist/docs/versions.json --version 0.1.0 --trusted-release-root ./dist/docs
./eng/release check --version 0.1.0 --allow-existing-targets --fail-on-warnings --docs-catalog ./dist/docs/versions.json --docs-trusted-release-root ./dist/docs
./eng/release publish --version 0.1.0 --tag v0.1.0 --dry-run
./eng/release docs-publication --version 0.1.0 --tag v0.1.0 --docs-exact-tree ./dist/docs --archive-output ./artifacts/appsurface-docs-v0.1.0.tar.gz --pages-staging-root /tmp/appsurface-pages --plan-output ./artifacts/docs-publication-plan.json --expected-release-manifest-sha256 <sha256>
```

`appsurface docs verify-archive` checks the same catalog-pinned exact tree used by runtime docs mounting. The release tool adds the release-specific gate: the selected stable catalog entry must be unique, public, available, pinned with `releaseManifestSha256`, equal to the release evidence docs fields, safely relative to the trusted release root, and byte-verified against `.appsurface-docs-release-manifest.json`. The release readiness report prints the authored catalog exact tree path and manifest digest separately from the resolved physical exact tree, catalog path, trusted root, verification state, and verified file count.

The protected `nuget-stable-publish.yml` workflow repeats the stable docs proof before the irreversible NuGet publish job. It checks out the annotated tag commit, exports AppSurface Docs into the `docsArchive.exactTreePath` recorded by `releases/v{version}.evidence.json`, stages a minimal `versions.json` with the recorded `releaseManifestSha256`, runs `appsurface docs verify-archive`, and then runs `./eng/release check` with the staged catalog and trusted root. If export output, catalog fields, or release evidence disagree, the workflow stops before requesting the NuGet trusted publishing token. The later GitHub Release workflow runs `./eng/release publish` for tag/package proof, then `./eng/release docs-publication` for the deterministic archive, public catalog, Pages staging, digest ledger, and recovery summary before it publishes the draft release.

Repair loops are intentionally concrete:

- `release-evidence-docs-archive-required`: regenerate stable release evidence from a completed docs export and catalog entry.
- `release-docs-catalog-input-missing`: pass the staged docs `versions.json` for `check`; release publishing creates its own docs publication plan from the tag export.
- `release-docs-catalog-version-unavailable`: repair the selected catalog entry so the stable version is present once, public, and pinned.
- `release-evidence-catalog-entry-mismatch`: regenerate evidence from the same catalog entry that publish verifies.
- `release-evidence-docs-exacttreepath-unsafe`: make `exactTreePath` trusted-root-relative with no parent or hidden segments.
- `release-docs-archive-verification-failed` or `release-evidence-docs-manifest-digest-mismatch`: rerun docs export, restore the exact tree, or copy the matching manifest digest printed by export.
- `release-docs-publication-manifest-digest-mismatch`: re-export docs from the annotated tag commit; the exact-tree manifest does not match release evidence.
- `release-docs-publication-output-path-unsafe`: move `--pages-staging-root` to a disposable directory that cannot delete the repository, exact tree, existing Pages root, or generated artifact outputs.
- `release-docs-publication-recommended-downgrade`: publish a newer stable release or perform documented manual recovery before changing `recommendedVersion`.

## Stable Release Policy

Stable GitHub Releases require the protected `nuget-stable-publish.yml` path. The workflow validates annotated `vX.Y.Z` tags, checks the configured release base branch, proves stable docs archive evidence before NuGet publication, publishes through the `nuget-stable` environment, waits through `nuget-stable-smoke`, and uploads docs proof, publish, and smoke evidence. The release cockpit verifies a successful stable workflow run for the exact tag commit before creating the GitHub Release. Prerelease publishing remains on `nuget-prerelease-publish.yml` and the `nuget-prerelease` environments.

## Diagnostics

Every failure uses the same envelope:

- `Severity`
- `Code`
- `Problem`
- `Cause`
- `Fix`
- `Docs`

Common codes include `release-version-leading-v`, `release-version-invalid`, `release-target-exists`, `release-sidecar-invalid`, `release-stable-package-policy-missing`, `release-stable-packages-not-published`, `release-prerelease-label-unprotected`, `release-prerelease-packages-not-published`, `release-base-ref-invalid`, `release-tag-lightweight`, `release-tag-unreachable-from-base-ref`, `release-github-output-path-invalid`, `release-github-release-exists`, `release-github-release-state-unavailable`, `release-evidence-missing`, `release-evidence-duplicate`, `release-evidence-schema-invalid`, `release-evidence-version-mismatch`, `release-evidence-artifact-digest-mismatch`, `release-evidence-content-source-commit-mismatch`, `release-evidence-release-manifest-schema-invalid`, `release-evidence-subject-digest-mismatch`, `release-evidence-docs-archive-required`, `release-evidence-docs-archive-incomplete`, `release-evidence-docs-exacttreepath-unsafe`, `release-evidence-docs-manifest-digest-mismatch`, `release-evidence-catalog-entry-mismatch`, `release-docs-catalog-input-missing`, `release-docs-catalog-version-unavailable`, `release-docs-archive-verification-failed`, `release-docs-publication-manifest-digest-mismatch`, `release-docs-publication-output-path-unsafe`, `release-docs-publication-recommended-downgrade`, and `release-evidence-tag-commit-mismatch`.
