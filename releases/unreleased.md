# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.4`. It stays provisional until the next tag is cut.

## What is taking shape

- Sanitized AppSurface Config audit diffs for comparing captured runtime configuration reports.

## Included in the next coordinated version

### Release and docs surface

- AppSurface Config now exposes a sanitized config audit diff surface. `ConfigAuditReportDiffer` compares two existing `ConfigAuditReport` snapshots without re-resolving providers, `ConfigAuditDiffTextRenderer` renders deterministic same-host or captured-snapshot evidence with redaction uncertainty called out, and `ConfigAuditDiffCommandRunner` gives apps command-framework-agnostic same-host and captured JSON workflows with display-safe problem/cause/fix/docs-link failures.

### AppSurface Flow

- Reduce internal `InMemoryFlowRunner<TContext>` routing overhead by using prevalidated `FlowDefinition<TContext>` execution metadata while keeping public Flow APIs unchanged.
- Reduce synchronous in-memory runner allocations by making `FlowExecutionContext<TContext>` an immutable value-type snapshot passed into each node execution.
- Expand the Flow benchmark suite with runner-shape, generated-authoring, and outcome-allocation lanes so future performance work can attribute remaining overhead before changing public APIs.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
- `FlowExecutionContext<TContext>` is now a readonly record struct instead of a sealed record class. Most node implementations continue to read `FlowId`, `Version`, `NodeId`, `State`, and `ResumeEvent` the same way, but code that depended on reference identity, nullable context parameters, or `context is null` checks should switch to checking the populated members it requires.
