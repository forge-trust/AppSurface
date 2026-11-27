# EvidenceHost Cookbook

The repository validation tool has two distinct uses. Its [direct package recipe](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#run-the-direct-package-journeys) validates a supplied package outside EvidenceHost and writes `native-validation-receipt.json`, with no EvidenceHost claim or gate authority. AppSurface's [policy-backed local and CI workflow](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#local-development) selects changed-path coverage, package checks and native acceptance through the checked-in policy and emits the manifest used by the repository evidence gate.

Its [shared local and CI policy recipe](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#local-development)
shows the full changed-path selection loop: real PR diff, checked-in policy, selected coverage, package and native producers,
retained plan and artifacts, and a gate that verifies the final manifest. The local command also includes
staged, unstaged and untracked edits and skips native packing when it is unnecessary. Mixed profiles
combine their required producers rather than forcing the full conservative set. Use `--scope coverage`
or `--scope packages` to reproduce one CI lane locally; default `all` verifies the complete selection.

This cookbook turns the [EvidenceHost guide](../start-here/evidencehost.md) into concrete CI patterns. The goal is not a higher coverage number. The goal is a visible, truthful claim that the changed risk was mediated.

## Direct package validation

Use the [direct `--package` recipe](../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md#run-the-direct-package-journeys) when the question is whether a particular packed CLI package passes its fixed storage or installed-consumer journeys. It emits a digest-checked `native-validation-receipt.json`, not an EvidenceHost manifest; its `evidence-plan.json` describes the recipe but does not grant a claim. Use the policy-backed `--operation local` or `run` path when the result must satisfy the repository's changed-path manifest gate.

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

## Tests-only evidence for tooling paths

Some paths have no production code to cover: CI workflows, build scripts, release tooling. A coverage number for them is theater. The truthful evidence is that the tests which exercise them all ran and passed. The preview [`test` producer](../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#test-producer-preview) records exactly that and makes no coverage claim.

1. Name the test project that exercises the path. If no such test exists, write it first; this recipe cannot create evidence that is not there.
2. Add the profile and rule from the [first-run fragment](../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#first-run), replacing the project path and the rule pattern.
3. Run it once with no filter and read the counts table:

   ```bash
   appsurface evidence explain --path scripts/build.sh
   appsurface evidence run --path scripts/build.sh
   appsurface evidence verify TestResults/evidence/evidence-manifest.json
   ```

4. Set `minimumTests` to the discovered count the table reports, so a later change that silently selects fewer tests fails.
5. Before the route replaces broader evidence, replay one known regression in that path and confirm a selected test fails. The producer proves the selection ran; only a replay shows the selection is the right one.

Keep the path on the conservative profile instead when the tests return early on some platforms, when the project needs a database or browser (use a [resource-backed EvidenceHost](#resource-backed-browser-e2e) with your own producer), or when the project multi-targets or uses Microsoft.Testing.Platform. The [reason table](../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#reasons) lists every way a run can stay incomplete and what to do next.

Pitfall: upgrade every CLI that runs or verifies evidence before a `test` block enters a shared policy. An older CLI ignores the block and cannot verify plans written with it; see the [upgrade note](../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#upgrade-note-and-mixed-versions).

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

### HTTP readiness in an ordinary Aspire test

For one profile-based AppHost test, prefer the [typed readiness fixture](../Aspire/ForgeTrust.AppSurface.Aspire.Testing/README.md#http-readiness-fixture). It composes the existing `CreateAsync` or `CreateConfiguredAsync` builder with native resource health, one named endpoint, and one status-only HTTP probe. The fixture owns the returned builder and application lifecycle; `Application` and `Endpoint` are borrowed for assertions. Do not build or dispose the borrowed application separately.

The [Aspire AppHost example's ordinary native readiness theory](../examples/aspire-apphost/README.md#deterministic-profile-test) shows a different composition: it builds the typed `QaProfile` with `AppSurfaceAspireTestingBuilder`, waits for Aspire's native resource health, resolves the named endpoint, and calls the standalone `HttpReadinessProbe`. It does not use the typed readiness fixture above. The test emits no EvidenceHost manifest or claim; its status check proves only the declared readiness condition and the test separately checks application/client disposal. The example also retains a separate [EvidenceHost lifecycle regression](../examples/aspire-apphost/README.md#evidencehost-integration-regression) for the readiness adapter and producer claim.

Use [Aspire's native health wait](https://learn.microsoft.com/dotnet/aspire/testing/accessing-resources) alone when resource health is enough. Use [native Aspire HTTP health checks](https://aspire.dev/reference/api/csharp/aspire.hosting/resourcebuilderextensions/methods/) when their configured endpoint/status condition already meets the test's need. The shared [standalone readiness probe](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#httpreadinessprobewaitasync) is for an already-owned `HttpClient` and endpoint. None of these status checks replaces a body assertion, browser journey, or business readiness predicate.

The complete [.NET 10 standalone package consumer](https://github.com/forge-trust/AppSurface/tree/main/tests/evidence-readiness-consumer) and [typed fixture consumer](https://github.com/forge-trust/AppSurface/tree/main/tests/evidence-readiness-typed-consumer) are the canonical executable quickstarts; the [consumer verification script](https://github.com/forge-trust/AppSurface/blob/main/scripts/verify-evidence-readiness-consumers.sh) runs their package proofs. Follow the projects there for exact coordinated package versions, setup, commands, and recorded output rather than copying a second sample into this cookbook. Controlled-handler output proves package behavior; it is not a live endpoint or real Aspire resource result.

For an EvidenceHost resource, keep the application lease and browser producer in the existing explicit evidence composition. `EvidenceAspireApplication.CreateHttpReadiness(...)` binds the declared Evidence resource id to one Aspire resource, endpoint, and probe path. Each readiness wait first observes native resource health, then resolves the endpoint and runs the shared HTTP condition with an independent owned client. Register the adapter once; its cleanup drains admitted waits and releases its own application registration. Other adapters retain the same application until their registrations also release; the final release joins application cleanup. Dispose all created adapters, and do not dispose the lease directly concurrently with an active readiness wait. See the [Evidence.Aspire adapter reference](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md#http-readiness-adapter) and the coordination record for [issue #778](https://github.com/forge-trust/AppSurface/issues/778).

Both pinned Skoolit gate equivalences preserve their existing consumer policy: three-minute startup deadline, `MaxAttempts = 30`, three-second request timeout, 250 ms retry delay, `UseProxy = false`, and two-second `ConnectTimeout`. These are explicit migration settings, not SDK defaults. Equivalence fixtures are not actual Skoolit adoption, and headers-only probing does not buffer or assert response bodies as a prior probe might. No latency or reliability improvement is claimed without measurements.

## Release evidence

Release profiles require an explicit `IEvidenceExecutionEnvelopeVerifier` registration. The verifier should validate protected CI inputs and return no secret material. An accepted v1 result produces `ValidatedNotAttested`, so release automation must describe it as validated CI context, not independently attested provenance.

```csharp
registration.SetEnvelopeVerifier(githubActionsEnvelopeVerifier);
```

Use [`appsurface evidence doctor`](../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#doctor-preflight) to check the release policy, diff, and selected runtime prerequisites. The envelope check reports `unverified` in every environment. Validate the envelope through the registered verifier in the protected release workflow.

## Diagnose before you rerun

| Signal | Meaning | Next action |
| --- | --- | --- |
| `ready_with_external_prerequisites` from `doctor` | The policy and diff resolved. The envelope is unverified, and selected Docker or browser prerequisites may still be missing. | Read each check to prepare the run. Gate releases on a `ReleaseComplete` claim from the [protected workflow](#release-evidence) with its registered verifier. |
| Nonzero exit from `doctor` | The command could not resolve the policy or diff. | Read the diagnostic and fix the input. |
| `ClaimKind.None` | A producer, obligation, artifact, or envelope did not complete. | Read `evidence-summary.json`; do not lower a gate blindly. |
| A `reason=` line from a `test` producer | One declared test project is incomplete. | Follow the `next:` line printed with it, or look the reason up in the [reason table](../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#reasons). |
| `ObservationOnly` | A useful signal that cannot satisfy a gate. | Use it for discovery, then define the missing profile/producer before enforcement. |

## Safe extension rules

- Keep policies checked in and review `no-evidence` rules like any other risk exception.
- Version producer behavior and assertion ids when their meaning changes.
- Preserve the exact plan and manifest as CI artifacts; use `appsurface evidence verify` before a downstream consumer trusts them.
- Use existing coverage exclusions for known generated sources. Do not classify arbitrary low-value lines with a hidden heuristic.
- Keep test profiles, browser binaries, containers, credentials, threshold values, and release policy owned by the consumer environment.

Read next: [EvidenceHost start here](../start-here/evidencehost.md), [planner reference](../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md), and [coverage gate reference](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate).
