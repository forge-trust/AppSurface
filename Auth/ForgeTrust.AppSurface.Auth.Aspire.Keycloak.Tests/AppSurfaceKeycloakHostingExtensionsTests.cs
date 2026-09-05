using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
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
        Assert.Equal($"https://localhost:{keycloakPort}/realms/appsurface-dev", resource.Configuration.Authority);
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
    public void AddAppSurfaceKeycloak_WhenExplicitAdministratorParametersAreProvided_UsesOnlyATypedSecretPassword()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var username = builder.AddParameter("keycloak-admin-username", "admin", secret: false);
        var password = builder.AddParameter("keycloak-admin-password", "LOCAL_TEST_SECRET_SENTINEL", secret: true);

        var resource = AddWithAvailablePorts(builder, directory.Path, username, password);

        Assert.Same(username.Resource, resource.Resource.Resource.AdminUserNameParameter);
        Assert.Same(password.Resource, resource.Resource.Resource.AdminPasswordParameter);
    }

    [Fact]
    public void AddAppSurfaceKeycloak_WhenExplicitAdministratorPasswordIsNotSecret_ThrowsInvalidOptions()
    {
        using var directory = new TempDirectory();
        var nonSecretBuilder = DistributedApplication.CreateBuilder([]);
        var nonSecretUsername = nonSecretBuilder.AddParameter("keycloak-admin-username", "admin", secret: false);
        var nonSecretPassword = nonSecretBuilder.AddParameter("keycloak-admin-password", "not-secret", secret: false);
        var exception = Assert.Throws<AppSurfaceKeycloakException>(() =>
            AddWithAvailablePorts(nonSecretBuilder, directory.Path, nonSecretUsername, nonSecretPassword));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, exception.Code);
        Assert.Contains("not secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAppSurfaceKeycloak_WhenLoginThemeConfigured_MountsAndDescribesTheValidatedTheme()
    {
        using var directory = new TempDirectory();
        var source = Path.Join(directory.Path, "application");
        Directory.CreateDirectory(Path.Join(source, "login", "resources"));
        File.WriteAllText(Path.Join(source, "login", "theme.properties"), "parent=keycloak\n");
        File.WriteAllText(Path.Join(source, "login", "resources", "site.css"), "body { color: black; }");
        File.WriteAllText(Path.Join(source, "login", "resources", "dev-only.css"), "body { outline-color: black; }");
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloakPort = GetAvailablePort();
        var webProofPort = GetAvailablePort();
        var image = AppSurfaceKeycloakImageReference.Parse($"quay.io/keycloak/keycloak:26.6@sha256:{new string('a', 64)}");

        var resource = builder.AddAppSurfaceKeycloak("keycloak-theme", options =>
        {
            options.KeycloakPort = keycloakPort;
            options.WebProofPort = webProofPort;
            options.RealmImportDirectory = Path.Join(directory.Path, "realms");
            options.RedirectUris.Clear();
            options.RedirectUris.Add(new Uri($"http://localhost:{webProofPort}/signin-appsurface-oidc", UriKind.Absolute));
            options.PostLogoutRedirectUris.Clear();
            options.PostLogoutRedirectUris.Add(new Uri($"http://localhost:{webProofPort}/signout-callback-appsurface-oidc", UriKind.Absolute));
            var loginTheme = AppSurfaceKeycloakThemeOptions.Login("application", source, image);
            loginTheme.RequiredThemeProperties.Add("parent");
            loginTheme.RequiredResourcePaths.Add("login/resources/site.css");
            loginTheme.DevelopmentOnlyResourcePaths.Add("login/resources/dev-only.css");
            options.LoginTheme = loginTheme;
        });

        Assert.NotNull(resource.Theme);
        Assert.Equal("application", resource.Theme.Name);
        Assert.Equal(image.Value, resource.Theme.BaseImage);
        var imageAnnotation = Assert.Single(resource.Resource.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(image.Registry, imageAnnotation.Registry);
        Assert.Equal(image.Image, imageAnnotation.Image);
        Assert.Null(imageAnnotation.Tag);
        Assert.Equal(image.Sha256, imageAnnotation.SHA256);
        Assert.True(resource.Resource.Resource.TryGetContainerMounts(out var mounts));
        var mount = Assert.Single(mounts, annotation => annotation.Target == "/opt/keycloak/themes/application");
        Assert.Equal(Path.GetFullPath(source), mount.Source);
        Assert.True(mount.IsReadOnly);
        Assert.True(resource.Resource.Resource.TryGetEnvironmentVariables(out var environmentAnnotations));
        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            environment,
            CancellationToken.None);
        foreach (var annotation in environmentAnnotations)
        {
            await annotation.Callback(context);
        }

        Assert.Equal("false", Assert.IsType<string>(environment["KC_SPI_THEME_CACHE_THEMES"]));
        Assert.Equal("false", Assert.IsType<string>(environment["KC_SPI_THEME_CACHE_TEMPLATES"]));

        var realmJson = File.ReadAllText(resource.RealmImportFile);
        Assert.Contains("\"loginTheme\": \"application\"", realmJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppSurfaceKeycloak_CapturesAValidatedSnapshotBeforeTheConfigureCallbackCanBeMutated()
    {
        using var directory = new TempDirectory();
        var source = Path.Join(directory.Path, "application");
        Directory.CreateDirectory(Path.Join(source, "login", "resources"));
        File.WriteAllText(Path.Join(source, "login", "theme.properties"), "parent=keycloak\n");
        File.WriteAllText(Path.Join(source, "login", "resources", "site.css"), "body { color: black; }");
        var builder = DistributedApplication.CreateBuilder([]);
        var keycloakPort = GetAvailablePort();
        var webProofPort = GetAvailablePort();
        var image = AppSurfaceKeycloakImageReference.Parse($"quay.io/keycloak/keycloak:26.6@sha256:{new string('b', 64)}");
        AppSurfaceKeycloakOptions? configured = null;

        var resource = builder.AddAppSurfaceKeycloak("keycloak-snapshot", options =>
        {
            configured = options;
            options.KeycloakPort = keycloakPort;
            options.WebProofPort = webProofPort;
            options.RealmImportDirectory = Path.Join(directory.Path, "realms");
            options.LoginTheme = AppSurfaceKeycloakThemeOptions.Login("application", source, image);
        });

        configured!.Realm = "mutated-realm";
        configured.LoginTheme!.Name = "mutated-theme";
        configured.SeededUsers[0].Claims["appsurface_role"] = "mutated";

        Assert.Equal($"https://localhost:{keycloakPort}/realms/appsurface-dev", resource.Configuration.Authority);
        Assert.Equal("application", resource.Theme!.Name);
        var realmJson = File.ReadAllText(resource.RealmImportFile);
        Assert.Contains("\"realm\": \"appsurface-dev\"", realmJson, StringComparison.Ordinal);
        Assert.Contains("\"loginTheme\": \"application\"", realmJson, StringComparison.Ordinal);
        Assert.DoesNotContain("mutated", realmJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Projection_WhenClientSecretRequired_UsesBooleanStringAndRejectsNullProject()
    {
        var projection = new AppSurfaceKeycloakConfigurationProjection(
            "https://localhost:8080/realms/appsurface-dev",
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
    public void AspireCompletionSpike_UsesTheDocumentedHealthyThenSuccessfulCompletionGraph()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var (keycloak, _) = AddWithAvailablePorts(builder, directory.Path, usePersistentDataVolume: false);
        var gate = builder
            .AddProject("readiness-gate", GetCurrentTestProjectPath())
            .WaitFor(keycloak.Resource);
        var firstFiniteProject = builder
            .AddProject("first-finite-project", GetCurrentTestProjectPath())
            .WaitForCompletion(gate);
        var secondFiniteProject = builder
            .AddProject("second-finite-project", GetCurrentTestProjectPath())
            .WaitForCompletion(firstFiniteProject);

        AssertWait(gate.Resource, keycloak.Resource.Resource.Name, WaitType.WaitUntilHealthy);
        AssertWait(firstFiniteProject.Resource, gate.Resource.Name, WaitType.WaitForCompletion);
        AssertWait(secondFiniteProject.Resource, firstFiniteProject.Resource.Name, WaitType.WaitForCompletion);
    }

    [Fact]
    public async Task AspireSecretBindingSpike_EmitsOnlyAParameterReferenceForItsDeclaredProject()
    {
        const string sentinel = "LOCAL_TEST_SECRET_SENTINEL";
        var builder = DistributedApplication.CreateBuilder([]);
        var secret = builder.AddParameter("seed-admin-secret", sentinel, secret: true);
        var seed = builder
            .AddProject("seed-worker", GetCurrentTestProjectPath())
            .WithEnvironment("SEED_ADMIN_SECRET", secret);
        var web = builder.AddProject("web", GetCurrentTestProjectPath());

        var seedManifest = await WriteEnvironmentManifestAsync(seed.Resource);
        var webManifest = await WriteEnvironmentManifestAsync(web.Resource);

        Assert.Contains("SEED_ADMIN_SECRET", seedManifest, StringComparison.Ordinal);
        Assert.Contains("seed-admin-secret", seedManifest, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, seedManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("SEED_ADMIN_SECRET", webManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("seed-admin-secret", webManifest, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, webManifest, StringComparison.Ordinal);
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
        return WithAvailablePorts(
            (keycloakPort, webProofPort) =>
            {
                var resource = builder.AddAppSurfaceKeycloak("keycloak-proof", options =>
                {
                    options.KeycloakPort = keycloakPort;
                    options.WebProofPort = webProofPort;
                    options.RealmImportDirectory = realmImportDirectory;
                    options.UsePersistentDataVolume = usePersistentDataVolume;
                });
                return (resource, keycloakPort);
            },
            "Could not reserve distinct local ports for the Keycloak hosting test.");
    }

    private static AppSurfaceKeycloakResource AddWithAvailablePorts(
        IDistributedApplicationBuilder builder,
        string realmImportDirectory,
        IResourceBuilder<ParameterResource> username,
        IResourceBuilder<ParameterResource> password)
    {
        return WithAvailablePorts(
            (keycloakPort, webProofPort) => builder.AddAppSurfaceKeycloak(
                "keycloak-explicit-admin",
                username,
                password,
                options =>
                {
                    options.KeycloakPort = keycloakPort;
                    options.WebProofPort = webProofPort;
                    options.RealmImportDirectory = realmImportDirectory;
                }),
            "Could not reserve distinct local ports for the explicit-admin hosting test.");
    }

    private static T WithAvailablePorts<T>(Func<int, int, T> create, string failureMessage)
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
                return create(keycloakPort, webProofPort);
            }
            catch (AppSurfaceKeycloakException exception)
                when (exception.Code == AppSurfaceKeycloakDiagnosticCodes.PortOccupied && attempt < maxAttempts)
            {
                // Retry with fresh ports if another process wins the preflight race.
            }
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static string GetCurrentTestProjectPath() => TestPathUtils.PathUnder(
        TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
        "Auth",
        "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests",
        "ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.csproj");

    private static void AssertWait(IResource resource, string dependencyName, WaitType expectedWaitType)
    {
        var wait = Assert.Single(resource.Annotations.OfType<WaitAnnotation>());

        Assert.Equal(dependencyName, wait.Resource.Name);
        Assert.Equal(expectedWaitType, wait.WaitType);
        Assert.Equal(0, wait.ExitCode);
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
