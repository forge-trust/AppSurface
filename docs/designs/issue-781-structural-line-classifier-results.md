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

The immutable [Skoolit PR #548 diff](https://github.com/forge-trust/skoolit/pull/548/files), at head commit `3802397c1742a3a3724cd4a0a205948999010fbe`, adds three `SkoolitDbContext` properties of the form `DbSet<TEntity> Name => Set<TEntity>()`. These are expression-bodied method calls, not auto-properties. The earlier positive `DbSet<T> { get; set; }` fixture tested the generic type but **did not reproduce the original accessor syntax**. The new pinned-source regression replays the three declaration lines with locally declared types and synthetic measured patch-line evidence: all three reject as `accessor-expression-body`, leaving their raw measured/uncovered state intact. Its surrounding compilation and Cobertura values are fixtures, not Skoolit's original build or report.

The original Cobertura fragment was not retained among the accessible artifacts for the cited PR's [successful CI run](https://github.com/forge-trust/skoolit/actions/runs/32225391324). The historical [EvidenceHost design](appsurface-evidencehost-contract-first.md) reports 94.34% patch-line and 100% patch-branch coverage for that incident, but this follow-up cannot independently reconstruct its three line records or attribute that result to a particular compiler sequence point. In the motivating source sample, v0 applies to **0 of 3** accessor lines. That is an applicability result, not a measured false-negative rate for the deliberately narrower auto-property policy.

As an independent compiled-code spot check, the same Release test build of this AppSurface checkout (`70607130`) produced a portable PDB for `ForgeTrust.AppSurface.Flow`. A temporary .NET 10 probe used `System.Reflection.Metadata` and `PEReader` to match `FlowOutcomeAttribute` and `FlowExecutionContext<TContext>` method definitions to their IL bodies and non-hidden portable-PDB sequence points. The PDB maps both accessors of [`FlowOutcomeAttribute.CallsiteId`](../../Flow/ForgeTrust.AppSurface.Flow/FlowAuthoringAttributes.cs) to source line 176 and both accessors of [`FlowExecutionContext<TContext>.ActivityResult`](../../Flow/ForgeTrust.AppSurface.Flow/FlowExecutionContext.cs) to source line 33. Their getters contain `ldarg.0; ldfld; ret`, and setters contain `ldarg.0; ldarg.1; stfld; ret`. This confirms that two real AppSurface auto-properties compile to backing-field accessors and retain source sequence points. It does **not** prove that either property is behaviorally unimportant: one is a stable activity callsite identifier and the other carries a flow activity result. The spot check did not run the classifier with the projects' full compiler contexts or join a same-build Cobertura report; the probe was not added to the repository because this result does not justify a maintained comparator yet.

The bounded comparison therefore has no defensible cross-repository false-positive/false-negative rate or end-to-end latency delta. The decisive result is more basic: the exact motivating source form is excluded by design, while even compiled-trivial in-scope properties can carry domain meaning. Extending v0 to expression-bodied `Set<T>()` calls or changing coverage arithmetic would be a new policy decision, not a pilot follow-up. Keep the existing gate unchanged and park this implementation unless a separately scoped proposal supplies matching-build coverage/PDB/IL evidence, representative repositories, quantified errors and cost, and artifact/CLI compatibility requirements.
