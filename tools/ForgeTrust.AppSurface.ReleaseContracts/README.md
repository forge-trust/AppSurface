# ForgeTrust.AppSurface.ReleaseContracts

`ForgeTrust.AppSurface.ReleaseContracts` is the small, transitive package that keeps AppSurface's published Docs package and repository release tooling on the same release-metadata vocabulary. It is published so a clean consumer restore of `ForgeTrust.AppSurface.Docs` can resolve its exact dependency graph; application authors normally install Docs or use the [`appsurface release compose`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose) command instead of adding this package directly.

## Public API

The package exposes immutable release-link vocabulary for package indexes and release-aware documentation:

- `PackageReleaseTrack` has two values. `Coordinated` uses the repository's checked-in current-release pointer, while `Explicit` uses a package row's own `release_notes_path`.
- `PackageReleaseLink` is a record with `Track` and `ReleaseNotesPath` properties. Its `CoordinatedReleaseNotesPath` constant is `releases/current.md`; `CoordinatedReleaseSidecarPath` is the permanent metadata sidecar `releases/current.md.yml`. The record does not validate direct construction, so use the resolver for package-index values.
- `PackageReleaseLinkResolver.TryResolve(string? releaseTrack, string? releaseNotesPath, out PackageReleaseLink? link, out string? error)` trims inputs and accepts track names case-insensitively. It returns `true` with a non-null link when the declaration is valid, or `false` with a null link and a maintainer-facing error when it is not. A missing `release_track` plus a non-empty path preserves the legacy `Explicit` behavior. `coordinated` must omit `release_notes_path`; `explicit` requires one; a row that declares neither is invalid. Failures begin with `package-release-track-invalid`, `package-release-link-missing`, or `package-release-link-conflict` so callers can classify them.

Use these types when building an AppSurface package index or release-aware documentation integration. For application release notes, follow the [append-only unreleased-entry workflow](../../releases/README.md#append-only-unreleased-entries) through the public [CLI composition command](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose) instead.

## Composition boundary

The package also contains the `internal` `UnreleasedEntryComposer`, `UnreleasedEntry`, and `UnreleasedEntryException` types shared by AppSurface Docs and the repository's [release-preparation workflow](../../tools/ForgeTrust.AppSurface.Release/README.md#append-only-unreleased-entries). They accept a flat, filename-sorted entry directory whose files begin with `<!-- appsurface:unreleased-entry section="section" -->`; templates declare each accepted section exactly once with `<!-- appsurface:unreleased-entries section="section" -->`. Section identifiers use lowercase letters, digits, and single hyphens. The internal workflow rejects undeclared or duplicate sections, malformed or code-block markers, source markers, top-level entry headings, and terminal control characters other than tabs and ordinary line breaks; it reports those failures as `UnreleasedEntryException`.

This support is intentionally not a second public authoring API. Consumer projects should invoke `appsurface release compose`, which provides the guarded preview and explicit-write contract, including link rebasing based on the selected destination and path protections around consumer-owned files.

## Compatibility and pitfalls

- Treat this package as a transitive support dependency. Pinning or installing it independently can create a graph that does not match the Docs package that owns the consumer-facing workflow.
- `PackageReleaseLink` paths describe repository release-note destinations; they are not filesystem authorization tokens and do not create or publish releases.
- The command composes notes only. Changelog rollover, release preparation, tags, and package publication remain separate, repository-owned operations.
