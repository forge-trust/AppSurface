using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ForgeTrust.AppSurface.Docs.Tests;

/// <summary>Locks the copyable Durable PostgreSQL adoption and recovery guidance to its canonical sources.</summary>
public sealed class DurableSlice7AdoptionDocumentationContractTests
{
    [Fact]
    public void LocalTutorial_ShouldProvideASecretSafePrerequisiteCheckAndCanonicalRoleRecipe()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var tutorial = Read(repoRoot, "examples/durable-postgresql/README.md");
        var prerequisiteScript = Read(repoRoot, "examples/durable-postgresql/check-prerequisites.sh");

        Assert.Contains("### Copy-paste prerequisite check", tutorial, StringComparison.Ordinal);
        Assert.Contains("bash examples/durable-postgresql/check-prerequisites.sh", tutorial, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_MIGRATION_CONNECTION", tutorial, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_DISPATCHER_CONNECTION", tutorial, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_RUNTIME_CONNECTION", tutorial, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_RUNTIME_EPOCH", tutorial, StringComparison.Ordinal);
        Assert.Contains("../../Durable/configure-postgresql-roles.sql", tutorial, StringComparison.Ordinal);
        Assert.DoesNotContain("POSTGRES_PASSWORD=", tutorial, StringComparison.Ordinal);
        Assert.Contains("require_command dotnet", prerequisiteScript, StringComparison.Ordinal);
        Assert.Contains("require_command docker", prerequisiteScript, StringComparison.Ordinal);
        Assert.Contains("/dev/tcp/127.0.0.1", prerequisiteScript, StringComparison.Ordinal);
        Assert.Contains("local TCP port %s", prerequisiteScript, StringComparison.Ordinal);
        Assert.DoesNotContain("psql", prerequisiteScript, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_isready", prerequisiteScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrerequisiteScript_ShouldExecuteWithOnlyItsDocumentedToolContract()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var script = TestPathUtils.PathUnder(repoRoot, "examples/durable-postgresql/check-prerequisites.sh");
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var occupiedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await RunPrerequisiteScriptAsync(
            script,
            occupiedPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "#!/bin/bash\nprintf '10.0.100\\n'\n",
            "#!/bin/bash\n[[ \"${1:-}\" == 'info' ]]\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[ok] .NET SDK 10.0.100", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[ok] Docker daemon", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_RUNTIME_EPOCH", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("local TCP port", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public async Task PrerequisiteScript_ShouldRejectInvalidPortBeforeProbingIt(string port)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var script = TestPathUtils.PathUnder(repoRoot, "examples/durable-postgresql/check-prerequisites.sh");
        var result = await RunPrerequisiteScriptAsync(
            script,
            port,
            "#!/bin/bash\nprintf '10.0.100\\n'\n",
            "#!/bin/bash\n[[ \"${1:-}\" == 'info' ]]\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"local TCP port {port} must be an integer from 1 through 65535", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrerequisiteScript_ShouldReportMissingAndUnavailableTools()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var script = TestPathUtils.PathUnder(repoRoot, "examples/durable-postgresql/check-prerequisites.sh");
        var result = await RunPrerequisiteScriptAsync(script, "54329", dotnetScript: null, dockerScript: null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[missing] dotnet", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[missing] docker", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[missing] Docker daemon", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrerequisiteScript_ShouldReportUnreadableSdkVersionAndUnavailableDocker()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var script = TestPathUtils.PathUnder(repoRoot, "examples/durable-postgresql/check-prerequisites.sh");
        var result = await RunPrerequisiteScriptAsync(
            script,
            "0",
            "#!/bin/bash\nexit 1\n",
            "#!/bin/bash\nexit 1\n");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[missing] .NET SDK version could not be read", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[missing] Docker daemon", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void AdoptionDocumentation_ShouldKeepForwardOnlyRecoveryAndWorkerBoundariesConsistent()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var documentation = string.Join(
            Environment.NewLine,
            Read(repoRoot, "examples/durable-postgresql/README.md"),
            Read(repoRoot, "Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md"),
            Read(repoRoot, "Cli/ForgeTrust.AppSurface.Cli/README.md"),
            Read(repoRoot, "releases/unreleased.md"));

        Assert.Contains("0006_flow_trace_context.sql", documentation, StringComparison.Ordinal);
        Assert.Contains("Never delete `appsurface_durable.schema_migration` rows", documentation, StringComparison.Ordinal);
        Assert.Contains("regenerate the correct forward script", documentation, StringComparison.Ordinal);
        Assert.Contains("rerun the canonical role recipe after migrations", documentation, StringComparison.Ordinal);
        Assert.Contains("AddWorkerHost()", documentation, StringComparison.Ordinal);
        Assert.Contains("application startup never applies DDL", documentation, StringComparison.Ordinal);
    }

    private static string Read(string repoRoot, string path) =>
        File.ReadAllText(TestPathUtils.PathUnder(repoRoot, path));

    private static async Task<PrerequisiteScriptResult> RunPrerequisiteScriptAsync(
        string script,
        string port,
        string? dotnetScript,
        string? dockerScript)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"appsurface-durable-prerequisites-{Guid.NewGuid():N}");
        var commandDirectory = Path.Combine(directory, "bin");
        Directory.CreateDirectory(commandDirectory);
        try
        {
            if (dotnetScript is not null)
            {
                WriteExecutable(Path.Combine(commandDirectory, "dotnet"), dotnetScript);
            }

            if (dockerScript is not null)
            {
                WriteExecutable(Path.Combine(commandDirectory, "docker"), dockerScript);
            }

            using var process = Process.Start(new ProcessStartInfo("/bin/bash", script)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                Environment =
                {
                    ["PATH"] = commandDirectory,
                    ["APPSURFACE_DURABLE_PREREQUISITE_PORT"] = port,
                },
            })!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                var timedOutStandardOutput = await process.StandardOutput.ReadToEndAsync();
                var timedOutStandardError = await process.StandardError.ReadToEndAsync();
                throw new TimeoutException(
                    $"The prerequisite script did not complete within 10 seconds. Output: {timedOutStandardOutput} Error: {timedOutStandardError}");
            }

            return new PrerequisiteScriptResult(
                process.ExitCode,
                await process.StandardOutput.ReadToEndAsync(),
                await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private sealed record PrerequisiteScriptResult(int ExitCode, string StandardOutput, string StandardError);
}
