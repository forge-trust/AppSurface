using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || RuntimeInformation.OSArchitecture != Architecture.X64)
            return 65;
        return await Probe.RunAsync(args).ConfigureAwait(false);
    }
}

/// <summary>OS namespace lifetime data only; never AppSurface admission or coverage hit data.</summary>
[SupportedOSPlatform("linux")]
internal static class Probe
{
    private const uint ChildId = 65534;
    private static readonly byte[] Payload = "actual-child-process-exit\n"u8.ToArray();
    private static readonly byte[] Release = "root-pinned-namespace\n"u8.ToArray();
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 2 && args[0] == "child" && Guid.TryParseExact(args[1], "N", out var parsed)
            && args[1] == parsed.ToString("N"))
            return await ChildAsync(args[1]).ConfigureAwait(false);
        if (args.Length != 2 || args[0] is not ("parent" or "coverage-parent") || GetUid() != 0 || GetEffectiveUid() != 0
            || !Path.IsPathFullyQualified(args[1])) return 65;
        var output = args[1];
        if (Path.Exists(output)) return 65;
        var generation = Guid.NewGuid().ToString("N");
        var unit = "issue779-private-tmp-probe-" + generation + ".service";
        var report = new Dictionary<string, object?>
        {
            ["schema"] = "issue779-private-tmp-exit-probe-v1", ["generation"] = generation,
            ["native_acceptance"] = false, ["coverage_credit"] = false,
        };
        Exception? failure = null;
        try { await ParentAsync(generation, unit, report, args[0] == "coverage-parent").ConfigureAwait(false); }
        catch (Exception error) { failure = error; report["failure_category"] = error.GetType().Name; }
        finally
        {
            // This exact fresh name is retained before Start; cleanup also runs after ambiguous startup.
            try
            {
                await UtilityAsync("/usr/bin/systemctl", ["stop", unit], acceptNonzero: false).ConfigureAwait(false);
            }
            catch (Exception error) { failure ??= error; }
            try
            {
                await UtilityAsync("/usr/bin/systemctl", ["kill", "--kill-whom=all", "--signal=KILL", unit], acceptNonzero: true).ConfigureAwait(false);
            }
            catch (Exception error) { failure ??= error; }
            try
            {
                var leaf = "/sys/fs/cgroup/system.slice/" + unit;
                var empty = !Directory.Exists(leaf) || Encoding.ASCII.GetString(ReadFile(leaf + "/cgroup.events", 1024))
                    .Split('\n').Contains("populated 0", StringComparer.Ordinal);
                report["cleanup_complete"] = empty;
                if (!empty) failure ??= new IOException("Generated helper group is not empty.");
            }
            catch (Exception error) { failure ??= error; report["cleanup_complete"] = false; }
        }
        if (failure is not null) report["failure_category"] = failure.GetType().Name;
        report["exit"] = failure is null ? 0 : 1;
        Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
        if (bytes.Length > 16384) return 1;
        using var result = new FileStream(Path.Join(output, "receipt.json"), new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        await result.WriteAsync(bytes).ConfigureAwait(false);
        return failure is null ? 0 : 1;
    }

    private static async Task<int> ChildAsync(string generation)
    {
        if (GetUid() != ChildId || GetGid() != ChildId) return 65;
        if (ChildOnly(true) + ChildOnly(false) != 83) return 65;
        var ready = "/tmp/issue779-release-" + generation;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            using var file = new FileStream("/tmp/issue779-exit-" + generation, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            file.Write(Payload);
            file.Flush(flushToDisk: true);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(ready) || File.GetUnixFileMode(ready) != (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead))
            await Task.Delay(10, deadline.Token).ConfigureAwait(false);
        return ReadFile(ready, 128).AsSpan().SequenceEqual(Release) ? 0 : 65;
    }

    private static async Task ParentAsync(string generation, string unit, Dictionary<string, object?> report, bool copyTracker)
    {
        var host = Environment.ProcessPath ?? throw new IOException();
        var entry = typeof(Probe).Assembly.Location;
        var plan = copyTracker ? TrackerPlan.Read(entry) : null;
        report["stage"] = "unit-start";
        await UtilityAsync("/usr/bin/systemd-run",
        [
            "--quiet", "--service-type=exec", "--unit=" + unit,
            "--property=User=65534", "--property=Group=65534", "--property=SupplementaryGroups=",
            "--property=NoNewPrivileges=yes", "--property=CapabilityBoundingSet=", "--property=AmbientCapabilities=",
            "--property=PrivateTmp=yes", "--property=ProtectHome=yes", "--property=ProtectSystem=strict",
            "--property=ProtectControlGroups=yes", "--property=RestrictSUIDSGID=no",
            "--property=TasksMax=64", "--property=MemoryMax=1G", "--property=KillMode=control-group",
            "--property=RemainAfterExit=yes", "--property=Restart=no", "--property=RuntimeMaxSec=30",
            "--property=TimeoutStopSec=2", "--property=LimitCORE=0", "/usr/bin/env", "-i",
            "PATH=/usr/bin:/bin", "HOME=/nonexistent", "LANG=C.UTF-8", "DOTNET_CLI_HOME=/tmp", "XDG_RUNTIME_DIR=/tmp",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1", host, entry, "child", generation,
        ], acceptNonzero: false).ConfigureAwait(false);
        report["stage"] = "main-pid";
        uint pid = 0;
        using var startup = new CancellationTokenSource(Limit);
        while (pid == 0)
        {
            startup.Token.ThrowIfCancellationRequested();
            var text = await UtilityAsync("/usr/bin/systemctl", ["show", "--property=MainPID", "--value", unit], false).ConfigureAwait(false);
            if (!uint.TryParse(Encoding.ASCII.GetString(text).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) throw new IOException();
            if (pid == 0) await Task.Delay(10, startup.Token).ConfigureAwait(false);
        }
        report["stage"] = "live-identity";
        var before = Sample(pid, unit);
        var notification = PidFdOpen(434, checked((int)pid), 0);
        if (notification < 0) throw new IOException();
        using var process = new SafeFileHandle(notification, ownsHandle: true);
        if (Exited(notification)) throw new IOException();
        report["stage"] = "private-tmp-open";
        var fd = Open("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/root/tmp", 0x10000 | 0x80000);
        if (fd < 0) throw new IOException();
        using var temporary = new SafeFileHandle(fd, ownsHandle: true);
        var privateRoot = Inspect(fd);
        var hostRoot = InspectPath("/tmp");
        if (privateRoot.Uid != 0 || privateRoot.Gid != 0 || privateRoot.Mode >> 12 != 4 || privateRoot.Inode == 0) throw new IOException();
        if (privateRoot.Inode == hostRoot.Inode && privateRoot.DeviceMajor == hostRoot.DeviceMajor && privateRoot.DeviceMinor == hostRoot.DeviceMinor)
            throw new IOException();
        if (before != Sample(pid, unit) || Exited(notification)) throw new IOException();
        report["pid"] = pid; report["start_time"] = before;
        report["uid"] = ChildId; report["gid"] = ChildId; report["cgroup"] = "/system.slice/" + unit;
        report["private_tmp_distinct"] = true;
        var root = "/proc/self/fd/" + fd.ToString(CultureInfo.InvariantCulture);
        var release = root + "/issue779-release-" + generation;
        using (var file = new FileStream(release, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        })) { file.Write(Release); file.Flush(flushToDisk: true); }
        File.SetUnixFileMode(release, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        report["stage"] = "original-pidfd-exit";
        using (var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!Exited(notification)) await Task.Delay(10, exit.Token).ConfigureAwait(false);
        report["stage"] = "retained-terminal";
        using (var terminalDeadline = new CancellationTokenSource(Limit))
            while (true)
            {
                var terminal = Encoding.ASCII.GetString(await UtilityAsync("/usr/bin/systemctl",
                    ["show", "--property=MainPID,ExecMainPID,ExecMainCode,ExecMainStatus", unit], false, terminalDeadline.Token).ConfigureAwait(false))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.Split('=', 2))
                    .ToDictionary(row => row[0], row => row[1], StringComparer.Ordinal);
                if (terminal.Count != 4) throw new IOException();
                if (terminal["MainPID"] == "0" && terminal["ExecMainPID"] == pid.ToString(CultureInfo.InvariantCulture)
                    && terminal["ExecMainCode"] == "1" && terminal["ExecMainStatus"] == "0") break;
                await Task.Delay(10, terminalDeadline.Token).ConfigureAwait(false);
            }
        report["child_exit_code"] = 0;
        report["original_pidfd_exit_observed"] = true;
        report["stage"] = "retained-exit-data";
        var data = OpenAt(fd, "issue779-exit-" + generation, 0x80000 | 0x20000 | 0x800);
        if (data < 0)
        {
            if (Marshal.GetLastPInvokeError() != 2) throw new IOException();
            report["retained_data_reachable"] = false;
            return; // A measured missing file is data, never coverage or product failure attribution.
        }
        using var retained = new SafeFileHandle(data, ownsHandle: true);
        var metadata = Inspect(data);
        if (metadata.Uid != ChildId || metadata.Gid != ChildId || metadata.Nlink != 1
            || metadata.Mode != 0x8180 || metadata.Size != (ulong)Payload.Length) throw new IOException();
        using var stream = new FileStream(retained, FileAccess.Read);
        var actual = Read(stream, 1024);
        var after = Inspect(data);
        if (metadata != after || !actual.AsSpan().SequenceEqual(Payload)) throw new IOException();
        report["retained_data_reachable"] = true;
        report["data_sha256"] = Convert.ToHexStringLower(SHA256.HashData(actual));
        if (plan is not null)
        {
            report["stage"] = "actual-tracker-handoff";
            var hitFd = OpenAt(fd, Path.GetFileName(plan.Path), 0x80000 | 0x20000 | 0x800);
            if (hitFd < 0) throw new IOException();
            using var hit = new SafeFileHandle(hitFd, ownsHandle: true);
            var initial = Inspect(hitFd);
            if (initial.Uid != ChildId || initial.Gid != ChildId || initial.Nlink != 1
                || initial.Mode is not (0x8180 or 0x81a4) || initial.Size != (ulong)(4 * (plan.Candidates + 1))) throw new IOException();
            using var hits = new FileStream(hit, FileAccess.Read);
            var bytes = Read(hits, 512 * 1024);
            if (initial != Inspect(hitFd) || bytes.Length != 4 * (plan.Candidates + 1)
                || BinaryPrimitives.ReadInt32LittleEndian(bytes) != plan.Candidates) throw new IOException();
            for (var offset = 4; offset < bytes.Length; offset += 4)
                if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)) < 0) throw new IOException();
            // Copy unchanged real bytes. Only Coverlet's tracker and standard reader interpret/add counts.
            using (var hostHit = new FileStream(plan.Path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            })) { hostHit.Write(bytes); hostHit.Flush(flushToDisk: true); }
            var copied = ReadFile(plan.Path, 512 * 1024);
            if (!bytes.AsSpan().SequenceEqual(copied)) throw new IOException();
            report["worker_hit_handoff"] = true;
            report["worker_hit_candidates"] = plan.Candidates;
            report["worker_hit_sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            report["host_handoff_sha256"] = Convert.ToHexStringLower(SHA256.HashData(copied));
            report["instrumented_module_sha256"] = plan.ModuleSha256;
        }
    }

    // The root parent never calls this method. Its positive report hits must originate in the child.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ChildOnly(bool first) => first ? 41 : 42;

    private sealed record TrackerPlan(string Path, int Candidates, string ModuleSha256)
    {
        internal static TrackerPlan Read(string module)
        {
            var bytes = ReadFile(module, 4 * 1024 * 1024);
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = new PEReader(stream);
            var reader = image.GetMetadataReader();
            var trackers = reader.TypeDefinitions.Where(handle => reader.GetString(reader.GetTypeDefinition(handle).Namespace)
                == "Coverlet.Core.Instrumentation.Tracker").ToArray();
            if (trackers.Length != 1) throw new IOException();
            var type = reader.GetTypeDefinition(trackers[0]);
            var name = reader.GetString(type.Name);
            var prefix = System.IO.Path.GetFileNameWithoutExtension(module) + "_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "D", out var id)
                || name[prefix.Length..] != id.ToString("D")) throw new IOException();
            var constructors = type.GetMethods().Select(reader.GetMethodDefinition)
                .Where(method => reader.GetString(method.Name) == ".cctor").ToArray();
            if (constructors.Length != 1) throw new IOException();
            var il = image.GetMethodBody(constructors[0].RelativeVirtualAddress).GetILBytes() ?? throw new IOException();
            // Exact pinned 10.0.1 Instrumenter tail; reject another layout rather than guess mapping.
            if (il.Length < 38) throw new IOException();
            var tail = il.AsSpan(il.Length - 38);
            if (tail[0] != 0x20 || tail[5] != 0x8d || tail[10] != 0x80 || tail[15] != 0x72 || tail[20] != 0x80
                || tail[25] != 0x16 || tail[26] != 0x80 || tail[31] != 0x17 || tail[32] != 0x80 || tail[37] != 0x2a) throw new IOException();
            var count = BinaryPrimitives.ReadInt32LittleEndian(tail[1..]);
            if (count is < 1 or > 131071) throw new IOException();
            var element = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(tail[6..]));
            if (element.Kind != HandleKind.TypeReference) throw new IOException();
            var elementType = reader.GetTypeReference((TypeReferenceHandle)element);
            if (reader.GetString(elementType.Namespace) != "System" || reader.GetString(elementType.Name) != "Int32"
                || elementType.ResolutionScope.Kind != HandleKind.AssemblyReference) throw new IOException();
            var core = reader.GetAssemblyReference((AssemblyReferenceHandle)elementType.ResolutionScope);
            if (reader.GetString(core.Name) is not ("System.Runtime" or "System.Private.CoreLib")) throw new IOException();
            string Field(int offset, byte[] signature)
            {
                var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(il.Length - 38 + offset));
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind != HandleKind.FieldDefinition) throw new IOException();
                var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                if (field.GetDeclaringType() != trackers[0]
                    || (field.Attributes & System.Reflection.FieldAttributes.Static) == 0
                    || !reader.GetBlobBytes(field.Signature).AsSpan().SequenceEqual(signature)) throw new IOException();
                return reader.GetString(field.Name);
            }
            if (Field(11, [0x06, 0x1d, 0x08]) != "HitsArray" || Field(21, [0x06, 0x0e]) != "HitsFilePath"
                || Field(27, [0x06, 0x02]) != "SingleHit" || Field(33, [0x06, 0x02]) != "FlushHitFile") throw new IOException();
            var textToken = BinaryPrimitives.ReadInt32LittleEndian(tail[16..]);
            if ((textToken & unchecked((int)0xff000000)) != 0x70000000) throw new IOException();
            var path = reader.GetUserString(MetadataTokens.UserStringHandle(textToken & 0xffffff));
            if (path != "/tmp/" + name) throw new IOException();
            return new(path, count, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private static ulong Sample(uint pid, string unit)
    {
        var root = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
        var before = Encoding.ASCII.GetString(ReadFile(root + "/stat", 16384));
        var status = Encoding.ASCII.GetString(ReadFile(root + "/status", 65536)).Split('\n');
        foreach (var key in new[] { "Uid:", "Gid:" })
        {
            var fields = status.Single(row => row.StartsWith(key, StringComparison.Ordinal))[key.Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4 || fields.Any(value => value != "65534")) throw new IOException();
        }
        if (!ReadFile(root + "/cgroup", 4096).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("0::/system.slice/" + unit + "\n"))) throw new IOException();
        var after = Encoding.ASCII.GetString(ReadFile(root + "/stat", 16384));
        static ulong Start(string stat)
        {
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            if (fields.Length < 20 || fields[0] is "Z" or "X" or "x") throw new IOException();
            return ulong.Parse(fields[19], CultureInfo.InvariantCulture);
        }
        var start = Start(before);
        if (start == 0 || start != Start(after)) throw new IOException();
        return start;
    }

    private static async Task<byte[]> UtilityAsync(string command, string[] arguments, bool acceptNonzero, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Limit);
        using var process = new Process { StartInfo = new(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var value in arguments) process.StartInfo.ArgumentList.Add(value);
        if (!process.Start()) throw new IOException();
        var stdout = DrainAsync(process.StandardOutput.BaseStream, deadline.Token);
        var stderr = DrainAsync(process.StandardError.BaseStream, deadline.Token);
        Exception? failure = null;
        byte[][]? output = null;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            output = await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!acceptNonzero && process.ExitCode != 0) throw new IOException();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) { failure ??= error; }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            if (!stdout.IsCompleted)
                try { process.StandardOutput.Dispose(); }
                catch (Exception error) { failure ??= error; }
            if (!stderr.IsCompleted)
                try { process.StandardError.Dispose(); }
                catch (Exception error) { failure ??= error; }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) throw new IOException("A fixed diagnostic utility or its original pipes did not join.", failure);
        return output![0];
    }

    private static async Task<byte[]> DrainAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > 16384) throw new IOException();
            output.Write(buffer, 0, count);
        }
    }
    private static byte[] ReadFile(string path, int maximum) { using var stream = File.OpenRead(path); return Read(stream, maximum); }
    private static byte[] Read(Stream stream, int maximum)
    {
        var bytes = new byte[maximum + 1]; var count = 0;
        while (count < bytes.Length) { var n = stream.Read(bytes, count, bytes.Length - count); if (n == 0) return bytes[..count]; count += n; }
        throw new IOException();
    }
    private static Metadata Inspect(int fd)
    {
        if (Statx(fd, "", 0x1000, 0x7ff, out var value) != 0) throw new IOException();
        return value;
    }
    private static Metadata InspectPath(string path)
    {
        if (Statx(-100, path, 0x100, 0x7ff, out var value) != 0) throw new IOException();
        return value;
    }
    private static bool Exited(int fd)
    {
        var value = new PollFd { Fd = fd, Events = 1 };
        var count = Poll(ref value, 1, 0);
        if (count < 0 || (value.Returned & (8 | 32)) != 0) throw new IOException();
        return count == 1 && (value.Returned & 1) != 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { internal int Fd; internal short Events; internal short Returned; }
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)] private static extern int Poll(ref PollFd value, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern int PidFdOpen(long number, int pid, uint flags);
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private readonly record struct Metadata
    {
        [FieldOffset(16)] internal readonly uint Nlink;
        [FieldOffset(20)] internal readonly uint Uid;
        [FieldOffset(24)] internal readonly uint Gid;
        [FieldOffset(28)] internal readonly ushort Mode;
        [FieldOffset(32)] internal readonly ulong Inode;
        [FieldOffset(40)] internal readonly ulong Size;
        [FieldOffset(136)] internal readonly uint DeviceMajor;
        [FieldOffset(140)] internal readonly uint DeviceMinor;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int dir, string path, int flags, uint mask, out Metadata value);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAt(int dir, string path, int flags);
    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "getgid")] private static extern uint GetGid();
}
