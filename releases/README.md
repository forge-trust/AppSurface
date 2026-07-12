# Release notes and change management

AppSurface now treats release notes as a product surface instead of a post-ship afterthought. This folder is the public record that answers three questions quickly:

1. What is changing next?
2. How risky is it to adopt?
3. Where will the final tagged story live once a version ships?

It also acts as a concrete AppSurface Docs example for teams that want stronger release notes in their own products.

## Start here

- [Package chooser](../packages/README.md) is the fastest install map for deciding which AppSurface package to add first.
- [v0.2.0-preview.2](./v0.2.0-preview.2.md) is the current package-facing story for the coordinated AppSurface prerelease.
- [v0.1.0](./v0.1.0.md) is the canonical archive for the first coordinated stable AppSurface release.
- The `v0.1.0` release-candidate routes redirect to [v0.1.0](./v0.1.0.md), where the RC dates are recorded.
- [Unreleased](./unreleased.md) is the living proof artifact for the next coordinated AppSurface version: the current merged-work ledger, not the final tagged narrative.
- [Changelog](../CHANGELOG.md) is the compact ledger that points to unreleased and tagged stories.
- [Pre-1.0 upgrade policy](./upgrade-policy.md) explains the stability contract before `v1.0.0`.
- [Release authoring checklist](./release-authoring-checklist.md) is the maintainer workflow for turning the unreleased page into a tagged release.

Older preview routes redirect to their canonical release notes so each release line has one live package-facing story.

## Official release artifacts

Each generated tagged release owns four checked-in artifacts:

- `releases/v{version}.md`: the human release narrative.
- `releases/v{version}.md.yml`: AppSurface Docs metadata for the release note.
- `releases/v{version}.release.json`: machine-readable release metadata and generated file list.
- `releases/v{version}.evidence.json`: generated release evidence bundle proving repository release-artifact consistency.

The release evidence bundle is not a signature or hosted-build attestation. It is the reviewable consistency proof used by release-prep and publish validation.

## Release format

### Story first

Each release note should open with the narrative that matters to evaluators and adopters. Explain what changed, why it matters, and which parts of the product surface are affected before dropping into mechanical lists.

### Consumer path next

Each major item should answer the reader's next question without making them inspect the repository:

- Who should care?
- What can they now do?
- Which package, guide, example, or CLI command should they start with?
- What boundary or pitfall should they know before adopting it?

Use internal feature names only after the reader-facing behavior is clear. For example, introduce [RazorWire auth projection](../Web/ForgeTrust.RazorWire.Auth.AspNetCore/README.md) as rendering allowed, forbidden, and anonymous UI from host-owned ASP.NET Core policies before relying on the phrase "passive auth projection." When a change ships with a guide or example, link that path from the release note next to the feature summary.

Release notes should also connect related concepts instead of only naming them. If an item mentions a package, guide, example, workflow, policy, diagnostic family, CLI command, or cross-package concept, link the first meaningful mention to the canonical page a consumer should read next. Prefer durable start-here links, package READMEs, guides, examples, and public docs routes over transient PRs or maintainer-only notes.

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
