using System.Text;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[CollectionDefinition("Coverage fan-in process environment", DisableParallelization = true)]
public sealed class CoverageFanInProcessEnvironmentCollection { }

/// <summary>Exercises real diff-context prerequisite and refusal paths through its production process-runner seam.</summary>
[Collection("Coverage fan-in process environment")]
public sealed class CoverageFanInV3PrerequisiteTests
{
    private const string BaseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HeadCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Tree = "cccccccccccccccccccccccccccccccccccccccc";
    private const string OldBlob = "1111111111111111111111111111111111111111";
    private const string NewBlob = "2222222222222222222222222222222222222222";

    [Theory]
    [InlineData("diff.custom.command")]
    [InlineData("extensions.partialclone")]
    [InlineData("remote.origin.promisor")]
    [InlineData("remote.origin.partialclonefilter")]
    public async Task InitializeRefusesUnsupportedDiffAndPartialCloneConfiguration(string name)
    {
        using var fixture = new Fixture();
        fixture.Runner.Configuration = name;

        await AssertCauseAsync(() => fixture.Context.InitializeAsync(CancellationToken.None), "unsupported-diff");
    }

    [Fact]
    public async Task InitializeRefusesNonemptyLocalInfoAttributes()
    {
        using var fixture = new Fixture();
        fixture.Temp.WriteFile(".git/info/attributes", "*.cs diff=custom\n");

        await AssertCauseAsync(() => fixture.Context.InitializeAsync(CancellationToken.None), "unsupported-diff");
    }

    [Fact]
    public async Task InitializeRefusesShallowHistoryWithoutFetch()
    {
        using var fixture = new Fixture();
        fixture.Runner.Shallow = "true";

        await AssertCauseAsync(() => fixture.Context.InitializeAsync(CancellationToken.None), "merge-base-unavailable");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("fetch", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadRefusesMultipleMergeBases()
    {
        using var fixture = new Fixture();
        fixture.Runner.MergeBases = BaseCommit + "\n" + Tree + "\n";

        await AssertCauseAsync(() => fixture.ReadAsync(), "merge-base-unavailable");
    }

    [Fact]
    public async Task ReadRefusesOverOneThousandRenameCandidatesBeforeBlobAdmission()
    {
        using var fixture = new Fixture();
        fixture.Runner.Raw = string.Concat(Enumerable.Range(0, 1001).Select(index =>
            $":000000 100644 {new string('0', 40)} {NewBlob} A\0file-{index}.cs\0"));

        await AssertCauseAsync(() => fixture.ReadAsync(), "limit-exceeded");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("cat-file", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("sha256", "identity-mismatch")]
    [InlineData("sha1", "merge-base-unavailable")]
    public async Task ReadRefusesObjectFormatAndMergeBaseLengthMismatch(string objectFormat, string cause)
    {
        using var fixture = new Fixture();
        fixture.Runner.ObjectFormat = objectFormat;
        if (objectFormat == "sha1") fixture.Runner.MergeBases = BaseCommit[..^1];

        await AssertCauseAsync(() => fixture.ReadAsync(), cause);
    }

    [Theory]
    [InlineData("120000")]
    [InlineData("160000")]
    public async Task ReadRefusesSymlinkAndGitlinkRawModes(string mode)
    {
        using var fixture = new Fixture();
        fixture.Runner.Raw = $":{mode} {mode} {OldBlob} {NewBlob} M\0link-or-submodule\0";

        await AssertCauseAsync(() => fixture.ReadAsync(), "unsupported-diff");
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("wrong-object-name")]
    [InlineData("missing-record")]
    public async Task ReadRefusesCorruptCatFileBatchInventory(string corruption)
    {
        using var fixture = new Fixture();
        fixture.Runner.Raw = $":000000 100644 {new string('0', 40)} {NewBlob} A\0added.cs\0";
        fixture.Runner.BatchCorruption = corruption;

        await AssertCauseAsync(() => fixture.ReadAsync(), "malformed-diff");
    }

    [Fact]
    public async Task ReadRefusesTruncatedPatchRunnerOutput()
    {
        using var fixture = new Fixture();
        fixture.Runner.TruncatePatch = true;

        await AssertCauseAsync(() => fixture.ReadAsync(), "truncated-diff");
    }

    [Fact]
    public async Task DiffCommandsUseTheSubmoduleHeadForItsAttributeSource()
    {
        using var fixture = new Fixture();
        var childDirectory = fixture.Temp.CreateDirectory("submodule");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await fixture.Context.InitializeAsync(deadline.Token);
        await fixture.Context.RunAsync(["rev-parse", "--show-object-format"], deadline.Token, childDirectory);

        var childRequest = fixture.Runner.Requests[^1];
        Assert.Equal(childDirectory, childRequest.WorkingDirectory);
        Assert.Equal("HEAD", childRequest.EnvironmentOverrides!["GIT_ATTR_SOURCE"]);
    }

    [Fact]
    public async Task ChildEnvironmentClearsInheritedGitNamesAndUsesNativeCaseRules()
    {
        var inheritedName = OperatingSystem.IsWindows()
            ? "gIt_CoDeX_Prerequisite_Test"
            : "GIT_CODEX_PREREQUISITE_TEST";
        var lookupName = OperatingSystem.IsWindows() ? inheritedName.ToUpperInvariant() : inheritedName;
        var previous = Environment.GetEnvironmentVariable(inheritedName);
        try
        {
            Environment.SetEnvironmentVariable(inheritedName, "temporary-value");
            using var fixture = new Fixture();
            await fixture.Context.InitializeAsync(CancellationToken.None);

            var overrides = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(fixture.Runner.Requests[0].EnvironmentOverrides);
            Assert.True(overrides.TryGetValue(lookupName, out var removed));
            Assert.Null(removed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedName, previous);
        }
    }

    private static async Task AssertCauseAsync(Func<Task> action, string expectedCause)
    {
        var exception = await Assert.ThrowsAsync<CoverageFanInEvidenceException>(action);
        Assert.Equal(expectedCause, exception.Cause);
    }

    private sealed class Fixture : IDisposable
    {
        internal TestDirectory Temp { get; } = TestDirectory.Create();
        internal string Root => Temp.Path;
        internal ScriptedGitRunner Runner { get; } = new();
        internal CoverageFanInDiffContext Context { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Join(Root, ".git"));
            var policy = CoverageFanInPolicyTests.Policy() with
            {
                Source = new(HeadCommit, Tree, "10.0.402", "Release"),
                ContractVersion = 3,
                ReportProfileId = "coverlet-collector-cobertura-v1",
                Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: BaseCommit)
            };
            var expected = new CoverageFanInExpected(CoveragePolicyEncoding.ToElement(policy), CoveragePolicyEncoding.Digest(policy));
            Context = new CoverageFanInDiffContext(new CoverageFanInEngineRequest(expected, Root) { IdentityRunner = Runner },
                new CoverageFanInBudget(expected.Bounds.TotalBytes));
        }

        internal async Task<CoverageFanInDiffInput> ReadAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Context.InitializeAsync(deadline.Token);
            return await Context.ReadAsync(deadline.Token);
        }

        public void Dispose() => Temp.Dispose();
    }

