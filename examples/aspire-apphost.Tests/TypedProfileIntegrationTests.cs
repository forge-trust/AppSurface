using Aspire.Hosting.Testing;
using AspireAppHostExample;
using ForgeTrust.AppSurface.Aspire.Testing;
using ForgeTrust.AppSurface.Evidence.Readiness;

namespace AspireAppHostExample.Tests;

[Collection(AspireAppHostIntegrationCollection.Name)]
public sealed class TypedProfileIntegrationTests
{
    private const int MaximumHttpRequestAttempts = 3;

    [Fact]
    public async Task QaProfile_StartsHealthyWebResourceAndServesHttp()
    {
        await using var fixture = await AppSurfaceAspireTestFixture.StartAsync(
            createBuilder: cancellationToken => AppSurfaceAspireTestingBuilder.CreateAsync<
                Projects.AspireAppHostExample,
                ExampleModule,
                QaProfile>(cancellationToken),
            target: new AspireHttpReadinessTarget("web", "http", "/"),
            options: new AppSurfaceAspireStartupOptions
            {
                StartupTimeout = TimeSpan.FromMinutes(2),
                Http = new HttpReadinessOptions { MaxAttempts = MaximumHttpRequestAttempts },
            });

        using var client = fixture.Application.CreateHttpClient("web", "http");
        using var response = await client.GetAsync("/");

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
            using var response = await GetResponseWithTransientRetryAsync(client, timeout.Token);
            Assert.True(
                response.IsSuccessStatusCode,
                $"Iteration {iteration + 1} expected HTTP success but received {(int)response.StatusCode}.");
        }
    }

    private static async Task<HttpResponseMessage> GetResponseWithTransientRetryAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.GetAsync("/", cancellationToken);
            }
            catch (HttpRequestException exception)
                when (exception.InnerException is HttpIOException
                {
                    HttpRequestError: HttpRequestError.ResponseEnded,
                } && attempt < MaximumHttpRequestAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }
}
