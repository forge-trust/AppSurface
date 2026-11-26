using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Defines the paths shared by local staging and supervised container commands.</summary>
/// <remarks>Host children are resolved under the private run directory. Container paths use Linux separators.
/// The launcher assigns access modes per workload, including when two private mounts use the same container path.</remarks>
internal static class SupervisedKeycloakRunLayout
{
    private const string PrivateDirectoryPrefix = "private-";

    /// <summary>The staged worker closure shared by the anchor and worker.</summary>
    internal static readonly MountDirectory Worker = new("worker", "/proof/code");
    /// <summary>The public configuration and pinned certificate shared across workloads.</summary>
    internal static readonly MountDirectory Public = new("public", "/proof/public");
    /// <summary>The owner fence, credential reference, and lifecycle receipts.</summary>
    internal static readonly MountDirectory State = new("state", "/proof/state");
    /// <summary>The deliberately empty package cache mounted in the .NET workloads.</summary>
    internal static readonly MountDirectory EmptyNuGet = new("empty-nuget", "/nuget");
    /// <summary>The confidential anchor's private input directory.</summary>
    internal static readonly MountDirectory AnchorPrivate = new(PrivateDirectoryPrefix + "anchor", "/proof/private");
    /// <summary>The Keycloak-only bootstrap and TLS directory.</summary>
    internal static readonly MountDirectory ServerPrivate = new(PrivateDirectoryPrefix + "server", "/proof/server");
    /// <summary>The generic worker's private bindings directory.</summary>
    internal static readonly MountDirectory WorkerPrivate = new(PrivateDirectoryPrefix + "worker", "/proof/private");

    /// <summary>The worker executable, also used to enter the owner anchor.</summary>
    internal static readonly MountedFile WorkerAssembly = new(Worker, AppSurfaceKeycloakRealmReadyWorker.WorkerAssemblyName + ".dll");
    /// <summary>The Aspire-free assembly shared by the worker closure and compatible consumer payloads.</summary>
    internal static readonly MountedFile WorkerCoreAssembly = new(Worker, "ForgeTrust.AppSurface.Auth.Keycloak.dll");
    /// <summary>The public desired realm declaration consumed by the generic worker.</summary>
    internal static readonly MountedFile Desired = new(Public, "desired.json");
    /// <summary>The public configuration delivered to the seed and web workloads.</summary>
    internal static readonly MountedFile PublicConfig = new(Public, "config.json");
    /// <summary>The protected input used to initialize the confidential anchor.</summary>
    internal static readonly MountedFile AnchorInput = new(AnchorPrivate, "input.json");
    /// <summary>The protected runtime bindings consumed by the generic worker.</summary>
    internal static readonly MountedFile WorkerBindings = new(WorkerPrivate, "bindings.json");
    /// <summary>The Keycloak server's private bootstrap configuration.</summary>
    internal static readonly MountedFile Bootstrap = new(ServerPrivate, "bootstrap.conf");
    /// <summary>The public TLS certificate used to pin the server.</summary>
    internal static readonly MountedFile PublicCertificate = new(Public, "server.crt");
    /// <summary>The same certificate in the historical UID 1000 server mount.</summary>
    internal static readonly MountedFile HistoricalCertificate = new(ServerPrivate, PublicCertificate.Name);
    /// <summary>The TLS private key mounted only for the Keycloak server.</summary>
    internal static readonly MountedFile ServerKey = new(ServerPrivate, "server.key");
    /// <summary>The reference published by the confidential anchor.</summary>
    internal static readonly MountedFile ClientReference = new(State, "client-reference.json");
    /// <summary>The owner-only fence published when no client credential is needed.</summary>
    internal static readonly MountedFile OwnerFence = new(State, "owner-fence");
    /// <summary>The receipt produced by the generic realm worker.</summary>
    internal static readonly MountedFile WorkerReceipt = new(State, "worker-result.json");
    /// <summary>The receipt path supplied to the required seed.</summary>
    internal static readonly MountedFile SeedReceipt = new(State, "seed-result.json");

    /// <summary>Gets the isolated code mount for a validated consumer workload name.</summary>
    internal static MountDirectory WorkloadPayload(string name) => new("payload-" + name, Worker.ContainerDirectory);

    /// <summary>Gets a validated consumer's private mount beneath a dedicated staging directory.</summary>
    /// <remarks>The parent is covered by the run's private-directory cleanup after stopped verification.
    /// Only the workload's own child is mounted into its container.</remarks>
    internal static MountDirectory WorkloadPrivate(string name) => new(PrivateDirectoryPrefix + "workloads/" + name, WorkerPrivate.ContainerDirectory);

    /// <summary>Lists existing private roots, including those from partially completed staging.</summary>
    /// <remarks>The caller verifies owned stopped state before deleting any returned directory.</remarks>
    internal static string[] GetPrivateCleanupDirectories(string runDirectory) =>
        Directory.GetDirectories(runDirectory, PrivateDirectoryPrefix + "*");

    /// <summary>Pairs a directory under the run root with its mount point in a workload.</summary>
    internal readonly record struct MountDirectory(string HostDirectory, string ContainerDirectory)
    {
        /// <summary>Resolves the local directory inside the private run.</summary>
        internal string HostPath(string runDirectory) => PathUtils.PathUnder(runDirectory, HostDirectory);

        /// <summary>Returns the path of a file at the container mount point. The caller supplies a single filename.</summary>
        internal string ContainerFile(string name) => ContainerDirectory + "/" + name;
    }

    /// <summary>Pairs the host and Linux container paths of one staged file or runtime receipt.</summary>
    internal readonly record struct MountedFile(MountDirectory Mount, string Name)
    {
        /// <summary>Resolves the local file inside the private run.</summary>
        internal string HostPath(string runDirectory) => PathUtils.PathUnder(runDirectory, Mount.HostDirectory, Name);

        /// <summary>Gets the path visible from the container with this mount attached.</summary>
        internal string ContainerPath => Mount.ContainerFile(Name);
    }
}
