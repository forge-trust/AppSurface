# Release authoring checklist

Use this checklist when turning the living unreleased story into a tagged AppSurface release.

## Before the release branch or tag

- run `./eng/release check --version x.y.z` to validate the release inputs, package policy, generated targets, and warning IDs
- make sure the pull request queue has updated [`unreleased.md`](./unreleased.md)
- regroup the story so the opening narrative explains what changed and why it matters
- confirm every breaking or behavior-changing update has migration guidance
- for stable releases, stage the AppSurface Docs exact archive and run `appsurface docs verify-archive --catalog <staging>/versions.json --version x.y.z --trusted-release-root <staging>` before asking the release tool to validate docs evidence
- for stable releases, confirm the checked-in evidence fields describe the same staged docs archive that `nuget-stable-publish.yml` will export and verify before `publish-nuget`
- use `./eng/release prepare --version x.y.z --dry-run` to inspect the generated tagged note, sidecar, release manifest, release evidence bundle, changelog rollover, package release-note path updates, and reset unreleased artifact before opening the release PR

## When cutting the tagged release note

- run `./eng/release prepare --version x.y.z --date YYYY-MM-DD` from an up-to-date release base branch (`main` for normal releases, or the maintained release branch such as `release/0.1.0`)
- review the generated `releases/vx.y.z.md`, `releases/vx.y.z.md.yml`, `releases/vx.y.z.release.json`, and `releases/vx.y.z.evidence.json`
- for stable releases, confirm `releases/vx.y.z.evidence.json` records `docsArchive.exactTreePath`, `docsArchive.releaseManifestSha256`, and matching `docsArchive.catalogEntry` fields from the staged docs catalog
- confirm the generated package path updates described in the [package registry](../packages/README.md) point every `classification: public` plus `publish_decision: publish` package at the tagged note
- review the generated [package readiness evidence](../packages/readiness.md) and resolve or explicitly track package-index blockers before asking maintainers to approve package artifacts; this package-index evidence is separate from the per-version release evidence bundle
- when a tagged or release-candidate note supersedes a preview page, remove the preview source file and carry its browser routes as `redirect_aliases` on the new canonical note
- keep the trust bar accurate for the release state and archive location
- link the tagged note from [`CHANGELOG.md`](../CHANGELOG.md)
- open the release preparation pull request and stop for manual maintainer review; automation and coding agents must not merge release PRs or create release tags without an explicit post-review instruction

## After the tag ships

- create the annotated tag from the maintainer-reviewed merge commit outside the release tool; v1 never creates tags automatically
- wait for the protected NuGet workflow for the tag classification to finish first: `nuget-prerelease-publish.yml` for prerelease tags, `nuget-stable-publish.yml` for stable tags
- for stable tags, treat the workflow's `appsurface-stable-docs-proof-x.y.z` artifact as the pre-NuGet docs proof: it contains the staged catalog plus archive manifests verified before the trusted publishing token was requested
- run `./eng/release publish --version x.y.z --tag vx.y.z --base-ref <release-base> --dry-run` before the publish workflow creates the GitHub Release; publish validation checks the release evidence bundle and protected package publish proof at the annotated tag commit, not the local worktree
- for stable releases, include `--docs-catalog <staging>/versions.json --docs-trusted-release-root <staging>` when running publish validation or the workflow dispatch; publish does not use the local `dist/docs/versions.json` fallback
- keep stable releases blocked until `nuget-stable` publish and `nuget-stable-smoke` install proof exists; `v0.1.0` must not become a GitHub-only release
- verify the `/docs` release hub resolves to the new tagged note and current policy pages

## Diagnostics and escape hatches

Release tool failures use a uniform `Code`, `Problem`, `Cause`, `Fix`, and `Docs` envelope. Prefer fixing the named input rather than bypassing the tool. The intended escape hatches are to rerun `prepare --dry-run`, adjust the release PR by hand before merge, or cut a new annotated tag; the v1 publish path intentionally does not update existing GitHub Releases.
