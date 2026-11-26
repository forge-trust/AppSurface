using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Holds the single AppHost-owned connection to the namespace's heartbeat fence.</summary>
/// <remarks>Frames contain only the nonsecret run id. No reconnect is attempted after the first acknowledgement.
/// Closing or losing the host socket ends the anchor; an eight-second frame deadline also fences a stalled host.
/// This channel assumes the qualified trusted host/daemon and is not authentication against either principal.</remarks>
internal sealed class AppSurfaceKeycloakOwnerConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly byte[] _frame;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _close;
    private readonly Task _pulse;
    private int _disposed;

    private AppSurfaceKeycloakOwnerConnection(TcpClient client, byte[] frame, Action lost, CancellationToken owner)
    {
        _client = client; _frame = frame;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner);
        _close = _lifetime.Token.Register(client.Dispose);
        _pulse = PulseAsync(lost);
    }

    /// <summary>Connects to an exact loopback publication and returns only after the anchor acknowledged its first frame.</summary>
    /// <param name="port">The run's separately reserved loopback control port.</param>
    /// <param name="ownerId">Nonsecret GUID run identifier carried by every frame.</param>
    /// <param name="lost">Latches a safe host failure if the channel fails while the owner is running.</param>
    /// <param name="cancellationToken">Cancels connection setup and the entire subsequent heartbeat lifetime.</param>
    /// <param name="connect">Optional connector for testing socket failure and cancellation deterministically.</param>
    internal static async Task<AppSurfaceKeycloakOwnerConnection> ConnectAsync(
        int port,
        string ownerId,
        Action lost,
        CancellationToken cancellationToken,
        Func<TcpClient, int, CancellationToken, ValueTask>? connect = null)
    {
        if (port is < 1024 or > 65535 || !Guid.TryParseExact(ownerId, "N", out _)) throw new ArgumentException("Invalid owner connection.");
        connect ??= static (client, port, token) => client.ConnectAsync(IPAddress.Loopback, port, token);
        var frame = Encoding.ASCII.GetBytes(ownerId + "\n");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try { await connect(client, port, deadline.Token); }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(50, deadline.Token);
                continue;
            }
            catch { client.Dispose(); throw; }
            try
            {
                await RoundAsync(client.GetStream(), frame, deadline.Token);
                return new(client, frame, lost, cancellationToken);
            }
            catch { client.Dispose(); throw; }
        }
    }

    private async Task PulseAsync(Action lost)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                await RoundAsync(_client.GetStream(), _frame, deadline.Token);
            }
        }
        catch { if (!_lifetime.IsCancellationRequested) lost(); }
        finally { _client.Dispose(); }
    }

    private static async Task RoundAsync(NetworkStream stream, byte[] frame, CancellationToken ct)
    {
        await stream.WriteAsync(frame, ct);
        var reply = new byte[1];
        await stream.ReadExactlyAsync(reply, ct);
        if (reply[0] != (byte)'R') throw new IOException("Owner acknowledgement unavailable.");
    }

    /// <summary>Observes completion under the caller's existing stop deadline.</summary>
    internal async Task ObserveStoppedAsync(CancellationToken ct)
    {
        Dispose();
        await _pulse.WaitAsync(ct);
        _lifetime.Dispose();
    }

    /// <summary>Closes the sole connection immediately; it never starts or stops a Docker workload itself.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _client.Dispose();
        _close.Dispose();
    }
}
