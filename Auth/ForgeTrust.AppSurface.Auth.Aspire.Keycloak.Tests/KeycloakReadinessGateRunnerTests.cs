using AuthAspireKeycloakReadinessGate;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class KeycloakReadinessGateRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenTheInjectedProbeSucceeds_ReconstructsOnlySafeProofInputsAndReturnsZero()
    {
        var environment = CreateEnvironment();
        using var standardError = new StringWriter();
        AppSurfaceKeycloakOptions? captured = null;

        var exitCode = await KeycloakReadinessGateRunner.RunAsync(
            name => environment.GetValueOrDefault(name),
            standardError,
            (options, _) =>
            {
                captured = options;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(KeycloakReadinessGateRunner.SuccessExitCode, exitCode);
        Assert.NotNull(captured);
        Assert.Equal("appsurface-dev", captured.Realm);
        Assert.Equal("appsurface-web", captured.ClientId);
        Assert.Equal("/signin-appsurface-oidc", captured.CallbackPath);
        Assert.Equal("/signout-callback-appsurface-oidc", captured.SignedOutCallbackPath);
        Assert.Equal(18181, captured.KeycloakPort);
        Assert.Equal(15000, captured.WebProofPort);
        Assert.Equal("/tmp", captured.RealmImportDirectory);
        Assert.Equal("AppSurface Keycloak readiness gate completed." + Environment.NewLine, standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenAProbeReportsASafeDiagnostic_RedactsTheExceptionMessage()
    {
        const string sentinel = "LOCAL_TEST_SECRET_SENTINEL";
        using var standardError = new StringWriter();

        var exitCode = await KeycloakReadinessGateRunner.RunAsync(
            name => CreateEnvironment().GetValueOrDefault(name),
            standardError,
            (_, _) => throw new AppSurfaceKeycloakException(AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable, sentinel),
            CancellationToken.None);

        Assert.Equal(KeycloakReadinessGateRunner.FailureExitCode, exitCode);
        Assert.Contains(AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable, standardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenAnUnexpectedExceptionContainsSensitiveText_RedactsTheExceptionMessage()
    {
        const string sentinel = "LOCAL_TEST_SECRET_SENTINEL";
        using var standardError = new StringWriter();

        var exitCode = await KeycloakReadinessGateRunner.RunAsync(
            name => CreateEnvironment().GetValueOrDefault(name),
            standardError,
            (_, _) => throw new InvalidOperationException(sentinel),
            CancellationToken.None);

        Assert.Equal(KeycloakReadinessGateRunner.FailureExitCode, exitCode);
        Assert.Equal(
            $"AppSurface Keycloak readiness gate failed. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}." + Environment.NewLine,
            standardError.ToString());
        Assert.DoesNotContain(sentinel, standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenConfigurationIsMissing_FailsBeforeTheProbeAndDoesNotPrintValues()
    {
        const string sentinel = "LOCAL_TEST_SECRET_SENTINEL";
        var environment = CreateEnvironment();
        environment.Remove(KeycloakReadinessGateEnvironment.ClientId);
        environment[KeycloakReadinessGateEnvironment.Authority] = sentinel;
        using var standardError = new StringWriter();
        var probeCalled = false;

        var exitCode = await KeycloakReadinessGateRunner.RunAsync(
            name => environment.GetValueOrDefault(name),
            standardError,
            (_, _) =>
            {
                probeCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(KeycloakReadinessGateRunner.FailureExitCode, exitCode);
        Assert.False(probeCalled);
        Assert.Contains(AppSurfaceKeycloakDiagnosticCodes.InvalidOptions, standardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, standardError.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://keycloak.example.test:18181/realms/appsurface-dev")]
    [InlineData("https://admin@localhost:18181/realms/appsurface-dev")]
    [InlineData("https://localhost:18181/realms/appsurface-dev?unexpected=value")]
    [InlineData("https://localhost:18181/realms/appsurface-dev#unexpected")]
    [InlineData("https://localhost:18181/realms%2fappsurface-dev")]
    [InlineData("https://localhost:18181/realms%5cappsurface-dev")]
    public async Task RunAsync_WhenAuthorityIsNotACanonicalLocalRealmUri_FailsBeforeTheProbe(string authority)
    {
        var environment = CreateEnvironment();
        environment[KeycloakReadinessGateEnvironment.Authority] = authority;
        using var standardError = new StringWriter();
        var probeCalled = false;

        var exitCode = await KeycloakReadinessGateRunner.RunAsync(
            name => environment.GetValueOrDefault(name),
            standardError,
            (_, _) =>
            {
                probeCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(KeycloakReadinessGateRunner.FailureExitCode, exitCode);
        Assert.False(probeCalled);
        Assert.Equal(
            $"AppSurface Keycloak readiness gate failed. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}." + Environment.NewLine,
            standardError.ToString());
    }

    [Theory]
    [InlineData(KeycloakReadinessGateEnvironment.RedirectUri, "https://keycloak.example.test:5059/signin-appsurface-oidc")]
    [InlineData(KeycloakReadinessGateEnvironment.RedirectUri, "http://localhost:5059/not-the-callback")]
    [InlineData(KeycloakReadinessGateEnvironment.PostLogoutRedirectUri, "http://localhost:5059/not-the-signout-callback")]
    public async Task RunAsync_WhenARedirectUriIsNotTheProjectedLocalCallback_FailsBeforeTheProbe(
        string environmentName,
        string redirectUri)
    {
        var environment = CreateEnvironment();
        environment[environmentName] = redirectUri;
        using var standardError = new StringWriter();
        var probeCalled = false;

        var exitCode = await KeycloakReadinessGateRunner.RunAsync(
            environment.GetValueOrDefault,
            standardError,
            (_, _) =>
            {
                probeCalled = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(KeycloakReadinessGateRunner.FailureExitCode, exitCode);
        Assert.False(probeCalled);
        Assert.Equal(
            $"AppSurface Keycloak readiness gate failed. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidOptions}." + Environment.NewLine,
            standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenCancellationWinsDuringAProbe_ReturnsTheDocumentedCancellationCode()
    {
        using var cancellation = new CancellationTokenSource();
        using var standardError = new StringWriter();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var gate = KeycloakReadinessGateRunner.RunAsync(
            name => CreateEnvironment().GetValueOrDefault(name),
            standardError,
            async (_, token) =>
            {
                probeStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            cancellation.Token);

        await probeStarted.Task;
        cancellation.Cancel();

        Assert.Equal(KeycloakReadinessGateRunner.CancellationExitCode, await gate);
        Assert.Equal("AppSurface Keycloak readiness gate was cancelled." + Environment.NewLine, standardError.ToString());
    }

    private static Dictionary<string, string> CreateEnvironment() => new(StringComparer.Ordinal)
    {
        [KeycloakReadinessGateEnvironment.Authority] = "https://localhost:18181/realms/appsurface-dev",
        [KeycloakReadinessGateEnvironment.ClientId] = "appsurface-web",
        [KeycloakReadinessGateEnvironment.CallbackPath] = "/signin-appsurface-oidc",
        [KeycloakReadinessGateEnvironment.SignedOutCallbackPath] = "/signout-callback-appsurface-oidc",
        [KeycloakReadinessGateEnvironment.RedirectUri] = "http://localhost:15000/signin-appsurface-oidc",
        [KeycloakReadinessGateEnvironment.PostLogoutRedirectUri] = "http://localhost:15000/signout-callback-appsurface-oidc",
        [KeycloakReadinessGateEnvironment.RealmImportDirectory] = "/tmp",
    };
}
