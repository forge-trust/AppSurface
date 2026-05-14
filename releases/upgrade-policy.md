# Pre-1.0 upgrade policy

AppSurface is still before `v1.0.0`, so rapid change is expected. That does not mean changes should be surprising. This policy explains how AppSurface will talk about risk and where migration guidance belongs before the first stable release.

## Core policy

- AppSurface releases the monorepo in unison. One version applies to the whole repository.
- Breaking and behavior-changing updates are allowed before `v1.0.0`, but they must be called out in the public release surface.
- Migration guidance must have a durable home. The short form belongs in [`CHANGELOG.md`](../CHANGELOG.md), and the detailed walkthrough belongs in the matching release note.

## Where migration notes live

- Before a version is tagged: put provisional guidance in [`unreleased.md`](./unreleased.md).
- When a version is tagged: move the finalized guidance into the versioned release note under `releases/`.
- Keep [`CHANGELOG.md`](../CHANGELOG.md) concise and link readers to the full release note when more detail is needed.

## What counts as a release-note-worthy change

- API changes that alter signatures, defaults, ordering requirements, or expected lifecycle behavior
- CLI changes that alter commands, flags, defaults, or output that users depend on
- Example changes that replace the recommended way to compose AppSurface
- Docs-facing behavior changes that affect how consumers discover, configure, or trust the product

## What does not count as a migration note by itself

- private maintainer-only recovery notes
- secret or credential handling details
- repo maintenance that does not affect adopters

Those belong outside harvested public docs.

## Expectations before `v0.1.0`

- Prefer documenting a breaking change the same day it lands.
- If a change is large enough to require choreography, add explicit step-by-step guidance to the unreleased note.
- Do not hide pre-1.0 instability. Make it legible.
