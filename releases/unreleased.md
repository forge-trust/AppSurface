# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.4`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

## Included in the next coordinated version

### Release and docs surface

- The [release contract](../.github/release-ops.md#release-contract-check) now automatically exempts only release prose for Dependabot GitHub Actions pull requests whose complete, bounded patches are syntactically limited to same-position pinned external-action references. Compatibility, supply-chain review, CI, title validation, and ordinary merge protection remain independent requirements; maintainers can still require an authored entry whenever adopter-visible impact is identified.
- [`ForgeTrust.AppSurface.Web` health and readiness probes](../Web/ForgeTrust.AppSurface.Web/README.md#health-and-readiness-probes) are now opt-in. New hosts avoid ASP.NET Core health-check registration and `/health` plus `/ready` endpoint mapping unless `WebOptions.Health.Enabled` is explicitly set to `true`; enabled probes also avoid general route-handler binding during startup. Hosts whose deployment or monitoring infrastructure consumes those probes must enable the shared flag; paths, readiness tags, response semantics, validation, and authorization behavior are unchanged.

## Migration watch

- Existing hosts that consume `/health` or `/ready` must set `WebOptions.Health.Enabled = true` when upgrading.
