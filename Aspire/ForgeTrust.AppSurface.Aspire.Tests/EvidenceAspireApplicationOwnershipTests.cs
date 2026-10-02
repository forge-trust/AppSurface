using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceAspireApplicationOwnershipTests
{
    [Fact]
    public async Task StartOwned_RejectsInactiveAdmissionBeforeBuilding()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var admission = await AdmitAsync(run, plan);
        var built = false;

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceAspireApplication.StartOwnedAsync(
                admission,
                plan,
                () => { built = true; return new TestApplication(); },
                _ => throw new InvalidOperationException("Ownership must not be invoked."),
                (_, _) => throw new InvalidOperationException("Startup must not be invoked.")));

        Assert.Equal("ASEVD410", exception.Code);
        Assert.False(built);
    }

    [Fact]
    public async Task StartOwned_RegistersBeforeStartingAndRetainsLeaseUntilCleanup()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var admission = await AdmitAsync(run, plan);
        admission.Activate("test-output");
        var events = new List<string>();
        var application = new TestApplication(() => events.Add("dispose"));
        TestApplication? owned = null;

        var result = await EvidenceAspireApplication.StartOwnedAsync(
            admission,
            plan,
            () => { events.Add("build"); return application; },
            lease => { events.Add("own"); owned = lease; },
            (lease, token) =>
            {
                Assert.Same(owned, lease);
                Assert.False(token.IsCancellationRequested);
                events.Add("start");
                return Task.CompletedTask;
            });

        Assert.Same(application, result);
        Assert.Equal(["build", "own", "start"], events);
        await owned!.DisposeAsync();
        Assert.Equal(["build", "own", "start", "dispose"], events);
    }

    [Fact]
    public async Task StartupFailure_JoinsWorkBeforeDisposingPartiallyStartedApplication()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var admission = await AdmitAsync(run, plan);
        admission.Activate("test-output");
        var events = new List<string>();
        TestApplication? owned = null;
        var application = new TestApplication(() =>
        {
            Assert.True(run.Supervisor.StopRequests > 0);
            Assert.True(run.Supervisor.ExitAcknowledgements > 0);
            events.Add("dispose");
        });
        var lifecycle = new EvidenceWorkerExecution(
            run.Supervisor, TimeProvider.System, TimeSpan.FromHours(1),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Assert.True(lifecycle.RegisterDisposer(_ => owned?.DisposeAsync() ?? ValueTask.CompletedTask));

        var result = await lifecycle.ExecuteAsync(
            EvidenceRunStage.Start,
            TimeSpan.FromSeconds(2),
            token => EvidenceAspireApplication.StartOwnedAsync(
                admission, plan,
                () => { events.Add("build"); return application; },
                lease => { owned = lease; events.Add("own"); },
                (_, _) =>
                {
                    events.Add("start-failed");
                    return Task.FromException(new InvalidOperationException("test startup failure"));
                }, token));

        Assert.Equal(EvidenceWorkerStageOutcome.Failed, result.Outcome);
        Assert.Equal(EvidenceWorkerTerminalCode.StageFailed, lifecycle.TerminalCode);
        Assert.True(lifecycle.OwnWorkStopped);
        Assert.Equal(["build", "own", "start-failed"], events);
        Assert.False(await lifecycle.StopAndDisposeAsync());
        Assert.True(lifecycle.CleanupCompleted);
        Assert.Equal(["build", "own", "start-failed", "dispose"], events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartOwned_ClosesAfterBuildWithoutLosingOwnership(bool cancel)
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var admission = await AdmitAsync(run, plan);
        admission.Activate("test-output");
        using var cancellation = new CancellationTokenSource();
        var application = new TestApplication();
        TestApplication? owned = null;
        var started = false;

        var exception = await Record.ExceptionAsync(async () =>
            await EvidenceAspireApplication.StartOwnedAsync(
                admission, plan,
                () => application,
                lease =>
                {
                    owned = lease;
                    if (cancel) cancellation.Cancel();
                    else admission.LatchFailure();
                },
                (_, _) => { started = true; return Task.CompletedTask; },
                cancellation.Token));

        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(exception);
        else Assert.Equal("ASEVD410", Assert.IsType<EvidenceAdmissionException>(exception).Code);
        Assert.Same(application, owned);
        Assert.False(started);
        Assert.Equal(0, application.DisposeCount);
        await owned!.DisposeAsync();
        Assert.Equal(1, application.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartOwned_FailedBuildDoesNotRegisterOrStart(bool throwDuringBuild)
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var admission = await AdmitAsync(run, plan);
        admission.Activate("test-output");
        var registered = false;
        var started = false;

        var exception = await Record.ExceptionAsync(async () =>
            await EvidenceAspireApplication.StartOwnedAsync<TestApplication>(
                admission, plan,
                () => throwDuringBuild ? throw new InvalidOperationException("test build failure") : null!,
                _ => registered = true,
                (_, _) => { started = true; return Task.CompletedTask; }));

        if (throwDuringBuild) Assert.IsType<InvalidOperationException>(exception);
        else Assert.IsType<ArgumentNullException>(exception);
        Assert.False(registered);
        Assert.False(started);
    }

    private static ValueTask<EvidenceAdmissionResult> AdmitAsync(EvidenceHostAdmissionTestRun run, EvidencePlan plan) =>
        EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan, run.Context, run.Supervisor, null, CancellationToken.None);

    private sealed class TestApplication(Action? dispose = null) : IAsyncDisposable
    {
        internal int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            dispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
