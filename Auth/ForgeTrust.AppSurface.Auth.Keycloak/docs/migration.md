# Migration and decision guide

This package is suitable when an owner can preserve the original bytes and all provenance associations. It is unsuitable when the only input is an old tag, a normalized archive, an image filename, or a package name without its exact signed index and coordinate evidence.

## Migration matrix

| Family | Required evidence | Boundary decision |
| --- | --- | --- |
| 29 artifact targets | Every original/replacement digest, destination, coordinate, classifier, and closed alignment membership | Preserve the exact old destination tuple; never mint a security replacement from a filename. |
| Runtime OS closure | Signed Release/keyring, exact amd64/arm64 package indexes, 3 approved OS package families, package bytes, payload manifests, and dependency facts | A package is accepted only when its tuple, signed index hash, bytes, and payload agree. |
| curl/libcurl | Signed package tuple and payload identities | Keep the package closure explicit; do not infer sibling packages. |
| Pebble/user | Runtime profile baseline, expected removed records, non-root user and group | Compare observed status and file ownership to the reviewed profile. |
| CLI | Original source CLI archive, selected module metadata, replacement archive and module map | Verify original module association from the selected source copy, then transform only the reviewed modules. |
| Entrypoint | Exact image config and reviewed `/opt/keycloak/bin/...` path | Pass a path as data; never construct a shell command. |
| Theme | Captured or approved theme digest, destination, platform association, and `.ftl` `KeycloakTemplateBaseline` | Theme-only composition is a separate profile; it does not imply security closure. The baseline archive member and entry hash must match the reviewed captured source during capture and evidence inspection. |
| Baseline/source/frontend | Original source OCI graph plus runtime/frontend index and selected platform archives | Preserve index, manifest, config, diff IDs, and source SHA identities together. |

The current supported security family is the native CI-approved profile associated with run `40079400914`. Current production qualification passes all four retained neutral/Skoolit arm64/amd64 recipes with exact image identities and 887/902/888/903 observations respectively; the [fixture card](../../../docs/designs/issue-887-feasibility-card.md#current-production-qualification--2026-10-24) records the measured result. A coordinated migration should carry the full fixture receipts and actual source/material manifests into the reviewed JSON files.

Choose ordinary Keycloak/provider deployment when the host needs mutable server administration. Choose this package when the host needs a finite, immutable, auditable transformation before its own Docker build. Keep the package reference to the checkout or the separately authorized prerelease while public package publication is held.
