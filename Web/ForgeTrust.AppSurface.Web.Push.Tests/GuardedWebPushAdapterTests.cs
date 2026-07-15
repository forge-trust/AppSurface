using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using ForgeTrust.AppSurface.Web.Push;

namespace ForgeTrust.AppSurface.Web.Push.Tests;

public sealed class GuardedWebPushAdapterTests
{
    [Fact]
    public void SharedTransport_ExposesAndDisposesOwnedHandler()
    {
        using var transport = new GuardedWebPushTransport();

        Assert.NotNull(transport.Handler);
    }

    [Fact]
    public async Task SendAsync_UsesAes128GcmVapidAndOneAttempt()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests);
        var response = await new GuardedWebPushAdapter(handler).SendAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(17), response.RetryAfter);
        Assert.False(response.IsNetworkFailure);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal("vapid", handler.AuthorizationScheme);
        Assert.Equal("aes128gcm", handler.ContentEncoding);
        Assert.Equal("60", handler.TimeToLive);
        Assert.True(handler.ContentDisposed);
        Assert.False(handler.ContentRead);
    }

    [Fact]
    public async Task SendAsync_DisposesHostileBodiesWithoutReading_OnSuccessAndFailure()
    {
        foreach (var statusCode in new[] { HttpStatusCode.Created, HttpStatusCode.Gone })
        {
            var handler = new CapturingHandler(statusCode, throwOnDispose: true);
            var response = await new GuardedWebPushAdapter(handler).SendAsync(CreateRequest(), CancellationToken.None);

            Assert.Equal(statusCode, response.StatusCode);
            Assert.True(handler.ContentDisposed);
            Assert.False(handler.ContentRead);
        }
    }

    [Fact]
    public async Task SendAsync_ClassifiesHttpRequestFailureAsNetworkFailure()
    {
        var response = await new GuardedWebPushAdapter(new ThrowingHandler(new HttpRequestException("offline")))
            .SendAsync(CreateRequest(), CancellationToken.None);

        Assert.Same(GuardedWebPushResponse.NetworkFailure, response);
        Assert.Null(response.StatusCode);
        Assert.Null(response.RetryAfter);
        Assert.True(response.IsNetworkFailure);
    }

    [Fact]
    public async Task SendAsync_ClassifiesTransportTimeoutAsNetworkFailure()
    {
        var response = await new GuardedWebPushAdapter(new ThrowingHandler(new TaskCanceledException("timeout")))
            .SendAsync(CreateRequest(), CancellationToken.None);

        Assert.Same(GuardedWebPushResponse.NetworkFailure, response);
    }

    [Fact]
    public void ProtocolFailure_IsSafeAndContainsNoTransportDetails()
    {
        var response = GuardedWebPushResponse.ProtocolFailure;

        Assert.Null(response.StatusCode);
        Assert.Null(response.RetryAfter);
        Assert.False(response.IsNetworkFailure);
    }

    [Fact]
    public void GuardedRequest_RedactsEverySensitiveField()
    {
        var request = new GuardedWebPushRequest(
            "private-endpoint",
            "private-p256dh",
            "private-auth",
            "private-payload",
            60,
            GuardedWebPushUrgency.High,
            "private-topic",
            "private-subject",
            "private-public-key",
            "private-private-key");

        var rendered = request.ToString();

        Assert.Equal("GuardedWebPushRequest { Redacted = true }", rendered);
        Assert.DoesNotContain(request.Endpoint, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(request.P256Dh, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Auth, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Payload, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Topic!, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(request.VapidSubject, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(request.VapidPublicKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(request.VapidPrivateKey, rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)GuardedWebPushUrgency.VeryLow, "very-low")]
    [InlineData((int)GuardedWebPushUrgency.Low, "low")]
    [InlineData((int)GuardedWebPushUrgency.Normal, null)]
    [InlineData((int)GuardedWebPushUrgency.High, "high")]
    public async Task SendAsync_MapsUrgencyHeader(
        int urgency,
        string? expectedHeader)
    {
        var handler = new CapturingHandler(HttpStatusCode.Created);

        await new GuardedWebPushAdapter(handler)
            .SendAsync(CreateRequest(urgency: (GuardedWebPushUrgency)urgency), CancellationToken.None);

        Assert.Equal(expectedHeader, handler.Urgency);
    }

    [Fact]
    public async Task SendAsync_RejectsUnexpectedUrgencyBeforeTransport()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await new GuardedWebPushAdapter(handler)
                .SendAsync(CreateRequest(urgency: (GuardedWebPushUrgency)99), CancellationToken.None));

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task SendAsync_UsesFutureRetryAfterDateAsDelay()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var handler = new CapturingHandler(
            HttpStatusCode.TooManyRequests,
            configureResponse: response => response.Headers.RetryAfter = new(retryAt));

        var result = await new GuardedWebPushAdapter(handler)
            .SendAsync(CreateRequest(), CancellationToken.None);

        Assert.InRange(result.RetryAfter!.Value, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task SendAsync_ClampsPastRetryAfterDateToZero()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.TooManyRequests,
            configureResponse: response => response.Headers.RetryAfter = new(DateTimeOffset.UtcNow.AddMinutes(-1)));

        var result = await new GuardedWebPushAdapter(handler)
            .SendAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, result.RetryAfter);
    }

    [Fact]
    public async Task SendAsync_IgnoresInvalidRetryAfter()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.TooManyRequests,
            configureResponse: response => response.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay"));

        var result = await new GuardedWebPushAdapter(handler)
            .SendAsync(CreateRequest(), CancellationToken.None);

        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task SendAsync_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new CapturingHandler(HttpStatusCode.Created);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new GuardedWebPushAdapter(handler).SendAsync(CreateRequest(), cancellation.Token));
    }

    [Fact]
    public async Task SendAsync_IsolatesConcurrentResponseCapture()
    {
        var handler = new CapturingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("gone", StringComparison.Ordinal)
                ? HttpStatusCode.Gone
                : HttpStatusCode.Created);
        var adapter = new GuardedWebPushAdapter(handler);

        var created = adapter.SendAsync(CreateRequest("https://push.example.test/created"), CancellationToken.None).AsTask();
        var gone = adapter.SendAsync(CreateRequest("https://push.example.test/gone"), CancellationToken.None).AsTask();
        await Task.WhenAll(created, gone);
        var createdResponse = await created;
        var goneResponse = await gone;

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, goneResponse.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public void CreateSharedTransport_DisablesRedirectsCookiesAndDecompression()
    {
        using var transport = GuardedWebPushAdapter.CreateSharedTransport();

        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        Assert.Equal(DecompressionMethods.None, transport.AutomaticDecompression);
    }

    private static GuardedWebPushRequest CreateRequest(
        string endpoint = "https://push.example.test/send",
        GuardedWebPushUrgency urgency = GuardedWebPushUrgency.Normal)
    {
        var vapid = CreateKeyPair(ECDsa.Create);
        var subscription = CreateKeyPair(ECDiffieHellman.Create);
        return new GuardedWebPushRequest(
            endpoint,
            subscription.PublicKey,
            Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            "{\"schemaVersion\":1,\"title\":\"Hello\"}",
            60,
            urgency,
            null,
            "mailto:push@example.test",
            vapid.PublicKey,
            vapid.PrivateKey);
    }

    private static (string PublicKey, string PrivateKey) CreateKeyPair<T>(Func<T> factory)
        where T : ECAlgorithm
    {
        using var algorithm = factory();
        algorithm.GenerateKey(ECCurve.NamedCurves.nistP256);
        var parameters = algorithm.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (Base64UrlEncode(publicKey), Base64UrlEncode(parameters.D!));
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpStatusCode> statusCode;
        private readonly bool throwOnDispose;
        private readonly Action<HttpResponseMessage>? configureResponse;
        private int attempts;

        public CapturingHandler(
            HttpStatusCode statusCode,
            bool throwOnDispose = false,
            Action<HttpResponseMessage>? configureResponse = null)
            : this(_ => statusCode, throwOnDispose, configureResponse)
        {
        }

        public CapturingHandler(
            Func<HttpRequestMessage, HttpStatusCode> statusCode,
            bool throwOnDispose = false,
            Action<HttpResponseMessage>? configureResponse = null)
        {
            this.statusCode = statusCode;
            this.throwOnDispose = throwOnDispose;
            this.configureResponse = configureResponse;
        }

        public int Attempts => attempts;

        public string? AuthorizationScheme { get; private set; }

        public string? ContentEncoding { get; private set; }

        public string? TimeToLive { get; private set; }

        public string? Urgency { get; private set; }

        public bool ContentRead { get; private set; }

        public bool ContentDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref attempts);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            ContentEncoding = request.Content?.Headers.ContentEncoding.SingleOrDefault();
            TimeToLive = request.Headers.GetValues("TTL").Single();
            Urgency = request.Headers.TryGetValues("Urgency", out var urgency)
                ? urgency.Single()
                : null;

            var response = new HttpResponseMessage(statusCode(request));
            if (configureResponse is null)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            }
            else
            {
                configureResponse(response);
            }

            response.Content = new HostileContent(
                () => ContentRead = true,
                () => ContentDisposed = true,
                throwOnDispose);
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class HostileContent(Action read, Action disposed, bool throwOnDispose) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            read();
            throw new InvalidOperationException("Hostile content must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            disposed();
            base.Dispose(disposing);
            if (disposing && throwOnDispose)
            {
                throw new InvalidOperationException("Hostile content disposal failed.");
            }
        }
    }
}
