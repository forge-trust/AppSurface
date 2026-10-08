using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

// PRIVATE FULL-IMAGE N06 VARIANT ONLY. Do not include this template in the ordinary build.
internal sealed partial class LinuxRunWorkspace
{
    private const string NegativeTargetName = "symlink-target";
    private const string NegativeSentinelName = "target-sentinel";
    private readonly SupervisionSingleAttempt _negativeSymlinkCreation = new();
    private static ReadOnlySpan<byte> NegativeSentinelBytes => "issue779-n06-symlink-target-v1\n"u8;

    /// <summary>Creates the fixed N06 symlink collision under this actual workspace, before listener/worker creation.</summary>
    /// <param name="owner">The reference-equal actual owner already retained by this workspace.</param>
    /// <param name="accounts">The reference-equal actual account holder; no identity data substitutes for it.</param>
    /// <param name="token">Original work cancellation; every native stage checks the original owner deadline.</param>
    /// <returns>A privately constructed FD owner for subsequent unchanged-object comparison, not artifact custody.</returns>
    /// <remarks>
    /// The call is fixed at WorkspaceCreate in the private full-image variant. There is no path, case, UID,
    /// environment or callback selector. One attempt is claimed before mutation. Existing names reject and
    /// are never adopted, removed or chmodded. Partial setup quarantines the workspace and preserves paths
    /// and accounts. This does not publish a descriptor, activate a writer or issue admission/custody.
    /// </remarks>
    internal SymlinkSlotInspection PrepareSymlinkSlotNegative(LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, CancellationToken token)
        => SymlinkSlotInspection.PrepareActual(this, owner, accounts, token);

