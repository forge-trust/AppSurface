<!-- appsurface:unreleased-entry section="included" -->
### Scope patch coverage to one package's tests

A built-in `coverage` producer can declare a [`coverageScope`](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#scoped-patch-coverage) block: the test projects to run and the source patterns, each with its assembly, that those tests answer for. It closes `appsurface/coverage/patch-scoped@1` when the changed lines under those sources meet the declared patch thresholds, counting only the declared assemblies' coverage. It fails closed when a declared assembly is missing from the report or when nothing in the change is in scope, and it writes a totals-only receipt for every outcome.

A scoped claim never evaluates overall thresholds and never closes a repository-wide coverage assertion, so keep a repository-wide gate on an unconditional route. Policies without the block keep their exact digests. An older CLI reports a scoped producer `Invalid` at `run`; upgrade every CLI that runs or verifies evidence first. `--reuse` is refused for scoped producers (`ASEVD220`).
