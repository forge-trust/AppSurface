# #781 Structural Line Classifier Pilot Results

Status: Complete as a test-only v0 pilot. A real-source follow-up found that the motivating Skoolit accessors are outside v0. No production coverage-policy change is proposed.

This note records the outcome of the approved [#781 structural line classifier plan](issue-781-fail-closed-structural-line-classifier-pilot.md). The implementation is deliberately isolated to the CLI test project, where it can assess immutable `PatchCoverageAnalysis` evidence without changing coverage calculations, pass/fail decisions, report formats, CLI arguments, or artifact-writing behavior.

## Evidence collected

The test-only classifier accepts only a semicolon-only C# auto-property with exactly `get; set;` or `get; init;`, and emits one sorted, deterministic in-memory audit entry for every changed coverage line. Each entry preserves the source policy/version, path and line, raw measured/covered/condition values, source fingerprint and provenance, parse-option identity, syntax-tree path, optional symbol identity, disposition, and reason code.

The focused suite ran the classifier against a real `PatchCoverageAnalysis` constructed by the existing Cobertura-plus-unified-diff evaluator. It proved that an unexecuted changed auto-property can be classified as structural while retaining `isMeasured=true`, `lineCovered=false`, and its raw `0/1` condition evidence. It also ran the normal CLI coverage gate before and after classification for both a passing threshold and an intentionally failing threshold, then compared exit outcomes, patch evidence, metrics, and every emitted report byte. All were unchanged.

The suite also proves fail-closed rejection for these categories:

- non-C# and unmeasured lines;
- brace-only, blank, documentation, comment-only, and mixed-property/member locations;
- missing, duplicate, generated, synthesized, fingerprint-mismatched, and tree-path-mismatched source evidence;
- compiler-tree and conditional-symbol mismatches;
- attributes, initializers, expression bodies, accessor bodies/modifiers, unsupported property shapes, partial containing types, overridden properties, unbound types, and semantic diagnostics on properties or containing base lists; and
- injected analysis exceptions, where the audit retains only the exception type rather than message text.

The audit serialization uses stable path/line/reason ordering and camel-cased JSON. It remains in memory: this pilot deliberately creates no structural-classifier file, changes no existing coverage artifact, and exposes no runtime CLI behavior.

## Reproducible validation

Run the focused pilot tests with:

```sh
dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore --configuration Release --filter FullyQualifiedName~StructuralLineClassifierTests --logger "console;verbosity=detailed"
```

The original focused run passed 43 tests. On 2026-09-26, the pinned-source regression passed with the full 44-test focused suite using `--configuration Release`, `--artifacts-path /private/tmp/issue781-real-evidence-artifacts`, and `-p:NuGetAudit=false` on .NET SDK 10.0.102. The original benchmark used the following environment:

| Component | Value |
| --- | --- |
| Host | macOS 26.6 on Darwin 25.6.0, arm64 |
| .NET SDK | 10.0.102 |
| .NET runtime | 10.0.2, arm64 |
| Roslyn | `Microsoft.CodeAnalysis.CSharp` 5.0.0 via central package management |
| Candidate corpus | 200 synthetic changed auto-property lines with one shared manifest and compilation |

## Benchmark observation

The focused suite makes 25 warmed classification runs over the 200-candidate corpus and 10 fresh fixture-manifest/compilation construction runs. This is a regression signal, not a production SLO: the code is test-only, current values are host-dependent, and the harness intentionally performs full Roslyn semantic checks for every candidate.

| Measure | Observed value |
| --- | --- |
| Classification min / p50 / p95 / max | 5.9328 / 6.2929 / 9.5920 / 13.8018 ms |
| Raw-evidence traversal control | 0.0039 ms p50, recorded separately; it is not a classifier-free coverage-gate baseline or an incremental overhead claim. |
| Maximum per-run allocation | 614,760 bytes (about 0.59 MiB) |
| Fixture-manifest/compilation min / p50 / p95 | 0.6540 / 0.6867 / 5.4321 ms |
| Allocation guard | 1 MiB per warmed run |

The warmed measurement window uses `GC.GetAllocatedBytesForCurrentThread` immediately around classification, after one complete cache-warming pass over the reusable manifest and compilation. The separate control walks the same raw changed-line evidence and reads its coverage fields, but deliberately omits source lookup, Roslyn binding, audit construction, and sorting; it is therefore not an incremental coverage-gate baseline and no delta is reported. This September 23 rerun includes the mixed-line token ownership guard, complete parse-option identity, and containing-base-list diagnostic check; host-sensitive timings do not establish a per-change latency delta. The observed p95 records classifier-only latency on the named baseline host; it is not an incremental coverage-gate claim or a production SLO. The observed allocation is below the approved 1 MiB automated guard. Any future path toward runtime use should replace this synthetic corpus with representative repositories and a performance budget agreed by the owners of the prospective integration.

## Decision

**Do not introduce a production policy or promote v0 as the solution to the motivating incident.** The pilot establishes that a narrow, auditable, fail-closed classification can be evaluated beside current coverage evidence without mutating it. It does not establish that this structural category is equivalent to compiler-generated execution behavior across real builds.

### Real-source follow-up (2026-09-26)

The immutable [Skoolit PR #548 diff](https://github.com/forge-trust/skoolit/pull/548/files), at head commit `3802397c1742a3a3724cd4a0a205948999010fbe`, adds three `SkoolitDbContext` properties on [lines 625, 628, and 631](https://github.com/forge-trust/skoolit/blob/3802397c1742a3a3724cd4a0a205948999010fbe/src/Skoolit.Persistence/SkoolitDbContext.cs#L624-L631) of the form `DbSet<TEntity> Name => Set<TEntity>()`. These are expression-bodied method calls, not auto-properties. The earlier positive `DbSet<T> { get; set; }` fixture tested the generic type but **did not reproduce the original accessor syntax**. The new pinned-source regression replays the three declaration lines with locally declared types and synthetic measured patch-line evidence: all three reject as `accessor-expression-body`, leaving their raw measured/uncovered state intact. Its surrounding compilation and Cobertura values are fixtures, not Skoolit's original build or report.

The original Cobertura fragment was not retained among the accessible artifacts for the cited PR's [successful CI run](https://github.com/forge-trust/skoolit/actions/runs/32225391324). The historical [EvidenceHost design](appsurface-evidencehost-contract-first.md) reports 94.34% patch-line and 100% patch-branch coverage for that incident, but this follow-up cannot independently reconstruct its three line records or attribute that result to a particular compiler sequence point. In the motivating source sample, v0 applies to **0 of 3** accessor lines. That is an applicability result, not a measured false-negative rate for the deliberately narrower auto-property policy.

### Bounded coverage-native/PDB comparison

A second pass sampled eleven authored declarations across six AppSurface packages at checkout `57678c57` (production source unchanged since `70607130`). It deliberately includes covered and uncovered auto-properties, a sensitive value, an MSBuild compatibility boundary, initialized properties, and an expression-bodied getter. This is a varied convenience sample, **not** a statistical or cross-repository error-rate corpus. The three original Skoolit lines above are a separate real-source sample without their original compiled artifacts.

The Release solution build produced the DLLs and portable PDBs used here. A temporary .NET 10 probe matched method definitions through `System.Reflection.Metadata`/`PEReader`, read each method's IL, and enumerated non-hidden PDB sequence points. Two [standard coverage runs](../../scripts/coverage-solution.sh) at this checkout merged collector Cobertura from all 53 test projects into the generated `TestResults/coverage-merged/coverage.cobertura.xml`; the eleven table hit counts agreed across both runs. Both builds had zero warnings/errors, but **four test projects failed in each run**. The first run encountered Docker storage/connection errors and child-process or Git-verification timeouts. With project parallelism reduced from two to the documented default of one, PostgreSQL passed but GoogleSecretManager, the durable example, AdoptionMetrics, and Docs had assertion or child-process timeout failures. The separately rerun failing tests passed in isolation. Consequently, these hit counts are observations from partial test runs, not a passing full-gate baseline; both full gate commands exited 1. Running `coverage gate` manually on each generated report passed its numeric checks (95.84% lines, 88.15% branches, 100% patch lines/branches), but that does not override either failed test run. The generated report path is overwritten on each run, so this note does not present a historical file digest as a current artifact.

| Real source declaration | v0 source shape, not a full-project audit | Same-build Cobertura line evidence |
| --- | --- | --- |
| [Tailwind task `Configuration`:101](../../Web/ForgeTrust.AppSurface.Web.Tailwind.Tasks/RunTailwindBuildTask.cs) | Eligible `get; set;`; public compatibility input | Measured, getter hits **0** |
| [Tailwind task `TargetFramework`:109](../../Web/ForgeTrust.AppSurface.Web.Tailwind.Tasks/RunTailwindBuildTask.cs) | Eligible `get; set;`; public compatibility input | Measured, getter hits **0** |
| [LocalSecrets `ApplicationName`:24](../../Config/ForgeTrust.AppSurface.Config.LocalSecrets/AppSurfaceLocalSecretsOptions.cs) | Eligible `get; set;`; configuration identity | Getter hits 310 |
| [Web Push `ActiveVapidKeyId`:13](../../Web/ForgeTrust.AppSurface.Web.Push/AppSurfaceWebPushOptions.cs) | Eligible `get; set;`; selects an active signing key | Getter hits 302 |
| [Web Push `PrivateKey`:45](../../Web/ForgeTrust.AppSurface.Web.Push/AppSurfaceWebPushOptions.cs) | Eligible `get; set;`; sensitive signing-key material | Getter hits 224 |
| [Flow `CallsiteId`:176](../../Flow/ForgeTrust.AppSurface.Flow/FlowAuthoringAttributes.cs) | Eligible `get; set;`; stable activity callsite identity | Getter hits 3 |
| [Aspire `LastRenderResult`:57](../../Aspire/ForgeTrust.AppSurface.Aspire/AspireDeploymentPipelineAdapter.cs) | Eligible `get; set;`; per-pipeline render cache | Getter hits 4 |
| [Observability `OtlpEndpoint`:43](../../Observability/ForgeTrust.AppSurface.Observability/AppSurfaceObservabilityOptions.cs) | Eligible `get; set;`; exporter destination | Getter hits 135 |
| [LocalSecrets `DocsHint`:34](../../Config/ForgeTrust.AppSurface.Config.LocalSecrets/AppSurfaceLocalSecretsOptions.cs) | Rejected: property initializer | Getter hits 97; constructor has a PDB point on line 34, but no constructor line-34 entry appears in Cobertura |
| [Web Push `VapidKeys`:20–21](../../Web/ForgeTrust.AppSurface.Web.Push/AppSurfaceWebPushOptions.cs) | Rejected: get-only property with initializer | Getter hits 398 on line 20; constructor hits 124 on initializer line 21 |
| [Flow `ReadOnlySet<T>.Count`:15](../../Flow/ForgeTrust.AppSurface.Flow/ReadOnlySet.cs) | Rejected: expression-bodied getter | Getter hits 1 |

For all eight plain auto-properties in the table, the compiled getter and setter contain only backing-field load/store IL (`ldarg.0; ldfld; ret` and `ldarg.0; ldarg.1; stfld; ret`), and both PDB accessor sequence points map to the declaration line. The merged Cobertura lists a getter method but **no setter method entry** for each of those eight. It therefore cannot tell whether the setter was used. The expression-bodied `ReadOnlySet<T>.Count` getter instead loads the `_inner` field and calls its `Count` getter. The two initialized near-misses show why constructor sequence points must be inspected separately from accessor bodies and why PDB mapping does not determine the collector's line accounting.

The two zero-hit [Tailwind task inputs](../../Web/ForgeTrust.AppSurface.Web.Tailwind.Tasks/RunTailwindBuildTask.cs) are still named bindings in the imported [MSBuild target](../../Web/ForgeTrust.AppSurface.Web.Tailwind/build/ForgeTrust.AppSurface.Web.Tailwind.targets) at lines 113–114. The collector's zero getter hits are not evidence that those setters, or the public compatibility contract, are dispensable. Likewise, trivial backing-field IL does not make `PrivateKey`, `CallsiteId`, or `LastRenderResult` semantically inert. A future exclusion based on this syntax could improve a patch percentage without proving lower risk.

The table reports **source-shape eligibility** (eight of eleven) and Cobertura/PDB observations, not actual v0 audit dispositions from the projects' complete Roslyn compilations. The test-only classifier requires an explicit matching fixture manifest and compilation; this pass did not add an arbitrary checkout loader or claim to reconstruct those project contexts. Two of the eight source-shape candidates have zero getter hits, while all three original Skoolit lines are outside v0. Neither fraction is a false-positive or false-negative rate: there are no independently labeled low-value lines, the Skoolit Cobertura/PDB was unavailable, and the full AppSurface test run failed. The existing 200-line synthetic timing result is classifier-only; no end-to-end latency delta for real projects was measured.

**Go/no-go: no-go for a production policy or arithmetic change.** The motivating source form is excluded, and the real zero-hit candidates expose public compatibility inputs rather than a proved low-risk category. Keep the current gate unchanged. Park the test-only pilot after review; any renewed proposal needs a separately scoped, matching-build multi-repository corpus, independent value/risk labels, quantified error and latency costs, and artifact/CLI compatibility requirements. Extending v0 to expression-bodied `Set<T>()` calls would be a new policy decision, not a correction to this pilot.
