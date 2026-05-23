# AppSurface CLI

The **AppSurface CLI** is the command-line home for repository-level AppSurface workflows. It is packaged as a .NET tool with the command name `appsurface`.

The first public verb family is `docs`, which replaces the earlier standalone `appsurfacedocs preview --repo .` idea with AppSurface-owned preview and export commands:

```bash
appsurface docs --repo .
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
```

`appsurface docs` runs the same AppSurface Docs standalone host used by CI and integration tests. It forwards AppSurface Docs configuration into that host instead of duplicating harvesting, routing, static web asset, or MVC setup in the CLI. `appsurface docs export` starts that same host in-process, binds an internal loopback listener, and delegates static crawling plus CDN validation to the RazorWire export engine.

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

### `appsurface docs`

Preview AppSurface Docs for a repository checkout.

```bash
appsurface docs --repo .
```

Options:

- `--repo`, `-r`: Repository root to preview. Defaults to the current directory.
- `--urls`, `-u`: Explicit host URL binding, such as `http://127.0.0.1:5189`.
- `--port`, `-p`: AppSurface Web port shortcut forwarded to the AppSurface Docs host.
- `--strict`: Enables `AppSurfaceDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs preview root.
- `--public-origin`: Public origin used for absolute canonical metadata, such as `https://docs.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, or fragment. Do not include the docs route path. When unset, canonical metadata remains app-relative and app routes do not change.
- `--environment`, `-e`: Host environment forwarded to the AppSurface Docs host. Defaults to `Development` so the AppSurface Web deterministic per-workspace localhost URL is used when no endpoint is configured.
- `--startup-timeout-seconds`: Seconds to wait for the web host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

`appsurface docs preview` is an alias for the same behavior, kept so the old deferred shape maps cleanly to the new AppSurface command family.

When no endpoint is configured, the command runs the host in `Development` from the selected repository root and chooses the same stable localhost port for that repository or worktree. The CLI keeps routine ASP.NET Core lifecycle logs quiet, prints the resolved docs URL after Kestrel is listening, and then attempts to open that page in the system browser. If browser launch fails, the preview keeps running and reports the URL to open manually. Pass `--port`, `--urls`, `--environment Production`, or endpoint settings such as `ASPNETCORE_URLS`, HTTP/HTTPS ports, or Kestrel endpoints when you intentionally want to bypass that local preview default.

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
- `--seeds`: Optional path to a seed-route file. This is long-only because `-r` means `--repo` in AppSurface CLI commands.
- `--strict`: Enables `AppSurfaceDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails. This is separate from `--mode cdn`, which validates the emitted static artifact and preserves `RWEXPORT00x` diagnostics.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs root. When `--seeds` is omitted, export seeds `/` and this resolved docs root, `/docs` by default.
- `--public-origin`: Public origin used for absolute canonical metadata in exported pages, such as `https://docs.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, or fragment. The export host still crawls loopback internally; this option keeps public canonical links from using that private listener. When unset, canonical metadata remains app-relative and app routes do not change.
- `--environment`, `-e`: Host environment forwarded to the AppSurface Docs host. Defaults to `Production` for export.
- `--startup-timeout-seconds`: Seconds to wait for the in-process AppSurface Docs host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

Export does not expose `--port` or `--urls`. It binds `http://127.0.0.1:0` internally, resolves the actual Kestrel listener, crawls that URL, then stops the host. Before crawling, it reads the AppSurface Docs route manifest from the in-process host, registers every public canonical docs route as an export seed, registers redirect artifacts for source-shaped Markdown URLs and declared aliases, and writes `.appsurface-docs-route-manifest.json` into the export root. This keeps unlinked-but-public docs pages exportable, gives each alias artifact a proven canonical target, and lets exact version archives preserve the route identity that existed when the release was captured. Use the generic `razorwire export` command when exporting arbitrary RazorWire apps via `--url`, `--project`, or `--dll`; use `appsurface docs export` when AppSurface owns the AppSurface Docs repository host.

Migration map for repo-owned AppSurface Docs export:

| Old path | New path |
| --- | --- |
| `razorwire export --project Web/ForgeTrust.AppSurface.Docs.Standalone/...` | `appsurface docs export --repo .` |
| `AppSurfaceDocs__Harvest__FailOnFailure=true` | `--strict` |
| `--mode cdn` | `--mode cdn` |
| `--seeds <file>` | `--seeds <file>` |
| `--output <dir>` | `--output <dir>` |
| `AppSurfaceDocs__Routing__PublicOrigin=https://docs.example.com` | `--public-origin https://docs.example.com` |
| `--project`, `--dll`, `--url`, `--app-args`, `--no-build`, `--framework` | remain RazorWire-only for arbitrary app export |

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
