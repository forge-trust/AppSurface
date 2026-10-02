# EvidenceHost: risk-mediated CI without coverage theater

EvidenceHost is AppSurface's contract-first approach to CI evidence. Its purpose is simple: make a meaningful change visibly prove the risk it introduces, while allowing an explicitly low-risk change to pass without someone inventing tests for incidental lines.

It is not a replacement for a test framework, coverage collector, GitHub Actions, Aspire AppHost, Docker, or a hosted coverage dashboard. It starts no infrastructure by default and has no outbound telemetry.

## Current support status

The supported user workflow in this checkout is **planning and structural verification**. Trusted PR and release
execution is not admitted on any provider or platform until the complete consumer and CI proofs are accepted.
The Linux x86_64 systemd/cgroup mechanism was observed in CI on October 2, 2026, but that run did not validate
the updated production broker or its complete consumer integration. Windows and macOS have no accepted worker
mechanism. The restricted CLI coverage adapter is implemented, including broker-retained report collection and
protected ReportGenerator execution, but the updated production broker and complete consumer path have not been
validated in CI. The Trusted gate proof remains closed; do not use `appsurface evidence run` as an operational
coverage or release gate.

`doctor` reports planning prerequisites only and leaves protected execution facts `unverified`. `explain` resolves
policy against explicit changed paths without executing producers. `verify` checks structure and digests; it does
not prove who supplied the files or admit them to a gate. The [migration and support guide](../docs/evidence/evidencehost-migration.md)
describes the current boundaries and required migration. Use the existing [private coverage gate](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate)
for numeric coverage enforcement. The [consumer acceptance record](../docs/evidence/issue779-consumer-acceptance.md)
tracks the required proofs and excluded providers.

## The first five minutes

Install the [AppSurface CLI](../Cli/ForgeTrust.AppSurface.Cli/README.md), then create the deliberately small policy starter:

```bash
appsurface evidence init --sample
appsurface evidence doctor --policy .appsurface/evidence/evidence.policy.json --path docs/README.md
appsurface evidence explain --policy .appsurface/evidence/evidence.policy.json --path src/Orders/SubmitOrder.cs
```

The generated policy maps `docs/**` to an explicit `no-evidence` profile and sends every unmatched path to a non-empty conservative coverage profile. `explain` tells a developer, before tests start, which profile won, which rule selected it, which producers must run, and which obligations they must close.

These commands are planning-only. `doctor` can report `ready` or `ready_with_external_prerequisites` when the policy and local checks are sound, but the protected worker/verifier check remains `unverified`. A CI environment flag does not change that result. Policy and generated JSON use the shared [20 MiB counted input limit](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input); invalid or oversized inputs fail before planning or verification.

```text
Evidence plan: targeted-coverage (targeted)
Why: conservative:targeted-coverage
Changed paths: src/Orders/SubmitOrder.cs
Obligations: changed-behavior-covered
Required producers: coverage
Required resources: none
Admission: planning only; protected execution facts remain unverified.
```

This output tells you what policy selected. It is not evidence that a test ran or a gate passed.

## What a claim means

The manifest vocabulary has four structural outcomes. Their names and digests do not supply protected admission:

| Claim | Current protected gate use | Meaning |
| --- | --- | --- |
| `TargetedComplete` | Not by itself | Every selected producer reported success and closed the selected obligations in the manifest. |
| `ReleaseComplete` | Not by itself | The manifest records a release-profile result and a validated, not attested, envelope. |
| `NoEvidenceRequired` | Not by itself | A checked-in empty profile matched the paths. It never means “tests were skipped.” |
| `ObservationOnly` | No | An informational result that cannot satisfy a gate. |

Only a protected gate with current expected identities and artifacts obtained through its protected channel can decide whether a claim is consumable. A filtered suite, unavailable producer, timeout, or unsatisfied assertion is incomplete evidence, not a partial pass. See the [contract reference](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#claim-rules).

## Choose the smallest path

| Situation | Use | Why |
| --- | --- | --- |
| You need to understand the selected policy before running existing tests. | [`appsurface evidence doctor/explain`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence) | Planning and local prerequisite diagnostics only. |
| You need numeric Cobertura enforcement today. | [`appsurface coverage gate`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) | The current private, local coverage gate; it does not depend on EvidenceHost admission. |
| Consumer has PostgreSQL, a browser, or a real E2E journey to prove. | [`ForgeTrust.AppSurface.Evidence.Aspire`](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md) and the [migration guide](../docs/evidence/evidencehost-migration.md) | The lifecycle API is provisional; protected consumer and platform proofs are still required before it can gate. |
| You are writing a producer, external gate, or policy editor. | [`ForgeTrust.AppSurface.Evidence.Contracts`](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md) and [`Planner`](../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md) | Stable contracts and deterministic diff-to-plan resolution. |

## Keep test and application hosts separate

An EvidenceHost is not a specialized production `AppHost`. Keep it in consumer test/CI composition and register every resource and producer explicitly. The obsolete legacy `EvidenceHostBootstrap.RunAsync(bool)` has no implicit mode: omission or `false` maps to `ASEVD401`; `true` selects only Observation and maps to `ASEVD402` when an independent supervisor is missing. The Boolean never supplies supervisor authority. The public prebuilt-builder `EvidenceAspireApplication.StartAsync(existingBuilder)` entry fails with `ASEVD400`. The explicit request is the migration target, but the current production Aspire factory path is unsupported and does not start an application. See the [request and admission reference](../docs/evidence/evidencehost-migration.md#api-migration) before changing a consumer. There is no assembly scanning, ambient resource discovery, hidden Docker provisioner, or automatic E2E selection.

## What v1 deliberately does not decide

The policy does not classify arbitrary C# constructors, property accessors, generated code, or “trivial” lines as low value. Existing coverage exclusions remain the appropriate mechanism for known generated or excluded source. The current runtime also does not establish independent artifact attestation, cross-job aggregation, Docker sandboxing, trend analytics, or an AppSurface-hosted dashboard. A `ValidatedNotAttested` value is not proof of origin.

## Read next

- [EvidenceHost cookbook](../guides/evidencehost-cookbook.md)
- [EvidenceHost migration and current support](../docs/evidence/evidencehost-migration.md)
- [CLI evidence command reference](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence)
- [Evidence contract reference](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md)
- [Aspire lifecycle reference](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md)
