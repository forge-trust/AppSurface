# RazorWire CLI

The **RazorWire CLI** is a command-line tool for managing RazorWire projects. Its primary feature is the ability to export a reactive RazorWire site into a static or hybrid directory for hosting on services like S3 or traditional web servers.

The CLI uses Runnable's command-first console mode. That means help and validation output are intentionally quiet, without Generic Host lifecycle banners, while real export runs still emit useful progress logs.

## Installation

The RazorWire CLI is packaged as a .NET tool with the command name `razorwire`.
Use an exact package version when running release builds so exports are reproducible.
The commands in this section require `ForgeTrust.Runnable.Web.RazorWire.Cli` to
exist on one of your configured NuGet sources, or for you to pass an explicit
local package source. Public package publishing is still manual until the
coordinated release automation tracked in #161 lands.

Run a published package without permanently installing it:

```bash
dnx ForgeTrust.Runnable.Web.RazorWire.Cli@<version> --yes -- export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

The equivalent SDK spelling is:

```bash
dotnet tool execute ForgeTrust.Runnable.Web.RazorWire.Cli@<version> --yes -- export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

Install the tool when you want a stable `razorwire` command on your PATH:

```bash
dotnet tool install --global ForgeTrust.Runnable.Web.RazorWire.Cli --version <version>
razorwire export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

During repository development, run the CLI directly from source:

```bash
dotnet run --project Web/ForgeTrust.Runnable.Web.RazorWire.Cli -- [command] [options]
```

When testing an unpublished package from a local folder, pack it first, pass that
folder as the package source, and keep the version exact:

```bash
dotnet pack Web/ForgeTrust.Runnable.Web.RazorWire.Cli -c Release -o ./artifacts/packages /p:PackageVersion=0.0.0-local.1
```

```bash
dnx ForgeTrust.Runnable.Web.RazorWire.Cli@0.0.0-local.1 --yes --source ./artifacts/packages -- --help
```

Do not combine `--version` and `--prerelease` for exact tool installs on recent SDKs; exact prerelease versions install without the extra flag.

## Commands

### Help and validation behavior

- Root help (`--help`) and command help (`export --help`) are command-first by design.
- Validation failures, such as invalid flags or missing source options, should surface actionable CLI output without host startup and shutdown chatter.
- Source validation failures include a concrete recovery path. When no source or multiple sources are provided, the error points to a single-source example such as `razorwire export --project ./MyApp.csproj --output ./dist` and reminds developers to run `razorwire export --help`.
- Successful export runs still keep command-owned progress output so long-running work remains understandable.

### `export`

Exports a RazorWire application to a static directory.

**Options:**
- **`-o|--output <path>`**: Output directory where the static files will be saved (default: `dist`).
- **`-r|--seeds <path>`**: Optional path to a file containing seed routes to crawl.
- **`-u|--url <url>`**: Base URL of a running application used for crawling.
- **`-p|--project <path.csproj>`**: Path to a .NET project to run automatically and export.
- **`-d|--dll <path.dll>`**: Path to a .NET DLL to run automatically and export.
- **`-f|--framework <TFM>`**: Target framework for project exports. Required when `--project` points at a multi-targeted project.
- **`--app-args <token>`**: Repeatable app-argument token to pass through when launching `--project` or `--dll`.
- **`--no-build`**: Project mode only. Skips the release publish step and reuses existing published output.

Exactly one source option is required: `--url`, `--project`, or `--dll`.

When launched app processes are started by the CLI (`--project` or `--dll`), they run in production environment (`DOTNET_ENVIRONMENT=Production`, `ASPNETCORE_ENVIRONMENT=Production`).

When `--project` is used:
- Project mode publishes a release build by default.
- The publish probe disables persistent build servers so command output capture cannot be held open by reused MSBuild nodes.
- Multi-targeted projects must pass `-f|--framework <TFM>` to select the target framework, for example `-f net6.0` or `--framework net7.0`; omitting it causes a CLI error before publish. `-f|--framework` can be combined with `--project` and `--no-build` when reusing existing published output.
- Project mode resolves the published app DLL and launches that DLL for crawling.
- Add `--no-build` to skip publishing and reuse existing published output.

When `--dll` is used:
- The CLI launches the provided DLL directly (no build or DLL resolution step).

For both `--project` and `--dll`:
- If you do not pass `--urls` via `--app-args`, the CLI appends `--urls http://127.0.0.1:0`.
- The CLI waits for startup, crawls the app, then shuts the process down automatically.

### RazorDocs versioned export notes

When the target app hosts RazorDocs:

- Export the live unreleased preview surface from its configured live docs root, such as `/docs` when versioning is off, `/docs/next` when versioning is on with defaults, or `/foo/bar/next` when the host sets `RazorDocs:Routing:RouteRootPath` to `/foo/bar`.
- Export exact published release trees as standalone static subtrees that already contain their own `index.html`, `search.html`, `search-index.json`, `search.css`, `search-client.js`, `minisearch.min.js`, section routes, and detail pages.
- Treat those exact release trees as immutable publish artifacts. The RazorDocs runtime mounts them later under `{RouteRootPath}/v/{version}` and may also mount the recommended one at `{RouteRootPath}`.
- The exporter recognizes custom-root RazorDocs pages by their RazorDocs client configuration or `doc-content` frame, not just by a `/docs` URL prefix. This keeps static partial generation working for mounted roots such as `/foo/bar/next`.
- Use `--seeds` when you want deterministic seeds for docs-specific surfaces instead of relying only on crawl discovery.

**Example:**

```bash
dotnet run --project Web/ForgeTrust.Runnable.Web.RazorWire.Cli -- export -o ./dist -u http://localhost:5233
```

```bash
dotnet run --project Web/ForgeTrust.Runnable.Web.RazorWire.Cli -- export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

```bash
dotnet run --project Web/ForgeTrust.Runnable.Web.RazorWire.Cli -- export -o ./dist -d ./bin/Release/net10.0/MyApp.dll --app-args --urls --app-args http://127.0.0.1:5009
```
