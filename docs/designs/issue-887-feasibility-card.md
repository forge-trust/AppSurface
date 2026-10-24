# Issue 887 feasibility fixture card

Current status: the [atomic file publication correction](#atomic-file-publication-correction--2026-10-24) is a newer production change awaiting final candidate verification. The [integrated qualification v3](#current-integrated-qualification-v3--2026-10-24) remains retained production/sample proof for its earlier source checkpoint. Earlier sections preserve their historical pending/failure states.

Historical starting status: four final-image static/OS/scan checks pass; arm64 full retained gates pass and amd64 full retained gates reach upgrade/restore but fail isolated-browser startup readiness under emulation. Updated 2026-10-24. Public API commitment remained gated on complete system feasibility at that checkpoint.

This card records the current consumer inventory for the [approved captured-plan design](issue-887-verified-keycloak-security-recipes.md) and [source case #887](https://github.com/forge-trust/AppSurface/issues/887). Prototype work does not commit public API or supported profile claims.

Skoolit source revision: `6623ffe5dd6f3eb454df609e1074407e0ae2e446`. AppSurface base: `b8248167b187dc94c6cec41ad16194daa9edd9d8`. The independent neutral fixture and all final-image/system gates remain required.

## Required owners

| Responsibility | Named owner | Acceptance |
| --- | --- | --- |
| AppSurface pure library, supported profiles and security response | @akrock, named by user on 2026-10-23 | Fixture acceptance after complete proof |
| Skoolit recipe/material policy, signing keys and mirror/retention | @akrock, named by user on 2026-10-23 | Fixture/policy evidence pending |
| Inspector/runtime compatibility and supported platforms | @akrock, named by user on 2026-10-23 | Measured support remains gated |
| Shared package boundary conflict resolution with #888 | @akrock, named by user on 2026-10-23 | Shared pure package retained |
| Coordinated package release authorization | @akrock, named by user on 2026-10-23 | Separate explicit authorization still required |

## Immutable image inputs

| Input | Platform/index | Digest |
| --- | --- | --- |
| Keycloak 26.7.2 | index | `sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669` |
| Keycloak 26.7.2 | linux/amd64 | `sha256:eb81d22bb82bc358ee10bdd4ab15dea2caae0add01416a1b146e2c7c7eb9cfab` |
| Keycloak 26.7.2 | linux/arm64 | `sha256:20874855592d23bc145f5e8b3ede829012014109905363f6c3eea7340fb708ba` |
| Temurin 21 JRE | index | `sha256:7a65df4b22d2de92d4e04056e884f3b9122d70b21e2847fd66084278bd0ce037` |
| Temurin 21 JRE | linux/amd64 | `sha256:d1d7f43094ea37e9ae77ea3ca40026bc1de1bf1dc2cad37b08d66b9a016a2ea6` |
| Temurin 21 JRE | linux/arm64 | `sha256:21a40fbc3e7d46fa48487450a01c3fd998012fc2659dd955afeb17663142844f` |

Both immutable platform manifests/configs and source trees were captured and verified. Both signed snapshot acquisitions and offline runtime proofs passed. Complete final-image/closed-input inspection remains pending; see the [measured observations](issue-887-feasibility-observations.json).

## Complete artifact migration inventory

All 28 distinct Maven downloads (29 ADD destinations) were acquired from the exact HTTPS Maven Central origin and matched the checked-in SHA-256. This is acquisition evidence; designated host-policy approval and final-image observations are still pending. Original internal identities/digests will come from the captured platform images, not inferred filename versions. The two Jackson temporary inputs also feed their server destinations and the embedded CLI transformation.

| Exact coordinate | Replacement SHA-256 | Existing destination retained |
| --- | --- | --- |
| `com.fasterxml.jackson.core:jackson-core:2.21.7:jar` | `8c5623b98f32d5f7e1287ff61ed50e08ab7b11381e991fff89327faabba42261` | `/tmp/jackson-core.jar` |
| `com.fasterxml.jackson.core:jackson-databind:2.21.7:jar` | `1290c2795e93e8a6861a6c4d9ff0d844d32f5ea178362cb2721edf5561e828b1` | `/tmp/jackson-databind.jar` |
| `com.microsoft.sqlserver:mssql-jdbc:13.4.0.jre11:jar` | `e36f5237c1267983e5b88dc2169f6b9d7e50eceec6dc1ca31018e3877e14af66` | `/opt/keycloak/lib/lib/main/com.microsoft.sqlserver.mssql-jdbc-13.2.1.jre11.jar` |
| `org.freemarker:freemarker:2.3.35:jar` | `0fac87dddd78f1223139e8ef88e819c7f483c0a3835cdf5982ad5e4576d1d896` | `/opt/keycloak/lib/lib/main/org.freemarker.freemarker-2.3.32.jar` |
| `io.micrometer:micrometer-commons:1.16.6:jar` | `45aff76226830db257f4bc39a5bcff83d633e572fee9dc4e45cfa12af9a0a49a` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-commons-1.16.6.jar` |
| `io.micrometer:micrometer-core:1.16.6:jar` | `1957ef2deaffbc1fb98cebfd0f5b3109ecf19c994d7e5378598c20747ba8d68b` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-core-1.16.6.jar` |
| `io.micrometer:micrometer-observation:1.16.6:jar` | `4c0826d5e7c8522a8e111470b4e77f77b0a10f520eb5502ddd143fdbcedb2340` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-observation-1.16.6.jar` |
| `io.micrometer:micrometer-registry-prometheus-simpleclient:1.16.6:jar` | `5f665369724caa8ffbb3f83a492b02d1de17a1420758e4a55debcb6ee2bd5e2a` | `/opt/keycloak/lib/lib/main/io.micrometer.micrometer-registry-prometheus-simpleclient-1.16.6.jar` |
| `io.netty:netty-buffer:4.1.137.Final:jar` | `f474b14c7734f15e0540394cb6f39d67777b7581a42919e4ac89d253d4efd929` | `/opt/keycloak/lib/lib/main/io.netty.netty-buffer-4.1.136.Final.jar` |
| `io.netty:netty-codec:4.1.137.Final:jar` | `9987b6a660b0a6b1f0d791485dae33180b3d1c63687c006fe6d3fd025e9e3798` | `/opt/keycloak/lib/lib/main/io.netty.netty-codec-4.1.136.Final.jar` |
| `io.netty:netty-codec-dns:4.1.137.Final:jar` | `2d9a33ce41bd9f6f607df95039e42518a1fca16f83c9bbfe72beb57df6e2489f` | `/opt/keycloak/lib/lib/main/io.netty.netty-codec-dns-4.1.136.Final.jar` |
| `io.netty:netty-codec-haproxy:4.1.137.Final:jar` | `5ffa58b94c5ac48fb7e41843c18acf7a268e90b5b85423987829c9291ce3a5df` | `/opt/keycloak/lib/lib/main/io.netty.netty-codec-haproxy-4.1.136.Final.jar` |
| `io.netty:netty-codec-http:4.1.137.Final:jar` | `0535bb5a736472bef5c948d15eb273c4ab9f796656fc7c5d6b982ad92bddbd49` | `/opt/keycloak/lib/lib/main/io.netty.netty-codec-http-4.1.136.Final.jar` |
| `io.netty:netty-codec-http2:4.1.137.Final:jar` | `576ddcfb51b78e86f145c9a52b60e500b1a1d5a257cafec8aa5416ffba044b49` | `/opt/keycloak/lib/lib/main/io.netty.netty-codec-http2-4.1.136.Final.jar` |
| `io.netty:netty-codec-socks:4.1.137.Final:jar` | `4a6190e08988c058bfeef2b5965e71946439c771abf721c68f4039b4e7b023e8` | `/opt/keycloak/lib/lib/main/io.netty.netty-codec-socks-4.1.136.Final.jar` |
| `io.netty:netty-common:4.1.137.Final:jar` | `d31926b01adcc07af86f5e27b81b6d6c115df17d366e835d1fc3f5a1924e7e52` | `/opt/keycloak/lib/lib/main/io.netty.netty-common-4.1.136.Final.jar` |
| `io.netty:netty-handler:4.1.137.Final:jar` | `d0e4c6ee4779f59f6ab2fb5d388e4f57147c82270164b37945764bb9bda96a44` | `/opt/keycloak/lib/lib/main/io.netty.netty-handler-4.1.136.Final.jar` |
| `io.netty:netty-handler-proxy:4.1.137.Final:jar` | `843de10c4cef34cbc1a5344090f1b57db532fbebf141559862da675f6b656df4` | `/opt/keycloak/lib/lib/main/io.netty.netty-handler-proxy-4.1.136.Final.jar` |
| `io.netty:netty-resolver:4.1.137.Final:jar` | `b4cf2aeedd9fc7c8c439bbfe574f63cfe5b83392e88bbc07ca0e8424b7cff955` | `/opt/keycloak/lib/lib/main/io.netty.netty-resolver-4.1.136.Final.jar` |
| `io.netty:netty-resolver-dns:4.1.137.Final:jar` | `898d63ca62ed68ff46543c5344f969424b417f1e8d082fe2bb0181ba10144194` | `/opt/keycloak/lib/lib/main/io.netty.netty-resolver-dns-4.1.136.Final.jar` |
| `io.netty:netty-transport:4.1.137.Final:jar` | `6251adc2a2921572382732a2db188d4f4f2251fd6ebb49c5d44bbf33d6bfb1a7` | `/opt/keycloak/lib/lib/main/io.netty.netty-transport-4.1.136.Final.jar` |
| `io.netty:netty-transport-classes-epoll:4.1.137.Final:jar` | `55049554b799dc8e53bf234fffa36e001f7b5b65c32994b9bd59fc6356f1c52f` | `/opt/keycloak/lib/lib/main/io.netty.netty-transport-classes-epoll-4.1.136.Final.jar` |
| `io.netty:netty-transport-native-unix-common:4.1.137.Final:jar` | `8056e7637f9948f953314894cf995ab664711b66c73c4cd5b593ae84f0b1048c` | `/opt/keycloak/lib/lib/main/io.netty.netty-transport-native-unix-common-4.1.136.Final.jar` |
| `io.netty:netty-transport-native-epoll:4.1.137.Final:linux-aarch_64:jar` | `82fb63a2dc90c0bddb81e04065a1c06fb4c5be722ad5d64b46491daf8b6f9a01` | `/opt/keycloak/lib/lib/main/io.netty.netty-transport-native-epoll-4.1.136.Final-linux-aarch_64.jar` |
| `io.netty:netty-transport-native-epoll:4.1.137.Final:linux-x86_64:jar` | `185db6053e0e9573d36fa4eb6567b26139afaa4daa7bf5a068e661568304542b` | `/opt/keycloak/lib/lib/main/io.netty.netty-transport-native-epoll-4.1.136.Final-linux-x86_64.jar` |
| `org.bouncycastle:bcprov-jdk18on:1.85:jar` | `20af26bf6060bb8005cc2389916812c1e0e998dc48d2ced7131b89461b54cff7` | `/opt/keycloak/lib/lib/main/org.bouncycastle.bcprov-jdk18on-1.84.jar` |
| `org.bouncycastle:bcprov-jdk18on:1.85:jar` | `20af26bf6060bb8005cc2389916812c1e0e998dc48d2ced7131b89461b54cff7` | `/opt/keycloak/bin/client/lib/bcprov-jdk18on-1.84.jar` |
| `org.bouncycastle:bcpkix-jdk18on:1.85:jar` | `c9f82b2d4e99c4bbdfccf684e52cc06ea06a0b567bfd0d08f9c5a3f417055996` | `/opt/keycloak/lib/lib/main/org.bouncycastle.bcpkix-jdk18on-1.84.jar` |
| `org.bouncycastle:bcutil-jdk18on:1.85:jar` | `590f55ed5d68529239898a4a5c4f730b6e37f45d1cfa3fbe51f8485abe32c42d` | `/opt/keycloak/lib/lib/main/org.bouncycastle.bcutil-jdk18on-1.84.jar` |

Additional Jackson destinations: `/opt/keycloak/lib/lib/main/com.fasterxml.jackson.core.jackson-core-2.21.5.jar`, `/opt/keycloak/lib/lib/main/com.fasterxml.jackson.core.jackson-databind-2.21.5.jar`, and the repaired `/opt/keycloak/bin/client/keycloak-admin-cli-26.7.2.jar`. Complete alignment sets are Micrometer four modules, Netty seventeen artifact/classifier variants, and Bouncy Castle three modules with the required second bcprov CLI copy. JDBC, FreeMarker and Jackson are also mandatory.

## Runtime, host and theme operation map

| Operation | Required captured input / observation | Current proof |
| --- | --- | --- |
| Signed OS closure | Dated `https://snapshot.ubuntu.com/ubuntu/20261002T000000Z/`, main, inherited suites/keyring; exact libssl3t64/openssl/openssl-provider-legacy `3.5.5-1ubuntu3.7` plus complete closure | Signatures/hash chain, six archives, all-four complete installed inventories and payload dispositions pass; explicit historical trust rule awaits owner acceptance |
| Removals | libcurl4t64 and dependent curl absent; `/usr/bin/pebble` absent | All-four static inventories and runtime smoke pass |
| Runtime identity | Captured Temurin platform bytes, actual Java version, no live dependency resolution in supported final build | All-eight closed local-OCI builds and runtime smoke pass; full live/system gates tracked separately |
| User/permissions | Remove inherited ubuntu account; add keycloak UID1000/GID0, nologin, home /opt/keycloak; expected files owned1000:0 | All-four final context files/directories/modes/ownership and runtime smoke pass |
| Certificates | Trust stores and Java CA handling preserved and executable | All-eight runtime smoke checks pass; live runtime operations separately tracked |
| Entrypoint | Exact captured Skoolit entrypoint, URL validation, exec kc.sh; explicit host composition and KC_RUN_IN_CONTAINER=true | Final bytes/config pass; unchanged image inspector URL rejection controls and arm64 isolated-browser gate pass |
| Theme/baseline | Read-only iteration; complete manifest/theme bytes, reviewed FreeMarker baseline, cache-disabled local behavior | Exact captures and static final image pass; arm64 retained isolated-browser gate passes; amd64 reaches the isolated-browser container but misses the unchanged 60-second readiness ceiling during emulated startup |
| Exposed ports/config | 8080/8443/9000, explicit entrypoint and USER1000; labels informational only | Actual config/manifest and runtime checks pass; labels alone are not conformance |

## Archive feasibility evidence

Private .NET10 compiled prototype repaired the Maven CLI source SHA-256 `386a6d7a0fce7221ab7eb42853c8917e73d6f3c1504249599a61a0a8567a8e64` twice to identical output `1ab7c9dfd70bcf6d730da9dfd7f58c019c081c341da861497cdae6e4b35be327`.

Independent Python ZIP/CRC and per-entry equality confirmed 2926 unrelated entries, 1038 selected replacement entries and the byte-identical source manifest; 9 rejection controls passed. Both immutable source-image CLI copies equal that exact Maven CLI SHA-256. The original and repaired CLI help/class loading passed on both platforms; real administrative auth/readback is a separate check.

The source manifest declares only Manifest-Version, Created-By, Java-Version and Build-Jdk-Spec. It has no Multi-Release attribute even though versioned entries exist. Those entries remain inactive under standard JAR semantics; no attribute is added or silently changed by the preservation probe. Exact CLI behavior and any required future profile decision must be established by independent JDK/runtime proof. This prototype does not implement the full production error contract, concurrent capture/lifetime model, OCI inspector, or resource-hardening coverage.

## Required remaining evidence and stop conditions

- Independent compiled neutral and full Skoolit proof for all three families on both required platforms.
- Actual source/original/replacement identities, signed snapshot trust/hash chain, retained material availability and measured resource budgets.
- Closed captured-input builder proof, exact static/runtime image identity, complete final inventories, deterministic archive and negative controls.
- Unchanged scan, CLI, rendered-login and database upgrade/restore gates on the exact final image; the retained amd64 isolated-browser readiness failure remains open for a native-amd64 or explicitly accepted emulation disposition.
- Named role acceptance and #888 boundary decision maker before API commitment; separate named release authorization before publication, then actual clean published-byte proof.
- Stop without API commitment if a family/platform, truthful manifest preservation or supported adapter cannot satisfy the accepted contract; return exact evidence and alternatives.

## Source receipts

- [`deploy/keycloak/Dockerfile`](https://github.com/forge-trust/skoolit/blob/6623ffe5dd6f3eb454df609e1074407e0ae2e446/deploy/keycloak/Dockerfile): SHA-256 `5a780564745291b46a534c71f52411796129d9e70855899b5f2439ec07bba54f`.
- [`deploy/keycloak/patch-shaded-jackson.py`](https://github.com/forge-trust/skoolit/blob/6623ffe5dd6f3eb454df609e1074407e0ae2e446/deploy/keycloak/patch-shaded-jackson.py): SHA-256 `89ce20b212322996a342719b15e8f6bc7033b30ebb4917615aa842adead3a5b1`.
- [`deploy/keycloak/entrypoint.sh`](https://github.com/forge-trust/skoolit/blob/6623ffe5dd6f3eb454df609e1074407e0ae2e446/deploy/keycloak/entrypoint.sh): SHA-256 `dcee4faf109c35f201fe7b8e04de41c9eb4ef96ecd406dc76f5c6c4dfe70b118`.
- [`scripts/qa/run-keycloak-theme-gates.sh`](https://github.com/forge-trust/skoolit/blob/6623ffe5dd6f3eb454df609e1074407e0ae2e446/scripts/qa/run-keycloak-theme-gates.sh): SHA-256 `aa3b8a8e0edbe4102976ea245d31690427446388464919687c6329ed91771deb`.

## Delivery and publication ordering

The existing [prerelease publisher](../../.github/workflows/nuget-prerelease-publish.yml) accepts only an annotated prerelease tag whose exact commit is reachable from `origin/main`, with successful main-branch build/package workflows and protected environment approval that prevents self-review. Published-byte consumer proof therefore follows reviewed integration and authorized publication. A draft implementation PR must report that proof as pending and cannot establish completion of issue887 or authorize release. No publisher policy change, feature-branch release or reviewer bypass is proposed.

The existing [coverage wrapper](../../scripts/coverage-solution.sh) remains `./scripts/coverage-solution.sh`: aggregate95%line/85%branch and the same patch thresholds against origin/main under the existing tolerance policy. The gate has not been run for a production change because implementation has not started. Package verification and solution coverage run sequentially.

## Capture isolation feasibility

A separately compiled private .NET probe confirmed that workspace changes after capture do not alter captured recipe/material/theme bytes, mutation of the snapshot path after a verified private copy does not alter that copy, a fresh use rejects the tampered snapshot without publishing a result, and two competing publications produce exactly one complete committed directory with loser staging cleaned. This is local byte/publication feasibility, not host-policy approval, image evidence or complete concurrency/cancellation coverage.

## Observed original-identity failure

[The four Micrometer expected paths do not exist in either immutable source platform](issue-887-feasibility-findings.md). The actual1.16.6 files already equal the requested replacement hashes. The unchanged recipe fails preflight; the owner-approved correction verifies the already-aligned group at its actual paths. The original missing-path failure remains a negative control before accepted recipe/API commitment. All other25declared final paths exist.

## Owner disposition — 2026-10-23

The human owner approved “Use verified1.16.6paths”: all four Micrometer entries now verify the already-aligned1.16.6group at the actual1.16.6classpath paths. No unused1.16.3copies are added. Every source/runtime pin, target version, other operation family and retained validation gate is unchanged. Named role acceptance is @akrock for all listed responsibilities; release publication remains unauthorized. The original mismatch is retained as a negative fixture and historical finding.

## Combined feasibility checkpoint

The [measured observations](issue-887-feasibility-observations.json) record all29final artifact destinations with actual original/replacement hashes and four explicitly already-aligned Micrometer members. A private compiled .NET materializer produced neutral and Skoolit contexts on both platforms, including the three captured signed packages, complete repaired archive and captured theme/entrypoint. Eight preliminary clean builds passed nonroot, removals, certificates, OS versions, Keycloak version and CLI help checks. Their image digests differ; whole-image reproducibility is not claimed. A local-OCI frontend/runtime compatibility build passed; the full closed-input builds and exact OCI inspection are still running.

The unchanged Skoolit fast gate passed (exit0,156216ms) against the exact captured source revision. The approved prebuilt full gate passed on arm64 in596seconds and passed amd64 fast/image/upgrade/restore phases before the unchanged isolated-browser readiness loop failed after830seconds; exact receipts are recorded below. This checkpoint neither commits a public API nor establishes a supported profile or release authorization.

## Final-image checkpoint — 2026-10-24

All eight closed-input builds and static runtime smoke checks passed. The four attempt-one manifests below passed bounded OCI/context conformance, complete OS package inventory/payload disposition, and Trivy0.70.0HIGH/CRITICALOS+library scanning under the unchanged empty-ignore policy. Runtime/system proof remains separately labelled in the [observations](issue-887-feasibility-observations.json).

| Fixture | Platform | Actual final manifest | Static/OS/scan |
| --- | --- | --- | --- |
| Neutral | linux/arm64 | `sha256:84ae40e6b2ba4317af8019343672eb2e8853112c5f9a7ad98e6111b57abdedba` | Passed |
| Skoolit | linux/arm64 | `sha256:b0061d58c10543370ec6f3d15fff7a67116c6bcd21468c37b6a80f751270ef79` | Passed |
| Neutral | linux/amd64 | `sha256:af053f9eea333d23bf3ca173d82dea783c29b091be586ef8a2de209df4b09c46` | Passed |
| Skoolit | linux/amd64 | `sha256:9a35806a9098145212faaf1e4935d82076ca48033b7fcf74b4c314dc0fd568a2` | Passed |

All29final artifact paths match their expected bytes, including the four already-aligned Micrometer files and repaired CLI digest. Each actual image has exactly the expected140-to-138installed package change; every selected signed regular payload file is either hash-equal or absent under the unchanged captured dpkg exclusion policy. Static reports check files, parent types, directory modes and ownership after ordered whiteouts.

The private runtime proof records actual admin authentication, theme update/readback, rejected wrong credentials and login HTTP200on arm64 for both fixtures. An initial request omitted the retained client query requirements; its failed observation is retained. The first amd64 runtime attempt remained in Quarkus configuration preparation at its provisional120second/2CPU/1.5GiB bound. A bounded follow-through will measure up to300seconds/4CPU/3GiB on the same image; this is a proposed feasibility resource revision, not a retained-gate timeout change or a passing amd64 runtime claim.

## Historical snapshot trust decision to ratify

The exact historical snapshot is `https://snapshot.ubuntu.com/ubuntu/20261002T000000Z/`. All eight signed InRelease copies verify with the captured keyringSHA256`80a36b0a6de2f69f49d2df75ef473ccde121e9e190b9ea01d20a4f63778d5c31`, signing fingerprint`F6ECB3762474EDA9D21B7022871920D1991BC93C`, RSA/SHA512. The base release Date is2026-04-23; updates/backports/security are2026-10-01. None declares Valid-Until. The signed metadata-to-index-to-package hash chain and all six exact package archives verify.

Proposed profile policy accepts only these reviewed historical bytes, signer, snapshot path, suites/main component and exact package coordinates/digests. It does not globally disable freshness checks, discover keys or fall back to a live repository. Missing/withdrawn materials or keys fail; a host policy change requires fresh capture/acceptance. The named policy owner must explicitly accept this historical rule before a supported profile/API commitment. This is separate from signature validity and from publication authorization.

## Retained full-gate integration decision

The captured full gate hardcodes a legacy Dockerfile build before its upgrade/browser checks. Supplying the captured-plan tag alone therefore replaces the intended image. The [reviewable prebuilt-image proposal](issue-887-skoolit-prebuilt-gate-proposal.patch) preserves the default build path and every existing inspection/browser/upgrade assertion, adds an explicit expected image ID check, and binds all later stages to that immutable ID. Syntax, dry-run application and four isolated integration controls pass. Actual use awaits the requested owner disposition; no unchanged `--all` pass is claimed.

## Proposed bounded support envelope

These are explicit proposed ceilings for owner ratification, with boundary/one-over enforcement tests still required in the implementation. Actual recorded maxima cover the two fixtures and both platforms; they establish no production latency or concurrency guarantee.

| Scope | Proposed hard ceiling | Observed fixture maximum / rationale |
| --- | --- | --- |
| Archive compressed input / entries / name |256MiB /65,536 /1KiBUTF-8 | Actual repaired archive has3,964entries; original and replacement archives fit captured byte bounds |
| Archive central-directory metadata / regular entry / expanded/output |16MiB /64MiB /1GiB |15,641,934expanded bytes in the deterministic output; no whole-archive payload dictionary |
| Context captured bytes / declared files |1GiB /65,536 | Four materializations each remain under385MiB of verified input copies |
| OCI archive / compressed blobs / metadata JSON |1GiB aggregate /1GiB each /1MiB each | Largest compressed blob aggregate301,417,896bytes |
| OCI regular entry / expanded layer / expanded aggregate |128MiB /2GiB /4GiB | Largest regular file101,048,209bytes; maximum expanded aggregate566,457,344bytes |
| OCI layers / members per layer / total members and merged paths |64 /250,000 /1,000,000 each | Actual9layers and at most9,736merged paths |
| OCI canonical path / parser wall deadline |4,096characters /300seconds | Observed full parser max7.879seconds during concurrent gates |
| Signed package index compressed / expanded / paragraph |8MiB /32MiB /64KiB | Both actual signed indices fit; linear paragraph parsing |
| Deb control regular bytes / tar extended metadata / decoder window |1MiB /64KiB /8MiB | All six packages pass; hostile16MiBdecoder window rejects |
| Acquisition aggregate / material count / redirect hops |2GiB /256 /5 | Captured fixture material set fits; every hop must match the exact host-approved mapping |
| Per-invocation managed payload buffer / owned temporary disk |512MiB /8GiB | Actual parser measurement79,249,408bytes maxRSS; aggregate enforcement still requires implementation. Preflight free space is advisory and exhaustion must fail safely |
| Subprocess combined captured output / per-process deadline |1MiB /300seconds | Bound while draining and terminate on exhaustion; raw output is sanitized before storage |
| Private live fixture container / startup deadline |3GiB memory /4CPU /300seconds | Four-image private live proof passed; retained amd64 full gate still hits its unchanged isolated-browser 60-second readiness ceiling while Quarkus schema initialization is active |

All SDK operations make one attempt. No implicit retry, global scheduler or shared mutable cache is proposed. Concurrent invocations each own these bounds; allocation failures preserve the primary typed cause and clean only their own staging. Retained Skoolit scanner/browser/upgrade deadlines and thresholds stay unchanged. A supported environment requires host.NET10, a compatible Docker29OCI export/build interface, Linux arm64oramd64 execution, and enough provisioned disk; native amd64system proof or an honestly measured emulation result is required before advertising that platform.

## Current completion ledger

| Required proof | Current result |
| --- | --- |
| Deterministic managed archive and independent byte oracle | Passed for actual CLI and complete neutral archive; truthful original manifest preserved |
| Capture/workspace mutation, fresh tamper and atomic publication race prototypes | Passed; complete production lifecycle/concurrency/error tests remain unimplemented |
| Closed input materialization and two builds per fixture/platform | Passed, eight builds with runtime smoke |
| Static final module/archive/theme bytes, modes and ownership | Passed on all four final attempt-one images |
| Signed package closure and complete final installed inventory/payload disposition | Passed on all four final images; exact historical policy accepted by @akrock on 2026-10-24 |
| Unchanged current Trivy policy | Passed on all four final images, zero selected findings |
| Real admin authentication/theme readback/wrong credential/HTTP login | Passed on all four exact images; strict captured realm policy, retained PKCE query, no SSL fallback; separate arm64 and bounded amd64 runs |
| Retained actual browser/accessibility | Skoolit arm64 full gate passed; amd64 full gate failed only at isolated-browser readiness after the candidate image, upgrade/restore, and test phases passed |
| Exact candidate upgrade and restored previous database behavior | Passed on both architectures in the approved adapted full gate; earlier incomplete private harness remains historical evidence only |
| Full retained `--all` on captured-plan output | Owner-approved prebuilt-image adaptation used. Arm64 exit 0; amd64 exit 1 at unchanged browser readiness after upgrade/restore passed |
| Supported public API, all caller tests/docs, enhancement, coverage and draft PR | Supported API commitment remains gated. An ignored private implementation integrates the core handlers; it is not a supported package or a complete caller/coverage proof |
| Actual published-byte consumer proof | Post-integration and separately authorized release gate, as documented above |

## Completed private live follow-through

Both amd64fixtures now pass real authentication, theme readback, rejected wrong credentials and loginHTTP200using the same immutable images with the recorded300second/4CPU/3GiBprobe ceiling. Combined with the earlier strict-policy arm64results, this covers all four final manifests. The host loopback client disables ambient proxies and sends the retained Skoolit HTTPS redirect/PKCE query; no realm SSL policy fallback is used. The original403cause is not conclusively isolated because its body/headers were not captured and more than one harness input changed. It is retained as an initial harness failure, not attributed conclusively to the image or proxy. The exact retained full-gate receipts and the amd64 isolated-browser startup disposition are recorded in the next section.

## Approved full retained gate receipts — 2026-10-24

Both runs used the unchanged adapted script at [the captured Skoolit gate source](../../.gstack/tmp/issue-887/skoolit-gates-source/scripts/qa/run-keycloak-theme-gates.sh), script SHA-256 `9c46d7393478164fe8c13c0b978cb645b06a5c28e1aa60e4f7dd4c27535283fd`, and the exact argv `bash scripts/qa/run-keycloak-theme-gates.sh --all --no-restore --no-build --resource-label skoolit.evidencehost.case=issue887 --defer-resource-cleanup --resource-manifest <run manifest>`. Each run verified its tag with `docker image inspect` before launch and retained separate stdout, stderr, metadata, and JSONL resource-manifest files under `.gstack/tmp/issue-887/approved-full-gate-proof` and `TestResults/`.

| Platform | Exact image tag / ID | Run and result | Phase evidence | Evidence paths |
| --- | --- | --- | --- | --- |
| linux/arm64 | `appsurface-887-combined:dc4875143ef048dfa4035a38dbd6be18-skoolit-arm64-1` / `sha256:b0061d58c10543370ec6f3d15fff7a67116c6bcd21468c37b6a80f751270ef79` | `arm64-20261024T071406Z-59409`, exit 0, 596s | fast 27.454s; image 2.133s; upgrade 512.785s; isolated-browser 53.921s; AppHost 39 passed; Playwright 37 passed; `keycloak-theme-gates-passed` | [metadata](../../.gstack/tmp/issue-887/approved-full-gate-proof/arm64-20261024T071406Z-59409.metadata.txt), [stdout](../../.gstack/tmp/issue-887/approved-full-gate-proof/arm64-20261024T071406Z-59409.stdout.log), [stderr](../../.gstack/tmp/issue-887/approved-full-gate-proof/arm64-20261024T071406Z-59409.stderr.log), [manifest](../../.gstack/tmp/issue-887/skoolit-gates-source/TestResults/issue887-full-gate-arm64-20261024T071406Z-59409.jsonl) |
| linux/amd64 | `appsurface-887-combined:fe5f931af4c94fd5aac5dada98291218-skoolit-amd64-1` / `sha256:9a35806a9098145212faaf1e4935d82076ca48033b7fcf74b4c314dc0fd568a2` | `amd64-20261024T072424Z-74872`, exit 1, 830s | fast 26.639s; image 8.992s; upgrade 731.812s passed; isolated-browser 62.140s failed with `KEYCLOAK_THEME_GATES_ISOLATED_KEYCLOAK_NOT_READY`; AppHost 39 passed; Playwright 37 passed | [metadata](../../.gstack/tmp/issue-887/approved-full-gate-proof/amd64-20261024T072424Z-74872.metadata.txt), [stdout](../../.gstack/tmp/issue-887/approved-full-gate-proof/amd64-20261024T072424Z-74872.stdout.log), [stderr](../../.gstack/tmp/issue-887/approved-full-gate-proof/amd64-20261024T072424Z-74872.stderr.log), [manifest](../../.gstack/tmp/issue-887/skoolit-gates-source/TestResults/issue887-full-gate-amd64-20261024T072424Z-74872.jsonl) |

The amd64 failure is a harness/fixture startup classification. The candidate container used the exact requested digest, and the preceding upgrade/restore phase passed its durable checks. The isolated-browser container was still running Quarkus schema initialization when the unchanged 60-second readiness loop ended: the gate logged failure at `2026-10-24T07:38:14Z`, while the container log first logged `Initializing database schema` at `07:38:28Z`; a read-only post-failure probe returned no HTTP response. The retained browser assertions and 60-iteration readiness loop were not changed or waived; the separate upgrade readiness loop retains its original 90 iterations. The next supported validation target is native amd64 execution with the same image and unchanged gate; no source or gate change is made here. All manifest-recorded containers and networks were removed after the read-only snapshot. The earlier malformed `tagappsurface-...` attempt remains a preserved failed setup record; it was never counted and its three manifest-recorded resources were cleaned separately.

One permitted unchanged browser-only retry was then run after owned-resource cleanup with the same amd64 tag and ID. Run `amd64-browser-20261024T074045Z-90314` exited 1 in 72 seconds with the same `KEYCLOAK_THEME_GATES_ISOLATED_KEYCLOAK_NOT_READY` result. The container started at `07:40:55.168Z`; Quarkus augmentation completed at `07:41:46.946Z` (43.074s), the server entered dev mode at `07:41:51.034Z`, and the gate failed at `07:41:57Z`. A post-failure well-known probe still returned an empty response; Docker reported 409.81% CPU, 674.2 MiB memory, and 41 PIDs. This confirms the retained 60-second readiness ceiling is exceeded under the current amd64 emulation environment. No prewarm, layer modification, timeout increase, assertion change, or further retry is justified. The next check is the same unchanged browser gate on a real amd64 host; if that is unavailable, the amd64 retained system gate remains blocked by measured environment startup latency.

## Owner dispositions accepted — 2026-10-24

@akrock explicitly approved the [prebuilt-image gate adaptation](issue-887-skoolit-prebuilt-gate-proposal.patch) for the exact-image proof and inclusion with validation in the draft PR. The isolated fixture uses that patch, preserving all retained inspection/browser/upgrade assertions and the default legacy build behavior. Original source bytes and hashes remain recorded separately. This approval resolves the candidate-selection decision; the adapted full gate must still pass on each required platform.

@akrock also explicitly accepted only the reviewed historical snapshot bytes and signing fingerprint, including the documented absence of Valid-Until, plus the proposed numeric resource ceilings above. This acceptance supplies the host historical trust rule; it is not a global freshness bypass, automatic key trust, waived system gate or release authorization. Both dispositions are recorded from the actual human replies. The exact arm64 full gate passes; the exact amd64 full gate remains disposition-blocked by the concrete isolated-browser startup readiness failure described above.


## Original-source OCI budget finding — 2026-10-24

The exact original source graph was recovered by immutable layer digest and descriptor size, preserving the original index/platform/config identities. The managed parser correctly rejects the upstream JDK `lib/modules` members (155,514,630 bytes arm64; 155,560,314 bytes amd64) under the accepted 128 MiB per-member ceiling. The source 494-file comparison and wrong-diffID control therefore have no passing result in this attempt. Synthetic retained-file OCI archives are not original-source graph evidence.

The [source-only budget proposal](issue-887-source-oci-budget-proposal.md) and [private-draft patch](issue-887-source-oci-budget-proposal.patch) request explicit disposition for 256 MiB only during original source capture. Final output inspection retains 128 MiB and archive replacement retains 64 MiB. Public API commitment remains gated on this disposition and the separate unchanged amd64 browser proof. Current private source compiles with zero warnings, but does not establish supported API completeness, packaged adoption, coverage or a draft PR.


## Accepted source budget and native CI follow-through — 2026-10-24

The owner selected 256 MiB only for original-source OCI regular members. This supersedes the earlier 192 MiB proposal. Final inspection retains 128 MiB; archive transformation retains 64 MiB. The applied private-draft proof verifies both original upstream index/manifest/config/layer graphs, compares each complete 494-file selected Keycloak tree (195,224,828 bytes), and rejects wrong diffIDs. Raw tar header controls accept the exact bound and reject one-over before payload consumption for both profiles; invalid constructor bounds reject. The retained receipt is `.gstack/tmp/issue-887/final-private-verification/original-source-proof/receipt.json`. No source or runtime pins changed.

At the owner's suggestion, native amd64 validation now uses the existing GitHub-hosted CI runner. [Run 40077359989](https://github.com/forge-trust/skoolit/actions/runs/40077359989), branch `codex/issue887-native-amd64-proof`, commit `3fa01cd738120dad7c94befbacdc3ded86937621`, verifies the transferred image identities before running the retained gate. Its result is pending. The unchanged local emulation failures remain recorded; starting CI does not waive them or establish a pass. Supported API commitment remains gated on the required actual CI result.


## Native amd64 retained full gate — passed 2026-10-24

[GitHub Actions run 40079400914](https://github.com/forge-trust/skoolit/actions/runs/40079400914) passed at private Skoolit commit `9dbc93901772be52b1412f3a67088dd27ec9c23f`. The native Linux X64 runner verified the original OCI graph and loaded image ID `sha256:9a35806a9098145212faaf1e4935d82076ca48033b7fcf74b4c314dc0fd568a2`; that same ID remained after the complete retained gate. `gate-exit.txt` is `0`; stdout records `keycloak-upgrade-and-restore-passed` and `keycloak-theme-gates-passed`. The private evidence directory is `.gstack/tmp/issue-887/native-ci-proof/40079400914/`.

Initial CI attempts remain historical failures: run 40077359989 omitted the existing pinned CLI tool restore and stopped before the gate; run 40077869431 passed fast/image checks but exhausted disk extracting pinned PostgreSQL. The accepted final workflow restores pinned tools and provisions capacity on its fresh disposable runner by removing unused mobile/Swift SDKs, recording before/after capacity and requiring 20 GiB free. The final runner observed approximately 27.7 GiB free. No retained assertion, timeout, source/image/package pin, scan policy or realm rule changed. Two repair rounds were used; no blind rerun occurred.

This closes the native amd64 full-system feasibility blocker. Supported API implementation, complete caller integration, production tests/docs, enhancement, QA and unchanged solution coverage still require their own results. No package release or draft PR exists yet.


## Current production qualification — 2026-10-24

The production SDK passes capture, materialization, and exact-image inspection for all four retained recipes after restoring the exact saved OCI image bytes to Docker 29.7.2. The initial fresh attempt failed safely because its immutable image was absent from the local Docker store; a private diagnostic isolated the nonzero image-inspect command. Each reloaded archive SHA256 and manifest identity matched the retained receipt before the successful rerun. No image bytes or checks changed.

| Fixture | Current elapsed time | Passing observations |
| --- | ---: | ---: |
| neutral-arm64 | 37.604s | 887 |
| skoolit-arm64 | 46.963s | 902 |
| neutral-amd64 | 50.799s | 888 |
| skoolit-amd64 | 61.296s | 903 |

The actual current public caller tests also pass all three operation families together against independently authored OCI, signed repository, package and archive bytes, including exact repaired CLI byte equality and provenance drift rejection. The focused pure suite passed 540 tests before removal of two redundant fixture-self assertions; measured pure-package coverage was 97.04% line and 85.13% branch. The Aspire suite passed 280 tests and the documentation policy suite passed 11 tests. The unchanged complete solution coverage gate remains in progress and is required before the draft PR.

A separate compiled consumer outside this checkout restored `ForgeTrust.AppSurface.Auth.Keycloak/0.0.0-issue887-local` and its two exact declared dependencies solely from an owned local feed into an absent package cache. Restore, build, and real neutral capture/materialize/inspect all exited zero; the execution took 51.312 seconds. This is preliminary unpublished package closure only. It does not satisfy the separately authorized actual published-byte adoption gate. Current private receipts are indexed by `.gstack/tmp/issue-887/final-private-verification/current-production-proof.json`; the eventual draft must carry final solution/QA/CI results as well.


## Current integrated qualification v3 — 2026-10-24

This checkpoint supersedes earlier pending production/sample and native-amd64 feasibility statuses without discarding their failure receipts. The compiled production SDK completed capture, materialization and exact-image inspection for all four retained recipes with the integrated stage events, bounded export and invocation disk accounting. The v3 receipt is `.gstack/tmp/issue-887/final-private-verification/current-four-recipes-v3/receipt.json`; each fixture directory retains its actual plan, image, repaired archive and evidence identities.

| Fixture | Automated v3 elapsed time | Passing observations |
| --- | ---: | ---: |
| neutral-arm64 | 32.349s | 887 |
| skoolit-arm64 | 33.524s | 902 |
| neutral-amd64 | 39.016s | 888 |
| skoolit-amd64 | 41.888s | 903 |

The checked-in [portable arm64 fixture](../../examples/auth-keycloak-security-recipes/fixtures/README.md#prepare-portable-bindings-without-changing-the-recipe) was prepared from the owner's exact retained digest store: all 41 bindings passed length/hash verification and the recipe bytes remained unchanged (`fixture-prepare-arm64.log`). The compiled [sample full flow](../../examples/auth-keycloak-security-recipes/README.md#run-the-checked-in-neutral-fixture) then captured those bindings, materialized, invoked the offline Docker build, resolved the resulting immutable image, inspected 887 checks and published fresh evidence (`sample-current-v3.log`). Its stage sidecar contains 16 structured start/terminal events. Its actual host receipt records Docker client/server `29.7.2` and buildx `v0.36.1-desktop.1`. The separate wrong-identity invocation printed `wrong-identity=rejected`; the forbidden passing-evidence path is absent, while its diagnostic stage sidecar is retained (`sample-current-v3-wrong-identity.log`). These private logs are retained under `.gstack/tmp/issue-887/final-private-verification/`.

The [native amd64 retained full gate](#native-amd64-retained-full-gate--passed-2026-10-24), run [40079400914](https://github.com/forge-trust/skoolit/actions/runs/40079400914), remains passed on the same retained Skoolit image. The earlier local emulation failures remain historical; no retained assertion or timeout was waived.

At this checkpoint the focused pure v37 suite passes **809/809**, with zero failures or skips. No v38 compilation pass is claimed. The unchanged [full solution coverage hard gate](../../scripts/coverage-solution.sh), including aggregate and patch requirements, remains pending and required before the draft PR; focused tests and actual-image proof do not replace it. Final frozen-source review and delivery checks remain separate.

These are automated runs with retained materials and existing host/image caches. They establish no new profile latency, cold-cache onboarding time or human usability result. The [cold-run protocol and human novice walkthrough](../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/cold-run.md) remain unexecuted; the earlier author-biased agent reading review is separately labelled. Both Keycloak packages remain publication-held. After reviewed integration, coordinated publication and the exact published-byte consumer proof still require separate release authorization. The earlier outside-checkout local-feed proof remains preliminary unpublished closure only.


## Atomic file publication correction — 2026-10-24

The v40 evidence-publication race reproduced a real contract failure: two competing writers could both report success. Unix `File.Move` can check destination existence before a later rename, allowing the concurrent winner to be replaced. The failing receipt remains `production-tests-v40.log` under `.gstack/tmp/issue-887/final-private-verification/`; it is not a passing full-suite result.

The internal [AtomicFilePublication](../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/AtomicFilePublication.cs) helper now publishes complete owned staged files with the operating system's single create-if-absent `link` primitive on supported Linux/macOS hosts. Evidence writing, bounded binary export, archive output and downloaded material publication use it. Staging and destination must be on the same local filesystem with hard-link support. Unsupported filesystems and existing destination nodes reject; there is no copy/rename fallback or external executable. After commit, only the invocation's staged name is removed, and failed staging-name cleanup does not remove the winner or misreport committed publication. See the [filesystem prerequisites](../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/host-prerequisites.md) for the supported boundary.

The focused follow-up passes **20/20**, zero failures or skips (`atomic-publication-green.log`), including the regression's 32 concurrent publication rounds checking one complete winner, preserved existing output and rejected existing destination nodes. This is focused proof of the corrected atomic contract. It does not establish a new full-suite, cross-host or solution coverage pass.

The candidate is not frozen. The v3 actual-image/sample results and v37 focused-suite result above remain earlier receipts; they do not verify this additional production change. Final candidate tests, review and the unchanged full solution aggregate/patch coverage hard gate remain required before the draft PR. Publication of both packages remains separately held, and no published-byte adoption or human novice/cold-cache result is claimed.

## Current validation checkpoint v48 — 2026-10-24

The focused Debug SDK suite passes **1,010/1,010** tests with zero failures or skips and no build warnings (`production-tests-v48.log`). SDK coverage is 97.65% line and 94.34% branch. A diagnostic report replacing only this package in the earlier solution report passes the unchanged aggregate/patch thresholds, with patch line 94.82% and branch 94.33%; this mixed diagnostic is **not** a fresh full-solution gate. The exact `./scripts/coverage-solution.sh` remains mandatory before the draft PR.

The current v4 retained-image runs pass all three operation families for neutral arm64, Skoolit arm64, neutral amd64 and Skoolit amd64 with 887/902/888/903 actual observations. The manifest identities remain those in the earlier production qualification, including `sha256:9a35806a9098145212faaf1e4935d82076ca48033b7fcf74b4c314dc0fd568a2` from the passing native amd64 system gate. Their private receipt is `current-four-recipes-v4/verification-summary.json` under the task's final validation directory. Those runs precede the final review's metadata-bound forwarding and failed-export cleanup repairs; they are retained proof, with final frozen-candidate verification still required.

Final review reproduced the same absent-destination race in the Aspire release-evidence writer: two synchronized calls both reported success. It now shares the SDK's create-if-absent implementation; the 32-round concurrent writer regression passes and preserves exactly one complete tuple. A forced invocation disk-budget failure also reproduced an export child remaining alive after return. Bounded child termination/draining now preserves the primary budget failure; both real-process regressions pass. Source manifest metadata now uses the caller's exact configured cap before allocation, as do the other metadata reads.

Named approvals, exact historical policy, source-only 256 MiB and final-image 128 MiB limits remain as recorded above. No scan, browser, upgrade/restore, coverage or publishing gate is relaxed. Both packages remain publication-held; full solution validation, final delivery checks and draft CI are pending. Cold-cache and human novice measurements, coordinated authorized publication, and published-byte adoption remain separate and unexecuted.
