using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using ForgeTrust.AppSurface.Auth.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

/// <summary>Exercises the supervised owner through the real AppHost lifecycle with a deterministic Docker transport.</summary>
public sealed class AppSurfaceKeycloakSupervisedRuntimeTests
{
    private const string ContainerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task RunAsyncFencesUncertainAnchorLaunchAndCleansOnlyAfterVerifiedStop(bool uncertain, bool refuseMarkerSave)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

        using var directory = new TempDirectory();
        var physicalRoot = Directory.ResolveLinkTarget("/var", returnFinalTarget: true) is { } varDirectory
            && directory.Path.StartsWith("/var/", StringComparison.Ordinal)
            ? varDirectory.FullName + directory.Path["/var".Length..]
            : directory.Path;
        var payload = TestPathUtils.PathUnder(physicalRoot, "payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(TestPathUtils.PathUnder(payload, "App.dll"), "synthetic workload payload");

        var commands = new ConcurrentQueue<string[]>();
        string? ownerId = null;
        string? runLabel = null;
        string? nativeName = null;
        string? nativeImage = null;
        string? nativeUser = null;
        string? nativeMounts = null;
        string? nativePorts = null;
        var nativeStatus = "created";
        var docker = new SupervisedDocker(DockerEndpoint(), (ct, args) =>
        {
            ct.ThrowIfCancellationRequested();
            commands.Enqueue(args);
            if (args[0] == "info") return Task.FromResult("synthetic-daemon");
            if (args[0] == "image" && args[1] == "inspect") return Task.FromResult(JsonSerializer.Serialize(new[] { new { Id = ImageId } }));
            if (args[0] == "create")
            {
                ownerId = args.Single(a => a.StartsWith("appsurface.run=", StringComparison.Ordinal))["appsurface.run=".Length..];
                runLabel = ownerId;
                nativeName = ValueAfter(args, "--name");
                nativeImage = args[Array.IndexOf(args, "--entrypoint") + 2];
                nativeUser = ValueAfter(args, "--user");
                nativeMounts = JsonSerializer.Serialize(ReadMounts(args));
                nativePorts = JsonSerializer.Serialize(ReadPublishedPorts(args));
                return Task.FromResult(ContainerId);
            }
            if (args[0] == "ps") return Task.FromResult(nativeName is null ? string.Empty : ContainerId);
            if (args[0] == "inspect" && args[1] == ContainerId)
                return Task.FromResult(InspectJson(runLabel!, nativeName!, nativeImage!, nativeUser!, nativeMounts!, nativePorts!, nativeStatus));
            if (args[0] == "start" && args[1] == ContainerId)
            {
                if (uncertain) throw new IOException("Synthetic start acknowledgement was lost.");
                if (refuseMarkerSave) nativeStatus = "running";
                return Task.FromResult(ContainerId);
            }
            if (args[0] == "stop" && args[^1] == ContainerId && nativeStatus == "running")
            {
                nativeStatus = "exited";
                return Task.FromResult(ContainerId);
            }
            if (args[0] == "rm" && args[1] == ContainerId)
            {
                nativeName = null;
                return Task.FromResult(ContainerId);
            }
            throw new InvalidOperationException("Unexpected synthetic Docker command.");
        });

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
        builder.Services.AddSingleton<IHostLifetime, RuntimeLifetime>();

        var username = builder.AddParameter("test-user", () => "test-user");
        var password = builder.AddParameter("test-password", () => "test-password", secret: true);
        var execution = new AppSurfaceKeycloakSupervisedOptions
        {
            LeaseRoot = TestPathUtils.PathUnder(physicalRoot, "leases"),
            RunRoot = TestPathUtils.PathUnder(physicalRoot, "runs"),
            DockerEndpoint = DockerEndpoint()
        };
        var options = new AppSurfaceKeycloakOptions
        {
            KeycloakPort = FreePort(),
            WebProofPort = FreePort(),
            ClientMode = KeycloakClientMode.Public,
            RequireIdentitySeed = true,
            SupervisedExecution = execution
        };
        options.SeededUsers.Clear();
        var snapshot = options.CreateValidatedSnapshot();
        var bindings = new AppSurfaceKeycloakCredentialBindings().CreateSnapshot(
            builder, snapshot.CreateDesiredConfiguration(), username, password);
        var resource = AppSurfaceKeycloakSupervisedRun.Register(builder, "identity", snapshot, bindings, docker);
        var run = Assert.IsType<AppSurfaceKeycloakSupervisedRun>(resource.SupervisedRun);
        run.WithSeed(new("seed-workload", payload, "App.dll", []));
        run.WithWeb(new("web-workload", payload, "App.dll", []));

        var leaseKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-daemon\ndisposable-" + run.OwnerId))).ToLowerInvariant();
        var leaseDirectory = TestPathUtils.PathUnder(execution.LeaseRoot, leaseKey);
        var markerPath = TestPathUtils.PathUnder(leaseDirectory, "active.json");
        UnixFileMode? originalLeaseMode = null;
        run.LifecycleObservation = (phase, _) =>
        {
            if (refuseMarkerSave && phase == "anchor-start-ack")
            {
                if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
                originalLeaseMode = File.GetUnixFileMode(leaseDirectory);
                // Refuse the post-acknowledgement save while leaving the previously written record intact.
                File.SetUnixFileMode(leaseDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            return phase == "anchor-started"
                ? Task.FromException(new IOException("Synthetic loss after acknowledged start."))
                : Task.CompletedTask;
        };
        var beforeClean = false;
        run.BeforeCleanObservation = _ =>
        {
            beforeClean = true;
            Assert.DoesNotContain(commands, command => command[0] == "rm");
            return Task.CompletedTask;
        };
        var app = builder.Build();
        AppSurfaceKeycloakSupervisedResult result;
        try { result = await run.RunAsync(app); }
        finally
        {
            if (originalLeaseMode is { } mode) File.SetUnixFileMode(leaseDirectory, mode);
        }

        var retained = uncertain || refuseMarkerSave;
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(retained ? "ASKEYC034" : "ASKEYC031", result.Code);
        Assert.Equal(retained, result.ActiveRetained);
        Assert.Equal(!retained, beforeClean);
        Assert.InRange(result.StopSeconds, 0, 30);
        Assert.True(Guid.TryParseExact(ownerId, "N", out _));
        Assert.Equal(ownerId, run.OwnerId);
        Assert.Equal(retained ? "appsurface-owned-" + ownerId + "-anchor" : null, nativeName);
        Assert.Equal(execution.RuntimeImage, nativeImage);
        Assert.Equal(runLabel, ownerId);

        var observed = commands.ToArray();
        Assert.Contains(observed, command => command[0] == "info");
        Assert.Contains(observed, command => command[0] == "create" && command.Contains("appsurface.run=" + ownerId, StringComparer.Ordinal)
            && command.Contains("appsurface.supervised=1", StringComparer.Ordinal));
        Assert.Contains(observed, command => command.SequenceEqual(["inspect", ContainerId]));
        Assert.Contains(observed, command => command.SequenceEqual(["start", ContainerId]));
        if (refuseMarkerSave) Assert.Contains(observed, command => command.SequenceEqual(["stop", "--time", "1", ContainerId]));
        else Assert.DoesNotContain(observed, command => command[0] == "stop");
        if (retained) Assert.DoesNotContain(observed, command => command[0] == "rm");
        else Assert.Equal(["rm", ContainerId], observed[^1]);
        Assert.Equal(1, observed.Count(command => command[0] == "create"));
        Assert.Equal(retained, File.Exists(markerPath));
        if (retained)
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            Assert.True(marker.RootElement.GetProperty("LaunchUncertain").GetBoolean());
        }
    }

    private sealed class RuntimeLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string DockerEndpoint()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return "unix://" + Path.GetFullPath(TestPathUtils.PathUnder(home, ".docker", "run", "docker.sock")).Replace('\\', '/');
    }

