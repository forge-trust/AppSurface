# Keycloak theme upgrade and rollback

Use this procedure when changing the pinned Keycloak image or releasing a new immutable image containing a registered
login theme. It applies to the [Keycloak AppHost package](../README.md), not to production automation. Application
CI and operators own image publication, health checks, realm mutation, rollout, and rollback.

## Compatible release tuple

Keep these values together: theme name, source-manifest digest, packaged-manifest digest, build-contract digest,
final pushed image digest, pinned Keycloak base image, Linux/amd64 platform, and the template-baseline digest when
the theme overrides FreeMarker templates. A tag, realm selection, or source manifest by itself is not a compatibility
claim.

## Upgrade

1. Inventory the target Keycloak image and pin its exact digest.
2. For template overrides, inspect the pinned image's supported theme archive layout and regenerate/review the
   template baseline. Do not infer compatibility from a previous Keycloak version.
3. Recreate the build contract from current source, build the consumer-owned image, and verify labels and the exact
   packaged theme subtree.
4. Run the required `keycloak-theme-evidence` Linux/amd64 job. It must report `pass`; `fail` and `not-run` block the
   release.
5. Publish and health-check the image using its immutable registry digest, create the release tuple, and retain the
   safe CI artifact.
6. Only then select the matching realm login theme, read it back, and verify the declared same-origin resource.

## Rollback

1. Keep the previous verified image and tuple available for the rollback window.
2. Restore the earlier immutable image before selecting its matching theme.
3. Select the previous realm login theme, read back the selection, and hash-check its declared resource.
4. Record the result with the previous tuple and remove the superseded image only after the rollback window closes.

## Persistent local proof data

The AppHost default is disposable data. If local persistent data was enabled, clear only that local Keycloak volume
before rerunning an import-based proof. Never treat a stale persistent realm as evidence about a released image.
