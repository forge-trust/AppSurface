# Keycloak security recipe harness

This checkout example is a small host entry point for the repository-held schema and API. It references the package project directly so it can be compiled against the reviewed source while publication remains held. It does not claim package availability or a published consumer.

The host supplies three complete JSON files, an absent snapshot directory, and a host image reference:

- `recipe.json`: the complete canonical `KeycloakSecurityRecipe`, including `templateBaseline` as `null` or `{ "archivePath": "...", "archiveSha256": "...", "entry": "...", "sha256": "...", "materialId": "..." }`. A theme destination ending in `.ftl` requires the object, and capture checks that the selected source archive entry matches the reviewed captured material.
- `policy.json`: the separately approved `KeycloakMaterialTrustPolicy`.
- `bindings.json`: `{ "reviewedRecipeId": "<canonical recipe SHA256>", "localInputs": [{ "materialId": "...", "localPath": "fixtures/source.tar", "sha256": "...", "maxBytes": 123 }] }`. Use repository-relative or otherwise host-owned fixture paths; do not put home-directory paths into the checked-in JSON.
- `host-image-reference`: an explicit local image reference. `--run` builds this local tag and then resolves the resulting immutable content through `KeycloakSecurity.ResolveLocalImageAsync`; `--inspect` resolves an existing image through the same Docker 29 containerd adapter. The adapter verifies the full manifest/config/diff-ID graph and export hashes; it does not trust labels or classic config-only stores. The caller does not report raw image metadata.

Place at most one action flag last. Unknown or repeated actions, invalid argument counts, and invalid local tags exit 2 before capture. Invalid or inaccessible JSON inputs and missing Docker prerequisites exit 1 with a bounded corrective message.

The default `--run` action is one bounded flow: it reads strict canonical JSON, calls `CaptureAsync`, calls `MaterializeAsync`, verifies the context, invokes Docker with the fixed `CreateDockerBuildArguments` through `ProcessStartInfo.ArgumentList`, resolves the built immutable image through Docker 29 containerd metadata and export hashes, calls `InspectAsync` on that same image, and writes evidence to a fresh path. `--inspect` skips the build and inspects an existing host image. `--wrong-identity` resolves the image, changes only the config digest to all zeroes, requires safe rejection, writes no evidence, and exits successfully when rejection is observed. The command does not publish, push, choose packages, fetch missing fixture data, or run shell hooks.

```text
dotnet run --project examples/auth-keycloak-security-recipes/AuthKeycloakSecurityRecipes.csproj -- recipe.json policy.json bindings.json /private/tmp/recipe-snapshot appsurface-keycloak-recipe:local /private/tmp/recipe-context /private/tmp/keycloak-evidence.json --run
```

The command above requires real complete files; its names are arguments, not downloadable placeholders. Use the actual source/material manifest and retained fixture receipts from the coordinated migration checkout. The canonical recipe JSON supplied to `ReadRecipe` is the identity input; after adding or correcting `templateBaseline`, recompute `reviewedRecipeId` from the canonical bytes. The `host-image-reference` is the same explicit local tag passed to `CreateDockerBuildArguments` and then to `ResolveLocalImageAsync`; digest references containing `@` are rejected for `--run`. The host adapter resolves the built image’s immutable manifest, config, platform, and ordered diff IDs; the sample does not accept a caller-supplied target JSON in the full flow.

Current production qualification passes neutral and Skoolit recipes on arm64 and amd64 with 887, 902, 888 and 903 observations respectively. The documented `--wrong-identity` control is a separate required sample check; inspect its rejection and confirm no evidence file was written. These observations do not establish published package availability or replace the full solution gate.
