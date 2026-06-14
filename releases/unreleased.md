# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.3`. It stays provisional until the next tag is cut.

## What is taking shape

- Reader-intent relevance for AppSurface Docs search.
- OS-backed local secrets for solo development before remote vault adoption.

## Included in the next coordinated version

### Release and docs surface

- AppSurface Docs search now hydrates MiniSearch candidates from the normalized docs payload and applies deterministic reader-intent ranking before both sidebar and full-page rendering. Exact title, path, source, alias, keyword, and entry-point matches stay protected; broad task queries prefer reader-facing guides; explicit API/internal filters override broad-task boosts; and contributor/internal docs are demoted unless the query asks for them directly.
- Added `ForgeTrust.AppSurface.Config.LocalSecrets` for local secret source posture, not production vaulting. The package registers `AppSurfaceLocalSecretsModule`, named posture modes, structured local secret result states, OS-backed/local fake store seams, fail-closed provider-chain behavior where only `Missing` falls through, and paste-safe diagnostics. The CLI now includes `appsurface secrets init|set|get|list|delete|doctor`, and the docs cover local setup, migration from `dotnet user-secrets` and `.env`, CI/container alternatives, and the future remote-vault ladder.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
