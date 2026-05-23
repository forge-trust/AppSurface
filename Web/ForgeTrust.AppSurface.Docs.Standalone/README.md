# ForgeTrust.AppSurface.Docs.Standalone

AppSurface host for serving or exporting AppSurface Docs as an application.

## What it is for

This project is the thin executable wrapper around the reusable [ForgeTrust.AppSurface.Docs](../ForgeTrust.AppSurface.Docs/README.md) package. It exists so the docs surface can run as:

- a local standalone site during development
- the export target in CI
- a smoke-testable host that proves the package seam stays honest

For public command-line workflows, use the AppSurface CLI as the public command surface:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs --repo .
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs export --repo . --output ./dist/docs --mode cdn --strict
```

The CLI delegates to this standalone host, so the host remains the source of truth for AppSurface Docs startup, static web assets, routes, and configuration binding. The `appsurface docs` preview command starts it in-process, keeps routine ASP.NET Core lifecycle output quiet, prints and opens the resolved docs URL after Kestrel is listening, and then runs until shutdown. Direct standalone-host runs should treat the host startup logs as the source of truth for the selected URL and binding. Export starts the host in-process through `AppSurfaceDocsStandaloneHost.CreateBuilder`, binds `http://127.0.0.1:0`, crawls the resolved loopback address with RazorWire export, then stops and disposes the host.

## Entry Point

The app boots through [Program.cs](./Program.cs), which delegates to `AppSurfaceDocsStandaloneHost`.
`AppSurfaceDocsStandaloneHost` is the reusable host entry point for this executable:

- `RunAsync(string[] args)` starts the standalone app and is what `Program.cs` uses.
- `CreateBuilder(string[] args, IEnvironmentProvider? environmentProvider = null)` returns an `IHostBuilder` without starting it.
- `CreateBuilder(string[] args, IEnvironmentProvider? environmentProvider, Action<WebOptions>? configureOptions)` adds the same builder seam plus web-option customization for package-hosted tools.

Use `CreateBuilder` when a test or tool needs the real standalone host in-process. It keeps the same `AppSurfaceDocsWebModule`, MVC routes, static web assets, and `AppSurface Docs` configuration binding as the executable path while avoiding a shell-out to `dotnet run`.

Do not duplicate standalone setup in test fixtures. If a scenario needs different URLs, repository roots, contributor templates, or environment behavior, pass those through command-line configuration or the optional environment provider so the normal host builder still owns the app shape.
`CreateBuilder` is lower level than `RunAsync`: callers that build and start the host themselves should pass `--urls`, `--port`, or configure the web host before `Build()` instead of relying on the executable startup path's development-port fallback.
The builder pins this standalone assembly as the host entry point identity so in-process callers, including xUnit, resolve the same static web asset manifest as the executable.

The optional `configureOptions` callback is for host-shape seams that must stay on the normal AppSurface Web path. `appsurface docs` and `appsurface docs export` use it to disable static web asset manifest loading for packaged tool runs because AppSurface Docs and RazorWire runtime assets are embedded in their assemblies.

The shared AppSurface Web startup watchdog still applies through `WebOptions.StartupTimeout`, which defaults to 10 seconds and fails fast when the process stalls before Kestrel starts listening. Export also enforces its own startup timeout because callers that build and start the host directly bypass the `RunAsync` watchdog path.

## Strict Harvest Failure

Use `AppSurfaceDocs:Harvest:FailOnFailure=true` when the standalone host is acting as an export or CI publish target and an all-failed harvest should stop the run before the app starts listening.

```bash
AppSurfaceDocs__Harvest__FailOnFailure=true \
dotnet run --project Web/ForgeTrust.AppSurface.Docs.Standalone -- --urls http://127.0.0.1:5189
```

Strict mode fails only when every configured harvester fails, times out, or cancels. Empty docs and partially degraded docs still start. The thrown `AppSurfaceDocsHarvestFailedException` uses a redacted summary suitable for CI output; raw exception details and repository paths remain in host logs for operators.

For the public AppSurface CLI export path, prefer the equivalent flag:

```bash
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
```

`--strict` is the harvest fail-closed gate. `--mode cdn` is the static artifact validation gate and preserves RazorWire `RWEXPORT00x` diagnostics when managed URLs cannot become CDN-safe files.

