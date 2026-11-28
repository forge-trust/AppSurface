using System.Diagnostics;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceSdkIdentityFailureCoverageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("dotnet")]
    [InlineData(".")]
    public void SdkQueryTestOverrideRequiresAnExplicitAbsoluteExecutable(string path)
    {
        var failure = Assert.Throws<ArgumentException>(() => Resolver(out _, path));
        Assert.Equal("sdkExecutableForTests", failure.ParamName);
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("blank")]
    [InlineData("long")]
    [InlineData("unlaunchable")]
    [InlineData("deadline")]
    [InlineData("fail-after-first")]
    public async Task UnavailableSdkIdentityKeepsFreshCoveragePassingWithoutPublishingAReceipt(string mode)
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository);
        using var fixture = new SdkProcessFixture(repository.Root, mode);
        var resolver = Resolver(out var runner, fixture.ExecutablePath);
        var output = TestPathUtils.PathUnder(repository.Root, "run");

        var result = await resolver.RunAsync(plan, null, solution, output, false, false,
            TestPathUtils.PathUnder(repository.Root, "receipts"), TimeSpan.FromSeconds(3),
            TextWriter.Null, TextWriter.Null, CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, result.Manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, result.Manifest.ClaimKind);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.Manifest.ProducerResults).Outcome);
        var decision = Assert.Single(result.Decisions);
        Assert.Equal("Ineligible", decision.Status);
        // A later failure inside the key callback retains its generic, exception-redacted reason.
        Assert.Equal(mode == "fail-after-first" ? "reuse.material_failed" : "reuse.sdk_unavailable", decision.Reason);
        if (mode == "fail-after-first")
            Assert.True(File.Exists(TestPathUtils.PathUnder(repository.Root, "sdk-probe.first-query")));
        Assert.Equal("suppressed", decision.Publication);
        Assert.DoesNotContain("private-sdk-probe-error-marker", decision.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        if (mode != "unlaunchable")
            Assert.Equal(mode, await File.ReadAllTextAsync(TestPathUtils.PathUnder(repository.Root, "sdk-probe.observed")));
        Assert.True(runner.TestInvocationCount > 0);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output, "coverage", "coverage.cobertura.xml")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkPipeDrainHonorsDeadlineAndCancellationAfterTheRootExits(bool cancelCaller)
    {
        var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository);
        var fixture = new SdkProcessFixture(repository.Root, "root-exits-with-pipe-child");
        using var cancellation = new CancellationTokenSource();
        using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Process? child = null;
        var settled = false;
        var explaining = Resolver(out _, fixture.ExecutablePath).ExplainAsync(plan, null, solution,
            TestPathUtils.PathUnder(repository.Root, "receipts"), TimeSpan.FromSeconds(3), cancellation.Token);
        try
        {
            var childPid = TestPathUtils.PathUnder(repository.Root, "sdk-probe.child-pid");
            var rootPid = TestPathUtils.PathUnder(repository.Root, "sdk-probe.root-pid");
            while (!File.Exists(childPid) || !File.Exists(rootPid))
            {
                // gstack-timing-ok: readiness waits for published PIDs, not elapsed-time evidence.
                await Task.Delay(TimeSpan.FromMilliseconds(20), readiness.Token);
            }
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childPid, readiness.Token)));
            try
            {
                using var root = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(rootPid, readiness.Token)));
                await root.WaitForExitAsync(readiness.Token);
            }
            catch (ArgumentException) { /* The independently identified root already exited. */ }
            Assert.False(child.HasExited);
            Assert.False(explaining.IsCompleted);

            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => explaining.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                var decision = Assert.Single(await explaining.WaitAsync(TimeSpan.FromSeconds(40)));
                Assert.Equal("reuse.sdk_unavailable", decision.Reason);
            }
            Assert.False(child.HasExited); // Returning unavailable is not a claim that an unregistered child settled.
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(repository.Root, "receipts")));
        }
        finally
        {
            try
            {
                cancellation.Cancel();
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(repository.Root, "stop-sdk-probe"), "stop");
                if (child is not null)
                {
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        if (!child.HasExited) child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                try { await explaining.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (explaining.IsCompleted) { }
                settled = explaining.IsCompleted && (child?.HasExited ?? true);
            }
            finally
            {
                fixture.Dispose();
                child?.Dispose();
                if (settled) repository.Dispose();
            }
        }
    }

    [Fact]
    public async Task ExplainMapsFailedSdkQueryToSafeUnavailableSummary()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository);
        using var fixture = new SdkProcessFixture(repository.Root, "nonzero");
        var resolver = Resolver(out var runner, fixture.ExecutablePath);
        var store = TestPathUtils.PathUnder(repository.Root, "receipts");

        var decision = Assert.Single(await resolver.ExplainAsync(plan, null, solution, store,
            TimeSpan.FromSeconds(3), CancellationToken.None));

        Assert.Equal("Ineligible", decision.Status);
        Assert.Equal("reuse.sdk_unavailable", decision.Reason);
        Assert.Equal("The actual dotnet SDK version could not be resolved; execute fresh or repair the local SDK installation.",
            decision.Diagnostic);
        Assert.Equal("suppressed", decision.Publication);
        Assert.DoesNotContain("private-sdk-probe-error-marker", decision.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("nonzero", await File.ReadAllTextAsync(TestPathUtils.PathUnder(repository.Root, "sdk-probe.observed")));
        Assert.Equal(0, runner.TestInvocationCount);
    }

    [Fact]
    public async Task ExplainSharesInitialSdkProbeAcrossCoverageProducersAndKeepsLiveQueries()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository, producerCount: 2);
        using var fixture = new SdkProcessFixture(repository.Root, "count");
        var resolver = Resolver(out var runner, fixture.ExecutablePath);
        var store = TestPathUtils.PathUnder(repository.Root, "receipts");

        var decisions = await resolver.ExplainAsync(plan, null, solution, store,
            TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision =>
        {
            Assert.Equal("Miss", decision.Status);
            Assert.NotNull(decision.ProducerKey);
        });
        var invocationCount = await File.ReadAllLinesAsync(TestPathUtils.PathUnder(repository.Root, "sdk-probe.invocations"));
        // One invocation-wide query plus one live key-material query for each coverage producer.
        Assert.Equal(3, invocationCount.Length);
        Assert.Equal(0, runner.TestInvocationCount);
        Assert.False(Directory.Exists(store));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplainSkipsSdkProbeForOutsideWorktreeSolution(bool solutionExists)
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, _) = await CreatePlanAsync(repository);
        using var outside = TestDirectory.Create();
        var outsideSolution = TestPathUtils.PathUnder(outside.Path, "outside.slnx");
        if (solutionExists) await File.WriteAllTextAsync(outsideSolution, "outside solution");
        using var fixture = new SdkProcessFixture(repository.Root, "count");
        var resolver = Resolver(out var runner, fixture.ExecutablePath);
        var store = TestPathUtils.PathUnder(repository.Root, "receipts");

        var decision = Assert.Single(await resolver.ExplainAsync(plan, null, outsideSolution, store,
            TimeSpan.FromSeconds(3), CancellationToken.None));

        Assert.Equal("Ineligible", decision.Status);
        Assert.Equal("reuse.solution_required", decision.Reason);
        Assert.Null(decision.ProducerKey);
        Assert.Equal(0, runner.TestInvocationCount);
        Assert.False(File.Exists(TestPathUtils.PathUnder(repository.Root, "sdk-probe.invocations")));
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public async Task SdkChangeDuringPreparationCannotRestoreAReceiptForThePreviousSdkIdentity()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository);
        using var fixture = new SdkProcessFixture(repository.Root, "stable");
        var resolver = Resolver(out var runner, fixture.ExecutablePath);
        var first = await RunAsync("first-run");
        var seededTestCount = runner.TestInvocationCount;
        var stableA = await RunAsync("stable-a-control");
        Assert.Equal(EvidenceClaimKind.LocalReusedComplete, stableA.Manifest.ClaimKind);
        Assert.Equal(Assert.Single(first.Decisions).ProducerKey, Assert.Single(stableA.Decisions).ProducerKey);
        Assert.Equal(seededTestCount, runner.TestInvocationCount);

        fixture.SetMode("change");
        var second = await RunAsync("second-run");
        Assert.True(runner.TestInvocationCount > seededTestCount);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(second.Manifest.ProducerResults).Outcome);
        // The transition itself cannot authorize reuse or publication. Seed a stable B execution
        // before asserting that the subsequent B invocation restores that receipt.
        var stableB = await RunAsync("stable-b-control");
        Assert.Equal(EvidenceClaimKind.TargetedComplete, stableB.Manifest.ClaimKind);
        var freshTestCount = runner.TestInvocationCount;
        var restored = await RunAsync("restored-run");

        Assert.Equal(EvidenceClaimKind.TargetedComplete, first.Manifest.ClaimKind);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, second.Manifest.ClaimKind);
        var firstKey = Assert.IsType<string>(Assert.Single(first.Decisions).ProducerKey);
        var secondKey = Assert.IsType<string>(Assert.Single(second.Decisions).ProducerKey);
        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(EvidenceClaimKind.LocalReusedComplete, restored.Manifest.ClaimKind);
        Assert.Equal(Assert.Single(stableB.Decisions).ProducerKey, Assert.Single(restored.Decisions).ProducerKey);
        Assert.NotEqual(firstKey, Assert.Single(stableB.Decisions).ProducerKey);
        Assert.Equal("restored", Assert.Single(restored.Decisions).Publication);
        Assert.Equal(freshTestCount, runner.TestInvocationCount);
        Assert.True(File.Exists(TestPathUtils.PathUnder(repository.Root, "sdk-probe.first-query")));
        Assert.Equal("change", await File.ReadAllTextAsync(TestPathUtils.PathUnder(repository.Root, "sdk-probe.observed")));

        Task<EvidenceReuseRunResult> RunAsync(string name) => resolver.RunAsync(plan, null, solution,
            TestPathUtils.PathUnder(repository.Root, name), false, false,
            TestPathUtils.PathUnder(repository.Root, "receipts"), TimeSpan.FromSeconds(3),
            TextWriter.Null, TextWriter.Null, CancellationToken.None);
    }

    [Fact]
    public async Task ClosedWaitDiagnosticWriterDoesNotChangeContendedReceiptExecution()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository);
        await repository.CommitAsync();
        using var fixture = new SdkProcessFixture(repository.Root, "stable");
        var producerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testCalls = 0;
        var resolver = Resolver(out var runner, fixture.ExecutablePath, () =>
        {
            if (Interlocked.Increment(ref testCalls) == 1)
            {
                producerEntered.TrySetResult();
                releaseProducer.Task.GetAwaiter().GetResult();
            }
        });
        Task<EvidenceReuseRunResult>? first = null;
        Task<EvidenceReuseRunResult>? waiting = null;
        var diagnosticWriter = new ThrowOnProgressWriter();
        var store = TestPathUtils.PathUnder(repository.Root, "receipts");

        try
        {
            first = RunAsync("first", TextWriter.Null);
            await producerEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            waiting = RunAsync("waiting", diagnosticWriter);

            var progress = await diagnosticWriter.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(9));
            Assert.StartsWith("reuse.waiting key=", progress, StringComparison.Ordinal);
            releaseProducer.TrySetResult();

            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(20));
            var waitingResult = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(EvidenceExecutionVerdict.Passed, firstResult.Manifest.ExecutionVerdict);
            Assert.Equal(EvidenceExecutionVerdict.Passed, waitingResult.Manifest.ExecutionVerdict);
            Assert.Equal(EvidenceProducerOutcome.Reused, Assert.Single(waitingResult.Manifest.ProducerResults).Outcome);
            Assert.Equal(EvidenceClaimKind.LocalReusedComplete, waitingResult.Manifest.ClaimKind);
            Assert.Equal(1, diagnosticWriter.WriteAttempts);
            Assert.Equal(1, runner.TestInvocationCount);
        }
        finally
        {
            releaseProducer.TrySetResult();
            await JoinIfStarted(first);
            await JoinIfStarted(waiting);
        }

        Task<EvidenceReuseRunResult> RunAsync(string name, TextWriter error) => resolver.RunAsync(plan, null, solution,
            TestPathUtils.PathUnder(repository.Root, name), false, false, store, TimeSpan.FromSeconds(12),
            TextWriter.Null, error, CancellationToken.None);

        static async Task JoinIfStarted(Task<EvidenceReuseRunResult>? task)
        {
            if (task is null) return;
            try { await task.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception) when (task.IsCompleted) { }
        }
    }

    [Fact]
    public async Task SuccessfulStringWriterReportsContentionBeforeReceiptIsReused()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository);
        await repository.CommitAsync();
        using var fixture = new SdkProcessFixture(repository.Root, "stable");
        var producerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testCalls = 0;
        var resolver = Resolver(out var runner, fixture.ExecutablePath, () =>
        {
            if (Interlocked.Increment(ref testCalls) == 1)
            {
                producerEntered.TrySetResult();
                releaseProducer.Task.GetAwaiter().GetResult();
            }
        });
        Task<EvidenceReuseRunResult>? first = null;
        Task<EvidenceReuseRunResult>? waiting = null;
        using var diagnosticWriter = new SuccessfulProgressWriter();
        var store = TestPathUtils.PathUnder(repository.Root, "receipts");

        try
        {
            first = RunAsync("first", TextWriter.Null);
            await producerEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            waiting = RunAsync("waiting", diagnosticWriter);

            var progress = await diagnosticWriter.Written.Task.WaitAsync(TimeSpan.FromSeconds(9));
            Assert.StartsWith("reuse.waiting key=", progress, StringComparison.Ordinal);
            Assert.False(releaseProducer.Task.IsCompleted);
            releaseProducer.TrySetResult();

            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(20));
            var waitingResult = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(EvidenceExecutionVerdict.Passed, firstResult.Manifest.ExecutionVerdict);
            Assert.Equal(EvidenceExecutionVerdict.Passed, waitingResult.Manifest.ExecutionVerdict);
            Assert.Equal(EvidenceProducerOutcome.Reused, Assert.Single(waitingResult.Manifest.ProducerResults).Outcome);
            Assert.Equal(EvidenceClaimKind.LocalReusedComplete, waitingResult.Manifest.ClaimKind);
            Assert.Equal(1, runner.TestInvocationCount);
            Assert.Contains("reuse.waiting key=", diagnosticWriter.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            releaseProducer.TrySetResult();
            await JoinIfStarted(first);
            await JoinIfStarted(waiting);
        }

        Task<EvidenceReuseRunResult> RunAsync(string name, TextWriter error) => resolver.RunAsync(plan, null, solution,
            TestPathUtils.PathUnder(repository.Root, name), false, false, store, TimeSpan.FromSeconds(12),
            TextWriter.Null, error, CancellationToken.None);

        static async Task JoinIfStarted(Task<EvidenceReuseRunResult>? task)
        {
            if (task is null) return;
            try { await task.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception) when (task.IsCompleted) { }
        }
    }

    [Fact]
    public async Task CancellingSdkDiscoveryKillsAndJoinsTheActualQueryBeforeReturningCancellation()
    {
        var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        EvidencePlan plan;
        string solution;
        SdkProcessFixture fixture;
        try
        {
            (plan, solution) = await CreatePlanAsync(repository);
            fixture = new SdkProcessFixture(repository.Root, "cancel");
        }
        catch
        {
            repository.Dispose();
            throw;
        }
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? query = null;
        var settled = false;
        var explaining = Resolver(out _, fixture.ExecutablePath).ExplainAsync(plan, null, solution,
            TestPathUtils.PathUnder(repository.Root, "receipts"), TimeSpan.FromSeconds(3), cancellation.Token);
        try
        {
            var pid = TestPathUtils.PathUnder(repository.Root, "sdk-probe.pid");
            while (!File.Exists(pid))
            {
                deadline.Token.ThrowIfCancellationRequested();
                // gstack-timing-ok: bounded readiness poll for the actual SDK query, not elapsed-time evidence.
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            query = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pid, deadline.Token)));
            Assert.False(explaining.IsCompleted);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => explaining.WaitAsync(deadline.Token));
            Assert.True(query.HasExited);
        }
        finally
        {
            try
            {
                cancellation.Cancel();
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(repository.Root, "stop-sdk-probe"), "stop");
                if (query is not null)
                {
                    try { await query.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        if (!query.HasExited) query.Kill(entireProcessTree: true);
                        await query.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                try { await explaining.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (explaining.IsCompleted) { }
                settled = explaining.IsCompleted && (query?.HasExited ?? true);
            }
            finally
            {
                fixture.Dispose();
                query?.Dispose();
                // A failed actual join retains the private fixture for the independently bounded probe.
                if (settled) repository.Dispose();
            }
        }
    }

    private static EvidenceReuseExecutionResolver Resolver(out EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner runner,
        string? executable = null, Action? onTestInvocation = null)
    {
        runner = new("tests/Sample.Tests/Sample.Tests.csproj", onTestInvocation);
        return new(new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System))), executable);
    }

    private static async Task<(EvidencePlan Plan, string Solution)> CreatePlanAsync(
        EvidenceIdentityCoverageTests.TestRepository repository, int producerCount = 1)
    {
        await repository.WriteAsync("src/input.txt", "declared input");
        await repository.WriteAsync("sample.slnx", "solution fixture");
        await repository.WriteAsync("tests/Sample.Tests/Sample.Tests.csproj", "<Project />");
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot()], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            Inputs = new(["src/**"], []),
            CoverageExecution = new(TestResults: EvidenceCoverageTestResultFormat.Junit) { CooperativeChildLifetime = true },
        };
        var producers = Enumerable.Range(0, producerCount).Select(index => index == 0 ? producer : producer with
        {
            Id = $"coverage-{index}",
            AssertionIds = [$"coverage/assertion-{index}@1"]
        }).ToArray();
        var obligations = producers.Select(item => new EvidenceObligation(item.Id, "test", "Declared coverage",
            [item.Id], item.AssertionIds.Single())).ToArray();
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], producers, obligations);
        var plan = new EvidencePlanner().Resolve(new("sdk-fixture", "1", profile.Id, [profile],
            [new("source", "src/**", profile.Id)]), [new NormalizedDiffPath("src/input.txt")]);
        return (plan, TestPathUtils.PathUnder(repository.Root, "sample.slnx"));
    }

    private sealed class SdkProcessFixture : IDisposable
    {
        private readonly string _directory;
        private readonly Dictionary<string, string?> _environment;
        internal string ExecutablePath { get; }

        internal SdkProcessFixture(string root, string mode)
        {
            var bin = TestPathUtils.PathUnder(root, "sdk-bin");
            var sdk = CoverageLeaseProbeStager.Stage(bin, "dotnet");
            ExecutablePath = sdk;
            if (mode == "unlaunchable")
            {
                // Replace the content in place so the staged Unix execute permissions remain intact.
                File.WriteAllText(sdk, "This fixture is not an executable SDK.");
            }
            _directory = Directory.GetCurrentDirectory();
            var values = new Dictionary<string, string?>
            {
                ["APPSURFACE_IDENTITY_SDK_PROBE_ROOT"] = root,
                ["APPSURFACE_IDENTITY_SDK_PROBE_MODE"] = mode,
                ["APPSURFACE_ARTIFACT_HOME"] = TestPathUtils.PathUnder(root, "history"),
                ["GITHUB_ACTIONS"] = null,
            };
            _environment = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
                Directory.SetCurrentDirectory(root);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_directory);
            foreach (var (name, value) in _environment) Environment.SetEnvironmentVariable(name, value);
        }

        internal void SetMode(string mode) => Environment.SetEnvironmentVariable("APPSURFACE_IDENTITY_SDK_PROBE_MODE", mode);
    }

    private sealed class ThrowOnProgressWriter : TextWriter
    {
        internal TaskCompletionSource<string> Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WriteAttempts { get; private set; }
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            Attempted.TrySetResult(value ?? string.Empty);
            throw new IOException("The diagnostic sink is closed.");
        }
    }

    private sealed class SuccessfulProgressWriter : StringWriter
    {
        internal TaskCompletionSource<string> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            Written.TrySetResult(value ?? string.Empty);
        }
    }
}
