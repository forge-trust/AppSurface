# TODOs

## Add protected NuGet prerelease publish workflow after rename pass

- **Case:** [#253](https://github.com/forge-trust/Runnable/issues/253)
- **What:** Add the second release-conveyor slice that publishes validated prerelease packages to NuGet from protected prerelease tags.
- **Why:** Issue #161 starts with a pack-only artifact workflow so package readiness can be verified before the public rename pass locks package IDs into NuGet. The publish step still needs to be captured explicitly so it does not disappear after the pack-only PR lands.
- **Pros:** Keeps the protected `nuget-prerelease` environment, `NUGET_API_KEY`, least-privilege permissions, `dotnet nuget push --skip-duplicate`, deterministic publish order, publish log artifact, partial publish recovery, and NuGet indexing smoke test visible as the next release slice.
- **Cons:** Adds a deferred release item that depends on naming decisions, final package IDs, and environment secret setup.
- **Context:** The package-artifacts slice should prove the exact `.nupkg` files without publishing them. After the rename pass, the publish workflow should consume the same checked-in manifest and validated artifacts rather than inventing a second package list.
- **Depends on / blocked by:** Public rename pass, final package IDs, and accepted NuGet ownership/environment setup.
