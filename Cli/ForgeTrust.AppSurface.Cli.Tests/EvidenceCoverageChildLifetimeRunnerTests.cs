using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceCoverageChildLifetimeRunnerTests
{
    [Fact]
    public void MissingRunnerOrChannelCannotConstructAnExecutionWrapperOrStartWork()
    {
        var runner = new CaptureRunner();

        Assert.Throws<ArgumentNullException>(() =>
            new EvidenceCoverageProcessRunner(runner, (EvidenceChildLifetimeSession)null!));
        Assert.Throws<ArgumentNullException>(() =>
            new EvidenceCoverageProcessRunner(null!, new Dictionary<string, string>()));
        Assert.Throws<ArgumentNullException>(() =>
            new EvidenceCoverageProcessRunner(runner, (IReadOnlyDictionary<string, string>)null!));

        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task ListenerLossBeforeDispatchRejectsDotnetRequestWithoutCallingInnerRunner()
    {
        await using var session = EvidenceChildLifetimeSession.Start("runner-listener-loss-" + Guid.NewGuid().ToString("N"),
            beforeListenerCreation: () => throw new IOException("Controlled listener creation failure."));
        var inner = new CaptureRunner();
        var wrapper = new EvidenceCoverageProcessRunner(inner, session);

        Assert.False(session.CanAcceptChildren);
        await Assert.ThrowsAsync<IOException>(() => wrapper.RunAsync(Request("dotnet", ["test"]), CancellationToken.None));

        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task EnrollmentClosedAfterWrapperCreationRejectsDotnetRequestBeforeDispatch()
    {
        await using var session = EvidenceChildLifetimeSession.Start("runner-session-loss-" + Guid.NewGuid().ToString("N"));
        var inner = new CaptureRunner();
        var wrapper = new EvidenceCoverageProcessRunner(inner, session);
        session.CloseEnrollment();

        await Assert.ThrowsAsync<IOException>(() => wrapper.RunAsync(Request("dotnet", ["build"]), CancellationToken.None));

        Assert.Empty(inner.Requests);
    }

    [Theory]
    [InlineData("msbuild", false)]
    [InlineData("build", true)]
    [InlineData("test", true)]
    public async Task SupportedSdkOperationsReceiveIsolationBeforeApplicationArguments(string operation, bool disableServers)
    {
        var inner = new CaptureRunner();
        var originalEnvironment = new Dictionary<string, string?> { ["retained"] = "value", ["channel"] = "original", ["removed"] = null };
        var channel = new Dictionary<string, string> { ["channel"] = "invocation" };
        var wrapper = new EvidenceCoverageProcessRunner(inner, channel);
        channel["channel"] = "mutated after construction";
        var request = Request("dotnet", [operation, "project.csproj", "--", "application-token"]) with
        {
            EnvironmentOverrides = originalEnvironment,
            StandardOutputLimitBytes = 12345,
            StrictUtf8 = true,
            StandardInputBytes = [1, 2, 3],
            BudgetCharge = static _ => { },
        };
        using var cancellation = new CancellationTokenSource();
        await wrapper.RunAsync(request, cancellation.Token);
        var actual = Assert.Single(inner.Requests);
        Assert.Equal(cancellation.Token, Assert.Single(inner.Tokens));
        Assert.Equal("invocation", actual.EnvironmentOverrides!["channel"]);
        Assert.Equal("value", actual.EnvironmentOverrides["retained"]);
        Assert.True(actual.EnvironmentOverrides.ContainsKey("removed"));
        Assert.Null(actual.EnvironmentOverrides["removed"]);
        Assert.Equal("0", actual.EnvironmentOverrides["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.Equal("1", actual.EnvironmentOverrides["MSBUILDDISABLENODEREUSE"]);
        var isolationArguments = disableServers
            ? new[] { "--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false" }
            : new[] { "-nodeReuse:false", "-p:UseSharedCompilation=false" };
        Assert.Equal(new[] { operation, "project.csproj" }.Concat(isolationArguments).Concat(["--", "application-token"]), actual.Arguments);
        Assert.Contains($"environment:DOTNET_CLI_USE_MSBUILD_SERVER={actual.EnvironmentOverrides["DOTNET_CLI_USE_MSBUILD_SERVER"]}",
            EvidenceCoverageProcessRunner.EffectiveArgumentIdentity);
        Assert.Contains($"environment:MSBUILDDISABLENODEREUSE={actual.EnvironmentOverrides["MSBUILDDISABLENODEREUSE"]}",
            EvidenceCoverageProcessRunner.EffectiveArgumentIdentity);
        foreach (var argument in isolationArguments)
            Assert.Contains($"{operation}:{argument}", EvidenceCoverageProcessRunner.EffectiveArgumentIdentity);
        Assert.Same(request.Lease, actual.Lease);
        Assert.Equal(request.StandardOutputLimitBytes, actual.StandardOutputLimitBytes);
        Assert.Equal(request.StrictUtf8, actual.StrictUtf8);
        Assert.Equal(request.StandardInputBytes, actual.StandardInputBytes);
        Assert.Same(request.BudgetCharge, actual.BudgetCharge);
        Assert.Same(request.OutputObserver, actual.OutputObserver);
        Assert.Equal(request.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(request.OutputFile, actual.OutputFile);
        Assert.Equal(new[] { operation, "project.csproj", "--", "application-token" }, request.Arguments);
        Assert.Equal("original", originalEnvironment["channel"]);
        Assert.Equal(3, originalEnvironment.Count);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--info")]
    [InlineData("sln")]
    public async Task OtherDotnetCommandsReceiveChannelWithoutUnsupportedFlags(string operation)
    {
        var inner = new CaptureRunner();
        var wrapper = new EvidenceCoverageProcessRunner(inner, new Dictionary<string, string> { ["channel"] = "one" });
        var request = Request(Path.Join("sdk", "dotnet"), [operation]);
        await wrapper.RunAsync(request, CancellationToken.None);
        var actual = Assert.Single(inner.Requests);
        Assert.Equal(request.Arguments, actual.Arguments);
        Assert.Equal("one", actual.EnvironmentOverrides!["channel"]);
        Assert.Null(request.EnvironmentOverrides);
    }

    [Fact]
    public async Task NonDotnetCommandsArePassedThroughWithoutChannelOrIsolation()
    {
        var inner = new CaptureRunner();
        var wrapper = new EvidenceCoverageProcessRunner(inner, new Dictionary<string, string> { ["channel"] = "one" });
        var request = Request("consumer-tool", ["build"]);
        await wrapper.RunAsync(request, CancellationToken.None);
        Assert.Same(request, Assert.Single(inner.Requests));
    }

    [Fact]
    public async Task ConcurrentInvocationsKeepChannelsAndParentEnvironmentIndependent()
    {
        var inner = new CaptureRunner();
        var key = "APPSURFACE_TEST_CHANNEL_" + Guid.NewGuid().ToString("N");
        var before = Environment.GetEnvironmentVariable(key);
        var first = new EvidenceCoverageProcessRunner(inner, new Dictionary<string, string> { [key] = "first" });
        var second = new EvidenceCoverageProcessRunner(inner, new Dictionary<string, string> { [key] = "second" });
        var original = Request("dotnet.exe", ["test"]);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).RunAsync(original, CancellationToken.None))));
        Assert.Equal(16, inner.Requests.Count(request => request.EnvironmentOverrides![key] == "first"));
        Assert.Equal(16, inner.Requests.Count(request => request.EnvironmentOverrides![key] == "second"));
        Assert.Equal(before, Environment.GetEnvironmentVariable(key));
        Assert.Null(original.EnvironmentOverrides);
        Assert.Equal(new[] { "test" }, original.Arguments);
        Assert.DoesNotContain(EvidenceCoverageProcessRunner.EffectiveArgumentIdentity, token => token.Contains(key, StringComparison.Ordinal));
        Assert.DoesNotContain(EvidenceCoverageProcessRunner.EffectiveArgumentIdentity, token => token.Contains("first", StringComparison.Ordinal)
            || token.Contains("second", StringComparison.Ordinal));
        AssertStableIsolationIdentity();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliWrapAppliesEnvironmentToBufferedAndStreamingProcessesWithoutGlobalMutation(bool streamed)
    {
        using var directory = TestDirectory.Create();
        const string key = "APPSURFACE_TEST_PROCESS_ENV";
        var before = Environment.GetEnvironmentVariable(key);
        var nonce = Guid.NewGuid().ToString("N");
        var output = streamed ? Path.Join(directory.Path, "process.log") : null;
        var request = Request(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows()
            ? ["/d", "/c", "echo %APPSURFACE_TEST_PROCESS_ENV%"]
            : ["-c", "printf '%s' \"$APPSURFACE_TEST_PROCESS_ENV\""]) with
        {
            WorkingDirectory = directory.Path,
            OutputFile = output,
            EnvironmentOverrides = new Dictionary<string, string?> { [key] = nonce }
        };
        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(nonce, (streamed ? await File.ReadAllTextAsync(output!) : result.StandardOutput!).Trim());
        Assert.Equal(before, Environment.GetEnvironmentVariable(key));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdapterClonesInjectedEngineOnlyForExplicitContractAndSession(bool cooperative, bool withSession)
    {
        using var directory = TestDirectory.Create();
        var solution = Path.Join(directory.Path, "sample.slnx");
        var project = Path.Join(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(solution, "{}");
        await File.WriteAllTextAsync(project, "<Project />");
        var runner = new WorkflowRunner();
        var generator = new Generator();
        var summaryCount = 0;
        var timingsCount = 0;
        var manifestCount = 0;
        var workflow = new CoverageRunWorkflow(runner, generator, TimeProvider.System,
            summaryStaged: () => summaryCount++, timingsStaged: () => timingsCount++,
            writeProjectManifest: async (output, root, model, token) =>
            {
                manifestCount++;
                await CoverageProjectManifest.WriteAsync(output, root, model, token);
            });
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(workflow));
        await using var session = EvidenceChildLifetimeSession.Start(Guid.NewGuid().ToString("N"));
        var declaration = Declaration() with { CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = cooperative } };
        var custody = new EvidenceArtifactWriter(declaration, Path.Join(directory.Path, "custody"));
        var result = await adapter.RunWithInventoryAsync(declaration, solution, Path.Join(directory.Path, "run"), null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None,
            custody, withSession ? session : null);
        Assert.Equal(EvidenceProducerOutcome.Passed, result.Result.Outcome);
        Assert.Equal(1, summaryCount);
        Assert.Equal(1, timingsCount);
        Assert.Equal(1, manifestCount);
        Assert.Equal(1, generator.MergeCount);
        Assert.Contains(runner.Requests, request => request.Arguments[0] == "sln");
        Assert.Contains(runner.Requests, request => request.Arguments[0] == "msbuild");
        Assert.Contains(runner.Requests, request => request.Arguments[0] == "build");
        Assert.Contains(runner.Requests, request => request.Arguments[0] == "test");
        foreach (var request in runner.Requests)
        {
            if (cooperative && withSession)
            {
                foreach (var pair in session.Environment) Assert.Equal(pair.Value, request.EnvironmentOverrides![pair.Key]);
                Assert.Equal("1", request.EnvironmentOverrides!["MSBUILDDISABLENODEREUSE"]);
                if (request.Arguments[0] is "msbuild" or "build" or "test")
                    Assert.Contains("-p:UseSharedCompilation=false", request.Arguments);
            }
            else
            {
                Assert.Null(request.EnvironmentOverrides);
                Assert.DoesNotContain("-nodeReuse:false", request.Arguments);
            }
        }
        if (!cooperative) Assert.Equal("reuse.child_lifetime_required", result.InventoryIneligibilityReason);
        else if (!withSession) Assert.Equal("reuse.child_lifetime_session_required", result.InventoryIneligibilityReason);
        else
        {
            Assert.Null(result.InventoryIneligibilityReason);
            Assert.NotEmpty(result.Result.Artifacts!);
            Assert.True(await custody.VerifyWrittenArtifactsAsync());
        }
        var previouslyRecorded = runner.Requests.Count;
        var legacy = await adapter.RunAsync(Declaration(), solution, Path.Join(directory.Path, "legacy-run"), null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Passed, legacy.Outcome);
        Assert.All(runner.Requests.Skip(previouslyRecorded), request => Assert.Null(request.EnvironmentOverrides));
        Assert.False(session.IsSettled);
        // The adapter does not close enrollment per command or producer; the coordinator owns this transition.
        session.CloseEnrollment();
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedCustodyKeepsFreshManifestPassedUnlessConsumerExplicitlyRequiresArtifacts(bool explicitlyRequired)
    {
        using var directory = TestDirectory.Create();
        var solution = Path.Join(directory.Path, "sample.slnx");
        var project = Path.Join(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(solution, "{}");
        await File.WriteAllTextAsync(project, "<Project />");
        var recommended = CoverageEvidenceInventory.CreateSlot();
        Assert.False(recommended.Required);
        Assert.Equal(128, recommended.MaximumFiles);
        Assert.Equal(256L * 1024 * 1024, recommended.MaximumBytes);
        var declaration = Declaration() with
        {
            ArtifactSlots = [recommended with { Required = explicitlyRequired, MaximumBytes = 1 }],
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true }
        };
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(new WorkflowRunner(), new Generator(), TimeProvider.System)));
        await using var session = EvidenceChildLifetimeSession.Start(Guid.NewGuid().ToString("N"));
        var output = Path.Join(directory.Path, "run");
        var custodyRoot = Path.Join(directory.Path, "custody");
        var writer = new EvidenceArtifactWriter(declaration, custodyRoot);
        var execution = await adapter.RunWithInventoryAsync(declaration, solution, output, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None, writer, session);
        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        Assert.Null(execution.Result.Artifacts);
        Assert.Equal("inventory-slot-ineligible", execution.InventoryIneligibilityReason);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(custodyRoot));
        foreach (var report in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
            Assert.True(File.Exists(Path.Join(output, "coverage", report)), report);
        var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("coverage-required", "coverage", "declared coverage gate", [declaration.Id], "coverage/assertion@1")]);
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        NormalizedDiffPath[] paths = [];
        var plan = new EvidencePlan("1.1", policy.Id, EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(paths), profile, paths, [], string.Empty, policy);
        plan = plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
        var manifest = EvidenceManifestBuilder.Build(plan, [execution.Result]);
        Assert.Equal(explicitlyRequired ? EvidenceExecutionVerdict.Invalid : EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        if (!explicitlyRequired)
        {
            Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
            Assert.Contains("coverage-required", manifest.ClosedObligationIds);
            var missingAssertion = EvidenceManifestBuilder.Build(plan, [execution.Result with { SatisfiedAssertionIds = [] }]);
            Assert.Equal(EvidenceExecutionVerdict.Incomplete, missingAssertion.ExecutionVerdict);
        }
        session.CloseEnrollment();
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public void CloneRejectsMissingDecoratorOrRunnerWithoutMutatingOriginal()
    {
        var original = new CoverageRunWorkflow(new WorkflowRunner(), new Generator(), TimeProvider.System);
        Assert.Throws<ArgumentNullException>(() => original.WithProcessRunner(null!));
        Assert.Throws<ArgumentNullException>(() => original.WithProcessRunner(_ => null!));
    }

    private static CoverageRunProcessRequest Request(string executable, IReadOnlyList<string> arguments) =>
        new(executable, arguments, Directory.GetCurrentDirectory(), null, null, CoverageRunProcessLease.Detached());

    private static void AssertStableIsolationIdentity()
    {
        string[] expected =
        [
            "build:--disable-build-servers",
            "build:-nodeReuse:false",
            "build:-p:UseSharedCompilation=false",
            "child-lifetime:cooperative-v1",
            "environment:DOTNET_CLI_USE_MSBUILD_SERVER=0",
            "environment:MSBUILDDISABLENODEREUSE=1",
            "msbuild:-nodeReuse:false",
            "msbuild:-p:UseSharedCompilation=false",
            "test:--disable-build-servers",
            "test:-nodeReuse:false",
            "test:-p:UseSharedCompilation=false",
        ];

        Assert.Equal(expected, EvidenceCoverageProcessRunner.EffectiveArgumentIdentity);
    }

    private static EvidenceProducerDeclaration Declaration() => new("coverage", "coverage", "1.0.0", [], ["coverage/assertion@1"],
        [CoverageEvidenceInventory.CreateSlot()], 60, new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0));

    private sealed class CaptureRunner : ICoverageRunProcessRunner
    {
        internal ConcurrentBag<CoverageRunProcessRequest> Requests { get; } = [];
        internal ConcurrentBag<CancellationToken> Tokens { get; } = [];
        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            return Task.FromResult(new CoverageRunProcessResult(0, "fake"));
        }
    }

    private const string Cobertura = "<coverage lines-covered=\"10\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";

    private sealed class WorkflowRunner : ICoverageRunProcessRunner
    {
        internal List<CoverageRunProcessRequest> Requests { get; } = [];
        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            request.Lease.Complete();
            if (request.Arguments[0] == "sln") return new(0, "Project(s)\n----------\ntests/Sample.Tests/Sample.Tests.csproj\n");
            if (request.Arguments[0] == "msbuild")
            {
                const string capabilities = """{"Properties":{"TestingPlatformDotnetTestSupport":"false","TargetFramework":"net10.0"},"Items":{"PackageReference":[{"Identity":"coverlet.collector"}]}}""";
                return new(0, capabilities, StandardOutput: capabilities);
            }
            if (request.OutputFile is not null) await File.WriteAllTextAsync(request.OutputFile, "fake output", cancellationToken);
            if (request.Arguments[0] == "test")
            {
                var index = request.Arguments.ToList().IndexOf("--results-directory");
                var raw = Path.Join(request.Arguments[index + 1], "collector");
                Directory.CreateDirectory(raw);
                await File.WriteAllTextAsync(Path.Join(raw, "coverage.cobertura.xml"), Cobertura, cancellationToken);
            }
            return new(0, "fake output");
        }
    }

    private sealed class Generator : ICoverageRunReportGenerator
    {
        internal int MergeCount { get; private set; }
        public async Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory, CancellationToken cancellationToken)
        {
            MergeCount++;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Join(outputDirectory, "Cobertura.xml");
            var summary = Path.Join(outputDirectory, "Summary.txt");
            await File.WriteAllTextAsync(path, Cobertura, cancellationToken);
            await File.WriteAllTextAsync(summary, "fake summary", cancellationToken);
            return new(0, path, summary);
        }
    }
}
