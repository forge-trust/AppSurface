# Packed typed readiness consumer

This prepared .NET 10 Aspire AppHost project restores the locally packed `ForgeTrust.AppSurface.Aspire.Testing` package with Aspire 13.4.4, builds a public AppHost marker/module/profile, and runs the complete `AppSurfaceAspireTestFixture.StartAsync` call from that package. The profile adds a local executable resource that serves `/healthz`; Aspire's native DCP starts and health-checks that process before the fixture returns the named HTTP endpoint.

The program contains two executable, version-bound startup-call equivalences for the pinned Skoolit source commit `a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e`:

- [`MonitoringReceiptBrowserGateTests.cs`](https://github.com/forge-trust/skoolit/blob/a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e/tests/Skoolit.Playwright.Tests/MonitoringReceiptBrowserGateTests.cs) creates `skoolit-web`, selects endpoint `http`, and calls the shared QA probe at `/healthz`.
- [`BillingTierBrowserGateTests.cs`](https://github.com/forge-trust/skoolit/blob/a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e/tests/Skoolit.Playwright.Tests/BillingTierBrowserGateTests.cs) uses the same resource, endpoint, and probe sequence from its browser fixture.

Both runs retain the source's three-minute caller/startup budget, exact-200 behavior, 30-attempt cap, three-second request timeout, 250 ms retry delay, `UseProxy = false`, and two-second `ConnectTimeout`. The test project is a `.csproj.template`, not a repository project, so package-index and solution discovery do not enroll it. The compiled AppHost assembly must carry a usable `dcpclipath` metadata value from Aspire's AppHost SDK.

The pinned browser gates used this startup sequence before their browser and business assertions:

```csharp
// Before: each gate assembled these startup/readiness operations itself.
await app.StartAsync(cancellationToken);
await app.ResourceNotifications.WaitForResourceHealthyAsync("skoolit-web", cancellationToken);
var endpoint = app.GetEndpoint("skoolit-web", "http");
await QaEndpointProbe.WaitForHealthyAsync(endpoint, cancellationToken);

// After: the fixture returns the named endpoint only after native health and /healthz pass.
await using var fixture = await AppSurfaceAspireTestFixture.StartAsync(
    token => AppSurfaceAspireTestingBuilder.CreateAsync<ConsumerAppHost, ConsumerModule, ConsumerProfile>(token),
    new AspireHttpReadinessTarget("skoolit-web", "http", "/healthz"),
    new AppSurfaceAspireStartupOptions { StartupTimeout = TimeSpan.FromMinutes(3), Http = pinnedHttpOptions },
    cancellationToken);
var endpoint = fixture.Endpoint;
```

Each call injects an immutable `GateScenario` through `CreateConfiguredAsync` and verifies it in the profile constructor/composition path, without changing process-wide environment variables. The existing [native-only example](../../examples/aspire-apphost.Tests/TypedProfileIntegrationTests.cs) compiles alongside the composed fixture example and remains the choice when resource health alone is enough.

The equivalence is native Aspire startup/readiness only. Browser execution, authentication, test data, and billing or monitoring assertions remain downstream responsibilities. The executable resource serves its local `/healthz` path so the packed consumer can exercise the fixture without relying on a remote service.

Prerequisites are the .NET 10 SDK, network access to NuGet.org for Aspire dependencies, and an OS-supported Aspire 13.4.4 AppHost/DCP runtime. This fixture graph contains an executable resource and does not require a container image. The repository script retains the generated feed and restore/build/run logs under `.gstack/tmp/issue-867-readiness-consumers`; use its `packageVersion`, `sourceCommit`, `sourceTree`, and `feed` values for the exact local install:

```sh
dotnet add package ForgeTrust.AppSurface.Aspire.Testing \
  --version "$PACKAGE_VERSION" \
  --source "$PACKAGE_FEED"
```

For the standalone prepared project, copy this directory's `Consumer.csproj.template` to `Consumer.csproj`, replace `{{PACKAGE_VERSION}}` with the reported exact version, copy `Program.cs.fixture` to `Program.cs`, and use a NuGet config with the reported local feed plus NuGet.org. Run the full proof with:

```sh
scripts/verify-evidence-readiness-consumers.sh
```

The script's final line is transactional: `EVIDENCE_READINESS_CONSUMERS_COMPLETE status=PASS ...` appears only after both packed consumers restore, build, and run. The typed run emits two `READINESS_GATE_EQUIVALENCE` lines and then a `READINESS_TYPED_CONSUMER_OK` line containing the actual package version, repository commit, dirty/clean source-tree state, and gate inputs. Both outputs say `timing=unmeasured`.

Run both neutral and typed packed consumers from the repository root with:

```sh
scripts/verify-evidence-readiness-consumers.sh
```

The script also packs Evidence.Contracts and Evidence.Aspire from a separately restored adapter closure. The typed consumer verifies each first-party nuspec's package version and repository commit, compares each restored first-party package's raw SHA-512 with the fresh local-feed archive and isolated-cache hash, confirms the packed restore graph has no project assets, checks the expected Aspire package versions, and checks the generated native DCP metadata before running both fixture calls. It inspects Evidence.Aspire's nuspec to require exactly Aspire.Hosting, Evidence.Contracts, and Evidence.Readiness dependencies; the typed consumer does not reference that adapter. These are executable source-bound equivalence examples; they do not prove Skoolit adoption and do not reproduce browser, authentication, billing, or business assertions. The local executable is a real Aspire-managed process resource, while its HTTP server is local to the consumer proof. Timing remains `unmeasured`.
