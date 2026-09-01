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
        var builder = DistributedApplication.CreateBuilder([]);
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

        Assert.Equal(keycloak.Configuration.Authority, environment[AppSurfaceKeycloakRealmReadyEnvironment.Authority]);
        Assert.Equal(keycloak.Configuration.ClientId, environment[AppSurfaceKeycloakRealmReadyEnvironment.ClientId]);
        Assert.Equal("admin;viewer", environment[AppSurfaceKeycloakRealmReadyEnvironment.SeededUserNames]);
        Assert.DoesNotContain(
            environment.Values.OfType<string>(),
            value => value.Contains("appsurface-admin-local-only", StringComparison.Ordinal)
                || value.Contains("appsurface-viewer-local-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealmReady_WhenAThemeIsConfigured_ProjectsOnlyItsNameToTheExecutableGate()
    {
        using var directory = new TempDirectory();
        var themeDirectory = Path.Join(directory.Path, "theme");
        Directory.CreateDirectory(Path.Join(themeDirectory, "login", "resources"));
        File.WriteAllText(Path.Join(themeDirectory, "login", "theme.properties"), "parent=keycloak\n");
        var builder = DistributedApplication.CreateBuilder([]);
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

        Assert.Equal("application", environment[AppSurfaceKeycloakRealmReadyEnvironment.LoginThemeName]);
    }

    [Fact]
    public void RealmReady_WhenTheWrapperWasNotCreatedByTheHostingExtension_FailsWithTheStableWorkerDiagnostic()
    {
        var builder = DistributedApplication.CreateBuilder([]);
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
        var outputDirectory = Path.Join(directory.Path, "consumer", "bin");
        var workerDirectory = Path.Join(outputDirectory, "appsurface-keycloak-realm-ready");
        Directory.CreateDirectory(workerDirectory);
        var assemblyPath = Path.Join(outputDirectory, $"{assemblyName}.dll");
        File.WriteAllText(assemblyPath, string.Empty);
        File.WriteAllText(Path.Join(workerDirectory, $"{workerAssemblyName}.dll"), string.Empty);
        File.WriteAllText(Path.Join(workerDirectory, $"{workerAssemblyName}.deps.json"), string.Empty);
        File.WriteAllText(Path.Join(workerDirectory, $"{workerAssemblyName}.runtimeconfig.json"), string.Empty);

        var worker = AppSurfaceKeycloakRealmReadyWorker.Resolve(assemblyPath);

        Assert.Equal(workerDirectory, worker.WorkingDirectory);
        Assert.Contains(Path.Join(workerDirectory, $"{workerAssemblyName}.dll"), worker.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RealmReadyRunner_WhenConfigurationIsSafe_UsesOnlyNonsecretInputsAndReturnsSuccess()
    {
        var environment = CreateEnvironment();
        using var output = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyRunner.RunAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            output,
            (configuration, _) =>
            {
                Assert.Equal("appsurface-dev", configuration.Options.Realm);
                Assert.Equal("appsurface-web", configuration.Options.ClientId);
                Assert.Equal(["admin", "viewer"], configuration.SeededUserNames);
                Assert.Equal("application", configuration.LoginThemeName);
                Assert.All(configuration.Options.SeededUsers, user => Assert.Equal("not-used", user.Password));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(AppSurfaceKeycloakRealmReadyRunner.SuccessExitCode, exitCode);
        Assert.Contains("completed", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealmReadyRunner_WhenTheProbeFails_RedactsTheExceptionAndReturnsFailure()
    {
        const string sentinel = "LOCAL_TEST_SECRET_SENTINEL";
        using var output = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyRunner.RunAsync(
            name =>
            {
                var environment = CreateEnvironment();
                return environment.TryGetValue(name, out var value) ? value : null;
            },
            output,
            (_, _) => throw new AppSurfaceKeycloakException(AppSurfaceKeycloakDiagnosticCodes.MetadataInvalid, sentinel),
            CancellationToken.None);

        Assert.Equal(AppSurfaceKeycloakRealmReadyRunner.FailureExitCode, exitCode);
        Assert.Contains(AppSurfaceKeycloakDiagnosticCodes.MetadataInvalid, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealmReadyRunner_WhenAnUnexpectedExceptionOccurs_RedactsItAndReturnsFailure()
    {
        const string sentinel = "LOCAL_TEST_SECRET_SENTINEL";
        using var output = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyRunner.RunAsync(
            name => CreateEnvironment().GetValueOrDefault(name),
            output,
            (_, _) => throw new InvalidOperationException(sentinel),
            CancellationToken.None);

        Assert.Equal(AppSurfaceKeycloakRealmReadyRunner.FailureExitCode, exitCode);
        Assert.Contains(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealmReadyRunner_WhenCancelled_ReturnsTheCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyRunner.RunAsync(
            name =>
            {
                var environment = CreateEnvironment();
                return environment.TryGetValue(name, out var value) ? value : null;
            },
            output,
            (_, token) => Task.FromCanceled(token),
            cancellation.Token);

        Assert.Equal(AppSurfaceKeycloakRealmReadyRunner.CancellationExitCode, exitCode);
        Assert.Contains("cancelled", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.Authority)]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.SeededUserNames)]
    public async Task RealmReadyRunner_WhenARequiredValueIsMissing_ReturnsTheStableInvalidOptionsDiagnostic(string missingName)
    {
        var environment = CreateEnvironment();
        environment.Remove(missingName);
        using var output = new StringWriter();

        var exitCode = await AppSurfaceKeycloakRealmReadyRunner.RunAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            output,
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(AppSurfaceKeycloakRealmReadyRunner.FailureExitCode, exitCode);
        Assert.Contains(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.RedirectUris, "not-json")]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.RedirectUris, "[]")]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.RedirectUris, "[\"https://keycloak.example.test:5059/signin-appsurface-oidc\"]")]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.RedirectUris, "[\"http://localhost:5059/not-the-callback\"]")]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.SeededUserNames, "admin;admin")]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.Authority, "http://localhost:8080/realms/appsurface-dev")]
    [InlineData(AppSurfaceKeycloakRealmReadyEnvironment.Authority, "https://localhost:8080/not-a-realm")]
    public async Task RealmReadyRunner_WhenAProjectedConfigurationValueIsInvalid_ReturnsTheStableDiagnostic(
        string name,
        string value)
    {
        var environment = CreateEnvironment();
        environment[name] = value;
        using var output = new StringWriter();
        var probeCalled = false;

        var exitCode = await AppSurfaceKeycloakRealmReadyRunner.RunAsync(
            environment.GetValueOrDefault,
            output,
            (_, _) =>
            {
                probeCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(AppSurfaceKeycloakRealmReadyRunner.FailureExitCode, exitCode);
        Assert.False(probeCalled);
        Assert.Contains(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealmReady_WhenCalledConcurrently_CachesOneWorkerResource()
    {
        using var directory = new TempDirectory();
        var builder = DistributedApplication.CreateBuilder([]);
        var (keycloak, _) = AddWithAvailablePorts(builder, directory.Path);

        var workers = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(keycloak.RealmReady)));

        Assert.All(workers, worker => Assert.Same(workers[0], worker));
    }

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

    private static Dictionary<string, string?> CreateEnvironment() =>
        new(StringComparer.Ordinal)
        {
            [AppSurfaceKeycloakRealmReadyEnvironment.Authority] = "https://localhost:8080/realms/appsurface-dev",
            [AppSurfaceKeycloakRealmReadyEnvironment.CallbackPath] = "/signin-appsurface-oidc",
            [AppSurfaceKeycloakRealmReadyEnvironment.ClientId] = "appsurface-web",
            [AppSurfaceKeycloakRealmReadyEnvironment.LoginThemeName] = "application",
            [AppSurfaceKeycloakRealmReadyEnvironment.PostLogoutRedirectUris] = "[\"http://localhost:5059/signout-callback-appsurface-oidc\"]",
            [AppSurfaceKeycloakRealmReadyEnvironment.RealmImportDirectory] = "/tmp/appsurface-keycloak-realms",
            [AppSurfaceKeycloakRealmReadyEnvironment.RedirectUris] = "[\"http://localhost:5059/signin-appsurface-oidc\"]",
            [AppSurfaceKeycloakRealmReadyEnvironment.SeededUserNames] = "admin;viewer",
            [AppSurfaceKeycloakRealmReadyEnvironment.SignedOutCallbackPath] = "/signout-callback-appsurface-oidc",
        };

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
