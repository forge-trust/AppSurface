# AppSurface Release Tool

`./eng/release` is the repository-owned release cockpit for coordinated AppSurface releases. It prepares the release pull request from the living unreleased note, validates the tagged release state, and emits structured data for GitHub Actions. Release evidence is consistency evidence for repository release artifacts; it is not a signature, hosted-build attestation, or SLSA/Sigstore provenance.

## Quickstart

```bash
./eng/release check --version 0.1.0-preview.1
./eng/release prepare --version 0.1.0-preview.1 --dry-run
./eng/release prepare --version 0.1.0-preview.1 --date 2026-05-25
./eng/release verify-prep-diff --base-ref main --report /tmp/appsurface-release-prep-diff.md
./eng/release tag-message --version 0.1.0-preview.1 > /tmp/appsurface-v0.1.0-preview.1-tag-message.txt
./eng/release inspect --version 0.1.0-preview.1 --tag v0.1.0-preview.1 --base-ref main
./eng/release publish --version 0.1.0-preview.1 --tag v0.1.0-preview.1 --dry-run
./eng/release check --version 0.1.0 --allow-existing-targets --fail-on-warnings --docs-catalog ./dist/docs/versions.json --docs-trusted-release-root ./dist/docs
./eng/release publish --version 0.1.0 --tag v0.1.0 --base-ref release/0.1.0 --dry-run
./eng/release docs-publication --version 0.1.0 --tag v0.1.0 --docs-exact-tree ./dist/docs --archive-output ./artifacts/appsurface-docs-v0.1.0.tar.gz --pages-staging-root /tmp/appsurface-pages --plan-output ./artifacts/docs-publication-plan.json --expected-release-manifest-sha256 <sha256>
```

Use `--version` without a leading `v`. `tag-message` derives the canonical tag ID from that version; `inspect` and `publish` require a matching annotated `--tag v<version>`. Both commands default `--base-ref` to `main`; pass the release branch, such as `--base-ref release/0.1.0`, when publishing from a maintained release branch. `--base-ref` accepts branch names plus `origin/<branch>`, `refs/heads/<branch>`, and `refs/remotes/origin/<branch>` refs, then normalizes them before checking reachability from `origin/<branch>`.

## Check

`check` validates the release inputs without mutating the repository. It verifies required release files, target versioned artifacts, package publishing policy, package manifest shape, release evidence bundles, optional stable docs archive inputs, and warning IDs that the review workflow can enforce with `--fail-on-warnings`. A public package cannot combine `publish_decision: publish` with a `readiness_blocker`; resolve and clear the blocker, or hold the package with `publish_decision: do_not_publish` plus a `publish_reason`, before release preparation. The release-prep review workflow also passes `--allow-existing-targets` because it reviews the versioned artifacts that the preparation workflow intentionally generated.

`--fail-on-warnings` and `--allow-existing-targets` are intentionally check-only options. `--docs-catalog` and `--docs-trusted-release-root` are stable docs evidence review options for `check` and optional publish-time diagnostics. `prepare` rejects all of these options so a maintainer does not think warning policy, target-collision policy, or docs archive selection changed while generating release artifacts.

For compatibility, keep historical package-index rows that contain only `release_notes_path` unchanged; they retain explicit-path semantics. Use `release_track: coordinated` only for new or migrated public packages that should resolve through the tree-local `releases/current.md` pointer. Do not combine `release_track: coordinated` with `release_notes_path`, because older readers and archived trees can otherwise disagree about the target. See the [coordinated release-links guide](../../releases/coordinated-release-links.md) for the field matrix and migration example.

For prereleases, `check` warns when the version cannot trigger protected NuGet prerelease publishing. The protected workflow currently accepts `preview`, `alpha`, `beta`, and `rc` labels with a positive numeric suffix, for example `0.1.0-preview.1`.

For stable releases, `check` also validates prepared release evidence and verifies the AppSurface Docs catalog entry and exact archive tree recorded there. Pass `--docs-catalog <path>` to the staged `versions.json` and `--docs-trusted-release-root <path>` to the directory that contains the catalog exact trees. When `--docs-catalog` is omitted, `check` uses `dist/docs/versions.json` only as a local review fallback if it exists. Use `--allow-existing-targets` for the release-prep review pass so generated target files are treated as the prepared artifact set instead of stale collisions.

