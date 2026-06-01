# RazorWire CLI

The **RazorWire CLI** is a command-line tool for managing RazorWire projects. Its primary feature is exporting a reactive RazorWire site into CDN-ready static files by default, with an opt-in hybrid mode for deployments that still provide server routing.

The CLI uses AppSurface's command-first console mode. That means help and validation output are intentionally quiet, without Generic Host lifecycle banners, while real export runs still emit useful progress logs.

## Start here: export the sample

During repository development, the fastest confidence check is to export the RazorWire MVC sample from source:

```bash
dotnet run --project Web/ForgeTrust.RazorWire.Cli -- export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
test -f ./dist/index.html
```

A successful run publishes the sample, starts it on an ephemeral loopback URL, crawls the discovered pages, writes static files under `./dist`, and shuts the target app down automatically. Inspect `./dist/index.html` first; it proves the exporter emitted the root artifact.

## Installation

The RazorWire CLI project is configured as a .NET tool with the command name
`razorwire`. Use an exact package version when running release builds so exports
are reproducible. The package chooser still excludes `ForgeTrust.RazorWire.Cli`
from the direct-install matrix until issue #171 lands stable public tool
packaging, so the commands in this section require the package to exist on one
of your configured NuGet sources or for you to pass an explicit local package
source. During normal repository development, prefer the source-run command below.

Run a published package without permanently installing it:

```bash
dnx ForgeTrust.RazorWire.Cli@<version> --yes -- export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

The equivalent SDK spelling is:

```bash
dotnet tool execute ForgeTrust.RazorWire.Cli@<version> --yes -- export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

Install the tool when you want a stable `razorwire` command on your PATH:

```bash
dotnet tool install --global ForgeTrust.RazorWire.Cli --version <version>
razorwire export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

During repository development, run the CLI directly from source:

```bash
dotnet run --project Web/ForgeTrust.RazorWire.Cli -- [command] [options]
```

When testing an unpublished package from a local folder, pack it first, pass that
folder as the package source, and keep the version exact:

```bash
dotnet pack Web/ForgeTrust.RazorWire.Cli -c Release -o ./artifacts/packages /p:PackageVersion=0.0.0-local.1
```

```bash
dnx ForgeTrust.RazorWire.Cli@0.0.0-local.1 --yes --source ./artifacts/packages -- --help
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
- **`-m|--mode <cdn|hybrid>`**: Export mode. `cdn` is the default and rewrites managed internal URLs to emitted static artifacts. `hybrid` preserves application-style internal URLs for hosts that still route extensionless paths.
- **`--live-origin <origin>`**: Optional live origin for split-origin hybrid exports, such as `https://api.example.com`. The value must be an `http` or `https` origin only, with no path, query, fragment, or userinfo.
- **`--hybrid-credentials <auto|include|omit>`**: Credential behavior for RazorWire-managed live calls. `auto` is the default and includes credentials when `--live-origin` is set; use `omit` only for intentionally anonymous live endpoints.
- **`--app-args <token>`**: Repeatable app-argument token to pass through when launching `--project` or `--dll`.
- **`--no-build`**: Project mode only. Skips the release publish step and reuses existing published output.

Exactly one source option is required: `--url`, `--project`, or `--dll`.

#### Export modes

`cdn` mode is the default because most `razorwire export` runs produce folders that are uploaded to plain static hosting or a CDN. It emits extension-backed artifact URLs for exporter-managed internal references:

