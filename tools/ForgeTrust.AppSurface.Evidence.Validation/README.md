# Native Evidence validation procedure

**Status:** the executable runs a portable validation procedure. Its output is not protected Evidence, native acceptance, or gate input. The [native workflow](../../.github/workflows/native-evidence-validation.yml) labels and checks it accordingly. The [migration design](../../docs/designs/native-evidence-validation.md) records which procedure assertions exist and which protected acceptance work remains open. This follows the current [EvidenceHost support contract](../../docs/evidence/evidencehost-migration.md): public legacy host entry points fail closed, and this tool has no registered protected producer path.

Use this tool to run local package, storage, installed-consumer, and verified fan-in procedure checks against a supplied candidate CLI package. It can report that those explicit checks passed. It cannot create an EvidenceHost or manifest, assert native acceptance, or make a gate-eligible claim.

## Operation selection

`--operation` is required. Its value selects a deliberately narrow execution boundary:

| Operation | Result | When to use it |
| --- | --- | --- |
| `procedure` | Runs the portable package/storage/consumer/fan-in checks and writes a procedure plan plus procedure report. It creates no EvidenceHost and no Evidence manifest. | Local development and the cross-platform procedure workflow. Exit zero means only that the declared local procedure and cleanup passed. |
| `protected` | Rejected with `ASEVD404` before input or output I/O because no protected native operation is registered. | Do not select until a separately reviewed implementation registers the actual protected operation. |
| Missing or unsupported value | Rejected with `ASEVD401` before input or output I/O. | Supply `--operation procedure` for the supported local workflow. |

This option is mandatory for execution and has no implicit default. An empty invocation prints usage and returns exit 2; `--help` prints usage and returns exit 0. Neither reads inputs or starts a procedure. Other invocations without an operation return `ASEVD401`. A matching package, run identity, verifier result, or successful procedure report cannot grant admission.

## Run the portable procedure

Pack a candidate CLI package with its source commit in NuGet repository metadata. Choose an absolute .NET host path and a fresh output directory:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.Evidence.Validation --configuration Release -- \
  --operation procedure \
  --package /absolute/feed/ForgeTrust.AppSurface.Cli.VERSION.nupkg \
  --version VERSION --source-commit COMMIT \
  --dotnet /absolute/dotnet --output /absolute/fresh-procedure \
  --scratch-parent /absolute/existing-scratch-parent
