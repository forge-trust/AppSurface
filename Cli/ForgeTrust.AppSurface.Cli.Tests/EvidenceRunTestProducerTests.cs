using System.Text.Json;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceRunTestProducerTests
{
    private const string ProducerId = "tooling-tests";
    private const string ProjectPath = "tests/A/A.csproj";
    private const string HiddenTestName = "Zeta.Hidden.CaseName";

    [Fact]
    public async Task Run_ShouldCloseTheAssertionPrintTheCountsTableAndVerify()
    {
        using var fixture = Fixture.Create();
        fixture.Source.Inventory = ["A.One", HiddenTestName];
        fixture.Source.Results = [new("A.One", TestCaseOutcome.Passed), new(HiddenTestName, TestCaseOutcome.Passed)];
        var stepSummary = Path.Join(fixture.Root, "step-summary.md");
        fixture.StepSummaryPath = stepSummary;
        await fixture.RunAsync();

        var output = fixture.Console.ReadOutputString();
        Assert.Contains(TestEvidenceRunSupport.PreviewLine, output, StringComparison.Ordinal);
        Assert.Contains("Evidence result: TargetedComplete", output, StringComparison.Ordinal);
        Assert.Contains("| Project | Discovered | Executed | Passed | Failed | Skipped | Reason |", output, StringComparison.Ordinal);
        Assert.Contains("| tests/A/A.csproj | 2 | 2 | 2 | 0 | 0 | Complete |", output, StringComparison.Ordinal);
        Assert.Contains("No coverage claim was made.", output, StringComparison.Ordinal);
        Assert.Contains("declares no minimumTests", output, StringComparison.Ordinal);
        Assert.Contains("[1/1] running 2 tests", fixture.Console.ReadErrorString(), StringComparison.Ordinal);
        var summary = File.ReadAllText(stepSummary);
        Assert.Contains("| tests/A/A.csproj | 2 | 2 | 2 | 0 | 0 | Complete |", summary, StringComparison.Ordinal);
        Assert.Contains("No coverage claim was made.", summary, StringComparison.Ordinal);
        var verified = await fixture.VerifyAsync();
        Assert.Equal(EvidenceClaimKind.TargetedComplete, verified.ClaimKind);
        Assert.Equal(["tests-complete"], verified.ClosedObligationIds);
        fixture.AssertNoHiddenName(output + fixture.Console.ReadErrorString() + summary);
    }

    [Fact]
    public async Task IncompleteRun_ShouldFailWithReasonsNextActionsAndRerunHint()
    {
        using var fixture = Fixture.Create(filter: "Name=a b|Name=c\\&d");
        fixture.Source.Inventory = ["A.One", HiddenTestName];
        fixture.Source.Results = [new("A.One", TestCaseOutcome.Passed), new(HiddenTestName, TestCaseOutcome.Failed)];

        var exception = await Assert.ThrowsAsync<CommandException>(() => fixture.RunAsync());

        Assert.StartsWith("ASEVD211", exception.Message, StringComparison.Ordinal);
        Assert.Contains("test producer output is not kept", exception.Message, StringComparison.Ordinal);
        var error = fixture.Console.ReadErrorString();
        Assert.Contains("evidence run: test producer 'tooling-tests' is incomplete for tests/A/A.csproj (1 of 1 project(s) incomplete, 1 with failing tests).", error, StringComparison.Ordinal);
        Assert.Contains("- producer=tooling-tests project=tests/A/A.csproj phase=reconcile reason=TestsFailed", error, StringComparison.Ordinal);
        Assert.Contains("  next: Run the printed command and fix the failing tests.", error, StringComparison.Ordinal);
        Assert.Contains("  docs: Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#reason-testsfailed", error, StringComparison.Ordinal);
        Assert.Contains("    Name=a b|Name=c\\&d", error, StringComparison.Ordinal);
        Assert.Contains("'Name=a b|Name=c\\&d'", error, StringComparison.Ordinal);
        Assert.Contains("| tests/A/A.csproj | 2 | 2 | 1 | 1 | 0 | TestsFailed |", fixture.Console.ReadOutputString(), StringComparison.Ordinal);
        var verified = await fixture.VerifyAsync();
        Assert.Equal(EvidenceClaimKind.None, verified.ClaimKind);
        fixture.AssertNoHiddenName(error + fixture.Console.ReadOutputString() + exception.Message);
    }

    [Fact]
    public async Task UnknownKind_ShouldStayUnavailableWithTheExistingMessage()
    {
        using var fixture = Fixture.Create(kind: "browser-e2e");

        var exception = await Assert.ThrowsAsync<CommandException>(() => fixture.RunAsync());

        Assert.Contains("Inspect evidence-summary.json and the producer output", exception.Message, StringComparison.Ordinal);
        var result = Assert.Single((await fixture.VerifyAsync()).ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Equal("Producer kind 'browser-e2e' requires an explicit consumer EvidenceHost registration.", result.Diagnostic);
        Assert.DoesNotContain("Preview:", fixture.Console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Source.Calls);
    }

    [Fact]
    public async Task ResourceBackedProfile_ShouldBeRejectedBeforeAnythingIsWritten()
    {
        using var fixture = Fixture.Create(withResource: true);

        var exception = await Assert.ThrowsAsync<CommandException>(() => fixture.RunAsync());

        Assert.StartsWith("ASEVD214", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not available through a resource-backed profile", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public async Task CancelledRerun_ShouldLeaveNoEarlierManifestToVerify()
    {
        using var fixture = Fixture.Create();
        fixture.Source.Inventory = ["A.One"];
        fixture.Source.Results = [new("A.One", TestCaseOutcome.Passed)];
        await fixture.RunAsync();
        Assert.Equal(EvidenceClaimKind.TargetedComplete, (await fixture.VerifyAsync()).ClaimKind);

        fixture.Source.CancelAt = "restore";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(() => fixture.VerifyAsync());
        Assert.Equal("ASEVD208", exception.Code);
        Assert.False(File.Exists(Path.Join(fixture.Output, "artifacts", ProducerId, "test", "test-receipt.json")));
        Assert.True(File.Exists(Path.Join(fixture.Output, "evidence-plan.json")));
    }

    [Fact]
    public async Task InternalError_ShouldPrintTheTracePathAndTheUnreadableReceiptNote()
    {
        using var fixture = Fixture.Create();
        fixture.Source.ThrowAt = "build";

        await Assert.ThrowsAsync<CommandException>(() => fixture.RunAsync());

        Assert.Contains(Path.Join(fixture.Output, "diagnostics", "test-producer-trace-tooling-tests.txt"), fixture.Console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Contains("The receipt could not be read back", fixture.Console.ReadOutputString(), StringComparison.Ordinal);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, (await fixture.VerifyAsync()).ExecutionVerdict);
    }

    [Fact]
    public async Task Explain_ShouldListProjectsFiltersAndMinimumsWithThePreviewLine()
    {
        using var fixture = Fixture.Create(filter: "FullyQualifiedName~A", minimumTests: 7);
        var command = new EvidenceExplainCommand(new EvidenceCliWorkflow(new EvidencePlanner()))
        {
            PolicyPath = fixture.PolicyPath,
            Paths = ["scripts/build.sh"],
            OutputDirectory = fixture.Output,
        };

        await command.ExecuteAsync(fixture.Console);

        var output = fixture.Console.ReadOutputString();
        Assert.Contains("Test producer 'tooling-tests' (timeout 900s):", output, StringComparison.Ordinal);
        Assert.Contains("  tests/A/A.csproj | filter: FullyQualifiedName~A | minimumTests: 7", output, StringComparison.Ordinal);
        Assert.Contains(TestEvidenceRunSupport.PreviewLine, output, StringComparison.Ordinal);
        using var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Output, "evidence-summary.json")));
        var producer = summary.RootElement.GetProperty("testProducers")[0];
        Assert.Equal(ProducerId, producer.GetProperty("id").GetString());
        Assert.True(producer.GetProperty("preview").GetBoolean());
        Assert.Equal(ProjectPath, producer.GetProperty("projects")[0].GetProperty("path").GetString());
        Assert.Equal("FullyQualifiedName~A", producer.GetProperty("projects")[0].GetProperty("filter").GetString());
        Assert.Equal(7, producer.GetProperty("projects")[0].GetProperty("minimumTests").GetInt32());
        Assert.Empty(fixture.Source.Calls);
    }

    [Fact]
    public void FormatPlanAndSummary_ShouldOmitTestDetailsWithoutATestProducer()
    {
        var plan = Fixture.Plan(Fixture.Policy(kind: "coverage"));

        Assert.Empty(TestEvidenceRunSupport.FormatPlan(plan));
        Assert.Empty(TestEvidenceRunSupport.DescribePlan(plan));
        Assert.DoesNotContain("Preview", EvidenceCliWorkflow.FormatSummary(plan), StringComparison.Ordinal);
        Assert.Contains("filter: (none, every test) | minimumTests: 1", EvidenceCliWorkflow.FormatSummary(Fixture.Plan(Fixture.Policy())), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSelection_ShouldRejectCaseCollidingArtifactWriters()
    {
        var slot = new EvidenceArtifactSlot("report", "r", "text/plain", false, 10);
        var profile = new EvidenceProfile(
            "p",
            EvidenceProfileScope.Targeted,
            [],
            [
                new EvidenceProducerDeclaration("Writer", "custom", "1", [], ["a@1"], [slot], 60),
                new EvidenceProducerDeclaration("writer", "custom", "1", [], ["a@1"], [slot], 60),
                new EvidenceProducerDeclaration("WRITER", "custom", "1", [], ["a@1"], [], 60),
            ],
            [new EvidenceObligation("o", "r", "why", ["Writer"], "a@1")]);
        var plan = new EvidencePlanner().Resolve(new EvidencePolicy("s", "1", "p", [profile], []), [new NormalizedDiffPath("x")]);

        var exception = Assert.Throws<EvidenceCliException>(() => TestEvidenceRunSupport.ValidateSelection(plan));

        Assert.Equal("ASEVD215", exception.Code);
        Assert.Contains("'Writer' and 'writer'", exception.Message, StringComparison.Ordinal);
        var distinct = plan with { Profile = profile with { Producers = [profile.Producers[0], profile.Producers[2]] } };
        TestEvidenceRunSupport.ValidateSelection(distinct);
        Assert.Throws<ArgumentNullException>(() => TestEvidenceRunSupport.ValidateSelection(null!));
    }

    [Fact]
    public void ValidateReuseContext_ShouldRequireFreshExecutionForThePreviewProducer()
    {
        var plan = Fixture.Plan(Fixture.Policy());

        var exception = Assert.Throws<CommandException>(() =>
            EvidenceRunCommand.ValidateReuseContext(true, plan, _ => null));

        Assert.Contains("reuse.test_preview_requires_fresh", exception.Message, StringComparison.Ordinal);
        EvidenceRunCommand.ValidateReuseContext(false, plan, _ => null);
    }

    [Fact]
    public void RetireOutputs_ShouldDeleteOnlyTheManifestSummaryAndSelectedArtifactDirectories()
    {
        using var directory = TestDirectory.Create();
        var plan = Fixture.Plan(Fixture.Policy());
        var output = Path.Join(directory.Path, "evidence");
        Directory.CreateDirectory(Path.Join(output, "artifacts", ProducerId, "test"));
        Directory.CreateDirectory(Path.Join(output, "artifacts", "other-producer"));
        Directory.CreateDirectory(Path.Join(output, "coverage"));
        foreach (var name in new[] { "evidence-manifest.json", "evidence-summary.json", "evidence-plan.json" })
        {
            File.WriteAllText(Path.Join(output, name), "{}");
        }

        TestEvidenceRunSupport.RetireOutputs(Path.Join(directory.Path, "never-created"), plan);
        TestEvidenceRunSupport.RetireOutputs(output, plan);
        TestEvidenceRunSupport.RetireOutputs(output, plan);

        Assert.False(File.Exists(Path.Join(output, "evidence-manifest.json")));
        Assert.False(File.Exists(Path.Join(output, "evidence-summary.json")));
        Assert.True(File.Exists(Path.Join(output, "evidence-plan.json")));
        Assert.False(Directory.Exists(Path.Join(output, "artifacts", ProducerId)));
        Assert.True(Directory.Exists(Path.Join(output, "artifacts", "other-producer")));
        Assert.True(Directory.Exists(Path.Join(output, "coverage")));
    }

    /// <summary>
    /// Regression: a producer of another kind may have an id such as <c>../../victim</c>; retiring outputs must never
    /// join such an id into a path it deletes.
    /// </summary>
    [Fact]
    public void RetireOutputs_ShouldNeverDeleteThroughAnUnvalidatedProducerId()
    {
        using var directory = TestDirectory.Create();
        var output = Path.Join(directory.Path, "nested", "evidence");
        Directory.CreateDirectory(Path.Join(output, "artifacts"));
        var victim = Path.Join(directory.Path, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Join(victim, "keep.txt"), "keep");
        var slot = new EvidenceArtifactSlot("report", "r", "text/plain", false, 10);
        var profile = new EvidenceProfile(
            "p",
            EvidenceProfileScope.Targeted,
            [],
            [new EvidenceProducerDeclaration("../../../victim", "custom", "1", [], ["a@1"], [slot], 60)],
            [new EvidenceObligation("o", "r", "why", ["../../../victim"], "a@1")]);
        var plan = new EvidencePlanner().Resolve(new EvidencePolicy("s", "1", "p", [profile], []), [new NormalizedDiffPath("x")]);

        TestEvidenceRunSupport.RetireOutputs(output, plan);

        Assert.True(File.Exists(Path.Join(victim, "keep.txt")));
    }

    [Theory]
    [InlineData("artifacts")]
    [InlineData("producer")]
    public void RetireOutputs_ShouldRefuseToDeleteThroughALinkedDirectory(string linked)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var plan = Fixture.Plan(Fixture.Policy());
        var output = Path.Join(directory.Path, "evidence");
        var saved = Path.Join(directory.Path, "saved");
        Directory.CreateDirectory(Path.Join(saved, ProducerId));
        File.WriteAllText(Path.Join(saved, ProducerId, "keep.txt"), "keep");
        Directory.CreateDirectory(output);
        if (linked == "artifacts")
        {
            Directory.CreateSymbolicLink(Path.Join(output, "artifacts"), saved);
        }
        else
        {
            Directory.CreateDirectory(Path.Join(output, "artifacts"));
            Directory.CreateSymbolicLink(Path.Join(output, "artifacts", ProducerId), Path.Join(saved, ProducerId));
        }

        var exception = Assert.Throws<EvidenceCliException>(() => TestEvidenceRunSupport.RetireOutputs(output, plan));

        Assert.Equal("ASEVD216", exception.Code);
        Assert.True(File.Exists(Path.Join(saved, ProducerId, "keep.txt")));
    }

    /// <summary>
    /// Regression: link checks ran one producer at a time, so an earlier producer's directory, the manifest and the
    /// summary were already deleted when a later linked directory stopped the run.
    /// </summary>
    [Fact]
    public void RetireOutputs_ShouldDeleteNothingWhenALaterProducerDirectoryIsLinked()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var plan = Fixture.Plan(Fixture.Policy());
        var first = plan.Profile.Producers[0];
        plan = plan with { Profile = plan.Profile with { Producers = [first, first with { Id = "second-tests" }] } };
        var output = Path.Join(directory.Path, "evidence");
        var saved = Path.Join(directory.Path, "saved");
        Directory.CreateDirectory(saved);
        Directory.CreateDirectory(Path.Join(output, "artifacts", ProducerId));
        File.WriteAllText(Path.Join(output, "artifacts", ProducerId, "receipt.json"), "{}");
        File.WriteAllText(Path.Join(output, "evidence-manifest.json"), "{}");
        File.WriteAllText(Path.Join(output, "evidence-summary.json"), "{}");
        Directory.CreateSymbolicLink(Path.Join(output, "artifacts", "second-tests"), saved);

        var exception = Assert.Throws<EvidenceCliException>(() => TestEvidenceRunSupport.RetireOutputs(output, plan));

        Assert.Equal("ASEVD216", exception.Code);
        Assert.True(File.Exists(Path.Join(output, "artifacts", ProducerId, "receipt.json")));
        Assert.True(File.Exists(Path.Join(output, "evidence-manifest.json")));
        Assert.True(File.Exists(Path.Join(output, "evidence-summary.json")));
    }

    [Fact]
    public async Task EnforceArtifacts_ShouldDowngradeAProducerWhoseReceiptIsGone_SoTheObligationStaysOpen()
    {
        using var directory = TestDirectory.Create();
        var plan = Fixture.Plan(Fixture.Policy());
        var declaration = plan.Profile.Producers[0];
        var root = Path.Join(directory.Path, "artifacts");
        var writer = new EvidenceArtifactWriter(declaration, Path.Join(root, ProducerId));
        var artifact = await writer.WriteAsync(EvidenceTestProducerContract.ReceiptSlotLogicalName, TestEvidenceProducer.ReceiptRelativePath, "{}"u8.ToArray());
        var passed = new EvidenceProducerResult(ProducerId, EvidenceProducerOutcome.Passed, [EvidenceTestProducerContract.AssertionId], Artifacts: [artifact]);

        Assert.Same(passed, await TestEvidenceRunSupport.EnforceArtifactsAsync(root, passed, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Path.Join(root, ProducerId, "test"), "*.tmp-*"));
        File.Delete(Path.Join(root, ProducerId, "test", "test-receipt.json"));
        var downgraded = await TestEvidenceRunSupport.EnforceArtifactsAsync(root, passed, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, downgraded.Outcome);
        Assert.Empty(downgraded.SatisfiedAssertionIds);
        Assert.Null(downgraded.Artifacts);
        Assert.Contains("'test-receipt'", downgraded.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Rerun appsurface evidence run", downgraded.Diagnostic, StringComparison.Ordinal);
        var manifest = EvidenceManifestBuilder.Build(plan, [downgraded]);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Empty(manifest.ClosedObligationIds);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        var noArtifacts = new EvidenceProducerResult(ProducerId, EvidenceProducerOutcome.Failed, []);
        Assert.Same(noArtifacts, await TestEvidenceRunSupport.EnforceArtifactsAsync(root, noArtifacts, CancellationToken.None));
    }

    [Fact]
    public void TryReadReceipt_ShouldRejectMissingMalformedAndUnknownVersions()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Join(directory.Path, "receipt.json");

        Assert.False(TestEvidenceRunSupport.TryReadReceipt(path, out _));
        File.WriteAllText(path, "not json");
        Assert.False(TestEvidenceRunSupport.TryReadReceipt(path, out _));
        File.WriteAllText(path, """{ "schemaVersion": 2, "projects": [] }""");
        Assert.False(TestEvidenceRunSupport.TryReadReceipt(path, out _));
        File.WriteAllText(path, """{ "schemaVersion": 1 }""");
        Assert.False(TestEvidenceRunSupport.TryReadReceipt(path, out _));
        File.WriteAllText(path, """{ "schemaVersion": 1, "projects": [ { "path": 5 } ] }""");
        Assert.False(TestEvidenceRunSupport.TryReadReceipt(path, out _));
        File.WriteAllBytes(path, TestEvidenceProducer.SerializeReceipt(
        [
            new TestProjectOutcome("a|b.csproj", null, 1, new TestRunCounts(3, 2, 1, 1, 1), TestRunReason.DiscoveryFailed, "ExitCode", "discover", [], null),
        ]));
        Assert.True(TestEvidenceRunSupport.TryReadReceipt(path, out var rows));
        Assert.Equal([new TestReceiptRow("a|b.csproj", 3, 2, 1, 1, 1, "DiscoveryFailed/ExitCode")], rows);
        Assert.Contains("| a\\|b.csproj | 3 | 2 | 1 | 1 | 1 | DiscoveryFailed/ExitCode |", TestEvidenceRunSupport.FormatCountsTable("p", rows), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatIncomplete_ShouldBeEmptyWhenCompleteAndCarryDetailAndSubReasonOtherwise()
    {
        var complete = new TestProjectOutcome("a.csproj", null, 1, TestRunCounts.Zero, TestRunReason.Complete, null, "reconcile", [], null);
        var missing = new TestProjectOutcome("b.csproj", null, 1, TestRunCounts.Zero, TestRunReason.ProjectNotFound, null, "preflight", [], "policy error: missing");
        var listing = new TestProjectOutcome("c.csproj", null, 1, TestRunCounts.Zero, TestRunReason.DiscoveryFailed, "OutputTooLarge", "discover", ["dotnet", "test"], null);

        Assert.Empty(TestEvidenceRunSupport.FormatIncomplete("p", [complete]));
        var lines = TestEvidenceRunSupport.FormatIncomplete("p", [complete, missing, listing]);
        Assert.Equal("evidence run: test producer 'p' is incomplete for b.csproj (2 of 3 project(s) incomplete).", lines[0]);
        Assert.Contains("  detail: policy error: missing", lines);
        Assert.Contains("- producer=p project=c.csproj phase=discover reason=DiscoveryFailed/OutputTooLarge", lines);
        Assert.Contains("  as one POSIX shell line: dotnet test", lines);
        Assert.Throws<ArgumentNullException>(() => TestEvidenceRunSupport.FormatIncomplete("p", null!));
    }

    [Fact]
    public void FormatMinimumNotices_ShouldOnlyFlagUnsetMinimumsWithMoreThanOneTest()
    {
        EvidenceTestProject[] declared = [new("a.csproj"), new("b.csproj") { MinimumTests = 2 }, new("c.csproj")];
        TestProjectOutcome Outcome(string path, int discovered) =>
            new(path, null, 1, new TestRunCounts(discovered, discovered, discovered, 0, 0), TestRunReason.Complete, null, "reconcile", [], null);

        var notices = TestEvidenceRunSupport.FormatMinimumNotices(declared, [Outcome("a.csproj", 5), Outcome("b.csproj", 5), Outcome("c.csproj", 1)]);

        Assert.Equal("Notice: a.csproj discovered 5 tests and declares no minimumTests; a filter that starts matching fewer would still pass. Consider \"minimumTests\": 5.", Assert.Single(notices));
    }

    [Fact]
    public void FormatRerunHint_ShouldPrintAShellLineOnlyWhenEveryArgumentCanBeQuoted()
    {
        var safe = TestEvidenceRunSupport.FormatRerunHint(["dotnet", "test", "My Tests.csproj", "--filter", "Name=a b|Name=c&d", ""]);
        Assert.Equal("rerun (run from the project directory), one argument per line:", safe[0]);
        Assert.Contains("    My Tests.csproj", safe);
        Assert.Equal("as one POSIX shell line: dotnet test 'My Tests.csproj' --filter 'Name=a b|Name=c&d' ''", safe[^1]);

        var unsafeQuote = TestEvidenceRunSupport.FormatRerunHint(["dotnet", "test", "--filter", "Name=it's"]);
        Assert.DoesNotContain(unsafeQuote, static line => line.StartsWith("as one POSIX shell line", StringComparison.Ordinal));
        Assert.Contains("    Name=it's", unsafeQuote);
        Assert.Equal(
            "as one POSIX shell line: a-b_c.d/e:f=g,h Z9 'x y' 'a~b' 'a;b' 'a$b'",
            TestEvidenceRunSupport.FormatRerunHint(["a-b_c.d/e:f=g,h", "Z9", "x y", "a~b", "a;b", "a$b"])[^1]);
        Assert.DoesNotContain(
            TestEvidenceRunSupport.FormatRerunHint(["dotnet", "a\nb"]),
            static line => line.StartsWith("as one POSIX shell line", StringComparison.Ordinal));
        Assert.Empty(TestEvidenceRunSupport.FormatRerunHint([]));
        Assert.Throws<ArgumentNullException>(() => TestEvidenceRunSupport.FormatRerunHint(null!));
    }

    [Fact]
    public void EveryReason_ShouldHaveANextActionAndAnAnchor()
    {
        foreach (var reason in Enum.GetValues<TestRunReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TestEvidenceRunSupport.DescribeNextAction(reason)));
            Assert.Equal("reason-" + reason.ToString().ToLowerInvariant(), TestEvidenceRunSupport.Anchor(reason));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => TestEvidenceRunSupport.DescribeNextAction((TestRunReason)999));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory;

        private Fixture(TestDirectory directory, EvidencePolicy policy)
        {
            _directory = directory;
            Root = Path.GetFullPath(directory.Path);
            Output = Path.Join(Root, "evidence");
            PolicyPath = Path.Join(Root, "evidence.policy.json");
            File.WriteAllBytes(PolicyPath, EvidenceCanonicalJson.Serialize(policy));
            var project = TestPathUtils.PathUnder(Root, "repo", ProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project />");
        }

        public string Root { get; }

        public string Output { get; }

        public string PolicyPath { get; }

        public FakeSource Source { get; } = new();

        public string? StepSummaryPath { get; set; }

        public FakeInMemoryConsole Console { get; } = new();

        public static Fixture Create(string kind = "test", string? filter = null, int? minimumTests = null, bool withResource = false) =>
            new(TestDirectory.Create(), Policy(kind, filter, minimumTests, withResource));

        public static EvidencePolicy Policy(string kind = "test", string? filter = null, int? minimumTests = null, bool withResource = false)
        {
            var isTest = kind == EvidenceTestProducerContract.Kind;
            var declaration = new EvidenceProducerDeclaration(
                ProducerId,
                kind,
                "1.0.0",
                [],
                [EvidenceTestProducerContract.AssertionId],
                isTest ? [new EvidenceArtifactSlot(EvidenceTestProducerContract.ReceiptSlotLogicalName, "test", EvidenceTestProducerContract.ReceiptMediaType, true, 65536)] : [],
                900)
            {
                Test = isTest ? new EvidenceTestDeclaration([new EvidenceTestProject(ProjectPath) { Filter = filter, MinimumTests = minimumTests }]) : null,
            };
            return new EvidencePolicy(
                "sample",
                "1",
                "tests",
                [
                    new EvidenceProfile(
                        "tests",
                        EvidenceProfileScope.Targeted,
                        withResource ? [new EvidenceResourceDeclaration("db", "completion", 30, [])] : [],
                        [declaration],
                        [new EvidenceObligation("tests-complete", "tooling", "Selected tests ran and passed.", [ProducerId], EvidenceTestProducerContract.AssertionId)]),
                ],
                []);
        }

        public static EvidencePlan Plan(EvidencePolicy policy) => new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("scripts/build.sh")]);

        public async Task RunAsync()
        {
            var command = new EvidenceRunCommand(new EvidenceCliWorkflow(new EvidencePlanner()), CoverageEvidenceProducerTests.CreateUnusedProducer())
            {
                PolicyPath = PolicyPath,
                Paths = ["scripts/build.sh"],
                OutputDirectory = Output,
                RepositoryRootResolver = () => Path.Join(Root, "repo"),
                StepSummaryPathResolver = () => StepSummaryPath,
                TestProducerFactory = (id, options) => new TestEvidenceProducer(id, options with { ScratchRoot = Path.Join(Root, "scratch") }, Source),
            };
            await command.ExecuteAsync(Console);
        }

        public async Task<EvidenceManifest> VerifyAsync() =>
            (await new EvidenceCliWorkflow(new EvidencePlanner()).VerifyAsync(
                Path.Join(Output, "evidence-plan.json"),
                Path.Join(Output, "evidence-manifest.json"),
                CancellationToken.None)).Manifest;

        public void AssertNoHiddenName(string consoleText)
        {
            Assert.DoesNotContain(HiddenTestName, consoleText, StringComparison.Ordinal);
            foreach (var file in Directory.EnumerateFiles(Output, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(HiddenTestName, File.ReadAllText(file), StringComparison.Ordinal);
            }
        }

        public void Dispose()
        {
            Console.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class FakeSource : ITestRunSource
    {
        public List<string> Calls { get; } = [];

        public string[] Inventory { get; set; } = [];

        public TestCaseResult[] Results { get; set; } = [];

        public string? CancelAt { get; set; }

        public string? ThrowAt { get; set; }

        public Task<TestRunStep> EvaluateAsync(TestRunProject project, CancellationToken cancellationToken) => Step("evaluate", project);

        public Task<TestRunStep> RestoreAsync(TestRunProject project, CancellationToken cancellationToken) => Step("restore", project);

        public Task<TestRunStep> BuildAsync(TestRunProject project, CancellationToken cancellationToken) => Step("build", project);

        public async Task<TestRunInventory> DiscoverAsync(TestRunProject project, CancellationToken cancellationToken) =>
            new(await Step("discover", project), Inventory);

        public async Task<TestRunResults> RunAsync(TestRunProject project, string resultsDirectory, CancellationToken cancellationToken) =>
            new(await Step("run", project), Results);

        private Task<TestRunStep> Step(string phase, TestRunProject project)
        {
            Calls.Add(phase);
            if (CancelAt == phase)
            {
                throw new OperationCanceledException();
            }

            if (ThrowAt == phase)
            {
                throw new InvalidOperationException($"boom {HiddenTestName}");
            }

            string[] command = project.Filter is null
                ? ["dotnet", "test", Path.GetFileName(project.FullPath)]
                : ["dotnet", "test", Path.GetFileName(project.FullPath), "--filter", project.Filter];
            return Task.FromResult(new TestRunStep(phase, command));
        }
    }
}
