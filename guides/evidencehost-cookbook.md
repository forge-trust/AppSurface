# EvidenceHost Cookbook

For a checked-in executable example of local package checks, see the [native validation procedure](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md). AppSurface uses that recipe to check its packed storage CLI and installed-package consumer through explicit producer contracts. Its procedure report grants no protected admission, native acceptance, or gate eligibility; see the [current support and migration contract](../docs/evidence/evidencehost-migration.md).

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

### HTTP readiness in an ordinary Aspire test

For one profile-based AppHost test, prefer the [typed readiness fixture](../Aspire/ForgeTrust.AppSurface.Aspire.Testing/README.md#http-readiness-fixture). It composes the existing `CreateAsync` or `CreateConfiguredAsync` builder with native resource health, one named endpoint, and one status-only HTTP probe. The fixture owns the returned builder and application lifecycle; `Application` and `Endpoint` are borrowed for assertions. Do not build or dispose the borrowed application separately.

Use [Aspire's native health wait](https://learn.microsoft.com/dotnet/aspire/testing/accessing-resources) alone when resource health is enough. Use [native Aspire HTTP health checks](https://aspire.dev/reference/api/csharp/aspire.hosting/resourcebuilderextensions/methods/) when their configured endpoint/status condition already meets the test's need. The shared [standalone readiness probe](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#httpreadinessprobewaitasync) is for an already-owned `HttpClient` and endpoint. None of these status checks replaces a body assertion, browser journey, or business readiness predicate.

The complete [.NET 10 standalone package consumer](https://github.com/forge-trust/AppSurface/tree/main/tests/evidence-readiness-consumer) and [typed fixture consumer](https://github.com/forge-trust/AppSurface/tree/main/tests/evidence-readiness-typed-consumer) are the canonical executable quickstarts; the [consumer verification script](https://github.com/forge-trust/AppSurface/blob/main/scripts/verify-evidence-readiness-consumers.sh) runs their package proofs. Follow the projects there for exact coordinated package versions, setup, commands, and recorded output rather than copying a second sample into this cookbook. Controlled-handler output proves package behavior; it is not a live endpoint or real Aspire resource result.

For an EvidenceHost resource, keep the application lease and browser producer in the existing explicit evidence composition. `EvidenceAspireApplication.CreateHttpReadiness(...)` binds the declared Evidence resource id to one Aspire resource, endpoint, and probe path. Each readiness wait first observes native resource health, then resolves the endpoint and runs the shared HTTP condition with an independent owned client. Register the adapter once; its cleanup drains admitted waits and releases its own application registration. Other adapters retain the same application until their registrations also release; the final release joins application cleanup. Dispose all created adapters, and do not dispose the lease directly concurrently with an active readiness wait. See the [Evidence.Aspire adapter reference](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#http-readiness-adapter) and the coordination record for [issue #778](https://github.com/forge-trust/AppSurface/issues/778).

Both pinned Skoolit gate equivalences preserve their existing consumer policy: three-minute startup deadline, `MaxAttempts = 30`, three-second request timeout, 250 ms retry delay, `UseProxy = false`, and two-second `ConnectTimeout`. These are explicit migration settings, not SDK defaults. Equivalence fixtures are not actual Skoolit adoption, and headers-only probing does not buffer or assert response bodies as a prior probe might. No latency or reliability improvement is claimed without measurements.

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
