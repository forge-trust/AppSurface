# Choosing a repository command form

Use this guide with the revision-specific [script inventory](script-inventory.md). It helps contributors choose a form that matches who must run a task, what it depends on, and where its behavior belongs. File-based .NET execution is an option for appropriate .NET tasks, not a requirement for every script.

## Choose by task boundary

| Form | Choose it when | Example and prerequisites |
| --- | --- | --- |
| Taskfile entry | Contributors need a discoverable name for an existing checkout command. Give the task a description and keep its implementation in the tool or script it calls. | With Task v3 installed, `task evidence:plan` previews the checks for a change. See [repository tasks](../CONTRIBUTING.md#repository-tasks). |
| Bootstrap script | The command installs the SDK or runs before CI selects one. | Keep the [SDK setup script](https://github.com/forge-trust/AppSurface/blob/main/agent-setup.sh) usable with Bash. |
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
| 1 | PWA verifier wrappers | The [file-based verifier](../scripts/web-pwa/README.md) replaces the Bash and PowerShell commands. The public CLI owns the schema and checks. | Both wrappers were retired after [Windows, Linux, and macOS passed at `8d46e44a`](https://github.com/forge-trust/AppSurface/actions/runs/44872027102). Restore the two wrappers and their callers together if the replacement regresses. [PWA example](../examples/web-pwa-install/README.md) |
| 2 | Coverage scheduling history | Implemented as the repository-only [`coverage-schedule-history.cs` file app](https://github.com/forge-trust/AppSurface/blob/main/.github/scripts/coverage-schedule-history.cs), with [offline contracts](https://github.com/forge-trust/AppSurface/blob/main/tests/coverage-schedule-history/contracts.cs) and a [shared scheduler fixture](https://github.com/forge-trust/AppSurface/blob/main/tests/coverage-schedule-history/fixtures/timings.json). | The [evidence-validation guide](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#coverage-scheduling-history) documents the pinned SDK, live and offline commands, advisory and fatal outcomes, and the separate restore and scheduling-reader contracts. The revision-pinned script inventory remains historical. |
| 3 | Release automation | `eng/release` and the release workflows coordinate repository state, package publication, and external service actions. | Keep orchestration in repository/workflow tooling. Extract only a stable, testable local decision that removes duplicated logic. A public command needs an outside-checkout release task and must not expose repository credentials or workflow policy as an accidental API. |
| 4 | Consumer/package verification scripts | Scripts under `scripts/`, `Durable/`, and examples exercise packed artifacts and isolated consumers. | These are useful candidates for shared internal helpers only where duplicated behavior is demonstrated. Isolated package-consumer tests must keep private caches/feeds and checkout-independent layouts. |

## Canary comparison for configuration binding

The existing `canary poll` scheduling rule illustrates why validation follows binding. With a five-minute timeout, a configured 500 ms interval overridden by `1s` yields 300 attempts and qualifies. A configured one-second interval overridden by `999ms` yields 301 attempts and should produce `ASCAN401` before polling. These are paper evaluations of the request factory's existing predicate. Keep the constructor guard for direct callers. [Canary rule](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli/CanaryPollCommand.cs) · [Canary tests](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli.Tests/CanaryPollTests.cs)

An independent agent used the published-package example in a temporary directory on macOS with SDK 10.0.402. It added a bounded `Attempts` setting to the JSON, nullable switch, key annotation, settings record and final binding expression. The copy returned 3 from JSON and 5 for explicit `--attempts 5`, as predicted. The setting name appeared in help and key metadata, and each switch needed the same explicit-then-config lookup. The range was declared once on the final property. Human time to first success remains unmeasured. [Package example](../examples/file-based-console/README.md)

## Decision threshold

In a timed agent walkthrough on macOS 26.7.3 arm64 with SDK 10.0.402, the first package-example invocation was attempted after 2 min 18 s. The sandbox denied access to the SDK’s runfile cache. The first successful help output arrived after 9 min 34 s, exceeding the five-minute target. With the SDK cache writable and an empty NuGet package cache, help took 2.91 s. Warm `inspect` calls took 0.32–0.37 s. [Outside-checkout instructions](../examples/file-based-console/README.md#run-outside-the-checkout)

Prefer documentation or a repo-local helper when one command repeats a convention but no second real command demonstrates the same mechanical work. Consider a public binding API only after a second command repeats that work and the API removes it while preserving final-value validation, diagnostics, source-location behavior, and clean-directory package use. Any public candidate must identify a task a user can complete outside this checkout. Record the decision and observed contributor friction in the issue follow-up rather than treating the inventory ranking as a commitment.
