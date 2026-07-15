using System.Security.Claims;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Contains one sensitive browser push subscription snapshot.</summary>
public sealed class AppSurfaceWebPushSubscription
{
    /// <summary>Creates a complete immutable subscription snapshot whose formats are validated at intake and send.</summary>
    /// <param name="endpoint">The sensitive absolute push-service endpoint.</param>
    /// <param name="p256Dh">The sensitive canonical base64url P-256 public key.</param>
    /// <param name="auth">The sensitive canonical base64url authentication secret.</param>
    /// <param name="vapidKeyId">The retained host key identifier used when the subscription was created.</param>
    public AppSurfaceWebPushSubscription(string endpoint, string p256Dh, string auth, string vapidKeyId)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        P256Dh = p256Dh ?? throw new ArgumentNullException(nameof(p256Dh));
        Auth = auth ?? throw new ArgumentNullException(nameof(auth));
        VapidKeyId = vapidKeyId ?? throw new ArgumentNullException(nameof(vapidKeyId));
    }

    /// <summary>Gets the sensitive push-service endpoint.</summary>
    public string Endpoint { get; }

    /// <summary>Gets the sensitive browser P-256 Diffie-Hellman public key.</summary>
    public string P256Dh { get; }

    /// <summary>Gets the sensitive browser authentication secret.</summary>
    public string Auth { get; }

    /// <summary>Gets the safe retained VAPID key identifier.</summary>
    public string VapidKeyId { get; }

    /// <inheritdoc />
    public override string ToString() => "AppSurfaceWebPushSubscription { Redacted = true }";
}

/// <summary>Identifies a sensitive browser subscription for an app-owned unregister operation.</summary>
public sealed class AppSurfaceWebPushSubscriptionReference
{
    /// <summary>Creates an immutable subscription reference.</summary>
    /// <param name="endpoint">The sensitive absolute endpoint to remove from the authenticated principal's custody.</param>
    public AppSurfaceWebPushSubscriptionReference(string endpoint)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <summary>Gets the sensitive push-service endpoint.</summary>
    public string Endpoint { get; }

    /// <inheritdoc />
    public override string ToString() => "AppSurfaceWebPushSubscriptionReference { Redacted = true }";
}

/// <summary>Provides the authenticated principal to app-owned subscription custody.</summary>
public sealed class AppSurfaceWebPushSubscriptionWriteContext
{
    /// <summary>Creates a write context for an authenticated principal.</summary>
    /// <param name="principal">The authenticated principal whose app-owned user and tenant keys scope the write.</param>
    public AppSurfaceWebPushSubscriptionWriteContext(ClaimsPrincipal principal)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
    }

    /// <summary>Gets the authenticated principal. The host derives its own user and tenant keys.</summary>
    public ClaimsPrincipal Principal { get; }
}

/// <summary>Stores subscriptions and conditionally marks complete snapshots terminal.</summary>
public interface IAppSurfaceWebPushSubscriptionCustody
{
    /// <summary>Idempotently registers or refreshes a subscription for the authenticated principal.</summary>
    /// <param name="context">The authenticated write context.</param>
    /// <param name="subscription">The complete validated subscription snapshot.</param>
    /// <param name="cancellationToken">Cancels app-owned custody work.</param>
    /// <returns>A disposition that distinguishes create, update, no-op, ownership conflict, and policy rejection.</returns>
    ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
        AppSurfaceWebPushSubscriptionWriteContext context,
        AppSurfaceWebPushSubscription subscription,
        CancellationToken cancellationToken);

    /// <summary>Idempotently unregisters a subscription for the authenticated principal.</summary>
    /// <param name="context">The authenticated write context.</param>
    /// <param name="subscription">The endpoint reference to remove.</param>
    /// <param name="cancellationToken">Cancels app-owned custody work.</param>
    /// <returns>A disposition that distinguishes removal, absence, ownership conflict, and policy rejection.</returns>
    ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
        AppSurfaceWebPushSubscriptionWriteContext context,
        AppSurfaceWebPushSubscriptionReference subscription,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compares and marks the complete subscription snapshot terminal. Implementations must not retire a replacement
    /// subscription that happens to reuse the same endpoint.
    /// </summary>
    /// <param name="subscription">The complete snapshot that received a terminal push-service response.</param>
    /// <param name="reason">The safe terminal reason.</param>
    /// <param name="cancellationToken">Bounds cleanup independently from the caller's send token.</param>
    /// <returns>A compare-and-mark disposition; replacement records must produce <see cref="AppSurfaceWebPushTerminalDisposition.AlreadyTerminal"/>.</returns>
    ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
        AppSurfaceWebPushSubscription subscription,
        AppSurfaceWebPushTerminalReason reason,
        CancellationToken cancellationToken);
}

/// <summary>Describes an app-owned registration result.</summary>
public enum AppSurfaceWebPushRegistrationDisposition
{
    /// <summary>A new principal-owned record was created.</summary>
    Created,
    /// <summary>The same principal's record was refreshed.</summary>
    Updated,
    /// <summary>The complete record already matched.</summary>
    Unchanged,
    /// <summary>The endpoint belongs to another principal.</summary>
    Conflict,
    /// <summary>Host policy rejected the write.</summary>
    Rejected,
}

/// <summary>Describes an app-owned unregister result.</summary>
public enum AppSurfaceWebPushUnregistrationDisposition
{
    /// <summary>The principal-owned record was removed.</summary>
    Removed,
    /// <summary>No matching principal-owned record existed.</summary>
    NotFound,
    /// <summary>The endpoint belongs to another principal.</summary>
    Conflict,
    /// <summary>Host policy rejected the write.</summary>
    Rejected,
}

/// <summary>Describes compare-and-mark terminal cleanup.</summary>
public enum AppSurfaceWebPushTerminalDisposition
{
    /// <summary>The matching complete snapshot was marked terminal.</summary>
    Completed,
    /// <summary>The matching snapshot was already terminal or a replacement won the race.</summary>
    AlreadyTerminal,
    /// <summary>Host custody rejected terminal cleanup.</summary>
    Rejected,
}

/// <summary>Identifies the terminal push-service response.</summary>
public enum AppSurfaceWebPushTerminalReason
{
    /// <summary>The push service returned HTTP 404.</summary>
    NotFound,
    /// <summary>The push service returned HTTP 410.</summary>
    Gone,
}
