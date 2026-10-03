# Private qualification decision for issue 779

## Why a decision is required

The admission contract rejects resource-backed Observation with ASEVD406. Production C# and root application catalogues are empty and reject selection with ASEVD407; accepted-consumer proof resolution remains false. Parser or metadata tests cannot grant the authority needed to execute the shared restricted application and producer path. A qualification rule is a policy change requiring a human decision under the make-it-so workflow.

Native run 37097164915 measured patch-line coverage at 3904/4514 (86.486486%), below the unchanged effective 94.5% minimum. It needs 362 additional fully covered changed lines. Three currently identified safe test candidates affect at most seven target lines; their preparation is proceeding independently. This does not establish that all other legitimate tests are exhausted or that qualification alone will pass coverage.

## Proposed authorization

Authorize one separately compiled private qualification variant. Its only admission exception would permit the exact compiled resource-backed qualification request to proceed past the Observation resource rejection (ASEVD406) and select its private compiled entry without claiming an already accepted consumer proof. Default Observation and Trusted rules remain unchanged in the production build. The private branch must force Eligibility=None and Claim=None, and never return a fabricated accepted proof or make the ordinary accepted-proof resolver return true.

The request must enter through the real `EvidenceHostBootstrap.RunAsync(request)` and protected CLI execution paths. Synthetic admission contexts, `RunSharedCoreForTestsAsync`, supplied application factories, local artifact writers, injectable transports and accepted-proof substitution are excluded. The private catalogue may contain only the separately compiled reviewed qualification entry; production catalogues, accepted-proof registry and Trusted allowlist remain empty.

Before execution, the qualification entry must bind and verify this finite immutable inventory:

- The exact source commit and all source SHA256/modes used to build the private variant.
- The exact GitHub repository, workflow file, workflow build SHA, run/head identity and protected tool assembly/package hashes.
- The complete canonical policy and re-resolved plan digests, including declaration, resource dependency and obligation metadata.
- Every bundle path, closed role, mode, byte length and SHA256; the application entry and complete catalogue digests.
- The fixed SDK/DCP versions, closed application/resource/producer implementations, all eight account/group identities, all seven capability bounds and the retained filesystem roots.

The genuine root-authenticated supervisor must be armed before any subject restore, asset execution, MSBuild evaluation, application startup or producer work. Selection must be compiled, immutable and unavailable through caller JSON, environment switches or public factories. These are implementation and pre-execution review requirements; no final qualification bundle or measured positive result exists yet.

The private qualification variant must execute the real shared CLI/Aspire coverage producer with an actual root-authenticated worker, separate application/producer accounts, actual kernel UID/GID/groups/cgroups, retained bundle and artifact handles, bounded capabilities and shared output/time budgets, real restricted AppHost/DCP/resource HTTP readiness, and aggregate stop/wait/join before collection. It cannot accept fabricated observations or a pre-labelled passed proof.

Qualification output must retain Eligibility=None and Claim=None and must not authorize Trusted execution or publication. This permission would authorize implementation and review of that narrow bootstrap rule, not automatic enrollment of the measured result into a production registry. A later immutable proof/registry decision remains separate if required by the final accepted-proof design.

## Current status

No qualification implementation or positive resource-backed claim has been made. The separate resource-free collector diagnostic is already authorized and uses unchanged limits/timeouts; it grants no qualification or Trusted authority. A human answer to the qualification proposal is still pending.