## Dogfood Harvest Boundary

The standalone host ships an `appsettings.json` that dogfoods the reusable harvest path policy for this repository. It keeps the live AppSurface docs surface focused on intentional public docs paths such as the root `README.md`, `LICENSE`, package READMEs, colocated `NAMESPACE.md` namespace intros, authored docs folders, releases, guides, troubleshooting, and example READMEs. It also excludes host-specific generated and test-result paths:

```json
{
  "AppSurfaceDocs": {
    "Harvest": {
      "Paths": {
        "IncludeGlobs": [
          "README.md",
          "LICENSE",
          "packages/**/README.md",
          "Web/**/README.md",
          "Web/**/NAMESPACE.md",
          "**/*.cs"
        ],
        "ExcludeGlobs": [
          "**/TestResults/**",
          "**/generated/**"
        ]
      }
    }
  }
}
```

Those excludes are dogfood policy, not package defaults. Reusable AppSurface Docs defaults still cover build output, hidden directories, test projects, and C# example source for every host. Add or override `AppSurfaceDocs:Harvest:Paths`, `AppSurfaceDocs:Harvest:Markdown`, or `AppSurfaceDocs:Harvest:CSharp` in environment-specific configuration when using this executable for another repository.

## Local URL Behavior

When you run this host in `Development` without explicit endpoint configuration, AppSurface Web assigns a deterministic localhost-only development URL from the current workspace path. That keeps sibling worktrees from colliding on the same default localhost URL.

- The standalone host redirects `/` to the configured AppSurface Docs home, `/docs` by default. The reusable AppSurface Docs package keeps embedded apps isolated to their configured docs routes; this root redirect exists only because this executable is a docs-only host and CI export target.
- The public `appsurface docs` and `appsurface docs preview` commands default the forwarded host environment to `Development`, so when no endpoint is configured they use this deterministic local URL behavior.
- For direct standalone-host runs, use the host startup log as the source of truth for the selected local URL.
- Pass `--port 5189`, `--urls http://127.0.0.1:5189`, `ASPNETCORE_HTTP_PORTS=5189`, or a `Kestrel:Endpoints` appsettings/environment entry when you intentionally want a fixed address.
- The checked-in launch profile no longer pins a single shared localhost port, because that was the source of cross-worktree QA confusion.

## Contributor Provenance Smoke Testing

The standalone host does not ship a checked-in source or edit target. Hard-coding a public repository or branch in the executable host would make feature-branch and fork smoke tests point readers at the wrong revision.

If you want the live standalone host to exercise the full `Source of truth` strip, provide `AppSurfaceDocs:Contributor` explicitly in the environment or app settings that launch the host:

```json
{
  "AppSurfaceDocs": {
    "Contributor": {
      "Enabled": true,
      "DefaultBranch": "feature/issue-143",
      "SourceUrlTemplate": "https://github.com/owner/repo/blob/{branch}/{path}",
      "EditUrlTemplate": "https://github.com/owner/repo/edit/{branch}/{path}",
      "LastUpdatedMode": "Git"
    }
  }
}
```

- Set `DefaultBranch` and the repository templates to the exact repo and ref you want readers to reach.
- Slash-separated refs such as `feature/issue-143` are preserved in the generated GitHub-style URLs while still escaping special characters inside each segment.
- For local forks or branch previews, do not reuse upstream `main` unless that is truly the page's source of truth.
- Set `LastUpdatedMode` to `Git` when you want the standalone host to exercise relative freshness too. The package default is `None`, so git-backed timestamps stay opt-in.
- If you cannot provide a trustworthy source or edit destination, leave the templates unset. AppSurface Docs will still omit unsafe links instead of guessing, and it will omit git-backed `Last updated` unless you explicitly opt into git freshness. Page-level `last_updated_override` metadata can still supply an explicit timestamp.
- The Playwright integration suite starts this standalone host in-process with explicit contributor settings so this runtime configuration seam stays covered. It intentionally does not run `dotnet run` from the fixture; focused test runs should build and host the current project source directly instead of reusing stale standalone `bin` output.

## Related Projects

- [ForgeTrust.AppSurface.Docs](../ForgeTrust.AppSurface.Docs/README.md) for the reusable docs package
- [Back to Web List](../README.md)
- [Back to Root](../../README.md)
