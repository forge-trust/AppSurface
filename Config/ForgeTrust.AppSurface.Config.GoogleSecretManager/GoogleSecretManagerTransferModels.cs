using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Grpc.Core;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Classifies Google Secret Manager transfer operations without exposing payload bytes.
/// </summary>
public enum GoogleSecretManagerTransferStatus
{
    /// <summary>
    /// The requested Google Secret Manager resource exists and can participate in transfer planning.
    /// </summary>
    Ready,

    /// <summary>
    /// The requested Google Secret Manager resource was written.
    /// </summary>
    Written,

    /// <summary>
    /// The requested Google Secret Manager resource does not exist.
    /// </summary>
    Missing,

    /// <summary>
    /// The current credentials cannot access the requested Google Secret Manager resource.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// Google Secret Manager or the local transport is temporarily unavailable.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The supplied Google Secret Manager resource name is invalid.
    /// </summary>
    InvalidResource,

    /// <summary>
    /// The Google Secret Manager request was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Google Secret Manager returned an unexpected provider failure.
    /// </summary>
    ProviderFailed,

    /// <summary>
    /// The requested Google Secret Manager secret version is not enabled for access.
    /// </summary>
    NotEnabled
}

/// <summary>
/// Describes a value-safe Google Secret Manager transfer diagnostic.
/// </summary>
public sealed record AppSurfaceGoogleSecretTransferDiagnostic(
    string Code,
    string Problem,
    string Cause,
    string Fix,
    string Docs,
    bool Retryable)
{
    /// <summary>
    /// Renders the diagnostic for terminal output without secret payloads.
    /// </summary>
    /// <returns>A display-safe diagnostic string.</returns>
    public string ToDisplayString() =>
        string.Join(
            Environment.NewLine,
            $"Problem: {Problem}",
            $"Cause: {Cause}",
            $"Fix: {Fix}",
            $"Docs: {Docs}",
            $"Retryable: {(Retryable ? "yes" : "no")}");

    /// <inheritdoc />
    public override string ToString() => ToDisplayString();
}

/// <summary>
/// Describes a value-safe Google Secret Manager probe result.
/// </summary>
/// <param name="Status">The probe status.</param>
/// <param name="ResourceName">The requested resource name.</param>
/// <param name="HasEnabledVersions">Whether an existing secret has at least one enabled version.</param>
/// <param name="Diagnostic">A display-safe diagnostic for failed probes.</param>
public sealed record AppSurfaceGoogleSecretProbeResult(
    GoogleSecretManagerTransferStatus Status,
    string ResourceName,
    bool HasEnabledVersions,
    AppSurfaceGoogleSecretTransferDiagnostic? Diagnostic)
{
    /// <summary>
    /// Creates a successful probe result.
    /// </summary>
    /// <param name="resourceName">The requested resource name.</param>
    /// <param name="hasEnabledVersions">Whether an existing secret has enabled versions.</param>
    /// <returns>The successful probe result.</returns>
    public static AppSurfaceGoogleSecretProbeResult Ready(string resourceName, bool hasEnabledVersions = false) =>
        new(GoogleSecretManagerTransferStatus.Ready, resourceName, hasEnabledVersions, null);

    /// <summary>
    /// Creates a failed probe result.
    /// </summary>
    /// <param name="status">The failure status.</param>
    /// <param name="resourceName">The requested resource name.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <returns>The failed probe result.</returns>
    public static AppSurfaceGoogleSecretProbeResult Failed(
        GoogleSecretManagerTransferStatus status,
        string resourceName,
        AppSurfaceGoogleSecretTransferDiagnostic diagnostic) =>
        new(status, resourceName, false, diagnostic);
}

