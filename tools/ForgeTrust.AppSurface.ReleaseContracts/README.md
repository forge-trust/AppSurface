# ForgeTrust.AppSurface.ReleaseContracts

`ForgeTrust.AppSurface.ReleaseContracts` is the small, transitive package that keeps AppSurface's published Docs package and repository release tooling on the same release-metadata vocabulary. It is published so a clean consumer restore of `ForgeTrust.AppSurface.Docs` can resolve its exact dependency graph; application authors normally install Docs or use the [`appsurface release compose`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose) command instead of adding this package directly.

## Public API

The package exposes immutable release-link vocabulary for package indexes and release-aware documentation:

- `PackageReleaseTrack` has two values. `Coordinated` uses the repository's checked-in current-release pointer, while `Explicit` uses a package row's own `release_notes_path`.
- `PackageReleaseLink` is a record with `Track` and `ReleaseNotesPath` properties. Its `CoordinatedReleaseNotesPath` constant is `releases/current.md`; `CoordinatedReleaseSidecarPath` is the permanent metadata sidecar `releases/current.md.yml`. The record does not validate direct construction, so use the resolver for package-index values.
- `PackageReleaseLinkResolver.TryResolve(string? releaseTrack, string? releaseNotesPath, out PackageReleaseLink? link, out string? error)` trims inputs and accepts track names case-insensitively. It returns `true` with a non-null link when the declaration is valid, or `false` with a null link and a maintainer-facing error when it is not. A missing `release_track` plus a non-empty path preserves the legacy `Explicit` behavior. `coordinated` must omit `release_notes_path`; `explicit` requires one; a row that declares neither is invalid. Failures begin with `package-release-track-invalid`, `package-release-link-missing`, or `package-release-link-conflict` so callers can classify them.

