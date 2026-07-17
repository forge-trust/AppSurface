using Aspire.Hosting.Testing;
using AspireAppHostExample;
using ForgeTrust.AppSurface.Aspire.Testing;

namespace AspireAppHostExample.Tests;

public sealed class TypedProfileIntegrationTests
{
    [Fact]
    public async Task QaProfile_StartsHealthyWebResourceAndServesHttp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<
            Projects.AspireAppHostExample,
            ExampleModule,
            QaProfile>(timeout.Token);
        await using var application = await builder.BuildAsync(timeout.Token);

        await application.StartAsync(timeout.Token);
        await application.ResourceNotifications.WaitForResourceHealthyAsync("web", timeout.Token);

        using var client = application.CreateHttpClient("web", "http");
        using var response = await client.GetAsync("/", timeout.Token);

        Assert.True(response.IsSuccessStatusCode, $"Expected HTTP success but received {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task QaProfile_RepeatedBuildAndDisposalDoesNotUseEntryPointDiagnostics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        for (var iteration = 0; iteration < 3; iteration++)
        {
            await using var builder = await AppSurfaceAspireTestingBuilder.CreateAsync<
                Projects.AspireAppHostExample,
                ExampleModule,
                QaProfile>(timeout.Token);
            Assert.Contains(builder.Resources, resource => resource.Name == "web");
            await using var application = await builder.BuildAsync(timeout.Token);

            await application.StartAsync(timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync("web", timeout.Token);

            using var client = application.CreateHttpClient("web", "http");
            using var response = await client.GetAsync("/", timeout.Token);
            Assert.True(
                response.IsSuccessStatusCode,
                $"Iteration {iteration + 1} expected HTTP success but received {(int)response.StatusCode}.");
        }
    }
}
