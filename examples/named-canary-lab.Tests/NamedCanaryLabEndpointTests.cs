using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NamedCanaryLab.Tests;

public sealed class NamedCanaryLabEndpointTests
{
    private const string Token = "operator-token-sentinel";
    private const string Marker = "marker-sentinel";

    [Fact]
    public async Task TriggerAndCanaryRoutes_AreProtectedAndPassWithoutLeakingRawValues()
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var client = host.Client;

        using var anonymousResponse = await client.PostAsync("/lab/canary/trigger", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        using var anonymousCanaryResponse = await client.GetAsync($"/_appsurface/canaries/{NamedCanaryLabApp.CanaryName}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCanaryResponse.StatusCode);

        using var triggerResponse = await client.SendAsync(TriggerRequest());
        var triggerBody = await triggerResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, triggerResponse.StatusCode);
        Assert.Contains("accepted", triggerBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, triggerBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, triggerBody, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryLabMarkerFingerprint.Create(Marker), triggerBody, StringComparison.Ordinal);

        using var canaryResponse = await client.SendAsync(CanaryRequest(DateTimeOffset.UtcNow.AddMinutes(-1)));
        var canaryBody = await canaryResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, canaryResponse.StatusCode);
        Assert.Equal("pass", ReadStatus(canaryBody));
        Assert.DoesNotContain(Token, canaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, canaryBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pending", "pending", "proof-not-observed")]
    [InlineData("Stale", "stale", "proof-stale")]
    public async Task TriggeredConfiguredScenario_ProducesOnlyBoundedTerminalEvidence(
        string scenarioName,
        string expectedStatus,
        string expectedReason)
    {
        await using var host = await CreateHostAsync(Enum.Parse<CanaryLabScenario>(scenarioName));
        var client = host.Client;

        using var triggerResponse = await client.SendAsync(TriggerRequest());
        Assert.Equal(HttpStatusCode.Accepted, triggerResponse.StatusCode);

        using var canaryResponse = await client.SendAsync(CanaryRequest(DateTimeOffset.UtcNow.AddMinutes(-1)));
        var body = await canaryResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, canaryResponse.StatusCode);
        Assert.Equal(expectedStatus, ReadStatus(body));
        Assert.Equal(expectedReason, ReadReasonCode(body));
        Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_RejectsMissingMarkerWithoutEchoingCredentials()
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var client = host.Client;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/lab/canary/trigger")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Token) },
        };

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid-request", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_RejectsInvalidMarkerShapesWithoutEchoingCredentials()
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var client = host.Client;
        var invalidValues = new[]
        {
            new[] { " " },
            new[] { new string('m', 257) },
            new[] { new string('é', 128) + "x" },
            new[] { Marker, "second-marker" },
        };

        foreach (var values in invalidValues)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/lab/canary/trigger");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            request.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, values);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid-request", body, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CanaryRoute_RejectsWrongOrMalformedBearerCredentials()
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var client = host.Client;

        foreach (var authorization in new[]
                 {
                     new AuthenticationHeaderValue("Bearer", "wrong-token"),
                     new AuthenticationHeaderValue("Basic", Token),
                 })
        {
            using var request = CanaryRequest(DateTimeOffset.UtcNow.AddMinutes(-1));
            request.Headers.Authorization = authorization;

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Trigger_AcceptsTheExactUtf8MarkerLimit()
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var client = host.Client;
        var marker = new string('é', 128);
        using var request = TriggerRequest(marker);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public void MarkerValidation_RejectsControlCharactersAndMalformedUnicode()
    {
        Assert.False(NamedCanaryLabApp.IsValidMarker("\u0001"));
        Assert.False(NamedCanaryLabApp.IsValidMarker(new string('\ud800', 1)));
    }

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("bearer operator-token-sentinel")]
    public async Task CanaryRoute_RejectsEmptyOrCaseMismatchedBearerCredentials(string authorization)
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var client = host.Client;
        using var request = CanaryRequest(DateTimeOffset.UtcNow.AddMinutes(-1));
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CanaryRoute_RejectsOversizedBearerCredentials()
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var client = host.Client;
        using var request = CanaryRequest(DateTimeOffset.UtcNow.AddMinutes(-1));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('t', 16 * 1024 + 1));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CanaryRoute_AcceptsBearerCredentialsAtTheExactTokenLimit()
    {
        var token = new string('t', 16 * 1024);
        await using var host = await CreateHostAsync(CanaryLabScenario.Pending, token);
        using var request = CanaryRequest(DateTimeOffset.UtcNow.AddMinutes(-1), token);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Trigger_RejectsNewMarkersWhenTheBoundedStoreIsFullWithoutLeakingValues()
    {
        await using var host = await CreateHostAsync(CanaryLabScenario.Pass);
        var proofStore = host.Services.GetRequiredService<CanaryLabProofStore>();
        var identity = new CanaryProofIdentity("candidate-sentinel", "development");
        for (var index = 0; index < CanaryLabProofStore.MaximumRecordCount; index++)
        {
            var marker = $"marker-{index}";
            Assert.NotNull(proofStore.Record(new CanaryProofRecord(
                identity,
                CanaryLabMarkerFingerprint.Create(marker),
                DateTimeOffset.UtcNow,
                AppSurfaceCanaryStatus.Pass)));
        }

        using var response = await host.Client.SendAsync(TriggerRequest());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("proof-store-full", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, body, StringComparison.Ordinal);
    }

    private static async Task<LabTestHost> CreateHostAsync(CanaryLabScenario scenario, string operatorToken = Token)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{CanaryLabSettings.SectionName}:OperatorToken"] = operatorToken,
            [$"{CanaryLabSettings.SectionName}:Candidate"] = "candidate-sentinel",
            [$"{CanaryLabSettings.SectionName}:Environment"] = "development",
            [$"{CanaryLabSettings.SectionName}:Scenario"] = scenario.ToString(),
        });
        NamedCanaryLabApp.Configure(builder);

        var app = builder.Build();
        NamedCanaryLabApp.Map(app);
        await app.StartAsync();
        return new LabTestHost(app, app.GetTestClient());
    }

    private static HttpRequestMessage TriggerRequest(string marker = Marker)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/lab/canary/trigger");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, marker);
        return request;
    }

    private static HttpRequestMessage CanaryRequest(DateTimeOffset freshSince, string operatorToken = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/_appsurface/canaries/{NamedCanaryLabApp.CanaryName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        request.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, Marker);
        request.Headers.Add(AppSurfaceCanaryHeaderNames.FreshSince, freshSince.ToUniversalTime().ToString("O"));
        return request;
    }

    private static string? ReadStatus(string response) => ReadString(response, "status");

    private static string? ReadReasonCode(string response) => ReadString(response, "reasonCode");

    private static string? ReadString(string response, string propertyName)
    {
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty(propertyName).GetString();
    }

    private sealed class LabTestHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public IServiceProvider Services => app.Services;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
