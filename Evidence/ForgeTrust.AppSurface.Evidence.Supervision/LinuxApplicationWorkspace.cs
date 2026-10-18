using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed application-bundle roles copied from the separately selected immutable definition.</summary>
internal enum LinuxApplicationBundleRole
{
    /// <summary>Top-level managed AppHost entry DLL.</summary>
    AppHost,
    /// <summary>AppHost runtime configuration.</summary>
    AppHostRuntimeConfiguration,
    /// <summary>Managed native HTTP resource DLL.</summary>
    Resource,
    /// <summary>Resource runtime configuration.</summary>
    ResourceRuntimeConfiguration,
    /// <summary>Fixed DCP executable.</summary>
    Dcp,
    /// <summary>Executable extension below dcp/ext.</summary>
    DcpExtension,
    /// <summary>Read-only application dependency.</summary>
    Dependency,
    /// <summary>One declared immutable input.</summary>
    DeclaredInput,
    /// <summary>Read-only managed dependency manifest.</summary>
    DependencyManifest,
}

/// <summary>One copied definition row, kept local to Supervision so portable tests need no Planner internals.</summary>
internal sealed record LinuxApplicationBundleFile(string RelativePath, LinuxApplicationBundleRole Role,
    long LengthBytes, string Sha256, uint Mode);

/// <summary>Detached native metadata used only to compare a fully traversed tree with its selected rows.</summary>
/// <remarks>Constructing a row grants no file descriptor, account, workspace, or application authority.</remarks>
internal sealed record LinuxApplicationObservedEntry(string RelativePath, bool IsDirectory, uint Uid, uint Gid,
    ushort Mode, uint LinkCount, ulong Inode, long LengthBytes, string? Sha256);

/// <summary>Validated paths and file rows for one run-scoped application workspace; creates no native state.</summary>
internal sealed class LinuxApplicationWorkspaceLayout
{
    private const ushort ReadOnlyDirectoryMode = 0x16d; // 0555.
    private const int MaximumTreeDepth = 32;
    internal const int MaximumTreeEntries = 8192;
    private const long MaximumBundleBytes = 512L * 1024 * 1024;

    private LinuxApplicationWorkspaceLayout(Guid runId, string toolRoot, string entryDigest, uint uid, uint gid,
        LinuxApplicationLimits limits, IReadOnlyList<LinuxApplicationBundleFile> files)
    {
        RunId = runId;
        ToolRoot = toolRoot;
        EntryDigest = entryDigest;
        ApplicationUid = uid;
        ApplicationGid = gid;
        Limits = limits with { };
        BundleFiles = Array.AsReadOnly(files.ToArray());
        RootName = "appsurface-app-" + runId.ToString("N", CultureInfo.InvariantCulture);
        Root = "/run/" + RootName;
        ControlRoot = Root + "/control";
        BundleRoot = Root + "/bundle";
        ScratchRoot = Root + "/scratch";
        ProtectedOutputRoot = Root + "/denied-output";
        DeniedToolRoot = toolRoot;
        AppHostPath = BundleRoot + "/" + BundleFiles.Single(static file => file.Role == LinuxApplicationBundleRole.AppHost).RelativePath;
        DcpPath = BundleRoot + "/dcp/dcp";
        InputPath = BundleRoot + "/" + BundleFiles.Single(static file => file.Role == LinuxApplicationBundleRole.DeclaredInput).RelativePath;
        DirectoryPaths = Array.AsReadOnly(FindDirectories(BundleFiles).OrderBy(PathDepth).ThenBy(static path => path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the reserved run generation.</summary>
    internal Guid RunId { get; }
    /// <summary>Gets the original protected ToolRoot whose complete tree is masked from the application.</summary>
    internal string ToolRoot { get; }
    /// <summary>Gets the exact externally selected canonical entry digest.</summary>
    internal string EntryDigest { get; }
    /// <summary>Gets the actual application UID read from the active run-account holder.</summary>
    internal uint ApplicationUid { get; }
    /// <summary>Gets the actual application GID read from the active run-account holder.</summary>
    internal uint ApplicationGid { get; }
    /// <summary>Gets copied bounded unit limits; this value cannot grant a runtime capability.</summary>
    internal LinuxApplicationLimits Limits { get; }
    /// <summary>Gets the fixed root-owned generation component.</summary>
    internal string RootName { get; }
    /// <summary>Gets the fresh run-scoped root path.</summary>
    internal string Root { get; }
    /// <summary>Gets the private root-only application control directory.</summary>
    internal string ControlRoot { get; }
    /// <summary>Gets the copied immutable bundle, outside and disjoint from ToolRoot.</summary>
    internal string BundleRoot { get; }
    /// <summary>Gets the application-owned, host-visible size-bounded tmpfs path.</summary>
    internal string ScratchRoot { get; }
    /// <summary>Gets the root-only protected-output denial directory.</summary>
    internal string ProtectedOutputRoot { get; }
    /// <summary>Gets the complete original ToolRoot path to hide from the application unit.</summary>
    internal string DeniedToolRoot { get; }
    /// <summary>Gets the fixed top-level AppHost DLL path.</summary>
    internal string AppHostPath { get; }
    /// <summary>Gets the fixed DCP executable path.</summary>
    internal string DcpPath { get; }
    /// <summary>Gets the one exact definition-declared input path.</summary>
    internal string InputPath { get; }
    /// <summary>Gets a copied read-only view of every selected immutable file row.</summary>
    internal ReadOnlyCollection<LinuxApplicationBundleFile> BundleFiles { get; }
    /// <summary>Gets every implicit bundle directory, including the bundle root represented by an empty path.</summary>
    internal ReadOnlyCollection<string> DirectoryPaths { get; }

    /// <summary>Builds path data only after validating the complete definition projection and denied-root scope.</summary>
    internal static LinuxApplicationWorkspaceLayout Create(Guid runId, string toolRoot, string entryDigest,
        uint applicationUid, uint applicationGid, LinuxApplicationLimits limits,
        IReadOnlyList<LinuxApplicationBundleFile> files, IReadOnlyList<string> readOnlyInputs)
    {
        if (runId == Guid.Empty || limits is null || files is null || readOnlyInputs is null
            || applicationUid is 0 or uint.MaxValue || applicationGid is 0 or uint.MaxValue
            || !IsDigest(entryDigest) || limits.ScratchBytes is < 1 or > 1024L * 1024 * 1024
            || limits.MemoryBytes is < 1 or > 1024L * 1024 * 1024 || limits.MaximumTasks is < 1 or > 128
            || limits.MaximumOutputBytes is < 1 or > 1024 * 1024 || limits.StartSeconds is < 1 or > 120
            || limits.StoppingSeconds is < 1 or > 30)
            throw Invalid();
        LinuxProtectedDeployment.ValidateAbsolute(toolRoot);
        ValidateFiles(files);
        var appHost = files.Single(static file => file.Role == LinuxApplicationBundleRole.AppHost);
        var dcp = files.Single(static file => file.Role == LinuxApplicationBundleRole.Dcp);
        var declared = files.Single(static file => file.Role == LinuxApplicationBundleRole.DeclaredInput);
        if (appHost.RelativePath.Contains('/', StringComparison.Ordinal) || dcp.RelativePath != "dcp/dcp"
            || readOnlyInputs.Count != 1 || !StringComparer.Ordinal.Equals(readOnlyInputs[0], declared.RelativePath))
            throw Invalid();

        var rootName = "appsurface-app-" + runId.ToString("N", CultureInfo.InvariantCulture);
        var root = "/run/" + rootName;
        if (Overlaps(toolRoot, root)) throw Invalid();
        return new(runId, toolRoot, entryDigest, applicationUid, applicationGid, limits, files);
    }

    /// <summary>Requires exact root-owned metadata, names, lengths, links, and hashes for a flattened tree inventory.</summary>
    /// <remarks>This pure comparison is not native inspection; callers must supply rows built from retained descriptors.</remarks>
    internal static void RequireExactInventory(IReadOnlyList<LinuxApplicationBundleFile> files,
        IReadOnlyList<LinuxApplicationObservedEntry> actual)
    {
        ValidateFiles(files);
        if (actual is null || actual.Count > MaximumTreeEntries) throw Invalid();
        var expectedDirectories = FindDirectories(files).Append(string.Empty).ToHashSet(StringComparer.Ordinal);
        var expectedFiles = files.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (actual.Count != expectedDirectories.Count + expectedFiles.Count) throw Invalid();
        foreach (var entry in actual)
        {
            if (entry is null || !seen.Add(entry.RelativePath) || entry.Uid != 0 || entry.Gid != 0
                || entry.Inode == 0 || (entry.Mode & 0x0e00) != 0) throw Invalid();
            if (entry.IsDirectory)
            {
                if (!expectedDirectories.Contains(entry.RelativePath) || entry.Mode != (ushort)(0x4000 | ReadOnlyDirectoryMode)
                    || entry.Sha256 is not null) throw Invalid();
                continue;
            }
            if (!expectedFiles.TryGetValue(entry.RelativePath, out var file)
                || entry.Mode != (ushort)(0x8000 | file.Mode) || entry.LinkCount != 1
                || entry.LengthBytes != file.LengthBytes || !StringComparer.Ordinal.Equals(entry.Sha256, file.Sha256))
                throw Invalid();
        }
        if (!expectedDirectories.All(seen.Contains) || !expectedFiles.Keys.All(seen.Contains)) throw Invalid();
    }

    /// <summary>Returns a stable read-only set of implicit parent directories represented by selected file paths.</summary>
    internal static IReadOnlyCollection<string> FindDirectories(IReadOnlyList<LinuxApplicationBundleFile> files)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var parts = file.RelativePath.Split('/');
            var prefix = string.Empty;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                prefix = prefix.Length == 0 ? parts[index] : prefix + "/" + parts[index];
                result.Add(prefix);
            }
        }
        if (result.Count + files.Count + 1 > MaximumTreeEntries) throw Invalid();
        if (result.Any(directory => PathDepth(directory) > MaximumTreeDepth)) throw Invalid();
        return Array.AsReadOnly(result.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Requires a closed, disjoint ASCII file inventory with the mandatory role cardinalities and limits.</summary>
    /// <exception cref="EvidenceAdmissionException">A row, including a later row's path, is incomplete or invalid.</exception>
    internal static void ValidateFiles(IReadOnlyList<LinuxApplicationBundleFile> files)
    {
        if (files is null || files.Count is < 6 or > 256 || files.Any(static file => file is null || file.RelativePath is null)
            || files.Select(static file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw Invalid();
        long total = 0;
        foreach (var file in files)
        {
            ValidateRelative(file.RelativePath);
            var executable = file.Role is LinuxApplicationBundleRole.Dcp or LinuxApplicationBundleRole.DcpExtension;
            if (!Enum.IsDefined(file.Role) || file.LengthBytes is < 1 or > 128L * 1024 * 1024
                || file.LengthBytes > MaximumBundleBytes - total || !IsDigest(file.Sha256)
                || file.Mode != (executable ? 0x16dU : 0x124U)) throw Invalid();
            total += file.LengthBytes;
            if (file.Role == LinuxApplicationBundleRole.Dcp && file.RelativePath != "dcp/dcp"
                || file.Role == LinuxApplicationBundleRole.DcpExtension && !file.RelativePath.StartsWith("dcp/ext/", StringComparison.Ordinal)
                || file.Role is LinuxApplicationBundleRole.AppHost or LinuxApplicationBundleRole.Resource
                    && !file.RelativePath.EndsWith(".dll", StringComparison.Ordinal)
                || file.Role is LinuxApplicationBundleRole.AppHostRuntimeConfiguration or LinuxApplicationBundleRole.ResourceRuntimeConfiguration
                    && !file.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)
                || file.Role == LinuxApplicationBundleRole.DependencyManifest && !file.RelativePath.EndsWith(".deps.json", StringComparison.Ordinal))
                throw Invalid();
            if (files.Any(other => !ReferenceEquals(other, file)
                && other.RelativePath.StartsWith(file.RelativePath + "/", StringComparison.OrdinalIgnoreCase))) throw Invalid();
        }
        foreach (var role in new[] { LinuxApplicationBundleRole.AppHost, LinuxApplicationBundleRole.AppHostRuntimeConfiguration,
            LinuxApplicationBundleRole.Resource, LinuxApplicationBundleRole.ResourceRuntimeConfiguration,
            LinuxApplicationBundleRole.Dcp, LinuxApplicationBundleRole.DeclaredInput })
            if (files.Count(file => file.Role == role) != 1) throw Invalid();
        _ = FindDirectories(files);
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || Encoding.ASCII.GetByteCount(path) > 4095
            || path.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '/'))
            || path.Split('/').Any(static part => part.Length is < 1 or > 255 || part is "." or "..")) throw Invalid();
    }

