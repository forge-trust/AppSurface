using Aspire.Hosting;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceHostBootstrapTests
{
    [Fact]
    public async Task EvidenceAspireApplication_ShouldDisposePartialApplicationWhenAspireStartupFails()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(EvidenceHostBootstrapTests).Assembly.GetName().Name,
            ProjectDirectory = repoRoot,
            DisableDashboard = true,
        });

        var exception = await Record.ExceptionAsync(() => EvidenceAspireApplication.StartAsync(builder));

        Assert.NotNull(exception);
        Assert.IsNotType<ObjectDisposedException>(exception);
    }

    [Fact]
    public void EvidenceHostException_ShouldExposeTheStableDiagnosticAndRecoveryAction()
    {
        var exception = new EvidenceHostException("ASEVD999", "Evidence collection failed.", "Register a compatible producer.");

        Assert.Equal("ASEVD999", exception.Code);
        Assert.Equal("Register a compatible producer.", exception.Fix);
    }

    [Fact]
    public async Task RunAsync_ShouldCloseObligationAfterExplicitResourceAndProducerRegistrations()
    {
        var resource = new ReadyResource("postgres");
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(resource);
                registration.AddProducer(producer);
            });

        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, host.Plan);
        var manifest = await run.RunAsync(host);

        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(["persistence"], manifest.ClosedObligationIds);
        var resourceResult = Assert.Single(manifest.ResourceResults);
        Assert.Equal(EvidenceResourceOutcome.Ready, resourceResult.Outcome);
        Assert.Equal(1, resource.WaitCount);
        Assert.Equal(1, producer.RunCount);
        var context = Assert.IsType<EvidenceProducerContext>(producer.LastContext);
        Assert.Same(host.Plan, context.Plan);
        Assert.Equal("coverage", context.Producer.Id);
        Assert.Same(TimeProvider.System, context.TimeProvider);
        Assert.Equal(EvidenceHostState.Completed, host.State);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task RunAsync_ShouldReturnIncompleteClaimWhenResourceDeadlineExpires()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(resourceDeadlineSeconds: 1),
            registration =>
            {
                registration.AddResource(new CallerCancellableResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var manifest = await RunAcceptedAsync(host);

        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Contains("did not become ready", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldEnforceProducerDeadlineAndReturnTerminalManifestAfterCooperativeStop()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(producerTimeoutSeconds: 1),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new CallerCancellableProducer("coverage"));
            });

        var manifest = await RunAcceptedAsync(host);

        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.TimedOut, result.Outcome);
        Assert.Equal(nameof(EvidenceWorkerTerminalCode.DeadlineExceeded), manifest.Metrics.TerminalFailureCode);
        Assert.True(manifest.Metrics.CleanupCompleted);
    }

    [Fact]
    public async Task RunAsync_ShouldRequireExplicitProducerRegistration()
    {
        var resource = new DisposableReadyResource("postgres");
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration => registration.AddResource(resource));

        var exception = await Assert.ThrowsAsync<EvidenceHostException>(() => RunAcceptedAsync(host));

        Assert.Contains("ASEVD303", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task RunSharedCore_ShouldEmitObservationOnlyForDependencyFreeProfile()
    {
        var producer = new PassingProducer("inventory", "inventory/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(
            EvidenceHostAdmissionTestRun.CreateObservationPlan(),
            registration => registration.AddProducer(producer));

        var manifest = await RunAcceptedAsync(host, EvidenceExecutionMode.Observation);

        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.Equal(1, producer.RunCount);
    }

    [Fact]
    public async Task RunSharedCore_ObservationRejectsResourceBackedProfileBeforeConfiguration()
    {
        var configured = false;
        var resource = new ReadyResource("postgres");
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                configured = true;
                registration.AddResource(resource);
                registration.AddProducer(producer);
            });

        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, host.Plan);
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD406", exception.Code);
        Assert.False(configured);
        Assert.Equal(0, resource.WaitCount);
        Assert.Equal(0, producer.RunCount);
    }

    [Fact]
    public async Task RunAsync_ShouldRequireAcceptedEnvelopeForReleaseProfile()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(scope: EvidenceProfileScope.Release),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var manifest = await RunAcceptedAsync(host);

        Assert.Equal(EvidenceClaimKind.ReleaseComplete, manifest.ClaimKind);
        Assert.Equal(EvidenceEnvelopeStatus.ValidatedNotAttested, manifest.EnvelopeStatus);
    }

    [Fact]
    public async Task RunSharedCore_TrustedAdmissionWithoutAcceptanceRejectsBeforeConfiguration()
    {
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(scope: EvidenceProfileScope.Release),
            registration =>
            {
                configured = true;
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        using var run = EvidenceHostAdmissionTestRun.Create(
            EvidenceExecutionMode.Trusted,
            host.Plan,
            consumerAcceptanceMatches: false);
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD407", exception.Code);
        Assert.False(configured);
    }

    [Fact]
    public async Task RunSharedCore_TrustedVerifierRejectionRejectsBeforeConfiguration()
    {
        var configured = false;
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(scope: EvidenceProfileScope.Release),
            registration =>
            {
                configured = true;
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        using var run = EvidenceHostAdmissionTestRun.Create(
            EvidenceExecutionMode.Trusted,
            host.Plan,
            verifierAccepts: false);
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD408", exception.Code);
        Assert.False(configured);
    }

    [Fact]
    public async Task RunSharedCore_RejectsAspireFactoryBeforeInvocationWithoutRestrictedChild()
    {
        var factoryInvoked = false;
        var resource = new ReadyResource("postgres");
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(resource);
                registration.AddProducer(producer);
                registration.SetApplicationFactory(() =>
                {
                    factoryInvoked = true;
                    throw new InvalidOperationException("An unrestricted Aspire factory must not run.");
                });
            });

        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, host.Plan);
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD407", exception.Code);
        Assert.False(factoryInvoked);
        Assert.Equal(0, resource.WaitCount);
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task RunSharedCore_RejectsMismatchedCompleteRegistrationBeforeReadinessOrProducer()
    {
        var plan = CreatePlan();
        var resource = new ReadyResource("postgres");
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        var changedDeclaration = plan.Profile.Resources.Single() with { DeadlineSeconds = 31 };
        await using var host = EvidenceHostBootstrap.Create(
            plan,
            registration =>
            {
                registration.AddResource(changedDeclaration, resource);
                registration.AddProducer(producer);
            });

        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, host.Plan);
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD404", exception.Code);
        Assert.Equal(0, resource.WaitCount);
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task RunAsync_ShouldRecordUnavailableResourceAndFailedProducerOutcomes()
    {
        await using var unavailable = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new FailingResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var unavailableManifest = await RunAcceptedAsync(unavailable);
        Assert.Equal(EvidenceResourceOutcome.Unavailable, Assert.Single(unavailableManifest.ResourceResults).Outcome);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, Assert.Single(unavailableManifest.ProducerResults).Outcome);

        await using var failed = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new FailingProducer("coverage"));
            });

        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single((await RunAcceptedAsync(failed)).ProducerResults).Outcome);
    }

    [Fact]
    public async Task RunAsync_ShouldPropagateCriticalProducerFailure()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new CriticalFailingProducer("coverage"));
            });

        await Assert.ThrowsAsync<OutOfMemoryException>(() => RunAcceptedAsync(host));
    }

    [Fact]
    public void Registration_ShouldRequireOneDistinctEntryForEachExplicitCapability()
    {
        var registration = new EvidenceHostRegistration();
        registration.AddResource(new ReadyResource("postgres"));
        registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
        registration.SetEnvelopeVerifier(new StaticEnvelopeVerifier(accepted: true));

        Assert.Throws<InvalidOperationException>(() => registration.AddResource(new ReadyResource("postgres")));
        Assert.Throws<InvalidOperationException>(() => registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1")));
        Assert.Throws<InvalidOperationException>(() => registration.SetEnvelopeVerifier(new StaticEnvelopeVerifier(accepted: true)));
    }

    [Fact]
    public async Task RunSharedCore_ShouldRequireResourcesAndRemainSingleUse()
    {
        await using var missingResource = EvidenceHostBootstrap.Create(CreatePlan(), _ => { });
        var resourceException = await Assert.ThrowsAsync<EvidenceHostException>(() => RunAcceptedAsync(missingResource));
        Assert.Contains("ASEVD302", resourceException.Message, StringComparison.Ordinal);

        await using var singleUse = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, singleUse.Plan);
        await run.RunAsync(singleUse);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(singleUse));
    }

    [Fact]
    public async Task RunAsync_ShouldReportCallerCancellationAndInvalidProducerOutputs()
    {
        using var cancellation = new CancellationTokenSource();
        var blockingResource = new CallerCancellableResource("postgres");
        await using var cancelled = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(blockingResource);
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        using var cancelledRun = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, cancelled.Plan);
        var cancellationTask = cancelledRun.RunAsync(cancelled, cancellation.Token);
        await blockingResource.WaitStarted.Task;
        cancellation.Cancel();
        var cancelledManifest = await cancellationTask;
        Assert.Equal(EvidenceResourceOutcome.Cancelled, Assert.Single(cancelledManifest.ResourceResults).Outcome);
        Assert.Equal(nameof(EvidenceWorkerTerminalCode.CallerCancelled), cancelledManifest.Metrics.TerminalFailureCode);
        Assert.True(cancelledManifest.Metrics.CleanupCompleted);
        Assert.Equal(EvidenceClaimKind.None, cancelledManifest.ClaimKind);
        Assert.True(cancelledRun.Supervisor.SawFreshStoppingToken);
        Assert.Equal(1, cancelledRun.Supervisor.CompletionAcknowledgements);
        Assert.True(cancelledRun.Supervisor.CompletionToken is { CanBeCanceled: true, IsCancellationRequested: false });
        Assert.NotEqual(cancellation.Token, cancelledRun.Supervisor.CompletionToken);

        await using var wrongId = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new WrongIdProducer("coverage"));
            });
        Assert.Equal(EvidenceProducerOutcome.Invalid, Assert.Single((await RunAcceptedAsync(wrongId)).ProducerResults).Outcome);

        await using var spoofedArtifacts = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new SpoofedArtifactProducer("coverage"));
            });
        Assert.Equal(EvidenceProducerOutcome.Invalid, Assert.Single((await RunAcceptedAsync(spoofedArtifacts)).ProducerResults).Outcome);
    }

    [Fact]
    public async Task RunAsync_ShouldProtectResourceOrderingAndRequiredArtifactIntegrity()
    {
        var cyclicPlan = SealPlan(CreatePlan().Profile with
            {
                Resources =
                [
                    new EvidenceResourceDeclaration("postgres", "aspire_health", 30, ["redis"]),
                    new EvidenceResourceDeclaration("redis", "aspire_health", 30, ["postgres"]),
                ],
            });
        await using var cyclic = EvidenceHostBootstrap.Create(
            cyclicPlan,
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddResource(new ReadyResource("redis"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var cycleException = await Assert.ThrowsAsync<EvidenceHostException>(() => RunAcceptedAsync(cyclic));
        Assert.Contains("ASEVD304", cycleException.Message, StringComparison.Ordinal);

        var artifactPlan = SealPlan(CreatePlan().Profile with
            {
                Producers =
                [
                    new EvidenceProducerDeclaration(
                        "coverage",
                        "coverage",
                        "1.0.0",
                        ["postgres"],
                        ["coverage/assertion@1"],
                        [new EvidenceArtifactSlot("report", "coverage", "text/plain", Required: true, MaximumBytes: 16)],
                        30),
                ],
            });
        await using var requiredArtifact = EvidenceHostBootstrap.Create(
            artifactPlan,
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var requiredArtifactManifest = await RunAcceptedAsync(requiredArtifact);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(requiredArtifactManifest.ProducerResults).Outcome);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, requiredArtifactManifest.ExecutionVerdict);
    }

    [Fact]
    public async Task RunAsync_ShouldInvalidateAProducerWhenItsWrittenArtifactChangesBeforeCollection()
    {
        var artifactPlan = SealPlan(CreatePlan().Profile with
            {
                Producers =
                [
                    new EvidenceProducerDeclaration(
                        "coverage",
                        "coverage",
                        "1.0.0",
                        ["postgres"],
                        ["coverage/assertion@1"],
                        [new EvidenceArtifactSlot("report", "coverage", "text/plain", Required: true, MaximumBytes: 16)],
                        30),
                ],
            });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, artifactPlan);
        await using var host = EvidenceHostBootstrap.Create(
            artifactPlan,
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new TamperingArtifactProducer("coverage", run.ArtifactDirectory));
            });

        var manifest = await run.RunAsync(host);

        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Contains("final verification", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(EvidenceExecutionVerdict.Invalid, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
    }

    [Fact]
    public async Task RunAsync_ShouldProtectLifecycleBoundsSharedDependenciesAndCallerCancelledProducers()
    {
        var oversizedPlan = SealPlan(CreatePlan().Profile with
            {
                Resources = Enumerable.Range(0, 17).Select(index => new EvidenceResourceDeclaration($"resource-{index}", "aspire_health", 30, [])).ToArray(),
            });
        await using var oversized = EvidenceHostBootstrap.Create(oversizedPlan, _ => { });
        var oversizedException = await Assert.ThrowsAsync<EvidenceHostException>(() => RunAcceptedAsync(oversized));
        Assert.Contains("ASEVD301", oversizedException.Message, StringComparison.Ordinal);

        var dependencyPlan = SealPlan(CreatePlan().Profile with
            {
                Resources =
                [
                    new EvidenceResourceDeclaration("postgres", "aspire_health", 30, []),
                    new EvidenceResourceDeclaration("cache", "aspire_health", 30, ["postgres"]),
                    new EvidenceResourceDeclaration("search", "aspire_health", 30, ["postgres"]),
                ],
            });
        var postgres = new SyncDisposableReadyResource("postgres");
        await using var dependencyHost = EvidenceHostBootstrap.Create(
            dependencyPlan,
            registration =>
            {
                registration.AddResource(postgres);
                registration.AddResource(new ReadyResource("cache"));
                registration.AddResource(new ReadyResource("search"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var dependencyManifest = await RunAcceptedAsync(dependencyHost);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, dependencyManifest.ClaimKind);
        Assert.Equal(1, postgres.DisposeCount);

        var missingDependencyPlan = SealPlan(CreatePlan().Profile with
            {
                Resources = [new EvidenceResourceDeclaration("postgres", "aspire_health", 30, ["missing"])],
            });
        await using var missingDependency = EvidenceHostBootstrap.Create(
            missingDependencyPlan,
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });
        var missingDependencyException = await Assert.ThrowsAsync<EvidenceHostException>(() => RunAcceptedAsync(missingDependency));
        Assert.Contains("ASEVD305", missingDependencyException.Message, StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        var producer = new CallerCancellableProducer("coverage");
        await using var cancelled = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ReadyResource("postgres"));
                registration.AddProducer(producer);
            });
        using var cancelledAdmission = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, cancelled.Plan);
        var cancelledRun = cancelledAdmission.RunAsync(cancelled, cancellation.Token);
        await producer.Started.Task;
        cancellation.Cancel();
        var cancelledManifest = await cancelledRun;
        Assert.Equal(EvidenceProducerOutcome.Cancelled, Assert.Single(cancelledManifest.ProducerResults).Outcome);
        Assert.Equal(nameof(EvidenceWorkerTerminalCode.CallerCancelled), cancelledManifest.Metrics.TerminalFailureCode);
        Assert.True(cancelledManifest.Metrics.CleanupCompleted);
        Assert.Equal(EvidenceClaimKind.None, cancelledManifest.ClaimKind);
        Assert.True(cancelledAdmission.Supervisor.SawFreshStoppingToken);
        Assert.Equal(1, cancelledAdmission.Supervisor.CompletionAcknowledgements);
        Assert.True(cancelledAdmission.Supervisor.CompletionToken is { CanBeCanceled: true, IsCancellationRequested: false });
        Assert.NotEqual(cancellation.Token, cancelledAdmission.Supervisor.CompletionToken);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeRegisteredResourcesAndProducersOnce()
    {
        var resource = new DisposableReadyResource("postgres");
        var producer = new DisposablePassingProducer("coverage", "coverage/assertion@1");
        var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(resource);
                registration.AddProducer(producer);
            });

        await RunAcceptedAsync(host);
        await host.DisposeAsync();
        await host.DisposeAsync();

        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_ShouldInvalidateCompleteClaimWhenCleanupFails()
    {
        await using var host = EvidenceHostBootstrap.Create(
            CreatePlan(),
            registration =>
            {
                registration.AddResource(new ThrowingDisposableReadyResource("postgres"));
                registration.AddProducer(new PassingProducer("coverage", "coverage/assertion@1"));
            });

        var manifest = await RunAcceptedAsync(host);

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.False(manifest.Metrics.CleanupCompleted);
        Assert.Contains("cleanup failed", manifest.Metrics.CleanupDiagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunSharedCore_ShouldWriteAndVerifyArtifactAfterAtomicCompleteRegistration()
    {
        var plan = SealPlan(CreatePlan().Profile with
        {
            Producers =
            [
                CreatePlan().Profile.Producers.Single() with
                {
                    ArtifactSlots = [new EvidenceArtifactSlot("report", "coverage", "text/plain", Required: true, MaximumBytes: 16)],
                },
            ],
        });
        var resource = new DisposableReadyResource("postgres");
        var producer = new ArtifactWritingProducer("coverage");
        var wrongResource = new ReadyResource("wrong-resource");
        var wrongProducer = new PassingProducer("wrong-producer", "coverage/assertion@1");
        var configureCount = 0;
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, plan);
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            configureCount++;
            var resourceMismatch = Assert.Throws<ArgumentException>(() =>
                registration.AddResource(plan.Profile.Resources.Single(), wrongResource));
            var producerMismatch = Assert.Throws<ArgumentException>(() =>
                registration.AddProducer(plan.Profile.Producers.Single(), wrongProducer));
            Assert.Equal("resource", resourceMismatch.ParamName);
            Assert.Equal("producer", producerMismatch.ParamName);
            registration.AddResource(plan.Profile.Resources.Single(), resource);
            registration.AddProducer(plan.Profile.Producers.Single(), producer);
        });

        var manifest = await run.RunAsync(host);

        Assert.Equal(1, configureCount);
        Assert.Equal(0, wrongResource.WaitCount);
        Assert.Equal(0, wrongProducer.RunCount);
        Assert.Equal(1, resource.WaitCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.RunCount);
        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        var artifact = Assert.Single(result.Artifacts!);
        var bytes = await File.ReadAllBytesAsync(Path.Join(run.ArtifactDirectory, "coverage", artifact.RelativePath));
        Assert.Equal("written"u8.ToArray(), bytes);
        Assert.Equal(bytes.LongLength, artifact.LengthBytes);
        Assert.Equal(EvidenceDigest.Sha256(bytes), artifact.Sha256);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.Equal(EvidenceDigest.CanonicalSha256(manifest with { ManifestDigest = string.Empty }), manifest.ManifestDigest);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task RunSharedCore_ShouldRejectUnrestrictedFactoryAfterHealthRegistrationWithoutInvokingEitherFactory()
    {
        var plan = CreatePlan();
        var declaration = plan.Profile.Resources.Single();
        var producer = new DisposablePassingProducer("coverage", "coverage/assertion@1");
        var factoryCount = 0;
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            Assert.Throws<ArgumentException>(() => registration.AddAspireHealthResource(
                declaration with { Readiness = "unsupported" }, "database"));
            registration.AddAspireHealthResource(declaration, "database");
            Assert.Throws<InvalidOperationException>(() => registration.AddAspireHealthResource(declaration, "another-database"));
            registration.AddProducer(plan.Profile.Producers.Single(), producer);
            registration.SetApplicationFactory(() =>
            {
                factoryCount++;
                throw new InvalidOperationException("An unrestricted application must not start.");
            });
            Assert.Throws<InvalidOperationException>(() => registration.SetApplicationFactory(() =>
            {
                factoryCount++;
                throw new InvalidOperationException("A replacement application must not start.");
            }));
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, plan);

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD407", exception.Code);
        Assert.Equal(0, factoryCount);
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(1, run.Supervisor.StopRequests);
        Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
    }

    [Theory]
    [InlineData("producer-declaration")]
    [InlineData("extra-producer")]
    [InlineData("extra-resource")]
    public async Task RunSharedCore_ShouldRejectRegistrationDriftAndDisposeBeforeAnyStageStarts(string drift)
    {
        var plan = CreatePlan();
        var resource = new DisposableReadyResource("postgres");
        var producer = new DisposablePassingProducer("coverage", "coverage/assertion@1");
        var extraResource = new DisposableReadyResource("extra-resource");
        var extraProducer = new DisposablePassingProducer("extra-producer", "coverage/assertion@1");
        var configureCount = 0;
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            configureCount++;
            registration.AddResource(plan.Profile.Resources.Single(), resource);
            var declaration = plan.Profile.Producers.Single();
            registration.AddProducer(drift == "producer-declaration"
                ? declaration with { TimeoutSeconds = declaration.TimeoutSeconds + 1 }
                : declaration, producer);
            if (drift == "extra-producer") registration.AddProducer(extraProducer);
            if (drift == "extra-resource") registration.AddResource(extraResource);
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, plan);

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD404", exception.Code);
        Assert.Equal(1, configureCount);
        Assert.Equal(0, resource.WaitCount);
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(0, extraResource.WaitCount);
        Assert.Equal(0, extraProducer.RunCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(drift == "extra-resource" ? 1 : 0, extraResource.DisposeCount);
        Assert.Equal(drift == "extra-producer" ? 1 : 0, extraProducer.DisposeCount);
        Assert.Equal(1, run.Supervisor.StopRequests);
        Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task RunSharedCore_ShouldRejectInsufficientAggregateBudgetBeforeConfigurationAndConsumeAttempt()
    {
        var configureCount = 0;
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), _ => configureCount++);
        using var run = EvidenceHostAdmissionTestRun.Create(
            EvidenceExecutionMode.Trusted, host.Plan, jobRemaining: TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD410", exception.Code);
        Assert.Equal(0, configureCount);
        Assert.False(Directory.Exists(run.ArtifactDirectory));
        Assert.Equal(0, run.Supervisor.StopRequests);
        Assert.Equal(0, run.Supervisor.ExitAcknowledgements);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(121)]
    public async Task RunSharedCore_ShouldRejectInvalidStartAllowanceWithoutConsumingTheHost(int seconds)
    {
        var configureCount = 0;
        var resource = new ReadyResource("postgres");
        var producer = new PassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), registration =>
        {
            configureCount++;
            registration.AddResource(resource);
            registration.AddProducer(producer);
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, host.Plan);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            run.RunAsync(host, loweredStartAllowance: TimeSpan.FromSeconds(seconds)));
        Assert.Equal("loweredStartAllowance", exception.ParamName);
        Assert.Equal(0, configureCount);
        Assert.False(Directory.Exists(run.ArtifactDirectory));
        Assert.Equal(0, run.Supervisor.StopRequests);

        var manifest = await run.RunAsync(host, loweredStartAllowance: TimeSpan.FromSeconds(30));

        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(1, configureCount);
        Assert.Equal(1, resource.WaitCount);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunSharedCore_ShouldCleanPartialConfigurationAfterFailureOrCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var configureCount = 0;
        var resource = new DisposableReadyResource("postgres");
        var producer = new DisposablePassingProducer("coverage", "coverage/assertion@1");
        var host = EvidenceHostBootstrap.Create(CreatePlan(), registration =>
        {
            configureCount++;
            registration.AddResource(resource);
            registration.AddProducer(producer);
            if (cancel) cancellation.Cancel();
            else throw new InvalidOperationException("Configuration failed after taking ownership.");
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, host.Plan);
        await using (host)
        {
            var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host, cancellation.Token));
            Assert.Equal("ASEVD410", exception.Code);
            await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(host));
            Assert.Equal(1, resource.DisposeCount);
            Assert.Equal(1, producer.DisposeCount);
        }

        Assert.Equal(1, configureCount);
        Assert.Equal(0, resource.WaitCount);
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(1, run.Supervisor.StopRequests);
        Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
        Assert.Equal(EvidenceHostState.Disposed, host.State);
    }

    [Theory]
    [InlineData("undefined-outcome", EvidenceProducerOutcome.Invalid)]
    [InlineData("null-assertions", EvidenceProducerOutcome.Invalid)]
    [InlineData("foreign-assertion", EvidenceProducerOutcome.Invalid)]
    [InlineData("null-result", EvidenceProducerOutcome.Failed)]
    [InlineData("failed-result", EvidenceProducerOutcome.Failed)]
    public async Task RunSharedCore_ShouldStopAfterMalformedOrFailedResultWithoutInvokingLaterProducer(
        string failure,
        EvidenceProducerOutcome expectedOutcome)
    {
        var firstDeclaration = CreatePlan().Profile.Producers.Single();
        var plan = SealPlan(CreatePlan().Profile with
        {
            Producers = [firstDeclaration, firstDeclaration with { Id = "later" }],
        });
        var producer = new ResultProducer("coverage", failure switch
        {
            "undefined-outcome" => new("coverage", (EvidenceProducerOutcome)int.MaxValue, ["coverage/assertion@1"]),
            "null-assertions" => new("coverage", EvidenceProducerOutcome.Passed, null!),
            "foreign-assertion" => new("coverage", EvidenceProducerOutcome.Passed, ["not-declared/assertion@1"]),
            "null-result" => null,
            _ => new("coverage", EvidenceProducerOutcome.Failed, []),
        });
        var later = new DisposablePassingProducer("later", "coverage/assertion@1");
        var resource = new DisposableReadyResource("postgres");
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            registration.AddResource(resource);
            registration.AddProducer(producer);
            registration.AddProducer(later);
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, plan);

        var manifest = await run.RunAsync(host);

        var result = Assert.Single(manifest.ProducerResults);
        Assert.Equal("coverage", result.ProducerId);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(0, later.RunCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(1, later.DisposeCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        Assert.True(manifest.Metrics.CleanupCompleted);
        if (failure != "null-result")
        {
            Assert.Equal(nameof(EvidenceWorkerTerminalCode.StageFailed), manifest.Metrics.TerminalFailureCode);
        }
        Assert.Equal(1, run.Supervisor.StopRequests);
        Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task RunSharedCore_ShouldRejectCollectionWhenWorkerCompletionFailsAfterOwnedCleanup()
    {
        var resource = new DisposableReadyResource("postgres");
        var producer = new DisposablePassingProducer("coverage", "coverage/assertion@1");
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), registration =>
        {
            registration.AddResource(resource);
            registration.AddProducer(producer);
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, host.Plan);
        var completionCount = 0;

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() => run.RunAsync(host, completeWorker: token =>
        {
            completionCount++;
            Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
            Assert.Equal(1, resource.DisposeCount);
            Assert.Equal(1, producer.DisposeCount);
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            return ValueTask.FromException(new IOException("Worker completion was not acknowledged."));
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(host));

        Assert.Equal("ASEVD410", exception.Code);
        Assert.Contains("Manifest collection", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, completionCount);
        Assert.Equal(1, resource.WaitCount);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(0, run.Supervisor.CompletionAcknowledgements);
        Assert.NotEqual(EvidenceHostState.Completed, host.State);
    }

    [Fact]
    public async Task RunSharedCore_ShouldSerializeConcurrentAttemptsWithoutReconfiguringOrRerunningProducer()
    {
        var configureCount = 0;
        var producer = new BarrierProducer("coverage");
        var resource = new DisposableReadyResource("postgres");
        await using var host = EvidenceHostBootstrap.Create(CreatePlan(), registration =>
        {
            configureCount++;
            registration.AddResource(resource);
            registration.AddProducer(producer);
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, host.Plan);
        var first = run.RunAsync(host);
        await producer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = run.RunAsync(host);
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            producer.Release.TrySetResult();
        }

        var manifest = await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);

        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(1, configureCount);
        Assert.Equal(1, resource.WaitCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    [Fact]
    public async Task RunSharedCore_ShouldContinueReverseCleanupAfterFailureAndDisposeSharedInstanceOnlyOnce()
    {
        var cleanup = new List<string>();
        var plan = SealPlan(CreatePlan().Profile with
        {
            Resources =
            [
                new EvidenceResourceDeclaration("shared", "aspire_health", 30, []),
                new EvidenceResourceDeclaration("database", "aspire_health", 30, ["shared"]),
            ],
            Producers =
            [
                new EvidenceProducerDeclaration("shared", "coverage", "1.0.0", ["database"], ["coverage/assertion@1"], [], 30),
                new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", ["database"], ["coverage/assertion@1"], [], 30),
            ],
        });
        var shared = new SharedDisposableCapability("shared", cleanup);
        var resource = new RecordingSyncResource("database", cleanup);
        var producer = new RecordingFailingDisposableProducer("coverage", cleanup);
        var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            registration.AddResource(shared);
            registration.AddResource(resource);
            registration.AddProducer(shared);
            registration.AddProducer(producer);
        });
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Trusted, plan);
        await using (host)
        {
            var manifest = await run.RunAsync(host);
            Assert.All(manifest.ProducerResults, result => Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome));
            Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
            Assert.False(manifest.Metrics.CleanupCompleted);
            Assert.Equal(nameof(EvidenceWorkerTerminalCode.CleanupFailed), manifest.Metrics.TerminalFailureCode);
            Assert.Equal(["coverage", "shared", "database"], cleanup);
            await host.DisposeAsync();
        }

        Assert.Equal(["coverage", "shared", "database"], cleanup);
        Assert.Equal(1, shared.WaitCount);
        Assert.Equal(1, shared.RunCount);
        Assert.Equal(1, shared.DisposeCount);
        Assert.Equal(1, resource.WaitCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, producer.RunCount);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(1, run.Supervisor.ExitAcknowledgements);
        Assert.Equal(1, run.Supervisor.CompletionAcknowledgements);
    }

    private static EvidencePlan CreatePlan(
        int resourceDeadlineSeconds = 30,
        EvidenceProfileScope scope = EvidenceProfileScope.Targeted,
        int producerTimeoutSeconds = 30) => EvidenceHostAdmissionTestRun.CreatePlan(
            resourceDeadlineSeconds,
            scope,
            producerTimeoutSeconds);

    private static EvidencePlan SealPlan(EvidenceProfile profile) => EvidenceHostAdmissionTestRun.SealPlan(profile);

    private static async Task<EvidenceManifest> RunAcceptedAsync(
        EvidenceHostBootstrap host,
        EvidenceExecutionMode mode = EvidenceExecutionMode.Trusted,
        CancellationToken cancellationToken = default)
    {
        using var run = EvidenceHostAdmissionTestRun.Create(mode, host.Plan);
        return await run.RunAsync(host, cancellationToken).ConfigureAwait(false);
    }

    private class ReadyResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public int WaitCount { get; private set; }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ArtifactWritingProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public int RunCount { get; private set; }

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            var artifact = await context.Artifacts!.WriteAsync("report", "coverage/report.txt", "written"u8.ToArray(), cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/assertion@1"], Artifacts: [artifact]);
        }
    }

    private sealed class ResultProducer(string id, EvidenceProducerResult? result) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id { get; } = id;

        public int RunCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            return ValueTask.FromResult(result!);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BarrierProducer(string id) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id { get; } = id;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RunCount { get; private set; }

        public int DisposeCount { get; private set; }

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SharedDisposableCapability(string id, List<string> cleanup) : IEvidenceResourceReadiness, IEvidenceProducer, IAsyncDisposable
    {
        public string Id { get; } = id;

        public int WaitCount { get; private set; }

        public int RunCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            return Task.CompletedTask;
        }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanup.Add(Id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSyncResource(string id, List<string> cleanup) : ReadyResource(id), IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            cleanup.Add(Id);
        }
    }

    private sealed class RecordingFailingDisposableProducer(string id, List<string> cleanup) : PassingProducer(id, "coverage/assertion@1"), IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanup.Add(Id);
            return ValueTask.FromException(new InvalidOperationException("Producer cleanup failed."));
        }
    }

    private sealed class BlockingResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private sealed class FailingResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException("unavailable"));
    }

    private sealed class CallerCancellableResource(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;

        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private class PassingProducer(string id, string assertion) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public int RunCount { get; private set; }

        public EvidenceProducerContext? LastContext { get; private set; }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            LastContext = context;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [assertion]));
        }
    }

    private sealed class StaticEnvelopeVerifier(bool accepted) : IEvidenceExecutionEnvelopeVerifier
    {
        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceEnvelopeResult(accepted, Attested: false, accepted ? null : "Envelope rejected."));
    }

    private sealed class FailingProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromException<EvidenceProducerResult>(new InvalidOperationException("producer failed"));
    }

    private sealed class CriticalFailingProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromException<EvidenceProducerResult>(new OutOfMemoryException("critical producer failure"));
    }

    private sealed class WrongIdProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceProducerResult("other", EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]));
    }

    private sealed class SpoofedArtifactProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceProducerResult(
                Id,
                EvidenceProducerOutcome.Passed,
                ["coverage/assertion@1"],
                null,
                [new EvidenceArtifactResult("report", "coverage/report.txt", "text/plain", 1, new string('a', 64))]));
    }

    private sealed class TamperingArtifactProducer(string id, string artifactDirectory) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            await context.Artifacts!.WriteAsync("report", "coverage/report.txt", "written"u8.ToArray(), cancellationToken);
            await File.WriteAllTextAsync(Path.Join(artifactDirectory, Id, "coverage", "report.txt"), "changed", cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]);
        }
    }

    private sealed class IgnoringCancellationProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan).ContinueWith(
                static _ => new EvidenceProducerResult("coverage", EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
    }

    private sealed class DisposableReadyResource(string id) : ReadyResource(id), IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyncDisposableReadyResource(string id) : ReadyResource(id), IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingDisposableReadyResource(string id) : ReadyResource(id), IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("cleanup"));
    }

    private sealed class DisposablePassingProducer(string id, string assertion) : PassingProducer(id, assertion), IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallerCancellableProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/assertion@1"]);
        }
    }
}