## Verify prep diff

`verify-prep-diff` is the release-preparation pull-request gate. It fetches `origin/<base-ref>` by default, resolves one merge base and `HEAD`, then parses the complete `git diff --name-status -z --find-renames` stream. A PR becomes a release-preparation candidate when it adds or changes a versioned release manifest; candidates require one added V2 manifest, the exact generated release artifact statuses, and only manifest-declared unreleased-entry deletions. Non-release PRs receive no release-preparation classification. Candidates reject the permanent `releases/current.md.yml` sidecar, renames, copies, type changes, unsafe paths, and unrelated changes.

Package documentation is the one conditional exception: a modified chooser, readiness dashboard, or managed package README is accepted only when the read-only [PackageIndex witness](../ForgeTrust.AppSurface.PackageIndex/README.md#release-preparation-witness) has the same base tip, merge base, and head; declares the changed `packages/package-index.yml` or `release-guidance.template` input; and hashes the expected output. Managed README checks preserve every byte outside the exact marker pair and bind the marker body to the witness digest. The normal command invokes PackageIndex once only when the full diff contains a package candidate. `--witness <path>` is a controlled CI/test seam; normal users should omit it. Use `--no-fetch` only for an intentionally offline checkout with a current local `origin/<base-ref>`.

The Markdown report lists identities, every changed path, and escaped diagnostic rows. The supported recovery loop is: run PackageIndex `generate` for package drift, run `prepare` again for release-artifact drift, inspect the complete diff, then rerun `verify-prep-diff`. Reverting the release-preparation commit removes this gate's changes; witness JSON is temporary and never committed.

## Prepare

`prepare` creates the release PR payload:

- `releases/v{version}.md`
- `releases/v{version}.md.yml`
- `releases/v{version}.release.json`
- `releases/v{version}.evidence.json`
- `CHANGELOG.md` compact rollover entries
- preserves existing `packages/package-index.yml` release-link contracts for every `classification: public` plus `publish_decision: publish` package; preparation does not rewrite package rows. See the [coordinated release-links guide](../../releases/coordinated-release-links.md)
- reset `releases/unreleased.md` and `releases/unreleased.md.yml`

The changelog is a compact ledger, not the detailed release narrative. During preparation, the detailed `CHANGELOG.md` `Unreleased`
body is reset to the standard pointer list while the full story moves from `releases/unreleased.md` into the generated tagged release
note.

### Append-only unreleased entries

`releases/unreleased.md` is a stable template, not the file feature pull requests edit. Each release-facing pull request adds one file under `releases/unreleased.entries/`, named `YYYY-MM-DD-topic.md`, with an exact first-line directive such as `<!-- appsurface:unreleased-entry section="included" -->`. Supported destinations are `taking-shape`, `included`, and `migration-watch`; the remainder of the file is ordinary Markdown and may use nested `###` headings, but cannot introduce a `#` or `##` heading. The composer sorts entry filenames and inserts each body after its section placeholder, so new content stays at the bottom of the section without competing for a shared line range.

Both `check` and `prepare` validate the entry filename, directive, flat non-symlink directory shape, and strict one-template-marker-per-section contract even when no entries exist. `prepare` builds the versioned narrative from the composed note, records the exact archived entry paths in the V2 release manifest that its evidence digests, writes the normal generated artifacts, then deletes only those entries before it advances `releases/current.md`. Deletion uses a guarded private handoff: a concurrently changed candidate is restored without overwrite when possible, or retained under `releases/.release-prep-recovery/` for manual reconciliation when another writer has already recreated the source path. A feature entry that reaches `main` after the release-preparation branch was created is not deleted by that branch and remains in the next release cycle. AppSurface Docs uses the same composition when rendering `releases/unreleased.md`; the entry files themselves are not published as standalone Docs pages. The composed living note cannot opt in to raw Markdown download because its rendered body has no single checked-in source file.

`--dry-run` prints the readiness report, release evidence bundle summary, manual review gate, and planned file list without changing repository files. `--date` is parsed as invariant `YYYY-MM-DD`; malformed sidecar YAML fails with the standard diagnostic envelope instead of a raw parser exception.

Non-dry-run preparation writes files sequentially. The report names every planned or written artifact under `## Preparation recovery`. If a local write fails partway through, stop and inspect `git status --short`; preserve unrelated work, remove or restore only the listed generated artifacts, run `git diff --check`, confirm the artifacts are absent or back to their pre-run contents, and rerun `./eng/release check --version x.y.z --allow-existing-targets` before retrying. This avoids turning a partial `releases/v{version}.*` artifact into an accidental release input. `releases/current.md` may be overwritten only after its canonical pointer content passes validation; `releases/current.md.yml` is permanent metadata and is preserved.

Release preparation ends at a pull request. Maintainers must manually review and merge release PRs before any annotated tag is created
or any publish workflow is started; automation and coding agents should stop at the ready-for-review PR unless a maintainer gives an
explicit post-review instruction to continue.

## Prepared-to-tagged state

Versioned sidecars are committed source artifacts, not a claim that a release has shipped. `prepare` writes `release.schema: appsurface-release-sidecar-v1`, `release.state: prepared`, `release.id: v<version>`, and `trust.status: Prepared`; it does not claim a final narrative, tag-derived date, verified annotated tag, or GitHub Release. Prepared validation rejects those tagged-only terms even if a hand edit leaves `trust.status` as `Prepared`. The reset unreleased sidecar declares `release.state: unreleased`. V2 evidence digests this prepared sidecar alongside the frozen current pointer, manifest, and versioned note.

`./eng/release tag-message --version <version>` validates the prepared artifacts at `HEAD` and emits the final four-line annotated-tag binding block: `AppSurface-Release-Id`, `AppSurface-Release-Prepared-Sidecar-Sha256`, `AppSurface-Release-Manifest-Sha256`, and `AppSurface-Release-Evidence-Subject-Sha256`. Trailers must be final, ordered, unique, and use lowercase 64-character SHA-256 values. Unknown `AppSurface-Release-*` trailers fail. Historical tags that have no explicit sidecar state fail new `inspect` and `publish` operations with `release-legacy-tag-binding-unsupported`; use their existing archives rather than retagging history.

After merging a prepared release PR, use the local proof path before pushing:

```bash
git switch main
git pull --ff-only origin main
./eng/release tag-message --version 0.1.0-preview.1 > /tmp/appsurface-v0.1.0-preview.1-tag-message.txt
git tag -a v0.1.0-preview.1 -F /tmp/appsurface-v0.1.0-preview.1-tag-message.txt
./eng/release inspect --version 0.1.0-preview.1 --tag v0.1.0-preview.1 --base-ref main
git push origin v0.1.0-preview.1
```

`inspect` emits the verified `tagged` projection without changing repository source. Its optional `--out` file must be an ordinary path outside the repository source tree: the output file and user-supplied parent directories must not be symbolic links or reparse points. After validation succeeds, `inspect` opens every parent directory without following links. Unix creates the temporary file and atomically replaces the final relative name through that retained directory handle; Windows keeps non-delete-sharing handles open for each traversed directory and uses a handle-relative replacement target. A later pathname swap therefore cannot redirect the projection into an attacker-controlled directory. The release-publish docs job writes this projection under `RUNNER_TEMP`, overlays it in its disposable detached checkout before docs export, and never commits it. `publish` uses the same resolver before it calls GitHub or checks package publication. For an unpushed failed tag, recreate it from a regenerated message; never move or retag a pushed tag.

## Compatibility and local validation

Use the repository-owned `check` command as the compatibility gate before opening the release pull request. It validates the package-index release-link contract, generated target collisions, release manifest and evidence schemas, and warning IDs without mutating the worktree. For a migrated package-index row, validate both the legacy and coordinated interpretations: historical rows must still expose their explicit note path, while coordinated rows must resolve to `releases/current.md` and must not retain `release_notes_path`.

Run these checks from the repository root:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify
./eng/release check --version x.y.z --allow-existing-targets --fail-on-warnings
./eng/release prepare --version x.y.z --dry-run
git diff --check
```

For stable releases, add the staged docs catalog and trusted release root to the `check` invocation, then run `appsurface docs verify-archive` against the same catalog-pinned exact tree. Review the report's generated-artifact list, evidence fields, full diagnostic envelopes, and recovery section before merging. A dry run must not create `releases/v{version}.*`, change `releases/current.md`, or rewrite `CHANGELOG.md`.

## Publish

`publish` first resolves the tag-bound projection: it validates annotation, protected-branch reachability, prepared sidecar state, V1 or V2 evidence, the V2 frozen pointer/package set when present, and canonical tag trailers from the tag’s own blobs. Only then does it check protected NuGet publication proof and GitHub Release state. Prerelease tags require `nuget-prerelease-publish.yml` proof. Stable tags require `nuget-stable-publish.yml` proof before the public GitHub Release can be promoted, so `v0.1.0` cannot become a GitHub-only release. Existing draft releases may be reused by the workflow when they still point at the same tag; already-public releases remain no-clobber by default. The command writes `version`, `tag`, `tag_commit`, `note_path`, `notes_file`, `release_classification`, `evidence_path`, `evidence_subject_sha256`, `evidence_tag_commit`, `docs_release_manifest_sha256`, and `prerelease` outputs when `--github-output` is supplied.

Stable release docs publication is handled by the `docs-publication` command and the `release-publish.yml` workflow after package publish proof exists. The workflow exports docs from the annotated tag commit, creates the deterministic docs archive and `.sha256`, stages `versions.json` plus `releases/{version}/`, verifies the staged archive, deploys Pages, fetches the public catalog and exact-tree manifest, verifies the uploaded release asset digest, and only then promotes the draft GitHub Release.

## Docs Publication

`docs-publication` is the release-owned planner for the public AppSurface Docs trust path. It takes the exported exact tree for the tag and produces:

- `appsurface-docs-v{version}.tar.gz` with deterministic file ordering, normalized file modes, zero tar mtimes, and a paired `.sha256`
- `docs-publication-plan.json` with archive digest, catalog entry, exact tree path, release manifest digest, retry policy, and recovery summary path
- a Pages staging root containing the current docs payload, `versions.json`, and `releases/{version}/`
- a maintainer recovery summary with resume, publish, and abort commands for partial failures

Stable publication rejects release-manifest digest mismatches and recommended-version downgrades. GitHub Release assets are policy-immutable rather than platform-immutable: draft assets may be replaced during recovery for the same tag, but public release assets are no-clobber and require manual recovery or a fix-forward release.

`--pages-staging-root` is reset before the verified Pages payload is copied into it. Use a disposable directory outside the repository and outside the exported exact tree, existing Pages root, archive output, plan output, and recovery summary output. Generated outputs must also stay outside `--existing-pages-root`, because that root is copied into the public Pages payload before the new release tree is staged. The command rejects root paths and overlapping staging or generated-output paths before deleting or copying anything.

## Release Evidence Bundle

Historical evidence uses schema `appsurface-release-evidence-bundle-v1`. New coordinated releases use `appsurface-release-evidence-bundle-v2` and `appsurface-release-manifest-v2`. The V2 manifest records the pre-write `preparationBaseCommit`, ordinal-sorted published package projects, coordinated package resolutions, and the exact append-only entry paths it consumes. Each resolution fixes `releases/current.md` to `releases/v{version}.md`, the release tag, and the same preparation base commit. Explicit package links remain published-package metadata but never enter that resolution array. V2 evidence proves the prepared note sidecar, V2 release JSON, frozen `releases/current.md`, and current-pointer sidecar agree. Draft evidence is validated during release-prep pull request review. Tag-bound evidence is validated by [`inspect`](#prepared-to-tagged-state) and `publish` against the resolved annotated tag commit.

The bundle records release identity, release note and sidecar paths, the release JSON digest, ordered coordinated resolutions, optional AppSurface Docs archive catalog fields, split commit identities, generator metadata, and a deterministic subject SHA-256. V2 calls the original checked-out SHA `preparationBaseCommit`; later `releasePreparationCommit` and `tagCommit` identities remain nullable in preparation evidence and are populated only by the appropriate later release phase. V2 binds the frozen pointer to the tagged note for this docs tree; it never performs a global current-version lookup. The subject digest excludes the generated timestamp, workflow run, release-preparation commit, and tag commit so maintainers can review those later identities without churning the proof. Optional GitHub artifact attestations are not required; default workflows must not request attestation permissions unless a future explicit attestation mode is added.

The authoritative Draft 2020-12 definitions are `schemas/release-manifest-v2.schema.json` and `schemas/release-evidence-v2.schema.json`. The release tool also checks raw schema names before typed deserialization, rejects V1-only fields in V2, and validates ordinal ordering because JSON Schema cannot express arbitrary lexical ordering. Existing V1 readers remain separate: do not relabel or rewrite historical `v0.1.0` artifacts when adding V2 releases.

Before preparation, `releases/current.md` must be exactly one canonical template: the initial `none` marker with no release link, or the marker and link for the latest reachable annotated `v*` tag. Preparation accepts `none` only before the first qualifying tag, rejects stale or malformed markers, and renders the target template rather than editing prose in place. The sidecar remains permanent input. Immediately before the first write, preparation rechecks both the captured base commit and pointer/sidecar digests so concurrent changes cannot produce a mixed release set.

The AppSurface Docs `.appsurface-docs-release-manifest.json` remains the exact-tree byte manifest produced by docs export. Runtime archive mounting still trusts only the version catalog's `releaseManifestSha256` pin plus local archive verification; release evidence connects that catalog/archive identity to the repository release artifacts when those docs archive fields are present.

Stable evidence must record a configured docs archive: `docsArchive.exactTreePath`, `docsArchive.releaseManifestSha256`, `docsArchive.catalogEntry.exactTreePath`, and `docsArchive.catalogEntry.releaseManifestSha256`. The catalog-entry fields must mirror the top-level docs archive fields. Self-referential stable docs archives may use `generated` for the manifest digest; release workflows translate that sentinel to the exported exact tree's `.appsurface-docs-release-manifest.json` SHA-256 before staging or publishing the catalog pin. Stable `notConfigured` evidence is an error; prerelease `notConfigured` evidence remains allowed because prerelease docs archives may still be staged out-of-band.

## Stable Docs Evidence

Use the docs exporter and verifier before asking the release tool to validate a stable release:

```bash
appsurface docs export --repo . --output ./dist/docs --strict
appsurface docs verify-archive --catalog ./dist/docs/versions.json --version 0.1.0 --trusted-release-root ./dist/docs
./eng/release check --version 0.1.0 --allow-existing-targets --fail-on-warnings --docs-catalog ./dist/docs/versions.json --docs-trusted-release-root ./dist/docs
./eng/release publish --version 0.1.0 --tag v0.1.0 --dry-run
./eng/release docs-publication --version 0.1.0 --tag v0.1.0 --docs-exact-tree ./dist/docs --archive-output ./artifacts/appsurface-docs-v0.1.0.tar.gz --pages-staging-root /tmp/appsurface-pages --plan-output ./artifacts/docs-publication-plan.json --expected-release-manifest-sha256 <sha256>
```

`appsurface docs verify-archive` checks the same catalog-pinned exact tree used by runtime docs mounting. The release tool adds the release-specific gate: the selected stable catalog entry must be unique, public, available, pinned with `releaseManifestSha256`, match the release evidence docs fields or resolve its `generated` digest sentinel, stay safely relative to the trusted release root, and byte-verify against `.appsurface-docs-release-manifest.json`. The release readiness report prints the authored catalog exact tree path and manifest digest separately from the resolved physical exact tree, catalog path, trusted root, verification state, and verified file count.

Archive manifests retain the logical route casing emitted on Linux. When an archive is extracted on a case-insensitive Windows or macOS filesystem, source-shaped aliases and canonical routes can share a physical directory whose displayed casing differs from the manifest. Runtime and release verification match enumerated physical paths with the host filesystem's casing rules while keeping manifest duplicate detection, file reads, lengths, and digests bound to the exact logical entries. A differently cased extra file therefore remains an error on case-sensitive hosts.

The protected `nuget-stable-publish.yml` workflow repeats the stable docs proof before the irreversible NuGet publish job. It checks out the annotated tag commit, exports AppSurface Docs into the `docsArchive.exactTreePath` recorded by `releases/v{version}.evidence.json`, stages a minimal `versions.json` with the recorded `releaseManifestSha256`, runs `appsurface docs verify-archive`, and then runs `./eng/release check` with the staged catalog and trusted root. If export output, catalog fields, or release evidence disagree, the workflow stops before requesting the NuGet trusted publishing token. When stable evidence uses `generated`, stable publish and release publish both compute the release manifest digest from the exported exact tree before invoking archive verification or `docs-publication`. The later GitHub Release workflow runs `./eng/release publish` for tag/package proof, then `./eng/release docs-publication` for the deterministic archive, public catalog, Pages staging, digest ledger, and recovery summary before it publishes the draft release.

Repair loops are intentionally concrete:

- `release-evidence-docs-archive-required`: regenerate stable release evidence from a completed docs export and catalog entry.
- `release-docs-catalog-input-missing`: pass the staged docs `versions.json` for `check`; release publishing creates its own docs publication plan from the tag export.
- `release-docs-catalog-version-unavailable`: repair the selected catalog entry so the stable version is present once, public, and pinned.
- `release-evidence-catalog-entry-mismatch`: regenerate evidence from the same catalog entry that publish verifies.
- `release-evidence-docs-exacttreepath-unsafe`: make `exactTreePath` trusted-root-relative with no parent or hidden segments.
- `release-docs-archive-verification-failed` or `release-evidence-docs-manifest-digest-mismatch`: rerun docs export, restore the exact tree, or copy the matching manifest digest printed by export.
- `release-docs-publication-catalog-invalid`: repair or remove the existing Pages `versions.json`; malformed JSON cannot be merged into the public catalog.
- `release-docs-publication-manifest-digest-mismatch`: re-export docs from the annotated tag commit; the exact-tree manifest does not match release evidence.
- `release-docs-publication-output-path-unsafe`: move `--pages-staging-root` to a disposable directory that cannot delete the repository, exact tree, existing Pages root, or generated artifact outputs; keep archives, plans, and summaries outside `--existing-pages-root` so they cannot be copied into public Pages.
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

Common codes include `release-version-leading-v`, `release-version-invalid`, `release-target-exists`, `release-sidecar-invalid`, `release-current-page-body-invalid`, `release-current-page-stale`, `release-current-page-version-not-newer`, `release-current-page-tag-ambiguous`, `release-current-page-target-tag-exists`, `release-preparation-base-commit-concurrent-update`, `release-preparation-base-commit-invalid`, `release-preparation-base-commit-not-contained-by-tag`, `release-preparation-output-path-unsafe`, `release-prep-base-fetch-failed`, `release-prep-merge-base-invalid`, `release-prep-unsupported-status`, `release-prep-rename-forbidden`, `release-prep-permanent-sidecar-changed`, `release-prep-unexpected-path`, `release-prep-release-manifest-shape`, `release-prep-package-surface-without-source`, `release-prep-package-witness-invalid`, `release-prep-package-witness-mismatch`, `release-stable-package-policy-missing`, `release-stable-packages-not-published`, `release-prerelease-label-unprotected`, `release-prerelease-packages-not-published`, `release-base-ref-invalid`, `release-tag-lightweight`, `release-tag-unreachable-from-base-ref`, `release-github-output-path-invalid`, `release-github-release-exists`, `release-github-release-state-unavailable`, `release-evidence-missing`, `release-evidence-duplicate`, `release-evidence-schema-invalid`, `release-evidence-version-mismatch`, `release-evidence-artifact-digest-mismatch`, `release-evidence-content-source-commit-mismatch`, `release-evidence-release-manifest-schema-invalid`, `release-evidence-subject-digest-mismatch`, `release-evidence-docs-archive-required`, `release-evidence-docs-archive-incomplete`, `release-evidence-docs-exacttreepath-unsafe`, `release-evidence-docs-manifest-digest-mismatch`, `release-evidence-catalog-entry-mismatch`, `release-docs-catalog-input-missing`, `release-docs-catalog-version-unavailable`, `release-docs-archive-verification-failed`, `release-docs-publication-catalog-invalid`, `release-docs-publication-manifest-digest-mismatch`, `release-docs-publication-output-path-unsafe`, `release-docs-publication-recommended-downgrade`, and `release-evidence-tag-commit-mismatch`.

The prepared-to-tagged contract adds `release-sidecar-schema-invalid`, `release-sidecar-state-invalid`, `release-sidecar-id-mismatch`, `release-sidecar-final-claim-invalid`, `release-legacy-tag-binding-unsupported`, `release-tag-trailer-missing`, `release-tag-trailer-invalid`, `release-tag-trailer-mismatch`, `release-tag-tagger-missing`, `release-tag-tagger-invalid`, and `release-inspect-output-path-invalid`.
