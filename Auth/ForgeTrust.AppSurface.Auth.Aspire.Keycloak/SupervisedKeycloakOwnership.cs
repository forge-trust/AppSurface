using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;
using static ForgeTrust.AppSurface.Auth.Aspire.Keycloak.SupervisedKeycloakRunLayout;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Tracks durable launch intent and verifies the stopped state of one owner under its native lease.</summary>
/// <remarks>The caller holds the lease. Recovery supplies a recorded owner and uses the same inventory and
/// volume observations independently of Aspire graph resources or credentials.</remarks>
internal sealed class SupervisedKeycloakOwnership(
    SupervisedDocker docker, string runtimeImage, SupervisedVolumeLease lease,
    SupervisedKeycloakOwnership.ActiveRecord active, Func<AppSurfaceKeycloakException> failure,
    Func<string, CancellationToken, Task>? observe = null)
{
    internal static readonly TimeSpan StopBound = TimeSpan.FromSeconds(30);
    // Persisted schema-3 role values. Helper name suffixes differ from their logical roles.
    internal const string AnchorRole = "anchor";
    internal const string ServerRole = "keycloak";
    internal const string WorkerRole = "worker";
    internal const string VolumeObservationRole = "observation";
    internal const string VolumeInitializationRole = "initialization";
    private const string InitializationNameSuffix = "initialize";
    private const string ObservationNamePrefix = "observe-";
    private readonly List<OwnedContainer> _containers = [.. active.Containers];

    /// <summary>Returns whether a workload name would overlap an infrastructure role or its Docker name suffix.</summary>
    internal static bool IsReservedWorkloadName(string name) =>
        name is AnchorRole or ServerRole or WorkerRole or VolumeObservationRole or VolumeInitializationRole or InitializationNameSuffix
        || (name.StartsWith(ObservationNamePrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name.AsSpan(ObservationNamePrefix.Length), "N", out _));

    /// <summary>Gets the supplied baseline or the latest successfully persisted ownership snapshot.</summary>
    internal ActiveRecord Active { get; private set; } = active;

    /// <summary>Persists current ownership state with the exact collected launch intents.</summary>
    internal void Save(ActiveRecord owner)
    {
        var snapshot = owner with { Containers = _containers.ToArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (lease.HasActive) lease.ReplaceActive(bytes);
        else lease.WriteNewActive(bytes);
        Active = snapshot;
    }

    /// <summary>Clears a verified marker within the original stop deadline and restores it on deadline failure.</summary>
    /// <remarks>Stopped-state verification must precede this operation. Recovery also supplies the exact original
    /// marker bytes, which must still match while the lease is held.</remarks>
    /// <param name="heldLease">The lease for the stopped workloads.</param>
    /// <param name="elapsed">Reads monotonic time elapsed since the original shutdown or recovery began.</param>
    /// <param name="stop">The cancellation token for that same stop deadline.</param>
    /// <param name="expected">The original marker bytes for guarded recovery, when supplied.</param>
    internal static void ClearVerifiedMarker(
        SupervisedVolumeLease heldLease, Func<TimeSpan> elapsed, CancellationToken stop, byte[]? expected = null)
    {
        var saved = heldLease.ReadActive();
        if (expected is not null && !saved.AsSpan().SequenceEqual(expected))
            throw new QualifiedOwnershipFailure("MarkerChanged");

        stop.ThrowIfCancellationRequested();
        heldLease.ClearActive();
        try
        {
            stop.ThrowIfCancellationRequested();
            if (elapsed() >= StopBound) throw new TimeoutException();
        }
        catch
        {
            heldLease.RestoreActive(saved);
            throw;
        }
    }

    /// <summary>Creates and starts a container only after persisting the intent and verifying its created profile.</summary>
    /// <remarks>The role determines the init and server flags on new intents. Recovery uses the recorded schema-3 values.</remarks>
    internal async Task<OwnedContainer> StartContainerAsync(
        string role, string name, string image, OwnedMount[] mounts, int[] ports, string? anchorId,
        bool historicalServerUser, IReadOnlyList<string> arguments, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null, IReadOnlyList<string>? privateValues = null)
    {
        using var imageDocument = JsonDocument.Parse(await docker.RunAsync(ct, "image", "inspect", image));
        var imageId = imageDocument.RootElement[0].GetProperty("Id").GetString()!;
        var init = role is AnchorRole or VolumeObservationRole or VolumeInitializationRole;
        var server = role == ServerRole;
        var intent = new OwnedContainer(role, name, null, image, imageId, anchorId, init, server, mounts, ports)
        {
            HistoricalServerUser = server && historicalServerUser
        };
        var profile = new SupervisedKeycloakContainerProfile(intent);
        if (!profile.HasValidRole) throw failure();
        _containers.Add(intent);
        Save(Active);
        await ObserveWorkloadAsync(profile, role + "-intent", ct);
        if (privateValues is not null
            && (arguments.Any(argument => privateValues.Any(value => argument.Contains(value, StringComparison.Ordinal)))
                || environment is not null && environment.Values.Any(argument => privateValues.Any(value => argument.Contains(value, StringComparison.Ordinal)))))
            throw failure();

        var command = profile.CreateArguments(Active.OwnerId, Active.VolumeName is null, arguments, environment);
        var id = await LaunchMutationAsync(role == AnchorRole ? "anchor-create-ack" : null, ct, command);
        await ObserveWorkloadAsync(profile, role + "-created-unrecorded", ct);
        var container = intent with { Id = id };
        _containers[^1] = container;
        Save(Active);
        await ObserveWorkloadAsync(profile, role + "-created", ct);
        await VerifyContainerAsync(Active, container, false, ct);
        await ObserveWorkloadAsync(profile, role + "-before-start", ct);
        await LaunchMutationAsync(role == AnchorRole ? "anchor-start-ack" : null, ct, "start", id);
        await ObserveWorkloadAsync(profile, role + "-started", ct);
        return container;
    }

    private Task ObserveWorkloadAsync(SupervisedKeycloakContainerProfile profile, string phase, CancellationToken ct) =>
        profile.VolumeHelper ? Task.CompletedTask : observe?.Invoke(phase, ct) ?? Task.CompletedTask;

    // A lost response can precede a daemon-side commit. Only an acknowledged result clears that uncertainty.
    private async Task<string> LaunchMutationAsync(string? acknowledgement, CancellationToken ct, params string[] arguments)
    {
        ct.ThrowIfCancellationRequested();
        Save(Active with { LaunchUncertain = true });
        var result = await docker.RunAsync(ct, arguments);
        if (acknowledgement is not null && observe is not null)
            await observe(acknowledgement, ct);
        Save(Active with { LaunchUncertain = false });
        return result;
    }

    /// <summary>Requests a bounded stop only on the verified anchor for this owner.</summary>
    internal async Task FenceAsync(CancellationToken ct)
    {
        var anchor = _containers.FirstOrDefault(container => container.Role == AnchorRole);
        if (anchor is null) return;
        var actual = await VerifyContainerAsync(Active, anchor, false, ct);
        if (actual?.Running == true) await docker.RunAsync(ct, "stop", "--time", "1", actual.Id);
    }

    /// <summary>Initializes a newly reserved empty volume. The caller owns the creation and has excluded attachments.</summary>
    internal async Task InitializeNewVolumeAsync(string volume, CancellationToken ct)
    {
        var name = "appsurface-owned-" + Active.OwnerId + "-" + InitializationNameSuffix;
        var container = await StartContainerAsync(VolumeInitializationRole, name, runtimeImage,
            [WorkerMount(), new OwnedMount(volume, KeycloakVolumeIncarnation.VolumeRoot, true, true)],
            [], null, historicalServerUser: false,
            [WorkerAssembly.ContainerPath, KeycloakWorkerProtocol.InitializeVolume, SupervisedKeycloakContainerProfile.UserId()], ct);
        var id = container.Id!;
        if (await docker.RunAsync(ct, "wait", id) != "0") throw failure();
        await VerifyContainerAsync(Active, container, true, ct);
        await docker.RunAsync(ct, "rm", id);
    }

    /// <summary>Checks a container against the exact daemon, recorded owner, and Docker profile.</summary>
    /// <returns>The verified snapshot, or null when the owned container is conclusively absent.</returns>
    internal async Task<SupervisedObservedContainer?> VerifyContainerAsync(ActiveRecord owner, OwnedContainer expected, bool stopped, CancellationToken ct)
    {
        if (await docker.DaemonAsync(ct) != owner.DaemonId) throw failure();
        var observed = await docker.ContainerAsync(expected.Name, expected.Id, owner.OwnerId, ct);
        if (observed is null) return null;
        var profile = new SupervisedKeycloakContainerProfile(expected);
        var snapshot = observed.Snapshot;
        var host = snapshot.GetProperty("HostConfig");
        if (!profile.HasValidRole || observed.Image != expected.Image || observed.ImageId != expected.ImageId || observed.Privileged
            || observed.Init != expected.Init || observed.RestartPolicy != SupervisedKeycloakContainerProfile.RestartPolicy
            || observed.ReadonlyRoot != profile.ReadOnlyRoot || observed.User != profile.User
            || !profile.MatchesNamespace(observed) || !profile.MatchesCapabilities(host)
            || !host.GetProperty("SecurityOpt").EnumerateArray().Select(x => x.GetString()).SequenceEqual([SupervisedKeycloakContainerProfile.SecurityOption])
            || host.GetProperty("PidsLimit").GetInt64() != SupervisedKeycloakContainerProfile.ProcessLimit
            || host.GetProperty("Memory").GetInt64() != profile.MemoryMiB * 1024L * 1024
            || host.GetProperty("Devices").GetArrayLength() != 0)
            throw failure();
        var actualMounts = snapshot.GetProperty("Mounts").EnumerateArray().ToArray();
        if (actualMounts.Length != expected.Mounts.Length) throw failure();
        foreach (var m in actualMounts)
        {
            var declared = expected.Mounts.SingleOrDefault(x => x.Target == m.GetProperty("Destination").GetString()) ?? throw failure();
            if (m.GetProperty("Type").GetString() != (declared.Volume ? "volume" : "bind") || m.GetProperty("RW").GetBoolean() != declared.Writable
                || !MountSourceMatches(declared, m.GetProperty(declared.Volume ? "Name" : "Source").GetString())) throw failure();
        }
        if (!host.TryGetProperty("PortBindings", out var ports) || ports.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) throw failure();
        if (expected.Ports.Length == 0 && ports.ValueKind == JsonValueKind.Object && ports.EnumerateObject().Any()) throw failure();
        foreach (var port in expected.Ports)
        {
            if (!ports.TryGetProperty(port + "/tcp", out var binding) || binding.GetArrayLength() != 1 || binding[0].GetProperty("HostIp").GetString() != SupervisedKeycloakContainerProfile.PublishedHost
                || binding[0].GetProperty("HostPort").GetString() != port.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw failure();
        }
        if (expected.Ports.Length != 0 && ports.EnumerateObject().Count() != expected.Ports.Length) throw failure();
        if (stopped)
        {
            RequireStopped(observed);
            // Docker's stopped-state result and zero PID are definitive observations; an arbitrary failed CLI is not absence.
        }
        return observed;
    }

    /// <summary>Verifies all recorded workloads are stopped and any persistent volume still has its original identity.</summary>
    /// <remarks>Recovery supplies the original bytes to restore after its metadata-only observer is removed.</remarks>
    internal async Task VerifyStoppedAsync(ActiveRecord owner, CancellationToken ct, byte[]? recoveryMarker = null)
    {
        foreach (var container in owner.Containers)
        {
            while (true)
            {
                var observed = await VerifyContainerAsync(owner, container, false, ct);
                if (observed is null) break;
                if (!observed.Running) { RequireStopped(observed); break; }
                await Task.Delay(30, ct);
            }
        }
        if (owner.VolumeName is not null)
        {
            var current = await docker.VolumeAsync(owner.VolumeName, ct) ?? throw new QualifiedOwnershipFailure("VolumeMissing");
            if (owner.Volume is null || (await ObserveVolumeAsync(current, ct, recoveryMarker)).Identity != owner.Volume)
                throw new QualifiedOwnershipFailure("VolumeChanged");
            var allowed = owner.Containers.Where(c => c.Server).Select(c => c.Id).ToHashSet();
            if ((await docker.AttachmentsAsync(owner.VolumeName, ct)).Any(id => !allowed.Contains(id))) throw failure();
        }
    }

    /// <summary>Accepts only a definite stopped snapshot after complete identity/profile verification.</summary>
    internal static void RequireStopped(SupervisedObservedContainer observed)
    {
        if (observed.Running || observed.Paused || observed.Restarting || observed.HostPid != 0 || observed.Status is not ("exited" or "created"))
            throw new IOException("Owned stopped state could not be verified.");
    }

    private static bool MountSourceMatches(OwnedMount declared, string? observed) =>
        observed == declared.Source
        // Docker Desktop can report a translated VM path for the read-only live theme mount.
        || !declared.Volume && !declared.Writable && declared.Target.StartsWith("/opt/keycloak/themes/", StringComparison.Ordinal)
            && observed == "/host_mnt" + declared.Source;

    /// <summary>Removes only exact owned containers after reverifying stopped state, in reverse launch order.</summary>
    internal async Task RemoveStoppedAsync(ActiveRecord owner, CancellationToken ct)
    {
        foreach (var container in owner.Containers.Reverse())
        {
            var observed = await VerifyContainerAsync(owner, container, true, ct);
            if (observed is not null) await docker.RunAsync(ct, "rm", observed.Id);
        }
    }

    /// <summary>Observes the physical volume using a read-only helper and optionally reads the root and pinned H2 database owners.</summary>
    internal async Task<(VolumeIdentity Identity, KeycloakWorkerVolumeOwnership? Owner)> ObserveVolumeAsync(
        SupervisedVolume volume, CancellationToken ct, byte[]? recoveryMarker = null, bool selectServerUser = false)
    {
        var original = Active;
        var name = "appsurface-owned-" + original.OwnerId + "-" + ObservationNamePrefix + Guid.NewGuid().ToString("N");
        var observer = await StartContainerAsync(VolumeObservationRole, name, runtimeImage,
            [WorkerMount(), new OwnedMount(volume.Mountpoint, KeycloakVolumeIncarnation.VolumeRoot, false, false)],
            [], null, historicalServerUser: false,
            [WorkerAssembly.ContainerPath, selectServerUser ? KeycloakWorkerProtocol.VolumeOwnership : KeycloakWorkerProtocol.VolumeIncarnation], ct);
        var id = observer.Id!;
        if (await docker.RunAsync(ct, "wait", id) != "0") throw failure();
        var text = await docker.RunAsync(ct, "logs", id);
        var data = selectServerUser ? JsonSerializer.Deserialize<KeycloakWorkerVolumeOwnership>(text) : null;
        var stamp = selectServerUser ? data?.Incarnation : JsonSerializer.Deserialize<KeycloakVolumeIncarnation>(text);
        if (stamp is null) throw failure();
        if (await docker.VolumeAsync(volume.Name, ct) != volume) throw failure();
        await VerifyContainerAsync(Active, observer, true, ct);
        await docker.RunAsync(ct, "rm", id);

        // Retain an uncertain observer's durable intent. Restore the original record only after stopped proof and removal.
        if (recoveryMarker is not null)
        {
            lease.ReplaceActive(recoveryMarker);
            _containers.Remove(observer);
            Active = original;
        }
        return (new(volume.Name, volume.CreatedAt, volume.Mountpoint, stamp), data);
    }

    private OwnedMount WorkerMount() => new(Worker.HostPath(Active.RunRoot), Worker.ContainerDirectory, false, false);

    // These record members and their JSON names are the durable schema-3 contract.
    internal sealed record OwnedMount(string Source, string Target, bool Writable, bool Volume);
    internal sealed record OwnedContainer(string Role, string Name, string? Id, string Image, string ImageId, string? AnchorId, bool Init, bool Server, OwnedMount[] Mounts, int[] Ports)
    {
        public bool HistoricalServerUser { get; init; }
    }
    internal sealed record VolumeIdentity(string Name, string CreatedAt, string Mountpoint, KeycloakVolumeIncarnation Root);
    internal sealed record ActiveRecord(int Schema, string OwnerId, string DaemonId, string? VolumeName, VolumeIdentity? Volume, string RunRoot, QualifiedOwnedProcess OwnerProcess, OwnedContainer[] Containers, bool StagingComplete, bool OwnerFenceEstablished, bool LaunchUncertain);
}
