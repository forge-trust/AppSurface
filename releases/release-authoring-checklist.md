# Release authoring checklist

Use this checklist when turning the living unreleased story into a tagged AppSurface release.

## Before the release branch or tag

- make sure the pull request queue has updated [`unreleased.md`](./unreleased.md)
- regroup the story so the opening narrative explains what changed and why it matters
- confirm every breaking or behavior-changing update has migration guidance
- update [`CHANGELOG.md`](../CHANGELOG.md) so the compact ledger matches the narrative release note

## When cutting the tagged release note

- start from the [tagged release template](./templates/tagged-release-template.md)
- replace provisional language with tagged facts: version, date, shipped scope, and finalized migration steps
- keep the trust bar accurate for the release state and archive location
- link the tagged note from [`CHANGELOG.md`](../CHANGELOG.md)

## After the tag ships

- trim [`unreleased.md`](./unreleased.md) back to the next in-flight version
- keep only still-unreleased items in the proof artifact
- verify the `/docs` release hub resolves to the new tagged note and current policy pages
