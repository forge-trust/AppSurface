using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

internal sealed class ControlledHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private readonly Channel<Uri> _requests = Channel.CreateUnbounded<Uri>();
    private int _requestCount;
    private int _disposed;

    internal int RequestCount => Volatile.Read(ref _requestCount);
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal ConcurrentQueue<HttpRequestMessage> SeenRequests { get; } = new();

    internal ValueTask<Uri> NextRequestAsync(CancellationToken cancellationToken = default) =>
        new(_requests.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(ReadinessTestData.TestTimeout, cancellationToken));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        SeenRequests.Enqueue(request);
        await _requests.Writer.WriteAsync(request.RequestUri!, CancellationToken.None).ConfigureAwait(false);
        return await send(request, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
        }

        base.Dispose(disposing);
    }
}

internal sealed class ObservedFakeTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _createdTimers = Channel.CreateUnbounded<TimeSpan>();
    private readonly ConcurrentBag<TimeSpan> _timerDurations = [];
    private static readonly TimeSpan TestTimeout = ReadinessTestData.TestTimeout;

    internal int TimerCount(TimeSpan dueTime) => _timerDurations.Count(item => item == dueTime);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _timerDurations.Add(dueTime);
        _createdTimers.Writer.TryWrite(dueTime);
        return timer;
    }

    internal async Task AdvanceWhenTimerCreatedAsync(TimeSpan expectedDueTime, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var dueTime = await _createdTimers.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
            if (dueTime == expectedDueTime)
            {
                Advance(dueTime);
                return;
            }
        }
    }

    internal async Task AdvanceWhenNthTimerCreatedAsync(TimeSpan expectedDueTime, int occurrence, CancellationToken cancellationToken = default)
    {
        var matches = 0;
        while (matches < occurrence)
        {
            var dueTime = await _createdTimers.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
            if (dueTime == expectedDueTime)
            {
                matches++;
            }
        }

        Advance(expectedDueTime);
    }

    internal async Task AdvanceWhenTimerCountReachedAsync(TimeSpan expectedDueTime, int expectedCount, CancellationToken cancellationToken = default)
    {
        while (TimerCount(expectedDueTime) < expectedCount)
        {
            _ = await _createdTimers.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
        }

        Advance(expectedDueTime);
    }
}

internal sealed class TrackingContent : HttpContent
{
    private static readonly byte[] Body = "body-that-must-not-be-read"u8.ToArray();
    private readonly Action? _onDispose;
    private int _disposeCallbackInvoked;

    internal TrackingContent(Action? onDispose = null)
    {
        _onDispose = onDispose;
    }

    internal int SerializeCount { get; private set; }
    internal bool WasDisposed { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        SerializeCount++;
        return stream.WriteAsync(Body).AsTask();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = Body.Length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WasDisposed = true;
            if (Interlocked.Exchange(ref _disposeCallbackInvoked, 1) == 0)
            {
                _onDispose?.Invoke();
            }
        }

        base.Dispose(disposing);
    }
}

internal static class ReadinessTestData
{
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    internal static readonly Uri Endpoint = new("https://readiness.example:8443/apps/service/");

    internal static HttpResponseMessage Response(HttpStatusCode status, TrackingContent? content = null) =>
        new(status) { Content = content ?? new TrackingContent() };

    internal static HttpClient Client(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(handler, disposeHandler: false) { Timeout = timeout ?? Timeout.InfiniteTimeSpan };

    internal static HttpReadinessOptions OneAttempt(TimeSpan? requestTimeout = null) => new()
    {
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3),
        RetryDelay = TimeSpan.FromMilliseconds(1),
        MaxAttempts = 1,
    };
}