/// <summary>
/// Describes a value-safe Google Secret Manager write result.
/// </summary>
/// <param name="Status">The write status.</param>
/// <param name="ResourceName">The requested secret parent resource name.</param>
/// <param name="WrittenVersionResourceName">The written version resource name, when Google returned one.</param>
/// <param name="Diagnostic">A display-safe diagnostic for failed writes.</param>
public sealed record AppSurfaceGoogleSecretWriteResult(
    GoogleSecretManagerTransferStatus Status,
    string ResourceName,
    string? WrittenVersionResourceName,
    AppSurfaceGoogleSecretTransferDiagnostic? Diagnostic)
{
    /// <summary>
    /// Creates a successful write result.
    /// </summary>
    /// <param name="resourceName">The secret parent resource name.</param>
    /// <param name="writtenVersionResourceName">The written version resource name.</param>
    /// <returns>The successful write result.</returns>
    public static AppSurfaceGoogleSecretWriteResult Written(string resourceName, string? writtenVersionResourceName) =>
        new(GoogleSecretManagerTransferStatus.Written, resourceName, writtenVersionResourceName, null);

    /// <summary>
    /// Creates a failed write result.
    /// </summary>
    /// <param name="status">The failure status.</param>
    /// <param name="resourceName">The requested secret parent resource name.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <returns>The failed write result.</returns>
    public static AppSurfaceGoogleSecretWriteResult Failed(
        GoogleSecretManagerTransferStatus status,
        string resourceName,
        AppSurfaceGoogleSecretTransferDiagnostic diagnostic) =>
        new(status, resourceName, null, diagnostic);
}

/// <summary>
/// Describes a value-safe Google Secret Manager payload access result.
/// </summary>
/// <param name="Status">The access status.</param>
/// <param name="ResourceName">The requested version resource name.</param>
/// <param name="Payload">The payload when access succeeded.</param>
/// <param name="Diagnostic">A display-safe diagnostic for failed access.</param>
public sealed record AppSurfaceGoogleSecretAccessResult(
    GoogleSecretManagerTransferStatus Status,
    string ResourceName,
    AppSurfaceGoogleSecretPayload? Payload,
    AppSurfaceGoogleSecretTransferDiagnostic? Diagnostic)
{
    /// <summary>
    /// Creates a successful access result.
    /// </summary>
    /// <param name="resourceName">The requested version resource name.</param>
    /// <param name="payload">The accessed payload.</param>
    /// <returns>The successful access result.</returns>
    public static AppSurfaceGoogleSecretAccessResult Accessed(string resourceName, AppSurfaceGoogleSecretPayload payload) =>
        new(GoogleSecretManagerTransferStatus.Ready, resourceName, payload, null);

    /// <summary>
    /// Creates a failed access result.
    /// </summary>
    /// <param name="status">The failure status.</param>
    /// <param name="resourceName">The requested version resource name.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <returns>The failed access result.</returns>
    public static AppSurfaceGoogleSecretAccessResult Failed(
        GoogleSecretManagerTransferStatus status,
        string resourceName,
        AppSurfaceGoogleSecretTransferDiagnostic diagnostic) =>
        new(status, resourceName, null, diagnostic);
}

/// <summary>
/// Probes and writes Google Secret Manager resources for explicit AppSurface secret transfer workflows.
/// </summary>
/// <remarks>
/// This seam is intentionally separate from <see cref="IAppSurfaceGoogleSecretManagerClient"/>, which remains the
/// read-only runtime configuration provider client. Implementations must keep diagnostics value-safe and must not create,
/// disable, destroy, rotate, or provision secrets for the v1 transfer workflow.
/// </remarks>
public interface IAppSurfaceGoogleSecretTransferClient
{
    /// <summary>
    /// Probes one existing Google Secret Manager secret parent.
    /// </summary>
    /// <param name="secretResourceName">The full <c>projects/.../secrets/...</c> resource name.</param>
    /// <param name="timeout">The bounded probe timeout.</param>
    /// <returns>The probe result.</returns>
    AppSurfaceGoogleSecretProbeResult ProbeSecret(string secretResourceName, TimeSpan timeout);

    /// <summary>
    /// Probes one existing Google Secret Manager secret version.
    /// </summary>
    /// <param name="versionResourceName">The full <c>projects/.../secrets/.../versions/...</c> resource name.</param>
    /// <param name="timeout">The bounded probe timeout.</param>
    /// <returns>The probe result.</returns>
    AppSurfaceGoogleSecretProbeResult ProbeSecretVersion(string versionResourceName, TimeSpan timeout);

