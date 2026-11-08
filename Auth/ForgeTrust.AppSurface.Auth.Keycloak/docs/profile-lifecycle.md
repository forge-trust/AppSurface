# Profile ownership, review, and retirement

The [reviewed fixture card](../../../docs/designs/issue-887-feasibility-card.md#required-owners) names @akrock for the library/profile, consumer material policy, inspector compatibility, security response, and shared-package conflict resolution with #888. Release authorization is a separate responsibility and remains outstanding. These roles establish review routing, without a support SLA.

## Current supported shape

Schema 1 uses source profile `keycloak-26.7.2`, runtime profile `temurin-ubuntu` version `1`, archive profile `jackson` version `1`, and deterministic transformer `managed-zip-v1`. Security recipes use the exact three-package OpenSSL closure and two curl/libcurl removals in the [contract reference](api-and-contracts.md). Source and runtime indexes, selected manifests/configs/diff IDs, package versions, Maven coordinates, and authority associations are explicit in the [neutral fixtures](../../../examples/auth-keycloak-security-recipes/fixtures/README.md). There is no floating default or automatic pin update. The retained frontend platform is explicitly arm64 even in the qualified amd64 target recipe; changing the builder host/frontend combination requires fresh proof.

The supported adapter is Unix file modes and Docker 29-compatible containerd OCI storage. The [fixture card](../../../docs/designs/issue-887-feasibility-card.md#current-production-qualification--2026-10-24) separates target arm64/amd64 proof from native amd64 system CI. A .NET target does not establish Windows, another engine, or another host/frontend combination as supported.

## Required reevaluation checklist

At every upstream Keycloak release, relevant runtime/OS advisory, or material/key withdrawal, the profile owner records a dated decision with this checklist. Silence does not renew support.

- Identify the candidate official distribution by index, platform manifest, config, and ordered diff IDs; preserve the previous reviewed inputs and approval.
- Run the official unmodified distribution through the same retained scan, module, CLI, rendered-login/accessibility, startup, and database upgrade/restore gates. Record each pass/failure and the concrete repair benefit that remains.
- Recheck all original Maven identities/paths, complete alignment sets, signed snapshot scope and validity, fingerprints/withdrawals, exact package closure/removals, template baseline, and [material availability](material-retention.md). Do not substitute a moved tag or live package repository.
- Recheck the Docker/export/probe handoff and [resource ceilings](api-and-contracts.md#resource-ceilings) on both required image platforms. Record commands, tool versions, hashes, maxima, cache/network conditions, and independently authored negative controls.
- Choose continued support with evidence, an explicitly reviewed new recipe/profile/policy, suspension pending evidence, or retirement. The material-policy owner approves changed authority/digests; the inspector owner approves adapter changes. Resolve shared-package conflicts through the named #888 owner.
- Require fresh capture and current host-policy acceptance before promotion. Preserve failed checks; never renew by waiving a scan threshold, unsupported signature, manifest truthfulness, or required platform.

## Retirement criteria

Prefer the official distribution when it passes every retained gate and makes the repair unnecessary. Propose removal with an operation-by-operation comparison, exact candidate hashes, and passing neutral/consumer system proof. Retire only the demonstrated unnecessary repair; keep unrelated alignments, OS closure, ownership, themes, and baseline requirements until separately proven unnecessary. A retirement that changes a schema/profile or supported input needs the [migration procedure](migration.md#coherent-versions-and-schema-changes).

Missing original bytes, withdrawn authority, a false source manifest, unsupported conversion, or failure to meet accepted resource/system bounds suspends that candidate profile. Preserve its receipts and request an owner decision. A replacement profile has a new explicit identity; never rewrite an accepted recipe or claim the previous evidence applies to new bytes.
