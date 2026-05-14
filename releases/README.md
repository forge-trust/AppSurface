# Release notes and change management

AppSurface now treats release notes as a product surface instead of a post-ship afterthought. This folder is the public record that answers three questions quickly:

1. What is changing next?
2. How risky is it to adopt?
3. Where will the final tagged story live once a version ships?

It also acts as a concrete RazorDocs example for teams that want stronger release notes in their own products.

## Start here

- [Package chooser](../packages/README.md) is the fastest install map for deciding which AppSurface package to add first.
- [Unreleased](./unreleased.md) is the living proof artifact for the next coordinated AppSurface version.
- [Changelog](../CHANGELOG.md) is the compact ledger that points to unreleased and tagged stories.
- [Pre-1.0 upgrade policy](./upgrade-policy.md) explains the stability contract before `v1.0.0`.
- [Release authoring checklist](./release-authoring-checklist.md) is the maintainer workflow for turning the unreleased page into a tagged release.

## Release format

### Story first

Each release note should open with the narrative that matters to evaluators and adopters. Explain what changed, why it matters, and which parts of the product surface are affected before dropping into mechanical lists.

### Safety second

Every release note should make upgrade risk obvious near the top. Call out whether the note is unreleased or tagged, which surfaces are affected, how fresh the information is, and where migration guidance lives.

### Archive third

Once AppSurface starts cutting tags, the long-form release note will live in this folder and the compact summary will live in [`CHANGELOG.md`](../CHANGELOG.md). Tagged notes become the durable archive for migration details and release narrative.

## What belongs in the release surface

- Package behavior changes
- CLI behavior changes
- Docs-facing behavior changes that affect adopters or evaluators
- Example changes that alter the recommended path
- Release policy changes

## What does not belong in public release notes

Private maintainer-only recovery steps, secret handling, and operational escape hatches should live outside harvested docs. In this repository, those notes belong under `.github/`.
