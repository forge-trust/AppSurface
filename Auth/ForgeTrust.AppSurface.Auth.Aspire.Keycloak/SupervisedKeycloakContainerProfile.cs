using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private static readonly string[] InitializationCapabilities = ["CHOWN", "FOWNER"];

    internal bool VolumeHelper => Container.Role is VolumeObservationRole or VolumeInitializationRole
        && Container is { AnchorId: null, Init: true, Server: false };
    internal bool HasValidRole => Container.Role is not (VolumeObservationRole or VolumeInitializationRole) || VolumeHelper;
    internal bool ReadOnlyRoot => !Container.Server;
    internal int MemoryMiB => Container.Server ? 1024 : 512;
    internal string User => VolumeHelper ? "0:0"
        : Container.HistoricalServerUser && Container.Server ? "1000:0"
        : UserId() + ":" + (Container.Server ? "0" : getgid().ToString(CultureInfo.InvariantCulture));

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

    /// <summary>Checks observed namespace settings against the persisted topology.</summary>
    internal bool MatchesNamespace(SupervisedObservedContainer observed)
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
    internal bool MatchesCapabilities(JsonElement host)
    {
        var added = host.GetProperty("CapAdd");
        if (!host.GetProperty("CapDrop").EnumerateArray().Select(value => value.GetString()).SequenceEqual([DroppedCapabilities]))
            return false;
        return VolumeHelper && Container.Role == VolumeInitializationRole
            ? added.EnumerateArray().Select(value => value.GetString()).Order().SequenceEqual(InitializationCapabilities.Select(value => "CAP_" + value))
            : added.ValueKind != JsonValueKind.Array || added.GetArrayLength() == 0;
    }

    internal static string UserId() => getuid().ToString(CultureInfo.InvariantCulture);

    [DllImport("libSystem.B.dylib")] private static extern uint getuid();
    [DllImport("libSystem.B.dylib")] private static extern uint getgid();
}
