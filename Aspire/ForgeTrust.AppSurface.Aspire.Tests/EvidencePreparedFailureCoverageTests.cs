using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidencePreparedFailureCoverageTests
{
    [Theory]
    [InlineData("cleanup-zero")]
    [InlineData("cleanup-long")]
    [InlineData("wait-zero")]
    [InlineData("wait-infinite")]
    [InlineData("oversized-patch")]
    public async Task InvalidPreparationBoundsCannotCallTheRegistrationCallback(string defect)
    {
        var calls = 0;
        var options = new EvidenceHostOptions { EnvironmentReader = _ => null };
        options = defect switch
        {
            "cleanup-zero" => options with { ProducerCleanupTimeout = TimeSpan.Zero },
            "cleanup-long" => options with { ProducerCleanupTimeout = TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1) },
            "wait-zero" => options with { ReceiptWaitTimeout = TimeSpan.Zero },
            "wait-infinite" => options with { ReceiptWaitTimeout = Timeout.InfiniteTimeSpan },
            _ => options with { CapturedPatch = new byte[20 * 1024 * 1024 + 1] }
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvidenceHostBootstrap.PrepareAsync(
            Plan(), _ => calls++, options));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NullArgumentsAndInitialCancellationCannotCallRegistration()
    {
        var calls = 0;
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceHostBootstrap.PrepareAsync(null!, _ => calls++));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceHostBootstrap.PrepareAsync(Plan(), null!));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceHostBootstrap.PrepareAsync(
            Plan(), _ => calls++, cancellationToken: cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("policy-resolution")]
    public async Task ForgedPlanCannotRegisterOrStartProducerWork(string defect)
    {
        var plan = Plan();
        if (defect == "digest") plan = plan with { PlanDigest = new string('0', 64) };
        else
        {
            plan = plan with
            {
                Profile = plan.Profile with { Producers = [plan.Profile.Producers[0] with { Version = "manufactured-version" }] },
                PlanDigest = string.Empty
            };
            plan = plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
        }
        var calls = 0;

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => EvidenceHostBootstrap.PrepareAsync(plan,
            _ => calls++, new EvidenceHostOptions { EnvironmentReader = _ => null }));

        Assert.Equal("reuse.plan_invalid", failure.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingResourceIdentityRejectsAdmissionAndPreservesTheFailureWhileDisposingProducer()
    {
        var producer = new SynchronousProducer(new IOException("controlled untransferred disposal"));

        var observed = await Record.ExceptionAsync(() => EvidenceHostBootstrap.PrepareAsync(PlanWithResource(), registration =>
            registration.AddProducer(producer, "fixture-v1"), new EvidenceHostOptions { EnvironmentReader = _ => null }));

        var failure = Assert.IsType<EvidenceHostException>(observed);
        Assert.Equal("ASEVD302", failure.Code);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, producer.Runs);
    }

    [Fact]
    public async Task ReuseWithATrustedEnvelopeIsRejectedBeforeRegistration()
    {
        var calls = 0;

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => EvidenceHostBootstrap.PrepareAsync(Plan(),
            _ => calls++, new EvidenceHostOptions { Reuse = true, RequireTrustedEnvelope = true, EnvironmentReader = _ => null }));

        Assert.Equal("reuse.local_only", failure.Code);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("domain")]
    public async Task FailedRegistrationDisposesSynchronousOwnersAndPreservesFatalCleanupFailures(string shape)
    {
        var fatal = shape != "io";
        var original = new InvalidOperationException("controlled registration failure");
        var cleanup = CleanupFailure(shape);
        var producer = new SynchronousProducer(cleanup);

        var observed = await Record.ExceptionAsync(() => EvidenceHostBootstrap.PrepareAsync(Plan(), registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            throw original;
        }, new EvidenceHostOptions { EnvironmentReader = _ => null }));

        Assert.Same(fatal ? cleanup : original, observed);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, producer.Runs);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("domain")]
    public async Task AbandonedPreparationJoinsSynchronousCleanupExactlyOnce(string shape)
    {
        var fatal = shape != "io";
        var cleanup = CleanupFailure(shape);
        var producer = new SynchronousProducer(cleanup);
        var prepared = await EvidenceHostBootstrap.PrepareAsync(Plan(), registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions { RepositoryRoot = Path.GetTempPath(), EnvironmentReader = _ => null });

        var first = await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask());
        var second = await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask());

        if (fatal) { Assert.Same(cleanup, first); Assert.Same(cleanup, second); }
        else { Assert.Null(first); Assert.Null(second); }
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, producer.Runs);
    }

    [Fact]
    public async Task ResourceScopeDisposesSynchronousReadinessBeforeItsApplicationOwner()
    {
        var order = new List<string>();
        var readiness = new SynchronousReadiness(order);
        var application = new ApplicationOwner(order);
        var scope = new EvidenceHostResourceScope([readiness], application);

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.Equal(["readiness", "application"], order);
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-owner")]
    [InlineData("null-resource")]
    [InlineData("empty-id")]
    [InlineData("duplicate-id")]
    public void InvalidResourceTransferLeavesAllPartialConstructionOwnedByTheCaller(string defect)
    {
        var order = new List<string>();
        var resource = new NamedReadiness(defect == "empty-id" ? string.Empty : "database", order);
        IReadOnlyList<IEvidenceResourceReadiness> resources = defect switch
        {
            "null-list" => null!,
            "null-resource" => [null!],
            "duplicate-id" => [resource, resource],
            _ => [resource]
        };
        var owner = new ApplicationOwner(order);

        var failure = Record.Exception(() => new EvidenceHostResourceScope(resources, defect == "null-owner" ? null! : owner));

        Assert.IsAssignableFrom<ArgumentException>(failure);
        Assert.Empty(order);
    }

    [Theory]
    [InlineData("access")]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("domain")]
    public async Task FatalFactoryFailureIsPropagatedAfterPreparedOwnershipIsJoined(string shape)
    {
        using var directory = TempDirectory.Create("evidence-fatal-factory-");
        var failure = CleanupFailure(shape);
        var producer = new SynchronousProducer(new IOException("controlled producer disposal"));
        var plan = PlanWithResource();
        var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database-v1");
        }, new EvidenceHostOptions
        {
            RepositoryRoot = directory.Path,
            ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
            EnvironmentReader = _ => null
        });
        try
        {
            var observed = await Record.ExceptionAsync(() => prepared.RunAsync((_, _) => throw failure));
            Assert.Same(failure, observed);
        }
        finally
        {
            Assert.Same(failure, await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask()));
        }
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, producer.Runs);
    }

    [Fact]
    public async Task CancelledPreparationKeepsRegistrationAliveUntilTheActualMaterialCallbackSettles()
    {
        using var directory = TempDirectory.Create("evidence-cancelled-preparation-");
        using var caller = new CancellationTokenSource();
        var producer = new MaterialOwner();
        var declaration = new EvidenceProducerDeclaration(producer.Id, "custom", "1", [], ["valid"], [], 30)
        { Inputs = new EvidenceProducerInputs(["input.txt"], []) };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("check", "test", "Fixture", [producer.Id], "valid")]);
        var plan = new EvidencePlanner().Resolve(new("fixture", "1", profile.Id, [profile], [new("all", "**", profile.Id)]),
            [new NormalizedDiffPath("input.txt")]);
        var store = TestPathUtils.PathUnder(directory.Path, "receipts");
        var preparing = EvidenceHostBootstrap.PrepareAsync(plan, registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                Reuse = true,
                RepositoryRoot = directory.Path,
                ReceiptStore = store,
                ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
                ProducerCleanupTimeout = TimeSpan.FromMilliseconds(20),
                EnvironmentReader = _ => null
            }, cancellationToken: caller.Token);
        try
        {
            await producer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            caller.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, producer.Disposals);
            Assert.Equal(0, producer.Runs);
            Assert.False(Directory.Exists(store));
        }
        finally
        {
            caller.Cancel();
            producer.Release.TrySetResult(new Dictionary<string, string>());
            try { await preparing.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (preparing.IsCompleted) { }
            await producer.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(1, producer.MaterialCalls);
    }

    [Fact]
    public async Task SettledInvalidKeyMaterialFallsBackToFreshExecutionWithTypedDecision()
    {
        using var directory = TempDirectory.Create("evidence-invalid-material-fallback-");
        var producer = new MaterialOwner();
        producer.Release.TrySetResult(null!);
        var declaration = new EvidenceProducerDeclaration(producer.Id, "custom", "1", [], ["valid"], [], 30)
        {
            Inputs = new EvidenceProducerInputs(["input.txt"], []),
        };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("check", "test", "Fixture", [producer.Id], "valid")]);
        var plan = new EvidencePlanner().Resolve(
            new EvidencePolicy("fixture", "1", profile.Id, [profile], [new("all", "**", profile.Id)]),
            [new NormalizedDiffPath("input.txt")]);
        await using var prepared = await EvidenceHostBootstrap.PrepareAsync(plan,
            registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                Reuse = true,
                RepositoryRoot = directory.Path,
                ReceiptStore = TestPathUtils.PathUnder(directory.Path, "receipts"),
                ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
                EnvironmentReader = _ => null
            });

        var decision = Assert.Single(prepared.Decisions);
        var result = await prepared.RunAsync((_, _) =>
            throw new InvalidOperationException("No resources are required for the fresh fallback."));

        Assert.Equal(EvidenceReuseStatus.Ineligible, decision.Status);
        Assert.Equal("reuse.material_invalid", decision.Reason);
        Assert.Contains("returned no material map", decision.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(EvidenceExecutionVerdict.Passed, result.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(1, producer.MaterialCalls);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task LateFatalMaterialCallbackRemainsOwnedAndItsFailureSurvivesPreparedDisposal()
    {
        var root = Directory.CreateTempSubdirectory("evidence-late-fatal-material-").FullName;
        var clock = new FakeTimeProvider();
        var fatal = new OutOfMemoryException("synthetic late key-material failure");
        var producer = new LateFatalMaterialOwner();
        var declaration = new EvidenceProducerDeclaration(producer.Id, "custom", "1", [], ["valid"], [], 1)
        {
            Inputs = new EvidenceProducerInputs(["input.txt"], []),
        };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("check", "test", "Fixture", [producer.Id], "valid")]);
        var plan = new EvidencePlanner().Resolve(
            new EvidencePolicy("fixture", "1", profile.Id, [profile], [new("all", "**", profile.Id)]),
            [new NormalizedDiffPath("input.txt")]);
        var preparing = EvidenceHostBootstrap.PrepareAsync(plan,
            registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                Reuse = true,
                ProducerCleanupTimeout = TimeSpan.FromSeconds(1),
                RepositoryRoot = root,
                ReceiptStore = TestPathUtils.PathUnder(root, "receipts"),
                ArtifactDirectory = TestPathUtils.PathUnder(root, "artifacts"),
                EnvironmentReader = _ => null
            }, clock);
        EvidencePreparedRun? prepared = null;

        try
        {
            await producer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromSeconds(1));
            await producer.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // gstack-timing-ok: advance fake cleanup time only while a bounded real poll confirms
            // preparation is still waiting; the cancellation and callback barriers control state.
            using var readinessDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!preparing.IsCompleted)
            {
                readinessDeadline.Token.ThrowIfCancellationRequested();
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(TimeSpan.FromMilliseconds(10), readinessDeadline.Token);
            }
            prepared = await preparing;

            var decision = Assert.Single(prepared.Decisions);
            Assert.Equal(EvidenceReuseStatus.Unavailable, decision.Status);
            Assert.Equal("reuse.material_unsettled", decision.Reason);
            Assert.Equal(0, producer.Runs);
            producer.Release.TrySetException(fatal);

            var observed = await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Same(fatal, observed);
            Assert.Equal(1, producer.Disposals);
        }
        finally
        {
            producer.Release.TrySetException(fatal);
            var cleanupJoined = true;
            if (prepared is null)
            {
                try { prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { }
            }
            await producer.CallbackSettled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (prepared is null && !preparing.IsCompleted)
            {
                try { prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { }
            }
            if (prepared is not null)
            {
                var disposal = prepared.DisposeAsync().AsTask();
                try { await disposal.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { cleanupJoined = disposal.IsCompleted; }
            }
            if (preparing.IsCompleted && producer.CallbackSettled.Task.IsCompleted && cleanupJoined)
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("access")]
    [InlineData("memory")]
    [InlineData("stack")]
    [InlineData("domain")]
    public async Task FatalPreparedProducerFailureIsPreservedAfterItsActualCleanup(string shape)
    {
        using var directory = TempDirectory.Create("evidence-fatal-producer-");
        var failure = CleanupFailure(shape);
        var producer = new LifecycleProducer(failure);
        var prepared = await EvidenceHostBootstrap.PrepareAsync(Plan(), registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                RepositoryRoot = directory.Path,
                ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
                EnvironmentReader = _ => null
            });
        try
        {
            Assert.Same(failure, await Record.ExceptionAsync(() => prepared.RunAsync((_, _) =>
                throw new InvalidOperationException("A resource-free producer must not start a factory."))));
        }
        finally
        {
            Assert.Same(failure, await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask()));
        }
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task FatalPreparedOwnerDisposalIsPropagatedAfterEveryOwnerWasAttempted()
    {
        using var directory = TempDirectory.Create("evidence-fatal-disposal-");
        var fatalDisposal = new OutOfMemoryException("synthetic fatal prepared-owner disposal");
        var producer = new LifecycleProducer(disposalFailure: fatalDisposal);
        var prepared = await EvidenceHostBootstrap.PrepareAsync(Plan(),
            registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                RepositoryRoot = directory.Path,
                ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
                EnvironmentReader = _ => null
            });

        var runFailure = await Record.ExceptionAsync(() => prepared.RunAsync((_, _) =>
            throw new InvalidOperationException("No resources requested.")));
        var disposalFailure = await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask());

        Assert.Same(fatalDisposal, runFailure);
        Assert.Same(fatalDisposal, disposalFailure);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task FatalProducerFailureRemainsPrimaryWhenFatalOwnerDisposalAlsoFails()
    {
        using var directory = TempDirectory.Create("evidence-primary-fatal-");
        var producerFailure = new AccessViolationException("synthetic fatal producer callback");
        var disposalFailure = new OutOfMemoryException("synthetic fatal owner disposal");
        var producer = new LifecycleProducer(producerFailure, disposalFailure: disposalFailure);
        var prepared = await EvidenceHostBootstrap.PrepareAsync(Plan(),
            registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                RepositoryRoot = directory.Path,
                ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
                EnvironmentReader = _ => null
            });

        var runFailure = await Record.ExceptionAsync(() => prepared.RunAsync((_, _) =>
            throw new InvalidOperationException("No resources requested.")));
        var disposalObserved = await Record.ExceptionAsync(() => prepared.DisposeAsync().AsTask());

        Assert.Same(producerFailure, runFailure);
        Assert.Same(producerFailure, disposalObserved);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task CleanupGraceExpiryCannotReleaseThePreparedRegistrationOrProduceACompleteClaim()
    {
        using var directory = TempDirectory.Create("evidence-cleanup-grace-");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new LifecycleProducer(cleanupRelease: release);
        var prepared = await EvidenceHostBootstrap.PrepareAsync(Plan(), registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                RepositoryRoot = directory.Path,
                ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
                // gstack-timing-ok: exercises the cleanup grace; a barrier controls actual disposal completion.
                ProducerCleanupTimeout = TimeSpan.FromMilliseconds(20),
                EnvironmentReader = _ => null
            });
        try
        {
            var manifest = await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resources requested."))
                .WaitAsync(TimeSpan.FromSeconds(10));
            await producer.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposal = prepared.DisposeAsync().AsTask();

            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.Contains("reuse.cleanup_unsettled", manifest.Metrics.CleanupDiagnostic, StringComparison.Ordinal);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(1, producer.Disposals);

            release.SetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, producer.Runs);
            Assert.Equal(1, producer.Disposals);
        }
        finally
        {
            release.TrySetResult();
            await prepared.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PreparedExecutionRejectsNullFactoryRepeatedUseAndUseAfterDisposal()
    {
        using var directory = TempDirectory.Create("evidence-single-use-");
        var producer = new LifecycleProducer();
        var prepared = await EvidenceHostBootstrap.PrepareAsync(Plan(), registration => registration.AddProducer(producer, "fixture-v1"),
            new EvidenceHostOptions
            {
                RepositoryRoot = directory.Path,
                ArtifactDirectory = TestPathUtils.PathUnder(directory.Path, "artifacts"),
                EnvironmentReader = _ => null
            });
        try
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => prepared.RunAsync(null!));
            Assert.Equal(0, producer.Runs);
            var manifest = await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resources requested."));
            Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
            await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.RunAsync((_, _) => throw new InvalidOperationException("Must not run twice.")));
        }
        finally { await prepared.DisposeAsync(); }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => prepared.RunAsync((_, _) => throw new InvalidOperationException("Must not run after disposal.")));
        await prepared.DisposeAsync();
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    private static Exception CleanupFailure(string shape) => shape switch
    {
        "access" => new AccessViolationException("synthetic fatal cleanup"),
        "memory" => new OutOfMemoryException("synthetic fatal cleanup"),
        "stack" => new StackOverflowException("synthetic fatal cleanup"),
        "domain" => new AppDomainUnloadedException("synthetic fatal cleanup"),
        _ => new IOException("controlled cleanup failure")
    };

    private static EvidencePlan Plan()
    {
        var producer = new EvidenceProducerDeclaration("check", "custom", "1", [], ["valid"], [], 10);
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [producer],
            [new EvidenceObligation("check", "test", "Fixture", [producer.Id], "valid")]);
        return new EvidencePlanner().Resolve(new EvidencePolicy("fixture", "1", profile.Id, [profile], [new("all", "**", profile.Id)]),
            [new NormalizedDiffPath("input.txt")]);
    }

    private static EvidencePlan PlanWithResource()
    {
        var producer = new EvidenceProducerDeclaration("check", "custom", "1", ["database"], ["valid"], [], 10);
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [new("database", "completion", 10, [])], [producer],
            [new EvidenceObligation("check", "test", "Fixture", [producer.Id], "valid")]);
        return new EvidencePlanner().Resolve(new("fixture", "1", profile.Id, [profile], [new("all", "**", profile.Id)]),
            [new NormalizedDiffPath("input.txt")]);
    }

    private sealed class SynchronousProducer(Exception failure) : IEvidenceProducer, IDisposable
    {
        public string Id => "check";
        internal int Disposals;
        internal int Runs;
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["valid"]));
        }
        public void Dispose() { Disposals++; throw failure; }
    }

    private sealed class SynchronousReadiness(List<string> order) : IEvidenceResourceReadiness, IDisposable
    {
        public string Id => "readiness";
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => order.Add("readiness");
    }

    private sealed class ApplicationOwner(List<string> order) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { order.Add("application"); return ValueTask.CompletedTask; }
    }

    private sealed class NamedReadiness(string id, List<string> order) : IEvidenceResourceReadiness, IDisposable
    {
        public string Id => id;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => order.Add("readiness");
    }

    private sealed class MaterialOwner : IEvidenceProducer, IEvidenceProducerKeyMaterial, IAsyncDisposable
    {
        public string Id => "material-owner";
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int MaterialCalls;
        internal int Disposals;
        internal int Runs;
        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(EvidenceProducerKeyMaterialContext context,
            CancellationToken cancellationToken)
        {
            MaterialCalls++;
            Entered.TrySetResult();
            return new(Release.Task);
        }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["valid"]));
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LateFatalMaterialOwner : IEvidenceProducer, IEvidenceProducerKeyMaterial, IAsyncDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CallbackSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Runs;
        internal int Disposals;
        public string Id => "late-fatal-material";

        public async ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            try { return await Release.Task.ConfigureAwait(false); }
            finally { CallbackSettled.TrySetResult(); }
        }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["valid"]));
        }

        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        internal string Path { get; } = path;
        internal static TempDirectory Create(string prefix) => new(Directory.CreateTempSubdirectory(prefix).FullName);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class LifecycleProducer(Exception? failure = null, TaskCompletionSource? cleanupRelease = null,
        Exception? disposalFailure = null)
        : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => "check";
        internal readonly TaskCompletionSource CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Runs;
        internal int Disposals;
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return failure is null
                ? ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["valid"]))
                : ValueTask.FromException<EvidenceProducerResult>(failure);
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            CleanupEntered.TrySetResult();
            if (cleanupRelease is not null) await cleanupRelease.Task;
            if (disposalFailure is not null) throw disposalFailure;
        }
    }
}
