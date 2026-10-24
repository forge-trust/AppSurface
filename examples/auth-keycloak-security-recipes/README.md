# Keycloak security recipe harness

This checkout example is a small host entry point for the repository-held schema and API. It references the package project directly so it can be compiled against the reviewed source while publication remains held. It does not claim package availability or a published consumer.

The host supplies three complete JSON files, an absent snapshot directory, and a host image reference:

- `recipe.json`: the complete canonical `KeycloakSecurityRecipe`, including `templateBaseline` as `null` or `{ "archivePath": "...", "archiveSha256": "...", "entry": "...", "sha256": "...", "materialId": "..." }`. A theme destination ending in `.ftl` requires the object, and capture checks that the selected source archive entry matches the reviewed captured material.
- `policy.json`: the separately approved `KeycloakMaterialTrustPolicy`.
- `bindings.json`: `{ "reviewedRecipeId": "<canonical recipe SHA256>", "localInputs": [{ "materialId": "...", "localPath": "fixtures/source.tar", "sha256": "...", "maxBytes": 123 }] }`. Resolve relative `localPath` values against the directory containing `bindings.json`; absolute paths are host-local overrides only; do not put home-directory paths into the checked-in JSON.
- `host-image-reference`: an explicit local image reference. `--run` builds this local tag and then resolves the resulting immutable content through `KeycloakSecurity.ResolveLocalImageAsync`; `--inspect` resolves an existing image through the same Docker 29 containerd adapter. The adapter verifies the full manifest/config/diff-ID graph and export hashes; it does not trust labels or classic config-only stores. The caller does not report raw image metadata.

Place at most one action flag last. Unknown or repeated actions, invalid argument counts, and invalid local tags exit 2 before capture. Invalid or inaccessible JSON inputs and missing Docker prerequisites exit 1 with a bounded corrective message.

The default `--run` action is one bounded flow: it reads strict canonical JSON, calls `CaptureAsync`, calls `MaterializeAsync`, verifies the context, invokes Docker with the fixed `CreateDockerBuildArguments` through `ProcessStartInfo.ArgumentList`, resolves the built immutable image through Docker 29 containerd metadata and export hashes, calls `InspectAsync` on that same image, and writes evidence to a fresh path. `--inspect` skips the build and inspects an existing host image. `--wrong-identity` resolves the image, changes only the config digest to all zeroes, requires safe rejection, writes no passing image evidence, and exits successfully when rejection is observed. The command does not publish, push, choose packages, fetch missing fixture data, or run shell hooks.

```text
dotnet run --project examples/auth-keycloak-security-recipes/AuthKeycloakSecurityRecipes.csproj -- recipe.json policy.json bindings.json /private/tmp/recipe-snapshot appsurface-keycloak-recipe:local /private/tmp/recipe-context /private/tmp/keycloak-evidence.json --run
```

The command above illustrates the argument roles. Complete checked-in neutral recipes, independently approved policy records and portable bindings are available in the [fixture guide](fixtures/README.md); acquire their exact material receipts before running the commands below. The canonical recipe JSON supplied to `ReadRecipe` is the identity input; after adding or correcting `templateBaseline`, recompute `reviewedRecipeId` from the canonical bytes. The `host-image-reference` is the same explicit local tag passed to `CreateDockerBuildArguments` and then to `ResolveLocalImageAsync`; digest references containing `@` are rejected for `--run`. The host adapter resolves the built image’s immutable manifest, config, platform, and ordered diff IDs; the sample does not accept a caller-supplied target JSON in the full flow.

