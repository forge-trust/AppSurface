using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push;

internal sealed class AppSurfaceWebPushSender(
    IOptions<AppSurfaceWebPushOptions> configuredOptions,
    GuardedWebPushAdapter adapter,
    IServiceScopeFactory scopeFactory) : IAppSurfaceWebPushSender
{
    private static readonly TimeSpan TerminalCleanupTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask<AppSurfaceWebPushSendResult> SendAsync(
        AppSurfaceWebPushSendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = configuredOptions.Value;
        var subscription = request.Subscription;
        ValidateRequest(request);

        if (!AppSurfaceWebPushValidation.TryValidateEndpoint(
            subscription.Endpoint,
            options.AllowedPushServiceOrigins,
            out _))
        {
            return Result(AppSurfaceWebPushSendOutcome.PushServiceNotAllowed, "ASPUSHSEND001", subscription.VapidKeyId);
        }

        if (!options.VapidKeys.TryGetValue(subscription.VapidKeyId, out var vapidKey))
        {
            return Result(AppSurfaceWebPushSendOutcome.VapidKeyUnavailable, "ASPUSHSEND002", subscription.VapidKeyId);
        }

        var payload = SerializeNotification(request.Notification);
        GuardedWebPushResponse response;
        try
        {
            response = await adapter.SendAsync(
                new GuardedWebPushRequest(
                    subscription.Endpoint,
                    subscription.P256Dh,
                    subscription.Auth,
                    payload,
                    request.Options.TimeToLiveSeconds,
                    (GuardedWebPushUrgency)request.Options.Urgency,
                    request.Options.Topic,
                    vapidKey.Subject!,
                    vapidKey.PublicKey!,
                    vapidKey.PrivateKey!),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AppSurfaceWebPushSendOutcome.ProtocolFailure, "ASPUSHSEND003", subscription.VapidKeyId);
        }

        if (response.IsNetworkFailure)
        {
            return Result(
                AppSurfaceWebPushSendOutcome.TransientFailure,
                "ASPUSHSEND004",
                subscription.VapidKeyId,
                retryAfter: response.RetryAfter);
        }

        if (response.StatusCode is null)
        {
            return Result(AppSurfaceWebPushSendOutcome.ProtocolFailure, "ASPUSHSEND005", subscription.VapidKeyId);
        }

        var statusCode = response.StatusCode.Value;
        if (statusCode == HttpStatusCode.Created)
        {
            return Result(
                AppSurfaceWebPushSendOutcome.Accepted,
                "ASPUSHSEND006",
                subscription.VapidKeyId,
                statusCode,
                response.RetryAfter);
        }

        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            var cleanup = await MarkTerminalAsync(subscription, statusCode).ConfigureAwait(false);
            return Result(
                AppSurfaceWebPushSendOutcome.TerminalSubscription,
                statusCode == HttpStatusCode.NotFound ? "ASPUSHSEND007" : "ASPUSHSEND008",
                subscription.VapidKeyId,
                statusCode,
                response.RetryAfter,
                cleanup);
        }

        var numericStatus = (int)statusCode;
        if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || numericStatus is >= 500 and <= 599)
        {
            return Result(
                AppSurfaceWebPushSendOutcome.TransientFailure,
                "ASPUSHSEND009",
                subscription.VapidKeyId,
                statusCode,
                response.RetryAfter);
        }

        if (numericStatus is >= 400 and <= 499)
        {
            return Result(
                AppSurfaceWebPushSendOutcome.Rejected,
                "ASPUSHSEND010",
                subscription.VapidKeyId,
                statusCode,
                response.RetryAfter);
        }

        return Result(
            AppSurfaceWebPushSendOutcome.ProtocolFailure,
            "ASPUSHSEND011",
            subscription.VapidKeyId,
            statusCode,
            response.RetryAfter);
    }

    private async ValueTask<AppSurfaceWebPushCleanupState> MarkTerminalAsync(
        AppSurfaceWebPushSubscription subscription,
        HttpStatusCode statusCode)
    {
        CancellationTokenSource? cleanupCancellation = new();
        try
        {
            var custodyCancellation = cleanupCancellation;
            var cleanup = Task.Run(
                async () =>
                {
                    await using var cleanupScope = scopeFactory.CreateAsyncScope();
                    var custody = cleanupScope.ServiceProvider.GetService<IAppSurfaceWebPushSubscriptionCustody>();
                    if (custody is null)
                    {
                        return (AppSurfaceWebPushTerminalDisposition?)null;
                    }

                    return await custody.MarkTerminalAsync(
                        subscription,
                        statusCode == HttpStatusCode.NotFound
                            ? AppSurfaceWebPushTerminalReason.NotFound
                            : AppSurfaceWebPushTerminalReason.Gone,
                        custodyCancellation.Token).ConfigureAwait(false);
                },
                CancellationToken.None);
            var timeout = Task.Delay(TerminalCleanupTimeout);
            var completed = await Task.WhenAny(cleanup, timeout).ConfigureAwait(false);
            if (completed != cleanup)
            {
                cleanupCancellation = null;
                _ = CancelCleanupAndReleaseAsync(custodyCancellation, cleanup);
                return AppSurfaceWebPushCleanupState.Failed;
            }

            var disposition = await cleanup.ConfigureAwait(false);
            return disposition switch
            {
                AppSurfaceWebPushTerminalDisposition.Completed => AppSurfaceWebPushCleanupState.Completed,
                AppSurfaceWebPushTerminalDisposition.AlreadyTerminal => AppSurfaceWebPushCleanupState.AlreadyTerminal,
                AppSurfaceWebPushTerminalDisposition.Rejected => AppSurfaceWebPushCleanupState.Rejected,
                _ => AppSurfaceWebPushCleanupState.Failed,
            };
        }
        catch (Exception)
        {
            return AppSurfaceWebPushCleanupState.Failed;
        }
        finally
        {
            cleanupCancellation?.Dispose();
        }
    }

    private static async Task CancelCleanupAndReleaseAsync(
        CancellationTokenSource cleanupCancellation,
        Task cleanup)
    {
        try
        {
            await Task.Run(cleanupCancellation.Cancel).ConfigureAwait(false);
            await cleanup.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed-out app-owned cleanup is best effort and must never escape the background observer.
        }
        finally
        {
            cleanupCancellation.Dispose();
        }
    }

    private static void ValidateRequest(AppSurfaceWebPushSendRequest request)
    {
        var subscription = request.Subscription;
        if (!AppSurfaceWebPushValidation.IsSafeKeyId(subscription.VapidKeyId))
        {
            throw new ArgumentException("The subscription VAPID key ID is invalid.", nameof(request));
        }

        if (!AppSurfaceWebPushValidation.IsValidP256PublicKey(subscription.P256Dh)
            || !AppSurfaceWebPushValidation.TryDecodeCanonicalBase64Url(subscription.Auth, 16, out _))
        {
            throw new ArgumentException("The subscription key material is invalid.", nameof(request));
        }

        var notification = request.Notification;
        if (notification.Title.Length is < 1 or > 256
            || (notification.Body is not null && notification.Body.Length is < 1 or > 2048)
            || (notification.Tag is not null && notification.Tag.Length is < 1 or > 128)
            || (notification.IconPath is not null && !AppSurfaceWebPushValidation.IsValidAssetPath(notification.IconPath))
            || (notification.BadgePath is not null && !AppSurfaceWebPushValidation.IsValidAssetPath(notification.BadgePath))
            || (notification.DestinationPath is not null && !AppSurfaceWebPushValidation.IsValidDestinationPath(notification.DestinationPath)))
        {
            throw new ArgumentException("The notification does not match the PWA worker payload contract.", nameof(request));
        }

        if (request.Options.TimeToLiveSeconds is < 1 or > 2_419_200
            || !Enum.IsDefined(request.Options.Urgency)
            || !AppSurfaceWebPushValidation.IsValidTopic(request.Options.Topic))
        {
            throw new ArgumentException("The send options are invalid.", nameof(request));
        }
    }

    private static string SerializeNotification(AppSurfaceWebPushNotification notification)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("title", notification.Title);
            WriteOptional(writer, "body", notification.Body);
            WriteOptional(writer, "iconPath", notification.IconPath);
            WriteOptional(writer, "badgePath", notification.BadgePath);
            WriteOptional(writer, "tag", notification.Tag);
            WriteOptional(writer, "destinationPath", notification.DestinationPath);
            writer.WriteEndObject();
        }

        if (stream.Length > 3993)
        {
            throw new ArgumentException("The serialized notification exceeds the PWA worker payload limit.", nameof(notification));
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static AppSurfaceWebPushSendResult Result(
        AppSurfaceWebPushSendOutcome outcome,
        string reasonCode,
        string vapidKeyId,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        AppSurfaceWebPushCleanupState cleanupState = AppSurfaceWebPushCleanupState.NotRequired) =>
        new(outcome, cleanupState, statusCode is null ? null : (int)statusCode, retryAfter, reasonCode, vapidKeyId);
}
