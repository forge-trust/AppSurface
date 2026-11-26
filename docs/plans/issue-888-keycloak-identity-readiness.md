# Keycloak identity readiness for local AppHosts

[AppSurface #888](https://github.com/forge-trust/AppSurface/issues/888) follows the approved Approach B design. A finite Aspire-free worker verifies the managed identity configuration. The Aspire adapter owns the local lifecycle and uses the same protected client capture for the worker and the web app. The required seed owns application identities. The web app waits for seed completion and subsequent identity readback.

The November 15 lifecycle experiment found that the supported Aspire stop result could not establish that arbitrary host-project descendants had stopped. Andrew approved the package-supervised boundary for the seed and web workloads with the original 30-second stop bound and stop-before-clean requirement. The [supervised execution guide](../supervised-keycloak-execution.md) describes the resulting contract and its limits.

## Recorded concerns

| Item | Decision and obligation |
| --- | --- |
| R3-1 | Keep reconciliation execution internal to the Aspire-free Keycloak library and its coordinated friend assemblies. The public declarations and capture API have their own contracts. |
| R3-2 | Require typed administrator bindings for configured generic reconciliation. Preserve the default disposable public-client path. Recovery resolves no administrator credentials. |
| R3-3 | The recorded concerns were regenerated from the completed design review. The original verdict remains in the local feasibility archive. |
| R3-4 | Run guarded recovery before graph construction, using the exact expected owner. A successful one-shot request clears its verified marker and exits with `workloads_started=false`. A stale or uncertain request fails closed. |
| R3-5 | Capture the confidential-client credential once per run and deliver the same protected value to the worker and real web app. Verify custody with packaged components and real login. |
| R3-6 | A privileged seed must respect the managed-field contract. The adapter verifies drift after seeding. Maintainer and consumer acceptance of this contract is required before API commitment. |
| R3-7 | Apply `AllowedEnvironmentNames` during local registration. Preserve the default local policy and the refusal in Aspire Publish mode. |

Andrew is the named AppSurface maintainer, Skoolit consumer maintainer, and release owner. Role identification and planning approval are recorded. Acceptance of the daemon-and-volume lease scope, local registration and legacy migration changes, and seed contract remains required before API commitment. Coordinated publication, published-package-only replay, and actual Skoolit adoption under #608 are separate gates.

## Proof gates

- Use pinned versions and source-bound receipts. The supported local qualification target is a macOS ARM64 AppHost and a Linux ARM64 Docker runtime. Qualify both coverage producers and the existing full solution aggregate and applicable patch thresholds. The local gate uses the complete repository evidence selection on one host.
- Build and execute the extracted worker and package consumers outside the checkout with an empty NuGet cache at runtime and no checkout mount or fallback. Verify the package closure, public capture, and real seed-plus-OIDC behavior. Include neutral and Skoolit configuration fixtures, routed tunnel login/logout, required-seed and managed-field failure controls, and compatible local theme behavior.
- Require a nonzero terminal status for required-gate failures. Confirm stopped ownership independently before cleanup and within the 30-second bound. Preserve the active record if verification fails. Recovery checks exact daemon, host and owner incarnation, volume identity, stopped workload inventory, and unchanged marker bytes before clearing.
- Preserve existing volume state across qualified legacy reuse, including Keycloak-created UID 1000 H2 data, the exact volume name, and seeded identities. Changed-volume, changed-marker, live-owner, and ambiguous migration controls must fail closed.
- Keep the bounded worker reconciliation and retry contract, post-seed readback, protected credential custody, local usage policy, and package publication holds from the approved plan.

## Feasibility results

These runs established the boundary for the named fixtures. Run the same proofs at the final draft commit and retain their source-bound records separately.

| Area | Observed result |
| --- | --- |
| Extracted packages and browser qualification | At `37f3346d24b4e1196c4063b8f7c7d1101ff38bfa`, the cold, neutral, Skoolit-configured, routed tunnel, and legacy UID 1000 groups passed 19 cases, 744 fixture checks, 10 real Chromium journeys, and 270 browser checks. All groups recorded an empty runtime NuGet cache and verified stopped ownership before cleanup. The longest observed stop was 15.36 seconds. |
| Cold first use | The same run observed login at 20.68 seconds and login/logout at 30.16 seconds after AppHost launch. This clock starts with a prepared local toolchain and packages. |
| Linux ARM64 worker | All 29 worker tests passed on the pinned .NET 10.0.402 Linux ARM64 SDK at the same source commit. |
| Legacy volume | Keycloak created the H2 data as UID 1000. The precisely named volume retained UID:GID `1000:0` ownership and seeded identities through the two login/logout paths. |
| Live theme and tunnel | The earlier November 22 combined replay observed a served live theme edit on the same Keycloak container and a real routed Chromium login/logout. The November 26 replay refreshed the routed browser result at the stated candidate. |
| Failure and recovery | The supervised AppHost-loss and post-seed experiments verified owner fencing and teardown, rejection of managed drift before web launch, and guarded unchanged-volume/marker recovery. Changed volume or marker, uncertain launch, and missing owner acknowledgement retained state. |
| Public capture and worker closure | The public consumer called `CaptureAsync` from the extracted pure package. The archive retains the capture, signed fixture, and recursive worker closure proofs. |

The local archive `tracked-feasibility-37f3346d.tar.gz` preserves 293 original tracked feasibility and decision files byte-for-byte at the historical commit, with SHA-256 `f42cfec075d3eeb4b217c74a4680ea3488a7d58f27f4fa9847dbc2d1d4acc7ee`. Raw runtime receipts, package archives, prepared inputs, and failed-run diagnostics are kept with the local runs. The [replay README](../../tests/keycloak-identity-feasibility/README.md) identifies the current checked-in drivers and test entry points.
