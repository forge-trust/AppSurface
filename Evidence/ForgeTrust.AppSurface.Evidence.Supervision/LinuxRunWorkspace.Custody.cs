using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

internal sealed partial class LinuxRunWorkspace
{
    /// <summary>Claims and joins one native root transfer after actual server and worker settlement.</summary>
    /// <remarks>
    /// No pathname, Boolean, JSON receipt or callback supplies custody authority. Repeated callers observe
    /// the original transfer task and token, including failure. Beginning transfer irreversibly closes
    /// live workspace use. Partial transfer preserves paths and accounts for root quarantine.
    /// </remarks>
    internal Task<RootCustody> TakeRootCustodyAsync(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, LinuxWorkerProcess worker, LinuxEmptyObservationControlServer server,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(accounts); ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(server);
        var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RootCustody> execution;
        lock (_gate)
        {
            if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(accounts, _accounts)) throw LinuxWorkspaceLayout.Invalid();
            if (_custodyExecution is not null)
            {
                if (!ReferenceEquals(input, _custodyInput) || !ReferenceEquals(worker, _custodyWorker)
                    || !ReferenceEquals(server, _custodyServer)) throw LinuxWorkspaceLayout.Invalid();
                return _custodyExecution;
            }
            if (_closed || _quarantined) throw LinuxWorkspaceLayout.Invalid();
            _custodyStarted = true;
            _custodyInput = input; _custodyWorker = worker; _custodyServer = server;
            execution = RootCustody.TakeActualAsync(this, input, owner, accounts, worker, server, token, dispatch.Task);
            _custodyExecution = execution;
        }
        dispatch.SetResult();
        return execution;
    }

    /// <summary>Privately issued retained root custody; data parsing and portable procedures cannot construct it.</summary>
    /// <remarks>
    /// All account-associated workspace nodes become root:root under a separate terminal policy, after
    /// whole-tree preflight and physical server/worker joins. Files and names are rechecked before each
    /// read or account release. This holder grants no successful-run claim, admission or accepted proof.
    /// Original owners close before issuance; root handles remain retained for final structural verification.
    /// </remarks>
    internal sealed class RootCustody : IDisposable
    {
        private readonly LinuxRunWorkspace _workspace;
        private readonly EvidenceProtectedLaunchInput _input;
        private readonly LinuxOwnerActivation _owner;
        private readonly LinuxRunAccounts _accounts;
        private readonly LinuxWorkerProcess _worker;
        private readonly LinuxEmptyObservationControlServer _server;
        private readonly List<CustodyNode> _nodes = [];
        private readonly object _sync = new();
        private readonly SupervisionCustodyTransfer _transfer = new();
        private int _issued;
        private bool _closed;
        private bool _failed;
        private Task? _accountClose;
        private readonly AsyncLocal<bool> _insideAccountClose = new();

        private RootCustody(LinuxRunWorkspace workspace, EvidenceProtectedLaunchInput input,
            LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxWorkerProcess worker,
            LinuxEmptyObservationControlServer server)
        { _workspace = workspace; _input = input; _owner = owner; _accounts = accounts; _worker = worker; _server = server; }

        internal static async Task<RootCustody> TakeActualAsync(LinuxRunWorkspace workspace, EvidenceProtectedLaunchInput input,
            LinuxOwnerActivation owner, LinuxRunAccounts accounts, LinuxWorkerProcess worker,
            LinuxEmptyObservationControlServer server, CancellationToken token, Task dispatch)
        {
            await dispatch.ConfigureAwait(false);
            var custody = new RootCustody(workspace, input, owner, accounts, worker, server);
            lock (workspace._gate) workspace._activeCustody = custody;
            try
            {
                LinuxProtectedDeployment.RequirePlatform();
                // The callbacks are fixed native methods on this privately constructed owner. No public
                // producer, configure hook or supplied procedure is imported into root transfer.
                await custody._transfer.RunAsync(custody.ValidateSettlementAsync,
                    () => custody.PreflightAsync(token), () => custody.MutateAsync(token),
                    custody.CloseOriginalOwnersAsync, () => custody.FinalRecheckAsync(token), token).ConfigureAwait(false);
                custody.Check(token);
                Interlocked.Exchange(ref custody._issued, 1);
                return custody;
            }
            catch (Exception error) when (Recoverable(error))
            {
                custody._failed = true;
                workspace.Quarantine();
                try { custody.Dispose(); }
                catch (Exception closeError) when (Recoverable(closeError)) { /* Every retained handle was attempted. */ }
                throw LinuxWorkspaceLayout.Invalid();
            }
        }

        private Task ValidateSettlementAsync()
        {
            _worker.RequireCustodyOwner(_input, _owner, _accounts, _workspace, _server, default);
            _accounts.RequireControlOwnedBy(_owner, default);
            return Task.CompletedTask;
        }

        private Task PreflightAsync(CancellationToken token)
        {
            Check(token);
            var ancestors = _workspace._nodes.Where(static node => node.Policy is null).ToArray();
            if (ancestors.Length != 2 || ancestors[0].Name != "/" || ancestors[1].Name != "run") throw LinuxWorkspaceLayout.Invalid();
            var slash = Add(null, "/", null, ancestors[0].Metadata, DirectoryFlags, 0, token);
            var run = Add(slash, "run", null, ancestors[1].Metadata, DirectoryFlags, ParentResolution, token);
            var generation = AddDirectory(run, _workspace._layout.Name, LinuxCustodyNodeKind.Generation, token);
            var control = AddDirectory(generation, "worker", LinuxCustodyNodeKind.Control, token);
            var broker = AddDirectory(control, "broker", LinuxCustodyNodeKind.Broker, token);
            var output = AddDirectory(generation, "output", LinuxCustodyNodeKind.Output, token);
            var raw = AddDirectory(generation, "raw-results", LinuxCustodyNodeKind.RawResults, token);
            _ = raw;
            var descriptor = Add(control, LinuxWorkspaceLayout.DescriptorName, LinuxCustodyNodeKind.Descriptor,
                _workspace._descriptorMetadata, 0x800 | 0x80000, ChildResolution, token);
            descriptor.Hash = Hash(descriptor, token);
            if (_workspace._descriptorHash is null || descriptor.Hash != _workspace._descriptorHash) throw LinuxWorkspaceLayout.Invalid();
            Add(broker, LinuxControlListenerPolicy.SocketName, LinuxCustodyNodeKind.Socket,
                _server.RequireCustodySocket(token), PathFlags, ChildResolution, token);
            var outputNames = Names(output, token);
            LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Output, outputNames);
            if (outputNames.Count != 0)
            {
                var slot = Add(output, LinuxWorkspaceLayout.ArtifactSlot, LinuxCustodyNodeKind.Slot,
                    null, DirectoryFlags, ChildResolution, token);
                var files = Names(slot, token);
                LinuxCustodyData.RequireInventory(LinuxCustodyNodeKind.Slot, files);
                foreach (var name in files)
                {
                    var kind = name switch
                    {
                        "evidence-plan.json" => LinuxCustodyNodeKind.Plan,
                        "evidence-manifest.json" => LinuxCustodyNodeKind.Manifest,
                        "evidence-summary.json" => LinuxCustodyNodeKind.Summary,
                        _ => throw LinuxWorkspaceLayout.Invalid(),
                    };
                    // Inspect through O_PATH before content open, so special files cannot stall a read.
                    using var probe = OpenAt2(Fd(slot.Handle), name, PathFlags, 0, ChildResolution);
                    var expected = LinuxProtectedMetadata.From(StatFd(probe));
                    LinuxCustodyData.RequireOriginal(kind, expected, _workspace._layout.AccountData);
                    var file = Add(slot, name, kind, expected, 0x800 | 0x80000, ChildResolution, token);
                    file.Hash = Hash(file, token);
                }
            }
            RecheckTree(terminal: false, token);
            Check(token);
            return Task.CompletedTask;
        }

        private CustodyNode AddDirectory(CustodyNode parent, string name, LinuxCustodyNodeKind kind, CancellationToken token)
        {
            var original = _workspace._nodes.Single(node => node.Policy is not null
                && (kind == LinuxCustodyNodeKind.Generation ? node.Policy.ParentName is null : node.Name == name));
            return Add(parent, name, kind, original.Metadata, DirectoryFlags, ChildResolution, token);
        }

        private CustodyNode Add(CustodyNode? parent, string name, LinuxCustodyNodeKind? kind,
            LinuxProtectedMetadata? expected, ulong flags, ulong resolution, CancellationToken token)
        {
            Check(token);
            var handle = OpenAt2(parent is null ? -100 : Fd(parent.Handle), name, flags, 0, resolution);
            var node = new CustodyNode(handle, parent, name, kind);
            _nodes.Add(node); // Own before stat, policy, name or hash failure.
            node.Original = LinuxProtectedMetadata.From(StatFd(handle));
            if (kind is null) LinuxWorkspaceLayout.RequireAncestor(node.Original);
            else LinuxCustodyData.RequireOriginal(kind.Value, node.Original, _workspace._layout.AccountData);
            if (expected is { } baseline && (IsDirectory(node.Original)
                ? !node.Original.SameAncestorAs(baseline) : node.Original != baseline)) throw LinuxWorkspaceLayout.Invalid();
            RequireNamed(node, node.Original);
            Check(token);
            return node;
        }

        private Task MutateAsync(CancellationToken token)
        {
            RecheckTree(terminal: false, token);
            foreach (var node in _nodes.Where(static node => node.Kind.HasValue))
            {
                Check(token);
                RequireNamed(node, node.Original);
                if (LinuxProtectedMetadata.From(StatFd(node.Handle)) != node.Original) throw LinuxWorkspaceLayout.Invalid();
                var permissions = IsDirectory(node.Original) ? 0x1c0u : node.Kind == LinuxCustodyNodeKind.Socket ? 0x180u : 0x100u;
                if (node.Kind == LinuxCustodyNodeKind.Socket)
                {
                    if (CustodyFchownAt(node.Handle, string.Empty, 0, 0, 0x1000) != 0
                        || CustodyFchmodAt2(452, Fd(node.Handle), string.Empty, permissions, 0x1000) != 0)
                        throw LinuxWorkspaceLayout.Invalid();
                }
                else if (Fchown(node.Handle, 0, 0) != 0 || Fchmod(node.Handle, permissions) != 0)
                    throw LinuxWorkspaceLayout.Invalid();
                node.Terminal = LinuxProtectedMetadata.From(StatFd(node.Handle));
                LinuxCustodyData.RequireTerminal(node.Kind!.Value, node.Original, node.Terminal.Value);
                RequireNamed(node, node.Terminal.Value);
                Check(token);
            }
            RecheckTree(terminal: true, token);
            return Task.CompletedTask;
        }

        private async Task CloseOriginalOwnersAsync()
        {
            var failed = false;
            try { await _worker.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { failed = true; }
            try { _workspace.CloseOriginalForCustody(this); }
            catch (Exception error) when (Recoverable(error)) { failed = true; }
            if (failed) throw LinuxWorkspaceLayout.Invalid();
        }

        private Task FinalRecheckAsync(CancellationToken token)
        {
            _worker.RequireCustodyOwner(_input, _owner, _accounts, _workspace, _server, token);
            RecheckTree(terminal: true, token);
            Check(token);
            return Task.CompletedTask;
        }

        /// <summary>Rechecks this actual issued holder before the exact account reservation may be released.</summary>
        /// <remarks>Private native issuance, original references and complete root ownership remain required.</remarks>
        internal void RequireAccountRelease(LinuxRunAccounts accounts, LinuxOwnerActivation owner,
            LinuxWorkerProcess worker, CancellationToken token)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(accounts, _accounts) || !ReferenceEquals(owner, _owner)
                    || !ReferenceEquals(worker, _worker) || Volatile.Read(ref _issued) == 0
                    || !_transfer.SuccessfulCompleted) throw LinuxWorkspaceLayout.Invalid();
                try
                {
                    _accounts.RequireWorkspaceCustody(_workspace);
                    _worker.RequireCustodyOwner(_input, _owner, _accounts, _workspace, _server, token);
                    RecheckTree(terminal: true, token);
                    Check(token);
                }
                catch (Exception error) when (Recoverable(error))
                { _failed = true; _workspace.Quarantine(); throw LinuxWorkspaceLayout.Invalid(); }
            }
        }

        /// <summary>Retains this actual root tree throughout strict account deletion and final native reinspection.</summary>
        /// <remarks>
        /// The original task is reserved before account I/O. Disposal cannot close root handles while it is
        /// pending. Repeated callers join the first token and task; failed cleanup cannot become successful.
        /// Account deletion does not itself grant a manifest claim or accepted consumer proof.
        /// </remarks>
        internal Task CloseAccountsAsync(CancellationToken token)
        {
            var dispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task execution;
            lock (_sync)
            {
                if (_accountClose is not null) return _accountClose;
                Check(token);
                execution = CloseAccountsCoreAsync(dispatch.Task, token);
                _accountClose = execution;
            }
            dispatch.SetResult();
            return execution;
        }

        private async Task CloseAccountsCoreAsync(Task dispatch, CancellationToken token)
        {
            await dispatch.ConfigureAwait(false);
            var failed = false;
            try
            {
                RequireAccountRelease(_accounts, _owner, _worker, token);
                _insideAccountClose.Value = true;
                await _accounts.CloseUnderCustodyAsync(this, token).ConfigureAwait(false);
            }
            catch (Exception error) when (Recoverable(error)) { failed = true; }
            finally { _insideAccountClose.Value = false; }
            try { RequireAccountRelease(_accounts, _owner, _worker, token); }
            catch (Exception error) when (Recoverable(error)) { failed = true; }
            if (failed)
            {
                lock (_sync) _failed = true;
                _workspace.Quarantine();
                throw LinuxWorkspaceLayout.Invalid();
            }
        }

        /// <summary>Requires the private native account-close procedure's retained dispatch, not possession of metadata.</summary>
        /// <remarks>No injected callback receives this execution context; only the fixed account owner may use it.</remarks>
        internal void RequireAccountCleanupDispatch(LinuxRunAccounts accounts, CancellationToken token)
        {
            lock (_sync)
                if (!ReferenceEquals(accounts, _accounts) || !_insideAccountClose.Value
                    || _accountClose is not { IsCompleted: false }) throw LinuxWorkspaceLayout.Invalid();
            RequireAccountRelease(accounts, _owner, _worker, token);
        }

        /// <summary>Reads only a fixed retained root evidence file; bytes and JSON grant no authority.</summary>
        internal byte[] ReadFile(LinuxCustodyNodeKind kind, CancellationToken token)
        {
            lock (_sync)
            {
                if (kind is not (LinuxCustodyNodeKind.Plan or LinuxCustodyNodeKind.Manifest or LinuxCustodyNodeKind.Summary)
                    || Volatile.Read(ref _issued) == 0) throw LinuxWorkspaceLayout.Invalid();
                try
                {
                    RecheckTree(terminal: true, token);
                    var node = _nodes.SingleOrDefault(node => node.Kind == kind) ?? throw LinuxWorkspaceLayout.Invalid();
                    var expected = node.Terminal ?? throw LinuxWorkspaceLayout.Invalid();
                    var maximum = kind == LinuxCustodyNodeKind.Summary ? EvidenceEmptyObservationFiles.MaximumSummaryBytes
                        : EvidenceCanonicalJson.MaximumInputBytes;
                    if (expected.Length == 0 || expected.Length > (ulong)maximum) throw LinuxWorkspaceLayout.Invalid();
                    var bytes = new byte[checked((int)expected.Length)];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        Check(token);
                        var count = RandomAccess.Read(node.Handle, bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)), offset);
                        if (count == 0) throw LinuxWorkspaceLayout.Invalid();
                        offset += count;
                    }
                    if (RandomAccess.Read(node.Handle, new byte[1], offset) != 0
                        || Convert.ToHexStringLower(SHA256.HashData(bytes)) != node.Hash) throw LinuxWorkspaceLayout.Invalid();
                    RecheckTree(terminal: true, token);
                    return bytes;
                }
                catch (Exception error) when (Recoverable(error))
                { _failed = true; _workspace.Quarantine(); throw LinuxWorkspaceLayout.Invalid(); }
            }
        }

        /// <summary>Verifies all three final files only after genuine protocol completion and normal physical worker exit.</summary>
        /// <remarks>
        /// The expected plan is resolved again from the original retained protected inputs. Pure file verification
        /// grants no admission or proof. Account deletion, every retained close and a final deadline check still
        /// must succeed before the run owner may return this detached manifest.
        /// </remarks>
        internal EvidenceManifest VerifyObservationFiles(CancellationToken token)
        {
            lock (_sync)
            {
                try
                {
                    if (Volatile.Read(ref _issued) == 0 || !_transfer.SuccessfulCompleted) throw LinuxWorkspaceLayout.Invalid();
                    _server.RequireSuccessfulCompletion(token);
                    _worker.RequireSuccessfulCompletion();
                    var expected = EvidenceEmptyObservationPlan.FromInput(_input, token);
                    var manifest = EvidenceEmptyObservationFiles.Verify(expected,
                        ReadFile(LinuxCustodyNodeKind.Plan, token), ReadFile(LinuxCustodyNodeKind.Manifest, token),
                        ReadFile(LinuxCustodyNodeKind.Summary, token), token);
                    _server.RequireSuccessfulCompletion(token);
                    _worker.RequireSuccessfulCompletion();
                    RecheckTree(terminal: true, token);
                    Check(token);
                    return manifest;
                }
                catch (Exception error) when (Recoverable(error))
                { _failed = true; _workspace.Quarantine(); throw LinuxWorkspaceLayout.Invalid(); }
            }
        }

        private void RecheckTree(bool terminal, CancellationToken token)
        {
            Check(token);
            foreach (var node in _nodes)
            {
                var expected = terminal && node.Kind.HasValue
                    ? node.Terminal ?? throw LinuxWorkspaceLayout.Invalid() : node.Original;
                var value = LinuxProtectedMetadata.From(StatFd(node.Handle));
                if (node.Kind is null ? !value.SameAncestorAs(expected) : value != expected) throw LinuxWorkspaceLayout.Invalid();
                RequireNamed(node, expected);
                if (node.Kind.HasValue && IsDirectory(expected))
                {
                    var names = Names(node, token);
                    LinuxCustodyData.RequireInventory(node.Kind.Value, names);
                    if (!names.Order(StringComparer.Ordinal).SequenceEqual(_nodes.Where(child => ReferenceEquals(child.Parent, node))
                        .Select(static child => child.Name).Order(StringComparer.Ordinal))) throw LinuxWorkspaceLayout.Invalid();
                }
                else if (node.Hash is not null && Hash(node, token) != node.Hash) throw LinuxWorkspaceLayout.Invalid();
                Check(token);
            }
        }

        private IReadOnlyList<string> Names(CustodyNode node, CancellationToken token)
        {
            // A new open-file description has an independent getdents cursor for each complete pass.
            using var directory = OpenAt2(Fd(node.Handle), ".", DirectoryFlags, 0, ChildResolution);
            if (LinuxProtectedMetadata.From(StatFd(directory)) != (node.Terminal ?? node.Original)) throw LinuxWorkspaceLayout.Invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var buffer = new byte[4096];
            while (true)
            {
                Check(token);
                var count = ReadDirectory(directory, buffer);
                if (count == 0) break;
                foreach (var name in LinuxDirectoryData.Parse(buffer.AsSpan(0, count)))
                    if (names.Count >= 3 || !names.Add(name)) throw LinuxWorkspaceLayout.Invalid();
            }
            var expected = node.Terminal ?? node.Original;
            if (LinuxProtectedMetadata.From(StatFd(directory)) != expected) throw LinuxWorkspaceLayout.Invalid();
            RequireNamed(node, expected);
            Check(token);
            return names.Order(StringComparer.Ordinal).ToArray();
        }

        private string Hash(CustodyNode node, CancellationToken token)
        {
            var expected = node.Terminal ?? node.Original;
            if (LinuxProtectedMetadata.From(StatFd(node.Handle)) != expected) throw LinuxWorkspaceLayout.Invalid();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[4096];
            long offset = 0;
            while ((ulong)offset < expected.Length)
            {
                Check(token);
                var count = RandomAccess.Read(node.Handle, buffer.AsSpan(0, (int)Math.Min((ulong)buffer.Length, expected.Length - (ulong)offset)), offset);
                if (count == 0) throw LinuxWorkspaceLayout.Invalid();
                hash.AppendData(buffer.AsSpan(0, count)); offset += count;
            }
            if (RandomAccess.Read(node.Handle, buffer.AsSpan(0, 1), offset) != 0
                || LinuxProtectedMetadata.From(StatFd(node.Handle)) != expected) throw LinuxWorkspaceLayout.Invalid();
            RequireNamed(node, expected); Check(token);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        private void Check(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_closed || _failed) throw LinuxWorkspaceLayout.Invalid();
            _owner.RequireControlIdentity(token);
        }

        private static bool IsDirectory(LinuxProtectedMetadata value) => (value.Mode & 0xf000) == 0x4000;

        private static void RequireNamed(CustodyNode node, LinuxProtectedMetadata expected)
        {
            using var named = OpenAt2(node.Parent is null ? -100 : Fd(node.Parent.Handle), node.Name,
                PathFlags, 0, node.Parent is null ? 0 : node.Kind is null ? ParentResolution : ChildResolution);
            var value = LinuxProtectedMetadata.From(StatFd(named));
            if (node.Kind is null ? !value.SameAncestorAs(expected) : value != expected) throw LinuxWorkspaceLayout.Invalid();
        }

        /// <summary>Closes every root-retained handle independently; never deletes paths or releases account ownership.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_closed) return;
                if (_accountClose is { IsCompleted: false }) throw LinuxWorkspaceLayout.Invalid();
                _closed = true;
                if (!Close(_nodes.Select(static node => node.Handle)))
                { _failed = true; _workspace.Quarantine(); throw LinuxWorkspaceLayout.Invalid(); }
            }
        }

        private sealed class CustodyNode(SafeFileHandle handle, CustodyNode? parent, string name, LinuxCustodyNodeKind? kind)
        {
            internal SafeFileHandle Handle { get; } = handle;
            internal CustodyNode? Parent { get; } = parent;
            internal string Name { get; } = name;
            internal LinuxCustodyNodeKind? Kind { get; } = kind;
            internal LinuxProtectedMetadata Original { get; set; }
            internal LinuxProtectedMetadata? Terminal { get; set; }
            internal string? Hash { get; set; }
        }

        [DllImport("libc", EntryPoint = "fchownat", SetLastError = true)]
        private static extern int CustodyFchownAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            uint uid, uint gid, int flags);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        private static extern long CustodyFchmodAt2(long number, int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            uint mode, int flags);
    }
}
