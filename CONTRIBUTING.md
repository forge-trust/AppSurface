# Contributing to AppSurface

AppSurface maintains a coordinated release contract. This file explains the contribution rules that feed the public release surface.

## Feedback path

AppSurface treats docs and onboarding feedback as product input, not as a second-class support queue. File issues when a package, example, README, or release note leaves you unable to reproduce the intended path.
For quick access, use GitHub's issue template chooser: [choose an issue template](https://github.com/forge-trust/AppSurface/issues/new/choose).

- Use the [**Bug report** issue form](https://github.com/forge-trust/AppSurface/issues/new?template=bug_report.yml) when behavior is broken or surprising.
- Use the [**Feature request** issue form](https://github.com/forge-trust/AppSurface/issues/new?template=feature_request.yml) when you can name a focused product capability, API shape, workflow, or example that would remove friction.
- Use the [**Docs or developer experience feedback** issue form](https://github.com/forge-trust/AppSurface/issues/new?template=docs_feedback.yml) when the code may work, but the route to understanding it is unclear.
- Do not use public issue forms for suspected vulnerabilities, leaked secrets, or exploit details. Use the [security policy](./SECURITY.md) and report sensitive findings privately.
- Include the command, page, example, package, or API where the confusion started. The sharpest reports name the exact step that failed and the next thing you expected to see.
- If you are unsure whether something is a bug, feature request, or docs gap, file the docs/DX form and explain the behavior you expected.

Avoid broad requests such as "improve the docs" without a concrete page, task, or decision point. Narrow feedback is easier to verify and much more likely to turn into a useful change.

## Local setup

Use a current .NET SDK supported by this repository, then restore and build from the repository root before opening a pull request:

```bash
dotnet restore
dotnet build
```

Run the sample or package-specific command you changed, and include that command in your pull request notes. For broad changes, also run the full test suite and coverage command listed in [Local verification](#local-verification).

## Working on docs

Documentation changes should explain both how to use an API and why a reader would choose it. When a docs change touches public behavior, update the package-level README, repository-level entry point, or release note surface that helps someone discover the change.

Good docs pull requests usually include:

- Reference content for API shape, defaults, constraints, and examples.
- Decision guidance that explains when to use the API and when another approach fits better.
- Pitfalls that call out ordering requirements, generated output, hosting assumptions, or common mistakes.
- Verification notes for commands, links, snippets, or examples that were checked.
- For shared package-policy text, follow the [PackageIndex release-guidance maintainer guide](./tools/ForgeTrust.AppSurface.PackageIndex/README.md#release-guidance) instead of hand-copying `## Release Guidance` prose across package READMEs.

## Test fixture path policy

Tests that build a full filesystem path under a repository root, temp workspace, project directory, output directory, or other trusted base should use `TestPathUtils.PathUnder` from `ForgeTrust.AppSurface.Testing`. Add a project reference to `tests/ForgeTrust.AppSurface.Testing/ForgeTrust.AppSurface.Testing.csproj` and a `Using Include="ForgeTrust.AppSurface.Testing"` entry when a test project needs the helper.

Use `PathUnder` for dynamic repo-relative, temp-relative, or output-relative values such as `projectPath`, `relativePath`, and `outputRelativePath`. Use `TestPathUtils.RelativePath` only when the test needs a validated relative string rather than a full filesystem path.

Literal expected paths and tests intentionally exercising `Path.Join` or `Path.Combine` platform behavior may continue to use the BCL APIs directly. If the policy test flags an intentional case, add a reasoned entry to `tests/ForgeTrust.AppSurface.Testing/path-policy-allowlist.yml`; entries without reasons or entries that no longer match a violation fail validation.

## Release contract

- AppSurface releases the monorepo in unison. Packages, CLI tooling, examples, and docs-facing behavior all roll into the same next version.
- Pull request titles that land on `main` must follow [Conventional Commits](https://www.conventionalcommits.org/) using release-note-friendly types such as `feat`, `fix`, `docs`, `perf`, `refactor`, `test`, `build`, `ci`, `chore`, or `revert`. The squash-merge title is the durable signal for future automation and changelog grouping.
- Add a new file under `releases/unreleased.entries/` whenever a pull request changes behavior, usage guidance, release policy, examples, or docs consumers would care about in release notes. See the [append-only release-note guidance](./releases/README.md#append-only-unreleased-entries). Do not edit [`releases/unreleased.md`](./releases/unreleased.md) for an ordinary feature pull request; CI rejects that shared-template edit unless the pull request is release preparation or is itself updating the release-contract policy. [AppSurface Docs](./Web/ForgeTrust.AppSurface.Docs/README.md) and [release preparation](./tools/ForgeTrust.AppSurface.Release/README.md#append-only-unreleased-entries) compose the stable template.
- Name the file `YYYY-MM-DD-topic.md`, start it with an exact directive such as `<!-- appsurface:unreleased-entry section="included" -->`, then write the Markdown entry. The section value must be `taking-shape`, `included`, or `migration-watch`. The composer sorts filenames and inserts each entry at the bottom of its declared section, after that section's placeholder bullet. Add a new file for corrections instead of changing another pull request's entry; release preparation archives and removes only the entries it consumed and records their paths in the evidence-digested release manifest.
- Maintainers may apply the `no-unreleased-entry` label only for changes that do not belong in the public release story, such as repo administration or workflow-only cleanup.

## Writing documentation

- Link named concepts to their canonical documentation instead of only mentioning them. When prose names a package, guide, example, workflow, policy, diagnostic family, CLI command, or cross-package concept, add a nearby link to the best start-here or reference page unless the same paragraph already defines it completely.
- Prefer useful cross-links over link noise. Link the first meaningful mention in a section, and link again only when the reader is likely to need the destination without scrolling back.
- Use links to connect concepts, not just files. For example, a release note that mentions [PWA install support](./Web/ForgeTrust.AppSurface.Web/Docs/pwa-install.md) should link to the Web package PWA guide and [PWA example](./examples/web-pwa-install/README.md); a note that mentions [RazorWire auth projection](./Web/ForgeTrust.RazorWire.Auth.AspNetCore/README.md) should link to the RazorWire auth package README or [auth proof example](./examples/auth-web-razorwire-proof/README.md).
- Keep link targets durable. Prefer package READMEs, guides, examples, release notes, and public docs routes over transient PRs, local scratch notes, or maintainer-only recovery material.

## Writing release notes

- Start from the public [release hub](./releases/README.md).
- Keep [`CHANGELOG.md`](./CHANGELOG.md) compact. It is the ledger, not the full story.
- Put detailed adoption notes in the current unreleased page or a tagged release page under [`releases/`](./releases/README.md).
- Capture breaking or behavior-changing updates in the unreleased page even before `v0.1.0`. Finalized migration guidance moves into the tagged release page when the version ships.
- Write for package consumers before maintainers. Start each notable entry with the adopter outcome, the package or app shape it applies to, and the next action a reader can take.
- Link every substantial feature, named concept, workflow, diagnostic family, or package boundary to its best start-here material, such as the package README, guide, example, or CLI proof. Do not leave readers to search the repository after a release note names a capability.
- Define internal phrases in plain language before using them as labels. Prefer "[RazorWire can render allowed, forbidden, and anonymous UI from your existing ASP.NET Core policies](./Web/ForgeTrust.RazorWire.Auth.AspNetCore/README.md)" over opaque shorthand such as "passive auth projection" by itself.
- Keep maintainer evidence, warning IDs, diagnostics, and generated-artifact details after the consumer path unless those details change adoption risk.

## Maintainer workflow

- Use the [release authoring checklist](./releases/release-authoring-checklist.md) when preparing a release.
- Use the [PackageIndex maintainer guide](./tools/ForgeTrust.AppSurface.PackageIndex/README.md) when package manifest, generated chooser, readiness, or README release-guidance policy changes.
- Use the [tagged release template](./releases/templates/tagged-release-template.md) when cutting the first versioned release note.
- Keep private maintainer-only recovery notes outside harvested public docs. In this repository, `.github/` is the safe home for that material.

## Local verification

Build and test the full solution before pushing substantive changes:

```bash
dotnet build
dotnet test --no-build
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- --operation local-plan
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- --operation local
```

The C# repository evidence tool uses the shared [evidence policy](https://github.com/forge-trust/AppSurface/blob/main/.github/evidence.policy.json) and compares the working tree with `origin/main` by default. `local-plan` prints the selected profile, producers, and obligations; `local` executes and verifies the selected evidence on the current OS. Add `--base <revision>` to select another comparison base, or `--scope all|native|coverage|packages` to plan or run one lane. Use `all` for the complete local policy selection; a scoped pass closes only its selected lane. `selection.json` exposes `requiresNativeCoverage` from the root policy selection. Test-only changes use the direct patch gate; source-code changes combine coverage and package checks, with native fan-in on PRs. Package checks may still be affected by ignored local artifacts in inspected paths; ignore status does not skip a package failure. See the [coverage fan-in guide](./guides/coverage-fan-in.md) and the [validation tool reference](./tools/ForgeTrust.AppSurface.Evidence.Validation/README.md) for coverage gates, evidence output, and CI behavior. The standalone `coverage-solution.sh` remains an unconditional compatibility command; it is not the current `build.yml` evidence path.

### Documentation publishing and recovery

The [build workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/build.yml)
exports and validates docs on pull requests. Pushes to `main` and manual runs selecting `main` also
hydrate release archives, upload the `github-pages` artifact, and publish it. Manual runs on other
branches validate the export without publishing. Both the build workflow and the
[release publisher](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/release-publish.yml)
share a `github-pages` deployment concurrency group across runs and let an active deployment finish
before the next one starts; builds and exports can still run in parallel. The
[concurrency queue](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency)
uses `queue: max` so a new build does not replace a pending release deployment. GitHub allows up to
100 pending jobs in that group; job readiness determines queue order, so an older commit can still
publish later if its export takes longer.

The Pages artifact is retained for seven days so a failed deployment can be retried without
rebuilding the docs. If `deploy-appsurface-docs` times out in `syncing_files` after a successful
export, inspect its logs and [GitHub Status](https://www.githubstatus.com/) before rerunning the
deployment job. The pinned [deploy-pages action](https://github.com/actions/deploy-pages) caps its
timeout at ten minutes; a larger timeout input cannot extend that limit.

If the artifact has expired, rerun `export-appsurface-docs` and its dependent deployment from the
current `main` run, or start a fresh manual run with `gh workflow run build.yml --ref main`.
See [rerunning jobs](https://docs.github.com/en/actions/managing-workflow-runs-and-deployments/managing-workflow-runs/re-running-workflows-and-jobs)
for the Actions UI and CLI options. Check that the selected commit is still the intended published
version before retrying an older run. Repeated synchronization timeouts with a valid artifact need
Pages service investigation; deployment serialization does not resolve a backend outage.

### CI test-result diagnostics

The current [build workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/build.yml) resolves the shared evidence policy first. Coverage selections run full solution coverage and a separate packed Durable consumer proof. The native coverage-security matrix and PR fan-in run only when the root policy selection includes coverage plus another producer; test-only selections use the direct patch gate. The consumer
job runs the same [packed-consumer verification](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-packed-consumers.sh) with the exact
coverage SDK and locked restore. Its failure blocks the build and downstream documentation export.
The coverage lane runs the complete solution through the C# repository evidence validation tool and preserves the existing aggregate and patch gates; see the [coverage fan-in guide](./guides/coverage-fan-in.md) for the current workflow and artifact layout.
Keeping the packed consumer proof in its own job preserves the build job's 60-minute budget for solution verification; it does not reduce the selected tests, change exclusive scheduling, or relax either coverage gate.

The direct Codecov-mode patch gate follows the [CLI's documented no-measurable-points behavior](./Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate). Native fan-in keeps its strict nonempty-denominator verification. Package checks are not exempted based on Git ignore status: ignored local artifacts in paths the existing package checks inspect can still cause a package-check failure; review the package diagnostics and remove or resolve unintended local artifacts.

The GitHub Actions full-solution coverage lane publishes a bounded, failure-first test summary from
the AppSurface-managed JUnit output. The CLI owns this report alongside its slow-test diagnostics:
it names failed projects and test cases, includes safely truncated failure detail, and never exceeds
the GitHub step-summary limit. This summary deliberately replaces the per-project GitHub Actions
test logger in the broad coverage lane, which can exceed that limit as the suite grows. Download the
`test-result-diagnostics` artifact for the complete JUnit XML and per-project `dotnet test` logs under `TestResults/coverage-evidence/coverage/`,
especially for fork pull requests where [Codecov test-result reporting](https://docs.codecov.com/docs/test-analytics)
is unavailable without a repository token.

The coverage run treats missing or malformed JUnit evidence as a diagnostics warning, never as a
replacement failure for the original test result; use the artifact and the coverage-step log to
diagnose that condition.

The [Package Artifacts workflow](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/package-artifacts.yml) reclaims the preinstalled
Android toolkit at `/usr/local/lib/android` before restore or proof work on its disposable GitHub-hosted
Linux runner. This .NET/Node job does not use Android. Preparation rejects other runner environments,
reports available blocks and inodes before and after, and preserves .NET, Node, NuGet caches, checkout
files, proof workspaces, package archives, and diagnostic evidence. The executable
[runner-preparation guard checks](https://github.com/forge-trust/AppSurface/blob/main/.github/tests/package_runner_capacity_tests.py) exercise the actual
workflow shell with a recording `sudo`; they do not reclaim space on a developer machine. If the job
reports `No space left on device`, inspect its disk diagnostics and the uploaded package-consumer logs
before changing cache ownership, artifact retention, or verification requirements.

The compatibility `coverage-solution.sh` wrapper requires a non-sandboxed host by default and fails before discovery, cleanup, build, or tests when an explicit sandbox marker is enabled. Set `COVERAGE_REQUIRE_NON_SANDBOX=false` only when a restricted compatibility-script run is intentional. The current repository evidence producer also requires a non-sandboxed host for its collection command; see the [non-sandboxed runner reference](./Cli/ForgeTrust.AppSurface.Cli/README.md#require-a-non-sandboxed-runner).

When the compatibility CLI gate uses `COVERAGE_GATE_DIFF_BASE` to select a patch comparison, it also writes
`TestResults/coverage-merged/coverage-patch-targets.json` and
`TestResults/coverage-merged/coverage-patch-targets.md`, even without patch thresholds. Read the
brief, open each `path:line`, identify the relevant test project, run
`dotnet test <project> --no-restore`, then run the full coverage lane and one final full gate.
An empty active queue means the patch is fully covered for the selected gate semantics. Clearing
`COVERAGE_GATE_DIFF_BASE` intentionally runs a nonpatch gate and removes those two target files.
The output stays untracked through the existing `TestResults` ignore rule; do not add target
filenames to `.gitignore`. See the [CLI target-artifact guidance](./Cli/ForgeTrust.AppSurface.Cli/README.md#agent-actionable-patch-targets)
for `--no-clean`, `ASCOV019`, and optional retention in an existing CI artifact upload.
