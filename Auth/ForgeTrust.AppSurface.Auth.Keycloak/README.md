# ForgeTrust.AppSurface.Auth.Keycloak

This repository-held package schema turns a complete, reviewed Keycloak recipe into a bounded capture, an offline build context, and evidence about one immutable image. It is not currently published or available as a package install. It is a host-side security workflow, not a Keycloak server, an authentication middleware package, or a package chooser. The package has no Python runtime dependency; its managed runtime dependencies are BouncyCastle.Cryptography and ZstdSharp.Port for compressed Debian payload handling. The supported host profile is Unix with Docker 29-compatible OCI support and the containerd snapshotter. Any future publication requires the separate pure publish decision and coordinated dependency authorization.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Choose the boundary

Use this package when a host already owns exact source, runtime, frontend, theme, archive, Maven, signed OS, and policy material and needs deterministic provenance checks. Use ordinary Keycloak, ASP.NET Core authentication, or the provider package when the task is sign-in, realm administration, token handling, user storage, or deployment. The package cannot mint a security recipe from an old image tuple, infer a version from a filename, approve a new source, or publish an image.

## Public workflow

1. Read complete JSON with `KeycloakCanonicalJson.ReadRecipe` and `ReadPolicy`. The recipe JSON must include `templateBaseline` as `null` or a complete object; any `.ftl` theme input requires the reviewed baseline object. Read capture bindings separately and compute the canonical recipe SHA256 for `KeycloakCaptureInputs.ReviewedRecipeId`.
2. Call `KeycloakSecurity.CaptureAsync(recipe, policy, bindings, absentSnapshotDirectory)`. Capture verifies exact material hashes, OCI index/manifest/config/diff IDs, source associations, Maven metadata, signed repository indexes, and bounded package payloads.
3. Call `KeycloakSecurity.MaterializeAsync(plan, absentContextDirectory)`. The result is a `KeycloakBuildContext`; call `Verify()` immediately before host build.
4. Obtain `CreateDockerBuildArguments(tag)`. Append those fixed arguments to `ProcessStartInfo.ArgumentList` and invoke Docker without a shell. The context grants build inputs, not push or deployment authority.
5. Call `KeycloakSecurity.ResolveLocalImageAsync(tag, cancellationToken)` for the Docker 29 containerd image. The resolver checks the immutable manifest/config/platform/diff-ID graph and export hashes; labels are not identity evidence. Classic Docker image stores that expose only config metadata are unsupported.
6. Pass the resolved identity to `KeycloakSecurity.InspectAsync(plan, image)`. Inspection checks the selected manifest, config, ordered diff IDs, files, runtime closure, user, entrypoint, certificates, each declared theme file hash, and the read-only probe. A wrong identity is a rejection; it must not be replaced with a synthesized identity.

Current production qualification passes all four retained recipe fixtures: neutral arm64 (887 checks), Skoolit arm64 (902), neutral amd64 (888), and Skoolit amd64 (903). The [fixture card](../../docs/designs/issue-887-feasibility-card.md#current-production-qualification--2026-10-24) records exact identities, scope and measured durations. The [explicit example](../../examples/auth-keycloak-security-recipes/README.md) is a sample harness and project reference, not a published-consumer claim.

## References

- [Auth adoption ladder](../../start-here/auth-adoption-ladder.md)
- [API and JSON contract](docs/api-and-contracts.md)
- [Bounded engine guide](docs/engine.md)
- [Diagnostics and recovery](docs/diagnostics.md)
- [Migration matrix and decision guide](docs/migration.md)
- [Native CI and host prerequisites](docs/host-prerequisites.md)
- [Example harness](../../examples/auth-keycloak-security-recipes/README.md)