- `/` is emitted as `index.html` and may still be referenced as `/`.
- `/about` is emitted as `about.html` and internal references rewrite to `/about.html`.
- `/docs/start` is emitted as `docs/start.html` and internal references rewrite to `/docs/start.html`.
- Dotted page slugs still follow page-route rules: `/docs/web/forgetrust.razorwire` is emitted as `docs/web/forgetrust.razorwire.html`, while assets that return non-HTML content keep their real extension.
- AppSurface Docs content frames also emit `.partial.html` artifacts when a `doc-content` frame exists, so static frame navigation can fetch the content island.
- Host-registered seed routes are crawled in addition to configured seed files or in-memory defaults. This lets a host with its own route graph export public pages that are not linked from the initial crawl roots.
- Redirect aliases registered by the host are validated after their canonical routes are proven. The default `html` redirect strategy emits tiny alias HTML fallback files after canonical artifacts; provider integrations can instead select `netlify` to write exact root `_redirects` rules and skip alias HTML files. If a host registers aliases, it should also register or otherwise expose their canonical routes as crawl seeds so validation can prove the canonical artifacts exist.
- Assets that already have extensions, such as `/css/site.css`, `/img/logo.png`, or `/_content/.../razorwire.js`, keep their path. Cache-busting query strings on assets are allowed only when the query-free path maps to an exported file.
- The conventional `/_appsurface/errors/404` page, when available, is emitted as `404.html` and participates in the same CDN validation and URL rewriting.

CDN validation fails the export when exporter-managed dependencies cannot be represented as static artifacts. Diagnostics use stable codes and include the discovered surface, such as `<img src>`, `<a href>`, `stylesheet url()`, or `stylesheet @import string`, plus the normalized path the exporter tried to prove:

- `RWEXPORT001`: a server-fetched frame route did not materialize. Add or seed the frame route, or switch to `--mode hybrid` when a live server owns it.
- `RWEXPORT002`: a query-bearing frame route cannot be represented as one static artifact. Export a query-free route, split the frame into static pages, or keep server routing with `--mode hybrid`.
- `RWEXPORT003`: a required internal asset did not materialize. Add the asset route, correct path casing, make the reference external/data/hash-only, or use `--mode hybrid`.
- `RWEXPORT004`: a managed internal URL could not be rewritten to an emitted artifact URL. Seed or expose the target route so it emits an artifact, or mark authoring-only anchors with `data-rw-export-ignore`.
- `RWEXPORT005`: a registered redirect alias cannot safely point at its canonical route, collides with another route or provider redirect output, or was already crawled as a normal HTML page. Fix the host redirect registration so aliases map to exported canonical routes without artifact collisions.
- `RWEXPORT006`: an export found anti-forgery behavior that cannot run safely in the selected mode. CDN mode rejects any anti-forgery surface because a plain static host cannot mint runtime tokens. Hybrid RazorWire-managed forms are auto-converted to lazy token refresh when they have a safe app-owned action and credentialed split-origin live calls are enabled. Export fails early when the form opts out, posts to an external action, uses `--hybrid-credentials omit` with `--live-origin`, or is not managed by RazorWire.

`hybrid` mode preserves the older application-style URL behavior. Use it when the exported directory will still be served by infrastructure that resolves extensionless URLs, dynamic frame endpoints, or other live-server behavior. Hybrid mode logs missing discovered dependencies but does not enforce CDN static-safety validation. Safe RazorWire forms with static anti-forgery tokens are converted to lazy runtime refresh in hybrid mode even without `--live-origin`; this supports same-origin CDN passthrough to the backend for RazorWire endpoints.

Split-origin hybrid export is enabled by adding `--live-origin` to `--mode hybrid`:

```bash
razorwire export --mode hybrid \
  --live-origin https://api.example.com \
  --project ./MyApp.csproj
```

With a live origin, the exporter rewrites RazorWire-owned runtime surfaces to the live app: `rw-stream-source` URLs, server-backed island frame sources, safe RazorWire forms, and path-base-aware lazy anti-forgery endpoints. If a RazorWire form contains crawler-minted `__RequestVerificationToken` inputs, export removes those stale static token inputs and marks the form `data-rw-antiforgery="lazy"` so the browser fetches a fresh token from `/_rw/antiforgery/token` on first intent or immediately before submit. When the crawl target is mounted under a path base, the exporter strips that crawl path base from `data-rw-antiforgery-endpoint` before the runtime combines it with `--live-origin`; otherwise split-origin deployments would call the crawler path instead of the live app root. The runtime wakes the live origin only when the user interacts with the form, not on every static page visit.

The per-form `rw-antiforgery="lazy"` TagHelper attribute is optional. Use it as an assertion when authoring a form that must lazy-refresh, but split-origin export applies the safe conversion automatically. `rw-antiforgery="off"` is an explicit opt-out and will fail export if the form still contains a static anti-forgery token.

