using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        var cleanObservation = await reader.ReadWithOutcomeAsync(temp.Path, Policy);
        var clean = cleanObservation.Provenance;
        var expectedRevision = ExpectedFingerprint((await GitOutput(temp.Path, "rev-parse", "--verify", "HEAD")).Trim());
        Assert.Equal(expectedRevision, clean.GitRevisionFingerprint);
        Assert.NotNull(clean.GitRevisionFingerprint);
        Assert.Equal(64, clean.GitRevisionFingerprint.Length);
        var cleanStatus = await GitOutput(temp.Path, "status", "--porcelain=v1", "--untracked-files=normal");
        Assert.Equal(string.Empty, cleanStatus);
        AssertRealStateProbe(cleanObservation, cleanStatus);
        Assert.Equal(Policy.ConfigurationFingerprint, clean.ConfigurationFingerprint);

        File.WriteAllText(Path.Join(temp.Path, "untracked-secret-name.txt"), "private contents");
        var dirtyObservation = await reader.ReadWithOutcomeAsync(temp.Path, Policy);
        var dirty = dirtyObservation.Provenance;
        Assert.Equal(expectedRevision, dirty.GitRevisionFingerprint);
        Assert.Equal(clean.GitRevisionFingerprint, dirty.GitRevisionFingerprint);
        var dirtyStatus = await GitOutput(temp.Path, "status", "--porcelain=v1", "--untracked-files=normal");
        Assert.Contains("untracked-secret-name.txt", dirtyStatus);
        AssertRealStateProbe(dirtyObservation, dirtyStatus);
        if (cleanObservation.GitStateProbeOutcome == CoverageArtifactGitStateProbeOutcome.Fingerprinted
            && dirtyObservation.GitStateProbeOutcome == CoverageArtifactGitStateProbeOutcome.Fingerprinted)
            Assert.NotEqual(clean.GitStateFingerprint, dirty.GitStateFingerprint);
        Assert.DoesNotContain("untracked-secret-name", dirty.GitStateFingerprint);
        Assert.DoesNotContain("private contents", dirty.GitStateFingerprint);
    }

    [Fact]
    public async Task ReadWithOutcomeAsync_FingerprintsControlledCleanAndDirtyStatesAndRedactsStatus()
    {
        const string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var status = string.Empty;
        var reader = new CoverageArtifactProvenanceReader((_, arguments, timeout, _) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(500), timeout);
            return Task.FromResult<string?>(arguments[0] == "rev-parse" ? revision : status);
        });

        var clean = await reader.ReadWithOutcomeAsync("unused", Policy);
        Assert.Equal(ExpectedFingerprint(revision), clean.Provenance.GitRevisionFingerprint);
        Assert.Equal(CoverageArtifactGitStateProbeOutcome.Fingerprinted, clean.GitStateProbeOutcome);
        Assert.Equal(ExpectedStateFingerprint("clean", string.Empty), clean.Provenance.GitStateFingerprint);

        status = "?? untracked-secret-name.txt\n";
        var dirty = await reader.ReadWithOutcomeAsync("unused", Policy);
        Assert.Equal(ExpectedFingerprint(revision), dirty.Provenance.GitRevisionFingerprint);
        Assert.Equal(CoverageArtifactGitStateProbeOutcome.Fingerprinted, dirty.GitStateProbeOutcome);
        Assert.Equal(ExpectedStateFingerprint("dirty", status), dirty.Provenance.GitStateFingerprint);
        Assert.NotEqual(clean.Provenance.GitStateFingerprint, dirty.Provenance.GitStateFingerprint);
        Assert.DoesNotContain("untracked-secret-name", dirty.Provenance.GitStateFingerprint);
        Assert.DoesNotContain("private contents", dirty.Provenance.GitStateFingerprint);
    }

    [Theory]
    [InlineData("io", "IoFailure")]
    [InlineData("invalid-data", "OutputRejected")]
    [InlineData("invalid-operation", "InvalidProcessState")]
    [InlineData("native", "NativeProcessFailure")]
    [InlineData("permission", "PermissionDenied")]
    [InlineData("null", "NoUsableOutput")]
    [InlineData("oversized", "OutputRejected")]
    public async Task ReadWithOutcomeAsync_ClassifiesStatusFailureWithoutPublishingItsMessage(
        string failure, string expectedOutcome)
    {
        var calls = 0;
        var reader = new CoverageArtifactProvenanceReader((_, _, _, _) =>
        {
            if (++calls == 1) return Task.FromResult<string?>(new string('a', 40));
            return failure switch
            {
                "io" => Task.FromException<string?>(new IOException("path=private repository detail")),
                "invalid-data" => Task.FromException<string?>(new InvalidDataException("path=private repository detail")),
                "invalid-operation" => Task.FromException<string?>(new InvalidOperationException("path=private repository detail")),
                "native" => Task.FromException<string?>(new System.ComponentModel.Win32Exception(5, "private repository detail")),
                "permission" => Task.FromException<string?>(new UnauthorizedAccessException("path=private repository detail")),
                "null" => Task.FromResult<string?>(null),
                "oversized" => Task.FromResult<string?>(new string('x', 16 * 1024 + 1)),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
        });

        var observed = await reader.ReadWithOutcomeAsync("unused", Policy);

        Assert.Equal(2, calls);
        Assert.NotNull(observed.Provenance.GitRevisionFingerprint);
        Assert.Equal("unknown", observed.Provenance.GitStateFingerprint);
        Assert.Equal(expectedOutcome, observed.GitStateProbeOutcome.ToString());
        Assert.DoesNotContain("private repository detail", observed.Provenance.GitStateFingerprint);
    }

    [Fact]
    public async Task ReadWithOutcomeAsync_DoesNotTreatUnrequestedRunnerCancellationAsDeadlineExpiry()
    {
        var calls = 0;
        var reader = new CoverageArtifactProvenanceReader((_, _, _, _) =>
        {
            if (++calls == 1) return Task.FromResult<string?>(new string('a', 40));
            return Task.FromException<string?>(new OperationCanceledException("runner canceled without the deadline token"));
        });

        var observed = await reader.ReadWithOutcomeAsync("unused", Policy);

        Assert.NotNull(observed.Provenance.GitRevisionFingerprint);
        Assert.Equal("unknown", observed.Provenance.GitStateFingerprint);
        Assert.Equal(CoverageArtifactGitStateProbeOutcome.RunnerCancelled, observed.GitStateProbeOutcome);
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
        var bounded = await slow.ReadWithOutcomeAsync(temp.Path, Policy);
        Assert.Equal(2, calls);
        Assert.NotNull(bounded.Provenance.GitRevisionFingerprint);
        Assert.Equal("unknown", bounded.Provenance.GitStateFingerprint);
        Assert.Equal(Policy.ConfigurationFingerprint, bounded.Provenance.ConfigurationFingerprint);
        Assert.Equal(CoverageArtifactGitStateProbeOutcome.DeadlineExceeded, bounded.GitStateProbeOutcome);
    }

    private static async Task Git(string root, params string[] arguments)
    {
        _ = await GitOutput(root, arguments);
    }

    private static async Task<string> GitOutput(string root, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var outputText = await output;
        Assert.True(process.ExitCode == 0, await error);
        return outputText;
    }

    private static void AssertRealStateProbe(CoverageArtifactProvenanceReadResult observation, string expectedStatus)
    {
        var expectedClassification = expectedStatus.Length == 0 ? "clean" : "dirty";
        if (observation.GitStateProbeOutcome == CoverageArtifactGitStateProbeOutcome.Fingerprinted)
        {
            Assert.Equal(ExpectedStateFingerprint(expectedClassification, expectedStatus), observation.Provenance.GitStateFingerprint);
            return;
        }

        Assert.Equal("unknown", observation.Provenance.GitStateFingerprint);
        Assert.Equal(CoverageArtifactGitStateProbeOutcome.DeadlineExceeded, observation.GitStateProbeOutcome);
    }

    private static string ExpectedStateFingerprint(string classification, string status)
        => ExpectedFingerprint((classification + "\n" + status).Trim());

    private static string ExpectedFingerprint(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadWithOutcomeAsync("unused", Policy, cancelled.Token));
    }
}
