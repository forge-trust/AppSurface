# ForgeTrust.AppSurface.Web.Tailwind

Tailwind CSS integration for AppSurface web applications with zero Node.js dependency.

## Overview

This package wires the Tailwind standalone CLI into the AppSurface web build pipeline so your app can compile generated CSS during builds and run Tailwind in watch mode during development.

## Release Guidance

AppSurface has cut the first coordinated `v0.1.0` release candidate. Before installing this package from a prerelease feed, read the [v0.1.0 RC 2 release note](../../releases/v0.1.0-rc.2.md) for current release risk, migration guidance, and package readiness.

## Features

- **No Node.js required**: Uses the official standalone Tailwind CLI binaries.
- **RID-aware runtime packages**: Pulls in the platform-specific runtime package automatically when the package is restored.
- **Build integration**: Compiles `wwwroot/css/app.css` to `wwwroot/css/site.gen.css` by default.
- **Development watch mode**: Starts Tailwind in `--watch` during development when you register the service.

## Usage

### First Tailwind page in five minutes

1. Add the package to an AppSurface web project:

```bash
dotnet add package ForgeTrust.AppSurface.Web.Tailwind
```

2. Create the default input file:

```css
/* wwwroot/css/app.css */
@import "tailwindcss";
```

3. Register watch mode for local development:

```csharp
services.AddTailwind(options =>
{
    options.InputPath = "wwwroot/css/app.css";
    options.OutputPath = "wwwroot/css/site.gen.css";
});
```

4. Reference the generated stylesheet from your layout:

```html
<link rel="stylesheet" href="~/css/site.gen.css" asp-append-version="true" />
```

5. Build and run:

```bash
dotnet build
dotnet run
```

The build should log `Tailwind CSS: Running build for wwwroot/css/app.css -> wwwroot/css/site.gen.css`, create `wwwroot/css/site.gen.css`, and include that file in static web assets when the output remains under `wwwroot/`.

### Build and watch behavior

When `OutputPath` stays under `wwwroot/`, the generated file is registered as an ASP.NET Core static web
asset on clean builds and publish runs. That means Razor Class Libraries and other package-style consumers can
serve the generated CSS without checking `site.gen.css` into source control first.
That registration also stays in place for projects that disable the SDK's default content items and rely on the
package to declare the generated web-root asset explicitly.

Keep `InputPath` and `OutputPath` pointed at different files. The build target and the development watch service both reject configurations where the two paths resolve to the same file, even if one path uses a normalized relative form such as `./wwwroot/css/../css/app.css`.

During development, the watch service is non-blocking. If the Tailwind CLI is not available on the current machine, the app still starts and serves any existing CSS while logging a warning that points developers to the runtime package or `TailwindOptions.CliPath` override. Other startup failures still log as errors.

Build mode uses a compiled MSBuild task instead of `<Exec>`. The task receives structured arguments, emits stable `ASTW###` diagnostics, and keeps build resolution behavior explicit. Build mode does not search `PATH`; use `TailwindCliPath` when you want a custom CLI during builds. Watch mode may use `PATH` as a development convenience after checking packaged runtime locations.

### MSBuild property reference

