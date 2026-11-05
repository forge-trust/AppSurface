using ForgeTrust.AppSurface.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInApiValidationTests
{
    [Fact]
    public void EveryPublicOperationRejectsNullBeforeReturningWork()
    {
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.CollectAndCaptureAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.CaptureAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.ComposeAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.AcceptAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.VerifyAsync(null!));
    }

    [Fact]
    public void UndefinedModesAndMissingCallerCollectionsFailBeforeFilesystemEffects()
    {
        using var temp = TestDirectory.Create();
        var policy = CoverageFanInPolicyTests.Policy();
        var output = Path.Join(temp.Path, "output");
        Assert.Throws<ArgumentException>(() => CoverageFanIn.CaptureAsync(new(policy, temp.Path, output,
            "primary", OutputMode: (CoverageOutputMode)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.ComposeAsync(new(policy, temp.Path, "primary", [], output,
            OutputMode: (CoverageOutputMode)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.ComposeAsync(new(policy, temp.Path, "primary", [], output,
            EvaluationIntent: (CoverageEvaluationIntent)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.AcceptAsync(new(policy, temp.Path, "primary", [], "composed", output,
            EvaluationIntent: (CoverageEvaluationIntent)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.VerifyAsync(new(policy, temp.Path, "primary", [], "composed", "accepted",
            EvaluationIntent: (CoverageEvaluationIntent)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.ComposeAsync(new(policy, temp.Path, "primary", null!, output)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.AcceptAsync(new(policy, temp.Path, "primary", null!, "composed", output)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.VerifyAsync(new(policy, temp.Path, "primary", null!, "composed", "accepted")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task PreCancelledOperationsReturnTypedResultsAndLeaveCallerWritersUsable()
    {
        using var temp = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var progress = new StringWriter();
        using var diagnostics = new StringWriter();
        var policy = CoverageFanInPolicyTests.Policy();
        var result = await CoverageFanIn.CaptureAsync(new(policy, temp.Path, Path.Join(temp.Path, "output"),
            "primary", "coverage.xml", "timings.json"), cancellation.Token, progress, diagnostics);
        Assert.Equal(CoverageOperationOutcome.Cancelled, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.True(result.Effects.CleanupComplete);
        progress.Write("caller retains progress");
        diagnostics.Write("caller retains diagnostics");
        Assert.Contains("caller retains progress", progress.ToString());
        Assert.Contains("caller retains diagnostics", diagnostics.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }
}
