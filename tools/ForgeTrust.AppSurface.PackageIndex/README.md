# ForgeTrust.AppSurface.PackageIndex maintainer guide

`ForgeTrust.AppSurface.PackageIndex` owns the curated package chooser, readiness dashboard, package-gate policy, and
the generated `## Release Guidance` region in public package READMEs. Start with the generated
[package chooser](../../packages/README.md) when deciding which package a consumer should install; use this guide when
you are changing the repository's package-story policy rather than one package's authored technical documentation.

## Release guidance

Every managed package README declares one `release_guidance_variant` in
[`packages/package-index.yml`](https://github.com/forge-trust/AppSurface/blob/main/packages/package-index.yml). The value is a finite reader-facing policy choice:

| Variant | Use when | Do not use when |
| --- | --- | --- |
| `default` | The package follows the ordinary coordinated prerelease story. | A package needs an AppHost-only or publication-held statement. |
| `apphost` | The package is primarily an AppHost, development, or test integration surface. | A runtime package merely happens to have an Aspire example. |
| `experimental` | The package has an explicitly experimental or publication-held contract. | A package needs extra product-specific release prose; keep that prose authored outside the region. |

The canonical bodies live in the generator-only
[`release-guidance.template`](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template).
The non-Markdown extension keeps its required unexpanded URL tokens out of the published Docs graph. Each body expands
the package chooser and release-hub links to canonical absolute GitHub URLs. This is deliberate: the package root
`README.md` is included in a NuGet package, but repository-relative targets are not package contents. The package
artifact validator confirms that the marked region and both URLs survive packing.

Do not add a fourth variant for package-specific instructions. Keep those instructions outside the managed marker pair:

```markdown
<!-- appsurface-release-guidance: begin -->
## Release Guidance
<!-- generated content -->
<!-- appsurface-release-guidance: end -->

## Package-specific operations
<!-- authored content -->
```

The renderer changes only the bytes inside the marker pair. It rejects missing, duplicate, reversed, unknown, or
unexpanded markers and tokens rather than guessing, and rejects README paths that cross symbolic links or other
reparse points before it reads or replaces them. For legacy README sections with one `## Release Guidance` heading, the
first `generate` migration inserts the pair before the next H2 or Markdown horizontal rule so trailing footer navigation
remains authored content; after that, retain the markers exactly.

## Change workflow

1. Edit the finite variant or a manifest field in [`packages/package-index.yml`](https://github.com/forge-trust/AppSurface/blob/main/packages/package-index.yml).
2. Keep package-specific adoption, operational, and proof content outside the generated region.
3. Reconcile the checked-in outputs:

   ```bash
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate
   ```

   `generate` states its changed and managed README counts. It validates every target before replacement, stages
   same-directory temporary files, and rolls back ordinary replacement failures. Inspect and commit the resulting
   README, chooser, and readiness diffs.

4. Verify without writing:

   ```bash
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- gate
   ```

   `verify` compares all generated documents and managed README regions; `gate` validates manifest, template, and
   marker policy without writing files. [Package-gate CI](https://github.com/forge-trust/AppSurface/blob/main/.github/workflows/package-gate.yml) runs both commands.

5. When a change affects package payloads or published documentation, run the existing package artifact proof:

   ```bash
   dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify-packages --package-version 0.0.0-ci.local
   ```

   It verifies that the packed `README.md` has exactly one managed marker pair and exactly one canonical chooser and
   release-hub URL inside that region.

   The [Coverage CLI consumer proof](../../Cli/ForgeTrust.AppSurface.Cli/README.md#agent-actionable-patch-targets)
   also passes its consumer fixture directory as `coverage gate --repository-root` when comparing synthetic diff
   paths with Cobertura paths. A fixture nested inside this checkout must use that explicit root: the CLI's
   automatic Git-root detection otherwise selects the outer checkout and cannot match consumer-relative paths.

## Tailwind artifact provenance (#798)

The approved [#798 design](../../docs/designs/issue-798-tailwind-artifact-provenance.md) and [implementation plan](../../docs/plans/issue-798-tailwind-artifact-provenance.md) define a release proof binding native Tailwind consumer tests to the exact package bundle passed to NuGet. See the canonical [API and operator reference](../../docs/tailwind-artifact-provenance.md) for the implemented CLI flags, evidence schemas, and release workflow.

Authority flows through four stages: the validated tag checkout and package plan establish source and allowed identities; `pack-and-verify` creates one immutable bundle with the package manifest and producer subject; each native job proves it restored and built from that bundle; then aggregation and publication validate the exact host set and candidate again. Protected workflow outputs establish artifact transport identity. Receipt contents cannot select their own authority. The aggregate has its own immutable ID and JSON digest, separate from the producer bundle's ID and subject digest.

The native consumer starts with the [committed consumer lock](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.PackageIndex/tailwind-native-consumer.lock.json): its third-party graph is fixed, while the exact Core and Tailwind version and archive hashes come from the producer subject. Both restores run in locked mode. See the [operator reference](../../docs/tailwind-artifact-provenance.md#consumer-local-and-release-modes) for the update procedure and failure behavior.

The required hosts are exactly `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64`. Native v2 receipts (`appsurface-tailwind-native-host-proof-v2`) are eligible; v1 is historical diagnostic data only. Each success binds producer ID/run/attempt, subject and manifest digests, source/version, current native invocation, expected and observed host RID/OS/process architecture, and per-first-party producer/restored raw SHA-512 and protected payload evidence. The closure includes Core and Tailwind and comes from the producer's successful `net10.0` consumer graph plus validated package inventory. It also records Tailwind release-manifest and selected host CLI digests, generated CSS, absence of companion dependency, and absence of native consumer output. Full modes, flags, report shapes, limits, recipes and recovery are in the [reference](../../docs/tailwind-artifact-provenance.md).

## Durable preflight artifact proof (#845)

The [schema-11 operations guide](../../Durable/heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist)
defines the candidate, published and deployment gates for complete runtime-set preflight. The release carriers use
[`verify-preflight-artifacts.sh`](https://github.com/forge-trust/AppSurface/blob/main/Durable/verify-preflight-artifacts.sh) and its
[disposable package consumer](../../Durable/consumers/PostgreSqlPreflightConsumer/Program.cs) for one pair, enrollment
of a second pair, identical reconciliation and a modeled two-pair schema-10 upgrade. The consumer installs the exact
packed CLI and restores the matching PostgreSQL provider into isolated roots; it does not repack the product.
The pre-upgrade schema-10 fixture proves SQL role capabilities: durable Work completion, Flow dispatch discovery,
and a Schedule dispatch claim, plus Source dispatcher denials. It retires its unique probes through the restricted
runtime and verifies their persisted state before upgrading. Actual registered provider Work, Flow and Schedule
execution runs after migration and reconciliation. This modeled baseline does not claim historical binary
compatibility. Guard-loss negatives use a separate disposable database with the same package bytes, roles and store
identity, retaining canceled lane facts until the fixture container is disposed.
The consumer also tests an observer failure while a real provider Work operation waits on a table lock in another
disposable database. The controller must cancel and await its lane child and guard monitor before releasing that
lock, verify the owned sessions and fence are gone, and withhold the receipt. Failure or timeout during this cleanup
blocks the candidate proof. The same checked cleanup covers observation and backend-termination errors in the
guard-loss probes; an elapsed wait alone never counts as a drained child.
An independent fixture repeats the observer-failure check with a throwing cancellation callback. Nested cleanup
must still drain both owned tasks and release their sessions before propagating that callback failure.
An actual verified activation-drain failure follows the same cleanup path: it drains the guard monitor, releases the
guard, and finishes and disposes the queued writer, while withholding the receipt.

Candidate proof runs for each bundle in both release channels before the artifact manifest can authorize publication.
A failed command or missing, incomplete, mismatched or stale receipt leaves that gate closed. The published smoke
carrier then compares restored package contents and extracted role-recipe bytes with the candidate and runs the same proof.
An earlier candidate result cannot stand in for the published proof. Source tests and tool help remain separate checks.

The carrier binds `sourceCommit`, `runId`, and the immutable producer `artifactId` to the receipt and checks every
first-party archive's nuspec package id, package version, canonical repository URL, and repository commit before the
consumer runs. It rechecks the candidate bundle after the run. The public smoke restore explicitly requests every
`publish` and `support_publish` manifest entry, including support packages unreachable from public dependency roots.
Each restored archive must have the same ZIP entry names and uncompressed contents as its candidate, except the
optional root `.signature.p7s` envelope added or updated by [NuGet repository signing](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference).
Duplicate entry names, changed package metadata, added/missing payloads, or a changed extracted PostgreSQL role recipe
close the gate. ZIP compression, entry order and timestamps are not payload identity.

The shared consumer runs from copies of the approved candidate archives, whose original manifest SHA-512 values are
checked before and after execution. Passing signed public archives to that candidate manifest would invalidate its
hash binding. The version-2 `.carrier.json` uses `ProofKind: issue845-public-feed-package-content-identity` and records
`CandidatePackageSha256` and `PublicPackageSha256` maps for every manifest package, plus the candidate receipt and
shared proof receipt hashes. These raw archive hashes may differ after signing. The carrier proves payload identity;
NuGet restore owns signature validation, and the carrier receipt makes no independent signature-authenticity claim.
Retain both receipts and the original manifest; a whole-archive public hash must never replace a candidate manifest hash.

Each canonical CLI result row carries a `Scenario` identity. The one-pair scenario proves forwarder Work, Flow, and
Schedule before and after; the installed `work_only` pair scenarios also prove Work succeeds while Flow, Schedule,
and All are denied. Each before/after lane record binds its scenario, store, epoch, guard backend, and runtime-role
results. The queued-writer receipt uses the additive `outcome` values `completed-before-writer` or
`bounded-failure-then-rerun`, with `authoritativeScenario` bound to the fourth complete CLI evidence set
(`queued-writer-prewriter-complete` or `queued-writer-postwriter-rerun`). The first branch records that the complete
window finished before the writer acquired its lock; the recovery branch records a bounded failure, writer release
and completion, then the complete post-writer window. This discriminator is an additive receipt-contract correction
for the writer outcome union; the producer and shared contract must emit the same fields before release receipts can
pass.

The internal artifact-proof request supplies the repository root, frozen artifact directory, caller-owned manifest
path, exact source commit, carrier run identifier and artifact identifier. A successful candidate proof moves the
manifest to the approved path; on failure, the carrier preserves it at the caller-supplied path, rolling back a
partial promotion when that path remains available. Candidate and published operations propagate cancellation to
bounded child commands, validate complete proof receipts and retain package SHA-256 values alongside the artifact
manifest's SHA-512 binding. Proof receipts contain synthetic role/store/epoch identity and stage timings; connection
credentials stay in child-process environment variables. Consult the generated `--help` for the explicit carrier
arguments, and retain the candidate receipt with the original bundle for the public smoke invocation.

The internal `Program.RunAsync` command boundary accepts an optional `preflightCommandRunner` for verification of
candidate and published command dispatch. Omitting it uses `CliWrapCommandRunner` to launch the real
bounded consumer process. The override changes only that external-command boundary: argument parsing, archive and
receipt validation, checked scratch cleanup, and manifest promotion still run through the production carrier.
Promotion validates retained evidence without launching a consumer. This is an internal test seam, not a CLI option;
injected fixture results do not replace the [exact-package proof](../../Durable/heartbeat-retention-operations.md#complete-runtime-set-preflight-and-proof-checklist).

The fixture owner guard spans distinct runtime invocations, lane checks and a cancellable fixture activation callback.
A released receipt records a completed disposable proof window; it cannot authorize a later deployment. An actual
application activation requires independently reviewed inputs, named owners and its own continuous session-affine
coordinator, as described in the canonical operations guide. Keep the existing release job deadlines: this proof does
not authorize increasing them or skipping a scenario after a timeout.

For production `smoke-install`, pass `--artifacts-input` for the downloaded frozen bundle, `--artifact-manifest`
for its promoted manifest, `--preflight-source-commit`, `--preflight-run-id`, `--preflight-artifact-id`, and
`--preflight-candidate-receipt`. The bundle directory must contain the receipt-bound candidate archives; the
manifest path alone does not select that directory. The command validates the retained candidate receipt before public-feed restore
evidence can authorize the shared proof. Its published receipt and `.carrier.json` record are written under the smoke
work directory and should be retained with the smoke report.

## Python parser candidate gate

The `inspect-python-parser-candidate` command is a bounded, static dependency-selection proof. It accepts one local
`.nupkg`, enumerates its native runtime assets, NuGet metadata, and license/notice paths, then writes JSON containing the
archive hash, compressed size, RID inventory, and rejection reasons. It never adds, restores, builds, loads, or executes
candidate package content. A disposable local feed or child process would not sandbox untrusted NuGet build assets,
managed assemblies, analyzers, or native libraries.

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  inspect-python-parser-candidate \
  --python-parser-package /tmp/candidate.nupkg \
  --python-parser-proof-report artifacts/python-parser-proof.json
```

An exit code of `0` means the inspection completed and the report was written; it does **not** mean the candidate was
accepted. Reports must be written below the repository's `artifacts/` directory. Read `rejectionReasons` and
`isEligibleForFurtherReview` from the JSON before taking any dependency action. Archive size is recorded as decision
evidence rather than a hard product budget; the command's separate 64 MiB archive-read limit protects the inspection
process itself. Report destinations are immutable: choose a new filename for every run, because existing files and
symbolic-link paths are rejected rather than overwritten.

The [source-controlled TreeSitter.DotNet 1.3.0 candidate record](https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs.Tests/TestData/PythonParserDecision/README.md)
documents the accepted bounded Python-docstring spike. Its 50.93 MiB all-grammar, multi-RID archive is an explicit
distribution trade-off, not a reason to exclude it from product code. Package upgrades must repeat the archive,
native-RID, and redistribution-notice review.

## Recovery and release boundary

If `generate` reports a marker, variant, path, or token error, fix the named manifest row or README and rerun the
command. If a write is interrupted, rerun `verify` to identify drift, then rerun `generate`, inspect the diff, and
rerun `verify`. Do not edit the generated region by hand as a substitute for updating its template or manifest field.

Package README reconciliation is intentionally separate from the [release authoring checklist](../../releases/release-authoring-checklist.md).
`eng/release prepare`, including its dry run, must not regenerate package README files; release preparation validates
its own exact artifact set while PackageIndex remains the owner of checked-in package-policy documentation.

### Release-preparation witness

When a release-preparation pull request also changes generated package documentation, the [Release verifier](../ForgeTrust.AppSurface.Release/README.md#verify-prep-diff) invokes this read-only command once:

```bash
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- release-prep-witness --base-ref <base-tip-commit> --witness /tmp/appsurface-release-prep-witness.json
```

It does not write chooser, readiness, or README files. Instead it records the base tip, exactly one merge base, HEAD, changed semantic sources, and deterministic SHA-256 hashes for the chooser, readiness dashboard, and each managed README body. Only a changed `packages/package-index.yml` or `release-guidance.template` can authorize those surfaces; `packages/README.md.yml` is hand-authored metadata and is never an input. When an authorized input changes a surface relative to the merge base, the release pull request must commit that surface and match the witness digest; a partial PackageIndex regeneration is rejected. The Release verifier rejects unknown, duplicate, unordered, unsafe, or uppercase-hash JSON values, and it requires a README's bytes outside the managed marker body to be identical to the merge-base version. Treat `--witness` as an advanced CI/test seam; use `./eng/release verify-prep-diff --base-ref main` as the normal front door.

## Adding a variant

Adding a variant is a policy change, not a per-package escape hatch. Document why the existing three variants cannot
express the reader-facing posture, add one exact template pair in [`release-guidance.template`](https://github.com/forge-trust/AppSurface/blob/main/tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template), extend
the renderer's finite allowlist and tests, update this table with a non-example, and add the package artifact proof.
Otherwise, use an existing variant and preserve the package-specific explanation outside the marker pair.
