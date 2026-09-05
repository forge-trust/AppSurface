# ForgeTrust.AppSurface.Web.Tailwind

Node-free Tailwind CSS build and development-watch integration for AppSurface web applications.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## What the package provides

ForgeTrust.AppSurface.Web.Tailwind treats the Tailwind standalone executable as a
**build-host tool**, not an application runtime dependency. The main package contains
MSBuild targets, the compiled task, tailwind.release.json, and notices. It does not
carry a native Tailwind executable or depend on a Tailwind.Runtime companion package.

On the first normal build, the task selects the machine running MSBuild, obtains the
matching official Tailwind binary, verifies it against the package-pinned release
manifest, and atomically stores it in a user or CI-owned cache. Later builds rehash and
reuse that one host entry. This keeps binaries out of project-reference and application
outputs while retaining a zero-Node default.

See the generated release-guidance section above for coordinated-version guidance and
release risk.

## First successful build

1. Add the package.

       dotnet add package ForgeTrust.AppSurface.Web.Tailwind

2. Add the default input file.

       /* wwwroot/css/app.css */
       @import "tailwindcss";

3. Build the project.

       dotnet build

The first connected build logs whether its host CLI was acquired or reused and writes
wwwroot/css/site.gen.css. Link that generated file from the layout when the page is
ready to use it:

    <link rel="stylesheet" href="~/css/site.gen.css" asp-append-version="true" />

When TailwindOutputPath remains under wwwroot, the targets register it as a static web
asset, including on a clean Razor Class Library build. Keep input and output paths
different: the targets and watch service reject configurations that resolve to the same
file.

## Build-host resolution and trust

Normal resolution maps the **current process host**, never the consuming project's
RuntimeIdentifier:

| Host | Selected asset |
|---|---|
| Linux x64 | linux-x64 |
| Linux Arm64 | linux-arm64 |
| macOS x64 | osx-x64 |
| macOS Arm64 | osx-arm64 |
| Windows x64 or Arm64 | win-x64 (Windows x64 emulation on Arm64) |

The package-owned build/tailwind.release.json supplies the canonical Tailwind version,
official HTTPS release base URL, binary name, and SHA-256 digest for exactly those five
mappings. A downloaded sha256sums.txt must agree with that pinned digest, and the
executable is hashed again before every run. Downloaded checksums are an audit signal;
the packaged manifest is the trust anchor.

The cache identity is:

    <TailwindDownloadCacheRoot>/tailwind-<version>/<host-rid>/<binary-name>

With no explicit root, the resolver chooses XDG_CACHE_HOME, LOCALAPPDATA, HOME, then
USERPROFILE, and appends the AppSurface Tailwind cache directory. It uses an exclusive
entry lock, GUID-suffixed partial downloads, hash verification, same-directory atomic
publication, and owner-only partial cleanup. A partial, rejected, symlinked, or
digest-mismatched entry is never executed.

Treat the cache root as user-private infrastructure. The resolver rejects symbolic links
and reparse points at or below that root before it accepts a cached executable, but a
user who can replace the configured cache root or its parent controls that local path.
Use a directory writable only by the build or watch identity; do not point
`TailwindDownloadCacheRoot` at a shared or untrusted location.

## Offline and CI behavior

A verified, prewarmed cache works with the network disabled. On a connected machine,
run the normal three-step build once against the intended cache root; preserve that
directory with your CI cache mechanism. A fresh offline machine has no trusted binary
and fails before it starts a child process. Supply an explicit CLI path or prewarm the
cache instead of copying an unverified executable into a build output folder.

Example CI configuration:

    <PropertyGroup>
      <TailwindDownloadCacheRoot>/mnt/ci-cache/appsurface-tailwind</TailwindDownloadCacheRoot>
    </PropertyGroup>

CI cache keys should include the manifest version and native build host, for example
appsurface-tailwind-4.1.18-linux-x64. Release validation records native-host evidence
for all five supported mappings; a cross-RID executable simulation is not a substitute
for native execution proof.

## Build and watch policy

| Mode | Ordered resolution | Failure behavior |
|---|---|---|
| MSBuild build | Existing TailwindCliPath -> verified host cache/acquisition | Missing explicit path is final. Resolver failure is a build error. Build never searches PATH. |
| Development watch | Existing TailwindOptions.CliPath -> verified host cache/acquisition -> one PATH attempt | Missing explicit watch path is final. With no explicit path, a resolver and PATH failure logs a warning and the app starts without watch mode. |

