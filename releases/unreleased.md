# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.1`. It stays provisional until the next tag is cut.

## What is taking shape

Post-RC1 work is now collected here as deltas from the current release candidate. The package-facing story for the first coordinated prerelease lives in [v0.1.0 RC 1](./v0.1.0-rc.1.md); this page records what has changed after that candidate.

## Included in the next coordinated version

### Release and docs surface

- Older `v0.1` preview routes now redirect to the current RC1 release note, so package consumers land on one canonical release story instead of a stale pre-RC preview.
- Public package READMEs now link directly to the [v0.1.0 RC 1 release note](./v0.1.0-rc.1.md) for release risk, migration guidance, and package readiness.
- The release authoring checklist now records the preview-rollup rule: when a tagged or release-candidate note supersedes a preview, remove the preview source file and carry its browser routes as `redirect_aliases` on the canonical note.

### Console and CLI polish

- RazorWire CLI process execution now follows an explicit reliability contract for one-shot commands and launched target apps. No action is expected: command names, flags, defaults, and export semantics are unchanged. Target-app failures that were previously hidden behind startup or readiness timeouts may now surface earlier with captured output and recovery guidance.

### Web host development defaults

- Tailwind build execution now uses a compiled MSBuild task with stable `ASTW###` diagnostics, structured CLI arguments, bounded output capture, cancellation support, and a packed-package smoke test that proves task and dependency loading from a real nupkg consumer.

### Dependency maintenance

- The central .NET dependency set now carries `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Hosting`, and `Microsoft.Extensions.Logging.Console` `10.0.8`, while the ABP benchmark host now uses ABP `10.4.0`; solution lock files were regenerated so locked restore sees the same graph in CI and local development.

## Migration watch

- Existing RazorWire CLI users do not need command or flag changes for the process-execution migration. The main behavior difference is that target-app failures may now appear earlier with captured output instead of being hidden behind startup or readiness timeouts.
- Tailwind package consumers do not need source changes for the compiled MSBuild task. Maintainers should keep the packed-package smoke path green when changing task dependencies or diagnostics.
