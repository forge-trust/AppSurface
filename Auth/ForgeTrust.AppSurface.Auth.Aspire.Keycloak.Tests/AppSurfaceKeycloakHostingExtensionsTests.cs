using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakHostingExtensionsTests
{
    [Fact]
    public void AddAppSurfaceKeycloak_WritesRealmImportAndReturnsSecretSafeWrapper()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);

        var (resource, keycloakPort) = AddWithAvailablePorts(builder, directory.Path, usePersistentDataVolume: true);

        Assert.Equal("appsurface-web", resource.Configuration.ClientId);
        Assert.Equal($"http://localhost:{keycloakPort}/realms/appsurface-dev", resource.Configuration.Authority);
        Assert.Equal(Path.Join(directory.Path, "appsurface-dev-realm.json"), resource.RealmImportFile);
        Assert.True(File.Exists(resource.RealmImportFile));
        Assert.NotNull(resource.Resource);
        Assert.NotNull(resource.Readiness);
    }

    [Fact]
    public void AddAppSurfaceKeycloak_WhenPersistentVolumeDisabled_StillWritesRealmImport()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);

        var (resource, _) = AddWithAvailablePorts(builder, directory.Path, usePersistentDataVolume: false);

        Assert.True(File.Exists(resource.RealmImportFile));
    }

    [Fact]
    public void Projection_WhenClientSecretRequired_UsesBooleanStringAndRejectsNullProject()
    {
        var projection = new AppSurfaceKeycloakConfigurationProjection(
            "http://localhost:8080/realms/appsurface-dev",
            "appsurface-web",
            "/signin-appsurface-oidc",
            "/signout-callback-appsurface-oidc",
            requireClientSecret: true);

        Assert.Equal("true", projection.EnvironmentVariables["Authentication__Oidc__RequireClientSecret"]);
        Assert.Throws<ArgumentNullException>(() => projection.ApplyTo(null!));
    }

    [Fact]
    public async Task Projection_ApplyToAddsAllowlistedEnvironmentVariablesToProjectResource()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var project = builder.AddProject(
            "web",
            GetCurrentTestProjectPath());
        var projection = new AppSurfaceKeycloakOptions().CreateConfigurationProjection();

        var returned = projection.ApplyTo(project);
        Assert.True(project.Resource.TryGetEnvironmentVariables(out var annotations));
        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            environment,
            CancellationToken.None);
        foreach (var annotation in annotations)
        {
            await annotation.Callback(context);
        }

        Assert.Same(project, returned);
        foreach (var pair in projection.EnvironmentVariables)
        {
            Assert.Equal(pair.Value, Assert.IsType<string>(environment[pair.Key]));
        }
    }

    [Fact]
    public void Defaults_ExposeBoundedReadinessTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), AppSurfaceKeycloakDefaults.ReadinessTimeout);
    }

    [Fact]
    public void ResourceConstructor_StoresWrapperValues()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = builder.AddKeycloak("keycloak-wrapper", GetAvailablePort());
        var projection = new AppSurfaceKeycloakOptions().CreateConfigurationProjection();
        var readiness = new AppSurfaceKeycloakReadinessProbe(new AppSurfaceKeycloakOptions());

        var resource = new AppSurfaceKeycloakResource(keycloak, projection, readiness, "/tmp/appsurface-dev-realm.json");

        Assert.Same(keycloak, resource.Resource);
        Assert.Same(projection, resource.Configuration);
        Assert.Same(readiness, resource.Readiness);
        Assert.Equal("/tmp/appsurface-dev-realm.json", resource.RealmImportFile);
    }

    [Fact]
    public void ResourceConstructor_WhenArgumentsInvalid_Throws()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloak = builder.AddKeycloak("keycloak-wrapper", GetAvailablePort());
        var projection = new AppSurfaceKeycloakOptions().CreateConfigurationProjection();
        var readiness = new AppSurfaceKeycloakReadinessProbe(new AppSurfaceKeycloakOptions());

        Assert.Throws<ArgumentNullException>(() => new AppSurfaceKeycloakResource(null!, projection, readiness, "/tmp/appsurface-dev-realm.json"));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceKeycloakResource(keycloak, null!, readiness, "/tmp/appsurface-dev-realm.json"));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceKeycloakResource(keycloak, projection, null!, "/tmp/appsurface-dev-realm.json"));
        Assert.Throws<ArgumentException>(() => new AppSurfaceKeycloakResource(keycloak, projection, readiness, " "));
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static (AppSurfaceKeycloakResource Resource, int KeycloakPort) AddWithAvailablePorts(
        IDistributedApplicationBuilder builder,
        string realmImportDirectory,
        bool usePersistentDataVolume)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var keycloakPort = GetAvailablePort();
            var webProofPort = GetAvailablePort();
            if (keycloakPort == webProofPort)
            {
                continue;
            }

            try
            {
                var resource = builder.AddAppSurfaceKeycloak("keycloak-proof", options =>
                {
                    options.KeycloakPort = keycloakPort;
                    options.WebProofPort = webProofPort;
                    options.RealmImportDirectory = realmImportDirectory;
                    options.UsePersistentDataVolume = usePersistentDataVolume;
                });
                return (resource, keycloakPort);
            }
            catch (AppSurfaceKeycloakException exception)
                when (exception.Code == AppSurfaceKeycloakDiagnosticCodes.PortOccupied && attempt < maxAttempts)
            {
                // Retry with fresh ports if another process wins the preflight race.
            }
        }

        throw new InvalidOperationException("Could not reserve distinct local ports for the Keycloak hosting test.");
    }

    private static string GetCurrentTestProjectPath()
    {
        const string projectPath = "../../../ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj";
        if (Path.IsPathRooted(projectPath))
        {
            throw new InvalidOperationException("The test project path must stay relative.");
        }

        return Path.GetFullPath(projectPath, AppContext.BaseDirectory);
    }
}
