# Implementation plan for explicit Config mappings in file-based CLI commands

**Implementation plan approved November 20, 2026.** [Approved design](../designs/issue-928-file-based-cli-binding.md) · [Issue #928](https://github.com/forge-trust/AppSurface/issues/928)

## Decision

Start with approach B. A contributor declares a normal CliFx switch and explicitly associates it with an AppSurface.Config logical key. Pilot a command-local attribute beside the switch declaration. The attribute supplies only the key. CliFx owns the switch name and argument parsing. The pilot owns one operation that returns fully validated settings to the handler. The existing ConfigKey attribute describes configuration types. The pilot attribute is internal, and its spelling can change. [Console command authoring](https://github.com/forge-trust/AppSurface/tree/main/Console/ForgeTrust.AppSurface.Console#command-authoring-with-clifx-3) · [ConfigKey contract](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/ConfigKeyAttribute.cs)

Approach A, a hand-written merge per command, would repeat the ordering decision. Approach C, generating switches from settings types, would expand the framework’s responsibilities before a second command demonstrates the need.

## Binding contract

| Explicit switch | Config key | Unscoped environment name | Default |
| --- | --- | --- | --- |
| `--port` | `PwaVerify:Port` | `APP_SURFACE_WEB_PWA_PORT` | 5055, on the child |
| `--evidence-path` | `PwaVerify:EvidencePath` | `APP_SURFACE_WEB_PWA_EVIDENCE` | `examples/web-pwa-install/pwa-verify-v3.json` in the checkout |
| `--startup-timeout-seconds` | `PwaVerify:StartupTimeoutSeconds` | `PWAVERIFY__STARTUPTIMEOUTSECONDS` | 60 |

1. Preserve whether each switch was supplied. An explicit `--port 5055` wins even when JSON supplies a different port. Invalid explicit input fails before the child starts. An omitted switch resolves through Config’s existing providers, then the default if the key is missing. A provider failure remains an error. Use the existing environment mappings for the two legacy names. [Logical keys and environment mappings](https://github.com/forge-trust/AppSurface/blob/main/guides/config-logical-keys.md)
2. Use nullable command inputs for the three optional switches. Resolve missing scalar inputs through `IConfigManager`, then construct one typed settings object. Run native .NET DataAnnotations on that final object. Use property constraints for single values and `IValidatableObject` for relationships. Config’s independent startup checks still validate provider registrations and secret descriptors. [Config manager](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/IConfigManager.cs) · [Config startup validation](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/ConfigCompositionStartupValidator.cs)
3. Port accepts 1–65535, timeout accepts positive seconds, and an evidence path must name a file. Resolve relative user paths from the caller’s working directory. Resolve settings files beside the script using the Config file-location contract. Document the verifier’s active environment and its matching appsettings file separately from the child’s Development environment. Help can name keys using metadata. Credentials use typed secret destinations, and one-invocation actions such as `--apply` require explicit input. [Config file locations](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/DefaultConfigFileLocationProvider.cs) · [Typed secrets](https://github.com/forge-trust/AppSurface/blob/main/Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md)

## What the pilot must prove

Deliver one inventory at a named revision, with generated, vendored and browser assets accounted for. The permanent automation is the native PWA matrix. Its jobs use fresh evidence destinations. Local runs atomically publish complete reports and identify prior-run files on failure. After the contributor exercise, record the follow-up recommendation in TODOS.md.

- **Portability.** Run `dotnet run --file scripts/web-pwa/verify.cs` on native Windows, Linux and macOS through permanent path-filtered CI. Verify the child owns the requested port before trusting its HTTP responses. Include a case with an unrelated listening process. It must survive, and the verifier must fail. Check manifest readiness, the status endpoint, schema-v3 combined evidence, preservation of failed-report exit codes, and child cleanup on success, timeout, failure and cancellation. Remove both wrappers and update callers only after all three platforms pass. [PWA example](https://github.com/forge-trust/AppSurface/tree/main/examples/web-pwa-install) · [Core host port handling](https://github.com/forge-trust/AppSurface/blob/main/ForgeTrust.AppSurface.Core/AppSurfaceStartup.cs#L140)
- **Contributor experience.** Use checkout project references for the repository pilot and a runnable pinned-package example outside the checkout. Show JSON, environment and flag precedence, invocation from a different directory, a path with spaces, and help without secret reads. Use a synthetic secret only in the package example. The earlier macOS probe demonstrated package composition and precedence. Native lifecycle verification is still outstanding.
- **Cross-property validation.** Evaluate `canary poll` on paper against its existing scheduling predicate. At a five-minute timeout, a configured 500 ms interval plus explicit `1s` must pass at 300 attempts. A configured one-second interval plus explicit `999ms` must fail at 301 with `ASCAN401` before any poll. Preserve the constructor guard for direct callers. The independent review identified early validation of the configured pair as a false-rejection risk. The request factory already checks the same predicate before construction. These mixed-source cases have yet to run. [Canary rule](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli/CanaryPollCommand.cs#L649) · [Diagnostic contract](https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli.Tests/CanaryPollTests.cs#L179)

## Inventory and follow-up

Reconcile tracked automation, embedded workflow steps, build and package scripts, examples, fixtures and exclusions at a named commit. Record purpose, callers, SDK availability, platform evidence and a reasoned disposition. Publish the inventory and decision guide under `guides/` and link from `CONTRIBUTING.md`. Rank heavier internal and public CLI candidates. A public candidate needs a task someone can perform outside this checkout. The pilot, inventory and guide ship through the repository. Public package changes and heavier migrations require follow-up decisions.

## The assignment

During pilot review, give a contributor the package example in a clean directory and ask them to add one configurable setting. Have them predict the value when JSON and an explicit flag disagree, then run it. Record where they needed to repeat the key, precedence rule or validator. Use that record and the two canary cases to decide whether a reusable API would remove work.

## Implementation steps and acceptance

<!-- autoplan-accepted:ceo -->
- Inventory the implementation SHA with `git ls-files --stage -z`, shebang and executable-bit inspection, workflow job/step keys, package manifests and MSBuild targets. Track independent inline programs by workflow path, job ID and step ID or ordinal. Connect wrapper-only calls, tests and fixtures to their owner. Count distinct source programs and call sites separately. Reconcile the historical 29 shell, 16 Python, one PowerShell, 26 MJS and 209 workflow run steps across 16 workflows against that SHA. Every discovered item needs a row, a caller link or a reason for exclusion.
- Publish a linked inventory and decision guide under `guides/` and link both from `CONTRIBUTING.md`. Record purpose, platform evidence, prerequisites, audience, duplicated work, owner/caller and disposition. Keep tooling that must provision the SDK runnable before the SDK exists, including `agent-setup.sh`. Contrast a framework-free file, an AppSurface-backed file, a repository-only command and a public command using named tasks. Rank larger candidates including coverage scheduling history and release automation. Require an outside-checkout task for each public candidate.
- Stage the pilot with both legacy scripts present. The example owner compares the exact existing public verifier arguments, manifest and status probes and schema-v3 JSON. An explicit flag equal to its built-in default must beat a different configured value. An omitted integer reads a nullable scalar to distinguish missing from zero. Exercise missing, malformed and out-of-range inputs before invoking any subprocess. Use AppSurface.Config for provider resolution and its file-location abstraction bound to the source directory. Use the caller directory for explicit relative output paths.
- Require proof tied to the current child instance before invoking the public verifier. First evaluate child-originated startup notification identifying the bound loopback address, paired with child liveness and both HTTP probes. If the existing host cannot furnish reliable identity on all three runners, use an opt-in per-launch challenge returned by the example on loopback in Development. Record the mechanism and run the unrelated-listener case, including a listener which serves the expected manifest. An occupied port fails without affecting its owner.
- Treat each invocation's artifact as evidence for that invocation only. Use a fresh sibling temporary file for captured verifier stdout, diagnostics on stderr, and publish a complete schema-v3 report for success or a reported verification failure by replacing the destination. Assert exact verifier exit status and preserve the preceding report on startup failure or malformed output. CI uses a unique evidence destination and uploads that run's file only. Concurrent runs use distinct destinations.
- Put the documented command and its regression cases in permanent path-filtered native CI on Windows, Linux and macOS. Include pilot, example, CLI PWA, Web, Console, Config, Core, SDK, shared build/dependency files and the workflow itself. Gate deletion and caller updates on a named green pre-removal revision. Rerun on the final revision and record per-OS links and evidence. If a platform fails, retain both wrappers and land inventory/guidance independently. Restore the two wrappers and their callers together if the replacement regresses.
- Compare cold and warm wall time of the existing wrapper and the new file on the same runner and SDK. Record runner, cache state and first-run restore cost alongside correctness. A maintainer reviews material CI cost before retirement. The inventory owner records any unknown platform as unverified. The pilot owner captures the clean-directory contributor exercise, including where the contributor had to repeat a key, validation or precedence rule.
- Document and run `dotnet run --file scripts/web-pwa/verify.cs -- --port 5056 --evidence-path "out dir/proof.json"`. Use the actual schema-v3 field names and casing. Assert the successful public report selects all surfaces and passes, then assert a failed report selects all surfaces and preserves the exact CLI exit code. On success, failure, timeout and supported cancellation, wait for bounded cleanup and immediately bind the same port. In the unrelated-listener case verify that listener remains reachable instead.
- Add the pinned single-file example to the Console README with a copyable command that runs in an empty directory on the pinned SDK. Resolve a synthetic typed secret there, assert the expected result in memory, and scan stdout and stderr for disclosure without printing the value. Help must succeed with an unavailable secret provider and zero secret payload reads.
- Explicitly reconcile generated files, vendored tooling and browser-runtime assets as exclusions with paths and reasons. Record the recommendation in TODOS.md after the field exercise and canary comparison, choosing a documented convention, a repo-local helper or a separately scoped public API proposal.
- Emit a publication message on stderr with the destination only after atomically installing this run's valid report. On startup failure or invalid output, explicitly say this run produced no evidence and any existing destination is from a prior run. File existence alone never qualifies. Exercise a pre-existing successful report on both failure paths. This protects caller files and prevents a partial write without modifying the public JSON schema.
- Keep three independent decisions on the issue. The inventory can land on its reconciliation proof. Promote the replacement on native parity and cleanup proof. Recommend a public binding design only when a second real command repeats the same mechanical work and the local operation would remove it without weakening its validation or diagnostic. A one-person exercise plus a paper comparison can justify documenting the convention or exploring a repo-local helper. A working public package example must declare the local attribute and binding operation in the example itself if the pinned packages lack them.
- Use one implementation PR with separately reviewable commits by default. Before rollout, the CI owner records the existing path-filtered job’s timeout and relative cold/warm costs. Timebox the initial portability investigation to the issue’s 0.5–1 engineering day estimate. If parity is still blocked, ship the independently accepted documentation, leave the existing commands canonical and keep the unfinished pilot on its implementation branch until a maintainer owns the failing case.
<!-- /autoplan-accepted:ceo -->

<!-- autoplan-accepted:dx -->
- Put the installed SDK version check, checkout command and outside-checkout example at the first meaningful README mention. Verify SDK 10.0.402 in the actual caller directory. Link a single precedence table from the Console README, example and contributor guide. CLI help lists each flag, logical key, applicable environment name and default through metadata. The first two names preserve legacy scripts. The timeout uses the normal hierarchical spelling.
- Give an example with verifier environment Production using appsettings.json beside the source, and a separate Development child. Add an example running an absolute script path from another directory. Document inherited host environment selection, how to select the matching JSON file, and the effect on a relative evidence destination. The verifier selects default port 5055 and passes it to the child.
- Report errors on stderr and return nonzero. For usage, include a stable pilot-owned category or existing diagnostic, switch or logical key, allowed range and a concrete correction. Identify explicit CLI input vs Config resolution. Include a provider or environment identity only when available from existing, safe diagnostics. Never guess file-vs-environment provenance. Preserve native Config diagnostic codes. Avoid echoing input values in exceptions, logs or validation messages. Use sanitized operation/category context for unexpected errors. Test stderr, exit, publication status and synthetic redaction.
- For the destination, reject an existing directory or invalid file name. Create a missing parent and sibling temporary file, handle denied creation, flush/replace failure and cleanup failure explicitly. Preserve the previous valid artifact on failed publication. Say which operation failed, whether a report was published and how to choose a writable path. If cleanup alone fails after publication, identify the published file and leftover temporary path. Test via injectable file operations where OS permissions do not reproduce reliably.
- In the clean-directory exercise, time from opening the Console README with the pinned SDK installed to a successful invocation. Record cold restore, warm rerun, OS, machine and first confusion. Target under five minutes with a functioning feed. If missed, explain the cause and correct documentation or setup friction. A feed outage cannot decide wrapper retirement. Copied internal example code is owned by its example and versioned with the documented package pin.
<!-- /autoplan-accepted:dx -->

<!-- autoplan-accepted:eng -->
- Use a fresh random proof value per launch, carried in the child-only environment. An opt-in Development-only endpoint on the example returns that value only on loopback. A fixed path request never sends the expected value to the listener. Disable proxying and redirects for this check. Compare the response with the expected value at the exact loopback scheme and port. Also require that the launched child is alive and both manifest and status probes succeed. Recheck identity before accepting a completed report. Test missing and prior-run values and child exit. Leave the route absent when verification is disabled. This is an instance check for accidental collisions.
- Build the example and CLI before capture. Launch the built products with structured argument lists, explicit working directories and no compilation output on the JSON stream. Preserve the sample content root and required static assets. Register CliFx cancellation and track each owned process as soon as it starts. Concurrently drain stdout/stderr to bounded buffers or files. Enforce one monotonic startup deadline with bounded HTTP attempts. On cancellation during build, startup or verification, terminate the owned process trees and wait up to five seconds total for teardown using a fresh cancellation budget. Use a native outer watchdog of ten seconds for the cleanup assertion. Exercise POSIX SIGINT and a native console Ctrl+C event on Windows in an isolated console; if the runner cannot deliver an actual event, keep native retirement blocked. Check all owned PIDs as well as port reuse.
- Read one complete UTF-8 JSON document after child exit and EOF on both streams. Check schemaVersion, surface, passed and the required combined install/push/diagnostics shape against the current public PWA contract. Cross-check passed with exit status. A real valid failed report retains its exact nonzero exit. Treat empty, truncated, wrong-shape or contaminated output, inconsistent exit/report, inability to publish, and a child that dies during the proof as harness failures. Move the completed sibling temp file over the destination in the same directory. Verify both an absent and existing destination, rejected output and a failed replace on each OS. Document that concurrent invocations require separate destinations. The contract covers same-volume local filesystems used by the runners.
- Bind only declared, nonsecret optional properties. Fail mapping-definition errors before side effects. Validate missing, zero, empty and malformed values through the installed Config providers to establish actual behavior. Use the source directory supplied by the file-based runtime (or an explicitly verified source-path seam on the pinned SDK) to register the script-level IConfigFileLocationProvider after Config defaults. A different cwd with decoy JSON must not change which settings are loaded. Capture the caller cwd before starting children. Reuse the existing final-validation predicate for the paper canary comparison.
- The inventory owner uses the issue estimate of 1–1.5 days. If reconciliation cannot finish, report the outstanding locators and do not call it complete. Keep cold/warm benchmarks advisory and record the job’s enforced timeout before the first native comparison. A repeatable warm regression of over twice the baseline warrants maintainer review with raw durations. Native correctness and lifecycle proof remain mandatory.
- This engineering decision settles the earlier startup-notification alternative. The per-launch HTTP identity check is required on every runner. Treat host startup notifications only as diagnostic context. For the bounded native cancellation test, bind the launched processes to an isolated console or process group supported by that runner and verify the exact documented entry point receives cooperative cancellation. Record natural child exit separately from a teardown timeout.
<!-- /autoplan-accepted:eng -->
## Review record

<!-- autoplan-accepted:ceo -->
- Inventory the implementation SHA with `git ls-files --stage -z`, shebang and executable-bit inspection, workflow job/step keys, package manifests and MSBuild targets. Track independent inline programs by workflow path, job ID and step ID or ordinal. Connect wrapper-only calls, tests and fixtures to their owner. Count distinct source programs and call sites separately. Reconcile the historical 29 shell, 16 Python, one PowerShell, 26 MJS and 209 workflow run steps across 16 workflows against that SHA. Every discovered item needs a row, a caller link or a reason for exclusion.
- Publish a linked inventory and decision guide under `guides/` and link both from `CONTRIBUTING.md`. Record purpose, platform evidence, prerequisites, audience, duplicated work, owner/caller and disposition. Keep tooling that must provision the SDK runnable before the SDK exists, including `agent-setup.sh`. Contrast a framework-free file, an AppSurface-backed file, a repository-only command and a public command using named tasks. Rank larger candidates including coverage scheduling history and release automation. Require an outside-checkout task for each public candidate.
- Stage the pilot with both legacy scripts present. The example owner compares the exact existing public verifier arguments, manifest and status probes and schema-v3 JSON. An explicit flag equal to its built-in default must beat a different configured value. An omitted integer reads a nullable scalar to distinguish missing from zero. Exercise missing, malformed and out-of-range inputs before invoking any subprocess. Use AppSurface.Config for provider resolution and its file-location abstraction bound to the source directory. Use the caller directory for explicit relative output paths.
- Require proof tied to the current child instance before invoking the public verifier. First evaluate child-originated startup notification identifying the bound loopback address, paired with child liveness and both HTTP probes. If the existing host cannot furnish reliable identity on all three runners, use an opt-in per-launch challenge returned by the example on loopback in Development. Record the mechanism and run the unrelated-listener case, including a listener which serves the expected manifest. An occupied port fails without affecting its owner.
- Treat each invocation's artifact as evidence for that invocation only. Use a fresh sibling temporary file for captured verifier stdout, diagnostics on stderr, and publish a complete schema-v3 report for success or a reported verification failure by replacing the destination. Assert exact verifier exit status and preserve the preceding report on startup failure or malformed output. CI uses a unique evidence destination and uploads that run's file only. Concurrent runs use distinct destinations.
- Put the documented command and its regression cases in permanent path-filtered native CI on Windows, Linux and macOS. Include pilot, example, CLI PWA, Web, Console, Config, Core, SDK, shared build/dependency files and the workflow itself. Gate deletion and caller updates on a named green pre-removal revision. Rerun on the final revision and record per-OS links and evidence. If a platform fails, retain both wrappers and land inventory/guidance independently. Restore the two wrappers and their callers together if the replacement regresses.
- Compare cold and warm wall time of the existing wrapper and the new file on the same runner and SDK. Record runner, cache state and first-run restore cost alongside correctness. A maintainer reviews material CI cost before retirement. The inventory owner records any unknown platform as unverified. The pilot owner captures the clean-directory contributor exercise, including where the contributor had to repeat a key, validation or precedence rule.
- Document and run `dotnet run --file scripts/web-pwa/verify.cs -- --port 5056 --evidence-path "out dir/proof.json"`. Use the actual schema-v3 field names and casing. Assert the successful public report selects all surfaces and passes, then assert a failed report selects all surfaces and preserves the exact CLI exit code. On success, failure, timeout and supported cancellation, wait for bounded cleanup and immediately bind the same port. In the unrelated-listener case verify that listener remains reachable instead.
- Add the pinned single-file example to the Console README with a copyable command that runs in an empty directory on the pinned SDK. Resolve a synthetic typed secret there, assert the expected result in memory, and scan stdout and stderr for disclosure without printing the value. Help must succeed with an unavailable secret provider and zero secret payload reads.
- Explicitly reconcile generated files, vendored tooling and browser-runtime assets as exclusions with paths and reasons. Record the recommendation in TODOS.md after the field exercise and canary comparison, choosing a documented convention, a repo-local helper or a separately scoped public API proposal.
- Emit a publication message on stderr with the destination only after atomically installing this run's valid report. On startup failure or invalid output, explicitly say this run produced no evidence and any existing destination is from a prior run. File existence alone never qualifies. Exercise a pre-existing successful report on both failure paths. This protects caller files and prevents a partial write without modifying the public JSON schema.
- Keep three independent decisions on the issue. The inventory can land on its reconciliation proof. Promote the replacement on native parity and cleanup proof. Recommend a public binding design only when a second real command repeats the same mechanical work and the local operation would remove it without weakening its validation or diagnostic. A one-person exercise plus a paper comparison can justify documenting the convention or exploring a repo-local helper. A working public package example must declare the local attribute and binding operation in the example itself if the pinned packages lack them.
- Use one implementation PR with separately reviewable commits by default. Before rollout, the CI owner records the existing path-filtered job’s timeout and relative cold/warm costs. Timebox the initial portability investigation to the issue’s 0.5–1 engineering day estimate. If parity is still blocked, ship the independently accepted documentation, leave the existing commands canonical and keep the unfinished pilot on its implementation branch until a maintainer owns the failing case.
<!-- /autoplan-accepted:ceo -->


### Strategy review

**Input and scope.** The owner approved B during office hours. Three passes of the scope document scored 6.4, 7 and 7 out of 10. The final two placement issues were checked against the amended working plan and scope summary at the review cap. The spec reviewer has not graded those final edits. Native strategy review returned six concerns. The outside review returned six concerns on its validated retry. The first outside response lacked the required completion marker. The full CEO methodology was read through line 2160.

| Dimension | Native | Outside | Decision |
| --- | --- | --- | --- |
| Premises | Portability alone cannot establish API value | One exercise is weak evidence | Confirmed. Require a second command for a public proposal. |
| Problem | Evaluate scripts by user task | Inventory has independent value | Confirmed. Give the inventory its own acceptance. |
| Scope | Separate operational and design outcomes | Unbounded pilot risks dual maintenance | Confirmed. Defer promotion if the timebox expires. |
| Alternatives | Compare A, B and C by adopter cost | One exercise cannot exclude C permanently | Confirmed. Keep B for this pilot. |
| Competitive risk | Compare existing public authoring conventions | No independent finding on alternatives in the ecosystem | Partial. Check the actual contributor experience. |
| Six-month trajectory | Avoid a framework built around a single demanding pilot | Set cost expectations and a stopping point | Confirmed. Record relative cost and a follow-up owner. |

**What already exists.** The Bash verifier supplies status and exact CLI arguments. PowerShell supplies a deadline and child cleanup. The public PWA verifier writes the canonical report. AppSurface.Config already resolves environment mappings, JSON and typed secrets. The native config contract workflow provides the runner matrix. Reuse each owner’s behavior.

**Architecture and data flow.** Source inventory → classification → ranked guide. Separately, optional switches + Config → final validation → owned child → existing PWA CLI → evidence → cleanup. The documentation decision needs inventory coverage. Deletion requires native proof. Public API work requires evidence from another command.

**Error and rescue registry.**

| Condition | Consequence | Recovery and verification |
| --- | --- | --- |
| Missing vs malformed configuration | Incorrect fallback | Nullable resolution for missing keys, sanitized validation error for invalid values before child launch |
| Occupied port or child exits during startup | Wrong app can appear ready | Require child-specific readiness, fail boundedly and leave unrelated listener alive |
| CLI returns a failing report | Failure evidence lost | Publish valid failure JSON and propagate the exact CLI code |
| Incomplete output or cancellation | Old report mistaken for fresh, orphaned child | Record no publication, dispose owned process tree and assert port reuse |

**Security.** Test secret resolution with synthetic data and scan emitted streams. Keep argument lists structured. Operate only on processes created by this invocation.

**Data interaction and quality.** Explicit default values and cwd-dependent paths require separate presence and resolution tests. Keeping the binding internal gives the maintainer one place to answer whether parsing, lookup or validation has failed.

**Verification.** Unit-level cases cover boundaries and the external package exercise covers installability. The new matrix carries real lifecycle checks. The canary analysis carries design evidence only.

**Performance and observability.** Cold restore and warm runs have different costs. Capture both on a named runner. Record the phase, environment name, key and child exit where relevant. Keep raw secret values out of diagnostic output.

**Rollout and future.** Submit one PR by default. Record the green revision before deletion and rerun on the final revision. Keep the inventory independently landable. Public binding, bigger migrations and an automatic inventory policy are outside delivery. The twelve-month goal is a guide that points maintainers toward reusable commands when they have a demonstrated external task.

**Failure modes.** Incorrect precedence harms a single invocation and is detected before launch. Stale reports or an unrelated listener could falsely green a CI check and have dedicated process regressions. CI amplification is measured before retirement. The file migration remains reversible with a single restoration of wrappers and callers.

**Work sequence.** First pin the implementation revision and compare the wrappers. Next prove absent vs explicit inputs and the child's identity. Then exercise the installed package and native matrix. Finish with contributor feedback, caller replacement and a final green revision. Estimated human effort from #928 is 2.5–3.5 days plus queue time. Hosted review and CI latency is unmeasured.

**Completion summary.** Selective expansion. Three scope items accepted, one deferred, one skipped. Architecture, error recovery, security, interaction, quality, tests, performance, observability, rollout and long-term maintenance examined above. No rendered UI change. Four error/recovery cases. No unmitigated release-critical gap in the plan. Runtime evidence remains due during implementation. Reversibility is high because wrappers can be restored together. No scored scope-choice preference. The architecture and test path are shown in the data flow above.


```text
REALITY: premise-table pass unverified=1. Existing duplication is observable, future public demand is a hypothesis. examples/web-pwa-install/verify.sh:1
REALITY: already-done pass Fetched main and checked named paths and file-based commit subjects. The wrapper remains canonical. examples/web-pwa-install/README.md:26
REALITY: surface-check pass Existing verifier handles combined reports. The pilot entry point is planned. Cli/ForgeTrust.AppSurface.Cli/PwaCommand.cs:48
REALITY: binding-decisions pass Owner selected the internal attribute, final validation and native removal gate. docs/designs/issue-928-file-based-cli-binding.md:7
REALITY: numbers pass SDK pin is exact, historical inventory totals require reconciliation at implementation. global.json:3
REALITY: deferred-asks pass Heavier migrations and public binding are follow-ups as agreed. docs/designs/issue-928-file-based-cli-binding.md:31
REALITY: pr-count pass Default one PR with verifiable checkpoints. docs/designs/issue-928-file-based-cli-binding.md:25
REALITY: prior-art pass Adopt existing CliFx switch parsing and AppSurface.Config lookup. Console/ForgeTrust.AppSurface.Console/README.md:57
REALITY: contract-assumptions pass Provider file locations and the Core port shortcut require actual consumer proof. ForgeTrust.AppSurface.Core/AppSurfaceStartup.cs:140
REALITY: units pass The existing timeout is already expressed in seconds. examples/web-pwa-install/verify.ps1:5
REALITY: perf-table finding No controlled timings yet. Measure cold and warm against same-OS baseline at implementation. examples/web-pwa-install/verify.sh:20
REALITY: dependency-fallback pass Guides can ship independently. docs/designs/issue-928-file-based-cli-binding.md:31
REALITY: obligation-tiers pass Inventory and guarded pilot are delivery work while public API work is deferred. docs/designs/issue-928-file-based-cli-binding.md:31
```

<!-- autoplan-accepted:dx -->
- Put the installed SDK version check, checkout command and outside-checkout example at the first meaningful README mention. Verify SDK 10.0.402 in the actual caller directory. Link a single precedence table from the Console README, example and contributor guide. CLI help lists each flag, logical key, applicable environment name and default through metadata. The first two names preserve legacy scripts. The timeout uses the normal hierarchical spelling.
- Give an example with verifier environment Production using appsettings.json beside the source, and a separate Development child. Add an example running an absolute script path from another directory. Document inherited host environment selection, how to select the matching JSON file, and the effect on a relative evidence destination. The verifier selects default port 5055 and passes it to the child.
- Report errors on stderr and return nonzero. For usage, include a stable pilot-owned category or existing diagnostic, switch or logical key, allowed range and a concrete correction. Identify explicit CLI input vs Config resolution. Include a provider or environment identity only when available from existing, safe diagnostics. Never guess file-vs-environment provenance. Preserve native Config diagnostic codes. Avoid echoing input values in exceptions, logs or validation messages. Use sanitized operation/category context for unexpected errors. Test stderr, exit, publication status and synthetic redaction.
- For the destination, reject an existing directory or invalid file name. Create a missing parent and sibling temporary file, handle denied creation, flush/replace failure and cleanup failure explicitly. Preserve the previous valid artifact on failed publication. Say which operation failed, whether a report was published and how to choose a writable path. If cleanup alone fails after publication, identify the published file and leftover temporary path. Test via injectable file operations where OS permissions do not reproduce reliably.
- In the clean-directory exercise, time from opening the Console README with the pinned SDK installed to a successful invocation. Record cold restore, warm rerun, OS, machine and first confusion. Target under five minutes with a functioning feed. If missed, explain the cause and correct documentation or setup friction. A feed outage cannot decide wrapper retirement. Copied internal example code is owned by its example and versioned with the documented package pin.
<!-- /autoplan-accepted:dx -->

### Developer experience review

The primary reader is a maintainer on Windows or Linux following a checkout README. A secondary reader owns a different .NET project and has only the published packages. “I need to know what to run, which process I verified and whether I can trust the report after a failure.”

| Stage | Developer action | Proof or intervention |
| --- | --- | --- |
| Discover | Open CONTRIBUTING or the PWA example | Direct link to the guide and canonical invocation |
| Install | Check SDK from the actual working directory | Exact version and feed prerequisites |
| First success | Copy one run command | Parse the current report, record cold and warm time |
| Configure | Give a JSON value, then an environment override or flag | Predict the effective value, check child port |
| Recover | Supply an invalid port or unwritable directory | Identify the input or operation and next safe action from stderr |
| Extend | Add a fourth setting in a clean directory | Record repeated code and where help was needed |

The official .NET example establishes a low-friction baseline with a single file, a package directive and one run command. The local Console authoring guide establishes the existing CliFx vocabulary. The maintainer comparison is also the two existing wrappers on the same machine. The package probe reported during office hours verified functionality, while time to first success has yet to be measured. [Microsoft file-based app reference](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps)

| Dimension | Before | Amended design | Reason |
| --- | ---: | ---: | --- |
| Getting started | 6 | 8 | One command, explicit prerequisites, measurement still due |
| API and CLI | 8 | 8 | Familiar switches, help names both environment conventions |
| Error messages | 5 | 8 | Recovery and source context defined, negative-path proof due |
| Documentation | 7 | 8 | One copyable precedence table and path examples |
| Upgrade path | 7 | 8 | Both wrappers retired together on a named revision |
| Development environment | 6 | 8 | Tests run outside checkout and on native operating systems |
| Community and ecosystem | 7 | 7 | Named maintainers and package consumers are the target |
| Measurement and feedback | 6 | 8 | First run and repeated work measured independently |

Average design score moves from 6.5 to 7.9 out of ten. Outside assessed the incoming plan at 6/10. The first outside attempt had unrecognized severity formatting. Native returned four findings. No observed time-to-first-success figure exists. The target with SDK installed is under five minutes.

| Consensus dimension | Native | Outside | Decision |
| --- | --- | --- | --- |
| First run | Missing time target | Wants a cold target | Confirmed. Measure and publish. |
| Naming | Mapping is understandable but needs canonical help | Mixed names need explanation | Confirmed. Preserve aliases. |
| Errors | Provider and filesystem recovery incomplete | Source and redaction incomplete | Confirmed. Define actionable failures. |
| Docs | State precedence once | Flag names and package copies need documentation | Confirmed. Use one canonical reference. |
| Upgrade | Pilot-owned pattern, defer API | Copying requires version guidance | Confirmed. Pin and version the example. |
| Environment | SDK prerequisites need an example | Restore cost matters | Confirmed. Exercise a clean directory. |

**Implementation checklist.** Publish the SDK check, three-key reference and both working-directory examples. Run the synthetic-secret success, help and controlled failure cases. Exercise an invalid flag, an invalid provider result and an unwritable path. Capture the first contributor run and its environment. These are plan scores and future checks, not measured product behavior.


<!-- autoplan-accepted:eng -->
- Use a fresh random proof value per launch, carried in the child-only environment. An opt-in Development-only endpoint on the example returns that value only on loopback. A fixed path request never sends the expected value to the listener. Disable proxying and redirects for this check. Compare the response with the expected value at the exact loopback scheme and port. Also require that the launched child is alive and both manifest and status probes succeed. Recheck identity before accepting a completed report. Test missing and prior-run values and child exit. Leave the route absent when verification is disabled. This is an instance check for accidental collisions.
- Build the example and CLI before capture. Launch the built products with structured argument lists, explicit working directories and no compilation output on the JSON stream. Preserve the sample content root and required static assets. Register CliFx cancellation and track each owned process as soon as it starts. Concurrently drain stdout/stderr to bounded buffers or files. Enforce one monotonic startup deadline with bounded HTTP attempts. On cancellation during build, startup or verification, terminate the owned process trees and wait up to five seconds total for teardown using a fresh cancellation budget. Use a native outer watchdog of ten seconds for the cleanup assertion. Exercise POSIX SIGINT and a native console Ctrl+C event on Windows in an isolated console; if the runner cannot deliver an actual event, keep native retirement blocked. Check all owned PIDs as well as port reuse.
- Read one complete UTF-8 JSON document after child exit and EOF on both streams. Check schemaVersion, surface, passed and the required combined install/push/diagnostics shape against the current public PWA contract. Cross-check passed with exit status. A real valid failed report retains its exact nonzero exit. Treat empty, truncated, wrong-shape or contaminated output, inconsistent exit/report, inability to publish, and a child that dies during the proof as harness failures. Move the completed sibling temp file over the destination in the same directory. Verify both an absent and existing destination, rejected output and a failed replace on each OS. Document that concurrent invocations require separate destinations. The contract covers same-volume local filesystems used by the runners.
- Bind only declared, nonsecret optional properties. Fail mapping-definition errors before side effects. Validate missing, zero, empty and malformed values through the installed Config providers to establish actual behavior. Use the source directory supplied by the file-based runtime (or an explicitly verified source-path seam on the pinned SDK) to register the script-level IConfigFileLocationProvider after Config defaults. A different cwd with decoy JSON must not change which settings are loaded. Capture the caller cwd before starting children. Reuse the existing final-validation predicate for the paper canary comparison.
- The inventory owner uses the issue estimate of 1–1.5 days. If reconciliation cannot finish, report the outstanding locators and do not call it complete. Keep cold/warm benchmarks advisory and record the job’s enforced timeout before the first native comparison. A repeatable warm regression of over twice the baseline warrants maintainer review with raw durations. Native correctness and lifecycle proof remain mandatory.
- This engineering decision settles the earlier startup-notification alternative. The per-launch HTTP identity check is required on every runner. Treat host startup notifications only as diagnostic context. For the bounded native cancellation test, bind the launched processes to an isolated console or process group supported by that runner and verify the exact documented entry point receives cooperative cancellation. Record natural child exit separately from a teardown timeout.
<!-- /autoplan-accepted:eng -->

### Engineering review

**Existing components and scope.** CliFx parses options and registers cancellation. CommandService preserves a previously assigned exit code. Config supplies typed values, terminal provider failures and a replaceable source location. Its default location is AppContext.BaseDirectory, which can differ from the file’s source directory. PwaCommand serializes a V3 report before raising its failure exception. The existing HTTP readiness library checks status with a deadline and does not establish process identity. Avoid duplicating the public verifier or introducing a portable OS-to-PID socket database. The public Config and Console APIs and production canary command need no changes in this pilot.

```text
CliFx nullable options ---> local key mapping <--- Config manager / file-location provider
                                  |
                            final settings ----> .NET validation
                                  |
                          supervised entry point
                            |                |
                example (child identity)    public PWA CLI
                            |                |
                    manifest + status        v3 stdout / exit
                                             |
                                       validate and publish
Every child handle ------------------> one bounded teardown
```

**Code quality.** The file-based command has three owned responsibilities: bind final settings, supervise the example and run the existing verifier, then publish evidence. Keep any helpers local and readable. The explicit mapping metadata must drive binding or a validated lookup so an attribute edit changes actual lookup. A second unverified dictionary would defeat the contributor experiment.

**Codepath-to-test map.**

```text
discovery → program / caller / exclusion            I1     manual audit at SHA
flag present / absent / invalid / terminal          B1     process + Config integration
package help / secret / new setting                 B2     installed consumer
foreign cwd / path with spaces / environment        B3     process
child identity / unrelated listener / true success  P1     native OS matrix
start / cancel / timeout / teardown                 P2     native control events and PIDs
valid / failed / malformed / unpublished artifact   P3     real CLI + controlled fixture
cost / pre-removal / final revision / rollback      P4     CI and caller audit
final interval under / over attempt budget          C1     derived design comparison
```

Each is a new orchestration or documentation assertion. Existing PwaVerifierTests and CanaryPollTests cover their owners’ predicates, and existing provider tests cover provider behavior. They do not exercise the file entry point. The native tests must invoke the file normally. The engineering test plan records each branch, pass condition, existing evidence boundary and oracle. No LLM prompt or model evaluation is part of this feature.

**Engineering consensus.** Seven native concerns and five outside concerns. Both flagged ownership of the listening endpoint, cancellation and the adequacy of lifecycle tests. The originally unresolved critical design gaps now have explicit acceptance criteria. Native execution is still outstanding.

| Dimension | Native | Outside | Outcome |
| --- | --- | --- | --- |
| Architecture | Identity proof unresolved | Choose a mechanism | Confirmed. Per-launch response from the owned child. |
| Tests | Need cancellation and negative report cases | Native cancellation and orphan tests missing | Confirmed. P1–P3. |
| Performance | No independent budget finding | Requests numeric cost limits | Single voice. Use a relative review trigger. |
| Security | Verify ownership and redact errors | Flags identity risk | Confirmed on scope of identity. |
| Errors | Define strict publication and cleanup | Highlights cancellation cleanup | Confirmed on cancellation. Add native-only publication cases. |
| Deployment | Supports staged retirement | Prefers two PRs | Owner choice on sequencing. Same native gate in either case. |

**Performance and failure registry.** The dominant costs are restore, build and HTTP startup. Build each target once per run and keep polling bounded. No database or multi-tenant dependencies. Invalid configuration must terminate before starting processes. An impostor, owned process exit, or unusable cancellation signal blocks the native gate. Malformed or mismatched evidence fails without replacement. CI-check timeouts cap hung jobs but do not prove cleanup. After a native failure the canonical wrappers continue to work.

**Deferred scope and completion.** A shared binding API, generated switches, production canary binding and heavier CLI migrations require follow-up evidence under TODOS.md. The failure matrix and test plan now cover the two critical incoming gaps. There are zero unresolved design blockers to starting the pilot, two native proof obligations that can block retirement, and one owner choice about sequencing. Scoped architecture, quality, testing, performance and rollback were reviewed. No product changes were executed during planning.

```text
REALITY: premise-table pass unverified=1. Existing duplication is observable, future public demand is a hypothesis. examples/web-pwa-install/verify.sh:1
REALITY: already-done pass Fetched main and checked named paths and file-based commit subjects. The wrapper remains canonical. examples/web-pwa-install/README.md:26
REALITY: surface-check pass Existing verifier handles combined reports. The pilot entry point is planned. Cli/ForgeTrust.AppSurface.Cli/PwaCommand.cs:48
REALITY: binding-decisions pass Owner selected the internal attribute, final validation and native removal gate. docs/designs/issue-928-file-based-cli-binding.md:7
REALITY: numbers pass SDK pin is exact, historical inventory totals require reconciliation at implementation. global.json:3
REALITY: deferred-asks pass Heavier migrations and public binding are follow-ups as agreed. docs/designs/issue-928-file-based-cli-binding.md:31
REALITY: pr-count pass Default one PR with verifiable checkpoints. docs/designs/issue-928-file-based-cli-binding.md:25
REALITY: prior-art pass Adopt existing CliFx switch parsing and AppSurface.Config lookup. Console/ForgeTrust.AppSurface.Console/README.md:57
REALITY: contract-assumptions pass Provider file locations and the Core port shortcut require actual consumer proof. ForgeTrust.AppSurface.Core/AppSurfaceStartup.cs:140
REALITY: units pass The existing timeout is already expressed in seconds. examples/web-pwa-install/verify.ps1:5
REALITY: perf-table finding No controlled timings yet. Measure cold and warm against same-OS baseline at implementation. examples/web-pwa-install/verify.sh:20
REALITY: dependency-fallback pass Guides can ship independently. docs/designs/issue-928-file-based-cli-binding.md:31
REALITY: obligation-tiers pass Inventory and guarded pilot are delivery work while public API work is deferred. docs/designs/issue-928-file-based-cli-binding.md:31
```


## Decision audit trail

| Phase | Decision | Reason |
| --- | --- | --- |
| Strategy | Reconcile a named SHA, with caller and source counts kept separately | A workflow step may invoke an already counted script |
| Strategy | Keep SDK bootstrap scripts runnable before SDK installation | A file-based replacement would create a circular prerequisite |
| Strategy | Give inventory, portability and API adoption separate acceptance decisions | Native parity alone tells little about contributor cost |
| Strategy | Use one PR by default and split documentation if the pilot stalls | Review sequencing is the owner’s remaining choice |
| Strategy | Defer public binding and heavier migrations | No second implemented command or external adopter has demonstrated the need |
| DX | Declare internal binding code in the pinned outside-checkout sample | Existing public packages cannot supply the pilot’s internal attribute |
| DX | Explain the three environment spellings in help and give both path bases | Maintainers can predict which file and value a run uses |
| DX | Define provider, validation and filesystem recovery on stderr | A stale file can otherwise look like new proof |
| DX | Target a first success under five minutes with the SDK installed | The clean-directory exercise will supply the actual value |
| Engineering | Require a per-launch child identity and bounded cancellation on each OS | Port liveness and manifest responses alone can come from another process |
| Engineering | Validate a complete combined report before replacing the destination | The public CLI can produce valid JSON before returning failure |
| Engineering | Keep startup, cleanup and CI-cost numbers explicit | These are proposed budgets for implementation proof, not measurements |
| Engineering | Evaluate canary with its existing predicate on paper | This respects the approved scope for a second command |

### Implementation tasks

- [ ] **T1 · Inventory.** Reconcile the script inventory at a named commit and publish the decision guide. Language totals mix programs and callers.
- [ ] **T2 · Verifier.** Implement explicit switch mapping and validate final settings before starting children. Config validation can run before overrides.
- [ ] **T3 · Verifier.** Prove child identity, bound teardown and publish the existing CLI’s schema-v3 reports. Manifest-only readiness and incomplete output can falsely pass.
- [ ] **T4 · CI.** Run native regressions before and after deleting the two wrappers. Windows console cancellation and port reuse need actual runner proof.
- [ ] **T5 · Console docs.** Run the pinned one-file package example and contributor exercise outside the checkout. The internal attribute is not shipped by the existing packages.
- [ ] **T6 · Guidance.** Compare canary’s final values and record the binding and tooling recommendations. A working PWA runner alone cannot justify a public API.

### Final review record

Strategy and engineering each passed all 13 applicable reality checks. One unverified premise is demand for a broader public binding API. The visual review was skipped. The full methodology reads covered CEO lines 1–2160, DX lines 1–1825 and engineering lines 1–2057. Each applied phase had both a completed native and a validated outside review. Across phases, independent findings concerned process identity, contributor diagnostics and keeping the three delivery decisions independent. The scope review reached its three-pass cap at 7/10. Its two final edits were checked locally. The sequential orchestration guard is unavailable on this host; this run used the phase artifacts and actual reviewer results. Timing was 33 minutes at engineering close.

**Owner decision.** On November 20, 2026, the owner accepted the recommendations at gate revision 1 (`rec`, selecting `d1a` and `p1a`). Use one PR with independently reviewable inventory and pilot commits. The inventory and guide can land separately if native verification stalls. Retire both wrappers after the Windows, Linux and macOS checks pass, then rerun the checks on the final revision.
