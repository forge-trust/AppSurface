# ForgeTrust.AppSurface.Evidence.Planner

`ForgeTrust.AppSurface.Evidence.Planner` turns an explicit diff and a checked-in `EvidencePolicy` into one deterministic `EvidencePlan`. It is the boundary that makes a coverage or E2E gate explainable before work begins.

Begin with the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md). This package owns planning only: it does not invoke Git, start Aspire, run tests, create containers, or decide that missing evidence is acceptable.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Minimal use

```csharp
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

var planner = new EvidencePlanner();
var plan = planner.Resolve(policy, [new NormalizedDiffPath("src/Orders/SubmitOrder.cs")]);
```

`Resolve` normalizes and sorts paths, applies the most-specific matching rules, and falls back to `ConservativeProfileId` for a path that has no rule. If equally specific rules choose different profiles, it throws `EvidencePlanningException` instead of silently taking a lower-risk route. `EvidenceUnifiedDiffReader.Read(...)` accepts a CI-provided Git-formatted unified diff when callers do not want to depend on a local Git checkout. Hunked diffs must include `diff --git` file headers; use explicit paths when only a non-Git diff is available.

## Policy design

Keep the policy small and explicit:

- map genuinely non-behavioral files to an empty `no-evidence` profile;
- map behavior-sensitive paths to producers that make a real assertion, such as coverage or browser E2E;
- choose a conservative non-empty fallback profile; and
- give every obligation one named risk rationale and assertion id.

The planner does not classify C# semantics or infer that a getter, constructor, or generated line is low value. That belongs in a future, separately versioned behavior classifier; v1 refuses to pretend an unimplemented heuristic is trustworthy.

## Internal closed application catalogue prerequisite

[`EvidenceClosedApplicationCatalogue`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceClosedApplicationCatalogue.cs) defines immutable metadata for a future restricted Aspire application adapter. The production table is empty. `Resolve(policy, plan, binding)` therefore rejects with `ASEVD407`; it does not start an application or change consumer-proof acceptance. The public application factory remains rejected as described in the [Aspire integration reference](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md). This prerequisite supplies neither a supported Trusted-positive path nor native/systemd acceptance; all required acceptance groups remain required.

The first definition shape is a native HTTP resource with `native-http-uds` capability version `1.0.0`, `aspire_health` readiness, coverage producers version `1.0.0`, and Aspire SDK **13.4.4**. It permits a private network, declared immutable input and bounded scratch, memory, tasks, output, startup and stop time. Secrets, privileged groups and protected/result-root projections have no grant in this shape. Later adapters must preserve real authenticated Connect, admission, allocation and supervision.

### Metadata API and exact matching

| Internal model/API | Contract |
| --- | --- |
| `EvidenceClosedApplicationDefinition` | Application ID/version/build ID, entire protected policy, selected profile, complete resource/producer registrations, bundle inventory and capability grants. No delegate, absolute host path or argument vector. |
| `EvidenceClosedResourceRegistration`, `EvidenceClosedProducerRegistration` | Complete policy declarations plus closed capability/implementation identity. Readiness, dependencies, timeouts, assertions, artifacts and coverage gates all participate in matching. |
| `EvidenceClosedBundleFile` | Normalized ASCII relative name, closed role, positive exact length, lower-case SHA-256 and Unix permission bits. DLLs, runtime configurations, DCP, extensions, dependencies, `.deps.json` dependency manifests and declared input are explicit inventory. |
| `EvidenceClosedApplicationCapabilities` | Exactly one declared input; scratch/memory each at most 1 GiB, process/thread tasks at most 128, output at most 1 MiB, startup at most 120 seconds and fresh stop at most 30 seconds. Every allowance is positive. |
| `EvidenceClosedApplicationBinding`, `EvidenceClosedApplicationIdentities` | Observed provider `github-actions`, platform `linux-x64`, proposed protocol `evidence-worker-linux-v2`, digests, complete registrations/inventory/grants and three positive distinct UIDs plus five positive distinct GIDs. These values cannot populate the compiled table. |
| `Snapshot`, `ComputeEntryDigest`, `ComputeCatalogueDigest`, `VerifyCandidateBinding` | Pure bounded metadata audit. A matching candidate cannot enroll itself or create admission, context, proof or a runtime capability. |
| `Resolve` | Selects only a compile-owned entry and verifies complete correspondence. Its result is read-only definition metadata, not an execution lease. |
| `CreateBinding(descriptor, policy, plan)` | Copies authenticated v2 application metadata into the complete typed comparison shape. It maps all nine bundle roles explicitly, checks SDK 13.4.4, snapshots declarations/grants and preserves the descriptor's actual provider/platform/schema and identity values. It grants no registration or admission. |

