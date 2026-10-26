# Original-source OCI entry budget disposition (#887)

Status: the approved source-only ceiling is implemented in the private proof and the new package source; final-image limits remain unchanged. The [approved fixture card](issue-887-feasibility-card.md) keeps the final-image reader at 128 MiB. The [implementation patch](https://github.com/forge-trust/AppSurface/blob/7bded81f8ae4ff76c2c524e1d9a3e9ab50d93927/docs/designs/issue-887-source-oci-budget-proposal.patch) is a reviewable private-draft adaptation, not a supported API commitment.

Original upstream manifest/config/layer bytes were recovered using only the already-pinned identities and verified SHA-256/descriptor sizes. Neither a synthetic archive nor extracted source files can substitute for this graph verification. Acquisition completed in 9.407 seconds; the private receipt is `.gstack/tmp/issue-887/prepared-inputs/original-source/receipt.json`.

The actual managed original-source proof built with zero warnings/errors, then rejected the first source archive at raw tar preflight before selected output publication. Its observed members are:

| Platform | Original source member | Actual bytes | Current ceiling |
| --- | --- | --- | --- |
| linux/arm64 | `/usr/lib/jvm/java-21-openjdk-21.0.12.1.1-1.2.el9.aarch64/lib/modules` | 155,514,630 | 134,217,728 (128 MiB) |
| linux/amd64 | Corresponding original JDK `lib/modules` member | 155,560,314 | 134,217,728 (128 MiB) |

Approved disposition: use a distinct 256 MiB (268,435,456 byte) regular-member ceiling only while verifying/capturing the original Keycloak source OCI. Final runtime/output inspection retains 128 MiB. Archive replacement retains 64 MiB per expanded entry. All metadata, aggregate input/output, layer, selected-copy, memory and deadline ceilings remain unchanged. The large original JDK member is hashed as a stream, never retained as an in-memory payload or copied into the Keycloak application tree.

The approved human disposition was: “source-only OCI per-entry ceiling 256 MiB (NOT192).” The patch was applied once to the private draft, with exact-boundary/one-over streaming controls and constructor-limit validation. Approval does not establish passing proof, authorize a package release, change source/runtime pins, or waive the amd64 browser gate. The historical 192 MiB proposal wording is retained only in the patch review context and was not applied.

The two historical emulated amd64 browser readiness attempts failed the retained 60-second loop. The unchanged full gate later passed on the native amd64 GitHub-hosted runner in [run 40079400914](https://github.com/forge-trust/skoolit/actions/runs/40079400914), against the exact retained image. Neither the timeout nor any browser or upgrade assertion was weakened.