    private sealed class ScriptedGitRunner : ICoverageRunProcessRunner
    {
        internal List<CoverageRunProcessRequest> Requests { get; } = [];
        internal string Configuration { get; set; } = string.Empty;
        internal string Shallow { get; set; } = "false";
        internal string ObjectFormat { get; set; } = "sha1";
        internal string MergeBases { get; set; } = BaseCommit + "\n";
        internal string Raw { get; set; } = $":100644 100644 {OldBlob} {NewBlob} M\0source.cs\0";
        internal string? BatchCorruption { get; set; }
        internal bool TruncatePatch { get; set; }

        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var args = request.Arguments;
            if (args.Contains("--version", StringComparer.Ordinal)) return Task.FromResult(Result("git version 2.46.0\n"));
            if (args.Contains("config", StringComparer.Ordinal))
                return Task.FromResult(Result(Configuration, Configuration.Length == 0 ? 1 : 0));
            if (args.Contains("--is-shallow-repository", StringComparer.Ordinal)) return Task.FromResult(Result(Shallow + "\n"));
            if (args.Contains("--git-common-dir", StringComparer.Ordinal)) return Task.FromResult(Result(".git\n"));
            if (args.Contains("--show-object-format", StringComparer.Ordinal)) return Task.FromResult(Result(ObjectFormat + "\n"));
            if (args.Contains("merge-base", StringComparer.Ordinal)) return Task.FromResult(Result(MergeBases));
            if (args.Contains("cat-file", StringComparer.Ordinal)) return Task.FromResult(Result(BatchOutput(request)));
            if (args.Contains("--raw", StringComparer.Ordinal)) return Task.FromResult(Result(Raw));
            if (args.Contains("--patch", StringComparer.Ordinal))
                return Task.FromResult(Result("truncated patch\n", truncated: TruncatePatch));
            throw new InvalidOperationException("Unexpected Git command in prerequisite test.");
        }

        private string BatchOutput(CoverageRunProcessRequest request)
        {
            if (BatchCorruption is { } corruption)
            {
                var oid = Encoding.UTF8.GetString(request.StandardInputBytes!).Trim();
                return corruption switch
                {
                    "tree" => $"{oid} tree 1\n",
                    "wrong-object-name" => $"{new string('3', oid.Length)} blob 1\n",
                    _ => string.Empty
                };
            }

            var ids = Encoding.UTF8.GetString(request.StandardInputBytes!).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(ids.Select(id => $"{id} blob 1\n"));
        }

        private static CoverageRunProcessResult Result(string output, int exitCode = 0, bool truncated = false) =>
            new(exitCode, output, truncated, output);
    }
}
