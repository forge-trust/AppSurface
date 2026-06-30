using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Intelligence;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireEndpointAuthorizationTests
{
    [Fact]
    public async Task StreamEndpoint_DefaultConfiguration_ReturnsUnauthorizedForAnonymousCaller()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Production);

        using var response = await fixture.Client.GetAsync("/_rw/streams/public");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_DoesNotStartSseOrSubscribe()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services => services.AddSingleton<IRazorWireStreamHub>(hub));

        using var response = await fixture.Client.GetAsync("/_rw/streams/sensitive-channel");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, hub.SubscribeCount);
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_InDevelopmentWritesSafePlainTextDiagnostic()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Development);

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("The caller must authenticate before this stream can open", body, StringComparison.Ordinal);
        Assert.Contains("Authenticate the request in host middleware", body, StringComparison.Ordinal);
        Assert.Contains(nameof(AppSurfaceAuthOutcome.Challenge), body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_InProductionWritesEmptyBody()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Production);

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(string.Empty, body);
    }

    public static TheoryData<AppSurfaceAuthResult, HttpStatusCode, string> ResultDenials =>
        new()
        {
            { AppSurfaceAuthResult.Unauthenticated(), HttpStatusCode.Unauthorized, nameof(AppSurfaceAuthOutcome.Challenge) },
            { AppSurfaceAuthResult.Forbidden(), HttpStatusCode.Forbidden, nameof(AppSurfaceAuthOutcome.Forbid) },
            { AppSurfaceAuthResult.MissingPolicy(), HttpStatusCode.InternalServerError, nameof(AppSurfaceAuthOutcome.SetupFailure) },
            { AppSurfaceAuthResult.MissingServices(), HttpStatusCode.InternalServerError, nameof(AppSurfaceAuthOutcome.SetupFailure) },
            { AppSurfaceAuthResult.MissingSubject(), HttpStatusCode.InternalServerError, nameof(AppSurfaceAuthOutcome.SetupFailure) },
            { AppSurfaceAuthResult.UnsafeReturnUrl(), HttpStatusCode.BadRequest, nameof(AppSurfaceAuthOutcome.UnsafeNavigation) },
            { AppSurfaceAuthResult.StaleOrUnknownSession(), HttpStatusCode.Unauthorized, nameof(AppSurfaceAuthOutcome.StaleOrUnknownSession) }
        };

    [Theory]
    [MemberData(nameof(ResultDenials))]
    public async Task StreamEndpoint_ResultDenial_ReturnsMappedStatusBeforeSseOrSubscribe(
        AppSurfaceAuthResult result,
        HttpStatusCode expectedStatus,
        string expectedOutcome)
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireStreamAuthorizer>(new FixedStreamAuthorizer(result));
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains("Problem:", body, StringComparison.Ordinal);
        Assert.Contains("Cause:", body, StringComparison.Ordinal);
        Assert.Contains("Fix:", body, StringComparison.Ordinal);
        Assert.Contains("Docs: Web/ForgeTrust.RazorWire/Docs/stream-authorization.md", body, StringComparison.Ordinal);
        Assert.Contains(expectedOutcome, body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_FilterDenial_RunsBeforePermissiveAuthorizer()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireStreamAuthorizationFilter>(
                    new FixedStreamAuthorizationFilter(AppSurfaceAuthResult.Forbidden()));
                services.AddSingleton<IRazorWireStreamAuthorizer>(
                    new FixedStreamAuthorizer(AppSurfaceAuthResult.Allowed()));
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains(nameof(AppSurfaceAuthOutcome.Forbid), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_FilterDenial_RunsBeforeFallbackAuthorizationPolicy()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services
                    .AddAuthentication(RazorWireHeaderAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, RazorWireHeaderAuthenticationHandler>(
                        RazorWireHeaderAuthenticationHandler.SchemeName,
                        _ => { });
                services.AddAuthorization(
                    options =>
                    {
                        options.FallbackPolicy = new AuthorizationPolicyBuilder(RazorWireHeaderAuthenticationHandler.SchemeName)
                            .RequireAuthenticatedUser()
                            .RequireClaim("scope", "app.fallback")
                            .Build();
                    });
                services.AddSingleton<IRazorWireStreamAuthorizationFilter>(
                    new FixedStreamAuthorizationFilter(AppSurfaceAuthResult.Forbidden()));
                services.AddSingleton<IRazorWireStreamAuthorizer>(
                    new FixedStreamAuthorizer(AppSurfaceAuthResult.Allowed()));
            },
            configureApp: app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(nameof(AppSurfaceAuthOutcome.Forbid), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_ResultAuthorizerPrivacy_OmitsAppMessageMetadataExceptionChannelAndClaims()
    {
        var hub = new TrackingStreamHub();
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireStreamAuthorizer>(
                    new FixedStreamAuthorizer(
                        AppSurfaceAuthResult.Forbidden(
                            message: "token=secret-token-123 and andrew@example.test",
                            metadata: new Dictionary<string, string>
                            {
                                ["return_url"] = "https://evil.example.test/phish?token=secret-token-123"
                            })));
            },
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(loggerProvider);
            },
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, "user-123"),
                                new Claim(ClaimTypes.Email, "andrew@example.test")
                            ],
                            "TestAuth"));

                    await next();
                });
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();
        var deniedLog = Assert.Single(loggerProvider.Entries, entry => entry.EventId.Id == 13700);
        var renderedState = string.Join(" ", deniedLog.State.Select(pair => $"{pair.Key}={pair.Value}"));
        var combined = body + " " + deniedLog.Message + " " + renderedState;

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.DoesNotContain("tenant-secret-42", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-123", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example.test", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("user-123", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("andrew@example.test", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_NullResult_FailsClosedBeforeSseOrSubscribe()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireStreamAuthorizer, NullStreamAuthorizer>();
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/public");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains("NullResult", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_AuthorizerException_FailsClosedAndLogsExceptionTypeOnly()
    {
        var hub = new TrackingStreamHub();
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireStreamAuthorizer, ThrowingStreamAuthorizer>();
            },
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(loggerProvider);
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/public");
        var body = await response.Content.ReadAsStringAsync();
        var entry = Assert.Single(loggerProvider.Entries, log => log.EventId.Id == 13700);
        var renderedState = string.Join(" ", entry.State.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains("Exception", body, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("exploded secret", entry.Message + renderedState + body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_LegacyAuthorizerFactoryException_FailsClosedAndLogsExceptionTypeOnly()
    {
        var hub = new TrackingStreamHub();
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireChannelAuthorizer>(_ => throw new InvalidOperationException("legacy secret"));
            },
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(loggerProvider);
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/public");
        var body = await response.Content.ReadAsStringAsync();
        var entry = Assert.Single(loggerProvider.Entries, log => log.EventId.Id == 13700);
        var renderedState = string.Join(" ", entry.State.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains("Exception", body, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy secret", entry.Message + renderedState + body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_LegacyAuthorizerMethodException_LogsLegacyAuthorizerType()
    {
        var hub = new TrackingStreamHub();
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireChannelAuthorizer, ThrowingLegacyChannelAuthorizer>();
            },
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(loggerProvider);
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/public");
        var body = await response.Content.ReadAsStringAsync();
        var entry = Assert.Single(loggerProvider.Entries, log => log.EventId.Id == 13700);
        var renderedState = string.Join(" ", entry.State.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains(nameof(ThrowingLegacyChannelAuthorizer), renderedState, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(RazorWireBoolChannelAuthorizerAdapter), renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy method secret", entry.Message + renderedState + body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AntiforgeryTokenEndpoint_ReturnsTokenPayloadWithNoStoreHeaders()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Production);

        using var response = await fixture.Client.GetAsync("/_rw/antiforgery/token");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains(response.Headers.Pragma, pragma => string.Equals(pragma.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("__RequestVerificationToken", document.RootElement.GetProperty("formFieldName").GetString());
        Assert.Equal("RequestVerificationToken", document.RootElement.GetProperty("headerName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("requestToken").GetString()));
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_LogOmitsRawChannelAndUserDetail()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: null,
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(loggerProvider);
            },
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, "user-123"),
                                new Claim(ClaimTypes.Email, "andrew@example.test")
                            ],
                            "TestAuth"));

                    await next();
                });
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = Assert.Single(
            loggerProvider.Entries,
            log => log.EventId.Id == 13700 && log.EventId.Name == "StreamSubscriptionDenied");
        Assert.Contains("RazorWire stream subscription denied", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ConfiguredAuthorizationMode", entry.Message, StringComparison.Ordinal);
        Assert.Contains("AuthorizerType", entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DenyAllRazorWireChannelAuthorizer), entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RazorWireStreamAuthorizationMode.DenyAll), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user-123", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("andrew@example.test", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_InvalidChannel_ReturnsBadRequestBeforeAuthorizerOrHub()
    {
        var hub = new TrackingStreamHub();
        var authorizer = new CountingAllowAuthorizer();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireChannelAuthorizer>(authorizer);
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant%20secret");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains(nameof(RazorWireStreamAdmissionRejectionReason.InvalidChannelName), body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_AppSurfaceEndpointAwareMiddleware_PrincipalReachesAuthorizer()
    {
        var root = new RazorWireEndpointAwareAuthModule();
        root.Authorizer.Reset();
        var startup = new RazorWireEndpointAwareAuthStartup(root);

        await using var fixture = await AppSurfaceRazorWireFixture.StartAsync(startup, root);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/_rw/streams/public");
        request.Headers.Add(RazorWireHeaderAuthenticationHandler.UserHeaderName, "razorwire-user");
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("razorwire-user", root.Authorizer.LastUserName);
    }

    [Fact]
    public async Task StreamEndpoint_OverLimit_ReturnsTooManyRequestsBeforeSseOrSubscribe()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveSubscriptions = 1;
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/public",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("text/event-stream", accepted.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotEqual("text/event-stream", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, hub.SubscribeCount);
    }

    [Fact]
    public async Task StreamEndpoint_EncodedAllowedChannel_DecodesBeforeSubscribe()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                });
            });

        using var response = await fixture.Client.GetAsync(
            "/_rw/streams/tenant%3Aorders",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tenant:orders", hub.SubscribedChannel);
    }

    [Fact]
    public async Task StreamEndpoint_AdmissionRejection_InDevelopmentWritesActionableSafeDiagnostic()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveChannels = 1;
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/tenant-secret-42",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other-secret-99");
        var body = await rejected.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Contains(nameof(RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels), body, StringComparison.Ordinal);
        Assert.Contains(nameof(RazorWireStreamOptions.MaxLiveChannels), body, StringComparison.Ordinal);
        Assert.Contains("ConfiguredLimit: 1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", body, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret-99", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_AdmissionRejection_LogStateOmitsRawChannelAndUserDetail()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: services =>
            {
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveChannels = 1;
                });
            },
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(loggerProvider);
            },
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, "user-123"),
                                new Claim(ClaimTypes.Email, "andrew@example.test")
                            ],
                            "TestAuth"));

                    await next();
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/tenant-secret-42",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other-secret-99");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var entry = Assert.Single(
            loggerProvider.Entries,
            log => log.EventId.Id == 13701 && log.EventId.Name == "StreamAdmissionRejected");

        var renderedState = string.Join(
            " ",
            entry.State.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.Contains(nameof(RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels), entry.Message, StringComparison.Ordinal);
        Assert.Contains("RejectionReason", renderedState, StringComparison.Ordinal);
        Assert.Contains("LiveChannels=1", renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", entry.Message + renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret-99", entry.Message + renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("user-123", entry.Message + renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("andrew@example.test", entry.Message + renderedState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_AdmissionRejection_CapturesProductIntelligenceWhenEnabled()
    {
        var sink = new ProductIntelligenceRecordingSink();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddAppSurfaceProductIntelligence(options => options.EnableExperimentalEvents());
                services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.BasePath = "/custom-streams";
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveChannels = 1;
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/custom-streams/tenant-secret-42",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/custom-streams/other-secret-99");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var productEvent = Assert.Single(sink.Events);
        Assert.Equal(AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected, productEvent.Name);
        Assert.Equal("/custom-streams/{channel}", productEvent.Route);
        Assert.Equal("TooManyLiveChannels", productEvent.Properties["rejection_reason"]);
        Assert.Equal("max_live_channels", productEvent.Properties["limit_name"]);
        Assert.Equal("AllowAll", productEvent.Properties["authorization_mode"]);
        Assert.DoesNotContain("tenant-secret-42", string.Join(" ", productEvent.Properties.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret-99", string.Join(" ", productEvent.Properties.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_AdmissionRejection_DoesNotCaptureProductIntelligenceWhenExperimentalDisabled()
    {
        var sink = new ProductIntelligenceRecordingSink();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddAppSurfaceProductIntelligence();
                services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveChannels = 1;
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/tenant-secret-42",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other-secret-99");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task StreamEndpoint_AdmissionRejection_ProductIntelligenceCaptureIsTimeBound()
    {
        var sink = new ProductIntelligenceBlockingSink();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddAppSurfaceProductIntelligence(options => options.EnableExperimentalEvents());
                services.AddSingleton<IAppSurfaceProductIntelligenceSink>(sink);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveChannels = 1;
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/tenant-secret-42",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other-secret-99");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        await WaitUntilAsync(() => sink.CancellationObserved);
    }

    [Fact]
    public async Task StreamEndpoint_SubscribeThrow_ReleasesAdmissionCapacity()
    {
        var hub = new TrackingStreamHub { ThrowOnSubscribe = true };
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveSubscriptions = 1;
                });
            });

        using var failed = await fixture.Client.GetAsync("/_rw/streams/public");
        hub.ThrowOnSubscribe = false;
        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/public",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("text/event-stream", accepted.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task StreamEndpoint_UnsubscribeThrow_ReleasesAdmissionCapacity()
    {
        var hub = new TrackingStreamHub { ThrowOnUnsubscribe = true };
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveSubscriptions = 1;
                });
            });

        using (var accepted = await fixture.Client.GetAsync(
                   "/_rw/streams/public",
                   HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await WaitUntilAsync(() => hub.UnsubscribeCount > 0);

        hub.ThrowOnUnsubscribe = false;
        using var next = await fixture.Client.GetAsync(
            "/_rw/streams/public",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    private sealed class RazorWireEndpointFixture : IAsyncDisposable
    {
        private RazorWireEndpointFixture(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public HttpClient Client { get; }

        private WebApplication App { get; }

        public static async Task<RazorWireEndpointFixture> StartAsync(
            string environmentName,
            Action<IServiceCollection>? configureServices = null,
            Action<ILoggingBuilder>? configureLogging = null,
            Action<WebApplication>? configureApp = null)
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    EnvironmentName = environmentName
                });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            configureLogging?.Invoke(builder.Logging);
            configureServices?.Invoke(builder.Services);
            builder.Services.AddControllersWithViews();
            builder.Services.AddRazorWire();

            var app = builder.Build();
            configureApp?.Invoke(app);
            app.MapRazorWire();
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);
            var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            return new RazorWireEndpointFixture(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class AppSurfaceRazorWireFixture : IAsyncDisposable
    {
        private readonly IHost _host;

        private AppSurfaceRazorWireFixture(IHost host, HttpClient client)
        {
            _host = host;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<AppSurfaceRazorWireFixture> StartAsync<TModule>(
            WebStartup<TModule> startup,
            TModule root)
            where TModule : IAppSurfaceWebModule, new()
        {
            var context = new StartupContext([], root);
            var builder = ((IAppSurfaceStartup)startup).CreateHostBuilder(context);
            builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

            var host = builder.Build();
            await host.StartAsync();

            var server = host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);
            var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            return new AppSurfaceRazorWireFixture(host, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed class RazorWireEndpointAwareAuthStartup : WebStartup<RazorWireEndpointAwareAuthModule>
    {
        private readonly RazorWireEndpointAwareAuthModule _module;

        public RazorWireEndpointAwareAuthStartup(RazorWireEndpointAwareAuthModule module)
        {
            _module = module;
        }

        protected override RazorWireEndpointAwareAuthModule CreateRootModule() => _module;
    }

    private sealed class RazorWireEndpointAwareAuthModule : IAppSurfaceWebModule
    {
        public RecordingAppSurfaceAuthorizer Authorizer { get; } = new();

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            services
                .AddAuthentication(RazorWireHeaderAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, RazorWireHeaderAuthenticationHandler>(
                    RazorWireHeaderAuthenticationHandler.SchemeName,
                    _ => { });
            services.AddAuthorization();
            services.AddSingleton(Authorizer);
            services.AddSingleton<IRazorWireChannelAuthorizer>(Authorizer);
        }

        public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<RazorWireWebModule>();
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }

    private sealed class RecordingAppSurfaceAuthorizer : IRazorWireChannelAuthorizer
    {
        private string? _lastUserName;

        public string? LastUserName => Volatile.Read(ref _lastUserName);

        public void Reset()
        {
            Volatile.Write(ref _lastUserName, null);
        }

        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            Volatile.Write(ref _lastUserName, context.User.Identity?.Name);

            return ValueTask.FromResult(context.User.Identity?.IsAuthenticated == true);
        }
    }

    private sealed class RazorWireHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "RazorWireHeaderTest";
        public const string UserHeaderName = "X-Test-User";

        public RazorWireHeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeaderName, out var userValues)
                || string.IsNullOrWhiteSpace(userValues[0]))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userName = userValues[0]!;
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, userName),
                    new Claim(ClaimTypes.NameIdentifier, userName)
                ],
                SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TrackingStreamHub : IRazorWireStreamHub
    {
        private int _subscribeCount;
        private int _unsubscribeCount;

        public string? SubscribedChannel { get; private set; }

        public bool ThrowOnSubscribe { get; set; }

        public bool ThrowOnUnsubscribe { get; set; }

        public int SubscribeCount => Volatile.Read(ref _subscribeCount);

        public int UnsubscribeCount => Volatile.Read(ref _unsubscribeCount);

        public ValueTask PublishAsync(string channel, string message)
        {
            return ValueTask.CompletedTask;
        }

        public ChannelReader<string> Subscribe(string channel)
        {
            Interlocked.Increment(ref _subscribeCount);
            SubscribedChannel = channel;
            if (ThrowOnSubscribe)
            {
                throw new InvalidOperationException("Subscribe failed for test.");
            }

            return Channel.CreateUnbounded<string>().Reader;
        }

        public void Unsubscribe(string channel, ChannelReader<string> reader)
        {
            Interlocked.Increment(ref _unsubscribeCount);
            if (ThrowOnUnsubscribe)
            {
                throw new InvalidOperationException("Unsubscribe failed for test.");
            }
        }
    }

    private sealed class ProductIntelligenceRecordingSink : IAppSurfaceProductIntelligenceSink
    {
        public IReadOnlyCollection<AppSurfaceProductEvent> Events => _events.ToArray();

        private readonly ConcurrentQueue<AppSurfaceProductEvent> _events = new();

        public ValueTask CaptureAsync(
            AppSurfaceProductEvent productEvent,
            CancellationToken cancellationToken = default)
        {
            _events.Enqueue(productEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProductIntelligenceBlockingSink : IAppSurfaceProductIntelligenceSink
    {
        private int _cancellationObserved;

        public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) == 1;

        public async ValueTask CaptureAsync(
            AppSurfaceProductEvent productEvent,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _cancellationObserved, 1);
                throw;
            }
        }
    }

    private sealed class FixedStreamAuthorizer(AppSurfaceAuthResult result) : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult>(result);
        }
    }

    private sealed class FixedStreamAuthorizationFilter(AppSurfaceAuthResult? result) : IRazorWireStreamAuthorizationFilter
    {
        public ValueTask<AppSurfaceAuthResult?> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult?>(result);
        }
    }

    private sealed class NullStreamAuthorizer : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            return new ValueTask<AppSurfaceAuthResult>((AppSurfaceAuthResult)null!);
        }
    }

    private sealed class ThrowingStreamAuthorizer : IRazorWireStreamAuthorizer
    {
        public ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
        {
            throw new InvalidOperationException("exploded secret");
        }
    }

    private sealed class CountingAllowAuthorizer : IRazorWireChannelAuthorizer
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class ThrowingLegacyChannelAuthorizer : IRazorWireChannelAuthorizer
    {
        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            throw new InvalidOperationException("legacy method secret");
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

        public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(_entries);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var stateValues = state as IEnumerable<KeyValuePair<string, object?>>;
            entries.Enqueue(
                new CapturedLogEntry(
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    stateValues?.ToArray() ?? []));
        }
    }

    private sealed record CapturedLogEntry(
        LogLevel LogLevel,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(predicate());
    }
}
