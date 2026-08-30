<!-- appsurface:unreleased-entry section="included" -->
### Coverage evidence execution

- [`appsurface coverage run` and `appsurface coverage gate`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) now share one private execution engine with the built-in [`evidence run` coverage producer](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md). Coverage policies therefore evaluate the same collection, merge, gate, patch-target, artifact, and watchdog behavior regardless of which supported workflow invokes it; the private support package is not a new consumer dependency or installation choice.
- When an evidence policy supplies `--diff-file`, planning captures a bounded immutable snapshot before execution starts. Replacing that file later in the run cannot change the patch coverage input that the resolved policy evaluates.
