using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceReuseCliCoverageTests
{
    [Theory]
    [InlineData(true, false, "00:00:01")]
    [InlineData(true, true, "00:00:01")]
    public void ReuseOptionCombinationsAcceptOnlyValidOptIn(bool reuse, bool verify, string timeout)
        => EvidenceRunCommand.ValidateReuseOptions(reuse, verify, timeout);

    [Theory]
    [InlineData("0")]
    [InlineData("-00:00:01")]
    [InlineData("Infinite")]
    [InlineData("not-a-duration")]
    public void InvalidReuseWaitIsRejectedWithStableDiagnostic(string timeout)
    {
        var error = Assert.Throws<CommandException>(() => EvidenceRunCommand.ValidateReuseOptions(true, false, timeout));
        Assert.Contains("reuse.invalid_wait_timeout", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyReuseWithoutReuseIsRejectedBeforeExecution()
    {
        var error = Assert.Throws<CommandException>(() => EvidenceRunCommand.ValidateReuseOptions(false, true, "00:00:01"));

        Assert.Contains("reuse.verify_requires_reuse", error.Message, StringComparison.Ordinal);
        Assert.Contains("No store or output was created", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshRunsIgnoreProtectedContextWhileReuseRejectsItPerInvocation()
    {
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [], []);
        var plan = CreatePlan(profile);

        EvidenceRunCommand.ValidateReuseContext(false, plan, _ => "true");
        var error = Assert.Throws<CommandException>(() => EvidenceRunCommand.ValidateReuseContext(true, plan,
            name => name == "GITHUB_ACTIONS" ? "TRUE" : null));

        Assert.Contains("reuse.gate_context_forbidden", error.Message, StringComparison.Ordinal);
        EvidenceRunCommand.ValidateReuseContext(true, plan, _ => null);
    }

    [Fact]
    public async Task ReuseResolverReturnsConsumerHostIneligibilityBeforeStoreOrProducerWork()
    {
        using var directory = TestDirectory.Create();
        var producer = new EvidenceProducerDeclaration("browser", "browser-e2e", "1", ["database"], ["browser/assertion@1"], [], 10);
        var resource = new EvidenceResourceDeclaration("database", "completion", 10, []);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [resource], [producer],
            [new EvidenceObligation("browser-check", "browser", "browser readiness", [producer.Id], "browser/assertion@1")]);
        var plan = CreatePlan(profile);
        var resolver = new EvidenceReuseExecutionResolver(CreateCoverageProducer());

        var result = await resolver.RunAsync(plan, null, null, TestPathUtils.PathUnder(directory.Path, "run"),
            verifyReuse: false, observationOnly: false, storeRoot: TestPathUtils.PathUnder(directory.Path, "store"),
            waitTimeout: TimeSpan.FromSeconds(1), output: TextWriter.Null, error: TextWriter.Null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(result.Manifest.ProducerResults).Outcome);
        Assert.Equal(EvidenceResourceOutcome.Unavailable, Assert.Single(result.Manifest.ResourceResults).Outcome);
        var decision = Assert.Single(result.Decisions);
        Assert.Equal("Ineligible", decision.Status);
        Assert.Equal("reuse.consumer_host_required", decision.Reason);
        Assert.Equal("not-published", decision.Publication);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(directory.Path, "run")));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(directory.Path, "store")));
    }

    [Fact]
    public async Task ResolverRequiresGitWorktreeBeforeCreatingAnyRunOrStoreOutput()
    {
        using var directory = TestDirectory.Create();
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"], [], 10,
            new EvidenceCoverageGateRequirements(95, 85));
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("coverage-check", "coverage", "coverage gate", [producer.Id], "coverage/assertion@1")]);
        var output = TestPathUtils.PathUnder(directory.Path, "run-output");
        var store = TestPathUtils.PathUnder(directory.Path, "receipt-store");
        var resolver = new EvidenceReuseExecutionResolver(CreateCoverageProducer());
        var syntheticLocalContext = SyntheticLocalContext.Capture();

        try
        {
            syntheticLocalContext.Enter(directory.Path);
            var error = await Assert.ThrowsAsync<EvidenceCliException>(() => resolver.RunAsync(CreatePlan(profile), null,
                null, output, verifyReuse: false, observationOnly: false, storeRoot: store,
                waitTimeout: TimeSpan.FromSeconds(1), output: TextWriter.Null, error: TextWriter.Null,
                cancellationToken: CancellationToken.None));

            Assert.Equal("ASEVD218", error.Code);
            Assert.Contains("reuse.repository_required", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
            Assert.False(Directory.Exists(store));
        }
        finally
        {
            syntheticLocalContext.Restore();
        }
    }

    [Fact]
    public async Task ResolverExplainRequiresGitWorktreeBeforeInspectingOrCreatingReceiptStore()
    {
        using var directory = TestDirectory.Create();
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [], []);
        var resolver = new EvidenceReuseExecutionResolver(CreateCoverageProducer());
        var store = TestPathUtils.PathUnder(directory.Path, "receipt-store");
        var syntheticLocalContext = SyntheticLocalContext.Capture();

        try
        {
            syntheticLocalContext.Enter(directory.Path);
            var error = await Assert.ThrowsAsync<EvidenceCliException>(() => resolver.ExplainAsync(
                CreatePlan(profile), null, null, store, TimeSpan.FromSeconds(1), CancellationToken.None));

            Assert.Equal("ASEVD218", error.Code);
            Assert.Contains("reuse.repository_required", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(store));
        }
        finally
        {
            syntheticLocalContext.Restore();
        }
    }

    [Fact]
    public async Task ResolverReportsMissingSolutionWithoutStartingCoverageProcesses()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "declared stable source");
        await repository.CommitAsync();
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot()], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            Inputs = new EvidenceProducerInputs(["src/**"], []),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("coverage-check", "coverage", "coverage gate", [producer.Id], "coverage/assertion@1")]);
        var runner = new EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System)));
        var resolver = new EvidenceReuseExecutionResolver(adapter);
        var store = TestPathUtils.PathUnder(repository.Root, "missing-solution-store");
        var output = TestPathUtils.PathUnder(repository.Root, "missing-solution-output");
        var syntheticLocalContext = SyntheticLocalContext.Capture();

        try
        {
            syntheticLocalContext.Enter(repository.Root);
            var result = await resolver.RunAsync(CreatePlan(profile), null, null, output,
                verifyReuse: false, observationOnly: false, storeRoot: store,
                waitTimeout: TimeSpan.FromSeconds(2), output: TextWriter.Null, error: TextWriter.Null,
                cancellationToken: CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(result.Manifest.ProducerResults).Outcome);
            Assert.Equal("reuse.solution_required", Assert.Single(result.Decisions).Reason);
            Assert.Contains("requires --solution", Assert.Single(result.Manifest.ProducerResults).Diagnostic!, StringComparison.Ordinal);
            Assert.Equal(0, runner.InvocationCount);
            Assert.True(Directory.Exists(output));
        }
        finally
        {
            syntheticLocalContext.Restore();
        }
    }

    [Fact]
    public async Task WorkflowWritesReuseDecisionAndManifestSummaryAsObservableJson()
    {
        using var directory = TestDirectory.Create();
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [], []);
        var plan = CreatePlan(profile);
        var manifest = EvidenceManifestBuilder.Build(plan, [], observationOnly: true);
        var decision = new EvidenceReuseSummaryDecision("coverage", "Miss", "reuse.receipt_missing",
            "No matching receipt exists.", null, null, null, "not-published");

        var plainOutput = TestPathUtils.PathUnder(directory.Path, "plain");
        await workflow.WritePlanAsync(plan, plainOutput, CancellationToken.None);
        using (var plainPlanSummary = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(plainOutput, "evidence-summary.json"))))
        {
            Assert.Equal("plan", plainPlanSummary.RootElement.GetProperty("kind").GetString());
            Assert.False(plainPlanSummary.RootElement.TryGetProperty("reuseDecisions", out _));
            Assert.Contains("selected producer capabilities are ready", plainPlanSummary.RootElement.GetProperty("next").GetString(), StringComparison.Ordinal);
        }
        await workflow.WriteManifestAsync(manifest, plainOutput, CancellationToken.None);
        using (var plainManifestSummary = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(plainOutput, "evidence-summary.json"))))
        {
            Assert.Equal("manifest", plainManifestSummary.RootElement.GetProperty("kind").GetString());
            Assert.False(plainManifestSummary.RootElement.TryGetProperty("reuseDecisions", out _));
            Assert.Equal("Evidence claim is ready for its declared eligible consumer.", plainManifestSummary.RootElement.GetProperty("next").GetString());
        }

        await workflow.WritePlanAsync(plan, TestPathUtils.PathUnder(directory.Path, "out"), CancellationToken.None, [decision]);
        var summaryPath = TestPathUtils.PathUnder(directory.Path, "out", "evidence-summary.json");
        using (var planSummary = JsonDocument.Parse(await File.ReadAllBytesAsync(summaryPath)))
        {
            Assert.Equal("plan", planSummary.RootElement.GetProperty("kind").GetString());
            Assert.Equal("targeted", planSummary.RootElement.GetProperty("profile").GetString());
            Assert.Equal("reuse.receipt_missing", planSummary.RootElement.GetProperty("reuseDecisions")[0].GetProperty("Reason").GetString());
        }

        await workflow.WriteManifestAsync(manifest, TestPathUtils.PathUnder(directory.Path, "out"), CancellationToken.None, [decision]);
        var manifestBytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(directory.Path, "out", "evidence-manifest.json"));
        Assert.Equal(EvidenceCanonicalJson.Serialize(manifest), manifestBytes);
        var writtenManifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(manifestBytes);
        Assert.Equal(manifest.ClaimKind, writtenManifest.ClaimKind);
        Assert.Equal(manifest.ExecutionVerdict, writtenManifest.ExecutionVerdict);
        using var manifestSummary = JsonDocument.Parse(await File.ReadAllBytesAsync(summaryPath));
        Assert.Equal("manifest", manifestSummary.RootElement.GetProperty("kind").GetString());
        Assert.Equal(manifest.ExecutionVerdict.ToString(), manifestSummary.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(manifest.Eligibility.ToString(), manifestSummary.RootElement.GetProperty("eligibility").GetString());
        Assert.Equal("reuse.receipt_missing", manifestSummary.RootElement.GetProperty("reuseDecisions")[0].GetProperty("Reason").GetString());
    }

    [Fact]
    public void WorkflowSummarySeparatesRestoredHistoryFromFreshPublicationAndOptionalRoots()
    {
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"], [], 30);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("coverage-check", "coverage", "coverage is required", [producer.Id], "coverage/assertion@1")]);
        var plan = CreatePlan(profile);
        var reused = new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Reused, producer.AssertionIds, Artifacts: [])
        {
            ProducerKey = new string('c', 64),
            OriginManifestDigest = new string('a', 64),
            OriginCompletedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        var manifest = EvidenceManifestBuilder.Build(plan, [reused]);
        EvidenceReuseSummaryDecision[] rows =
        [
            new(producer.Id, "Hit", "reuse.published", "Restored verified receipt.", reused.ProducerKey,
                reused.OriginCompletedAt, null, "restored", ArtifactRoot: "coverage", ExecutionRoot: null),
            new(producer.Id, "Hit", "reuse.published", "Restored verified receipt.", reused.ProducerKey,
                reused.OriginCompletedAt, 250, "", ArtifactRoot: null, ExecutionRoot: null),
            new("fresh", "Miss", "reuse.receipt_missing", "No receipt exists.", null, null, null,
                "suppressed", ArtifactRoot: "producers/fresh", ExecutionRoot: "execution/fresh"),
        ];

        var summary = EvidenceCliWorkflow.FormatSummary(manifest, rows);

        Assert.Contains("invocation restore", summary, StringComparison.Ordinal);
        Assert.Contains("origin 0s ago", summary, StringComparison.Ordinal);
        Assert.Contains("historical elapsed unavailable", summary, StringComparison.Ordinal);
        Assert.Contains("historical elapsed 250ms", summary, StringComparison.Ordinal);
        Assert.Contains("artifact root coverage", summary, StringComparison.Ordinal);
        Assert.Contains("Reuse coverage: Hit (reuse.published)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("artifact root coverage; execution root", summary, StringComparison.Ordinal);
        Assert.Contains("receipt suppressed", summary, StringComparison.Ordinal);
        Assert.Contains("artifact root producers/fresh; execution root execution/fresh", summary, StringComparison.Ordinal);
        Assert.Equal(EvidenceClaimKind.LocalReusedComplete, manifest.ClaimKind);
    }

    [Theory]
    [InlineData("/repo/src/App.slnx", "src/App.slnx", "10.0.100")]
    [InlineData("/repo", "repo", "10.0.100")]
    [InlineData("/outside/App.slnx", "outside-repository", "10.0.100")]
    [InlineData("/repo/src/App.slnx", "src/App.slnx", "unavailable")]
    [InlineData(null, "missing", "not-applicable")]
    public void CoverageIdentityBindsSolutionAndExecutionConfiguration(string? solutionPath, string expectedSolution, string sdkVersion)
    {
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], [], [], 30)
        {
            CoverageExecution = new EvidenceCoverageExecutionOptions(Configuration: "Release", Parallelism: 3,
                TestResults: EvidenceCoverageTestResultFormat.Junit)
        };
        var identity = EvidenceReuseExecutionResolver.CreateToolIdentity(producer, "/repo", solutionPath, sdkVersion);

        Assert.Equal(expectedSolution, identity["solution"]);
        Assert.Equal("Release", identity["configuration"]);
        Assert.Equal("3", identity["parallelism"]);
        Assert.Equal("Junit", identity["testResults"]);
        Assert.Equal(sdkVersion, identity["sdk"]);
        Assert.Equal("not-applicable", EvidenceReuseExecutionResolver.CreateToolIdentity(
            producer with { Kind = "browser-e2e" }, "/repo", solutionPath, "10.0.100").GetValueOrDefault("sdk", "not-applicable"));
    }

    [Fact]
    public async Task ResolverExplainAndRunReportCustomProducerAdmissionWithoutExecutingIt()
    {
        using var localContext = new EnvironmentVariableScope("GITHUB_ACTIONS", null);
        using var directory = TestDirectory.Create();
        var declaration = new EvidenceProducerDeclaration("custom", "browser-e2e", "1", [], ["browser/assertion@1"], [], 10);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("browser", "browser", "browser contract", [declaration.Id], "browser/assertion@1")]);
        var plan = CreatePlan(profile);
        var resolver = new EvidenceReuseExecutionResolver(CreateCoverageProducer());
        var store = TestPathUtils.PathUnder(directory.Path, "store");

        var explained = await resolver.ExplainAsync(plan, null, null, store, TimeSpan.FromSeconds(1), CancellationToken.None);

        var explanation = Assert.Single(explained);
        Assert.Equal("Ineligible", explanation.Status);
        Assert.Equal("reuse.consumer_host_required", explanation.Reason);
        Assert.Null(explanation.ExecutionRoot);
        Assert.Null(explanation.ArtifactRoot);
        Assert.False(Directory.Exists(store));

        var output = TestPathUtils.PathUnder(directory.Path, "run");
        var executed = await resolver.RunAsync(plan, null, null, output, false, false, store,
            TimeSpan.FromSeconds(1), TextWriter.Null, TextWriter.Null, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(executed.Manifest.ProducerResults).Outcome);
        var decision = Assert.Single(executed.Decisions);
        Assert.Equal("Ineligible", decision.Status);
        Assert.Equal("reuse.consumer_host_required", decision.Reason);
        Assert.Equal("suppressed", decision.Publication);
        Assert.Equal("execution/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(declaration.Id))).ToLowerInvariant(), decision.ExecutionRoot);
        Assert.True(Directory.Exists(output));
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public async Task ExplainRejectsCustomProducerKeyMaterialWithoutCreatingReceiptStore()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "declared custom producer input");
        await repository.CommitAsync();
        var declaration = new EvidenceProducerDeclaration("custom", "browser-e2e", "1", [],
            ["browser/assertion@1"], [], 10)
        {
            Inputs = new EvidenceProducerInputs(["src/**"], []),
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("browser", "browser", "browser contract", [declaration.Id], "browser/assertion@1")]);
        var resolver = new EvidenceReuseExecutionResolver(CreateCoverageProducer());
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var context = SyntheticLocalContext.Capture();

        try
        {
            context.Enter(repository.Root);
            var decision = Assert.Single(await resolver.ExplainAsync(CreatePlan(profile), null, null, store,
                TimeSpan.FromSeconds(2), CancellationToken.None));

            Assert.Equal("Ineligible", decision.Status);
            Assert.Equal("reuse.consumer_host_required", decision.Reason);
            Assert.Null(decision.ProducerKey);
            Assert.False(Directory.Exists(store));
        }
        finally
        {
            context.Restore();
        }
    }

    [Fact]
    public async Task ResolverExplainRejectsResourceBackedProfilesWithoutInspectingOrCreatingAStore()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var producer = new EvidenceProducerDeclaration("browser", "browser-e2e", "1", ["database"],
            ["browser/assertion@1"], [], 10);
        var resource = new EvidenceResourceDeclaration("database", "completion", 10, []);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [resource], [producer],
            [new EvidenceObligation("browser", "browser", "browser contract", [producer.Id], "browser/assertion@1")]);
        var resolver = new EvidenceReuseExecutionResolver(CreateCoverageProducer());
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var context = SyntheticLocalContext.Capture();

        try
        {
            context.Enter(repository.Root);
            var explanation = Assert.Single(await resolver.ExplainAsync(CreatePlan(profile), null, null, store,
                TimeSpan.FromSeconds(1), CancellationToken.None));

            Assert.Equal("Ineligible", explanation.Status);
            Assert.Equal("reuse.consumer_host_required", explanation.Reason);
            Assert.Equal("not-published", explanation.Publication);
            Assert.Null(explanation.ProducerKey);
            Assert.Null(explanation.ExecutionRoot);
            Assert.Null(explanation.ArtifactRoot);
            Assert.False(Directory.Exists(store));
        }
        finally
        {
            context.Restore();
        }
    }

    [Fact]
    public async Task ExplainCommandWritesReadOnlyCustomProducerDecisionWithoutCreatingAStore()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "selected source");
        var producer = new EvidenceProducerDeclaration("browser", "browser-e2e", "1", [], ["browser/assertion@1"], [], 10);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("browser-check", "browser", "browser contract", [producer.Id], "browser/assertion@1")]);
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile],
            [new EvidencePolicyRule("src", "src/**", profile.Id)]);
        var policyPath = TestPathUtils.PathUnder(repository.Root, "evidence.policy.json");
        await File.WriteAllBytesAsync(policyPath, EvidenceCanonicalJson.Serialize(policy));
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var output = TestPathUtils.PathUnder(repository.Root, "explain-output");
        var context = SyntheticLocalContext.Capture();

        try
        {
            context.Enter(repository.Root);
            using var console = new FakeInMemoryConsole();
            await new EvidenceExplainCommand(new EvidenceCliWorkflow(new EvidencePlanner()), CreateCoverageProducer())
            {
                PolicyPath = policyPath,
                Paths = ["src/input.txt"],
                OutputDirectory = output,
                Reuse = true,
                ReceiptStore = store,
            }.ExecuteAsync(console);

            using var summary = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "evidence-summary.json")));
            Assert.Equal("reuse.consumer_host_required", summary.RootElement.GetProperty("reuseDecisions")[0].GetProperty("Reason").GetString());
            Assert.Contains("reuse.consumer_host_required", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, "evidence-plan.json")));
            Assert.False(Directory.Exists(store));
        }
        finally
        {
            context.Restore();
        }
    }

    [Fact]
    public async Task ResolverExplainMarksOutsideSolutionIdentityIneligibleAndStoreCollisionUnavailable()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("global.json", FindPinnedSdkConfiguration());
        await repository.WriteAsync("src/input.txt", "declared stable source");
        await repository.WriteAsync("src/repository.slnx", "in-repository solution fixture");
        await repository.CommitAsync();
        using var outside = TestDirectory.Create();
        var outsideSolution = TestPathUtils.PathUnder(outside.Path, "outside.slnx");
        await File.WriteAllTextAsync(outsideSolution, "outside solution fixture");
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot()], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            Inputs = new EvidenceProducerInputs(["global.json", "src/**"], []),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("coverage-check", "coverage", "coverage gate", [producer.Id], "coverage/assertion@1")]);
        var resolver = new EvidenceReuseExecutionResolver(CreateCoverageProducer());
        var outsideStore = TestPathUtils.PathUnder(repository.Root, "outside-solution-store");
        var context = SyntheticLocalContext.Capture();

        try
        {
            context.Enter(repository.Root);
            var outsideExplanation = Assert.Single(await resolver.ExplainAsync(CreatePlan(profile), null, outsideSolution,
                outsideStore, TimeSpan.FromSeconds(2), CancellationToken.None));

            Assert.Equal("Ineligible", outsideExplanation.Status);
            Assert.Equal("reuse.solution_required", outsideExplanation.Reason);
            Assert.Contains("inside this Git worktree", outsideExplanation.Diagnostic, StringComparison.Ordinal);
            Assert.Null(outsideExplanation.ProducerKey);
            Assert.False(Directory.Exists(outsideStore));

            var collisionStore = TestPathUtils.PathUnder(repository.Root, "explain-store-collision");
            await File.WriteAllTextAsync(collisionStore, "preserve occupied path");
            var collisionExplanation = Assert.Single(await resolver.ExplainAsync(CreatePlan(profile), null,
                TestPathUtils.PathUnder(repository.Root, "src/repository.slnx"), collisionStore,
                TimeSpan.FromSeconds(2), CancellationToken.None));

            Assert.Equal("Unavailable", collisionExplanation.Status);
            Assert.StartsWith("reuse.", collisionExplanation.Reason, StringComparison.Ordinal);
            Assert.Equal("preserve occupied path", await File.ReadAllTextAsync(collisionStore));
        }
        finally
        {
            context.Restore();
        }
    }

    [Fact]
    public async Task ResolverRunsFreshThenRestoresInformationalReceiptAndSuccessfullyAuditsIt()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var sdkJson = FindPinnedSdkConfiguration();
        await repository.WriteAsync("global.json", sdkJson);
        await repository.WriteAsync("src/input.txt", "declared stable source");
        await repository.WriteAsync("sample.slnx", "synthetic solution fixture");
        await repository.WriteAsync("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        await repository.CommitAsync();

        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot()], 60, new EvidenceCoverageGateRequirements(95, 85))
        {
            Inputs = new EvidenceProducerInputs(["global.json", "src/**", "sample.slnx", "tests/**"], []),
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit)
            { CooperativeChildLifetime = true },
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("coverage-check", "coverage", "coverage gate", [producer.Id], "coverage/assertion@1")]);
        var plan = CreatePlan(profile);
        var solution = TestPathUtils.PathUnder(repository.Root, "sample.slnx");
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var runner = new EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System)));
        var resolver = new EvidenceReuseExecutionResolver(adapter);
        var freshOutput = TestPathUtils.PathUnder(repository.Root, "fresh-output");
        var syntheticLocalContext = SyntheticLocalContext.Capture();

        // SyntheticLocalContext scopes the resolver's repository discovery and local-only admission contract.
        try
        {
            syntheticLocalContext.Enter(repository.Root);
            EvidenceRunCommand.ValidateReuseContext(true, plan);

            var missExplanation = Assert.Single(await resolver.ExplainAsync(plan, null, solution, store,
                TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal("Miss", missExplanation.Status);
            Assert.Equal("reuse.no-comparable-receipt", missExplanation.Reason);
            Assert.Null(missExplanation.ExecutionRoot);
            Assert.Null(missExplanation.ArtifactRoot);
            Assert.False(Directory.Exists(store));
            Assert.Equal(0, runner.InvocationCount);

            var fresh = await resolver.RunAsync(plan, null, solution, freshOutput, verifyReuse: false,
                observationOnly: false, storeRoot: store, waitTimeout: TimeSpan.FromSeconds(5),
                output: TextWriter.Null, error: TextWriter.Null, cancellationToken: CancellationToken.None);
            var freshResult = Assert.Single(fresh.Manifest.ProducerResults);
            Assert.Equal(EvidenceProducerOutcome.Passed, freshResult.Outcome);
            Assert.Equal(EvidenceExecutionVerdict.Passed, fresh.Manifest.ExecutionVerdict);
            var freshDecision = Assert.Single(fresh.Decisions);
            Assert.True(freshResult.Artifacts is not null,
                $"Fresh coverage passed but receipt artifacts were ineligible. Decision={freshDecision.Reason}: {freshDecision.Diagnostic}; producer={freshResult.Diagnostic}");
            Assert.NotEmpty(freshResult.Artifacts);
            Assert.True(runner.TestInvocationCount > 0);
            var freshInvocationCount = runner.InvocationCount;
            var expectedHashes = await HashArtifactsAsync(freshOutput, freshResult.Artifacts);
            Assert.Equal("Miss", freshDecision.Status);
            Assert.Contains("reuse.published", freshDecision.Reason, StringComparison.Ordinal);

            var storeBeforeExplainHit = await SnapshotTreeAsync(store);
            var hitExplanation = Assert.Single(await resolver.ExplainAsync(plan, null, solution, store,
                TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal("Hit", hitExplanation.Status);
            Assert.Equal("reuse.candidate", hitExplanation.Reason);
            Assert.Null(hitExplanation.ExecutionRoot);
            Assert.Null(hitExplanation.ArtifactRoot);
            Assert.Equal(storeBeforeExplainHit.ToArray(), (await SnapshotTreeAsync(store)).ToArray());
            Assert.Equal(freshInvocationCount, runner.InvocationCount);

            var warmOutput = TestPathUtils.PathUnder(repository.Root, "warm-output");
            var warm = await resolver.RunAsync(plan, null, solution, warmOutput, verifyReuse: false,
                observationOnly: false, storeRoot: store, waitTimeout: TimeSpan.FromSeconds(5),
                output: TextWriter.Null, error: TextWriter.Null, cancellationToken: CancellationToken.None);
            var warmResult = Assert.Single(warm.Manifest.ProducerResults);
            Assert.Equal(EvidenceProducerOutcome.Reused, warmResult.Outcome);
            Assert.Equal(EvidenceClaimKind.LocalReusedComplete, warm.Manifest.ClaimKind);
            Assert.Equal(EvidenceClaimEligibility.Informational, warm.Manifest.Eligibility);
            Assert.Equal(EvidenceExecutionVerdict.Passed, warm.Manifest.ExecutionVerdict);
            Assert.Equal("Hit", Assert.Single(warm.Decisions).Status);
            Assert.Equal(freshInvocationCount, runner.InvocationCount);
            Assert.Equal(expectedHashes.ToArray(), (await HashArtifactsAsync(warmOutput, warmResult.Artifacts!)).ToArray());

            var auditOutput = TestPathUtils.PathUnder(repository.Root, "audit-output");
            var beforeAudit = runner.InvocationCount;
            var audit = await resolver.RunAsync(plan, null, solution, auditOutput, verifyReuse: true,
                observationOnly: false, storeRoot: store, waitTimeout: TimeSpan.FromSeconds(5),
                output: TextWriter.Null, error: TextWriter.Null, cancellationToken: CancellationToken.None);
            var auditResult = Assert.Single(audit.Manifest.ProducerResults);
            Assert.Equal(EvidenceProducerOutcome.Passed, auditResult.Outcome);
            Assert.Equal(EvidenceExecutionVerdict.Passed, audit.Manifest.ExecutionVerdict);
            Assert.True(runner.InvocationCount > beforeAudit);
            Assert.Equal("Miss", Assert.Single(audit.Decisions).Status);
            Assert.Contains("reuse.published", Assert.Single(audit.Decisions).Reason, StringComparison.Ordinal);

            var producerKey = Assert.IsType<string>(freshDecision.ProducerKey);
            var keyDirectory = Assert.Single(Directory.EnumerateDirectories(store, producerKey, SearchOption.AllDirectories));
            var repositoryNamespace = Path.GetFileName(Path.GetDirectoryName(keyDirectory)!);
            var writableStore = EvidenceReceiptStore.Open(new EvidenceReceiptStoreOptions
            {
                RootDirectory = store,
                RepositoryNamespace = repositoryNamespace,
                WaitTimeout = TimeSpan.FromSeconds(5),
            });
            using var inFlightLease = Assert.Single(await writableStore.AcquireAsync([producerKey], CancellationToken.None));
            var inFlightExecutionId = "explain-inflight-" + Guid.NewGuid().ToString("N");
            inFlightLease.EstablishAuthority(inFlightExecutionId);
            var authorityBeforeContendedAdmission = inFlightLease.ReadState();
            Assert.Equal(inFlightExecutionId, authorityBeforeContendedAdmission?.InFlightExecutionId);
            var inFlightReservationId = inFlightLease.EssentialReservationId;
            var quotaBeforeContendedAdmission = await ReadQuotaEntryAsync(store, repositoryNamespace, producerKey);
            var reservationsBeforeContendedAdmission = ReadEssentialReservationIds(quotaBeforeContendedAdmission);
            Assert.Equal(new[] { inFlightReservationId }, reservationsBeforeContendedAdmission);
            var protectedStoreBytesBeforeContendedAdmission = await SnapshotTreeAsync(store);
            var payloadBytesBeforeContendedAdmission = await SnapshotTreeAsync(Path.Join(keyDirectory,
                Assert.IsType<string>(authorityBeforeContendedAdmission?.PayloadDirectoryName)));
            Assert.NotEmpty(protectedStoreBytesBeforeContendedAdmission);
            Assert.NotEmpty(payloadBytesBeforeContendedAdmission);
            var storeBeforeInFlightExplain = await SnapshotTreeAsync(store);
            var inFlightExplanation = Assert.Single(await resolver.ExplainAsync(plan, null, solution, store,
                TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal("Unavailable", inFlightExplanation.Status);
            Assert.Equal("reuse.unresolved-execution", inFlightExplanation.Reason);
            Assert.Equal(storeBeforeInFlightExplain.ToArray(), (await SnapshotTreeAsync(store)).ToArray());

            var runnerBeforeContendedAdmission = runner.InvocationCount;
            var contendedOutput = TestPathUtils.PathUnder(repository.Root, "contended-output");
            var throwingProgressWriter = new ThrowOnWriteTextWriter(new IOException("diagnostic stream closed"));
            var contended = await resolver.RunAsync(plan, null, solution, contendedOutput, verifyReuse: false,
                observationOnly: false, storeRoot: store, waitTimeout: TimeSpan.FromSeconds(7),
                output: TextWriter.Null, error: throwingProgressWriter, cancellationToken: CancellationToken.None);

            var contendedResult = Assert.Single(contended.Manifest.ProducerResults);
            Assert.Equal(EvidenceProducerOutcome.Unavailable, contendedResult.Outcome);
            Assert.Equal(EvidenceExecutionVerdict.Incomplete, contended.Manifest.ExecutionVerdict);
            Assert.Equal(EvidenceClaimKind.None, contended.Manifest.ClaimKind);
            Assert.Equal("Unavailable", Assert.Single(contended.Decisions).Status);
            Assert.Equal("reuse.coordination_unavailable", Assert.Single(contended.Decisions).Reason);
            Assert.True(throwingProgressWriter.WriteAttempts > 0,
                "The held producer-key lease should emit advisory wait progress before admission times out.");
            Assert.Equal(runnerBeforeContendedAdmission, runner.InvocationCount);
            Assert.False(Directory.Exists(contendedOutput));

            var quotaDuringContention = await ReadQuotaEntryAsync(store, repositoryNamespace, producerKey);
            var reservationsDuringContention = ReadEssentialReservationIds(quotaDuringContention);
            var waitingReservationId = Assert.Single(reservationsDuringContention.Except(reservationsBeforeContendedAdmission));
            Assert.Contains(inFlightReservationId, reservationsDuringContention);
            Assert.Equal(authorityBeforeContendedAdmission, inFlightLease.ReadState());
            AssertQuotaPayloadAccountingUnchanged(quotaBeforeContendedAdmission, quotaDuringContention);
            Assert.InRange(quotaDuringContention.GetProperty("MetadataBytes").GetInt64(),
                quotaBeforeContendedAdmission.GetProperty("MetadataBytes").GetInt64(),
                EvidenceReceiptQuota.EssentialReservationBytes);
            AssertQuotaEntryWithinBounds(quotaDuringContention, store);
            Assert.Equal(protectedStoreBytesBeforeContendedAdmission.Where(static item => item.Key != "F:quota-index.json").ToArray(),
                (await SnapshotTreeAsync(store)).Where(static item => item.Key != "F:quota-index.json").ToArray());
            Assert.Equal(payloadBytesBeforeContendedAdmission.ToArray(),
                (await SnapshotTreeAsync(Path.Join(keyDirectory, authorityBeforeContendedAdmission!.PayloadDirectoryName!))).ToArray());

            // The active key owner can reconcile the timed-out waiter's speculative hold without changing authority.
            EvidenceReceiptQuota.ReconcilePayloadReservations(inFlightLease, TimeSpan.FromSeconds(5), CancellationToken.None);
            var quotaAfterReconciliation = await ReadQuotaEntryAsync(store, repositoryNamespace, producerKey);
            Assert.Equal(new[] { inFlightReservationId }, ReadEssentialReservationIds(quotaAfterReconciliation));
            Assert.DoesNotContain(waitingReservationId, ReadEssentialReservationIds(quotaAfterReconciliation));
            Assert.Equal(authorityBeforeContendedAdmission, inFlightLease.ReadState());
            AssertQuotaPayloadAccountingUnchanged(quotaBeforeContendedAdmission, quotaAfterReconciliation);
            AssertQuotaEntryWithinBounds(quotaAfterReconciliation, store);
            Assert.Equal(protectedStoreBytesBeforeContendedAdmission.Where(static item => item.Key != "F:quota-index.json").ToArray(),
                (await SnapshotTreeAsync(store)).Where(static item => item.Key != "F:quota-index.json").ToArray());
            Assert.Equal(payloadBytesBeforeContendedAdmission.ToArray(),
                (await SnapshotTreeAsync(Path.Join(keyDirectory, authorityBeforeContendedAdmission!.PayloadDirectoryName!))).ToArray());

            var collisionStore = TestPathUtils.PathUnder(repository.Root, "store-collision");
            await File.WriteAllTextAsync(collisionStore, "occupied by a file");
            var beforeCollision = runner.InvocationCount;
            var collision = await resolver.RunAsync(plan, null, solution,
                TestPathUtils.PathUnder(repository.Root, "collision-output"), verifyReuse: false,
                observationOnly: false, storeRoot: collisionStore, waitTimeout: TimeSpan.FromSeconds(5),
                output: TextWriter.Null, error: TextWriter.Null, cancellationToken: CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(collision.Manifest.ProducerResults).Outcome);
            Assert.Equal(beforeCollision, runner.InvocationCount);
            Assert.True(File.Exists(collisionStore));
            Assert.Equal("Unavailable", Assert.Single(collision.Decisions).Status);
            Assert.StartsWith("reuse.", Assert.Single(collision.Decisions).Reason, StringComparison.Ordinal);

            var reportConflictRoot = TestPathUtils.PathUnder(repository.Root, "report-conflict-output");
            var occupiedReport = TestPathUtils.PathUnder(reportConflictRoot, "coverage", "coverage-gate.md");
            Directory.CreateDirectory(Path.GetDirectoryName(occupiedReport)!);
            await File.WriteAllTextAsync(occupiedReport, "preserve pre-existing report");
            var reportConflict = await resolver.RunAsync(plan, null, solution, reportConflictRoot, verifyReuse: false,
                observationOnly: false, storeRoot: TestPathUtils.PathUnder(repository.Root, "report-conflict-store"),
                waitTimeout: TimeSpan.FromSeconds(5), output: TextWriter.Null, error: TextWriter.Null,
                cancellationToken: CancellationToken.None);
            var conflictResult = Assert.Single(reportConflict.Manifest.ProducerResults);
            Assert.Equal(EvidenceProducerOutcome.Failed, conflictResult.Outcome);
            Assert.Equal(EvidenceExecutionVerdict.Incomplete, reportConflict.Manifest.ExecutionVerdict);
            Assert.Empty(conflictResult.SatisfiedAssertionIds);
            Assert.Null(conflictResult.Artifacts);
            Assert.Contains("ordinary-report-promotion-unavailable", conflictResult.Diagnostic!, StringComparison.Ordinal);
            Assert.Equal("preserve pre-existing report", await File.ReadAllTextAsync(occupiedReport));
            var conflictDecision = Assert.Single(reportConflict.Decisions);
            Assert.Equal("Ineligible", conflictDecision.Status);
            Assert.Equal("reuse.inventory-result-not-passed", conflictDecision.Reason);
            Assert.Equal("suppressed", conflictDecision.Publication);
            var executionRoot = EvidenceReuseExecutionResolver.GetProducerRoots(reportConflictRoot, producer.Id,
                useInvocationArtifactRoot: true).Execution;
            Assert.True(File.Exists(TestPathUtils.PathUnder(executionRoot, "coverage", "coverage-gate.json")));
            Assert.True(File.Exists(TestPathUtils.PathUnder(executionRoot, "coverage", "coverage-gate.md")));
        }
        finally
        {
            syntheticLocalContext.Restore();
        }
    }

    [Fact]
    public async Task ResolverCancellationDuringFreshProducerSettlesAuthorityAndAllowsAReadOnlyMiss()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("global.json", FindPinnedSdkConfiguration());
        await repository.WriteAsync("src/input.txt", "declared stable source");
        await repository.WriteAsync("sample.slnx", "synthetic solution fixture");
        await repository.WriteAsync("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        await repository.CommitAsync();

        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot()], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            Inputs = new EvidenceProducerInputs(["global.json", "src/**", "sample.slnx", "tests/**"], []),
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit)
            { CooperativeChildLifetime = true },
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("coverage-check", "coverage", "coverage gate", [producer.Id], "coverage/assertion@1")]);
        var plan = CreatePlan(profile);
        var solution = TestPathUtils.PathUnder(repository.Root, "sample.slnx");
        var store = TestPathUtils.PathUnder(repository.Root, "cancelled-run-store");
        using var cancellation = new CancellationTokenSource();
        var runner = new EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner(
            "tests/Sample.Tests/Sample.Tests.csproj", cancellation.Cancel);
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System)));
        var resolver = new EvidenceReuseExecutionResolver(adapter);
        var syntheticLocalContext = SyntheticLocalContext.Capture();

        try
        {
            syntheticLocalContext.Enter(repository.Root);
            var run = resolver.RunAsync(plan, null, solution, TestPathUtils.PathUnder(repository.Root, "cancelled-output"),
                verifyReuse: false, observationOnly: false, storeRoot: store, waitTimeout: TimeSpan.FromSeconds(3),
                output: TextWriter.Null, error: TextWriter.Null, cancellationToken: cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(1, runner.TestInvocationCount);

            var afterSettlement = Assert.Single(await resolver.ExplainAsync(plan, null, solution, store,
                TimeSpan.FromSeconds(3), CancellationToken.None));
            Assert.Equal("Miss", afterSettlement.Status);
            Assert.Equal("reuse.no-comparable-receipt", afterSettlement.Reason);
            Assert.Equal(1, runner.TestInvocationCount);
        }
        finally
        {
            syntheticLocalContext.Restore();
        }
    }

    [Fact]
    public async Task ResolverPreservesFreshReportsButWithholdsClaimWhenAdmittedChildDisconnects()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("global.json", FindPinnedSdkConfiguration());
        await repository.WriteAsync("src/input.txt", "declared stable source");
        await repository.WriteAsync("sample.slnx", "synthetic solution fixture");
        await repository.WriteAsync("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        await repository.CommitAsync();

        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot()], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            Inputs = new EvidenceProducerInputs(["global.json", "src/**", "sample.slnx", "tests/**"], []),
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit)
            { CooperativeChildLifetime = true },
        };
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("coverage-check", "coverage", "coverage gate", [producer.Id], "coverage/assertion@1")]);
        var plan = CreatePlan(profile);
        var solution = TestPathUtils.PathUnder(repository.Root, "sample.slnx");
        var outputRoot = TestPathUtils.PathUnder(repository.Root, "child-disconnect-output");
        var store = TestPathUtils.PathUnder(repository.Root, "child-disconnect-store");
        var processRunner = new DisconnectingFixtureRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            processRunner, new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System)));
        var resolver = new EvidenceReuseExecutionResolver(adapter);
        var syntheticLocalContext = SyntheticLocalContext.Capture();

        try
        {
            syntheticLocalContext.Enter(repository.Root);
            var result = await resolver.RunAsync(plan, null, solution, outputRoot, verifyReuse: false,
                observationOnly: false, storeRoot: store, waitTimeout: TimeSpan.FromSeconds(5),
                output: TextWriter.Null, error: TextWriter.Null, cancellationToken: CancellationToken.None);

            var producerResult = Assert.Single(result.Manifest.ProducerResults);
            Assert.True(processRunner.ChannelCredentialsForwarded);
            Assert.True(processRunner.ChildAcknowledged);
            Assert.True(processRunner.Inner.TestInvocationCount > 0);
            Assert.Equal(EvidenceProducerOutcome.Passed, producerResult.Outcome);
            Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.Manifest.ExecutionVerdict);
            Assert.Equal(EvidenceClaimKind.None, result.Manifest.ClaimKind);
            Assert.False(result.Manifest.Metrics.CleanupCompleted);
            Assert.Contains("reuse.child_settlement_unknown", result.Manifest.Metrics.CleanupDiagnostic!, StringComparison.Ordinal);
            Assert.True(File.Exists(TestPathUtils.PathUnder(outputRoot, "coverage", "coverage-gate.json")));
            Assert.True(File.Exists(TestPathUtils.PathUnder(outputRoot, "coverage", "coverage-gate.md")));
            Assert.Equal("Miss", Assert.Single(result.Decisions).Status);
            var unresolvedReceipt = Assert.Single(await resolver.ExplainAsync(plan, null, solution, store,
                TimeSpan.FromSeconds(3), CancellationToken.None));
            Assert.Equal("Unavailable", unresolvedReceipt.Status);
            Assert.Equal("reuse.unresolved-execution", unresolvedReceipt.Reason);
        }
        finally
        {
            syntheticLocalContext.Restore();
        }
    }

    private static async Task<SortedDictionary<string, string>> HashArtifactsAsync(string root, IReadOnlyList<EvidenceArtifactResult> artifacts)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            var bytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(root, artifact.RelativePath));
            hashes.Add(artifact.RelativePath, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
        return hashes;
    }

    private static async Task<JsonElement> ReadQuotaEntryAsync(string storeRoot, string repositoryNamespace, string key)
    {
        var indexPath = TestPathUtils.PathUnder(storeRoot, "quota-index.json");
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(4, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.InRange(document.RootElement.GetProperty("Entries").GetArrayLength(), 1, EvidenceReceiptQuota.MaximumEntries);
        return Assert.Single(document.RootElement.GetProperty("Entries").EnumerateArray()
            .Where(entry => entry.GetProperty("RepositoryNamespace").GetString() == repositoryNamespace
                && entry.GetProperty("Key").GetString() == key)
            .Select(static entry => entry.Clone()));
    }

    private static string[] ReadEssentialReservationIds(JsonElement entry)
        => entry.GetProperty("EssentialReservationIds").EnumerateArray()
            .Select(static value => Assert.IsType<string>(value.GetString())).ToArray();

    private static void AssertQuotaPayloadAccountingUnchanged(JsonElement before, JsonElement after)
    {
        foreach (var property in new[]
        {
            "RepositoryNamespace", "Key", "PayloadBytes", "ReservedPayloadBytes", "EssentialReservationBytes",
            "EssentialReserved", "HasInFlight", "HasAuditIntent", "PayloadDirectoryName", "OriginCompletedAt",
            "LastSuccessfulUse", "Quarantined", "AuthorityState", "RetiredPayloads", "ProducerId",
            "InputIdentityDigest", "PayloadReservations", "EvictionPending"
        })
            Assert.Equal(PropertyJson(before, property), PropertyJson(after, property));
    }

    private static void AssertQuotaEntryWithinBounds(JsonElement entry, string storeRoot)
    {
        var metadataBytes = entry.GetProperty("MetadataBytes").GetInt64();
        var payloadBytes = entry.GetProperty("PayloadBytes").GetInt64();
        var reservedPayloadBytes = entry.GetProperty("ReservedPayloadBytes").GetInt64();
        var retiredPayloadBytes = ReadArray(entry, "RetiredPayloads")
            .Sum(static payload => payload.GetProperty("Bytes").GetInt64());
        var boundPayloadBytes = ReadArray(entry, "PayloadReservations")
            .Sum(static reservation => reservation.GetProperty("ReservedBytes").GetInt64());
        var essentialReservationIds = ReadEssentialReservationIds(entry);

        Assert.InRange(metadataBytes, 0, EvidenceReceiptQuota.EssentialReservationBytes);
        Assert.InRange(payloadBytes + reservedPayloadBytes + retiredPayloadBytes + metadataBytes,
            0, EvidenceReceiptQuota.MaximumBytes);
        Assert.InRange(essentialReservationIds.Length, 0, 64);
        Assert.InRange(boundPayloadBytes, 0, reservedPayloadBytes);
        var indexPath = TestPathUtils.PathUnder(storeRoot, "quota-index.json");
        var indexLength = new FileInfo(indexPath).Length;
        Assert.InRange(indexLength, 1, EvidenceReceiptQuota.IndexMaximumBytes);
        using var document = JsonDocument.Parse(File.ReadAllBytes(indexPath));
        var totalAccountedBytes = document.RootElement.GetProperty("Entries").EnumerateArray().Sum(static item =>
            item.GetProperty("MetadataBytes").GetInt64()
            + item.GetProperty("PayloadBytes").GetInt64()
            + item.GetProperty("ReservedPayloadBytes").GetInt64()
            + ReadArray(item, "RetiredPayloads").Sum(static retired => retired.GetProperty("Bytes").GetInt64()));
        Assert.InRange(totalAccountedBytes + indexLength, 0, EvidenceReceiptQuota.MaximumBytes);
    }

    private static string PropertyJson(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.GetRawText() : "<default-omitted>";

    private static IEnumerable<JsonElement> ReadArray(JsonElement parent, string property)
        => parent.GetProperty(property).ValueKind == JsonValueKind.Array
            ? parent.GetProperty(property).EnumerateArray().ToArray()
            : [];

    private static async Task<SortedDictionary<string, string>> SnapshotTreeAsync(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            snapshot.Add("D:" + Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/'), string.Empty);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var bytes = await File.ReadAllBytesAsync(file);
            snapshot.Add("F:" + relative, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
        return snapshot;
    }

    private static string FindPinnedSdkConfiguration()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = TestPathUtils.PathUnder(directory.FullName, "global.json");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new InvalidOperationException("The test assembly must be located beneath the pinned SDK repository configuration.");
    }

    private sealed record SyntheticLocalContext(string OriginalDirectory, string? OriginalActions)
    {
        internal static SyntheticLocalContext Capture()
            => new(Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

        internal void Enter(string repositoryRoot)
        {
            Directory.SetCurrentDirectory(repositoryRoot);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
        }

        internal void Restore()
        {
            Directory.SetCurrentDirectory(OriginalDirectory);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", OriginalActions);
        }
    }

    private sealed class ThrowOnWriteTextWriter(Exception exception) : TextWriter
    {
        public int WriteAttempts { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            throw exception;
        }
    }

    private static CoverageEvidenceProducer CreateCoverageProducer()
        => new(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            new NoopRunner(), new NoopGenerator(), TimeProvider.System)));

    private static EvidencePlan CreatePlan(EvidenceProfile profile)
    {
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        var paths = Array.Empty<NormalizedDiffPath>();
        var plan = new EvidencePlan("1.1", policy.Id, EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(paths), profile, paths, [], string.Empty, policy);
        return plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
    }

    private sealed class NoopRunner : ICoverageRunProcessRunner
    {
        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The consumer-host admission branch must not start a process.");
    }

    private sealed class DisconnectingFixtureRunner(string projectPath) : ICoverageRunProcessRunner
    {
        internal EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner Inner { get; } = new(projectPath);
        internal bool ChannelCredentialsForwarded { get; private set; }
        internal bool ChildAcknowledged { get; private set; }

        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.Arguments.FirstOrDefault() != "test")
                return await Inner.RunAsync(request, cancellationToken);

            var environment = request.Environment;
            var pipeName = string.Empty;
            var executionId = string.Empty;
            var nonce = string.Empty;
            var hasChannel = environment?.TryGetValue(EvidenceChildLifetimeSession.PipeEnvironmentName, out pipeName) == true;
            var hasExecution = environment?.TryGetValue(EvidenceChildLifetimeSession.ExecutionEnvironmentName, out executionId) == true;
            var hasNonce = environment?.TryGetValue(EvidenceChildLifetimeSession.NonceEnvironmentName, out nonce) == true;
            ChannelCredentialsForwarded = hasChannel && hasExecution && hasNonce
                && !string.IsNullOrWhiteSpace(pipeName) && !string.IsNullOrWhiteSpace(executionId) && !string.IsNullOrWhiteSpace(nonce);
            if (!ChannelCredentialsForwarded)
                throw new InvalidOperationException("The dotnet test request did not receive the cooperative child channel credentials.");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using (var child = new NamedPipeClientStream(".", pipeName!, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await child.ConnectAsync(3000, deadline.Token);
                await EvidenceOwnedChildProtocol.WriteFrameAsync(child, $"BEGIN|{executionId}|{nonce}", deadline.Token);
                var acknowledgement = await EvidenceOwnedChildProtocol.ReadFrameAsync(child, deadline.Token);
                ChildAcknowledged = acknowledgement is not null && acknowledgement.StartsWith("ACK|", StringComparison.Ordinal);
                if (!ChildAcknowledged)
                    throw new InvalidOperationException("The resolver did not admit the fixture child connection.");
                // Closing after ACK intentionally omits COMPLETE; the resolver must preserve reports but deny custody.
            }

            return await Inner.RunAsync(request, cancellationToken);
        }
    }

    private sealed class NoopGenerator : ICoverageRunReportGenerator
    {
        public Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The consumer-host admission branch must not generate reports.");
    }
}

[CollectionDefinition("Evidence reuse synthetic local context", DisableParallelization = true)]
public sealed class EvidenceReuseSyntheticLocalContextCollection
{
    public const string Name = "Evidence reuse synthetic local context";
}
