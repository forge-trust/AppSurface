#if !EVIDENCE_PRIVATE_C3_SDK
#error This measurement bridge belongs only to the private SDK image.
#endif
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using Microsoft.Win32.SafeHandles;
using static ForgeTrust.AppSurface.Evidence.Contracts.EvidenceLinuxFileSystem;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Private measurement cache serviced by the original monitor, never a second process owner.</summary>
/// <remarks>
/// The support library's final exit delegate publishes READY after pinned Coverlet delegates return.
/// While the original pidfd remains live this holder caches unchanged, source-bound hit bytes and
/// publishes ACK. Only the original terminal code1/status1 and pidfd exit permit host publication.
/// This is a pre-exit snapshot after original handlers, not a complete terminal counter snapshot.
/// </remarks>
internal sealed partial class LinuxWorkerMeasurement
{
    private const ulong Beneath = 0x01 | 0x02 | 0x04 | 0x08;
    private SafeFileHandle? _exitSlot, _exitWorker;
    private LinuxProtectedMetadata _exitSlotIdentity, _exitWorkerIdentity;
    private string? _exitSlotName, _exitNonce;
    private DateTimeOffset _exitOriginalDeadline;
    private List<ExitCachedHits>? _exitCache;
    private bool _exitAcknowledged, _exitReleased;
    private sealed record ExitCachedHits(TrackerPlan Plan, byte[]? Bytes);

