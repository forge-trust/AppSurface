# AppSurface CLI

The **AppSurface CLI** is the command-line home for repository-level AppSurface workflows. It is packaged as a .NET tool with the command name `appsurface`.

The first public verb family is `docs`, which replaces the earlier standalone `appsurfacedocs preview --repo .` idea with AppSurface-owned preview and export commands:

```bash
appsurface docs --repo .
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3
```

`appsurface docs` runs the same AppSurface Docs standalone host used by CI and integration tests. It forwards AppSurface Docs configuration into that host instead of duplicating harvesting, routing, static web asset, or MVC setup in the CLI. `appsurface docs export` starts that same host in-process, binds an internal loopback listener, and delegates static crawling plus CDN validation to the RazorWire export engine. `appsurface docs verify-archive` checks one catalog-pinned exact release tree locally before deploy.

The CLI also includes the first stable coverage command, `appsurface coverage gate`, for private-by-default CI coverage enforcement. It evaluates a local Cobertura XML file, writes JSON and Markdown reports, and can append the same Markdown to GitHub Actions step summaries without uploading coverage data to a hosted coverage service.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 2 release note](../../releases/v0.1.0-rc.2.md) for current release risk, migration guidance, and package readiness.

## Install

Install the published tool from NuGet:

```bash
dotnet tool install --global ForgeTrust.AppSurface.Cli --prerelease
```

Update an existing global install with the same package id:

```bash
dotnet tool update --global ForgeTrust.AppSurface.Cli --prerelease
```

Verify the installed global or tool-path command reports the package SemVer exactly:

```bash
appsurface --version
```

Prerelease installs print values such as `0.1.0-rc.1`, without a leading `v` or build metadata. Use the package artifact manifest, publish ledger, or release note when you need build provenance beyond the package version.

Use a local tool manifest when you want the command version pinned per repository:

```bash
dotnet new tool-manifest
dotnet tool install ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface --version
dotnet tool run appsurface docs --repo .
dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 95 --min-branch 85 --diff-base origin/main --min-patch-line 85 --min-patch-branch 85
```

Update the repo-scoped tool version with:

```bash
dotnet tool update ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface --version
```

## Commands

### `appsurface coverage gate`

Enforce a private coverage quality gate from an existing Cobertura XML file.

```bash
appsurface coverage gate \
  --coverage ./TestResults/coverage-merged/coverage.cobertura.xml \
  --min-line 95 \
  --min-branch 85 \
  --diff-base origin/main \
  --min-patch-line 85 \
  --min-patch-branch 85
```

`coverage gate` is the stable v1 coverage API. It does not run tests, merge shards, upload coverage, call GitHub APIs, or store trends. It reads one Cobertura file, evaluates line and branch percentages, optionally estimates changed-line and changed-branch coverage from `git diff`, writes `coverage-gate.json` and `coverage-gate.md`, prints the result, and exits nonzero when any configured threshold fails. When `$GITHUB_STEP_SUMMARY` is set, the Markdown report is appended by default so GitHub Actions logs show the gate result without requiring Codecov or another hosted dashboard. Use `--no-github-summary` when a workflow wants only file artifacts.

Options:

- `--coverage`: Cobertura XML file to evaluate. Defaults to `TestResults/coverage-merged/coverage.cobertura.xml`.
- `--min-line`: Minimum line coverage percentage from `0` through `100`. Defaults to `0`.
- `--min-branch`: Minimum branch coverage percentage from `0` through `100`. Defaults to `0`.
- `--diff-base`: Git ref or commit compared with `HEAD` for patch coverage. When set, the command runs `git diff --unified=0 --no-ext-diff --relative <base>...HEAD --` from the current directory.
- `--min-patch-line`: Minimum changed-line coverage percentage from `0` through `100`. Requires `--diff-base`. Omit it to report changed-line coverage without gating on it.
- `--min-patch-branch`: Minimum changed-branch coverage percentage from `0` through `100`. Requires `--diff-base`. Omit it to report changed-branch coverage without gating on it.
- `--output`: Directory for `coverage-gate.json` and `coverage-gate.md`. Defaults to the coverage file directory.
- `--github-summary`: Append Markdown to `$GITHUB_STEP_SUMMARY` when it is set. Enabled by default.
- `--no-github-summary`: Suppress GitHub step summary output.

