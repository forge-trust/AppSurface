# EvidenceHost: risk-mediated CI without coverage theater

EvidenceHost is AppSurface's contract-first approach to CI evidence. Its purpose is simple: make a meaningful change visibly prove the risk it introduces, while allowing an explicitly low-risk change to pass without someone inventing tests for incidental lines.

It is not a replacement for a test framework, coverage collector, GitHub Actions, Aspire AppHost, Docker, or a hosted coverage dashboard. It starts no infrastructure by default and has no outbound telemetry.

## The first five minutes

Install the [AppSurface CLI](../Cli/ForgeTrust.AppSurface.Cli/README.md), then create the deliberately small policy starter:

```bash
appsurface evidence init --sample
appsurface evidence doctor --path docs/README.md
appsurface evidence explain --path src/Orders/SubmitOrder.cs
```

The generated policy maps `docs/**` to an explicit `no-evidence` profile and sends every unmatched path to a non-empty conservative coverage profile. `explain` tells a developer, before tests start, which profile won, which rule selected it, which producers must run, and which obligations they must close.

```text
Evidence plan: targeted-coverage (targeted)
Why: conservative:targeted-coverage
Changed paths: src/Orders/SubmitOrder.cs
Obligations: changed-behavior-covered
Required producers: coverage
Required resources: none
```

That is the desired “quiet when right, obvious when wrong” interaction: a docs-only PR does not chase coverage; a behavior-bearing change states its required evidence up front.

## What a claim means

EvidenceHost has four honest outcomes:

| Claim | May gate? | Meaning |
| --- | --- | --- |
| `TargetedComplete` | Pull request | Every selected producer passed and closed every selected obligation. |
| `ReleaseComplete` | Release only | The release profile completed and an explicit CI envelope verifier accepted the run. v1 is still not independently attested. |
| `NoEvidenceRequired` | Pull request | A checked-in, explicit empty profile matched the changed paths. It never means “tests were skipped.” |
| `ObservationOnly` | No | A useful diagnostic run that cannot satisfy a gate. |

Anything else has `ClaimKind.None`: a filtered test suite, missing browser runtime, unavailable Docker dependency, timed-out producer, or unsatisfied assertion is incomplete evidence—not a partial pass. This is intentional. A repository-wide gate on an incomplete test profile is a configuration error, and EvidenceHost makes that condition visible instead of encouraging a misleading green badge.

## Choose the smallest path

| Situation | Use | Why |
| --- | --- | --- |
| Existing coverage command needs clearer scope and artifacts. | [`appsurface evidence`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence) | Policy planning, diagnostics, canonical plan/manifest, and GitHub summary. |
| Consumer has PostgreSQL, a browser, or a real E2E journey to prove. | [`ForgeTrust.AppSurface.Evidence.Aspire`](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md) | A separate explicit EvidenceHost waits for consumer-owned readiness, runs typed producers, and cleans them up. |
| You are writing a producer, external gate, or policy editor. | [`ForgeTrust.AppSurface.Evidence.Contracts`](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md) and [`Planner`](../Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md) | Stable contracts and deterministic diff-to-plan resolution. |
| You only need numeric Cobertura thresholds. | [`appsurface coverage gate`](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) | Keep the current private, local coverage gate; EvidenceHost does not hide its configured threshold. |

## Keep test and application hosts separate

An EvidenceHost is not a specialized production `AppHost`. Keep it in consumer test/CI composition and register every resource and producer explicitly:

```csharp
await using var host = EvidenceHostBootstrap.Create(plan, registration =>
{
    registration.AddResource(postgresReadiness);
    registration.AddProducer(orderSubmissionBrowserE2e);
});

var manifest = await host.RunAsync();
```

There is no assembly scanning, ambient resource discovery, hidden Docker provisioner, or automatic E2E selection. The policy is the control plane; the consumer's code owns what “ready” and “asserted” mean.

## What v1 deliberately does not decide

The first release does not classify arbitrary C# constructors, property accessors, generated code, or “trivial” lines as low value. A heuristic with false negatives would let a behavior-bearing change escape. Existing coverage exclusions remain the appropriate mechanism for known generated or excluded source. A future classifier must be versioned, observable, and conservative before it can alter a gate.

Similarly, v1 validates a registered release envelope but labels it `ValidatedNotAttested`. It does not claim artifact attestation, cross-job aggregation, Docker sandboxing, trend analytics, or an AppSurface-hosted dashboard.

## Read next

- [EvidenceHost cookbook](../guides/evidencehost-cookbook.md)
- [CLI evidence command reference](../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence)
- [Evidence contract reference](../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md)
- [Aspire lifecycle reference](../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md)
