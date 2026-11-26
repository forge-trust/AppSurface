using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

// Test-only native process identity recorder used by the owned browser executable.
if (args is ["process-identity", var browserPid] && int.TryParse(browserPid, out var pid) && pid > 1)
{
    using var process = Process.GetProcessById(pid);
    Console.WriteLine(JsonSerializer.Serialize(new { pid = process.Id, startTicks = process.StartTime.ToUniversalTime().Ticks }));
    return 0;
}
if (args is ["recover", var recoverySettings, var expectedOwner])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var recoveryOptions = JsonSerializer.Deserialize<AppSurfaceKeycloakOwnershipSettings>(File.ReadAllBytes(recoverySettings))!;
    var result = await AppSurfaceKeycloakRunOwnership.RecoverAsync(recoveryOptions, expectedOwner, deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result.Cleared ? 0 : 70;
}
if (args is ["probe", var probeFile])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        using var probe = new AppSurfaceKeycloakRunOwnership(JsonSerializer.Deserialize<AppSurfaceKeycloakOwnershipSettings>(File.ReadAllBytes(probeFile))!);
        await probe.AcquireAsync(deadline.Token);
        Console.WriteLine("Acquired");
        return 0; // This creates an active record; the driver never uses positive probes on an empty namespace.
    }
    catch (AppSurfaceKeycloakOwnershipException error) { Console.WriteLine(error.Code); return 71; }
}
if (args is ["cleanup-capture", var capturedRoot])
{
    RealmClientCapture.UnsealForOwnedCleanup(capturedRoot);
    return 0;
}
// Test-only finite gates control adversarial timing; they are not production readiness resources.
if (args is ["proof-gate", var gateRoot, var gateKind])
{
    using var process = Process.GetCurrentProcess();
    File.WriteAllText(Path.Join(gateRoot, gateKind + ".process-started"), JsonSerializer.Serialize(new { pid = process.Id, startTicks = process.StartTime.ToUniversalTime().Ticks }));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; deadline.Cancel(); });
    try
    {
        File.WriteAllText(Path.Join(gateRoot, gateKind + ".ready"), "ready");
        while (!File.Exists(Path.Join(gateRoot, gateKind + ".release"))) await Task.Delay(30, deadline.Token);
        if (gateKind == "acceptance")
        {
            using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Join(gateRoot, "post-seed-observation.json")));
            if (!result.RootElement.GetProperty("verified").GetBoolean()) return 42;
        }
        return 0;
    }
    catch (OperationCanceledException) { return 124; }
}
if (args.Length != 4) return 64;
var root = Path.GetFullPath(args[0]);
var scenario = args[1];
var seedProject = args[2];
var webAssembly = args[3];
using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Join(root, "input.json")));
var runId = input.RootElement.GetProperty("runId").GetString()!;
var realm = input.RootElement.GetProperty("realm").GetString()!;
var port = input.RootElement.GetProperty("port").GetInt32();
var webPort = input.RootElement.GetProperty("webPort").GetInt32();
var settings = JsonSerializer.Deserialize<AppSurfaceKeycloakOwnershipSettings>(File.ReadAllBytes(Path.Join(root, "ownership.json")))!;
using var ownership = new AppSurfaceKeycloakRunOwnership(settings);
var desired = JsonSerializer.Deserialize<RealmReconcileDesired>(File.ReadAllBytes(Path.Join(root, "desired-input.json")))!;
var profile = input.RootElement.GetProperty("fixture").GetString()!;
var providerState = input.RootElement.GetProperty("providerState").GetString()!;
var tunnel = input.RootElement.GetProperty("tunnel").GetBoolean();
var realBrowser = input.RootElement.TryGetProperty("realBrowser", out var browserMode) && browserMode.GetBoolean();
var runDirectory = Path.Join(root, "capture");
var containerName = settings.ContainerName;
DistributedApplication? app = null;
Task? startTask = null;
string? containerId = null;
var owned = new List<IResource>();
var clean = false;
var rosterComplete = false;
var failure = "not-started";
var code = 41;
var stopSeconds = 0d;
using var cancel = new CancellationTokenSource();
using var signal = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; cancel.Cancel(); });
var events = new List<object>();
void Event(string kind) { lock (events) { events.Add(new { kind, at = DateTimeOffset.UtcNow }); File.WriteAllText(Path.Join(root, "events.json"), JsonSerializer.Serialize(events)); } }
ImmutableArray<AppSurfaceKeycloakOwnedProcess> Processes()
{
    var result = ImmutableArray.CreateBuilder<AppSurfaceKeycloakOwnedProcess>();
    foreach (var (file, role) in new[] { ("capture/result.json.started", "worker"), ("seed.started", "seed"), ("web.process-started", "web"), ("worker-release.process-started", "worker-release"), ("acceptance.process-started", "acceptance"),
        ("browser.process-started", "browser"), ("chromium-untrusted.process-started", "chromium-untrusted"), ("chromium.process-started", "chromium") })
    {
        var identityPath = ForgeTrust.AppSurface.Core.PathUtils.PathUnder(root, file);
        if (!File.Exists(identityPath)) continue;
        using var identity = JsonDocument.Parse(File.ReadAllBytes(identityPath));
        result.Add(new(role, identity.RootElement.GetProperty("pid").GetInt32(), identity.RootElement.GetProperty("startTicks").GetInt64()));
    }
    return result.ToImmutable();
}
async Task<string> Docker(CancellationToken ct, params string[] values)
{
    var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add("--host"); info.ArgumentList.Add("unix:///Users/andrew/.docker/run/docker.sock");
    foreach (var value in values) info.ArgumentList.Add(value);
    using var process = Process.Start(info) ?? throw new IOException();
    var output = process.StandardOutput.ReadToEndAsync(ct);
    var error = process.StandardError.ReadToEndAsync(ct);
    try { await process.WaitForExitAsync(ct); if (process.ExitCode != 0) throw new IOException("Docker observation failed."); return (await output).Trim(); }
    finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } _ = await error; }
}
async Task Acquire(CancellationToken ct)
{
    using var acquire = CancellationTokenSource.CreateLinkedTokenSource(ct);
    acquire.CancelAfter(TimeSpan.FromSeconds(30));
    await ownership.AcquireAsync(acquire.Token);
    ownership.RecordResources("Starting", null, [], false);
    File.WriteAllText(Path.Join(root, "lease-ready.json"), JsonSerializer.Serialize(new { active = ownership.ActivePath, lease = ownership.LeasePath }));
    Event("lease-marker");
    while (!File.Exists(Path.Join(root, "bootstrap.ready"))) await Task.Delay(30, ct);
}
try
{
    var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true, ProjectDirectory = root, EnableResourceLogging = true });
    builder.Services.AddSingleton<IHostLifetime, ControlledLifetime>();
    var user = builder.AddParameter("adapter-user", () => scenario == "worker-failure" ? "wrong-user" : "integrated-admin");
    var password = builder.AddParameter("adapter-password", () => File.ReadAllText(Path.Join(root, "server-private/admin.password")), secret: true);
    var client = desired.Confidential ? builder.AddParameter("adapter-client", () => File.ReadAllText(Path.Join(root, "server-private/client.secret")), secret: true) : null;
    var providers = new Dictionary<string, IResourceBuilder<ParameterResource>>(StringComparer.Ordinal);
    if (providerState is "full" or "partial-secret")
        providers.Add("google", builder.AddParameter("google-secret", () => File.ReadAllText(Path.Join(root, "server-private/provider.secret")), secret: true));
    var keycloak = builder.AddKeycloak("owned-keycloak", adminUsername: user, adminPassword: password)
        .WithImage("keycloak/keycloak", "26.7.2").WithImageRegistry("quay.io")
        .WithImageSHA256("831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669")
        .WithContainerName(containerName).WithVolume(settings.VolumeName, "/opt/keycloak/data");
    File.WriteAllText(Path.Join(root, "annotation-types.json"), JsonSerializer.Serialize(keycloak.Resource.Annotations.Select(annotation => annotation.GetType().FullName)));
    // Replace the fixture server's standard bootstrap/environment/endpoints with protected TLS bootstrap files.
    foreach (var annotation in keycloak.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray()) keycloak.Resource.Annotations.Remove(annotation);
    foreach (var annotation in keycloak.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ToArray()) keycloak.Resource.Annotations.Remove(annotation);
    foreach (var annotation in keycloak.Resource.Annotations.OfType<EndpointAnnotation>().Where(endpoint => endpoint.Name != "management").ToArray()) keycloak.Resource.Annotations.Remove(annotation);
    foreach (var annotation in keycloak.Resource.Annotations.OfType<HealthCheckAnnotation>().ToArray()) keycloak.Resource.Annotations.Remove(annotation);
    keycloak.WithEnvironment("KC_HEALTH_ENABLED", "true");
    keycloak.WithArgs("--config-file=/run/private/bootstrap.conf", "start-dev", "--http-enabled=false",
            "--https-certificate-file=/run/public/server.crt", "--https-certificate-key-file=/run/private/server.key", "--http-management-scheme=http")
        .WithBindMount(Path.Join(root, "server-private"), "/run/private", isReadOnly: true)
        .WithBindMount(Path.Join(root, "public"), "/run/public", isReadOnly: true)
        .WithContainerRuntimeArgs("--user", "0:0", "--publish", $"127.0.0.1:{port}:8443", "--label", "appsurface.feasibility=issue888-integrated", "--label", "appsurface.run=" + runId);
    var authority = desired.Authority;
    var webBase = tunnel ? "https://app-dev.getskoolit.com" : $"http://127.0.0.1:{webPort}";
    File.WriteAllText(Path.Join(root, "web-public.json"), JsonSerializer.Serialize(new
    {
        authority,
        webBaseAddress = webBase,
        clientId = desired.ClientId,
        confidential = desired.Confidential,
        fixture = profile,
        callbackPath = profile == "neutral" ? "/signin-appsurface-oidc" : "/signin-oidc",
        signedOutCallbackPath = profile == "neutral" ? "/signout-callback-appsurface-oidc" : "/signout-callback-oidc",
        keycloakPort = port,
        webPort,
        tunnel,
        realBrowser,
        browserCancel = scenario == "browser-cancel",
        providerState,
        logicalVolume = profile == "skoolit" ? "skoolit-keycloak-data-26-7-2" : "neutral-disposable-volume"
    }));
    keycloak.WithBindMount(Path.Join(root, "theme"), "/opt/keycloak/themes/acceptance", isReadOnly: true)
        .WithArgs("--spi-theme--static-max-age=-1", "--spi-theme--cache-themes=false", "--spi-theme--cache-templates=false");
    if (tunnel) keycloak.WithArgs("--hostname=https://auth-dev.getskoolit.com");
    builder.Services.AddHealthChecks().AddAsyncCheck("owned-tls", async ct =>
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    using var expected = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Join(root, "public/server.crt")));
                    return DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime()
                        && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), expected.GetCertHash(HashAlgorithmName.SHA256));
                }
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var reply = await http.GetAsync($"https://localhost:{port}/realms/master/.well-known/openid-configuration", ct);
            return reply.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("HTTP " + (int)reply.StatusCode);
        }
        catch (Exception error) { return HealthCheckResult.Unhealthy(error.GetType().Name + "/" + error.InnerException?.GetType().Name); }
    });
    keycloak.WithHealthCheck("owned-tls");
    owned.Add(keycloak.Resource);
    builder.OnBeforeStart(async (BeforeStartEvent _, CancellationToken ct) => await Acquire(ct));
    var wrapper = AppSurfaceKeycloakReconciliation.RegisterOwned(keycloak, desired, user, password, client, runDirectory, ownership, providers);
    var gate = wrapper.RealmReady();
    gate.Resource.WithEnvironment("APPSURFACE_KEYCLOAK_CA_CERTIFICATE_FILE", Path.Join(root, "public/server.crt"));
    if (!ReferenceEquals(gate, wrapper.RealmReady()) || Directory.Exists(runDirectory)) throw new InvalidDataException("Eager effects/cache failure.");
    if (scenario.StartsWith("capture-worker-", StringComparison.Ordinal))
    {
        var release = builder.AddExecutable("worker-release", "dotnet", AppContext.BaseDirectory, typeof(ControlledLifetime).Assembly.Location, "proof-gate", root, "worker-release").WaitFor(keycloak);
        owned.Add(release.Resource);
        gate.Resource.WaitForCompletion(release);
    }
    owned.Add(gate.Resource.Resource);
    SeedMetadata.Path = seedProject;
    var seed = wrapper.WithLocalSeed("required-seed", context => builder.AddProject<SeedMetadata>(context.ResourceName)
        .WithArgs(root, scenario is "seed-failure" ? "fail" : scenario is "cancel" or "uncertain-stop" ? "hold" : scenario.StartsWith("drift-", StringComparison.Ordinal) ? scenario : "success"));
    owned.Add(seed.Resource.Resource);
    var acceptance = builder.AddExecutable("post-seed-acceptance", "dotnet", AppContext.BaseDirectory, typeof(ControlledLifetime).Assembly.Location, "proof-gate", root, "acceptance").WaitForCompletion(seed.Resource);
    owned.Add(acceptance.Resource);
    var web = builder.AddExecutable("real-web", "dotnet", Path.GetDirectoryName(webAssembly)!, webAssembly, root, runId).WaitForCompletion(seed.Resource).WaitForCompletion(acceptance);
    owned.Add(web.Resource);
    IResourceBuilder<ExecutableResource>? browser = null;
    if (realBrowser)
    {
        var runtime = input.RootElement.GetProperty("browserRuntime");
        browser = builder.AddExecutable("browser-acceptance", runtime.GetProperty("node").GetString()!,
            runtime.GetProperty("directory").GetString()!, runtime.GetProperty("script").GetString()!, root,
            runtime.GetProperty("engine").GetString()!, typeof(ControlledLifetime).Assembly.Location)
            .WithEnvironment("HOME", Path.Join(root, "browser-private/home"))
            .WithEnvironment("TMPDIR", Path.Join(root, "browser-private/tmp"))
            .WithEnvironment("DEBUG", "")
            .WaitFor(web);
        owned.Add(browser.Resource);
    }
    app = builder.Build();
    if (Directory.Exists(runDirectory)) throw new InvalidDataException("Build staged secrets.");
    File.WriteAllText(Path.Join(root, "graph.json"), JsonSerializer.Serialize(new
    {
        cached = ReferenceEquals(gate, wrapper.RealmReady()),
        gate = gate.Resource.Resource.Name,
        workerArgs = gate.Resource.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().Count(),
        seedWaits = seed.Resource.Resource.Annotations.OfType<WaitAnnotation>().Select(a => new { name = a.Resource.Name, type = a.WaitType.ToString() }),
        projection = wrapper.Configuration.EnvironmentVariables,
        adapterPath = typeof(AppSurfaceKeycloakResource).Assembly.Location
    }));
    using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
    startup.CancelAfter(TimeSpan.FromSeconds(120));
    startTask = app.StartAsync(startup.Token);
    Event("app-start-requested");
    _ = Task.Run(async () =>
    {
        while (!startup.IsCancellationRequested)
        {
            var states = owned.Select(resource => app.ResourceNotifications.TryGetCurrentState(resource.Name, out var state)
                ? new { name = resource.Name, state = state.Snapshot.State?.Text ?? "unknown", exit = state.Snapshot.ExitCode } : new { name = resource.Name, state = "unknown", exit = (int?)null });
            File.WriteAllText(Path.Join(root, "resource-states.json"), JsonSerializer.Serialize(states));
            await Task.Delay(500, startup.Token);
        }
    }, startup.Token);
    var terminal = await app.ResourceNotifications.WaitForResourceAsync(gate.Resource.Resource.Name, state => state.Snapshot.ExitCode is not null || state.Snapshot.State?.Text == "FailedToStart", startup.Token);
    using (var observed = JsonDocument.Parse(await Docker(startup.Token, "inspect", containerName)))
    {
        var record = observed.RootElement[0];
        if (record.GetProperty("Name").GetString() != "/" + containerName || record.GetProperty("Config").GetProperty("Labels").GetProperty("appsurface.run").GetString() != runId) throw new IOException("Changed owned container.");
        containerId = record.GetProperty("Id").GetString();
    }
    File.WriteAllText(Path.Join(root, "gate-terminal.json"), JsonSerializer.Serialize(new { exitCode = terminal.Snapshot.ExitCode, state = terminal.Snapshot.State?.Text }));
    ownership.RecordResources("Running", containerId, Processes(), false);
    Event("gate-terminal");
    File.WriteAllText(Path.Join(root, "gate-observe.ready"), "ready");
    while (!File.Exists(Path.Join(root, "gate-observe.release"))) await Task.Delay(30, startup.Token);
    if (terminal.Snapshot.ExitCode != 0)
    {
        if (File.Exists(Path.Join(root, "seed.started")) || File.Exists(Path.Join(root, "web.started"))) throw new InvalidDataException();
        rosterComplete = terminal.Snapshot.ExitCode is not null && File.Exists(Path.Join(root, "capture/result.json.started"))
            && (!scenario.StartsWith("capture-worker-", StringComparison.Ordinal) || File.Exists(Path.Join(root, "worker-release.process-started")));
        failure = "required-worker-failed";
    }
    else
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        // Browser acceptance is a separate finite budget; worker and owned stop retain their 30-second gates.
        operation.CancelAfter(TimeSpan.FromSeconds(realBrowser ? 90 : 25));
        if (scenario is "cancel" or "uncertain-stop")
        {
            while (!File.Exists(Path.Join(root, "seed.started"))) await Task.Delay(50, operation.Token);
            rosterComplete = File.Exists(Path.Join(root, "capture/result.json.started")) && !File.Exists(Path.Join(root, "web.started"));
            File.WriteAllText(Path.Join(root, "ready-for-cancel"), "ready");
            if (scenario == "cancel") await Task.Delay(Timeout.Infinite, operation.Token);
            failure = "unverified-stop-injected";
        }
        else
        {
            var seedTerminal = await app.ResourceNotifications.WaitForResourceAsync(seed.Resource.Resource.Name, state => state.Snapshot.ExitCode is not null, operation.Token);
            if (seedTerminal.Snapshot.ExitCode != 0)
            {
                rosterComplete = File.Exists(Path.Join(root, "capture/result.json.started")) && File.Exists(Path.Join(root, "seed.started")) && !File.Exists(Path.Join(root, "web.started"));
                failure = "required-seed-failed";
            }
            else
            {
                var accepted = await app.ResourceNotifications.WaitForResourceAsync(acceptance.Resource.Name, state => state.Snapshot.ExitCode is not null, operation.Token);
                if (accepted.Snapshot.ExitCode == 42)
                {
                    if (File.Exists(Path.Join(root, "web.process-started"))) throw new InvalidDataException("Drift allowed web start.");
                    rosterComplete = File.Exists(Path.Join(root, "acceptance.process-started")) && File.Exists(Path.Join(root, "seed.started")) && File.Exists(Path.Join(root, "capture/result.json.started"));
                    failure = "post-seed-managed-drift";
                }
                else if (accepted.Snapshot.ExitCode != 0) throw new InvalidDataException("Acceptance proof failed.");
                else if (scenario.StartsWith("capture-web-", StringComparison.Ordinal))
                {
                    var rejected = await app.ResourceNotifications.WaitForResourceAsync(web.Resource.Name, state => state.Snapshot.ExitCode is not null, operation.Token);
                    if (rejected.Snapshot.ExitCode != 2 || !File.Exists(Path.Join(root, "web.proof-error")) || File.Exists(Path.Join(root, "web.started"))) throw new InvalidDataException("Expected capture rejection missing.");
                    using var captureError = JsonDocument.Parse(File.ReadAllBytes(Path.Join(root, "web.proof-error")));
                    if (captureError.RootElement.GetProperty("stage").GetString() != "client-capture") throw new InvalidDataException("Wrong web rejection stage.");
                    rosterComplete = File.Exists(Path.Join(root, "web.process-started")) && File.Exists(Path.Join(root, "acceptance.process-started")) && File.Exists(Path.Join(root, "seed.started")) && File.Exists(Path.Join(root, "capture/result.json.started"));
                    failure = "web-client-capture-rejected";
                }
                else
                {
                    while (!File.Exists(Path.Join(root, "web.started"))) await Task.Delay(50, operation.Token);
                    rosterComplete = !realBrowser && File.Exists(Path.Join(root, "capture/result.json.started")) && File.Exists(Path.Join(root, "seed.started")) && File.Exists(Path.Join(root, "acceptance.process-started"));
                    ownership.RecordResources("Running", containerId, Processes(), rosterComplete);
                    File.WriteAllText(Path.Join(root, "login.ready"), "ready");
                    while (!File.Exists(Path.Join(root, "login.release"))) await Task.Delay(50, operation.Token);
                    if (browser is not null)
                    {
                        var browserTerminal = await app.ResourceNotifications.WaitForResourceAsync(browser.Resource.Name,
                            state => state.Snapshot.ExitCode is not null, operation.Token);
                        rosterComplete = File.Exists(Path.Join(root, "browser.process-started"))
                            && File.Exists(Path.Join(root, "chromium.process-started"))
                            && File.Exists(Path.Join(root, "chromium-untrusted.process-started"));
                        if (browserTerminal.Snapshot.ExitCode != 0 || !File.Exists(Path.Join(root, "browser.complete")))
                            throw new InvalidDataException("Real browser acceptance failed.");
                    }
                    if (!File.Exists(Path.Join(root, "web.login-verified"))) throw new InvalidDataException("Real web login unverified.");
                    failure = "none"; code = 0;
                }
            }
        }
    }
}
catch (OperationCanceledException) { failure = cancel.IsCancellationRequested ? "cancelled" : "deadline"; }
catch (AppSurfaceKeycloakException error) when (providerState.StartsWith("partial-", StringComparison.Ordinal))
{
    File.WriteAllText(Path.Join(root, "validation-rejected.json"), JsonSerializer.Serialize(new { code = error.Code, ownershipAcquired = ownership.IsAcquired, captureExists = Directory.Exists(runDirectory) }));
    failure = "provider-pair-rejected";
    code = 44;
}
catch (Exception error) { failure = error.GetType().Name; File.WriteAllText(Path.Join(root, "safe-failure.json"), JsonSerializer.Serialize(new { type = error.GetType().Name, validation = error is Microsoft.Extensions.Options.OptionsValidationException optionsError ? optionsError.Failures.ToArray() : [], stack = error.StackTrace })); }
finally
{
    var clock = Stopwatch.StartNew();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        if (ownership.IsAcquired) ownership.RecordResources("Stopping", containerId, Processes(), rosterComplete);
        if (app is not null)
        {
            foreach (var resource in owned.AsEnumerable().Reverse())
            {
                try { using var item = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); item.CancelAfter(TimeSpan.FromSeconds(3)); await app.ResourceCommands.ExecuteCommandAsync(resource, KnownResourceCommands.StopCommand, item.Token).WaitAsync(item.Token); }
                catch { }
            }
            await app.StopAsync(stop.Token).WaitAsync(stop.Token);
            if (startTask?.IsCompleted == true) { try { await startTask; } catch (OperationCanceledException) when (!stop.IsCancellationRequested) { } }
            await app.DisposeAsync().AsTask().WaitAsync(stop.Token);
            app = null;
        }
        if (realBrowser && File.Exists(Path.Join(root, "browser.process-started"))
            && File.Exists(Path.Join(root, "chromium.process-started"))
            && File.Exists(Path.Join(root, "chromium-untrusted.process-started"))
            && File.Exists(Path.Join(root, "web.process-started"))
            && File.Exists(Path.Join(root, "capture/result.json.started"))
            && File.Exists(Path.Join(root, "seed.started")) && File.Exists(Path.Join(root, "acceptance.process-started")))
        {
            // Both fixed browser launch slots have native birth records. Verification still rejects live resources.
            rosterComplete = true;
            ownership.RecordResources("Stopping", containerId, Processes(), rosterComplete);
        }
        if (ownership.IsAcquired)
        {
            // Exact owned process/container observations share this one deadline.
            await ownership.VerifyStoppedAsync(stop.Token);
        }
        stopSeconds = clock.Elapsed.TotalSeconds;
        if (scenario == "uncertain-stop") throw new IOException("Unverified stop injected.");
        if (!ownership.IsAcquired)
        {
            if (code != 44) throw new IOException("No ownership acquired.");
        }
        else
        {
            if (!File.Exists(ownership.ActivePath)) throw new IOException("No ownership acquired.");
            File.WriteAllText(Path.Join(root, "preclean.ready"), "ready");
            while (!File.Exists(Path.Join(root, "preclean.release"))) await Task.Delay(30, stop.Token);
            stopSeconds = clock.Elapsed.TotalSeconds;
            if (stopSeconds >= 30 || !ownership.IsAcquired || !File.Exists(ownership.ActivePath)) throw new IOException();
            RealmClientCapture.UnsealForOwnedCleanup(root);
            if (Directory.Exists(Path.Join(root, "client-custody"))) Directory.Delete(Path.Join(root, "client-custody"), true);
            if (Directory.Exists(runDirectory)) Directory.Delete(runDirectory, true);
            if (realBrowser && Directory.Exists(Path.Join(root, "browser-private"))) Directory.Delete(Path.Join(root, "browser-private"), true);
            await ownership.ClearVerifiedAsync(stop.Token);
            stopSeconds = clock.Elapsed.TotalSeconds;
            clean = true;
        }
    }
    catch (Exception error) { stopSeconds = clock.Elapsed.TotalSeconds; failure += "/stop-" + error.GetType().Name; code = 43; }
    if (failure != "none" && code is not (43 or 44)) code = 41;
    File.WriteAllText(Path.Join(root, "host-receipt.json"), JsonSerializer.Serialize(new
    {
        scenario,
        failure,
        code,
        cleaned = clean,
        shutdownSeconds = stopSeconds,
        containerId,
        daemon = "recorded-in-active-state",
        activeRetained = File.Exists(ownership.ActivePath),
        ownerLockRetained = File.Exists(Path.Join(ownership.LeasePath, "owner.lock")),
        assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => new { name = a.GetName().Name, location = a.Location })
    }));

}
return code;

internal sealed class ControlledLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SeedMetadata : IProjectMetadata
{
    internal static string Path { get; set; } = string.Empty;
    public string ProjectPath => Path;
    public bool SuppressBuild => true;
}