    private static int PathDepth(string path) => path.Length == 0 ? 0 : path.Count(static character => character == '/') + 1;
    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);
    private static bool Contains(string parent, string child) => parent == "/" || child == parent
        || child.StartsWith(parent + "/", StringComparison.Ordinal);
    internal static EvidenceAdmissionException Invalid() =>
        new("ASEVD410", "The restricted application workspace is unavailable or changed.");
}

/// <summary>Actual root-owned copied bundle and host-visible application scratch retained through process settlement.</summary>
/// <remarks>
/// The source is the exact protected <c>ToolRoot/application</c> tree. Every source file is matched to the
/// separately selected definition before it is copied to a disjoint fresh <c>/run</c> generation. The
/// application reads the copied bundle while the entire original ToolRoot is masked. Scratch is a real
/// size-limited tmpfs mount. Account custody is reserved before the first native mutation; failed creation,
/// cancellation, or teardown leaves this holder and its descriptors quarantined.
/// </remarks>
internal sealed class LinuxApplicationWorkspace : IDisposable
{
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong PathFlags = 0x200000 | 0x80000;
    private const ulong ReadFileFlags = 0x800 | 0x80000;
    private const ulong DestinationFileFlags = 0x01 | 0x40 | 0x80 | 0x20000 | 0x80000;
    private const ulong ParentResolution = 0x02 | 0x04 | 0x08;
    private const ulong ChildResolution = ParentResolution | 0x01;
    private const ushort MutableDirectoryMode = 0x1c0; // 0700.
    private const ushort RootDirectoryMode = 0x1c9; // 0711.
    private const ushort BundleDirectoryMode = 0x16d; // 0555.
    private const ulong OutputProbePathFlags = 0x200000 | 0x20000 | 0x80000; // O_PATH|O_NOFOLLOW|O_CLOEXEC.
    private const string OutputProbeName = "native-resource-output-probe";
    private static readonly byte[] ArmedContents = "appsurface-application-armed-v1\n"u8.ToArray();

