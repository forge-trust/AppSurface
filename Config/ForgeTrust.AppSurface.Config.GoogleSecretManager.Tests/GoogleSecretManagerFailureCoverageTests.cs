using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerFailureCoverageTests
{
    [Theory]
    [InlineData(StatusCode.NotFound, ConfigSecretProviderResolutionStatus.Missing, false)]
    [InlineData(StatusCode.PermissionDenied, ConfigSecretProviderResolutionStatus.AccessDenied, false)]
    [InlineData(StatusCode.Unauthenticated, ConfigSecretProviderResolutionStatus.AccessDenied, false)]
    [InlineData(StatusCode.InvalidArgument, ConfigSecretProviderResolutionStatus.InvalidReference, false)]
    [InlineData(StatusCode.Cancelled, ConfigSecretProviderResolutionStatus.Unavailable, true)]
    [InlineData(StatusCode.Unavailable, ConfigSecretProviderResolutionStatus.Unavailable, true)]
    [InlineData(StatusCode.DeadlineExceeded, ConfigSecretProviderResolutionStatus.Unavailable, true)]
    [InlineData(StatusCode.Unknown, ConfigSecretProviderResolutionStatus.ProviderFailed, true)]
    public void Resolve_MapsGoogleFailuresToSafeRetryableResolution(
        StatusCode statusCode,
        ConfigSecretProviderResolutionStatus expectedStatus,
        bool expectedRetryable)
    {
        var provider = CreateProvider(new ThrowingClient(
            new RpcException(new Status(statusCode, "raw-secret must not be exposed"))));

        var result = provider.Resolve(
            new ConfigSecretReference("Production", "Service:ApiKey", "api-key", null),
            new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromSeconds(5)));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedRetryable, result.Retryable);
        Assert.Null(result.ReadSensitiveValue());
        ValueSafeAssert.DoesNotExpose("raw-secret", result.ToString());
    }

    [Fact]
    public void Resolve_InvalidDeclarationReferenceReturnsInvalidWithoutQueryingGoogle()
    {
        var client = new RecordingClient();
        var provider = CreateProvider(client);

        var result = provider.Resolve(
            new ConfigSecretReference("Production", " ", "api-key", null),
            new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromSeconds(5)));

        Assert.Equal(ConfigSecretProviderResolutionStatus.InvalidReference, result.Status);
        Assert.Empty(client.Resources);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("stable_alias")]
    public void OptionsValidator_AcceptsNumericAndAliasVersionSegments(string version)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project" };
        options.MapSecret("Service:ApiKey", "api-key", version);

        var result = new AppSurfaceGoogleSecretManagerOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    private static GoogleSecretManagerConfigProvider CreateProvider(IAppSurfaceGoogleSecretManagerClient client)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions { ProjectId = "project", DefaultVersion = "5" };
        options.MapSecret("Service:ApiKey", "api-key");
        return new GoogleSecretManagerConfigProvider(Options.Create(options), client);
    }

    private sealed class RecordingClient : IAppSurfaceGoogleSecretManagerClient
    {
        public List<string> Resources { get; } = [];

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Resources.Add(resourceName);
            return new(Encoding.UTF8.GetBytes("unused"), resourceName);
        }
    }

    private sealed class ThrowingClient(RpcException exception) : IAppSurfaceGoogleSecretManagerClient
    {
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) => throw exception;
    }
}