    /// <summary>
    /// Accesses one Google Secret Manager version payload for an apply operation.
    /// </summary>
    /// <param name="versionResourceName">The full version resource name.</param>
    /// <param name="timeout">The bounded access timeout.</param>
    /// <returns>The version payload.</returns>
    AppSurfaceGoogleSecretAccessResult AccessSecretVersion(string versionResourceName, TimeSpan timeout);

    /// <summary>
    /// Adds a new enabled version to an existing Google Secret Manager secret.
    /// </summary>
    /// <param name="secretResourceName">The full secret parent resource name.</param>
    /// <param name="value">The UTF-8 text secret value.</param>
    /// <param name="timeout">The bounded write timeout.</param>
    /// <returns>The write result.</returns>
    AppSurfaceGoogleSecretWriteResult AddSecretVersion(string secretResourceName, string value, TimeSpan timeout);
}

/// <summary>
/// Default transfer client backed by Google Cloud Secret Manager.
/// </summary>
public sealed class GoogleSecretManagerTransferClientAdapter : IAppSurfaceGoogleSecretTransferClient
{
    private const string Docs = "google-secret-manager-transfer";
    private readonly Lazy<SecretManagerServiceClient> _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleSecretManagerTransferClientAdapter"/> class.
    /// </summary>
    public GoogleSecretManagerTransferClientAdapter()
        : this(SecretManagerServiceClient.Create)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleSecretManagerTransferClientAdapter"/> class.
    /// </summary>
    /// <param name="client">The Google Secret Manager client.</param>
    public GoogleSecretManagerTransferClientAdapter(SecretManagerServiceClient client)
        : this(CreateExplicitClientFactory(client))
    {
    }

