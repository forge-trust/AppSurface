using System.Diagnostics;
using ForgeTrust.AppSurface.Testing;

/// <summary>Locks the one-command proof to its local-only, explicit-migration safety boundary.</summary>
public sealed class DurablePostgreSqlLocalProofScriptTests
{
    [Fact]
    public void Script_composes_the_required_local_proof_with_ephemeral_credentials_and_no_startup_ddl()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "examples",
            "durable-postgresql",
            "run-local-proof.sh");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("check-prerequisites.sh", script, StringComparison.Ordinal);
        Assert.Contains("postgres:16.5@sha256:", script, StringComparison.Ordinal);
        Assert.Contains("durable schema apply", script, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_MIGRATION_CONNECTION", script, StringComparison.Ordinal);
        Assert.Contains("Durable/configure-postgresql-roles.sql", script, StringComparison.Ordinal);
        Assert.Contains("schema-bootstrap-dev", script, StringComparison.Ordinal);
        Assert.Contains("verify-local", script, StringComparison.Ordinal);
        Assert.Contains("dotnet build", script, StringComparison.Ordinal);
        Assert.Contains("-m:1", script, StringComparison.Ordinal);
        Assert.Contains("-p:UseSharedCompilation=false", script, StringComparison.Ordinal);
        Assert.Contains("--no-build", script, StringComparison.Ordinal);
        Assert.Contains("docker rm --force", script, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_PASSWORD", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$MIGRATION_OWNER_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$DISPATCHER_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$RUNTIME_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$RETENTION_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("printf '%s\\n' \"$ROLE_SQL\" |", script, StringComparison.Ordinal);
        Assert.Contains("docker exec -i", script, StringComparison.Ordinal);
        Assert.Contains("unset ROLE_SQL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-c \"$ROLE_SQL\"", script, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS=420", script, StringComparison.Ordinal);
        Assert.Contains("kill -TERM \"$$\"", script, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", script, StringComparison.Ordinal);
        Assert.Contains("trap interrupt INT TERM", script, StringComparison.Ordinal);
        Assert.DoesNotContain("POSTGRES_HOST_AUTH_METHOD=trust", script, StringComparison.Ordinal);
        Assert.DoesNotContain("set -x", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_has_valid_bash_syntax()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Bash syntax validation runs only on Unix hosts.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "examples",
            "durable-postgresql",
            "run-local-proof.sh");
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();

        Assert.True(process.ExitCode == 0, error);
    }
}