The command accepts Cobertura root attributes such as `line-rate`, `branch-rate`, `lines-covered`, `lines-valid`, `branches-covered`, and `branches-valid`. XML parsing disables DTD processing and external resolution. Coverage counts must be non-negative, covered counts cannot exceed valid counts, rates must be from `0` through `1`, and zero valid line or branch counts fail with `ASCOV006` because a quality gate with no measurable denominator is misleading.

Patch coverage counts added or modified diff lines, intersects those lines with Cobertura `<class filename>` and `<line number hits>` entries, and reports covered/measurable lines. Changed lines that do not appear in the Cobertura line map are ignored for the denominator, which keeps docs, project files, generated artifacts, and other non-coverable edits from failing the patch gate. Changed-branch coverage uses Cobertura line-level `condition-coverage` counts on those same changed measurable lines, so ordinary changed statements without branch conditions do not inflate the branch denominator. When a diff has no measurable changed lines or no measurable changed branches, the corresponding patch metric reports `100%` and says so explicitly in Markdown.

Reports are private local artifacts:

```json
{
  "passed": false,
  "coverage": "/repo/TestResults/coverage-merged/coverage.cobertura.xml",
  "thresholds": {
    "line": 95,
    "branch": 85,
    "patchLine": 85,
    "patchBranch": 85
  },
  "line": {
    "covered": 80,
    "valid": 100,
    "percent": 80
  },
  "branch": {
    "covered": 30,
    "valid": 50,
    "percent": 60
  },
  "patchLine": {
    "diffBase": "origin/main",
    "changed": 28,
    "measurable": 20,
    "covered": 18,
    "percent": 90
  },
  "patchBranch": {
    "diffBase": "origin/main",
    "changed": 28,
    "measurable": 8,
    "covered": 7,
    "percent": 87.5
  }
}
```

Use this GitHub Actions shape when your repo already produces the AppSurface merged Cobertura artifact:

```yaml
- uses: actions/setup-dotnet@v5
  with:
    dotnet-version: 10.0.x
- run: git fetch --no-tags --depth=1 origin main
- run: dotnet tool restore
- run: ./scripts/coverage-solution.sh
- run: dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 95 --min-branch 85 --diff-base origin/main --min-patch-line 85 --min-patch-branch 85
- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: coverage-gate
    path: |
      TestResults/coverage-merged/coverage-gate.json
      TestResults/coverage-merged/coverage-gate.md
```

For other repositories, replace `./scripts/coverage-solution.sh` and the `--coverage` path with the command and Cobertura file path your test setup actually produces.

Diagnostics use `ASCOV###` codes so CI logs are searchable:

| Code | Meaning | Fix |
| --- | --- | --- |
| `ASCOV001` | The Cobertura file is missing or `--coverage` is blank. | Produce coverage first or pass the correct file path. |
| `ASCOV006` | The Cobertura file is malformed or has unsupported/misleading metrics. | Regenerate coverage and verify counts/rates on the root `<coverage>` element. |
| `ASCOV007` | A threshold is outside the `0` through `100` range or a patch threshold is missing `--diff-base`. | Correct `--min-line`, `--min-branch`, `--min-patch-line`, `--min-patch-branch`, or `--diff-base`. |
| `ASCOV008` | GitHub step summary could not be written. | Check `$GITHUB_STEP_SUMMARY` permissions or add `--no-github-summary`. |
| `ASCOV009` | The report output path is unsafe. | Use a dedicated artifact directory, not a filesystem root or working directory. |
| `ASCOV010` | The command could not run or read `git diff` for changed-line coverage. | Fetch the diff base or pass a valid local commit/ref to `--diff-base`. |
| `ASCOV020` | The gate ran successfully and coverage is below threshold. | Raise coverage or lower the threshold intentionally in source control. |

