# Coordinated release links

Package documentation needs a release link that remains honest when the same documentation is viewed as a historical archive. Use `release_track: coordinated` for the coordinated AppSurface package family. It resolves to `releases/current.md`, a pointer generated during release preparation that links to the tagged note selected for that documentation tree.

This is a tree-local coordinated release pointer, not a global latest-version lookup. A current documentation tree can point at a newer release, while `/docs/v/0.1.0/releases/current` continues to point at the version that was current when the `0.1.0` tree was published.

## Choose the right release link

| Package situation | Package-index fields | Result |
| --- | --- | --- |
| Public package in the coordinated release | `release_track: coordinated` | Links to `releases/current.md`, frozen by each exported docs tree. |
| Package with its own historical narrative | `release_track: explicit` plus `release_notes_path` | Links to the named versioned Markdown file. |
| Held package | `publish_decision: do_not_publish` plus `publish_reason` | Remains visible as held; use an explicit note when one is useful. |
| Proof host or support surface | Existing non-public classification and an explicit note path | Stays outside the direct-install path. |

Legacy rows that have only `release_notes_path` keep their explicit-path meaning so historical package-index snapshots stay readable. New coordinated rows must not set `release_notes_path`; combining a mutable path with the pointer makes the reader-facing target ambiguous.

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

Run the full dry-run after the migration. It renders and reports the planned versioned note and sidecar, overwriteable current pointer and sidecar, changelog and unreleased-file updates, and V2 release evidence that digests all five frozen release artifacts. Re-run the same command without `--dry-run` to write those artifacts.

```bash
./eng/release prepare --version x.y.z --dry-run
```

The only overwriteable artifacts are `releases/current.md` and `releases/current.md.yml`. `releases/v{version}.*` artifacts remain create-only. Do not hand-edit a current pointer to chase the globally newest tag: prepare it from the release branch, export the docs tree, and let the archived tree preserve its own pointer.

## Verify before review

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify
./eng/release prepare --version x.y.z --dry-run
```

For a prepared release pull request, review the versioned artifacts, both current-pointer files, `CHANGELOG.md`, and the reset unreleased artifacts. Release prep intentionally does not rewrite `packages/package-index.yml`; the coordinated track remains stable across releases.
