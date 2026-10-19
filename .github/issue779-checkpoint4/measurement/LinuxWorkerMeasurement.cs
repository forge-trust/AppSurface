using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Private C3 measurement fixture, compiled out of every ordinary product image.</summary>
/// <remarks>
/// Retains the authentic worker's private temporary directory before descriptor publication. On its
/// original normal exit, copies only genuine pinned Coverlet hit bytes before the root tracker flushes.
/// It grants no process, artifact, admission or proof authority. Root/worker policies and deadlines do
/// not change. A prior root hit file is left unchanged, so only the first normal worker contributes;
/// subsequent root processes accumulate their own counters through Coverlet's original implementation.
/// </remarks>
internal sealed partial class LinuxWorkerMeasurement : IDisposable
{
    internal static readonly string[] Modules = ["ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Evidence.Contracts",
        "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Evidence.Aspire", "ForgeTrust.AppSurface.Evidence.Supervision"];
    private const ulong DirectoryFlags = 0x10000 | 0x80000;
    private const ulong FileFlags = 0x80000 | 0x20000 | 0x800;
    private const int MaximumHits = 4 * 1024 * 1024;
    private readonly LinuxProcessIdentity _worker;
    private readonly SafeFileHandle _temporary;
    private readonly SafeFileHandle _hostTemporary;
    private readonly SafeFileHandle _notification;
    private readonly TrackerPlan[] _plans;
    private bool _copied;

    private LinuxWorkerMeasurement(LinuxProcessIdentity worker, SafeFileHandle temporary,
        SafeFileHandle hostTemporary, SafeFileHandle notification, TrackerPlan[] plans)
    { _worker = worker; _temporary = temporary; _hostTemporary = hostTemporary; _notification = notification; _plans = plans; }

