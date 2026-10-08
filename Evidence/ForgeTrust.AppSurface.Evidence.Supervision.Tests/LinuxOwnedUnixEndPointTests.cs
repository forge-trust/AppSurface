using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Endpoint data controls and unprivileged local socket regressions; never root custody acceptance.</summary>
public sealed class LinuxOwnedUnixEndPointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.sock")]
    [InlineData("/")]
    [InlineData("//control.sock")]
    [InlineData("/tmp//control.sock")]
    [InlineData("/tmp/./control.sock")]
    [InlineData("/tmp/../control.sock")]
    [InlineData("/tmp/control.sock/")]
    [InlineData("/tmp/\0control.sock")]
    [InlineData("/tmp/\ncontrol.sock")]
    [InlineData("/tmp/$control.sock")]
    [InlineData("/tmp/%control.sock")]
    public void InvalidPathIsRejectedBeforeStandardSerialization(string? path) =>
        Assert.ThrowsAny<ArgumentException>(() => new LinuxOwnedUnixEndPoint(path!));

    [Fact]
    public void InvalidUtf16CannotBeSilentlyEncoded()
    {
        // Construct inside the test: attribute-data serialization can replace an unpaired surrogate.
        var path = "/tmp/" + new string((char)0xd800, 1);
        Assert.Throws<ArgumentException>(() => new LinuxOwnedUnixEndPoint(path));
    }

    [Fact]
    public void Utf8ByteBoundRejectsMultibyteAndAsciiOverflow()
    {
        Assert.Throws<ArgumentException>(() => new LinuxOwnedUnixEndPoint("/" + new string('a', 100)));
        Assert.Throws<ArgumentException>(() => new LinuxOwnedUnixEndPoint("/tmp/" + new string('é', 48)));
    }

    [Fact]
    public void StandardSerializationAndCreateRoundTripPreservePath()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxOwnedUnixEndPoint("/tmp/control.sock"));
            return; // Explicit platform guard, not a socket or custody success.
        }
        var path = "/tmp/" + new string('é', 47) + "a"; // Exactly 100 UTF-8 bytes.
        var owned = new LinuxOwnedUnixEndPoint(path);
        var standard = new UnixDomainSocketEndPoint(path);
        Assert.Equal(AddressFamily.Unix, owned.AddressFamily);
        Assert.Equal(path, owned.ToString());
        Assert.Equal(AddressBytes(standard.Serialize()), AddressBytes(owned.Serialize()));
        var restored = Assert.IsType<UnixDomainSocketEndPoint>(owned.Create(owned.Serialize()));
        Assert.Equal(path, restored.ToString());
        Assert.IsNotType<UnixDomainSocketEndPoint>((EndPoint)owned);
    }

    [Fact]
    public void SerializedAddressMutationCannotChangeSubsequentSerialization()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxOwnedUnixEndPoint("/tmp/control.sock"));
            return;
        }
        var endpoint = new LinuxOwnedUnixEndPoint("/tmp/control.sock");
        var baseline = AddressBytes(endpoint.Serialize());
        var supplied = endpoint.Serialize();
        supplied[supplied.Size - 1] ^= 1;
        Assert.Equal(baseline, AddressBytes(endpoint.Serialize()));
    }

    [Theory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public void ForeignAddressFamilyCannotCreateUnixEndpoint(AddressFamily family)
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxOwnedUnixEndPoint("/tmp/control.sock"));
            return;
        }
        var endpoint = new LinuxOwnedUnixEndPoint("/tmp/control.sock");
        Assert.Throws<ArgumentException>(() => endpoint.Create(new SocketAddress(family)));
    }

    [Fact]
    public void NullKernelAddressIsRejected()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxOwnedUnixEndPoint("/tmp/control.sock"));
            return;
        }
        Assert.Throws<ArgumentNullException>(() => new LinuxOwnedUnixEndPoint("/tmp/control.sock").Create(null!));
    }

    [Fact]
    public void UnnamedUnixKernelAddressRemainsStandardEndpointData()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxOwnedUnixEndPoint("/tmp/control.sock"));
            return;
        }
        var address = new SocketAddress(AddressFamily.Unix, 2);
        var expected = new UnixDomainSocketEndPoint("/tmp/control.sock").Create(address);
        var actual = new LinuxOwnedUnixEndPoint("/tmp/control.sock").Create(address);
        Assert.IsType<UnixDomainSocketEndPoint>(actual);
        Assert.Equal(expected.ToString(), actual.ToString());
        Assert.Equal(AddressFamily.Unix, actual.AddressFamily);
    }

    [UnixSocketFact]
    public async Task OriginalIoTasksJoinAndDisposeRetainsOwnedSocketName()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = FreshDirectory();
        var path = Path.Combine(directory, "control.sock");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new LinuxOwnedUnixEndPoint(path));
            listener.Listen(1);
            var local = Assert.IsType<UnixDomainSocketEndPoint>(listener.LocalEndPoint);
            Assert.Equal(path, local.ToString());
            var originalMode = File.GetUnixFileMode(path);
            await ExchangeAndJoinAsync(listener, path, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            Assert.True(File.Exists(path));
            Assert.Equal(originalMode, File.GetUnixFileMode(path));
            using var collision = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            Assert.Throws<SocketException>(() => collision.Bind(new LinuxOwnedUnixEndPoint(path)));
            Assert.True(File.Exists(path));
            deadline.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            listener.Dispose();
            File.Delete(path); // Only this test's fresh, owned name; never product custody.
            Directory.Delete(directory);
        }
    }

    [UnixSocketFact]
    public void ExistingSocketCollisionDoesNotDeleteTheFirstOwnedName()
    {
        var directory = FreshDirectory();
        var path = Path.Combine(directory, "control.sock");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var first = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var second = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            first.Bind(new LinuxOwnedUnixEndPoint(path));
            Assert.Throws<SocketException>(() => second.Bind(new LinuxOwnedUnixEndPoint(path)));
            second.Dispose();
            first.Dispose();
            Assert.True(File.Exists(path));
            deadline.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            second.Dispose(); first.Dispose();
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [UnixSocketFact]
    public void DisposeDoesNotDeleteARegularSentinelReplacingTheOwnedSocketName()
    {
        var directory = FreshDirectory();
        var path = Path.Combine(directory, "control.sock");
        byte[] sentinel = [7, 11, 13, 17];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            listener.Bind(new LinuxOwnedUnixEndPoint(path));
            File.Delete(path); // Deliberate replacement solely inside this private test directory.
            File.WriteAllBytes(path, sentinel);
            listener.Dispose();
            Assert.Equal(sentinel, File.ReadAllBytes(path));
            Assert.Equal((long)sentinel.Length, new FileInfo(path).Length);
            deadline.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            listener.Dispose();
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static byte[] AddressBytes(SocketAddress address) =>
        Enumerable.Range(0, address.Size).Select(index => address[index]).ToArray();

    private static string FreshDirectory()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = "/tmp/asuds-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Fresh test directory unavailable.");
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    // Uses only the original BCL socket tasks. Closing every actual socket precedes joining all tasks;
    // no canceled WaitAsync proxy, sleep, root factory, peer identity or filesystem custody seam is involved.
    private static async Task ExchangeAndJoinAsync(Socket listener, string path, CancellationToken token)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Socket? accepted = null;
        var operations = new List<Task>();
        Task<Socket>? accept = null;
        Exception? failure = null;
        try
        {
            accept = listener.AcceptAsync(token).AsTask();
            operations.Add(accept);
            var connect = client.ConnectAsync(new UnixDomainSocketEndPoint(path), token).AsTask();
            operations.Add(connect);
            await connect;
            accepted = await accept;
            byte[] sent = [42];
            byte[] received = [0];
            var send = client.SendAsync(sent.AsMemory(), SocketFlags.None, token).AsTask();
            operations.Add(send);
            var receive = accepted.ReceiveAsync(received.AsMemory(), SocketFlags.None, token).AsTask();
            operations.Add(receive);
            Assert.Equal(1, await send);
            Assert.Equal(1, await receive);
            Assert.Equal(sent, received);
            client.Shutdown(SocketShutdown.Send);
            var eof = accepted.ReceiveAsync(received.AsMemory(), SocketFlags.None, token).AsTask();
            operations.Add(eof);
            Assert.Equal(0, await eof);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { listener.Dispose(); } catch (Exception error) { failure ??= error; }
            try { client.Dispose(); } catch (Exception error) { failure ??= error; }
            try { accepted?.Dispose(); } catch (Exception error) { failure ??= error; }
            try { await Task.WhenAll(operations); } catch (Exception error) { failure ??= error; }
            if (accept?.IsCompletedSuccessfully == true)
                try { accept.Result.Dispose(); } catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Unsupported platforms are explicitly skipped. A skipped case is not an executed socket regression.
    private sealed class UnixSocketFactAttribute : FactAttribute
    {
        public UnixSocketFactAttribute()
        {
            if (!Socket.OSSupportsUnixDomainSockets
                || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()))
                Skip = "Requires supported unprivileged UNIX sockets; no kernel custody acceptance is claimed.";
        }
    }
}
