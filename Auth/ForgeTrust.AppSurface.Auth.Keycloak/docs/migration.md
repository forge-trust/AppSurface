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


## Coherent versions and schema changes

Follow the repository's [pre-1.0 upgrade policy](../../../releases/upgrade-policy.md) and [release entry for this change](../../../releases/unreleased.entries/2026-10-24-keycloak-security-recipes.md). Both Keycloak packages are currently source-only and publication-held. Use project references to the same reviewed checkout. After separate release authorization, pin the pure library and Aspire adapter to the same exact coordinated published version; do not mix a checkout, local package, and another preview. The [package inventory](../../../packages/package-index.yml) is the release decision source.

Before upgrading, retain the old recipe/policy, all material receipts, image/config/platform identities, compatible database backup, and existing caller tests. Review release guidance for changed contracts; compile callers against the candidate coherent package set. Recipe schema `1`, operation profile versions, evidence schema `appsurface-keycloak-observed-image-v1`, and transformer `managed-zip-v1` are distinct identities. A package version change does not authorize silently rewriting any of them. Unsupported schema/profile inputs fail; do not coerce an unknown version or drop unknown fields to make it parse. Adapt explicit input construction, recompute canonical recipe/policy identities and bindings, capture afresh, then rerun image and retained system gates before accepting new evidence.

## Before and after caller shape

The existing declaration-only caller remains supported for its theme contract:

```csharp
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

// themeOptions and finalImageReference are the host's reviewed existing inputs.
var contract = AppSurfaceKeycloakThemeBuildContract.Create(themeOptions);
var tuple = AppSurfaceKeycloakThemeReleaseEvidence.Create(contract, finalImageReference);
tuple.Verify(contract, finalImageReference);
```

This verifies declaration consistency; it does not inspect image bytes. To add actual observations, run the full capture/build/inspect flow in the compiled [Program.cs](../../../examples/auth-keycloak-security-recipes/Program.cs), using the [complete neutral files](../../../examples/auth-keycloak-security-recipes/fixtures/README.md) as the input-shape reference. A host integration after its reviewed capture and build has this shape:

```csharp
using ForgeTrust.AppSurface.Auth.Keycloak;

// recipe, policy and captureInputs contain this host's complete reviewed theme.
using var plan = await KeycloakSecurity.CaptureAsync(
    recipe, policy, captureInputs, absentSnapshotDirectory, cancellationToken);
var context = await KeycloakSecurity.MaterializeAsync(
    plan, absentContextDirectory, cancellationToken);
context.Verify(cancellationToken);
// Host builds using context.CreateDockerBuildArguments(localTag, cancellationToken)
// with ProcessStartInfo.ArgumentList, as implemented in the compiled Program.cs.
var image = await KeycloakSecurity.ResolveLocalImageAsync(localTag, cancellationToken);
var observed = await KeycloakSecurity.InspectAsync(plan, image, cancellationToken);
tuple.VerifyObservedImage(observed);
await observed.WriteAsync(absentEvidenceFile, cancellationToken);
```

These fragments use host-supplied variables and illustrate the migration seam; they are not standalone executables or a claim that this documentation edit compiled them. The linked Program.cs is the complete compiled caller. Preserve the tuple's exact packaged theme files and reviewed `.ftl` baseline in the recipe. `VerifyObservedImage` requires the same final manifest/platform and actual hash of every packaged member; either ThemeOnly or Security evidence can establish those theme facts. ThemeOnly never inherits security conformance. A stock source exceeding the 128 MiB final-image member ceiling is unsupported by this new inspector even though the existing theme API remains usable; there is no ThemeOnly limit waiver.

Dispose the plan after all accepted consumers complete. Concurrent consumers need separate absent output directories; retain snapshot bytes until all return. A new attempt must not reuse another invocation's staging or overwrite prior evidence.

## Rollback and database state

The host owns image promotion and database restore. Before rollout, retain the previous immutable image, exact coordinated package set, recipe/profile/policy, evidence, and a tested compatible database backup. Run the retained upgrade and restore gates on the candidate digest. If adoption fails, stop promotion; choose the previous known image/package/profile tuple and perform the operator's tested database restore procedure. An application downgrade alone may not reverse a database migration. The SDK neither executes rollback nor authorizes deleting database state. Keep the [existing theme upgrade/rollback ordering](../../ForgeTrust.AppSurface.Auth.Aspire.Keycloak/docs/theme-upgrade.md) when migrating that caller, and retain the old image through its operator-defined rollback window.
