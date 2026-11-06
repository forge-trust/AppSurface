# Concept-preserving version switching

AppSurface Docs can keep a reader on the same documented concept when they choose another exact release. A stable `concept_id` follows a guide across a route or title change; a pinned bridge can supply identity facts for an older archive. The reader sees a move notice, an honest unknown/absence result, or target-local browsing recovery. See the [Docs package reference](../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-artifacts) for the package contract, the [CLI export and archive verifier](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-docs-export), and the [Release publication contract](../tools/ForgeTrust.AppSurface.Release/README.md#docs-publication) for their caller boundaries.

Use this when a public guide needs to remain findable across reorganized documentation releases. It does not identify subjects by matching titles, prose, translations, source filenames, or redirect aliases; maintainers author identity explicitly.

## Authoring decision and metadata shape

Add `concept_id` to the guide's existing front matter or metadata sidecar. It maps to nullable `DocMetadata.ConceptId` and uses the usual metadata precedence and merge rules:

```markdown
---
title: Getting started with configuration
concept_id: guides.configuration.setup
---

Write the guide content here.
```

A paired `guide.md.yml` or `guide.md.yaml` may supply the same field under its existing metadata shape. A missing, null, or blank value means the page has no authored identity. Surrounding whitespace is trimmed. A nonblank ID must be 1–128 ASCII characters from lowercase `a`–`z`, digits, `.`, `_`, or `-`; the first and last characters must be alphanumeric. IDs compare ordinally and case-sensitively, so `guides.Setup` is invalid rather than a different spelling of `guides.setup`. Invalid authored values are rejected; they are not silently repaired or replaced by a fallback field.

YAML scalar types matter: quote an ID such as `"true"` that YAML would otherwise read as a boolean.
An explicit `!!str` value follows the same ID grammar, and `!!null` stays unspecified. Numeric, boolean,
collection, and unknown tagged values are invalid identities. The concept parser's internal type resolver
handles tags only for this field; other metadata retains its existing YAML behavior.

Keep the ID when the same guide is renamed or moved. Give a different subject a different ID. A split or merge needs a deliberately authored canonical hub that keeps the old identity, or the normal unknown/recovery path; the resolver never picks one descendant as a winner. A `concept_id` is independent of release, route, title, and language. Keep [`translation_key`](../Web/ForgeTrust.AppSurface.Docs/README.md#localization-foundation) for locale relationships and [`redirect_aliases`](../Web/ForgeTrust.AppSurface.Docs/README.md#document-route-identity) for navigation within one release.

There may be no more than one public canonical page per `(instance, exact release, normalized locale, concept_id)`. Locale variants may share an ID, but selection stays in the reader's selected locale. Assign IDs to all public canonical pages in a release when the exported identity index should report complete coverage; leaving any canonical page without an ID makes the index partial.

## Pinned identity artifacts

Each Docs export writes `.appsurface-docs-concepts.json` with schema `appsurface-docs-concepts-v1`. The file is written before `.appsurface-docs-release-manifest.json`, so its exact bytes and digest are covered by the release manifest and that manifest's catalog `releaseManifestSha256` pin. The index shape is:

```json
{
  "schema": "appsurface-docs-concepts-v1",
  "identityCoverage": "complete",
  "pages": [
    {
      "canonicalRoutePath": "guides/configuration/setup",
      "locale": "en",
      "conceptId": "guides.configuration.setup",
      "title": "Getting started with configuration",
      "anchorIds": ["choose-a-provider"],
      "sectionLanding": false
    }
  ]
}
```

The `pages` array covers every public canonical winner in the frozen route manifest, including pages with `conceptId: null`. `canonicalRoutePath` is the logical docs-root-relative route, not a filename; each page must match its public winner exactly, with its verified ordinary HTML artifact resolved through `canonicalArtifactPath`. Do not build a physical filename by concatenating a route. `locale` is the existing normalized locale identity or `null` for a known non-localized page. The bridge's required `locale` field uses the same normalized value and is `null` for that default/non-localized identity. Anchor IDs are the exact IDs emitted by that page. `sectionLanding` defaults to `false`; export it as `true` only for the actual resolved public-section landing winner, not every raw candidate.

`identityCoverage` is `complete` only when every page has an authored ID; otherwise it is `partial`. A missing sidecar means legacy/unknown coverage. A missing ID in a partial sidecar cannot prove absence. A complete sidecar can establish known absence for a locale when it contains no matching ID. Aliases, duplicate routes, and losing canonical/landing candidates do not count as additional identity pages.

An older archive can receive an optional bridge in the version catalog. `versions[]` may contain both `conceptMapPath` and `conceptMapSha256`, or neither. The default is no bridge; a half-specified pair is invalid. Resolve the relative map path beneath `AppSurfaceDocs:Versioning:TrustedReleaseRootPath` (which defaults to the catalog directory) and pin the exact bridge bytes with SHA-256. The map schema is `appsurface-docs-concept-map-v1`:

```json
{
  "version": "1.0.0",
  "exactTreePath": "releases/1.0.0",
  "releaseManifestSha256": "<generated release manifest digest>",
  "conceptMapPath": "concept-maps/legacy.json",
  "conceptMapSha256": "<computed SHA-256 of the exact bridge file bytes>"
}
```

Those angle-bracketed strings describe generated values; they are not literal digests. The catalog path points to the bridge file in the trusted release root, and its pin is over the exact JSON bytes, including line endings.

```json
{
  "schema": "appsurface-docs-concept-map-v1",
  "concepts": [
    {
      "conceptId": "guides.configuration.setup",
      "memberships": [
        {
          "version": "1.0.0",
          "releaseManifestSha256": "<generated release manifest digest>",
          "locale": "en",
          "kind": "page",
          "canonicalRoutePath": "guides/setup"
        },
        {
          "version": "2.0.0",
          "releaseManifestSha256": "<generated release manifest digest>",
          "locale": "en",
          "kind": "page",
          "canonicalRoutePath": "guides/configuration/setup"
        }
      ]
    },
    {
      "conceptId": "guides.legacy-cli",
      "memberships": [
        {
          "version": "1.0.0",
          "releaseManifestSha256": "<generated release manifest digest>",
          "locale": "en",
          "kind": "page",
          "canonicalRoutePath": "guides/legacy-cli"
        },
        {
          "version": "2.0.0",
          "releaseManifestSha256": "<generated release manifest digest>",
          "locale": "en",
          "kind": "absent"
        }
      ]
    }
  ]
}
```

The digest values above are labels for values generated from the local fixture; they are not literal hashes to copy. The [two-release walkthrough](#two-release-walkthrough) creates the exact JSON from the exported manifest bytes and prints the catalog pins. Each membership names one exact `version`, its matching `releaseManifestSha256`, the normalized `locale`, and either `kind: "page"` with an exact canonical route or `kind: "absent"`. A page membership may add `fallbackSectionRoute` and optional historical `sectionLanding` eligibility (default false); an absence membership may offer a fallback section but cannot name a page or set landing eligibility.

The host combines every distinct map referenced by the catalog into one immutable snapshot. A map referenced on either a source or target version participates in either switch direction. Identical path/digest references and identical facts are deduplicated. Membership identity comes from its exact release pin, version, and locale, not from the descriptor that points to the map. Duplicate memberships, a legacy page assigned to multiple concepts, a bridge that contradicts an embedded ID, an absence that erases an embedded page, or conflicting maps quarantine every affected concept group; declaration order never chooses a winner and route continuity cannot resurrect a quarantined match. Static publication reads the catalog and pinned maps from the trusted verification input, strips `conceptMapPath` and `conceptMapSha256` from the public `versions.json`, and keeps bridge bytes out of the Pages serving tree. The reader receives only validated page-local projections. Authored static publication rejects conflicts before it publishes the serving output.

The index and bridge are optional identity data. At host startup, invalid or unreadable concept data disables the affected concept resolution and logs an operator diagnostic while valid archives remain readable under the existing archive rules. A bad release manifest pin or frozen route manifest still makes the release unavailable under the existing archive contract. The host does not watch for catalog changes: editing an index, map, or pin requires the correct re-export/repin and a restart or redeploy.

A parsed bridge conflict quarantines its known concept/page group and leaves unrelated verified concepts usable.
An unreadable, malformed, or digest-invalid bridge cannot establish which groups it affects, so the host
conservatively disables cross-version matching across that snapshot. This distinction does not make an archive
unreadable or turn a conflict into evidence of absence. An untrusted bridge also prevents a known-absence claim
from a separate bridge when the target's locale facts are incomplete; recovery stays unknown and offers only
independently verified actions.

## Resolution and reader outcomes

The shared resolver accepts a verified source page identity, its exact source release pin and normalized locale, and the selected target exact release. The target alias is first resolved to one eligible exact release; a hidden or unavailable version cannot be matched. It then applies this precedence:

1. Use an embedded or explicitly bridged concept membership for the selected locale. A unique counterpart wins even when the old route now belongs to another concept. Explicit `kind: "absent"` wins over route continuity.
2. A complete target index with no matching ID in that locale establishes known absence. A partial or legacy index cannot establish absence from a missing ID.
3. If identity evidence has not settled the result, retain the same public canonical route only when it exists in the target's established selected locale and no authored evidence contradicts it. This preserves old route continuity; it does not claim semantic identity.
4. Incomplete evidence produces unknown; conflicting evidence produces ambiguous recovery. Neither result is “removed,” and neither uses a similar title or page as a guess.

Logical canonical route comparison and strict ancestor checks use the existing normalized ordinal-ignore-case routing rules. A case-only route change is continuity and does not itself trigger a move notice; emitted URLs retain the target winner's authored spelling and verified physical mapping. Concept IDs and emitted anchor IDs compare ordinally and case-sensitively. A valid source fragment is carried only if the target index records that exact emitted anchor; the target's own canonical fragment takes precedence. Otherwise the destination opens at its top. Unrelated source query parameters are cleared.

| Evidence | Reader result |
| --- | --- |
| Same canonical counterpart | Open it at the selected exact version with no move warning. |
| Same concept at a different route | Open the target page and explain that the guide moved in the selected version; show its original title/version and a return link. |
| Same route with a changed title | Open the target page and explain the title change. Comparing two releases does not claim when the change happened. |
| Explicit or complete-index absence | Show **“No documented counterpart”** and say there is no documented counterpart for the guide in that version. This does not establish whether the feature exists in that version. |
| Partial, legacy, or unverifiable identity | Show **“No verified counterpart”** and explain that available identity data cannot establish a match. Do not call it removed. |
| Conflicting mapping | Give ambiguous recovery without selecting a declaration-order winner. |
| Hidden or unavailable target | Give a separate availability result; do not disclose hidden release details or call the guide absent. |
| No docs in the selected locale | Explain the selected-language limitation and omit section/search/home actions that would silently change language. Offer only verified archive and original-guide return actions. |

Every outcome is locale-specific. A page that exists only in another language is not a counterpart. Canonical-path continuity also requires an established target locale matching the source; unverifiable legacy locale facts produce unknown. `translation_key` remains the locale-link contract and cannot fill a missing cross-release concept ID.

Recovery first uses a valid authored `fallbackSectionRoute`. If none exists, select only a public, verified, serveable target-release page in the selected locale with verified `sectionLanding: true` whose canonical route is a strict non-root ancestor of the source route. Ignore fragments during the ancestry check and choose the eligible ancestor with the most path segments. An ordinary page, root, raw losing landing candidate, missing legacy eligibility, or ambiguous eligible target does not qualify. If no unique section is available, offer version-local search when its index is valid, then that release's documentation home. Omit missing actions; never send the reader to live or source-release search. Keep the original verified guide as a separate return action and label a section as browsing recovery, not as a concept match.

## Hosted and static delivery

The selector stays beside the Docs identity and shows the selected exact version and language. It is a disclosure with real links, not an ARIA application menu: Tab follows links, Enter/Space opens the disclosure, Escape closes it and returns focus, and arrow keys do not navigate. When no other eligible version exists, show the selected identity and “No other versions available,” with no empty interactive control. The no-JavaScript path uses the ordinary `versions/switch/{sourceKey}.html` link. That choice page explains a move or recovery before its direct counterpart/fallback/search/home link, so the reader gets one explicit link selection after choosing the version. Enhanced version links go directly to a resolved counterpart and skip the choice page.

Each source page has a public SHA-256 `sourceKey` derived from an unambiguous normalized tuple of instance, exact release pin, locale identity, and canonical route. This key is a lookup identifier, not authorization. A destination accepts optional `docsFrom=<sourceKey>` transition context only when the exact key appears in its own embedded incoming projection and is bound to that destination's instance, exact release pin, locale, and canonical identity. The projection includes only verified public sources whose resolver outcome actually lands on that exact page. A browsing fallback never becomes a claimed counterpart. Unknown keys, unrelated routes, stale pins, wrong locales, or mismatched destinations produce no transition claim and no caller-controlled return URL; the guide remains readable. The projection is built from the immutable served snapshot, without fetching a plan, scanning archived HTML, consulting Git/network, reversing a hash, or caching unknown caller keys.

The destination derives its notice and return target from its validated projection, never from query-supplied URLs or display text. Markup-bearing titles and notices are encoded as text and data is inert JSON. The packaged external runtime's hash is derived from its exact build-owned bytes; static output uses that SHA-256 in an early meta CSP and the matching `integrity` attribute. Static meta preserves `connect-src 'none'` and does not claim `sandbox` enforcement. Hosted HTTP responses retain the sandbox policy and allow script execution only for the verified packaged runtime. Neither surface adds a host-wide script source, `unsafe-inline`, `unsafe-eval`, dynamic code loading, or a plan/network fetch. Ordinary choice-page links remain available when JavaScript is blocked or disabled.

Static publication verifies original exact archives as immutable inputs, then generates separate serving copies and `.appsurface-docs-serving-inventory.json` (`appsurface-docs-serving-inventory-v1`, transform `docs-version-switch-static-v1`). Its `sourceReleases` records each public available exact version, release-manifest pin, and exact-tree path. The `files` array records generated switch/runtime/style/archive artifacts and every member under each mounted exact-version tree; each row includes the path, kind, optional source version and release-manifest pin, byte length, and SHA-256. Transformed HTML/search output and unchanged mounted members use distinct kinds. The public catalog removes `conceptMapPath` and `conceptMapSha256`; bridge bytes and the full catalog remain in the private verification tree and are excluded from the Pages serving tree. The catalog and bridge for verification live under `<pages-staging-root>.verification`; use the plan's `verification_root` and `catalog_path` outputs with `appsurface docs verify-archive`. The Pages staging root contains only derived serving output. For the default mount, links to frozen exact-version pages use `/docs/v/{version}/{canonicalArtifactPath}`; the logical `canonicalRoutePath` is not appended to make a filename. The inventory is separate from the release manifest; transformed bytes never receive an original archive hash. All generated links must remain below the selected mount and work with exact/alias roots, custom route roots, and `PathBase`. See [Release docs publication](../tools/ForgeTrust.AppSurface.Release/README.md#docs-publication) for the staging boundary and publication result, and [ReleaseContracts](../tools/ForgeTrust.AppSurface.ReleaseContracts/README.md) for the internal BCL-only resolver and projection contract.

When reusing an earlier serving tree, publication treats a reserved mount or switch file as generated output only when the prior inventory has the supported schema and transform and lists that confined file with its actual length and SHA-256. A marker file alone cannot claim ownership. Missing, malformed, unlisted, or changed reserved files fail publication; restore the complete prior generated tree or regenerate from the original verified archives. This ownership check permits replacement of prior generated output without letting authored files silently occupy reserved namespaces. It does not replace release-manifest verification.

## Size limits and overflow

`AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes` remains the single per-artifact UTF-8 byte limit. It defaults to `4,194,304` bytes (4 MiB) and accepts `1` through `33,554,432` bytes (32 MiB). It bounds each concept index, bridge, complete switch fragment with inert incoming/outgoing data, and standalone choice page as well as the existing rewritten HTML and search input. Generated bytes count toward the limit. Reads check growth and UTF-8 bytes; an artifact is either accepted complete or rejected whole. No truncated prefix is used. The limit does not promise a cap on total catalog size, process memory, or aggregate static output.

Raising the option also raises the existing HTML/search-input budget. There is no separate switch-only knob. For example, `AppSurfaceDocs__Versioning__MaxRewrittenFileSizeBytes=8388608` sets the host to 8 MiB; pass `--max-rewritten-file-size-bytes 8388608` to [`./eng/release docs-publication`](../tools/ForgeTrust.AppSurface.Release/README.md#docs-publication) and `appsurface docs verify-archive` as well. This increases the accepted HTML/search-input budget alongside every switch artifact. Prefer reducing payload size; request an override only when the maintainers deliberately accept the coupled increase.

Overflow has three caller-specific outcomes. Hosted guide presentation discards the whole optional switch artifact and keeps the otherwise valid guide readable. It emits the complete generic “Version switching is unavailable for this guide.” notice with verified public archive/original-guide actions when that complete fragment fits; otherwise it omits the optional UI and preserves existing navigation. A hosted standalone choice request returns HTTP 503 with a complete budget-checked generic recovery page containing only verified archive and original-guide links, or an empty 503 if even that complete page cannot fit. Static publication stops before Docs-asset or Pages uploads, retains original verification inputs and published content, and publishes neither a partial inventory nor degraded substitutes. Existing oversized-HTML/search-input rejection remains unchanged.

## Diagnostics and correction

The [Docs concept-switch diagnostic table](../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-diagnostics) is canonical. These codes are operator-only; reader HTML uses generic outcomes and never reveals hidden IDs, paths, titles, mapping errors, local paths, raw query strings, or exceptions.

| Code | Correction path |
| --- | --- |
| `docs-concept-id-invalid` | Correct `concept_id` in front matter or the source sidecar using the documented grammar. Re-export the complete release and use its new `releaseManifestSha256`; do not patch the archive. |
| `docs-concept-index-invalid` | Re-export from the intended source/tag so the sidecar covers the public canonical route inventory. Verify the exact tree, update its release pin, and restart/redeploy the host snapshot. |
| `docs-concept-map-invalid` | Correct all conflicting memberships from source evidence, or repair the complete trusted `conceptMapPath`/`conceptMapSha256` pair. Repin changed bridge bytes and restart/redeploy; do not choose a first/last mapping. |
| `docs-switch-artifact-overflow` | Reduce the complete artifact or have the maintainer deliberately raise the existing shared option within its supported range. Re-export, verify, update changed pins, configure the same cap on host and publication, then retry. Static publication stops before uploads. |
| `docs-switch-runtime-invalid` | Restore/rebuild the matching packaged runtime/hash/SRI and regenerate static serving output. Keep the narrow approved CSP; do not broaden script policy. |
| `docs-switch-processing-failed` | For a hosted request, use the existing request correlation to inspect the protected operator trace and send that identifier to the maintainer. For static assembly, use the Release diagnostic's sanitized failure type and bounded method context, repair the exact exports or metadata, and rerun `docs-publication`. Preserve the deployment, correct the cause, and retry once; do not patch archive bytes or CSP. |

Expected validation and overflow diagnostics use safe cause/field/artifact context and an exact correction path, without stack dumps. Unexpected host processing uses the existing `Activity`/`TraceIdentifier` correlation and sanitized bounded method information; the internal trace does not enter the public response. Release publication uses its existing diagnostic envelope and nonzero failure path. See [`appsurface docs verify-archive`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-docs-verify-archive) and [`docs-publication`](../tools/ForgeTrust.AppSurface.Release/README.md#docs-publication) for their command-specific recovery surfaces.

## Pitfalls

- Do not use `translation_key` or `redirect_aliases` as a substitute for `concept_id`; they describe locale relationships and routing within a release.
- Do not infer absence from a missing ID in a partial or legacy index. Use a digest-pinned `kind: "absent"` membership when an older archive needs explicit evidence.
- Do not bridge a page with a different embedded ID or assign one legacy page to multiple concepts. Resolve the source identity facts instead of choosing a map order or a route-continuity winner.
- Do not treat `sectionLanding` as an authored preference for any convenient ancestor. New exports set it only for the resolved section landing winner; a legacy bridge must pin the historical eligibility explicitly.
- Do not match anchors case-insensitively or lowercase a physical file path. Logical routes use the existing case-insensitive route rules; concept IDs and emitted anchor IDs retain their exact comparison contracts.
- Do not expect a catalog edit or a new map digest to hot-reload. Finish the re-export and repin, then restart or redeploy the host.
- Do not patch an immutable archive to add identity, truncate a switch payload to fit, or raise the size option automatically. Re-export the complete release or deliberately change the existing coupled limit on every applicable caller.
- Do not widen CSP when the packaged runtime fails integrity. Repair the package/runtime bytes and regenerate the static projection under the approved exact hash and SRI.
- Do not deploy transformed serving copies as if their bytes had the original release manifest hash. Verify the original archives and retain the separate generated serving inventory.

For the schema and safe operator fixes, use the canonical [Docs artifact reference](../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-artifacts) and [diagnostic table](../Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-diagnostics).

## Two-release walkthrough

The checked-in [concept-switch fixture](https://github.com/forge-trust/AppSurface/blob/1bdac5ca3c6e0f99c9f3dd1b3d5eaaf31c948cb0/tests/fixtures/concept-switch/README.md) contains two small repositories: v1 publishes `guides.setup` at `guides/setup`, while v2 moves it to `guides/configuration/getting-started`; v1's explicitly identified `guides.removed` has no v2 counterpart. The fixture also covers a reused route, unchanged route, title-only change, legacy unknown, and a French-only target. This walkthrough uses fresh exports and local release publication output under one newly created temporary directory. It does not upload anything. The integration fixture uses the same export and publication command arguments, derives pins from manifest bytes, snapshots each original export's SHA-256 before publication, and rechecks them after publication and browser assertions.

From the repository root, export both fixtures and compute their release pins from the actual manifest bytes. Keep the exports as immutable verification inputs:

```bash
set -eu
repo="$(pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-docs-switch.XXXXXX")"
mkdir -p "$work/exports" "$work/publication/1.0.0" "$work/publication/2.0.0"

dotnet run --project "$repo/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj" --no-launch-profile -- docs export --repo "$repo/tests/fixtures/concept-switch/v1" --output "$work/exports/1.0.0" --mode cdn --strict
dotnet run --project "$repo/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj" --no-launch-profile -- docs export --repo "$repo/tests/fixtures/concept-switch/v2" --output "$work/exports/2.0.0" --mode cdn --strict

pin1="$(python3 -c 'import hashlib,pathlib,sys; print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())' "$work/exports/1.0.0/.appsurface-docs-release-manifest.json")"
pin2="$(python3 -c 'import hashlib,pathlib,sys; print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())' "$work/exports/2.0.0/.appsurface-docs-release-manifest.json")"
printf '1.0.0 releaseManifestSha256=%s\n2.0.0 releaseManifestSha256=%s\n' "$pin1" "$pin2"
```

Record every original export file's relative path and SHA-256 before publication. This check includes both manifests and every generated page/sidecar; it detects a mutation or newly added file after the serving output is built:

```bash
python3 - "$work/exports" "$work/original-hashes.json" <<'PY'
import hashlib, json, pathlib, sys
roots = pathlib.Path(sys.argv[1])
snapshot = {}
for version in ("1.0.0", "2.0.0"):
    root = roots / version
    snapshot[version] = {
        path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(root.rglob("*")) if path.is_file()
    }
pathlib.Path(sys.argv[2]).write_text(json.dumps(snapshot, sort_keys=True, indent=2) + "\n")
PY
```

Publish v1 to a disposable Pages staging root. The expected digest is the `pin1` printed by the export step:

```bash
dotnet run --project "$repo/tools/ForgeTrust.AppSurface.Release/ForgeTrust.AppSurface.Release.csproj" --no-launch-profile -- docs-publication \
  --version 1.0.0 --tag v1.0.0 \
  --docs-exact-tree "$work/exports/1.0.0" \
  --archive-output "$work/publication/1.0.0/appsurface-docs-v1.0.0.tar.gz" \
  --pages-staging-root "$work/pages-v1" \
  --plan-output "$work/publication/1.0.0/plan.json" \
  --summary-output "$work/publication/1.0.0/summary.md" \
  --expected-release-manifest-sha256 "$pin1"
```

The [GitHub release workflow](../.github/workflows/release-publish.yml) rebuilds its archive catalog from release assets and does not carry forward the private verification catalog or bridge files. A static publication that relies on legacy bridges needs a trusted, persistent verification input, such as the companion tree used in the CLI example below. Keep it outside the public Pages tree and retain its catalog pins with the bridge bytes.

To demonstrate an optional legacy bridge, use the fixture's v1 `Legacy guide with unknown identity` page. It has no authored ID, and v2 has no same-route page. The following script reads the canonical route and normalized locale from the exported concept index, pins both generated release manifests, writes the exact bridge bytes and updates the full catalog under the first publication's trusted verification root, then hashes those bytes. The second publication reuses the first Pages root and its sibling `.verification` input to carry the bridge into the final verification tree. Static output keeps the bridge private to verification and removes its path and digest from the public catalog. All values are derived from those exports; there are no sample hashes to replace:

```bash
python3 - "$work" <<'PY'
import hashlib, json, pathlib, sys
work = pathlib.Path(sys.argv[1])
def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()
def load(path):
    return json.loads(path.read_text())

v1 = work / "exports/1.0.0"
v2 = work / "exports/2.0.0"
index = load(v1 / ".appsurface-docs-concepts.json")
legacy = [page for page in index["pages"] if page["title"] == "Legacy guide with unknown identity"]
if len(legacy) != 1 or legacy[0]["conceptId"] is not None:
    raise SystemExit("Expected one legacy page without an authored concept ID")
page = legacy[0]
pins = {
    "1.0.0": sha(v1 / ".appsurface-docs-release-manifest.json"),
    "2.0.0": sha(v2 / ".appsurface-docs-release-manifest.json"),
}
bridge = {
    "schema": "appsurface-docs-concept-map-v1",
    "concepts": [{
        "conceptId": "guides.legacy-walkthrough",
        "memberships": [
            {"version": "1.0.0", "releaseManifestSha256": pins["1.0.0"],
             "locale": page["locale"], "kind": "page",
             "canonicalRoutePath": page["canonicalRoutePath"]},
            {"version": "2.0.0", "releaseManifestSha256": pins["2.0.0"],
             "locale": page["locale"], "kind": "absent"},
        ],
    }],
}
bridge_bytes = (json.dumps(bridge, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
verification_root = work / "pages-v1.verification"
bridge_path = verification_root / "concept-maps/legacy.json"
bridge_path.parent.mkdir(parents=True, exist_ok=True)
bridge_path.write_bytes(bridge_bytes)
catalog_path = verification_root / "versions.json"
catalog = load(catalog_path)
entry = next(version for version in catalog["versions"] if version["version"] == "1.0.0")
entry["conceptMapPath"] = "concept-maps/legacy.json"
entry["conceptMapSha256"] = hashlib.sha256(bridge_bytes).hexdigest()
catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n")
print("Legacy bridge and catalog pins generated from the exact local bytes.")
PY
```

Publish v2 using the v1 staging tree as prior Pages content. The v1 catalog entry and bridge are carried forward; the Release publisher adds the v2 exact tree and its generated manifest pin, then builds the combined version-switch output:

```bash
dotnet run --project "$repo/tools/ForgeTrust.AppSurface.Release/ForgeTrust.AppSurface.Release.csproj" --no-launch-profile -- docs-publication \
  --version 2.0.0 --tag v2.0.0 \
  --docs-exact-tree "$work/exports/2.0.0" \
  --archive-output "$work/publication/2.0.0/appsurface-docs-v2.0.0.tar.gz" \
  --pages-staging-root "$work/pages-v2" \
  --plan-output "$work/publication/2.0.0/plan.json" \
  --summary-output "$work/publication/2.0.0/summary.md" \
  --existing-pages-root "$work/pages-v1" \
  --expected-release-manifest-sha256 "$pin2"
```

Verify both exact trees through the public CLI verifier against the untouched verification input tree. Publication writes `catalog_path` at `$work/pages-v2.verification/versions.json` and reports `$work/pages-v2.verification` as `verification_root`; the Pages root at `$work/pages-v2` contains the derived serving output uploaded by the workflow:

```bash
dotnet run --project "$repo/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj" --no-launch-profile -- docs verify-archive \
  --catalog "$work/pages-v2.verification/versions.json" --version 1.0.0 --trusted-release-root "$work/pages-v2.verification"
dotnet run --project "$repo/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj" --no-launch-profile -- docs verify-archive \
  --catalog "$work/pages-v2.verification/versions.json" --version 2.0.0 --trusted-release-root "$work/pages-v2.verification"
```

Confirm the checked-in inputs still match the snapshot, then validate the separate static serving inventory. In these commands, `--pages-staging-root "$work/pages-v2"` is the derived serving root, so its inventory binds each generated switch artifact and every member of each mounted exact-version tree to the source release pins and transformation; those serving records are separate from either original archive manifest. Verify each recorded output's length and digest against its derived serving copy:

```bash
python3 - "$work" <<'PY'
import hashlib, json, pathlib, sys
work = pathlib.Path(sys.argv[1]).resolve()
root = (work / "pages-v2").resolve()
inventory_path = root / ".appsurface-docs-serving-inventory.json"
if not inventory_path.is_file():
    raise SystemExit(f"Static publication did not write {inventory_path}")
inventory = json.loads(inventory_path.read_text())
if inventory.get("schema") != "appsurface-docs-serving-inventory-v1":
    raise SystemExit("Unexpected serving inventory schema")
if inventory.get("transformVersion") != "docs-version-switch-static-v1":
    raise SystemExit("Unexpected serving transformation version")
source_pins = {item["version"]: item["releaseManifestSha256"] for item in inventory["sourceReleases"]}
for version in ("1.0.0", "2.0.0"):
    manifest = work / "exports" / version / ".appsurface-docs-release-manifest.json"
    expected = hashlib.sha256(manifest.read_bytes()).hexdigest()
    if source_pins.get(version) != expected:
        raise SystemExit(f"Serving inventory does not bind release {version} to its original manifest pin")
for item in inventory["files"]:
    output = (root / pathlib.PurePosixPath(item["path"])).resolve()
    output.relative_to(root)
    data = output.read_bytes()
    if len(data) != item["length"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
        raise SystemExit(f"Derived output failed its inventory digest: {item['path']}")
print(f"Verified {len(inventory['files'])} derived outputs from the separate serving inventory.")
PY
```

The integration fixture captures every original export file's SHA-256 before publication and checks that inventory generation and browser use leave those inputs unchanged. The original archive verification root is `$work/pages-v2.verification`; `$work/pages-v2` is the derived serving copy uploaded by the workflow. See the [Release publication contract](../tools/ForgeTrust.AppSurface.Release/README.md#docs-publication) for staging and recovery details. No live upload is part of this walkthrough.

```bash
python3 - "$work" <<'PY'
import hashlib, json, pathlib, sys
work = pathlib.Path(sys.argv[1])
before = json.loads((work / "original-hashes.json").read_text())
for version, expected in before.items():
    root = work / "exports" / version
    actual = {
        path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(root.rglob("*")) if path.is_file()
    }
    if actual != expected:
        raise SystemExit(f"Original exact tree {version} changed during publication")
print("Both original exact trees retain every recorded file and digest.")
PY
```

In this pair, the v1 `guides.setup` page resolves to v2 `guides/configuration/getting-started` and the reader gets a moved-guide notice. The `guides.removed` concept resolves to a documented absence from v2 identity evidence. The optional `guides.legacy-walkthrough` bridge makes the otherwise-unknown legacy page resolve to an explicit absent result. A legacy source without that bridge remains unknown, even when no v2 page shares its route. The disposable tree contains both the verified exact archives and separate derived serving output; do not patch either exported release to add identity.

The checked-in two-release integration fixture uses the same export and publication command arguments, derives its pins from actual manifest bytes, verifies the original export hashes after publication, and serves the derived Pages root in hosted/static browser checks. The walkthrough keeps archive verification on the publication's untouched `$work/pages-v2.verification` catalog and trusted root. To inspect or serve the static result locally, use `$work/pages-v2`; keep both roots disposable and do not point the commands at a production Pages root.
