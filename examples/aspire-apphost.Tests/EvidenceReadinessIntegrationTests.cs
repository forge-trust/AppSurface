using Aspire.Hosting;
using AspireAppHostExample;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace AspireAppHostExample.Tests;

public sealed class EvidenceReadinessIntegrationTests
{
    [Fact]
    public async Task EvidenceHost_NativeHttpReadinessRunsProducerAndDisposesItsSingleLease()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(ExampleModule).Assembly.GetName().Name,
            ProjectDirectory = Projects.AspireAppHostExample.ProjectPath,
            DisableDashboard = true,
        });
        builder.AddProject<Projects.WebAppExample>("web").WithHttpEndpoint();
        await using var application = await EvidenceAspireApplication.StartAsync(builder, timeout.Token);
        var readiness = application.CreateHttpReadiness("web", "web", "http", "/", TimeSpan.FromMinutes(1));
        var producer = new HttpConditionProducer();
        var plan = new EvidencePlan(
            "1.0", "readiness-example", "policy-digest", "diff-digest",
            new EvidenceProfile(
                "http-condition", EvidenceProfileScope.Targeted,
                [new EvidenceResourceDeclaration("web", "aspire_http", 90, [])],
                [new EvidenceProducerDeclaration("http-condition", "assertion", "1.0.0", ["web"], ["http-condition@1"], [], 30)],
                [new EvidenceObligation("http-condition", "example", "Verify the declared HTTP condition.", ["http-condition"], "http-condition@1")]),
            [new NormalizedDiffPath("examples/web-app/Program.cs")], ["http-condition"], "plan-digest");
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            registration.AddResource(readiness);
            registration.AddProducer(producer);
        });

        var manifest = await host.RunAsync(cancellationToken: timeout.Token);

        Assert.Equal(EvidenceResourceOutcome.Ready, Assert.Single(manifest.ResourceResults).Outcome);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(manifest.ProducerResults).Outcome);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.True(manifest.Metrics.CleanupCompleted);
        Assert.Equal(1, producer.Runs);
        Assert.Throws<ObjectDisposedException>(() => application.CreateHealthReadiness("web", "web"));
    }

    private sealed class HttpConditionProducer : IEvidenceProducer
    {
        public string Id => "http-condition";
        public int Runs { get; private set; }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["http-condition@1"]));
        }
    }
}
