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

## Internal concept-switching core

The package also contains the shared, `internal` BCL-only concept-switching core used by hosted Docs and static Release publication. Its public package surface remains unchanged. The core accepts only detached canonical route facts and exact bounded artifact bytes; callers retain ownership of archive verification, trusted-root file reads, URL construction, HTTP/DOM handling, and publication. See the [published Docs exact-version tree contract](../../Web/ForgeTrust.AppSurface.Docs/README.md#exact-version-tree-contract), [concept-switch artifact schemas](../../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-artifacts), [operator diagnostics](../../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-diagnostics), and [Release Docs publication workflow](../../tools/ForgeTrust.AppSurface.Release/README.md#docs-publication).

### Core reference

- `DocConceptReleaseInput` carries the exact release version and manifest SHA-256, public/availability/search facts, a verified home route, the frozen canonical route-to-artifact inventory, and a detached optional sidecar. A missing sidecar is represented by `null`; a present but unreadable or invalid sidecar is represented by present bytes (including an empty byte array) so it is quarantined instead of mistaken for a legacy release.
- `DocConceptCanonicalRouteFact` binds each canonical route to its verified physical HTML artifact and locale facts. The core never derives a filesystem destination from a route. Route identity uses ordinal-ignore-case routing semantics and preserves the canonical winner's spelling; concept IDs, locale identity, and emitted anchors remain ordinal.
- `DocConceptMapReference` supplies a descriptor-relative path, exact digest pin, and detached exact bytes. `DocConceptIdentityIndex.BuildCombined` loads all catalog maps once, deduplicates identical references and compatible facts, and produces an immutable index plus typed issues. `Resolve` takes a verified source identity and exact target version/pin; `EnumerateOutgoing`, `EnumeratePublicPages`, and `GetIncoming` expose lazy projections without retaining a source-by-version matrix. `GetIncoming` includes only a real counterpart that lands on its exact destination; browsing fallbacks never become claimed concept matches.
- `DocConceptPageIdentity` carries version, manifest pin, canonical route, and known-locale state. `DocConceptCanonicalPage` carries the mapped artifact, title, exact anchor inventory, verified section-landing eligibility, optional concept ID, and serveability. `DocConceptResolution` reports `SamePage`, `MovedPage`, `RenamedPage`, `KnownAbsent`, `Unknown`, `Ambiguous`, `TargetUnavailable`, or `NoSelectedLocaleDocs`, plus only a validated counterpart or target-local recovery options.
- The release sidecar schema is `appsurface-docs-concepts-v1`: root fields are `schema`, `identityCoverage`, and `pages`; page fields are `canonicalRoutePath`, `locale`, nullable `conceptId`, `title`, `anchorIds`, and optional `sectionLanding` (default false). `complete` coverage requires an ID on every canonical winner; `partial` coverage cannot prove absence for an omitted ID.
- The catalog-wide bridge schema is `appsurface-docs-concept-map-v1`: each concept has `conceptId` and `memberships`; a membership pins exact `version`, `releaseManifestSha256`, and normalized `locale`, then declares `kind: page` with `canonicalRoutePath` or `kind: absent`. Either kind may carry an authored `fallbackSectionRoute`; page memberships may also carry `sectionLanding`. Every destination must resolve to a public, available exact-pinned canonical winner and ordinary serving artifact.
- `DocConceptArtifactBudget` shares the existing 4 MiB default and 1–32 MiB range for complete UTF-8 artifacts. `DocConceptSourceKey.Create` makes a lowercase SHA-256 key from a versioned, length-prefixed UTF-8 tuple containing instance, exact version/pin, locale state/value, and canonical route.

### Decisions

- Explicit concept membership and absence settle a switch before route continuity. A complete target index proves absence only for the selected locale. When identity does not settle a result, the same case-insensitive canonical route may continue only when no authored identity contradicts it; partial and legacy unknown IDs never prove absence.
- Language is part of identity. Resolution never moves to a counterpart in another locale. Local recovery first uses a verified authored fallback; otherwise it selects the deepest serveable, `sectionLanding`-eligible strict non-root ancestor. Search and home remain in the selected target release and locale.
- A pinned bridge is catalog-wide identity data, independent of which descriptor references it. Strict publication rejects malformed artifacts, invalid pins, and conflicts. Startup quarantine keeps verified archive serving available while returning conservative outcomes for untrusted identities; unreadable or digest-invalid maps are treated as capable of affecting every potentially referenced release/group.
- A page assigned to multiple concepts connects those concepts into one conflict group. The full connected group is quarantined in either input order; the loader never chooses a first or last map winner and never revives route compatibility through the conflicting page.
- Incoming contexts are destination-bound and computed from the same immutable snapshot and resolver as outgoing choices. Source keys are public lookup keys, not authorization tokens; each caller must also validate that a key is present in that page's own embedded projection.

### Pitfalls

- Do not pass `null` for a present unreadable sidecar or map. `null` means the optional sidecar is absent and the release is legacy; present empty bytes are intentionally invalid evidence.
- Do not mix a route with its physical artifact path, infer a filename, lowercase a physical path, or accept an alias as the canonical page. Callers provide verified route winners and the core returns the winner's original spelling.
- Do not borrow an unpinned release, a different locale, a live route, or a target title from a hidden/unavailable target. Hidden targets produce only the generic `TargetUnavailable` outcome and no target release/page projection.
- Do not treat `Unknown` or `Ambiguous` as absence, or count a section/search/home fallback as a counterpart. Omit any action that is not verified for the selected exact release and locale.
- Budget each complete generated artifact in UTF-8 bytes. The core and callers reject the whole artifact above the shared configured limit; they must not truncate a projection or cache a page-by-version result matrix.

## Compatibility and pitfalls

- Treat this package as a transitive support dependency. Pinning or installing it independently can create a graph that does not match the Docs package that owns the consumer-facing workflow.
- `PackageReleaseLink` paths describe repository release-note destinations; they are not filesystem authorization tokens and do not create or publish releases.
- The command composes notes only. Changelog rollover, release preparation, tags, and package publication remain separate, repository-owned operations.
