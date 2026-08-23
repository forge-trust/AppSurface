# EvidenceHost Cookbook

This cookbook turns the [EvidenceHost guide](../start-here/evidencehost.md) into concrete CI patterns. The goal is not a higher coverage number. The goal is a visible, truthful claim that the changed risk was mediated.

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

The referenced `no-evidence` profile must have no resources, producers, or obligations. Then run:

```bash
appsurface evidence explain --path docs/README.md
appsurface evidence run --path docs/README.md
```

The manifest can claim `NoEvidenceRequired`. Do not use this as a broad `**/*.cs` escape hatch, and do not point `conservativeProfileId` at it: unknown changes must select real evidence.

## Existing coverage run with an explainable envelope

For an existing AppSurface coverage setup, use the same diff source for planning and numeric coverage gating:

```bash
appsurface evidence doctor --diff-file artifacts/changed.patch
appsurface evidence run --diff-file artifacts/changed.patch --solution App.slnx
```

The selected coverage producer runs the existing `coverage gate` evaluator with its `coverageGate` values from the checked-in profile. Keep both outputs: the manifest makes profile selection and missing capabilities legible, while `coverage-gate.md` names the exact uncovered patch targets. The existing standalone `coverage gate` command remains available for a consumer that has not adopted EvidenceHost.

If CI intentionally selects only part of the test suite, call that a targeted observation or create a policy profile whose declared obligations match that selection. Do not run a repository-wide threshold and call the outcome “full coverage.”

## Resource-backed browser E2E

Put the EvidenceHost in a test/CI project, separate from the application AppHost. Register each resource readiness probe and browser producer explicitly:

```csharp
await using var host = EvidenceHostBootstrap.Create(plan, registration =>
{
    registration.AddResource(postgresReadiness);
    registration.AddResource(webApplicationReadiness);
    registration.AddProducer(submitOrderBrowserProducer);
});

var manifest = await host.RunAsync(cancellationToken: cancellationToken);
if (manifest.ClaimKind != EvidenceClaimKind.TargetedComplete)
{
    throw new InvalidOperationException("Order submission evidence is incomplete.");
}
```

`postgresReadiness` and `webApplicationReadiness` should wait for the condition the test needs—not simply a created resource. The browser producer must return only its declared assertion ids. A producer time limit, resource deadline, test failure, or cleanup failure produces no gate-eligible claim.

## Release evidence

Release profiles require an explicit `IEvidenceExecutionEnvelopeVerifier` registration. The verifier should validate protected CI inputs and return no secret material. An accepted v1 result produces `ValidatedNotAttested`, so release automation must describe it as validated CI context, not independently attested provenance.

```csharp
registration.SetEnvelopeVerifier(githubActionsEnvelopeVerifier);
```

Run `appsurface evidence doctor` from the protected release workflow first. A local release run reports `blocked`; it does not guess a release envelope.

## Diagnose before you rerun

| Signal | Meaning | Next action |
| --- | --- | --- |
| `ready` from `doctor` | Policy and currently selected prerequisites are available. | Run `explain`, then `run`. |
| `ready_with_external_prerequisites` | The policy is valid but the CI image must provide Docker or browser capability. | Fix the runner/image; do not treat it as a pass. |
| `blocked` | A policy/diff/envelope condition prevents a truthful run. | Read the named diagnostic and fix the source condition. |
| `ClaimKind.None` | A producer, obligation, artifact, or envelope did not complete. | Read `evidence-summary.json`; do not lower a gate blindly. |
| `ObservationOnly` | A useful signal that cannot satisfy a gate. | Use it for discovery, then define the missing profile/producer before enforcement. |

## Safe extension rules

- Keep policies checked in and review `no-evidence` rules like any other risk exception.
- Version producer behavior and assertion ids when their meaning changes.
- Preserve the exact plan and manifest as CI artifacts; use `appsurface evidence verify` before a downstream consumer trusts them.
- Use existing coverage exclusions for known generated sources. Do not classify arbitrary low-value lines with a hidden heuristic.
- Keep test profiles, browser binaries, containers, credentials, threshold values, and release policy owned by the consumer environment.

Read next: [EvidenceHost start here](../start-here/evidencehost.md), [planner reference](../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md), and [coverage gate reference](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate).
