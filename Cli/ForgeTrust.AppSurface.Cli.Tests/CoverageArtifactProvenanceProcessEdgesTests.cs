using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactProvenanceProcessEdgesTests
{
    [Fact]
    public async Task LargeRealGitStatusIsBoundedAndLeavesNoChildProcessHoldingTheProbe()
    {
        using var temp = TestDirectory.Create();
        await Git(temp.Path, "init", "-q");
        await Git(temp.Path, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "fixture");
        // -u normal still lists files at the repository root individually. This exceeds
        // the probe's 16 KiB output bound without injecting a synthetic process result.
        for (var index = 0; index < 400; index++)
            File.WriteAllText(System.IO.Path.Join(temp.Path, $"file-{index:D4}-" + new string('x', 80)), "fixture");
        var result = await new CoverageArtifactProvenanceReader().ReadAsync(temp.Path, new(null, "unknown", "policy"));
        Assert.NotNull(result.GitRevisionFingerprint);
        Assert.Equal("unknown", result.GitStateFingerprint);
        Assert.Equal("policy", result.ConfigurationFingerprint);
        Assert.Equal(400, Directory.EnumerateFiles(temp.Path).Count());
        // A bounded failure must not leave a Git operation or index lock preventing the
        // next read. A fresh status process can still run and report the files.
        await Git(temp.Path, "status", "--porcelain=v1", "--untracked-files=normal");
        Assert.False(File.Exists(System.IO.Path.Join(temp.Path, ".git", "index.lock")));
    }

    private static async Task Git(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        Assert.True(process.ExitCode == 0, await error);
    }
}