Redirect strategy is a host-integration setting, not a generic `razorwire export` CLI option. `ExportContext.AddRedirectAlias(...)` is the preferred API for registering alias-to-canonical relationships; `AddRedirectArtifact(...)` remains as a compatibility wrapper for older integrations. `ExportRedirectStrategy.Html` works on generic static hosts such as GitHub Pages. `ExportRedirectStrategy.Netlify` is for CDN exports published to Netlify-compatible providers: it writes one publish-root `_redirects` file, serializes exact site-local paths with per-segment percent encoding, uses `301!`, de-duplicates exact serialized source/target pairs, rejects self-redirects after serialization, rejects same-source/different-target rules, rejects aliases that conflict with exported `_redirects`, and never uses `PublicOrigin` or emitted `.html` artifact URLs as rule targets.

CDN mode validates the static references the exporter owns and can see while crawling HTML and CSS: parser-discovered page links, Turbo Frame sources, supported HTML asset references, `<link rel="canonical">` values that point at managed app routes, `<img>` and `<source>` `srcset` candidates, CSS `url(...)` references, and both `@import url(...)` and string-form `@import "..."` stylesheet dependencies. It does not prove arbitrary app-authored JavaScript `fetch` calls, form posts, Server-Sent Events, import maps, or other runtime behavior that is not represented as exporter-managed markup or CSS references.

Parser-backed discovery can surface valid HTML or CSS references that older exporter versions missed. After upgrading, a new `RWEXPORT###` failure may be correct rather than a regression: export the route or asset, fix path casing, mark authoring-only anchors with `data-rw-export-ignore`, or choose `--mode hybrid` when the dependency is intentionally served by live infrastructure.

CDN export skips relative anchors that point at common source or project file extensions, such as `./Program.cs` or `../Project.csproj`, because those links are usually for GitHub and editor navigation rather than static-site dependencies. For other authoring-only anchors in app-rendered HTML, use `data-rw-export-ignore`; the anchor remains rendered and clickable, but CDN export will not crawl, validate, or rewrite its `href`.

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
- The launched app inherits the parent process environment, while the CLI forces `ASPNETCORE_ENVIRONMENT=Production` and `DOTNET_ENVIRONMENT=Production` for deployed-runtime semantics.
- The CLI waits for startup, crawls the app, then shuts the process down automatically.
- Pass multiple target-app arguments by repeating `--app-args` once per token. For example, `--app-args --urls --app-args http://127.0.0.1:5009` launches the app with `--urls http://127.0.0.1:5009`.

#### If export fails

Process failures are reported with the command stage, exit code or startup exception when available, captured target-app stdout/stderr, and the next recovery step. Target-app output is fully captured for the current export attempt; the CLI does not apply a line-count or byte-count truncation limit. Common branches:

- **Missing source option or multiple sources**: choose exactly one of `--url`, `--project`, or `--dll`; run `razorwire export --help` for the current command shape.
- **Multi-targeted project without `--framework`**: pass `-f|--framework <TFM>`, such as `--framework net10.0`, so publish and DLL resolution use the same target.
- **Project publish fails**: read the captured `dotnet publish` stdout/stderr, fix the build error, and rerun the same export command.
- **Target app exits before listening**: inspect the captured target-app output in the error. The app failed during startup before the exporter could discover a base URL.
- **Readiness timeout after a listening URL**: verify the app can serve requests at the emitted loopback URL and that startup work is not blocking the first response.
- **Cancellation or interrupted export**: the CLI performs best-effort shutdown of the launched target app before returning.

### AppSurface Docs versioned export notes

When the target app hosts AppSurface Docs:

