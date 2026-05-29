# Release authoring checklist

Use this checklist when turning the living unreleased story into a tagged AppSurface release.

## Before the release branch or tag

- run `./eng/release check --version x.y.z` to validate the release inputs, package policy, generated targets, and warning IDs
- make sure the pull request queue has updated [`unreleased.md`](./unreleased.md)
- regroup the story so the opening narrative explains what changed and why it matters
- confirm every breaking or behavior-changing update has migration guidance
- use `./eng/release prepare --version x.y.z --dry-run` to inspect the generated tagged note, sidecar, manifest, changelog rollover, package release-note path updates, and reset unreleased artifact before opening the release PR

## When cutting the tagged release note

- run `./eng/release prepare --version x.y.z --date YYYY-MM-DD` from an up-to-date `main` branch
- review the generated `releases/vx.y.z.md`, `releases/vx.y.z.md.yml`, and `releases/vx.y.z.release.json`
- confirm the generated package path updates described in the [package registry](../packages/README.md) point every `classification: public` plus `publish_decision: publish` package at the tagged note
- when a tagged or release-candidate note supersedes a preview page, remove the preview source file and carry its browser routes as `redirect_aliases` on the new canonical note
- keep the trust bar accurate for the release state and archive location
- link the tagged note from [`CHANGELOG.md`](../CHANGELOG.md)

## After the tag ships

- create the annotated tag outside the release tool; v1 never creates tags automatically
- run `./eng/release publish --version x.y.z --tag vx.y.z --dry-run` before the publish workflow creates the GitHub Release
- keep stable releases blocked until a protected stable NuGet publish workflow exists and the release cockpit verifies it; `v0.1.0` must not become a GitHub-only release
- verify the `/docs` release hub resolves to the new tagged note and current policy pages

## Diagnostics and escape hatches

Release tool failures use a uniform `Code`, `Problem`, `Cause`, `Fix`, and `Docs` envelope. Prefer fixing the named input rather than bypassing the tool. The intended escape hatches are to rerun `prepare --dry-run`, adjust the release PR by hand before merge, or cut a new annotated tag; the v1 publish path intentionally does not update existing GitHub Releases.