The task allowance counts processes and threads in the owned application group, including AppHost, DCP, resource processes and their runtime threads. Its structural range is 1–128; zero or 129 rejects. The selected allowance remains part of the canonical entry/catalogue digests and exact descriptor comparison, so a larger allowance requires matching compile-owned metadata rather than a runtime override.

Snapshots copy and wrap every policy/declaration list, registration list, bundle inventory and input list. Entry digests cover the complete snapshot, including unselected profiles and rules. Catalogue digests cover the fixed `evidence-closed-application-catalogue-v1` schema and entries ordered by ordinal application ID. Bundle files, registrations and inputs are ordered for matching; nested policy list order is retained and exact. The supplied plan must equal a fresh planner resolution of its changed paths against the complete protected policy.

Bounds are eight catalogue entries, 256 bundle files, 128 MiB per file, 512 MiB total file bytes and 1 MiB canonical entry metadata. Policies are bounded to 32 profiles and 128 rules with the existing [profile declaration limits](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs). Duplicate application IDs or policy/profile bindings, duplicate declarations, case-aliasing/path-prefix bundle collisions, traversal, writable files and any declaration drift reject. Inventory permissions are `0444`, or `0555` for DCP/native extensions; DCP and its extensions require executable `0555`. Invalid or mismatched metadata produces a fixed `ASEVD404` diagnostic without echoing supplied values.

The descriptor overload `Resolve(policy, plan, descriptor)` performs this mapping and then selects only an immutable compiled registration. [`EvidenceProtectedWorkerInputs.CreateContext`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceProtectedWorkerInputs.cs) uses that overload for the [restricted application protocol v2](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#internal-restricted-application-protocol-v2-prerequisite). Its capability digest includes the entire matched application definition, all grants and worker/producer/application/results/resource-access identities; its catalogue digest is the independently matched compiled catalogue digest. V1 retains its existing resource/producer binding. A malformed v2 descriptor rejects with `ASEVD404`; an otherwise matching candidate still rejects with `ASEVD407` while the production table is empty. Parsing metadata or passing `VerifyCandidateBinding` cannot produce a context through this path. Actual root bundle inspection, supplementary-group enforcement, application startup/readiness and accepted consumer proof remain separate requirements.

### Compile-owned fixture registration and pending integration

A separately compiled controlled-release fixture may include the production catalogue source and one same-assembly partial source containing reviewed literal definitions:

```csharp
using System.Collections.Generic;

namespace ForgeTrust.AppSurface.Evidence.Planner;

internal static partial class EvidenceClosedApplicationCatalogue
{
    static partial void RegisterCompiledEntries(
        List<EvidenceClosedApplicationDefinition> entries)
    {
        entries.Add(FixtureDefinitions.NativeHttp); // Immutable compile-owned data.
    }
}
```

`FixtureDefinitions.NativeHttp` is a prerequisite fixture definition, not a shipped registration. The fixture must be a distinct build artifact with its finished binaries and source binding pinned by the protected root parent. A partial class in a separate referencing assembly cannot modify the production table. No runtime file loader, environment switch, public Boolean, supplied factory, reflection or additional friend access may register entries. Metadata audit methods are never a substitute for `Resolve` in the future adapter.

The root parent still must independently inspect retained bundle bytes, ownership, modes and hashes; authenticate the worker channel; verify actual UID/GID and supplementary-group membership; and enforce capabilities. The root v2 handler and separately restricted Aspire child/resource adapter are pending integration. This slice does not alter the production Trusted allowlist or proof resolver. A separately compiled immutable proof registry and real shared-path positive execution remain prerequisites for controlled-release acceptance; matching structural metadata alone is insufficient.

## Failure and recovery

| Diagnostic | Meaning | Recovery |
| --- | --- | --- |
| `ASEVD105` | Conservative fallback points at an empty profile. | Choose a non-empty profile that genuinely mediates unknown changes. |
| `ASEVD117` | Same-precedence rules selected different profiles. | Add an explicit precedence or remove the overlap. |
| `ASEVD118` | A supplied changed path is not normalized. | Use a repository-relative forward-slash path without `.` or `..` segments. |
| `ASEVD121` | An identifier is empty or exceeds 128 characters. | Use a stable identifier up to 128 characters. |
| `ASEVD111`, `ASEVD124` | A producer or resource requires an undeclared resource. | Declare every required resource in the same profile. |
| `ASEVD128` | A hunked unified diff does not include Git file headers. | Supply a Git-formatted diff or explicit changed paths. |

Read next: [contracts](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md), [CLI workflow](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md), and the [policy cookbook](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md).
