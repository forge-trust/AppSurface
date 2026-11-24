using System.Diagnostics;

namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

// Catalog DDL shares a PostgreSQL advisory lock, and process-exit cleanup has a two-second best-effort budget.
[CollectionDefinition("PostgreSQL process exit", DisableParallelization = true)]
public sealed class PostgreSqlProcessExitCollection
{
    public const string Name = "PostgreSQL process exit";
}

[Collection(PostgreSqlProcessExitCollection.Name)]
public sealed class PostgreSqlProcessExitIntegrationTests
{
    [SkippableFact]
    public async Task ProcessExit_RemovesTemplateOwnedByNormallyExitingChildProcess()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var templatePrefix = PostgreSqlTestServer.NewPrefix("process_exit_template");
        var processName = "ForgeTrust.AppSurface.PostgreSql.Testing.Tests"
            + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var startInfo = new ProcessStartInfo(Path.Join(AppContext.BaseDirectory, processName))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--process-exit-fallback-child");
        startInfo.Environment[PostgreSqlTestServer.ConnectionEnvironmentVariable] = connectionString;
        startInfo.Environment[PostgreSqlFixtureTestProcess.TemplatePrefixEnvironmentVariable] = templatePrefix;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The PostgreSQL fixture child process did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The PostgreSQL fixture child process did not exit within 30 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, $"Child process exited {process.ExitCode}. stdout: {output} stderr: {error}");

        var templateDatabaseName = output.Trim();
        Assert.StartsWith(templatePrefix[..Math.Min(templatePrefix.Length, 46)], templateDatabaseName, StringComparison.Ordinal);
        Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, templateDatabaseName));
    }
}
