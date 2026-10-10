using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>Private N03 observation broker. It sends no bytes to the accepted worker socket.</summary>
/// <remarks>Argument and JSON data grant no production authority. Root fixture containment is mandatory.</remarks>
internal static class Program
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Accepts exactly one peer, records kernel credentials, reads EOF, then awaits root release.</summary>
    internal static async Task<int> Main(string[] args)
    {
        Socket? listener = null;
        Socket? peer = null;
        Stream? input = null;
        Deadline? clock = null;
        CancellationTokenRegistration interruption = default;
        int interruptionCloseFailed = 0;
        string stage = "arguments";
        bool failed = false;
        Credentials observed = default;
        long end = 0;
        try
        {
            var selected = Parse(args);
            end = selected.End;
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new InvalidOperationException("broker-rejected");
            stage = "credentials";
            Require(GetUid() == selected.BrokerUid && GetEffectiveUid() == selected.BrokerUid
                && GetGid() == selected.BrokerGid && GetEffectiveGid() == selected.BrokerGid
                && GetGroups(0, IntPtr.Zero) == 0);
            int brokerPid = GetPid();
            Require(brokerPid > 0);
            ulong start = SelfStart(brokerPid);
            clock = new Deadline(end);
            input = Console.OpenStandardInput();
            stage = "listen";
            clock.Check();
            // The trusted root caller must have pinned the separate broker directory and full ancestry.
            // Bind never removes an existing path; this helper never unlinks any filesystem object.
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            interruption = clock.Token.Register(() =>
            {
                // Interrupt owned I/O on expiry, then the main method still awaits the original calls.
                try { peer?.Dispose(); } catch (Exception) { Interlocked.Exchange(ref interruptionCloseFailed, 1); }
                try { listener?.Dispose(); } catch (Exception) { Interlocked.Exchange(ref interruptionCloseFailed, 1); }
                try { input?.Dispose(); } catch (Exception) { Interlocked.Exchange(ref interruptionCloseFailed, 1); }
            });
            listener.Bind(new UnixDomainSocketEndPoint(selected.Socket));
            File.SetUnixFileMode(selected.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            listener.Listen(1);
            clock.Check();
            await EmitAsync("{\"schema\":\"issue779-n03-managed-broker-event-v1\",\"stage\":\"ready\","
                + Numbers(("broker_pid", brokerPid), ("broker_uid", selected.BrokerUid),
                    ("broker_gid", selected.BrokerGid), ("broker_start_ticks", start)) + "}", clock);
            stage = "accept";
            // Always await the actual operation. Cancellation never substitutes a proxy completion.
            peer = await listener.AcceptAsync(clock.Token).ConfigureAwait(false);
            clock.Check();
            observed = ReadPeer(peer);
            Require(observed.Pid > 0 && observed.Uid == selected.WorkerUid && observed.Gid == selected.WorkerGid);
            await EmitAsync("{\"schema\":\"issue779-n03-managed-broker-event-v1\",\"stage\":\"peer\","
                + Numbers(("peer_pid", observed.Pid), ("peer_uid", observed.Uid), ("peer_gid", observed.Gid))
                + ",\"identity_kind\":\"kernel-so-peercred-sample\"}", clock);
            stage = "receive";
            byte[] one = new byte[1];
            int received = await peer.ReceiveAsync(one.AsMemory(), SocketFlags.None, clock.Token).ConfigureAwait(false);
            clock.Check();
            Require(received == 0); // Any request byte is a failure. No descriptor/READY is sent.
            Require(ReadPeer(peer).Equals(observed));
            await EmitAsync("{\"schema\":\"issue779-n03-managed-broker-event-v1\",\"stage\":\"eof\","
                + Numbers(("peer_pid", observed.Pid)) + ",\"request_bytes\":0,\"sent_bytes\":0}", clock);
            stage = "release";
            // Root retains the write end. R+LF+EOF is a release, not a success/admission message.
            foreach (byte expected in new byte[] { (byte)'R', (byte)'\n' })
            {
                Require(await input.ReadAsync(one.AsMemory(), clock.Token).ConfigureAwait(false) == 1 && one[0] == expected);
                clock.Check();
            }
            Require(await input.ReadAsync(one.AsMemory(), clock.Token).ConfigureAwait(false) == 0);
            clock.Check();
        }
        catch (Exception) { failed = true; }
        finally
        {
            // Independent actual closes, preserving any earlier failure. The root caller joins this process.
            try { interruption.Dispose(); } catch (Exception) { failed = true; }
            try { peer?.Dispose(); } catch (Exception) { failed = true; }
            try { listener?.Dispose(); } catch (Exception) { failed = true; }
            try { input?.Dispose(); } catch (Exception) { failed = true; }
            if (clock is not null)
                try { await clock.DisposeAsync().ConfigureAwait(false); } catch (Exception) { failed = true; }
            if (Volatile.Read(ref interruptionCloseFailed) != 0) failed = true;
        }
        try
        {
            Require(end > UptimeMilliseconds());
            string packet = failed
                ? "{\"schema\":\"issue779-n03-managed-broker-result-v1\",\"status\":\"rejected\",\"stage\":\"" + stage
                    + "\",\"native_acceptance\":false}"
                : "{\"schema\":\"issue779-n03-managed-broker-result-v1\",\"status\":\"observed\","
                    + Numbers(("peer_pid", observed.Pid))
                    + ",\"request_bytes\":0,\"sent_bytes\":0,\"native_acceptance\":false}";
            // Publication may block in the kernel; the original external root deadline still owns the process.
            Console.WriteLine(packet);
            Console.Out.Flush();
            Require(end > UptimeMilliseconds());
        }
        catch (Exception) { return 1; }
        return failed ? 1 : 0;
    }

    /// <summary>Parses fixed root-selected data; no case, environment, request or command injection is accepted.</summary>
    private static Selection Parse(string[] args)
    {
        string[] names = ["--socket", "--broker-uid", "--broker-gid", "--worker-uid", "--worker-gid", "--deadline-uptime-ms"];
        Require(args.Length == names.Length * 2);
        for (int i = 0; i < names.Length; i++) Require(args[i * 2] == names[i]);
        string path = args[1];
        Require(path.StartsWith('/') && path == Path.GetFullPath(path) && !path.Contains('\0')
            && Encoding.UTF8.GetByteCount(path) <= 100);
        foreach (string part in path.Split('/').Skip(1))
            Require(part.Length > 0 && part is not "." and not ".." && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
        uint brokerUid = Positive(args[3]), brokerGid = Positive(args[5]);
        uint workerUid = Positive(args[7]), workerGid = Positive(args[9]);
        Require(brokerUid != workerUid && brokerGid == workerGid);
        Require(long.TryParse(args[11], NumberStyles.None, Invariant, out long end));
        long remaining = end - UptimeMilliseconds();
        Require(remaining > 0 && remaining <= 600_000); // A ceiling only: root must pass its inherited end.
        return new(path, brokerUid, brokerGid, workerUid, workerGid, end);
    }

    private static uint Positive(string text)
    {
        Require(uint.TryParse(text, NumberStyles.None, Invariant, out uint value) && value is > 0 and < uint.MaxValue);
        return value;
    }

    /// <summary>Reads the same Linux uptime domain as the Bash fixture, including time spent suspended.</summary>
    private static long UptimeMilliseconds()
    {
        byte[] bytes = ReadBounded("/proc/uptime", 128);
        string text = Encoding.ASCII.GetString(bytes);
        int space = text.IndexOf(' ');
        Require(space > 0);
        string stamp = text[..space];
        Require(stamp.Count(c => c == '.') == 1 && stamp.All(c => char.IsAsciiDigit(c) || c == '.'));
        Require(decimal.TryParse(stamp, NumberStyles.AllowDecimalPoint, Invariant, out decimal seconds)
            && seconds > 0 && seconds < long.MaxValue / 1000m);
        return decimal.ToInt64(decimal.Floor(seconds * 1000m));
    }

    private static ulong SelfStart(int pid)
    {
        string text = Encoding.ASCII.GetString(ReadBounded("/proc/self/stat", 4096));
        int left = text.IndexOf('('), right = text.LastIndexOf(')');
        Require(left > 0 && right > left && int.Parse(text[..left].Trim(), Invariant) == pid);
        string[] fields = text[(right + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Require(fields.Length > 19 && ulong.TryParse(fields[19], NumberStyles.None, Invariant, out ulong start) && start > 0);
        return ulong.Parse(fields[19], Invariant);
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128);
        byte[] buffer = new byte[maximum + 1];
        int count = 0;
        while (count < buffer.Length)
        {
            int got = stream.Read(buffer, count, buffer.Length - count);
            if (got == 0) break;
            count += got;
        }
        Require(count is > 0 && count <= maximum);
        return buffer[..count];
    }

    private static Credentials ReadPeer(Socket socket)
    {
        uint size = 12;
        Require(GetPeerCredentials(socket.SafeHandle, 1, 17, out Credentials value, ref size) == 0
            && size == 12 && value.Pid > 0);
        return value;
    }

    private static string Numbers(params (string Name, object Value)[] facts) => string.Join(",",
        facts.Select(f => "\"" + f.Name + "\":" + Convert.ToString(f.Value, Invariant)));

    private static async Task EmitAsync(string packet, Deadline clock)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(packet + "\n");
        Require(bytes.Length <= 1024);
        clock.Check();
        // This is a diagnostic stdout wrapper, never the worker socket or a completion authority.
        using var output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes.AsMemory(), clock.Token).ConfigureAwait(false);
        await output.FlushAsync(clock.Token).ConfigureAwait(false);
        clock.Check();
    }

    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("broker-rejected"); }
    private sealed record Selection(string Socket, uint BrokerUid, uint BrokerGid, uint WorkerUid, uint WorkerGid, long End);

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials : IEquatable<Credentials>
    {
        internal int Pid;
        internal uint Uid;
        internal uint Gid;
        public readonly bool Equals(Credentials other) => Pid == other.Pid && Uid == other.Uid && Gid == other.Gid;
    }

    /// <summary>Polling only wakes checks of the one inherited absolute deadline; it creates no renewed allowance.</summary>
    private sealed class Deadline : IAsyncDisposable
    {
        private readonly long _end;
        private readonly CancellationTokenSource _expired = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _watch;
        private int _failed;
        internal Deadline(long end) { _end = end; _watch = WatchAsync(); }
        internal CancellationToken Token => _expired.Token;
        internal void Check() { Require(Volatile.Read(ref _failed) == 0 && UptimeMilliseconds() < _end); Token.ThrowIfCancellationRequested(); }
        private async Task WatchAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    long remaining = _end - UptimeMilliseconds();
                    if (remaining <= 0) { Interlocked.Exchange(ref _failed, 1); _expired.Cancel(); return; }
                    await Task.Delay((int)Math.Min(50, remaining), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception) { Interlocked.Exchange(ref _failed, 1); _expired.Cancel(); }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                _stop.Cancel();
                await _watch.ConfigureAwait(false);
                Require(Volatile.Read(ref _failed) == 0 && UptimeMilliseconds() < _end);
            }
            finally { _expired.Dispose(); _stop.Dispose(); }
        }
    }

    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "getgid")] private static extern uint GetGid();
    [DllImport("libc", EntryPoint = "getegid")] private static extern uint GetEffectiveGid();
    [DllImport("libc", EntryPoint = "getpid")] private static extern int GetPid();
    [DllImport("libc", EntryPoint = "getgroups")] private static extern int GetGroups(int size, IntPtr groups);
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetPeerCredentials(SafeSocketHandle socket, int level, int option, out Credentials value, ref uint size);
}
