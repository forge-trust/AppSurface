using System.Diagnostics;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidencePreparedRunTests
{
    [Fact]
    public async Task VerifiedHits_ShouldStartNoResourcesAndReturnOnlyInformationalCurrentClaims()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan(fixture.Declaration("a", "a.txt", "db-a"));
        var firstProducer = new Producer("a");
        var firstOwner = new Owner();
        await using (var first = await fixture.PrepareAsync(plan, firstProducer))
        {
            Assert.Equal(["base", "db-a"], first.RequiredResourceIds);
            var fresh = await first.RunAsync((ids, _) => ValueTask.FromResult(fixture.Scope(ids, firstOwner)));
            Assert.Equal(EvidenceExecutionVerdict.Passed, fresh.ExecutionVerdict);
            Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(fresh.ProducerResults).Outcome);
            Assert.Equal(1, firstOwner.Disposals);
            Assert.Equal(1, firstProducer.Disposals);
        }
        var secondProducer = new Producer("a");
        await using var second = await fixture.PrepareAsync(plan, secondProducer);
        Assert.Empty(second.RequiredResourceIds);
        Assert.Equal(EvidenceReuseStatus.Hit, Assert.Single(second.Decisions).Status);
        var factoryCalls = 0;

        var reused = await second.RunAsync((_, _) =>
        {
            factoryCalls++;
            throw new InvalidOperationException("An all-hit factory must never run.");
        });

        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, secondProducer.Runs);
        Assert.Equal(EvidenceExecutionVerdict.Passed, reused.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.LocalReusedComplete, reused.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, reused.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(second.Plan, reused));
        Assert.False(EvidenceManifestBuilder.VerifyForGate(second.Plan, reused));
        var result = Assert.Single(reused.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Reused, result.Outcome);
        Assert.Equal(0, result.ElapsedMilliseconds);
        Assert.NotNull(result.OriginManifestDigest);
        Assert.All(reused.ResourceResults, resource => Assert.Equal(EvidenceResourceOutcome.NotRequiredForExecution, resource.Outcome));
        Assert.True(File.Exists(Path.Join(second.ArtifactDirectories["a"], "reports", "result.txt")));
    }

    [Fact]
    public async Task ChangedProducerResourceFailure_ShouldPreserveOtherReusedResultsAndAvoidTheirStartup()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan(fixture.Declaration("a", "a.txt", "db-a"), fixture.Declaration("b", "b.txt", "db-b"));
        await using (var first = await fixture.PrepareAsync(plan, new Producer("a"), new Producer("b")))
            Assert.Equal(EvidenceExecutionVerdict.Passed,
                (await first.RunAsync((ids, _) => ValueTask.FromResult(fixture.Scope(ids, new Owner())))).ExecutionVerdict);
        File.WriteAllText(Path.Join(fixture.Repository, "b.txt"), "changed b");
        var a = new Producer("a");
        var b = new Producer("b");
        var owner = new Owner();
        await using var second = await fixture.PrepareAsync(plan, a, b);
        Assert.Equal(["db-b"], second.RequiredResourceIds);

        var result = await second.RunAsync((ids, _) => ValueTask.FromResult(fixture.Scope(ids, owner, fail: "db-b")));

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Reused, result.ProducerResults.Single(item => item.ProducerId == "a").Outcome);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.ProducerResults.Single(item => item.ProducerId == "b").Outcome);
        Assert.Equal(0, a.Runs);
        Assert.Equal(0, b.Runs);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(EvidenceResourceOutcome.NotRequiredForExecution, result.ResourceResults.Single(item => item.ResourceId == "db-a").Outcome);
    }

    [Fact]
    public async Task LateFactoryAfterCancellation_ShouldRemainOwnedUntilActualTransferAndCleanup()
    {
        using var fixture = new Fixture();
        var producer = new Producer("a");
        var prepared = await fixture.PrepareAsync(fixture.Plan(fixture.Declaration("a", "a.txt", "db-a")), producer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<EvidenceHostResourceScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var owner = new Owner();
        Task? disposal = null;
        try
        {
            var execution = prepared.RunAsync((_, _) =>
            {
                entered.SetResult();
                return new ValueTask<EvidenceHostResourceScope>(release.Task);
            }, cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            disposal = prepared.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(result.Metrics.CleanupCompleted);
            Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(result.ProducerResults).Outcome);
            Assert.Equal(0, producer.Disposals);
            Assert.Equal(0, owner.Disposals);
        }
        finally
        {
            release.TrySetResult(fixture.Scope(prepared.RequiredResourceIds, owner));
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            else await prepared.DisposeAsync();
        }
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, producer.Runs);
    }

    [Fact]
    public async Task ActiveCooperativeChild_ShouldKeepResourceScopeAndAuthorityUntilActualSettlement()
    {
        using var fixture = new Fixture();
        var declaration = fixture.Declaration("a", "a.txt", "db-a") with
        {
            Kind = "coverage",
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var plan = fixture.Plan(declaration);
        var producer = new CooperativeChildProducer("a");
        var owner = new Owner();
        var prepared = await fixture.PrepareAsync(plan, producer);
        Task? disposal = null;

        try
        {
            var manifest = await prepared.RunAsync((ids, _) => ValueTask.FromResult(fixture.Scope(ids, owner)))
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(producer.ChildStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
            Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(0, owner.Disposals);
            Assert.Equal(0, producer.Disposals);

            var decision = Assert.Single(prepared.Decisions);
            var identityResult = await EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(
                fixture.Repository,
                prepared.Plan,
                declaration,
                "fixture-v1",
                fixture.ResourceIdentities(plan),
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                null), CancellationToken.None);
            var identity = Assert.IsType<EvidenceProducerIdentity>(identityResult.Identity);
            Assert.Equal(identity.Key, decision.ProducerKey);
            var inFlight = EvidenceReceiptQuota.ReadAuthority(fixture.ReceiptStore, identity.RepositoryNamespace, identity.Key);
            Assert.NotNull(inFlight?.InFlightExecutionId);

            disposal = prepared.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, owner.Disposals);
            Assert.Equal(0, producer.Disposals);
            Assert.Equal(inFlight!.InFlightExecutionId,
                EvidenceReceiptQuota.ReadAuthority(fixture.ReceiptStore, identity.RepositoryNamespace, identity.Key)?.InFlightExecutionId);

            producer.ReleaseChild.TrySetResult();
            await producer.ChildTask!.WaitAsync(TimeSpan.FromSeconds(10));
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            disposal = null;

            Assert.Equal(1, owner.Disposals);
            Assert.Equal(1, producer.Disposals);
            Assert.Null(EvidenceReceiptQuota.ReadAuthority(fixture.ReceiptStore, identity.RepositoryNamespace, identity.Key)?.InFlightExecutionId);
        }
        finally
        {
            producer.ReleaseChild.TrySetResult();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            else if (producer.ChildTask is { } childTask) await childTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposal is null) await prepared.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnexpectedFactoryBinding_ShouldBeDisposedBeforeRejectingTheScope()
    {
        using var fixture = new Fixture();
        var producer = new Producer("a");
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(fixture.Plan(fixture.Declaration("a", "a.txt", "db-a")), producer);

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => prepared.RunAsync((_, _) =>
            ValueTask.FromResult(fixture.Scope(["db-b"], owner))));
        await prepared.DisposeAsync();

        Assert.Equal("reuse.resource_scope_invalid", failure.Code);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, producer.Runs);
    }

    [Fact]
    public async Task PreparedRun_ShouldBeSingleUseAndItsDisposalIdempotent()
    {
        using var fixture = new Fixture();
        var producer = new Producer("a");
        var prepared = await fixture.PrepareAsync(fixture.Plan(fixture.Declaration("a", "a.txt")), producer);
        Assert.Empty(prepared.RequiredResourceIds);
        await prepared.RunAsync((_, _) => throw new InvalidOperationException("No resource factory is needed."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.RunAsync((_, _) => throw new InvalidOperationException()));
        await prepared.DisposeAsync();
        await prepared.DisposeAsync();

        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshPreparationPreservesVersionedResourceClosureWithReuseDisabled(bool version11)
    {
        using var fixture = new Fixture();
        var declaration = fixture.Declaration("a", "a.txt", "db-a");
        if (!version11) declaration = declaration with { Inputs = null };
        var plan = fixture.Plan(declaration);
        var producer = new Producer("a");
        var owner = new Owner();
        await using var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            foreach (var (id, identity) in fixture.ResourceIdentities(plan)) registration.AddResource(id, identity);
        }, new EvidenceHostOptions
        {
            Reuse = false,
            RepositoryRoot = fixture.Repository,
            ArtifactDirectory = fixture.Options().ArtifactDirectory,
            EnvironmentReader = _ => null,
        });
        Assert.Equal(version11 ? "1.1" : "1.0", prepared.Plan.ContractVersion);
        Assert.Equal(version11 ? new[] { "base", "db-a" } : new[] { "base", "db-a", "db-b", "unused" }, prepared.RequiredResourceIds);

        var manifest = await prepared.RunAsync((ids, _) => ValueTask.FromResult(fixture.Scope(ids, owner)));

        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(0, manifest.ReuseCount);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.True(EvidenceManifestBuilder.VerifyForGate(prepared.Plan, manifest));
        foreach (var resource in manifest.ResourceResults)
            Assert.Equal(version11 && (resource.ResourceId is "db-b" or "unused")
                ? EvidenceResourceOutcome.NotRequiredForExecution : EvidenceResourceOutcome.Ready, resource.Outcome);
    }

    [Fact]
    public async Task EveryDescriptor_ShouldBeValidatedEvenWhenItsResourceWouldNotStart()
    {
        using var fixture = new Fixture();
        var plan = fixture.Plan(fixture.Declaration("a", "a.txt"));
        var producer = new Producer("a");

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => EvidenceHostBootstrap.PrepareAsync(plan, registration =>
            registration.AddProducer(producer, "fixture-v1"), fixture.Options()));

        Assert.Equal("ASEVD302", failure.Code);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task FactoryFailure_ShouldReturnUnavailableAndJoinItsExplicitPartialCleanup()
    {
        using var fixture = new Fixture();
        var producer = new Producer("a");
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(fixture.Plan(fixture.Declaration("a", "a.txt", "db-a")), producer);

        var manifest = await prepared.RunAsync(async (_, _) =>
        {
            try { throw new IOException("controlled startup failure"); }
            finally { await owner.DisposeAsync(); }
        });
        await prepared.DisposeAsync();

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, producer.Runs);
    }

    [Fact]
    public async Task ProtectedReuse_ShouldRejectBeforeRegistrationCallbacksOrResourceStartup()
    {
        using var fixture = new Fixture();
        var registered = false;
        var options = fixture.Options() with { EnvironmentReader = name => name == "GITHUB_ACTIONS" ? "true" : null };

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => EvidenceHostBootstrap.PrepareAsync(
            fixture.Plan(fixture.Declaration("a", "a.txt")), _ => registered = true, options));

        Assert.Equal("reuse.local_only", failure.Code);
        Assert.False(registered);
        Assert.False(Directory.Exists(options.ArtifactDirectory));
    }

    [Fact]
    public async Task OpaqueIdentifierAndFrozenInputs_ShouldNotBecomePathsOrObserveCallerMutation()
    {
        using var fixture = new Fixture();
        string[] includes = ["a.txt"];
        var declaration = fixture.Declaration("../opaque", "a.txt") with { Inputs = new(includes, []) };
        var plan = fixture.Plan(declaration);
        var producer = new Producer("../opaque");
        await using var prepared = await fixture.PrepareAsync(plan, producer);
        includes[0] = "b.txt";

        var manifest = await prepared.RunAsync((_, _) => throw new InvalidOperationException());

        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal("a.txt", prepared.Plan.Profile.Producers[0].Inputs!.Include[0]);
        var artifactRoot = prepared.ArtifactDirectories["../opaque"];
        Assert.StartsWith(prepared.ArtifactDirectory + Path.DirectorySeparatorChar, artifactRoot, StringComparison.Ordinal);
        Assert.DoesNotContain("..", Path.GetRelativePath(prepared.ArtifactDirectory, artifactRoot), StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private int _run;
        internal string Repository { get; }
        internal string ReceiptStore => Path.Join(_root, "store");
        internal Fixture()
        {
            _root = Directory.CreateTempSubdirectory("appsurface-prepared-917-").FullName;
            if (OperatingSystem.IsMacOS() && _root.StartsWith("/var/", StringComparison.Ordinal)) _root = "/private" + _root;
            Repository = Path.Join(_root, "repo");
            Directory.CreateDirectory(Repository);
            Git("init", "-q");
            Git("config", "commit.gpgsign", "false");
            Git("config", "core.hooksPath", Path.Join(_root, "unused-hooks"));
            File.WriteAllText(Path.Join(Repository, "a.txt"), "a input");
            File.WriteAllText(Path.Join(Repository, "b.txt"), "b input");
            Git("add", ".");
            Git("-c", "user.name=Evidence tests", "-c", "user.email=evidence@example.invalid", "commit", "-qm", "fixture");
        }
        internal EvidenceHostOptions Options() => new()
        {
            Reuse = true,
            RepositoryRoot = Repository,
            ReceiptStore = ReceiptStore,
            ReceiptWaitTimeout = TimeSpan.FromSeconds(10),
            ProducerCleanupTimeout = TimeSpan.FromMilliseconds(100),
            ArtifactDirectory = Path.Join(_root, "run-" + Interlocked.Increment(ref _run)),
            EnvironmentReader = _ => null,
        };
        internal EvidenceProducerDeclaration Declaration(string id, string input, params string[] resources) => new(id, "custom", "1",
            resources, ["ok"], [new("report", "reports", "text/plain", true, 1024)], 30)
        { Inputs = new([input], []) };
        internal EvidencePlan Plan(params EvidenceProducerDeclaration[] producers)
        {
            var resources = new EvidenceResourceDeclaration[]
            {
                new("base", "completion", 30, []), new("db-a", "completion", 30, ["base"]),
                new("db-b", "completion", 30, []), new("unused", "completion", 30, []),
            };
            var obligations = producers.Select(producer => new EvidenceObligation("obligation-" + producer.Id, "test", "Fixture", [producer.Id], "ok")).ToArray();
            var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, resources, producers, obligations);
            return new EvidencePlanner().Resolve(new("fixture", "1", "local", [profile], [new("all", "**", "local")]), [new("a.txt")]);
        }
        internal IReadOnlyDictionary<string, string> ResourceIdentities(EvidencePlan plan) =>
            plan.Profile.Resources.ToDictionary(static resource => resource.Id, resource => "fixture-" + resource.Id + "-v1", StringComparer.Ordinal);
        internal Task<EvidencePreparedRun> PrepareAsync(EvidencePlan plan, params IEvidenceProducer[] producers) => EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            foreach (var producer in producers) registration.AddProducer(producer, "fixture-v1");
            foreach (var (id, identity) in ResourceIdentities(plan)) registration.AddResource(id, identity);
        }, Options());
        internal EvidenceHostResourceScope Scope(IReadOnlyList<string> ids, Owner owner, string? fail = null) =>
            new(ids.Select(id => (IEvidenceResourceReadiness)new Readiness(id, id == fail)).ToArray(), owner);
        private void Git(params string[] arguments)
        {
            using var process = new Process { StartInfo = new("git") { WorkingDirectory = Repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output + error);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class Producer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => id;
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }
        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            await context.Artifacts!.WriteAsync("report", "reports/result.txt", Encoding.UTF8.GetBytes("bounded report"), cancellationToken);
            return new(id, EvidenceProducerOutcome.Passed, ["ok"], Artifacts: context.Artifacts.WrittenArtifacts);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class CooperativeChildProducer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        internal TaskCompletionSource ChildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseChild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? ChildTask { get; private set; }
        internal int Disposals { get; private set; }
        public string Id => id;

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ChildTask = EvidenceOwnedChild.RunAsync(async _ =>
            {
                ChildStarted.TrySetResult();
                await ReleaseChild.Task.ConfigureAwait(false);
            }, context.OwnedChildEnvironment!, CancellationToken.None);
            await ChildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await context.Artifacts!.WriteAsync("report", "reports/result.txt", Encoding.UTF8.GetBytes("bounded report"), cancellationToken);
            return new(id, EvidenceProducerOutcome.Passed, ["ok"], Artifacts: context.Artifacts.WrittenArtifacts);
        }

        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Readiness(string id, bool fail) : IEvidenceResourceReadiness
    {
        public string Id => id;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => fail
            ? Task.FromException(new InvalidOperationException("Controlled unavailable fixture")) : Task.CompletedTask;
    }
    private sealed class Owner : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
