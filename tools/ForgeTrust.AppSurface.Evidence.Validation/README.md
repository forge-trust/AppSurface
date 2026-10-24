# Native EvidenceHost acceptance

This repository tool uses the public [EvidenceHost lifecycle](../../start-here/evidencehost.md) to validate AppSurface itself. It runs explicit producers against a supplied CLI package, checks published behavior independently, and emits a canonical plan and manifest. Use it when changing [coverage storage](../../guides/coverage-storage.md), package installation, or the native Evidence execution facilities.

## Run the acceptance journeys

Build a candidate CLI package with its real source commit in NuGet repository metadata. Select an absolute .NET SDK host path. Invoke the tool from the checkout:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- \
  --package /absolute/feed/ForgeTrust.AppSurface.Cli.VERSION.nupkg \
  --version VERSION --source-commit COMMIT \
  --dotnet /absolute/dotnet --output /absolute/fresh-evidence
```

On Windows, supply absolute Windows paths to the same options. The tool passes arguments directly to processes and does not interpolate a shell command. `--help` lists all options. `--mode storage` selects the packed storage journey, `--mode consumer` selects the installed consumer, and the default `all` requires both to pass.

The storage producer extracts the package into an owned workspace and verifies its package ID, exact version and source metadata. It then runs read-only status, first and repeated merge, invalid input, maintenance preview, pin, restore, overwrite refusal, unpin, and explicit expiry. Hashes of the published original-file inventory must match restored bytes; current and restored outputs must survive maintenance. Regenerated metadata is checked under its documented contract rather than treated as an original archived file.

The consumer producer installs the supplied package in an isolated tool directory and cache, verifies installed bytes, and collects fresh Release coverage and JUnit results from the checked-in two-project consumer. Every required project and test fragment must execute successfully. Overall coverage must pass 95% line and 85% branch with zero tolerance. Report, manifest, log and timing readers must still work, and source/settings integrity must remain unchanged. This fixture proves the repeatable consumer recipe; it does not claim to have executed an arbitrary external application's tests.

## External consumer profile

Pass `--consumer-profile /absolute/profile.json` to run a consumer you own. A profile supplies these fields:

```json
{
  "sourceDirectory": "/absolute/consumer-repository",
  "expectedCommit": "PINNED_COMMIT",
  "solutionPath": "Consumer.slnx",
  "testProjects": [],
  "expectedRequiredTestFragments": ["CriticalBehaviorTests"],
  "permittedSkipPatterns": {
    "KnownPlatformOnlyTest": "Requires unavailable native platform capability on this runner"
  },
  "diffBase": "PINNED_DIFF_BASE",
  "environment": {}
}
```

The source repository is read for identity/integrity and cloned into scratch. The original remains caller-owned. An explicit project list must contain the complete intended acceptance inventory; otherwise the solution determines it. `testProjects` is a scoped acceptance declaration, so it must list every intended test project for that profile; it does not silently claim full repository coverage. `.sln` and `.slnx` inputs are parsed before execution and must resolve to at least one test project. When `diffBase` is supplied, the consumer also requires 95/85 patch coverage with zero tolerance. Provide required test fragments so a successful exit cannot hide a missing critical suite; required fragments must be present in non-skipped, passing JUnit cases. Skips are rejected by default. External profiles may explicitly allow a skip only when `permittedSkipPatterns` contains a matching testcase attribute fragment and a nonempty rationale; those skips remain part of the declared profile scope. The checked-in fixture has no permitted skips. JUnit acceptance reads exactly one top-level `junit-*.xml` report per declared project, capped at 16 MiB per report, 64 MiB total and 100,000 testcases. Oversized evidence fails acceptance; it never silently drops tests. DTDs are prohibited, and retained testcase summaries omit output bodies and parent-document references to bound memory. For larger applications, compose separate Evidence obligations that together retain the full intended test inventory. These report bounds never waive the repository coverage gate or allow a missing required suite to pass. Resource-backed applications should compose explicit readiness probes through the [Aspire Evidence package](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md); this tool does not infer or provision their databases. Keep secrets out of profile files and durable evidence.

## Storage and lifetime

`--maximum-scratch-bytes` defaults to 2 GiB per producer and `--minimum-free-bytes` to 1 GiB. The native [workspace budget](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#owned-workspaces-and-command-budgets) checks before commands and during execution. Measurements are sampled, so a child can allocate between checks; this is not an operating-system disk quota. A budget failure must stop the command and prevent a complete claim. Entry/depth limits also bound filesystem inspection.

`--scratch-parent` must already exist and defaults to the platform temporary directory. Each producer owns a fresh random child. Cleanup removes only that child after checking its ownership and rejecting links. Durable `--output` must be fresh and outside scratch, and remains caller-owned. If cleanup cannot complete, EvidenceHost records an incomplete result. Use trusted local workspaces; path checks do not provide an adversarial filesystem sandbox.

## Evidence and claims

The output contains `evidence-plan.json`, `evidence-manifest.json` and bounded declared artifacts beneath `artifacts/PRODUCER/`. The manifest includes artifact hashes, producer assertions, lifecycle timing and cleanup status. Verify it through [the native contracts](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md) or `appsurface evidence verify`. Verification detects internal inconsistency; obtain artifacts and trusted inputs through an appropriate CI channel because hashes are not independent attestation.

Only `TargetedComplete` returns exit zero. Missing prerequisites, failed assertions, incomplete output capture, timeouts and failed cleanup prevent success. Cancellation returns a nonzero exit. Each journey retains its scope: passing the fixture is not a replacement for the full solution or an external consumer's required gates.

## Verified native coverage fan-in

`--fan-in-request /absolute/trusted-request.json` adds a required producer using [native fan-in verification](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#verified-coverage-fan-in). Supply the trusted expected source commit/tree, SDK, Release/Debug configuration, workflow run and attempt, full solution project inventory, primary capture, exactly three native captures, composed reports and gate JSON. The request is trusted configuration, not an automatically authenticated attestation.

The verifier checks every input hash and recomputes composition. The independently required full-solution 95/85 aggregate gate and 95/85 patch gate retain the repository's 0.5-point tolerance, Codecov patch mode and nonempty, nondecreasing measurable patch denominators. Native-only points cannot be discarded. Restored or historical reports cannot be relabeled as current evidence. The existing [coverage fan-in collection workflow](../../guides/coverage-fan-in.md) remains the producer of these inputs.