An explicit path is deliberately a trusted escape hatch. Relative TailwindCliPath values
resolve from the project directory; relative TailwindOptions.CliPath values resolve from
the host content root. Explicit paths bypass runtime mapping, cache lookup, cache
writes, and network acquisition. On Windows, watch supports .cmd and .ps1 shims as
well as the standalone executable.

## Configuration reference

| Property | Default | Use it when |
|---|---|---|
| TailwindEnabled | true | Set to false to use another asset pipeline. |
| TailwindInputPath | wwwroot/css/app.css | Move the Tailwind CSS input. |
| TailwindOutputPath | wwwroot/css/site.gen.css | Move generated CSS; retain wwwroot for static web assets. |
| TailwindCliPath | empty | Build with a deliberate local or custom standalone binary. |
| TailwindVersion | build/tailwind.version | Internal coordinated-release value. It must match the package manifest. |
| TailwindDownloadCacheRoot | user or CI cache derived from environment | Use a durable or isolated cache root. |

| TailwindOptions member | Default | Use it when |
|---|---|---|
| Enabled | true | Disable development watch mode. |
| InputPath | wwwroot/css/app.css | Move watch-mode input relative to the content root. |
| OutputPath | wwwroot/css/site.gen.css | Move watch-mode output relative to the content root. |
| CliPath | null | Use an explicit development-watch CLI. |

The retained ForgeTrust.AppSurface.Web.Tailwind.Runtime packages are independent
companion/compatibility artifacts. They are not part of normal installation or the main
package dependency graph. See [runtime package guidance](runtimes/README.md) before
using one directly.

## Package release proof

The repository package gate runs a real packed-consumer proof through
[`scripts/verify-tailwind-package-consumer.sh`](../../scripts/verify-tailwind-package-consumer.sh).
It restores only the freshly packed main package from the local artifact feed, maps
third-party resolution to reviewed CliWrap, Microsoft.Extensions, and
System.Diagnostics.EventLog package sources, isolates all NuGet caches inside the proof
workspace, verifies the generated consumer lock file with `--locked-mode`, then builds
with the default resolver. The proof confirms the current host cache entry and
generated CSS, and rejects any runtime companion edge or copied Tailwind executable in
consumer output.
Run `verify-packages` as described in the [package release workflow](../../packages/README.md#package-release-workflow)
before publishing changes to this package boundary.

## Diagnostics

Every task diagnostic has a stable ASTW code, cause, recovery, and this document as its
help anchor.

| Code | Meaning | Recovery |
|---|---|---|
| ASTW001 | No explicit path was supplied and the build host is unsupported. | Build on a supported host or set TailwindCliPath. |
| ASTW002 | The package Tailwind version is missing. | Restore the package or provide an explicit CLI path. |
| ASTW003 | An explicit build CLI path does not exist. | Correct it or remove it to use verified host resolution. |
| ASTW005 | The task assembly or resolved executable could not start. | Restore package task assets and verify the executable or override is runnable. |
| ASTW006 | Tailwind exited non-zero. | Read the captured output and fix CSS or configuration. |
| ASTW007 | MSBuild canceled Tailwind. | Re-run when cancellation was unintended. |
| ASTW008 | Input and output resolve to the same file. | Choose a distinct generated output. |
| ASTW012 | Manifest, version, cache, lock, checksum, or acquisition verification failed. | Use the classification and redacted cache identity; prewarm the cache, fix the root, or set an explicit CLI path. |

ASTW012 classifications are finite: invalid-version, no-cache-root, invalid-cache,
checksum-failure, non-writable-root, network-failure, retry-exhausted, and lock-timeout.
Diagnostics never render a custom absolute cache root, release URL, response body, or
credential.

## When not to use this integration

Use TailwindEnabled=false and retain your existing Node/npm/pnpm/yarn pipeline when
your project needs npm-only Tailwind plugins, custom JavaScript tool orchestration, or
full command-line control. Do not edit the imported targets file. If you only need a
custom standalone binary, use TailwindCliPath or TailwindOptions.CliPath instead.
