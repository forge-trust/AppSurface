# ForgeTrust.AppSurface.Docs.Standalone

AppSurface host for serving or exporting RazorDocs as an application.

## What it is for

This project is the thin executable wrapper around the reusable [ForgeTrust.AppSurface.Docs](../ForgeTrust.AppSurface.Docs/README.md) package. It exists so the docs surface can run as:

- a local standalone site during development
- the export target in CI
- a smoke-testable host that proves the package seam stays honest

For public command-line workflows, use the AppSurface CLI as the public command surface:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs --repo .
```

The CLI delegates to this standalone host, so the host remains the source of truth for RazorDocs startup, static web assets, routes, and configuration binding.

## Entry Point

The app boots through [Program.cs](./Program.cs), which delegates to `RazorDocsStandaloneHost`.
`RazorDocsStandaloneHost` is the reusable host entry point for this executable:

- `RunAsync(string[] args)` starts the standalone app and is what `Program.cs` uses.
- `CreateBuilder(string[] args, IEnvironmentProvider? environmentProvider = null)` returns an `IHostBuilder` without starting it.
- `CreateBuilder(string[] args, IEnvironmentProvider? environmentProvider, Action<WebOptions>? configureOptions)` adds the same builder seam plus web-option customization for package-hosted tools.

Use `CreateBuilder` when a test or tool needs the real standalone host in-process. It keeps the same `RazorDocsWebModule`, MVC routes, static web assets, and `RazorDocs` configuration binding as the executable path while avoiding a shell-out to `dotnet run`.

Do not duplicate standalone setup in test fixtures. If a scenario needs different URLs, repository roots, contributor templates, or environment behavior, pass those through command-line configuration or the optional environment provider so the normal host builder still owns the app shape.
`CreateBuilder` is lower level than `RunAsync`: callers that build and start the host themselves should pass `--urls`, `--port`, or configure the web host before `Build()` instead of relying on the executable startup path's development-port fallback.
The builder pins this standalone assembly as the host entry point identity so in-process callers, including xUnit, resolve the same static web asset manifest as the executable.

The optional `configureOptions` callback is for host-shape seams that must stay on the normal AppSurface Web path. `appsurface docs` uses it to disable static web asset manifest loading for packaged tool runs because RazorDocs and RazorWire runtime assets are embedded in their assemblies. The shared AppSurface Web startup watchdog still applies through `WebOptions.StartupTimeout`, which defaults to 10 seconds and fails fast when the process stalls before Kestrel starts listening.

## Strict Harvest Failure

Use `RazorDocs:Harvest:FailOnFailure=true` when the standalone host is acting as an export or CI publish target and an all-failed harvest should stop the run before the app starts listening.

```bash
RazorDocs__Harvest__FailOnFailure=true \
dotnet run --project Web/ForgeTrust.AppSurface.Docs.Standalone -- --urls http://127.0.0.1:5189
```

Strict mode fails only when every configured harvester fails, times out, or cancels. Empty docs and partially degraded docs still start. The thrown `RazorDocsHarvestFailedException` uses a redacted summary suitable for CI output; raw exception details and repository paths remain in host logs for operators.

## Local URL Behavior

When you run this host in `Development` without explicit endpoint configuration, AppSurface Web assigns a deterministic localhost-only development URL from the current workspace path. That keeps sibling worktrees from colliding on the same default localhost URL.

- The standalone host redirects `/` to the configured RazorDocs home, `/docs` by default. The reusable RazorDocs package keeps embedded apps isolated to their configured docs routes; this root redirect exists only because this executable is a docs-only host and CI export target.
- The public `appsurface docs` and `appsurface docs preview` commands default the forwarded host environment to `Development`, so when no endpoint is configured they use this deterministic local URL behavior.
- Use the startup log as the source of truth for the selected local URL.
- Pass `--port 5189`, `--urls http://127.0.0.1:5189`, `ASPNETCORE_HTTP_PORTS=5189`, or a `Kestrel:Endpoints` appsettings/environment entry when you intentionally want a fixed address.
- The checked-in launch profile no longer pins a single shared localhost port, because that was the source of cross-worktree QA confusion.

## Contributor Provenance Smoke Testing

The standalone host does not ship a checked-in source or edit target. Hard-coding a public repository or branch in the executable host would make feature-branch and fork smoke tests point readers at the wrong revision.

If you want the live standalone host to exercise the full `Source of truth` strip, provide `RazorDocs:Contributor` explicitly in the environment or app settings that launch the host:

```json
{
  "RazorDocs": {
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
- If you cannot provide a trustworthy source or edit destination, leave the templates unset. RazorDocs will still omit unsafe links instead of guessing, and it will omit git-backed `Last updated` unless you explicitly opt into git freshness. Page-level `last_updated_override` metadata can still supply an explicit timestamp.
- The Playwright integration suite starts this standalone host in-process with explicit contributor settings so this runtime configuration seam stays covered. It intentionally does not run `dotnet run` from the fixture; focused test runs should build and host the current project source directly instead of reusing stale standalone `bin` output.

## Related Projects

- [ForgeTrust.AppSurface.Docs](../ForgeTrust.AppSurface.Docs/README.md) for the reusable docs package
- [Back to Web List](../README.md)
- [Back to Root](../../README.md)
