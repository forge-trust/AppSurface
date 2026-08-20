# ForgeTrust.AppSurface.ReleaseContracts

`ForgeTrust.AppSurface.ReleaseContracts` is the small, transitive package that keeps AppSurface's published Docs package and repository release tooling on the same release-metadata vocabulary. It is published so a clean consumer restore of `ForgeTrust.AppSurface.Docs` can resolve its exact dependency graph; application authors normally install Docs or use the [`appsurface release compose`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose) command instead of adding this package directly.

## Public API

The package exposes immutable release-link vocabulary:

- `PackageReleaseTrack` identifies whether a package row follows the coordinated release, a historical archive, a held release, or a proof-host release surface.
- `PackageReleaseLink` carries that track together with its release-note path.
- `PackageReleaseLinkResolver` normalizes those links into the canonical release destination used by package documentation.

Use these types only when building an AppSurface package index or release-aware documentation integration. For application release notes, follow the [append-only unreleased-entry workflow](../../releases/README.md#append-only-unreleased-entries) through the public CLI instead.

## Composition boundary

The package also contains internal composition support shared by AppSurface Docs and the repository's [release-preparation workflow](../../tools/ForgeTrust.AppSurface.Release/README.md#append-only-unreleased-entries). That support validates independently owned Markdown entries, template markers, links, and paths before composing a deterministic note. It is intentionally not a second public authoring API: consumer projects should invoke `appsurface release compose`, which provides the guarded preview and explicit-write contract.

## Compatibility and pitfalls

- Treat this package as a transitive support dependency. Pinning or installing it independently can create a graph that does not match the Docs package that owns the consumer-facing workflow.
- `PackageReleaseLink` paths describe repository release-note destinations; they are not filesystem authorization tokens and do not create or publish releases.
- The command composes notes only. Changelog rollover, release preparation, tags, and package publication remain separate, repository-owned operations.
