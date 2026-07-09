# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.2`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- Documentation and release authoring guidance now require concept links instead of mention-only prose: start with adopter outcomes, explain internal feature labels in plain language, link named packages, concepts, workflows, diagnostics, guides, examples, and CLI commands to their canonical docs, and keep maintainer evidence after the adoption path.
- AppSurface DevAuth now centralizes its environment activation policy. DevAuth remains Development-by-default, but
  package consumers can explicitly add local/proof environment names through `AllowedEnvironmentNames`; the marker
  self-suppresses outside allowed environments and mapped control/mutation endpoints stay fail-closed.
- Split stable and prerelease NuGet publish tag triggers so prerelease tags no longer start the stable publish workflow before the prerelease gate.
- AppSurface DevAuth marker overlays now start collapsed by default while keeping the active fake persona visible, and
  `AppSurfaceDevAuthMarkerOptions.StartExpanded` lets local proof pages opt back into immediate persona controls.
- Add `ForgeTrust.AppSurface.Auth.Aspire.Keycloak`, an AppHost-only real local OIDC proof package that builds on
  the official Aspire Keycloak hosting integration, generates deterministic realm/client/user import JSON, projects
  only safe OIDC settings into a paired `Auth.AspNetCore.Oidc` web proof, adds fixed-port/readiness diagnostics, and
  keeps Keycloak/Aspire hosting dependencies out of runtime web packages.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
