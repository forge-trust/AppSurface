using System.Text;
using ForgeTrust.AppSurface.Config;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests;

public sealed class GoogleSecretManagerCompositionProviderTests
{
    [Fact]
    public void Provider_Should_ExposeCanonicalIdAndResolveStrictUtf8Secret()
    {
        var client = new RecordingClient("projects/project/secrets/api-key/versions/4", Encoding.UTF8.GetBytes("secret-value"));
        var provider = CreateProvider(client, options => options.ProjectId = "project");
        var reference = new ConfigSecretReference("Production", "Service:ApiKey", "api-key", "4");

        var result = provider.Resolve(reference, new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromSeconds(1)));

        Assert.Equal("google-secret-manager", provider.Id);
        Assert.Equal(ConfigSecretProviderResolutionStatus.Resolved, result.Status);
        Assert.Equal("google-secret-manager", result.Source.ProviderId);
        Assert.Equal("remote", result.Source.SourceKind);
        Assert.Equal("secret-value", result.ReadSensitiveValue());
    }

    [Fact]
    public void Provider_Should_ClampSynchronousTimeoutToRemainingBudget()
    {
        var client = new RecordingClient("projects/project/secrets/api-key/versions/4", Encoding.UTF8.GetBytes("secret"));
        var provider = CreateProvider(client, options =>
        {
            options.ProjectId = "project";
            options.LookupTimeout = TimeSpan.FromMinutes(1);
        });
        var context = new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromMilliseconds(50));

        var result = provider.Resolve(
            new ConfigSecretReference("Production", "Service:ApiKey", "api-key", "4"), context);

        Assert.Equal(ConfigSecretProviderResolutionStatus.Resolved, result.Status);
        Assert.True(client.LastTimeout <= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Provider_Should_RejectInvalidReferenceWithoutClientCall()
    {
        var client = new RecordingClient("unused", Encoding.UTF8.GetBytes("secret"));
        var provider = CreateProvider(client, _ => { });
        var reference = new ConfigSecretReference("Production", "Service:ApiKey", "api-key", null);

        Assert.Equal(ConfigSecretReferenceValidationStatus.Invalid, provider.ValidateReference(reference).Status);
        Assert.Equal(
            ConfigSecretProviderResolutionStatus.InvalidReference,
            provider.Resolve(reference, new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromSeconds(1))).Status);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void Provider_Should_InspectMappingsAcrossRootIncludingPlainDestinationsButNotDescendantConventions()
    {
        var provider = CreateProvider(new RecordingClient("unused", []), options =>
        {
            options.ProjectId = "project";
            options.MapSecret("Service:Plain", "plain", "1");
            options.MapSecret("Service:Nested:ApiKey", "nested", "2");
            options.EnableConventionResolver("Service", "service-", version: "3");
        });

        var claims = provider.InspectClaims("Service", ["Service:ApiKey"]);

        Assert.Equal(2, claims.Count(claim => claim.Kind == ConfigSecretConfiguredClaimKind.ExactMapping));
        Assert.Contains(claims, claim => claim.Kind == ConfigSecretConfiguredClaimKind.RootConvention
            && claim.LogicalPath == "Service");
        Assert.DoesNotContain(claims, claim => claim.LogicalPath == "Service:ApiKey");
    }

    [Fact]
    public void Provider_Should_MapGoogleFailuresWithoutLeakingExceptionText()
    {
        var provider = CreateProvider(
            new ThrowingClient(new RpcException(new Status(StatusCode.PermissionDenied, "secret-payload"))),
            options => options.ProjectId = "project");

        var result = provider.Resolve(
            new ConfigSecretReference("Production", "Service:ApiKey", "api-key", "4"),
            new ConfigSecretResolutionContext(TimeProvider.System, TimeSpan.FromSeconds(1)));

        Assert.Equal(ConfigSecretProviderResolutionStatus.AccessDenied, result.Status);
        ValueSafeAssert.DoesNotExpose("secret-payload", result.ToString());
    }

    private static GoogleSecretManagerConfigProvider CreateProvider(
        IAppSurfaceGoogleSecretManagerClient client,
        Action<AppSurfaceGoogleSecretManagerOptions> configure)
    {
        var options = new AppSurfaceGoogleSecretManagerOptions();
        configure(options);
        return new GoogleSecretManagerConfigProvider(Options.Create(options), client);
    }

    private sealed class RecordingClient : IAppSurfaceGoogleSecretManagerClient
    {
        private readonly string _resourceName;
        private readonly byte[] _payload;

        public RecordingClient(string resourceName, byte[] payload)
        {
            _resourceName = resourceName;
            _payload = payload;
        }

        public int Calls { get; private set; }
        public TimeSpan LastTimeout { get; private set; }

        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
        {
            Assert.Equal(_resourceName, resourceName);
            Calls++;
            LastTimeout = timeout;
            return new AppSurfaceGoogleSecretPayload(_payload, resourceName);
        }
    }

    private sealed class ThrowingClient(Exception exception) : IAppSurfaceGoogleSecretManagerClient
    {
        public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout) => throw exception;
    }
}
