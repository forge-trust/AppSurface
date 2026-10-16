using Aspire.Hosting;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
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

    // Value: protects=independent health/HTTP application registrations; fails_when=one adapter stops another's owner;
    // why_new=the existing public tests cover guards only; seam=none; no_claim=active transport draining is tested separately.
    [Fact]
    public async Task HealthAndHttpAdapters_KeepTheApplicationUntilTheFinalRegistrationDisposes()
    {
        await using var application = await StartEmptyApplicationAsync(builder =>
            builder.AddExecutable("api", "unused", ".").WithHttpEndpoint(targetPort: 8080, name: "http"));
        var health = application.CreateHealthReadiness("health", "api");
        var http = application.CreateHttpReadiness("http", "api", "http", "/health", TimeSpan.FromSeconds(1));

        await Assert.IsAssignableFrom<IAsyncDisposable>(health).DisposeAsync();
        IEvidenceResourceReadiness? final = null;
        Assert.Null(Record.Exception(() => final = application.CreateHealthReadiness("final", "api")));
        await Assert.IsAssignableFrom<IAsyncDisposable>(http).DisposeAsync();
        await Assert.IsAssignableFrom<IAsyncDisposable>(final).DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => application.CreateHealthReadiness("late", "api"));
        await application.DisposeAsync();
    }

    private static async Task<EvidenceAspireApplication> StartEmptyApplicationAsync(Action<IDistributedApplicationBuilder>? configure = null)
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
            configure?.Invoke(builder);
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
