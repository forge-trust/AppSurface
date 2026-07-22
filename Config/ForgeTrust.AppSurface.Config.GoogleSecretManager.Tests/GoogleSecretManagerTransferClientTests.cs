using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Grpc.Core;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerTransferClientTests
{
    [Fact]
    public void PublicTransferStatus_Should_KeepStableNumericValues()
    {
        Assert.Equal(0, (int)GoogleSecretManagerTransferStatus.Ready);
        Assert.Equal(1, (int)GoogleSecretManagerTransferStatus.Written);
        Assert.Equal(2, (int)GoogleSecretManagerTransferStatus.Missing);
        Assert.Equal(3, (int)GoogleSecretManagerTransferStatus.AccessDenied);
        Assert.Equal(4, (int)GoogleSecretManagerTransferStatus.Unavailable);
        Assert.Equal(5, (int)GoogleSecretManagerTransferStatus.InvalidResource);
        Assert.Equal(6, (int)GoogleSecretManagerTransferStatus.Cancelled);
        Assert.Equal(7, (int)GoogleSecretManagerTransferStatus.ProviderFailed);
        Assert.Equal(8, (int)GoogleSecretManagerTransferStatus.NotEnabled);
        Assert.Equal(9, (int)GoogleSecretManagerTransferStatus.IndeterminateWrite);
    }

    [Fact]
    public void CredentialFileFactory_Should_CreateIsolatedClientFromValidServiceAccountJson()
    {
        var path = Path.Join(Path.GetTempPath(), $"appsurface-google-credential-{Guid.NewGuid():N}.json");
        try
        {
            using var rsa = RSA.Create(2048);
            var json = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = "project",
                private_key_id = "test-key",
                private_key = rsa.ExportPkcs8PrivateKeyPem(),
                client_email = "appsurface-test@project.iam.gserviceaccount.com",
                client_id = "123",
                auth_uri = "https://accounts.google.com/o/oauth2/auth",
                token_uri = "https://oauth2.googleapis.com/token"
            });
            File.WriteAllText(path, json);

            var client = GoogleSecretManagerTransferClientAdapter.FromCredentialFile(path);

            Assert.NotNull(client);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExplicitClientConstructor_Should_RejectNullAndUseProvidedClient()
    {
        Assert.Throws<ArgumentNullException>(() => new GoogleSecretManagerTransferClientAdapter((SecretManagerServiceClient)null!));

        var serviceClient = new TransferSecretManagerServiceClient();
        var client = new GoogleSecretManagerTransferClientAdapter(serviceClient);

        Assert.Equal(
            GoogleSecretManagerTransferStatus.Ready,
            client.ProbeSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1)).Status);
    }

    [Theory]
    [InlineData("rpc")]
    [InlineData("timeout")]
    [InlineData("invalid")]
    public void ProbeSecret_Should_MapClientFailuresValueSafely(string failure)
    {
        Exception exception = failure switch
        {
            "rpc" => new RpcException(new Status(StatusCode.PermissionDenied, "sentinel-secret")),
            "timeout" => new TimeoutException("sentinel-secret"),
            _ => new InvalidOperationException("sentinel-secret")
        };
        var client = new GoogleSecretManagerTransferClientAdapter(() => throw exception);

        var result = client.ProbeSecret("projects/project/secrets/api-key", TimeSpan.FromSeconds(1));

        Assert.Equal(
            failure == "rpc" ? GoogleSecretManagerTransferStatus.AccessDenied : GoogleSecretManagerTransferStatus.Unavailable,
            result.Status);
        ValueSafeAssert.DoesNotExpose("sentinel-secret", result.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProbeSecret_Should_ReturnEnabledVersionAvailability(bool hasEnabledVersions)
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(hasEnabledVersions: hasEnabledVersions));

        var result = client.ProbeSecret("projects/project/secrets/api-key", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Ready, result.Status);
        Assert.Equal(hasEnabledVersions, result.HasEnabledVersions);
    }

    [Fact]
    public void ProbeSecret_Should_RequestOneEnabledVersionWithSharedAbsoluteDeadline()
    {
        var serviceClient = new TransferSecretManagerServiceClient(
            hasEnabledVersions: false,
            hasAdditionalEnabledVersionPage: true);
        var client = new GoogleSecretManagerTransferClientAdapter(() => serviceClient);

        var result = client.ProbeSecret("projects/project/secrets/api-key", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Ready, result.Status);
        Assert.False(result.HasEnabledVersions);
        Assert.Equal("projects/project/secrets/api-key", serviceClient.LastGetRequest?.Name);
        Assert.Equal("projects/project/secrets/api-key", serviceClient.LastListRequest?.Parent);
        Assert.Equal("state:ENABLED", serviceClient.LastListRequest?.Filter);
        Assert.Equal(1, serviceClient.LastListRequest?.PageSize);
        Assert.Equal(1, serviceClient.ListRawResponseEnumerationCount);
        Assert.Equal(ExpirationType.Deadline, serviceClient.LastGetCallSettings?.Expiration?.Type);
        Assert.Equal(
            serviceClient.LastGetCallSettings?.Expiration?.Deadline,
            serviceClient.LastListCallSettings?.Expiration?.Deadline);
    }

    [Fact]
    public void ProbeSecret_Should_MapNullClientFactoryResultValueSafely()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(() => null!);

        var result = client.ProbeSecret("projects/project/secrets/api-key", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Unavailable, result.Status);
        Assert.Equal("google-secret-transfer-unavailable", result.Diagnostic?.Code);
        Assert.DoesNotContain("Application Default Credentials", result.Diagnostic?.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeSecretVersion_Should_ReturnReady_WhenVersionExists()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient());

        var result = client.ProbeSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Ready, result.Status);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void ProbeSecretVersion_Should_Fail_WhenVersionIsDisabled()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(versionState: SecretVersion.Types.State.Disabled));

        var result = client.ProbeSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.NotEnabled, result.Status);
        Assert.Equal("google-secret-transfer-version-not-enabled", result.Diagnostic?.Code);
        Assert.DoesNotContain("LocalSecrets", result.Diagnostic?.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeSecretVersion_Should_MapNotFoundWithoutLeakingRawStatusMessage()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new RpcException(new Status(StatusCode.NotFound, "raw-secret should not leak"))));

        var result = client.ProbeSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Missing, result.Status);
        Assert.Equal("google-secret-transfer-missing", result.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("raw-secret", result.Diagnostic?.ToDisplayString());
    }

    [Theory]
    [InlineData(StatusCode.InvalidArgument, GoogleSecretManagerTransferStatus.InvalidResource, "google-secret-transfer-invalid-resource")]
    [InlineData(StatusCode.Cancelled, GoogleSecretManagerTransferStatus.Cancelled, "google-secret-transfer-cancelled")]
    [InlineData(StatusCode.Unauthenticated, GoogleSecretManagerTransferStatus.AccessDenied, "google-secret-transfer-access-denied")]
    [InlineData(StatusCode.Unavailable, GoogleSecretManagerTransferStatus.Unavailable, "google-secret-transfer-unavailable")]
    [InlineData(StatusCode.DeadlineExceeded, GoogleSecretManagerTransferStatus.Unavailable, "google-secret-transfer-unavailable")]
    [InlineData(StatusCode.Unknown, GoogleSecretManagerTransferStatus.ProviderFailed, "google-secret-transfer-failed")]
    public void ProbeSecretVersion_Should_MapProviderStatusesValueSafely(
        StatusCode statusCode,
        GoogleSecretManagerTransferStatus expectedStatus,
        string expectedDiagnostic)
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new RpcException(new Status(statusCode, "sentinel-secret"))));

        var result = client.ProbeSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedDiagnostic, result.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("sentinel-secret", result.Diagnostic?.ToDisplayString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProbeSecretVersion_Should_MapClientAvailabilityFailures(bool timeout)
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(timeout ? new TimeoutException() : new InvalidOperationException()));

        var result = client.ProbeSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Unavailable, result.Status);
        Assert.Equal("google-secret-transfer-unavailable", result.Diagnostic?.Code);
    }

    [Fact]
    public void AddSecretVersion_Should_WriteUtf8PayloadToExistingSecret()
    {
        var serviceClient = new TransferSecretManagerServiceClient();
        var client = new GoogleSecretManagerTransferClientAdapter(() => serviceClient);

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "payload", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Written, result.Status);
        Assert.Equal("projects/project/secrets/api-key/versions/1", result.WrittenVersionResourceName);
        Assert.Equal("projects/project/secrets/api-key", serviceClient.LastAddParent);
        Assert.Equal("payload", serviceClient.LastAddPayload?.ToStringUtf8());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("projects/project/secrets/api-key/versions/latest")]
    [InlineData("projects/other/secrets/api-key/versions/1")]
    public void AddSecretVersion_Should_TreatInvalidWrittenVersionNameAsIndeterminate(string writtenVersionName)
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(addVersionName: writtenVersionName));

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "sentinel-secret", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.IndeterminateWrite, result.Status);
        Assert.Equal("google-secret-transfer-indeterminate-write", result.Diagnostic?.Code);
        Assert.False(result.Diagnostic?.Retryable);
        ValueSafeAssert.DoesNotExpose("sentinel-secret", result.ToString());
    }

    [Fact]
    public void AddSecretVersion_Should_MapPermissionDeniedWithoutLeakingRawStatusMessage()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new RpcException(new Status(StatusCode.PermissionDenied, "raw-secret should not leak"))));

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "payload", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.AccessDenied, result.Status);
        Assert.Equal("google-secret-transfer-access-denied", result.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("raw-secret", result.ToString());
    }

    [Fact]
    public void AccessSecretVersion_Should_MapPermissionDeniedWithoutLeakingRawStatusMessage()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(accessException: new RpcException(new Status(StatusCode.PermissionDenied, "raw-secret should not leak"))));

        var result = client.AccessSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.AccessDenied, result.Status);
        Assert.Equal("google-secret-transfer-access-denied", result.Diagnostic?.Code);
        Assert.Null(result.Payload);
        ValueSafeAssert.DoesNotExpose("raw-secret", result.ToString());
    }

    [Fact]
    public void AccessSecretVersion_Should_FailValueSafely_WhenProviderOmitsPayload()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(returnNullPayload: true));

        var result = client.AccessSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.ProviderFailed, result.Status);
        Assert.Equal("google-secret-transfer-failed", result.Diagnostic?.Code);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void AccessSecretVersion_Should_ReturnPayload_WhenProviderResponds()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient());

        var result = client.AccessSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Ready, result.Status);
        Assert.Equal("payload", Encoding.UTF8.GetString(result.Payload!.Data));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AccessSecretVersion_Should_MapClientAvailabilityFailures(bool timeout)
    {
        var exception = timeout ? (Exception)new TimeoutException() : new InvalidOperationException();
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(accessException: exception));

        var result = client.AccessSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Unavailable, result.Status);
        Assert.Equal("google-secret-transfer-unavailable", result.Diagnostic?.Code);
        Assert.Null(result.Payload);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddSecretVersion_Should_MapClientCreationAvailabilityFailures(bool timeout)
    {
        var exception = timeout ? (Exception)new TimeoutException() : new InvalidOperationException();
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => throw exception);

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "payload", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Unavailable, result.Status);
        Assert.Equal("google-secret-transfer-unavailable", result.Diagnostic?.Code);
    }

    [Fact]
    public void AddSecretVersion_Should_MapPostDispatchTimeoutToIndeterminateWrite()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new TimeoutException()));

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "payload", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.IndeterminateWrite, result.Status);
        Assert.Equal("google-secret-transfer-indeterminate-write", result.Diagnostic?.Code);
        Assert.False(result.Diagnostic?.Retryable);
    }

    [Theory]
    [InlineData(StatusCode.NotFound, GoogleSecretManagerTransferStatus.Missing)]
    [InlineData(StatusCode.InvalidArgument, GoogleSecretManagerTransferStatus.InvalidResource)]
    [InlineData(StatusCode.Cancelled, GoogleSecretManagerTransferStatus.Cancelled)]
    [InlineData(StatusCode.Unknown, GoogleSecretManagerTransferStatus.ProviderFailed)]
    public void AccessSecretVersion_Should_MapProviderFailuresValueSafely(StatusCode statusCode, GoogleSecretManagerTransferStatus expected)
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(accessException: new RpcException(new Status(statusCode, "sentinel-secret"))));

        var result = client.AccessSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(expected, result.Status);
        ValueSafeAssert.DoesNotExpose("sentinel-secret", result.ToString());
    }

    [Theory]
    [InlineData(StatusCode.NotFound, GoogleSecretManagerTransferStatus.Missing, "google-secret-transfer-missing")]
    [InlineData(StatusCode.PermissionDenied, GoogleSecretManagerTransferStatus.AccessDenied, "google-secret-transfer-access-denied")]
    [InlineData(StatusCode.Unauthenticated, GoogleSecretManagerTransferStatus.AccessDenied, "google-secret-transfer-access-denied")]
    [InlineData(StatusCode.InvalidArgument, GoogleSecretManagerTransferStatus.InvalidResource, "google-secret-transfer-invalid-resource")]
    public void AddSecretVersion_Should_KeepDefinitiveProviderFailuresDefinitive(
        StatusCode statusCode,
        GoogleSecretManagerTransferStatus expected,
        string expectedDiagnostic)
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new RpcException(new Status(statusCode, "sentinel-secret"))));

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "payload", TimeSpan.FromSeconds(1));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expectedDiagnostic, result.Diagnostic?.Code);
        ValueSafeAssert.DoesNotExpose("sentinel-secret", result.ToString());
    }

    [Theory]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Unknown)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Aborted)]
    [InlineData(StatusCode.DataLoss)]
    [InlineData(StatusCode.ResourceExhausted)]
    public void AddSecretVersion_Should_MapAmbiguousProviderFailuresToIndeterminateWrite(StatusCode statusCode)
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new RpcException(new Status(statusCode, "sentinel-secret"))));

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "payload", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.IndeterminateWrite, result.Status);
        Assert.Equal("google-secret-transfer-indeterminate-write", result.Diagnostic?.Code);
        Assert.False(result.Diagnostic?.Retryable);
        ValueSafeAssert.DoesNotExpose("sentinel-secret", result.ToString());
    }

    private sealed class TransferSecretManagerServiceClient(
        Exception? exception = null,
        SecretVersion.Types.State versionState = SecretVersion.Types.State.Enabled,
        Exception? accessException = null,
        bool returnNullPayload = false,
        bool hasEnabledVersions = true,
        bool hasAdditionalEnabledVersionPage = false,
        string? addVersionName = null) : SecretManagerServiceClient
    {
        public GetSecretRequest? LastGetRequest { get; private set; }

        public CallSettings? LastGetCallSettings { get; private set; }

        public ListSecretVersionsRequest? LastListRequest { get; private set; }

        public CallSettings? LastListCallSettings { get; private set; }

        public int ListRawResponseEnumerationCount { get; private set; }

        public string? LastAddParent { get; private set; }

        public ByteString? LastAddPayload { get; private set; }

        public override Secret GetSecret(GetSecretRequest request, CallSettings? callSettings = null)
        {
            if (exception != null)
            {
                throw exception;
            }

            LastGetRequest = request;
            LastGetCallSettings = callSettings;
            return new Secret { Name = request.Name };
        }

        public override PagedEnumerable<ListSecretVersionsResponse, SecretVersion> ListSecretVersions(
            ListSecretVersionsRequest request,
            CallSettings? callSettings = null)
        {
            LastListRequest = request;
            LastListCallSettings = callSettings;
            return new SecretVersionPages(
                hasEnabledVersions,
                hasAdditionalEnabledVersionPage,
                () => ListRawResponseEnumerationCount++);
        }

        public override SecretVersion GetSecretVersion(
            GetSecretVersionRequest request,
            CallSettings? callSettings = null)
        {
            if (exception != null)
            {
                throw exception;
            }

            return new SecretVersion { Name = request.Name, State = versionState };
        }

        public override AccessSecretVersionResponse AccessSecretVersion(
            AccessSecretVersionRequest request,
            CallSettings? callSettings = null)
        {
            if (accessException != null)
            {
                throw accessException;
            }

            if (returnNullPayload)
            {
                return new AccessSecretVersionResponse { Name = request.Name };
            }

            return new AccessSecretVersionResponse
            {
                Name = request.Name,
                Payload = new SecretPayload { Data = ByteString.CopyFromUtf8("payload") }
            };
        }

        public override SecretVersion AddSecretVersion(
            AddSecretVersionRequest request,
            CallSettings? callSettings = null)
        {
            if (exception != null)
            {
                throw exception;
            }

            LastAddParent = request.Parent;
            LastAddPayload = request.Payload.Data;
            return new SecretVersion { Name = addVersionName ?? $"{request.Parent}/versions/1" };
        }
    }

    private sealed class SecretVersionPages(
        bool hasEnabledVersions,
        bool hasAdditionalEnabledVersionPage,
        Action rawResponseEnumerated) :
        PagedEnumerable<ListSecretVersionsResponse, SecretVersion>
    {
        public override IEnumerator<SecretVersion> GetEnumerator() =>
            AsRawResponses().SelectMany(static response => response.Versions).GetEnumerator();

        public override IEnumerable<ListSecretVersionsResponse> AsRawResponses()
        {
            var response = new ListSecretVersionsResponse();
            response.Versions.Add(new SecretVersion
            {
                State = hasEnabledVersions
                    ? SecretVersion.Types.State.Enabled
                    : SecretVersion.Types.State.Disabled
            });
            rawResponseEnumerated();
            yield return response;

            if (hasAdditionalEnabledVersionPage)
            {
                var additionalResponse = new ListSecretVersionsResponse();
                additionalResponse.Versions.Add(new SecretVersion { State = SecretVersion.Types.State.Enabled });
                rawResponseEnumerated();
                yield return additionalResponse;
            }
        }
    }
}
