using System.Collections.Concurrent;
using System.Diagnostics;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: stage telemetry has one correlated start/terminal pair and bounded safe fields.
// fails_when: observers receive raw paths/causes, terminal events are duplicated, or failure state is lost.
// why_new: public capture/materialize/inspect orchestration now exposes a supportable progress seam.
// seam: public KeycloakStageEvents and KeycloakStageScope; no reflection or logging sink required.
public sealed class StageEventsTests
{
    [Fact]
    public void RebindingKnownFailureRewritesOnlyTheCorrelationAndRetainsRecovery()
    {
        const string oldCorrelation = "old-correlation";
        const string newCorrelation = "new-correlation";
        var original = KeycloakDiagnostics.Failure(KeycloakFailureCode.MaterialUnavailable, KeycloakSafePhase.Acquisition,
            "material", KeycloakEffectState.None, "retry unchanged input",
            new MaterialAcquisitionException(true, "private cause"), new KeycloakCorrelationId(oldCorrelation));

        var rebound = KeycloakDiagnostics.Map(original, KeycloakSafePhase.Capture, "ignored", KeycloakEffectState.Unknown, newCorrelation);

        Assert.Equal(newCorrelation, rebound.CorrelationId.Value);
        Assert.Contains(newCorrelation, rebound.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(oldCorrelation, rebound.Message, StringComparison.Ordinal);
        Assert.Equal(original.Recovery, rebound.Recovery);
        Assert.Contains("Recovery: " + original.Recovery, rebound.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private cause", rebound.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityListenerCorrelationMatchesPublishedStageCorrelation()
    {
        var activityTraceIds = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ForgeTrust.AppSurface.Auth.Keycloak",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => activityTraceIds.Enqueue(activity.TraceId.ToString())
        };
        ActivitySource.AddActivityListener(listener);
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Enqueue);
        using (var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Capture)) scope.Complete(KeycloakEffectState.None);

        var stage = Assert.Single(events, x => x.Outcome == KeycloakStageOutcome.Succeeded);
        Assert.Contains(stage.CorrelationId, activityTraceIds);
    }