    // Fixed private native ABI; requires the existing Linux x64/root platform guard. No fallback.
    [LibraryImport("libc", EntryPoint = "symlinkat", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int N06SymlinkAt(string target, SafeFileHandle directory, string name);

    [DllImport("libc", EntryPoint = "fchownat", SetLastError = true)]
    private static extern int N06ChownLink(SafeFileHandle descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint uid, uint gid, int flags);

    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static extern nint N06ReadLink(SafeFileHandle descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer, nuint size);

    private static IOException NegativeNativeFailure()
    {
        var errno = Marshal.GetLastPInvokeError(); // Capture immediately after the failed native call.
        return new IOException("Fixed N06 native operation failed.",
            new System.ComponentModel.Win32Exception(errno));
    }

    private static void RequireNegativeLink(LinuxProtectedMetadata value, uint uid, uint gid)
    {
        if (value.Inode == 0 || value.Uid != uid || value.Gid != gid || value.Mode != 0xa1ff
            || value.LinkCount != 1 || value.Length != (ulong)Encoding.UTF8.GetByteCount(NegativeTargetName))
            throw LinuxWorkspaceLayout.Invalid();
    }

    private static void RequireNegativeLinkBytes(SafeFileHandle link)
    {
        var expected = Encoding.UTF8.GetBytes(NegativeTargetName);
        var buffer = new byte[expected.Length + 1]; // Extra byte rejects a longer target, never truncates it into a match.
        var count = N06ReadLink(link, string.Empty, buffer, (nuint)buffer.Length);
        if (count < 0) throw NegativeNativeFailure();
        if (count != expected.Length || !buffer.AsSpan(0, expected.Length).SequenceEqual(expected))
            throw LinuxWorkspaceLayout.Invalid();
    }

    private static void RequireNegativeLinkNamed(SafeFileHandle parent, LinuxProtectedMetadata expected)
    {
        using var named = OpenAt2(Fd(parent), LinuxWorkspaceLayout.ArtifactSlot,
            PathFlags | 0x20000, 0, ChildResolution); // Final link itself, never its target.
        if (LinuxProtectedMetadata.From(StatFd(named)) != expected) throw LinuxWorkspaceLayout.Invalid();
        RequireNegativeLinkBytes(named);
        if (LinuxProtectedMetadata.From(StatFd(named)) != expected) throw LinuxWorkspaceLayout.Invalid();
    }

    private static void RequireNegativeCreated(LinuxProtectedMetadata value, bool directory)
    {
        var permissions = directory ? 0x1c0 : 0x180;
        if (value.Inode == 0 || value.Uid != 0 || value.Gid != 0
            || (value.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (value.Mode & 0x0fff & ~permissions) != 0
            || (directory ? value.LinkCount != 2 : value.LinkCount != 1 || value.Length != 0))
            throw LinuxWorkspaceLayout.Invalid();
    }

    private static void RequireNegativeOwned(LinuxProtectedMetadata value, bool directory,
        LinuxRunAccountSnapshot accounts)
    {
        if (value.Inode == 0 || value.Uid != accounts.WorkerUid || value.Gid != accounts.WorkerGid
            || value.Mode != (directory ? 0x41c0 : 0x8180)
            || (directory ? value.LinkCount != 2
                : value.LinkCount != 1 || value.Length != (ulong)NegativeSentinelBytes.Length))
            throw LinuxWorkspaceLayout.Invalid();
    }

    /// <summary>Immutable actual setup samples and expected fixed bytes; this data grants no native authority.</summary>
    /// <param name="Parent">Output-parent sample after the intentional target insertion.</param>
    /// <param name="Target">Worker-owned searchable target directory sample after fixed sentinel insertion.</param>
    /// <param name="Sentinel">Single-link regular sentinel sample after write/fsync.</param>
    /// <param name="Link">Worker-owned symlink inode, inspected without following it.</param>
    /// <param name="Sha256">Expected and actually re-read fixed sentinel SHA256.</param>
    internal sealed record SymlinkSlotSnapshot(LinuxProtectedMetadata Parent, LinuxProtectedMetadata Target,
        LinuxProtectedMetadata Sentinel, LinuxProtectedMetadata Link, string Sha256);

    /// <summary>Privately constructed ownership of actual comparison FDs; never an artifact/custody capability.</summary>
    /// <remarks>
    /// The root execution retains this owner until the original worker/server joins and the postjoin check,
    /// and closes it before workspace disposal. Dispose attempts every FD and asserts no process exit,
    /// cleanup or account release. No entry is removed. Caller ordering remains mandatory.
    /// </remarks>
    internal sealed class SymlinkSlotInspection : IDisposable
    {
        private readonly LinuxRunWorkspace _workspace;
        private readonly SafeFileHandle _parent;
        private readonly SafeFileHandle _target;
        private readonly SafeFileHandle _sentinel;
        private readonly SafeFileHandle _link;
        private bool _disposed;
        private bool _setupEvidenceAttempted;
        private bool _setupEvidenceComplete;
        private bool _postJoinEvidenceAttempted;

        private SymlinkSlotInspection(LinuxRunWorkspace workspace, SafeFileHandle parent,
            SafeFileHandle target, SafeFileHandle sentinel, SafeFileHandle link, SymlinkSlotSnapshot data)
        { _workspace = workspace; _parent = parent; _target = target; _sentinel = sentinel; _link = link; PreparedData = data; }

        /// <summary>Acquires only the fixed native objects from actual reference-equal owners.</summary>
        /// <remarks>No path, sampled metadata or supplied FD can construct this private comparison owner.</remarks>
        internal static SymlinkSlotInspection PrepareActual(LinuxRunWorkspace workspace,
            LinuxOwnerActivation owner, LinuxRunAccounts accounts, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            LinuxProtectedDeployment.RequirePlatform();
            lock (workspace._gate)
            {
                SafeFileHandle? parent = null;
                SafeFileHandle? target = null;
                SafeFileHandle? link = null;
                SafeFileHandle? writer = null;
                SafeFileHandle? reader = null;
                var failed = false;
                try
                {
                    if (!ReferenceEquals(owner, workspace._owner) || !ReferenceEquals(accounts, workspace._accounts)
                        || workspace._descriptorAttempted || workspace._descriptor is not null) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    workspace._negativeSymlinkCreation.Claim();
                    workspace.RecheckDirectories(token);
                    var originalParent = workspace._outputParent ?? throw LinuxWorkspaceLayout.Invalid();
                    var generation = originalParent.Parent ?? throw LinuxWorkspaceLayout.Invalid();
                    // Register every newly acquired FD immediately, before inspection or ownership changes.
                    parent = OpenAt2(Fd(generation.Handle), originalParent.Name, DirectoryFlags, 0, ChildResolution);
                    var openedParent = LinuxProtectedMetadata.From(StatFd(parent));
                    LinuxWorkspaceLayout.RequireDirectory(openedParent, workspace._layout.OutputDirectory);
                    if (!openedParent.SameAncestorAs(originalParent.Metadata)) throw LinuxWorkspaceLayout.Invalid();
                    RequireSameNamed(generation.Handle, originalParent.Name, openedParent);
                    workspace.Check(token);
                    if (MkdirAt(Fd(parent), NegativeTargetName, 0x1c0) != 0)
                        throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    target = OpenAt2(Fd(parent), NegativeTargetName, DirectoryFlags, 0, ChildResolution);
                    var createdSlot = LinuxProtectedMetadata.From(StatFd(target));
                    RequireNegativeCreated(createdSlot, directory: true);
                    RequireSameNamed(parent, NegativeTargetName, createdSlot);
                    workspace.Check(token);
                    if (Fchown(target, workspace._layout.AccountData.WorkerUid, workspace._layout.AccountData.WorkerGid) != 0
                        || Fchmod(target, 0x1c0) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    var ownedSlot = LinuxProtectedMetadata.From(StatFd(target));
                    RequireNegativeOwned(ownedSlot, directory: true, workspace._layout.AccountData);
                    RequireSameNamed(parent, NegativeTargetName, ownedSlot);
                    workspace.Check(token);
                    // O_WRONLY|O_CREAT|O_EXCL|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC; fixed child beneath retained FD.
                    writer = OpenAt2(Fd(target), NegativeSentinelName,
                        0x01 | 0x40 | 0x80 | 0x800 | 0x20000 | 0x80000, 0x180, ChildResolution);
                    var createdFile = LinuxProtectedMetadata.From(StatFd(writer));
                    RequireNegativeCreated(createdFile, directory: false);
                    RequireSameNamed(target, NegativeSentinelName, createdFile);
                    workspace.Check(token);
                    if (Fchown(writer, workspace._layout.AccountData.WorkerUid, workspace._layout.AccountData.WorkerGid) != 0
                        || Fchmod(writer, 0x180) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    RandomAccess.Write(writer, NegativeSentinelBytes, 0);
                    workspace.Check(token);
                    if (Fsync(writer) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    var fileMetadata = LinuxProtectedMetadata.From(StatFd(writer));
                    RequireNegativeOwned(fileMetadata, directory: false, workspace._layout.AccountData);
                    RequireSameNamed(target, NegativeSentinelName, fileMetadata);
                    reader = OpenAt2(Fd(target), NegativeSentinelName, 0x800 | 0x20000 | 0x80000, 0, ChildResolution);
                    if (LinuxProtectedMetadata.From(StatFd(reader)) != fileMetadata) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    writer.Dispose(); writer = null;
                    if (Fsync(target) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    if (Fsync(parent) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    // Exclusive fixed relative link creation: never follow, replace or adopt a collision.
                    if (N06SymlinkAt(NegativeTargetName, parent, LinuxWorkspaceLayout.ArtifactSlot) != 0)
                        throw NegativeNativeFailure();
                    workspace.Check(token);
                    link = OpenAt2(Fd(parent), LinuxWorkspaceLayout.ArtifactSlot,
                        PathFlags | 0x20000, 0, ChildResolution); // O_PATH | O_NOFOLLOW | O_CLOEXEC.
                    var createdLink = LinuxProtectedMetadata.From(StatFd(link));
                    RequireNegativeLink(createdLink, 0, 0);
                    RequireNegativeLinkNamed(parent, createdLink);
                    workspace.Check(token);
                    if (N06ChownLink(link, string.Empty, workspace._layout.AccountData.WorkerUid,
                        workspace._layout.AccountData.WorkerGid, 0x1000 | 0x100) != 0)
                        throw NegativeNativeFailure(); // AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW.
                    workspace.Check(token);
                    var ownedLink = LinuxProtectedMetadata.From(StatFd(link));
                    RequireNegativeLink(ownedLink, workspace._layout.AccountData.WorkerUid,
                        workspace._layout.AccountData.WorkerGid);
                    RequireNegativeLinkNamed(parent, ownedLink);
                    RequireNegativeLinkBytes(link);
                    workspace.Check(token);
                    if (Fsync(parent) != 0) throw LinuxWorkspaceLayout.Invalid();
                    workspace.Check(token);
                    var snapshot = new SymlinkSlotSnapshot(LinuxProtectedMetadata.From(StatFd(parent)),
                        LinuxProtectedMetadata.From(StatFd(target)), fileMetadata, ownedLink,
                        Convert.ToHexStringLower(SHA256.HashData(NegativeSentinelBytes)));
                    var result = new SymlinkSlotInspection(workspace, parent, target, reader, link, snapshot);
                    result.Recheck(token, InspectionPurpose.Work);
                    workspace.Check(token);
                    parent = target = reader = link = null; // Ownership transfers only after every check succeeds.
                    return result;
                }
                catch
                {
                    failed = true;
                    workspace._quarantined = true;
                    throw; // Preserve the original setup/cancellation failure.
                }
                finally
                {
                    if (!Close(new[] { parent, target, writer, reader, link }.OfType<SafeFileHandle>()))
                    {
                        workspace._quarantined = true;
                        if (!failed) throw LinuxWorkspaceLayout.Invalid();
                    }
                }
            }
        }

        /// <summary>Gets captured immutable data only; metadata cannot reconstruct this private FD owner.</summary>
        internal SymlinkSlotSnapshot PreparedData { get; }

        /// <summary>Rechecks and writes one fixed bounded private setup line before any worker launch.</summary>
        /// <remarks>Publication failure rejects preparation; it cannot be silently treated as setup success.</remarks>
        internal void WriteSetupEvidence(CancellationToken token)
        {
            lock (_workspace._gate)
            {
                try
                {
                    if (_setupEvidenceAttempted) throw LinuxWorkspaceLayout.Invalid();
                    _setupEvidenceAttempted = true; // Claim before checks or stderr I/O; failed publication cannot retry.
                    Recheck(token, InspectionPurpose.Work);
                    WriteEvidence(postJoin: false);
                    Recheck(token, InspectionPurpose.Work);
                    _setupEvidenceComplete = true;
                }
                catch { _workspace._quarantined = true; throw; }
            }
        }

        /// <summary>Checks unchanged retained objects only after the actual worker/server physical-ownership guard.</summary>
        /// <param name="input">Original actual protected input, not reconstructed metadata.</param>
        /// <param name="worker">Original actual worker holder whose shared StopAndJoin completed.</param>
        /// <param name="server">Original actual server whose handlers and I/O joined.</param>
        /// <param name="token">Original root teardown token; never a replacement cleanup allowance.</param>
        /// <remarks>
        /// Uses the same native pre-custody guard, including pending starts, exact group emptiness and pump
        /// joins. That guard and unchanged samples still issue no filesystem custody or successful run.
        /// A failed guard emits no postjoin-positive line; uncertainty remains failure/quarantine.
        /// </remarks>
        internal void WritePostJoinEvidence(EvidenceProtectedLaunchInput input, LinuxWorkerProcess worker,
            LinuxEmptyObservationControlServer server, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(worker);
            ArgumentNullException.ThrowIfNull(server);
            lock (_workspace._gate)
            {
                if (_disposed || !_setupEvidenceComplete || _postJoinEvidenceAttempted)
                    throw LinuxWorkspaceLayout.Invalid();
                _postJoinEvidenceAttempted = true; // No retry even if the original physical guard fails.
            }
            // Original native guard may perform I/O; no workspace lock is held across its composition.
            LinuxNegativeKernelObservation kernel;
            try { kernel = worker.CaptureNegativeObservation(input, _workspace._owner, _workspace._accounts, _workspace, server, token); }
            catch { _workspace.Quarantine(); throw; }
            lock (_workspace._gate)
            {
                try
                {
                    // Data only; reached only after the original native custody guard above.
                    var projected = N06JoinedWorkerOutput.Parse(worker.Output, token);
                    Recheck(token, InspectionPurpose.Control);
                    WriteEvidence(postJoin: true);
                    Recheck(token, InspectionPurpose.Control);
                    var diagnostic = N06JoinedWorkerOutput.Serialize(projected, token);
                    Console.Error.WriteLine(diagnostic);
                    Console.Error.Flush();
                    Recheck(token, InspectionPurpose.Control);
                    token.ThrowIfCancellationRequested();
                    Console.Error.WriteLine(Encoding.UTF8.GetString(kernel.Bytes));
                    Console.Error.Flush();
                    Recheck(token, InspectionPurpose.Control);
                }
                catch { _workspace._quarantined = true; throw; }
            }
        }

        private void Recheck(CancellationToken token, InspectionPurpose purpose)
        {
            if (_disposed) throw LinuxWorkspaceLayout.Invalid();
            _workspace.RecheckCore(token, purpose);
            var parentNode = _workspace._outputParent ?? throw LinuxWorkspaceLayout.Invalid();
            var generation = parentNode.Parent ?? throw LinuxWorkspaceLayout.Invalid();
            LinuxWorkspaceLayout.RequireDirectory(PreparedData.Parent, _workspace._layout.OutputDirectory);
            if (!PreparedData.Parent.SameAncestorAs(parentNode.Metadata)) throw LinuxWorkspaceLayout.Invalid();
            _workspace.Check(token, purpose);
            if (LinuxProtectedMetadata.From(StatFd(_parent)) != PreparedData.Parent)
                throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(generation.Handle, parentNode.Name, PreparedData.Parent);
            _workspace.Check(token, purpose);
            RequireNegativeOwned(PreparedData.Target, directory: true, _workspace._layout.AccountData);
            if (LinuxProtectedMetadata.From(StatFd(_target)) != PreparedData.Target)
                throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(_parent, NegativeTargetName, PreparedData.Target);
            _workspace.Check(token, purpose);
            RequireNegativeOwned(PreparedData.Sentinel, directory: false, _workspace._layout.AccountData);
            RequireSameNamed(_target, NegativeSentinelName, PreparedData.Sentinel);
            if (_workspace.HashDescriptor(_sentinel, PreparedData.Sentinel, token, purpose) != PreparedData.Sha256)
                throw LinuxWorkspaceLayout.Invalid();
            RequireSameNamed(_target, NegativeSentinelName, PreparedData.Sentinel);
            RequireSameNamed(_parent, NegativeTargetName, PreparedData.Target);
            _workspace.Check(token, purpose);
            RequireNegativeLink(PreparedData.Link, _workspace._layout.AccountData.WorkerUid,
                _workspace._layout.AccountData.WorkerGid);
            if (LinuxProtectedMetadata.From(StatFd(_link)) != PreparedData.Link)
                throw LinuxWorkspaceLayout.Invalid();
            RequireNegativeLinkBytes(_link);
            if (LinuxProtectedMetadata.From(StatFd(_link)) != PreparedData.Link)
                throw LinuxWorkspaceLayout.Invalid();
            RequireNegativeLinkNamed(_parent, PreparedData.Link);
            _workspace.Check(token, purpose);
            RequireSameNamed(generation.Handle, parentNode.Name, PreparedData.Parent);
            if (LinuxProtectedMetadata.From(StatFd(_target)) != PreparedData.Target
                || LinuxProtectedMetadata.From(StatFd(_parent)) != PreparedData.Parent)
                throw LinuxWorkspaceLayout.Invalid();
            _workspace.Check(token, purpose);
        }

        private void WriteEvidence(bool postJoin)
        {
            static object Identity(LinuxProtectedMetadata value) => new
            {
                device_major = value.DeviceMajor, device_minor = value.DeviceMinor, inode = value.Inode,
                uid = value.Uid, gid = value.Gid, mode = value.Mode, links = value.LinkCount, length = value.Length,
            };
            var line = JsonSerializer.Serialize(new
            {
                schema = "issue779-n06-symlink-inspection-v1", phase = postJoin ? "post_join" : "setup",
                parent = Identity(PreparedData.Parent), target = Identity(PreparedData.Target),
                link = Identity(PreparedData.Link),
                target_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NegativeTargetName))),
                sentinel = Identity(PreparedData.Sentinel), sha256 = PreparedData.Sha256,
            });
            if (Encoding.UTF8.GetByteCount(line) + 1 > 1024) throw LinuxWorkspaceLayout.Invalid();
            Console.Error.WriteLine(line); // Existing private root stderr, never a public success summary.
            Console.Error.Flush();
        }

        /// <summary>Attempts all owned closes once, preserves paths, and never claims physical settlement.</summary>
        public void Dispose()
        {
            lock (_workspace._gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (!Close(new[] { _parent, _target, _sentinel, _link }))
                { _workspace._quarantined = true; throw LinuxWorkspaceLayout.Invalid(); }
            }
        }
    }
}

/// <summary>Parses complete joined worker bytes for the fixed private N06 image; grants no native authority.</summary>
/// <remarks>Only the actual retained worker's custody guard may precede production use. Test receipts are data.</remarks>
internal static class N06JoinedWorkerOutput
{
    /// <summary>Maximum complete stderr bytes, comprising two lines each bounded to 1024 bytes including LF.</summary>
    internal const int MaximumBytes = 2048;
    /// <summary>Exact source-selected terminal text; neither arbitrary messages nor prefixes are accepted.</summary>
    internal const string TerminalLine = "ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    { "schema", "phase", "operation", "stageOutcome", "terminalCode", "errorClass", "nativeErrno" };

    /// <summary>Detached worker-reported observations, never a root syscall receipt or proof of activation.</summary>
    /// <param name="WorkerReportedErrno">Actual decoded bounded worker report, or null; never inferred.</param>
    /// <param name="StderrBytes">Complete received stderr count from joined pump observations.</param>
    internal sealed record Projection(int? WorkerReportedErrno, int StderrBytes);

    /// <summary>Requires two successful EOFs, full retention, empty stdout and the exact closed allocation/terminal pair.</summary>
    /// <param name="output">Immutable actual joined output, or data-only receipts in intentionally portable tests.</param>
    /// <param name="token">Original teardown token; no timeout or cancellation source is created.</param>
    /// <returns>Only detached fixed-case data. It cannot authenticate a process, root peer or allocation.</returns>
    /// <exception cref="InvalidOperationException">Fixed rejection without input, inner exception or arbitrary output.</exception>
    internal static Projection Parse(SupervisionOutputReceipt? output, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (output is null || output.Stdout is null || output.Stderr is null
                || output.Stdout.Prefix.IsDefault || output.Stderr.Prefix.IsDefault
                || !output.Successful || output.ReceivedByteLimit <= 0
                || output.ReceivedByteLimit > EvidenceRunBudgetLimits.MaximumProcessOutputBytes
                || output.Stdout.ReceivedBytes != 0 || output.Stdout.Prefix.Length != 0
                || output.Stderr.ReceivedBytes <= 0 || output.Stderr.ReceivedBytes > MaximumBytes
                || output.Stderr.ReceivedBytes != output.Stderr.Prefix.Length
                || output.ReceivedBytes != output.Stderr.ReceivedBytes) throw Rejected();
            var bytes = output.Stderr.Prefix.AsSpan(); // Count checked before copying/decoding.
            if (bytes[^1] != (byte)'\n') throw Rejected();
            var text = StrictUtf8.GetString(bytes);
            var lines = text.Split('\n');
            if (lines.Length != 3 || lines[2].Length != 0 || lines[1] != TerminalLine
                || StrictUtf8.GetByteCount(lines[0]) + 1 > 1024
                || StrictUtf8.GetByteCount(lines[1]) + 1 > 1024) throw Rejected();
            using var document = JsonDocument.Parse(lines[0], new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Rejected();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!Keys.Contains(field.Name) || !seen.Add(field.Name)) throw Rejected();
            if (!seen.SetEquals(Keys)) throw Rejected();
            RequireString(root, "schema", "evidence-allocation-failure-v1");
            RequireString(root, "phase", "Allocation");
            RequireString(root, "operation", "CreateSlot");
            RequireString(root, "stageOutcome", "Failed");
            RequireString(root, "terminalCode", "StageFailed");
            RequireString(root, "errorClass", "Io");
            var errno = root.GetProperty("nativeErrno");
            int? value = null;
            if (errno.ValueKind != JsonValueKind.Null)
            {
                if (errno.ValueKind != JsonValueKind.Number || !errno.TryGetInt32(out var number)
                    || number is < 1 or > 4095) throw Rejected();
                value = number;
            }
            token.ThrowIfCancellationRequested();
            return new(value, checked((int)output.Stderr.ReceivedBytes));
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException
            or InvalidOperationException or ArgumentException or OverflowException)
        { throw Rejected(); }
    }

    /// <summary>Serializes only closed worker-reported values, at most 1024 UTF-8 bytes including the caller's LF.</summary>
    /// <remarks>Requires the original token. The native caller rechecks custody/files around actual write and flush.</remarks>
    /// <param name="value">Detached parser data; constructing it in tests creates no execution capability.</param>
    /// <param name="token">Original cleanup token, never renewed.</param>
    /// <returns>Fixed private JSON with native authority explicitly false and no raw output.</returns>
    internal static string Serialize(Projection value, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (value is null || value.StderrBytes is < 1 or > MaximumBytes
            || value.WorkerReportedErrno is < 1 or > 4095) throw Rejected();
        var line = JsonSerializer.Serialize(new
        {
            schema = "issue779-n06-joined-worker-output-v1", origin = "joined-worker-output",
            allocation = new { schema = "evidence-allocation-failure-v1", phase = "Allocation",
                operation = "CreateSlot", stageOutcome = "Failed", terminalCode = "StageFailed",
                errorClass = "Io", nativeErrno = value.WorkerReportedErrno },
            terminal_diagnostic = "ASEVD409", stdout_bytes = 0, stderr_bytes = value.StderrBytes,
            native_authority = false,
        });
        if (StrictUtf8.GetByteCount(line) + 1 > 1024) throw Rejected();
        token.ThrowIfCancellationRequested();
        return line;
    }

    private static void RequireString(JsonElement root, string name, string expected)
    {
        var field = root.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String || field.GetString() != expected) throw Rejected();
    }

    private static InvalidOperationException Rejected() => new("ASEVD410: N06 joined output diagnostic rejected.");
}
