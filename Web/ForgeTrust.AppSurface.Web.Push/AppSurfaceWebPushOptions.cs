namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>
/// Configures the active VAPID key ring and the exact push-service origins an AppSurface host permits.
/// </summary>
public sealed class AppSurfaceWebPushOptions
{
    /// <summary>Gets or sets the safe identifier used for newly created browser subscriptions.</summary>
    public string? ActiveVapidKeyId { get; set; }

    /// <summary>Gets the bounded VAPID key ring. Retain old keys while stored subscriptions still reference them.</summary>
    public IDictionary<string, AppSurfaceWebPushVapidKeyOptions> VapidKeys { get; } =
        new Dictionary<string, AppSurfaceWebPushVapidKeyOptions>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the exact normalized HTTPS origins allowed to receive push requests, for example
    /// <c>https://updates.push.services.mozilla.com</c>. Wildcards and custom ports are rejected.
    /// </summary>
    public ISet<string> AllowedPushServiceOrigins { get; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>Configures one retained VAPID signing key pair.</summary>
/// <remarks>The private key is sensitive. Store it in user-secrets or a production secret provider.</remarks>
public sealed class AppSurfaceWebPushVapidKeyOptions
{
    /// <summary>Gets or sets the RFC 8292 contact subject, as a <c>mailto:</c> or HTTPS URI.</summary>
    public string? Subject { get; set; }

    /// <summary>Gets or sets the canonical unpadded base64url P-256 public key.</summary>
    public string? PublicKey { get; set; }

    /// <summary>Gets or sets the canonical unpadded base64url P-256 private key.</summary>
    public string? PrivateKey { get; set; }

    /// <inheritdoc />
    public override string ToString() => "AppSurfaceWebPushVapidKeyOptions { Redacted = true }";
}
