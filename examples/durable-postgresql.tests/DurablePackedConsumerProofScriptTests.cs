using System.Diagnostics;
using ForgeTrust.AppSurface.Testing;

/// <summary>Locks the packed Durable consumer proof to fresh-cache, local-artifact identity checks.</summary>
public sealed class DurablePackedConsumerProofScriptTests
{
    [Fact]
    public void Script_must_prove_local_feed_restore_and_package_byte_identity()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(repositoryRoot, "Durable", "verify-packed-consumers.sh");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("export NUGET_PACKAGES=\"$WORK_DIR/packages\"", script, StringComparison.Ordinal);
        Assert.Contains("ARTIFACTS_DIR=\"$WORK_DIR/artifacts\"", script, StringComparison.Ordinal);
        Assert.Contains("--artifacts-path \"$ARTIFACTS_DIR\"", script, StringComparison.Ordinal);
        Assert.Contains("--configfile \"$CONFIG_FILE\"", script, StringComparison.Ordinal);
        Assert.Contains("<clear />", script, StringComparison.Ordinal);
        Assert.Contains("<package pattern=\"ForgeTrust.*\" />", script, StringComparison.Ordinal);
        Assert.Contains("project.assets.json", script, StringComparison.Ordinal);
        Assert.Contains(".nupkg.metadata", script, StringComparison.Ordinal);
        Assert.Contains(
            "$(lowercase \"$package_id\").$(lowercase \"$PACKAGE_VERSION\").nupkg",
            script,
            StringComparison.Ordinal);
        Assert.Contains("\\\"source\\\": \\\"$FEED_DIR\\\"", script, StringComparison.Ordinal);
        Assert.Contains("cmp -s", script, StringComparison.Ordinal);
        Assert.Contains("freshly packed local artifact", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_has_valid_bash_syntax()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The packed consumer proof script is a Unix Bash entry point.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(repositoryRoot, "Durable", "verify-packed-consumers.sh");
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var error = await process.StandardError.ReadToEndAsync();

        Assert.True(process.ExitCode == 0, error);
    }
}
