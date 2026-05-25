using CliWrap;
using CliWrap.Buffered;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigValidationExampleSmokeTests
{
    [Fact]
    public async Task ConsoleAppExample_ConfigDiagnosticsCommand_RendersActiveEnvironmentReport()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap("dotnet")
                .WithArguments(["run", "--project", "examples/console-app", "-p:NodeReuse=false", "--", "config", "diagnostics"])
                .WithWorkingDirectory(repositoryRoot)
                .WithEnvironmentVariables(new Dictionary<string, string?>
                {
                    ["MSBUILDDISABLENODEREUSE"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException("The console app diagnostics command did not finish within 45 seconds.");
        }

        var output = result.StandardOutput + result.StandardError;
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Environment: Production", output, StringComparison.Ordinal);
        Assert.Contains("Entries:", output, StringComparison.Ordinal);
        Assert.Contains("FooConfig", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--environment", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigValidationExample_FailsWithScalarValidationMessage()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap("dotnet")
                .WithArguments(["run", "--project", "examples/config-validation", "-p:NodeReuse=false"])
                .WithWorkingDirectory(repositoryRoot)
                .WithEnvironmentVariables(new Dictionary<string, string?>
                {
                    ["MSBUILDDISABLENODEREUSE"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException("The config validation example did not finish within 30 seconds.");
        }

        var output = result.StandardOutput + result.StandardError;
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Configuration validation failed for key 'PortConfig'", output);
        Assert.Contains("- <value>: The configuration value must be between 1 and 65535.", output);
        Assert.Contains("Fix the configured value or relax the scalar rule", output);
        Assert.DoesNotContain("70000", output);
    }

}