    /// <summary>
    /// Creates a transfer client that uses one explicit service-account credential file.
    /// </summary>
    /// <param name="credentialFilePath">Absolute path to the Google credential file.</param>
    /// <returns>A client isolated from ambient Application Default Credentials.</returns>
    /// <remarks>
    /// Use this factory for operator-owned transfer profiles only. The caller owns file validation and must never include
    /// the path or file contents in diagnostics. Runtime configuration continues to use its existing credential flow.
    /// </remarks>
    public static GoogleSecretManagerTransferClientAdapter FromCredentialFile(string credentialFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialFilePath);
        var credential = CredentialFactory.FromFile<ServiceAccountCredential>(credentialFilePath).ToGoogleCredential();
        return new GoogleSecretManagerTransferClientAdapter(
            () => new SecretManagerServiceClientBuilder { GoogleCredential = credential }.Build());
    }

    internal GoogleSecretManagerTransferClientAdapter(Func<SecretManagerServiceClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);

        _client = new Lazy<SecretManagerServiceClient>(
            () => clientFactory() ?? throw new InvalidOperationException("Google Secret Manager client factory returned null."),
            isThreadSafe: true);
    }

    /// <inheritdoc />
    public AppSurfaceGoogleSecretProbeResult ProbeSecret(string secretResourceName, TimeSpan timeout)
    {
        try
        {
            _client.Value.GetSecret(
                new GetSecretRequest { Name = secretResourceName },
                CallSettings.FromExpiration(Expiration.FromTimeout(timeout)));
            var versions = _client.Value.ListSecretVersions(
                new ListSecretVersionsRequest { Parent = secretResourceName },
                CallSettings.FromExpiration(Expiration.FromTimeout(timeout)));
            var hasEnabledVersions = versions.Any(version => version.State == SecretVersion.Types.State.Enabled);
            return AppSurfaceGoogleSecretProbeResult.Ready(secretResourceName, hasEnabledVersions);
        }
        catch (RpcException ex)
        {
            return AppSurfaceGoogleSecretProbeResult.Failed(
                MapRpcStatus(ex.StatusCode),
                secretResourceName,
                CreateDiagnostic("probe", ex.StatusCode));
        }
        catch (TimeoutException)
        {
            return AppSurfaceGoogleSecretProbeResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                secretResourceName,
                CreateUnavailableDiagnostic("probe"));
        }
        catch (InvalidOperationException)
        {
            return AppSurfaceGoogleSecretProbeResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                secretResourceName,
                CreateUnavailableDiagnostic("probe"));
        }
    }

    /// <inheritdoc />
    public AppSurfaceGoogleSecretProbeResult ProbeSecretVersion(string versionResourceName, TimeSpan timeout)
    {
        try
        {
            var version = _client.Value.GetSecretVersion(
                new GetSecretVersionRequest { Name = versionResourceName },
                CallSettings.FromExpiration(Expiration.FromTimeout(timeout)));
            if (version.State != SecretVersion.Types.State.Enabled)
            {
                return AppSurfaceGoogleSecretProbeResult.Failed(
                    GoogleSecretManagerTransferStatus.NotEnabled,
                    versionResourceName,
                    CreateVersionNotEnabledDiagnostic(version.State));
            }

            return AppSurfaceGoogleSecretProbeResult.Ready(versionResourceName);
        }
        catch (RpcException ex)
        {
            return AppSurfaceGoogleSecretProbeResult.Failed(
                MapRpcStatus(ex.StatusCode),
                versionResourceName,
                CreateDiagnostic("probe", ex.StatusCode));
        }
        catch (TimeoutException)
        {
            return AppSurfaceGoogleSecretProbeResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                versionResourceName,
                CreateUnavailableDiagnostic("probe"));
        }
        catch (InvalidOperationException)
        {
            return AppSurfaceGoogleSecretProbeResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                versionResourceName,
                CreateUnavailableDiagnostic("probe"));
        }
    }

    /// <inheritdoc />
    public AppSurfaceGoogleSecretAccessResult AccessSecretVersion(string versionResourceName, TimeSpan timeout)
    {
        try
        {
            var response = _client.Value.AccessSecretVersion(
                new AccessSecretVersionRequest { Name = versionResourceName },
                CallSettings.FromExpiration(Expiration.FromTimeout(timeout)));
            var payload = response.Payload?.Data?.ToByteArray();
            if (payload is null)
            {
                return AppSurfaceGoogleSecretAccessResult.Failed(
                    GoogleSecretManagerTransferStatus.ProviderFailed,
                    versionResourceName,
                    CreateProviderDiagnostic("access"));
            }

            return AppSurfaceGoogleSecretAccessResult.Accessed(
                versionResourceName,
                new AppSurfaceGoogleSecretPayload(payload, response.Name));
        }
        catch (RpcException ex)
        {
            return AppSurfaceGoogleSecretAccessResult.Failed(
                MapRpcStatus(ex.StatusCode),
                versionResourceName,
                CreateDiagnostic("access", ex.StatusCode));
        }
        catch (TimeoutException)
        {
            return AppSurfaceGoogleSecretAccessResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                versionResourceName,
                CreateUnavailableDiagnostic("access"));
        }
        catch (InvalidOperationException)
        {
            return AppSurfaceGoogleSecretAccessResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                versionResourceName,
                CreateUnavailableDiagnostic("access"));
        }
    }

    /// <inheritdoc />
    public AppSurfaceGoogleSecretWriteResult AddSecretVersion(string secretResourceName, string value, TimeSpan timeout)
    {
        try
        {
            var response = _client.Value.AddSecretVersion(
                new AddSecretVersionRequest
                {
                    Parent = secretResourceName,
                    Payload = new SecretPayload { Data = ByteString.CopyFromUtf8(value) }
                },
                CallSettings.FromExpiration(Expiration.FromTimeout(timeout)));
            return AppSurfaceGoogleSecretWriteResult.Written(secretResourceName, response.Name);
        }
        catch (RpcException ex)
        {
            return AppSurfaceGoogleSecretWriteResult.Failed(
                MapRpcStatus(ex.StatusCode),
                secretResourceName,
                CreateDiagnostic("write", ex.StatusCode));
        }
        catch (TimeoutException)
        {
            return AppSurfaceGoogleSecretWriteResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                secretResourceName,
                CreateUnavailableDiagnostic("write"));
        }
        catch (InvalidOperationException)
        {
            return AppSurfaceGoogleSecretWriteResult.Failed(
                GoogleSecretManagerTransferStatus.Unavailable,
                secretResourceName,
                CreateUnavailableDiagnostic("write"));
        }
    }

    private static Func<SecretManagerServiceClient> CreateExplicitClientFactory(SecretManagerServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return () => client;
    }

    private static GoogleSecretManagerTransferStatus MapRpcStatus(StatusCode statusCode) =>
        statusCode switch
        {
            StatusCode.NotFound => GoogleSecretManagerTransferStatus.Missing,
            StatusCode.PermissionDenied or StatusCode.Unauthenticated => GoogleSecretManagerTransferStatus.AccessDenied,
            StatusCode.InvalidArgument => GoogleSecretManagerTransferStatus.InvalidResource,
            StatusCode.Cancelled => GoogleSecretManagerTransferStatus.Cancelled,
            StatusCode.Unavailable or StatusCode.DeadlineExceeded => GoogleSecretManagerTransferStatus.Unavailable,
            _ => GoogleSecretManagerTransferStatus.ProviderFailed
        };

    private static AppSurfaceGoogleSecretTransferDiagnostic CreateDiagnostic(string operation, StatusCode statusCode)
    {
        var status = MapRpcStatus(statusCode);
        return new AppSurfaceGoogleSecretTransferDiagnostic(
            CodeFor(status),
            "Google Secret Manager transfer could not complete.",
            $"Google Secret Manager returned {statusCode} during {operation}.",
            "Verify the resource name, credentials, IAM permissions, and network availability.",
            Docs,
            status is GoogleSecretManagerTransferStatus.Unavailable or GoogleSecretManagerTransferStatus.Cancelled);
    }

    private static AppSurfaceGoogleSecretTransferDiagnostic CreateUnavailableDiagnostic(string operation) =>
        new(
            CodeFor(GoogleSecretManagerTransferStatus.Unavailable),
            "Google Secret Manager transfer could not complete.",
            $"The Google Secret Manager client was unavailable during {operation}.",
            "Verify Application Default Credentials and retry after the provider is available.",
            Docs,
            true);

    private static AppSurfaceGoogleSecretTransferDiagnostic CreateProviderDiagnostic(string operation) =>
        new(
            CodeFor(GoogleSecretManagerTransferStatus.ProviderFailed),
            "Google Secret Manager transfer could not complete.",
            $"Google Secret Manager returned an incomplete response during {operation}.",
            "Retry the operation, then check provider status if the failure persists.",
            Docs,
            true);

    private static AppSurfaceGoogleSecretTransferDiagnostic CreateVersionNotEnabledDiagnostic(SecretVersion.Types.State state) =>
        new(
            CodeFor(GoogleSecretManagerTransferStatus.NotEnabled),
            "Google Secret Manager version is not enabled.",
            $"Google Secret Manager reported the version state as {state}.",
            "Choose an enabled version or enable the version before pulling it into LocalSecrets.",
            Docs,
            false);

    private static string CodeFor(GoogleSecretManagerTransferStatus status) =>
        status switch
        {
            GoogleSecretManagerTransferStatus.Missing => "google-secret-transfer-missing",
            GoogleSecretManagerTransferStatus.AccessDenied => "google-secret-transfer-access-denied",
            GoogleSecretManagerTransferStatus.Unavailable => "google-secret-transfer-unavailable",
            GoogleSecretManagerTransferStatus.InvalidResource => "google-secret-transfer-invalid-resource",
            GoogleSecretManagerTransferStatus.Cancelled => "google-secret-transfer-cancelled",
            GoogleSecretManagerTransferStatus.NotEnabled => "google-secret-transfer-version-not-enabled",
            _ => "google-secret-transfer-failed"
        };
}
