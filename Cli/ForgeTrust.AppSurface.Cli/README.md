# AppSurface CLI

The **AppSurface CLI** is the command-line home for repository-level AppSurface workflows. It is packaged as a .NET tool with the command name `appsurface`.

The first public verb family is `docs`, which replaces the earlier standalone `appsurfacedocs preview --repo .` idea with AppSurface-owned preview and export commands:

```bash
appsurface docs --repo .
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3
```

`appsurface docs` runs the same AppSurface Docs standalone host used by CI and integration tests. It forwards AppSurface Docs configuration into that host instead of duplicating harvesting, routing, static web asset, or MVC setup in the CLI. `appsurface docs export` starts that same host in-process, binds an internal loopback listener, and delegates static crawling plus CDN validation to the RazorWire export engine. `appsurface docs verify-archive` checks one catalog-pinned exact release tree locally before deploy.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 1 release note](../../releases/v0.1.0-rc.1.md) for current release risk, migration guidance, and package readiness.

## Install

Install the published tool from NuGet:

```bash
dotnet tool install --global ForgeTrust.AppSurface.Cli --prerelease
```

Update an existing global install with the same package id:

```bash
dotnet tool update --global ForgeTrust.AppSurface.Cli --prerelease
```

Use a local tool manifest when you want the command version pinned per repository:

```bash
dotnet new tool-manifest
dotnet tool install ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface docs --repo .
```

Update the repo-scoped tool version with:

```bash
dotnet tool update ForgeTrust.AppSurface.Cli --prerelease
```

## Commands

### `appsurface export`

Export a general AppSurface or RazorWire application through the product-facing CLI.

```bash
appsurface export --mode hybrid \
  --public-origin https://www.example.com \
  --live-origin https://api.example.com \
  --project ./src/MyApp/MyApp.csproj
```

The command shares the RazorWire export engine and accepts the same source choices as `razorwire export`: exactly one of `--url`, `--project`, or `--dll`, plus `--framework`, `--app-args`, and `--no-build` for launched apps. `--public-origin` rewrites same-origin canonical metadata to the public static host; it does not change crawl routing or app links. `--mode hybrid` by itself preserves application-style URLs and can support same-origin backend passthrough for RazorWire endpoints, including lazy anti-forgery token refresh. Adding `--live-origin` enables split-origin rewriting for RazorWire-managed live surfaces. `--hybrid-credentials auto` is the default and includes credentials for managed live calls when a live origin is configured; `omit` is an advanced escape hatch for anonymous split-origin live endpoints.

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

Options:

- `--repo`, `-r`: Repository root to harvest. Defaults to the current directory.
- `--output`, `-o`: Output directory for exported static docs. Defaults to `dist/docs`; the directory must be missing or empty before export starts. CI should pass this explicitly and create a fresh output location per run.
- `--mode`, `-m`: Export mode. `cdn` is the default and validates plus rewrites managed URLs for plain static hosts. Use `hybrid` only when the output still sits behind application-aware routing.
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

Export does not expose `--port`, `--urls`, or `--all-hosts`. It binds `http://127.0.0.1:0` internally, resolves the actual Kestrel listener, crawls that URL, then stops the host. Before crawling, it reads the AppSurface Docs route manifest from the in-process host, registers every public canonical docs route as an export seed, registers redirect aliases for source-shaped Markdown URLs and declared aliases, and writes `.appsurface-docs-route-manifest.json` into the export root. After all final files are materialized, export writes `.appsurface-docs-release-manifest.json`, hashes it, and prints a copy-ready `"releaseManifestSha256": "..."` catalog snippet. This keeps unlinked-but-public docs pages exportable, gives each alias a proven canonical target before the selected redirect strategy materializes it, lets exact version archives preserve the route identity that existed when the release was captured, and gives the runtime a catalog-pinned integrity proof for archive SVG. Export fails with `ASDOCSARCHIVE005` when unsupported hidden files such as `.nojekyll` or `.well-known/...` are present, so export exact releases to a clean directory before copying the pin. Do not hand-author `_redirects` inside the export output; use `--redirects netlify` so the exporter can validate and own that provider file. Use the generic `razorwire export` command when exporting arbitrary RazorWire apps via `--url`, `--project`, or `--dll`; use `appsurface docs export` when AppSurface owns the AppSurface Docs repository host.

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
