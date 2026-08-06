# Release authoring checklist

Use this checklist when turning the living unreleased story into a tagged AppSurface release.

## Before the release branch or tag

- run `./eng/release check --version x.y.z` to validate the release inputs, package policy, generated targets, and warning IDs
- make sure the pull request queue has updated [`unreleased.md`](./unreleased.md)
- regroup the story so the opening narrative explains what changed and why it matters
- rewrite maintainer-led bullets into consumer-led entries for every prerelease, release-candidate, and stable note: outcome first, affected package or app shape second, maintainer evidence last
- replace opaque shorthand with a plain-language explanation before the label, especially for cross-package concepts such as [auth projection](../Web/ForgeTrust.RazorWire.Auth.AspNetCore/README.md), [static export safety](../Web/ForgeTrust.RazorWire/README.md), or [release evidence](./README.md)
- link every substantial feature, named concept, package boundary, workflow, diagnostic family, and CLI command to its best start-here material: package README, guide, example, CLI command reference, or migration section
- confirm every breaking or behavior-changing update has migration guidance
- for stable releases, stage the AppSurface Docs exact archive and run `appsurface docs verify-archive --catalog <staging>/versions.json --version x.y.z --trusted-release-root <staging>` before asking the release tool to validate docs evidence
- for stable releases, confirm the checked-in evidence fields describe the same staged docs archive that `nuget-stable-publish.yml` will export and verify before `publish-nuget`
- review [coordinated release links](./coordinated-release-links.md) before changing package release metadata; public coordinated rows retain `release_track: coordinated`, while explicit historical, held, and proof-host rows keep their documented paths
- use `./eng/release prepare --version x.y.z --dry-run` to inspect the generated tagged note and sidecar, release manifest, V2 release evidence bundle, generated frozen current pointer, preserved permanent current-pointer metadata sidecar, changelog rollover, and reset unreleased artifact before opening the release PR

## When cutting the tagged release note

- run `./eng/release prepare --version x.y.z --date YYYY-MM-DD` from an up-to-date release base branch (`main` for normal releases, or the maintained release branch such as `release/0.1.0`)
- review the generated `releases/vx.y.z.md`, `releases/vx.y.z.md.yml`, `releases/vx.y.z.release.json`, and `releases/vx.y.z.evidence.json`
- for stable releases, confirm `releases/vx.y.z.evidence.json` records `docsArchive.exactTreePath`, `docsArchive.releaseManifestSha256`, and matching `docsArchive.catalogEntry` fields from the staged docs catalog
- confirm the generated current pointer targets the tagged note and the [package registry](../packages/README.md) keeps every `classification: public` plus `publish_decision: publish` package on `release_track: coordinated`; release prep must not rewrite those package rows
- review the generated [package readiness evidence](../packages/readiness.md) and resolve or explicitly track package-index blockers before asking maintainers to approve package artifacts; this package-index evidence is separate from the per-version release evidence bundle
- check that the tagged note gives adopters direct paths to related guides, examples, package docs, and command references instead of only naming the capability
- when a tagged or release-candidate note supersedes a preview page, remove the preview source file and carry its browser routes as `redirect_aliases` on the new canonical note
- keep the trust bar accurate for the release state and archive location
- link the tagged note from [`CHANGELOG.md`](../CHANGELOG.md)
- open the release preparation pull request and stop for manual maintainer review; automation and coding agents must not merge release PRs or create release tags without an explicit post-review instruction

## When creating and pushing the tag

- from the maintainer-reviewed merge commit, generate the canonical tag trailers with `./eng/release tag-message --version x.y.z > /tmp/appsurface-vx.y.z-tag-message.txt`, create `git tag -a vx.y.z -F /tmp/appsurface-vx.y.z-tag-message.txt`, and run `./eng/release inspect --version x.y.z --tag vx.y.z --base-ref <release-base>` before pushing the tag; see the [prepared-to-tagged state contract](../tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state)
- the committed versioned sidecar remains `release.state: prepared`; `inspect` creates a temporary verified `tagged` projection for the detached docs-export checkout, so never commit a tagged sidecar back to the release branch
- if inspection fails before the tag is pushed, delete and recreate the local tag after regenerating the message; if it fails after push, preserve the tag and use a maintainer-approved fix-forward release or recovery path instead of retagging history
- wait for the protected NuGet workflow for the tag classification to finish first: `nuget-prerelease-publish.yml` for prerelease tags, `nuget-stable-publish.yml` for stable tags
- for stable tags, treat the workflow's `appsurface-stable-docs-proof-x.y.z` artifact as the pre-NuGet docs proof: it contains the staged catalog plus archive manifests verified before the trusted publishing token was requested
- run `./eng/release publish --version x.y.z --tag vx.y.z --base-ref <release-base> --dry-run` before the publish workflow promotes the GitHub Release; publish validation checks the release evidence bundle and protected package publish proof at the annotated tag commit, not the local worktree
- for stable releases, let `release-publish.yml` derive docs publication from the tag: it exports the exact docs tree, runs `./eng/release docs-publication`, uploads `appsurface-docs-vx.y.z.tar.gz` plus `.sha256` to a draft release, verifies staged Pages, deploys Pages, fetches the public catalog/exact manifest, verifies the uploaded asset digest, and only then publishes the draft release
- keep stable releases blocked until `nuget-stable` publish and `nuget-stable-smoke` install proof exists; `v0.1.0` must not become a GitHub-only release
- verify the `/docs` release hub resolves to the new tagged note, current policy pages, `versions.json`, and `releases/x.y.z/`
- after the first release that uses this contract, run a short maintainer retro: record the time from merge to successful local `inspect`, any confusing diagnostic, and whether the recovery guidance was needed; the target is two to five minutes without manual hash calculations

## Diagnostics and escape hatches

Release tool failures use a uniform `Code`, `Problem`, `Cause`, `Fix`, and `Docs` envelope. Prefer fixing the named input rather than bypassing the tool. The intended escape hatches are to rerun `prepare --dry-run`, adjust the release PR by hand before merge, reuse or delete an unpublished draft release, or cut a new annotated tag; the v1 publish path intentionally does not update public GitHub Releases.
