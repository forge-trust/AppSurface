using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Metadata and owned-task controls; fake lifecycle observations establish no peer or Trusted authority.</summary>
public sealed class EvidenceRestrictedCoverageFactoryTests
{
    private const string Canary = "restricted-factory-canary-779";

    [Fact]
    public void FactorySnapshotsEveryListAndProvidesInspectableFixedMetadata()
    {
        string[] resources = ["http"];
        string[] assertions = ["appsurface/coverage/behavioral-patch@1"];
        EvidenceArtifactSlot[] slots = [new("coverage-report", "coverage", "application/xml", true, 1024)];
        var declaration = Declaration() with { RequiredResources = resources, AssertionIds = assertions, ArtifactSlots = slots };
        var producer = EvidenceRestrictedCoverageProducerFactory.Create(declaration);
        resources[0] = "changed"; assertions[0] = "changed"; slots[0] = slots[0] with { MaximumBytes = 1 };

        IEvidenceProducer typed = producer;
        Assert.Equal("coverage", typed.Id);
        Assert.Equal("http", producer.Declaration.RequiredResources[0]);
        Assert.Equal("appsurface/coverage/behavioral-patch@1", producer.Declaration.AssertionIds[0]);
        Assert.Equal(1024, producer.Declaration.ArtifactSlots[0].MaximumBytes);
        Assert.Equal(declaration.CoverageGate, producer.Declaration.CoverageGate);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)producer.Declaration.RequiredResources)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)producer.Declaration.AssertionIds)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceArtifactSlot>)producer.Declaration.ArtifactSlots)[0] = slots[0]);
    }

    [Fact]
    public async Task FactoryCannotRunThroughMissingOrPublicWriterAndPublicWriterCannotBind()
    {
        using var directory = TestDirectory.Create();
        var declaration = Declaration(); var plan = Plan(declaration);
        var producer = EvidenceRestrictedCoverageProducerFactory.Create(declaration);
        var missing = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration, TimeProvider.System), CancellationToken.None));
        AssertSafe(missing, "ASEVD410");
        var writer = new EvidenceArtifactWriter(declaration, TestPathUtils.PathUnder(directory.Path, "artifacts"));
        var local = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration, TimeProvider.System, writer), CancellationToken.None));
        AssertSafe(local, "ASEVD410");
        AssertSafe(Assert.Throws<EvidenceAdmissionException>(() => writer.BindRestrictedProducerLease(
            null!, null!, plan, null, null!, null!, CancellationToken.None)), "ASEVD410");
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("id-null")]
    [InlineData("id-control")]
    [InlineData("id-limit")]
    [InlineData("resources-null")]
    [InlineData("resources-limit")]
    [InlineData("assertion-null")]
    [InlineData("assertion-limit")]
    [InlineData("slot-null")]
    [InlineData("slot-limit")]
    [InlineData("slot-path-limit")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-limit")]
    public void UnsupportedMetadataRejectsWithoutEchoOrInnerException(string change)
    {
        var declaration = Declaration();
        declaration = change switch
        {
            "kind" => declaration with { Kind = Canary }, "version" => declaration with { Version = "2.0.0" },
            "id-null" => declaration with { Id = null! }, "id-control" => declaration with { Id = Canary + "\n" },
            "id-limit" => declaration with { Id = new string('a', 97) },
            "resources-null" => declaration with { RequiredResources = null! },
            "resources-limit" => declaration with { RequiredResources = Enumerable.Range(0, 17).Select(i => "r" + i).ToArray() },
            "assertion-null" => declaration with { AssertionIds = [null!] },
            "assertion-limit" => declaration with { AssertionIds = Enumerable.Range(0, 129).Select(i => "a" + i).ToArray() },
            "slot-null" => declaration with { ArtifactSlots = [null!] },
            "slot-limit" => declaration with { ArtifactSlots = Enumerable.Repeat(new EvidenceArtifactSlot("slot", "coverage", "application/xml", true, 1024), 65).ToArray() },
            "slot-path-limit" => declaration with { ArtifactSlots = [new("slot", new string('a', 4096), "application/xml", true, 1024)] },
            "timeout-zero" => declaration with { TimeoutSeconds = 0 }, _ => declaration with { TimeoutSeconds = 1801 },
        };
        AssertSafe(Assert.Throws<EvidenceAdmissionException>(() => EvidenceRestrictedCoverageProducerFactory.Create(declaration)), "ASEVD404");
    }

    [Fact]
    public void NullDeclarationHasAnArgumentDiagnosticBeforeAnyExecution()
    {
        Assert.Equal("declaration", Assert.Throws<ArgumentNullException>(() => EvidenceRestrictedCoverageProducerFactory.Create(null!)).ParamName);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("latched")]
    [InlineData("completed")]
    [InlineData("run")]
    [InlineData("plan")]
    [InlineData("declaration")]
    [InlineData("absent")]
    [InlineData("duplicate")]
    public void PureLeaseMetadataRejectsStaleAndFullDeclarationPlanRunMismatch(string change)
    {
        var declaration = Declaration(); var plan = Plan(declaration);
        if (change == "absent") plan = plan with { Profile = plan.Profile with { Producers = [] } };
        if (change == "duplicate") plan = plan with { Profile = plan.Profile with { Producers = [declaration, declaration] } };
        var admission = new EvidenceAdmissionResult(plan, EvidenceExecutionMode.Observation, "fixture/1", null);
        if (change != "inactive") admission.Activate("metadata-only-not-a-real-output");
        if (change == "latched") admission.LatchFailure();
        if (change == "completed") admission.Complete(true, true, true);
        if (change == "plan") plan = plan with { PolicyId = Canary };
        if (change == "declaration") declaration = declaration with { TimeoutSeconds = 19 };
        AssertSafe(Assert.Throws<EvidenceAdmissionException>(() => EvidenceRestrictedProducerLease.ValidateMetadata(
            admission, plan, declaration, change == "run" ? "other/1" : "fixture/1")), "ASEVD410");
    }

    [Fact]
    public void MatchingMetadataDoesNotIssueALeaseAndOneAttemptCannotBeReopened()
    {
        var declaration = Declaration(); var plan = Plan(declaration);
        var admission = new EvidenceAdmissionResult(plan, EvidenceExecutionMode.Observation, "fixture/1", null);
        admission.Activate("metadata-only-not-a-real-output");
        EvidenceRestrictedProducerLease.ValidateMetadata(admission, plan, declaration, "fixture/1");
        using var attempt = new EvidenceRestrictedProducerAttempt();
        attempt.Claim();
        AssertSafe(Assert.Throws<EvidenceAdmissionException>(attempt.Claim), "ASEVD410");
        attempt.Dispose(); attempt.Dispose();
        AssertSafe(Assert.Throws<EvidenceAdmissionException>(attempt.RequireOpen), "ASEVD410");
        using var closed = new EvidenceRestrictedProducerAttempt(); closed.Dispose();
        AssertSafe(Assert.Throws<EvidenceAdmissionException>(closed.Claim), "ASEVD410");
    }

    [Fact]
    public async Task ConcurrentClaimsAllowExactlyOneAttempt()
    {
        using var attempt = new EvidenceRestrictedProducerAttempt();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = Enumerable.Range(0, 16).Select(async _ =>
        {
            await start.Task;
            try { attempt.Claim(); return true; }
            catch (EvidenceAdmissionException error) { AssertSafe(error, "ASEVD410"); return false; }
        }).ToArray();
        start.SetResult();
        Assert.Equal(1, (await Task.WhenAll(claims)).Count(static claimed => claimed));
    }

    [Fact]
    public async Task IgnoredProcedureTaskRemainsOwnedUntilItsActualCompletion()
    {
        var supervisor = new MetadataSupervisor(); var execution = Execution(supervisor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? procedure = null;
        var stage = execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), token =>
        {
            procedure = EvidenceRestrictedProducerLease.TrackProcedureAsync(execution, token, CancellationToken.None,
                async _ => { entered.SetResult(); return await release.Task; });
            returned.SetResult();
            return ValueTask.FromResult(true);
        }).AsTask();
        await Task.WhenAll(entered.Task, returned.Task);
        Assert.False(stage.IsCompleted);
        Assert.False(procedure!.IsCompleted);
        release.SetResult(17);
        Assert.Equal(EvidenceWorkerStageOutcome.Passed, (await stage).Outcome);
        Assert.Equal(17, await procedure);
        Assert.True(await execution.StopAndDisposeAsync());
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(1, supervisor.WaitCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WholeProcedureLinksActualStageAndAdditionalCallerCancellation(bool cancelStage)
    {
        var supervisor = new MetadataSupervisor(); var execution = Execution(supervisor);
        using var stage = new CancellationTokenSource(); using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = EvidenceRestrictedProducerLease.TrackProcedureAsync(execution, stage.Token, caller.Token,
            async token => { entered.SetResult(); return await never.Task.WaitAsync(token); });
        await entered.Task;
        (cancelStage ? stage : caller).Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(await execution.StopAndDisposeAsync());
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(1, supervisor.WaitCount);
    }

    [Fact]
    public async Task IgnoredProcedureFaultFailsTheOwningStageAndUnboundStageCannotTrack()
    {
        var supervisor = new MetadataSupervisor(); var execution = Execution(supervisor);
        AssertSafe(Assert.Throws<EvidenceAdmissionException>(() =>
        {
            _ = EvidenceRestrictedProducerLease.TrackProcedureAsync(
                execution, CancellationToken.None, CancellationToken.None, _ => Task.FromResult(0));
        }), "ASEVD410");
        Task<int>? procedure = null;
        var result = await execution.ExecuteAsync(EvidenceRunStage.Producer, TimeSpan.FromSeconds(10), token =>
        {
            procedure = EvidenceRestrictedProducerLease.TrackProcedureAsync<int>(execution, token, CancellationToken.None,
                _ => Task.FromException<int>(new IOException(Canary)));
            return ValueTask.FromResult(true);
        });
        Assert.Equal(EvidenceWorkerStageOutcome.Failed, result.Outcome);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, execution.TerminalCode);
        await Assert.ThrowsAsync<IOException>(() => procedure!);
        Assert.False(await execution.StopAndDisposeAsync());
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(1, supervisor.WaitCount);
    }

    private static EvidenceProducerDeclaration Declaration() => new("coverage", "coverage", "1.0.0", [],
        ["appsurface/coverage/behavioral-patch@1"], [], 20, new EvidenceCoverageGateRequirements(95, 85));
    private static EvidencePlan Plan(EvidenceProducerDeclaration declaration)
    {
        var profile = new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [], [declaration], []);
        var policy = new EvidencePolicy("factory", "1", "coverage", [profile],
            [new EvidencePolicyRule("factory-source", "src/**", "coverage")]);
        return new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/FactoryFixture.cs")]);
    }
    private static EvidenceWorkerExecution Execution(MetadataSupervisor supervisor) => new(supervisor, TimeProvider.System,
        TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
    private static void AssertSafe(EvidenceAdmissionException error, string code)
    {
        Assert.Equal(code, error.Code); Assert.DoesNotContain(Canary, error.Message); Assert.Null(error.InnerException);
    }
    private sealed class MetadataSupervisor : IEvidenceExecutionSupervisor
    {
        public bool IsArmed => true;
        public string RunId => "metadata-only/1";
        public int WaitCount { get; private set; }
        public void CloseAdmission() { }
        public ValueTask RequestStopAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
        public ValueTask WaitForOwnedExitAsync(CancellationToken stoppingToken) { WaitCount++; return ValueTask.CompletedTask; }
    }
}
