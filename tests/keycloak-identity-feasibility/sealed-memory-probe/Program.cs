using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

return await Probe.RunAsync(args);

static class Probe
{
    private const int AddSeals = 1033, GetSeals = 1034, GetFlags = 3, SetFlags = 4;
    private const int RequiredSeals = 15;
    private const int Cloexec = 0x80000, MemfdCloexec = 1, Append = 0x400, AllowSealing = 2;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 1, StatxMode = 2, StatxLinks = 4, StatxUid = 8, StatxInode = 0x100, StatxSize = 0x200;
    private const int MaxSecretBytes = 4096, MaxEnvelopeBytes = 8192;
    private static readonly byte[] Magic = "SMB1"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Summary("unsupported-platform", false, false, 0);
            return 78;
        }

        try
        {
            if (args is ["anchor"]) return await RunAnchorAsync();
            if (args.Length == 1) return await ReadChildAsync(args[0]);
            if (args.Length != 0) throw new InvalidDataException();
            return await RunParentAsync();
        }
        catch
        {
            Summary("probe-failed", false, false, 0);
            return 1;
        }
    }

    private static async Task<int> RunAnchorAsync()
    {
        var input = ReadBounded(Console.OpenStandardInput(), MaxEnvelopeBytes);
        byte[]? secret = null;
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var value = root.GetProperty("secret").GetString() ?? throw new InvalidDataException();
            secret = StrictUtf8.GetBytes(value);
            if (secret.Length is 0 or > MaxSecretBytes || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                throw new InvalidDataException();
            var header = new Header(Guid.NewGuid().ToString("N"), SafeLabel(root.GetProperty("authority").GetString(), 512),
                SafeLabel(root.GetProperty("client").GetString(), 256), DateTime.UtcNow.AddMinutes(5).Ticks);
            using var capture = CreateMemfd("sealed-memory-anchor", Frame(header, secret), sealedFile: true);
            var fd = capture.DangerousGetHandle().ToInt32();
            var descriptor = new Descriptor(Environment.ProcessId, StartTicks(Environment.ProcessId), fd, Inode(fd), header.Run, header.Authority, header.Client);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(secret);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "anchor-ready", descriptor }, JsonOptions));
            await Task.Delay(TimeSpan.FromSeconds(60));
            return 124; // A lost controller cannot leave an unbounded private credential holder running.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static async Task<int> RunParentAsync()
    {
        byte[]? secret = null;
        try
        {
            var input = ReadBounded(Console.OpenStandardInput(), MaxEnvelopeBytes);
            try
            {
                using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
                var root = document.RootElement;
                var source = root.GetProperty("secret").GetString() ?? throw new InvalidDataException();
                var authority = SafeLabel(root.GetProperty("authority").GetString(), 512);
                var client = SafeLabel(root.GetProperty("client").GetString(), 256);
                secret = StrictUtf8.GetBytes(source);
                if (secret.Length is 0 or > MaxSecretBytes || string.IsNullOrWhiteSpace(source) || source.Any(char.IsControl)) throw new InvalidDataException();

                var run = Guid.NewGuid().ToString("N");
                var expires = DateTime.UtcNow.AddMinutes(5).Ticks;
                var header = new Header(run, authority, client, expires);
                using var capture = CreateMemfd("sealed-memory-probe", Frame(header, secret), sealedFile: true);
                source = "rotated-" + Guid.NewGuid().ToString("N");
                var fd = capture.DangerousGetHandle().ToInt32();
                var descriptor = new Descriptor(Environment.ProcessId, StartTicks(Environment.ProcessId), fd, Inode(fd), run, authority, client);

                var seals = Native.fcntl(fd, GetSeals, 0);
                var writeRejected = !TryWrite(fd, [0x41]);
                var truncateRejected = Native.ftruncate(fd, 0) == -1;
                var flags = Native.fcntl(fd, GetFlags, 0);
                var appendRejected = flags >= 0 && Native.fcntl(fd, SetFlags, flags | Append) == 0 && !TryWrite(fd, [0x42]);
                var removalRejected = Native.fcntl(fd, AddSeals, 0) == -1
                    && (Native.fcntl(fd, GetSeals, 0) & RequiredSeals) == RequiredSeals;

                using var unsealed = CreateMemfd("unsealed-probe", [], sealedFile: false);
                var nonsealedRejected = await RunReaderAsync(descriptor with { Fd = unsealed.DangerousGetHandle().ToInt32() }, secret, false, "seals");
                var wrongRunRejected = await RunReaderAsync(descriptor with { Run = Guid.NewGuid().ToString("N") }, secret, false, "binding");
                var wrongAuthorityRejected = await RunReaderAsync(descriptor with { Authority = authority + ".changed" }, secret, false, "binding");
                var wrongClientRejected = await RunReaderAsync(descriptor with { Client = client + ".changed" }, secret, false, "binding");
                var wrongParentRejected = await RunReaderAsync(descriptor with { ParentStartTicks = descriptor.ParentStartTicks + 1 }, secret, false, "parent-identity");
                var wrongInodeRejected = await RunReaderAsync(descriptor with { Inode = descriptor.Inode + 1 }, secret, false, "file-identity");
                using var replacement = CreateMemfd("replacement-probe", Frame(header, secret), sealedFile: true);
                var replacementRejected = await RunReaderAsync(descriptor with { Fd = replacement.DangerousGetHandle().ToInt32() }, secret, false, "file-identity");
                using var expired = CreateMemfd("expired-probe", Frame(header with { ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(-1).Ticks }, secret), sealedFile: true);
                var expiredFd = expired.DangerousGetHandle().ToInt32();
                var expiredRejected = await RunReaderAsync(descriptor with { Fd = expiredFd, Inode = Inode(expiredFd) }, secret, false, "binding");

                // The worker and web readers receive only this metadata argument. Their
                // comparison value arrives over stdin, and both open the same parent fd.
                var worker = RunReaderAsync(descriptor, secret);
                var web = RunReaderAsync(descriptor, secret);
                var results = await Task.WhenAll(worker, web);
                var workerAccepted = results[0];
                var webAccepted = results[1];
                var checks = new[] { seals >= 0 && (seals & RequiredSeals) == RequiredSeals, writeRejected, truncateRejected, appendRejected,
                    removalRejected, nonsealedRejected, wrongRunRejected, wrongAuthorityRejected,
                    wrongClientRejected, wrongParentRejected, wrongInodeRejected, replacementRejected, expiredRejected, workerAccepted, webAccepted };
                var names = new[] { "seals", "write", "truncate", "append", "add-seals", "unsealed", "wrong-run", "wrong-authority", "wrong-client", "wrong-parent", "wrong-inode", "replacement", "expired", "worker", "web" };
                Console.Error.WriteLine(JsonSerializer.Serialize(names.Zip(checks).ToDictionary(pair => pair.First, pair => pair.Second)));
                Summary("complete", checks.All(x => x), checks.All(x => x), checks.Length);
                return checks.All(x => x) ? 0 : 1;
            }
            finally { CryptographicOperations.ZeroMemory(input); }
        }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }

    private static async Task<bool> RunReaderAsync(Descriptor descriptor, byte[] expected, bool shouldAccept = true, string expectedStage = "none")
    {
        using var process = StartReader(descriptor);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(expected);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            using var json = JsonDocument.Parse(output);
            Console.Error.WriteLine(JsonSerializer.Serialize(new { stage = "reader-observation", expectedAccepted = shouldAccept,
                exitCode = process.ExitCode, accepted = json.RootElement.GetProperty("accepted").GetBoolean(),
                failureStage = json.RootElement.GetProperty("failureStage").GetString() }));
            return process.ExitCode == (shouldAccept ? 0 : 20)
                && json.RootElement.GetProperty("accepted").GetBoolean() == shouldAccept
                && json.RootElement.GetProperty("failureStage").GetString() == expectedStage
                && json.RootElement.GetProperty("stage").GetString() == (shouldAccept ? "reader-accepted" : "reader-rejected");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(reap.Token); } catch { }
            return false;
        }
    }

    private static Process StartReader(Descriptor descriptor)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException();
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add(JsonSerializer.Serialize(descriptor, JsonOptions));
        return Process.Start(start) ?? throw new InvalidOperationException();
    }

    private static async Task<int> ReadChildAsync(string descriptorJson)
    {
        var accepted = false;
        byte[]? expected = null;
        byte[]? actual = null;
        var failureStage = "descriptor";
        try
        {
            if (descriptorJson.Length > 4096) throw new InvalidDataException();
            var d = JsonSerializer.Deserialize<Descriptor>(descriptorJson, JsonOptions) ?? throw new InvalidDataException();
            expected = ReadBounded(Console.OpenStandardInput(), MaxSecretBytes);
            if (expected.Length == 0) throw new InvalidDataException();
            failureStage = "parent-identity";
            if (StartTicks(d.ParentPid) != d.ParentStartTicks) throw new InvalidDataException();

            var path = $"/proc/{d.ParentPid}/fd/{d.Fd}";
            var rawFd = Native.open(path, Cloexec);
            if (rawFd < 0) throw new InvalidDataException();
            using var opened = new SafeFileHandle((IntPtr)rawFd, ownsHandle: true);
            var fd = rawFd;
            failureStage = "seals";
            var seals = Native.fcntl(fd, GetSeals, 0);
            if (seals < 0 || (seals & RequiredSeals) != RequiredSeals) throw new InvalidDataException();
            var stat = new byte[256];
            failureStage = "file-identity";
            const uint required = StatxType | StatxMode | StatxSize | StatxInode | StatxUid | StatxLinks;
            if (Native.statx(fd, "", AtEmptyPath, required, stat) != 0) throw new InvalidDataException();
            var mask = BinaryPrimitives.ReadUInt32LittleEndian(stat);
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(stat.AsSpan(28));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(40));
            var inode = BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(32));
            var uid = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(20));
            var links = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(16));
            if ((mask & required) != required
                || (mode & 0xF000) != 0x8000 || (mode & 0x1FF) != 0x100
                || inode != d.Inode || uid != Native.geteuid() || links != 0
                || size > 16 + 4096 + MaxSecretBytes) throw new InvalidDataException();

            using var stream = new FileStream(opened, FileAccess.Read);
            failureStage = "frame";
            var prefix = new byte[12];
            stream.ReadExactly(prefix);
            if (!prefix.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException();
            var headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
            var secretLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(8));
            if (headerLength is 0 or > 4096 || secretLength > MaxSecretBytes
                || size != 12UL + headerLength + secretLength) throw new InvalidDataException();
            var headerBytes = new byte[headerLength];
            stream.ReadExactly(headerBytes);
            var h = JsonSerializer.Deserialize<Header>(headerBytes, JsonOptions) ?? throw new InvalidDataException();
            failureStage = "binding";
            if (!Bound(d, h)) throw new InvalidDataException();
            actual = new byte[secretLength];
            stream.ReadExactly(actual);
            failureStage = "value-or-final-identity";
            if (stream.ReadByte() != -1 || StartTicks(d.ParentPid) != d.ParentStartTicks
                || StrictUtf8.GetCharCount(actual) == 0
                || !CryptographicOperations.FixedTimeEquals(actual, expected)) throw new InvalidDataException();
            accepted = true;
        }
        catch { accepted = false; }
        finally
        {
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { stage = accepted ? "reader-accepted" : "reader-rejected", accepted,
            failureStage = accepted ? "none" : failureStage }));
        return accepted ? 0 : 20;
    }

    private static SafeFileHandle CreateMemfd(string name, byte[] frame, bool sealedFile)
    {
        var fd = Native.memfd_create(name, AllowSealing | MemfdCloexec);
        if (fd < 0) throw new InvalidOperationException();
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            if (frame.Length != 0) WriteAll(fd, frame);
            if (Native.fchmod(fd, 0x100) != 0) throw new InvalidOperationException();
            if (sealedFile)
            {
                if (Native.fcntl(fd, AddSeals, RequiredSeals) != 0) throw new InvalidOperationException();
                var seals = Native.fcntl(fd, GetSeals, 0);
                if (seals < 0 || (seals & RequiredSeals) != RequiredSeals) throw new InvalidOperationException();
            }
            return handle;
        }
        catch { handle.Dispose(); throw; }
        finally { if (frame.Length != 0) CryptographicOperations.ZeroMemory(frame); }
    }

    private static byte[] Frame(Header header, byte[] secret)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var frame = new byte[12 + json.Length + secret.Length];
        Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)json.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), (uint)secret.Length);
        json.CopyTo(frame, 12);
        secret.CopyTo(frame, 12 + json.Length);
        CryptographicOperations.ZeroMemory(json);
        return frame;
    }

    private static bool Bound(Descriptor d, Header h) => d.Run == h.Run && d.Authority == h.Authority
        && d.Client == h.Client && h.ExpiresUtcTicks > DateTime.UtcNow.Ticks;

    private static string SafeLabel(string? value, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || StrictUtf8.GetByteCount(value) > maxBytes || value.Any(char.IsControl))
            throw new InvalidDataException();
        return value;
    }

    private static byte[] ReadBounded(Stream stream, int max)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (buffer.Length <= max)
        {
            var n = stream.Read(chunk, 0, Math.Min(chunk.Length, max + 1 - (int)buffer.Length));
            if (n == 0) break;
            buffer.Write(chunk, 0, n);
        }
        CryptographicOperations.ZeroMemory(chunk);
        if (buffer.Length > max) throw new InvalidDataException();
        return buffer.ToArray();
    }

    private static long StartTicks(int pid)
    {
        if (pid <= 0) throw new InvalidDataException();
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var end = stat.LastIndexOf(')');
        if (end < 0) throw new InvalidDataException();
        var fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Field 22 is the kernel's integral boot-relative start counter, not a reconstructed wall-clock timestamp.
        return long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ulong Inode(int fd)
    {
        var stat = new byte[256];
        if (Native.statx(fd, "", AtEmptyPath, StatxInode, stat) != 0
            || (BinaryPrimitives.ReadUInt32LittleEndian(stat) & StatxInode) == 0) throw new InvalidDataException();
        return BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(32));
    }

    private static void WriteAll(int fd, byte[] bytes)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var result = Native.write(fd, IntPtr.Add(pin.AddrOfPinnedObject(), offset), (nuint)(bytes.Length - offset));
                if (result < 0 && Marshal.GetLastPInvokeError() == 4) continue;
                if (result <= 0) throw new InvalidOperationException();
                offset += (int)result;
            }
        }
        finally { pin.Free(); }
    }

    private static bool TryWrite(int fd, byte[] bytes)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return Native.write(fd, pin.AddrOfPinnedObject(), (nuint)bytes.Length) >= 0; }
        finally { pin.Free(); }
    }

    private static void Summary(string stage, bool passed, bool children, int tests) =>
        Console.WriteLine(JsonSerializer.Serialize(new { stage, passed, childrenStopped = children,
            testCount = tests, platformVersion = Environment.Version.ToString() }));

    private sealed record Header(string Run, string Authority, string Client, long ExpiresUtcTicks);
    private sealed record Descriptor(int ParentPid, long ParentStartTicks, int Fd, ulong Inode, string Run, string Authority, string Client);

    private static class Native
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "memfd_create")]
        internal static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int fcntl(int fd, int command, int argument);
        [DllImport("libc", SetLastError = true)] internal static extern int fchmod(int fd, uint mode);
        [DllImport("libc", SetLastError = true)] internal static extern int ftruncate(int fd, long length);
        [DllImport("libc")] internal static extern uint geteuid();
        [DllImport("libc", SetLastError = true)] internal static extern nint write(int fd, IntPtr buffer, nuint count);
        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
        internal static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
    }
}
