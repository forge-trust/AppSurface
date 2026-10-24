# Issue 887: observed fixture mismatch


> Current disposition (2026-10-24): the owner accepted the exact historical policy and 256 MiB source-only OCI member limit. Both source graphs and all four final images pass retained static/OS/scan proof; the complete retained system gate passes on arm64 and native amd64 CI. The [fixture card](issue-887-feasibility-card.md#native-amd64-retained-full-gate--passed-2026-10-24) contains the final run. Earlier failures and pending decisions below are chronological evidence and have been superseded where that card records a passing result. Public implementation and validation are in progress; published-byte adoption remains a separate authorized release gate.

Status: owner approved the explicit Micrometer inventory correction on2026-10-23; full recipe proof remains in progress. The failed unchanged-fixture observations below are retained as history.

The [approved plan](issue-887-verified-keycloak-security-recipes.md) requires original identity/path verification and forbids silently accepting unexpected originals. This check failed for the Micrometer group in the current [fixture card](issue-887-feasibility-card.md). No passing recipe evidence or public API commitment is made.

## Immutable source and observations

- Skoolit source revision: `6623ffe5dd6f3eb454df609e1074407e0ae2e446`.
- Source index: `sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669`.
- Actual source platform manifests: arm64 `sha256:20874855592d23bc145f5e8b3ede829012014109905363f6c3eea7340fb708ba`; amd64 `sha256:eb81d22bb82bc358ee10bdd4ab15dea2caae0add01416a1b146e2c7c7eb9cfab`.
- Both raw manifests, config digests, config fields and ordered rootfs diffIDs were verified before copying source bytes; both platforms have the same selected module bytes.

| Module | Dockerfile destination absent from both source images | Actual source file, already equal to requested replacement bytes | SHA-256 |
| --- | --- | --- | --- |
| `micrometer-commons` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-commons-1.16.3.jar` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-commons-1.16.6.jar` | `45aff76226830db257f4bc39a5bcff83d633e572fee9dc4e45cfa12af9a0a49a` |
| `micrometer-core` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-core-1.16.3.jar` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-core-1.16.6.jar` | `1957ef2deaffbc1fb98cebfd0f5b3109ecf19c994d7e5378598c20747ba8d68b` |
| `micrometer-observation` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-observation-1.16.3.jar` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-observation-1.16.6.jar` | `4c0826d5e7c8522a8e111470b4e77f77b0a10f520eb5502ddd143fdbcedb2340` |
| `micrometer-registry-prometheus-simpleclient` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-registry-prometheus-simpleclient-1.16.3.jar` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-registry-prometheus-simpleclient-1.16.6.jar` | `5f665369724caa8ffbb3f83a492b02d1de17a1420758e4a55debcb6ee2bd5e2a` |

The checked-in Dockerfile writes Micrometer1.16.6 bytes to old1.16.3 filenames. The pinned image already contains the complete1.16.6 group under1.16.6 filenames. Repeating the old paths would add redundant JARs rather than replace the declared originals. This is a source-fixture identity mismatch, not evidence about vulnerability freedom or runtime behavior.

The remaining25 declared final JAR destinations exist in both platforms. All28 distinct requested downloads match their pinned digests. The actual embedded CLI in both images matches the independently downloaded Maven CLI bytes, so the completed archive-byte feasibility is applicable to that CLI source.

## Recommended correction for owner review

Keep the exact approved source/runtime pins and replacement versions. Change the four Micrometer entries to explicitly verify the already-aligned1.16.6 group at its actual1.16.6 paths and record that no Micrometer payload change was needed. Do not add the four unused1.16.3 paths. Keep every other artifact/runtime/archive operation and retained gate. This corrects the accepted migration inventory; it does not create a new handler family or select a new security pin.

Alternative: the owner supplies a different approved source digest whose originals actually match1.16.3, then rerun original identity and all family/platform proofs. Do not follow the moved floating tag or silently pick another digest.

## Other observed proof and limits

- Private .NET archive: two equal outputs; independent Python verifies manifest,2926unrelated and1038replacement entries;9rejection controls pass.
- Private .NET capture: workspace edits isolated; fresh snapshot tamper rejected; verified private copy unaffected by later path mutation; exactly one of two publication racers commits.
- Neutral Java execution observed2.21.5 before and2.21.7 after; wrong-version negative control exits1. Full neutral recipe proof is not complete.
- The optional JDK jar validator reports FastDoubleSwar versioned-API inconsistencies in the original stock CLI and standalone replacement core. This check is not passing; its source/output applicability and the neutral source versioned-entry fixture need resolution before any archive support claim.
- Arm64 signed Ubuntu snapshot acquisition verified inherited signatures without expiry/trust bypass and downloaded all three exact OpenSSL packages. amd64 acquisition and offline installation/output inspection remain pending.
- Named owner for all required roles: @akrock by explicit user reply on2026-10-23. Publication is not authorized.

All observations are preliminary feasibility. Actual image conformance, scan, CLI/render/login, database upgrade/restore, package closure, solution coverage and the draft PR remain required.

## Decision recorded

On2026-10-23 the human owner selected “Use verified1.16.6paths (Recommended)”. Apply the recommended correction above, keep all other pins/operations/gates, and preserve rejection of the uncorrected expected1.16.3originals.

## Additional observed feasibility constraints

The compiled signature proof verified all eight captured InRelease copies and rejected two independent tamper/signer controls. A managed .NET signed-metadata hash-chain proof verified all six exact OS package archives and rejected wrong package hash, coordinate and index digest. These results establish the recorded historical snapshot content; no current freshness approval is inferred.

The closed build proof uses captured, hash-verified local OCI layouts for both the frontend and runtime. Raw Docker can reuse cached OCI data despite a missing supplied layout. A compiled host verifier therefore checks every declared file and directory before invoking Docker; missing, altered, linked and undeclared input controls reject before invocation.

Two materializer defects were found and corrected privately: copying the whole rootfs changed /opt ownership, and file-only capture omitted empty runtime directories. The template now copies only /opt/keycloak and preserves all source directories/modes. Six clean final-context builds and smoke checks passed; the remaining Skoolit amd64 export failed with a containerd I/O error. This is incomplete required-platform proof.

The bounded OCI inspector compiles and passes ordinary/opaque whiteout order controls. Its actual image parse failed with no space left on device. The retained vulnerability scan has not run because the required10GiB free-space check is unsatisfied. Task-owned generated binaries and terminal context copies were removed, preserving source/input captures and all reports; about4GiB remained after cleanup. Complete bounded inspection, final-image live/browser/upgrade/scan gates, resource ratification and the implementation/coverage/draft-PR workflow remain outstanding. See the [measured observations](issue-887-feasibility-observations.json).

After the task-owned cleanup settled, the host had13GiB free. Docker still returned HTTP500/ping errors, and a fresh bounded info probe timed out after15seconds. Restart authorization was requested because other local containers may be interrupted; no restart or shared cache prune has occurred. Offline hardening now enforces an8MiB decoder window and raw tar metadata preflight. All six package cases still pass; complete final-image inventory and retained gates remain pending.

## Offline inventory follow-through

The full OS inventory check accounted for every regular payload file on both retained intermediate images. Arm64 has247regular payload files:15exact file hashes and232explicit omissions under the unchanged captured base dpkg policy. Amd64 has248:16exact hashes and232explicit omissions. Both installed package lists match the complete expected change from140to138packages, with only the three selected version changes and two removals. This strengthens the OS operation proof on those exact intermediate digests; the final images still need verification after Docker recovery.

The JVM modules file is101048209bytes, exceeding the private OCI parser default64MiB entry cap. The OCI profile now provisionally uses128MiB per file while streaming; archive-repair limits remain as recorded. Profile/resource acceptance remains pending. See the [measured observations](issue-887-feasibility-observations.json) for the bounded offline content-inventory result and its scope.

## Preliminary packaged dependency closure

A compiled consumer outside the checkout restored the private local prototype and exactly BouncyCastle.Cryptography2.7.0 and ZstdSharp.Port0.8.8 from an owned local feed into an empty owned cache. Restore and execution both exited0. Packaged managed code exercised deterministic archive repair and manifest preservation, all29context targets and captured runtime/frontend inputs, preserved empty directories, real signature verification, all six OS payloads, bounded OCI content inventory, and wrong-input rejection. The measured restore/run time was9.614seconds; this is a warm local-feed packaging measurement, not the prepared cold onboarding clock. No source code or ambient global NuGet cache supplied runtime dependencies.

The initial preflight incorrectly compared signed archive SHA512 to NuGet normalized contentHash; official signature/content-hash verification and separate archive byte hashes resolved that mismatch. A subsequent post-check incorrectly required a trailing path separator; canonical path validation confirmed the sole owned package cache and local feed. Failed harness attempts remain recorded alongside the actual successful restore/run output. See [NuGet content-hash semantics](https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File) and the [official verifier](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify).

This private unsupported package was never published and commits no supported SDK API. It does not satisfy the post-publication byte-consumer gate or current final-image conformance. A fresh read-only Docker check still timed out after15seconds with11689492480freebytes; restart authorization remains pending because it may interrupt unrelated container work.

## Docker recovery and current final-image results — 2026-10-24

Docker recovered and the interrupted two Skoolit amd64 builds completed. The closed-input run now has eight successful builds and runtime smoke checks, two per fixture/platform. Regenerated captured contexts have the same receipt hashes as the prior six successful builds. No restart or shared cache prune was performed by this agent.

The bounded compiled OCI inspector verified all four attempt-one final manifests/configs, ordered layer digests, merged-rootfs files, directory modes and ownership against those contexts. Independent signed-package inventory checks read every actual final image: arm64 has247regular payload files with15exact hashes and232explicit captured-policy exclusions; amd64 has248with16exact hashes and232exclusions. All four installed package inventories match the complete140-to-138expected change. The wrong-diffID control rejects without a report.

All four final images passed Trivy0.70.0 under the unchangedHIGH/CRITICALOS+library policy and empty vulnerability ignore list: raw and blocking runs exited0 with zero selected findings. The vulnerability and Java databases were updated on2026-10-24. This records that observation, not future vulnerability freedom.

Two clean Skoolit arm64 images differ in build/config timestamps, directory modification times, `/var/log/dpkg.log`, and `/var/cache/ldconfig/aux-cache`. Every copied Keycloak regular payload is equal. Archive determinism is preserved; whole-image byte reproducibility is not promised.

The retained full gate has an integration conflict: `--all` and `--upgrade` unconditionally call `run_image`, which rebuilds the legacy Dockerfile and replaces the supplied image tag. A previous successful upgrade run therefore certified a different image. Its result cannot certify the captured-plan image. The [proposed prebuilt-image input](issue-887-skoolit-prebuilt-gate-proposal.patch) validates the expected platform image ID, binds subsequent checks to that immutable ID, and preserves default legacy builds and every existing inspection/browser/upgrade assertion. Four focused control cases and syntax/dry-run checks pass; actual adaptation awaits owner disposition. Exact retained-function proofs are recorded separately and do not claim a byte-unchanged `--all` pass.

## Exact system-gate outcome — 2026-10-24

The retained isolated-browser/accessibility gate passed on final Skoolit arm64 manifest`sha256:b0061d58c10543370ec6f3d15fff7a67116c6bcd21468c37b6a80f751270ef79`, exit0in100492ms. The exact-image retained-function upgrade/restore attempt did not produce a passing terminal result. The worker reported reaching candidate/restore phases and a failure at the function-level`verify_durable_state`ERRtrap; the precise inner assertion remains unisolated. Early private harness setup failures and overlapping attempt logs limit diagnosis. A stale exit-code file is not independent proof of the final attempt's precise failure. Keep this classified as incomplete/failed required proof until a clean isolated run identifies the command and phase. The source gate and image pins remain unchanged.

A separate actual full-conformance parser measurement used macOS`/usr/bin/time -l`:79,249,408bytes maximum resident memory and3.263seconds wall time on a300,256,768byte local final-image export. This warm measurement ran alongside container proofs; it does not establish cold onboarding, aggregate SDK bounds, native Linux performance or a concurrency guarantee.

The final proof index records SHA256digests for each current report. Supported API commitment, production implementation, enhancement, solution coverage and draft-PR publication have not started. Complete runtime/system feasibility and the explicitly requested gate/policy owner dispositions remain required by the accepted plan. No scan threshold, package pin, retained assertion or publication safeguard has been waived.