    /// <summary>Publishes root-owned plan data before descriptor publication, under the original job deadline.</summary>
    internal void PrepareFailedExitBarrier(DateTimeOffset originalDeadline, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || _exitSlot is not null || _exitSlotName is not null
            || originalDeadline <= DateTimeOffset.UtcNow) throw new IOException();
        _worker.Recheck(token);
        if (Exited(_notification)) throw new IOException();
        _exitOriginalDeadline = originalDeadline;
        _exitNonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _exitSlotName = "appsurface-sdk-exit-" + _worker.Pid.ToString(CultureInfo.InvariantCulture);
        if (MkdirAt(Fd(_temporary), _exitSlotName, 0x1c0) != 0) throw new IOException();
        _exitSlot = OpenAt2(Fd(_temporary), _exitSlotName, DirectoryFlags, 0, Beneath);
        if (BarrierChown(_exitSlot, 0, 0) != 0 || BarrierChmod(_exitSlot, 0x1c0) != 0) throw new IOException();
        if (MkdirAt(Fd(_exitSlot), "worker", 0x1c0) != 0) throw new IOException();
        _exitWorker = OpenAt2(Fd(_exitSlot), "worker", DirectoryFlags, 0, Beneath);
        if (BarrierChown(_exitWorker, _worker.Uid, _worker.Gid) != 0
            || BarrierChmod(_exitWorker, 0x1c0) != 0) throw new IOException();
        var plan = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "evidence-sdk-exit-plan-v1", nonce = _exitNonce, pid = _worker.Pid,
            uid = _worker.Uid, gid = _worker.Gid, start_time = _worker.StartTimeTicks,
            cgroup = _worker.ControlGroup, deadline_utc = originalDeadline.ToString("O", CultureInfo.InvariantCulture)
        });
        if (plan.Length is < 1 or > 2048) throw new IOException();
        WriteBarrierLeaf(_exitSlot, "plan.json", plan, 0x124);
        if (BarrierChmod(_exitSlot, 0x1ed) != 0) throw new IOException(); // 0755, root cannot be renamed by the worker.
        _exitSlotIdentity = Metadata(_exitSlot);
        _exitWorkerIdentity = Metadata(_exitWorker);
        CheckExitSlot(token);
        _worker.Recheck(token);
        if (Exited(_notification)) throw new IOException();
    }

    /// <summary>One synchronous bounded observation inside the already retained original exit-monitor task.</summary>
    internal void ObserveFailedExitBarrier(CancellationToken token)
    {
        if (_exitSlot is null || _exitCache is not null || Exited(_notification)) return;
        using var ready = OpenOptional(_exitWorker!, "ready");
        if (ready is null) return;
        var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(5));
        CheckBarrierDeadline(deadline, token);
        CheckExitSlot(token);
        _worker.Recheck(token);
        if (Exited(_notification)) throw new IOException();
        var expected = Encoding.ASCII.GetBytes(_exitNonce!);
        if (!ReadBarrierLeaf(ready, _exitWorker!, "ready", _worker.Uid, _worker.Gid, 0x8180, 32, deadline, token)
            .AsSpan().SequenceEqual(expected)) throw new IOException();
        var cached = new List<ExitCachedHits>();
        foreach (var plan in _plans)
        {
            CheckBarrierDeadline(deadline, token);
            var name = Path.GetFileName(plan.Path);
            using var hit = OpenOptional(_temporary, name);
            if (hit is null)
            {
                if (plan.Module is "ForgeTrust.AppSurface.Cli.dll" or "ForgeTrust.AppSurface.Evidence.Contracts.dll"
                    or "ForgeTrust.AppSurface.Evidence.Coverage.dll" or "ForgeTrust.AppSurface.Evidence.Aspire.dll"
                    or "ForgeTrust.AppSurface.Evidence.Cli.dll") throw new IOException();
                cached.Add(new(plan, null));
                continue;
            }
            var bytes = ReadHits(hit, plan, _worker.Uid, _worker.Gid, deadline, token);
            using var named = OpenOptional(_temporary, name) ?? throw new IOException();
            if (Metadata(hit) != Metadata(named)) throw new IOException();
            cached.Add(new(plan, bytes));
        }
        CheckExitSlot(token);
        _worker.Recheck(token);
        if (Exited(_notification)) throw new IOException();
        CheckBarrierDeadline(deadline, token);
        // Atomic no-replace publication: a polling worker never sees the pending writable file.
        WriteBarrierLeaf(_exitSlot, "ack.pending", expected, 0x124);
        CheckBarrierDeadline(deadline, token);
        CheckExitSlot(token);
        _worker.Recheck(token);
        if (Exited(_notification)) throw new IOException();
        if (BarrierRename(Fd(_exitSlot), "ack.pending", Fd(_exitSlot), "ack", 1) != 0) throw new IOException();
        if (Exited(_notification)) throw new IOException();
        _exitCache = cached;
        _exitAcknowledged = true;
        // ACK alone does not release the worker. Its separate bounded RELEASE wait permits the
        // post-ACK live pidfd observation above without racing a valid immediate process exit.
        CheckBarrierDeadline(deadline, token);
        WriteBarrierLeaf(_exitSlot, "release.pending", expected, 0x124);
        CheckBarrierDeadline(deadline, token);
        if (BarrierRename(Fd(_exitSlot), "release.pending", Fd(_exitSlot), "release", 1) != 0) throw new IOException();
        _exitReleased = true;
        // The worker may now return; no post-RELEASE /proc liveness condition is imposed.
        CheckBarrierDeadline(deadline, token);
    }

    private void CommitFailedExitCache(LinuxUnitProperties terminal, CancellationToken token)
    {
        if (_copied || !_exitAcknowledged || !_exitReleased || _exitCache is null || terminal.ExecMainPid != _worker.Pid
            || terminal.MainPid != 0 || terminal.ExecMainCode != 1 || terminal.ExecMainStatus != 1
            || !Exited(_notification)) throw new IOException();
        _copied = true;
        var deadline = new LinuxInputDeadline(TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(5));
        var rows = new List<object>();
        foreach (var item in _exitCache)
        {
            CheckBarrierDeadline(deadline, token);
            var plan = item.Plan;
            var name = Path.GetFileName(plan.Path);
            using var prior = OpenOptional(_hostTemporary, name);
            if (prior is not null)
            {
                _ = ReadHits(prior, plan, 0, 0, deadline, token);
                rows.Add(new { module = plan.Module, outcome = "prior-root-file-unchanged", module_sha256 = plan.ModuleSha256 });
                continue;
            }
            if (item.Bytes is not { } bytes)
            {
                rows.Add(new { module = plan.Module, outcome = "worker-file-absent", module_sha256 = plan.ModuleSha256 });
                continue;
            }
            using (var target = OpenAt2(Fd(_hostTemporary), name, 0x01 | 0x40 | 0x80 | 0x80000 | 0x20000, 0x180, Beneath))
            {
                using var stream = new FileStream(target, FileAccess.Write);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            using var copied = OpenOptional(_hostTemporary, name) ?? throw new IOException();
            var actual = ReadHits(copied, plan, 0, 0, deadline, token);
            if (!bytes.AsSpan().SequenceEqual(actual)) throw new IOException();
            rows.Add(new { module = plan.Module, outcome = "unchanged-worker-handoff", candidates = plan.Candidates,
                worker_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), cached_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                host_sha256 = Convert.ToHexStringLower(SHA256.HashData(actual)), module_sha256 = plan.ModuleSha256 });
        }
        CheckBarrierDeadline(deadline, token);
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "evidence-worker-measurement-v3", pid = _worker.Pid, start_time = _worker.StartTimeTicks,
            uid = _worker.Uid, gid = _worker.Gid, cgroup = _worker.ControlGroup, original_pidfd_exit = true,
            normal_exit_code = 1, capture_phase = "pre_exit_after_original_handlers", cached_original_pid_live = true,
            acknowledged_before_original_exit = true, barrier_nonce_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(_exitNonce!))),
            modules = rows, native_acceptance = false
        }));
    }

    private void CheckBarrierDeadline(LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        if (DateTimeOffset.UtcNow >= _exitOriginalDeadline) throw new IOException();
    }

    private void CheckExitSlot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_exitSlot is null || _exitWorker is null || !Metadata(_exitSlot).SameAncestorAs(_exitSlotIdentity)
            || !Metadata(_exitWorker).SameAncestorAs(_exitWorkerIdentity)) throw new IOException();
        using var named = OpenAt2(Fd(_temporary), _exitSlotName!, DirectoryFlags, 0, Beneath);
        using var worker = OpenAt2(Fd(_exitSlot), "worker", DirectoryFlags, 0, Beneath);
        if (!Metadata(named).SameAncestorAs(_exitSlotIdentity) || !Metadata(worker).SameAncestorAs(_exitWorkerIdentity)) throw new IOException();
    }

    private static void WriteBarrierLeaf(SafeFileHandle parent, string name, byte[] bytes, uint mode)
    {
        using var handle = OpenAt2(Fd(parent), name, 0x01 | 0x40 | 0x80 | 0x80000 | 0x20000, 0x180, Beneath);
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
        if (BarrierChown(handle, 0, 0) != 0 || BarrierChmod(handle, mode) != 0) throw new IOException();
    }

    private static byte[] ReadBarrierLeaf(SafeFileHandle handle, SafeFileHandle parent, string name,
        uint uid, uint gid, ushort mode, int length, LinuxInputDeadline deadline, CancellationToken token)
    {
        deadline.Check(token);
        var before = Metadata(handle);
        if (before.Uid != uid || before.Gid != gid || before.Mode != mode || before.LinkCount != 1
            || before.Length != (ulong)length) throw new IOException();
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            deadline.Check(token);
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new IOException();
            offset += count;
        }
        using var named = OpenOptional(parent, name) ?? throw new IOException();
        if (Metadata(handle) != before || Metadata(named) != before) throw new IOException();
        deadline.Check(token);
        return bytes;
    }

    private void CloseExitBarrier()
    {
        _exitCache = null;
        try { _exitWorker?.Dispose(); }
        finally { _exitSlot?.Dispose(); }
    }

    [DllImport("libc", EntryPoint = "fchown", SetLastError = true)] private static extern int BarrierChown(SafeFileHandle handle, uint uid, uint gid);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] private static extern int BarrierChmod(SafeFileHandle handle, uint mode);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] private static extern int BarrierRename(int source, string sourceName, int destination, string destinationName, uint flags);
}
