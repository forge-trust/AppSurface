# Issue #176 acceptance fixture

This is the [alias plan](../issue-176-version-aliases.md)'s local fixture. Both exact releases passed the archive verifier on2026-10-07. The implementation's real-release HTTP and two-host retarget checks passed on2026-10-09; the recorded boundaries below distinguish those checks from the remaining human acceptance observation.

The full [catalog](https://github.com/forge-trust/AppSurface/blob/main/docs/plans/issue-176-fixtures/acceptance-catalog.json), [host options](https://github.com/forge-trust/AppSurface/blob/main/docs/plans/issue-176-fixtures/host-options.json) and [inventory/checksums](https://github.com/forge-trust/AppSurface/blob/main/docs/plans/issue-176-fixtures/published-archive-evidence.json) supply expected values. Replace the local example TrustedReleaseRootPath with your own trusted extraction root and make CatalogPath identify this catalog. Use the [archive contract](../../../Web/ForgeTrust.AppSurface.Docs/README.md#exact-version-tree-contract), [rewrite limit](../../../Web/ForgeTrust.AppSurface.Docs/README.md#published-tree-rewrite-limit) and [verification command](../../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-docs-verify-archive).

## Prepare exact trees

Download [0.1.0](https://github.com/forge-trust/AppSurface/releases/download/v0.1.0/appsurface-docs-v0.1.0.tar.gz) and [0.2.0-preview.11](https://github.com/forge-trust/AppSurface/releases/download/v0.2.0-preview.11/appsurface-docs-v0.2.0-preview.11.tar.gz). Archive SHA-256 values are respectively 0155762af86eb3b42ac3349759c178b5ec8e209e60c3aa26ae37b62516665cc5 and 6940694384411d8f20591405a9f4c20d797bf1da414fc6a9ab6ac35494a2ea70.

Check hashes before extraction. Reject absolute/parent-relative paths and non-regular entries other than directories, including symlinks/hardlinks. Extract beneath one trusted root as releases/0.1.0 and releases/0.2.0-preview.11. Preserve covered bytes and manifest pins. Cold download/extraction/build time is separate from the prepared-host DX clock.

From the repository root with its supported .NET SDK, set task-specific paths (replace the root used in recorded verification):

    issue176_catalog_path="$PWD/docs/plans/issue-176-fixtures/acceptance-catalog.json"
    issue176_fixture_root="/private/tmp/issue176-fixture-verification"

    dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs verify-archive --catalog "$issue176_catalog_path" --trusted-release-root "$issue176_fixture_root" --max-rewritten-file-size-bytes 16777216 --version 0.1.0
    dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs verify-archive --catalog "$issue176_catalog_path" --trusted-release-root "$issue176_fixture_root" --max-rewritten-file-size-bytes 16777216 --version 0.2.0-preview.11

Recorded outcome: both existing verifier calls exited 0 with 403/892 covered files and catalog manifest pins. The prerelease search index is 8,741,119 bytes; this fixture needs 16 MiB while production default remains 4 MiB. Raising the limit increases rewritten-response memory exposure. Exact verification does not validate labels/collisions.

## Serve the candidate

The [standalone host](../../../Web/ForgeTrust.AppSurface.Docs.Standalone/README.md#entry-point) is the source seam used for the recorded acceptance run:

    AppSurfaceDocs__Versioning__Enabled=true AppSurfaceDocs__Versioning__CatalogPath="$issue176_catalog_path" AppSurfaceDocs__Versioning__TrustedReleaseRootPath="$issue176_fixture_root" AppSurfaceDocs__Versioning__MaxRewrittenFileSizeBytes=16777216 dotnet run --no-launch-profile --project Web/ForgeTrust.AppSurface.Docs.Standalone -- --urls http://127.0.0.1:5180

Use startup logs for actual listening readiness and operator codes. Namespace collisions must fail startup. Restart after catalog edits. Named hosts retain [host authorization conventions](../../../Web/ForgeTrust.AppSurface.Docs/use-appsurface-docs.md#run-multiple-independent-docs-products).

## Reader assertions

| Request | Required candidate result |
|---|---|
| /docs/versions | stable → 0.1.0, preview → 0.2.0-preview.11, v1 → 0.1.0 in authored order; recommendation independently prerelease |
| /docs/a/stable/agents | AGENTS.md; canonical /docs/v/0.1.0/agents |
| /docs/a/v1/agents and its nav/search | AGENTS.md; alias-local /docs/a/v1/agents, canonical /docs/v/0.1.0/agents |
| /docs/a/preview/agents | AGENTS.md; canonical /docs/v/0.2.0-preview.11/agents |
| Each alias search-index.json | AGENTS.md points to that alias's /agents; fragments retained |
| /docs/a/stable/artifacts/issue-728-test-efficiency/candidate-inventory | Owned 404 though recommended contains this page |
| Same suffix under preview | Candidate inventory page, exact prerelease canonical |
| Alias HEAD | GET status/headers without body |
| Unsupported alias verb | 405, Allow GET, HEAD |
| Owned content/assets/search/redirect/error | no-store under approved C1 |

Retarget stable to 0.2.0-preview.11 in a temporary catalog copy and restart a second host. Content/search/canonical move together; preview/v1 and both exact releases stay correct. Keep the checked-in fixture unchanged.

## Evidence boundaries

Separate ingestion, candidate alias acceptance, human onboarding and runtime scale measurements. Human onboarding remains unobserved. Observations are not a capacity limit or timing SLO.

The Release candidate was built on .NET SDK10.0.102/runtime10.0.2, macOS26.7.1 Arm64, from branch `codex/make-it-so-docs-version-aliases` at base `40de2ef62` with the uncommitted #176 implementation. Task-owned hosts used ports5180 and5181 and the catalog/root/16MiB options above. The retarget host used a temporary catalog copy; the checked-in catalog and exact covered bytes were preserved. On2026-10-09,29 real HTTP checks passed: stable/preview/v1 content, alias-local AGENTS.md search results, exact canonicals, GET-equivalent HEAD content type/length without a body, unchanged exact releases and non-retargeted labels, stable-only missing-page404, preview/retargeted-stable availability, generic unknown-label404 and unsupported POST/OPTIONS405 with `Allow: GET, HEAD`. Checked owned responses sent `no-store`. Browser checks confirmed archive alias order and exact identities,320px bounds and targets,375/768/1280 rendering, actual light/dark palettes and native Tab focus. A640CSSpx viewport at2x density provided1280px200% reflow-equivalent evidence; direct browser-toolbar zoom was not driven.

The [10,000-label](https://github.com/forge-trust/AppSurface/blob/main/docs/plans/issue-176-fixtures/scale-10000.observed.json) and [20,000-label](https://github.com/forge-trust/AppSurface/blob/main/docs/plans/issue-176-fixtures/scale-20000.observed.json) records are uninstrumented observations from `ManyLabelsShareTargetProvidersAndCachesAndRetainAllArchiveRows` in the [alias integration suite](../../../Web/ForgeTrust.AppSurface.Docs.Tests/AppSurfaceDocsVersionAliasIntegrationTests.cs). Run it with `dotnet test Web/ForgeTrust.AppSurface.Docs.Tests/ForgeTrust.AppSurface.Docs.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ManyLabelsShareTargetProvidersAndCachesAndRetainAllArchiveRows`. Each record includes UTC/runtime/OS/architecture, one warmup per operation, allocation scope, response bytes and first/middle/last requests. Resolution includes a fresh catalog parse plus exact verification; projection uses an already resolved catalog; rendering includes Razor and localhost HTTP transfer, with process-wide allocations that may include background work.

| Labels | Resolution ms / allocated bytes | Projection ms / allocated bytes | Render ms / allocated bytes | Response bytes / rows |
|---|---|---|---|---|
|10,000|46.1574 /11,611,064|1.1243 /1,143,568|54.2761 /36,255,552|14,451,783 /10,000|
|20,000|36.0308 /23,248,920|12.2871 /2,285,736|160.4445 /69,872,224|28,911,783 /20,000|

Both cases used two exact targets/providers overall and one provider/frozen-manifest-cache identity for all aliases sharing the stable target. They verified complete row count/authored order and selected first/middle/last alias requests. Single-run timing variation does not establish a speed ratio, supported capacity or service guarantee. Keep coverage-instrumented timings separate.

Human clock starts with an existing upgraded host and verified trees, includes reading/config/restart/page/search/canonical inspection and ends with understanding. Target <=5 minutes is unmeasured; human help/failures, cold prerequisites and automated time remain separate. Missed timing is friction evidence, not a new CI gate.

On2026-10-10, actual native Chrome toolbar zoom at200% was observed on candidate `cf62ea69` for healthy, unavailable, conflict and long-label archive states, including visible keyboard focus and Enter activation. This supplements the earlier reflow-equivalent evidence; it does not establish full WCAG conformance or all viewport/theme combinations. Zoom was restored to100% and the task-owned window closed. The user explicitly deferred X5's fresh human trial for the draft PR and instructed continuation. Human timing and comprehension remain unobserved; the deferral is not a passing trial result. See the [current execution record](../issue-176-execution-progress.md#current-acceptance-status--2026-10-10).
