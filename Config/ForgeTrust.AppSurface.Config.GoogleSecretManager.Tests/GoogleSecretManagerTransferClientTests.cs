using Google.Api.Gax.Grpc;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Grpc.Core;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerTransferClientTests
{
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
    }

    [Fact]
    public void ProbeSecretVersion_Should_MapNotFoundWithoutLeakingRawStatusMessage()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new RpcException(new Status(StatusCode.NotFound, "raw-secret should not leak"))));

        var result = client.ProbeSecretVersion("projects/project/secrets/api-key/versions/5", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.Missing, result.Status);
        Assert.Equal("google-secret-transfer-missing", result.Diagnostic?.Code);
        Assert.DoesNotContain("raw-secret", result.Diagnostic?.ToDisplayString(), StringComparison.Ordinal);
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

    [Fact]
    public void AddSecretVersion_Should_MapPermissionDeniedWithoutLeakingRawStatusMessage()
    {
        var client = new GoogleSecretManagerTransferClientAdapter(
            () => new TransferSecretManagerServiceClient(new RpcException(new Status(StatusCode.PermissionDenied, "raw-secret should not leak"))));

        var result = client.AddSecretVersion("projects/project/secrets/api-key", "payload", TimeSpan.FromSeconds(1));

        Assert.Equal(GoogleSecretManagerTransferStatus.AccessDenied, result.Status);
        Assert.Equal("google-secret-transfer-access-denied", result.Diagnostic?.Code);
        Assert.DoesNotContain("raw-secret", result.ToString(), StringComparison.Ordinal);
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
        Assert.DoesNotContain("raw-secret", result.ToString(), StringComparison.Ordinal);
    }

    private sealed class TransferSecretManagerServiceClient(
        Exception? exception = null,
        SecretVersion.Types.State versionState = SecretVersion.Types.State.Enabled,
        Exception? accessException = null) : SecretManagerServiceClient
    {
        public string? LastAddParent { get; private set; }

        public ByteString? LastAddPayload { get; private set; }

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
            return new SecretVersion { Name = $"{request.Parent}/versions/1" };
        }
    }
}
