# Offline evidence subject image candidate

The [Containerfile](Containerfile) is the base-owned image recipe for the fixed [code-coverage subject entrypoint](../evidence-gate-subject-entrypoint.py). It starts from a digest-pinned .NET 10 SDK image, adds Python for the fixed runner, and copies a local NuGet feed into `/opt/appsurface/locked-dependencies`. The [subject launcher](../evidence-gate-subject.py) accepts only a published image reference ending in `@sha256:<digest>`, requires that exact digest already in rootless Podman storage, and runs it without network, credentials, or writable image layers.

Publish only from a reviewed protected-base checkout. Implementation-branch builds are local recipe checks and cannot be promoted as trusted base-owned inputs. The build context must contain `evidence-gate-subject-entrypoint.py` from that same checkout and a `locked-dependencies/` directory assembled from the solution's tracked lock files. Every package archive must match the lock file's `contentHash`; do not populate this directory from an unverified cache or from a PR checkout for publication. This is NuGet's package content hash as reported by [`dotnet nuget verify`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify), not a plain SHA-512 of the `.nupkg` ZIP bytes, and the cache's `.nupkg.metadata` value alone does not authenticate the archive. A build context assembled for one policy/tool revision is not automatically valid for a later revision. The checked-in recipe pins the *base* image; after publishing, record and configure the **resulting image's** immutable registry digest. A tag, local image ID, or the base image digest is not a valid runtime value.

Use the [offline feed builder](../evidence-gate-build-offline-feed.py) on that protected checkout to select only projects in `ForgeTrust.AppSurface.slnx`, resolve each project's default or `linux-x64` lock exactly as the fixed subject entrypoint does, and verify each copied package with NuGet. A macOS lock beside a selected project does not add packages to this Linux image. The builder takes a physical, already-restored NuGet global cache and a **new** destination directory. For example, after setting `trusted_checkout`, `global_packages`, and `context` to absolute physical paths and creating the empty context parent:

```sh
python3 "$trusted_checkout/scripts/evidence-gate-build-offline-feed.py" \
  --checkout "$trusted_checkout" \
  --solution ForgeTrust.AppSurface.slnx \
  --global-packages "$global_packages" \
  --output "$context/locked-dependencies"
cp "$trusted_checkout/scripts/evidence-gate-subject-entrypoint.py" \
  "$context/evidence-gate-subject-entrypoint.py"
docker buildx build --platform linux/amd64 \
  --file "$trusted_checkout/scripts/evidence-gate-subject-image/Containerfile" \
  --tag appsurface-subject-candidate:local "$context"
```

The context must not contain a PR checkout. The builder fails if a selected archive is missing, linked, malformed, or has the wrong NuGet content hash; a dependency-changing PR that needs a new package therefore remains ineligible until the base-owned image or an approved mirror is refreshed. The builder does not run restore or build hooks. Its result counts are useful for audit logs, but do not attest the final image.

Run the feed assembly in a reviewed Linux .NET 10 builder. `dotnet nuget verify` also checks signatures; host trust stores can differ. A local macOS run stopped on `CommandLineParser` 2.9.1 because its historical signature chain was rejected there, while the pinned Linux SDK verified the same archive and reported a matching lock hash. Treat a verifier failure as an unavailable build input and investigate it; do not accept a cache sidecar or bypass the content-hash check.

Run the cold-cache restore proof on native `linux/amd64`. The local arm64 Docker host could build and start the x64 candidate image, but x64 emulation aborted the .NET SDK during MSBuild project loading before NuGet resolved packages. That attempt proves neither feed completeness nor a lock mismatch; the [rollout record](../../docs/evidence-gate-rollout.md) contains the observed failure.

The fixed entrypoint scans the entire feed for links and special files, then restores the subject solution with `--locked-mode` and an offline NuGet config that clears all other sources. Do not move restore or build hooks to the trusted controller to make a dependency-changing PR pass. The [policy matrix](../../docs/evidence-gate-policy-matrix.md) keeps code coverage blocked because container-backed tests, subject checkout/isolation attestation, and complete artifact verification are not yet registered. The [rollout record](../../docs/evidence-gate-rollout.md) requires a cold-cache and dependency-change pilot before any check becomes required.

For a local recipe smoke check, prepare a disposable context with those two entries, then run `docker build --file scripts/evidence-gate-subject-image/Containerfile --tag appsurface-subject-candidate:local <context>`. Verify the image starts the entrypoint as UID 65532 with a read-only root filesystem and no network. That smoke check proves the image recipe can start; it does not prove the real feed, rootless Podman envelope, final image digest, or coverage verdict. Publish and configure the candidate only after the base-owned feed contents, final digest, and isolation pilot have been reviewed.
