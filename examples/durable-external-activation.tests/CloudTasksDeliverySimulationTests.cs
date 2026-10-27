using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests;

/// <summary>Bridges a locally recorded empty task to the real reference-host route using its existing test auth.</summary>
public sealed class CloudTasksDeliverySimulationTests
{
    [Fact]
    public async Task Recorded_named_empty_wake_uses_existing_bearer_auth_and_invokes_activation_once()
    {
        // protects=composition of a recorded named empty wake with the actual host route, authorization, body guard, and activation service; fails_when=bridge sends payload, bypasses host auth, changes the route, or loses task identity; why_new=existing host contract tests do not connect a provider-shaped recorded task to that route; seam=recorded task fixture plus the host's explicit local bearer test credential; no_claim=Google-issued OIDC acceptance, Cloud Tasks delivery, or persisted Work state
        var task = new RecordedTask(
            "projects/123456789/locations/us-central1/queues/wake/tasks/wake_01",
            "POST",
            "/private/durable/activate",
            [],
            "worker@example.iam.gserviceaccount.com",
            "https://worker.example");
        await using var host = await ActivationTestHost.StartAsync();

        using var missingAuth = await DeliverAsync(host, task, bearerToken: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missingAuth.StatusCode);
        Assert.Equal(0, host.Dependencies.Activation.CallCount);

        using var delivered = await DeliverAsync(host, task, ActivationTestHost.ValidToken);
        using var json = JsonDocument.Parse(await delivered.Content.ReadAsStreamAsync());

        Assert.Equal("POST", task.Method);
        Assert.Equal("/private/durable/activate", task.EndpointPath);
        Assert.Empty(task.Body);
        Assert.Equal("projects/123456789/locations/us-central1/queues/wake/tasks/wake_01", task.Name);
        Assert.Equal("worker@example.iam.gserviceaccount.com", task.OidcServiceAccountEmail);
        Assert.Equal("https://worker.example", task.OidcAudience);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        Assert.Equal("Completed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, host.Dependencies.Activation.CallCount);
        Assert.Equal(DurableRuntimeSurface.Work, Assert.Single(host.Dependencies.Activation.Calls).Request.PumpRequest.Surfaces);
        Assert.Equal(2, host.Dependencies.BodyReads.Observations.Count);
        Assert.All(host.Dependencies.BodyReads.Observations, observation => Assert.Equal(0, observation.BytesRead));
        Assert.True(host.Dependencies.BodyReads.ReadStarted.IsCompleted);
    }

    private static Task<HttpResponseMessage> DeliverAsync(ActivationTestHost host, RecordedTask task, string? bearerToken)
    {
        // Simulated delivery deliberately translates only the recorded task intent and uses the host's local test credential.
        var request = new HttpRequestMessage(new HttpMethod(task.Method), task.EndpointPath)
        {
            Content = new EmptyUnknownLengthContent(task.Body),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return host.Client.SendAsync(request);
    }

    private sealed record RecordedTask(
        string Name,
        string Method,
        string EndpointPath,
        byte[] Body,
        string OidcServiceAccountEmail,
        string OidcAudience);

    private sealed class EmptyUnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
