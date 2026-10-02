# EvidenceHost Cookbook

This cookbook turns the [EvidenceHost guide](../start-here/evidencehost.md) into concrete CI patterns. The goal is not a higher coverage number. The goal is a visible, truthful claim that the changed risk was mediated.

## Status and safe scope

The copy-pastable workflows in this cookbook are for policy planning and diagnostics. Trusted execution is not
admitted on any provider or platform until full consumer and CI proofs are accepted. Linux x86_64 systemd/cgroup
mechanism behavior was observed in CI on October 2, 2026, but that did not validate the updated production broker
or complete consumer integration. Windows and macOS are unsupported. The restricted CLI coverage adapter is
implemented, but the updated production broker has not passed CI proof and the Trusted gate proof remains closed.
Do not use `appsurface evidence run` as a coverage or release gate. Follow the
[migration guide](../docs/evidence/evidencehost-migration.md) for the API transition and proof requirements.

## Documentation-only change

Use an explicit empty profile for a path family that your team has reviewed as non-behavioral:

```json
{
  "id": "documentation-only",
  "pattern": "docs/**",
  "profileId": "no-evidence",
  "precedence": 0
}
```

The referenced `no-evidence` profile must have no resources, producers, or obligations. Then inspect the selected profile:

```bash
appsurface evidence explain --path docs/README.md
```

The plan records that the checked-in policy selected an empty profile. Planning does not admit execution or
produce a gate result. Do not use this as a broad `**/*.cs` escape hatch, and do not point
`conservativeProfileId` at it: unknown changes must select real evidence.

## Existing coverage run with an explainable envelope

For an existing AppSurface coverage setup, use the same diff source for planning and numeric coverage gating:

```bash
appsurface evidence doctor --diff-file artifacts/changed.patch
appsurface evidence explain --diff-file artifacts/changed.patch
```

These commands resolve and display policy only. A restricted coverage adapter is implemented for a protected
Evidence run: it runs the declared subject test command through the independent broker, collects bounded
broker-retained Cobertura reports after child exit, merges them with the shared coverage workflow, and evaluates
the declared numeric gate. That implementation is not yet an admitted production workflow because the updated
broker and full consumer/CI proof remain unverified. It requires the ReportGenerator dependency beneath the
protected tool root; the repository's tool-pack path includes it, while `dotnet publish` does not include it by
default. For numeric enforcement today, use the existing
[`appsurface coverage run` and `coverage gate`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate)
workflow. It does not depend on EvidenceHost admission.

If CI intentionally selects only part of the test suite, use a separate targeted coverage setup or create a policy
profile whose declared obligations match that selection. Do not run a repository-wide threshold and call the
outcome “full coverage.”

## Resource-backed browser E2E

Keep resource probes and browser producers in consumer-owned test/CI composition, separate from the application
AppHost, and declare each one explicitly. The legacy `RunAsync(bool)` has no implicit mode: omission or `false`
maps to `ASEVD401`; `true` selects only Observation and maps to `ASEVD402` when an independent supervisor is
missing. The Boolean never supplies supervisor authority. The public prebuilt-builder
`EvidenceAspireApplication.StartAsync(existingBuilder)` entry fails with `ASEVD400`. The explicit request is the
migration target, but the current production Aspire factory path is unsupported and does not start an
application. This checkout also lacks accepted full consumer/CI proof. Do not copy an execution snippet into a gate workflow; see
[API migration](../docs/evidence/evidencehost-migration.md#api-migration).

When a supported consumer path is admitted, readiness probes must wait for the condition the test needs, not just
resource creation, and a producer must return only its declared assertion ids. A timeout, failed assertion, or
incomplete cleanup must remain ineligible.

## Release evidence

Trusted and release execution remain unadmitted on every platform. A verifier-shaped value or
`ValidatedNotAttested` status is not enough: admission needs a current accepted consumer/platform proof, and the
downstream gate needs its expected identities plus the plan and manifest through a protected channel. The
[`EvidenceProtectedGateExpectation` reference](../docs/evidence/evidencehost-migration.md#protected-gate-and-artifacts)
lists those facts. `doctor` can inspect policy and local prerequisites, but never runs the protected verifier.

## Diagnose before you rerun

| Signal | Meaning | Next action |
| --- | --- | --- |
| `ready` from `doctor` | Structural planning and local checks succeeded. Protected execution remains unverified. | Review the plan with `explain`; do not treat this status as admission. |
| `ready_with_external_prerequisites` | Planning succeeded, but a local or external prerequisite is missing; protected execution remains unverified. | Review the named check; this status does not authorize an execution or make the current production Aspire factory path available. |
| `blocked` | A policy/diff/envelope condition prevents a truthful run. | Read the named diagnostic and fix the source condition. |
| `ClaimKind.None` | A manifest records incomplete producer or obligation results. | Read its summary; do not lower a gate blindly. |
| `ObservationOnly` | Informational mode; it cannot satisfy a gate and still requires an independent protected worker. | Do not select it as a shortcut for local execution or CI admission. |

## Safe extension rules

- Keep policies checked in and review `no-evidence` rules like any other risk exception.
- Version producer behavior and assertion ids when their meaning changes.
- Preserve plan and manifest through the protected channel. `appsurface evidence verify` checks structural binding only; it does not authenticate provenance.
- Use existing coverage exclusions for known generated sources. Do not classify arbitrary low-value lines with a hidden heuristic.
- Keep test profiles, browser binaries, containers, credentials, threshold values, and release policy owned by the consumer environment.

Read next: [EvidenceHost start here](../start-here/evidencehost.md), the [migration and support guide](../docs/evidence/evidencehost-migration.md), [planner reference](../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md), and [coverage gate reference](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate).
