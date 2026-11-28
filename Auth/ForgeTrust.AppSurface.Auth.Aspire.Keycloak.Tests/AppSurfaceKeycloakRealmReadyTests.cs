using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakRealmReadyTests
{
    [Fact]
    public async Task RealmReady_CachesOneExecutableGateWithSafeEnvironmentAndHealthyDependency()
    {
        using var directory = new TempDirectory();
        var builder = CreateLocalBuilder();
        var (keycloak, _) = AddWithAvailablePorts(builder, directory.Path);

        var first = keycloak.RealmReady();
        var second = keycloak.RealmReady();

        Assert.Same(first, second);
        Assert.Equal("keycloak-proof-realm-ready", first.Resource.Resource.Name);
        var wait = Assert.Single(first.Resource.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Equal(keycloak.Resource.Resource.Name, wait.Resource.Name);
        Assert.Equal(WaitType.WaitUntilHealthy, wait.WaitType);
        Assert.True(first.Resource.Resource.TryGetEnvironmentVariables(out var annotations));

        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            environment,
            CancellationToken.None);
        foreach (var annotation in annotations)
        {
            await annotation.Callback(context);
        }

        Assert.Equal(keycloak.Configuration.Authority, environment["APPSURFACE_KEYCLOAK_REALM_READY_AUTHORITY"]);
        Assert.Equal(keycloak.Configuration.ClientId, environment["APPSURFACE_KEYCLOAK_REALM_READY_CLIENT_ID"]);
        Assert.Equal("admin;viewer", environment["APPSURFACE_KEYCLOAK_REALM_READY_SEEDED_USER_NAMES"]);
        Assert.DoesNotContain(
            environment.Values.OfType<string>(),
            value => value.Contains("appsurface-admin-local-only", StringComparison.Ordinal)
                || value.Contains("appsurface-viewer-local-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealmReady_WhenAThemeIsConfigured_ProjectsOnlyItsNameToTheExecutableGate()
    {
        using var directory = new TempDirectory();
        var themeDirectory = TestPathUtils.PathUnder(directory.Path, "theme");
        Directory.CreateDirectory(TestPathUtils.PathUnder(themeDirectory, "login", "resources"));
        File.WriteAllText(TestPathUtils.PathUnder(themeDirectory, "login", "theme.properties"), "parent=keycloak\n");
        var builder = CreateLocalBuilder();
        var (keycloak, _) = AddWithAvailablePorts(
            builder,
            directory.Path,
            options => options.LoginTheme = AppSurfaceKeycloakThemeOptions.Login(
                "application",
                themeDirectory,
                AppSurfaceKeycloakImageReference.Parse($"quay.io/keycloak/keycloak:26.6@sha256:{new string('a', 64)}")));
        var realmReady = keycloak.RealmReady();
        Assert.True(realmReady.Resource.Resource.TryGetEnvironmentVariables(out var annotations));
        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            environment,
            CancellationToken.None);

        foreach (var annotation in annotations)
        {
            await annotation.Callback(context);
        }

        Assert.Equal("application", environment["APPSURFACE_KEYCLOAK_REALM_READY_LOGIN_THEME_NAME"]);
    }

    [Fact]
    public void RealmReady_WhenTheWrapperWasNotCreatedByTheHostingExtension_FailsWithTheStableWorkerDiagnostic()
    {
        var builder = CreateLocalBuilder();
        var resource = builder.AddKeycloak("external-keycloak", GetAvailablePort());
        var wrapper = new AppSurfaceKeycloakResource(
            resource,
            new AppSurfaceKeycloakOptions().CreateConfigurationProjection(),
            new AppSurfaceKeycloakReadinessProbe(new AppSurfaceKeycloakOptions()),
            "/tmp/appsurface-dev-realm.json");

        var exception = Assert.Throws<AppSurfaceKeycloakException>(wrapper.RealmReady);

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable, exception.Code);
        Assert.DoesNotContain("/tmp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerResolver_UsesTheProjectReferencePayloadWhenThePackageToolPayloadIsNotInstalled()
    {
        var worker = AppSurfaceKeycloakRealmReadyWorker.Resolve();

        Assert.Equal("exec", worker.Arguments[0]);
        Assert.Contains("--runtimeconfig", worker.Arguments, StringComparer.Ordinal);
        Assert.Contains("--depsfile", worker.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            worker.Arguments,
            argument => argument.EndsWith(
                $"{AppSurfaceKeycloakRealmReadyWorker.WorkerAssemblyName}.dll",
                StringComparison.Ordinal));
        Assert.Equal("--appsurface-keycloak-realm-ready", worker.Arguments[^1]);
    }

    [Fact]
    public void WorkerResolver_UsesThePayloadCopiedByTheNuGetBuildTargetToConsumerOutput()
    {
        using var directory = new TempDirectory();
        var assemblyName = typeof(AppSurfaceKeycloakRealmReadyWorker).Assembly.GetName().Name!;
        var workerAssemblyName = AppSurfaceKeycloakRealmReadyWorker.WorkerAssemblyName;
        var outputDirectory = TestPathUtils.PathUnder(directory.Path, "consumer", "bin");
        var workerDirectory = TestPathUtils.PathUnder(outputDirectory, "appsurface-keycloak-realm-ready");
        Directory.CreateDirectory(workerDirectory);
        var assemblyPath = TestPathUtils.PathUnder(outputDirectory, $"{assemblyName}.dll");
        File.WriteAllText(assemblyPath, string.Empty);
        File.WriteAllText(TestPathUtils.PathUnder(workerDirectory, $"{workerAssemblyName}.dll"), string.Empty);
        File.WriteAllText(TestPathUtils.PathUnder(workerDirectory, $"{workerAssemblyName}.deps.json"), string.Empty);
        File.WriteAllText(TestPathUtils.PathUnder(workerDirectory, $"{workerAssemblyName}.runtimeconfig.json"), string.Empty);

        var worker = AppSurfaceKeycloakRealmReadyWorker.Resolve(assemblyPath);

        Assert.Equal(workerDirectory, worker.WorkingDirectory);
        Assert.Contains(TestPathUtils.PathUnder(workerDirectory, $"{workerAssemblyName}.dll"), worker.Arguments, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dll")]
    [InlineData("deps.json")]
    [InlineData("runtimeconfig.json")]
    public void WorkerResolver_BundledAppRequiresTheExternalWorkerFiles(string? missingExtension)
    {
        using var directory = new TempDirectory();
        var workerDirectory = TestPathUtils.PathUnder(directory.Path, "appsurface-keycloak-realm-ready");
        Directory.CreateDirectory(workerDirectory);
        var assemblyName = AppSurfaceKeycloakRealmReadyWorker.WorkerAssemblyName;
        foreach (var extension in new[] { "dll", "deps.json", "runtimeconfig.json" }.Where(extension => extension != missingExtension))
        {
            File.WriteAllText(TestPathUtils.PathUnder(workerDirectory, $"{assemblyName}.{extension}"), string.Empty);
        }

        if (missingExtension is not null)
        {
            var error = Assert.Throws<AppSurfaceKeycloakException>(
                () => AppSurfaceKeycloakRealmReadyWorker.ResolveBundled(directory.Path));
            Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmReadyWorkerUnavailable, error.Code);
            return;
        }

        var worker = AppSurfaceKeycloakRealmReadyWorker.ResolveBundled(directory.Path);
        Assert.Equal(workerDirectory, worker.WorkingDirectory);
        Assert.Contains(TestPathUtils.PathUnder(workerDirectory, $"{assemblyName}.dll"), worker.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RealmReady_WhenCalledConcurrently_CachesOneWorkerResource()
    {
        using var directory = new TempDirectory();
        var builder = CreateLocalBuilder();
        var (keycloak, _) = AddWithAvailablePorts(builder, directory.Path);

        var workers = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(keycloak.RealmReady)));

        Assert.All(workers, worker => Assert.Same(workers[0], worker));
    }

    private static IDistributedApplicationBuilder CreateLocalBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--environment", "Development"],
            DisableDashboard = true,
        });

    private static (AppSurfaceKeycloakResource Resource, int KeycloakPort) AddWithAvailablePorts(
        IDistributedApplicationBuilder builder,
        string realmImportDirectory,
        Action<AppSurfaceKeycloakOptions>? configureOptions = null)
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
                return (
                    builder.AddAppSurfaceKeycloak("keycloak-proof", options =>
                    {
                        options.KeycloakPort = keycloakPort;
                        options.WebProofPort = webPort;
                        options.RealmImportDirectory = realmImportDirectory;
                        configureOptions?.Invoke(options);
                    }),
                    keycloakPort);
            }
            catch (AppSurfaceKeycloakException exception)
                when (exception.Code == AppSurfaceKeycloakDiagnosticCodes.PortOccupied && attempt < 4)
            {
                // Retry after the ephemeral-port preflight race.
            }
        }

        throw new InvalidOperationException("Could not reserve ports for the realm-ready test.");
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
