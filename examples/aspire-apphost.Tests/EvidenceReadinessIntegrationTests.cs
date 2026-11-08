using Aspire.Hosting;
using Aspire.Hosting.Testing;
using AspireAppHostExample;
using ForgeTrust.AppSurface.Aspire.Testing;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Microsoft.Extensions.DependencyInjection;

namespace AspireAppHostExample.Tests;

[Collection(AspireAppHostIntegrationCollection.Name)]
public sealed class EvidenceReadinessIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryNativeHttpReadiness_VerifiesStatusAndDisposesItsApplicationAndClient(bool customClient)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var createdClients = 0;
        var disposedClients = 0;
        DistributedApplication application;
        await using (var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<
            Projects.AspireAppHostExample, ExampleModule, QaProfile>(timeout.Token))
        {
            await using (application = await builder.BuildAsync(timeout.Token))
            {
                await application.StartAsync(timeout.Token);
                await application.ResourceNotifications.WaitForResourceHealthyAsync("web", timeout.Token);
                var endpoint = application.GetEndpoint("web", "http");
                using var client = customClient
                    ? CreateObservedClient(endpoint)
                    : application.CreateHttpClient("web", "http");
                await HttpReadinessProbe.WaitAsync(client, endpoint, "/", TimeSpan.FromMinutes(1),
                    cancellationToken: timeout.Token);
            }

            Assert.Throws<ObjectDisposedException>(() => application.Services.GetRequiredService<IServiceScopeFactory>());
        }
        Assert.Equal(customClient ? 1 : 0, createdClients);
        Assert.Equal(createdClients, disposedClients);

        HttpClient CreateObservedClient(Uri endpoint)
        {
            createdClients++;
            return new HttpClient(new ObservedClientHandler(() => disposedClients++)) { BaseAddress = endpoint };
        }
    }

    private sealed class ObservedClientHandler(Action disposed) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed();
            base.Dispose(disposing);
        }
    }

}
