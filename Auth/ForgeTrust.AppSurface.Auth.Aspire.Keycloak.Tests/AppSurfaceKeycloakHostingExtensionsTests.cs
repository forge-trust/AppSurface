using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakHostingExtensionsTests
{
    [Fact]
    public void AddAppSurfaceKeycloak_WritesRealmImportAndReturnsSecretSafeWrapper()
    {
        using var directory = new TempDirectory();
        var keycloakPort = GetAvailablePort();
        var webProofPort = GetAvailablePort();
        var builder = DistributedApplication.CreateBuilder([]);

        var resource = builder.AddAppSurfaceKeycloak("keycloak-proof", options =>
        {
            options.KeycloakPort = keycloakPort;
            options.WebProofPort = webProofPort;
            options.RealmImportDirectory = directory.Path;
            options.UsePersistentDataVolume = true;
        });

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
        var keycloakPort = GetAvailablePort();
        var webProofPort = GetAvailablePort();
        var builder = DistributedApplication.CreateBuilder([]);

        var resource = builder.AddAppSurfaceKeycloak("keycloak-proof", options =>
        {
            options.KeycloakPort = keycloakPort;
            options.WebProofPort = webProofPort;
            options.RealmImportDirectory = directory.Path;
        });

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
    public void Projection_ApplyToAddsAllowlistedEnvironmentVariablesToProjectResource()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var project = builder.AddProject(
            "web",
            GetCurrentTestProjectPath());
        var projection = new AppSurfaceKeycloakOptions().CreateConfigurationProjection();

        var returned = projection.ApplyTo(project);

        Assert.Same(project, returned);
    }

    [Fact]
    public void Defaults_ExposeBoundedReadinessTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), AppSurfaceKeycloakDefaults.ReadinessTimeout);
    }

    [Fact]
    public void ResourceConstructor_StoresWrapperValues()
    {
        var projection = new AppSurfaceKeycloakOptions().CreateConfigurationProjection();
        var readiness = new AppSurfaceKeycloakReadinessProbe(new AppSurfaceKeycloakOptions());

        var resource = new AppSurfaceKeycloakResource(null!, projection, readiness, "/tmp/appsurface-dev-realm.json");

        Assert.Null(resource.Resource);
        Assert.Same(projection, resource.Configuration);
        Assert.Same(readiness, resource.Readiness);
        Assert.Equal("/tmp/appsurface-dev-realm.json", resource.RealmImportFile);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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