    private readonly object _gate = new();
    private readonly EvidenceProtectedLaunchInput _input;
    private readonly LinuxOwnerActivation _owner;
    private readonly LinuxRunAccounts _accounts;
    private readonly LinuxApplicationWorkspaceLayout _layout;
    private readonly LinuxInputDeadline _deadline;
    private readonly IReadOnlyDictionary<string, LinuxApplicationBundleFile> _files;
    private readonly Dictionary<string, LinuxProtectedNode> _sourceDirectories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinuxProtectedNode> _sourceFiles = new(StringComparer.Ordinal);
    private LinuxProtectedNode? _toolProbe;
    private readonly List<LinuxProtectedNode> _sourceNodes = [];
    private readonly Dictionary<string, WorkspaceNode> _bundleDirectories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceNode> _bundleFiles = new(StringComparer.Ordinal);
    private readonly List<WorkspaceNode> _nodes = [];
    private WorkspaceNode? _slash;
    private WorkspaceNode? _run;
    private WorkspaceNode? _root;
    private WorkspaceNode? _control;
    private WorkspaceNode? _bundle;
    private WorkspaceNode? _scratchMountPoint;
    private WorkspaceNode? _deniedOutput;
    private WorkspaceNode? _mountedScratch;
    private WorkspaceNode? _armedMarker;
    private LinuxApplicationProcess? _application;
    private Task? _sealTask;
    private bool _applicationRetentionAttempted;
    private bool _armed;
    private bool _scratchMounted;
    private bool _sealAttempted;
    private bool _sealed;
    private bool _quarantined;
    private bool _handlesClosed;
    private bool _releaseGuardPassed;
    private bool _initialized;

