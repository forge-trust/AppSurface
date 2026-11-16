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
    public async Task SdkChangeDuringPreparationCannotRestoreAReceiptForThePreviousSdkIdentity()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        var (plan, solution) = await CreatePlanAsync(repository);
        using var fixture = new SdkProcessFixture(repository.Root, "change");
        var resolver = Resolver(out var runner, fixture.ExecutablePath);
        var first = await RunAsync("first-run");
        var second = await RunAsync("second-run");
        var freshTestCount = runner.TestInvocationCount;
        var restored = await RunAsync("restored-run");

        Assert.Equal(EvidenceClaimKind.TargetedComplete, first.Manifest.ClaimKind);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, second.Manifest.ClaimKind);
        var firstKey = Assert.IsType<string>(Assert.Single(first.Decisions).ProducerKey);
        var secondKey = Assert.IsType<string>(Assert.Single(second.Decisions).ProducerKey);
        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(EvidenceClaimKind.LocalReusedComplete, restored.Manifest.ClaimKind);
        Assert.Equal(secondKey, Assert.Single(restored.Decisions).ProducerKey);
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
        EvidenceIdentityCoverageTests.TestRepository repository)
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
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [producer],
            [new("coverage", "test", "Declared coverage", [producer.Id], "coverage/assertion@1")]);
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
            var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
            var source = TestPathUtils.PathUnder(repository, "tests", "ForgeTrust.AppSurface.CoverageLeaseProbe", "bin", configuration, "net10.0");
            var executable = "ForgeTrust.AppSurface.CoverageLeaseProbe" + (OperatingSystem.IsWindows() ? ".exe" : "");
            Assert.True(File.Exists(TestPathUtils.PathUnder(source, executable)));
            var bin = TestPathUtils.PathUnder(root, "sdk-bin");
            Directory.CreateDirectory(bin);
            foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, TestPathUtils.PathUnder(bin, Path.GetFileName(file)));
            var sdk = TestPathUtils.PathUnder(bin, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            ExecutablePath = sdk;
            if (mode == "unlaunchable") File.WriteAllText(sdk, "This fixture is not an executable SDK.");
            else File.Copy(TestPathUtils.PathUnder(bin, executable), sdk);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(sdk, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
