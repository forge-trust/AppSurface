using System.Security.Cryptography;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceCoverageCustodyDeadlineTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompletedOrdinaryDeliverySurvivesOnlyOptionalDeadlineExpiry(bool required, bool cancelCaller)
    {
        using var directory = TestDirectory.Create();
        var solution = TestPathUtils.PathUnder(directory.Path, "sample.slnx");
        await File.WriteAllTextAsync(solution, "solution fixture");
        var project = TestPathUtils.PathUnder(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        var declaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/assertion@1"],
            [CoverageEvidenceInventory.CreateSlot() with { Required = required }], 30, new EvidenceCoverageGateRequirements(95, 85))
        {
            CoverageExecution = new(TestResults: EvidenceCoverageTestResultFormat.Junit) { CooperativeChildLifetime = true },
        };
        var source = TestPathUtils.PathUnder(directory.Path, "execution");
        var final = TestPathUtils.PathUnder(directory.Path, "delivered");
        var writer = new EvidenceArtifactWriter(declaration, final);
        var runner = new EvidenceCoverageInventoryEdgeTests.FixtureProcessRunner("tests/Sample.Tests/Sample.Tests.csproj");
        var clock = new CustodyClock();
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(
            runner, new EvidenceCoverageInventoryEdgeTests.FixtureReportGenerator(), TimeProvider.System)), clock);
        using var cancellation = new CancellationTokenSource();
        await using var children = EvidenceChildLifetimeSession.Start("custody-deadline-" + Guid.NewGuid().ToString("N"));
        void ExpireAfterDelivery()
        {
            foreach (var report in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
                Assert.Equal(File.ReadAllBytes(TestPathUtils.PathUnder(source, "coverage", report)),
                    File.ReadAllBytes(TestPathUtils.PathUnder(final, "coverage", report)));
            if (cancelCaller) cancellation.Cancel();
            else clock.Advance(TimeSpan.FromSeconds(31));
        }
        Task<CoverageEvidenceProducerExecutionResult> Run() => adapter.RunWithInventoryAsync(declaration, solution, source,
            null, CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), cancellation.Token, writer, children,
            final, ExpireAfterDelivery);

        if (cancelCaller)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else
        {
            var result = await Run();
            Assert.Equal(EvidenceProducerOutcome.Passed, result.Result.Outcome);
            Assert.Equal("inventory-deadline", result.InventoryIneligibilityReason);
            Assert.Null(result.Inventory);
            Assert.Null(result.Result.Artifacts);
            Assert.Equal(!required, EvidenceArtifactValidation.AreValid(declaration, result.Result.Artifacts ?? []));
        }
        Assert.Empty(writer.WrittenArtifacts);
        Assert.True(runner.TestInvocationCount > 0);
        Assert.True(File.Exists(TestPathUtils.PathUnder(final, "coverage", "coverage-gate.json")));
        Assert.Empty(Directory.EnumerateFiles(final, ".report-*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("changed")]
    [InlineData("root")]
    [InlineData("limit")]
    [InlineData("cancel")]
    public async Task DeliveredCustodyAdoptionVerifiesTheCompleteSetWithoutRewritingReports(string scenario)
    {
        using var directory = TestDirectory.Create();
        var relative = "coverage/report.xml";
        var report = TestPathUtils.PathUnder(directory.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var bytes = "<coverage />"u8.ToArray();
        await File.WriteAllBytesAsync(report, bytes);
        var originalTime = File.GetLastWriteTimeUtc(report);
        var file = new CoverageEvidenceInventoryFile(relative, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var inventory = new CoverageEvidenceInventory(CoverageEvidenceInventory.CurrentVersion, [file]);
        var slot = CoverageEvidenceInventory.CreateSlot();
        if (scenario == "limit") slot = slot with { MaximumBytes = bytes.Length - 1 };
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], [], [slot], 30);
        var writer = new EvidenceArtifactWriter(producer, scenario == "root"
            ? TestPathUtils.PathUnder(directory.Path, "other") : directory.Path);
        using var cancellation = new CancellationTokenSource();
        if (scenario == "changed") await File.WriteAllTextAsync(report, "changed");
        if (scenario == "cancel") cancellation.Cancel();
        IReadOnlyList<EvidenceArtifactResult> Adopt() => inventory.AdoptDelivered(writer, slot, directory.Path, cancellation.Token);

        if (scenario == "success")
        {
            Assert.Equal(file.Sha256, Assert.Single(Adopt()).Sha256);
            Assert.True(EvidenceArtifactValidation.AreValid(producer, writer.WrittenArtifacts));
            Assert.Equal(originalTime, File.GetLastWriteTimeUtc(report));
        }
        else
        {
            if (scenario == "cancel") Assert.ThrowsAny<OperationCanceledException>(Adopt);
            else Assert.Throws<CoverageEvidenceInventoryException>(Adopt);
            Assert.Empty(writer.WrittenArtifacts);
        }
        Assert.Equal(scenario == "changed" ? "changed"u8.ToArray() : bytes, await File.ReadAllBytesAsync(report));
        Assert.Single(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    private sealed class CustodyClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state));
            _timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class ManualTimer(Action callback) : ITimer
        {
            private Action? _callback = callback;
            internal void Fire() => Interlocked.Exchange(ref _callback, null)?.Invoke();
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Interlocked.Exchange(ref _callback, null);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
