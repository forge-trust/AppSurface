using Aspire.Hosting;
using AspireAppHostExample;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace AspireAppHostExample.Tests;

public sealed class EvidenceReadinessIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreconfiguredEvidenceApplication_RejectsBeforeStartupAndEndpointAllocation(bool canceled)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (canceled) timeout.Cancel();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(ExampleModule).Assembly.GetName().Name,
            ProjectDirectory = Projects.AspireAppHostExample.ProjectPath,
            DisableDashboard = true,
        });
        var web = builder.AddProject<Projects.WebAppExample>("web").WithHttpEndpoint();
        var starts = 0;
        builder.OnBeforeStart((_, _) =>
        {
            starts++;
            throw new InvalidOperationException("The rejected Evidence application must not start.");
        });

        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceAspireApplication.StartAsync(builder, timeout.Token));

        Assert.Equal("ASEVD400", exception.Code);
        Assert.Equal(0, starts);
        Assert.False(web.GetEndpoint("http").IsAllocated);
        Assert.Contains(web.Resource, builder.Resources);
    }
}
