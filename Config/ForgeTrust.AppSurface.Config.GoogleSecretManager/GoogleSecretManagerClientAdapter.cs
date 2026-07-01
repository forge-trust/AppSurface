using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.SecretManager.V1;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Default <see cref="IAppSurfaceGoogleSecretManagerClient"/> backed by Google Cloud Secret Manager.
/// </summary>
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

    internal GoogleSecretManagerClientAdapter(Func<SecretManagerServiceClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);

        _client = new Lazy<SecretManagerServiceClient>(clientFactory, isThreadSafe: true);
    }

    /// <inheritdoc />
    public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
    {
        var response = _client.Value.AccessSecretVersion(
            new AccessSecretVersionRequest { Name = resourceName },
            CallSettings.FromExpiration(Expiration.FromTimeout(timeout)));
        return new AppSurfaceGoogleSecretPayload(
            response.Payload?.Data?.ToByteArray() ?? [],
            response.Name);
    }
}
