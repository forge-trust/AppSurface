using System.Net;

namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

public sealed class ReadinessDeadlineTests
{
    [Fact]
    public async Task Later_http_check_with_default_attempt_retains_latest_safe_metadata()
    {
        var timeout = TimeSpan.FromSeconds(5);
        var time = new ObservedFakeTimeProvider();
        using var deadline = new ReadinessDeadline(timeout, time, CancellationToken.None);
        var latest = new ReadinessAttempt(3, ReadinessFailureCategory.StatusRejected, HttpStatusCode.ServiceUnavailable);

        deadline.Check(ReadinessPhase.Http, latest);
        await time.AdvanceWhenTimerCreatedAsync(timeout).WaitAsync(ReadinessTestData.TestTimeout);

        var exception = Assert.Throws<ReadinessException>(() => deadline.Check(ReadinessPhase.Http));

        Assert.Equal(ReadinessPhase.Http, exception.Phase);
        Assert.Equal(ReadinessFailureCategory.Deadline, exception.Category);
        Assert.Equal(ReadinessFailureCategory.StatusRejected, exception.LastCause);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.LastStatusCode);
        Assert.Null(exception.LastHttpRequestError);
        Assert.Equal(3, exception.Attempts);
    }

    [Fact]
    public async Task Pre_cancelled_caller_is_preserved_and_prevents_dispatch()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.OK)));
        using var client = ReadinessTestData.Client(handler);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpReadinessProbe.WaitAsync(
            client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5), cancellationToken: caller.Token)
            .WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_gated_request_keeps_the_original_token()
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var client = ReadinessTestData.Client(handler);
        var operation = HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromMinutes(1),
            cancellationToken: caller.Token);

        await entered.Task.WaitAsync(ReadinessTestData.TestTimeout);
        caller.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Caller_cancellation_wins_when_deadline_also_expires_before_a_late_success()
    {
        using var caller = new CancellationTokenSource();
        var time = new ObservedFakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledHandler((_, _) =>
        {
            entered.TrySetResult();
            return response.Task;
        });
        using var client = ReadinessTestData.Client(handler);
        var deadline = TimeSpan.FromSeconds(5);
        var operation = HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", deadline,
            new() { RequestTimeout = TimeSpan.FromSeconds(10) }, cancellationToken: caller.Token, timeProvider: time);

        await entered.Task.WaitAsync(ReadinessTestData.TestTimeout);
        await time.AdvanceWhenTimerCountReachedAsync(deadline, 2).WaitAsync(ReadinessTestData.TestTimeout);
        caller.Cancel();
        response.TrySetResult(ReadinessTestData.Response(HttpStatusCode.OK));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Late_success_after_the_original_deadline_is_rejected()
    {
        var time = new ObservedFakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledHandler((_, _) =>
        {
            entered.TrySetResult();
            return response.Task;
        });
        using var client = ReadinessTestData.Client(handler);
        var deadline = TimeSpan.FromSeconds(5);
        var operation = HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", deadline,
            new() { RequestTimeout = TimeSpan.FromSeconds(10) }, cancellationToken: default, timeProvider: time);

        await entered.Task.WaitAsync(ReadinessTestData.TestTimeout);
        await time.AdvanceWhenTimerCountReachedAsync(deadline, 2).WaitAsync(ReadinessTestData.TestTimeout);
        response.TrySetResult(ReadinessTestData.Response(HttpStatusCode.OK));
        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessPhase.Http, exception.Phase);
        Assert.Equal(ReadinessFailureCategory.Deadline, exception.Category);
        Assert.Equal(ReadinessFailureCategory.RequestTimeout, exception.LastCause);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Per_request_timeout_cannot_be_overridden_by_a_late_success_from_an_ignoring_handler()
    {
        var time = new ObservedFakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledHandler((_, _) =>
        {
            entered.TrySetResult();
            return response.Task;
        });
        using var client = ReadinessTestData.Client(handler);
        var requestTimeout = TimeSpan.FromMilliseconds(40);
        var operation = HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5),
            new() { RequestTimeout = requestTimeout, MaxAttempts = 1 }, cancellationToken: default, timeProvider: time);

        await entered.Task.WaitAsync(ReadinessTestData.TestTimeout);
        await time.AdvanceWhenTimerCreatedAsync(requestTimeout).WaitAsync(ReadinessTestData.TestTimeout);
        var content = new TrackingContent();
        response.TrySetResult(ReadinessTestData.Response(HttpStatusCode.OK, content));
        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessFailureCategory.AttemptLimit, exception.Category);
        Assert.Equal(ReadinessFailureCategory.RequestTimeout, exception.LastCause);
        Assert.Equal(1, exception.Attempts);
        Assert.True(content.WasDisposed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Request_timeout_is_a_retryable_failure_and_is_retained_at_the_attempt_cap()
    {
        var time = new ObservedFakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var client = ReadinessTestData.Client(handler);
        var requestTimeout = TimeSpan.FromMilliseconds(40);
        var operation = HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5),
            new() { RequestTimeout = requestTimeout, MaxAttempts = 1 }, cancellationToken: default, timeProvider: time);

        await entered.Task.WaitAsync(ReadinessTestData.TestTimeout);
        await time.AdvanceWhenTimerCreatedAsync(requestTimeout).WaitAsync(ReadinessTestData.TestTimeout);
        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessFailureCategory.AttemptLimit, exception.Category);
        Assert.Equal(ReadinessFailureCategory.RequestTimeout, exception.LastCause);
        Assert.Null(exception.LastStatusCode);
        Assert.Equal(1, exception.Attempts);
    }

    [Fact]
    public async Task Retry_delay_is_capped_at_remaining_budget_and_deadline_keeps_last_status()
    {
        var time = new ObservedFakeTimeProvider();
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable)));
        using var client = ReadinessTestData.Client(handler);
        var budget = TimeSpan.FromSeconds(10);
        var operation = HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", budget, new()
        {
            RetryDelay = TimeSpan.FromSeconds(30),
            MaxAttempts = 5,
        }, cancellationToken: default, timeProvider: time);

        // The deadline and capped retry delay each register one 10-second timer.
        await time.AdvanceWhenTimerCountReachedAsync(budget, 2).WaitAsync(ReadinessTestData.TestTimeout);
        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessFailureCategory.Deadline, exception.Category);
        Assert.Equal(ReadinessFailureCategory.StatusRejected, exception.LastCause);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.LastStatusCode);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(2, time.TimerCount(budget));
    }
}