```

On Windows, provide absolute Windows paths. Arguments are passed directly to child processes; the tool does not interpolate a shell command. `--scratch-parent` must already exist. `--output` must be fresh, outside scratch, and remains caller-owned. Each procedure owns a fresh random scratch child and removes only its own child after checking its ownership and rejecting links. Use trusted local workspaces: path checks and capacity sampling are not an adversarial filesystem sandbox or an operating-system disk quota.

The packed-storage procedure checks package identity, source metadata, read-only status, first and repeated merge behavior, invalid input, maintenance preview, pin/restore, overwrite refusal, unpin, and explicit expiry. It compares published original-file inventories and restored bytes independently. Regenerated metadata is evaluated under its documented contract rather than treated as an archived original file.

The installed-consumer procedure installs the supplied package in an isolated tool directory and cache, checks installed bytes, then runs the checked-in two-project consumer in Release. It requires the declared projects and test fragments to pass, and checks report readers, source/settings integrity, and the existing 95% line and 85% branch gates with zero tolerance. This fixture validates the repeatable recipe; it does not execute an arbitrary external application's tests.

An external consumer profile is supplied with `--consumer-profile /absolute/profile.json`:

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

The original source repository is read for identity and integrity, then cloned into scratch; the original stays caller-owned. External profiles require a nonblank `expectedCommit`, and source HEAD must match it before cloning. A project list declares the scope of local checks and must enumerate every intended project for that profile. Without one, the solution determines projects. `.sln` and `.slnx` inputs must resolve to at least one test project. Required fragments must appear in passing, non-skipped JUnit cases. Skips fail by default; an explicit matching `permittedSkipPatterns` entry with a nonempty rationale permits only that declared skip and keeps it in scope. With `diffBase`, the consumer also requires 95/85 patch coverage with zero tolerance. Do not put secrets in profiles or durable procedure output.

## Plans and reports

A settled `procedure` operation writes these files into the fresh output directory, including when selected checks fail:

- `procedure-plan.json` records the portable procedure's declared local work.
- `native-validation-procedure.json` is a bounded report using schema `appsurface-native-validation-procedure-v1`. Its JSON property names use camel case. It reports `procedurePassed`, `cleanupCompleted`, and `producerResults` for the procedure checks. `protectedAdmission`, `nativeAcceptance`, and `gateEligible` are always `false`.

`planDigest` links the report to the canonical procedure plan for local diagnosis. Each `producerResults`
entry records `producerId`, numeric `outcome` (`0` for passed, `1` for failed), `satisfiedAssertionIds`,
`diagnostic`, `artifacts`, and `elapsedMilliseconds`. Failed results retain no satisfied assertions.
Artifact metadata carries the logical slot, producer-relative path, media type, byte length, and lower-case
SHA-256 digest; bytes are rechecked after cleanup. The shared [artifact contract](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md)
defines validation of this metadata. None of these data bindings grants admission.

The storage transcript retains the command runner's fixed failure code and secret-safe diagnostic when
a command throws before returning an exit result. Procedure and cleanup exceptions report only an
allowlisted category and numeric HRESULT, without raw exception messages, paths, or supplied type names.
If cleanup also fails, its diagnostic retains the prior procedure failure within the same 1,024-character
limit. Both failures still prevent success. These fields support diagnosis; they grant no cleanup waiver.

The report is structurally bounded by the closed procedure catalogue: at most three producer results (`packed-storage`, `installed-consumer`, and optional `native-fan-in`), at most four declared artifact metadata records, and diagnostics capped at 1,024 characters per producer. The tool does not accept caller-selected producer declarations. It is not an Evidence manifest and is not accepted by `appsurface evidence verify` as one. Procedure mode must not write `evidence-manifest.json`. A successful process exit and `procedurePassed: true` mean the declared local checks and cleanup passed; they do not mean protected admission, native qualification, a consumer proof, or a gate passed. Keep the procedure plan/report in the CI artifact for diagnostics, not as gate evidence.

The CI workflow uses read-only repository permissions and runs storage and consumer as separate, parallel matrix entries on each of Linux, Windows, and macOS. Each entry checks these exact files and flags after its procedure command and fails if an Evidence manifest appears. The original 25-minute outer job deadline applies to each entry; it may abort a long consumer run, which then cannot pass. Producer deadlines are unchanged. The unchanged lifecycle, tamper, and fan-in contract tests run separately. A passing workflow check is not native accepted evidence.

## Bounds and unchanged policy

`--maximum-scratch-bytes` defaults to 2 GiB per producer and `--minimum-free-bytes` defaults to 1 GiB. Workspace capacity is checked before commands and while work runs. Measurements are sampled; a child can allocate between checks. Entry and depth limits also bound inspection. These checks stop local work that exceeds its declared budget; they do not authorize changing a production limit or claim.

Candidate JSON inputs remain bounded: 1 MiB for the consumer's `coverage-gate.json` and 16 MiB for the storage run's `.appsurface-coverage-run.json`. Missing, empty, linked, oversized, or invalid JSON fails the procedure before it can be accepted. JUnit reads exactly one top-level `junit-*.xml` per declared project, capped at 16 MiB per report, 64 MiB total, and 100,000 testcases. DTDs are prohibited. Testcase summaries omit output bodies and parent-document references. These limits do not trim evidence to manufacture success or waive source scope and coverage gates.

The existing full-solution and patch gate policy remains 95% line and 85% branch. Verified coverage fan-in retains the established source, SDK, configuration, project-inventory, capture, report, and run binding. Its full-solution aggregate and patch gates, repository tolerance, Codecov patch mode, and measurable denominators remain unchanged. See the [coverage fan-in workflow](../../guides/coverage-fan-in.md). These checks are portable procedure assertions until a protected operation is implemented and independently accepted.

For resource-backed applications, compose explicit readiness checks through the [Aspire Evidence package](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md); this procedure does not infer or provision databases. Preserve the separate [protected EvidenceHost migration requirements](../../docs/evidence/evidencehost-migration.md) and the [Issue 779 cutover record](../../docs/evidence/issue779-csharp-runtime-cutover.md#local-integration-with-current-main--2026-10-27). Neither a local pass nor this workflow replaces that work. The protected controls in the [runtime proof workflow](../../.github/workflows/evidencehost-runtime-proof.yml), [protected qualification workflow](../../.github/workflows/evidencehost-protected-qualification.yml), and [protected parent workflow](../../.github/workflows/evidencehost-protected-parent.yml) remain separate and unchanged by the portable procedure.

## Cancellation and publication

The tool joins each original procedure and disposes its source-owned producers before verifying artifact bytes
and returning a report. Each operation keeps its declared cancellation deadline: 600 seconds for storage,
1,800 seconds for the consumer, and 300 seconds for optional fan-in. Those tokens are cooperative for
in-process code; the command runner retains its independent process termination and output bounds.
The tool never publishes a passing result while an original procedure or cleanup task remains unjoined.
Caller cancellation returns exit 130 after joined cleanup and publishes no report. Cancellation after output
creation can leave partial artifacts for diagnosis; their presence is neither a completed procedure nor an
Evidence claim. The final plan/report are written only after the procedure returns and artifacts are checked.
