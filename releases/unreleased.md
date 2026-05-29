# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.1`. It stays provisional until the next tag is cut.

## What is taking shape

Post-RC1 work is now collected here as deltas from the current release candidate. The package-facing story for the first coordinated prerelease lives in [v0.1.0 RC 1](./v0.1.0-rc.1.md); this page records what has changed after that candidate.

## Included in the next coordinated version

### Release and docs surface

- AppSurface now has `ForgeTrust.AppSurface.Auth` as a boundary-preview composition package for future AppSurface auth contracts. It registers only the neutral module/options boundary and does not sign users in, enforce authorization, or integrate with identity providers.
- AppSurface Docs JavaScript API pages can now resolve public doclets into reader-facing API families. Explicit `@namespace` and `@module` tags remain authoritative, ordered `GroupNameRules` can name known source trees, and untagged fallback groups use path-aware identities so same-stem files in different folders do not merge.
- Older `v0.1` preview routes now redirect to the current RC1 release note, so package consumers land on one canonical release story instead of a stale pre-RC preview.
- Public package READMEs now link directly to the [v0.1.0 RC 1 release note](./v0.1.0-rc.1.md) for release risk, migration guidance, and package readiness.
- The release authoring checklist now records the preview-rollup rule: when a tagged or release-candidate note supersedes a preview, remove the preview source file and carry its browser routes as `redirect_aliases` on the canonical note.
- Release preparation now leaves `CHANGELOG.md` as a compact ledger, moves detailed release narrative into tagged release notes, and makes generated release PR reports stop at a manual maintainer review gate before merge, tag, or publish.

### Configuration diagnostics

- Config audit entries can now be explicitly classified with `ConfigAuditEntryOptions.Sensitivity`, letting package authors mark domain-specific provider-only or wrapper-discovered keys sensitive without changing key names. `Sensitive` redacts root values, traversed child values, and value-derived dictionary labels before structured/text output; `NonSensitive` documents intent but never downgrades conservative redaction from fragments or sources.
- Config audit redaction now recognizes additional secret-bearing fragments such as passphrase, DSN, assertion, certificate, cookie, client secret, private key, JWT, bearer, access/refresh tokens, session IDs, and shared access signatures. This can create new false-positive redaction in operator reports by design; full reports remain internal support artifacts because source metadata can still be sensitive.

### Console and CLI polish

- RazorWire CLI process execution now follows an explicit reliability contract for one-shot commands and launched target apps. No action is expected: command names, flags, defaults, and export semantics are unchanged. Target-app failures that were previously hidden behind startup or readiness timeouts may now surface earlier with captured output and recovery guidance.

### RazorWire streams

- RazorWire's in-memory stream hub now releases empty live channel tracking after the last subscriber disconnects or publish-time cleanup prunes stale writers. Replay buffers remain separate from live subscriber state, keep 25 retained messages per channel, prune inactive replay channels after more than 256 replay channels are retained, and are not deleted just because the last live subscriber disconnects.

### Web host development defaults

- Tailwind build execution now uses a compiled MSBuild task with stable `ASTW###` diagnostics, structured CLI arguments, bounded output capture, cancellation support, and a packed-package smoke test that proves task and dependency loading from a real nupkg consumer.

### Dependency maintenance

- The central .NET dependency set now carries `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Hosting`, and `Microsoft.Extensions.Logging.Console` `10.0.8`, while the ABP benchmark host now uses ABP `10.4.0`; solution lock files were regenerated so locked restore sees the same graph in CI and local development.

## Migration watch

- Existing RazorWire CLI users do not need command or flag changes for the process-execution migration. The main behavior difference is that target-app failures may now appear earlier with captured output instead of being hidden behind startup or readiness timeouts.
- RazorWire stream consumers do not need application code changes for the live-channel cleanup update. Public or demo stream endpoints should still validate or namespace channel names before opting into `AllowAll`; active connection cardinality limits are tracked separately from this cleanup work.
- Tailwind package consumers do not need source changes for the compiled MSBuild task. Maintainers should keep the packed-package smoke path green when changing task dependencies or diagnostics.
- AppSurface Docs JavaScript harvest consumers can keep explicit `@namespace` and `@module` tags when they need a stable API family name; otherwise the new fallback grouping may split same-stem files by path instead of merging them.
- Operators may see more conservative redaction in config audit reports as the secret-fragment list expands. Treat that as the intended default, and use explicit sensitivity options only to document package-owned keys.
