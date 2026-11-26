using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakSupervisedRecoveryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("0000000000000000000000000000000z")]
    public async Task RecoveryRejectsMalformedExpectedOwnerBeforeInventory(string expectedOwner)
    {
        using var folder = new TempDirectory();
        var options = new AppSurfaceKeycloakSupervisedOptions
        {
            LeaseRoot = TestPathUtils.PathUnder(folder.Path, "leases"),
            RunRoot = TestPathUtils.PathUnder(folder.Path, "runs")
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(options, "appsurface-volume", expectedOwner));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task RecoveryRejectsUnsupportedInventoryEndpointWithoutStartingDocker()
    {
        using var folder = new TempDirectory();
        var options = new AppSurfaceKeycloakSupervisedOptions
        {
            LeaseRoot = TestPathUtils.PathUnder(folder.Path, "leases"),
            RunRoot = TestPathUtils.PathUnder(folder.Path, "runs"),
            DockerEndpoint = "tcp://127.0.0.1:2375"
        };

        await Assert.ThrowsAsync<IOException>(() => AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(
            options, "appsurface-volume", Guid.NewGuid().ToString("N")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("owner-mismatch")]
    [InlineData("launch-uncertain")]
    [InlineData("owner-alive")]
    [InlineData("owner-invalid-pid")]
    [InlineData("owner-invalid-birth")]
    [InlineData("owner-invalid-role")]
    [InlineData("marker-invalid-json")]
    [InlineData("missing-roster")]
    [InlineData("incomplete-roster")]
    [InlineData("wrong-run-root")]
    [InlineData("volume-missing")]
    public async Task RecoveryRejectsStaleOrUnsafeRecordsWithoutWorkloadsOrClear(string condition)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

        const string volume = "historical-volume";
        const string daemon = "fixture-daemon";
        var owner = Guid.NewGuid().ToString("N");
        var root = TestPathUtils.PathUnder("/private/tmp", "issue888-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var options = new AppSurfaceKeycloakSupervisedOptions
            {
                LeaseRoot = TestPathUtils.PathUnder(root, "leases"),
                RunRoot = root
            };
            var runPath = TestPathUtils.PathUnder(root, owner);
            Directory.CreateDirectory(runPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var process = Process.GetCurrentProcess();
            var started = process.StartTime.ToUniversalTime().Ticks;
            var record = new
            {
                Schema = 3,
                OwnerId = condition == "owner-mismatch" ? Guid.NewGuid().ToString("N") : owner,
                DaemonId = daemon,
                VolumeName = volume,
                Volume = new
                {
                    Name = volume,
                    CreatedAt = "created",
                    Mountpoint = "/vol/data",
                    Root = new { DeviceMajor = 0, DeviceMinor = 17, Inode = 123, BirthSeconds = 456, BirthNanoseconds = 789 }
                },
                RunRoot = condition == "wrong-run-root" ? TestPathUtils.PathUnder(root, "wrong") : runPath,
                OwnerProcess = new
                {
                    Role = condition == "owner-invalid-role" ? "other" : "apphost",
                    Pid = condition == "owner-invalid-pid" ? 0 : process.Id,
                    StartTimeUtcTicks = condition switch
                    {
                        "owner-alive" => started,
                        "owner-invalid-birth" => 0,
                        _ => started - TimeSpan.TicksPerSecond
                    }
                },
                Containers = condition switch
                {
                    "missing-roster" => null,
                    "incomplete-roster" => new object?[] { null },
                    _ => Array.Empty<object?>()
                },
                StagingComplete = true,
                OwnerFenceEstablished = false,
                LaunchUncertain = condition == "launch-uncertain"
            };
            var recordedBytes = condition == "marker-invalid-json" ? "{ invalid"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(record);
            if (condition != "missing")
            {
                using var lease = await SupervisedVolumeLease.Acquire(options.LeaseRoot, daemon, volume);
                lease.WriteNewActive(recordedBytes);
            }

            var commands = new List<string>();
            var docker = new SupervisedDocker("unix:///var/run/docker.sock", (_, args) =>
            {
                commands.Add(args.Length > 1 ? args[0] + " " + args[1] : args[0]);
                return Task.FromResult(args[0] == "info" ? daemon : string.Empty);
            });

            if (condition == "volume-missing")
            {
                var error = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(() =>
                    AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(options, volume, owner, beforeClear: null, docker: docker));
                Assert.Equal("VolumeMissing", error.Kind);
                Assert.Equal(["info --format", "volume ls"], commands);
            }
            else
            {
                var error = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() =>
                    AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(options, volume, owner, beforeClear: null, docker: docker));
                Assert.Equal("ASKEYC033", error.Code);
                Assert.Equal(["info --format"], commands);
            }

            using var retained = await SupervisedVolumeLease.Acquire(options.LeaseRoot, daemon, volume);
            if (condition == "missing") Assert.False(retained.HasActive);
            else Assert.Equal(recordedBytes, retained.ReadActive());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("changed-root")]
    [InlineData("foreign-attachment")]
    [InlineData("observer-nonzero")]
    [InlineData("observer-empty-metadata")]
    [InlineData("changed-volume-during-observation")]
    [InlineData("marker-changed-before-clear")]
    public async Task RecoveryReadsOnlyPhysicalVolumeMetadataBeforeClearingTheExactStoppedOwner(string variant)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

        const string volume = "historical-volume";
        const string daemon = "fixture-daemon";
        const string mountpoint = "/fixture/volumes/historical-volume/data";
        const string imageId = "sha256:fixture-runtime";
        var containerId = new string('a', 64);
        var owner = Guid.NewGuid().ToString("N");
        var root = TestPathUtils.PathUnder("/private/tmp", "issue888-observed-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var options = new AppSurfaceKeycloakSupervisedOptions
            {
                LeaseRoot = TestPathUtils.PathUnder(root, "leases"),
                RunRoot = root
            };
            var runPath = TestPathUtils.PathUnder(root, owner);
            Directory.CreateDirectory(runPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var process = Process.GetCurrentProcess();
            var original = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 3,
                OwnerId = owner,
                DaemonId = daemon,
                VolumeName = volume,
                Volume = new
                {
                    Name = volume,
                    CreatedAt = "created",
                    Mountpoint = mountpoint,
                    Root = new { DeviceMajor = 1, DeviceMinor = 2, Inode = 3, BirthSeconds = 4, BirthNanoseconds = 5 }
                },
                RunRoot = runPath,
                OwnerProcess = new
                {
                    Role = "apphost",
                    Pid = process.Id,
                    StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerSecond
                },
                Containers = Array.Empty<object>(),
                StagingComplete = true,
                OwnerFenceEstablished = false,
                LaunchUncertain = false
            });
            string activeMarkerPath;
            using (var lease = await SupervisedVolumeLease.Acquire(options.LeaseRoot, daemon, volume))
            {
                activeMarkerPath = TestPathUtils.PathUnder(lease.Path, "active.json");
                lease.WriteNewActive(original);
            }
            var replacementMarker = "{\"Phase\":\"newer-owner\"}"u8.ToArray();

            var actions = new List<string>();
            string? observedName = null;
            var observedStatus = "created";
            var inspectVolumeCount = 0;
            string ListContainers(string filter) => filter.StartsWith("name=", StringComparison.Ordinal)
                ? observedName is null ? string.Empty : containerId
                : variant == "foreign-attachment" ? new string('b', 64) : string.Empty;
            string Inspect() => JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = containerId,
                    Name = "/" + observedName,
                    Image = imageId,
                    Config = new
                    {
                        Image = options.RuntimeImage,
                        User = "0:0",
                        Labels = new Dictionary<string, string> { ["appsurface.run"] = owner }
                    },
                    HostConfig = new
                    {
                        PidMode = "private", NetworkMode = "none", Init = true,
                        RestartPolicy = new { Name = "no" }, Privileged = false, ReadonlyRootfs = true,
                        CapAdd = Array.Empty<string>(), CapDrop = new[] { "ALL" }, SecurityOpt = new[] { "no-new-privileges" },
                        PidsLimit = 256, Memory = 512L * 1024 * 1024, Devices = Array.Empty<string>(), PortBindings = (object?)null
                    },
                    State = new { Running = false, Paused = false, Restarting = false, Pid = 0, Status = observedStatus },
                    Mounts = new[]
                    {
                        new { Type = "bind", Source = TestPathUtils.PathUnder(runPath, "worker"), Destination = "/proof/code", RW = false },
                        new { Type = "bind", Source = mountpoint, Destination = "/volume", RW = false }
                    }
                }
            });
            var docker = new SupervisedDocker("unix:///var/run/docker.sock", (_, args) =>
            {
                actions.Add(args[0]);
                switch (args[0])
                {
                    case "info": return Task.FromResult(daemon);
                    case "volume" when args[1] == "ls": return Task.FromResult(volume);
                    case "volume" when args[1] == "inspect":
                        var changed = variant == "changed-volume-during-observation" && ++inspectVolumeCount > 1;
                        return Task.FromResult(JsonSerializer.Serialize(new[]
                        {
                            new { Name = volume, Driver = "local", Scope = "local", CreatedAt = changed ? "replacement" : "created", Mountpoint = mountpoint,
                                Options = (object?)null, Labels = (object?)null }
                        }));
                    case "image": return Task.FromResult(JsonSerializer.Serialize(new[] { new { Id = imageId } }));
                    case "create":
                        observedName = args[2];
                        Assert.StartsWith("appsurface-owned-" + owner + "-observe-", observedName, StringComparison.Ordinal);
                        Assert.Contains("type=bind,source=" + mountpoint + ",target=/volume,readonly", args);
                        return Task.FromResult(containerId);
                    case "ps": return Task.FromResult(ListContainers(args[^1]));
                    case "inspect": return Task.FromResult(Inspect());
                    case "start": return Task.FromResult(containerId);
                    case "wait":
                        observedStatus = "exited";
                        return Task.FromResult(variant == "observer-nonzero" ? "1" : "0");
                    case "logs":
                        if (variant == "observer-empty-metadata") return Task.FromResult("null");
                        return Task.FromResult(JsonSerializer.Serialize(new
                        {
                            DeviceMajor = 1,
                            DeviceMinor = 2,
                            Inode = variant == "changed-root" ? 17 : 3,
                            BirthSeconds = 4,
                            BirthNanoseconds = 5
                        }));
                    case "rm":
                        Assert.Equal("exited", observedStatus);
                        observedName = null;
                        return Task.FromResult(containerId);
                    default:
                        throw new InvalidOperationException("Unexpected recovery command.");
                }
            });

            var recovered = false;
            async Task Recover()
            {
                Func<CancellationToken, Task>? beforeClear = null;
                if (variant == "marker-changed-before-clear")
                    beforeClear = _ =>
                    {
                        File.WriteAllBytes(activeMarkerPath, replacementMarker);
                        return Task.CompletedTask;
                    };
                var result = await AppSurfaceKeycloakSupervisedRun.VerifyStoppedAndClearAsync(
                    options, volume, owner, beforeClear, docker: docker);
                Assert.Equal(new AppSurfaceKeycloakSupervisedRecoveryResult("RecoveryOnlyCompleted", false, true), result);
                recovered = true;
            }
            switch (variant)
            {
                case "verified": await Recover(); break;
                case "changed-root":
                    var changed = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(Recover);
                    Assert.Equal("VolumeChanged", changed.Kind);
                    break;
                case "marker-changed-before-clear":
                    var markerChanged = await Assert.ThrowsAsync<QualifiedOwnershipFailure>(Recover);
                    Assert.Equal("MarkerChanged", markerChanged.Kind);
                    break;
                default: await Assert.ThrowsAsync<AppSurfaceKeycloakException>(Recover); break;
            }

            Assert.Equal(variant == "verified", recovered);
            Assert.Single(actions, verb => verb == "create");
            Assert.Single(actions, verb => verb == "start");
            var observerRetained = variant is "observer-nonzero" or "observer-empty-metadata" or "changed-volume-during-observation";
            if (observerRetained) Assert.DoesNotContain("rm", actions);
            else
            {
                Assert.Single(actions, verb => verb == "rm");
                Assert.True(actions.IndexOf("wait") < actions.IndexOf("rm"));
            }
            Assert.DoesNotContain("stop", actions);
            using var after = await SupervisedVolumeLease.Acquire(options.LeaseRoot, daemon, volume);
            if (recovered) Assert.False(after.HasActive);
            else if (observerRetained)
            {
                using var current = JsonDocument.Parse(after.ReadActive());
                var retained = Assert.Single(current.RootElement.GetProperty("Containers").EnumerateArray());
                Assert.Equal(containerId, retained.GetProperty("Id").GetString());
                Assert.Equal("observation", retained.GetProperty("Role").GetString());
                Assert.Equal(owner, current.RootElement.GetProperty("OwnerId").GetString());
                Assert.False(current.RootElement.GetProperty("LaunchUncertain").GetBoolean());
            }
            else if (variant == "marker-changed-before-clear") Assert.Equal(replacementMarker, after.ReadActive());
            else Assert.Equal(original, after.ReadActive());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
