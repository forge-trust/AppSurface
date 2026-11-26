using System.Diagnostics;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Runs Git for the Evidence identity fixtures with isolated configuration and bounded process cleanup.</summary>
internal static class EvidenceGitTestRunner
{
    /// <summary>Runs Git with a 20-second deadline and returns untrimmed standard output.</summary>
    /// <param name="workingDirectory">The fixture's repository directory.</param>
    /// <param name="arguments">Git arguments passed verbatim.</param>
    /// <exception cref="TimeoutException">Git or its output streams exceeded the command deadline.</exception>
    /// <exception cref="InvalidOperationException">Git could not start or returned a nonzero exit code.</exception>
    internal static async Task<string> RunAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var environment = startInfo.Environment;
        foreach (var name in environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            environment.Remove(name);
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = TestPathUtils.PathUnder(workingDirectory, "absent-global-config");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git fixture command could not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        var streams = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await streams.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException) { }
            try { await streams.WaitAsync(cleanup.Token); }
            catch (OperationCanceledException) { }
            throw new TimeoutException("Git fixture command exceeded its 20-second deadline.");
        }

        if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
        return await stdout;
    }
}
