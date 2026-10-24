using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactProvenanceDecisionEdgesTests
{
    [Fact]
    public void NullCommandRunnerIsRejected()
        => Assert.Throws<ArgumentNullException>(() => new CoverageArtifactProvenanceReader(null!));

    [Theory]
    [InlineData("io")]
    [InlineData("metadata")]
    [InlineData("state")]
    [InlineData("native")]
    [InlineData("permission")]
    public async Task FailedStatusProbeKeepsRevisionWithoutPublishingExceptionText(string failure)
    {
        var calls = 0;
        var reader = new CoverageArtifactProvenanceReader((_, _, _, _) =>
        {
            if (++calls == 1) return Task.FromResult<string?>(new string('a', 40));
            throw failure switch
            {
                "io" => new IOException("token=secret"),
                "metadata" => new InvalidDataException("token=secret"),
                "state" => new InvalidOperationException("token=secret"),
                "native" => new System.ComponentModel.Win32Exception(5),
                _ => new UnauthorizedAccessException("token=secret")
            };
        });
        var result = await reader.ReadAsync("unused", new(null, "unknown", "configuration"));
        Assert.Equal(2, calls);
        Assert.Equal(64, result.GitRevisionFingerprint!.Length);
        Assert.Equal("unknown", result.GitStateFingerprint);
        Assert.Equal("configuration", result.ConfigurationFingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidHeadDoesNotAttemptStatus(string? head)
    {
        var calls = 0;
        var reader = new CoverageArtifactProvenanceReader((_, _, _, _) => { calls++; return Task.FromResult(head); });
        var result = await reader.ReadAsync("unused", new(null, "unknown", "configuration"));
        Assert.Equal(1, calls);
        Assert.Null(result.GitRevisionFingerprint);
        Assert.Equal("unknown", result.GitStateFingerprint);
    }
}
