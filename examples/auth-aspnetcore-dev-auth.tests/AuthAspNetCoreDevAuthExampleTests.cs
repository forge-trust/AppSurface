using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthAspNetCoreDevAuthExample.Tests;

public sealed class AuthAspNetCoreDevAuthExampleTests
{
    private const string ReloadConfigEnvironmentVariable = "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE";

    [Fact]
    public async Task HostFlow_StartsAndExercisesAdminStatusAndClearContracts()
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });

            var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
            Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);

            using var rootResponse = await client.GetAsync("/");
            var rootBody = await rootResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
            Assert.Contains("AppSurface DevAuth proof is running.", rootBody, StringComparison.Ordinal);
            Assert.Contains("AppSurface development authentication state", rootBody, StringComparison.Ordinal);

            using var controlResponse = await client.GetAsync("/_appsurface/dev-auth/");
            var controlBody = await controlResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, controlResponse.StatusCode);
            Assert.Contains("AppSurface Dev Auth [FAKE LOCAL AUTH]", controlBody, StringComparison.Ordinal);
            Assert.Contains("Local Admin", controlBody, StringComparison.Ordinal);
            Assert.Contains("Local Viewer", controlBody, StringComparison.Ordinal);

            using var selectResponse = await client.PostAsync("/_appsurface/dev-auth/select/admin", content: null);
            Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

            using var proofResponse = await client.GetAsync("/api/auth-proof");
            Assert.Equal(HttpStatusCode.OK, proofResponse.StatusCode);
            using (var proof = JsonDocument.Parse(await proofResponse.Content.ReadAsStringAsync()))
            {
                Assert.Equal("allowed", proof.RootElement.GetProperty("result").GetString());
                Assert.Equal("admin-1", proof.RootElement.GetProperty("subject").GetString());
            }

            using var selectedStatusResponse = await client.GetAsync("/_appsurface/dev-auth/status");
            Assert.Equal(HttpStatusCode.OK, selectedStatusResponse.StatusCode);
            using (var status = JsonDocument.Parse(await selectedStatusResponse.Content.ReadAsStringAsync()))
            {
                var root = status.RootElement;
                Assert.True(root.GetProperty("enabled").GetBoolean());
                Assert.Equal("Development", root.GetProperty("environment").GetString());
                Assert.Equal("AppSurface.DevAuth", root.GetProperty("scheme").GetString());
                Assert.Equal("/_appsurface/dev-auth", root.GetProperty("pathPrefix").GetString());
                Assert.Equal("admin", root.GetProperty("personaId").GetString());
                Assert.Equal("Local Admin", root.GetProperty("displayName").GetString());
                Assert.Equal("admin-1", root.GetProperty("subject").GetString());
                Assert.False(root.GetProperty("isAnonymous").GetBoolean());
                Assert.Empty(root.GetProperty("warnings").EnumerateArray());
            }

            using var clearResponse = await client.PostAsync("/_appsurface/dev-auth/clear", content: null);
            Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);

            using var clearedStatusResponse = await client.GetAsync("/_appsurface/dev-auth/status");
            Assert.Equal(HttpStatusCode.OK, clearedStatusResponse.StatusCode);
            using var clearedStatus = JsonDocument.Parse(await clearedStatusResponse.Content.ReadAsStringAsync());
            var clearedRoot = clearedStatus.RootElement;
            Assert.Equal(JsonValueKind.Null, clearedRoot.GetProperty("personaId").ValueKind);
            Assert.Equal(JsonValueKind.Null, clearedRoot.GetProperty("displayName").ValueKind);
            Assert.Equal(JsonValueKind.Null, clearedRoot.GetProperty("subject").ValueKind);
            Assert.True(clearedRoot.GetProperty("isAnonymous").GetBoolean());

            using var clearedProofResponse = await client.GetAsync("/api/auth-proof");
            var clearedProofBody = await clearedProofResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, clearedProofResponse.StatusCode);
            Assert.Contains("\"appsurfaceAuthOutcome\":\"Challenge\"", clearedProofBody, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ViewerPersona_IsForbiddenFromOperatorProof()
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = factory.CreateClient();
            using var selectResponse = await client.PostAsync("/_appsurface/dev-auth/select/viewer", content: null);
            Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

            using var proofResponse = await client.GetAsync("/api/auth-proof");
            var body = await proofResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, proofResponse.StatusCode);
            Assert.Contains("\"appsurfaceAuthOutcome\":\"Forbid\"", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AnonymousUser_IsChallengedByOperatorProof()
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = factory.CreateClient();
            using var proofResponse = await client.GetAsync("/api/auth-proof");
            var body = await proofResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, proofResponse.StatusCode);
            Assert.Contains("\"appsurfaceAuthOutcome\":\"Challenge\"", body, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("preserve-existing-value")]
    public async Task FactoryScope_RestoresReloadConfigEnvironmentVariable(string? priorValue)
    {
        var originalValue = Environment.GetEnvironmentVariable(ReloadConfigEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(ReloadConfigEnvironmentVariable, priorValue);

            await WithFactoryAsync(async factory =>
            {
                Assert.Equal(
                    "false",
                    Environment.GetEnvironmentVariable(ReloadConfigEnvironmentVariable));

                using var client = factory.CreateClient();
                using var response = await client.GetAsync("/");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            });

            Assert.Equal(
                priorValue,
                Environment.GetEnvironmentVariable(ReloadConfigEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReloadConfigEnvironmentVariable, originalValue);
        }
    }

    private static async Task WithFactoryAsync(Func<WebApplicationFactory<Program>, Task> action)
    {
        var priorReloadConfig = Environment.GetEnvironmentVariable(ReloadConfigEnvironmentVariable);
        Environment.SetEnvironmentVariable(ReloadConfigEnvironmentVariable, "false");

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment(Environments.Development);
                    builder.ConfigureServices(services =>
                    {
                        services.AddDataProtection().UseEphemeralDataProtectionProvider();
                        services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();
                    });
                });
            await action(factory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReloadConfigEnvironmentVariable, priorReloadConfig);
        }
    }

    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                    await nextMiddleware(context);
                });
                next(app);
            };
        }
    }
}
