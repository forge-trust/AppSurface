# AppSurface CLI

The **AppSurface CLI** is the command-line home for repository-level AppSurface workflows. It is packaged as a .NET tool with the command name `appsurface`.

The first public verb family is `docs`, which replaces the earlier standalone `razordocs preview --repo .` idea with AppSurface-owned preview and export commands:

```bash
appsurface docs --repo . --urls http://127.0.0.1:5189
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
```

`appsurface docs` runs the same RazorDocs standalone host used by CI and integration tests. It forwards RazorDocs configuration into that host instead of duplicating harvesting, routing, static web asset, or MVC setup in the CLI. `appsurface docs export` starts that same host in-process, binds an internal loopback listener, and delegates static crawling plus CDN validation to the RazorWire export engine.

## Commands

### `appsurface docs`

Preview RazorDocs for a repository checkout.

```bash
appsurface docs --repo . --port 5189
```

Options:

- `--repo`, `-r`: Repository root to preview. Defaults to the current directory.
- `--urls`, `-u`: Explicit host URL binding, such as `http://127.0.0.1:5189`.
- `--port`, `-p`: AppSurface Web port shortcut forwarded to the RazorDocs host.
- `--strict`: Enables `RazorDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs preview root.
- `--environment`, `-e`: Host environment forwarded to the RazorDocs host.
- `--startup-timeout-seconds`: Seconds to wait for the web host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

`appsurface docs preview` is an alias for the same behavior, kept so the old deferred shape maps cleanly to the new AppSurface command family.

### `appsurface docs export`

Export RazorDocs for a repository checkout to static files.

```bash
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
```

Options:

- `--repo`, `-r`: Repository root to harvest. Defaults to the current directory.
- `--output`, `-o`: Output directory for exported static docs. Defaults to `dist/docs`; CI should pass this explicitly.
- `--mode`, `-m`: Export mode. `cdn` is the default and validates plus rewrites managed URLs for plain static hosts. Use `hybrid` only when the output still sits behind application-aware routing.
- `--seeds`: Optional path to a seed-route file. This is long-only because `-r` means `--repo` in AppSurface CLI commands.
- `--strict`: Enables `RazorDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails. This is separate from `--mode cdn`, which validates the emitted static artifact and preserves `RWEXPORT00x` diagnostics.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs root. When `--seeds` is omitted, export seeds `/` and this resolved docs root, `/docs` by default.
- `--environment`, `-e`: Host environment forwarded to the RazorDocs host. Defaults to `Production` for export.
- `--startup-timeout-seconds`: Seconds to wait for the in-process RazorDocs host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

Export does not expose `--port` or `--urls`. It binds `http://127.0.0.1:0` internally, resolves the actual Kestrel listener, crawls that URL, then stops the host. Use the generic `razorwire export` command when exporting arbitrary RazorWire apps via `--url`, `--project`, or `--dll`; use `appsurface docs export` when AppSurface owns the RazorDocs repository host.

Migration map for repo-owned AppSurface Docs export:

| Old path | New path |
| --- | --- |
| `razorwire export --project Web/ForgeTrust.AppSurface.Docs.Standalone/...` | `appsurface docs export --repo .` |
| `RazorDocs__Harvest__FailOnFailure=true` | `--strict` |
| `--mode cdn` | `--mode cdn` |
| `--seeds <file>` | `--seeds <file>` |
| `--output <dir>` | `--output <dir>` |
| `--project`, `--dll`, `--url`, `--app-args`, `--no-build`, `--framework` | remain RazorWire-only for arbitrary app export |

## Development

Run the tool from source while developing:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs --repo . --urls http://127.0.0.1:5189
```

Use `--strict` for CI-like validation when an all-failed harvest should stop the preview before the host begins serving.

Run the export command from source while developing:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs export --repo . --output ./dist/docs --mode cdn --strict
```
