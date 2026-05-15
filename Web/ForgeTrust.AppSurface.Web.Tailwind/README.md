# ForgeTrust.AppSurface.Web.Tailwind

Tailwind CSS integration for AppSurface web applications with zero Node.js dependency.

## Overview

This package wires the Tailwind standalone CLI into the AppSurface web build pipeline so your app can compile generated CSS during builds and run Tailwind in watch mode during development.

## Features

- **No Node.js required**: Uses the official standalone Tailwind CLI binaries.
- **RID-aware runtime packages**: Pulls in the platform-specific runtime package automatically when the package is restored.
- **Build integration**: Compiles `wwwroot/css/app.css` to `wwwroot/css/site.gen.css` by default.
- **Development watch mode**: Starts Tailwind in `--watch` during development when you register the service.

## Usage

Install the package and register Tailwind in your web module:

```csharp
services.AddTailwind(options =>
{
    options.InputPath = "wwwroot/css/app.css";
    options.OutputPath = "wwwroot/css/site.gen.css";
});
```

Reference the generated stylesheet from your layout:

```html
<link rel="stylesheet" href="~/css/site.gen.css" asp-append-version="true" />
```

When `OutputPath` stays under `wwwroot/`, the generated file is registered as an ASP.NET Core static web
asset on clean builds and publish runs. That means Razor Class Libraries and other package-style consumers can
serve the generated CSS without checking `site.gen.css` into source control first.
That registration also stays in place for projects that disable the SDK's default content items and rely on the
package to declare the generated web-root asset explicitly.

Keep `InputPath` and `OutputPath` pointed at different files. The build target and the development watch service both reject configurations where the two paths resolve to the same file, even if one path uses a normalized relative form such as `./wwwroot/css/../css/app.css`.

During development, the watch service is non-blocking. If the Tailwind CLI is not available on the current machine, the app still starts and serves any existing CSS while logging a warning that points developers to the runtime package or `TailwindCliPath` override. Other startup failures still log as errors.

## CI

`ForgeTrust.AppSurface.Web.Tailwind` hooks into the normal `dotnet build` and `dotnet publish` pipeline through MSBuild targets, so the default integration does not require a separate `npm install` or `npm run build` step in CI.

Runtime packages download the official Tailwind checksum file and standalone binary during build or publish, then verify the binary with SHA-256 before packaging it. These downloads retry transient network failures by default, which keeps CI resilient to brief GitHub release CDN 5xx responses and timeouts without weakening checksum validation.

The retry behavior is configurable with MSBuild properties:

```xml
<PropertyGroup>
  <TailwindDownloadRetries>4</TailwindDownloadRetries>
  <TailwindDownloadRetryDelayMilliseconds>5000</TailwindDownloadRetryDelayMilliseconds>
</PropertyGroup>
```

Raise these values for slower CI networks. Lower them only when you prefer fail-fast behavior and have another way to provide the Tailwind CLI, such as `TailwindCliPath`.

If you need to suppress the package-driven build temporarily, set `TailwindEnabled=false` in MSBuild, for example with `dotnet build -p:TailwindEnabled=false` or a project-level `<TailwindEnabled>false</TailwindEnabled>` property.

If you want to keep the package-driven build but point it at a different standalone Tailwind executable, set `TailwindCliPath` to an absolute path or a project-relative file path:

```xml
<PropertyGroup>
  <TailwindCliPath>tools/tailwindcss/tailwindcss</TailwindCliPath>
</PropertyGroup>
```

On Windows, `TailwindCliPath` can point at the standalone binary directly or at the npm-generated `.cmd` or `.ps1` shim. The package wraps those shims consistently in both build mode and development watch mode, so the same override works in each flow.

## Escape hatch (plugin-heavy Tailwind setups)

If your Tailwind configuration depends on npm-only plugins or custom JavaScript tooling, keep your existing Node-based asset pipeline instead of forcing the standalone CLI path.

Disable the package-driven MSBuild integration with `TailwindEnabled=false`, and either omit `services.AddTailwind()` or set `options.Enabled = false` so the development watch service does not start. After that, run your existing npm, pnpm, or yarn Tailwind command as part of your normal frontend build.

If your custom setup still uses the standalone CLI but stores it outside the package runtime layout, prefer `TailwindCliPath` over editing the imported `.targets` file directly.

## Notes

- The generated CSS file is intended to be build output and is commonly ignored in source control.
- Generated CSS outside `wwwroot/` still builds locally, but it is not exposed automatically through the static web asset pipeline.
- The platform-specific `ForgeTrust.AppSurface.Web.Tailwind.Runtime.*` packages are support packages consumed transitively by this package and are not usually installed directly.
- Tailwind CLI selection follows the current build host, not `RuntimeIdentifier`, because the standalone CLI runs during the build. Cross-targeted builds still execute the host-compatible binary.
- Windows Arm64 hosts intentionally use the `win-x64` runtime under emulation. There is no `ForgeTrust.AppSurface.Web.Tailwind.Runtime.win-arm64` package because Tailwind `v4.1.18` does not ship a native Windows Arm64 standalone CLI.
