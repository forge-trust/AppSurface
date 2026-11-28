using System.Globalization;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Auth.Keycloak;
using static ForgeTrust.AppSurface.Auth.Aspire.Keycloak.SupervisedKeycloakOwnership;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Describes the Docker execution policy for a schema-3 owned container.</summary>
/// <remarks>New launches render arguments from this policy. Recovery applies the same expectations to independently
/// observed Docker inventory using the already persisted role, namespace, and mount declarations.</remarks>
internal readonly record struct SupervisedKeycloakContainerProfile(OwnedContainer Container)
{
    internal const string RestartPolicy = "no";
    internal const string SecurityOption = "no-new-privileges";
    internal const string PublishedHost = "127.0.0.1";
    internal const int ProcessLimit = 256;
    private const string DroppedCapabilities = "ALL";
    private const string IsolatedNetwork = "none";
    private const string AnchorNetwork = "bridge";
    // The schema-3 HistoricalServerUser flag binds this identity for launch and recovery.
    private const uint HistoricalServerUid = 1000;
    private const uint HistoricalServerGid = 0;
    private static readonly string HistoricalServerUser = FormattableString.Invariant($"{HistoricalServerUid}:{HistoricalServerGid}");
    private static readonly string[] InitializationCapabilities = ["CHOWN", "FOWNER"];

    internal bool VolumeHelper => Container.Role is VolumeObservationRole or VolumeInitializationRole
        && Container is { AnchorId: null, Init: true, Server: false };
    internal bool HasValidRole => Container.Role is not (VolumeObservationRole or VolumeInitializationRole) || VolumeHelper;
    internal bool ReadOnlyRoot => !Container.Server;
    internal int MemoryMiB => Container.Server ? 1024 : 512;
    internal string User => VolumeHelper ? "0:0"
        : Container.HistoricalServerUser && Container.Server ? HistoricalServerUser
        : UserId() + ":" + (Container.Server ? "0" : getgid().ToString(CultureInfo.InvariantCulture));

    /// <summary>Checks whether the qualified image can use its historical user for the observed volume.</summary>
    /// <remarks>The root must match. A present H2 database must also match, since the host user may have created it.</remarks>
    internal static bool CanUseHistoricalServerUser(KeycloakWorkerVolumeOwnership? ownership) =>
        ownership is
        {
            Uid: HistoricalServerUid, Gid: HistoricalServerGid,
            Database: null or { Uid: HistoricalServerUid, Gid: HistoricalServerGid }
        };

    /// <summary>Builds the create command for the already recorded intent.</summary>
    internal string[] CreateArguments(string ownerId, bool disposableData, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var command = new List<string> { "create", "--name", Container.Name, "--label", "appsurface.run=" + ownerId };
        if (!VolumeHelper) command.AddRange(["--label", "appsurface.supervised=1"]);
        command.AddRange(["--restart", RestartPolicy, "--cap-drop", DroppedCapabilities]);
        if (VolumeHelper && Container.Role == VolumeInitializationRole)
        {
            foreach (var capability in InitializationCapabilities) command.AddRange(["--cap-add", capability]);
        }
        command.AddRange(["--security-opt", SecurityOption, "--pids-limit", ProcessLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", MemoryMiB.ToString(CultureInfo.InvariantCulture) + "m", "--user", User]);
        if (Container.Init) command.Add("--init");
        if (VolumeHelper) command.AddRange(["--network", IsolatedNetwork]);
        else if (Container.AnchorId is { } anchor)
            command.AddRange(["--pid", "container:" + anchor, "--network", "container:" + anchor]);

        foreach (var port in Container.Ports) command.AddRange(["--publish", $"{PublishedHost}:{port}:{port}"]);
        if (ReadOnlyRoot) command.Add("--read-only");
        if (!VolumeHelper) command.AddRange(["--ulimit", "core=0", "--tmpfs", "/tmp:rw,mode=1777,size=256m"]);
        if (Container.Server && disposableData) command.AddRange(["--tmpfs", "/opt/keycloak/data:rw,mode=1777,size=256m"]);
        foreach (var mount in Container.Mounts)
            command.AddRange(["--mount", $"type={(mount.Volume ? "volume" : "bind")},source={mount.Source},target={mount.Target}"
                + (mount.Writable ? "" : ",readonly")]);

        if (!VolumeHelper)
            command.AddRange(["--env", "NUGET_PACKAGES=" + SupervisedKeycloakRunLayout.EmptyNuGet.ContainerDirectory, "--env", "HOME=/tmp/home"]);
        if (Container.Server) command.AddRange(["--env", "JAVA_OPTS_KC_HEAP=-Xms128m -Xmx512m"]);
        if (Container.Role == WorkerRole)
            command.AddRange(["--env", KeycloakWorkerProtocol.CertificateFileEnvironmentVariable + "=" + SupervisedKeycloakRunLayout.PublicCertificate.ContainerPath]);
        if (environment is not null)
        {
            foreach (var variable in environment) command.AddRange(["--env", variable.Key + "=" + variable.Value]);
        }

        command.AddRange(["--entrypoint", Container.Server ? "/opt/keycloak/bin/kc.sh" : "/usr/share/dotnet/dotnet", Container.Image]);
        command.AddRange(arguments);
        return command.ToArray();
    }

    /// <summary>Checks the observed container and typed Docker proof against this recorded execution profile.</summary>
    internal bool Matches(SupervisedObservedContainer observed, SupervisedContainerProfileProof proof)
    {
        if (!HasValidRole || observed.Image != Container.Image || observed.ImageId != Container.ImageId || observed.Privileged
            || observed.Init != Container.Init || observed.RestartPolicy != RestartPolicy
            || observed.ReadonlyRoot != ReadOnlyRoot || observed.User != User
            || !MatchesNamespace(observed) || !MatchesCapabilities(proof)
            || !proof.SecurityOptions.SequenceEqual([SecurityOption])
            || proof.ProcessLimit != ProcessLimit || proof.MemoryBytes != MemoryMiB * 1024L * 1024
            || proof.DeviceCount != 0)
            return false;

        return MatchesMounts(proof.Mounts) && MatchesPublishedPorts(proof.PortBindings);
    }

    /// <summary>Checks observed namespace settings against the persisted topology.</summary>
    private bool MatchesNamespace(SupervisedObservedContainer observed)
    {
        if (Container.AnchorId is { } anchor)
        {
            var expected = "container:" + anchor;
            if (observed.PidMode != expected || observed.NetworkMode != expected) return false;
        }
        else if (observed.PidMode is not ("" or "private")) return false;

        return VolumeHelper ? observed.NetworkMode == IsolatedNetwork
            : !Container.Init || observed.NetworkMode == AnchorNetwork;
    }

    /// <summary>Checks the observed dropped capabilities and the initialization-only additions.</summary>
    private bool MatchesCapabilities(SupervisedContainerProfileProof observed)
    {
        var added = observed.CapAdd;
        if (!observed.CapDrop.SequenceEqual([DroppedCapabilities]))
            return false;
        return VolumeHelper && Container.Role == VolumeInitializationRole
            ? added is not null
                && added.Order().SequenceEqual(InitializationCapabilities.Select(value => "CAP_" + value))
            : added is null || added.Length == 0;
    }

    private bool MatchesMounts(SupervisedObservedMount[] actualMounts)
    {
        if (actualMounts.Length != Container.Mounts.Length) return false;
        foreach (var mount in actualMounts)
        {
            var declared = Container.Mounts.SingleOrDefault(value => value.Target == mount.Destination);
            if (declared is null || mount.Type != (declared.Volume ? "volume" : "bind")
                || mount.Writable != declared.Writable
                || !MountSourceMatches(declared, declared.Volume ? mount.Name : mount.Source))
                return false;
        }
        return true;
    }

    private bool MatchesPublishedPorts(SupervisedObservedPortBinding[] ports)
    {
        if (ports.Length != Container.Ports.Length) return false;
        foreach (var port in Container.Ports)
        {
            var binding = ports.LastOrDefault(value => value.Name == port + "/tcp");
            if (binding?.Mappings is not { Length: 1 } mappings || mappings[0].HostIp != PublishedHost
                || mappings[0].HostPort != port.ToString(CultureInfo.InvariantCulture))
                return false;
        }
        return true;
    }

    private static bool MountSourceMatches(OwnedMount declared, string? observed) =>
        observed == declared.Source
        // Docker Desktop can report a translated VM path for the read-only live theme mount.
        || !declared.Volume && !declared.Writable && declared.Target.StartsWith("/opt/keycloak/themes/", StringComparison.Ordinal)
            && observed == "/host_mnt" + declared.Source;

    internal static string UserId() => getuid().ToString(CultureInfo.InvariantCulture);

    [DllImport("libSystem.B.dylib")] private static extern uint getuid();
    [DllImport("libSystem.B.dylib")] private static extern uint getgid();
}
