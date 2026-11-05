# Material retention and rebuild availability

The consumer material-policy owner named in the [fixture card](../../../docs/designs/issue-887-feasibility-card.md#required-owners) owns retention. This checkout supplies [complete neutral JSON and receipts](../../../examples/auth-keycloak-security-recipes/fixtures/README.md), without distributing OCI archives, Maven binaries, Debian packages, or keyrings. Upstream registries and snapshot services are acquisition locations, not an indefinite retention promise.

## Explicit rebuild-availability policy

Before accepting a recipe for a release, the host must preserve every `materials.json` row in its own access-controlled, digest-addressed artifact storage for as long as that image/profile is supported or retained for rollback. Preserve original OCI archive bytes and raw upstream index JSON, signed Release/index/package/keyring bytes, replacement archives, theme/entrypoint inputs, recipe/policy/canonical digests, baseline bytes, transformer version, and final inspection/system receipts together. The owner records storage location privately, retention expiry, authorized readers, and a successful retrieval/hash check. These storage facts are host prerequisites; the package does not create a mirror or promise that the issue's private working directory is a distribution service.

Before a rebuild or promotion, retrieve each exact object, check its SHA256 and length against the receipt, verify current host policy and key withdrawal state, and use a fresh capture/output location. A local cache hit never replaces authority or signature verification. Periodically test retrieval as part of the [profile reevaluation checklist](profile-lifecycle.md#required-reevaluation-checklist).

If any object is absent, expired, withdrawn, or hash-different, stop that rebuild. Ask the owner to restore the exact retained object. Do not weaken digest checks, fetch a moving tag, rediscover a signing key, disable freshness globally, or fall back to live apt repositories. If exact recovery is impossible, explicitly review a new material set, policy and recipe, then repeat all affected capture/image/system gates. New OCI tar metadata can change its archive hash even when its internal graph is equal; this still requires fresh approval of the archive material.

## Acquisition and cleanup

The [fixture acquisition instructions](../../../examples/auth-keycloak-security-recipes/fixtures/README.md#acquire-the-exact-materials) distinguish raw checked-in indexes, approved exact HTTPS downloads, and host-captured OCI/keyring inputs. HTTPS redirect destinations must already be authorized; mirrors require explicit policy review. Never broaden policy to make an unavailable download appear successful.

Keep originals, approved policy, portable receipts and final evidence after temporary snapshots/contexts are removed. Dispose a captured handle only after its accepted consumers return, then explicitly remove only caller-owned scratch when no reader needs it. Handle disposal itself leaves the snapshot intact. Preserve the previously deployed image and its compatible database backup through the host's [rollback procedure](migration.md#rollback-and-database-state).
