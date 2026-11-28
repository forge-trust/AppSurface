using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakSupervisedStagingTests
{
    [Theory]
    [InlineData("seed-missing", "ASKEYC028", 0, 0)]
    [InlineData("web-missing", "ASKEYC028", 0, 0)]
    [InlineData("foreign-executable", "ASKEYC028", 0, 0)]
    [InlineData("duplicate-volume", "ASKEYC028", 1, 0)]
    [InlineData("legacy-name-missing", "ASKEYC028", 1, 0)]
    [InlineData("legacy-volume-missing", "ASKEYC028", 1, 1)]
    [InlineData("legacy-volume-vanished", "ASKEYC028", 1, 2)]
    [InlineData("existing-owner", "ASKEYC029", 1, 0)]
    [InlineData("already-attached", "ASKEYC034", 1, 2)]
    [InlineData("create-vanished", "ASKEYC034", 1, 3)]
    public async Task StartupRefusesInvalidGraphOrUnownedVolumeBeforeResolvingInputs(
        string scenario, string expectedCode, int expectedDaemonReads, int expectedVolumeLists)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

        const string daemonId = "fixture-daemon";
        const string volumeName = "fixture-existing-data";
        const string dataTarget = "/opt/keycloak/data";
        var leaseOwner = "{\"owner\":\"prior-verified-fixture\"}"u8.ToArray();
        var reservationRetained = scenario is "already-attached" or "create-vanished";
        var root = TestPathUtils.PathUnder("/private/tmp", "issue888-prestart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var calls = new List<string[]>();
            var volumeLists = 0;
            var docker = new SupervisedDocker("unix:///var/run/docker.sock", (_, args) =>
            {
                calls.Add(args);
                return Task.FromResult(args switch
                {
                    ["info", ..] => daemonId,
                    ["volume", "ls", ..] => (++volumeLists == 1 && scenario == "legacy-volume-vanished") || scenario == "already-attached"
                        ? volumeName : "",
                    ["volume", "inspect", volumeName] => JsonSerializer.Serialize(new[]
                    {
                        new { Name = volumeName, Driver = "local", Scope = "local", CreatedAt = "created",
                            Mountpoint = "/fixture/volumes/fixture-existing-data/_data", Options = (object?)null, Labels = (object?)null }
                    }),
                    ["volume", "create", volumeName] when scenario == "create-vanished" => volumeName,
                    ["ps", ..] when scenario == "already-attached" => new string('a', 64),
                    _ => throw new InvalidOperationException("Unexpected Docker operation during startup refusal.")
                });
            });
            var builder = CreateBuilder(root);
            var resolved = 0;
            var username = builder.AddParameter("test-admin", () => { resolved++; return "fixture-admin"; });
            var password = builder.AddParameter("test-password", () => { resolved++; return "fixture-only-password"; }, secret: true);
            var legacy = scenario.StartsWith("legacy-", StringComparison.Ordinal);
            var leaseRoot = TestPathUtils.PathUnder(root, "leases");
            var options = new AppSurfaceKeycloakOptions
            {
                KeycloakPort = ReservePort(),
                WebProofPort = ReservePort(),
                RequireIdentitySeed = true,
                UsePersistentDataVolume = legacy,
                PersistentVolumeName = scenario == "existing-owner" || reservationRetained ? volumeName : null,
                SupervisedExecution = new() { LeaseRoot = leaseRoot, RunRoot = TestPathUtils.PathUnder(root, "runs") }
            };
            options.SeededUsers.Clear();
            if (scenario == "existing-owner")
            {
                using var prior = await SupervisedVolumeLease.Acquire(leaseRoot, daemonId, volumeName);
                prior.WriteNewActive(leaseOwner);
            }

            var bindings = new AppSurfaceKeycloakCredentialBindings().CreateSnapshot(builder, options.CreateDesiredConfiguration(), username, password);
            var resource = AppSurfaceKeycloakSupervisedRun.Register(builder, "identity", options, bindings, docker);
            if (legacy)
            {
                // Reproduce the mount Aspire has resolved before its BeforeStart event.
                var mount = Assert.Single(resource.Resource.Resource.Annotations.OfType<ContainerMountAnnotation>(), m => m.Target == dataTarget);
                resource.Resource.Resource.Annotations.Remove(mount);
                if (scenario != "legacy-name-missing") resource.Resource.WithVolume(volumeName, dataTarget);
            }
            if (scenario == "duplicate-volume")
            {
                resource.Resource.WithVolume(volumeName, dataTarget);
                resource.Resource.WithVolume("another-fixture-volume", dataTarget);
            }
            if (scenario == "foreign-executable") builder.AddExecutable("foreign", "/usr/bin/false", root);
            var run = Assert.IsType<AppSurfaceKeycloakSupervisedRun>(resource.SupervisedRun);
            if (scenario != "seed-missing") run.WithSeed(new("required-seed", root, "Fixture.dll", []));
            if (scenario is not ("seed-missing" or "web-missing")) run.WithWeb(new("fixture-web", root, "Fixture.dll", []));
            var result = await run.RunAsync(builder.Build(), CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(expectedCode, result.Code);
            Assert.Equal(expectedCode, run.DiagnosticCode);
            Assert.Equal(reservationRetained, result.ActiveRetained);
            Assert.InRange(result.StopSeconds, 0, 30);
            Assert.Equal(0, resolved);
            Assert.Equal(reservationRetained, Directory.Exists(run.RunDirectory));
            Assert.Equal(expectedDaemonReads, calls.Count(call => call[0] == "info"));
            Assert.Equal(expectedVolumeLists, volumeLists);
            Assert.All(calls, call => Assert.True(call is ["info", ..] or ["volume", "ls" or "inspect", ..]
                || scenario == "already-attached" && call is ["ps", ..]
                || scenario == "create-vanished" && call is ["volume", "create", volumeName]));
            Assert.Equal(scenario == "already-attached" ? 1 : 0, calls.Count(call => call[0] == "ps"));
            Assert.Equal(scenario == "create-vanished" ? 1 : 0, calls.Count(call => call is ["volume", "create", ..]));
            if (scenario == "existing-owner")
            {
                using var prior = await SupervisedVolumeLease.Acquire(leaseRoot, daemonId, volumeName);
                Assert.Equal(leaseOwner, prior.ReadActive());
            }
            else if (reservationRetained)
            {
                using var retained = await SupervisedVolumeLease.Acquire(leaseRoot, daemonId, volumeName);
                using var marker = JsonDocument.Parse(retained.ReadActive());
                Assert.Equal(run.OwnerId, marker.RootElement.GetProperty("OwnerId").GetString());
                Assert.False(marker.RootElement.GetProperty("StagingComplete").GetBoolean());
                Assert.Empty(marker.RootElement.GetProperty("Containers").EnumerateArray());
            }
            else
            {
                Assert.Empty(Directory.EnumerateFiles(root, "active.json", SearchOption.AllDirectories));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PayloadSourceDirectoryLinkIsRejectedBeforeCopy()
    {
        if (OperatingSystem.IsWindows()) return;

        using var folder = new TempDirectory();
        var target = TestPathUtils.PathUnder(folder.Path, "target");
        var source = TestPathUtils.PathUnder(folder.Path, "source");
        var destination = TestPathUtils.PathUnder(folder.Path, "destination");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(destination);
        File.WriteAllText(TestPathUtils.PathUnder(target, "private"), "must-not-copy");
        Directory.CreateSymbolicLink(source, target);

        Assert.Throws<IOException>(() => SupervisedKeycloakRunStager.CopyPayload(source, destination));

        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.Equal("must-not-copy", File.ReadAllText(TestPathUtils.PathUnder(target, "private")));
    }

    [Theory]
    [InlineData("reserved", 0, true)]
    [InlineData("staged", 3, false)]
    [InlineData("staged-cancellation", 3, false)]
    [InlineData("assembly-missing", 3, false)]
    [InlineData("assembly-drift", 3, false)]
    [InlineData("public-input-collision", 3, false)]
    [InlineData("seed-binding-invalid", 3, false)]
    [InlineData("run-directory-collision", 0, false)]
    [InlineData("run-directory-replaced", 3, true)]
    public async Task StartFailureVerifiesOwnershipBeforeResolvingBindingsAndCleansOnlyAfterStop(string phase, int expectedResolutions, bool retained)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

        using var folder = new TempDirectory();
        const string privateRoot = "/private/tmp";
        var root = TestPathUtils.PathUnder(privateRoot, "issue888-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var registrations = 0;
            var builder = CreateBuilder(root);
            var username = builder.AddParameter("test-admin", () => { registrations++; return "fixture-admin"; });
            var password = builder.AddParameter("test-password", () => { registrations++; return "fixture-password"; }, secret: true);
            var seedSecret = builder.AddParameter("test-seed", () =>
            {
                registrations++;
                return phase == "seed-binding-invalid" ? "fixture\nseed" : "fixture-seed-token";
            }, secret: true);
            var options = new AppSurfaceKeycloakOptions
            {
                KeycloakPort = ReservePort(),
                WebProofPort = ReservePort(),
                RequireIdentitySeed = true,
                ClientMode = KeycloakClientMode.Public,
                SupervisedExecution = new()
                {
                    LeaseRoot = TestPathUtils.PathUnder(root, "leases"),
                    RunRoot = TestPathUtils.PathUnder(root, "runs")
                }
            };
            options.SeededUsers.Clear();

            var commands = new List<string>();
            var docker = new SupervisedDocker("unix:///var/run/docker.sock", (_, args) =>
            {
                commands.Add(args[0]);
                if (args[0] == "info") return Task.FromResult("fixture-daemon");
                throw new InvalidOperationException("A staging failure must not reach a Docker mutation");
            });
            var bindings = new AppSurfaceKeycloakCredentialBindings().CreateSnapshot(builder, options.CreateDesiredConfiguration(), username, password);
            var wrapper = AppSurfaceKeycloakSupervisedRun.Register(builder, "identity", options, bindings, docker);
            var run = Assert.IsType<AppSurfaceKeycloakSupervisedRun>(wrapper.SupervisedRun);
            if (phase == "public-input-collision")
                run.WithPublicInputs(new Dictionary<string, string> { ["WebBaseAddress"] = "fixture-public-value" });
            File.WriteAllText(TestPathUtils.PathUnder(folder.Path, "Fixture.dll"), "staging-only payload");
            if (phase == "assembly-drift")
                File.WriteAllText(TestPathUtils.PathUnder(folder.Path, "ForgeTrust.AppSurface.Auth.Keycloak.dll"), "drifted assembly");
            var seedAssembly = phase == "assembly-missing" ? "Missing.dll" : "Fixture.dll";
            run.WithSeed(new("required-seed", folder.Path, seedAssembly, [])
            {
                PrivateInputs = ImmutableDictionary<string, IResourceBuilder<ParameterResource>>.Empty.Add("SeedToken", seedSecret)
            });
            run.WithWeb(new("fixture-web", folder.Path, "Fixture.dll", []));
            var observations = new List<string>();
            var cancelDuringPreparation = phase == "staged-cancellation";
            var unownedRunDirectory = phase is "run-directory-collision" or "run-directory-replaced";
            var stagedOriginal = TestPathUtils.PathUnder(root, "staged-original");
            var unownedFile = TestPathUtils.PathUnder(run.RunDirectory, "private-workloads", "unowned.txt");
            void CreateUnownedRunDirectory()
            {
                if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
                const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(options.SupervisedExecution.RunRoot, privateMode);
                Directory.CreateDirectory(run.RunDirectory, privateMode);
                Directory.CreateDirectory(Path.GetDirectoryName(unownedFile)!, privateMode);
                File.WriteAllText(unownedFile, "must survive failed ownership verification");
            }
            using var callerCancellation = new CancellationTokenSource();
            run.LifecycleObservation = (stage, token) =>
            {
                observations.Add(stage);
                Assert.Single(Directory.EnumerateFiles(options.SupervisedExecution.LeaseRoot, "active.json", SearchOption.AllDirectories));
                if (stage == "reserved")
                {
                    Assert.Equal(0, registrations);
                    Assert.False(Directory.Exists(run.RunDirectory));
                    if (phase == "run-directory-collision") CreateUnownedRunDirectory();
                }
                if (stage == "staged")
                {
                    Assert.Equal(3, registrations);
                    var publicConfig = File.ReadAllText(TestPathUtils.PathUnder(run.RunDirectory, "public", "config.json"));
                    Assert.DoesNotContain("fixture-seed-token", publicConfig, StringComparison.Ordinal);
                    Assert.DoesNotContain("fixture-password", publicConfig, StringComparison.Ordinal);
                    Assert.True(File.Exists(TestPathUtils.PathUnder(run.RunDirectory, "private-workloads", "required-seed", "input.json")));
                    if (cancelDuringPreparation)
                    {
                        callerCancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                }
                if (stage == phase || stage == "staged" && phase == "run-directory-replaced")
                    throw new InvalidOperationException("synthetic staging failure");
                return Task.CompletedTask;
            };
            var beforeClean = false;
            run.BeforeCleanObservation = _ =>
            {
                Assert.Single(Directory.EnumerateFiles(options.SupervisedExecution.LeaseRoot, "active.json", SearchOption.AllDirectories));
                if (phase == "run-directory-replaced")
                {
                    Directory.Move(run.RunDirectory, stagedOriginal);
                    CreateUnownedRunDirectory();
                }
                beforeClean = true;
                return Task.CompletedTask;
            };
            var app = builder.Build();

            var result = await run.RunAsync(app, callerCancellation.Token);

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(retained ? "ASKEYC034" : cancelDuringPreparation ? "ASKEYC032" : "ASKEYC028", result.Code);
            Assert.Equal(result.Code, run.DiagnosticCode);
            Assert.Equal(retained, result.ActiveRetained);
            Assert.InRange(result.StopSeconds, 0, 30);
            Assert.Equal(expectedResolutions, registrations);
            string[] expectedObservations = phase is "staged" or "staged-cancellation" or "run-directory-replaced"
                ? ["reserved", "staged"] : ["reserved"];
            Assert.Equal(expectedObservations, observations);
            Assert.True(beforeClean);
            Assert.Equal(["info"], commands);
            var markers = Directory.EnumerateFiles(options.SupervisedExecution.LeaseRoot, "active.json", SearchOption.AllDirectories);
            if (retained) Assert.Single(markers);
            else Assert.Empty(markers);

            if (unownedRunDirectory)
                Assert.Equal("must survive failed ownership verification", File.ReadAllText(unownedFile));
            else if (retained)
                Assert.False(Directory.Exists(run.RunDirectory));
            else
                Assert.Empty(Directory.GetDirectories(run.RunDirectory, "private-*"));

            if (phase == "run-directory-replaced")
                Assert.True(File.Exists(TestPathUtils.PathUnder(stagedOriginal, "private-workloads", "required-seed", "input.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IDistributedApplicationBuilder CreateBuilder(string root)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--environment", "Testing"],
            DisableDashboard = true
        });
        builder.Configuration["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        builder.Configuration["DcpPublisher:CliPath"] = TestPathUtils.PathUnder(root, "unused-dcp");
        builder.Configuration["DcpPublisher:DashboardPath"] = TestPathUtils.PathUnder(root, "unused-dashboard");
        builder.Services.AddSingleton<IHostLifetime, StagingLifetime>();
        return builder;
    }

    private sealed class StagingLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
