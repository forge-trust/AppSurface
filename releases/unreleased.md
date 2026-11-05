# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.2.0-preview.14`. It stays provisional until the next tag is cut.

## What is taking shape

- Add merged public changes here as they land.

<!-- appsurface:unreleased-entries section="taking-shape" -->

## Included in the next coordinated version

### Release and docs surface

- Add release-facing changes here as they land.

### Portable coverage fan-in

- Add the public [Coverage package](../Coverage/ForgeTrust.AppSurface.Coverage/README.md) and installed [fan-in commands](../guides/coverage-fan-in.md) for explicit collection, capture/import, conservative composition, aggregate/patch acceptance, and independent verification. The private engine carries its pinned normalization runtime; direct library consumers do not require Aspire or the CLI.
- Native producers support multiple declared projects with byte-exact original reports, complete successful timings and an exact project index. The [capture request](../Coverage/ForgeTrust.AppSurface.Coverage/README.md#request-and-result-reference) accepts existing trusted indexed artifacts, while supervised collection discovers and binds each project's original attachment. Installed package proofs exercise two native projects through import, acceptance and altered-output rejection.
- AppSurface's CI supplies its complete source/run/native inventory through the [dogfood adapter](../tools/ForgeTrust.AppSurface.Coverage.Dogfood/README.md). Existing coverage thresholds, tolerance, instrumentation and primary-solution execution remain authoritative. Schema v2 uses fresh local custody; historical v1 evidence retains read-only compatibility and is never recertified as new execution.

<!-- appsurface:unreleased-entries section="included" -->

## Migration watch

- Record-breaking or behavior-changing guidance here before it moves into the tagged release note.

<!-- appsurface:unreleased-entries section="migration-watch" -->