- Use `appsurface docs export --repo .` for AppSurface's own repository docs surface. That command starts the AppSurface Docs standalone host in-process, uses the same packaged static-asset fallbacks as preview, derives default seeds from AppSurface Docs routing, and keeps `-r` reserved for `--repo`.
- Export the live unreleased preview surface from its configured live docs root, such as `/docs` when versioning is off, `/docs/next` when versioning is on with defaults, or `/foo/bar/next` when the host sets `AppSurfaceDocs:Routing:RouteRootPath` to `/foo/bar`.
- Export exact published release trees as standalone static subtrees that already contain their own `.appsurface-docs-route-manifest.json`, `.appsurface-docs-release-manifest.json`, `index.html`, `search.html`, `search-index.json`, `search.css`, `search-client.js`, `minisearch.min.js`, section routes, and detail pages.
- Keep exact-tree `search-index.json` document paths canonical and deployment-independent. Valid release search rows use root-relative `/docs/...` paths; they do not include request path bases, custom route roots, origins, executable schemes, traversal, or docs operational routes. AppSurface Docs rebases those canonical paths when the archive is mounted at `{RouteRootPath}`, `{RouteRootPath}/v/{version}`, a custom route root, or a virtual directory.
- The hidden `.appsurface-docs-route-manifest.json` file freezes canonical routes plus source-shaped and declared aliases from the release being exported. AppSurface Docs uses it when mounting exact archives so old aliases redirect to the archive-local canonical page instead of using whatever route rules current docs have later.
- The hidden `.appsurface-docs-release-manifest.json` file records the final exported files and SHA-256 digests after materialization. Export logs the manifest SHA-256 and a copy-ready `"releaseManifestSha256": "..."` snippet for the AppSurface Docs version catalog.
- Export fails with `ASDOCSARCHIVE005` when the output directory contains unsupported hidden paths such as `.nojekyll` or `.well-known/...`; export to a clean directory before pinning the manifest digest.
- Plain `razorwire export` remains generic and does not emit `.appsurface-docs-release-manifest.json`; use `appsurface docs export` for AppSurface Docs exact release archives that need a catalog pin.
- Treat those exact release trees as immutable publish artifacts. The AppSurface Docs runtime mounts them later under `{RouteRootPath}/v/{version}` and may also mount the recommended one at `{RouteRootPath}`.
- The exporter recognizes custom-root AppSurface Docs pages by their AppSurface Docs client configuration or `doc-content` frame, not just by a `/docs` URL prefix. This keeps static partial generation working for mounted roots such as `/foo/bar/next`.
- Use `--seeds` when you want deterministic seeds for docs-specific surfaces instead of relying only on crawl discovery.
- For release publishing, set `AppSurfaceDocs__Harvest__FailOnFailure=true` in the parent environment before `razorwire export --project` or `razorwire export --dll`. The launched target app inherits that setting, fails before listening when aggregate harvest health is `Failed`, and the export command surfaces the target app's startup output. The strict exception summary is redacted, but ordinary target host logs may still contain operator diagnostics such as repository paths or raw exception messages.

Keep `razorwire export` for arbitrary RazorWire apps where the CLI must launch a `--project`, launch a `--dll`, or crawl a pre-running `--url`. Use `appsurface docs export` when the AppSurface CLI owns the AppSurface Docs repository host and should avoid the generic child-process startup path.

**Example:**

```bash
dotnet run --project Web/ForgeTrust.RazorWire.Cli -- export -o ./dist -u http://localhost:5233
```

```bash
dotnet run --project Web/ForgeTrust.RazorWire.Cli -- export -o ./dist -p ./examples/razorwire-mvc/RazorWireWebExample.csproj
```

```bash
dotnet run --project Web/ForgeTrust.RazorWire.Cli -- export -o ./dist -d ./bin/Release/net10.0/MyApp.dll --app-args --urls --app-args http://127.0.0.1:5009
```

## Contributor process execution policy

RazorWire CLI owns two process boundaries:

| Boundary | Use for | Contract |
|---|---|---|
| Command executor | Finite commands such as publish probes and MSBuild property reads. | Return exit code, stdout, and stderr as data; do not throw for non-zero exits or ordinary startup failures; propagate cancellation. |
| Target app process | Long-running app launched for crawling. | Raise stdout/stderr as non-empty lines, surface startup failure before readiness timeout, raise exit after output drain when possible, and perform best-effort process-tree cleanup on disposal. |

Keep command arguments tokenized as ordered argument lists. Do not build shell command strings. User-facing docs should describe observable behavior and recovery steps; implementation dependencies belong in contributor docs and tests.
