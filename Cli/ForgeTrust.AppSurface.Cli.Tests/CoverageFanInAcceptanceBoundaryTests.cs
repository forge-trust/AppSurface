using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInAcceptanceBoundaryTests
{
    [Theory]
    [InlineData(CoverageGateMode.AggregateOnly, 64, 'a')]
    [InlineData(CoverageGateMode.AggregateAndPatch, 0, 'a')]
    [InlineData(CoverageGateMode.AggregateAndPatch, 63, 'a')]
    [InlineData(CoverageGateMode.AggregateAndPatch, 65, 'a')]
    [InlineData(CoverageGateMode.AggregateAndPatch, 64, 'g')]
    [InlineData(CoverageGateMode.AggregateAndPatch, 64, 'é')]
    public async Task InvalidExpectedPatchBindingIsRejectedBeforeOutputCreation(
        CoverageGateMode mode, int length, char digit)
    {
        using var temp = TestDirectory.Create();
        var output = Path.Join(temp.Path, "accepted");
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            Gate = new(mode, DiffBase: mode == CoverageGateMode.AggregateAndPatch ? "origin/main" : null)
        };
        var request = new CoverageAcceptRequest(policy, temp.Path, "primary", [], "composed", output)
        { ExpectedPatchSha256 = new string(digit, length) };

        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await CoverageFanIn.AcceptAsync(request));

        Assert.Contains("expectedPatchSha256", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('F')]
    public async Task ValidExpectedPatchBindingPreservesPreCancellationWithoutPublishing(char digit)
    {
        using var temp = TestDirectory.Create();
        var output = Path.Join(temp.Path, "accepted");
        var policy = CoverageFanInPolicyTests.Policy() with
        { Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: "origin/main") };
        var request = new CoverageAcceptRequest(policy, temp.Path, "primary", [], "composed", output)
        { ExpectedPatchSha256 = new string(digit, 64) };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await CoverageFanIn.AcceptAsync(request, cancellation.Token);

        Assert.Equal(CoverageOperationOutcome.Cancelled, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }
}
