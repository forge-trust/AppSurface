using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ForgeTrust.AppSurface.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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
        using var client = host.Client;

        using var anonymousResponse = await client.PostAsync("/lab/canary/trigger", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

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
        using var client = host.Client;

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
        using var client = host.Client;
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
        using var client = host.Client;
        var invalidValues = new[]
        {
            new[] { " " },
            new[] { new string('m', 257) },
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
        using var client = host.Client;

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

    private static async Task<LabTestHost> CreateHostAsync(CanaryLabScenario scenario)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{CanaryLabSettings.SectionName}:OperatorToken"] = Token,
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

    private static HttpRequestMessage TriggerRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/lab/canary/trigger");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Add(AppSurfaceCanaryHeaderNames.Marker, Marker);
        return request;
    }

    private static HttpRequestMessage CanaryRequest(DateTimeOffset freshSince)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/_appsurface/canaries/{NamedCanaryLabApp.CanaryName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
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

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