Current production qualification passes neutral and Skoolit recipes on arm64 and amd64 with 887, 902, 888 and 903 observations respectively. The documented `--wrong-identity` control is a separate required sample check; inspect its rejection and confirm no evidence file was written. All four production v3 runs passed in 32.349/33.524/39.016/41.888 seconds in that fixture order. These automated retained-input observations establish no profile latency guarantee, published package availability or full solution gate pass. The [current qualification](../../docs/designs/issue-887-feasibility-card.md#current-integrated-qualification-v3--2026-10-24) records the remaining draft gates.


## Run the checked-in neutral fixture

From the repository root, complete [exact material acquisition](fixtures/README.md#acquire-the-exact-materials) first. The `arm64` commands below use the retained arm64 frontend. The [amd64 recipe](fixtures/neutral-amd64/recipe.json) targets amd64 but retains that same frontend platform; provision a qualified host/frontend combination rather than silently substituting another frontend. All requested output paths and their `.host.json` / `.stages.json` sidecar paths below must be absent. Use a fresh owned directory name for a later attempt; preserve existing evidence.

```sh
mkdir -p .gstack/tmp/issue-887-neutral/arm64

dotnet run --project examples/auth-keycloak-security-recipes/AuthKeycloakSecurityRecipes.csproj -- examples/auth-keycloak-security-recipes/fixtures/neutral-arm64/recipe.json examples/auth-keycloak-security-recipes/fixtures/neutral-arm64/policy.json examples/auth-keycloak-security-recipes/fixtures/neutral-arm64/bindings.json .gstack/tmp/issue-887-neutral/arm64/run-snapshot appsurface-keycloak-neutral:local .gstack/tmp/issue-887-neutral/arm64/run-context .gstack/tmp/issue-887-neutral/arm64/run-evidence.json --run

dotnet run --project examples/auth-keycloak-security-recipes/AuthKeycloakSecurityRecipes.csproj -- examples/auth-keycloak-security-recipes/fixtures/neutral-arm64/recipe.json examples/auth-keycloak-security-recipes/fixtures/neutral-arm64/policy.json examples/auth-keycloak-security-recipes/fixtures/neutral-arm64/bindings.json .gstack/tmp/issue-887-neutral/arm64/wrong-snapshot appsurface-keycloak-neutral:local .gstack/tmp/issue-887-neutral/arm64/unused-context .gstack/tmp/issue-887-neutral/arm64/forbidden-evidence.json --wrong-identity

test ! -e .gstack/tmp/issue-887-neutral/arm64/forbidden-evidence.json
```

The first command must complete capture, offline build, immutable-image resolution, inspection and fresh evidence with exit 0. The neutral arm64 retained qualification observed 887 checks; inspect the actual evidence's expected/observed identities and operation profiles rather than using the count as the only oracle. A new build may have a different image digest. The second invocation must print `wrong-identity=rejected`, exit 0 for that successful negative control, and write no passing image evidence at `forbidden-evidence.json`. A diagnostic `forbidden-evidence.json.stages.json` sidecar may still be written. Its fresh snapshot is intentional; `--wrong-identity` does not build another image. Preserve the earlier successful evidence byte-for-byte.

For amd64, use the complete files in `fixtures/neutral-amd64/` and distinct task-owned amd64 capture/output paths and tag. Bindings are relative to their platform directory containing `bindings.json`. The [real preparer](fixtures/prepare.sh) resolves those same paths and writes the task-owned cache; run it from the repository root as documented in the fixture guide. The [target-platform records](fixtures/README.md) describe retained inspected images and are not accepted by this CLI as a shortcut to observation. The checked-in arm64 path layout has now been executed: preparation verified all 41 bindings with unchanged recipe bytes, and the compiled v3 sample completed the full offline build/resolve/inspect/evidence flow with 887 checks. It recorded 16 structured stage events and actual Docker client/server 29.7.2 and buildx v0.36.1-desktop.1 observations. The wrong-identity run rejected the altered config and left the forbidden passing-evidence path absent. The [current qualification receipt](../../docs/designs/issue-887-feasibility-card.md#current-integrated-qualification-v3--2026-10-24) separates this retained-material automated proof from cold-cache or human novice timing.

Read [recovery scenarios](../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/diagnostics.md#four-recovery-walkthroughs) for invalid input/build/inspection/commit failures, [profile review](../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/profile-lifecycle.md) before changing pins, and the [cold-run clock](../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/cold-run.md) before claiming onboarding timing. The cold-cache run and human novice walkthrough remain **unexecuted**; the linked record separately identifies the completed author-biased agent reading review. Future published-feed adoption requires separate release authorization; this checkout route remains source-only.


## Progress and output files

Read the canonical [stage-events guide](../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/docs/stage-events.md#sample-output-files) for event fields and output semantics. The sample writes structured stage `Started` and terminal events to stderr, including correlation, elapsed milliseconds, counts when available, effect and safe failure code. These describe actual activity, without guessed percentages. Stage completion alone is not image verification or release permission.

For a requested evidence path `run-evidence.json`, the sample uses:

| File | When written | Meaning |
| --- | --- | --- |
| `run-evidence.json` | Only after successful exact-image inspection and atomic evidence publication | Passing observed-image checks, with release gates still separate. |
| `run-evidence.json.host.json` | In `--run`, after Docker preflight and before capture | Docker client/server/buildx version observations; it can remain when a later stage fails. `--inspect` and `--wrong-identity` skip this preflight receipt. |
| `run-evidence.json.stages.json` | Best effort at invocation exit, including failures and the wrong-identity control | Schema `appsurface-keycloak-stage-progress-v1`, deadline 300 seconds and collected stage events. It is diagnostic data and may be absent if writing fails. |

All files preserve existing destinations; use fresh sidecar paths as well as fresh evidence/snapshot/context paths. An argument rejection before progress setup creates no sidecar. A parse failure after progress setup can produce an empty stage record. A `.host.json` or `.stages.json` file never substitutes for passing image evidence, and a missing diagnostic sidecar alone does not change the operation's exit status. Preserve the typed error and stderr when diagnosing a failed write.
