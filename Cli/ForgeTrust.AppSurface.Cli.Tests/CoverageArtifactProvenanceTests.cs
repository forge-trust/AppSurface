using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactProvenanceTests
{
    private static readonly CoverageArtifactProvenance Policy = new(null, "unknown", "configuration-fingerprint");

    [Fact]
    public async Task ReadAsync_FingerprintsCommittedCleanAndDirtyRepositoriesWithoutExposingStatus()
    {
        using var temp = TestDirectory.Create();
        await Git(temp.Path, "init", "-q");
        await Git(temp.Path, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "fixture");
        var reader = new CoverageArtifactProvenanceReader();

        var clean = await reader.ReadAsync(temp.Path, Policy);
        Assert.NotNull(clean.GitRevisionFingerprint);
        Assert.Equal(64, clean.GitRevisionFingerprint.Length);
        Assert.Equal(64, clean.GitStateFingerprint!.Length);
        Assert.Equal(Policy.ConfigurationFingerprint, clean.ConfigurationFingerprint);

        File.WriteAllText(Path.Join(temp.Path, "untracked-secret-name.txt"), "private contents");
        var dirty = await reader.ReadAsync(temp.Path, Policy);
        Assert.Equal(clean.GitRevisionFingerprint, dirty.GitRevisionFingerprint);
        Assert.NotEqual(clean.GitStateFingerprint, dirty.GitStateFingerprint);
        Assert.DoesNotContain("untracked-secret-name", dirty.GitStateFingerprint);
        Assert.DoesNotContain("private contents", dirty.GitStateFingerprint);
    }

    [Fact]
    public async Task ReadAsync_ReportsUnknownForNonRepositoryAndBoundedProbeFailure()
    {
        using var temp = TestDirectory.Create();
        var reader = new CoverageArtifactProvenanceReader();
        var absent = await reader.ReadAsync(temp.Path, Policy);
        Assert.Null(absent.GitRevisionFingerprint);
        Assert.Equal("unknown", absent.GitStateFingerprint);

        var calls = 0;
        var slow = new CoverageArtifactProvenanceReader(async (_, _, _, token) =>
        {
            calls++;
            if (calls == 1) return new string('a', 40);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        var bounded = await slow.ReadAsync(temp.Path, Policy);
        Assert.Equal(2, calls);
        Assert.NotNull(bounded.GitRevisionFingerprint);
        Assert.Equal("unknown", bounded.GitStateFingerprint);
        Assert.Equal(Policy.ConfigurationFingerprint, bounded.ConfigurationFingerprint);
    }

    private static async Task Git(string root, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }
}

public sealed class CoverageArtifactProvenanceBoundaryTests
{
    private static readonly CoverageArtifactProvenance Policy = new(null, "unknown", "configuration");

    [Fact]
    public async Task HeadWithNormalGitNewlineIsAcceptedAndMalformedHeadRemainsUnknown()
    {
        var valid = new CoverageArtifactProvenanceReader((_, args, _, _) =>
            Task.FromResult<string?>(args[0] == "rev-parse" ? new string('a', 40) + "\n" : ""));
        var result = await valid.ReadAsync("unused", Policy);
        Assert.NotNull(result.GitRevisionFingerprint);
        Assert.NotEqual("unknown", result.GitStateFingerprint);
        var invalid = new CoverageArtifactProvenanceReader((_, _, _, _) => Task.FromResult<string?>("secret-invalid-head"));
        var unknown = await invalid.ReadAsync("unused", Policy);
        Assert.Null(unknown.GitRevisionFingerprint);
        Assert.Equal("unknown", unknown.GitStateFingerprint);
    }

    [Fact]
    public async Task OutputBoundAndUnavailableStatusKeepKnownRevisionAndUnknownState()
    {
        foreach (var status in new string?[] { null, new string('x', 16 * 1024 + 1) })
        {
            var reader = new CoverageArtifactProvenanceReader((_, args, _, _) =>
                Task.FromResult<string?>(args[0] == "rev-parse" ? new string('a', 40) : status));
            var observed = await reader.ReadAsync("unused", Policy);
            Assert.NotNull(observed.GitRevisionFingerprint);
            Assert.Equal("unknown", observed.GitStateFingerprint);
            Assert.Equal("configuration", observed.ConfigurationFingerprint);
        }
    }

    [Fact]
    public async Task UnavailableProcessNeverCopiesExceptionSecretsIntoProvenance()
    {
        var reader = new CoverageArtifactProvenanceReader((_, _, _, _) => throw new IOException("token=secret"));
        var observed = await reader.ReadAsync("unused", Policy);
        Assert.Null(observed.GitRevisionFingerprint);
        Assert.Equal("unknown", observed.GitStateFingerprint);
        Assert.Equal("configuration", observed.ConfigurationFingerprint);
    }

    [Fact]
    public async Task CallerCancellationIsNotMisreportedAsSuccessfulGitObservation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var reader = new CoverageArtifactProvenanceReader((_, _, _, token) => Task.FromCanceled<string?>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync("unused", Policy, cancelled.Token));
    }
}
