using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Aspire.Tests;

/// <summary>Metadata and abstract lifecycle controls; these issue no protected application or producer capability.</summary>
public sealed class EvidenceRestrictedHostRegistrationTests
{
    [Fact]
    public void CompleteSealedRegistrationPassesMetadataAuditWithoutStartingWork()
    {
        var definition = Definition();
        var registration = Register(definition);
        registration.ValidateRestrictedDeclarations(definition);
        var captured = registration.CaptureRestrictedProducers(definition.Producers.Select(item => item.Declaration).ToArray());

        Assert.Empty(registration.Resources);
        Assert.IsType<EvidenceRestrictedCoverageRegistration>(Assert.Single(captured).Value);
        Assert.Empty(registration.AdditionalOwned);
    }

    [Fact]
    public void CapturedProducerMapCannotBeChangedOrSubstitutedThroughPublicRegistration()
    {
        var definition = Definition();
        var registration = Register(definition);
        var captured = registration.CaptureRestrictedProducers([definition.Producers[0].Declaration]);
        var original = captured["coverage"];

        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, IEvidenceProducer>)registration.Producers)["coverage"] = new SubstituteProducer("coverage"));
        var extra = definition.Producers[0].Declaration with { Id = "extra" };
        registration.AddProducer(extra, EvidenceRestrictedCoverageProducerFactory.Create(extra));
        Assert.Same(original, captured["coverage"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, IEvidenceProducer>)captured).Clear());
        Reject(() => registration.ValidateRestrictedDeclarations(definition));
    }

    [Theory]
    [InlineData("missing-resource")]
    [InlineData("extra-resource")]
    [InlineData("public-readiness")]
    [InlineData("resource-name")]
    [InlineData("resource-deadline")]
    [InlineData("resource-dependencies")]
    [InlineData("missing-producer")]
    [InlineData("extra-producer")]
    [InlineData("id-only-producer")]
    [InlineData("public-substitute")]
    [InlineData("producer-version")]
    [InlineData("producer-resources")]
    [InlineData("producer-assertions")]
    [InlineData("producer-timeout")]
    [InlineData("producer-artifacts")]
    [InlineData("producer-gate")]
    [InlineData("sealed-declaration-drift")]
    public void CompleteAuditRejectsRegistrationOrImplementationDriftBeforeStartup(string change)
    {
        var definition = Definition();
        var resource = definition.Resources[0].Declaration;
        var producer = definition.Producers[0].Declaration;
        var registration = new EvidenceHostRegistration();
        if (change != "missing-resource")
        {
            if (change == "public-readiness") registration.AddResource(resource, new SubstituteReadiness(resource.Id));
            else registration.AddAspireHealthResource(change switch
            {
                "resource-deadline" => resource with { DeadlineSeconds = 31 },
                "resource-dependencies" => resource with { Requires = ["canary-resource"] },
                _ => resource,
            }, change == "resource-name" ? "canary-resource" : "native-http");
        }
        if (change == "extra-resource") registration.AddAspireHealthResource(resource with { Id = "extra" }, "native-http");

        if (change != "missing-producer")
        {
            var declared = change switch
            {
                "producer-version" => producer with { Version = "canary-version" },
                "producer-resources" => producer with { RequiredResources = ["canary-resource"] },
                "producer-assertions" => producer with { AssertionIds = ["canary-assertion"] },
                "producer-timeout" => producer with { TimeoutSeconds = 61 },
                "producer-artifacts" => producer with { ArtifactSlots = [] },
                "producer-gate" => producer with { CoverageGate = new(91, 81) },
                _ => producer,
            };
            IEvidenceProducer adapter = change == "public-substitute"
                ? new SubstituteProducer(producer.Id)
                : EvidenceRestrictedCoverageProducerFactory.Create(change == "sealed-declaration-drift"
                    ? producer with { TimeoutSeconds = 62 } : producer);
            if (change == "id-only-producer") registration.AddProducer(adapter);
            else registration.AddProducer(declared, adapter);
        }
        if (change == "extra-producer")
        {
            var extra = producer with { Id = "extra" };
            registration.AddProducer(extra, EvidenceRestrictedCoverageProducerFactory.Create(extra));
        }

        Reject(() => registration.ValidateRestrictedDeclarations(definition));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("id-only")]
    [InlineData("substitute")]
    [InlineData("declared-drift")]
    [InlineData("sealed-drift")]
    public void DependencyFreeCaptureAlsoRequiresCompleteSealedProducerMetadata(string change)
    {
        var producer = Definition().Producers[0].Declaration;
        var registration = new EvidenceHostRegistration();
        if (change != "missing")
        {
            IEvidenceProducer adapter = change == "substitute" ? new SubstituteProducer(producer.Id)
                : EvidenceRestrictedCoverageProducerFactory.Create(change == "sealed-drift"
                    ? producer with { TimeoutSeconds = 61 } : producer);
            if (change == "id-only") registration.AddProducer(adapter);
            else registration.AddProducer(change == "declared-drift" ? producer with { TimeoutSeconds = 62 } : producer, adapter);
        }
        Reject(() => registration.CaptureRestrictedProducers([producer]));
    }

    [Fact]
    public async Task RegisteredCleanupUsesJoinedPhaseWhileItsOwnTaskIsStillRunning()
    {
        var supervisor = new EvidenceHostAdmissionTestRun.FakeSupervisor("metadata/run", TimeSpan.FromMinutes(2));
        var execution = Execution(supervisor);
        var cleanup = new EvidenceRestrictedApplicationCleanup(execution);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(execution.RegisterDisposer(async _ =>
        {
            entered.SetResult();
            await release.Task;
            Assert.False(execution.OwnWorkStopped);
            Assert.True(cleanup.Close());
            Assert.False(cleanup.Close());
        }));
        var stopping = execution.StopAndDisposeAsync().AsTask();
        await entered.Task;
        Assert.False(execution.OwnWorkStopped);
        release.SetResult();

        Assert.True(await stopping);
        Assert.True(execution.CleanupCompleted);
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal(1, supervisor.ExitAcknowledgements);
    }

    [Fact]
    public async Task PrematureCleanupFailureRemainsLatchedAfterOwnedExitAndCleanup()
    {
        var execution = Execution(new EvidenceHostAdmissionTestRun.FakeSupervisor("metadata/run", TimeSpan.FromMinutes(2)));
        var cleanup = new EvidenceRestrictedApplicationCleanup(execution);
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() => cleanup.Close()).Code);
        Assert.True(execution.RegisterDisposer(_ =>
        {
            Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() => cleanup.Close()).Code);
            return ValueTask.CompletedTask;
        }));
        Assert.True(await execution.StopAndDisposeAsync());
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(() => cleanup.Close()).Code);
    }

    [Fact]
    public async Task JoinedPhaseIsUnavailableAfterStopUntilRegisteredCleanupBegins()
    {
        var execution = Execution(new EvidenceHostAdmissionTestRun.FakeSupervisor("metadata/run", TimeSpan.FromMinutes(2)));
        await execution.RequestTerminalStopAsync(EvidenceWorkerTerminalCode.StageFailed);
        Assert.True(execution.OwnWorkStopped);
        Assert.Equal("ASEVD410", Assert.Throws<EvidenceAdmissionException>(execution.RequireJoinedCleanupPhase).Code);
        Assert.False(await execution.StopAndDisposeAsync());
        execution.RequireJoinedCleanupPhase();
    }

    private static EvidenceWorkerExecution Execution(IEvidenceExecutionSupervisor supervisor) => new(supervisor,
        TimeProvider.System, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

    private static void Reject(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD404", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("canary", error.Message, StringComparison.Ordinal);
    }

    private static EvidenceHostRegistration Register(EvidenceClosedApplicationDefinition definition)
    {
        var registration = new EvidenceHostRegistration();
        registration.AddAspireHealthResource(definition.Resources[0].Declaration, definition.Resources[0].ResourceName);
        var producer = definition.Producers[0].Declaration;
        registration.AddProducer(producer, EvidenceRestrictedCoverageProducerFactory.Create(producer));
        return registration;
    }

    private static EvidenceClosedApplicationDefinition Definition()
    {
        var resource = new EvidenceResourceDeclaration("http", "aspire_health", 30, []);
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", ["http"], ["coverage/assertion@1"],
            [new("report", "merged", "application/xml", true, 1024)], 60, new(90, 80));
        var profile = new EvidenceProfile("native-http", EvidenceProfileScope.Targeted, [resource], [producer], []);
        var policy = new EvidencePolicy("metadata", "1", profile.Id, [profile], []);
        return new("native-http", "1.0.0", "metadata-only", "13.4.4", policy, profile.Id,
            [new(resource, "native-http-uds", "1.0.0", "native-http")], [new(producer, "coverage", "1.0.0")], [],
            new([], 1024, 1024, 4, 1024, 30, 5));
    }

    private sealed class SubstituteProducer(string id) : IEvidenceProducer
    {
        public string Id { get; } = id;
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata audit must not invoke a public substitute.");
    }

    private sealed class SubstituteReadiness(string id) : IEvidenceResourceReadiness
    {
        public string Id { get; } = id;
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Metadata audit must not invoke public readiness.");
    }
}
