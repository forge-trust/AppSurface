using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Private compiled consumer using the public supervised Host entry; absent from production.</summary>
/// <remarks>
/// Registrations contain only complete compiled metadata and the shared sealed coverage producer.
/// No application factory, synthetic context, supplied readiness adapter or test-core entry is used.
/// The real Host still authenticates root supervision and independently resolves the protected plan.
/// </remarks>
internal static class EvidencePrivateQualificationHostEntry
{
    internal static async Task<EvidenceManifest> RunAsync(string controlChannel, CancellationToken cancellationToken)
    {
        var plan = EvidencePrivateQualificationBinding.CopyExpectedPlan();
        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            foreach (var resource in plan.Profile.Resources)
                registration.AddAspireHealthResource(resource, "native-http");
            foreach (var declaration in plan.Profile.Producers)
                registration.AddProducer(EvidenceRestrictedCoverageProducerFactory.Create(declaration));
        });
        return await host.RunAsync(new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, controlChannel),
            cancellationToken).ConfigureAwait(false);
    }
}
