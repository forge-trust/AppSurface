using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using ForgeTrust.AppSurface.Auth.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

/// <summary>Runs the supervised graph through Aspire startup and shutdown using a loopback owner fixture and synthetic Docker.</summary>
public sealed class AppSurfaceKeycloakSupervisedWorkflowTests
{
    private const string DaemonId = "synthetic-workflow-daemon";
    private const string ImageId = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string VolumeName = "simulated-keycloak-data";

    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(true, false, false, null)]
    [InlineData(true, false, true, null)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(false, false, false, "new")]
    [InlineData(false, false, false, "uid1000")]
    [InlineData(false, false, false, "uid1000-empty")]
    [InlineData(false, false, false, "mixed")]
    public Task RunAsyncRequiresConfirmedStopBeforeCleanup(bool failSeed, bool confidential, bool unverifiedStop, string? storage)
        => RunScenarioAsync(new WorkflowScenario(
            Failure: failSeed ? WorkloadFailure.SeedNonzero : WorkloadFailure.None,
            Confidential: confidential,
            UnverifiedStop: unverifiedStop,
            Storage: storage));

    [Theory]
    [InlineData("image")]
    [InlineData("image-id")]
    [InlineData("user")]
    [InlineData("privileged")]
    [InlineData("restart")]
    [InlineData("init")]
    [InlineData("writable-root")]
    [InlineData("pid-namespace")]
    [InlineData("network-namespace")]
    [InlineData("capabilities")]
    [InlineData("retained-capabilities")]
    [InlineData("security-options")]
    [InlineData("process-limit")]
    [InlineData("memory-limit")]
    [InlineData("devices")]
    [InlineData("undeclared-mount")]
    [InlineData("missing-mount")]
    [InlineData("mount-type")]
    [InlineData("writable-mount")]
    [InlineData("changed-mount-source")]
    [InlineData("unexpected-port")]
    [InlineData("nonloopback-port")]
    [InlineData("missing-anchor-port")]
    [InlineData("extra-anchor-port")]
    [InlineData("multiple-anchor-bindings")]
    [InlineData("invalid-anchor-port-mapping")]
    [InlineData("alternate-host-port")]
    [InlineData("anchor-network")]
    [InlineData("anchor-pid-namespace")]
    [InlineData("changed-daemon")]
    public Task RunAsyncRetainsOwnershipWhenStoppedInventoryChanges(string inventoryDrift)
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.None,
            Confidential: false,
            UnverifiedStop: false,
            Storage: null,
            InventoryDrift: inventoryDrift));

    [Fact]
    public Task RequiredSeedTimeoutStopsTheStillRunningSeedBeforeCleanup()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.SeedTimeout, Confidential: false, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task CallerCancellationDuringRequiredSeedStopsItBeforeCleanupAndWithholdsWeb()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.SeedCancelled, Confidential: false, UnverifiedStop: false, Storage: null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CreatedWorkerOrSeedWithZeroExitCodeWithholdsTheNextWorkload(bool worker)
        => RunScenarioAsync(new WorkflowScenario(
            Failure: worker ? WorkloadFailure.WorkerStillCreated : WorkloadFailure.SeedStillCreated,
            Confidential: false, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task RunPublishesReadinessAndShutsDownWithoutLifecycleObservationHooks()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.None, Confidential: false, UnverifiedStop: false, Storage: null, AttachLifecycleObservation: false));

    [Fact]
    public Task WaitsForAnOwnedWebContainerThatIsStillStoppingBeforeCleanup()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.None, Confidential: false, UnverifiedStop: false, Storage: null, DelayedWebStop: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RefusesToPassAResolvedSecretToDockerBeforeCreatingTheSeed(bool environment)
        => RunScenarioAsync(new WorkflowScenario(
            Failure: environment ? WorkloadFailure.SeedSecretInEnvironment : WorkloadFailure.SeedSecretInArguments,
            Confidential: false, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task RunsTheWorkerAfterKeycloakRespondsWithTheStagedCertificate()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.None, Confidential: false, UnverifiedStop: false, Storage: null,
            UsePinnedTransport: true));

    [Fact]
    public Task UnexpectedWebExitFencesTheOwnedGraphBeforeCleanup()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.WebExit, Confidential: false, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task UnexpectedKeycloakExitAfterReadinessFencesTheOwnedGraphBeforeCleanup()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.ServerExit, Confidential: false, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task OwnerConnectionFailureRemainsLatchedWhenWorkflowFailsDuringShutdown()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.OwnerLossDuringWorkflow, Confidential: false, UnverifiedStop: false, Storage: null));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public Task WorkerReadinessFailureWithholdsRequiredSeedsAndWeb(bool confidential, bool referenceMissing)
        => RunScenarioAsync(new WorkflowScenario(
            Failure: referenceMissing ? WorkloadFailure.MissingReference : WorkloadFailure.WorkerNonzero,
            Confidential: confidential, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task PublicThemeAndOrderedSeedsKeepResolvedCredentialsInPrivateMounts()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.None, Confidential: false, UnverifiedStop: false, Storage: null,
            PublicThemeAndSeeds: true));

    [Fact]
    public Task UnacknowledgedAnchorStartFencesButRetainsTheOwnerMarker()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.AnchorStartAcknowledgement, Confidential: false, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task RejectsAnUnownedHostProjectBeforeQueryingDockerOrStaging()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.UnownedProject, Confidential: false, UnverifiedStop: false, Storage: null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReportsAnActiveMarkerOnHostStopFailureOnlyAfterReservation(bool reservesOwnership)
        => RunScenarioAsync(new WorkflowScenario(
            Failure: reservesOwnership ? WorkloadFailure.None : WorkloadFailure.UnownedProject,
            Confidential: false, UnverifiedStop: false, Storage: null, HostStopFailure: true));

    [Fact]
    public Task CancellationBeforeDockerCreateAllowsVerifiedCleanup()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.CancelBeforeCreate, Confidential: false, UnverifiedStop: false, Storage: null));

    [Fact]
    public Task TransportDeadlineWithholdsWorkerSeedsAndWebAndStopsTheServer()
        => RunScenarioAsync(new WorkflowScenario(
            Failure: WorkloadFailure.TransportUnavailable, Confidential: false, UnverifiedStop: false, Storage: null));

    private enum WorkloadFailure
    {
        None, SeedNonzero, SeedTimeout, SeedCancelled, SeedSecretInArguments, SeedSecretInEnvironment,
        WebExit, ServerExit, WorkerNonzero, MissingReference, AnchorStartAcknowledgement, TransportUnavailable, OwnerLossDuringWorkflow,
        UnownedProject, CancelBeforeCreate, WorkerStillCreated, SeedStillCreated
    }

    private sealed record WorkflowScenario(
        WorkloadFailure Failure,
        bool Confidential,
        bool UnverifiedStop,
        string? Storage,
        string? InventoryDrift = null,
        bool UsePinnedTransport = false,
        bool PublicThemeAndSeeds = false,
        bool DelayedWebStop = false,
        bool AttachLifecycleObservation = true,
        bool HostStopFailure = false)
    {
        internal bool RetainActive => UnverifiedStop || InventoryDrift is not null
            || Failure == WorkloadFailure.AnchorStartAcknowledgement || HostStopFailure && Failure != WorkloadFailure.UnownedProject;
        internal string ExpectedCode => RetainActive || HostStopFailure || Failure == WorkloadFailure.OwnerLossDuringWorkflow ? "ASKEYC034"
            : Failure switch
            {
                WorkloadFailure.None => "Completed",
                WorkloadFailure.SeedCancelled or WorkloadFailure.CancelBeforeCreate => "ASKEYC032",
                WorkloadFailure.UnownedProject => "ASKEYC028",
                _ => "ASKEYC031"
            };
        internal int ExpectedExitCode => ExpectedCode == "Completed" ? 0 : 1;
        internal bool KeepsSeedRunning => Failure is WorkloadFailure.SeedTimeout or WorkloadFailure.SeedCancelled;
        internal bool StartsServer => Failure is not (
            WorkloadFailure.MissingReference or WorkloadFailure.AnchorStartAcknowledgement or WorkloadFailure.OwnerLossDuringWorkflow);
        internal bool StartsWorker => StartsServer && Failure != WorkloadFailure.TransportUnavailable;
        internal bool ReachesReady => Failure is WorkloadFailure.None or WorkloadFailure.WebExit or WorkloadFailure.ServerExit;
        internal bool ExpectsSeed => StartsWorker && Failure is not
            (WorkloadFailure.WorkerNonzero or WorkloadFailure.WorkerStillCreated
            or WorkloadFailure.SeedSecretInArguments or WorkloadFailure.SeedSecretInEnvironment);
        internal bool ExpectsObservation => Storage is not null;
        internal bool ExpectsInitializer => Storage == "new";
        internal bool ExpectsWeb => ReachesReady;
        internal bool RetainsMarker => RetainActive;
        internal bool ExpectsCleanup => !RetainActive && Failure != WorkloadFailure.UnownedProject;
        internal string[] ExpectedRoles
        {
            get
            {
                var roles = new List<string> { "anchor" };
                if (StartsServer) roles.Add("keycloak");
                if (ExpectsInitializer) roles.Add("initialize");
                if (StartsWorker) roles.Add("worker");
                if (ExpectsSeed) roles.Add("seed-workload");
                if (PublicThemeAndSeeds && ExpectsSeed) roles.Add("second-seed");
                if (ExpectsWeb) roles.Add("web-workload");
                return roles.ToArray();
            }
        }
    }

    private static Task RunScenarioAsync(WorkflowScenario scenario) => WorkflowFixture.RunAsync(scenario);

    /// <summary>Runs the Aspire host and verifies each supervised workflow scenario.</summary>
    private static class WorkflowFixture
    {
        internal static async Task RunAsync(WorkflowScenario scenario)
        {
            var failure = scenario.Failure;
            var confidential = scenario.Confidential;
            var storage = scenario.Storage;
            var usePinnedTransport = scenario.UsePinnedTransport;
            var publicThemeAndSeeds = scenario.PublicThemeAndSeeds;
            if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

            var anchorStartUncertain = failure == WorkloadFailure.AnchorStartAcknowledgement;
            var transportUnavailable = failure == WorkloadFailure.TransportUnavailable;
            var reachesReady = scenario.ReachesReady;
            var cancelsDuringSeed = failure == WorkloadFailure.SeedCancelled;

            using var directory = new TempDirectory();
            var physicalRoot = Directory.ResolveLinkTarget("/var", returnFinalTarget: true) is { } varDirectory
                && directory.Path.StartsWith("/var/", StringComparison.Ordinal)
                ? varDirectory.FullName + directory.Path["/var".Length..]
                : directory.Path;
            var payload = TestPathUtils.PathUnder(physicalRoot, "payload");
            Directory.CreateDirectory(payload);
            File.WriteAllText(TestPathUtils.PathUnder(payload, "App.dll"), "synthetic workload payload");
            const string publicIssuer = "https://auth.example.test";
            const string publicWeb = "https://web.example.test";
            const string themeName = "fixture-theme";
            var themeRoot = TestPathUtils.PathUnder(physicalRoot, "theme");
            if (publicThemeAndSeeds)
            {
                var login = Directory.CreateDirectory(TestPathUtils.PathUnder(themeRoot, "login"));
                File.WriteAllText(TestPathUtils.PathUnder(login.FullName, "theme.properties"), "parent=keycloak\n");
                File.Copy(typeof(KeycloakSecurity).Assembly.Location,
                    TestPathUtils.PathUnder(payload, "ForgeTrust.AppSurface.Auth.Keycloak.dll"));
            }
            var shutdownMilestone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var fixtureLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var keycloakPort = FreePort();
            using var tlsListener = usePinnedTransport ? new TcpListener(IPAddress.Loopback, keycloakPort) : null;
            var dockerFixture = new SyntheticDockerFixture(scenario, fixtureLifetime, keycloakPort, tlsListener);
            var docker = dockerFixture.Docker;

            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = ["--environment", "Testing"],
                DisableDashboard = true
            });
            builder.Configuration["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
            builder.Configuration["DcpPublisher:CliPath"] = TestPathUtils.PathUnder(physicalRoot, "unused-dcp");
            builder.Configuration["DcpPublisher:DashboardPath"] = TestPathUtils.PathUnder(physicalRoot, "unused-dashboard");
            builder.Services.RemoveAll<IHostedService>();
            builder.Services.AddSingleton<IHostedService, PublishAfterResourcesCreatedHostedService>();
            builder.Services.AddSingleton<IHostLifetime>(new RuntimeLifetime(scenario.HostStopFailure));

            var username = builder.AddParameter("test-user", () => "test-user");
            var password = builder.AddParameter("test-password", () => "test-password", secret: true);
            var execution = new AppSurfaceKeycloakSupervisedOptions
            {
                LeaseRoot = TestPathUtils.PathUnder(physicalRoot, "leases"),
                RunRoot = TestPathUtils.PathUnder(physicalRoot, "runs"),
                DockerEndpoint = DockerEndpoint(),
                TransportReadyTimeout = TimeSpan.FromSeconds(2),
                LocalSeedTimeout = TimeSpan.FromSeconds(2)
            };
            var options = new AppSurfaceKeycloakOptions
            {
                KeycloakPort = keycloakPort,
                WebProofPort = FreePort(),
                ClientMode = confidential ? KeycloakClientMode.Confidential : KeycloakClientMode.Public,
                RequireIdentitySeed = true,
                PersistentVolumeName = storage is null ? null : VolumeName,
                SupervisedExecution = execution
            };
            options.SeededUsers.Clear();
            var expectedKeycloakImage = AppSurfaceKeycloakSupervisedOptions.TaggedQualifiedKeycloakImage;
            if (publicThemeAndSeeds)
            {
                expectedKeycloakImage = "quay.io/keycloak/keycloak:26.6@" + ImageId;
                execution.KeycloakImage = "quay.io/keycloak/keycloak@" + ImageId;
                options.Authority = new Uri(publicIssuer + "/realms/" + options.Realm);
                options.ApprovedHttpsOrigins.Add(new Uri(publicIssuer));
                options.ApprovedHttpsOrigins.Add(new Uri(publicWeb));
                options.RedirectUris.Add(new Uri(publicWeb + options.CallbackPath));
                options.PostLogoutRedirectUris.Add(new Uri(publicWeb + options.SignedOutCallbackPath));
                options.LoginTheme = AppSurfaceKeycloakThemeOptions.Login(themeName, themeRoot,
                    AppSurfaceKeycloakImageReference.Parse(expectedKeycloakImage));
                options.LoginTheme.Platform = "linux/arm64";
            }
            var declaredBindings = new AppSurfaceKeycloakCredentialBindings();
            if (confidential)
            {
                options.IdentityProviders.Add(new("google", "synthetic-provider-client"));
                declaredBindings.ClientSecret = builder.AddParameter("client-secret", () => "fixture-private-client", secret: true);
                declaredBindings.IdentityProviderSecrets.Add("google",
                    builder.AddParameter("provider-secret", () => "fixture-private-provider", secret: true));
            }
            var snapshot = options.CreateValidatedSnapshot();
            var bindings = declaredBindings.CreateSnapshot(builder, snapshot.CreateDesiredConfiguration(), username, password);
            var transport = new TransportReadinessHandler(publicThemeAndSeeds, transportUnavailable);
            using var http = new HttpClient(transport);
            var resource = AppSurfaceKeycloakSupervisedRun.Register(builder, "identity", snapshot, bindings, docker,
                httpClient: usePinnedTransport ? null : () => http);
            var run = Assert.IsType<AppSurfaceKeycloakSupervisedRun>(resource.SupervisedRun);
            run.InjectUnverifiedStop = scenario.UnverifiedStop;
            var seed = new AppSurfaceKeycloakSupervisedWorkload("seed-workload", payload, "App.dll", []);
            const string seedCredentialValue = "qualified-seed-only-fixture-value";
            var seedResolutions = 0;
            if (publicThemeAndSeeds)
            {
                run.WithPublicInputs(new Dictionary<string, string> { ["PublicWebOrigin"] = publicWeb });
                var seedCredential = builder.AddParameter("seed-credential", () =>
                {
                    Interlocked.Increment(ref seedResolutions);
                    return seedCredentialValue;
                }, secret: true);
                seed = seed with { PrivateInputs = seed.PrivateInputs.Add("SeedCredential", seedCredential) };
            }
            if (failure == WorkloadFailure.SeedSecretInArguments)
                seed = seed with { Arguments = ["--fixture=test-password"] };
            if (failure == WorkloadFailure.SeedSecretInEnvironment)
                seed = seed with { Environment = ImmutableDictionary<string, string>.Empty.Add("FIXTURE_VALUE", "test-password") };
            run.WithSeed(seed);
            if (publicThemeAndSeeds)
                run.WithSeed(seed with { Name = "second-seed" });
            run.WithWeb(new("web-workload", payload, "App.dll", [])
            {
                Environment = ImmutableDictionary<string, string>.Empty.Add("APP_WORKLOAD", "fixture-web")
            });
            if (failure == WorkloadFailure.UnownedProject)
                builder.AddResource(new ProjectResource("unowned-host-project"));
            using var appCancellation = new CancellationTokenSource();
            if (scenario.AttachLifecycleObservation) run.LifecycleObservation = Observe;

            async Task Observe(string phase, CancellationToken token)
            {
                if (anchorStartUncertain && phase == "anchor-start-ack")
                    throw new IOException("Fixture loses the anchor start acknowledgement before saving it.");
                if (failure == WorkloadFailure.OwnerLossDuringWorkflow && phase == "fence-ready")
                {
                    dockerFixture.DisconnectOwner();
                    // Wait for the owner-connection failure to trigger shutdown before failing the workflow too.
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    throw new IOException("Fixture workflow fails after owner connection loss.");
                }
                if (cancelsDuringSeed && phase == "seed-workload-started")
                    shutdownMilestone.TrySetResult();
                if (failure == WorkloadFailure.CancelBeforeCreate && phase == "anchor-intent")
                {
                    appCancellation.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                if (phase == "ready")
                {
                    if (failure == WorkloadFailure.WebExit)
                    {
                        lock (dockerFixture.ContainersLock)
                        {
                            var web = Assert.Single(dockerFixture.Containers.Values, container => container.Role == "web-workload");
                            web.Running = false;
                            web.Status = "exited";
                            web.ExitCode = 19;
                        }
                    }
                    if (failure == WorkloadFailure.ServerExit)
                    {
                        lock (dockerFixture.ContainersLock)
                        {
                            var server = Assert.Single(dockerFixture.Containers.Values, container => container.Role == "keycloak");
                            server.Running = false;
                            server.Status = "exited";
                            server.ExitCode = 19;
                        }
                    }
                    shutdownMilestone.TrySetResult();
                }
            }

            var beforeClean = false;
            run.BeforeCleanObservation = _ =>
            {
                beforeClean = true;
                if (publicThemeAndSeeds)
                {
                    foreach (var role in new[] { "seed-workload", "second-seed" })
                    {
                        var privateDirectory = TestPathUtils.PathUnder(run.RunDirectory, "private-workloads", role);
                        var privateFile = Assert.Single(Directory.EnumerateFileSystemEntries(privateDirectory));
                        Assert.Equal(TestPathUtils.PathUnder(privateDirectory, "input.json"), privateFile);
                        var privateInput = File.ReadAllText(privateFile);
                        using var parsed = JsonDocument.Parse(privateInput);
                        Assert.Equal(seedCredentialValue, parsed.RootElement.GetProperty("SeedCredential").GetString());
                    }
                    var publicInput = File.ReadAllText(TestPathUtils.PathUnder(run.RunDirectory, "public", "config.json"));
                    using var publicConfig = JsonDocument.Parse(publicInput);
                    Assert.Equal(publicWeb, publicConfig.RootElement.GetProperty("PublicWebOrigin").GetString());
                    Assert.Equal(options.Authority!.OriginalString, publicConfig.RootElement.GetProperty("Authority").GetString());
                    Assert.Equal($"https://localhost:{keycloakPort}/realms/{options.Realm}",
                        publicConfig.RootElement.GetProperty("AdminAuthority").GetString());
                    using var workerBindings = JsonDocument.Parse(
                        File.ReadAllText(TestPathUtils.PathUnder(run.RunDirectory, "private-worker", "bindings.json")));
                    Assert.Equal($"https://localhost:{keycloakPort}/", workerBindings.RootElement.GetProperty("BaseAddress").GetString());
                    Assert.DoesNotContain(seedCredentialValue, publicInput);
                }
                lock (dockerFixture.ContainersLock)
                {
                    Assert.All(dockerFixture.Containers.Values, container => Assert.False(container.Running));
                    foreach (var seedContainer in dockerFixture.Containers.Values.Where(container => container.Role is "seed-workload" or "second-seed"))
                    {
                        var mount = Assert.Single(seedContainer.Mounts, candidate => candidate.Destination == "/proof/private");
                        Assert.Equal(TestPathUtils.PathUnder(run.RunDirectory, "private-workloads", seedContainer.Role), mount.Source);
                        Assert.False(mount.Writable);
                    }
                    Assert.All(dockerFixture.Commands.Where(command => command[0] == "rm"),
                        command => Assert.Contains(dockerFixture.Containers[command[1]].Role, new[] { "observation", "initialize" }));
                }
                return Task.CompletedTask;
            };

            var app = builder.Build();
            var running = run.RunAsync(app, appCancellation.Token);
            if (reachesReady || cancelsDuringSeed)
            {
                using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var milestone = scenario.AttachLifecycleObservation ? shutdownMilestone.Task : ObservePublicReadiness();
                var first = await Task.WhenAny(milestone, running).WaitAsync(observation.Token);
                if (first == running)
                {
                    observation.Cancel();
                    if (dockerFixture.HeartbeatFixture is { IsFaulted: true } heartbeat)
                        await heartbeat;
                    if (dockerFixture.TlsFixture is not null) await dockerFixture.TlsFixture.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Fail("The supervised run exited before the expected lifecycle milestone with " + (await running).Code);
                }
                await milestone;
                if (!scenario.AttachLifecycleObservation) Assert.Equal("Ready", run.DiagnosticCode);
                if (failure == WorkloadFailure.None || cancelsDuringSeed) appCancellation.Cancel();

                async Task ObservePublicReadiness()
                {
                    while (run.DiagnosticCode != "Ready")
                        await Task.Delay(TimeSpan.FromMilliseconds(20), observation.Token);
                }
            }
            var result = await running.WaitAsync(TimeSpan.FromSeconds(30));
            if (scenario.HostStopFailure) await app.DisposeAsync();
            if (dockerFixture.HeartbeatFixture is not null) await dockerFixture.HeartbeatFixture.WaitAsync(TimeSpan.FromSeconds(2));
            if (usePinnedTransport)
                await Assert.IsAssignableFrom<Task>(dockerFixture.TlsFixture).WaitAsync(TimeSpan.FromSeconds(2));

            var expectedCode = scenario.ExpectedCode;
            Assert.Equal(scenario.ExpectedExitCode, result.ExitCode);
            Assert.Equal(expectedCode, result.Code);
            Assert.Equal(result.Code, run.DiagnosticCode);
            Assert.Equal(scenario.RetainActive, result.ActiveRetained);
            Assert.Equal(scenario.ExpectsCleanup, beforeClean);
            Assert.InRange(result.StopSeconds, 0, 30);
            if (failure == WorkloadFailure.UnownedProject)
            {
                Assert.Empty(dockerFixture.Commands);
                Assert.False(Directory.Exists(run.RunDirectory));
                Assert.False(Directory.Exists(execution.LeaseRoot));
                return;
            }
            if (failure == WorkloadFailure.CancelBeforeCreate)
            {
                Assert.DoesNotContain(dockerFixture.Commands, command => command[0] is "create" or "start" or "rm");
                Assert.Empty(Directory.EnumerateFiles(execution.LeaseRoot, "active.json", SearchOption.AllDirectories));
                return;
            }
            Assert.True(Guid.TryParseExact(dockerFixture.OwnerId, "N", out _));
            Assert.Equal(dockerFixture.OwnerId, run.OwnerId);
            lock (dockerFixture.ContainersLock)
            {
                var roles = dockerFixture.Containers.Values.Select(container => container.Role).ToArray();
                Assert.Equal(scenario.ExpectedRoles.Order(StringComparer.Ordinal),
                    roles.Where(role => role != "observation").Order(StringComparer.Ordinal));
                if (scenario.ExpectsObservation) Assert.Contains("observation", roles);
                if (scenario.StartsServer)
                {
                    var server = Assert.Single(dockerFixture.Containers.Values, item => item.Role == "keycloak");
                    Assert.Equal(expectedKeycloakImage, server.Image);
                    Assert.Equal(storage is not null, server.Mounts.Any(mount => mount.Type == "volume" && mount.Source == VolumeName));
                    if (storage is "uid1000" or "uid1000-empty")
                    {
                        Assert.Equal("1000:0", server.User);
                        Assert.Contains("--https-certificate-file=/proof/server/server.crt", server.CreateArguments);
                    }
                    if (storage == "mixed")
                    {
                        Assert.Equal(SupervisedKeycloakContainerProfile.UserId() + ":0", server.User);
                        Assert.Contains("--https-certificate-file=/proof/public/server.crt", server.CreateArguments);
                    }
                    if (publicThemeAndSeeds)
                    {
                        var mount = Assert.Single(server.Mounts, candidate => candidate.Destination == "/opt/keycloak/themes/" + themeName);
                        Assert.Equal(themeRoot, mount.Source);
                        Assert.False(mount.Writable);
                        Assert.Contains("--hostname=" + publicIssuer, server.CreateArguments);
                        Assert.Contains("--hostname-admin=https://localhost:" + keycloakPort, server.CreateArguments);
                        Assert.Contains("--spi-theme--cache-templates=false", server.CreateArguments);
                    }
                }
                Assert.All(dockerFixture.Containers.Values, container =>
                {
                    Assert.False(container.Running);
                    Assert.Equal(scenario.ExpectsCleanup, container.Removed);
                });
                var anchor = Assert.Single(dockerFixture.Containers.Values, item => item.Role == "anchor");
                Assert.Equal(confidential, anchor.Mounts.Any(mount => mount.Destination == "/proof/private"));
                if (reachesReady)
                {
                    var web = Assert.Single(dockerFixture.Containers.Values, item => item.Role == "web-workload");
                    Assert.DoesNotContain(web.Mounts, mount => mount.Destination == "/proof/private");
                    Assert.Contains("APP_WORKLOAD=fixture-web", web.CreateArguments);
                }
            }

            var observed = dockerFixture.Commands.ToArray();
            Assert.Contains(observed, command => command[0] == "create" && command.Contains("appsurface.run=" + dockerFixture.OwnerId, StringComparer.Ordinal)
                && command.Contains("appsurface.supervised=1", StringComparer.Ordinal));
            Assert.Contains(observed, command => command[0] == "ps");
            Assert.Contains(observed, command => command[0] == "inspect");
            Assert.Contains(observed, command => command[0] == "stop" && command[^1] == dockerFixture.AnchorId);
            Assert.Equal(scenario.KeepsSeedRunning, dockerFixture.SeedWasRunningAtFence);
            Assert.Equal(scenario.DelayedWebStop, dockerFixture.ObservedWebStillStopping);
            Assert.DoesNotContain(observed.SelectMany(command => command),
                argument => argument.Contains("test-password", StringComparison.Ordinal));
            var firstRemove = Array.FindIndex(observed, command => command[0] == "rm"
                && dockerFixture.Containers[command[1]].Role is not ("observation" or "initialize"));
            var stopIndex = Array.FindIndex(observed, command => command[0] == "stop");
            if (scenario.RetainActive) Assert.Equal(-1, firstRemove);
            else Assert.True(firstRemove > stopIndex);
            string[] expectedRemoved;
            lock (dockerFixture.ContainersLock) expectedRemoved = scenario.ExpectsCleanup ? dockerFixture.Containers.Keys.Order(StringComparer.Ordinal).ToArray() : [];
            Assert.Equal(expectedRemoved, observed.Where(command => command[0] == "rm")
                .Select(command => command[1]).Order(StringComparer.Ordinal).ToArray());
            if (publicThemeAndSeeds)
            {
                Assert.Equal(3, transport.Calls);
                Assert.Equal(1, seedResolutions);
                var order = observed.Where(command => command[0] == "start")
                    .Select(command => dockerFixture.Containers[command[1]].Role).ToArray();
                Assert.Equal(new[] { "anchor", "keycloak", "worker", "seed-workload", "second-seed", "web-workload" }, order);
                Assert.DoesNotContain(observed.SelectMany(command => command), argument => argument.Contains(seedCredentialValue, StringComparison.Ordinal));
                Assert.False(Directory.Exists(TestPathUtils.PathUnder(run.RunDirectory, "private-workloads")));
            }
            var leaseName = storage is null ? "disposable-" + dockerFixture.OwnerId : VolumeName;
            var leaseKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DaemonId + "\n" + leaseName))).ToLowerInvariant();
            var markerPath = TestPathUtils.PathUnder(execution.LeaseRoot, leaseKey, "active.json");
            Assert.Equal(scenario.RetainsMarker, File.Exists(markerPath));
            if (anchorStartUncertain)
            {
                using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
                Assert.True(marker.RootElement.GetProperty("LaunchUncertain").GetBoolean());
                Assert.False(marker.RootElement.GetProperty("OwnerFenceEstablished").GetBoolean());
            }
            if (transportUnavailable) Assert.True(transport.Calls >= 2);
        }
    }

    private sealed class SyntheticDockerFixture
    {
        private const string VolumeRoot = "/fixture/volumes/simulated-keycloak-data/_data";
        private readonly WorkflowScenario _scenario;
        private readonly CancellationTokenSource _lifetime;
        private readonly int _keycloakPort;
        private readonly TcpListener? _tlsListener;
        private readonly bool _failSeed;
        private readonly bool _keepSeedRunning;
        private readonly bool _referenceMissing;
        private readonly bool _workerFails;
        private readonly bool _anchorStartUncertain;
        private readonly bool _publicThemeAndSeeds;
        private readonly TaskCompletionSource _disconnectOwner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly KeycloakVolumeIncarnation Stamp = new(1, 2, 3, 4, 5);
        private bool _volumeExists;
        private bool _fenceRequested;
        private int _nextId = 1;
        private string? _ownerId;
        private string? _anchorId;
        private Task? _heartbeatFixture;
        private Task? _tlsFixture;

        internal SyntheticDockerFixture(WorkflowScenario scenario, CancellationTokenSource lifetime, int keycloakPort,
            TcpListener? tlsListener)
        {
            _scenario = scenario;
            _lifetime = lifetime;
            _keycloakPort = keycloakPort;
            _tlsListener = tlsListener;
            _failSeed = scenario.Failure == WorkloadFailure.SeedNonzero || scenario.KeepsSeedRunning;
            _keepSeedRunning = scenario.KeepsSeedRunning;
            _referenceMissing = scenario.Failure == WorkloadFailure.MissingReference;
            _workerFails = scenario.Failure == WorkloadFailure.WorkerNonzero;
            _anchorStartUncertain = scenario.Failure == WorkloadFailure.AnchorStartAcknowledgement;
            _publicThemeAndSeeds = scenario.PublicThemeAndSeeds;
            _volumeExists = scenario.Storage is "uid1000" or "uid1000-empty" or "mixed";
            Docker = new SupervisedDocker(DockerEndpoint(), HandleCommandAsync);
        }

        internal SupervisedDocker Docker { get; }
        internal ConcurrentQueue<string[]> Commands { get; } = new();
        internal Dictionary<string, ContainerState> Containers { get; } = new(StringComparer.Ordinal);
        internal object ContainersLock { get; } = new();
        internal string? OwnerId => _ownerId;
        internal string? AnchorId => _anchorId;
        internal Task? HeartbeatFixture => _heartbeatFixture;
        internal Task? TlsFixture => _tlsFixture;
        internal bool SeedWasRunningAtFence { get; private set; }
        internal bool ObservedWebStillStopping { get; private set; }

        internal void DisconnectOwner() => _disconnectOwner.SetResult();

        private Task<string> CreateVolume()
        {
            _volumeExists = true;
            return Task.FromResult(VolumeName);
        }

        private Task<string> HandleCommandAsync(CancellationToken ct, string[] args)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Enqueue(args);
            if (args[0] == "info") return Task.FromResult(_fenceRequested && _scenario.InventoryDrift == "changed-daemon" ? "replacement-daemon" : DaemonId);
            if (args[0] == "volume")
            {
                if (_scenario.Storage is null) throw new InvalidOperationException("The disposable run must not use a named volume.");
                return args[1] switch
                {
                    "ls" => Task.FromResult(_volumeExists ? VolumeName : string.Empty),
                    "inspect" when _volumeExists && args[2] == VolumeName => Task.FromResult(JsonSerializer.Serialize(new[]
                    {
                        new { Name = VolumeName, Driver = "local", Scope = "local", CreatedAt = "created",
                            Mountpoint = VolumeRoot, Options = (object?)null, Labels = (object?)null }
                    })),
                    "create" when !_volumeExists && args[2] == VolumeName => CreateVolume(),
                    _ => throw new InvalidOperationException("Unexpected synthetic volume command.")
                };
            }
            if (args[0] == "image" && args[1] == "inspect")
                return Task.FromResult(JsonSerializer.Serialize(new[] { new { Id = ImageId } }));
            if (args[0] == "create")
            {
                var runLabel = args.Single(value => value.StartsWith("appsurface.run=", StringComparison.Ordinal))["appsurface.run=".Length..];
                _ownerId ??= runLabel;
                if (_ownerId != runLabel || !Guid.TryParseExact(runLabel, "N", out _))
                    throw new InvalidOperationException("Synthetic run label mismatch.");
                var name = ValueAfter(args, "--name");
                var role = name[("appsurface-owned-" + _ownerId + "-").Length..];
                if (role.StartsWith("observe-", StringComparison.Ordinal)) role = "observation";
                var id = (_nextId++).ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
                var image = args[Array.IndexOf(args, "--entrypoint") + 2];
                var state = new ContainerState(id, name, role, runLabel, image, ValueAfter(args, "--user"),
                    role is "anchor" or "observation" or "initialize" ? null : _anchorId, ReadMounts(args), ReadPublishedPorts(args), args);
                lock (ContainersLock) Containers.Add(id, state);
                if (role == "anchor") _anchorId = id;
                return Task.FromResult(id);
            }
            if (args[0] == "start")
            {
                lock (ContainersLock)
                {
                    var container = Containers[args[1]];
                    var staysCreated = (_scenario.Failure, container.Role) is
                        (WorkloadFailure.WorkerStillCreated, "worker") or (WorkloadFailure.SeedStillCreated, "seed-workload");
                    if (container.Role is "anchor" or "keycloak" or "web-workload" || _keepSeedRunning && container.Role == "seed-workload")
                    {
                        container.Running = true;
                        container.Status = "running";
                    }
                    else if (!staysCreated)
                    {
                        container.ExitCode = (_failSeed && container.Role == "seed-workload")
                            || (_workerFails && container.Role == "worker") ? 17 : 0;
                        container.Status = "exited";
                    }
                }
                if (Containers[args[1]].Role == "anchor" && !_anchorStartUncertain)
                {
                    var anchor = Containers[args[1]];
                    var flag = Array.IndexOf(anchor.CreateArguments, _scenario.Confidential
                        ? "--appsurface-keycloak-fenced-memory-anchor" : "--appsurface-keycloak-owner-fence");
                    var portArgument = flag + (_scenario.Confidential ? 3 : 2);
                    var port = int.Parse(anchor.CreateArguments[portArgument], System.Globalization.CultureInfo.InvariantCulture);
                    var owner = anchor.CreateArguments[portArgument + 1];
                    var stateRoot = anchor.Mounts.Single(mount => mount.Destination == "/proof/state").Source;
                    var marker = TestPathUtils.PathUnder(stateRoot, _scenario.Confidential ? "client-reference.json.listening" : "owner-fence.listening");
                    if (_scenario.Confidential && !_referenceMissing)
                        File.WriteAllText(TestPathUtils.PathUnder(stateRoot, "client-reference.json"), "{}");
                    var disconnect = _scenario.Failure == WorkloadFailure.OwnerLossDuringWorkflow ? _disconnectOwner.Task : null;
                    _heartbeatFixture = ServeOwnerAsync(port, owner, marker, _lifetime.Token, disconnect);
                }
                if (_tlsListener is not null && Containers[args[1]].Role == "keycloak")
                {
                    var server = Containers[args[1]];
                    var publicRoot = server.Mounts.Single(mount => mount.Destination == "/proof/public").Source;
                    var privateRoot = server.Mounts.Single(mount => mount.Destination == "/proof/server").Source;
                    _tlsListener.Start();
                    _tlsFixture = ServeKeycloakTransportAsync(_tlsListener, publicRoot, privateRoot, _lifetime.Token);
                }
                return Task.FromResult(args[1]);
            }
            if (args[0] == "ps")
            {
                var filter = ValueAfter(args, "--filter");
                lock (ContainersLock)
                {
                    if (filter == "volume=" + VolumeName)
                        return Task.FromResult(string.Join('\n', Containers.Values
                            .Where(container => !container.Removed && container.Mounts.Any(mount => mount.Type == "volume" && mount.Source == VolumeName))
                            .Select(container => container.Id)));
                    var match = filter.StartsWith("name=^/", StringComparison.Ordinal)
                        ? Containers.Values.SingleOrDefault(container => !container.Removed && container.Name == filter[7..^1])
                        : filter.StartsWith("id=", StringComparison.Ordinal)
                            ? Containers.GetValueOrDefault(filter[3..]) is { Removed: false } byId ? byId : null
                            : throw new InvalidOperationException("Unexpected synthetic ps filter.");
                    return Task.FromResult(match?.Id ?? string.Empty);
                }
            }
            if (args[0] == "inspect")
            {
                lock (ContainersLock)
                {
                    var container = Containers[args[1]];
                    var snapshot = InspectJson(container, _publicThemeAndSeeds);
                    if (_fenceRequested && _scenario.DelayedWebStop && container.Role == "web-workload" && container.Running)
                    {
                        ObservedWebStillStopping = true;
                        container.Running = false;
                        container.Status = "exited";
                    }
                    var target = _scenario.InventoryDrift is "nonloopback-port" or "missing-anchor-port" or "extra-anchor-port"
                        or "multiple-anchor-bindings" or "invalid-anchor-port-mapping" or "alternate-host-port"
                        or "anchor-network" or "anchor-pid-namespace" ? "anchor" : "web-workload";
                    return Task.FromResult(_fenceRequested && _scenario.InventoryDrift is not null && container.Role == target
                        ? WithInventoryDrift(snapshot, _scenario.InventoryDrift)
                        : snapshot);
                }
            }
            if (args[0] == "wait")
            {
                lock (ContainersLock)
                {
                    var container = Containers[args[1]];
                    if (container.Role is not ("observation" or "initialize") || container.Status != "exited")
                        throw new InvalidOperationException("Only a stopped volume helper can be observed here.");
                    return Task.FromResult("0");
                }
            }
            if (args[0] == "logs")
            {
                lock (ContainersLock)
                {
                    var observer = Containers[args[1]];
                    if (observer.Role != "observation" || observer.Status != "exited")
                        throw new InvalidOperationException("The volume stamp must come from the stopped observer.");
                    if (!observer.CreateArguments.Contains("--appsurface-keycloak-volume-ownership", StringComparer.Ordinal))
                        return Task.FromResult(JsonSerializer.Serialize(Stamp));
                    var databaseUid = _scenario.Storage == "mixed"
                        ? uint.Parse(SupervisedKeycloakContainerProfile.UserId(), System.Globalization.CultureInfo.InvariantCulture)
                        : 1000u;
                    var database = _scenario.Storage == "uid1000-empty" ? null : new KeycloakWorkerDatabaseOwnership(databaseUid, 0);
                    return Task.FromResult(JsonSerializer.Serialize(new KeycloakWorkerVolumeOwnership(Stamp, 1000, 0, database)));
                }
            }
            if (args[0] == "stop")
            {
                if (_anchorId != args[^1]) throw new InvalidOperationException("Only the exact anchor may be fenced.");
                lock (ContainersLock)
                {
                    _fenceRequested = true;
                    SeedWasRunningAtFence = Containers.Values.Any(container => container.Role == "seed-workload" && container.Running);
                    foreach (var container in Containers.Values.Where(container =>
                                 !(_scenario.DelayedWebStop && !ObservedWebStillStopping && container.Role == "web-workload")))
                    {
                        container.Running = false;
                        if (container.Status == "running") container.Status = "exited";
                    }
                }
                return Task.FromResult(args[^1]);
            }
            if (args[0] == "rm")
            {
                lock (ContainersLock)
                {
                    var container = Containers[args[1]];
                    if (container.Running) throw new InvalidOperationException("A running synthetic container cannot be removed.");
                    container.Removed = true;
                    return Task.FromResult(args[1]);
                }
            }
            throw new InvalidOperationException("Unexpected synthetic Docker command.");
        }
    }

    private static async Task ServeOwnerAsync(int port, string ownerId, string listeningMarker, CancellationToken cancellationToken,
        Task? disconnect = null)
    {
        await using var heartbeat = await KeycloakOwnerHeartbeat.AcceptAsync(port, ownerId, cancellationToken,
            () => File.WriteAllText(listeningMarker, "ready"));
        await heartbeat.AcknowledgeAsync(cancellationToken);
        if (disconnect is null) await heartbeat.WaitForLossAsync(cancellationToken);
        else await disconnect.WaitAsync(cancellationToken);
    }

    private static async Task ServeKeycloakTransportAsync(
        TcpListener listener, string publicRoot, string privateRoot, CancellationToken token)
    {
        using var certificate = X509Certificate2.CreateFromPemFile(
            TestPathUtils.PathUnder(publicRoot, "server.crt"), TestPathUtils.PathUnder(privateRoot, "server.key"));
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = new SslStream(client.GetStream());
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
        using var reader = new StreamReader(stream, leaveOpen: true);
        Assert.Equal("GET /realms/master/.well-known/openid-configuration HTTP/1.1", await reader.ReadLineAsync(token));
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
    }

    private static string DockerEndpoint()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return "unix://" + Path.GetFullPath(TestPathUtils.PathUnder(home, ".docker", "run", "docker.sock")).Replace('\\', '/');
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string ValueAfter(string[] arguments, string flag) => arguments[Array.IndexOf(arguments, flag) + 1];

    private static MountState[] ReadMounts(string[] args)
    {
        var mounts = new List<MountState>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--mount") continue;
            var spec = args[++index];
            var values = spec.Split(',').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            mounts.Add(new(values["type"], values["source"], values["target"], !spec.EndsWith(",readonly", StringComparison.Ordinal)));
        }
        return mounts.ToArray();
    }

    private static object ReadPublishedPorts(string[] args)
    {
        var ports = new Dictionary<string, object[]>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--publish") continue;
            var fields = args[++index].Split(':');
            ports.Add(fields[2] + "/tcp", [new { HostIp = fields[0], HostPort = fields[1] }]);
        }
        return ports;
    }

    private static string InspectJson(ContainerState container, bool translateTheme)
    {
        var ports = JsonSerializer.SerializeToElement(container.Ports);
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = container.Id,
                Name = "/" + container.Name,
                Image = ImageId,
                Config = new { Image = container.Image, Labels = new Dictionary<string, string>
                    { ["appsurface.run"] = container.RunLabel, ["appsurface.supervised"] = "1" }, User = container.User },
                HostConfig = new
                {
                    PidMode = container.AnchorId is null ? "" : "container:" + container.AnchorId,
                    NetworkMode = container.Role switch
                    {
                        "anchor" => "bridge", "observation" or "initialize" => "none", _ => "container:" + container.AnchorId
                    },
                    Init = container.Role is "anchor" or "observation" or "initialize" ? (bool?)true : null,
                    RestartPolicy = new { Name = "no" }, Privileged = false,
                    ReadonlyRootfs = container.Role != "keycloak",
                    CapAdd = container.Role == "initialize" ? new[] { "CAP_CHOWN", "CAP_FOWNER" } : Array.Empty<string>(),
                    CapDrop = new[] { "ALL" },
                    SecurityOpt = new[] { "no-new-privileges" }, PidsLimit = 256,
                    Memory = container.Role == "keycloak" ? 1024L * 1024 * 1024 : 512L * 1024 * 1024,
                    Devices = Array.Empty<object>(), PortBindings = ports
                },
                State = new { Running = container.Running, Paused = false, Restarting = false, Pid = container.Running ? 123 : 0,
                    Status = container.Running ? "running" : container.Status, ExitCode = container.ExitCode },
                Mounts = container.Mounts.Select(mount => new { Type = mount.Type,
                    Source = translateTheme && mount.Destination.StartsWith("/opt/keycloak/themes/", StringComparison.Ordinal)
                        ? "/host_mnt" + mount.Source : mount.Source,
                    Name = mount.Type == "volume" ? mount.Source : null, Destination = mount.Destination, RW = mount.Writable })
            }
        });
    }

    private static string WithInventoryDrift(string snapshot, string drift)
    {
        var root = JsonNode.Parse(snapshot)!.AsArray();
        var container = root[0]!.AsObject();
        var config = container["Config"]!.AsObject();
        var host = container["HostConfig"]!.AsObject();
        var mount = container["Mounts"]![0]!.AsObject();
        switch (drift)
        {
            case "image": config["Image"] = "unexpected-image"; break;
            case "image-id": container["Image"] = "sha256:" + new string('c', 64); break;
            case "user": config["User"] = "0:0"; break;
            case "privileged": host["Privileged"] = true; break;
            case "restart": host["RestartPolicy"]!["Name"] = "always"; break;
            case "init": host["Init"] = true; break;
            case "writable-root": host["ReadonlyRootfs"] = false; break;
            case "pid-namespace": host["PidMode"] = "host"; break;
            case "network-namespace": host["NetworkMode"] = "host"; break;
            case "capabilities": host["CapAdd"] = new JsonArray(JsonValue.Create("CAP_CHOWN")); break;
            case "retained-capabilities": host["CapDrop"] = new JsonArray(); break;
            case "security-options": host["SecurityOpt"] = new JsonArray(); break;
            case "process-limit": host["PidsLimit"] = 0; break;
            case "memory-limit": host["Memory"] = 0; break;
            case "devices": host["Devices"] = new JsonArray(new JsonObject()); break;
            case "undeclared-mount": mount["Destination"] = "/unexpected"; break;
            case "missing-mount": container["Mounts"]!.AsArray().RemoveAt(0); break;
            case "mount-type": mount["Type"] = "volume"; break;
            case "writable-mount": mount["RW"] = true; break;
            case "changed-mount-source": mount["Source"] = "/unexpected"; break;
            case "unexpected-port": host["PortBindings"]!["1234/tcp"] = new JsonArray(); break;
            case "nonloopback-port":
                host["PortBindings"]!.AsObject().First().Value![0]!["HostIp"] = "0.0.0.0";
                break;
            case "missing-anchor-port": host["PortBindings"]!.AsObject().Clear(); break;
            case "extra-anchor-port": host["PortBindings"]!["1234/tcp"] = new JsonArray(); break;
            case "multiple-anchor-bindings":
                var binding = host["PortBindings"]!.AsObject().First().Value!.AsArray();
                binding.Add(binding[0]!.DeepClone());
                break;
            case "invalid-anchor-port-mapping": host["PortBindings"] = 17; break;
            case "alternate-host-port": host["PortBindings"]!.AsObject().First().Value![0]!["HostPort"] = "0"; break;
            case "anchor-network": host["NetworkMode"] = "host"; break;
            case "anchor-pid-namespace": host["PidMode"] = "host"; break;
            default: throw new InvalidOperationException("Unknown inventory drift.");
        }
        return root.ToJsonString();
    }

    private sealed class ContainerState(string id, string name, string role, string runLabel, string image, string user,
        string? anchorId, MountState[] mounts, object ports, string[] createArguments)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Role { get; } = role;
        public string RunLabel { get; } = runLabel;
        public string Image { get; } = image;
        public string User { get; } = user;
        public MountState[] Mounts { get; } = mounts;
        public object Ports { get; } = ports;
        public string[] CreateArguments { get; } = createArguments;
        public string? AnchorId { get; } = anchorId;
        public bool Running { get; set; }
        public bool Removed { get; set; }
        public string Status { get; set; } = "created";
        public int ExitCode { get; set; }
    }

    private sealed record MountState(string Type, string Source, string Destination, bool Writable);

    private sealed class TransportReadinessHandler(bool transientFailure, bool unavailable) : HttpMessageHandler
    {
        private int _calls;

        internal int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedAttempt = Interlocked.Increment(ref _calls);
            if (transientFailure && observedAttempt == 1)
                throw new HttpRequestException("Synthetic listener startup.");
            var status = unavailable || transientFailure && observedAttempt == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class RuntimeLifetime(bool failStop) : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => failStop
            ? Task.FromException(new IOException("Fixture host stop failed.")) : Task.CompletedTask;
    }

    private sealed class PublishAfterResourcesCreatedHostedService(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => services.GetRequiredService<IDistributedApplicationEventing>()
            .PublishAsync(new AfterResourcesCreatedEvent(services, services.GetRequiredService<DistributedApplicationModel>()), cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
