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

    [Theory]
    [InlineData("tag")]
    [InlineData("tree")]
    [InlineData("blob")]
    public async Task ReadRefusesBaseObjectsThatAreNotCommitsBeforeMergeBase(string objectType)
    {
        using var fixture = new Fixture();
        fixture.Runner.BaseObjectType = objectType;

        await AssertCauseAsync(() => fixture.ReadAsync(), "identity-mismatch");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("merge-base", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadRefusesMissingBaseObjectWithHistoryRecovery()
    {
        using var fixture = new Fixture();
        fixture.Runner.BaseObjectMissing = true;

        await AssertCauseAsync(() => fixture.ReadAsync(), "merge-base-unavailable");
    }

    [Fact]
    public async Task ReadRefusesNoCommonAncestorWithoutFallingBackToEitherEndpoint()
    {
        using var fixture = new Fixture();
        fixture.Runner.MergeBases = string.Empty;

        await AssertCauseAsync(() => fixture.ReadAsync(), "merge-base-unavailable");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("--raw", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadMapsMergeBaseCommandFailureToHistoryRecovery()
    {
        using var fixture = new Fixture();
        fixture.Runner.FailureToken = "merge-base";

        await AssertCauseAsync(() => fixture.ReadAsync(), "merge-base-unavailable");
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("--raw", StringComparer.Ordinal));
    }

    [Fact]
    public async Task InvalidV3EngineRequestKeepsThePolicyDiagnosticCode()
    {
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            ContractVersion = 3,
            ReportProfileId = "coverlet-collector-cobertura-v1"
        };
        var expected = new CoverageFanInExpected(CoveragePolicyEncoding.ToElement(policy), CoveragePolicyEncoding.Digest(policy));
        var result = await CoverageFanInEngine.ExecuteAsync("capture", new(expected, " "),
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        Assert.Equal("ASCOV301", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("invalid-policy", Assert.Single(result.Diagnostics).Cause);
        Assert.False(result.Created);
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
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Any(argument => argument.StartsWith("--batch-check", StringComparison.Ordinal)));
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
    public async Task ReadRefusesRepositoryConfigurationChangedAfterPatchCollection()
    {
        using var fixture = new Fixture();
        fixture.Runner.Configurations = ["", "", "diff.algorithm"];

        await AssertCauseAsync(() => fixture.ReadAsync(), "identity-mismatch");
        Assert.Equal(3, fixture.Runner.ConfigurationReads);
    }

    [Fact]
    public async Task MetadataSnapshotRefusesAChangedButOtherwiseNeutralizedConfiguration()
    {
        using var fixture = new Fixture();
        fixture.Runner.Configurations = ["diff.algorithm", "diff.context"];
        await fixture.Context.InitializeAsync(CancellationToken.None);

        await AssertCauseAsync(() => fixture.Context.VerifyMetadataAsync(CancellationToken.None), "identity-mismatch");
        Assert.Equal(2, fixture.Runner.ConfigurationReads);
    }

    [Theory]
    [InlineData("git version 2.46", "2.46")]
    [InlineData("git version 2.54.0 (Apple Git-157)", "2.54.0")]
    [InlineData("git version 2.46.2.windows.1", "2.46.2.windows.1")]
    public async Task InitializeRetainsTheSupportedGitVersionSpelling(string version, string expected)
    {
        using var fixture = new Fixture();
        fixture.Runner.GitVersion = version;

        await fixture.Context.InitializeAsync(CancellationToken.None);

        Assert.Equal(expected, fixture.Context.GitVersion);
    }

    [Theory]
    [InlineData("exit-code", "unsupported-diff")]
    [InlineData("stderr", "unsupported-diff")]
    [InlineData("truncated-output", "truncated-diff")]
    [InlineData("invalid-utf8", "malformed-diff")]
    public async Task RunAsyncClosesChildOutputFailuresToEvidenceCauses(string failure, string expectedCause)
    {
        using var fixture = new Fixture();
        fixture.Runner.FailureToken = "probe";
        fixture.Runner.Failure = failure;

        await AssertCauseAsync(() => fixture.Context.RunAsync(["probe"], CancellationToken.None), expectedCause);
    }

    [Fact]
    public async Task RunAsyncUsesCombinedOutputOnlyWhenSeparateStandardOutputIsAbsent()
    {
        using var fixture = new Fixture();
        fixture.Runner.FailureToken = "probe";
        fixture.Runner.Failure = "null-standard-output";

        Assert.Equal("fallback output", await fixture.Context.RunAsync(["probe"], CancellationToken.None));
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
            var environment = CoverageFanInDiffContext.ChildEnvironment(HeadCommit);
            Assert.True(environment.TryGetValue(lookupName, out var removed));
            Assert.Null(removed);

            using var fixture = new Fixture();
            await fixture.Context.InitializeAsync(CancellationToken.None);

            var overrides = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(fixture.Runner.Requests[0].EnvironmentOverrides);
            var removal = Assert.Single(overrides, entry => string.Equals(entry.Key, inheritedName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Assert.Null(removal.Value);
            Assert.Equal("temporary-value", Environment.GetEnvironmentVariable(inheritedName));
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
        internal string[]? Configurations { get; set; }
        internal int ConfigurationReads { get; private set; }
        internal string GitVersion { get; set; } = "git version 2.46.0";
        internal string Shallow { get; set; } = "false";
        internal string ObjectFormat { get; set; } = "sha1";
        internal string MergeBases { get; set; } = BaseCommit + "\n";
        internal string Raw { get; set; } = $":100644 100644 {OldBlob} {NewBlob} M\0source.cs\0";
        internal string BaseObjectType { get; set; } = "commit";
        internal bool BaseObjectMissing { get; set; }
        internal string? BatchCorruption { get; set; }
        internal bool TruncatePatch { get; set; }
        internal string? FailureToken { get; set; }
        internal string Failure { get; set; } = "exit-code";

        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var args = request.Arguments;
            if (FailureToken is { } token && args.Contains(token, StringComparer.Ordinal))
            {
                if (Failure == "invalid-utf8") throw new DecoderFallbackException("invalid child encoding");
                if (Failure == "null-standard-output")
                    return Task.FromResult(new CoverageRunProcessResult(0, "fallback output", StandardOutput: null));
                if (Failure == "stderr")
                    return Task.FromResult(new CoverageRunProcessResult(0, "probe output", StandardOutput: "probe output")
                    { StandardError = "child diagnostic" });
                if (Failure == "truncated-output")
                    return Task.FromResult(new CoverageRunProcessResult(0, "partial", OutputTruncated: true));
                return Task.FromResult(new CoverageRunProcessResult(2, "probe output"));
            }
            if (args.Contains("--version", StringComparer.Ordinal)) return Task.FromResult(Result(GitVersion + "\n"));
            if (args.Contains("config", StringComparer.Ordinal))
            {
                var index = ConfigurationReads++;
                var configuration = Configurations is { } sequence && index < sequence.Length
                    ? sequence[index] : Configuration;
                return Task.FromResult(Result(configuration, configuration.Length == 0 ? 1 : 0));
            }
            if (args.Contains("--is-shallow-repository", StringComparer.Ordinal)) return Task.FromResult(Result(Shallow + "\n"));
            if (args.Contains("--git-common-dir", StringComparer.Ordinal)) return Task.FromResult(Result(".git\n"));
            if (args.Contains("--show-object-format", StringComparer.Ordinal)) return Task.FromResult(Result(ObjectFormat + "\n"));
            if (args.Contains("merge-base", StringComparer.Ordinal)) return Task.FromResult(Result(MergeBases));
            if (args.Contains("cat-file", StringComparer.Ordinal) && args.Contains("-t", StringComparer.Ordinal))
                return Task.FromResult(Result(BaseObjectMissing ? string.Empty : BaseObjectType + "\n", BaseObjectMissing ? 1 : 0));
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