`appsurface coverage gate` intentionally does not expose run or merge orchestration yet. In this repository, use `./scripts/coverage-solution.sh` for AppSurface-specific run and merge orchestration until the shared runner engine is extracted into a package-owned implementation.

### `appsurface export`

Export a general AppSurface or RazorWire application through the product-facing CLI.

```bash
appsurface export --mode hybrid \
  --public-origin https://www.example.com \
  --live-origin https://api.example.com \
  --project ./src/MyApp/MyApp.csproj
```

The command shares the RazorWire export engine and accepts the same source choices as `razorwire export`: exactly one of `--url`, `--project`, or `--dll`, plus `--framework`, `--app-args`, and `--no-build` for launched apps. `--public-origin` rewrites same-origin canonical metadata to the public static host; it does not change crawl routing or app links. `--mode hybrid` by itself preserves application-style URLs and can support same-origin backend passthrough for RazorWire endpoints, including lazy anti-forgery token refresh. Hybrid still fails missing browser-delivered static assets with RazorWire `RWEXPORT003` diagnostics; fix missing CSS, image, script, stylesheet, module preload, icon, font, and asset-shaped preload/prefetch references instead of using hybrid as an asset-ignore mode. Adding `--live-origin` enables split-origin rewriting for RazorWire-managed live surfaces. `--hybrid-credentials auto` is the default and includes credentials for managed live calls when a live origin is configured; `omit` is an advanced escape hatch for anonymous split-origin live endpoints.