    private LinuxApplicationWorkspace(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxApplicationWorkspaceLayout layout)
    {
        _input = input;
        _owner = owner;
        _accounts = accounts;
        _layout = layout;
        _deadline = input.Deadline;
        _files = new ReadOnlyDictionary<string, LinuxApplicationBundleFile>(
            layout.BundleFiles.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal));
    }

    /// <summary>Gets the root-owned run generation.</summary>
    internal string Root => _layout.Root;
    /// <summary>Gets the immutable copied application bundle root.</summary>
    internal string BundleRoot => _layout.BundleRoot;
    /// <summary>Gets the mounted application scratch path visible in the host mount namespace.</summary>
    internal string ScratchRoot => _layout.ScratchRoot;
    /// <summary>Gets the root-only protected-output denial directory.</summary>
    internal string ProtectedOutputRoot => _layout.ProtectedOutputRoot;
    /// <summary>Gets the original ToolRoot path which the unit must make inaccessible.</summary>
    internal string DeniedToolRoot => _layout.DeniedToolRoot;
    /// <summary>Gets the selected top-level AppHost path inside the copied bundle.</summary>
    internal string AppHostPath => _layout.AppHostPath;
    /// <summary>Gets the fixed DCP executable path inside the copied bundle.</summary>
    internal string DcpPath => _layout.DcpPath;
    /// <summary>Gets the exact declared-input path inside the copied bundle.</summary>
    internal string InputPath => _layout.InputPath;
    /// <summary>Gets the exact selected canonical entry digest.</summary>
    internal string EntryDigest => _layout.EntryDigest;
    /// <summary>Gets the active run-account application's actual UID.</summary>
    internal uint ApplicationUid => _layout.ApplicationUid;
    /// <summary>Gets the active run-account application's actual primary GID.</summary>
    internal uint ApplicationGid => _layout.ApplicationGid;

    /// <summary>Creates and retains a fresh copied bundle and actual tmpfs before returning launch paths.</summary>
    /// <param name="input">The authentic root-selected protected launch input and original monotonic deadline.</param>
    /// <param name="owner">The actual activated root owner for this run.</param>
    /// <param name="accounts">The actual live run-account holder, including the separate application identity.</param>
    /// <param name="definition">The compiled definition already selected by the caller; this factory does not select entries.</param>
    /// <param name="entryDigest">The caller-selected canonical digest, independently recomputed here.</param>
    /// <param name="token">The existing job token; no fresh allowance is created.</param>
    internal static LinuxApplicationWorkspace Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, EvidenceClosedApplicationDefinition definition, string entryDigest,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(definition);
        owner.RequireLaunchInput(input, token);
        accounts.RequireOwnedBy(owner, token);
        var canonicalDigest = EvidenceClosedApplicationCatalogue.ComputeEntryDigest(definition);
        if (!StringComparer.Ordinal.Equals(canonicalDigest, entryDigest)) throw LinuxApplicationWorkspaceLayout.Invalid();
        var files = definition.BundleFiles.Select(static file => new LinuxApplicationBundleFile(file.RelativePath,
            ToWorkspaceRole(file.Role), file.LengthBytes, file.Sha256, file.Mode)).ToArray();
        var limits = new LinuxApplicationLimits(definition.Capabilities.ScratchBytes, definition.Capabilities.MemoryBytes,
            definition.Capabilities.MaximumTasks, definition.Capabilities.MaximumOutputBytes,
            definition.Capabilities.StartSeconds, definition.Capabilities.StoppingSeconds);
        var layout = LinuxApplicationWorkspaceLayout.Create(owner.RunId, input.Request.ToolRoot, entryDigest,
            accounts.ApplicationUid, accounts.ApplicationGid, limits, files, definition.Capabilities.ReadOnlyInputs);
        var workspace = new LinuxApplicationWorkspace(input, owner, accounts, layout);

        // This reservation precedes source opens and every filesystem mutation. Any failure leaves the actual
        // object reachable from the account holder, with every opened descriptor retained for quarantine.
        accounts.RetainApplicationForCustody(workspace, owner);
        try
        {
            workspace.CreateNative(token);
            return workspace;
        }
        catch (OperationCanceledException)
        {
            workspace.Quarantine();
            throw;
        }
        catch (Exception error) when (Recoverable(error))
        {
            workspace.Quarantine();
            throw LinuxApplicationWorkspaceLayout.Invalid();
        }
    }

    /// <summary>Checks the exact source and copied inventories and their retained bytes before application work.</summary>
    /// <remarks>Every path operation uses retained descriptors and mandatory openat2; there is no pathname fallback.</remarks>
    internal void RecheckBundle(CancellationToken token)
    {
        lock (_gate)
        {
            RequireAccountOwner(_accounts, _owner);
            if (!_initialized || _quarantined || _sealed || _handlesClosed || _sealAttempted) throw LinuxApplicationWorkspaceLayout.Invalid();
            _owner.RequireActive(token);
            _accounts.RequireOwnedBy(_owner, token);
            RecheckBundleCore(token);
            RecheckScratchCore(token);
            RequireNoOutputProbe(token);
        }
    }

    /// <summary>Creates the root-owned freshness marker in the already mounted scratch directory before start dispatch.</summary>
    /// <remarks>The held inode and its current name are checked again at each guard; replacement or removal quarantines the run.</remarks>
    internal void Arm(CancellationToken token)
    {
        lock (_gate)
        {
            RequireActiveCore(token);
            if (_armed || _mountedScratch is null) throw LinuxApplicationWorkspaceLayout.Invalid();
            RequireScratchEmpty(token);
            SafeFileHandle? marker = null;
            try
            {
                _deadline.Check(token);
                marker = OpenAt2(Fd(_mountedScratch.Handle), "armed", DestinationFileFlags,
                    0x124, ChildResolution);
                if (Fchown(marker, 0, 0) != 0) throw LinuxApplicationWorkspaceLayout.Invalid();
                RandomAccess.Write(marker, ArmedContents, 0);
                if (Fchmod(marker, 0x124) != 0) throw LinuxApplicationWorkspaceLayout.Invalid();
                var metadata = LinuxProtectedMetadata.From(StatFd(marker));
                var readable = OpenMarkerReadOnly(_mountedScratch.Handle, marker, token);
                marker.Dispose();
                marker = readable;
                var node = new WorkspaceNode(marker, _mountedScratch, "armed", 0, 0, false, 0x124, allowMount: false)
                { Metadata = metadata };
                _nodes.Add(node);
                _armedMarker = node;
                marker = null;
                RecheckNode(node);
                RequireMarkerBytes(node, token);
                _armed = true;
                _deadline.Check(token);
            }
            catch (Exception error) when (Recoverable(error))
            {
                marker?.Dispose();
                Quarantine();
                throw LinuxApplicationWorkspaceLayout.Invalid();
            }
        }
    }

    /// <summary>Checks exact owner references without reading account properties, for account-holder reservation.</summary>
    internal void RequireAccountOwner(LinuxRunAccounts accounts, LinuxOwnerActivation owner)
    {
        if (!ReferenceEquals(_accounts, accounts) || !ReferenceEquals(_owner, owner)
            || _layout.RunId == Guid.Empty || _layout.RootName != "appsurface-app-" + owner.RunId.ToString("N"))
            throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    /// <summary>Retains the one authentic application process before its start, stop, or pipe work can dispatch.</summary>
    internal void RetainApplication(LinuxApplicationProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            RequireAccountOwner(_accounts, _owner);
            if (!_initialized || _quarantined || _sealAttempted || _applicationRetentionAttempted)
                throw LinuxApplicationWorkspaceLayout.Invalid();
            _owner.RequireActive(default);
            _accounts.RequireOwnedBy(_owner, default);
            _applicationRetentionAttempted = true;
            _application = process;
        }
    }

    /// <summary>Rechecks retained owner/account identities and every workspace path for active application work.</summary>
    internal void RequireOwnedBy(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            if (!_initialized || _quarantined || _sealed || _sealAttempted || _handlesClosed)
                throw LinuxApplicationWorkspaceLayout.Invalid();
            _owner.RequireActive(token);
            _accounts.RequireOwnedBy(owner, token);
            RecheckWorkspaceCore(token);
        }
    }

    /// <summary>Rechecks the same retained generation for stop and cleanup after work admission closes.</summary>
    internal void RequireControlOwnedBy(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            if (!_initialized || _quarantined || _sealed || _handlesClosed) throw LinuxApplicationWorkspaceLayout.Invalid();
            _owner.RequireControlIdentity(token);
            _accounts.RequireControlOwnedBy(owner, token);
            RecheckWorkspaceCore(token);
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Retains bounded private prefixes only from the original joined output pair.</summary>
    /// <remarks>Root-only diagnostic files do not participate in admission, health, cleanup success or account release.</remarks>
    internal void RetainPrivateOutput(SupervisionOutputReceipt output, CancellationToken token)
    {
        lock (_gate)
        {
            _owner.RequireControlIdentity(token);
            if (_control is null || _handlesClosed) throw LinuxApplicationWorkspaceLayout.Invalid();
            RecheckNode(_control);
            foreach (var item in new[] { (Name: "stdout.prefix", Bytes: output.Stdout.Prefix.ToArray()),
                (Name: "stderr.prefix", Bytes: output.Stderr.Prefix.ToArray()) })
            {
                if (item.Bytes.Length > 64 * 1024) throw LinuxApplicationWorkspaceLayout.Invalid();
                _deadline.Check(token);
                using var file = OpenAt2(Fd(_control.Handle), item.Name, DestinationFileFlags, 0x180, ChildResolution);
                SetOwnershipAndMode(file, 0, 0, 0x180);
                RandomAccess.Write(file, item.Bytes, 0);
                var metadata = LinuxProtectedMetadata.From(StatFd(file));
                if (metadata.Uid != 0 || metadata.Gid != 0 || metadata.Mode != (0x8000 | 0x180)
                    || metadata.LinkCount != 1 || metadata.Length != (ulong)item.Bytes.Length) throw LinuxApplicationWorkspaceLayout.Invalid();
                RequireSameNamed(_control.Handle, item.Name, metadata);
            }
            _owner.RequireControlIdentity(token);
        }
    }

    /// <summary>Authenticates and joins the retained application, then strictly unmounts and seals its storage.</summary>
    /// <remarks>
    /// Physical settlement is required before the first scratch cleanup operation. The tmpfs is unmounted with
    /// flags zero; a busy mount is a sticky quarantine failure. No lazy unmount or recursive removal is attempted.
    /// </remarks>
    internal Task SealForCleanupAsync(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            if (_sealTask is not null) return _sealTask;
            if (_quarantined || _sealed || _sealAttempted)
                return _sealTask = Task.FromException(LinuxApplicationWorkspaceLayout.Invalid());
            _sealAttempted = true;
            try
            {
                SealCore(owner, accounts, token);
                return _sealTask = Task.CompletedTask;
            }
            catch (OperationCanceledException error)
            {
                _quarantined = true;
                return _sealTask = Task.FromException(error);
            }
            catch (Exception error) when (Recoverable(error))
            {
                _quarantined = true;
                return _sealTask = Task.FromException(LinuxApplicationWorkspaceLayout.Invalid());
            }
        }
    }

    /// <summary>Requires successful sealing and repeats all native release checks before any application ID is deleted.</summary>
    internal void RequireSealedForAccountRelease(LinuxRunAccounts accounts, LinuxOwnerActivation owner,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireAccountOwner(accounts, owner);
            _owner.RequireControlIdentity(token);
            if (!_sealed || _quarantined || _handlesClosed || _scratchMounted) throw LinuxApplicationWorkspaceLayout.Invalid();
            try
            {
                RecheckSealedCore(token);
                _owner.RequireControlIdentity(token);
                _releaseGuardPassed = true;
            }
            catch (OperationCanceledException) { _quarantined = true; throw; }
            catch (Exception error) when (Recoverable(error))
            {
                _quarantined = true;
                throw LinuxApplicationWorkspaceLayout.Invalid();
            }
        }
    }

    private void CreateNative(CancellationToken token)
    {
        RequireActiveCore(token);
        _slash = OpenAncestor(null, "/", token, allowMount: true);
        _run = OpenAncestor(_slash, "run", token, allowMount: true);
        _root = CreateDirectory(_run, _layout.RootName, 0, 0, MutableDirectoryMode, token);
        _control = CreateDirectory(_root, "control", 0, 0, MutableDirectoryMode, token);
        _bundle = CreateDirectory(_root, "bundle", 0, 0, MutableDirectoryMode, token);
        _scratchMountPoint = CreateDirectory(_root, "scratch", 0, 0, MutableDirectoryMode, token);
        _deniedOutput = CreateDirectory(_root, "denied-output", 0, 0, MutableDirectoryMode, token);
        RequireNoOutputProbe(token);
        OpenAndInventorySource(token);
        CopyBundle(token);
        MountScratch(token);
        SetDirectoryMode(_root, RootDirectoryMode, token);
        RequireSameRootChildren(token);
        _initialized = true;
        RecheckWorkspaceCore(token);
        _deadline.Check(token);
    }

    private void OpenAndInventorySource(CancellationToken token)
    {
        _toolProbe = LinuxProtectedNode.OpenAbsolute(_layout.ToolRoot + "/protected-tool.dll", false,
            true, 64L * 1024 * 1024, _sourceNodes, token, _deadline);
        var expectedProbeHash = _layout.BundleFiles.Single(static file => file.Role == LinuxApplicationBundleRole.Resource).Sha256;
        if (_toolProbe.Metadata.Mode != (ushort)(0x8000 | 0x124)) throw LinuxApplicationWorkspaceLayout.Invalid();
        RequireManagedToolProbe(_toolProbe.ReadManagedProbeBytes(token, _deadline), expectedProbeHash);
        var sourceRoot = _layout.ToolRoot + "/application";
        _sourceDirectories[string.Empty] = null!;
        var root = LinuxProtectedNode.OpenAbsolute(sourceRoot, true, true, 0, _sourceNodes, token, _deadline);
        Inventory(root, string.Empty, 0);
        var rows = BuildSourceRows(token);
        LinuxApplicationWorkspaceLayout.RequireExactInventory(_layout.BundleFiles, rows);

        void Inventory(LinuxProtectedNode directory, string relative, int depth)
        {
            _deadline.Check(token);
            RequireProtectedDirectory(directory.Metadata);
            if (relative.Length != 0 && _sourceDirectories.ContainsKey(relative)) throw LinuxApplicationWorkspaceLayout.Invalid();
            _sourceDirectories[relative] = directory;
            _sourceDirectoryHandles[relative] = directory;
            if (_sourceNodes.Count > LinuxApplicationWorkspaceLayout.MaximumTreeEntries) throw LinuxApplicationWorkspaceLayout.Invalid();
            foreach (var name in directory.ReadNames(token))
            {
                _deadline.Check(token);
                var childPath = relative.Length == 0 ? name : relative + "/" + name;
                var isDirectory = directory.IsChildDirectory(name);
                var child = directory.OpenChild(name, isDirectory, token);
                _sourceNodes.Add(child);
                if (isDirectory)
                {
                    if (depth >= 32) throw LinuxApplicationWorkspaceLayout.Invalid();
                    Inventory(child, childPath, depth + 1);
                }
                else
                {
                    ValidateSourcePath(childPath);
                    if (!_sourceFiles.TryAdd(childPath, child)) throw LinuxApplicationWorkspaceLayout.Invalid();
                    var expected = _files.GetValueOrDefault(childPath);
                    if (expected is null || child.Metadata.Length != (ulong)expected.LengthBytes
                        || child.Metadata.Mode != (ushort)(0x8000 | expected.Mode)
                        || child.Metadata.Uid != 0 || child.Metadata.Gid != 0 || child.Metadata.LinkCount != 1
                        || child.Hash(token, _deadline) != expected.Sha256) throw LinuxApplicationWorkspaceLayout.Invalid();
                }
                if (_sourceNodes.Count > LinuxApplicationWorkspaceLayout.MaximumTreeEntries) throw LinuxApplicationWorkspaceLayout.Invalid();
            }
        }
    }

    private void CopyBundle(CancellationToken token)
    {
        _bundleDirectories[string.Empty] = _bundle!;
        foreach (var relative in _layout.DirectoryPaths)
        {
            _deadline.Check(token);
            var parentPath = Parent(relative);
            if (!_bundleDirectories.TryGetValue(parentPath, out var parent)) throw LinuxApplicationWorkspaceLayout.Invalid();
            _bundleDirectories[relative] = CreateDirectory(parent, Leaf(relative), 0, 0, MutableDirectoryMode, token);
        }
        foreach (var file in _layout.BundleFiles.OrderBy(static file => file.RelativePath, StringComparer.Ordinal))
        {
            _deadline.Check(token);
            if (!_sourceFiles.TryGetValue(file.RelativePath, out var source)
                || !_bundleDirectories.TryGetValue(Parent(file.RelativePath), out var parent)) throw LinuxApplicationWorkspaceLayout.Invalid();
            source.Recheck();
            using (var output = OpenAt2(Fd(parent.Handle), Leaf(file.RelativePath), DestinationFileFlags,
                0x180, ChildResolution))
            {
                source.CopyTo(output, token, _deadline);
                if (source.Hash(token, _deadline) != file.Sha256) throw LinuxApplicationWorkspaceLayout.Invalid();
                SetOwnershipAndMode(output, 0, 0, checked((ushort)file.Mode));
                var copied = LinuxProtectedMetadata.From(StatFd(output));
                RequireFileMetadata(copied, file, file.Mode);
            }
            var handle = OpenAt2(Fd(parent.Handle), Leaf(file.RelativePath), ReadFileFlags, 0, ChildResolution);
            var metadata = LinuxProtectedMetadata.From(StatFd(handle));
            var node = new WorkspaceNode(handle, parent, Leaf(file.RelativePath), 0, 0, false,
                checked((ushort)file.Mode), allowMount: false)
            { Metadata = metadata };
            _nodes.Add(node);
            _bundleFiles.Add(file.RelativePath, node);
        }
        foreach (var path in _layout.DirectoryPaths.OrderByDescending(PathDepth).ThenByDescending(static path => path, StringComparer.Ordinal))
            SetDirectoryMode(_bundleDirectories[path], BundleDirectoryMode, token);
        SetDirectoryMode(_bundle!, BundleDirectoryMode, token);
        RecheckBundleCore(token);
    }

    private void MountScratch(CancellationToken token)
    {
        RequireActiveCore(token);
        var options = "size=" + _layout.Limits.ScratchBytes.ToString(CultureInfo.InvariantCulture)
            + ",mode=0700,uid=" + _layout.ApplicationUid.ToString(CultureInfo.InvariantCulture)
            + ",gid=" + _layout.ApplicationGid.ToString(CultureInfo.InvariantCulture);
        var mountPoint = "/proc/self/fd/" + Fd(_scratchMountPoint!.Handle).ToString(CultureInfo.InvariantCulture);
        _deadline.Check(token);
        if (Mount("tmpfs", mountPoint, "tmpfs", 2 | 4 | 8, options) != 0)
            throw LinuxApplicationWorkspaceLayout.Invalid();
        _scratchMounted = true;
        _deadline.Check(token);
        var handle = OpenAt2(Fd(_root!.Handle), "scratch", DirectoryFlags, 0, ParentResolution);
        var metadata = LinuxProtectedMetadata.From(StatFd(handle));
        RequireDirectory(metadata, _layout.ApplicationUid, _layout.ApplicationGid, MutableDirectoryMode);
        var mounted = new WorkspaceNode(handle, _root, "scratch", _layout.ApplicationUid,
            _layout.ApplicationGid, true, MutableDirectoryMode, allowMount: true)
        { Metadata = metadata };
        _nodes.Add(mounted);
        _mountedScratch = mounted;
        RequireSameNamed(_root.Handle, "scratch", metadata, allowMount: true);
        RequireScratchEmpty(token);
    }

    private void SealCore(LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
    {
        var application = _application ?? throw LinuxApplicationWorkspaceLayout.Invalid();
        // This authentic process check is deliberately the first cleanup action. A failed stop/join never
        // detaches scratch, changes its ownership, closes its descriptors, or permits account deletion.
        application.RequirePhysicalSettlement(owner, accounts, this, token);
        RequireAccountOwner(accounts, owner);
        _owner.RequireControlIdentity(token);
        if (!_initialized || _quarantined || _handlesClosed || !_scratchMounted) throw LinuxApplicationWorkspaceLayout.Invalid();
        RecheckWorkspaceCore(token);
        RequireNoOutputProbe(token);

        if (_armedMarker is not null)
        {
            _armedMarker.Handle.Dispose();
            _nodes.Remove(_armedMarker);
            _armedMarker = null;
        }
        if (_mountedScratch is not null)
        {
            // Drop our own mount-root descriptor before umount. Other retained holders may still make this
            // fail with EBUSY; flags remain zero and that failure is terminal for this workspace generation.
            _mountedScratch.Handle.Dispose();
            _nodes.Remove(_mountedScratch);
            _mountedScratch = null;
        }
        _deadline.Check(token);
        // Resolve the fixed child from the retained root parent after closing our own mounted-root FD.
        // The hidden pre-mount scratch FD names the covered filesystem, not the selected tmpfs mount.
        var target = "/proc/self/fd/" + Fd(_root!.Handle).ToString(CultureInfo.InvariantCulture) + "/scratch";
        if (Umount2(target, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            try
            {
                var bytes = EvidenceCanonicalJson.Serialize(new
                {
                    schema = "evidence-application-storage-v1",
                    phase = "unmount-rejected",
                    errno = error is > 0 and <= 4095 ? error : (int?)null
                });
                using var stream = Console.OpenStandardError();
                stream.Write(bytes); stream.Write("\n"u8); stream.Flush();
            }
            catch (Exception diagnostic) when (diagnostic is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) { }
            throw LinuxApplicationWorkspaceLayout.Invalid();
        }
        _scratchMounted = false;
        _deadline.Check(token);
        RecheckNode(_scratchMountPoint!);
        RequireDirectoryNames(_scratchMountPoint!.Handle, Array.Empty<string>(), token);
        RequireNoOutputProbe(token);
        RecheckBundleCore(token);
        RecheckSealedPaths(token);
        _sealed = true;
    }

    private void RequireActiveCore(CancellationToken token)
    {
        RequireAccountOwner(_accounts, _owner);
        if (_quarantined || _sealAttempted || _sealed || _handlesClosed) throw LinuxApplicationWorkspaceLayout.Invalid();
        _owner.RequireActive(token);
        _accounts.RequireOwnedBy(_owner, token);
        _deadline.Check(token);
    }

    private void RecheckWorkspaceCore(CancellationToken token)
    {
        _owner.RequireControlIdentity(token);
        _deadline.Check(token);
        foreach (var node in _nodes)
        {
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(node, _mountedScratch) || ReferenceEquals(node, _armedMarker)) continue;
            if (_scratchMounted && ReferenceEquals(node, _scratchMountPoint))
            {
                // This held base inode is intentionally covered by the authenticated tmpfs mount.
                var hidden = LinuxProtectedMetadata.From(StatFd(node.Handle));
                if (!hidden.SameAncestorAs(node.Metadata)) throw LinuxApplicationWorkspaceLayout.Invalid();
                continue;
            }
            RecheckNode(node);
        }
        RequireSameRootChildren(token);
        RecheckBundleCore(token);
        RecheckScratchCore(token);
        RequireNoOutputProbe(token);
        _owner.RequireControlIdentity(token);
        _deadline.Check(token);
    }

    private void RecheckBundleCore(CancellationToken token)
    {
        if (_bundle is null || _sourceDirectories.Count == 0 || _bundleFiles.Count != _layout.BundleFiles.Count)
            throw LinuxApplicationWorkspaceLayout.Invalid();
        if (_toolProbe is null || _toolProbe.Hash(token, _deadline)
            != _layout.BundleFiles.Single(static file => file.Role == LinuxApplicationBundleRole.Resource).Sha256)
            throw LinuxApplicationWorkspaceLayout.Invalid();
        RequireSourceNames(token);
        RequireBundleNames(token);
        foreach (var source in _sourceNodes)
        {
            token.ThrowIfCancellationRequested();
            _deadline.Check(token);
            source.Recheck();
        }
        foreach (var file in _layout.BundleFiles)
        {
            _deadline.Check(token);
            var source = _sourceFiles[file.RelativePath];
            var destination = _bundleFiles[file.RelativePath];
            source.Recheck();
            RecheckNode(destination);
            if (source.Hash(token, _deadline) != file.Sha256 || HashWorkspaceFile(destination, token) != file.Sha256)
                throw LinuxApplicationWorkspaceLayout.Invalid();
        }
        LinuxApplicationWorkspaceLayout.RequireExactInventory(_layout.BundleFiles, BuildSourceRows(token));
        LinuxApplicationWorkspaceLayout.RequireExactInventory(_layout.BundleFiles, BuildBundleRows(token));
        _deadline.Check(token);
    }

    private void RequireSourceNames(CancellationToken token)
    {
        foreach (var pair in _sourceDirectories)
        {
            var expected = _sourceFiles.Keys.Where(path => Parent(path) == pair.Key).Select(Leaf)
                .Concat(_sourceDirectories.Keys.Where(path => path.Length != 0 && Parent(path) == pair.Key).Select(Leaf))
                .Order(StringComparer.Ordinal).ToArray();
            var directory = _sourceDirectoryHandles[pair.Key];
            if (!directory.ReadNames(token).SequenceEqual(expected, StringComparer.Ordinal)) throw LinuxApplicationWorkspaceLayout.Invalid();
        }
    }

    private readonly Dictionary<string, LinuxProtectedNode> _sourceDirectoryHandles = new(StringComparer.Ordinal);

    private IReadOnlyList<LinuxApplicationObservedEntry> BuildSourceRows(CancellationToken token)
    {
        var rows = new List<LinuxApplicationObservedEntry>();
        foreach (var pair in _sourceDirectoryHandles)
        {
            var metadata = pair.Value.Metadata;
            rows.Add(new(pair.Key, true, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                metadata.Inode, checked((long)metadata.Length), null));
        }
        foreach (var pair in _sourceFiles)
        {
            token.ThrowIfCancellationRequested();
            var metadata = pair.Value.Metadata;
            rows.Add(new(pair.Key, false, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                metadata.Inode, checked((long)metadata.Length), pair.Value.Hash(token, _deadline)));
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    private IReadOnlyList<LinuxApplicationObservedEntry> BuildBundleRows(CancellationToken token)
    {
        var rows = new List<LinuxApplicationObservedEntry>();
        foreach (var pair in _bundleDirectories)
        {
            var metadata = LinuxProtectedMetadata.From(StatFd(pair.Value.Handle));
            rows.Add(new(pair.Key, true, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                metadata.Inode, checked((long)metadata.Length), null));
        }
        foreach (var pair in _bundleFiles)
        {
            token.ThrowIfCancellationRequested();
            var metadata = LinuxProtectedMetadata.From(StatFd(pair.Value.Handle));
            rows.Add(new(pair.Key, false, metadata.Uid, metadata.Gid, metadata.Mode, metadata.LinkCount,
                metadata.Inode, checked((long)metadata.Length), HashWorkspaceFile(pair.Value, token)));
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    private void RequireBundleNames(CancellationToken token)
    {
        foreach (var pair in _bundleDirectories)
        {
            var expected = _bundleFiles.Keys.Where(path => Parent(path) == pair.Key).Select(Leaf)
                .Concat(_bundleDirectories.Keys.Where(path => path.Length != 0 && Parent(path) == pair.Key).Select(Leaf))
                .Order(StringComparer.Ordinal).ToArray();
            if (!ReadNames(pair.Value.Handle, token).SequenceEqual(expected, StringComparer.Ordinal))
                throw LinuxApplicationWorkspaceLayout.Invalid();
        }
    }

    private void RecheckScratchCore(CancellationToken token)
    {
        if (!_scratchMounted || _mountedScratch is null) throw LinuxApplicationWorkspaceLayout.Invalid();
        RecheckNode(_mountedScratch);
        if (_armed)
        {
            if (_armedMarker is null) throw LinuxApplicationWorkspaceLayout.Invalid();
            RecheckNode(_armedMarker);
            RequireMarkerBytes(_armedMarker, token);
        }
        else RequireScratchEmpty(token);
    }

    private void RequireScratchEmpty(CancellationToken token)
    {
        if (_mountedScratch is null) throw LinuxApplicationWorkspaceLayout.Invalid();
        RequireDirectoryNames(_mountedScratch.Handle, Array.Empty<string>(), token);
    }

    private void RequireSealedCore(CancellationToken token)
    {
        if (_scratchMounted || _scratchMountPoint is null) throw LinuxApplicationWorkspaceLayout.Invalid();
        RecheckNode(_scratchMountPoint!);
        RequireDirectoryNames(_scratchMountPoint!.Handle, Array.Empty<string>(), token);
        RecheckBundleCore(token);
        RecheckSealedPaths(token);
        RequireNoOutputProbe(token);
    }

    private void RecheckSealedCore(CancellationToken token)
    {
        RequireSealedCore(token);
        _owner.RequireControlIdentity(token);
        _deadline.Check(token);
    }

    private void RecheckSealedPaths(CancellationToken token)
    {
        foreach (var node in _nodes)
        {
            token.ThrowIfCancellationRequested();
            RecheckNode(node);
        }
        RequireSameRootChildren(token);
        if (_control is null || _deniedOutput is null || _root is null || _bundle is null
            || _control.Metadata.Mode != (ushort)(0x4000 | MutableDirectoryMode)
            || _deniedOutput.Metadata.Mode != (ushort)(0x4000 | MutableDirectoryMode)
            || _root.Metadata.Mode != (ushort)(0x4000 | RootDirectoryMode)
            || _bundle.Metadata.Mode != (ushort)(0x4000 | BundleDirectoryMode)) throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private void RequireSameRootChildren(CancellationToken token)
    {
        if (_root is null) throw LinuxApplicationWorkspaceLayout.Invalid();
        RequireDirectoryNames(_root.Handle, ["bundle", "control", "denied-output", "scratch"], token);
    }

    /// <summary>Checks bounded bytes against the selected resource digest and actual PE/CLR metadata.</summary>
    /// <param name="bytes">Retained root probe bytes, never loaded or executed by this method.</param>
    /// <param name="expectedSha256">The exact compiled resource-role digest.</param>
    /// <remarks>This data check grants no file, application, writer or admission capability. The owner separately
    /// retains the root-owned nonwritable file and rechecks its named bytes before start, health and cleanup.</remarks>
    internal static void RequireManagedToolProbe(ReadOnlyMemory<byte> bytes, string expectedSha256)
    {
        try
        {
            if (bytes.Length is < 1 or > 64 * 1024 * 1024
                || Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) != expectedSha256)
                throw LinuxApplicationWorkspaceLayout.Invalid();
            using var reader = new PEReader(new MemoryStream(bytes.ToArray(), writable: false));
            if (!reader.HasMetadata || reader.PEHeaders.CorHeader is null) throw LinuxApplicationWorkspaceLayout.Invalid();
            _ = reader.GetMetadata().GetContent();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { throw LinuxApplicationWorkspaceLayout.Invalid(); }
    }

    private void RequireNoOutputProbe(CancellationToken token)
    {
        if (_deniedOutput is null) throw LinuxApplicationWorkspaceLayout.Invalid();
        _deadline.Check(token);
        try
        {
            using var existing = OpenAt2(Fd(_deniedOutput.Handle), OutputProbeName, OutputProbePathFlags, 0, ChildResolution);
            throw LinuxApplicationWorkspaceLayout.Invalid();
        }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception native
            && native.NativeErrorCode == 2)
        {
            _deadline.Check(token);
        }
    }

    /// <summary>Reopens only the fixed marker as read-only after creation, preserving its exact sampled inode.</summary>
    /// <param name="parent">Already retained containing directory; this helper creates no workspace authority.</param>
    /// <param name="original">Actual creator descriptor retained after the exclusive write and final mode change.</param>
    /// <param name="token">Original startup token; no deadline is created.</param>
    /// <returns>A new read-only descriptor only if its actual metadata is unchanged.</returns>
    /// <remarks>Requires Linux openat2. Root ownership, fixed bytes and freshness are separately checked by Arm;
    /// the FD utility can also check ordinary owned test files without authenticating a root application.</remarks>
    internal static SafeFileHandle OpenMarkerReadOnly(SafeFileHandle parent, SafeFileHandle original,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var expected = LinuxProtectedMetadata.From(StatFd(original));
        var opened = OpenAt2(Fd(parent), "armed", ReadFileFlags, 0, ChildResolution);
        try
        {
            if (LinuxProtectedMetadata.From(StatFd(opened)) != expected
                || LinuxProtectedMetadata.From(StatFd(original)) != expected) throw LinuxApplicationWorkspaceLayout.Invalid();
            token.ThrowIfCancellationRequested();
            return opened;
        }
        catch { opened.Dispose(); throw; }
    }

    private void RequireMarkerBytes(WorkspaceNode marker, CancellationToken token)
    {
        if (marker.Metadata.Uid != 0 || marker.Metadata.Gid != 0 || marker.Metadata.Mode != (ushort)(0x8000 | 0x124)
            || marker.Metadata.LinkCount != 1 || marker.Metadata.Length != (ulong)ArmedContents.Length
            || !ReadExact(marker.Handle, ArmedContents.Length, token).AsSpan().SequenceEqual(ArmedContents))
            throw LinuxApplicationWorkspaceLayout.Invalid();
        RequireSameNamed(marker.Parent!.Handle, marker.Name, marker.Metadata);
    }

    private byte[] ReadExact(SafeFileHandle handle, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            _deadline.Check(token);
            var read = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (read == 0) throw LinuxApplicationWorkspaceLayout.Invalid();
            offset += read;
        }
        _deadline.Check(token);
        return bytes;
    }

    private string HashWorkspaceFile(WorkspaceNode node, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while ((ulong)offset < node.Metadata.Length)
        {
            _deadline.Check(token);
            var count = (int)Math.Min((ulong)buffer.Length, node.Metadata.Length - (ulong)offset);
            var read = RandomAccess.Read(node.Handle, buffer.AsSpan(0, count), offset);
            if (read == 0) throw LinuxApplicationWorkspaceLayout.Invalid();
            hash.AppendData(buffer.AsSpan(0, read));
            offset += read;
        }
        _deadline.Check(token);
        if (RandomAccess.Read(node.Handle, buffer.AsSpan(0, 1), offset) != 0) throw LinuxApplicationWorkspaceLayout.Invalid();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private WorkspaceNode OpenAncestor(WorkspaceNode? parent, string name, CancellationToken token, bool allowMount)
    {
        _deadline.Check(token);
        var handle = OpenAt2(parent is null ? -100 : Fd(parent.Handle), name, DirectoryFlags, 0,
            parent is null ? 0 : allowMount ? ParentResolution : ChildResolution);
        var metadata = LinuxProtectedMetadata.From(StatFd(handle));
        RequireAncestor(metadata);
        var node = new WorkspaceNode(handle, parent, name, 0, 0, true, 0, ancestor: true, allowMount: allowMount)
        { Metadata = metadata };
        _nodes.Add(node);
        RecheckNode(node);
        _deadline.Check(token);
        return node;
    }

    private WorkspaceNode CreateDirectory(WorkspaceNode parent, string name, uint uid, uint gid,
        ushort mode, CancellationToken token)
    {
        _deadline.Check(token);
        RecheckNode(parent);
        if (MkdirAt(Fd(parent.Handle), name, mode) != 0) throw LinuxApplicationWorkspaceLayout.Invalid();
        _deadline.Check(token);
        var handle = OpenAt2(Fd(parent.Handle), name, DirectoryFlags, 0, ChildResolution);
        var initial = LinuxProtectedMetadata.From(StatFd(handle));
        if (initial.Inode == 0 || initial.Uid != 0 || initial.Gid != 0
            || initial.Mode != (ushort)(0x4000 | mode)) throw LinuxApplicationWorkspaceLayout.Invalid();
        var node = new WorkspaceNode(handle, parent, name, uid, gid, true, mode)
        { Metadata = initial };
        _nodes.Add(node);
        RequireSameNamed(parent.Handle, name, initial);
        SetOwnershipAndMode(handle, uid, gid, mode);
        node.Metadata = LinuxProtectedMetadata.From(StatFd(handle));
        RequireDirectory(node.Metadata, uid, gid, mode);
        RequireSameNamed(parent.Handle, name, node.Metadata);
        _deadline.Check(token);
        return node;
    }

    private void SetDirectoryMode(WorkspaceNode node, ushort mode, CancellationToken token)
    {
        _deadline.Check(token);
        SetOwnershipAndMode(node.Handle, 0, 0, mode);
        node.Uid = 0;
        node.Gid = 0;
        node.Mode = mode;
        node.Metadata = LinuxProtectedMetadata.From(StatFd(node.Handle));
        RequireDirectory(node.Metadata, 0, 0, mode);
        RequireSameNamed(node.Parent!.Handle, node.Name, node.Metadata, node.AllowMount);
        _deadline.Check(token);
    }

    private void RecheckNode(WorkspaceNode node)
    {
        var current = LinuxProtectedMetadata.From(StatFd(node.Handle));
        if (node.Ancestor) RequireAncestor(current);
        else if (node.Directory) RequireDirectory(current, node.Uid, node.Gid, node.Mode);
        else if (ReferenceEquals(node, _armedMarker))
        {
            if (current.Uid != 0 || current.Gid != 0 || current.Mode != (0x8000 | 0x124)
                || current.LinkCount != 1 || current.Length != (ulong)ArmedContents.Length) throw LinuxApplicationWorkspaceLayout.Invalid();
        }
        else
        {
            var relative = _bundleFiles.SingleOrDefault(pair => ReferenceEquals(pair.Value, node)).Key;
            if (relative is null || !_files.TryGetValue(relative, out var selected)) throw LinuxApplicationWorkspaceLayout.Invalid();
            RequireFileMetadata(current, selected, selected.Mode);
        }
        if (node.Ancestor || node.Directory)
        {
            if (!current.SameAncestorAs(node.Metadata)) throw LinuxApplicationWorkspaceLayout.Invalid();
        }
        else if (current != node.Metadata) throw LinuxApplicationWorkspaceLayout.Invalid();
        if (node.Parent is not null) RequireSameNamed(node.Parent.Handle, node.Name, node.Metadata, node.AllowMount);
    }

    private void RequireSourceDirectory(LinuxProtectedNode node, string relative)
    {
        RequireProtectedDirectory(node.Metadata);
        _sourceDirectoryHandles[relative] = node;
    }

    private static void RequireProtectedDirectory(LinuxProtectedMetadata metadata)
    {
        if (!LinuxWorkspaceMetadata.MatchesDirectory(metadata, 0, 0, BundleDirectoryMode))
            throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static void RequireFileMetadata(LinuxProtectedMetadata metadata, LinuxApplicationBundleFile file, uint mode)
    {
        if (!LinuxWorkspaceMetadata.MatchesApplicationFile(metadata, file.LengthBytes, mode))
            throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static void RequireDirectory(LinuxProtectedMetadata metadata, uint uid, uint gid, ushort mode)
    {
        if (!LinuxWorkspaceMetadata.MatchesDirectory(metadata, uid, gid, mode)) throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static void RequireAncestor(LinuxProtectedMetadata metadata)
    {
        LinuxWorkspaceMetadata.RequireApplicationAncestor(metadata);
    }

    private void RequireDirectoryNames(SafeFileHandle directory, IReadOnlyCollection<string> expected, CancellationToken token)
    {
        if (!ReadNames(directory, token).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static IReadOnlyList<string> ReadNames(SafeFileHandle directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var fresh = OpenAt2(Fd(directory), ".", DirectoryFlags, 0, ChildResolution);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = ReadDirectory(fresh, buffer);
            if (count == 0) break;
            foreach (var name in LinuxDirectoryData.Parse(buffer.AsSpan(0, count)))
                if (names.Count >= LinuxProtectedDeployment.MaximumNodes || !names.Add(name)) throw LinuxApplicationWorkspaceLayout.Invalid();
        }
        token.ThrowIfCancellationRequested();
        return Array.AsReadOnly(names.Order(StringComparer.Ordinal).ToArray());
    }

    private static void RequireSameNamed(SafeFileHandle parent, string name, LinuxProtectedMetadata expected,
        bool allowMount = false)
    {
        using var named = OpenAt2(Fd(parent), name, PathFlags, 0, allowMount ? ParentResolution : ChildResolution);
        var actual = LinuxProtectedMetadata.From(StatFd(named));
        if ((expected.Mode & 0xf000) == 0x4000 ? !actual.SameAncestorAs(expected) : actual != expected)
            throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static void SetOwnershipAndMode(SafeFileHandle handle, uint uid, uint gid, ushort mode)
    {
        if (Fchown(handle, uid, gid) != 0 || Fchmod(handle, mode) != 0) throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string Leaf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static int PathDepth(string path) => path.Length == 0 ? 0 : path.Count(static character => character == '/') + 1;

    private static void ValidateSourcePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Any(static character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('-' or '_' or '.' or '/')) || path.Split('/').Any(static part => part.Length is < 1 or > 255
                || part is "." or "..")) throw LinuxApplicationWorkspaceLayout.Invalid();
    }

    private static LinuxApplicationBundleRole ToWorkspaceRole(EvidenceClosedBundleRole role) => role switch
    {
        EvidenceClosedBundleRole.AppHost => LinuxApplicationBundleRole.AppHost,
        EvidenceClosedBundleRole.AppHostRuntimeConfiguration => LinuxApplicationBundleRole.AppHostRuntimeConfiguration,
        EvidenceClosedBundleRole.Resource => LinuxApplicationBundleRole.Resource,
        EvidenceClosedBundleRole.ResourceRuntimeConfiguration => LinuxApplicationBundleRole.ResourceRuntimeConfiguration,
        EvidenceClosedBundleRole.Dcp => LinuxApplicationBundleRole.Dcp,
        EvidenceClosedBundleRole.DcpExtension => LinuxApplicationBundleRole.DcpExtension,
        EvidenceClosedBundleRole.Dependency => LinuxApplicationBundleRole.Dependency,
        EvidenceClosedBundleRole.DeclaredInput => LinuxApplicationBundleRole.DeclaredInput,
        EvidenceClosedBundleRole.DependencyManifest => LinuxApplicationBundleRole.DependencyManifest,
        _ => throw LinuxApplicationWorkspaceLayout.Invalid(),
    };

    private void Quarantine() { lock (_gate) _quarantined = true; }

    /// <summary>Closes native descriptors only after strict unmount, sealing, and a successful release guard.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_handlesClosed) return;
            try
            {
                if (!_sealed || _quarantined || !_releaseGuardPassed) throw LinuxApplicationWorkspaceLayout.Invalid();
                RequireSealedForAccountRelease(_accounts, _owner, default);
                _owner.RequireControlIdentity(default);
                var failed = false;
                for (var index = _nodes.Count - 1; index >= 0; index--)
                {
                    try { _nodes[index].Handle.Dispose(); }
                    catch (Exception error) when (Recoverable(error)) { failed = true; }
                }
                try { LinuxProtectedDeployment.Close(_sourceNodes); }
                catch (Exception error) when (Recoverable(error)) { failed = true; }
                if (failed) throw LinuxApplicationWorkspaceLayout.Invalid();
                _handlesClosed = true;
            }
            catch (OperationCanceledException) { _quarantined = true; throw; }
            catch (Exception error) when (Recoverable(error))
            {
                _quarantined = true;
                throw LinuxApplicationWorkspaceLayout.Invalid();
            }
        }
    }

    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException
        or PlatformNotSupportedException or ArgumentException or EvidenceAdmissionException
        or ObjectDisposedException or InvalidOperationException or OverflowException or System.ComponentModel.Win32Exception;

    private sealed class WorkspaceNode(SafeFileHandle handle, WorkspaceNode? parent, string name,
        uint uid, uint gid, bool directory, ushort mode, bool ancestor = false, bool allowMount = false)
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal WorkspaceNode? Parent { get; } = parent;
        internal string Name { get; } = name;
        internal string RelativePath => Parent is null ? string.Empty
            : Parent.Parent is null ? Name : Parent.RelativePath.Length == 0 ? Name : Parent.RelativePath + "/" + Name;
        internal uint Uid { get; set; } = uid;
        internal uint Gid { get; set; } = gid;
        internal bool Directory { get; } = directory;
        internal ushort Mode { get; set; } = mode;
        internal bool Ancestor { get; } = ancestor;
        internal bool AllowMount { get; } = allowMount;
        internal LinuxProtectedMetadata Metadata { get; set; }
    }

    [DllImport("libc", EntryPoint = "fchown", SetLastError = true)] private static extern int Fchown(SafeFileHandle handle, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] private static extern int Fchmod(SafeFileHandle handle, uint mode);
    [DllImport("libc", EntryPoint = "mount", SetLastError = true)]
    private static extern int Mount(string source, string target, string filesystemType, ulong flags, string data);
    [DllImport("libc", EntryPoint = "umount2", SetLastError = true)] private static extern int Umount2(string target, int flags);
}
