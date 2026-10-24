using System.Net;

namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

public sealed class HttpReadinessAttemptLimitTests
{
    private static readonly Uri Endpoint = ReadinessTestData.Endpoint;

    [Fact]
    public async Task Explicit_limit_allows_success_on_attempt_30_without_a_31st_request()
    {
        const int maximum = 30;
        var time = new ObservedFakeTimeProvider();
        var calls = 0;
        var handler = new ControlledHandler((_, _) =>
        {
            var attempt = Interlocked.Increment(ref calls);
            var status = attempt == maximum ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            return Task.FromResult(ReadinessTestData.Response(status));
        });
        using var client = ReadinessTestData.Client(handler);
        var delay = TimeSpan.FromMilliseconds(1);
        var operation = HttpReadinessProbe.WaitAsync(client, Endpoint, "healthz", TimeSpan.FromMinutes(1), new()
        {
            RetryDelay = delay,
            MaxAttempts = maximum,
        }, cancellationToken: default, timeProvider: time);

        for (var attempt = 1; attempt < maximum; attempt++)
        {
            await time.AdvanceWhenTimerCreatedAsync(delay);
        }

        await operation.WaitAsync(ReadinessTestData.TestTimeout);

        Assert.Equal(maximum, handler.RequestCount);
        Assert.Equal(maximum - 1, time.TimerCount(delay));
    }

    [Fact]
    public async Task Explicit_limit_stops_failure_on_attempt_30_without_a_31st_request_or_trailing_delay()
    {
        const int maximum = 30;
        var time = new ObservedFakeTimeProvider();
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable)));
        using var client = ReadinessTestData.Client(handler);
        var delay = TimeSpan.FromMilliseconds(1);
        var operation = HttpReadinessProbe.WaitAsync(client, Endpoint, "healthz", TimeSpan.FromMinutes(1), new()
        {
            RetryDelay = delay,
            MaxAttempts = maximum,
        }, cancellationToken: default, timeProvider: time);

        for (var attempt = 1; attempt < maximum; attempt++)
        {
            await time.AdvanceWhenTimerCreatedAsync(delay);
        }

        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessFailureCategory.AttemptLimit, exception.Category);
        Assert.Equal(ReadinessFailureCategory.StatusRejected, exception.LastCause);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.LastStatusCode);
        Assert.Equal(maximum, exception.Attempts);
        Assert.Equal(maximum, handler.RequestCount);
        Assert.Equal(maximum - 1, time.TimerCount(delay));
    }

    [Fact]
    public async Task Fast_status_failures_reach_the_two_minute_480_attempt_boundary_with_constant_clock_state()
    {
        const int expectedAttempts = 480;
        var time = new ObservedFakeTimeProvider();
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable)));
        using var client = ReadinessTestData.Client(handler);
        var retryDelay = TimeSpan.FromMilliseconds(250);
        var budget = TimeSpan.FromMinutes(2);
        var operation = HttpReadinessProbe.WaitAsync(client, Endpoint, "healthz", budget, new()
        {
            RequestTimeout = TimeSpan.FromMilliseconds(100),
            RetryDelay = retryDelay,
        }, cancellationToken: default, timeProvider: time);

        for (var delay = 0; delay < expectedAttempts; delay++)
        {
            await time.AdvanceWhenTimerCreatedAsync(retryDelay);
        }

        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessFailureCategory.Deadline, exception.Category);
        Assert.Equal(ReadinessFailureCategory.StatusRejected, exception.LastCause);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.LastStatusCode);
        Assert.Equal(expectedAttempts, exception.Attempts);
        Assert.Equal(expectedAttempts, handler.RequestCount);
        Assert.Equal(expectedAttempts, time.TimerCount(retryDelay));
        Assert.Equal(budget, time.GetUtcNow() - time.Start);
    }

    [Fact]
    public async Task Separate_concurrent_probes_keep_independent_attempt_caps_and_clocks()
    {
        var firstTime = new ObservedFakeTimeProvider();
        var secondTime = new ObservedFakeTimeProvider();
        var firstHandler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable)));
        var secondHandler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable)));
        using var firstClient = ReadinessTestData.Client(firstHandler);
        using var secondClient = ReadinessTestData.Client(secondHandler);
        var retryDelay = TimeSpan.FromMilliseconds(7);
        var options = new HttpReadinessOptions { RetryDelay = retryDelay, MaxAttempts = 2 };
        var first = HttpReadinessProbe.WaitAsync(firstClient, Endpoint, "first-health", TimeSpan.FromMinutes(1), options,
            cancellationToken: default, timeProvider: firstTime);
        var second = HttpReadinessProbe.WaitAsync(secondClient, Endpoint, "second-health", TimeSpan.FromMinutes(1), options,
            cancellationToken: default, timeProvider: secondTime);

        await Task.WhenAll(
            firstTime.AdvanceWhenTimerCreatedAsync(retryDelay),
            secondTime.AdvanceWhenTimerCreatedAsync(retryDelay));
        var exceptions = await Task.WhenAll(
            Assert.ThrowsAsync<ReadinessException>(() => first.WaitAsync(ReadinessTestData.TestTimeout)),
            Assert.ThrowsAsync<ReadinessException>(() => second.WaitAsync(ReadinessTestData.TestTimeout)));

        Assert.All(exceptions, exception =>
        {
            Assert.Equal(ReadinessFailureCategory.AttemptLimit, exception.Category);
            Assert.Equal(2, exception.Attempts);
        });
        Assert.Equal(2, firstHandler.RequestCount);
        Assert.Equal(2, secondHandler.RequestCount);
        Assert.Collection(firstHandler.SeenRequests,
            request => Assert.Equal(new Uri(Endpoint, "first-health"), request.RequestUri),
            request => Assert.Equal(new Uri(Endpoint, "first-health"), request.RequestUri));
        Assert.Collection(secondHandler.SeenRequests,
            request => Assert.Equal(new Uri(Endpoint, "second-health"), request.RequestUri),
            request => Assert.Equal(new Uri(Endpoint, "second-health"), request.RequestUri));
        Assert.Equal(1, firstTime.TimerCount(retryDelay));
        Assert.Equal(1, secondTime.TimerCount(retryDelay));
    }
}
