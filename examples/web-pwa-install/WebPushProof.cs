using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web.Push;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;

internal static class WebPushProof
{
    private const string PolicyName = "push.manage";

    public static void ConfigureHost(StartupContext context, IHostBuilder builder)
    {
        if (!context.IsDevelopment)
        {
            builder.ConfigureServices(services => services.AddSingleton(new WebPushProofState(false)));
            return;
        }

        builder.ConfigureServices((hostContext, services) =>
        {
            var configuration = hostContext.Configuration;
            var publicKey = configuration["WebPush:Keys:Primary:PublicKey"];
            var privateKey = configuration["WebPush:Keys:Primary:PrivateKey"];
            var subject = configuration["WebPush:Keys:Primary:Subject"] ?? "mailto:push@example.test";
            var origin = configuration["WebPush:AllowedPushServiceOrigins:0"];
            var enabled = !string.IsNullOrWhiteSpace(publicKey)
                && !string.IsNullOrWhiteSpace(privateKey)
                && !string.IsNullOrWhiteSpace(origin);
            services.AddSingleton(new WebPushProofState(enabled));
            services.AddSingleton<InMemoryPushCustody>();
            services.AddSingleton<IAppSurfaceWebPushSubscriptionCustody>(provider =>
                provider.GetRequiredService<InMemoryPushCustody>());

            if (!enabled)
            {
                return;
            }

            services.AddAuthorization(options =>
                options.AddPolicy(PolicyName, policy => policy
                    .AddAuthenticationSchemes(AppSurfaceDevAuthDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .RequireClaim("role", "admin")));
            services.AddAppSurfaceDevAuth(hostContext.HostingEnvironment, options =>
            {
                options.Users.Add("admin", user => user
                    .DisplayName("Push Admin")
                    .Subject("push-admin")
                    .Claim("role", "admin"));
                options.Users.Add("viewer", user => user
                    .DisplayName("Push Viewer")
                    .Subject("push-viewer")
                    .Claim("role", "viewer"));
            });
            services.AddAppSurfaceWebPush(options =>
            {
                options.ActiveVapidKeyId = "primary";
                options.VapidKeys.Add("primary", new AppSurfaceWebPushVapidKeyOptions
                {
                    Subject = subject,
                    PublicKey = publicKey,
                    PrivateKey = privateKey,
                });
                options.AllowedPushServiceOrigins.Add(origin!);
            });
            services.AddAppSurfaceWebPushDevelopmentProofTransport(hostContext.HostingEnvironment);
        });
    }

    public static void MapEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        if (!context.IsDevelopment)
        {
            return;
        }

        var state = endpoints.ServiceProvider.GetRequiredService<WebPushProofState>();
        if (!state.Enabled)
        {
            return;
        }

        endpoints.MapAppSurfaceDevAuth();
        endpoints.MapAppSurfaceWebPushSubscriptions("/account/push-subscriptions", PolicyName);
        endpoints.MapPost("/account/push-subscriptions/host-action-proof", async (
            HttpContext httpContext,
            IAntiforgery antiforgery,
            ClaimsPrincipal principal,
            InMemoryPushCustody custody,
            IAppSurfaceWebPushSender sender,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(httpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "The example host action failed antiforgery validation.");
            }

            if (!custody.TryGetSubscription(principal, out var subscription))
            {
                return Results.Problem(statusCode: 409, title: "Enable a subscription before running the example host action.");
            }

            var result = await sender.SendAsync(
                new AppSurfaceWebPushSendRequest(
                    subscription,
                    new AppSurfaceWebPushNotification("AppSurface proof", "Deterministic Development proof transport only."),
                    new AppSurfaceWebPushSendOptions(60)),
                cancellationToken);
            return Results.Json(new
            {
                senderClassification = result.Outcome.ToString(),
                senderCode = result.ReasonCode,
                pushDelivery = "Not proven",
                message = "The package sender classified the Development-only proof transport; no network request or browser delivery occurred.",
            });
        })
            .RequireAuthorization(PolicyName)
            .ExcludeFromDescription();
    }
}

internal sealed record WebPushProofState(bool Enabled);

internal sealed class InMemoryPushCustody : IAppSurfaceWebPushSubscriptionCustody
{
    private readonly object gate = new();
    private readonly Dictionary<string, AppSurfaceWebPushSubscription> subscriptions = new(StringComparer.Ordinal);

    public bool TryGetSubscription(
        ClaimsPrincipal principal,
        out AppSurfaceWebPushSubscription subscription) =>
        TryGetSubscriptionCore(Owner(principal), out subscription);

    public ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
        AppSurfaceWebPushSubscriptionWriteContext context,
        AppSurfaceWebPushSubscription subscription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var owner = Owner(context.Principal);
            if (subscriptions.Any(pair => pair.Key != owner && pair.Value.Endpoint == subscription.Endpoint))
            {
                return ValueTask.FromResult(AppSurfaceWebPushRegistrationDisposition.Conflict);
            }

            var disposition = subscriptions.TryGetValue(owner, out var current)
                ? Same(current, subscription)
                    ? AppSurfaceWebPushRegistrationDisposition.Unchanged
                    : AppSurfaceWebPushRegistrationDisposition.Updated
                : AppSurfaceWebPushRegistrationDisposition.Created;
            subscriptions[owner] = subscription;
            return ValueTask.FromResult(disposition);
        }
    }

    public ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
        AppSurfaceWebPushSubscriptionWriteContext context,
        AppSurfaceWebPushSubscriptionReference subscription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var owner = Owner(context.Principal);
            var removed = subscriptions.TryGetValue(owner, out var current)
                && string.Equals(current.Endpoint, subscription.Endpoint, StringComparison.Ordinal)
                && subscriptions.Remove(owner);
            return ValueTask.FromResult(removed
                ? AppSurfaceWebPushUnregistrationDisposition.Removed
                : AppSurfaceWebPushUnregistrationDisposition.NotFound);
        }
    }

    public ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
        AppSurfaceWebPushSubscription subscription,
        AppSurfaceWebPushTerminalReason reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            foreach (var pair in subscriptions.Where(pair => Same(pair.Value, subscription)).ToList())
            {
                if (subscriptions.Remove(pair.Key))
                {
                    return ValueTask.FromResult(AppSurfaceWebPushTerminalDisposition.Completed);
                }
            }

            return ValueTask.FromResult(AppSurfaceWebPushTerminalDisposition.AlreadyTerminal);
        }
    }

    private bool TryGetSubscriptionCore(
        string owner,
        out AppSurfaceWebPushSubscription subscription)
    {
        lock (gate)
        {
            return subscriptions.TryGetValue(owner, out subscription!);
        }
    }

    private static string Owner(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value
        ?? principal.Identity?.Name
        ?? throw new InvalidOperationException("The proof principal has no stable subject.");

    private static bool Same(AppSurfaceWebPushSubscription left, AppSurfaceWebPushSubscription right) =>
        left.Endpoint == right.Endpoint
        && left.P256Dh == right.P256Dh
        && left.Auth == right.Auth
        && left.VapidKeyId == right.VapidKeyId;
}
