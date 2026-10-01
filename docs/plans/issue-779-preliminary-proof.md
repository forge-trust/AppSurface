# #779 preliminary proof PR scope

This staged scope implements the user-selected route **A** from 2026-10-01. The
[full approved design](../designs/issue-779-evidencehost-trust-boundary.md),
[57-group acceptance plan](issue-779-evidencehost-test-plan.md) and
[execution record](issue-779-execution.md) remain the complete #779 objective.
This PR references #779 without closing it.

## Deliverables

1. Count actual policy, plan and manifest bytes under the shared 20 MiB ceiling before parsing.
   Reject ambiguous properties, invalid enums, missing/null required members, null collection items
   and unsupported plan/manifest versions with safe diagnostics. Preserve canonical serialization,
   nullable defaults, additive fields, stream ownership and cancellation behavior. See the
   [Contracts reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input).
2. Report protected execution facts as `unverified` in planning doctor for every profile and CI flag.
   Structural planning remains available without running resources/producers or creating a manifest.
3. Submit the provisional Linux allocation and independent-supervision fixtures to the
   [candidate workflow](../../.github/workflows/evidencehost-mechanism-proof.yml). Capture immutable
   run/revision/artifact identifiers from an actual Ubuntu run before selecting the public provider seam.
4. Resolve the release-route visual drift found during coverage diagnosis. The initial five-snapshot
   repair is superseded by the newer baselines merged from `main`; preserve its current release content
   and the existing comparison thresholds. The preliminary diff now changes no visual snapshot.
5. Pass the unchanged [coverage gate](../evidence/issue779-coverage-gate.md), scoped reviews, formatting,
   CLI planning QA and isolated packed CLI/SDK checks before creating a draft PR.

## Verification and expected outcomes

| Surface | Check | Expected outcome |
| --- | --- | --- |
| Contracts and callers | CLI Evidence unit/integration filter, including JSON input and shape cases | All cases pass; numeric/unknown enum input rejects, established serialized bytes remain unchanged. |
| CLI planning | Installed package help, sample initialization, doctor with CI flag, explain, invalid policy | Correct exit/output and owned files; doctor remains unverified, malformed input returns `ASEVD205`, no canary disclosure. |
| Packed SDK | Exact package version from isolated feed; counted nonseekable input, round-trip, required/null/schema failures | Public APIs execute from the package with correct values and exception categories, without project references. |
| Python fixture verifier | `python3 -B tests/evidencehost-consumer/test_linux_proof.py` | Complete Boolean observation map passes; missing/extra/wrong/false/truthy values reject. |
| Linux allocation | Combined script on disposable Ubuntu 24.04 | All 14 allocation cases are observed with their fixed expected outcomes; unsupported/missing controls fail. |
| Linux supervision | Combined script on disposable Ubuntu 24.04 | PID 1 arms identity/deadline/cgroup controls before callback activation; normal and cooperative completion markers match; stalled/fatal/descendant cases exit unsuccessfully with empty groups and no finalization. |
| Coverage | `./scripts/coverage-solution.sh` | Exit zero with unchanged aggregate and committed-patch gates against `origin/main`. |

The actual Ubuntu workflow run follows creation of the preliminary PR under the approved staging order.
Local unit, container and macOS observations cannot stand in for that run. See the
[consumer acceptance record](../evidence/issue779-consumer-acceptance.md) for the current excluded platforms.

## Continuation after runner proof

Integrate shared admission/claim capability, CLI/Aspire explicit modes, owned lifecycle and fatal ordering,
retained-handle writer hashing, aggregate artifact/output caps, job-time reserves, protected downstream
verification and all remaining hostile/migration/packed acceptance groups. Then repeat the full Make It So
validation and update the draft PR for complete #779 closure. A preliminary PR or mechanism-only result
cannot complete the durable goal or enable Trusted support.
