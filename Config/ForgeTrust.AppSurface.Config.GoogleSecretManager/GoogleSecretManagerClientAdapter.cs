using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.SecretManager.V1;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Default <see cref="IAppSurfaceGoogleSecretManagerClient"/> backed by Google Cloud Secret Manager.
/// </summary>
/// <remarks>
/// The default constructor creates the Google client lazily so applications that register the provider but do not resolve
/// Google-backed keys avoid opening Secret Manager transport during service registration.
/// </remarks>
public sealed class GoogleSecretManagerClientAdapter : IAppSurfaceGoogleSecretManagerClient
{
    private readonly Lazy<SecretManagerServiceClient> _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleSecretManagerClientAdapter"/> class.
    /// </summary>
    public GoogleSecretManagerClientAdapter()
        : this(SecretManagerServiceClient.Create)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleSecretManagerClientAdapter"/> class.
    /// </summary>
    /// <param name="client">The Google Secret Manager client.</param>
    public GoogleSecretManagerClientAdapter(SecretManagerServiceClient client)
        : this(() => client ?? throw new ArgumentNullException(nameof(client)))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleSecretManagerClientAdapter"/> class with a lazy client factory.
    /// </summary>
    /// <param name="clientFactory">
    /// Factory invoked on first access through the internal <see cref="Lazy{T}"/>. It must return a non-null
    /// <see cref="SecretManagerServiceClient"/>.
    /// </param>
    internal GoogleSecretManagerClientAdapter(Func<SecretManagerServiceClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);

        _client = new Lazy<SecretManagerServiceClient>(
            () => clientFactory() ?? throw new InvalidOperationException("Google Secret Manager client factory returned null."),
            isThreadSafe: true);
    }

    /// <inheritdoc />
    public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
    {
        var response = _client.Value.AccessSecretVersion(
            new AccessSecretVersionRequest { Name = resourceName },
            CallSettings.FromExpiration(Expiration.FromTimeout(timeout)));
        var payload = response.Payload?.Data?.ToByteArray();
        if (payload is null)
        {
            throw new InvalidOperationException("Secret Manager returned a response without a payload.");
        }

        return new AppSurfaceGoogleSecretPayload(
            payload,
            response.Name);
    }
}
