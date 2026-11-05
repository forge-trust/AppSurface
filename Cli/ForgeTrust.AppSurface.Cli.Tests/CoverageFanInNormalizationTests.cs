using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInNormalizationTests
{
    private static readonly CoverageFanInBounds Bounds = new(1000, 100, 3000, 10, 10, 100, 10, 100, 10, 20);

    [Fact]
    public async Task NormalizationChecksInputBeforeStartingTheRuntimeAndAllowsACompleteWithinBudgetResult()
    {
        using var temp = TestDirectory.Create();
        var input = temp.WriteFile("coverage.xml", "<coverage><line/></coverage>");
        var output = Path.Join(temp.Path, "output");
        var runtime = new Runtime((directory, _) =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Join(directory, "Cobertura.xml"), "<coverage/>");
            return Task.CompletedTask;
        });
        var normalizer = new CoverageFanInNormalization(runtime, Bounds);
        var result = await normalizer.MergeAsync([input], output, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal("<coverage/>", File.ReadAllText(result.CoberturaPath));
        File.WriteAllText(input, "<coverage><line/><line/></coverage>");
        var limited = new CoverageFanInNormalization(runtime, Bounds with { Lines = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => limited.MergeAsync([input], output, CancellationToken.None));
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task GrowingRuntimeOutputCancelsAndDrainsTheInnerOperationBeforeReturningFailure()
    {
        using var temp = TestDirectory.Create();
        var input = temp.WriteFile("coverage.xml", "<coverage/>");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime(async (directory, token) =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Join(directory, "Cobertura.xml"), new string('x', 1001));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { drained.TrySetResult(); }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CoverageFanInNormalization(runtime, Bounds)
            .MergeAsync([input], Path.Join(temp.Path, "output"), deadline.Token));
        Assert.True(drained.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FinalOversizeOutputAndCancellationCannotReturnSuccessfulNormalization()
    {
        using var temp = TestDirectory.Create();
        var input = temp.WriteFile("coverage.xml", "<coverage/>");
        var runtime = new Runtime((directory, _) =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Join(directory, "Summary.txt"), new string('x', 101));
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new CoverageFanInNormalization(runtime, Bounds)
            .MergeAsync([input], Path.Join(temp.Path, "output"), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CoverageFanInNormalization(runtime, Bounds)
            .MergeAsync([input], Path.Join(temp.Path, "cancelled"), cancellation.Token));
        Assert.Equal(1, runtime.Calls);
    }

    private sealed class Runtime(Func<string, CancellationToken, Task> run) : ICoverageRunReportGenerator
    {
        internal int Calls { get; private set; }
        public async Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles,
            string outputDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            await run(outputDirectory, cancellationToken);
            return new(0, Path.Join(outputDirectory, "Cobertura.xml"), Path.Join(outputDirectory, "Summary.txt"));
        }
    }
}
