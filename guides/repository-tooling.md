# Choosing a repository command form

Use this guide with the revision-specific [script inventory](script-inventory.md). It helps contributors choose a form that matches who must run a task, what it depends on, and where its behavior belongs. File-based .NET execution is an option for appropriate .NET tasks, not a requirement for every script.

## Choose by task boundary

| Form | Choose it when | Example and prerequisites |
| --- | --- | --- |
| Bootstrap script | The command installs the SDK or runs before CI selects one. | Keep the [SDK setup script](../agent-setup.sh) usable with Bash. |
| Framework-free C# file | The SDK is already present and BCL types are sufficient. | A file that reads a local schema-v3 report and prints its diagnostic codes can use `System.Text.Json` directly. |
| AppSurface-backed C# file | A focused task needs command help and the same logical settings from JSON, environment variables, or a secret provider. | The [checkout PWA verifier](../scripts/web-pwa/README.md) uses project references. The [standalone file](../examples/file-based-console/README.md) pins published packages. |
| Project-based internal CLI | Several maintainer commands share services, tested policy, or subcommands. | Keep branch and release orchestration within the [repository release tool](../tools/ForgeTrust.AppSurface.Release/README.md). Use [ForgeTrust.AppSurface.Console](../Console/ForgeTrust.AppSurface.Console/README.md) when extending a native CLI. |
| Project-based public CLI | An installed consumer can supply the inputs and act on a versioned result outside this checkout. | A site owner can run [`appsurface pwa verify`](../Cli/ForgeTrust.AppSurface.Cli/README.md) against their own site's URL. Starting this repository's sample belongs to the checkout verifier. |

## Selection checks

First identify the caller and whether they have a checkout. A task that needs private project references, repository history, generated fixture trees, workflow tokens, package-publishing credentials, or an internal evidence policy is repository-only unless that dependency is deliberately replaced by a supported consumer contract. Next identify bootstrap ordering. `agent-setup.sh` is needed before the selected SDK exists, so rewriting it as a file-based .NET command would make the setup dependency circular.

For a .NET file command, state the SDK pin and whether execution restores packages. Keep script configuration lookup anchored to the script when that is the contract, and resolve explicit relative output paths from the caller's working directory. Preserve the distinction between an omitted scalar and an explicitly supplied default-valued flag. Resolve provider values before validating the final settings object. Validate malformed and out-of-range inputs before launching subprocesses. The PWA pilot's accepted switches and precedence are documented in its [canonical configure-a-run table](../scripts/web-pwa/README.md#configure-a-run). Bind credential-bearing settings to [typed secret destinations](../Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md). Help describes switch/key metadata without fetching payloads. Keep one-invocation actions such as `--apply` explicit so a persistent environment or JSON value cannot authorize a later write.

When a command starts child processes or publishes evidence, document ownership, bounded cleanup, output streams, exit-code behavior, and whether a report belongs to this invocation. Give parallel CI runs distinct output paths. A failed attempt must not make an old report appear current. These are command contract concerns, not reasons to add framework machinery to a simple script.

## Candidate ranking

The ranking is a reasoned investigation order based on reuse potential, caller overlap, and risk. It is not authorization to migrate or delete the current entry points.

| Rank | Candidate | Why it merits review | Boundary and evidence needed |
| ---: | --- | --- | --- |
| 1 | PWA verifier wrappers | Bash and PowerShell implement a shared, user-visible verifier workflow, while the CLI owns the canonical report. This is the active portability pilot. | Keep both wrappers until the exact documented command passes native Windows, Linux, and macOS checks, including unrelated port listeners, exact report/exit parity, cancellation, cleanup, and fresh artifact publication. See the [PWA example](../examples/web-pwa-install/README.md) and the [implementation plan](../docs/plans/issue-928-file-based-cli-binding.md). |
| 2 | Coverage scheduling history | `.github/scripts/coverage-schedule-history.py` has a bounded purpose and neighboring contract tests. It could become a focused repository .NET command if shared configuration, diagnostics, and typed domain behavior materially simplify maintenance. | First measure actual duplication and CI/runtime cost. It is workflow-owned and repository-only unless a concrete outside-checkout consumer appears. Retain Python if it remains the smallest reliable bootstrap/runtime fit. |
| 3 | Release automation | `eng/release` and the release workflows coordinate repository state, package publication, and external service actions. | Keep orchestration in repository/workflow tooling. Extract only a stable, testable local decision that removes duplicated logic. A public command needs an outside-checkout release task and must not expose repository credentials or workflow policy as an accidental API. |
| 4 | Consumer/package verification scripts | Scripts under `scripts/`, `Durable/`, and examples exercise packed artifacts and isolated consumers. | These are useful candidates for shared internal helpers only where duplicated behavior is demonstrated. Isolated package-consumer tests must keep private caches/feeds and checkout-independent layouts. |

## Canary comparison for configuration binding

The existing `canary poll` scheduling rule illustrates why validation follows binding. With a five-minute timeout, a configured 500 ms interval overridden by `1s` yields 300 attempts and qualifies. A configured one-second interval overridden by `999ms` yields 301 attempts and should produce `ASCAN401` before polling. These are paper evaluations of the request factory's existing predicate. Keep the constructor guard for direct callers. [Canary rule](../Cli/ForgeTrust.AppSurface.Cli/CanaryPollCommand.cs) · [Canary tests](../Cli/ForgeTrust.AppSurface.Cli.Tests/CanaryPollTests.cs)

An independent agent used the published-package example in a temporary directory on macOS with SDK 10.0.402. It added a bounded `Attempts` setting to the JSON, nullable switch, key annotation, settings record and final binding expression. The copy returned 3 from JSON and 5 for explicit `--attempts 5`, as predicted. The setting name appeared in help and key metadata, and each switch needed the same explicit-then-config lookup. The range was declared once on the final property. Human time to first success remains unmeasured. [Package example](../examples/file-based-console/README.md)

## Decision threshold

Prefer documentation or a repo-local helper when one command repeats a convention but no second real command demonstrates the same mechanical work. Consider a public binding API only after a second command repeats that work and the API removes it while preserving final-value validation, diagnostics, source-location behavior, and clean-directory package use. Any public candidate must identify a task a user can complete outside this checkout. Record the decision and observed contributor friction in the issue follow-up rather than treating the inventory ranking as a commitment.
