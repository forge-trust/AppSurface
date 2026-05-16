# AppSurface CLI

The **AppSurface CLI** is the command-line home for repository-level AppSurface workflows. It is packaged as a .NET tool with the command name `appsurface`.

The first public verb is `docs`, which replaces the earlier standalone `razordocs preview --repo .` idea with an AppSurface-owned command:

```bash
appsurface docs --repo .
```

`appsurface docs` runs the same RazorDocs standalone host used by CI and integration tests. It forwards RazorDocs configuration into that host instead of duplicating harvesting, routing, static web asset, or MVC setup in the CLI.

## Commands

### `appsurface docs`

Preview RazorDocs for a repository checkout.

```bash
appsurface docs --repo .
```

Options:

- `--repo`, `-r`: Repository root to preview. Defaults to the current directory.
- `--urls`, `-u`: Explicit host URL binding, such as `http://127.0.0.1:5189`.
- `--port`, `-p`: AppSurface Web port shortcut forwarded to the RazorDocs host.
- `--strict`: Enables `RazorDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs preview root.
- `--environment`, `-e`: Host environment forwarded to the RazorDocs host. Defaults to `Development` so the AppSurface Web deterministic per-workspace localhost URL is used when no endpoint is supplied.
- `--startup-timeout-seconds`: Seconds to wait for the web host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

`appsurface docs preview` is an alias for the same behavior, kept so the old deferred shape maps cleanly to the new AppSurface command family.

When neither `--urls` nor `--port` is supplied, the command runs the host in `Development` and lets AppSurface Web choose the stable localhost port for the current repository or worktree. Use the startup log as the source of truth for the selected URL. Pass `--port`, `--urls`, or `--environment Production` when you intentionally want to bypass that local preview default.

## Development

Run the tool from source while developing:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs --repo .
```

Use `--strict` for CI-like validation when an all-failed harvest should stop the preview before the host begins serving.
