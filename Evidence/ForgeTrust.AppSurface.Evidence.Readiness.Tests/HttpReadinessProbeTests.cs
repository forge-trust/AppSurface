using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

public sealed class HttpReadinessProbeTests
{
    [Fact]
    public async Task Default_policy_requires_exact_200_and_disposes_without_reading_body()
    {
        var content = new TrackingContent();
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.OK, content)));
        using var client = ReadinessTestData.Client(handler);

        await HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5));

        var request = Assert.Single(handler.SeenRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri(ReadinessTestData.Endpoint, "healthz"), request.RequestUri);
        Assert.Equal(0, content.SerializeCount);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task Status_predicate_can_accept_a_non_200_status()
    {
        HttpStatusCode? observed = null;
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.NoContent)));
        using var client = ReadinessTestData.Client(handler);

        await HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "/healthz", TimeSpan.FromSeconds(5), new()
        {
            IsSuccess = status =>
            {
                observed = status;
                return status == HttpStatusCode.NoContent;
            },
            MaxAttempts = 1,
        });

        Assert.Equal(HttpStatusCode.NoContent, observed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Rejected_status_is_retried_and_attempt_limit_keeps_the_last_status()
    {
        var time = new ObservedFakeTimeProvider();
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable)));
        using var client = ReadinessTestData.Client(handler);
        var retryDelay = TimeSpan.FromMilliseconds(5);
        var operation = HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromMinutes(1), new()
        {
            RetryDelay = retryDelay,
            MaxAttempts = 3,
        }, cancellationToken: default, timeProvider: time);

        await time.AdvanceWhenTimerCreatedAsync(retryDelay);
        await time.AdvanceWhenTimerCreatedAsync(retryDelay);
        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessFailureCategory.AttemptLimit, exception.Category);
        Assert.Equal(ReadinessFailureCategory.StatusRejected, exception.LastCause);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.LastStatusCode);
        Assert.Equal(3, exception.Attempts);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(2, time.TimerCount(retryDelay));
    }

    [Fact]
    public async Task Predicate_exception_is_terminal_and_preserves_the_original_exception()
    {
        var sentinel = new InvalidOperationException("predicate-secret");
        var content = new TrackingContent();
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable, content)));
        using var client = ReadinessTestData.Client(handler);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => HttpReadinessProbe.WaitAsync(
            client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5), new()
            {
                IsSuccess = _ => throw sentinel,
                MaxAttempts = 10,
            }));

        Assert.Same(sentinel, actual);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(content.WasDisposed);
    }

    [Fact]
    public async Task Caller_cancellation_during_response_disposal_wins_over_success()
    {
        using var caller = new CancellationTokenSource();
        var content = new TrackingContent(caller.Cancel);
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.OK, content)));
        using var client = ReadinessTestData.Client(handler);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpReadinessProbe.WaitAsync(
            client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5), cancellationToken: caller.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.True(content.WasDisposed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task HttpRequestException_is_retried_and_generated_diagnostics_keep_only_safe_classification()
    {
        const string transportSecret = "transport-secret-marker";
        const string uriSecret = "path-secret-marker";
        var time = new ObservedFakeTimeProvider();
        var calls = 0;
        var handler = new ControlledHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    HttpRequestError.NameResolutionError, transportSecret, null, null));
            }

            return Task.FromResult(ReadinessTestData.Response(HttpStatusCode.ServiceUnavailable));
        });
        using var client = ReadinessTestData.Client(handler);
        var retryDelay = TimeSpan.FromMilliseconds(4);
        var operation = HttpReadinessProbe.WaitAsync(client, new Uri("https://readiness.example/private?token=" + uriSecret),
            "healthz", TimeSpan.FromMinutes(1), new() { RetryDelay = retryDelay, MaxAttempts = 2 }, cancellationToken: default, timeProvider: time);

        await time.AdvanceWhenTimerCreatedAsync(retryDelay);
        var exception = await Assert.ThrowsAsync<ReadinessException>(() => operation.WaitAsync(ReadinessTestData.TestTimeout));

        Assert.Equal(ReadinessFailureCategory.AttemptLimit, exception.Category);
        Assert.Equal(ReadinessFailureCategory.StatusRejected, exception.LastCause);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.LastStatusCode);
        Assert.Null(exception.LastHttpRequestError);
        Assert.Equal(2, exception.Attempts);
        Assert.DoesNotContain(transportSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(uriSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Terminal_transport_failure_exposes_http_error_enum_without_raw_message()
    {
        const string secret = "raw-transport-secret";
        var handler = new ControlledHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.ConnectionError, secret, null, null)));
        using var client = ReadinessTestData.Client(handler);

        var exception = await Assert.ThrowsAsync<ReadinessException>(() => HttpReadinessProbe.WaitAsync(
            client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5), ReadinessTestData.OneAttempt()));

        Assert.Equal(ReadinessFailureCategory.AttemptLimit, exception.Category);
        Assert.Equal(ReadinessFailureCategory.TransportFailure, exception.LastCause);
        Assert.Equal(HttpRequestError.ConnectionError, exception.LastHttpRequestError);
        Assert.Null(exception.LastStatusCode);
        Assert.Equal(1, exception.Attempts);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(ReadinessPhase.Creation, "Startup deadline expired.", "Startup phase: Creation.", "Check cancellation observation and host-local logs.")]
    [InlineData(ReadinessPhase.Build, "Startup deadline expired.", "Startup phase: Build.", "Check cancellation observation and host-local logs.")]
    [InlineData(ReadinessPhase.Start, "Startup deadline expired.", "Startup phase: Start.", "Check cancellation observation and host-local logs.")]
    [InlineData(ReadinessPhase.ResourceHealth, "Resource deadline expired.", "The native resource health condition did not complete.", "Check the explicit resource identity and configured health condition.")]
    [InlineData(ReadinessPhase.EndpointResolution, "Readiness deadline expired.", "The original operation budget was exhausted.", "Check the failed phase, retained HTTP cause, and explicit timeout.")]
    [InlineData(ReadinessPhase.Http, "Readiness deadline expired.", "The original operation budget was exhausted.", "Check the failed phase, retained HTTP cause, and explicit timeout.")]
    [InlineData(ReadinessPhase.Cleanup, "Readiness deadline expired.", "The original operation budget was exhausted.", "Check the failed phase, retained HTTP cause, and explicit timeout.")]
    public void Deadline_recipes_are_phase_specific_and_have_safe_empty_cause_fields(
        ReadinessPhase phase, string problem, string cause, string nextAction)
    {
        var exception = new ReadinessException(phase, ReadinessFailureCategory.Deadline);

        Assert.Equal(problem, exception.Problem);
        Assert.Equal(cause, exception.Cause);
        Assert.Equal(nextAction, exception.NextAction);
        Assert.Null(exception.LastCause);
        Assert.Null(exception.LastStatusCode);
        Assert.Null(exception.LastHttpRequestError);
        Assert.Equal(0, exception.Attempts);
        Assert.Equal("https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#troubleshooting", exception.DocumentationUrl);
    }

    [Theory]
    [InlineData(ReadinessFailureCategory.AttemptLimit, "Readiness attempt limit reached.", "Every permitted request failed its condition.", "Check the last cause and the explicit attempt cap.")]
    [InlineData(ReadinessFailureCategory.RequestTimeout, "HTTP request timed out.", "The request budget or a shorter client timer expired.", "Check responsiveness and the explicit request or client limits.")]
    [InlineData(ReadinessFailureCategory.EndpointUnavailable, "Named HTTP endpoint unavailable.", "The explicit resource or endpoint could not be resolved to HTTP(S).", "Check the resource name, endpoint name, and allocated endpoint.")]
    [InlineData(ReadinessFailureCategory.CleanupFailure, "Owned cleanup failed.", "Owned cleanup exception type: Exception.", "Inspect owner cleanup without claiming readiness.")]
    public void Non_http_failure_recipes_are_fixed_and_safe(
        ReadinessFailureCategory category, string problem, string cause, string nextAction)
    {
        var exception = new ReadinessException(ReadinessPhase.Http, category);

        Assert.Equal(problem, exception.Problem);
        Assert.Equal(cause, exception.Cause);
        Assert.Equal(nextAction, exception.NextAction);
        Assert.Null(exception.LastCause);
        Assert.Null(exception.LastStatusCode);
        Assert.Null(exception.LastHttpRequestError);
        Assert.Equal(0, exception.Attempts);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), "InvalidOperationException")]
    [InlineData(typeof(ObjectDisposedException), "ObjectDisposedException")]
    [InlineData(typeof(TimeoutException), "TimeoutException")]
    [InlineData(typeof(OperationCanceledException), "OperationCanceledException")]
    [InlineData(typeof(HttpRequestException), "HttpRequestException")]
    [InlineData(typeof(AggregateException), "AggregateException")]
    [InlineData(typeof(SecretCleanupMarker), "Exception")]
    public void Cleanup_recipe_uses_only_fixed_safe_type_names(Type type, string expected)
    {
        var error = new ReadinessException(ReadinessPhase.Cleanup, ReadinessFailureCategory.CleanupFailure,
            cleanupExceptionType: type);
        Assert.Equal($"Owned cleanup exception type: {expected}.", error.Cause);
        Assert.DoesNotContain(nameof(SecretCleanupMarker), error.ToString());
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
    }

    private sealed class SecretCleanupMarker : Exception;

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    public void Status_recipe_formats_only_the_numeric_status(int status)
    {
        var exception = new ReadinessException(ReadinessPhase.Http, ReadinessFailureCategory.StatusRejected,
            new(1, ReadinessFailureCategory.StatusRejected, (HttpStatusCode)status));

        Assert.Equal("HTTP condition rejected.", exception.Problem);
        Assert.Equal($"HTTP status: {status}.", exception.Cause);
        Assert.Equal("Check the path and predicate; check caller authentication for HTTP 401 or 403.", exception.NextAction);
        Assert.Equal(ReadinessFailureCategory.StatusRejected, exception.LastCause);
        Assert.Equal((HttpStatusCode)status, exception.LastStatusCode);
        Assert.Null(exception.LastHttpRequestError);
        Assert.Equal(1, exception.Attempts);
    }

    [Fact]
    public void Transport_recipe_formats_only_the_http_error_enum()
    {
        var exception = new ReadinessException(ReadinessPhase.Http, ReadinessFailureCategory.TransportFailure,
            new(2, ReadinessFailureCategory.TransportFailure, HttpError: HttpRequestError.NameResolutionError));

        Assert.Equal("HTTP transport failed.", exception.Problem);
        Assert.Equal("HTTP transport category: NameResolutionError.", exception.Cause);
        Assert.Equal("Check reachability and caller proxy or certificate policy.", exception.NextAction);
        Assert.Equal(ReadinessFailureCategory.TransportFailure, exception.LastCause);
        Assert.Null(exception.LastStatusCode);
        Assert.Equal(HttpRequestError.NameResolutionError, exception.LastHttpRequestError);
        Assert.Equal(2, exception.Attempts);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Probe_preserves_borrowed_client_configuration_and_lifetime()
    {
        var contents = new ConcurrentQueue<TrackingContent>();
        var handler = new ControlledHandler((_, _) =>
        {
            var content = new TrackingContent();
            contents.Enqueue(content);
            return Task.FromResult(ReadinessTestData.Response(HttpStatusCode.OK, content));
        });
        using var client = ReadinessTestData.Client(handler, TimeSpan.FromSeconds(20));
        var originalBaseAddress = new Uri("https://caller.example/root/");
        client.BaseAddress = originalBaseAddress;
        client.DefaultRequestHeaders.Add("X-Caller-Setting", "preserved");
        var originalHeaders = client.DefaultRequestHeaders.ToString();

        await HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5));

        Assert.Equal(originalBaseAddress, client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
        Assert.Equal(originalHeaders, client.DefaultRequestHeaders.ToString());
        Assert.False(handler.IsDisposed);
        Assert.True(Assert.Single(contents).WasDisposed);

        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, new Uri("https://caller.example/after"));
        using var secondResponse = await client.SendAsync(secondRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(2, handler.RequestCount);
        Assert.False(handler.IsDisposed);
    }

    [Fact]
    public async Task Unexpected_handler_cancellation_is_terminal_and_not_retried()
    {
        var sentinel = new OperationCanceledException("unrelated cancellation");
        var handler = new ControlledHandler((_, _) => Task.FromException<HttpResponseMessage>(sentinel));
        using var client = ReadinessTestData.Client(handler);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => HttpReadinessProbe.WaitAsync(
            client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5), new()
            {
                MaxAttempts = 4,
            }));

        Assert.Same(sentinel, actual);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Real_shorter_HttpClient_timeout_is_classified_and_bounded()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMilliseconds(120) };
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ReadinessException>(() => HttpReadinessProbe.WaitAsync(
            client, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(5), new()
            {
                RequestTimeout = TimeSpan.FromSeconds(2),
                RetryDelay = TimeSpan.FromMilliseconds(1),
                MaxAttempts = 1,
            }));

        Assert.True(entered.Task.IsCompletedSuccessfully);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"The shorter client timeout took {stopwatch.Elapsed}.");
        Assert.Equal(ReadinessFailureCategory.RequestTimeout, exception.LastCause);
        Assert.Equal(1, exception.Attempts);
    }
}
