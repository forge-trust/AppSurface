# Contributing to AppSurface

AppSurface is putting its release contract in place before the first tagged version. This file explains the contribution rules that feed the public release surface.

## Feedback path

AppSurface treats docs and onboarding feedback as product input, not as a second-class support queue. File issues when a package, example, README, or release note leaves you unable to reproduce the intended path.
For quick access, use GitHub's issue template chooser: [choose an issue template](https://github.com/forge-trust/AppSurface/issues/new/choose).

- Use the [**Bug report** issue form](https://github.com/forge-trust/AppSurface/issues/new?template=bug_report.yml) when behavior is broken or surprising.
- Use the [**Feature request** issue form](https://github.com/forge-trust/AppSurface/issues/new?template=feature_request.yml) when you can name a focused product capability, API shape, workflow, or example that would remove friction.
- Use the [**Docs or developer experience feedback** issue form](https://github.com/forge-trust/AppSurface/issues/new?template=docs_dx_feedback.yml) when the code may work, but the route to understanding it is unclear.
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

## Release contract

- AppSurface releases the monorepo in unison. Packages, CLI tooling, examples, and docs-facing behavior all roll into the same next version.
- Pull request titles that land on `main` must follow [Conventional Commits](https://www.conventionalcommits.org/) using release-note-friendly types such as `feat`, `fix`, `docs`, `perf`, `refactor`, `test`, `build`, `ci`, `chore`, or `revert`. The squash-merge title is the durable signal for future automation and changelog grouping.
- Update [`releases/unreleased.md`](./releases/unreleased.md) whenever a pull request changes behavior, usage guidance, release policy, examples, or docs consumers would care about in release notes.
- Maintainers may apply the `no-unreleased-entry` label only for changes that do not belong in the public release story, such as repo administration or workflow-only cleanup.

## Writing release notes

- Start from the public [release hub](./releases/README.md).
- Keep [`CHANGELOG.md`](./CHANGELOG.md) compact. It is the ledger, not the full story.
- Put detailed adoption notes in the current unreleased page or a tagged release page under [`releases/`](./releases/README.md).
- Capture breaking or behavior-changing updates in the unreleased page even before `v0.1.0`. Finalized migration guidance moves into the tagged release page when the version ships.

## Maintainer workflow

- Use the [release authoring checklist](./releases/release-authoring-checklist.md) when preparing a release.
- Use the [tagged release template](./releases/templates/tagged-release-template.md) when cutting the first versioned release note.
- Keep private maintainer-only recovery notes outside harvested public docs. In this repository, `.github/` is the safe home for that material.

## Local verification

Build and test the full solution before pushing substantive changes:

```bash
dotnet build
dotnet test --no-build
./scripts/coverage-solution.sh
```
