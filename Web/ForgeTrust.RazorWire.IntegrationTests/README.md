# RazorWire Playwright Integration Tests

This project contains browser-level integration tests for the RazorWire MVC sample application in `examples/razorwire-mvc`.
It also includes an AppSurface Docs browser regression test that runs against `Web/ForgeTrust.AppSurface.Docs.Standalone`.

## What it validates

- The sample app boots successfully.
- RazorWire stream connection is established.
- A message published from one browser session is received in another session via SSE.
- Reactivity message history persists when navigating away and back, and after full-page reload.
- Antiforgery behavior:
  - valid form submissions are accepted,
  - submissions without antiforgery token are rejected with `400`,
  - `RegisterTwoUsers_FromSingleSession_WithoutRefresh_AntiforgeryAllowsBothPosts` verifies antiforgery accepts both registration POSTs from a single session without refreshing the token.
- Increment counter behavior:
  - single-session increment updates instance/session/client-count values without refresh,
  - multi-session flow keeps session score independent while instance score is global,
  - session score persists while navigating across Home, Navigation, and Reactivity pages.
- RazorWire README contract:
  - the documented repo-root quickstart command points at the supported sample project,
  - local README links and heading anchors resolve,
  - source-backed snippets document the Markdown generator command that keeps README code fences in sync,
  - `ReadmeQuickstart_ReactivityCounter_UpdatesScoresWithoutRefresh` exercises the README quickstart's `/Reactivity` counter path in a browser.
- AppSurface Docs search behavior:
  - sidebar search works from `/docs`,
  - navigating to `/docs/search` via Turbo keeps both advanced search and sidebar search functional.

## AppSurface Docs standalone fixture

The AppSurface Docs Playwright fixture hosts `ForgeTrust.AppSurface.Docs.Standalone` in-process through `AppSurfaceDocsStandaloneHost.CreateBuilder`.
It still binds Kestrel to a real loopback HTTP listener, but it does not shell out to `dotnet run` and does not depend on previously built standalone `bin` output.

The fixture passes the same runtime settings that the standalone smoke path needs:

- `AppSurfaceDocs:Mode=Source`
- `AppSurfaceDocs:Source:RepositoryRoot=<repo root>`
- contributor source/edit URL templates for the current repository
- `AppSurfaceDocs:Contributor:LastUpdatedMode=Git`
- development environment settings without mutating process-wide environment variables

When adding AppSurface Docs browser coverage, use the shared fixture or `AppSurfaceDocsInProcessHost`. Avoid starting the standalone app as a child process from tests; that can rebuild unrelated projects during fixture startup and can hide stale output bugs during focused local test runs.

### Contributor timestamp rendering

The contributor-strip rendering regression uses [`AppSurfaceDocsContributorTimestampFixture`](AppSurfaceDocsContributorTimestampFixture.cs), a private source corpus served by the real Docs host. It supplies the documented [`contributor.last_updated_override`](../ForgeTrust.AppSurface.Docs/README.md#page-level-overrides) so the test always exercises a real server-rendered timestamp and the packaged browser formatter. The shared source-backed fixture continues to use Git freshness for its other tests. [Git freshness is optional and bounded](../ForgeTrust.AppSurface.Docs/README.md#contributor-provenance); do not require any particular page's timestamp from a large repository harvest or increase its production budget to satisfy a rendering test.

[`AppSurfaceDocsTimestampReadiness.WaitAsync`](AppSurfaceDocsTimestampReadiness.cs) retains the rendering regression's nonempty, non-UTC text predicate and 60-second default. When that wait fails, it preserves the original timeout as the inner exception and adds only bounded timestamp/attribute/runtime booleans and a capped element count. A missing timestamp, invalid datetime or absent formatter remains a failure. Snapshot collection has a separate five-second cap; if it cannot complete, the original failure still surfaces with `State: unavailable`. The allowance must be a positive integer; zero is rejected because Playwright would otherwise disable its timeout. Synthetic negative probes use a short readiness allowance only to verify the diagnostic branches. The helper does not retry or initialize the formatter.

### Docs outline layout evidence triage

Failure triage flow:

`failure message -> outline-layout.json -> viewport.png -> trace.zip`

- Artifact name: `docs-outline-layout-evidence`
- The artifact should not exist on green runs.
- Each failure writes one directory: `<evidence root>/docs-outline-layout/<yyyyMMddTHHmmssfffZ>-initial-<guid>/`. The three files are inside that directory, not directly under the evidence root.
- Download artifacts for a failed run:

  ```bash
  gh run download <run-id> -n docs-outline-layout-evidence
  ```

- Reproduce locally with absolute evidence output path:

  ```bash
  APP_SURFACE_TEST_ARTIFACTS_DIR=/absolute/path \
    dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj \
    --filter FullyQualifiedName~ThemeCriticalCss_ShouldRenderBeforeExternalStylesAndKeepTheOutlineResponsive
  ```

- `APP_SURFACE_TEST_ARTIFACTS_DIR` must be absolute. A relative or whitespace value records an `evidence-directory` capture failure in the assertion message and writes no artifacts. Leave it unset to use the system temporary directory.
- Repository retention policy governs CI artifact retention; local temp evidence stays on disk until manually removed after triage.

## Run

```bash
dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj
```

The fixture installs Playwright Chromium automatically on first run.

### Run specific subsets

```bash
dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj --filter "FullyQualifiedName~IncrementCounter"
```

```bash
dotnet test Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj --filter "Category=Integration"
```
