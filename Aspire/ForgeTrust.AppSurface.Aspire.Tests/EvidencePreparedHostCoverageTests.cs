using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidencePreparedHostCoverageTests
{
    [Fact]
    public async Task FailedRequiredResource_ShouldBlockProducerAndDisposeTransferredScope()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var owner = new Owner();
        var readiness = new Readiness("database", fail: true);
        await using var prepared = await fixture.PrepareAsync(producer);

        var result = await prepared.RunAsync((_, _) => ValueTask.FromResult(
            new EvidenceHostResourceScope([readiness], owner)));
        await prepared.DisposeAsync();

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(EvidenceResourceOutcome.Unavailable, Assert.Single(result.ResourceResults).Outcome);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task EmptyFreshResourceClosure_ShouldSkipFactoryAndStillRunProducer()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        await using var prepared = await fixture.PrepareAsync(producer, requiresResource: false);
        var factoryCalls = 0;

        var result = await prepared.RunAsync((_, _) =>
        {
            factoryCalls++;
            throw new InvalidOperationException("No resource startup is required.");
        });

        Assert.Empty(prepared.RequiredResourceIds);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(EvidenceExecutionVerdict.Passed, result.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(1, producer.Runs);
        var decision = Assert.Single(prepared.Decisions);
        Assert.Equal(EvidenceReuseStatus.Ineligible, decision.Status);
        Assert.Equal("reuse.disabled", decision.Reason);
    }

    [Fact]
    public async Task FactoryThrow_ShouldReturnTypedUnavailableAndRetainProducerCleanup()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        await using var prepared = await fixture.PrepareAsync(producer);

        var result = await prepared.RunAsync((_, _) => ValueTask.FromException<EvidenceHostResourceScope>(
            new IOException("controlled resource startup failure")));

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, result.ClaimKind);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task FailedDependency_ShouldSkipDependentReadinessAndReportBothOutcomes()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var database = new Readiness("database", fail: true);
        var api = new Readiness("api");
        var owner = new Owner();
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
            [new("database", "completion", 30, []), new("api", "completion", 30, ["database"])],
            [new("check", "custom", "1", ["api"], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        await using var prepared = await fixture.PrepareAsync(producer, profile);

        var result = await prepared.RunAsync((ids, _) =>
        {
            Assert.Equal(["api", "database"], ids);
            return ValueTask.FromResult(new EvidenceHostResourceScope([database, api], owner));
        });

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceResourceOutcome.Unavailable,
            result.ResourceResults.Single(resource => resource.ResourceId == "database").Outcome);
        var skipped = result.ResourceResults.Single(resource => resource.ResourceId == "api");
        Assert.Equal(EvidenceResourceOutcome.Unavailable, skipped.Outcome);
        Assert.Contains("resource_dependency_unavailable", skipped.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, api.ReadinessCalls);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, database.Disposals);
        Assert.Equal(1, api.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task SharedResourceDependencyStartsOnceAndSatisfiesBothBranches()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
            [new("base", "completion", 30, []), new("left", "completion", 30, ["base"]),
             new("right", "completion", 30, ["base"])],
            [new("check", "custom", "1", ["left", "right"], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        await using var prepared = await fixture.PrepareAsync(producer, profile);
        var resources = new[] { new Readiness("base"), new Readiness("left"), new Readiness("right") };
        var owner = new Owner();

        var result = await prepared.RunAsync((ids, _) =>
        {
            Assert.Equal(["base", "left", "right"], ids);
            return ValueTask.FromResult(new EvidenceHostResourceScope(resources, owner));
        });

        Assert.Equal(EvidenceExecutionVerdict.Passed, result.ExecutionVerdict);
        Assert.True(EvidenceManifestBuilder.Verify(prepared.Plan, result));
        Assert.Equal(1, producer.Runs);
        Assert.All(resources, resource => Assert.Equal(1, resource.ReadinessCalls));
        Assert.All(resources, resource => Assert.Equal(1, resource.Disposals));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task SynchronousProducerOwnerIsDisposedAfterSuccessfulPreparedExecution()
    {
        using var fixture = new Fixture();
        var producer = new SynchronousProducer("check");
        await using var prepared = await fixture.PrepareAsync(producer, requiresResource: false);

        var result = await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resources are required."));
        await prepared.DisposeAsync();

        Assert.Equal(EvidenceExecutionVerdict.Passed, result.ExecutionVerdict);
        Assert.True(EvidenceManifestBuilder.Verify(prepared.Plan, result));
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task CorrectlyBoundCapturedPatch_ShouldSurvivePreparationAndVerifyFinalManifest()
    {
        using var fixture = new Fixture();
        var patch = System.Text.Encoding.UTF8.GetBytes(
            "diff --git a/input.txt b/input.txt\n--- a/input.txt\n+++ b/input.txt\n@@ -1 +1 @@\n-old\n+new\n");
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [],
            [new("check", "custom", "1", [], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        var plan = fixture.Plan(profile, patch);
        var producer = new Producer("check");

        await using var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
        }, new EvidenceHostOptions
        {
            Reuse = false,
            CapturedPatch = patch,
            RepositoryRoot = TestPathUtils.PathUnder(fixture.Root, "repo"),
            ArtifactDirectory = TestPathUtils.PathUnder(fixture.Root, "patch-artifacts"),
            EnvironmentReader = _ => null,
        });

        var result = await prepared.RunAsync((_, _) =>
            throw new InvalidOperationException("The selected producer requires no resources."));

        Assert.Equal(plan.PatchSha256, prepared.Plan.PatchSha256);
        Assert.NotNull(prepared.Plan.PatchSha256);
        Assert.Equal(EvidenceExecutionVerdict.Passed, result.ExecutionVerdict);
        Assert.True(EvidenceManifestBuilder.Verify(prepared.Plan, result));
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task ResourceScopeAttemptsEveryOwnerAndCachesTheFailedCleanupTask()
    {
        var first = new SynchronousReadiness("first", new IOException("first readiness disposal"));
        var last = new Readiness("last", disposalFailure: new InvalidOperationException("last readiness disposal"));
        var owner = new Owner(new ApplicationException("application disposal"));
        var scope = new EvidenceHostResourceScope([first, last], owner);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => scope.DisposeAsync().AsTask());
        var repeatedFailure = await Assert.ThrowsAnyAsync<Exception>(() => scope.DisposeAsync().AsTask());

        Assert.Same(failure, repeatedFailure);
        Assert.Contains(failure.Message, new[] { "first readiness disposal", "last readiness disposal", "application disposal" });
        Assert.Equal(1, last.Disposals);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Theory]
    [InlineData("trusted-envelope", "reuse.local_only")]
    [InlineData("release-profile", "reuse.local_only")]
    [InlineData("github-actions", "reuse.local_only")]
    public async Task ReuseIsRejectedBeforeConfigurationInProtectedContexts(string context, string expectedCode)
    {
        using var fixture = new Fixture();
        var profile = new EvidenceProfile("local", context == "release-profile"
                ? EvidenceProfileScope.Release : EvidenceProfileScope.Targeted,
            [], [new("check", "custom", "1", [], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        var plan = fixture.Plan(profile);
        var configureCalls = 0;
        var options = new EvidenceHostOptions
        {
            Reuse = true,
            RequireTrustedEnvelope = context == "trusted-envelope",
            RepositoryRoot = TestPathUtils.PathUnder(fixture.Root, "repo"),
            ArtifactDirectory = TestPathUtils.PathUnder(fixture.Root, "artifacts"),
            EnvironmentReader = name => context == "github-actions" && name == "GITHUB_ACTIONS" ? "true" : null,
        };

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => EvidenceHostBootstrap.PrepareAsync(
            plan, _ => configureCalls++, options));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(0, configureCalls);
        Assert.False(Directory.Exists(options.ArtifactDirectory));
    }

    [Fact]
    public async Task ChangedReadinessBindingId_ShouldRejectTransferredScopeAndDisposeEveryOwner()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var readiness = new Readiness("changed-database");
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(producer);

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => prepared.RunAsync((_, _) => ValueTask.FromResult(
            new EvidenceHostResourceScope([readiness], owner))));
        await prepared.DisposeAsync();

        Assert.Equal("reuse.resource_scope_invalid", failure.Code);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task NullFactoryScope_ShouldRejectTransferAndDisposeRegisteredProducer()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var prepared = await fixture.PrepareAsync(producer);

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => prepared.RunAsync((_, _) =>
            ValueTask.FromResult<EvidenceHostResourceScope>(null!)));
        await prepared.DisposeAsync();

        Assert.Equal("reuse.resource_scope_invalid", failure.Code);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task UnsettledKeyMaterial_ShouldExposeUnavailableDecisionAndSkipFactoryUntilJoined()
    {
        using var fixture = new Fixture();
        var declaration = new EvidenceProducerDeclaration("check", "custom", "1", ["database"], ["valid"], [], 1)
        {
            Inputs = new EvidenceProducerInputs(["input.txt"], []),
        };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
            [new EvidenceResourceDeclaration("database", "completion", 30, [])], [declaration],
            [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
        var plan = fixture.Plan(profile);
        var producer = new UnsettledMaterialProducer("check");
        var preparation = EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        }, new EvidenceHostOptions
        {
            Reuse = true,
            ProducerCleanupTimeout = TimeSpan.FromMilliseconds(20),
            ReceiptWaitTimeout = TimeSpan.FromSeconds(2),
            RepositoryRoot = TestPathUtils.PathUnder(fixture.Root, "repo"),
            ArtifactDirectory = TestPathUtils.PathUnder(fixture.Root, "unavailable-artifacts"),
            EnvironmentReader = _ => null,
        });
        EvidencePreparedRun? prepared = null;

        try
        {
            prepared = await preparation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(producer.CancellationObserved.Task.IsCompleted);
            var decision = Assert.Single(prepared.Decisions);
            Assert.Equal(EvidenceReuseStatus.Unavailable, decision.Status);
            Assert.Equal("reuse.material_unsettled", decision.Reason);
            var factoryCalls = 0;
            var result = await prepared.RunAsync((_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("Unavailable preparation must not start resources.");
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, factoryCalls);
            Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(result.ProducerResults).Outcome);
            Assert.Contains("reuse.preparation_unavailable", Assert.Single(result.ProducerResults).Diagnostic, StringComparison.Ordinal);
            Assert.Equal(0, producer.Runs);
        }
        finally
        {
            producer.Release.TrySetResult(new Dictionary<string, string> { ["fixture"] = "settled" });
            prepared ??= await preparation.WaitAsync(TimeSpan.FromSeconds(10));
            await prepared.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task ReadinessDeadline_ShouldReportTimedOutAndDisposeTheOwnedScope()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var readiness = new Readiness("database", block: true);
        var owner = new Owner();
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
            [new("database", "completion", 1, [])],
            [new("check", "custom", "1", ["database"], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        await using var prepared = await fixture.PrepareAsync(producer, profile);

        var result = await prepared.RunAsync((_, _) => ValueTask.FromResult(
            new EvidenceHostResourceScope([readiness], owner)));

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceResourceOutcome.TimedOut, Assert.Single(result.ResourceResults).Outcome);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(1, readiness.ReadinessCalls);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
        Assert.True(result.Metrics.CleanupCompleted);
    }

    [Fact]
    public async Task CallerCancellationDuringReadiness_ShouldReturnCancelledAndJoinScopeCleanup()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var readiness = new Readiness("database", block: true);
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(producer);
        using var cancellation = new CancellationTokenSource();

        var execution = prepared.RunAsync((_, _) => ValueTask.FromResult(
            new EvidenceHostResourceScope([readiness], owner)), cancellationToken: cancellation.Token);
        await readiness.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
        Assert.True(result.Metrics.CleanupCompleted);
    }

    [Fact]
    public async Task CallerCancellationWhileFactoryIsStillBuilding_ShouldDisposeScopeWhenFactoryEventuallyTransfersIt()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var readiness = new Readiness("database");
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(producer);
        using var cancellation = new CancellationTokenSource();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = prepared.RunAsync(async (_, factoryToken) =>
        {
            factoryStarted.TrySetResult();
            using var registration = factoryToken.Register(() => factoryCancelled.TrySetResult());
            await releaseFactory.Task.ConfigureAwait(false);
            return new EvidenceHostResourceScope([readiness], owner);
        }, cancellationToken: cancellation.Token);
        try
        {
            await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await factoryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
            Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(result.ProducerResults).Outcome);
            Assert.Equal(0, producer.Runs);
            Assert.Equal(0, readiness.Disposals);
            Assert.Equal(0, owner.Disposals);
        }
        finally
        {
            releaseFactory.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
            await prepared.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, producer.Disposals);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task FatalReadinessDisposal_ShouldStillAttemptApplicationOwnerAndProducerCleanup()
    {
        using var fixture = new Fixture();
        var fatal = new OutOfMemoryException("controlled fatal readiness disposal");
        var producer = new Producer("check");
        var readiness = new Readiness("database", disposalFailure: fatal);
        var owner = new Owner();
        var prepared = await fixture.PrepareAsync(producer);

        var runFailure = await Record.ExceptionAsync(() => prepared.RunAsync((_, _) => ValueTask.FromResult(
            new EvidenceHostResourceScope([readiness], owner))));
        var disposalFailure = await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask());

        Assert.Same(fatal, runFailure);
        Assert.Same(fatal, disposalFailure);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task ProducerDeadline_ShouldReturnTimedOutAndJoinCooperativeProducerCleanup()
    {
        using var fixture = new Fixture();
        var producer = new CooperativeProducer("check");
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [],
            [new("check", "custom", "1", [], ["valid"], [], 1)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        await using var prepared = await fixture.PrepareAsync(producer, profile);

        var result = await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resources are required."));

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.TimedOut, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Cancellations);
        Assert.Equal(1, producer.Disposals);
        Assert.True(result.Metrics.CleanupCompleted);
    }

    [Theory]
    [InlineData("wrong-id", "different declaration id")]
    [InlineData("reused-outcome", "reuse.forged_provenance")]
    [InlineData("origin-digest", "reuse.forged_provenance")]
    public async Task ProducerResultOutsideFreshExecutionContract_ShouldBeInvalidated(string resultShape, string diagnostic)
    {
        using var fixture = new Fixture();
        var producer = new InvalidResultProducer("check", resultShape);
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [],
            [new("check", "custom", "1", [], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        await using var prepared = await fixture.PrepareAsync(producer, profile);

        var result = await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resources are required."));

        var producerResult = Assert.Single(result.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Invalid, producerResult.Outcome);
        Assert.Contains(diagnostic, producerResult.Diagnostic, StringComparison.Ordinal);
        // Contract 1.0 treats this as incomplete aggregate execution while preserving the
        // producer's explicit invalid result; the producer-level assertion above is authoritative.
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, result.ClaimKind);
        Assert.True(result.Metrics.CleanupCompleted);
        Assert.Equal(1, producer.Runs);
    }

    [Fact]
    public async Task CancellationBeforeExecution_ShouldLeavePreparedRunAvailableForRetry()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        await using var prepared = await fixture.PrepareAsync(producer, requiresResource: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factoryCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.RunAsync((_, _) =>
        {
            factoryCalls++;
            throw new InvalidOperationException("No resource factory should run.");
        }, cancellationToken: cancellation.Token));
        Assert.Equal(0, factoryCalls);

        var retry = await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resources are required."));

        Assert.Equal(EvidenceExecutionVerdict.Passed, retry.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(retry.ProducerResults).Outcome);
        Assert.Equal(1, producer.Runs);
    }

    [Fact]
    public async Task ProducerIdentifierChangedAfterPreparation_ShouldRejectBeforeFactoryAndStillDisposeOwner()
    {
        using var fixture = new Fixture();
        var producer = new MutableIdProducer("check");
        await using var prepared = await fixture.PrepareAsync(producer, requiresResource: false);
        producer.Id = "changed";
        var factoryCalls = 0;

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => prepared.RunAsync((_, _) =>
        {
            factoryCalls++;
            throw new InvalidOperationException("No resource factory should run.");
        }));
        await prepared.DisposeAsync();

        Assert.Equal("reuse.registration_changed", failure.Code);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task NonfatalProducerDisposalFailure_ShouldInvalidateCompletionAndRemainObservable()
    {
        using var fixture = new Fixture();
        var producer = new DisposalFailureProducer("check");
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [],
            [new("check", "custom", "1", [], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        await using var prepared = await fixture.PrepareAsync(producer, profile);

        var result = await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resources are required."));

        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.False(result.Metrics.CleanupCompleted);
        Assert.Contains("reuse.cleanup_failed", result.Metrics.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task PrepareArgumentsAndReuseOptions_ShouldRejectBeforeRegistrationSideEffects()
    {
        using var fixture = new Fixture();
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [],
            [new("check", "custom", "1", [], ["valid"], [], 30)],
            [new("check-obligation", "test", "Fixture", ["check"], "valid")]);
        var plan = fixture.Plan(profile);
        var configureCalls = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceHostBootstrap.PrepareAsync(null!, _ => configureCalls++));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceHostBootstrap.PrepareAsync(plan, null!));
        await Assert.ThrowsAsync<OperationCanceledException>(() => EvidenceHostBootstrap.PrepareAsync(
            plan, _ => configureCalls++, cancellationToken: cancelled.Token));
        var reuseDisabled = await Assert.ThrowsAsync<EvidenceHostException>(() => EvidenceHostBootstrap.PrepareAsync(
            plan, _ => configureCalls++, new EvidenceHostOptions { Reuse = false, VerifyReuse = true }));
        var invalidTimeout = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvidenceHostBootstrap.PrepareAsync(
            plan, _ => configureCalls++, new EvidenceHostOptions { ProducerCleanupTimeout = TimeSpan.Zero }));

        Assert.Equal("reuse.verify_requires_reuse", reuseDisabled.Code);
        Assert.Equal("options", invalidTimeout.ParamName);
        Assert.Equal(0, configureCalls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("prepared-host-evidence-").FullName;
        internal string Root => _root;
        private string Repository => TestPathUtils.PathUnder(_root, "repo");

        internal Fixture()
        {
            Directory.CreateDirectory(Repository);
        }

        internal Task<EvidencePreparedRun> PrepareAsync(IEvidenceProducer producer, bool requiresResource = true)
        {
            var resourceDeclarations = requiresResource
                ? new[] { new EvidenceResourceDeclaration("database", "completion", 30, []) }
                : [];
            var declaration = new EvidenceProducerDeclaration("check", "custom", "1",
                requiresResource ? ["database"] : [], ["valid"], [], 30);
            var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, resourceDeclarations,
                [declaration], [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
            return PrepareAsync(producer, profile);
        }

        internal Task<EvidencePreparedRun> PrepareAsync(IEvidenceProducer producer, EvidenceProfile profile)
        {
            var plan = Plan(profile);
            return EvidenceHostBootstrap.PrepareAsync(plan, registration =>
            {
                registration.AddProducer(producer, "fixture-v1");
                foreach (var resource in profile.Resources) registration.AddResource(resource.Id, $"{resource.Id}@sha256:fixture");
            }, new EvidenceHostOptions
            {
                Reuse = false,
                RepositoryRoot = Repository,
                ArtifactDirectory = TestPathUtils.PathUnder(_root, "artifacts"),
                EnvironmentReader = _ => null,
            });
        }

        internal EvidencePlan Plan(EvidenceProfile profile) => new EvidencePlanner().Resolve(
            new("fixture", "1", "local", [profile], [new("all", "**", "local")]),
            [new NormalizedDiffPath("input.txt")]);

        internal EvidencePlan Plan(EvidenceProfile profile, ReadOnlyMemory<byte> patch) =>
            new EvidencePlanner().ResolvePatch(
                new("fixture", "1", "local", [profile], [new("all", "**", "local")]), patch);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class Producer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => id;
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"]));
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class UnsettledMaterialProducer(string id) : IEvidenceProducer, IEvidenceProducerKeyMaterial, IAsyncDisposable
    {
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<string, string>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }

        public string Id => id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"]));
        }

        public async ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            using var cancellationRegistration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            return await Release.Task.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class Readiness(string id, bool fail = false, bool block = false, Exception? disposalFailure = null) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id => id;
        internal int Disposals { get; private set; }
        internal int ReadinessCalls { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            ReadinessCalls++;
            Started.TrySetResult();
            if (fail) throw new IOException("controlled readiness failure");
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return disposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposalFailure);
        }
    }

    private sealed class Owner(Exception? disposalFailure = null) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return disposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposalFailure);
        }
    }

    private sealed class SynchronousReadiness(string id, Exception? disposalFailure = null) : IEvidenceResourceReadiness, IDisposable
    {
        public string Id => id;
        internal int Disposals { get; private set; }
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose()
        {
            Disposals++;
            if (disposalFailure is not null) throw disposalFailure;
        }
    }

    private sealed class SynchronousProducer(string id) : IEvidenceProducer, IDisposable
    {
        public string Id => id;
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"]));
        }
        public void Dispose() => Disposals++;
    }

    private sealed class CooperativeProducer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => id;
        internal int Runs { get; private set; }
        internal int Cancellations { get; private set; }
        internal int Disposals { get; private set; }
        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Cancellations++; throw; }
            return new(id, EvidenceProducerOutcome.Passed, ["valid"]);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class DisposalFailureProducer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => id;
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"]));
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.FromException(new IOException("controlled producer disposal failure"));
        }
    }

    private sealed class InvalidResultProducer(string id, string shape) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => id;
        internal int Runs { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            var result = shape switch
            {
                "wrong-id" => new EvidenceProducerResult("other", EvidenceProducerOutcome.Passed, ["valid"]),
                "reused-outcome" => new EvidenceProducerResult(id, EvidenceProducerOutcome.Reused, ["valid"]),
                "origin-digest" => new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"])
                { OriginManifestDigest = new string('a', 64) },
                _ => throw new InvalidOperationException("Unknown controlled result shape."),
            };
            return ValueTask.FromResult(result);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableIdProducer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id { get; set; } = id;
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["valid"]));
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