Static website deployment extras follow the same export boundary as `razorwire export`: seeds are app routes, exporter-owned provider artifacts such as `_redirects` are generated by the exporter, and opaque files such as `CNAME` belong in the deployment publish root through `--publish-root-extras ./deploy/export-extras.yml`. The manifest is explicit single-file copy only and fails with RazorWire `RWEXPORT007` when an extra is malformed, symlinked, reserved, collides with generated output, or targets an existing file. See the RazorWire CLI [Static website deployment extras](../../Web/ForgeTrust.RazorWire.Cli/README.md#static-website-deployment-extras) guidance for the provider table, GitHub Pages `CNAME` flow, and migration examples.

### `appsurface docs`

Preview AppSurface Docs for a repository checkout.

```bash
appsurface docs --repo .
```

Options:

- `--repo`, `-r`: Repository root to preview. Defaults to the current directory.
- `--urls`, `-u`: Explicit host URL binding, such as `http://127.0.0.1:5189`.
- `--port`, `-p`: Localhost-only AppSurface Web port shortcut forwarded to the AppSurface Docs host.
- `--all-hosts`: Binds `--port` previews to localhost and the all-hosts wildcard. Use this only when LAN, container, or other non-loopback preview access is intentional.
- `--strict`: Enables `AppSurfaceDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs preview root.
- `--public-origin`: Public origin used for absolute canonical metadata, such as `https://docs.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, or fragment. Do not include the docs route path. When unset, canonical metadata remains app-relative and app routes do not change.
- `--environment`, `-e`: Host environment forwarded to the AppSurface Docs host. Defaults to `Development` so the AppSurface Web deterministic per-workspace localhost URL is used when no endpoint is configured.
- `--startup-timeout-seconds`: Seconds to wait for the web host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

`appsurface docs preview` is an alias for the same behavior, kept so the old deferred shape maps cleanly to the new AppSurface command family.

When no endpoint is configured, the command runs the host in `Development` from the selected repository root and chooses the same stable localhost port for that repository or worktree. The CLI keeps routine ASP.NET Core lifecycle logs quiet, prints the resolved docs URL after Kestrel is listening, and then attempts to open that page in the system browser. If browser launch fails, the preview keeps running and reports the URL to open manually. Pass `--port`, `--urls`, `--environment Production`, or endpoint settings such as `ASPNETCORE_URLS`, HTTP/HTTPS ports, or Kestrel endpoints when you intentionally want to bypass that local preview default. `--port 5189` binds `http://localhost:5189`; add `--all-hosts` only when you intentionally want the wildcard binding `http://localhost:5189;http://*:5189`, which can expose the preview host beyond the local machine.

Packaged .NET tools usually do not carry ASP.NET Core static web asset manifests. The AppSurface CLI disables static web asset manifest loading for the preview host and relies on AppSurface Docs and RazorWire embedded asset fallbacks instead, so a global or local tool install stays self-contained.

### `appsurface docs export`

Export AppSurface Docs for a repository checkout to static files.

```bash
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
```

Use `cdn` when the output folder will be uploaded to GitHub Pages, Netlify, S3, or a plain CDN. Use `hybrid` only when the exported pages remain behind app-aware routing or live RazorWire frames, forms, streams, or islands. Missing browser assets are never live-route escapes: a CSS reference such as `url('/img/map-image.png')`, an image path with the wrong casing, or a forgotten script file fails with `RWEXPORT003` until the asset is copied, corrected, externalized, or removed.

Options:

- `--repo`, `-r`: Repository root to harvest. Defaults to the current directory.
- `--output`, `-o`: Output directory for exported static docs. Defaults to `dist/docs`; the directory must be missing or empty before export starts. CI should pass this explicitly and create a fresh output location per run.
- `--mode`, `-m`: Export mode. `cdn` is the default and validates plus rewrites managed URLs for plain static hosts. Use `hybrid` only when the output still sits behind application-aware routing. Hybrid tolerates missing live/page routes but still validates browser-delivered static assets.
- `--redirects`: Redirect alias materialization strategy. `html` is the default for GitHub Pages and generic static hosts; it writes tiny alias HTML fallback files. Use `--mode cdn --redirects netlify` for Netlify-compatible CDN publishing; export writes one root `_redirects` file with exact `301!` rules and does not emit alias HTML files. Netlify export rejects self-redirects and conflicting same-source rules after provider path encoding. `--redirects netlify` is rejected with `--mode hybrid`.
- `--seeds`: Optional path to a seed-route file. This is long-only because `-r` means `--repo` in AppSurface CLI commands.
- `--strict`: Enables `AppSurfaceDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails. This is separate from `--mode cdn`, which validates the emitted static artifact and preserves `RWEXPORT00x` diagnostics.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs root. When `--seeds` is omitted, export seeds `/` and this resolved docs root, `/docs` by default.
- `--public-origin`: Public origin used for absolute canonical metadata in exported pages, such as `https://docs.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, fragment, or userinfo. The export host still crawls loopback internally; this option keeps public canonical links from using that private listener. When unset, canonical metadata remains app-relative and app routes do not change.
- `--live-origin`: Optional live origin for split-origin hybrid docs export, such as `https://api.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, fragment, or userinfo.
- `--hybrid-credentials`: Credential behavior for RazorWire-managed live calls in split-origin hybrid export: `auto` (default), `include`, or `omit`. `auto` includes credentials when `--live-origin` is set.
- `--environment`, `-e`: Host environment forwarded to the AppSurface Docs host. Defaults to `Production` for export.
- `--startup-timeout-seconds`: Seconds to wait for the in-process AppSurface Docs host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

Export does not expose `--port`, `--urls`, or `--all-hosts`. It binds `http://127.0.0.1:0` internally, resolves the actual Kestrel listener, crawls that URL, then stops the host. Before crawling, it reads the AppSurface Docs route manifest from the in-process host, registers every public canonical docs route as an export seed, registers redirect aliases for source-shaped Markdown URLs and declared aliases, and writes `.appsurface-docs-route-manifest.json` into the export root. After all final files are materialized, export writes `.appsurface-docs-release-manifest.json`, hashes it, and prints a copy-ready `"releaseManifestSha256": "..."` catalog snippet. This keeps unlinked-but-public docs pages exportable, gives each alias a proven canonical target before the selected redirect strategy materializes it, lets exact version archives preserve the route identity that existed when the release was captured, and gives the runtime a catalog-pinned integrity proof for mounted archive HTML, JavaScript, CSS, SVG, and search payloads. Export fails with `ASDOCSARCHIVE005` when unsupported hidden files such as `.nojekyll` or `.well-known/...` are present, so export exact releases to a clean directory before copying the pin. RazorWire `RWEXPORT00x` diagnostics come from the shared export engine: for `RWEXPORT003`, add/copy the missing browser asset, correct path casing, make the URL external/data/hash-only when appropriate, or remove the reference. Do not hand-author `_redirects` inside the export output; use `--redirects netlify` so the exporter can validate and own that provider file. Use the generic `razorwire export` command when exporting arbitrary RazorWire apps via `--url`, `--project`, or `--dll`; use `appsurface docs export` when AppSurface owns the AppSurface Docs repository host.

`appsurface docs export` intentionally does not expose `--publish-root-extras`. Exact AppSurface Docs archives are immutable release artifacts: `.appsurface-docs-route-manifest.json` and `.appsurface-docs-release-manifest.json` describe the files that belong to the archive, and deployment-owned files such as `CNAME`, `.nojekyll`, or `/.well-known/security.txt` must live in the surrounding publish root outside the exact release tree. Do not place opaque extras inside immutable exact release archives unless a future archive contract explicitly supports them. If a future docs export path wires extras by mistake, it should reject them before host startup with RazorWire `RWEXPORT007 [release-archive-incompatible]`. For raw provider files, do not copy `/_redirects` or `/_headers`; use `--redirects netlify` for Netlify redirects and wait for structured headers support instead of raw copy-through.

### `appsurface docs verify-archive`

Verify one exact release tree from a version catalog without starting the docs web host.

```bash
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3 --trusted-release-root ./published-docs
```

Options:

- `--catalog`: Path to the AppSurface Docs version catalog JSON file.
- `--version`: Exact version identifier to verify.
- `--trusted-release-root`: Trusted release root used to resolve `exactTreePath` entries. When omitted, paths resolve the same way as runtime defaults: relative to the catalog directory.

The command loads the catalog, resolves the selected `exactTreePath`, and runs the same release archive verification used at runtime. Pass `--trusted-release-root` when the deployment sets `AppSurfaceDocs:Versioning:TrustedReleaseRootPath`; otherwise the local verifier may inspect a different relative tree than the host would mount. It exits nonzero when the version is missing, lacks a `releaseManifestSha256` pin, has a mismatched manifest digest, has missing or changed files, or contains handler-servable files not covered by the manifest. The catalog pin proves local archive integrity relative to trusted host configuration; it is not a signature or build provenance attestation.

Migration map for repo-owned AppSurface Docs export:

| Old path | New path |
| --- | --- |
| `razorwire export --project Web/ForgeTrust.AppSurface.Docs.Standalone/...` | `appsurface docs export --repo .` |
| `AppSurfaceDocs__Harvest__FailOnFailure=true` | `--strict` |
| `--mode cdn` | `--mode cdn` |
| `--seeds <file>` | `--seeds <file>` |
| `--output <dir>` | `--output <dir>` |
| `AppSurfaceDocs__Routing__PublicOrigin=https://docs.example.com` | `--public-origin https://docs.example.com` |
| `--project`, `--dll`, `--url`, `--app-args`, `--no-build`, `--framework` | use `appsurface export` for arbitrary app export |

## Development

Run the tool from source while developing:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs --repo .
```

Use `--strict` for CI-like validation when an all-failed harvest should stop the preview before the host begins serving.

Run the export command from source while developing:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs export --repo . --output ./dist/docs --mode cdn --strict
```
