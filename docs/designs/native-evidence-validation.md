# Native Evidence validation procedure and protected acceptance

**Current state:** the repository tool and cross-platform workflow run a portable validation procedure. They do not generate protected Evidence, qualify native execution, or establish gate eligibility. The previously implemented package, storage, consumer, fan-in, lifecycle, and tamper assertions remain useful checks. Their historical success is recorded as procedure coverage only. A protected operation remains unimplemented and unaccepted.

This design separates two contracts so a portable test recipe cannot be mistaken for a protected EvidenceHost run.

## Operation contract

The executable requires an explicit `--operation` argument before opening any package, profile, scratch directory, or output path:

- Missing or unknown operation returns `ASEVD401` before input or output I/O. An empty invocation only prints usage and returns exit 2; `--help` only prints usage and returns exit 0.
- `--operation procedure` selects only the portable package, storage, installed-consumer, and fan-in checks. It creates no host and no Evidence manifest.
- `--operation protected` returns `ASEVD404` before input or output I/O because the registry has no protected native operation. No fake protected step, registration, test supervisor, or Observation fallback is allowed.

The procedure writes `procedure-plan.json` and `native-validation-procedure.json`. The latter uses schema `appsurface-native-validation-procedure-v1` and camel-case JSON options. It carries `procedurePassed`, `cleanupCompleted`, and `producerResults`, while `protectedAdmission`, `nativeAcceptance`, and `gateEligible` are fixed false. There is no `evidence-manifest.json` in procedure output. Exit zero means only that the selected portable assertions and their cleanup passed.

The report is structurally bounded by the closed catalogue to at most three producer results (`packed-storage`, `installed-consumer`, and optional `native-fan-in`), four declared artifact metadata records, and 1,024 diagnostic characters per result. It is diagnostic output, not gate input or proof. A future protected operation requires its own registered implementation, actual native execution evidence, and independent review; changing the operation string cannot enable it.

## Procedure scope and policy

The workflow retains its existing triggers, job ID, three operating systems, and repository `contents: read` permissions. Each OS now has separate, parallel `storage` and `consumer` matrix entries. The original 25-minute outer deadline applies to each entry; producer deadlines remain unchanged. Each command explicitly passes `--operation procedure` and its `--mode`, then checks for the exact plan/report files, schema, success and cleanup fields, fixed-false protected/gate fields, and absence of an Evidence manifest. It uploads artifacts with procedure-specific names. A passing workflow check reports only that this declared procedure passed. The separate [runtime proof](../../.github/workflows/evidencehost-runtime-proof.yml), [protected qualification](../../.github/workflows/evidencehost-protected-qualification.yml), and [protected parent](../../.github/workflows/evidencehost-protected-parent.yml) workflows remain unchanged and are not invoked by this procedure.

The separate native lifecycle, tamper, fan-in, command, and workspace contract test selection remains active after a successful build. These are meaningful tests of implementation contracts; they are not a protected acceptance journey. The workflow does not alter protected EvidenceHost admission, invoke conditional native accepted journeys, or claim downstream package acceptance.

The package and storage checks retain independent identity, metadata, byte, restore, and maintenance assertions. The consumer checks retain the declared project/test inventory and report requirements. External consumer profiles still require a pinned commit and explicit project/test scope. Skips fail by default and may only be allowed by a matching declared pattern and rationale.

Numerical and source policy are unchanged:

- Consumer full coverage remains at least 95% line and 85% branch with zero tolerance.
- Consumer patch coverage, when requested, remains at least 95/85 with zero tolerance.
- Verified fan-in retains its trusted source/SDK/configuration/run bindings, complete project inventory and captures, recomposition, and full-solution plus patch gates.
- Fan-in retains the repository's established 0.5-point tolerance, Codecov patch mode, and nonempty, nondecreasing measurable patch denominators.
- Scratch budgets remain 2 GiB per producer and 1 GiB minimum free space by default. JSON, JUnit, entry, and depth limits remain unchanged.

This migration changes how results are labelled and admitted. It does not widen production limits, reduce gate scope, or make historical reports current evidence.

## Decision

Keep the portable procedure because it catches package, storage, consumer, fan-in, lifecycle, and tamper regressions on Linux, Windows, and macOS. Explicit operation selection makes its scope reviewable and fails closed when the user asks for a protected operation that has no implementation.

A future protected operation should be added only when a real supported native setup exists. The tool must then prove protected worker identity, actual lifecycle and cleanup, current source/SDK/project bindings, and genuine native acceptance. A procedure report or package fixture must not be promoted or relabelled to satisfy those requirements. See the [current EvidenceHost support and migration contract](../evidence/evidencehost-migration.md) and [Issue 779 cutover record](../evidence/issue779-csharp-runtime-cutover.md#local-integration-with-current-main--2026-10-27).

## Pitfalls

- Do not infer protected acceptance from `procedurePassed`, a zero process exit, passing CI, producer results, matching hashes, or the presence of a plan.
- Do not add an Evidence manifest to procedure mode. Its absence is part of the contract.
- Do not default the operation. Missing operation must fail before file access.
- Do not make `protected` an alias for the procedure. It remains an explicit unsupported-operation error until real protected code is registered.
- Do not alter the existing numerical coverage thresholds, source scope, fan-in capture inventory, or time and size budgets to make the procedure easier to pass.
- Do not run conditional native accepted journeys as an unlabelled cross-platform test. Preserve them as separate, explicitly authorized qualification work.

## Acceptance history

The original implementation plan targeted a native EvidenceHost acceptance executable. The implementation produced useful bounded command/workspace/artifact APIs and procedure assertions for packed storage, installed consumers, and verified fan-in. Those items are complete only as portable procedure functionality. They do not demonstrate an admitted EvidenceHost run on this branch because production registration remains fail-closed.

| Original implementation item | Procedure status | Protected acceptance status |
| --- | --- | --- |
| Bounded command execution, workspace capacity, streaming artifacts, and execution configuration | Implemented with unit and contract tests; existing limits retained | Not evidence of protected worker admission or custody |
| Packed CLI storage and installed-consumer checks | Runnable local procedure assertions | Not an externally accepted package or arbitrary consumer proof |
| Verified fan-in input and recomposition checks | Runnable local data/contract assertions | Not proof that the captures came from a protected current run |
| Cross-platform workflow | Runs explicit procedure operation and separately selected contract tests | Does not claim native EvidenceHost acceptance |
| Protected lifecycle and downstream accepted execution | Not implemented by this procedure | Open; requires supported native setup and independent validation |

Historical acceptance checklists for source implementation and procedure tests must be read with this distinction. Formatting, relevant tests, review, QA, current coverage gates, and downstream protected acceptance remain release requirements where applicable. The workflow passing cannot close them by itself.

## References

- [Tool reference and invocation](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md)
- [Cross-platform procedure workflow](../../.github/workflows/native-evidence-validation.yml)
- [EvidenceHost entry point](../../start-here/evidencehost.md)
- [Aspire lifecycle and readiness reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md)
- [Evidence contract reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md)
- [Coverage fan-in workflow](../../guides/coverage-fan-in.md)
- [Coverage storage guide](../../guides/coverage-storage.md)
