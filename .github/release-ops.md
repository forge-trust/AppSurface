# Release contract maintainer notes

This file lives under `.github/` on purpose so AppSurface Docs does not publish it as public documentation.

<a id="release-contract-check"></a>

## Release contract check

The [release-contract workflow](workflows/release-contract.yml) accepts three
release-evidence paths. It evaluates them in this order, while Conventional Commits
title validation remains an independent requirement:

| Priority | Evidence | When to use it | Review consequence |
| --- | --- | --- | --- |
| 1 | Authored [`releases/unreleased.md`](../releases/unreleased.md) entry | The change is part of the adopter-visible release story. | Review the prose for accuracy. It takes precedence over both exemptions. |
| 2 | Exact, case-sensitive `no-unreleased-entry` label | A maintainer has determined the change is outside the public release story. | Follow the [override runbook](#no-unreleased-entry-label) and remove the label if impact is later found. |
| 3 | Automatic Dependabot classification | The exact bot identity and labels are present and every complete, bounded patch is syntactically limited to same-position pinned external-action references. | Release-note exemption only; all ordinary checks and review remain required. |

The automatic path decides only whether public release prose is required. It does
not approve action compatibility, action behavior, supply-chain safety, CI health,
review sufficiency, or merge safety. It is a textual, fail-closed classification of
base-owned normalized evidence, not a YAML validity or semantic safety proof.
Conventional-title validation, branch protection, CI, and ordinary human review are
independent. There is no force-fail label: when a reviewer identifies adopter-visible
impact, request an authored unreleased entry and remove any stale maintainer label.
The authored entry then wins by evidence precedence.

### Trusted-base and read-only boundary

The workflow remains on `pull_request` with only `contents: read` and
`pull-requests: read`. It checks out `github.event.pull_request.base.sha` into the
isolated `.release-contract-policy` path, restricts sparse checkout to
`.github/scripts`, disables persisted credentials, imports the module with
`pathToFileURL()`, and requires `contractVersion === 1`. Pull-request code never
supplies the policy executable. The job has no secrets, mutation, comments, label
writes, `pull_request_target`, or write permission.

The v1 classifier requires complete file enumeration and valid GitHub counts. It
accepts at most 3,000 unique modified workflow YAML files, 128 KiB per patch,
1 MiB aggregate patch data, and 8 KiB per patch line. Its linear unified-diff parser
validates paired file headers when present, every hunk range and consumed line count,
legal prefixes, complete input, and API addition/deletion totals. It rejects omitted,
binary, combined, truncated, malformed, over-limit, CR, newline-marker, movement,
reordering, input, permission, action-substitution, local, Docker, quoted, tag, and
reusable-workflow evidence. Automatic rejection is informational when an earlier
evidence path passes.

### Override runbook

Repository maintainers may apply `no-unreleased-entry` only after reviewing the
actual diff and confirming that it is outside the adopter-facing release story.
Apply it in the pull request Labels control or with:

```bash
gh pr edit <number> --add-label no-unreleased-entry
```

Record the rationale in normal review discussion when it is not self-evident. The
label comparison is exact and case-sensitive. Remove it when scope changes, public
impact is discovered, or an authored entry is requested:

```bash
gh pr edit <number> --remove-label no-unreleased-entry
```

An authored entry has higher precedence if both appear, but stale labels should still
be removed so the audit trail matches the decision.

### Troubleshooting and local reproduction

Use the stable diagnostic leaf code in the check summary. Eligibility codes mean the
evidence is outside the deliberately narrow automatic grammar; they do not mean the
dependency update is unsafe. Add public prose or use the maintainer runbook after
review. `infrastructure-event-invalid`, `infrastructure-api-failure`, `infrastructure-import-failure`,
`infrastructure-version-mismatch`, `infrastructure-runtime-failure`, and
`infrastructure-checkout-failure` fail the job rather than changing release evidence.
Re-run once to distinguish a transient GitHub failure, then inspect the named checkout,
API, import, or runtime boundary. Platform action-resolution failures remain in the
standard step failure details because no repository script can run before the action
resolves.

Maintainers can reproduce normalized evidence with Node.js 24. The command accepts a
JSON file path or standard input. A document may contain classifier fields directly or
wrap them as `{ "input": ..., "context": ... }`. It prints the same bounded Markdown
summary as CI, never renders patches, and exits 0 for pass, 1 for contract blockers,
or 2 for unreadable or invalid JSON:

```bash
node .github/scripts/release-contract-diagnose.mjs normalized-release-contract.json
```

Run the classifier, renderer, and diagnostic-command suite with the ordinary-build gate:

```bash
node --experimental-test-coverage \
  --test-coverage-lines=100 \
  --test-coverage-functions=100 \
  --test-coverage-branches=100 \
  --test .github/scripts/release-contract-v1.test.mjs
```

### Rollback and versioning

Rollback triggers are any false exemption, classifier runtime failure, unexplained
required-check result, or incompatible v1 output. Revert or disable the workflow
integration first while retaining the v1 module. Use an authored entry for the
rollback pull request, verify both manual evidence paths after the workflow can run,
and remove dormant code only in a later change. A maintainer may temporarily adjust
branch protection only when the required workflow cannot execute at all.

Breaking classifier contracts must use a new module and contract version through the
same bootstrap-then-integration sequence. Do not replace v1 and its importer in one
pull request.

### 6–8 week effectiveness checkpoint

Six to eight weeks after activation, the AppSurface release maintainer reviews every
GitHub Actions Dependabot pull request in the window. Record automatic exemption rate,
leaf rejection codes, manual-label use, public-prose cases, false or misleading
exemptions, missed release notes, runtime failures, and maintainer effort. Retain the
classifier only with zero false or misleading exemptions, zero missed release notes,
zero runtime failures, and at least a 50% exemption rate among syntactically eligible
candidates. If fewer than three eligible candidates occur, extend observation by four
weeks. End with an explicit **retain**, **narrow**, **expand**, or **remove** decision.

## `no-unreleased-entry` label

Use `no-unreleased-entry` only when a pull request does not belong in the public release story, such as:

- repository administration
- workflow-only cleanup
- maintainer ergonomics that do not affect adopters

Do not use it for package behavior, CLI behavior, examples, docs-facing behavior, or release policy changes.

## Deferred automation

The current workflow establishes the contracts that future release automation will consume:

- Conventional Commits PR titles
- one public unreleased proof artifact
- tagged release notes plus a compact changelog
- verified `.nupkg` artifacts from the manifest-backed package artifact workflow
- publishable .NET tool artifacts such as `ForgeTrust.AppSurface.Cli`

CLI docs that show `dnx`, `dotnet tool execute`, or `dotnet tool install` assume
either a NuGet-published package or an explicit local package source.
`ForgeTrust.AppSurface.Cli` is the public `appsurface` tool; RazorWire-specific
export workflows remain deferred to the separate `razorwire` tool.

Tracked follow-up: #161, "Automate coordinated monorepo releases from the public release contract".

## Release evidence bundle

Release preparation produces `releases/v{version}.evidence.json`, the release evidence bundle. This is repository release-artifact consistency evidence: it ties the release note, sidecar, release JSON, package release-note paths, optional docs archive catalog fields, and commit identity together for release-prep review and publish-time tag validation.

The release evidence bundle is not a GitHub artifact attestation. Keep `id-token: write` and `attestations: write` out of default release workflows unless an explicit future attestation mode is implemented and reviewed.

## Release-prep review boundary

Release preparation pull requests must include the four generated artifacts for
the prepared version: release note, sidecar, release manifest, and release
evidence bundle. They may also update an older generated release sidecar only
when carrying `redirect_aliases` forward to the new canonical release-candidate
note. Do not use that sidecar allowance for content edits, trust-bar edits, or
manifest/evidence rewrites from older releases.

## Package artifact dry run

The pack-only release slice uses `packages/package-index.yml` as the single package
contract. Maintain `publish_decision`, `publish_reason`, and
`expected_dependency_package_ids` there instead of creating a second release package
list. Review [`packages/readiness.md`](../packages/readiness.md) before release
review when deciding whether package-index evidence is complete enough for the
package artifact workflow. That dashboard is generated maintainer evidence from
the package index, not live NuGet publish, artifact, or smoke-install status.

Run the package artifact verifier with an exact stable or prerelease version:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify-packages \
  --package-version 0.0.0-ci.local \
  --artifacts-output artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --report artifacts/package-validation-report.md
```

The verifier restores and builds the solution once, packs manifest-selected packages
with `--no-restore --no-build`, inspects each `.nupkg`, and writes a markdown report.
It checks package metadata, expected same-version dependencies, first-party DLL
informational versions, .NET tool command settings, and Tailwind runtime binary
payloads. It accepts stable and prerelease SemVer identities, and intentionally
rejects SemVer build metadata because NuGet strips build metadata from package
identity.

`verify-packages` is the primary package-proof path for Tailwind runtime packages.
It forces `TailwindRuntimeBinaryResolutionEnabled=true` during restore, build, and
pack so runtime `.nupkg` files cannot be created without their native Tailwind
binary payload. Do not set `TailwindRuntimeBinaryResolutionEnabled=false` for
release package validation; that switch is only for non-package CI restore,
build, and test jobs that do not compile Tailwind-consuming projects.

Stable publishing is intentionally separate from prerelease publishing so the
environment protections, trusted-publishing policy, and smoke-install proof stay
auditable for the public stable line.

## Protected NuGet stable publish

The stable publish slice lives in `.github/workflows/nuget-stable-publish.yml`.
It has no `pull_request`, `pull_request_target`, or `workflow_dispatch` entry
point. It runs only when the main repository receives an annotated stable tag
that matches:

```text
v<major>.<minor>.<patch>
```

On main, the stable workflow treats `main` as the source base. It force-fetches
the exact remote tag ref, resolves annotated tags to the tagged commit with
`refs/tags/<tag>^{commit}`, verifies that commit is reachable from
`origin/main`, and then checks that `build.yml` and `package-gate.yml` have
successful completed runs for that exact commit on `main` before any protected
publish job can start. Maintained release branches should backport the workflow
and set `STABLE_BASE_REF` to that release branch, such as `release/0.1.0`.

Before creating the stable tag, create the GitHub environments used by the
workflow. Create `nuget-stable` with required reviewers and prevent self-review
enabled. Create `nuget-stable-smoke` with a 25-minute wait timer and no required
reviewers so NuGet validation and indexing can settle before the smoke-install
runner starts. Add `NUGET_USER` as an environment or repository variable
containing the nuget.org profile name, not an email address. On nuget.org, add a
Trusted Publishing policy for the package owner with these GitHub Actions
details:

- Repository Owner: `forge-trust`
- Repository: `AppSurface`
- Workflow File: `nuget-stable-publish.yml`
- Environment: `nuget-stable`

NuGet's policy UI expects only the workflow file name, not the
`.github/workflows/` path. Keep the policy environment-scoped so a token
exchange is valid only after the GitHub environment approval gate has passed.
The workflow fails closed if `nuget-stable` is missing, has no required-reviewer
protection, or does not prevent self-review. It also fails closed if
`nuget-stable-smoke` is missing, lacks the 25-minute wait timer, or has required
reviewers configured.

Stable package proof uses the same artifact validation and smoke-install
contract as prerelease publishing, but with stable package identity:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify-packages \
  --package-version 0.1.0 \
  --artifacts-output artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --report artifacts/packages/package-validation-report.md
```

The protected publish job downloads that exact artifact bundle and runs:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  publish-stable \
  --artifacts-input artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --publish-log artifacts/packages/package-publish-log.md
```

Stable package versions are immutable once any package has been accepted by
NuGet. For transient NuGet or environment failures, re-run the failed workflow
with the same tag and package version; already published packages should become
`duplicate-reported`. For content or metadata defects after any stable package
is accepted, do not retag `v0.1.0`; fix forward with a new stable patch version.
Only dispatch `release-publish.yml` after `nuget-stable-publish.yml` has
published and smoke-installed successfully. For maintained release-branch
publishing, dispatch GitHub Release creation with the matching release branch as
`base-ref`, such as `release/0.1.0`.

## Protected NuGet prerelease publish

The prerelease publish slice lives in `.github/workflows/nuget-prerelease-publish.yml`.
It has no `pull_request`, `pull_request_target`, or `workflow_dispatch` entry point.
It runs only when the main repository receives an annotated prerelease tag that
matches:

```text
v<major>.<minor>.<patch>-<preview|alpha|beta|rc>.<positive-number>
```

Examples: `v0.4.0-preview.1`, `v0.4.0-rc.2`.

The workflow force-fetches the exact remote tag ref before validation, resolves
annotated tags to the tagged commit with `refs/tags/<tag>^{commit}`, verifies that
commit is reachable from `origin/main`, and then checks that `build.yml` and
`package-gate.yml` have successful completed runs for that exact commit before any
protected publish job can start.

Publishing is gated by the GitHub Environment named `nuget-prerelease` and by a
nuget.org Trusted Publishing policy. The publish job requests a short-lived NuGet
API key through GitHub Actions OIDC immediately before it runs
`publish-prerelease`; do not create or store a long-lived `NUGET_API_KEY` secret
at the repository, organization, or environment level.

Before creating the first prerelease tag, create the GitHub environments used by
the workflow. Create `nuget-prerelease` with required reviewers and prevent
self-review enabled. Create `nuget-prerelease-smoke` with a 25-minute wait timer
and no required reviewers so NuGet validation and indexing can settle before the
smoke-install runner starts. Add `NUGET_USER` as an environment or repository
variable containing the nuget.org profile name, not an email address. On
nuget.org, add a Trusted Publishing policy for the package owner with these
GitHub Actions details:

- Repository Owner: `forge-trust`
- Repository: `AppSurface`
- Workflow File: `nuget-prerelease-publish.yml`
- Environment: `nuget-prerelease`

NuGet's policy UI expects only the workflow file name, not the
`.github/workflows/` path. Keep the policy environment-scoped so a token exchange
is valid only after the GitHub environment approval gate has passed. The workflow
fails closed if `nuget-prerelease` is missing, has no required-reviewer
protection, or does not prevent self-review. It also fails closed if
`nuget-prerelease-smoke` is missing, lacks the 25-minute wait timer, or has
required reviewers configured.

The PackageIndex tool owns the package contract for the workflow:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify
```

`verify` confirms both the adopter-facing package chooser and the maintainer
readiness dashboard are current. The readiness dashboard checks package-index
evidence such as release metadata, docs links, packability, tool command shape,
and expected first-party package dependencies before publish artifacts are built.

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify-packages \
  --package-version 0.4.0-preview.1 \
  --artifacts-output artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --report artifacts/packages/package-validation-report.md
```

`verify-packages` writes both the markdown validation report and
`package-artifact-manifest.json`. The JSON manifest records the package version,
manifest order, package id, project path, publish decision, artifact file name, tool
flag, declared tool command name for .NET tools, and SHA-512 hash for every
`publish` and `support_publish` package selected from `packages/package-index.yml`.
The `tool_command_name` value must be one file-name-safe command token, not a
path: no whitespace, path separators, reserved `.`/`..` segments, control
characters, trailing periods, Windows reserved device names or dotted aliases,
or Windows-invalid file-name characters.

The protected publish job downloads that exact artifact bundle and runs:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  publish-prerelease \
  --artifacts-input artifacts/packages \
  --artifact-manifest artifacts/packages/package-artifact-manifest.json \
  --publish-log artifacts/packages/package-publish-log.md
```

`publish-prerelease` re-reads `packages/package-index.yml`, verifies that the JSON
artifact manifest exactly matches the current package plan and SHA-512 hashes, then
runs `dotnet nuget push --skip-duplicate` in manifest order. Captured publish
output is redacted before it is written to the uploaded ledger artifact, and the
ledger is rewritten after every package attempt so partial-publish evidence survives
later command failures. The publish ledger uses these statuses:

- `pushed`: NuGet accepted the package.
- `duplicate-reported`: `--skip-duplicate` reported that the package already exists.
- `failed`: the package push failed.
- `skipped-after-failure`: the package was not attempted because an earlier package
  failed.

Partial publish recovery uses the same tag and the same package version. Re-run the
failed workflow after fixing transient NuGet or environment problems. Already
published packages should become `duplicate-reported`, and the first package that
previously failed should be the first new `pushed` entry. Do not retag or create a
new package version for a transient partial publish.

Content or metadata defects require a new prerelease version. Once any package has
been accepted by NuGet, treat the entire coordinated package version as immutable.
Fix the defect in source, create the next prerelease tag, and let the workflow
publish the next package family.

After publishing, the workflow runs `smoke-install` from a fresh NuGet configuration
that clears inherited sources and points only at nuget.org. It restores all direct
non-tool `publish` packages from `packages/package-index.yml` in one aggregate
smoke project with a shared fresh `NUGET_PACKAGES` directory, isolated
`DOTNET_CLI_HOME`, and retry/backoff for NuGet indexing delay. This keeps the
post-publish check representative of user installs without multiplying restore
timeouts by the number of public library packages. Tool packages install with
`dotnet tool install --tool-path`, then the workflow resolves the declared
`tool_command_name` shim and runs `<command> --help`. The smoke job references
the `nuget-prerelease-smoke` environment, which must keep a 25-minute wait timer
so NuGet can finish validation and indexing before the runner starts. The job
sets a 70-minute timeout so the 25-minute environment wait still leaves up to
45 minutes for active smoke install work. The
smoke step fails if the installed command exits non-zero or if help output does
not identify the command users are expected to type.