    /// <summary>Returns null for an uninstrumented C3 image; otherwise pins actual live kernel identity.</summary>
    internal static LinuxWorkerMeasurement? TryCapture(LinuxProcessIdentity worker, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new IOException();
        worker.Recheck(token);
        if (worker.Role != LinuxProcessSamplingRole.Worker) throw new IOException();
        var own = typeof(LinuxWorkerMeasurement).Assembly.Location;
        if (TrackerPlan.Read(own) is null) return null;
        var directory = Path.GetDirectoryName(own)!;
        var plans = Modules.Select(name => TrackerPlan.Read(Path.Join(directory, name + ".dll")) ?? throw new IOException()).ToArray();
        SafeFileHandle? notification = null, temporary = null, host = null;
        try
        {
            worker.Recheck(token);
            var pidfd = PidFdOpen(434, checked((int)worker.Pid), 0);
            if (pidfd < 0) throw new IOException();
            notification = new((nint)pidfd, ownsHandle: true);
            if (Exited(notification)) throw new IOException();
            // /proc/<pid>/root is deliberately a kernel magic link; identity is pinned around this open.
            var fd = Open("/proc/" + worker.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/root/tmp", (int)DirectoryFlags);
            if (fd < 0) throw new IOException();
            temporary = new((nint)fd, ownsHandle: true);
            host = OpenAt2(-100, "/tmp", DirectoryFlags, 0, 0x02 | 0x04);
            var first = Metadata(temporary); var second = Metadata(host);
            if (first.Uid != 0 || first.Gid != 0 || (first.Mode & 0xf000) != 0x4000 || first.Inode == 0
                || first.Inode == second.Inode && first.DeviceMajor == second.DeviceMajor && first.DeviceMinor == second.DeviceMinor) throw new IOException();
            worker.Recheck(token);
            if (Exited(notification)) throw new IOException();
            return new(worker, temporary, host, notification, plans);
        }
        catch
        {
            try { temporary?.Dispose(); }
            finally { try { host?.Dispose(); } finally { notification?.Dispose(); } }
            throw;
        }
    }

    /// <summary>Copies unchanged bytes only after retained systemd execution facts and original pidfd exit.</summary>
    internal void CaptureNormalExit(LinuxUnitProperties terminal, CancellationToken token) => CaptureExit(terminal, 0, token);

    /// <summary>Measures only the private SDK control's natural status1; no execution-success condition changes.</summary>
    internal void CaptureFailedNormalExit(LinuxUnitProperties terminal, CancellationToken token)
    {
#if EVIDENCE_PRIVATE_C3_SDK
        if (_exitCache is not null) { CommitFailedExitCache(terminal, token); return; }
#endif
        CaptureExit(terminal, 1, token);
    }

    private void CaptureExit(LinuxUnitProperties terminal, int expectedStatus, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new IOException();
        token.ThrowIfCancellationRequested();
        if (_copied || terminal.ExecMainPid != _worker.Pid || terminal.MainPid != 0
            || terminal.ExecMainCode != 1 || terminal.ExecMainStatus != expectedStatus || !Exited(_notification)) throw new IOException();
        _copied = true;
        var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(5));
        var rows = new List<object>();
        foreach (var plan in _plans)
        {
            deadline.Check(token);
            var name = Path.GetFileName(plan.Path);
            using var prior = OpenOptional(_hostTemporary, name);
            if (prior is not null)
            {
                _ = ReadHits(prior, plan, 0, 0, deadline, token);
                rows.Add(new { module = plan.Module, outcome = "prior-root-file-unchanged", module_sha256 = plan.ModuleSha256 });
                continue;
            }
            using var hit = OpenOptional(_temporary, name);
            if (hit is null)
            {
                rows.Add(new { module = plan.Module, outcome = "worker-file-absent", module_sha256 = plan.ModuleSha256 });
                continue; // A module not loaded by the worker contributes no fabricated zero counters.
            }
            var bytes = ReadHits(hit, plan, _worker.Uid, _worker.Gid, deadline, token);
            using (var target = OpenAt2(Fd(_hostTemporary), name, 0x01 | 0x40 | 0x80 | 0x80000 | 0x20000, 0x180, 0x01 | 0x02 | 0x04 | 0x08))
            {
                using var stream = new FileStream(target, FileAccess.Write);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            using var copied = OpenOptional(_hostTemporary, name) ?? throw new IOException();
            var actual = ReadHits(copied, plan, 0, 0, deadline, token);
            if (!bytes.AsSpan().SequenceEqual(actual)) throw new IOException();
            rows.Add(new { module = plan.Module, outcome = "unchanged-worker-handoff", candidates = plan.Candidates,
                worker_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), host_sha256 = Convert.ToHexStringLower(SHA256.HashData(actual)), module_sha256 = plan.ModuleSha256 });
        }
        deadline.Check(token);
        if (expectedStatus == 0)
            Console.Error.WriteLine(JsonSerializer.Serialize(new { schema = "evidence-worker-measurement-v1", pid = _worker.Pid,
                start_time = _worker.StartTimeTicks, uid = _worker.Uid, gid = _worker.Gid, cgroup = _worker.ControlGroup,
                original_pidfd_exit = true, modules = rows, native_acceptance = false }));
        else
            Console.Error.WriteLine(JsonSerializer.Serialize(new { schema = "evidence-worker-measurement-v2", pid = _worker.Pid,
                start_time = _worker.StartTimeTicks, uid = _worker.Uid, gid = _worker.Gid, cgroup = _worker.ControlGroup,
                original_pidfd_exit = true, normal_exit_code = expectedStatus, modules = rows, native_acceptance = false }));
    }

    /// <summary>Closes measurement-only handles after the original monitor joins, attempting every close.</summary>
    public void Dispose()
    {
        try
        {
#if EVIDENCE_PRIVATE_C3_SDK
            CloseExitBarrier();
#endif
        }
        finally
        {
            try { _temporary.Dispose(); }
            finally { try { _hostTemporary.Dispose(); } finally { _notification.Dispose(); } }
        }
    }

