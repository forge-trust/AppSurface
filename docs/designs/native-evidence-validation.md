# Native EvidenceHost validation

AppSurface's own acceptance checks should exercise the documented [EvidenceHost](../../start-here/evidencehost.md) extension points. Disposable orchestration that reimplements producer outcomes, resource readiness, deadlines, artifact integrity and cleanup hides gaps that downstream consumers also encounter.

## Agreed delivery

Create a checked-in native validation executable that composes explicit producers for the packed storage CLI and an installed-package consumer journey. The executable must use a supplied candidate package and selected .NET SDK, verify package identity, retain declared durable evidence outside owned scratch, and emit a canonical plan and manifest. A missing prerequisite, assertion, or cleanup must prevent a complete claim. Keep [coverage storage](../../guides/coverage-storage.md) behavior and numerical gates unchanged.

Add reusable command execution and workspace budgets to the existing [Aspire Evidence integration](../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md), streaming to the [artifact writer](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md), and explicit execution configuration to the [coverage producer](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md). Command launches use argument lists, bounded output, cancellation and owned-process termination. Workspace capacity must be checked while work is running, and only task-owned scratch may be retired. Artifact streaming preserves the existing declared size limits and hashes.

The storage journey verifies published CLI behavior with independent file/JSON/hash assertions. The consumer journey installs the supplied package in an isolated workspace and verifies test execution, report readers, unchanged coverage thresholds, and source/settings integrity. A small checked-in consumer fixture gives every developer and CI runner a reproducible baseline; external consumer recipes must declare their inputs and expected assertions explicitly.

Verified cross-platform coverage remains independently bound to matching source, SDK, configuration, scope and run identity. Compose it as an Evidence obligation using native verification without reducing the independent full-solution aggregate requirement. Historical reports never become current execution evidence.

## Acceptance

- [ ] Reusable bounded command execution with meaningful native process tests.
- [ ] Owned workspace and continuously enforced capacity budget, with preservation and exhaustion tests.
- [ ] Streaming artifact writes/verification with unchanged limits and failure tests.
- [ ] Explicit coverage execution configuration with legacy defaults and unchanged gates.
- [ ] Repeatable packed CLI storage journey through EvidenceHost.
- [ ] Repeatable installed-consumer journey with independent test/report/integrity assertions.
- [ ] Native verified fan-in consumption through EvidenceHost, retaining aggregate/patch policy.
- [ ] Reference, decision, pitfall and adoption documentation with a runnable native recipe.
- [ ] Formatting, relevant tests, independent review, QA and current coverage gates pass.
- [ ] Draft follow-up PR and required CI pass; at most three CI repair rounds and 60 cumulative minutes of CI waiting.

## Execution record

Base: `b5618531` on `codex/native-evidence-validation`, following the validated storage implementation. Existing draft #889 is preserved. Source scope and 95/85 coverage thresholds, including the repository's existing tolerance, remain unchanged. Local disk capacity was approximately 21 GiB at startup, so avoid duplicate consumer clones and unbounded acceptance output. Local validation orchestration for this work uses native tools rather than new Python harnesses.
