namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Checks the public command boundary for release proof modes and missing authority.</summary>
public sealed class TailwindCliDispatchTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder("/private/tmp", "tailwind-cli-dispatch", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("verify-tailwind-consumer", "invalid", "must be local or release")]
    [InlineData("verify-tailwind-evidence", "invalid", "must be producer-binding")]
    [InlineData("verify-tailwind-evidence", "publish-preflight", "--report-directory")]
    [InlineData("verify-tailwind-evidence", "validate-publication-start", "--report-directory")]
    public async Task ReleaseCommands_RejectInvalidOrIncompleteAuthorityAtCliBoundary(
        string command, string mode, string expectedError)
    {
        Directory.CreateDirectory(_root);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(
            [command, "--repo-root", _root, "--mode", mode], stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.Contains(expectedError, stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("succeeded", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggregateCli_LeavesNoSuccessReceiptWhenProducerAuthorityIsMissing()
    {
        Directory.CreateDirectory(_root);
        var report = TestPathUtils.PathUnder(_root, "aggregate-report");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(
            ["verify-tailwind-evidence", "--repo-root", _root, "--mode", "aggregate",
                "--report-directory", report], stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(TestPathUtils.PathUnder(report, "tailwind-native-host-evidence.json")));
        Assert.True(stderr.ToString().Length > 0 || stdout.ToString().Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
