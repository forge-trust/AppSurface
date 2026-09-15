# #781 Structural Line Classifier Pilot Results

Status: Complete as a test-only v0 pilot. No production coverage-policy change is proposed.

This note records the outcome of the approved [#781 structural line classifier plan](issue-781-fail-closed-structural-line-classifier-pilot.md). The implementation is deliberately isolated to the CLI test project, where it can assess immutable `PatchCoverageAnalysis` evidence without changing coverage calculations, pass/fail decisions, report formats, CLI arguments, or artifact-writing behavior.

## Evidence collected

The test-only classifier accepts only a semicolon-only C# auto-property with exactly `get; set;` or `get; init;`, and emits one sorted, deterministic in-memory audit entry for every changed coverage line. Each entry preserves the source policy/version, path and line, raw measured/covered/condition values, source fingerprint and provenance, parse-option identity, syntax-tree path, optional symbol identity, disposition, and reason code.

The focused suite ran the classifier against a real `PatchCoverageAnalysis` constructed by the existing Cobertura-plus-unified-diff evaluator. It proved that an unexecuted changed auto-property can be classified as structural while retaining `isMeasured=true`, `lineCovered=false`, and its raw `0/1` condition evidence. It also ran the normal CLI coverage gate before and after classification for both a passing threshold and an intentionally failing threshold, then compared exit outcomes, patch evidence, metrics, and every emitted report byte. All were unchanged.

The suite also proves fail-closed rejection for these categories:

- non-C# and unmeasured lines;
- brace-only, blank, documentation, and comment-only locations;
- missing, duplicate, generated, synthesized, fingerprint-mismatched, and tree-path-mismatched source evidence;
- compiler-tree and conditional-symbol mismatches;
- attributes, initializers, expression bodies, accessor bodies/modifiers, unsupported property shapes, partial containing types, overridden properties, unbound types, and semantic diagnostics; and
- injected analysis exceptions, where the audit retains only the exception type rather than message text.

The audit serialization uses stable path/line/reason ordering and camel-cased JSON. It remains in memory: this pilot deliberately creates no structural-classifier file, changes no existing coverage artifact, and exposes no runtime CLI behavior.

## Reproducible validation

Run the focused pilot tests with:

```sh
dotnet test Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj --no-restore --configuration Release --filter FullyQualifiedName~StructuralLineClassifierTests --logger "console;verbosity=detailed"
```

The recorded run passed all 31 tests. It used the following environment:

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
| Classification min / p50 / p95 / max | 2.1126 / 2.3182 / 2.3844 / 3.5713 ms |
| Raw-evidence traversal control | Recorded separately in test output; it is not a classifier-free coverage-gate baseline or an incremental overhead claim. |
| Maximum per-run allocation | 427,936 bytes (about 0.41 MiB) |
| Fixture-manifest/compilation min / p50 / p95 | 0.6452 / 0.6636 / 5.1962 ms |
| Allocation guard | 1 MiB per warmed run |

The warmed measurement window uses `GC.GetAllocatedBytesForCurrentThread` immediately around classification, after one complete cache-warming pass over the reusable manifest and compilation. The separate control walks the same raw changed-line evidence and reads its coverage fields, but deliberately omits source lookup, Roslyn binding, audit construction, and sorting; it is therefore not an incremental coverage-gate baseline and no delta is reported. The observed p95 records classifier-only latency on the named baseline host; it is not an incremental coverage-gate claim or a production SLO. The observed allocation is below the approved 1 MiB automated guard. Any future path toward runtime use should replace this synthetic corpus with representative repositories and a performance budget agreed by the owners of the prospective integration.

## Decision

**Collect more evidence; do not introduce a production policy.** The pilot establishes that a narrow, auditable, fail-closed classification can be evaluated beside current coverage evidence without mutating it. It does not establish that this structural category is equivalent to compiler-generated execution behavior across real builds.

Before proposing any non-test integration, gather a multi-repository corpus and compare each candidate against a coverage-native, PDB, or IL-grounded comparator. The proposal must quantify false positives and false negatives, identify generated and mixed-source edge cases, define artifact and CLI compatibility requirements, and receive a separate decision. Until then, changed structural lines continue to participate in the existing coverage gate exactly as they do today.