    [Fact]
    public void InvalidPhaseAndNullObserverRejectBeforeAnyEvent()
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Enqueue);
        Assert.Throws<ArgumentNullException>(() => KeycloakStageEvents.Subscribe(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeycloakStageEvents.Begin((KeycloakStagePhase)999));
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void EachNegativeCounterIsRejectedBeforeTerminal(int materialCount, long materialBytes, int checkCount)
    {
        using var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Capture);
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.SetCounts(materialCount, materialBytes, checkCount));
    }

    [Fact]
    public void TypedRejectionPreservesProfileAndUsesFailureOperationIdentity()
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Enqueue);
        using (var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Inspection, profile: "reviewed-profile", operationId: "inspection"))
        {
            var failure = KeycloakDiagnostics.Failure(KeycloakFailureCode.DigestMismatch, KeycloakSafePhase.Inspection,
                "check:theme", KeycloakEffectState.None, "correct reviewed input");
            scope.Reject(failure);
        }

        var terminal = Assert.Single(events, x => x.Outcome == KeycloakStageOutcome.Rejected);
        Assert.Equal("reviewed-profile", terminal.Profile);
        Assert.Equal("check:theme", terminal.OperationId);
    }

    [Fact]
    public void InvalidIdentityAndCodeValuesAreOmittedOrTruncatedSafely()
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Enqueue);
        var longKey = new string('k', 160) + "/private/path";
        using (var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Evidence,
            "UPPERCASE-DIGEST", "not-a-digest", new string('p', 120)))
        {
            scope.SetContext("also-invalid", longKey, longKey);
            scope.Reject(new string('!', 100));
        }

        var terminal = Assert.Single(events, x => x.Outcome == KeycloakStageOutcome.Rejected);
        Assert.Null(terminal.RecipeDigest);
        Assert.Null(terminal.PolicyDigest);
        Assert.NotNull(terminal.Profile);
        Assert.True(terminal.Profile!.Length <= 64);
        Assert.NotNull(terminal.OperationId);
        Assert.True(terminal.OperationId!.Length <= 64);
        Assert.NotNull(terminal.CheckId);
        Assert.True(terminal.CheckId!.Length <= 96);
        Assert.Equal(new string('_', 48), terminal.SafeCode);
        Assert.True(terminal.Truncated);
    }

    [Fact]
    public void NullTypedRejectionAndDuplicateSubscriptionDisposalAreSafe()
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        void Observe(KeycloakStageEvent value) => events.Enqueue(value);
        using var first = KeycloakStageEvents.Subscribe(Observe);
        using var second = KeycloakStageEvents.Subscribe(Observe);
        first.Dispose();
        using (var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Evidence))
        {
            Assert.Throws<ArgumentNullException>(() => scope.Reject((KeycloakSecurityException)null!));
            scope.Complete(KeycloakEffectState.None);
        }

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task SuppressedAsyncFlowDoesNotReceiveParentSubscription()
    {
        var parentEvents = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(parentEvents.Enqueue);
        Task task;
        using (ExecutionContext.SuppressFlow())
            task = Task.Run(() =>
            {
                using var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Build);
                scope.Complete(KeycloakEffectState.None);
            });
        await task;

        Assert.Empty(parentEvents);
    }

    [Theory]
    [InlineData(null, "UNSPECIFIED")]
    [InlineData("", "UNSPECIFIED")]
    [InlineData("lower-case", "LOWER-CASE")]
    public void SafeHostRejectionCodesRemainFinite(string? input, string expected)
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Enqueue);
        using (var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Build, profile: " ", operationId: " ", checkId: " ")) scope.Reject(input!);
        var terminal = Assert.Single(events, x => x.Outcome == KeycloakStageOutcome.Rejected);
        Assert.Equal(expected, terminal.SafeCode); Assert.Null(terminal.Profile); Assert.Null(terminal.OperationId); Assert.Null(terminal.CheckId);
    }

    [Fact]
    public void ScopePublishesCorrelatedStartAndSuccessWithSafeIdentity()
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Enqueue);
        using (var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Capture,
            new string('a', 64), new string('b', 64), "profile/private/path", new string('c', 64), "capture", "check-1"))
        {
            scope.SetCounts(3, 42, 0);
            scope.Complete(KeycloakEffectState.Committed);
            var own = events.Where(x => x.CorrelationId == scope.CorrelationId).ToArray();
            Assert.Equal(2, own.Length);
            Assert.Equal(KeycloakStageOutcome.Started, own[0].Outcome);
            Assert.Equal(KeycloakStageOutcome.Succeeded, own[1].Outcome);
            Assert.Equal(3, own[1].MaterialCount);
            Assert.Equal(42, own[1].MaterialBytes);
            Assert.Equal(new string('a', 64), own[1].RecipeDigest);
            Assert.Equal(new string('c', 64), own[0].PlanDigest);
            Assert.Equal("capture", own[0].OperationId);
            Assert.Equal("check-1", own[0].CheckId);
            Assert.DoesNotContain("/private/path", own[1].Profile, StringComparison.Ordinal);
            Assert.True(own[1].Truncated);
        }
    }

    [Fact]
    public void TypedRejectionCarriesOnlyStableFailureFields()
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var subscription = KeycloakStageEvents.Subscribe(events.Enqueue);
        using (var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Inspection))
        {
            var failure = KeycloakDiagnostics.Map(new InvalidDataException("secret-output"), KeycloakSafePhase.Inspection,
                "/private/host/path", KeycloakEffectState.None, scope.CorrelationId);
            scope.Reject(failure);
            var terminal = Assert.Single(events, x => x.CorrelationId == scope.CorrelationId && x.Outcome == KeycloakStageOutcome.Rejected);
            Assert.Equal(scope.CorrelationId, failure.CorrelationId.Value);
            Assert.Equal(KeycloakFailureCode.InspectionFailed.ToString(), terminal.SafeCode);
            Assert.Equal(KeycloakEffectState.None, terminal.Effect);
            Assert.DoesNotContain("secret-output", terminal.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("/private/host/path", terminal.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnfinishedScopeEmitsOneSafeRejectionAndObserverFailuresAreIsolated()
    {
        var events = new ConcurrentQueue<KeycloakStageEvent>();
        using var bad = KeycloakStageEvents.Subscribe(_ => throw new InvalidOperationException("observer detail"));
        using var good = KeycloakStageEvents.Subscribe(events.Enqueue);
        using var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Build, profile: new string('x', 100));
        scope.Dispose();

        var own = events.Where(x => x.CorrelationId == scope.CorrelationId).ToArray();
        Assert.Equal(2, own.Length);
        Assert.Equal(KeycloakStageOutcome.Rejected, own[1].Outcome);
        Assert.Equal("UNCOMPLETED", own[1].SafeCode);
        Assert.True(own[1].Truncated);
        Assert.DoesNotContain("observer detail", own[1].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidCountersAndDuplicateTerminalAreRejectedWithoutSecondEvent()
    {
        using var scope = KeycloakStageEvents.Begin(KeycloakStagePhase.Evidence);
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.SetCounts(materialBytes: -1));
        scope.Complete(KeycloakEffectState.None);
        scope.Reject("late-cause", KeycloakEffectState.Unknown);
    }
}
