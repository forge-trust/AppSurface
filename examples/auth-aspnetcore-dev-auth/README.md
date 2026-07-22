# AppSurface ASP.NET Core DevAuth Example

This example proves [`ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth`](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md) can give a local package consumer a fake, visible persona lab without configuring an external identity provider. DevAuth activates only in `Development` by default and can opt in additional local/proof environments through `AllowedEnvironmentNames`.

DevAuth is local tooling. It is not production authentication, OIDC, ASP.NET Identity, a user store, a durable app-user mapping layer, or the [`Auth.Testing` integration-test harness](../../Auth/ForgeTrust.AppSurface.Auth.Testing/README.md).

Use the [AppSurface Auth adoption ladder](../../start-here/auth-adoption-ladder.md) when deciding whether this local persona proof, Auth.Testing, OIDC, or raw ASP.NET Core authentication is the right next step.

The root page renders `AppSurfaceDevAuthMarker` without wrapping it in an environment check. The renderer returns an empty string outside allowed environments, so host layouts can call it unconditionally. With default styles, the marker is a fixed bottom-right overlay above 640 CSS pixels and participates in normal document flow at widths up to and including 640 CSS pixels. It starts collapsed, then expands when you need to select `Local Admin`, `Local Viewer`, or `Clear`. Those controls post to the DevAuth control endpoints and return to the same page, so the fake auth state remains visible while you use the app.

The example supplies the host-owned viewport meta tag and renders the marker after the page header but before `<main>`. That location lets the narrow-screen marker reserve space and push application content instead of covering it. The example also supplies narrow-screen outer margin through `AdditionalCssClass`; AppSurface cannot choose spacing that fits every host layout. Consumers can use the same option for a higher-specificity placement override, or set `IncludeDefaultStyles = false` and own the complete skin and responsive behavior. Fixed, absolutely positioned, clipped, or overriding host containers are outside the default non-obstruction guarantee. See the package's [responsive placement and customization guidance](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#responsive-placement-and-customization) for recipes and troubleshooting.

To opt in a host-owned proof environment, configure the package explicitly:

```csharp
builder.Services.AddAppSurfaceDevAuth(builder.Environment, dev =>
{
    dev.AllowedEnvironmentNames.Add("Staging");
    // personas...
});
```

