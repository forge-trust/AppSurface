using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Tracks one server-side Keycloak owner connection using bounded heartbeat frames.</summary>
/// <remarks>
/// The listener accepts one connection and stops listening immediately after that accept. The initial accept and
/// first frame share one eight-second deadline; subsequent frames and acknowledgements are independently bounded
/// to eight seconds. This is coordination for a same-user process and trusted daemon. It is not authentication
/// against a host administrator or a trusted daemon.
/// </remarks>
internal sealed class KeycloakOwnerHeartbeat : IAsyncDisposable
{
    private const int AwaitingAcknowledgement = 0;
    private const int AcknowledgementInProgress = 1;
    private const int Acknowledged = 2;
    private const int Monitoring = 3;
    private const int Disposed = 4;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);
    private static readonly byte[] Acknowledgement = [KeycloakWorkerProtocol.OwnerHeartbeatAcknowledgement];

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _expectedFrame;
    private readonly TimeSpan _operationTimeout;
    private int _state;

    private KeycloakOwnerHeartbeat(TcpClient client, byte[] expectedFrame, TimeSpan operationTimeout)
    {
        _client = client;
        _stream = client.GetStream();
        _expectedFrame = expectedFrame;
        _operationTimeout = operationTimeout;
    }

    /// <summary>Accepts one owner connection and validates its first fixed-size heartbeat frame.</summary>
    /// <param name="port">The TCP port to bind; ports must be greater than 1024.</param>
    /// <param name="expectedOwner">The expected owner identifier in GUID <c>N</c> format.</param>
    /// <param name="cancellationToken">Cancels the accept or first-frame read.</param>
    /// <param name="listenerReady">Optional nonsecret readiness publication invoked only after the listener has bound.</param>
    /// <returns>A heartbeat tied to the accepted connection.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is outside the supported range.</exception>
    /// <exception cref="ArgumentException"><paramref name="expectedOwner"/> is not a GUID in <c>N</c> format.</exception>
    /// <exception cref="TimeoutException">The connection and first frame did not arrive within eight seconds.</exception>
    /// <exception cref="InvalidDataException">The first frame was incomplete or did not match the expected owner.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the operation.</exception>
    internal static Task<KeycloakOwnerHeartbeat> AcceptAsync(
        int port,
        string expectedOwner,
        CancellationToken cancellationToken, Action? listenerReady = null)
        => AcceptAsync(port, expectedOwner, OperationTimeout, cancellationToken, listenerReady);

    /// <summary>Accepts one owner connection with an injectable deadline for focused protocol tests.</summary>
    /// <param name="port">The TCP port to bind; ports must be greater than 1024.</param>
    /// <param name="expectedOwner">The expected owner identifier in GUID <c>N</c> format.</param>
    /// <param name="operationTimeout">The test deadline used for the initial accept and first frame and later operations.</param>
    /// <param name="cancellationToken">Cancels the accept or first-frame read.</param>
    /// <param name="listenerReady">Optional nonsecret readiness publication invoked only after the listener has bound.</param>
    /// <returns>A heartbeat tied to the accepted connection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The port or test deadline is outside the supported range.</exception>
    /// <exception cref="ArgumentException"><paramref name="expectedOwner"/> is not a GUID in <c>N</c> format.</exception>
    /// <exception cref="TimeoutException">The connection and first frame did not arrive before the deadline.</exception>
    /// <exception cref="InvalidDataException">The first frame was incomplete or did not match the expected owner.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the operation.</exception>
    internal static async Task<KeycloakOwnerHeartbeat> AcceptAsync(
        int port,
        string expectedOwner,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken, Action? listenerReady = null)
    {
        if (port is <= 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "The owner heartbeat port must be between 1025 and 65535.");
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (!Guid.TryParseExact(expectedOwner, "N", out _))
            throw new ArgumentException("The expected owner identifier must be a GUID in N format.", nameof(expectedOwner));
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromDays(24))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));

        var expectedFrame = KeycloakWorkerProtocol.EncodeOwnerHeartbeatFrame(expectedOwner);
        var listener = new TcpListener(IPAddress.Any, port);
        TcpClient? client = null;
        var initialDeadline = new Deadline(operationTimeout);
        try
        {
            listener.Start(1);
            listenerReady?.Invoke();
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            try
            {
                var remaining = initialDeadline.Remaining;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("The Keycloak owner connection timed out.");

                client = await acceptTask.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                listener.Stop();
                await DisposeLateAcceptedClientAsync(acceptTask).ConfigureAwait(false);
                throw new TimeoutException("The Keycloak owner connection timed out.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                listener.Stop();
                await DisposeLateAcceptedClientAsync(acceptTask).ConfigureAwait(false);
                throw;
            }

            listener.Stop();
            var receivedFrame = new byte[KeycloakWorkerProtocol.OwnerHeartbeatFrameLength];
            try
            {
                if (!await ReadFrameAsync(client.GetStream(), receivedFrame, initialDeadline, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidDataException("The Keycloak owner connection ended before its first heartbeat.");

                if (!CryptographicOperations.FixedTimeEquals(receivedFrame, expectedFrame))
                    throw new InvalidDataException("The Keycloak owner heartbeat was rejected.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(receivedFrame);
            }

            var heartbeat = new KeycloakOwnerHeartbeat(client, expectedFrame, operationTimeout);
            client = null;
            expectedFrame = [];
            return heartbeat;
        }
        catch
        {
            listener.Stop();
            client?.Dispose();
            CryptographicOperations.ZeroMemory(expectedFrame);
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Writes the initial one-byte acknowledgement after the caller has published the owner reference.</summary>
    /// <param name="cancellationToken">Cancels the acknowledgement write.</param>
    /// <exception cref="InvalidOperationException">The initial acknowledgement is not available in the current state.</exception>
    /// <exception cref="TimeoutException">The acknowledgement could not be written within the operation deadline.</exception>
    /// <exception cref="IOException">The accepted owner connection failed during acknowledgement.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the operation.</exception>
    internal Task AcknowledgeAsync(CancellationToken cancellationToken) =>
        AcknowledgeAsync(WriteAcknowledgementAsync, cancellationToken);

    /// <summary>Completes the first acknowledgement against a supplied transport write.</summary>
    /// <remarks>Protocol tests can supply a failed write without relying on TCP reset timing.</remarks>
    /// <param name="writeAcknowledgement">Performs the bounded initial acknowledgement write.</param>
    /// <param name="cancellationToken">Cancels the acknowledgement write.</param>
    internal async Task AcknowledgeAsync(
        Func<CancellationToken, Task> writeAcknowledgement, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, AcknowledgementInProgress, AwaitingAcknowledgement)
            != AwaitingAcknowledgement)
            throw new InvalidOperationException("The initial owner acknowledgement is unavailable.");

        try
        {
            await writeAcknowledgement(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, Acknowledged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("The Keycloak owner acknowledgement timed out.");
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new IOException("The Keycloak owner acknowledgement failed.");
        }
    }

    /// <summary>Reads and acknowledges heartbeats until the accepted owner connection is lost.</summary>
    /// <param name="cancellationToken">Cancels monitoring; cancellation is propagated to the caller.</param>
    /// <returns>When EOF, timeout, malformed data, or a network failure ends the owner connection.</returns>
    /// <exception cref="InvalidOperationException">The initial acknowledgement was not completed or monitoring already started.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled monitoring.</exception>
    internal async Task WaitForLossAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, Monitoring, Acknowledged) != Acknowledged)
            throw new InvalidOperationException("Owner monitoring requires a completed initial acknowledgement.");

        while (true)
        {
            var receivedFrame = new byte[KeycloakWorkerProtocol.OwnerHeartbeatFrameLength];
            try
            {
                var frameDeadline = new Deadline(_operationTimeout);
                if (!await ReadFrameAsync(_stream, receivedFrame, frameDeadline, cancellationToken).ConfigureAwait(false)
                    || !CryptographicOperations.FixedTimeEquals(receivedFrame, _expectedFrame))
                {
                    await DisposeAsync().ConfigureAwait(false);
                    return;
                }

                await WriteAcknowledgementAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is TimeoutException || IsConnectionFailure(exception))
            {
                await DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(receivedFrame);
            }
        }
    }

    /// <summary>Closes the accepted connection and clears the buffered owner frame.</summary>
    /// <returns>A completed disposal operation.</returns>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _state, Disposed);
        _stream.Dispose();
        _client.Dispose();
        CryptographicOperations.ZeroMemory(_expectedFrame);
        return ValueTask.CompletedTask;
    }

    private async Task WriteAcknowledgementAsync(CancellationToken cancellationToken)
    {
        var writeTask = _stream.WriteAsync(Acknowledgement.AsMemory(), cancellationToken).AsTask();
        await writeTask.WaitAsync(_operationTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ReadFrameAsync(
        NetworkStream stream,
        byte[] frame,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("The Keycloak owner heartbeat frame timed out.");

            var readTask = stream.ReadAsync(frame.AsMemory(offset, frame.Length - offset), cancellationToken).AsTask();
            var count = await readTask.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return false;

            offset += count;
        }

        return true;
    }

    private static async Task DisposeLateAcceptedClientAsync(Task<TcpClient> acceptTask)
    {
        try
        {
            var lateClient = await acceptTask.ConfigureAwait(false);
            lateClient.Dispose();
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopping the listener cancels a still-pending accept; no late client remains to own.
        }
    }

    private static bool IsConnectionFailure(Exception exception)
        => exception is IOException or SocketException or ObjectDisposedException;

    private readonly struct Deadline
    {
        private readonly long _startedAt;
        private readonly TimeSpan _timeout;

        internal Deadline(TimeSpan timeout)
        {
            _startedAt = Stopwatch.GetTimestamp();
            _timeout = timeout;
        }

        internal TimeSpan Remaining => _timeout - Stopwatch.GetElapsedTime(_startedAt);
    }
}
