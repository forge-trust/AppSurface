# API and JSON contract

## Public entry points

`KeycloakCanonicalJson.ReadRecipe(ReadOnlySpan<byte>)` and `ReadPolicy` reject unknown, duplicate, missing, trailing, or incorrectly typed JSON properties and validate the closed schema. `Write` emits the canonical bytes used for recipe and policy identities. Canonical bytes, rather than formatting or file paths, produce the recipe SHA256.

`KeycloakSecurity.CaptureAsync` accepts a `KeycloakSecurityRecipe`, an independently approved `KeycloakMaterialTrustPolicy`, `KeycloakCaptureInputs`, and an absent destination. It returns `CapturedKeycloakPlan` only after all declared material is copied and verified. `CapturedKeycloakPlan.Digest`, `RecipeDigest`, `PolicyDigest`, `Platform`, and `Mode` are stable evidence fields. Dispose the plan after accepted operations complete.

`KeycloakSecurity.MaterializeAsync` accepts an accepted plan and an absent context directory. `KeycloakBuildContext.Digest`, `DirectoryPath`, and `RepairedCliSha256` identify the result. `Verify` revalidates the context. `CreateDockerBuildArguments` returns fixed Docker 29-compatible arguments and validates the local tag; it never invokes Docker or publishes.

`KeycloakSecurity.ResolveLocalImageAsync` accepts a host image reference and returns the immutable `KeycloakInspectionImage` resolved by the Docker 29 containerd adapter. The adapter is the safe handoff from a mutable local tag to the actual manifest, config, platform, ordered diff IDs, and full export hash checks. It does not trust image labels. Classic Docker image stores that expose only config metadata fail as unsupported because they cannot prove the selected manifest graph. `KeycloakSecurity.InspectAsync` accepts that resolved identity and the plan, returning `VerifiedKeycloakImageEvidence` only after every declared check, each actual theme file hash, and, in security mode, the read-only probe pass. Theme-only inspection instead preserves the source configuration and filesystem outside the declared application tree, and explicitly reports that security customization was not evaluated. A config digest of all zeroes, an unrelated image, reordered layers, or a changed theme file must fail.

## Complete input files

`recipe.json` contains the entire `KeycloakSecurityRecipe`: schema version 1, source index and platform identity, runtime/frontend identities, ordered operations, `templateBaseline`, mode, Keycloak version, entrypoint, theme inputs, captured material IDs, source profile, every material reference, and resource limits. `templateBaseline` is either `null` or an object with `archivePath`, `archiveSha256`, `entry`, `sha256`, and `materialId`. Security mode requires one reviewed runtime profile. Runtime/frontend and source archives need their exact index, selected manifest, config, and ordered diff IDs. When a theme destination ends in `.ftl`, the baseline object is mandatory and its archive member must match the reviewed captured source material during capture.

The canonical recipe example for a reviewed `.ftl` theme therefore includes:

```json
"templateBaseline": {
  "archivePath": "/opt/keycloak/lib/lib/main/org.keycloak.keycloak-themes-26.7.2.jar",
  "archiveSha256": "<sha256 of the retained source archive>",
  "entry": "themes/keycloak/login/login.ftl",
  "sha256": "<sha256 of the exact retained entry>",
  "materialId": "source-material-id"
}
```

The values above are contract-shaped examples only. Supply the actual retained archive and entry hashes from the reviewed fixture manifest; do not copy placeholder values into a recipe.

`policy.json` contains the independent owner approval reference, exact HTTPS origins and path mappings, exact coordinate mappings, digest map, signing fingerprints and algorithms, withdrawn keys, and historical release hashes. It cannot broaden the recipe.

`bindings.json` contains `reviewedRecipeId` equal to the lowercase SHA256 of `KeycloakCanonicalJson.Write(recipe)` and `localInputs` entries with `materialId`, local path, SHA256, and a maximum byte bound. Each local binding must name an explicit `CapturedHostInputs` entry. A cached downloadable material retains its exact HTTPS URI and must pass the same origin, namespace, digest, metadata and signed-repository checks as a newly acquired input. Only a material explicitly marked `CapturedHostInput` may omit its URI. Local paths are capture-only and never enter recipe identity.

`target-platform.json` contains the host-observed final `manifestDigest`, `configDigest`, `platform`, and ordered `diffIds`. The caller supplies this identity from the actual immutable image. The sample resolves its explicit local tag through `ResolveLocalImageAsync`; callers that already hold verified immutable identities can supply those directly to `InspectAsync`.

Materials must preserve the original source archive and exact retained runtime/frontend archives, indexes, manifests, configs, signed OS indexes, package bytes, themes, and replacement artifacts. The entrypoint is a captured, canonical path under `/opt/keycloak/bin/`; the generated Dockerfile encodes it as a JSON argument array. Missing associations are rejected. A source digest alone is insufficient to prove an embedded module coordinate.

## Resource ceilings

The validation ceiling is 1 GiB per material, 65,536 archive entries, 1 GiB expanded archive bytes, 1 GiB output bytes, and a 300-second operation deadline. The recipe may choose lower values. The bounded readers also apply their narrower format limits: release 1 MiB, keyring 8 MiB, compressed package index 8 MiB, expanded package index 32 MiB, Maven archive 256 MiB, and a manifest entry 16 MiB with a 64 KiB manifest main section. The engine keeps acquisition aggregate bounded and cleans owned staging/context scratch after failure.

OCI regular members are capped at 256 MiB while verifying the original source image and at 128 MiB while inspecting the final image. Archive transformation retains its separate 64 MiB entry limit. The source-only allowance accommodates the verified upstream Java module member and does not expand final-image or repaired-archive limits.

These are finite input, archive, output, and deadline bounds, not a promise about process RSS. The implementation reserves metadata and uses bounded BCL streams/collections; the metadata reserve must not be described as a 512 MiB RSS guarantee. Callers should budget host memory for the selected material and Docker 29 build separately.

## Host preconditions

The supported profile is Unix file modes, Docker 29-compatible offline OCI build support with the containerd snapshotter, an absent destination for each operation, and a host that can provide the complete explicit JSON and local captured bindings. The managed package uses only its two declared runtime dependencies; ZstdSharp.Port is used for compressed Debian payloads. Python is confined to CI harnesses and is not required by the package or sample. The host owns process supervision, image tagging, registry publication, secrets, and deployment.

## Observed evidence

Passing evidence includes the plan, canonical recipe and independent policy digests; safe source/runtime/frontend index, manifest, config and platform identities; evaluated operation profile versions; reviewed template baseline; inspector/transformer versions; and actual expected/observed checks. Each check carries a stable `Id`, declared `OperationId`, and `Outcome` of `passed`. Failed or unsupported checks throw a [typed diagnostic](diagnostics.md) and cannot produce passing evidence. In security mode, filesystem scope is the complete application tree, complete installed-package inventory, signed package payloads and explicit runtime profile facts. Unrelated host-composed paths outside the application tree are outside those checks; security evidence does not assert a complete root-filesystem allowlist. Theme-only mode additionally requires preservation of the source filesystem outside the application tree. Image references, host input paths, process output and realm data are excluded. Evidence is an observation record; [scan and release gates](host-prerequisites.md) remain separate.
