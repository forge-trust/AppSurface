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
