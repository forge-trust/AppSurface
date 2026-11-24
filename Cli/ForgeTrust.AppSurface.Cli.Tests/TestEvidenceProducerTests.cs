using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class TestEvidenceProducerTests
{
    private const string ProducerId = "tooling-tests";
    private const string SecretTestName = "Zeta.Hidden.CaseName";

    [Fact]
    public async Task Complete_ShouldCloseTheAssertionAndWriteTheReceipt()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj", "tests/B/B.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One", SecretTestName];
        fixture.Source.Results["tests/A/A.csproj"] = [Pass("A.One"), Pass(SecretTestName)];
        fixture.Source.Inventory["tests/B/B.csproj"] = ["B.One"];
        fixture.Source.Results["tests/B/B.csproj"] = [Pass("B.One")];

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal([EvidenceTestProducerContract.AssertionId], result.SatisfiedAssertionIds);
        Assert.Equal("2 project(s), 3 discovered, 3 executed. No coverage claim was made.", result.Diagnostic);
        var artifact = Assert.Single(result.Artifacts!);
        Assert.Equal("test/test-receipt.json", artifact.RelativePath);
        Assert.Equal(EvidenceArtifactFileStatus.Valid, await EvidenceArtifactFileValidation.CheckAsync(fixture.ArtifactRoot, ProducerId, artifact));
        using var receipt = fixture.ReadReceipt();
        Assert.Equal(1, receipt.RootElement.GetProperty("schemaVersion").GetInt32());
        var first = receipt.RootElement.GetProperty("projects")[0];
        Assert.Equal("tests/A/A.csproj", first.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("filterSha256").ValueKind);
        Assert.Equal(1, first.GetProperty("minimumTests").GetInt32());
        Assert.Equal(2, first.GetProperty("discovered").GetInt32());
        Assert.Equal(2, first.GetProperty("executed").GetInt32());
        Assert.Equal(2, first.GetProperty("passed").GetInt32());
        Assert.Equal(0, first.GetProperty("failed").GetInt32());
        Assert.Equal(0, first.GetProperty("skipped").GetInt32());
        Assert.Equal("Complete", first.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("subReason").ValueKind);
        Assert.Equal(
            ["evaluate:tests/A/A.csproj", "evaluate:tests/B/B.csproj", "restore:tests/A/A.csproj", "build:tests/A/A.csproj", "discover:tests/A/A.csproj", "run:tests/A/A.csproj", "restore:tests/B/B.csproj", "build:tests/B/B.csproj", "discover:tests/B/B.csproj", "run:tests/B/B.csproj"],
            fixture.Source.Calls);
        Assert.Equal("[1/2] evaluating", fixture.ProgressLines[0]);
        Assert.Contains("[1/2] running 2 tests", fixture.ProgressLines);
        fixture.AssertNoTestNameLeaked(result);
    }

    [Fact]
    public async Task EveryProjectIsAttempted_AndTheMostSevereOutcomeWins()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj", "tests/Missing/Missing.csproj", "tests/C/C.csproj", "tests/D/D.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One", "A.Two"];
        fixture.Source.Results["tests/A/A.csproj"] = [Pass("A.One"), new TestCaseResult("A.Two", TestCaseOutcome.Failed)];
        File.Delete(Path.Join(fixture.Repository, "tests", "Missing", "Missing.csproj"));
        fixture.Source.Failures["discover:tests/C/C.csproj"] = (TestRunReason.DiscoveryFailed, "ListingShapeUnrecognized");
        fixture.Source.Inventory["tests/D/D.csproj"] = ["D.One"];
        fixture.Source.Results["tests/D/D.csproj"] = [Pass("D.One")];

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal("4 project(s), 3 discovered, 3 executed. 1 project(s) with failing tests. No coverage claim was made.", result.Diagnostic);
        Assert.Equal(
            [TestRunReason.TestsFailed, TestRunReason.ProjectNotFound, TestRunReason.DiscoveryFailed, TestRunReason.Complete],
            fixture.Producer.LastProjects.Select(static project => project.Reason));
        Assert.Equal("preflight", fixture.Producer.LastProjects[1].Phase);
        Assert.Contains("policy error", fixture.Producer.LastProjects[1].Detail, StringComparison.Ordinal);
        Assert.Equal("ListingShapeUnrecognized", fixture.Producer.LastProjects[2].SubReason);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal("ListingShapeUnrecognized", receipt.RootElement.GetProperty("projects")[2].GetProperty("subReason").GetString());
        Assert.DoesNotContain(fixture.Source.Calls, static call => call.EndsWith("Missing.csproj", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("evaluate", TestRunReason.ToolchainUnavailable, EvidenceProducerOutcome.Unavailable)]
    [InlineData("evaluate", TestRunReason.MultiTargetUnsupported, EvidenceProducerOutcome.Unavailable)]
    [InlineData("evaluate", TestRunReason.RunSettingsUnsupported, EvidenceProducerOutcome.Unavailable)]
    [InlineData("evaluate", TestRunReason.UnsupportedRunner, EvidenceProducerOutcome.Unavailable)]
    [InlineData("restore", TestRunReason.RestoreFailed, EvidenceProducerOutcome.Unavailable)]
    [InlineData("build", TestRunReason.BuildFailed, EvidenceProducerOutcome.Failed)]
    [InlineData("discover", TestRunReason.DiscoveryFailed, EvidenceProducerOutcome.Unavailable)]
    [InlineData("run", TestRunReason.RunAborted, EvidenceProducerOutcome.Failed)]
    [InlineData("run", TestRunReason.ResultsMissingOrStale, EvidenceProducerOutcome.Unavailable)]
    internal async Task StepFailure_ShouldStopThatProjectWithItsReasonAndCommand(string phase, TestRunReason reason, EvidenceProducerOutcome outcome)
    {
        using var fixture = Fixture.Create("tests/A/A.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One"];
        fixture.Source.Failures[$"{phase}:tests/A/A.csproj"] = (reason, null);

        var result = await fixture.ProduceAsync();

        Assert.Equal(outcome, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        var project = Assert.Single(fixture.Producer.LastProjects);
        Assert.Equal(reason, project.Reason);
        Assert.Equal(phase, project.Phase);
        Assert.Equal(["dotnet", phase, "tests/A/A.csproj"], project.Command);
        Assert.Equal($"{phase}:tests/A/A.csproj", fixture.Source.Calls[^1]);
        Assert.Equal(phase == "run" ? 1 : 0, project.Counts.Discovered);
        Assert.Single(result.Artifacts!);
    }

    [Fact]
    public async Task ZeroMatch_ShouldSkipTheRunPass()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = [];

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Equal(TestRunReason.EmptyDiscovery, Assert.Single(fixture.Producer.LastProjects).Reason);
        Assert.DoesNotContain("run:tests/A/A.csproj", fixture.Source.Calls);
    }

    [Fact]
    public async Task MinimumSkippedAndFilterHash_ShouldReachTheReceipt()
    {
        using var fixture = Fixture.Create(
            new EvidenceTestProject("tests/A/A.csproj") { Filter = "FullyQualifiedName~A", MinimumTests = 3 },
            new EvidenceTestProject("tests/B/B.csproj"));
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One", "A.Two"];
        fixture.Source.Results["tests/A/A.csproj"] = [Pass("A.One"), Pass("A.Two")];
        fixture.Source.Inventory["tests/B/B.csproj"] = ["B.One", "B.Two"];
        fixture.Source.Results["tests/B/B.csproj"] = [Pass("B.One"), new TestCaseResult("B.Two", TestCaseOutcome.Skipped)];

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        using var receipt = fixture.ReadReceipt();
        var first = receipt.RootElement.GetProperty("projects")[0];
        Assert.Equal("BelowMinimum", first.GetProperty("reason").GetString());
        Assert.Equal(3, first.GetProperty("minimumTests").GetInt32());
        Assert.Equal("c8a0c6c9c7b5a0d1a0c6f6f6ad0be8a1c0bd4f0d0b3b1bbd1c2ec1e0a4b0b2a8".Length, first.GetProperty("filterSha256").GetString()!.Length);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("FullyQualifiedName~A"))).ToLowerInvariant(),
            first.GetProperty("filterSha256").GetString());
        var second = receipt.RootElement.GetProperty("projects")[1];
        Assert.Equal("TestsSkipped", second.GetProperty("reason").GetString());
        Assert.Equal(1, second.GetProperty("skipped").GetInt32());
        Assert.Equal(1, second.GetProperty("executed").GetInt32());
        Assert.DoesNotContain("FullyQualifiedName~A", File.ReadAllText(fixture.ReceiptPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalDeadline_ShouldMarkTheProjectInFlightAndTheRest_AndStillWriteTheReceipt()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj", "tests/B/B.csproj", "tests/C/C.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One"];
        fixture.Source.Results["tests/A/A.csproj"] = [Pass("A.One")];
        fixture.Source.HangAt = "build:tests/B/B.csproj";
        fixture.Source.OnHang = fixture.Clock.FireTimers;

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(
            [TestRunReason.Complete, TestRunReason.DeadlineExceeded, TestRunReason.NotRun],
            fixture.Producer.LastProjects.Select(static project => project.Reason));
        Assert.Equal("deadline", fixture.Producer.LastProjects[1].Phase);
        Assert.Single(result.Artifacts!);
        Assert.Equal(TimeSpan.FromSeconds(870), Assert.Single(fixture.Clock.TimerDelays));
        Assert.False(Directory.Exists(fixture.Scratch));
    }

    [Fact]
    public async Task InternalDeadlineDuringTheRun_ShouldKeepTheDiscoveredCount()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj", "tests/B/B.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One", "A.Two", "A.Three", "A.Four"];
        fixture.Source.HangAt = "run:tests/A/A.csproj";
        fixture.Source.OnHang = fixture.Clock.FireTimers;

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
        Assert.Equal(new TestRunCounts(4, 0, 0, 0, 0), fixture.Producer.LastProjects[0].Counts);
        Assert.Equal(TestRunCounts.Zero, fixture.Producer.LastProjects[1].Counts);
        Assert.Equal("2 project(s), 4 discovered, 0 executed. No coverage claim was made.", result.Diagnostic);
        using var receipt = fixture.ReadReceipt();
        Assert.Equal(4, receipt.RootElement.GetProperty("projects")[0].GetProperty("discovered").GetInt32());
        Assert.Equal("DeadlineExceeded", receipt.RootElement.GetProperty("projects")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InternalDeadlineDuringEvaluation_ShouldLeaveUnreachedProjectsNotRun()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj", "tests/B/B.csproj");
        fixture.Source.HangAt = "evaluate:tests/A/A.csproj";
        fixture.Source.OnHang = fixture.Clock.FireTimers;

        await fixture.ProduceAsync();

        Assert.Equal([TestRunReason.DeadlineExceeded, TestRunReason.NotRun], fixture.Producer.LastProjects.Select(static project => project.Reason));
    }

    [Theory]
    [InlineData(30, 27)]
    [InlineData(31, 27)]
    [InlineData(100, 90)]
    [InlineData(299, 269)]
    [InlineData(900, 870)]
    [InlineData(5400, 5370)]
    [InlineData(1, 1)]
    public void InternalDeadline_ShouldReserveOneTenthRoundedUpBetweenOneAndThirtySeconds(int timeoutSeconds, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TestEvidenceProducer.GetInternalDeadline(timeoutSeconds));

    [Fact]
    public async Task Cancellation_ShouldPropagateAndDeleteScratch()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj");
        using var cancellation = new CancellationTokenSource();
        fixture.Source.HangAt = "restore:tests/A/A.csproj";
        fixture.Source.OnHang = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ProduceAsync(cancellation.Token));

        Assert.False(Directory.Exists(fixture.Scratch));
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task UnhandledSourceError_ShouldWriteATraceWithoutMessagesAndReturnUnavailable()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj");
        fixture.Source.ThrowAt = "build:tests/A/A.csproj";

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        var trace = File.ReadAllText(fixture.Producer.LastTracePath!);
        Assert.StartsWith("System.InvalidOperationException", trace, StringComparison.Ordinal);
        Assert.Contains("   at ", trace, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretTestName, trace, StringComparison.Ordinal);
        Assert.StartsWith(Path.Join(fixture.Output, "diagnostics"), fixture.Producer.LastTracePath, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Scratch));
    }

    [Fact]
    public async Task UnhandledSourceError_WithoutADiagnosticsDirectory_ShouldWriteNoTrace()
    {
        using var fixture = Fixture.Create(diagnostics: false, "tests/A/A.csproj");
        fixture.Source.ThrowAt = "evaluate:tests/A/A.csproj";

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Null(fixture.Producer.LastTracePath);
    }

    [Fact]
    public async Task ScratchDeletionFailure_ShouldNotChangeTheOutcome()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = Fixture.Create("tests/A/A.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One"];
        fixture.Source.Results["tests/A/A.csproj"] = [Pass("A.One")];
        var locked = Path.Join(fixture.Scratch, "locked");
        fixture.Source.OnRun = () =>
        {
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Join(locked, "file"), "x");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
        };
        try
        {
            var result = await fixture.ProduceAsync();

            Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
            Assert.True(Directory.Exists(locked));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task ProjectLinkedOutsideTheRepository_ShouldBeProjectNotFound()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = Fixture.Create("tests/A/A.csproj");
        var outside = Path.Join(fixture.Root, "outside", "A.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "<Project />");
        var inside = Path.Join(fixture.Repository, "tests", "A", "A.csproj");
        File.Delete(inside);
        File.CreateSymbolicLink(inside, outside);

        var result = await fixture.ProduceAsync();

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Equal(TestRunReason.ProjectNotFound, Assert.Single(fixture.Producer.LastProjects).Reason);
        Assert.Empty(fixture.Source.Calls);
    }

    [Fact]
    public async Task ProjectLinkedInsideTheRepository_ShouldBeAccepted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = Fixture.Create("tests/A/A.csproj", "tests/Real/Real.csproj");
        var link = Path.Join(fixture.Repository, "tests", "A", "A.csproj");
        File.Delete(link);
        File.CreateSymbolicLink(link, Path.Join(fixture.Repository, "tests", "Real", "Real.csproj"));

        await fixture.ProduceAsync();

        Assert.Contains("evaluate:tests/A/A.csproj", fixture.Source.Calls);
    }

    [Fact]
    public async Task EvidenceHost_ShouldCloseTheObligationWithTheProducerReceipt()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One"];
        fixture.Source.Results["tests/A/A.csproj"] = [Pass("A.One")];
        await using var host = EvidenceHostBootstrap.Create(
            fixture.Plan,
            registration => registration.AddProducer(fixture.Producer),
            new EvidenceHostOptions(ArtifactDirectory: fixture.ArtifactRoot),
            fixture.Clock);

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(["tests-complete"], manifest.ClosedObligationIds);
        var artifact = Assert.Single(Assert.Single(manifest.ProducerResults).Artifacts!);
        Assert.Equal(EvidenceArtifactFileStatus.Valid, await EvidenceArtifactFileValidation.CheckAsync(fixture.ArtifactRoot, ProducerId, artifact));
    }

    /// <summary>
    /// When a host enforces the declared timeout before the producer's own deadline fires, there is no receipt:
    /// the result is TimedOut, the required slot is missing, and the claim is never gate-eligible.
    /// </summary>
    [Fact]
    public async Task EvidenceHostDeadlineFiringFirst_ShouldNeverYieldAGateEligibleClaim()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj");
        fixture.Source.HangAt = "restore:tests/A/A.csproj";
        var declaration = fixture.Plan.Profile.Producers[0] with { TimeoutSeconds = 1 };
        var plan = fixture.Plan with { Profile = fixture.Plan.Profile with { Producers = [declaration] } };
        await using var host = EvidenceHostBootstrap.Create(
            plan,
            registration => registration.AddProducer(fixture.Producer),
            new EvidenceHostOptions(ArtifactDirectory: fixture.ArtifactRoot),
            fixture.Clock);

        var manifest = await host.RunAsync();

        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
        Assert.Contains("deadline", result.Diagnostic, StringComparison.Ordinal);
        Assert.Null(result.Artifacts);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        Assert.Empty(manifest.ClosedObligationIds);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task MissingArtifactWriter_ShouldReturnNoArtifacts()
    {
        using var fixture = Fixture.Create("tests/A/A.csproj");
        fixture.Source.Inventory["tests/A/A.csproj"] = ["A.One"];
        fixture.Source.Results["tests/A/A.csproj"] = [Pass("A.One")];

        var result = await fixture.ProduceAsync(withArtifacts: false);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Null(result.Artifacts);
    }

    [Fact]
    public async Task Constructor_AndContext_ShouldBeValidated()
    {
        var options = new TestEvidenceProducerOptions("/repo");
        using var fixture = Fixture.Create("tests/A/A.csproj");

        Assert.ThrowsAny<ArgumentException>(() => new TestEvidenceProducer(" ", options, fixture.Source));
        Assert.Throws<ArgumentNullException>(() => new TestEvidenceProducer("id", null!, fixture.Source));
        Assert.Throws<ArgumentNullException>(() => new TestEvidenceProducer("id", options, null!));
        Assert.ThrowsAny<ArgumentException>(() => new TestEvidenceProducer("id", new TestEvidenceProducerOptions(" "), fixture.Source));
        Assert.Equal("id", new TestEvidenceProducer("id", options).Id);
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Producer.ProduceAsync(null!, CancellationToken.None).AsTask());
        var withoutBlock = fixture.Context(true) with { Producer = fixture.Context(true).Producer with { Test = null } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Producer.ProduceAsync(withoutBlock, CancellationToken.None).AsTask());
    }

    private static TestCaseResult Pass(string name) => new(name, TestCaseOutcome.Passed);

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory;
        private readonly StringWriter _progress = new();
        private readonly EvidencePlan _plan;

        private Fixture(TestDirectory directory, bool diagnostics, EvidenceTestProject[] projects)
        {
            _directory = directory;
            Root = Path.GetFullPath(directory.Path);
            Repository = Path.Join(Root, "repo");
            Output = Path.Join(Root, "output");
            Scratch = Path.Join(Root, "scratch");
            foreach (var path in projects.Select(project => TestPathUtils.PathUnder(Repository, project.Path)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "<Project />");
            }

            var declaration = new EvidenceProducerDeclaration(
                ProducerId,
                EvidenceTestProducerContract.Kind,
                "1.0.0",
                [],
                [EvidenceTestProducerContract.AssertionId],
                [new EvidenceArtifactSlot(EvidenceTestProducerContract.ReceiptSlotLogicalName, "test", EvidenceTestProducerContract.ReceiptMediaType, true, 65536)],
                900)
            {
                Test = new EvidenceTestDeclaration(projects),
            };
            var policy = new EvidencePolicy(
                "sample",
                "1",
                "tests",
                [
                    new EvidenceProfile(
                        "tests",
                        EvidenceProfileScope.Targeted,
                        [],
                        [declaration],
                        [new EvidenceObligation("tests-complete", "tooling", "Selected tests ran and passed.", [ProducerId], EvidenceTestProducerContract.AssertionId)]),
                ],
                []);
            _plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("scripts/build.sh")]);
            Producer = new TestEvidenceProducer(
                ProducerId,
                new TestEvidenceProducerOptions(Repository)
                {
                    ScratchRoot = Scratch,
                    DiagnosticsDirectory = diagnostics ? Path.Join(Output, "diagnostics") : null,
                    Progress = _progress,
                },
                Source);
        }

        public string Root { get; }

        public string Repository { get; }

        public string Output { get; }

        public string Scratch { get; }

        public string ArtifactRoot => Path.Join(Output, "artifacts");

        public string ReceiptPath => Path.Join(ArtifactRoot, ProducerId, "test", "test-receipt.json");

        public FakeSource Source { get; } = new();

        public ManualTimeProvider Clock { get; } = new();

        public TestEvidenceProducer Producer { get; }

        public EvidencePlan Plan => _plan;

        public string[] ProgressLines => _progress.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        public static Fixture Create(params string[] projectPaths) => Create(true, projectPaths);

        public static Fixture Create(bool diagnostics, params string[] projectPaths) =>
            new(TestDirectory.Create(), diagnostics, [.. projectPaths.Select(static path => new EvidenceTestProject(path))]);

        public static Fixture Create(params EvidenceTestProject[] projects) => new(TestDirectory.Create(), true, projects);

        public EvidenceProducerContext Context(bool withArtifacts)
        {
            var declaration = _plan.Profile.Producers[0];
            return new EvidenceProducerContext(
                _plan,
                declaration,
                Clock,
                withArtifacts ? new EvidenceArtifactWriter(declaration, Path.Join(ArtifactRoot, ProducerId)) : null);
        }

        public Task<EvidenceProducerResult> ProduceAsync(CancellationToken cancellationToken = default, bool withArtifacts = true) =>
            Producer.ProduceAsync(Context(withArtifacts), cancellationToken).AsTask();

        public JsonDocument ReadReceipt() => JsonDocument.Parse(File.ReadAllBytes(ReceiptPath));

        public void AssertNoTestNameLeaked(EvidenceProducerResult result)
        {
            foreach (var file in Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(SecretTestName, File.ReadAllText(file), StringComparison.Ordinal);
            }

            Assert.DoesNotContain(SecretTestName, _progress.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(SecretTestName, result.Diagnostic, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Scratch));
        }

        public void Dispose() => _directory.Dispose();
    }

    private sealed class FakeSource : ITestRunSource
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, (TestRunReason Reason, string? SubReason)> Failures { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string[]> Inventory { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TestCaseResult[]> Results { get; } = new(StringComparer.Ordinal);

        public string? HangAt { get; set; }

        public string? ThrowAt { get; set; }

        public Action? OnHang { get; set; }

        public Action? OnRun { get; set; }

        public Task<TestRunStep> EvaluateAsync(TestRunProject project, CancellationToken cancellationToken) => StepAsync("evaluate", project, cancellationToken);

        public Task<TestRunStep> RestoreAsync(TestRunProject project, CancellationToken cancellationToken) => StepAsync("restore", project, cancellationToken);

        public Task<TestRunStep> BuildAsync(TestRunProject project, CancellationToken cancellationToken) => StepAsync("build", project, cancellationToken);

        public async Task<TestRunInventory> DiscoverAsync(TestRunProject project, CancellationToken cancellationToken)
        {
            var step = await StepAsync("discover", project, cancellationToken);
            return new TestRunInventory(step, step.Failure is null ? Inventory.GetValueOrDefault(project.DeclaredPath, []) : []);
        }

        public async Task<TestRunResults> RunAsync(TestRunProject project, string resultsDirectory, CancellationToken cancellationToken)
        {
            OnRun?.Invoke();
            var step = await StepAsync("run", project, cancellationToken);
            return new TestRunResults(step, step.Failure is null ? Results.GetValueOrDefault(project.DeclaredPath, []) : []);
        }

        private async Task<TestRunStep> StepAsync(string phase, TestRunProject project, CancellationToken cancellationToken)
        {
            var key = $"{phase}:{project.DeclaredPath}";
            Calls.Add(key);
            if (string.Equals(ThrowAt, key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"boom while handling {SecretTestName}");
            }

            if (string.Equals(HangAt, key, StringComparison.Ordinal))
            {
                OnHang?.Invoke();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var step = new TestRunStep(phase, ["dotnet", phase, project.DeclaredPath]);
            return Failures.TryGetValue(key, out var failure) ? step with { Failure = failure.Reason, SubReason = failure.SubReason } : step;
        }
    }

    /// <summary>A clock whose timers fire only when the test says so.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public List<TimeSpan> TimerDelays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    TimerDelays.Add(dueTime);
                }
            }

            return timer;
        }

        public void FireTimers()
        {
            ManualTimer[] timers;
            lock (_timers)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
