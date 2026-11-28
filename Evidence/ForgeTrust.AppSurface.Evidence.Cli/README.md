# ForgeTrust.AppSurface.Evidence.Cli

`ForgeTrust.AppSurface.Evidence.Cli` supplies the internal workflow used by the public `appsurface evidence` commands. Most adopters install the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md), not this package directly.

Start with the [EvidenceHost guide](../../start-here/evidencehost.md). The workflow reads only explicit policy and diff inputs; it does not scan consumer assemblies, discover test projects, provision third-party services, or report outbound usage telemetry. The preview [`test` producer](#test-producer-preview) lists the tests of the projects a policy declares; it never searches for projects.

See the canonical [producer reuse guide](../../guides/evidence-reuse.md) for complete input declarations, optional multi-file report custody, cooperative child lifetime, reuse/audit CLI behavior, and candidate 1.1 constraints.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Command workflow

```bash
appsurface evidence init --sample
appsurface evidence doctor --path src/Orders/SubmitOrder.cs
appsurface evidence explain --path src/Orders/SubmitOrder.cs
appsurface evidence run --diff-file artifacts/changed.patch --solution App.slnx
appsurface evidence verify TestResults/evidence/evidence-manifest.json
```

| Command | Behavior |
| --- | --- |
| `init --sample` | Creates a marked, non-overwriting policy, host skeleton, and local README. `--force` may replace only previously marked starter files. |
| `doctor` | Resolves policy and diff, checks selected Docker and browser prerequisites, and reports [envelope verification status](#doctor-preflight). |
| `explain` | Writes the resolved plan and a human-readable summary without running producers. |
| `run` | Runs selected built-in `coverage`, [scoped coverage](#scoped-patch-coverage) and preview [`test`](#test-producer-preview) evidence one producer at a time, writes a canonical plan/manifest/summary, and writes a GitHub step summary when available. Once the new plan passes validation, the command deletes the earlier manifest, summary and the selected `test` and scoped coverage producers' artifact directories before starting producers, so a cancelled rerun cannot leave an old passing manifest or receipt behind. Consumer-owned browser/E2E or resource-backed producers run through the separate Aspire EvidenceHost package. |
| `verify` | Recomputes binding and digest verification and checks every manifest-listed artifact on disk, without running any producer. See [Verifying evidence](#verifying-evidence). |

Policy, plan, and manifest inputs use the shared [bounded JSON reader](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input). The CLI counts up to 20 MiB per input before parsing and rejects malformed or ambiguous documents. Invalid or unreadable policy input reports `ASEVD205`. Invalid or unreadable plan and manifest input reports `ASEVD209`. These errors omit JSON content and raw reader messages. Missing or invalid input paths report `ASEVD204` for policy and `ASEVD208` for plans and manifests.

The CLI also checks generated plans and manifests against the same 20 MiB verification limit before writing them. A plan can be larger than its policy because it contains both the selected profile and the policy snapshot. `explain` and `run` report `ASEVD217` if the plan exceeds the limit. `run` rejects an oversized plan before retiring earlier output or starting producers. It reports the same code if the resulting manifest exceeds the limit. Shorten descriptive policy text, changed-path inputs, or diagnostics and rerun.

## Doctor preflight

A valid policy and diff return exit code 0 with `ready_with_external_prerequisites`. The `trusted-envelope`
check reports `unverified` for every profile, including in GitHub Actions. Read the individual checks for
Docker and browser requirements. A planning or input error returns a nonzero exit with its diagnostic.

Use the report to prepare a run. Release-envelope validation takes place in the consumer's
[protected workflow with its registered verifier](../../guides/evidencehost-cookbook.md#release-evidence).

## Incomplete profiles are not complete evidence

An intentionally selected `no-evidence` profile may close a gate. A skipped test project, unavailable browser, missing Docker runtime, or unsupported producer cannot. A filtered test suite cannot close a repository-wide coverage assertion; it can close the [`test` assertion](#test-producer-preview) of a producer that names its filter in the plan, or the [scoped patch coverage assertion](#scoped-patch-coverage) of a producer that names its projects and sources in the plan. `run` returns a failing command and a manifest with `ClaimKind.None` for those cases, preserving the diagnostic and next action in `evidence-summary.json`.

For an `ObservationOnly` claim, the `next` field in `evidence-summary.json` identifies the result as informational and directs gate consumers to run fresh evidence without `--observation-only`.

The built-in coverage producer is an in-process adapter over the private [`ForgeTrust.AppSurface.Evidence.Coverage`](../ForgeTrust.AppSurface.Evidence.Coverage/README.md) engine shared with `appsurface coverage run` and `appsurface coverage gate`. Its policy declaration carries the exact overall and optional patch thresholds, tolerance, and patch-line mode; the resolved plan binds those values before collection begins. When a patch gate is selected, `evidence run` captures the bounded `--diff-file` bytes during planning and reuses that immutable snapshot for gate evaluation, so replacing the file mid-run cannot change the measured input. It does not silently convert a partial test selection into a full-profile claim.

The CLI reuse resolver has an internal per-instance `sdkExecutableForTests` seam for deterministic SDK-version-query tests. A supplied path must be fully qualified; it affects only SDK version queries. Production omits it and resolves `dotnet` normally. It does not replace the coverage engine process runner or add a user-facing executable override. The existing 30-second query deadline also covers stdout/stderr draining, including after root exit. Deadline expiry returns `unavailable`; caller cancellation propagates. Actual root termination is joined on cancellation. Releasing the readers does not prove that an unregistered descendant has stopped; the query never admits a receipt from incomplete SDK identity.

Coverage declarations may also set `coverageExecution` with a simple `configuration` identifier, `parallelism` from 1 through 32, and `testResults` of `none` or `junit`. The defaults preserve the legacy Debug, serial, no-managed-results run. These options select the build and test envelope only; the built-in producer keeps its collector, AppSurface test exclusions, coverage gates, and source scope unchanged. See the [`EvidenceCoverageExecutionOptions` contract](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceCoverageExecutionOptions.cs) for the public shape and validation limits.

Coverage execution uses the same fail-mode [watchdog and no-dump VSTest hang policy](../../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) as `coverage run`. Its `TimeoutSeconds` producer deadline includes discovery and build time and can further limit the automatic VSTest timer at each test launch. Evidence also fixes the no-progress watchdog at 10 minutes, which caps the automatic VSTest timeout at 9 minutes even when `TimeoutSeconds` is increased. Evidence has no watchdog or test-argument opt-out for disabling automatic blame; use `coverage run` when those controls are needed. An expired producer deadline maps to `TimedOut` without assertions. After a timeout, the Evidence result may point to `coverage/timings.json` when that owned artifact was written; it never copies test names or sequence XML into the result. The [#815 migration guide](../../releases/issue-815-vstest-hang.md#migration-choices) explains the timing limits and actor-specific outcomes.

## Test producer (preview)

The built-in `test` producer runs the test projects a policy declares and closes one assertion, `appsurface/test/discovery-complete@1`, only when **every test the declared projects and filters selected ran once and passed**. It makes no coverage claim.

**Preview.** The kind `test`, the assertion id and receipt schema version 1 may change without a compatibility step until a real route has closed with them. See the [upgrade note](#upgrade-note-and-mixed-versions).

**When to use it.** Use it for paths that have no production code to cover, such as workflows, scripts and release tooling, where the truthful evidence is that the tests exercising them ran. Use the `coverage` kind when a coverage threshold is the evidence. For resource-backed or browser tests, register your own producer in an [EvidenceHost](../ForgeTrust.AppSurface.Evidence.Aspire/README.md); the `test` kind is not available through a resource-backed profile.

### First run

Add this profile to `profiles` and this rule to `rules` in your [evidence policy](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#test-producer-declaration-preview). The only value to change is the project path.

<!-- test-producer-profile: begin -->
```json
{
  "id": "tooling-tests",
  "scope": "Targeted",
  "resources": [],
  "producers": [
    {
      "id": "tooling-tests",
      "kind": "test",
      "version": "1.0.0",
      "requiredResources": [],
      "assertionIds": ["appsurface/test/discovery-complete@1"],
      "artifactSlots": [
        { "logicalName": "test-receipt", "relativeRoot": "test", "mediaType": "application/json", "required": true, "maximumBytes": 65536 }
      ],
      "timeoutSeconds": 900,
      "test": {
        "projects": [
          { "path": "tests/REPLACE-ME.Tests/REPLACE-ME.Tests.csproj" }
        ]
      }
    }
  ],
  "obligations": [
    {
      "id": "tooling-tests-complete",
      "riskClass": "tooling",
      "rationale": "Script changes need the tests that exercise them to run and pass.",
      "requiredProducerIds": ["tooling-tests"],
      "requiredAssertionId": "appsurface/test/discovery-complete@1"
    }
  ]
}
```
<!-- test-producer-profile: end -->

<!-- test-producer-rule: begin -->
```json
{ "id": "scripts", "pattern": "scripts/**", "profileId": "tooling-tests", "precedence": 0 }
```
<!-- test-producer-rule: end -->

Then run the three commands from the repository root:

```bash
appsurface evidence explain --path scripts/build.sh
appsurface evidence run --path scripts/build.sh
appsurface evidence verify TestResults/evidence/evidence-manifest.json
```

`explain` starts no process and shows what will run:

```text
Evidence plan: tooling-tests (targeted)
Why: scripts
Changed paths: scripts/build.sh
Obligations: tooling-tests-complete
Required producers: tooling-tests
Required resources: none
Test producer 'tooling-tests' (timeout 900s):
  tests/Tooling.Tests/Tooling.Tests.csproj | filter: (none, every test) | minimumTests: 1
Preview: the 'test' producer kind, its assertion id and its receipt format may change without a compatibility step. See Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#test-producer-preview
```

`run` prints one progress line per phase on standard error, then the claim and the counts table. The same table goes to the GitHub step summary.

```text
[1/1] evaluating
[1/1] restoring
[1/1] building
[1/1] discovering
[1/1] running 41 tests
Notice: tests/Tooling.Tests/Tooling.Tests.csproj discovered 41 tests and declares no minimumTests; a filter that starts matching fewer would still pass. Consider "minimumTests": 41.
Evidence result: TargetedComplete
Execution: Passed
Closed obligations: tooling-tests-complete
Unmediated obligations: none
Next: Targeted evidence is complete.
Check manifest provenance and gate requirements separately.
Test producer 'tooling-tests':
| Project | Discovered | Executed | Passed | Failed | Skipped | Reason |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| tests/Tooling.Tests/Tooling.Tests.csproj | 41 | 41 | 41 | 0 | 0 | Complete |
No coverage claim was made.
```

`verify` prints the manifest's claim and eligibility on its first line, followed by a reminder about
provenance and gate requirements.

```text
Evidence manifest verified: TargetedComplete (PullRequestGate)
Check manifest provenance and gate requirements separately.
```

An incomplete run exits non-zero with `ASEVD211`. Its first line names the operation and the project, and each incomplete project gets its reason, the next action, a documentation anchor and the command to rerun:

```text
evidence run: test producer 'tooling-tests' is incomplete for tests/Tooling.Tests/Tooling.Tests.csproj (1 of 1 project(s) incomplete, 1 with failing tests).
- producer=tooling-tests project=tests/Tooling.Tests/Tooling.Tests.csproj phase=reconcile reason=TestsFailed
  next: Run the printed command and fix the failing tests. Rerunning unchanged cannot help.
  docs: Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#reason-testsfailed
  rerun (run from the project directory), one argument per line:
      dotnet
      test
      Tooling.Tests.csproj
      ...
  as one POSIX shell line: dotnet test Tooling.Tests.csproj -c Debug --no-build --nologo --disable-build-servers -nodeReuse:false -tl:off
```

Compiler and test output are not shown and not kept, because they can contain test names. The one-line shell form is printed only when every argument can be quoted safely.

### Choosing `filter` and `minimumTests`

1. Start with no filter. The producer then selects every test in the project.
2. Add a filter only by fully qualified name prefix, for example `FullyQualifiedName~MyCompany.Tooling.Release`. The filter is a [VSTest filter expression](https://learn.microsoft.com/dotnet/core/testing/selective-unit-tests), passed as one argument and never through a shell. `&` and `|` inside a value need a backslash.
3. Set `minimumTests` to the count the first passing receipt reports. Without it, a filter that starts matching fewer tests can still pass if it matches at least one test and every selected test runs and passes; a filter that matches no test always fails with `EmptyDiscovery`. `run` prints a notice when more than one test was discovered and no minimum is declared.

The filter is public plan data: it appears in the policy, the plan, `explain` and rerun hints. Changing a project, a filter or a minimum changes `PlanDigest`.

### What the producer does

For every declared project, in declaration order:

1. **File check, no process.** The project file must exist and stay inside the repository after links are resolved.
2. **Evaluate** with `dotnet msbuild -getProperty`: toolchain present, one target framework, no run-settings file (`RunSettingsFilePath` or `VSTestSetting`), VSTest runner.
3. **Restore**, then **build** with `--no-restore`.
4. **Discover** with `dotnet test --no-build --list-tests`. A listing with no test skips the run.
5. **Run** with a TRX logger into a private scratch directory, then **reconcile** the listing with the results by test name.

Steps 1 and 2 finish for every project before the first restore, so a mistake in the last declared project is reported before any build. Every project is attempted even after an earlier one is incomplete. The producer owns a deadline earlier than `timeoutSeconds` (a reserve of one tenth, between 1 and 30 seconds) so it can still write its receipt. Child processes run with standard input closed, build servers off and English output.

### Reasons

Each project gets exactly one reason: the first in this table that applies. `Unavailable` means the producer could not observe the project; `Failed` means it observed the project and the assertion does not hold. The producer outcome is the most severe project outcome in the order `TimedOut`, `Unavailable`, `Failed`, `Passed`, and only `Passed` closes the assertion.

| Reason | Outcome | Meaning | Next action, and whether rerunning unchanged can help |
| --- | --- | --- | --- |
| <a id="reason-projectnotfound"></a>`ProjectNotFound` | `Unavailable` | The declared project file does not exist, or resolves outside the repository. | Policy error: correct the declared project path. Rerunning unchanged cannot help. |
| <a id="reason-toolchainunavailable"></a>`ToolchainUnavailable` | `Unavailable` | The `dotnet` host could not start or reported no compatible SDK. | Install the SDK that global.json requires or put the right dotnet on PATH. Rerunning unchanged cannot help. |
| <a id="reason-multitargetunsupported"></a>`MultiTargetUnsupported` | `Unavailable` | The project evaluates to more than one target framework. | This preview runs single-target projects only; declare a single-target test project. Rerunning unchanged cannot help. |
| <a id="reason-runsettingsunsupported"></a>`RunSettingsUnsupported` | `Unavailable` | The project evaluates `RunSettingsFilePath` or `VSTestSetting`. | Remove RunSettingsFilePath and VSTestSetting for this project or use other evidence; a run-settings file can select tests the policy does not name. Rerunning unchanged cannot help. |
| <a id="reason-unsupportedrunner"></a>`UnsupportedRunner` | `Unavailable` | The nearest `global.json` or `TestingPlatformDotnetTestSupport` selects a runner other than VSTest. | Microsoft.Testing.Platform is not supported in this preview (tracked in TODOS.md); use a VSTest project. Rerunning unchanged cannot help. |
| <a id="reason-restorefailed"></a>`RestoreFailed` | `Unavailable` | `dotnet restore` failed. | Run the printed dotnet restore command and fix the feed or package error. Rerunning unchanged can help after an outage. |
| <a id="reason-buildfailed"></a>`BuildFailed` | `Failed` | `dotnet build` failed, or the project could not be evaluated. | Run the printed command and fix the build errors. Rerunning unchanged cannot help. |
| <a id="reason-discoveryfailed"></a>`DiscoveryFailed` | `Unavailable` | The listing exited non-zero (`ExitCode`), had an unrecognized shape (`ListingShapeUnrecognized`), or exceeded 8 MiB (`OutputTooLarge`). | Run the printed listing command and compare its output with the documented shape. Rerunning unchanged can help only for a transient exit code. |
| <a id="reason-runaborted"></a>`RunAborted` | `Failed` | The test host stopped, or the run exited non-zero without reporting a failed test. | The test host stopped without reporting a failed test; run the printed command to see why. Rerunning unchanged can help. |
| <a id="reason-resultsmissingorstale"></a>`ResultsMissingOrStale` | `Unavailable` | The results file was missing, malformed, too large, or disagreed with its own counters. | The results file was missing, malformed or inconsistent. Rerunning unchanged can help. |
| <a id="reason-emptydiscovery"></a>`EmptyDiscovery` | `Failed` | The project and filter select no test. | The project and filter select no test; correct the filter. Rerunning unchanged cannot help. |
| <a id="reason-belowminimum"></a>`BelowMinimum` | `Failed` | Fewer tests were discovered than `minimumTests`. | Fewer tests were discovered than minimumTests; restore the missing tests or correct the minimum. Rerunning unchanged cannot help. |
| <a id="reason-ambiguousidentity"></a>`AmbiguousIdentity` | `Failed` | Two listed tests or two results print the same name. | Two tests print the same name; narrow the filter to avoid them or give them distinct names. Rerunning unchanged cannot help. |
| <a id="reason-missingresult"></a>`MissingResult` | `Failed` | A listed test produced no result under its listed name. | A listed test produced no result under its listed name; run the two printed commands and compare. Rerunning unchanged cannot help. |
| <a id="reason-unexpectedresult"></a>`UnexpectedResult` | `Failed` | A result has no listed test. | A result has no listed test; run the two printed commands and compare. Rerunning unchanged cannot help. |
| <a id="reason-testsfailed"></a>`TestsFailed` | `Failed` | Every listed test has one result and at least one failed. | Run the printed command and fix the failing tests. Rerunning unchanged cannot help. |
| <a id="reason-testsskipped"></a>`TestsSkipped` | `Failed` | Every listed test has one result, none failed, and at least one was skipped. | A selected test was skipped; exclude it by filter with a minimumTests guard or make it run. Rerunning unchanged cannot help. |
| <a id="reason-complete"></a>`Complete` | `Passed` | Every listed test ran exactly once and passed. | Nothing to do. |
| <a id="reason-deadlineexceeded"></a>`DeadlineExceeded` | `TimedOut` | The project was running when the producer's internal deadline passed. | The producer deadline passed during this project; raise timeoutSeconds or fix the hang. Rerunning unchanged rarely helps. |
| <a id="reason-notrun"></a>`NotRun` | `TimedOut` | The project had not started when the producer's internal deadline passed. | The producer deadline passed before this project started; fix the earlier project or raise timeoutSeconds. |

### Receipt

`run` writes `artifacts/<producer id>/test/test-receipt.json` for every outcome the producer returns, and [`verify`](#verifying-evidence) fails if it is later missing or altered.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | `1`. Preview: a reader must reject a version it does not know. A change to field meaning increments it. |
| `projects[]` | One entry per declared project, in declaration order. |
| `projects[].path` | The declared project path. |
| `projects[].filterSha256` | Lower-case SHA-256 of the UTF-8 filter text, or `null` without a filter. It binds the receipt to the plan; it is not confidentiality. |
| `projects[].minimumTests` | The effective minimum. |
| `projects[].discovered` | Tests the listing selected. Kept when the deadline passes after the listing finished. |
| `projects[].executed` | Results that passed or failed. |
| `projects[].passed`, `failed`, `skipped` | Results by outcome. Zero where a step never ran. |
| `projects[].reason` | One of `ProjectNotFound`, `ToolchainUnavailable`, `MultiTargetUnsupported`, `RunSettingsUnsupported`, `UnsupportedRunner`, `RestoreFailed`, `BuildFailed`, `DiscoveryFailed`, `RunAborted`, `ResultsMissingOrStale`, `EmptyDiscovery`, `BelowMinimum`, `AmbiguousIdentity`, `MissingResult`, `UnexpectedResult`, `TestsFailed`, `TestsSkipped`, `Complete`, `DeadlineExceeded`, `NotRun`. |
| `projects[].subReason` | `ExitCode`, `ListingShapeUnrecognized` or `OutputTooLarge` for `DiscoveryFailed`; otherwise `null`. |

A passing receipt:

```json
{"schemaVersion":1,"projects":[{"path":"tests/Tooling.Tests/Tooling.Tests.csproj","filterSha256":null,"minimumTests":41,"discovered":41,"executed":41,"passed":41,"failed":0,"skipped":0,"reason":"Complete","subReason":null}]}
```

An incomplete receipt:

```json
{"schemaVersion":1,"projects":[{"path":"tests/Tooling.Tests/Tooling.Tests.csproj","filterSha256":"dcad9588ff7dae9e7b2bc061753c6d6c69ec31e4275f513a71d5f3172cd32127","minimumTests":1,"discovered":4,"executed":4,"passed":3,"failed":1,"skipped":0,"reason":"TestsFailed","subReason":null}]}
```

The receipt, the manifest, `evidence-summary.json`, the step summary and the console never contain a test name. An internal producer error writes a trace with the exception type and stack frames only under `<output>/diagnostics`.

### Gate recipe for CI

`verify` proves the files are intact. It does not prove the gate passed. Check the claim as well:

```bash
appsurface evidence run --diff-file artifacts/changed.patch --output TestResults/evidence
appsurface evidence verify TestResults/evidence/evidence-manifest.json
jq -e '.ClaimKind == "TargetedComplete" and .Eligibility == "PullRequestGate"' TestResults/evidence/evidence-manifest.json
```

`run` already exits non-zero when the claim is `None`. The `jq` line is for a later job that receives the manifest as an artifact.

### What closes in this preview

- **Proven:** xunit v2 projects on VSTest, single target framework. Other frameworks are unproven and are not rejected by name: an NUnit or MSTest project whose listing and results match the supported shape can reach `Complete`. A project whose listing or results do not match that shape fails closed.
- **Identity is the printed test name.** In a survey of this repository, 41 of 59 measured test projects reconciled complete with no filter. Projects where two tests print the same name, or where a theory lists once and reports several rows, cannot close unfiltered; those include the largest suites. Narrow the filter to avoid such rows.
- **Not supported:** more than one target framework, a run-settings file, Microsoft.Testing.Platform, resource-backed profiles.

### Finding an ambiguous or missing test

`AmbiguousIdentity`, `MissingResult` and `UnexpectedResult` name no test, by design. To find it, run the two printed commands from the project directory and compare:

```bash
dotnet test My.Tests.csproj -c Debug --no-build --list-tests --filter "<your filter>" | sort > listed.txt
dotnet test My.Tests.csproj -c Debug --no-build --filter "<your filter>" --logger "trx;LogFileName=local.trx"
```

A name that appears twice in `listed.txt` is ambiguous. A `testName` in the TRX file that is not in `listed.txt`, or the reverse, is the unexpected or missing one. The usual causes are theory rows with non-serializable data and long string arguments that xunit shortens to the same text.

### Excluding known-skipped tests

A skipped test leaves the assertion open (`TestsSkipped`). To leave a deliberately skipped test out, exclude it by name and guard the remainder with a minimum:

```json
{ "path": "tests/Tooling.Tests/Tooling.Tests.csproj", "filter": "FullyQualifiedName~Tooling&FullyQualifiedName!~Tooling.Slow.SkippedCase", "minimumTests": 40 }
```

### Upgrade note and mixed versions

Tested on 2026-11-22 with released CLI 0.2.0-preview.14 against a CLI that knows the `test` block.

- A CLI that does not know the `test` property plans such a policy without complaint, reports the producer `Unavailable` at `run`, and yields claim `None`. It cannot produce a gate-eligible claim from a `test` profile.
- It also fails `verify` with `ASEVD203` on every plan a newer CLI wrote from a policy that holds a `test` block, including plans for changes that select no `test` producer.
- A plan written by an older CLI from such a policy verifies on a newer CLI, but its `PolicyDigest` is that of the policy without the block. A consumer that must know the block was in force compares `PolicyDigest` with one computed by an upgraded CLI.

**Rollout order:** upgrade every CLI that runs `run` or `verify`, then add a `test` block to a shared policy. **Rollback:** remove the `test` block from the policy first, then downgrade.

### Test producer pitfalls

- The claim is "ran and passed", not "exercised the change". A test that returns early passes. Prefer an explicit skip, which leaves the assertion open.
- A route that replaces broader evidence needs a replayed known regression that the selected tests catch. Selection completeness does not show the tests are the right ones.
- A filter that matches fewer tests can still pass if it matches at least `minimumTests` tests, or one when no minimum is set, and every selected test runs and passes. A filter that matches nothing always fails.
- Identity is the printed name, so two different tests that swap one name between the listing and the run are not detected.
- A property supplied only by a restored package's build files is not seen by the evaluation step. A package that switches the runner still fails closed at the listing; a package that supplies a run-settings file would not be detected.
- `run` builds and executes repository code with the caller's environment, and writes build output into the working tree. Do not run it beside another build of the same project. Producers run one at a time.
- `run` deletes the earlier manifest, summary and the selected `test` producers' artifact directories before it starts. After a cancelled rerun, `verify` reports a missing manifest (`ASEVD208`).
- A resource-backed profile that selects a `test` producer is rejected with `ASEVD214`. Two artifact-writing producers whose ids differ only by case are rejected with `ASEVD215`.
- `run` refuses to start with `ASEVD216` when the `artifacts` directory or a `test` producer's directory under it is a symbolic link, because retiring earlier outputs would delete through the link. It checks every such directory before it deletes anything. It does not check the `--output` directory itself or its parents. Only `test` producers' artifact directories are retired.
- The receipt slot must use `relativeRoot` `test`, and its `maximumBytes` must be at least 4096 plus, per project, 512 plus six bytes per character of the project path. The planner checks both (`ASEVD146`, `ASEVD147`) so a run never finishes its tests and then fails to write the receipt.

Profiles that select the preview test producer require a fresh CLI run (`ASEVD219`). Omit `--reuse` for these profiles.

## Scoped patch coverage

A `coverage` producer that declares a `coverageScope` block runs only the test projects it names and closes one assertion, `appsurface/coverage/patch-scoped@1`, when the changed lines under its source patterns meet its patch thresholds, counting only the coverage of the assembly each source entry declares. It never closes a repository-wide coverage assertion and never evaluates overall thresholds.

**When to use it.** Use it for a pull request that touches one package, where running every test project in the solution costs more than the evidence is worth, and keep a repository-wide gate somewhere else, such as on the default branch. Use an ordinary `coverage` producer with `coverageGate` when overall coverage is the evidence, and the [`test` producer](#test-producer-preview) when the evidence is that selected tests ran with no coverage claim.

```json
{
  "id": "config-patch-coverage",
  "kind": "coverage",
  "version": "1.0.0",
  "requiredResources": [],
  "assertionIds": ["appsurface/coverage/patch-scoped@1"],
  "artifactSlots": [
    { "logicalName": "scoped-coverage-receipt", "relativeRoot": "coverage-scope", "mediaType": "application/json", "required": true, "maximumBytes": 262144 }
  ],
  "timeoutSeconds": 900,
  "coverageScope": {
    "projects": ["Config/ForgeTrust.AppSurface.Config.Tests/ForgeTrust.AppSurface.Config.Tests.csproj"],
    "sources": [
      { "pattern": "Config/ForgeTrust.AppSurface.Config/**", "assembly": "ForgeTrust.AppSurface.Config" }
    ],
    "minPatchLinePercent": 95,
    "minPatchBranchPercent": 85,
    "patchLineMode": "codecov",
    "tolerancePercent": 0.5
  }
}
```

| Field | Meaning |
| --- | --- |
| `projects` | 1 to 32 unique repository-relative test project paths. Only these run. They are resolved against the repository root, not the current directory. |
| `sources` | 1 to 32 entries. `pattern` uses the producer `inputs` grammar: `*` inside a segment, `**` as a whole segment. `assembly` is the Cobertura `package` name, which is the compiled assembly name. |
| `minPatchLinePercent`, `minPatchBranchPercent` | At least one is required, each 0 to 100. |
| `patchLineMode`, `tolerancePercent` | As in `coverageGate`: `measurable` or `codecov`, and a grace margin subtracted from each threshold. |

The planner enforces the shape with `ASEVD150` to `ASEVD161`, listed in the [planner diagnostics](../ForgeTrust.AppSurface.Evidence.Planner/README.md#scoped-coverage-diagnostics). `coverageGate` must be absent, the profile must be `Targeted`, and the producer id must be one safe path segment.

### How a scoped run is scored

`run` requires `--solution` and `--diff-file`, runs the declared projects once into `coverage-scoped/<producer id>`, and reads the merged report by package. Each added line under a source pattern is scored only from rows of that entry's assembly:

| Line | Result |
| --- | --- |
| The declared assembly has a row for the file and line | Covered or uncovered. `patchLineMode` decides partial branches. |
| No row, and the assembly is in the report | Not applicable, for example an interface or a comment, unless another package has rows for the file. |
| The declared assembly has no row for the file, but another package does | Mismatched. The run fails with `AssemblyMismatch`. |
| No row, and the assembly is not in the report | Unknown. The run fails before scoring. |
| The path matches no source pattern | Counted as out of scope, never scored. |

The verdict is the first that applies:

| Reason | Outcome | When |
| --- | --- | --- |
| `ScopeAssemblyNotInstrumented` | `Failed` | A declared assembly is missing from the merged report, whether or not the diff touched it. |
| `AssemblyMismatch` | `Failed` | A changed in-scope file has rows only under a package other than its declared assembly, so the declared name is wrong for it. The file is listed in `unknownFiles`. |
| `NothingInScope` | `Failed` | No changed file, by new or previous path, is under a source pattern or inside a declared test project's directory. |
| `BelowThreshold` | `Failed` | A declared metric with at least one measurable point is under its threshold after tolerance. |
| `Complete` | `Passed` | Otherwise. A metric with nothing to measure is `notApplicable`, never 100%. |
| `RunFailed` | `Failed` | A declared test, the merge or the report step failed. |
| `SolutionUnavailable`, `DiffUnavailable` | `Unavailable` | `--solution` or `--diff-file` is missing. |
| `DeadlineExceeded` | `TimedOut` | `timeoutSeconds` expired. |

A tests-only change inside a declared test project passes with both metrics `notApplicable`. A change that touches the scope and other files is scored on its in-scope lines, and the receipt counts the rest.

### Scoped receipt

`artifacts/<producer id>/coverage-scope/scoped-coverage-receipt.json`, schema version 1, is written for every reason, including a timeout. It holds totals only: `reason`, `overallGateEvaluated: false`, the declared `projects`, each source with `assemblyReported` (`true`, `false`, or `null` when no report was read), the `thresholds`, the `line` and `branch` metrics (`outcome`, `reason`, `changedLines`, `measurable`, `covered`, `percent`), `inScopeFiles`, `outOfScopeFiles`, and `unknownFiles` (changed files whose assembly was missing or mismatched), up to 50 paths of at most 512 characters with an `unknownFilesTruncated` flag. Per-line detail is in `coverage-scoped/<producer id>/coverage-patch-targets.json` and `.md`, which the manifest diagnostic names. The planner derives the smallest allowed slot `maximumBytes` from the declaration (`ASEVD159`), so a run never finishes its tests and then fails to write the receipt.

`explain` lists each scoped producer's projects, sources and thresholds, and `evidence-summary.json` carries them under `scopedCoverageProducers`.

### Scoped coverage pitfalls

- A scoped pass says nothing about overall coverage or about changed files outside `sources`. Without an unconditional repository-wide gate, overall coverage can erode one scoped pull request at a time.
- A line covered only by a test project that is not declared counts as uncovered. This fails closed and usually means the project list is incomplete.
- A source file compiled into two assemblies is scored from the declared assembly only. Hits from the other assembly do not count.
- The assembly name must match the Cobertura package name. A project that overrides `AssemblyName` needs that name declared. A name that is missing from the report fails with `ScopeAssemblyNotInstrumented`; a name that is in the report but wrong for a changed file fails with `AssemblyMismatch` when another package reports that file. A wrong name is not caught for a changed file that no package reports, such as an interface.
- A file excluded from coverage by `[ExcludeFromCodeCoverage]` or a collector filter has no rows and is reported as not applicable. The repository-wide patch gate has the same gap.
- Routing is not checked by the planner. A policy rule broader than the producer's sources, such as `Config/**` for a producer that answers for `Config/ForgeTrust.AppSurface.Config/**`, lets a change elsewhere under `Config/` reach the scoped producer, which then fails with `NothingInScope` or scores only part of the change. Add a policy test that every rule selecting a scoped profile matches only paths under its sources or declared test project directories.
- The supplied patch is not checked against the checkout that was measured, as with every coverage claim today.
- If a host enforces `timeoutSeconds` before the producer's own handler runs, the receipt is missing and the manifest is `Invalid`, which is never gate-eligible.
- The run also writes the ordinary `coverage-gate.json` and `.md` with zero overall thresholds, only so the patch-targets files exist. Read the scoped receipt, not that gate result. If the merged report has no valid lines or branches at all, that gate step fails and the run reports `RunFailed`.
- `--reuse` is refused for a profile with a scoped producer (`ASEVD220`). Run it fresh.
- An older CLI drops the `coverageScope` block while reading the policy, plans the producer as an ordinary coverage producer, and reports it `Invalid` at `run` because it has no `coverageGate`. Upgrade every CLI that runs `run` or `verify` before adding a scoped producer to a shared policy.

## Verifying evidence

```bash
appsurface evidence verify TestResults/evidence/evidence-manifest.json [--plan <evidence-plan.json>] [--artifact-root <directory>] [--for-gate]
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--plan` | `evidence-plan.json` beside the manifest | The resolved plan the manifest must bind to. |
| `--artifact-root` | the `artifacts` directory beside the manifest for the preview test producer | Explicit directory holding one subdirectory per producer id. |
| `--for-gate` | off | Require a gate-eligible claim in addition to checking integrity. |

`verify` re-resolves the policy snapshot inside the plan, compares digests, and then runs the shared [artifact file check](../ForgeTrust.AppSurface.Evidence.Contracts/README.md#retained-artifact-check) for every artifact a producer result lists. For a CLI coverage run with receipt custody, the default uses the [coverage custody layout](../../guides/evidence-reuse.md#coverage-report-custody) beside the manifest. Keep the whole run directory together when moving it. A manifest that lists no artifacts never touches the artifact root.

| Code | Meaning | Next action |
| --- | --- | --- |
| `ASEVD203` | The plan has no resolvable policy snapshot, or the plan and manifest do not bind. | Use the plan and manifest from one run; do not edit them. A plan written by a different CLI version can also fail here. |
| `ASEVD208` | A plan or manifest path is empty or invalid, or the file is missing. | Check that the paths point to files from the same run. If the files are missing, rerun `appsurface evidence run`. |
| `ASEVD209` | The plan or manifest could not be read as supported JSON within the input limit. | Regenerate the input using the current supported schema. |
| `ASEVD213` | A listed artifact is missing, linked, outside the artifact root, or has a different length or SHA-256. The message names the artifact, the producer and the status. | Rerun `appsurface evidence run` and verify the plan, manifest and artifacts from that one run. |

By default, `verify` checks integrity and exits 0 for an intact manifest whose claim is `None`.
Use `--for-gate` to also require a gate-eligible claim.

Before using the manifest as gate evidence, establish its provenance through the consumer's workflow and
apply that workflow's claim and eligibility requirements. Read `ClaimKind` and `Eligibility` from the
[manifest JSON](../ForgeTrust.AppSurface.Evidence.Contracts/README.md) in automation.

## Pitfalls

- Do not run a repository-wide coverage gate after intentionally filtering out required tests and expect a complete claim. A filtered suite cannot close a repository-wide coverage assertion. It can close the `test` assertion of a producer whose filter is declared in the policy, or the [scoped patch coverage](#scoped-patch-coverage) assertion of a producer whose projects and sources are declared in the policy, both bound into the plan.
- Treat `doctor` exit code 0 and `ready_with_external_prerequisites` as [preflight results](#doctor-preflight).
  Gate releases on a `ReleaseComplete` claim from the [protected workflow with its registered verifier](../../guides/evidencehost-cookbook.md#release-evidence).
- Do not set `--observation-only` on a job that must satisfy a PR or release gate.
- Do not hand-edit generated artifacts; use `verify`.

Read next: the [CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md), [contracts](../ForgeTrust.AppSurface.Evidence.Contracts/README.md), and the [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md).