Run it:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project examples/auth-aspnetcore-dev-auth -- --urls http://127.0.0.1:5058
```

Open the control page:

```bash
# macOS
open http://127.0.0.1:5058/_appsurface/dev-auth
# Linux
xdg-open http://127.0.0.1:5058/_appsurface/dev-auth
# Windows (PowerShell)
Start-Process http://127.0.0.1:5058/_appsurface/dev-auth
```

Or start from the app page and use the marker:

```text
http://127.0.0.1:5058/
```

Or prove the flow with curl:

```bash
cookie="$(curl -is -X POST http://127.0.0.1:5058/_appsurface/dev-auth/select/admin | awk 'tolower($0) ~ /^set-cookie: \.appsurface\.devauth\.persona=/{sub(/^[^:]*: /,""); sub(/;.*/,""); print; exit}')"
curl -s -H "Cookie: $cookie" http://127.0.0.1:5058/api/auth-proof
cookie="$(curl -is -X POST http://127.0.0.1:5058/_appsurface/dev-auth/select/viewer | awk 'tolower($0) ~ /^set-cookie: \.appsurface\.devauth\.persona=/{sub(/^[^:]*: /,""); sub(/;.*/,""); print; exit}')"
curl -s -H "Cookie: $cookie" http://127.0.0.1:5058/api/auth-proof
```

Expected outcomes:

| State | `/api/auth-proof` outcome |
| --- | --- |
| `admin` selected | HTTP 200 with `{"result":"allowed","subject":"admin-1"}` |
| `viewer` selected | HTTP 403 AppSurface ProblemDetails with `Forbid` / `Forbidden` |
| persona cleared | HTTP 401 AppSurface ProblemDetails with `Challenge` / `Unauthenticated` |

Run the verifier:

```bash
bash examples/auth-aspnetcore-dev-auth/verify.sh
```

The verifier is a real-socket acceptance proof. It launches the already-built example on `127.0.0.1`, waits for that child process to report its configured Kestrel address, and then checks the viewport tag, the recommended header-marker-main ordering, the package responsive declarations, and the DevAuth persona flow over HTTP. Persona state stays in a verifier-private curl cookie jar instead of a manually constructed cookie header. The verifier ignores user curl configuration, bypasses proxies for the loopback proof, and does not follow redirects: any `3xx` response fails the named HTTP contract, preventing a persona cookie from crossing to another listener through a same-host, different-port redirect. Each request has a five-second connect timeout and a fifteen-second total timeout.

## What the verifier proves

The verifier reports five explicit proof stages, followed by cleanup:

1. **Preflight** assumes standard Unix userland, explicitly checks `dotnet` and `curl`, and validates configuration before any build.
2. **Build** compiles the example in `Release`. A compiler failure stops here with the compiler output; it does not become a readiness timeout.
3. **Launch** starts the built DLL directly as a disposable `Development` child process and reports its PID and loopback URL.
4. **Readiness** checks that the child is still alive and waits for the child's own `Now listening on: ...` record before probing HTTP. A different process already using the port therefore cannot satisfy readiness on the child's behalf.
5. **HTTP proof** checks the root and control surfaces, selects a persona, proves the protected result and status projection, and clears the persona.
6. **Cleanup** terminates and reaps only the recorded child PID. It waits up to five seconds after `SIGTERM` before escalating that child to `SIGKILL`, then gives the killed child a second bounded exit window before reaping; it never kills by process name, port, glob, or broad process match. An `INT` or `TERM` interruption returns `130` or `143` only after cleanup succeeds. If both bounded cleanup attempts fail, the verifier returns `6` with `REAP_FAILED` evidence and the recorded PID instead of claiming a normal signal shutdown.

This division keeps failures specific: build or launch failure, readiness timeout, and HTTP contract failure are distinct outcomes. Readiness retains a 60-second default ceiling for cold hosts, with elapsed progress every five seconds while the child remains alive.

The verifier accepts these environment variables:

| Variable | Default | Accepted values | Purpose |
| --- | --- | --- | --- |
| `APP_SURFACE_DEV_AUTH_PORT` | `61258` | Decimal integer from `1` through `65535` | Chooses the loopback port. The host remains fixed to `127.0.0.1`; there is no separate URL override. |
| `APP_SURFACE_DEV_AUTH_READY_TIMEOUT_SECONDS` | `60` | Decimal integer from `1` through `300` | Bounds readiness polling. Lower values are useful for deterministic verifier-contract tests and focused diagnosis. |

Invalid values fail before the build. An occupied port is reported as an early child exit with Kestrel's bind diagnostic rather than as a generic timeout.

The disposable child receives `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false`. [.NET host configuration reload is enabled by default, and Microsoft documents this host setting as the supported way to disable it](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/generic-host?view=aspnetcore-10.0#disable-app-configuration-reload-on-change). Some restricted file-watcher environments can otherwise stall inside host creation before AppSurface or DevAuth code runs. The setting applies only to the verifier-owned child; normal interactive example runs and consumer applications keep their usual configuration-reload behavior.

The focused host regression uses [`WebApplicationFactory<TEntryPoint>`](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0) to prove that startup reaches `ApplicationStarted` and that the essential DevAuth workflow succeeds in process. `WebApplicationFactory` uses an in-memory test server, so it does **not** prove Kestrel socket binding. The shell verifier remains the acceptance proof for a real loopback socket.

## Failure evidence and troubleshooting

On failure, the verifier prints the stage, elapsed time, child PID, URL, classification, and available child exit status. It also prints a bounded application-log tail. The evidence is intentionally narrow: before preserving a failure directory, the verifier removes cookie jars, response headers, response bodies, and every non-allowlisted artifact. Only a text stage summary and an allowlisted application-log tail may remain; known hosting categories, exception-shaped lines in redacted canonical form, bind failure, and the configured listening record are retained while every other line becomes `[REDACTED]`. The tail is capped at 80 lines and 32 KiB. The verifier never prints an environment dump, cookies, Data Protection keys, seeded claims, or complete status payloads.

Use the stage to choose the next check:

- **Preflight:** inspect the named missing-command or invalid-configuration diagnostic before trying to launch the host.
- **Build:** read the compiler output directly and run the focused `dotnet build` command below.
- **Launch or early child exit:** inspect the bounded Kestrel log tail. Port conflicts appear here as bind failures.
- **Readiness timeout with a live child:** a quiet timeout is not evidence that DevAuth failed. Confirm whether the child emitted its exact configured-address record. In a restricted file-watcher environment, compare a disposable run with `hostBuilder:reloadConfigOnChange=false`. On macOS, a manual process sample can help isolate a host/framework stall, but the verifier never collects one automatically.
- **HTTP proof:** use the named route stage to identify the failed root, persona-selection, protected-proof, status, or clear-persona assertion.

Focused commands from the repository root:

```bash
# Build the example without entering readiness polling.
dotnet build examples/auth-aspnetcore-dev-auth/AuthAspNetCoreDevAuthExample.csproj -c Release

# Run the in-process host and verifier-contract regressions.
dotnet test examples/auth-aspnetcore-dev-auth.tests/AuthAspNetCoreDevAuthExample.Tests.csproj

# Run the real loopback Kestrel acceptance proof with defaults.
bash examples/auth-aspnetcore-dev-auth/verify.sh

# Use a short diagnostic ceiling or an alternate loopback port.
APP_SURFACE_DEV_AUTH_READY_TIMEOUT_SECONDS=10 APP_SURFACE_DEV_AUTH_PORT=5058 \
  bash examples/auth-aspnetcore-dev-auth/verify.sh

# Confirm solution discovery, formatting, documentation, and package indexes.
dotnet sln ForgeTrust.AppSurface.slnx list | \
  grep -F 'examples/auth-aspnetcore-dev-auth.tests/AuthAspNetCoreDevAuthExample.Tests.csproj'
dotnet format ForgeTrust.AppSurface.slnx --verify-no-changes
dotnet run --project tools/ForgeTrust.AppSurface.MarkdownSnippets/ForgeTrust.AppSurface.MarkdownSnippets.csproj \
  --configuration Release -- verify
dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- \
  verify

# Run the repository solution and coverage gate when practical.
./scripts/coverage-solution.sh
```

These verification controls are example and test infrastructure. They add no DevAuth package API, runtime dependency, production-host setting, package release, or version change.
