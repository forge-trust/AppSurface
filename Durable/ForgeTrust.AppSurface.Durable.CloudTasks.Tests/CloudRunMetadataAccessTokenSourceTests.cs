using System.Net;
using System.Text;
using ForgeTrust.AppSurface.Durable.CloudTasks;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

public sealed class CloudRunMetadataAccessTokenSourceTests
{
    private const string ValidJson = "{\"access_token\":\"metadata-token\",\"token_type\":\"Bearer\",\"expires_in\":60}";

    [Fact]
    public async Task Metadata_source_uses_fixed_endpoint_header_and_returns_bare_token()
    {
        // protects=metadata identity fetch contract; fails_when=endpoint, method, required header, or token shape differs; no_claim=Google-issued identity in local fixtures
        HttpRequestMessage? observed = null;
        using var client = Client((request, _) =>
        {
            observed = request;
            return Task.FromResult(JsonResponse(ValidJson));
        });
        using var source = Source(client, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var token = await source.GetAccessTokenAsync();

        Assert.Equal("metadata-token", token);
        Assert.NotNull(observed);
        Assert.Equal(HttpMethod.Get, observed!.Method);
        Assert.Equal("http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token", observed.RequestUri!.AbsoluteUri);
        Assert.Equal("Google", Assert.Single(observed.Headers.GetValues("Metadata-Flavor")));
    }

    [Fact]
    public async Task Metadata_source_accepts_exact_byte_limit_and_rejects_limit_plus_one()
    {
        // protects=bounded metadata JSON read; fails_when=source buffers beyond 16KiB or rejects exact cap; no_claim=metadata service protocol compatibility beyond fixture bytes
        const string prefix = "{\"access_token\":\"t\",\"token_type\":\"Bearer\",\"expires_in\":60,\"x\":\"";
        const string suffix = "\"}";
        var exact = prefix + new string('a', 16 * 1024 - Encoding.UTF8.GetByteCount(prefix + suffix)) + suffix;
        Assert.Equal(16 * 1024, Encoding.UTF8.GetByteCount(exact));
        using (var client = Client((_, _) => Task.FromResult(JsonResponse(exact))))
        using (var source = Source(client, new FakeTimeProvider(DateTimeOffset.UnixEpoch)))
        {
            Assert.Equal("t", await source.GetAccessTokenAsync());
        }

        using (var client = Client((_, _) => Task.FromResult(JsonResponse(exact + " "))))
        using (var source = Source(client, new FakeTimeProvider(DateTimeOffset.UnixEpoch)))
        {
            await AssertUnavailable(source.GetAccessTokenAsync());
        }
    }

    [Fact]
    public async Task Metadata_source_rejects_non_success_transport_timeout_malformed_json_and_deep_json_as_unavailable()
    {
        // protects=source availability classification; fails_when=HTTP/read/JSON availability failure is misclassified as invalid token; no_claim=remote cause details
        using var forbiddenResponse = new HttpResponseMessage(HttpStatusCode.Forbidden);
        using var forbiddenClient = Client((_, _) => Task.FromResult(forbiddenResponse));
        using var forbidden = Source(forbiddenClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await AssertUnavailable(forbidden.GetAccessTokenAsync());

        using var malformedClient = Client((_, _) => Task.FromResult(JsonResponse("not-json")));
        using var malformed = Source(malformedClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await AssertUnavailable(malformed.GetAccessTokenAsync());

        using var deepClient = Client((_, _) => Task.FromResult(JsonResponse("{\"a\":{\"b\":{\"c\":{\"d\":{}}}}}")));
        using var deep = Source(deepClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await AssertUnavailable(deep.GetAccessTokenAsync());

        using var ioClient = Client((_, _) => throw new HttpRequestException("fixture-secret"));
        using var io = Source(ioClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await AssertUnavailable(io.GetAccessTokenAsync());

        using var readFailureResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingReadStream()),
        };
        using var readFailureClient = Client((_, _) => Task.FromResult(readFailureResponse));
        using var readFailure = Source(readFailureClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await AssertUnavailable(readFailure.GetAccessTokenAsync());

        using var chunkedOversizeResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedOversizeStream()),
        };
        using var chunkedOversizeClient = Client((_, _) => Task.FromResult(chunkedOversizeResponse));
        using var chunkedOversize = Source(chunkedOversizeClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await AssertUnavailable(chunkedOversize.GetAccessTokenAsync());

        var timeoutClock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var timeoutClient = Client(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(ValidJson);
        });
        using var timeout = new CloudRunMetadataAccessTokenSource(
            new CloudRunMetadataAccessTokenOptions(TimeSpan.FromMilliseconds(100)), timeoutClient, timeoutClock);
        var timeoutTask = timeout.GetAccessTokenAsync(new SubmissionBudget(TimeSpan.FromSeconds(2), timeoutClock), CancellationToken.None).AsTask();
        timeoutClock.Advance(TimeSpan.FromMilliseconds(100));
        await AssertUnavailable(new ValueTask<string>(timeoutTask));
    }

    [Fact]
    public async Task Metadata_source_classifies_source_timeout_during_stalled_body_read_as_unavailable()
    {
        // protects=source-timeout classification after response headers; fails_when=body-read timeout is invalid-token or caller cancellation; no_claim=remote metadata service behavior
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((_, _) => Task.FromResult(StalledResponse(readEntered)));
        using var source = new CloudRunMetadataAccessTokenSource(
            new CloudRunMetadataAccessTokenOptions(TimeSpan.FromMilliseconds(100)), client, clock);
        var pending = source.GetAccessTokenAsync(
            new SubmissionBudget(TimeSpan.FromSeconds(2), clock), CancellationToken.None).AsTask();

        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(100));

        await AssertUnavailable(new ValueTask<string>(pending.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Metadata_source_preserves_caller_cancellation_during_stalled_body_read()
    {
        // protects=caller cancellation ownership after response headers; fails_when=body-read cancellation becomes source unavailability or loses the original token; no_claim=remote metadata service behavior
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((_, _) => Task.FromResult(StalledResponse(readEntered)));
        using var source = new CloudRunMetadataAccessTokenSource(
            new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(5)), client, clock);
        using var cancellation = new CancellationTokenSource();
        var pending = source.GetAccessTokenAsync(
            new SubmissionBudget(TimeSpan.FromSeconds(10), clock), cancellation.Token).AsTask();

        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Metadata_source_preserves_submission_budget_cancellation_during_stalled_body_read()
    {
        // protects=submission-budget cancellation after response headers; fails_when=body-read expiry becomes source unavailability; no_claim=remote metadata service behavior
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client((_, _) => Task.FromResult(StalledResponse(readEntered)));
        using var source = new CloudRunMetadataAccessTokenSource(
            new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(5)), client, clock);
        var pending = source.GetAccessTokenAsync(
            new SubmissionBudget(TimeSpan.FromSeconds(1), clock), CancellationToken.None).AsTask();

        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Metadata_source_rejects_non_object_and_declared_oversized_json_without_buffering()
    {
        // protects=JSON object and content-length bounds; fails_when=primitive JSON or declared oversized body is parsed/buffered; no_claim=remote response cause
        using (var primitiveClient = Client((_, _) => Task.FromResult(JsonResponse("[]"))))
        using (var primitive = Source(primitiveClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch)))
        {
            await AssertUnavailable(primitive.GetAccessTokenAsync());
        }

        using var oversizedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[16 * 1024 + 1]),
        };
        using var oversizedClient = Client((_, _) => Task.FromResult(oversizedResponse));
        using var oversized = Source(oversizedClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await AssertUnavailable(oversized.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Metadata_source_accepts_ascii_case_insensitive_bearer_and_int32_expiry_maximum()
    {
        // protects=provider field type/range compatibility; fails_when=ASCII token_type casing or int32 maximum is rejected; no_claim=token lifetime sufficiency against all submission budgets
        using var client = Client((_, _) => Task.FromResult(JsonResponse("{\"access_token\":\"a\",\"token_type\":\"bearer\",\"expires_in\":2147483647}")));
        using var source = Source(client, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        Assert.Equal("a", await source.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Concurrent_metadata_requests_keep_each_response_token_isolated()
    {
        // protects=concurrent per-invocation token isolation; fails_when=one response value is shared across calls; no_claim=metadata endpoint concurrency guarantees
        var count = 0;
        using var client = Client(async (_, _) =>
        {
            var number = Interlocked.Increment(ref count);
            await Task.Yield();
            return JsonResponse($"{{\"access_token\":\"token{number}\",\"token_type\":\"Bearer\",\"expires_in\":60}}");
        });
        using var source = Source(client, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var tokens = await Task.WhenAll(source.GetAccessTokenAsync().AsTask(), source.GetAccessTokenAsync().AsTask());

        Assert.Equal(2, count);
        Assert.Equal(new[] { "token1", "token2" }, tokens.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Metadata_source_rejects_duplicate_required_members_wrong_types_and_invalid_token_fields()
    {
        // protects=credential ambiguity rejection; fails_when=duplicate/type/grammar/expiry violations are accepted; no_claim=cryptographic token validation
        var invalid = new[]
        {
            "{}",
            "{\"access_token\":\"a\",\"access_token\":\"b\",\"token_type\":\"Bearer\",\"expires_in\":60}",
            "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"token_type\":\"Bearer\",\"expires_in\":60}",
            "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":60,\"expires_in\":60}",
            "{\"access_token\":\"Bearer abc\",\"token_type\":\"Bearer\",\"expires_in\":60}",
            "{\"access_token\":\"a b\",\"token_type\":\"Bearer\",\"expires_in\":60}",
            "{\"access_token\":\"a\",\"token_type\":\"Basic\",\"expires_in\":60}",
            "{\"access_token\":\"a\",\"token_type\":1,\"expires_in\":60}",
            "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":\"60\"}",
            "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":1.5}",
            "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":0}",
            "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":2147483648}",
        };

        foreach (var json in invalid)
        {
            using var client = Client((_, _) => Task.FromResult(JsonResponse(json)));
            using var source = Source(client, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
            await AssertInvalidToken(source.GetAccessTokenAsync());
        }
    }

    [Fact]
    public async Task Metadata_source_accounts_for_elapsed_read_time_and_remaining_submission_budget()
    {
        // protects=relative token expiry against request/read latency; fails_when=expiry is checked against wall time or start budget; no_claim=JWT exp or local clock skew
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var client = Client((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(4));
            return Task.FromResult(JsonResponse("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":10}"));
        });
        using var source = new CloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(5)), client, clock);
        var budget = new SubmissionBudget(TimeSpan.FromSeconds(5), clock);

        await AssertInvalidToken(source.GetAccessTokenAsync(budget, CancellationToken.None));

        clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var sufficientClient = Client((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(JsonResponse("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":12}"));
        });
        using var sufficient = Source(sufficientClient, clock);
        Assert.Equal("a", await sufficient.GetAccessTokenAsync(new SubmissionBudget(TimeSpan.FromSeconds(5), clock), CancellationToken.None));
    }

    [Fact]
    public async Task Metadata_source_enforces_strictly_greater_than_remaining_budget_plus_safety_margin()
    {
        // protects=expiry safety inequality; fails_when=exact boundary accepted or available margin rejected; no_claim=remote token's actual cryptographic validity
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var equalClient = Client((_, _) => Task.FromResult(JsonResponse("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":7}")));
        using var equalSource = Source(equalClient, clock);
        await AssertInvalidToken(equalSource.GetAccessTokenAsync(new SubmissionBudget(TimeSpan.FromSeconds(2), clock), CancellationToken.None));

        using var greaterClient = Client((_, _) => Task.FromResult(JsonResponse("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":8}")));
        using var greaterSource = Source(greaterClient, clock);
        Assert.Equal("a", await greaterSource.GetAccessTokenAsync(new SubmissionBudget(TimeSpan.FromSeconds(2), clock), CancellationToken.None));
    }

    [Fact]
    public async Task Standalone_metadata_source_rejects_token_at_exact_timeout_plus_safety_margin()
    {
        // protects=standalone expiry reserve; fails_when=token expiring exactly at the configured request budget plus margin is accepted; no_claim=JWT validation
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var client = Client((_, _) => Task.FromResult(JsonResponse("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":7}")));
        using var source = Source(client, clock);

        await AssertInvalidToken(source.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Metadata_source_uses_source_timeout_when_standalone_and_remaining_budget_when_dispatch_bound()
    {
        // protects=minimum of source request timeout and invocation remaining time; fails_when=standalone timeout or early expired budget is ignored; no_claim=wall-clock elapsed
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var standaloneClient = Client((_, token) => Task.FromResult(JsonResponse(ValidJson)));
        using var standalone = new CloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(3)), standaloneClient, clock);
        Assert.Equal("metadata-token", await standalone.GetAccessTokenAsync());

        using var expiredClient = Client((_, _) => throw new InvalidOperationException("must not send"));
        using var expired = new CloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(3)), expiredClient, clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await expired.GetAccessTokenAsync(
            new SubmissionBudget(TimeSpan.Zero, clock), CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_its_token_and_budget_expiry_stays_cancellation()
    {
        // protects=cancellation ownership through metadata I/O; fails_when=source reports generic unavailable on caller/budget cancellation; no_claim=provider request completion
        using var blockedClient = Client(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(ValidJson);
        });
        using var source = new CloudRunMetadataAccessTokenSource(
            new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(2)), blockedClient, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var task = source.GetAccessTokenAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);

        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var budgetSource = new CloudRunMetadataAccessTokenSource(
            new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(2)),
            Client(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonResponse(ValidJson);
            }), clock);
        using var budgetCancellation = new CancellationTokenSource();
        var budgetTask = budgetSource.GetAccessTokenAsync(
            new SubmissionBudget(TimeSpan.FromSeconds(1), clock), budgetCancellation.Token).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        budgetCancellation.Cancel();
        var budgetException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budgetTask);
        Assert.Equal(budgetCancellation.Token, budgetException.CancellationToken);

        var expiringClock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var expiringClient = Client(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(ValidJson);
        });
        using var expiringSource = new CloudRunMetadataAccessTokenSource(
            new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(5)), expiringClient, expiringClock);
        var expiringTask = expiringSource.GetAccessTokenAsync(
            new SubmissionBudget(TimeSpan.FromSeconds(1), expiringClock), CancellationToken.None).AsTask();
        expiringClock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expiringTask);

        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        var calls = 0;
        using var noIoClient = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(ValidJson));
        });
        using var noIoSource = Source(noIoClient, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await noIoSource.GetAccessTokenAsync(preCanceled.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Disposal_owns_client_and_future_calls_fail_without_io()
    {
        // protects=container-owned metadata transport lifecycle; fails_when=disposed source is reused or caller client is silently retained; no_claim=host stop ordering
        var calls = 0;
        var client = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(ValidJson));
        });
        var source = Source(client, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        Assert.Equal("metadata-token", await source.GetAccessTokenAsync());
        source.Dispose();
        source.Dispose();
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await source.GetAccessTokenAsync());
        Assert.Equal(1, calls);
    }

    private static CloudRunMetadataAccessTokenSource Source(HttpClient client, TimeProvider clock) =>
        new(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(2)), client, clock);

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new DelegateHandler(send), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage StalledResponse(TaskCompletionSource readEntered) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new StalledReadStream(readEntered)),
    };

    private static async Task AssertUnavailable(ValueTask<string> pending)
    {
        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await pending);
        Assert.False(exception.InvalidToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("fixture-secret", exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task AssertUnavailable(Task<string> pending) => await AssertUnavailable(new ValueTask<string>(pending));

    private static async Task AssertInvalidToken(ValueTask<string> pending)
    {
        var exception = await Assert.ThrowsAsync<TokenAcquisitionException>(async () => await pending);
        Assert.True(exception.InvalidToken);
        Assert.Null(exception.InnerException);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FailingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("fixture-secret");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("fixture-secret"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledReadStream(TaskCompletionSource readEntered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkedOversizeStream : Stream
    {
        private int _remaining = 16 * 1024 + 1;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Clear();
            _remaining -= count;
            return ValueTask.FromResult(count);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
