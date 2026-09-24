# Issue #815: VSTest hang diagnostics and migration

This change fixes literal `--test-argument` forwarding and adds AppSurface-owned VSTest hang diagnostics to `coverage run` and the first-party Evidence coverage producer. It affects the [AppSurface CLI](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) and its private [Evidence coverage engine](../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/README.md). It applies to the supported VSTest collector and explicit MSBuild drivers; Microsoft Testing Platform is outside this behavior.

## Behavior change

The CLI `--watchdog` default changes from `warn` to `fail`. A no-progress watchdog win cancels the process tree and exits `124` with `ASCOV121`. Evidence uses the same fail policy, while its producer deadline remains authoritative and still maps to `TimedOut` without assertions.

With fail mode, an invocation with at least 90 seconds remaining in its test-phase budget receives AppSurface's no-dump VSTest tuple: `--blame-hang`, `--blame-hang-timeout <duration>`, and `--blame-hang-dump-type none`. AppSurface reserves `min(60 seconds, one third of the budget)`, rounds down to whole seconds, and uses the remainder for VSTest. A 180-second budget gives a 120-second VSTest timeout; the CLI's default 10-minute no-progress timeout gives 9 minutes. If an Evidence producer starts its test phase with 150 seconds remaining after 30 seconds of discovery/build under a 180-second deadline, the VSTest timeout is 100 seconds. Under 90 seconds, automatic blame is skipped, but the watchdog/deadline still applies. The timer applies only to test execution, not discovery, build, merge, or gate.

VSTest sequence output records tests that started; it does not prove which test hung. VSTest host termination remains a test failure (usually `ASCOV120`). An AppSurface watchdog win remains `ASCOV121`/exit `124`. A producer deadline remains Evidence `TimedOut`. A missing or invalid `Sequence.xml` is secondary diagnostic information and does not replace the actor's outcome.

## Migration choices

| Situation | Configuration | Effect |
| --- | --- | --- |
| Normal CI stall detection | Omit `--watchdog` or use `--watchdog fail` | Fail after the existing 10-minute no-progress default; automatically request no-dump blame when at least 90 seconds remain. |
| A known healthy quiet test can exceed the computed VSTest timeout | For `coverage run`, increase `--no-progress-timeout`; for Evidence, increase `TimeoutSeconds` only while the remaining producer deadline limits the timer | The CLI timeout extends its watchdog budget and derived per-test VSTest timeout. Evidence's remaining deadline includes discovery/build time, but its fixed 10-minute no-progress watchdog caps the automatic VSTest timeout at 9 minutes. Evidence has no watchdog or test-argument opt-out; use `coverage run` for those controls. |
| Preserve the run after a quiet stall, or disable automatic hang blame | `--watchdog warn` | Records a warning and continues; no automatic blame tuple is added. |
| Disable stall classification and automatic hang blame | `--watchdog off` | No watchdog failure and no automatic blame; explicitly configured heartbeats can still be emitted. |
| Caller already configures blame | Pass any `--blame` or `--blame-*` token through `--test-argument` | Manual tokens take precedence as a group and pass through unchanged; AppSurface adds no tuple. |
| MSBuild caller configures blame in runsettings | Pass `--settings <file>` or `-s <file>` | The settings file claims the group; AppSurface cannot inspect it, and the caller owns timeout, dumps, and disk impact. |

Manual blame precedence applies even when the supplied group is incomplete. In collector mode the invocation-owned results remain available for bounded inspection. In MSBuild mode, manual blame or a settings file makes the result directory caller-owned, so AppSurface reports the diagnostic as `unscoped` and does not claim that a sequence file is missing. In fail mode without a manual override, MSBuild reserves AppSurface `--results-directory` ownership; conflicting caller tokens are rejected before discovery (`ASCOV101`).

The new `--test-argument` contract treats every occurrence as one literal token. Both split `--test-argument VALUE` and joined `--test-argument=VALUE` forms work. Split form always consumes exactly the following token, even when it begins with `-`; an unattached `--help` remains command help. For example, the original five tokens from #815 work in this complete command:

```bash
dotnet tool run appsurface coverage run \
  --test-project tests/MyApp.Tests/MyApp.Tests.csproj \
  --test-argument --blame-hang \
  --test-argument --blame-hang-timeout \
  --test-argument 120s \
  --test-argument --blame-hang-dump-type \
  --test-argument none
```

They are forwarded in the same order as `--blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none`.

## Artifacts and compatibility

`timings.json` retains its existing top-level schema and adds an optional per-project `hangDiagnostics` object with its own `schemaVersion: 1`, source (`automatic`, `manual`, or `none`), status, scoped relative sequence paths, safe last-started test names where available, and effective VSTest timeout. Consumers that do not understand an optional object's schema version should ignore that object and continue reading base timing and scheduling fields. Statuses distinguish found/missing output, disabled or not-started runs, short budgets, manual MSBuild unscoped output, and invalid or limited inspection (including malformed, oversized, escaping, duplicate, unreadable, inspection-limited, and unavailable). Test names are untrusted local metadata: output uses only conservative identifiers and labels them “last started test.” Evidence results never contain names or XML contents.

See the [CLI hang-diagnostics reference](../Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics) for the full timing relationship, invocation behavior, artifact contract, and status interpretation. The [root coverage guide](../README.md) links the package-consumer flow and this migration note.

## Validation context

The repository's local [slow-test diagnostics](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) from 2026-09-11 reported complete metadata for 12,031 test cases across 52 projects. Its longest recorded individual test was 32.94 seconds, below both the 120-second VSTest timer derived from a 180-second launch budget and the 9-minute timer derived from the default CLI budget. That historical sample does not establish a bound for another consumer's test suite.

The controlled [VSTest fixture](https://github.com/forge-trust/AppSurface/blob/main/tests/fixtures/coverage-hang/HangTests.cs) and [verification script](https://github.com/forge-trust/AppSurface/blob/main/scripts/verify-coverage-hang-fixture.sh) exercise a never-completing test, a healthy 130-second test under the 120-second automatic timer, and that same healthy test with `--watchdog off`, for both drivers. The real Skoolit checkout and its precise discovery/build spend and longest healthy test were unavailable in this workspace, so Skoolit-specific validation remains pending.

The fixture passed all six arms on 2026-09-23 with a 180-second outer budget. Collector hang and MSBuild hang exited in 127 and 126 seconds, respectively, with `ASCOV120`, scoped no-dump sequence files, and the safe last-started observation `CoverageHang.Tests.HangTests.NeverCompletes`. The healthy 130-second test exited in 127 seconds for collector and 124 seconds for MSBuild under the computed 120-second VSTest timer; both remained `ASCOV120` and identified `HealthyLongRunningTest` as last started. With `--watchdog off`, that same healthy test completed in 138 seconds for collector and 134 seconds for MSBuild with merged coverage. These measurements show the intended reserve and the compatibility boundary in the controlled fixture; they do not predict Skoolit's unmeasured workload.
