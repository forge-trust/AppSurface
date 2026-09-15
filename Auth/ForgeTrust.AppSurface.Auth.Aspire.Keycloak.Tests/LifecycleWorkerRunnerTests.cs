using AuthAspireKeycloakLifecycleWorker;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class LifecycleWorkerRunnerTests
{
    [Theory]
    [InlineData(AuthAspireKeycloakLifecycleWorkerEnvironment.Success, LifecycleWorkerRunner.SuccessExitCode)]
    [InlineData(AuthAspireKeycloakLifecycleWorkerEnvironment.Failure, LifecycleWorkerRunner.FailureExitCode)]
    [InlineData(AuthAspireKeycloakLifecycleWorkerEnvironment.Timeout, LifecycleWorkerRunner.FailureExitCode)]
    [InlineData("unknown", LifecycleWorkerRunner.InvalidModeExitCode)]
    public async Task RunAsync_ReturnsTheExpectedFiniteExitCode(string mode, int expectedExitCode)
    {
        var exitCode = await LifecycleWorkerRunner.RunAsync(mode, CancellationToken.None);

        Assert.Equal(expectedExitCode, exitCode);
    }

    [Fact]
    public async Task RunAsync_WhenHangModeIsCancelled_ReturnsTheCancellationExitCode()
    {
        using var cancellation = new CancellationTokenSource();
        var run = LifecycleWorkerRunner.RunAsync(AuthAspireKeycloakLifecycleWorkerEnvironment.Hang, cancellation.Token);

        cancellation.Cancel();

        var exitCode = await run;

        Assert.Equal(LifecycleWorkerRunner.CancellationExitCode, exitCode);
    }
}
