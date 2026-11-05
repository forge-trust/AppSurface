using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInIdentityTests
{
    [Fact]
    public async Task IdentityDeadlineCancelsTheRunnerAndCannotPublishACapture()
    {
        using var temp = TestDirectory.Create();
        var runner = new IdentityRunner(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, string.Empty);
        });
        var request = Request(temp.Path) with { IdentityRunner = runner };
        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);
        Assert.Equal("TimedOut", result.Outcome);
        Assert.True(runner.Token.IsCancellationRequested);
        Assert.Single(result.Diagnostics);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.False(Directory.Exists(request.OutputDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task IdentityDeadlineCancelsAtEveryConcreteSourceCommandBeforeCapturePublication(int stallAt)
    {
        using var temp = TestDirectory.Create();
        var source = CoverageFanInPolicyTests.Policy().Source;
        var expected = new[]
        {
            CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(temp.Path)),
            source.Commit,
            source.Tree,
            source.Sdk,
            string.Empty
        };
        var expectedCommands = new (string FileName, string Arguments)[]
        {
            ("git", "rev-parse --show-toplevel"),
            ("git", "rev-parse HEAD"),
            ("git", "rev-parse HEAD^{tree}"),
            ("dotnet", "--version"),
            ("git", "status --porcelain --untracked-files=all")
        };
        var commands = new List<CoverageRunProcessRequest>();
        var calls = 0;
        var runner = new IdentityRunner(async (command, token) =>
        {
            var index = calls++;
            commands.Add(command);
            if (index == stallAt)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(0, string.Empty);
            }
            return new(0, expected[index]);
        });
        var request = Request(temp.Path) with { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("TimedOut", result.Outcome);
        Assert.True(runner.Token.IsCancellationRequested);
        Assert.Equal(stallAt + 1, calls);
        Assert.Equal(expectedCommands.Take(stallAt + 1), commands.Select(command =>
            (command.FileName, string.Join(" ", command.Arguments))));
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.False(Directory.Exists(request.OutputDirectory));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task FailedOrTruncatedIdentityCannotBeTreatedAsAnObservation(int exitCode, bool truncated)
    {
        using var temp = TestDirectory.Create();
        var request = Request(temp.Path) with
        {
            IdentityRunner = new IdentityRunner((_, _) => Task.FromResult(new CoverageRunProcessResult(
                exitCode, "private-identity-output-sentinel", truncated)))
        };
        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);
        Assert.Equal("Rejected", result.Outcome);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.DoesNotContain("private-identity-output-sentinel", JsonSerializer.Serialize(result));
    }

    private static CoverageFanInEngineRequest Request(string root)
    {
        var policy = CoveragePolicyEncoding.Freeze(CoverageFanInPolicyTests.Policy() with
        { Limits = new CoverageOperationLimits(IdentitySeconds: 1) });
        return new(new(JsonSerializer.SerializeToElement(policy, CoveragePolicyEncoding.Json), CoveragePolicyEncoding.Digest(policy)),
            root, Path.Join(root, "capture"), CoveragePath: Path.Join(root, "coverage.xml"), TimingsPath: Path.Join(root, "timings.json"));
    }

    private sealed class IdentityRunner(Func<CoverageRunProcessRequest, CancellationToken, Task<CoverageRunProcessResult>> execute) : ICoverageRunProcessRunner
    {
        internal CancellationToken Token { get; private set; }
        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return execute(request, cancellationToken);
        }
    }
}
