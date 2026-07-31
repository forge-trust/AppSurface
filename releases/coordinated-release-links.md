# Coordinated release links

Package documentation needs a release link that remains honest when the same documentation is viewed as a historical archive. Use `release_track: coordinated` for the coordinated AppSurface package family. It resolves to `releases/current.md`, a pointer generated during release preparation that links to the tagged note selected for that documentation tree.

This is a tree-local coordinated release pointer, not a global latest-version lookup. A current documentation tree can point at a newer release, while `/docs/v/0.1.0/releases/current` continues to point at the version that was current when the `0.1.0` tree was published.

## Pointer contract

`releases/current.md` is generated exactly, including its final newline. Before the first reachable annotated coordinated tag it is:

```markdown
<!-- appsurface-current-coordinated-release: none -->
# Current coordinated release

No coordinated AppSurface release has been tagged yet.
```

After a release it identifies the frozen release for that tree:

```markdown
<!-- appsurface-current-coordinated-release: v{version} -->
# Current coordinated release

This documentation tree represents [Release {version}](./v{version}.md).
```

Preparation discovers annotated `v*` tags whose peeled commits are ancestors of the captured `HEAD`, orders them by SemVer precedence, and requires the marker to name the highest one. It rejects malformed, stale, ambiguous, or backward markers and refuses to prepare a version whose tag already exists. The next target must be strictly newer than the current marker. `releases/current.md.yml` is deliberately outside that lifecycle: it is permanent metadata, must not contain a version or link, and release evidence digests it without regenerating it.

## Choose the right release link

| Package situation | Package-index fields | Result |
| --- | --- | --- |
| Public package in the coordinated release | `release_track: coordinated` | Links to `releases/current.md`, frozen by each exported docs tree. |
| Package with its own historical narrative | `release_track: explicit` plus `release_notes_path` | Links to the named versioned Markdown file. |
| Held package | `publish_decision: do_not_publish` plus `publish_reason` | Remains visible as held; use an explicit note when one is useful. |
| Proof host or support surface | Existing non-public classification and an explicit note path | Stays outside the direct-install path. |

Legacy rows that have only `release_notes_path` keep their explicit-path meaning so historical package-index snapshots stay readable. New coordinated rows must not set `release_notes_path`; combining a mutable path with the pointer makes the reader-facing target ambiguous.

Compatibility rule: a reader that understands only `release_notes_path` must continue to resolve legacy rows, while a coordinated reader resolves `release_track: coordinated` through `releases/current.md`. Do not retrofit historical rows just to make the current tree uniform. Migrate only when the package is intentionally joining the coordinated release family, and validate the old and new row shapes from a checked-out historical tree plus the current tree.

## Migrate a public coordinated package

Before:

```yaml
classification: public
publish_decision: publish
release_notes_path: releases/v0.2.0-preview.4.md
```

After:

```yaml
classification: public
publish_decision: publish
release_track: coordinated
```

Run the full dry-run after the migration. It renders and reports the planned versioned note and sidecar, overwriteable current pointer, preserved permanent current-pointer metadata sidecar, changelog and unreleased-file updates, and V2 release evidence that digests all five frozen release artifacts. Re-run the same command without `--dry-run` to write the generated artifacts.

```bash
./eng/release prepare --version x.y.z --dry-run
```

The only overwriteable release artifact is `releases/current.md`; `releases/current.md.yml` is permanent, version-independent metadata preserved by preparation and included in release evidence. `releases/v{version}.*` artifacts remain create-only. Do not hand-edit a current pointer to chase the globally newest tag: prepare it from the release branch, export the docs tree, and let the archived tree preserve its own pointer.

Preparation reports include a `## Preparation recovery` section. It names every planned or written artifact, including `releases/v{version}.md`, `releases/v{version}.md.yml`, `releases/v{version}.release.json`, `releases/v{version}.evidence.json`, `releases/current.md`, `CHANGELOG.md`, and the reset unreleased files when applicable. If preparation stops after a local write, preserve unrelated work, inspect `git status --short`, restore only those generated artifacts, run `git diff --check`, and rerun `./eng/release check --version x.y.z --allow-existing-targets` before retrying. Never use a broad worktree reset as recovery.

## Verify before review

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify
./eng/release check --version x.y.z --allow-existing-targets --fail-on-warnings
./eng/release prepare --version x.y.z --dry-run
git diff --check
```

For a prepared release pull request, review the versioned artifacts, both current-pointer files, `CHANGELOG.md`, and the reset unreleased artifacts. Release prep intentionally does not rewrite `packages/package-index.yml`; the coordinated track remains stable across releases. A dry run should leave the worktree unchanged and must not create versioned release artifacts. For stable releases, also run the docs archive verifier against the staged catalog and trusted exact-tree root used by `check`.