| Property | Default | Behavior |
|---|---|---|
| `TailwindEnabled` | `true` | Set to `false` to skip package-driven build integration. |
| `TailwindInputPath` | `wwwroot/css/app.css` | CSS input path, resolved relative to the project directory. |
| `TailwindOutputPath` | `wwwroot/css/site.gen.css` | Generated CSS path, resolved relative to the project directory. Keep it under `wwwroot/` for static web asset registration. |
| `TailwindCliPath` | empty | Explicit build-time Tailwind CLI path. Relative paths resolve from the project directory. Build mode does not fall back to `PATH`. |
| `TailwindVersion` | from `build/tailwind.version` | Tailwind standalone CLI version used by runtime packages. Override only when testing a coordinated runtime-package change. |
| `TailwindRuntimeBinaryResolutionEnabled` | `true` | Maintainer/CI-only. Set to `false` only in non-package jobs that skip runtime binary downloads and do not build Tailwind-consuming projects, unless the job also intentionally sets `TailwindEnabled=false`. Do not use it for consumer builds or package creation; see [`eng/ci-critical-path.md`](../../eng/ci-critical-path.md#tailwind-runtime-binary-resolution-in-ci). |
| `TailwindDownloadCacheRoot` | source-tree user cache; empty for packed consumers unless set | Shared source-tree download cache used while building Tailwind runtime packages. Source-tree imports default to `$XDG_CACHE_HOME/forgetrust/appsurface/tailwind`, `%LOCALAPPDATA%/ForgeTrust/AppSurface/Tailwind`, `$HOME/.cache/forgetrust/appsurface/tailwind`, or `%USERPROFILE%/.cache/forgetrust/appsurface/tailwind`, in that order. |

### Runtime option reference

| Option | Default | Behavior |
|---|---|---|
| `Enabled` | `true` | Set to `false` to disable development watch mode. |
| `InputPath` | `wwwroot/css/app.css` | Watch-mode input path, resolved relative to the content root. |
| `OutputPath` | `wwwroot/css/site.gen.css` | Watch-mode output path, resolved relative to the content root. |
| `CliPath` | `null` | Explicit watch-mode Tailwind CLI path. Relative paths resolve from the content root. |

## CI

`ForgeTrust.AppSurface.Web.Tailwind` hooks into the normal `dotnet build` and `dotnet publish` pipeline through MSBuild targets, so the default integration does not require a separate `npm install` or `npm run build` step in CI.

Runtime packages download the official Tailwind checksum file and standalone binary during build or publish, then verify the binary with SHA-256 before packaging it. These downloads retry transient network failures by default, which keeps CI resilient to brief GitHub release CDN 5xx responses and timeouts without weakening checksum validation.

Maintainers can set `TailwindRuntimeBinaryResolutionEnabled=false` only for fast CI jobs that restore, build, or test without producing package artifacts and without compiling Tailwind-consuming projects. Jobs that intentionally skip CSS generation can pair it with `TailwindEnabled=false`, but solution builds should leave runtime binary resolution enabled. Package validation, `dotnet pack`, and release workflows must leave the property enabled or pass `/p:TailwindRuntimeBinaryResolutionEnabled=true`. The authoritative CI matrix and copy-paste examples live in [`eng/ci-critical-path.md`](../../eng/ci-critical-path.md#tailwind-runtime-binary-resolution-in-ci).

Source-tree builds cache downloaded Tailwind executables outside the worktree by default so multiple local worktrees reuse the same binary copy. The cache is versioned and RID-scoped under `TailwindDownloadCacheRoot`, for example `$HOME/.cache/forgetrust/appsurface/tailwind/tailwind-4.1.18/osx-arm64/tailwindcss-macos-arm64`. Packed NuGet consumers normally use NuGet's global package cache instead. Build-time shared-cache probing happens only when `TailwindDownloadCacheRoot` is explicitly set by MSBuild targets or project configuration, and development watch mode does not read the ambient user cache. Missing runtime-package dependencies therefore fail cleanly on developer machines and CI unless the build opts into a cache root or an explicit CLI path is configured.

The retry behavior is configurable with MSBuild properties:

```xml
<PropertyGroup>
  <TailwindDownloadCacheRoot>/mnt/ci-cache/appsurface-tailwind</TailwindDownloadCacheRoot>
  <TailwindDownloadRetries>4</TailwindDownloadRetries>
  <TailwindDownloadRetryDelayMilliseconds>5000</TailwindDownloadRetryDelayMilliseconds>
</PropertyGroup>
```

Point `TailwindDownloadCacheRoot` at a durable CI cache volume when build agents reuse caches between jobs. Raise the retry values for slower CI networks. Lower them only when you prefer fail-fast behavior and have another way to provide the Tailwind CLI, such as `TailwindCliPath`.

If you need to suppress the package-driven build temporarily, set `TailwindEnabled=false` in MSBuild, for example with `dotnet build -p:TailwindEnabled=false` or a project-level `<TailwindEnabled>false</TailwindEnabled>` property.

If you want to keep the package-driven build but point it at a different standalone Tailwind executable, set `TailwindCliPath` to an absolute path or a project-relative file path:

```xml
<PropertyGroup>
  <TailwindCliPath>tools/tailwindcss/tailwindcss</TailwindCliPath>
</PropertyGroup>
```

Use the matching runtime option for development watch mode:

```csharp
services.AddTailwind(options =>
{
    options.CliPath = "tools/tailwindcss/tailwindcss";
});
```

On Windows, `TailwindCliPath` and `TailwindOptions.CliPath` can point at the standalone binary directly or at the npm-generated `.cmd`, `.bat`, or `.ps1` shim. The package wraps those shims with the correct launcher while preserving argument boundaries for paths with spaces.

## Tailwind diagnostics

Every build-task diagnostic includes a stable code, problem, cause, fix, and this troubleshooting anchor.

| Code | Meaning | Fix |
|---|---|---|
| `ASTW001` | The build host could not be mapped to a supported Tailwind runtime identifier. | Build on Windows x64/Arm64, macOS x64/Arm64, or Linux x64/Arm64, or set `TailwindCliPath`. |
| `ASTW002` | `TailwindVersion` could not be resolved. | Ensure `build/tailwind.version` is present next to the targets file or set `TailwindVersion`. |
| `ASTW003` | `TailwindCliPath` was set but the resolved file does not exist. | Point `TailwindCliPath` at an existing standalone binary or remove it to use packaged runtimes. |
| `ASTW004` | No build-mode Tailwind CLI was found. | Install the matching runtime package or set `TailwindCliPath`; build mode does not search `PATH`. |
| `ASTW005` | The MSBuild task assembly could not load or the Tailwind process could not start. | Restore/build the package, verify `build/tasks` exists in the nupkg, and ensure the CLI file is executable. |
| `ASTW006` | Tailwind exited with a non-zero code. | Read the captured output tail, fix the CSS/configuration error, and run the build again. |
| `ASTW007` | MSBuild canceled the Tailwind task. | Re-run the build if cancellation was unintentional. |
| `ASTW008` | Input and output resolve to the same file. | Set `TailwindOutputPath` to a generated file distinct from `TailwindInputPath`. |
| `ASTW009` | `TailwindRuntimeBinaryResolutionEnabled` has an unsupported value. | Use `true` or `false`; unset defaults to `true`. |
| `ASTW010` | Runtime package creation was attempted with Tailwind runtime binary resolution disabled. | Rerun package validation, restore, build, and pack with `/p:TailwindRuntimeBinaryResolutionEnabled=true`. |
| `ASTW011` | The resolved runtime package payload file is missing. | Rerun package validation with binary resolution enabled, delete stale `obj` output if needed, and verify `TailwindBaseUrl`/`TailwindSumsUrl`. |

If an error mentions `build/tasks`, inspect the packed package. A valid package contains `build/ForgeTrust.AppSurface.Web.Tailwind.targets`, `build/tailwind.version`, `build/tasks/ForgeTrust.AppSurface.Web.Tailwind.Tasks.dll`, and `build/tasks/CliWrap.dll`.

## Escape hatch (plugin-heavy Tailwind setups)

If your Tailwind configuration depends on npm-only plugins or custom JavaScript tooling, keep your existing Node-based asset pipeline instead of forcing the standalone CLI path.

Disable the package-driven MSBuild integration with `TailwindEnabled=false`, and either omit `services.AddTailwind()` or set `options.Enabled = false` so the development watch service does not start. After that, run your existing npm, pnpm, or yarn Tailwind command as part of your normal frontend build.

If your custom setup still uses the standalone CLI but stores it outside the package runtime layout, prefer `TailwindCliPath` over editing the imported `.targets` file directly.

The v0.1 integration intentionally does not expose separate MSBuild knobs for minification, additional Tailwind arguments, working directory, template input globs, config input globs, or static web asset registration. Those defaults are part of the zero-Node path. Use `TailwindEnabled=false` and your own asset pipeline when a project needs full Tailwind command control.

## Notes

- The generated CSS file is intended to be build output and is commonly ignored in source control.
- Generated CSS outside `wwwroot/` still builds locally, but it is not exposed automatically through the static web asset pipeline.
- The platform-specific `ForgeTrust.AppSurface.Web.Tailwind.Runtime.*` packages are support packages consumed transitively by this package and are not usually installed directly.
- `TailwindDownloadCacheRoot` affects source-tree runtime downloads, not normal generated CSS output. Delete that cache when you intentionally want to force runtime package projects to re-download and re-verify the standalone Tailwind binaries.
- Tailwind CLI selection follows the current build host, not `RuntimeIdentifier`, because the standalone CLI runs during the build. Cross-targeted builds still execute the host-compatible binary.
- Windows Arm64 hosts intentionally use the `win-x64` runtime under emulation. There is no `ForgeTrust.AppSurface.Web.Tailwind.Runtime.win-arm64` package because Tailwind `v4.1.18` does not ship a native Windows Arm64 standalone CLI.
