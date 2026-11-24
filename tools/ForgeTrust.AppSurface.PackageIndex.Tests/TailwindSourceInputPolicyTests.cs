namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindSourceInputPolicyTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot,
        "tailwind-source-input-policy", Guid.NewGuid().ToString("N"));

    public static TheoryData<string> RelevantInputs => new()
    {
        "tools/ForgeTrust.AppSurface.PackageIndex",
        "tools/ForgeTrust.AppSurface.ReleaseContracts",
        "Web/ForgeTrust.AppSurface.Web.Tailwind",
        "packages/package-index.yml",
        "scripts/verify-tailwind-package-consumer.sh",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "NuGet.package-gate.config",
        "NuGet.config",
        "global.json"
    };

    [Theory]
    [MemberData(nameof(RelevantInputs))]
    public void InputPolicyMatchesExactPathsAndDescendantsButNotSiblingPrefixes(string input)
    {
        Assert.True(TailwindSourceIdentity.IsRelevantInputPath(input));
        Assert.True(TailwindSourceIdentity.IsRelevantInputPath(input + "/nested/output"));
        Assert.True(TailwindSourceIdentity.IsRelevantInputPath(input.Replace('/', Path.DirectorySeparatorChar)));
        Assert.False(TailwindSourceIdentity.IsRelevantInputPath(input + "-unrelated/output"));
        Assert.False(TailwindSourceIdentity.IsRelevantInputPath(input.ToUpperInvariant()));
        Assert.False(TailwindSourceIdentity.IsRelevantInputPath("reports/" + input));
    }

    [Theory]
    [MemberData(nameof(RelevantInputs))]
    public async Task EveryRelevantInputRefusesTransportOutputBeforeSourceOrHttp(string input)
    {
        var work = TestPathUtils.PathUnder(_root, input.Replace('/', Path.DirectorySeparatorChar));
        var report = TestPathUtils.PathUnder(_root, "report");
        var output = TestPathUtils.PathUnder(_root, "output");
        var expected = new ArtifactExpectedContext("forge-trust/AppSurface", "12345", "901", "1", new string('a', 40), "501");
        var request = new TailwindArtifactTransportRequest(_root, expected,
            TestPathUtils.PathUnder(_root, "receipt-input", "receipt.json"), new string('b', 64), work, output, report, "fixture-token");
        var sourceCalls = 0;
        using var handler = new RejectingHandler();
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) =>
        {
            sourceCalls++;
            return Task.CompletedTask;
        });

        var result = await workflow.RunAsync(request);

        Assert.Equal("artifact-input-invalid", result.Code);
        Assert.False(result.ReportWritten);
        Assert.Equal(0, sourceCalls);
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(report));
    }

    [Fact]
    public async Task SiblingPrefixOutputReachesSourceValidationInsteadOfBeingRejectedAsWatched()
    {
        var work = TestPathUtils.PathUnder(_root, "tools", "ForgeTrust.AppSurface.PackageIndex-reports");
        var report = TestPathUtils.PathUnder(_root, "report");
        var expected = new ArtifactExpectedContext("forge-trust/AppSurface", "12345", "901", "1", new string('a', 40), "501");
        var request = new TailwindArtifactTransportRequest(_root, expected,
            TestPathUtils.PathUnder(_root, "receipt-input", "receipt.json"), new string('b', 64), work,
            TestPathUtils.PathUnder(_root, "output"), report, "fixture-token");
        var sourceCalls = 0;
        using var handler = new RejectingHandler();
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) =>
        {
            sourceCalls++;
            throw new PackageIndexException("Stop at the source boundary.");
        });

        var result = await workflow.RunAsync(request);

        Assert.Equal("artifact-source-mismatch", result.Code);
        Assert.True(result.ReportWritten);
        Assert.Equal(1, sourceCalls);
        Assert.Equal(0, handler.Calls);
        Assert.False(Directory.Exists(work));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Path validation must precede HTTP.");
        }
    }
}
