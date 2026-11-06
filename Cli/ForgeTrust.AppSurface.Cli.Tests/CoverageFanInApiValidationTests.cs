using System.Text.Json;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

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

    [Theory]
    [InlineData("compose-inventory")]
    [InlineData("accept-composition")]
    [InlineData("verify-accepted")]
    [InlineData("collect-deadline")]
    public async Task EngineRejectsIncompleteOperationContractsBeforeFilesystemEffects(string scenario)
    {
        using var temp = TestDirectory.Create();
        var expected = Expected();
        var output = Path.Join(temp.Path, "output");
        var request = new CoverageFanInEngineRequest(expected, temp.Path, output,
            PrimaryCaptureDirectory: Path.Join(temp.Path, "primary"),
            SupplementaryCaptureDirectories: []);
        var operation = scenario switch
        {
            "compose-inventory" => "compose",
            "accept-composition" => "accept",
            "verify-accepted" => "verify",
            "collect-deadline" => "collect",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        if (scenario == "accept-composition")
        {
            request = request with { ComposedOutputDirectory = null };
        }
        else if (scenario == "compose-inventory")
        {
            request = request with { SupplementaryCaptureDirectories = null };
        }
        else if (scenario == "verify-accepted")
        {
            request = request with { ComposedOutputDirectory = Path.Join(temp.Path, "composed") };
        }
        else if (scenario == "collect-deadline")
        {
            request = request with { DeadlineSeconds = 0, CollectionDirectory = Path.Join(temp.Path, "collection") };
        }

        var result = await CoverageFanInEngine.ExecuteAsync(operation, request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        Assert.Equal("ASCOV301", Assert.Single(result.Diagnostics).Code);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task EngineReportsUnknownOperationAfterConfirmingTrustedSourceIdentity()
    {
        using var temp = TestDirectory.Create();
        var project = Path.Join(temp.Path, "tests", "A.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        var source = CoverageFanInPolicyTests.Policy().Source;
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            TestProjects = ["tests/A.Tests.csproj"]
        };
        var frozen = CoveragePolicyEncoding.Freeze(policy);
        var expected = new CoverageFanInExpected(
            JsonSerializer.SerializeToElement(frozen, CoveragePolicyEncoding.Json),
            CoveragePolicyEncoding.Digest(frozen));
        var request = new CoverageFanInEngineRequest(expected, temp.Path, Path.Join(temp.Path, "output"))
        {
            IdentityRunner = new IdentityRunner((command, _) => Task.FromResult(new CoverageRunProcessResult(
                0,
                command.FileName == "dotnet" ? source.Sdk : string.Join(" ", command.Arguments) switch
                {
                    "--no-replace-objects -c core.fsmonitor=false rev-parse --show-toplevel" => temp.Path,
                    "--no-replace-objects -c core.fsmonitor=false rev-parse HEAD" => source.Commit,
                    "--no-replace-objects -c core.fsmonitor=false rev-parse HEAD^{tree}" => source.Tree,
                    "--no-replace-objects -c core.fsmonitor=false ls-files --stage -v -z" => "H 100644 0123456789abcdef0123456789abcdef01234567 0\ttests/A.Tests.csproj\0",
                    "--no-replace-objects -c core.fsmonitor=false status --porcelain --untracked-files=all --ignore-submodules=none" => string.Empty,
                    _ => throw new InvalidOperationException("Unexpected source identity command.")
                })))
        };

        var result = await CoverageFanInEngine.ExecuteAsync("unrecognized", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        Assert.Equal("ASCOV301", Assert.Single(result.Diagnostics).Code);
        Assert.Contains("unrecognized", Assert.Single(result.Diagnostics).Field);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.Equal(6, ((IdentityRunner)request.IdentityRunner).Calls);
        Assert.False(Directory.Exists(request.OutputDirectory));
    }

    [Theory]
    [InlineData("capture", "repository")]
    [InlineData("capture", "output")]
    [InlineData("capture", "coverage")]
    [InlineData("capture", "timings")]
    [InlineData("capture", "raw")]
    [InlineData("capture", "producer")]
    [InlineData("import", "coverage")]
    [InlineData("import", "timings")]
    [InlineData("import", "raw")]
    [InlineData("import", "source-root")]
    [InlineData("compose", "primary")]
    [InlineData("compose", "supplements")]
    [InlineData("accept", "composition")]
    [InlineData("verify", "composition")]
    [InlineData("verify", "accepted")]
    [InlineData("collect", "deadline-small")]
    [InlineData("collect", "deadline-large")]
    [InlineData("collect", "parallelism")]
    [InlineData("collect", "collection")]
    public async Task TypedPublicOperationsRejectOneIncompleteSelectorBeforeSourceAccess(string operation, string selector)
    {
        using var temp = TestDirectory.Create();
        var policy = CoverageFanInPolicyTests.Policy();
        var output = Path.Join(temp.Path, "output");
        ValueTask<CoverageFanInResult> pending;
        if (operation is "capture" or "import")
        {
            var request = new CoverageCaptureRequest(policy, temp.Path, output, "primary", "coverage", "timings");
            if (operation == "import") request = request with { CoveragePath = null, TimingsPath = null, ImportDirectory = "portable", OriginalSourceRoot = "/producer" };
            request = selector switch
            {
                "repository" => request with { RepositoryRoot = " " },
                "output" => request with { OutputDirectory = " " },
                "coverage" => request with { CoveragePath = operation == "import" ? "conflict" : " " },
                "timings" => request with { TimingsPath = operation == "import" ? "conflict" : null },
                "raw" => request with { RawCoveragePath = "conflict" },
                "producer" => request with { ProducerId = "unknown" },
                _ => request with { OriginalSourceRoot = " " }
            };
            pending = CoverageFanIn.CaptureAsync(request);
        }
        else if (operation == "compose")
            pending = CoverageFanIn.ComposeAsync(new(policy, temp.Path, selector == "primary" ? " " : "primary",
                selector == "supplements" ? ["undeclared"] : [], output));
        else if (operation == "accept")
            pending = CoverageFanIn.AcceptAsync(new(policy, temp.Path, "primary", [], " ", output));
        else if (operation == "verify")
            pending = CoverageFanIn.VerifyAsync(new(policy, temp.Path, "primary", [],
                selector == "composition" ? " " : "composition", selector == "accepted" ? " " : "accepted"));
        else
            pending = CoverageFanIn.CollectAndCaptureAsync(new(policy, temp.Path, output,
                selector == "collection" ? " " : "collection", "primary",
                selector == "deadline-small" ? 0 : selector == "deadline-large" ? 86401 : 60,
                Parallelism: selector == "parallelism" ? 0 : 1));
        var result = await pending;
        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.Equal("ASCOV301", Assert.Single(result.Diagnostics).Code);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    private static CoverageFanInExpected Expected()
    {
        var policy = CoveragePolicyEncoding.Freeze(CoverageFanInPolicyTests.Policy());
        return new(JsonSerializer.SerializeToElement(policy, CoveragePolicyEncoding.Json),
            CoveragePolicyEncoding.Digest(policy));
    }

    private sealed class IdentityRunner(
        Func<CoverageRunProcessRequest, CancellationToken, Task<CoverageRunProcessResult>> execute)
        : ICoverageRunProcessRunner
    {
        internal int Calls { get; private set; }

        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return execute(request, cancellationToken);
        }
    }
}