    private static SafeFileHandle? OpenOptional(SafeFileHandle directory, string name)
    {
        try { return OpenAt2(Fd(directory), name, FileFlags, 0, 0x01 | 0x02 | 0x04 | 0x08); }
        catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }) { return null; }
    }

    private static byte[] ReadHits(SafeFileHandle handle, TrackerPlan plan, uint uid, uint gid,
        LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        var before = Metadata(handle);
        if (before.Uid != uid || before.Gid != gid || before.LinkCount != 1 || before.Inode == 0
            || before.Mode is not (0x8180 or 0x81a4) || before.Length != (ulong)(4 * (plan.Candidates + 1))) throw new IOException();
        var data = new byte[checked((int)before.Length)];
        var offset = 0;
        while (offset < data.Length)
        {
            deadline.Check(token);
            var count = RandomAccess.Read(handle, data.AsSpan(offset, Math.Min(8192, data.Length - offset)), offset);
            if (count == 0) throw new IOException();
            offset += count;
        }
        if (before != Metadata(handle) || BinaryPrimitives.ReadInt32LittleEndian(data) != plan.Candidates) throw new IOException();
        for (offset = 4; offset < data.Length; offset += 4)
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) < 0) throw new IOException();
        deadline.Check(token);
        return data;
    }

    private static LinuxProtectedMetadata Metadata(SafeFileHandle handle) => LinuxProtectedMetadata.From(StatFd(handle));
    private static bool Exited(SafeFileHandle handle)
    {
        var value = new PollFd { Fd = Fd(handle), Events = 1 };
        var count = Poll(ref value, 1, 0);
        if (count < 0 || (value.Returned & (8 | 32)) != 0) throw new IOException();
        return count == 1 && (value.Returned & 1) != 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PollFd { internal int Fd; internal short Events; internal short Returned; }
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollFd value, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern int PidFdOpen(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed record TrackerPlan(string Module, string Path, int Candidates, string ModuleSha256)
    {
        internal static TrackerPlan? Read(string module)
        {
            using var file = File.OpenRead(module);
            if (file.Length is < 1 or > 32 * 1024 * 1024) throw new IOException();
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            using var image = new PEReader(new MemoryStream(bytes, writable: false));
            var reader = image.GetMetadataReader();
            var trackers = reader.TypeDefinitions.Where(handle => reader.GetString(reader.GetTypeDefinition(handle).Namespace)
                == "Coverlet.Core.Instrumentation.Tracker").ToArray();
            if (trackers.Length == 0) return null;
            if (trackers.Length != 1) throw new IOException();
            var type = reader.GetTypeDefinition(trackers[0]); var name = reader.GetString(type.Name);
            var prefix = System.IO.Path.GetFileNameWithoutExtension(module) + "_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "D", out var id)
                || name[prefix.Length..] != id.ToString("D")) throw new IOException();
            var constructors = type.GetMethods().Select(reader.GetMethodDefinition).Where(method => reader.GetString(method.Name) == ".cctor").ToArray();
            if (constructors.Length != 1) throw new IOException();
            var il = image.GetMethodBody(constructors[0].RelativeVirtualAddress).GetILBytes() ?? throw new IOException();
            if (il.Length < 38) throw new IOException(); var tail = il.AsSpan(il.Length - 38);
            if (tail[0] != 0x20 || tail[5] != 0x8d || tail[10] != 0x80 || tail[15] != 0x72 || tail[20] != 0x80
                || tail[25] != 0x16 || tail[26] != 0x80 || tail[31] != 0x17 || tail[32] != 0x80 || tail[37] != 0x2a) throw new IOException();
            var count = BinaryPrimitives.ReadInt32LittleEndian(tail[1..]);
            if (count is < 1 || count > MaximumHits / 4 - 1) throw new IOException();
            var element = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(tail[6..]));
            if (element.Kind != HandleKind.TypeReference) throw new IOException();
            var elementType = reader.GetTypeReference((TypeReferenceHandle)element);
            if (reader.GetString(elementType.Namespace) != "System" || reader.GetString(elementType.Name) != "Int32"
                || elementType.ResolutionScope.Kind != HandleKind.AssemblyReference) throw new IOException();
            var core = reader.GetAssemblyReference((AssemblyReferenceHandle)elementType.ResolutionScope);
            if (reader.GetString(core.Name) is not ("System.Runtime" or "System.Private.CoreLib")) throw new IOException();
            string Field(int offset, byte[] signature)
            {
                var handle = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(il.Length - 38 + offset)));
                if (handle.Kind != HandleKind.FieldDefinition) throw new IOException();
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                if (field.GetDeclaringType() != trackers[0] || (field.Attributes & System.Reflection.FieldAttributes.Static) == 0
                    || !reader.GetBlobBytes(field.Signature).AsSpan().SequenceEqual(signature)) throw new IOException();
                return reader.GetString(field.Name);
            }
            if (Field(11, [0x06, 0x1d, 0x08]) != "HitsArray" || Field(21, [0x06, 0x0e]) != "HitsFilePath"
                || Field(27, [0x06, 0x02]) != "SingleHit" || Field(33, [0x06, 0x02]) != "FlushHitFile") throw new IOException();
            var text = BinaryPrimitives.ReadInt32LittleEndian(tail[16..]);
            if ((text & unchecked((int)0xff000000)) != 0x70000000) throw new IOException();
            var path = reader.GetUserString(MetadataTokens.UserStringHandle(text & 0xffffff));
            if (path != "/tmp/" + name) throw new IOException();
            return new(System.IO.Path.GetFileName(module), path, count, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }
}