    private static string ValueAfter(string[] arguments, string flag) => arguments[Array.IndexOf(arguments, flag) + 1];

    private static object[] ReadMounts(string[] args)
    {
        var mounts = new List<object>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--mount") continue;
            var values = args[++index].Split(',').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            mounts.Add(new { Type = values["type"], Source = values["source"], Destination = values["target"], RW = !args[index].EndsWith(",readonly", StringComparison.Ordinal) });
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

    private static string InspectJson(string runLabel, string name, string image, string user, string mountsJson, string portsJson, string status)
    {
        using var mounts = JsonDocument.Parse(mountsJson);
        using var ports = JsonDocument.Parse(portsJson);
        return JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = ContainerId,
                Name = "/" + name,
                Image = ImageId,
                Config = new { Image = image, Labels = new Dictionary<string, string> { ["appsurface.run"] = runLabel, ["appsurface.supervised"] = "1" }, User = user },
                HostConfig = new
                {
                    PidMode = "", NetworkMode = "bridge", Init = true, RestartPolicy = new { Name = "no" }, Privileged = false,
                    ReadonlyRootfs = true, CapAdd = Array.Empty<string>(), CapDrop = new[] { "ALL" }, SecurityOpt = new[] { "no-new-privileges" },
                    PidsLimit = 256, Memory = 512L * 1024 * 1024, Devices = Array.Empty<object>(), PortBindings = ports.RootElement
                },
                State = new { Running = status == "running", Paused = false, Restarting = false,
                    Pid = status == "running" ? 100 : 0, Status = status, ExitCode = 0 },
                Mounts = mounts.RootElement
            }
        });
    }

    private sealed class PublishAfterResourcesCreatedHostedService(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => services.GetRequiredService<IDistributedApplicationEventing>()
            .PublishAsync(new AfterResourcesCreatedEvent(services, services.GetRequiredService<DistributedApplicationModel>()), cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
