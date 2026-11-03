using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.ExitBarrier.Support;

/// <summary>Private measurement-only rendezvous owned entirely by an independently built support assembly.</summary>
/// <remarks>
/// No product assembly, callback, environment input or execution capability is accepted. The seven selected
/// product modules must be the only instrumentation targets; this assembly must remain uninstrumented.
/// Missing or malformed root plans leave measurement unavailable and never alter the original exit code.
/// </remarks>
public static class WorkerExitBarrier
{
    private const int DirectoryFlags = 0x10000 | 0x20000 | 0x80000;
    private const int ReadFlags = 0x20000 | 0x80000 | 0x800;
    private const int AtFdcwd = -100;
    private static int attempted;
    private static State? state;

    /// <summary>Attempts once to arm the bounded exit handler from the fixed root-owned plan for this process.</summary>
    /// <remarks>
    /// The private SDK caller invokes this only after its genuine host returns ClaimKind.None, before its
    /// unchanged failure exception. The handler has at most five seconds measured from this call, further
    /// capped by the original UTC deadline. Errors return without setting an exit code or granting success.
    /// </remarks>
    public static void Arm()
    {
        if (Interlocked.Exchange(ref attempted, 1) != 0) return;
        State? prepared = null;
        try
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
            prepared = State.Open();
            state = prepared;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            prepared = null;
        }
        catch (Exception)
        {
            state = null;
            prepared?.Dispose();
        }
    }

    private static void OnProcessExit(object? sender, EventArgs args)
    {
        var owned = Interlocked.Exchange(ref state, null);
        if (owned is null) return;
        try { owned.Rendezvous(); }
        catch (Exception) { } // No exception text, product callback, exit-code mutation or grace extension.
        finally { owned.Dispose(); }
    }

    private sealed class State : IDisposable
    {
        private int temporary = -1;
        private int slot = -1;
        private int worker = -1;
        private readonly int pid = Environment.ProcessId;
        private string slotName = string.Empty;
        private Metadata slotIdentity;
        private Metadata workerIdentity;
        private Plan? plan;
        private byte[] nonce = [];
        private long started;
        private TimeSpan allowance;

        internal static State Open()
        {
            var value = new State { started = Stopwatch.GetTimestamp() };
            try
            {
                value.temporary = OpenRequired(AtFdcwd, "/tmp", DirectoryFlags);
                var temporaryIdentity = Stat(value.temporary);
                RequireDirectory(temporaryIdentity, 0, 0, null);
                value.slotName = "appsurface-sdk-exit-" + value.pid.ToString(CultureInfo.InvariantCulture);
                value.slot = OpenRequired(value.temporary, value.slotName, DirectoryFlags);
                value.slotIdentity = Stat(value.slot);
                RequireDirectory(value.slotIdentity, 0, 0, 0x1ed); // 0755, never worker-writable.
                value.plan = ParsePlan(ReadFile(value.slot, "plan.json", 2048, 0, 0, 0x124)); // 0444.
                Require(value.plan.Pid == value.pid);
                var utcRemaining = value.plan.Deadline - DateTimeOffset.UtcNow;
                Require(utcRemaining > TimeSpan.Zero);
                value.allowance = utcRemaining < TimeSpan.FromSeconds(5) ? utcRemaining : TimeSpan.FromSeconds(5);
                value.Check();
                RequireSelf(value.plan);
                value.worker = OpenRequired(value.slot, "worker", DirectoryFlags);
                value.workerIdentity = Stat(value.worker);
                RequireDirectory(value.workerIdentity, value.plan.Uid, value.plan.Gid, 0x1c0); // 0700.
                RequireAbsent(value.worker, "ready");
                RequireAbsent(value.worker, "ready.pending");
                RequireAbsent(value.slot, "ack");
                RequireAbsent(value.slot, "ack.pending");
                RequireAbsent(value.slot, "release");
                RequireAbsent(value.slot, "release.pending");
                value.nonce = Encoding.ASCII.GetBytes(value.plan.Nonce);
                value.RecheckDirectories();
                RequireSelf(value.plan);
                value.Check();
                return value;
            }
            catch { value.Dispose(); throw; }
        }

        internal void Rendezvous()
        {
            Check();
            RequireSelf(plan!);
            RecheckDirectories();
            RequireAbsent(slot, "ack");
            RequireAbsent(slot, "ack.pending");
            RequireAbsent(slot, "release");
            RequireAbsent(slot, "release.pending");
            var ready = OpenRequired(worker, "ready.pending", 0x1 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180);
            try
            {
                var metadata = Stat(ready);
                RequireFile(metadata, plan!.Uid, plan.Gid, 0x180, 0);
                var offset = 0;
                while (offset < nonce.Length)
                {
                    Check();
                    var part = nonce.AsSpan(offset).ToArray();
                    var count = Write(ready, part, (nuint)part.Length);
                    Require(count > 0 && count <= part.Length);
                    offset += checked((int)count);
                }
                Require(Fsync(ready) == 0);
                RequireFile(Stat(ready), plan.Uid, plan.Gid, 0x180, 32);
            }
            finally { Require(Close(ready) == 0); }
            Check();
            Require(RenameNoReplace(worker, "ready.pending", worker, "ready", 1) == 0);
            RecheckDirectories();
            WaitForRootPublication("ack");
            WaitForRootPublication("release");
        }

        private void WaitForRootPublication(string name)
        {
            while (true)
            {
                Check();
                RecheckDirectories();
                var publication = OpenOptional(slot, name, ReadFlags);
                if (publication >= 0)
                {
                    try { Require(ReadOpenedFile(publication, slot, name, 32, 0, 0, 0x124).AsSpan().SequenceEqual(nonce)); }
                    finally { Require(Close(publication) == 0); }
                    Check();
                    return;
                }
                var remaining = allowance - Stopwatch.GetElapsedTime(started);
                Require(remaining > TimeSpan.Zero);
                Thread.Sleep(remaining < TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10));
            }
        }

        private void Check()
        {
            Require(plan is not null && allowance > TimeSpan.Zero
                && Stopwatch.GetElapsedTime(started) < allowance && DateTimeOffset.UtcNow < plan.Deadline);
        }

        private void RecheckDirectories()
        {
            Require(SameDirectory(slotIdentity, Stat(slot)) && SameDirectory(workerIdentity, Stat(worker)));
            RequireNamedDirectory(temporary, slotName, slotIdentity);
            RequireNamedDirectory(slot, "worker", workerIdentity);
        }

        public void Dispose()
        {
            CloseOwned(ref worker);
            CloseOwned(ref slot);
            CloseOwned(ref temporary);
        }
    }

    private sealed record Plan(string Nonce, int Pid, uint Uid, uint Gid, ulong StartTime,
        string Cgroup, DateTimeOffset Deadline);

    private static Plan ParsePlan(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var value = document.RootElement;
        Require(value.ValueKind == JsonValueKind.Object);
        string[] fields = ["schema", "nonce", "pid", "uid", "gid", "start_time", "cgroup", "deadline_utc"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            Require(fields.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name));
        Require(seen.Count == fields.Length && value.GetProperty("schema").GetString() == "evidence-sdk-exit-plan-v1");
        var nonce = value.GetProperty("nonce").GetString()!;
        var pid = value.GetProperty("pid").GetInt32();
        var uid = value.GetProperty("uid").GetUInt32();
        var gid = value.GetProperty("gid").GetUInt32();
        var start = value.GetProperty("start_time").GetUInt64();
        var cgroup = value.GetProperty("cgroup").GetString()!;
        var deadlineText = value.GetProperty("deadline_utc").GetString()!;
        Require(LowerHex(nonce, 32) && pid > 0 && uid is > 0 and < uint.MaxValue
            && gid is > 0 and < uint.MaxValue && start > 0);
        const string prefix = "/system.slice/appsurface-evidence-worker-";
        Require(cgroup is not null && cgroup.StartsWith(prefix, StringComparison.Ordinal)
            && cgroup.EndsWith(".service", StringComparison.Ordinal)
            && LowerHex(cgroup[prefix.Length..^8], 32));
        Require(DateTimeOffset.TryParseExact(deadlineText, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var deadline) && deadline.Offset == TimeSpan.Zero);
        Require(deadlineText == deadline.ToString("O", CultureInfo.InvariantCulture)
            || deadlineText == deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return new(nonce, pid, uid, gid, start, cgroup, deadline);
    }

    private static bool LowerHex(string? value, int length) => value is not null && value.Length == length
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireSelf(Plan plan)
    {
        Require(Environment.ProcessId == plan.Pid);
        var path = "/proc/" + plan.Pid.ToString(CultureInfo.InvariantCulture);
        var before = ReadProc(path + "/stat", 4096);
        var status = ReadProc(path + "/status", 32768);
        var group = ReadProc(path + "/cgroup", 4096);
        var after = ReadProc(path + "/stat", 4096);
        Require(StartTime(before, plan.Pid) == plan.StartTime && StartTime(after, plan.Pid) == plan.StartTime);
        Require(group == "0::" + plan.Cgroup + "\n");
        var rows = status.Split('\n');
        RequireStatus(rows, "Uid:", plan.Uid);
        RequireStatus(rows, "Gid:", plan.Gid);
        foreach (var key in new[] { "Pid:", "Tgid:" })
        {
            var row = rows.Where(line => line.StartsWith(key, StringComparison.Ordinal)).ToArray();
            Require(row.Length == 1 && row[0][key.Length..].Trim() == plan.Pid.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void RequireStatus(string[] rows, string key, uint expected)
    {
        var matches = rows.Where(line => line.StartsWith(key, StringComparison.Ordinal)).ToArray();
        Require(matches.Length == 1);
        var values = matches[0][key.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Require(values.Length == 4);
        foreach (var value in values)
            Require(uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number == expected);
    }

    private static ulong StartTime(string text, int pid)
    {
        var first = text.IndexOf(' ');
        var last = text.LastIndexOf(')');
        Require(first > 0 && last > first && last + 2 < text.Length
            && text[..first] == pid.ToString(CultureInfo.InvariantCulture) && text[first + 1] == '(');
        var fields = text[(last + 2)..].TrimEnd('\n').Split(' ');
        Require(fields.Length >= 20 && fields[0] is not ("Z" or "X" or "x"));
        Require(ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start > 0);
        return start;
    }

    private static string ReadProc(string path, int limit)
    {
        var fd = OpenRequired(AtFdcwd, path, ReadFlags);
        try { return new UTF8Encoding(false, true).GetString(ReadBytes(fd, limit)); }
        finally { Require(Close(fd) == 0); }
    }

    private static byte[] ReadFile(int parent, string name, int limit, uint uid, uint gid, ushort mode)
    {
        var fd = OpenRequired(parent, name, ReadFlags);
        try { return ReadOpenedFile(fd, parent, name, limit, uid, gid, mode); }
        finally { Require(Close(fd) == 0); }
    }

    private static byte[] ReadOpenedFile(int fd, int parent, string name, int limit, uint uid, uint gid, ushort mode)
    {
        var before = Stat(fd);
        RequireFile(before, uid, gid, mode, null);
        Require(before.Size > 0 && before.Size <= (ulong)limit);
        var bytes = ReadBytes(fd, limit);
        Require((ulong)bytes.Length == before.Size && before == Stat(fd));
        var named = OpenRequired(parent, name, ReadFlags);
        try { Require(before == Stat(named)); }
        finally { Require(Close(named) == 0); }
        return bytes;
    }

    private static byte[] ReadBytes(int fd, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(4096, limit + 1)];
        while (output.Length <= limit)
        {
            var count = Read(fd, buffer, (nuint)Math.Min(buffer.Length, limit + 1 - output.Length));
            Require(count >= 0 && count <= buffer.Length);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, checked((int)count));
        }
        throw new IOException();
    }

    private static void RequireDirectory(Metadata value, uint uid, uint gid, ushort? mode) =>
        Require(value.Inode != 0 && value.Uid == uid && value.Gid == gid
            && (value.Mode & 0xf000) == 0x4000 && (mode is null || value.Mode == (0x4000 | mode.Value)));

    private static void RequireFile(Metadata value, uint uid, uint gid, ushort mode, ulong? size) =>
        Require(value.Inode != 0 && value.Uid == uid && value.Gid == gid && value.Mode == (0x8000 | mode)
            && value.Links == 1 && (size is null || value.Size == size));

    private static bool SameDirectory(Metadata left, Metadata right) => left.DeviceMajor == right.DeviceMajor
        && left.DeviceMinor == right.DeviceMinor && left.Inode == right.Inode && left.Uid == right.Uid
        && left.Gid == right.Gid && left.Mode == right.Mode;

    private static void RequireNamedDirectory(int parent, string name, Metadata expected)
    {
        var fd = OpenRequired(parent, name, DirectoryFlags);
        try { Require(SameDirectory(expected, Stat(fd))); }
        finally { Require(Close(fd) == 0); }
    }

    private static void RequireAbsent(int parent, string name)
    {
        var fd = OpenOptional(parent, name, ReadFlags);
        if (fd < 0) return;
        try { throw new IOException(); }
        finally { Require(Close(fd) == 0); }
    }

    private static int OpenRequired(int parent, string name, int flags, uint mode = 0)
    {
        var fd = OpenAt(parent, name, flags, mode);
        Require(fd >= 0);
        return fd;
    }

    private static int OpenOptional(int parent, string name, int flags)
    {
        var fd = OpenAt(parent, name, flags, 0);
        if (fd >= 0) return fd;
        Require(Marshal.GetLastPInvokeError() == 2); // Only actual ENOENT is absence.
        return -1;
    }

    private static void CloseOwned(ref int fd)
    {
        var owned = fd;
        fd = -1;
        if (owned >= 0)
            try { _ = Close(owned); }
            catch (Exception) { } // Attempt every descriptor even if another close failed.
    }

    private static Metadata Stat(int fd)
    {
        Require(Statx(fd, string.Empty, 0x1000, 0x7ff, out var value) == 0 && (value.Mask & 0x7ff) == 0x7ff);
        return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Uid, value.Gid, value.Mode,
            value.Links, value.Size, value.ChangeSeconds, value.ChangeNanoseconds, value.ModifySeconds, value.ModifyNanoseconds);
    }

    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw new IOException(); }

    private readonly record struct Metadata(uint DeviceMajor, uint DeviceMinor, ulong Inode, uint Uid, uint Gid,
        ushort Mode, uint Links, ulong Size, long ChangeSeconds, uint ChangeNanoseconds, long ModifySeconds, uint ModifyNanoseconds);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct NativeStatx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(24)] internal uint Gid;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangeSeconds;
        [FieldOffset(104)] internal uint ChangeNanoseconds;
        [FieldOffset(112)] internal long ModifySeconds;
        [FieldOffset(120)] internal uint ModifyNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mode);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fd, [Out] byte[] buffer, nuint count);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int fd, byte[] buffer, nuint count);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameNoReplace(int oldParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldName,
        int newParent, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName, uint flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mask, out NativeStatx value);
}