Use these types when building an AppSurface package index or release-aware documentation integration. For application release notes, follow the [append-only unreleased-entry workflow](../../releases/README.md#append-only-unreleased-entries) through the public [CLI composition command](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose) instead.


## Offline release artifact binding

Use this internal verifier when a caller has a trusted manifest pin, expected release identity, and independent observations of every asset. Bind each promotion receipt separately to the verified manifest. The [PackageIndex replay](../ForgeTrust.AppSurface.PackageIndex.Tests/PreflightCarrierDispatchTests.cs) proves the package projection with real producer output and a local archive replacement. The [deployment fixture](../ForgeTrust.AppSurface.Release.Tests/ReleaseArtifactBindingTests.cs) uses synthetic OCI bytes. [Protected Tailwind publication](../../docs/tailwind-artifact-provenance.md) continues to enforce producer, per-host, and aggregate evidence through its own workflow.

### Records and limits

| Schema | Required fields |
|---|---|
| `appsurface-release-artifacts-v1` | `schema`, `repository`, `releaseId`, `sourceCommit`, `assets`. Each asset has `id`, `kind`, `locator`, `digestAlgorithm`, and `digest`. |
| `appsurface-promotion-receipt-v1` | `schema`, `promotionId`, `target`, `releaseManifestSha256`, `authorizationRef`, and `outcome` (`succeeded` or `failed`). |

Both schemas require strict UTF-8 JSON with the exact fields, no duplicate names, and no BOM, comments, or trailing commas. The parser bounds JSON depth to 8. The maximum document sizes are 256 KiB for a manifest and 16 KiB for a receipt. A manifest contains 1–128 assets, and the caller can supply up to 128 observations. A decoded field value can occupy up to 1,024 UTF-8 bytes. Blank values, controls, and invalid Unicode are rejected. Commit IDs are full 40- or 64-character lowercase hex. Digests are lowercase hex, 64 characters for `sha256` and 128 for `sha512`. Manifest pins always use SHA-256. All values compare ordinally, and asset IDs are unique across kinds. Kind and locator are opaque.

The methods recognize their respective v1 discriminators. Adding or removing a field requires an explicit new schema and migration decision. The pin refers to the original bytes, so changing whitespace or reserializing the same fields requires a new trusted pin.

### Internal API

- `VerifiedReleaseManifest.VerifyManifest(ReadOnlyMemory<byte>, string?, ReleaseIdentity?, IReadOnlyList<ReleaseAsset>?)` checks byte and observed count caps, then caller formats. It hashes a bounded snapshot before parsing it. The parsed manifest must be valid before release identity and inventory are compared. Inventory order is irrelevant, and every asset field must match by ID. `Success.Value` holds a detached `VerifiedReleaseManifest` with read-only `Identity` and `Sha256`. Only successful verification creates it.
- `VerifiedReleaseManifest.VerifyPromotionBinding(ReadOnlyMemory<byte>, VerifiedReleaseManifest?, PromotionIdentity?)` checks the byte cap, verified token and expected fields first. After the schema and record shape, it validates `outcome` before checking the exact promotion ID, target, release manifest digest, and authorization reference. `Success.Value` is the recorded `PromotionOutcome.Succeeded` or `PromotionOutcome.Failed`.
- `ReleaseIdentity`, `ReleaseAsset`, and `PromotionIdentity` carry the corresponding string fields above. There are no defaults for missing fields. Keep input memory and observations stable throughout each call. An invalid count or an unavailable index in the bounded observations produces `invalid-record`. Validate or refresh the observation before retrying.
- `ReleaseBindingResult<T>` has mutually exclusive `Success` and `Failure` cases. `Failure.Code` is a stable string, and that case has no success value. The verifier is read-only and reports no raw input values.

Check a manifest with an independently trusted pin and fresh observations:

```csharp
var binding = VerifiedReleaseManifest.VerifyManifest(
    retainedManifestBytes, trustedManifestSha256, expectedRelease, freshlyObservedAssets);
if (binding is ReleaseBindingResult<VerifiedReleaseManifest>.Failure rejected)
{
    return Reject(rejected.Code);
}

var verified = ((ReleaseBindingResult<VerifiedReleaseManifest>.Success)binding).Value;
```

Use the verified value for each independently identified receipt:

```csharp
var promotion = VerifiedReleaseManifest.VerifyPromotionBinding(receiptBytes, verified, expectedPromotion);
return promotion switch
{
    ReleaseBindingResult<PromotionOutcome>.Success bound => RecordOutcome(bound.Value),
    ReleaseBindingResult<PromotionOutcome>.Failure rejected => Reject(rejected.Code),
    _ => throw new InvalidOperationException(),
};
```

`Reject` and `RecordOutcome` are caller actions. A bound `Failed` receipt records that an attempt failed. The caller supplies the trusted pin and expectations, obtains the full observation, establishes receipt authenticity, and evaluates authorization, freshness, chronology, and outcome. The verifier performs no artifact downloads, signing, or provider checks. Use the source workflow's existing controls for those responsibilities.

### Failure codes and recovery

| Code | What failed | Next step |
|---|---|---|
| `invalid-record` | JSON encoding, shape, a required value or caller field is malformed, or authored asset IDs repeat. | Correct the record or caller input at its source. Read the required shape and field rules above before retrying. |
| `unsupported-schema` | The discriminator identifies a schema this method cannot bind. | Select the expected v1 record or an explicitly supported verifier for the new schema. |
| `input-limit-exceeded` | A byte, count, or decoded string limit was exceeded. | Correct the producer or input scope. Truncating an inventory would invalidate the proof. |
| `manifest-digest-mismatch` | The exact manifest bytes disagree with the trusted pin. | Retrieve the independently pinned bytes. Replacing the pin with a hash of the received bytes would discard the check. |
| `release-identity-mismatch` | The manifest is for a different repository, release, or source commit. | Retrieve the manifest for the expected identity from the trusted source. |
| `asset-mismatch` | The inventory is missing, extra, duplicated, or contains a mismatched field or digest. | Reopen the complete inventory and inspect its ID, kind, locator, algorithm and raw bytes against producer evidence. |
| `promotion-binding-mismatch` | A receipt differs in manifest pin, attempt ID, target, or authorization reference. | Retrieve the receipt for the exact expected attempt and recheck the caller's independent inputs. |

### Run the offline binding proof

From the repository root, use the SDK pinned by [`global.json`](../../global.json) and restore project dependencies. The existing test projects can access these internal types through their assembly friendship. Application code does not consume a supported package API for this contract.

```sh
dotnet --version
dotnet test tools/ForgeTrust.AppSurface.PackageIndex.Tests/ForgeTrust.AppSurface.PackageIndex.Tests.csproj --filter FullyQualifiedName~OfflineReleaseArtifactBindingReplay
dotnet test tools/ForgeTrust.AppSurface.Release.Tests/ForgeTrust.AppSurface.Release.Tests.csproj --filter FullyQualifiedName~ReleaseArtifactBindingTests
```

In the [replay test](../ForgeTrust.AppSurface.PackageIndex.Tests/PreflightCarrierDispatchTests.cs), the frozen PackageIndex manifest supplies the expected package IDs, report version, filenames, and raw SHA-512 values. The test projects these into a separate release manifest and computes its SHA-256 once. A fresh directory scan supplies every observed archive. The observer reads ID and version from each reopened nuspec, uses the actual filename as its opaque locator, and computes the raw SHA-512 again. Both paths encode ID as the invariant decimal UTF-16 length of the package ID, a colon, the exact ID, and the exact version. The fixture supplies the release context, file kind, digest algorithm and pin.

The replay passes when the original inventory verifies and replacing one archive payload in a scratch copy changes only its observed digest. The second verification must return `asset-mismatch` with the original release manifest and pin. The [contract tests](../ForgeTrust.AppSurface.Release.Tests/ReleaseArtifactBindingTests.cs) cover the byte pin, both record formats, limits, and promotion outcomes.

## Composition boundary

The package also contains the `internal` `UnreleasedEntryComposer`, `UnreleasedEntry`, and `UnreleasedEntryException` types shared by AppSurface Docs and the repository's [release-preparation workflow](../../tools/ForgeTrust.AppSurface.Release/README.md#append-only-unreleased-entries). They accept a flat, filename-sorted entry directory whose files begin with `<!-- appsurface:unreleased-entry section="section" -->`; templates declare each accepted section exactly once with `<!-- appsurface:unreleased-entries section="section" -->`. Section identifiers use lowercase letters, digits, and single hyphens. The internal workflow rejects undeclared or duplicate sections, malformed or code-block markers, source markers, top-level entry headings, and terminal control characters other than tabs and ordinary line breaks; it reports those failures as `UnreleasedEntryException`.

This support is intentionally not a second public authoring API. Consumer projects should invoke `appsurface release compose`, which provides the guarded preview and explicit-write contract, including link rebasing based on the selected destination and path protections around consumer-owned files.

## Internal concept-switching core

The package also contains the shared, `internal` BCL-only concept-switching core used by hosted Docs and static Release publication. Its public package surface remains unchanged. The core accepts only detached canonical route facts and exact bounded artifact bytes; callers retain ownership of archive verification, trusted-root file reads, URL construction, HTTP/DOM handling, and publication. See the [published Docs exact-version tree contract](../../Web/ForgeTrust.AppSurface.Docs/README.md#exact-version-tree-contract), [concept-switch artifact schemas](../../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-artifacts), [operator diagnostics](../../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-diagnostics), and [Release Docs publication workflow](../../tools/ForgeTrust.AppSurface.Release/README.md#docs-publication).

### Core reference

- `DocConceptReleaseInput` carries the exact release version and manifest SHA-256, public/availability/search facts, a verified home route, the frozen canonical route-to-artifact inventory, and a detached optional sidecar. A missing sidecar is represented by `null`; a present but unreadable or invalid sidecar is represented by present bytes (including an empty byte array) so it is quarantined instead of mistaken for a legacy release.
- `DocConceptCanonicalRouteFact` binds each canonical route to its verified physical HTML artifact and locale facts. The core never derives a filesystem destination from a route. Route identity uses ordinal-ignore-case routing semantics and preserves the canonical winner's spelling; concept IDs, locale identity, and emitted anchors remain ordinal.
- `DocConceptMapReference` supplies a descriptor-relative path, exact digest pin, and detached exact bytes. `DocConceptIdentityIndex.BuildCombined` loads all catalog maps once, deduplicates identical references and compatible facts, and produces an immutable index plus typed issues. `Resolve` takes a verified source identity and exact target version/pin; `EnumerateOutgoing`, `EnumeratePublicPages`, and `GetIncoming` expose lazy projections without retaining a source-by-version matrix. `GetIncoming` exhaustively validates a supplied candidate sequence; hosted and static page projections use `GetIncomingIndexed`, which unions canonical route/locale and verified concept/locale candidate buckets in catalog order, then runs those candidates through the same resolver. Both include only a real counterpart that lands on the exact destination; browsing fallbacks never become claimed concept matches.
- `DocConceptPageIdentity` carries version, manifest pin, canonical route, and known-locale state. `DocConceptCanonicalPage` carries the mapped artifact, title, exact anchor inventory, verified section-landing eligibility, optional concept ID, and serveability. `DocConceptResolution` reports `SamePage`, `MovedPage`, `RenamedPage`, `KnownAbsent`, `Unknown`, `Ambiguous`, `TargetUnavailable`, or `NoSelectedLocaleDocs`, plus only a validated counterpart or target-local recovery options.
- The release sidecar schema is `appsurface-docs-concepts-v1`: root fields are `schema`, `identityCoverage`, and `pages`; page fields are `canonicalRoutePath`, `locale`, nullable `conceptId`, `title`, `anchorIds`, and optional `sectionLanding` (default false). `complete` coverage requires an ID on every canonical winner; `partial` coverage cannot prove absence for an omitted ID.
- The catalog-wide bridge schema is `appsurface-docs-concept-map-v1`: each concept has `conceptId` and `memberships`; a membership pins exact `version`, `releaseManifestSha256`, and normalized `locale`, then declares `kind: page` with `canonicalRoutePath` or `kind: absent`. Either kind may carry an authored `fallbackSectionRoute`; page memberships may also carry `sectionLanding`. Every destination must resolve to a public, available exact-pinned canonical winner and ordinary serving artifact.
- `DocConceptArtifactBudget` shares the existing 4 MiB default and range of 1 byte through 32 MiB for complete UTF-8 artifacts. `DocConceptSourceKey.Create` makes a lowercase SHA-256 key from a versioned, length-prefixed UTF-8 tuple containing instance, exact version/pin, locale state/value, and canonical route.

### Decisions

- Explicit concept membership and absence settle a switch before route continuity. A complete target index proves absence only for the selected locale. When identity does not settle a result, the same case-insensitive canonical route may continue only when no authored identity contradicts it; partial and legacy unknown IDs never prove absence.
- Language is part of identity. Resolution never moves to a counterpart in another locale. A counterpart found only in another language does not hide verified recovery in the selected language: complete identity coverage establishes absence, while partial coverage remains unknown. Local recovery first uses a verified authored fallback; otherwise it selects the deepest serveable, `sectionLanding`-eligible strict non-root ancestor. Search and home remain in the selected target release and locale.
- A pinned bridge is catalog-wide identity data, independent of which descriptor references it. Strict publication rejects malformed artifacts, invalid pins, and conflicts. Startup quarantine keeps verified archive serving available while returning conservative outcomes for untrusted identities; unreadable or digest-invalid maps are treated as capable of affecting every potentially referenced release/group.
- A page assigned to multiple concepts connects those concepts into one conflict group. The full connected group is quarantined in either input order; the loader never chooses a first or last map winner and never revives route compatibility through the conflicting page.
- Incoming contexts are destination-bound and computed from the same immutable snapshot and resolver as outgoing choices. Indexed candidate buckets are shortlist hints only: `Resolve` remains authoritative for locale, exact pin, conflict, quarantine, route continuity, and counterpart eligibility. The shortlist retains original public-catalog order and has no per-destination or per-version result cache. Source keys are public lookup keys, not authorization tokens; each caller must also validate that a key is present in that page's own embedded projection.

### Pitfalls

- Do not pass `null` for a present unreadable sidecar or map. `null` means the optional sidecar is absent and the release is legacy; present empty bytes are intentionally invalid evidence.
- Do not mix a route with its physical artifact path, infer a filename, lowercase a physical path, or accept an alias as the canonical page. Callers provide verified route winners and the core returns the winner's original spelling.
- Legacy locale claims may come from either a direct page membership or an authored fallback. The loader keeps a claimed locale only when all claims for that unknown route agree; conflicting claims leave the route locale unknown and quarantine every claiming concept, independent of map order.
- A canonical artifact fact may retain a trusted `#fragment` after its physical HTML path. The core validates the whole-root physical path separately, rejects fragment separators that could become path components, and preserves well-formed percent escapes in fragment data. Strip the fragment before filesystem access.
- Do not borrow an unpinned release, a different locale, a live route, or a target title from a hidden/unavailable target. Hidden targets produce only the generic `TargetUnavailable` outcome and no target release/page projection.
- Do not treat `Unknown` or `Ambiguous` as absence, or count a section/search/home fallback as a counterpart. Omit any action that is not verified for the selected exact release and locale.
- Budget each complete generated artifact in UTF-8 bytes. The core and callers reject the whole artifact above the shared configured limit; they must not truncate a projection or cache a page-by-version result matrix.

## Compatibility and pitfalls

- Treat this package as a transitive support dependency. Pinning or installing it independently can create a graph that does not match the Docs package that owns the consumer-facing workflow.
- `PackageReleaseLink` paths describe repository release-note destinations; they are not filesystem authorization tokens and do not create or publish releases.
- The command composes notes only. Changelog rollover, release preparation, tags, and package publication remain separate, repository-owned operations.
