using Aspire.Hosting;
using ForgeTrust.AppSurface.Evidence.Aspire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceAspireApplicationHttpReadinessTests
{
    [Fact]
    public async Task CreateHttpReadiness_ValidatesPublicArgumentsBeforeResolvingTheResource()
    {
        await using var application = await StartEmptyApplicationAsync();

        Assert.Throws<ArgumentNullException>(() => application.CreateHttpReadiness(null!, "api", "http", "/health", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => application.CreateHttpReadiness(" ", "api", "http", "/health", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => application.CreateHttpReadiness("evidence", " ", "http", "/health", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => application.CreateHttpReadiness("evidence", "api", " ", "/health", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => application.CreateHttpReadiness("evidence", "api", "http", " ", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => application.CreateHttpReadiness("evidence", "api", "http", "/health", TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => application.CreateHttpReadiness("evidence", "api", "http", "/health", TimeSpan.FromSeconds(1),
            new EvidenceAspireHttpReadinessOptions { Http = null! }));
    }

    [Fact]
    public async Task CreateHttpReadiness_RejectsDisposedApplicationBeforeValidatingArguments()
    {
        var application = await StartEmptyApplicationAsync();
        await application.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => application.CreateHttpReadiness(null!, "api", "http", "/health", TimeSpan.FromSeconds(1)));
    }

    private static async Task<EvidenceAspireApplication> StartEmptyApplicationAsync()
    {
        var originalDcpPath = Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH");
        var originalDashboardPath = Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_PATH");
        Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", "dummy");
        Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", "dummy");
        try
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = [],
                AssemblyName = typeof(EvidenceAspireApplicationHttpReadinessTests).Assembly.GetName().Name,
                ProjectDirectory = Directory.GetCurrentDirectory(),
                DisableDashboard = true,
            });
            builder.Services.RemoveAll<IHostedService>();
            return await EvidenceAspireApplication.StartAsync(builder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", originalDcpPath);
            Environment.SetEnvironmentVariable("ASPIRE_DASHBOARD_PATH", originalDashboardPath);
        }
    }
}
