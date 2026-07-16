using System.Net;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace ForgeTrust.AppSurface.Web.Push;

internal sealed class GuardedWebPushTransport : IDisposable
{
    internal SocketsHttpHandler Handler { get; } = GuardedWebPushAdapter.CreateSharedTransport();

    public void Dispose() => Handler.Dispose();
}

internal sealed class GuardedWebPushAdapter(HttpMessageHandler transport)
{
    public async ValueTask<GuardedWebPushResponse> SendAsync(
        GuardedWebPushRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var transportLease = new NonDisposingHandler(transport);
        using var recorder = new RecordingSanitizingHandler(transportLease);
        using var httpClient = new HttpClient(recorder, disposeHandler: true);
        using var authentication = new VapidAuthentication(request.VapidPublicKey, request.VapidPrivateKey)
        {
            Subject = request.VapidSubject,
        };

        var client = new PushServiceClient(httpClient)
        {
            AutoRetryAfter = false,
            DefaultAuthenticationScheme = VapidAuthenticationScheme.Vapid,
        };

        var subscription = new PushSubscription { Endpoint = request.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, request.P256Dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, request.Auth);

        var message = new PushMessage(request.Payload)
        {
            TimeToLive = request.TimeToLive,
            Topic = request.Topic,
            Urgency = request.Urgency switch
            {
                GuardedWebPushUrgency.VeryLow => PushMessageUrgency.VeryLow,
                GuardedWebPushUrgency.Low => PushMessageUrgency.Low,
                GuardedWebPushUrgency.Normal => PushMessageUrgency.Normal,
                GuardedWebPushUrgency.High => PushMessageUrgency.High,
                _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported urgency."),
            },
        };

        try
        {
            await client.RequestPushMessageDeliveryAsync(
                subscription,
                message,
                authentication,
                VapidAuthenticationScheme.Vapid,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PushServiceClientException) when (recorder.StatusCode.HasValue)
        {
            // The dependency throws on non-success responses after reading the sanitized empty body.
            // Only the package-owned safe record crosses this boundary.
        }
        catch (HttpRequestException)
        {
            return GuardedWebPushResponse.NetworkFailure;
        }
        catch (TaskCanceledException)
        {
            return GuardedWebPushResponse.NetworkFailure;
        }

        return recorder.StatusCode.HasValue
            ? new GuardedWebPushResponse(recorder.StatusCode, recorder.RetryAfter, false)
            : GuardedWebPushResponse.ProtocolFailure;
    }

    internal static SocketsHttpHandler CreateSharedTransport() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    };

    private sealed class NonDisposingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override void Dispose(bool disposing)
        {
            // The shared transport lifetime is owned by DI, not an individual send.
        }
    }

    private sealed class RecordingSanitizingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public HttpStatusCode? StatusCode { get; private set; }

        public TimeSpan? RetryAfter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            StatusCode = response.StatusCode;
            RetryAfter = GetSafeRetryAfter(response);

            var originalContent = response.Content;
            response.Content = new ByteArrayContent([]);

            try
            {
                originalContent?.Dispose();
            }
            catch (Exception)
            {
                // Push-service content is hostile and disposal is best effort. It never changes status.
            }

            return response;
        }

        private static TimeSpan? GetSafeRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            {
                return delta;
            }

            if (retryAfter?.Date is { } date)
            {
                var value = date - DateTimeOffset.UtcNow;
                return value > TimeSpan.Zero ? value : TimeSpan.Zero;
            }

            return null;
        }
    }
}

internal sealed record GuardedWebPushRequest(
    string Endpoint,
    string P256Dh,
    string Auth,
    string Payload,
    int TimeToLive,
    GuardedWebPushUrgency Urgency,
    string? Topic,
    string VapidSubject,
    string VapidPublicKey,
    string VapidPrivateKey)
{
    public override string ToString() => "GuardedWebPushRequest { Redacted = true }";
}

internal enum GuardedWebPushUrgency
{
    VeryLow,
    Low,
    Normal,
    High,
}

internal sealed record GuardedWebPushResponse(
    HttpStatusCode? StatusCode,
    TimeSpan? RetryAfter,
    bool IsNetworkFailure)
{
    public static GuardedWebPushResponse NetworkFailure { get; } = new(null, null, true);

    public static GuardedWebPushResponse ProtocolFailure { get; } = new(null, null, false);
}
