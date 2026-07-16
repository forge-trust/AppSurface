using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using ForgeTrust.AppSurface.Web.Push;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push.Tests;

public sealed class AppSurfaceWebPushEndpointTests
{
    [Fact]
    public async Task RailAndClientAsset_AreAbsentUntilExplicitMapping()
    {
        await using var host = await CreateHostAsync(map: false, ambientAdmin: false, bearer: false);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/account/push/configuration")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/_appsurface/pwa/push-client.js")).StatusCode);
    }

    [Fact]
    public async Task Mapping_RejectsRouteGroupsBeforeRegisteringEndpoints()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateHostAsync(map: true, ambientAdmin: false, bearer: false, allowAnonymousGroup: true));
    }

    [Fact]
    public async Task CookieAuthorization_ForbidsThroughPolicyDeclaredScheme()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/push/configuration");
        request.Headers.Add("X-Ambient-Viewer", "true");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CookieConfiguration_ReturnsOnlyPublicKeyAndAntiforgeryContract()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: true, bearer: false);

        var response = await host.Client.GetAsync("/account/push/configuration");
        var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("primary", json.RootElement.GetProperty("vapidKeyId").GetString());
        Assert.Equal("antiforgery", json.RootElement.GetProperty("requestProtection").GetString());
        Assert.False(json.RootElement.TryGetProperty("privateKey", out _));
        Assert.NotEmpty(json.RootElement.GetProperty("antiforgery").GetProperty("requestToken").GetString()!);
    }

    [Fact]
    public async Task CookieAntiforgeryProviderFailures_ReturnSafeProblems()
    {
        await using var host = await CreateHostAsync(
            map: true,
            ambientAdmin: true,
            bearer: false,
            throwingAntiforgery: true);

        using var configuration = await host.Client.GetAsync("/account/push/configuration");
        using var content = new StringContent(CreateSubscriptionWire().Json, Encoding.UTF8, "application/json");
        using var put = await host.Client.PutAsync(
            "/account/push",
            content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, configuration.StatusCode);
        Assert.Equal("ASPUSH104", await ReadCodeAsync(configuration));
        Assert.DoesNotContain("secret antiforgery detail", await configuration.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, put.StatusCode);
        Assert.Equal("ASPUSH104", await ReadCodeAsync(put));
        Assert.DoesNotContain("secret antiforgery detail", await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, host.Custody.RegisterCalls);
    }

    [Fact]
    public async Task CookieAntiforgeryCancellation_RemainsCancellation()
    {
        await using var host = await CreateHostAsync(
            map: true,
            ambientAdmin: true,
            bearer: false,
            throwingAntiforgery: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/account/push/configuration";
            context.Request.Headers["X-Ambient-Admin"] = "true";
            context.Request.Headers["X-Cancel-Antiforgery"] = "true";
            context.RequestAborted = new CancellationToken(canceled: true);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Put;
            context.Request.Path = "/account/push";
            context.Request.Headers["X-Ambient-Admin"] = "true";
            context.Request.Headers["X-Cancel-Antiforgery"] = "true";
            context.RequestAborted = new CancellationToken(canceled: true);
        }));
    }

    [Fact]
    public async Task CookiePut_RequiresAntiforgeryThenRegistersValidatedSnapshot()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: true, bearer: false);
        var wire = CreateSubscriptionWire();
        using var missingToken = new StringContent(wire.Json, Encoding.UTF8, "application/json");

        var rejected = await host.Client.PutAsync("/account/push", missingToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("ASPUSH104", await ReadCodeAsync(rejected));

        var configuration = await host.Client.GetAsync("/account/push/configuration");
        var configJson = JsonDocument.Parse(await configuration.Content.ReadAsByteArrayAsync());
        var antiforgery = configJson.RootElement.GetProperty("antiforgery");
        using var acceptedRequest = new HttpRequestMessage(HttpMethod.Put, "/account/push")
        {
            Content = new StringContent(wire.Json, Encoding.UTF8, "application/json"),
        };
        acceptedRequest.Headers.TryAddWithoutValidation(
            antiforgery.GetProperty("headerName").GetString()!,
            antiforgery.GetProperty("requestToken").GetString()!);
        acceptedRequest.Headers.TryAddWithoutValidation(
            "Cookie",
            configuration.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);

        var accepted = await host.Client.SendAsync(acceptedRequest);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(1, host.Custody.RegisterCalls);
        Assert.Equal(wire.Endpoint, host.Custody.Registered!.Endpoint);
    }

    [Fact]
    public async Task BearerMapping_RejectsAmbientCookieIdentityAndAcceptsExplicitScheme()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: true, bearer: true);

        var ambientOnly = await host.Client.GetAsync("/account/push/configuration");
        Assert.Equal(HttpStatusCode.Unauthorized, ambientOnly.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/push/configuration");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");
        var explicitBearer = await host.Client.SendAsync(request);
        var json = JsonDocument.Parse(await explicitBearer.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, explicitBearer.StatusCode);
        Assert.Equal("bearer", json.RootElement.GetProperty("requestProtection").GetString());
        Assert.False(json.RootElement.TryGetProperty("antiforgery", out _));
    }

    [Fact]
    public async Task BearerMapping_RejectsSignInCapableAuthenticationScheme()
    {
        await using var host = await CreateHostAsync(
            map: true,
            ambientAdmin: false,
            bearer: true,
            authenticationScheme: "CookieProof");

        var response = await host.Client.GetAsync("/account/push/configuration");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ASPUSH108", await ReadCodeAsync(response));
        Assert.Equal(0, host.Custody.RegisterCalls);
        Assert.Equal(0, host.Custody.UnregisterCalls);
    }

    [Fact]
    public async Task BearerMapping_ReplacesAmbientIdentityForResourceAwareAuthorization()
    {
        await using var host = await CreateHostAsync(
            map: true,
            ambientAdmin: true,
            bearer: true,
            resourceAwarePolicy: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/push/configuration");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "viewer");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/account/push/configuration");
        adminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");
        var adminResponse = await host.Client.SendAsync(adminRequest);

        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task DirectAuthorization_ReturnsSafeFailureForMissingPolicyAndScheme()
    {
        await using var missingPolicy = await CreateHostAsync(
            map: true,
            ambientAdmin: false,
            bearer: true,
            registerPolicy: false);
        using var policyRequest = new HttpRequestMessage(HttpMethod.Get, "/account/push/configuration");
        policyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");

        var policyResponse = await missingPolicy.Client.SendAsync(policyRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, policyResponse.StatusCode);
        Assert.Equal("ASPUSH108", await ReadCodeAsync(policyResponse));

        await using var missingScheme = await CreateHostAsync(
            map: true,
            ambientAdmin: false,
            bearer: true,
            authenticationScheme: "MissingBearer");
        var schemeResponse = await missingScheme.Client.GetAsync("/account/push/configuration");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, schemeResponse.StatusCode);
        Assert.Equal("ASPUSH108", await ReadCodeAsync(schemeResponse));

        await using var ambiguousCookiePolicy = await CreateHostAsync(
            map: true,
            ambientAdmin: true,
            bearer: false,
            declareCookieScheme: false);
        var cookieResponse = await ambiguousCookiePolicy.Client.GetAsync("/account/push/configuration");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, cookieResponse.StatusCode);
        Assert.Equal("ASPUSH108", await ReadCodeAsync(cookieResponse));

        await using var throwingScheme = await CreateHostAsync(
            map: true,
            ambientAdmin: false,
            bearer: true,
            authenticationScheme: "ThrowingProof");
        var throwingResponse = await throwingScheme.Client.GetAsync("/account/push/configuration");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, throwingResponse.StatusCode);
        Assert.Equal("ASPUSH108", await ReadCodeAsync(throwingResponse));
        Assert.DoesNotContain("secret authentication detail", await throwingResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookieDelete_RequiresAntiforgeryBeforeCustody()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: true, bearer: false);
        using var missingToken = new StringContent(
            JsonSerializer.Serialize(new { schemaVersion = 1, endpoint = "https://push.example.test/send" }),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/account/push")
        {
            Content = missingToken,
        };
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ASPUSH104", await ReadCodeAsync(response));
        Assert.Equal(0, host.Custody.UnregisterCalls);
    }

    [Fact]
    public async Task BearerPut_RejectsDisallowedOriginWithoutCustody()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        var wire = CreateSubscriptionWire("https://evil.example.test/send");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/account/push")
        {
            Content = new StringContent(wire.Json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ASPUSH101", await ReadCodeAsync(response));
        Assert.Equal(0, host.Custody.RegisterCalls);
    }

    [Fact]
    public async Task BearerWrites_RejectUnauthenticatedRequestsBeforeCustody()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        using var putContent = new StringContent(CreateSubscriptionWire().Json, Encoding.UTF8, "application/json");
        using var put = await host.Client.PutAsync("/account/push", putContent);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/account/push")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { schemaVersion = 1, endpoint = "https://push.example.test/send" }),
                Encoding.UTF8,
                "application/json"),
        };
        using var delete = await host.Client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(0, host.Custody.RegisterCalls);
        Assert.Equal(0, host.Custody.UnregisterCalls);
    }

    [Fact]
    public async Task BearerWrites_RejectUnsupportedMediaTypeAndInvalidSchemaTypes()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        using var unsupportedRequest = new HttpRequestMessage(HttpMethod.Put, "/account/push")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };
        unsupportedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");
        using var unsupported = await host.Client.SendAsync(unsupportedRequest);
        var invalidSchemaJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "one",
            endpoint = "https://push.example.test/send",
        });
        using var invalidPut = await SendBearerJsonAsync(
            host.Client,
            HttpMethod.Put,
            "/account/push",
            invalidSchemaJson);
        using var invalidDelete = await SendBearerJsonAsync(
            host.Client,
            HttpMethod.Delete,
            "/account/push",
            invalidSchemaJson);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal("ASPUSH102", await ReadCodeAsync(unsupported));
        Assert.Equal(HttpStatusCode.BadRequest, invalidPut.StatusCode);
        Assert.Equal("ASPUSH100", await ReadCodeAsync(invalidPut));
        Assert.Equal(HttpStatusCode.BadRequest, invalidDelete.StatusCode);
        Assert.Equal("ASPUSH100", await ReadCodeAsync(invalidDelete));
        Assert.Equal(0, host.Custody.RegisterCalls);
        Assert.Equal(0, host.Custody.UnregisterCalls);
    }

    [Fact]
    public async Task BearerDelete_RejectsDisallowedOriginWithoutCustody()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, endpoint = "http://push.example.test/send" });

        using var response = await SendBearerJsonAsync(host.Client, HttpMethod.Delete, "/account/push", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ASPUSH101", await ReadCodeAsync(response));
        Assert.Equal(0, host.Custody.UnregisterCalls);
    }

    [Theory]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Conflict, HttpStatusCode.Conflict, "ASPUSH106")]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Rejected, HttpStatusCode.Forbidden, "ASPUSH105")]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Created, HttpStatusCode.NoContent, null)]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Updated, HttpStatusCode.NoContent, null)]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Unchanged, HttpStatusCode.NoContent, null)]
    [InlineData((AppSurfaceWebPushRegistrationDisposition)999, HttpStatusCode.ServiceUnavailable, "ASPUSH107")]
    public async Task BearerPut_MapsCustodyDisposition(
        AppSurfaceWebPushRegistrationDisposition disposition,
        HttpStatusCode expectedStatus,
        string? expectedCode)
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        host.Custody.RegistrationDisposition = disposition;

        var response = await SendBearerJsonAsync(host.Client, HttpMethod.Put, "/account/push", CreateSubscriptionWire().Json);

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, await ReadCodeAsync(response));
        }
    }

    [Theory]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.Conflict, HttpStatusCode.Conflict, "ASPUSH106")]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.Rejected, HttpStatusCode.Forbidden, "ASPUSH105")]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.Removed, HttpStatusCode.NoContent, null)]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.NotFound, HttpStatusCode.NoContent, null)]
    [InlineData((AppSurfaceWebPushUnregistrationDisposition)999, HttpStatusCode.ServiceUnavailable, "ASPUSH107")]
    public async Task BearerDelete_MapsCustodyDisposition(
        AppSurfaceWebPushUnregistrationDisposition disposition,
        HttpStatusCode expectedStatus,
        string? expectedCode)
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        host.Custody.UnregistrationDisposition = disposition;
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, endpoint = "https://push.example.test/send" });

        var response = await SendBearerJsonAsync(host.Client, HttpMethod.Delete, "/account/push", json);

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, await ReadCodeAsync(response));
        }
    }

    [Fact]
    public async Task BearerWrites_ContainMalformedBodiesAndCustodyFailures()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);

        var invalidJson = await SendBearerJsonAsync(host.Client, HttpMethod.Put, "/account/push", "{");
        Assert.Equal(HttpStatusCode.BadRequest, invalidJson.StatusCode);
        Assert.Equal("ASPUSH100", await ReadCodeAsync(invalidJson));

        var invalidSchema = await SendBearerJsonAsync(host.Client, HttpMethod.Delete, "/account/push", "[]");
        Assert.Equal(HttpStatusCode.BadRequest, invalidSchema.StatusCode);
        Assert.Equal("ASPUSH100", await ReadCodeAsync(invalidSchema));

        var stale = CreateSubscriptionWire().Json.Replace("primary", "retired", StringComparison.Ordinal);
        var staleResponse = await SendBearerJsonAsync(host.Client, HttpMethod.Put, "/account/push", stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Equal("ASPUSH109", await ReadCodeAsync(staleResponse));

        host.Custody.ThrowOnRegister = true;
        var custodyFailure = await SendBearerJsonAsync(
            host.Client,
            HttpMethod.Put,
            "/account/push",
            CreateSubscriptionWire().Json);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, custodyFailure.StatusCode);
        Assert.Equal("ASPUSH107", await ReadCodeAsync(custodyFailure));

        host.Custody.ThrowOnRegister = false;
        host.Custody.ThrowOnUnregister = true;
        var deleteJson = JsonSerializer.Serialize(new { schemaVersion = 1, endpoint = "https://push.example.test/send" });
        var unregisterFailure = await SendBearerJsonAsync(host.Client, HttpMethod.Delete, "/account/push", deleteJson);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unregisterFailure.StatusCode);
        Assert.Equal("ASPUSH107", await ReadCodeAsync(unregisterFailure));
    }

    [Fact]
    public async Task Writes_ReturnSafeFailureWhenCustodyIsNotRegistered()
    {
        await using var host = await CreateHostAsync(
            map: true,
            ambientAdmin: false,
            bearer: true,
            registerCustody: false);

        var response = await SendBearerJsonAsync(host.Client, HttpMethod.Put, "/account/push", CreateSubscriptionWire().Json);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ASPUSH107", await ReadCodeAsync(response));

        var deleteJson = JsonSerializer.Serialize(new { schemaVersion = 1, endpoint = "https://push.example.test/send" });
        using var delete = await SendBearerJsonAsync(host.Client, HttpMethod.Delete, "/account/push", deleteJson);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, delete.StatusCode);
        Assert.Equal("ASPUSH107", await ReadCodeAsync(delete));
    }

    [Fact]
    public async Task Writes_RejectOversizeBodiesWithAndWithoutContentLength()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        var oversized = new string('x', (16 * 1024) + 1);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/account/push")
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("ASPUSH103", await ReadCodeAsync(response));

        var streamingResponse = await host.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Delete;
            context.Request.Path = "/account/push";
            context.Request.ContentType = "application/json";
            context.Request.Headers.Authorization = "Bearer admin";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(oversized));
        });

        Assert.Null(streamingResponse.Request.ContentLength);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, streamingResponse.Response.StatusCode);
        Assert.Equal(0, host.Custody.UnregisterCalls);
    }

    [Fact]
    public async Task NamedRateLimiterPolicy_IsEnforcedForEveryProtectedEndpoint()
    {
        await using var host = await CreateHostAsync(
            map: true,
            ambientAdmin: false,
            bearer: true,
            rateLimiterPolicy: "push.limit");

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/account/push/configuration"),
            (HttpMethod.Put, "/account/push"),
            (HttpMethod.Delete, "/account/push"),
        })
        {
            using var first = CreateAuthorizedRequest(method, path);
            var firstResponse = await host.Client.SendAsync(first);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, firstResponse.StatusCode);

            using var second = CreateAuthorizedRequest(method, path);
            var secondResponse = await host.Client.SendAsync(second);
            Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
        }
    }

    [Fact]
    public async Task ClientAsset_UsesVersionedImmutableCachingAndHeadHasNoBody()
    {
        await using var host = await CreateHostAsync(map: true, ambientAdmin: false, bearer: true);
        var unversioned = await host.Client.GetAsync("/_appsurface/pwa/push-client.js");
        var content = await unversioned.Content.ReadAsStringAsync();
        var version = AppSurfaceWebPushClientAsset.Version;

        Assert.Equal("no-cache", unversioned.Headers.CacheControl!.ToString());
        Assert.Contains("ForgeTrust.AppSurface.Pwa.Push.v1", content, StringComparison.Ordinal);

        var versioned = await host.Client.GetAsync($"/_appsurface/pwa/push-client.js?v={version}");
        Assert.Contains("immutable", versioned.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/_appsurface/pwa/push-client.js");
        var head = await host.Client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Mapping_RejectsAppSurfaceReservedRouteSpace()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateHostAsync(map: true, ambientAdmin: false, bearer: false, basePath: "/_appsurface/push"));
    }

    [Theory]
    [InlineData("/_AppSurface/push")]
    [InlineData("/account/{id}")]
    [InlineData("/account/{**rest}")]
    [InlineData("/account/*")]
    [InlineData("/account/../push")]
    [InlineData("/account/./push")]
    public async Task Mapping_RejectsNonLiteralBasePaths(string basePath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateHostAsync(map: true, ambientAdmin: false, bearer: false, basePath: basePath));
    }

    [Fact]
    public async Task Mapping_RejectsCaseOnlyDuplicateBasePaths()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateHostAsync(
                map: true,
                ambientAdmin: false,
                bearer: true,
                secondaryBasePath: "/ACCOUNT/PUSH"));
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return json.RootElement.GetProperty("code").GetString();
    }

    private static async Task<HttpResponseMessage> SendBearerJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string json)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");
        return await client.SendAsync(request);
    }

    private static async Task<TestHostFixture> CreateHostAsync(
        bool map,
        bool ambientAdmin,
        bool bearer,
        bool allowAnonymousGroup = false,
        string basePath = "/account/push",
        string? rateLimiterPolicy = null,
        bool registerPolicy = true,
        string authenticationScheme = "BearerProof",
        bool registerCustody = true,
        bool declareCookieScheme = true,
        bool resourceAwarePolicy = false,
        bool throwingAntiforgery = false,
        string? secondaryBasePath = null)
    {
        var vapid = CreateKeyPair(ECDsa.Create);
        var custody = new RecordingCustody();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("None")
            .AddScheme<AuthenticationSchemeOptions, NoopAuthenticationHandler>("None", _ => { })
            .AddScheme<AuthenticationSchemeOptions, AmbientProofAuthenticationHandler>("AmbientProof", _ => { })
            .AddScheme<AuthenticationSchemeOptions, ThrowingAuthenticationHandler>("ThrowingProof", _ => { })
            .AddScheme<AuthenticationSchemeOptions, BearerProofAuthenticationHandler>("BearerProof", _ => { })
            .AddCookie("CookieProof");
        builder.Services.AddAuthorization(options =>
        {
            if (registerPolicy)
            {
                options.AddPolicy("push.manage", policy =>
                {
                    if (declareCookieScheme)
                    {
                        policy.AddAuthenticationSchemes("AmbientProof");
                    }

                    if (resourceAwarePolicy)
                    {
                        policy.AddRequirements(new AmbientContextAdminRequirement());
                    }
                    else
                    {
                        policy.RequireRole("Admin");
                    }
                });
            }
        });
        builder.Services.AddSingleton<IAuthorizationHandler, AmbientContextAdminHandler>();
        if (rateLimiterPolicy is not null)
        {
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy(rateLimiterPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        $"{context.Request.Method}:{context.Request.Path}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 1,
                            QueueLimit = 0,
                            Window = TimeSpan.FromMinutes(1),
                            AutoReplenishment = false,
                        }));
            });
        }

        if (registerCustody)
        {
            builder.Services.AddSingleton<IAppSurfaceWebPushSubscriptionCustody>(custody);
        }
        builder.Services.AddAppSurfaceWebPush(options =>
        {
            options.ActiveVapidKeyId = "primary";
            options.VapidKeys.Add("primary", new AppSurfaceWebPushVapidKeyOptions
            {
                Subject = "mailto:push@example.test",
                PublicKey = vapid.PublicKey,
                PrivateKey = vapid.PrivateKey,
            });
            options.AllowedPushServiceOrigins.Add("https://push.example.test");
        });
        if (throwingAntiforgery)
        {
            builder.Services.AddSingleton<IAntiforgery, ThrowingAntiforgery>();
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (ambientAdmin)
            {
                context.User = Principal("ambient", "Admin");
            }

            await next();
        });
        app.UseAuthorization();
        if (rateLimiterPolicy is not null)
        {
            app.UseRateLimiter();
        }

        if (map)
        {
            IEndpointRouteBuilder routes = app;
            if (allowAnonymousGroup)
            {
                routes = app.MapGroup("/").AllowAnonymous();
            }

            if (bearer)
            {
                routes.MapAppSurfaceWebPushBearerSubscriptions(
                    basePath,
                    "push.manage",
                    authenticationScheme,
                    rateLimiterPolicy);
                if (secondaryBasePath is not null)
                {
                    routes.MapAppSurfaceWebPushBearerSubscriptions(
                        secondaryBasePath,
                        "push.manage",
                        authenticationScheme,
                        rateLimiterPolicy);
                }
            }
            else
            {
                routes.MapAppSurfaceWebPushSubscriptions(basePath, "push.manage", rateLimiterPolicy);
            }
        }

        await app.StartAsync();
        var client = app.GetTestClient();
        if (ambientAdmin)
        {
            client.DefaultRequestHeaders.Add("X-Ambient-Admin", "true");
        }

        return new TestHostFixture(app, client, app.GetTestServer(), custody);
    }

    private static (string Json, string Endpoint) CreateSubscriptionWire(
        string endpoint = "https://push.example.test/send")
    {
        var subscription = CreateKeyPair(ECDiffieHellman.Create);
        var auth = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            endpoint,
            keys = new { p256dh = subscription.PublicKey, auth },
            vapidKeyId = "primary",
        });
        return (json, endpoint);
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin");
        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static ClaimsPrincipal Principal(string name, string role) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)],
            "proof"));

    private static (string PublicKey, string PrivateKey) CreateKeyPair<T>(Func<T> factory)
        where T : ECAlgorithm
    {
        using var algorithm = factory();
        algorithm.GenerateKey(ECCurve.NamedCurves.nistP256);
        var parameters = algorithm.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (Base64UrlEncode(publicKey), Base64UrlEncode(parameters.D!));
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TestHostFixture(
        WebApplication application,
        HttpClient client,
        TestServer server,
        RecordingCustody custody) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public TestServer Server { get; } = server;
        public RecordingCustody Custody { get; } = custody;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.DisposeAsync();
        }
    }

    private sealed class RecordingCustody : IAppSurfaceWebPushSubscriptionCustody
    {
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public AppSurfaceWebPushSubscription? Registered { get; private set; }
        public AppSurfaceWebPushRegistrationDisposition RegistrationDisposition { get; set; } = AppSurfaceWebPushRegistrationDisposition.Created;
        public AppSurfaceWebPushUnregistrationDisposition UnregistrationDisposition { get; set; } = AppSurfaceWebPushUnregistrationDisposition.Removed;
        public bool ThrowOnRegister { get; set; }
        public bool ThrowOnUnregister { get; set; }

        public ValueTask<AppSurfaceWebPushRegistrationDisposition> RegisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscription subscription,
            CancellationToken cancellationToken)
        {
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("hostile custody detail");
            }

            RegisterCalls++;
            Registered = subscription;
            return ValueTask.FromResult(RegistrationDisposition);
        }

        public ValueTask<AppSurfaceWebPushUnregistrationDisposition> UnregisterAsync(
            AppSurfaceWebPushSubscriptionWriteContext context,
            AppSurfaceWebPushSubscriptionReference subscription,
            CancellationToken cancellationToken)
        {
            if (ThrowOnUnregister)
            {
                throw new InvalidOperationException("hostile custody detail");
            }

            UnregisterCalls++;
            return ValueTask.FromResult(UnregistrationDisposition);
        }

        public ValueTask<AppSurfaceWebPushTerminalDisposition> MarkTerminalAsync(
            AppSurfaceWebPushSubscription subscription,
            AppSurfaceWebPushTerminalReason reason,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AppSurfaceWebPushTerminalDisposition.Completed);
    }

    private sealed class NoopAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class BearerProofAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization == "Bearer admin")
            {
                var ticket = new AuthenticationTicket(Principal("bearer", "Admin"), Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            if (Request.Headers.Authorization == "Bearer viewer")
            {
                var ticket = new AuthenticationTicket(Principal("bearer-viewer", "Viewer"), Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    private sealed class AmbientProofAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Ambient-Admin"] == "true")
            {
                var ticket = new AuthenticationTicket(Principal("ambient", "Admin"), Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            if (Request.Headers["X-Ambient-Viewer"] == "true")
            {
                var ticket = new AuthenticationTicket(Principal("ambient-viewer", "Viewer"), Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    private sealed class ThrowingAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            throw new InvalidOperationException("secret authentication detail");
    }

    private sealed class ThrowingAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext)
        {
            Throw(httpContext);
            throw new UnreachableException();
        }

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext)
        {
            Throw(httpContext);
            throw new UnreachableException();
        }

        public Task<bool> IsRequestValidAsync(HttpContext httpContext)
        {
            Throw(httpContext);
            throw new UnreachableException();
        }

        public void SetCookieTokenAndHeader(HttpContext httpContext) => Throw(httpContext);

        public Task ValidateRequestAsync(HttpContext httpContext)
        {
            Throw(httpContext);
            throw new UnreachableException();
        }

        private static void Throw(HttpContext context)
        {
            if (context.Request.Headers["X-Cancel-Antiforgery"] == "true")
            {
                throw new OperationCanceledException(context.RequestAborted);
            }

            throw new InvalidOperationException("secret antiforgery detail");
        }
    }

    private sealed class AmbientContextAdminRequirement : IAuthorizationRequirement
    {
    }

    private sealed class AmbientContextAdminHandler : AuthorizationHandler<AmbientContextAdminRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            AmbientContextAdminRequirement requirement)
        {
            if (context.Resource is HttpContext httpContext && httpContext.User.IsInRole("Admin"))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
