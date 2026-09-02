using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakLocalSeedTests
{
    [Fact]
    public void WithLocalSeed_RegistersALinearCompletionChainAndScopedSafeContext()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = AddKeycloak(builder, directory.Path);
        AppSurfaceKeycloakLocalSeedContext? firstContext = null;
        AppSurfaceKeycloakLocalSeedContext? secondContext = null;

        var first = keycloak.WithLocalSeed(
            "identity-bootstrap",
            context =>
            {
                firstContext = context;
                return AddProject(builder, context.ResourceName);
            },
            options => AllowCurrentEnvironment(builder, options));
        var second = keycloak.WithLocalSeed(
            "candidate-fixture",
            context =>
            {
                secondContext = context;
                return AddProject(builder, context.ResourceName);
            },
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.After(first);
            });

        Assert.Equal("identity-bootstrap", first.Name);
        Assert.Equal("candidate-fixture", second.Name);
        Assert.Equal("keycloak-proof-seed-identity-bootstrap", first.Resource.Resource.Name);
        Assert.Equal("keycloak-proof-seed-candidate-fixture", second.Resource.Resource.Name);
        Assert.Equal(keycloak.Configuration.Authority, firstContext!.Authority);
        Assert.Equal("appsurface-dev", firstContext.RealmName);
        Assert.Equal(keycloak.Configuration.ClientId, firstContext.PublicClientId);
        Assert.Equal(firstContext.ResourceName, first.Resource.Resource.Name);
        Assert.Equal(secondContext!.ResourceName, second.Resource.Resource.Name);

        AssertWaits(first.Resource.Resource, ["keycloak-proof-realm-ready"]);
        AssertWaits(second.Resource.Resource, ["keycloak-proof-realm-ready", first.Resource.Resource.Name]);
    }

    [Fact]
    public async Task WithLocalSeed_BindsASecretParameterOnlyToItsDeclaredConsumerProjectWithoutResolvingItsValue()
    {
        const string sentinel = "LOCAL_TEST_SECRET_SENTINEL";
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = AddKeycloak(builder, directory.Path);
        var secret = builder.AddParameter("seed-admin-secret", sentinel, secret: true);
        var seed = keycloak.WithLocalSeed(
            "identity-bootstrap",
            context => AddProject(builder, context.ResourceName),
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.WithRequiredSecretParameter("LOCAL_IDENTITY__BROKER__CREDENTIAL", secret);
            });
        var web = AddProject(builder, "web-proof");

        var seedManifest = await WriteEnvironmentManifestAsync(seed.Resource.Resource);
        var webManifest = await WriteEnvironmentManifestAsync(web.Resource);

        Assert.Contains("LOCAL_IDENTITY__BROKER__CREDENTIAL", seedManifest, StringComparison.Ordinal);
        Assert.Contains("seed-admin-secret", seedManifest, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, seedManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL_IDENTITY__BROKER__CREDENTIAL", webManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("seed-admin-secret", webManifest, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, webManifest, StringComparison.Ordinal);
    }

    [Fact]
    public void WithLocalSeed_WhenANameOrPredecessorIsInvalid_DoesNotInvokeTheRejectedFactoryOrAppendAStage()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = AddKeycloak(builder, directory.Path);
        var first = keycloak.WithLocalSeed(
            "identity-bootstrap",
            context => AddProject(builder, context.ResourceName),
            options => AllowCurrentEnvironment(builder, options));
        var rejectedFactoryCalls = 0;

        var missingPredecessor = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "candidate-fixture",
            _ =>
            {
                rejectedFactoryCalls++;
                return AddProject(builder, "should-not-exist");
            },
            options => AllowCurrentEnvironment(builder, options)));
        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, missingPredecessor.Code);
        Assert.Equal(0, rejectedFactoryCalls);

        var second = keycloak.WithLocalSeed(
            "candidate-fixture",
            context => AddProject(builder, context.ResourceName),
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.After(first);
            });
        Assert.Equal("candidate-fixture", second.Name);

        var nonImmediatePredecessor = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "third-stage",
            _ =>
            {
                rejectedFactoryCalls++;
                return AddProject(builder, "should-not-exist");
            },
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.After(first);
            }));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, nonImmediatePredecessor.Code);
        Assert.Equal(0, rejectedFactoryCalls);

        var third = keycloak.WithLocalSeed(
            "third-stage",
            context => AddProject(builder, context.ResourceName),
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.After(second);
            });
        Assert.Equal("third-stage", third.Name);
    }

    [Fact]
    public void WithLocalSeed_WhenTheFirstStageHasAPredecessorOrANameIsReused_RejectsItBeforeTheFactoryRuns()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = AddKeycloak(builder, directory.Path);
        var first = keycloak.WithLocalSeed(
            "identity-bootstrap",
            context => AddProject(builder, context.ResourceName),
            options => AllowCurrentEnvironment(builder, options));
        var rejectedFactoryCalls = 0;

        var duplicateName = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "identity-bootstrap",
            _ =>
            {
                rejectedFactoryCalls++;
                return AddProject(builder, "should-not-exist");
            },
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.After(first);
            }));

        var isolatedBuilder = DistributedApplication.CreateBuilder([]);
        var isolatedKeycloak = AddKeycloak(isolatedBuilder, directory.Path);
        var firstStageWithPredecessor = Assert.Throws<AppSurfaceKeycloakException>(() => isolatedKeycloak.WithLocalSeed(
            "identity-bootstrap",
            _ =>
            {
                rejectedFactoryCalls++;
                return AddProject(isolatedBuilder, "should-not-exist");
            },
            options =>
            {
                AllowCurrentEnvironment(isolatedBuilder, options);
                options.After(first);
            }));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, duplicateName.Code);
        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, firstStageWithPredecessor.Code);
        Assert.Equal(0, rejectedFactoryCalls);
    }

    [Fact]
    public void WithLocalSeed_WhenTheFactoryThrowsReturnsNullOrCreatesTheWrongResource_FailsWithoutAppendingTheRejectedStage()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = AddKeycloak(builder, directory.Path);

        Assert.Throws<InvalidOperationException>(() => keycloak.WithLocalSeed(
            "identity-bootstrap",
            _ => throw new InvalidOperationException("consumer factory failure"),
            options => AllowCurrentEnvironment(builder, options)));
        var nullResult = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "identity-bootstrap",
            _ => null!,
            options => AllowCurrentEnvironment(builder, options)));
        var wrongName = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "identity-bootstrap",
            _ => AddProject(builder, "wrong-resource-name"),
            options => AllowCurrentEnvironment(builder, options)));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, nullResult.Code);
        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, wrongName.Code);
        var accepted = keycloak.WithLocalSeed(
            "identity-bootstrap",
            context => AddProject(builder, context.ResourceName),
            options => AllowCurrentEnvironment(builder, options));
        Assert.Equal("identity-bootstrap", accepted.Name);
    }

    [Fact]
    public void WithLocalSeed_WhenTheRealmReadyWorkerCannotBeResolved_DoesNotMaterializeTheConsumerProject()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = builder.AddKeycloak("external-keycloak", GetAvailablePort());
        var wrapper = new AppSurfaceKeycloakResource(
            keycloak,
            new AppSurfaceKeycloakOptions().CreateConfigurationProjection(),
            new AppSurfaceKeycloakReadinessProbe(new AppSurfaceKeycloakOptions()),
            TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-keycloak-realm.json"));
        var factoryCalls = 0;

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => wrapper.WithLocalSeed(
            "identity-bootstrap",
            context =>
            {
                factoryCalls++;
                return AddProject(builder, context.ResourceName);
            },
            options => options.AllowedEnvironmentNames.Add(builder.Environment.EnvironmentName)));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable, exception.Code);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void WithLocalSeed_WhenASecretBindingIsNonsecretDuplicatedOrReused_FailsBeforeTheRejectedFactory()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = AddKeycloak(builder, directory.Path);
        var nonsecret = builder.AddParameter("nonsecret", "local", secret: false);
        var secret = builder.AddParameter("secret", "local-secret", secret: true);
        var rejectedFactoryCalls = 0;

        var nonsecretException = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "identity-bootstrap",
            _ =>
            {
                rejectedFactoryCalls++;
                return AddProject(builder, "should-not-exist");
            },
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.WithRequiredSecretParameter("LOCAL_SECRET", nonsecret);
            }));
        var duplicateNameException = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "identity-bootstrap",
            _ =>
            {
                rejectedFactoryCalls++;
                return AddProject(builder, "should-not-exist");
            },
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.WithRequiredSecretParameter("LOCAL_SECRET", secret);
                options.WithRequiredSecretParameter("LOCAL_SECRET", secret);
            }));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, nonsecretException.Code);
        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, duplicateNameException.Code);
        Assert.Equal(0, rejectedFactoryCalls);

        var first = keycloak.WithLocalSeed(
            "identity-bootstrap",
            context => AddProject(builder, context.ResourceName),
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.WithRequiredSecretParameter("LOCAL_SECRET", secret);
            });
        var reused = Assert.Throws<AppSurfaceKeycloakException>(() => keycloak.WithLocalSeed(
            "candidate-fixture",
            _ =>
            {
                rejectedFactoryCalls++;
                return AddProject(builder, "should-not-exist");
            },
            options =>
            {
                AllowCurrentEnvironment(builder, options);
                options.After(first);
                options.WithRequiredSecretParameter("LOCAL_SECOND_SECRET", secret);
            }));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedInvalid, reused.Code);
        Assert.Equal(0, rejectedFactoryCalls);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Publish)]
    [InlineData((DistributedApplicationOperation)99)]
    public void LocalSeedPolicy_WhenTheOperationIsNotRun_DeniesRegistrationBeforeAnyFactoryCanRun(DistributedApplicationOperation operation)
    {
        var exception = Assert.Throws<AppSurfaceKeycloakException>(() => AppSurfaceKeycloakLocalSeedPolicy.EnsureRunOperation(operation));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedNotAllowed, exception.Code);
    }

    [Fact]
    public void LocalSeedPolicy_WhenTheEnvironmentIsNotExplicitlyAllowed_DeniesRegistrationCaseInsensitively()
    {
        AppSurfaceKeycloakLocalSeedPolicy.EnsureAllowedEnvironment("testing", ["Testing"]);

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() =>
            AppSurfaceKeycloakLocalSeedPolicy.EnsureAllowedEnvironment("Staging", ["Development", "Test", "Testing"]));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.LocalSeedNotAllowed, exception.Code);
    }

    private static AppSurfaceKeycloakResource AddKeycloak(IDistributedApplicationBuilder builder, string importDirectory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var keycloakPort = GetAvailablePort();
            var webPort = GetAvailablePort();
            if (keycloakPort == webPort)
            {
                continue;
            }

            try
            {
                return builder.AddAppSurfaceKeycloak("keycloak-proof", options =>
                {
                    options.KeycloakPort = keycloakPort;
                    options.WebProofPort = webPort;
                    options.RealmImportDirectory = importDirectory;
                });
            }
            catch (AppSurfaceKeycloakException exception)
                when (exception.Code == AppSurfaceKeycloakDiagnosticCodes.PortOccupied && attempt < 4)
            {
                // Retry after an ephemeral-port preflight race.
            }
        }

        throw new InvalidOperationException("Could not reserve ports for the local seed test.");
    }

    private static IResourceBuilder<ProjectResource> AddProject(IDistributedApplicationBuilder builder, string resourceName) =>
        builder.AddProject(resourceName, GetCurrentTestProjectPath());

    private static void AllowCurrentEnvironment(IDistributedApplicationBuilder builder, AppSurfaceKeycloakLocalSeedOptions options)
    {
        options.AllowedEnvironmentNames.Clear();
        options.AllowedEnvironmentNames.Add(builder.Environment.EnvironmentName);
    }

    private static void AssertWaits(IResource resource, IReadOnlyCollection<string> expectedResourceNames)
    {
        Assert.Equal(
            expectedResourceNames.OrderBy(name => name, StringComparer.Ordinal),
            resource.Annotations
                .OfType<WaitAnnotation>()
                .Where(annotation => annotation.WaitType == WaitType.WaitForCompletion)
                .Select(annotation => annotation.Resource.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    private static string GetCurrentTestProjectPath() => TestPathUtils.PathUnder(
        TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
        "Auth",
        "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests",
        "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj");

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> WriteEnvironmentManifestAsync(IResource resource)
    {
        await using var stream = new MemoryStream();
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        var context = new ManifestPublishingContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            resource.Name,
            writer,
            CancellationToken.None);

        writer.WriteStartObject();
        await context.WriteEnvironmentVariablesAsync(resource);
        writer.WriteEndObject();
        await writer.FlushAsync();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
