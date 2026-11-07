# Concept-switch fixture

This fixture is the reusable two-release source for the hosted and static issue #183 integration tests. The tests export
each source with `appsurface docs export`, derive the release pins from the produced manifests, publish the exports with
the Release `docs-publication` command, and then use the same exact trees for the hosted catalog and static browser run.
All generated output belongs under a disposable temporary directory; the checked-in source fixture is never rewritten.
See the repository [version-switching guide](../../../guides/version-switching.md#two-release-walkthrough) for the local
two-export adoption walkthrough, generated pin handling, optional legacy bridge, and original-tree hash check.

The `v1` and `v2` repositories cover these identity cases:

- `guides/setup.md` moves to `guides/configuration/getting-started.md` while retaining `guides.setup`.
- `guides/setup.md` in v2 belongs to `guides.security`, exercising an old route reused by a different concept.
- `guides/unchanged.md` keeps both its route and concept ID.
- `guides/title-only.md` keeps its route and concept ID while its title changes.
- `guides/removed.md` has an authored ID in v1 and no v2 counterpart.
- `guides/legacy-unknown.md` has no ID and no v2 counterpart, so route continuity cannot establish absence.
- `guides/locale-en.md` has an English source; v2 publishes the same concept only in French.

From the repository root, the equivalent manual export and verification calls are:

```bash
fixture_root="$(mktemp -d)"
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs export --repo tests/fixtures/concept-switch/v1 --output "$fixture_root/v1" --mode cdn --strict
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs export --repo tests/fixtures/concept-switch/v2 --output "$fixture_root/v2" --mode cdn --strict
```

The integration fixture performs those same exports and reads each generated `.appsurface-docs-release-manifest.json`
hash. It never embeds a sample or placeholder digest. The Release publication output and browser evidence are produced
only in that disposable location, with no upload step.
