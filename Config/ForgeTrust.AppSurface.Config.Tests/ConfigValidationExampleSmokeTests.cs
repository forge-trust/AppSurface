using System.Diagnostics;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config.Tests;

public sealed class ConfigValidationExampleSmokeTests
{
    [Fact]
    public async Task ConfigValidationExample_FailsWithScalarValidationMessage()
    {
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --project examples/config-validation -p:NodeReuse=false",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        process.StartInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process can exit naturally between cancellation and kill.
            }

            await process.WaitForExitAsync();
        }

        var outputTask = Task.WhenAll(stdout, stderr);
        var outputCompleted = await Task.WhenAny(outputTask, Task.Delay(TimeSpan.FromSeconds(5)));
        if (outputCompleted != outputTask)
        {
            throw new TimeoutException("The config validation example output streams did not close within 5 seconds.");
        }

        var output = string.Concat(await stdout, await stderr);
        if (timedOut)
        {
            throw new TimeoutException("The config validation example did not finish within 30 seconds.");
        }

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Configuration validation failed for key 'PortConfig'", output);
        Assert.Contains("- <value>: The configuration value must be between 1 and 65535.", output);
        Assert.Contains("Fix the configured value or relax the scalar rule", output);
        Assert.DoesNotContain("70000", output);
    }

}
