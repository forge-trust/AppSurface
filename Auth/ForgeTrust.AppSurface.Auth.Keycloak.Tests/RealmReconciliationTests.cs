using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// Protects: reconciliation validates before transport, bounds retries, and observes uncertain writes before retrying.
// seam: the intended internal reconciler API exposed through InternalsVisibleTo; all HTTP is a scripted handler.
public sealed class RealmReconciliationTests
{
    private static readonly Uri BaseAddress = new("https://localhost:8443/");

    [Flags]
    private enum CommonSuccessRoutes
    {
        None = 0,
        AdministratorToken = 1,
        Realm = 2,
        Client = 4
    }

    private static ScriptedHandler SuccessfulHandler(CommonSuccessRoutes routes,
        Func<HttpRequestMessage, int, HttpResponseMessage?>? perCase = null)
        => new((request, count) =>
        {
            var response = perCase?.Invoke(request, count);
            if (response is not null) return response;

            var path = request.RequestUri!.PathAndQuery;
            if (routes.HasFlag(CommonSuccessRoutes.AdministratorToken)
                && request.Method == HttpMethod.Post
                && path == "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (routes.HasFlag(CommonSuccessRoutes.Realm)
                && request.Method == HttpMethod.Get
                && path == "/admin/realms/sample")
                return Json(HttpStatusCode.OK, RealmJson());
            if (routes.HasFlag(CommonSuccessRoutes.Client)
                && request.Method == HttpMethod.Get
                && path == "/admin/realms/sample/clients?clientId=web-app")
                return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");

            throw new InvalidOperationException($"Unexpected reconciliation request: {request.Method} {path}.");
        });

    [Fact]
    public async Task InvalidDesiredConfigurationFailsBeforeAnyRequest()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);
        var desired = Desired() with { Realm = "invalid/realm" };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidConfiguration", result.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("blank-username")]
    [InlineData("empty-password")]
    [InlineData("long-username")]
    [InlineData("long-password")]
    [InlineData("public-secret")]
    [InlineData("confidential-missing-secret")]
    [InlineData("confidential-long-secret")]
    public async Task InvalidCredentialsFailBeforeAnyRequest(string variant)
    {
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("Invalid credentials must never reach transport."));
        using var client = Client(handler);
        var desired = Desired() with { Confidential = variant.StartsWith("confidential-", StringComparison.Ordinal) };
        var normal = Credentials();
        var credentials = variant switch
        {
            "blank-username" => normal with { AdminUsername = " " },
            "empty-password" => normal with { AdminPassword = "" },
            "long-username" => normal with { AdminUsername = new string('a', 257) },
            "long-password" => normal with { AdminPassword = new string('a', 4097) },
            "public-secret" => normal with { ClientSecret = "discarded-fixture-secret" },
            "confidential-missing-secret" => normal,
            "confidential-long-secret" => normal with { ClientSecret = new string('a', 4097) },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidBinding", result.Code);
        Assert.Equal("validation", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:8443/")]
    [InlineData("https://localhost:8443/not-an-origin")]
    [InlineData("https://localhost:8443/?mode=invalid")]
    public async Task AdministratorCredentialsRequireAnExactHttpsTransportOrigin(string? baseAddress)
    {
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("Invalid transport must never receive credentials."));
        using var client = new HttpClient(handler) { BaseAddress = baseAddress is null ? null : new Uri(baseAddress) };

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidConfiguration", result.Code);
        Assert.Equal("validation", result.Stage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationBeforeTheFirstRequestReturnsNoSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("No request is expected."));
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), cancellation.Token);

        Assert.False(result.Ready);
        Assert.Equal("Cancelled", result.Code);
        Assert.Equal("validation", result.Stage);
        Assert.Equal("None", result.Effect);
        Assert.Equal("NoRetry", result.Recovery);
        Assert.Equal(0, result.Writes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidDeclarationPrecedesInvalidCredentialsWithoutSendingRequests()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);
        var desired = Desired() with { Realm = "invalid/realm" };
        var credentials = Credentials() with { AdminPassword = "" };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidConfiguration", result.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PartialGoogleCredentialPairFailsBeforeAnyRequest()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);
        var desired = Desired() with { Brokers = [new RealmReconcileBroker("google", true, "google", "google-client")] };
        var credentials = Credentials() with { BrokerSecrets = ImmutableDictionary<string, string>.Empty };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidBinding", result.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("missing-map")]
    [InlineData("missing-alias")]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("over-limit")]
    [InlineData("unknown-alias")]
    public async Task GoogleBrokerSecretsRequireExactNonBlankBindings(string variant)
    {
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("Invalid broker bindings must never reach transport."));
        using var client = Client(handler);
        var desired = Desired() with { Brokers = [new RealmReconcileBroker("google", true, "google", "google-client")] };
        var normal = ImmutableDictionary<string, string>.Empty.Add("google", "fixture-google-secret");
        var secrets = variant switch
        {
            "missing-map" => null,
            "missing-alias" => ImmutableDictionary<string, string>.Empty,
            "empty" => normal.SetItem("google", ""),
            "blank" => normal.SetItem("google", " \t"),
            "over-limit" => normal.SetItem("google", new string('a', 4097)),
            "unknown-alias" => normal.Add("unexpected", "fixture-extra-secret"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var credentials = Credentials() with { BrokerSecrets = secrets };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidBinding", result.Code);
        Assert.Equal("validation", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ApprovedPublicAuthorityCanUseLoopbackAdminTransport()
    {
        var desired = Desired() with
        {
            Authority = "https://login.example/realms/sample",
            TransportOrigin = "https://localhost:8443/",
            ApprovedHttpsOrigins = ["https://login.example", "https://app.example"],
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = ["https://app.example/signed-out"]
        };

        RealmReconciler.ValidateDesired(desired);
    }

    [Fact]
    public async Task PublicAuthorityRejectsMismatchedLoopbackAdminTransportBeforeAnyRequest()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:8444/") };
        var desired = Desired() with
        {
            Authority = "https://login.example/realms/sample",
            TransportOrigin = "https://localhost:8443/",
            ApprovedHttpsOrigins = ["https://login.example", "https://app.example"],
            RedirectUris = ["https://app.example/callback"],
            PostLogoutRedirectUris = ["https://app.example/signed-out"]
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidConfiguration", result.Code);
        Assert.Equal("validation", result.Stage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RemoteAdminTransportIsRejectedBeforeAnyRequest()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://admin.example/") };
        var desired = Desired() with
        {
            Authority = "https://login.example/realms/sample",
            TransportOrigin = "https://admin.example/",
            ApprovedHttpsOrigins = ["https://login.example", "https://admin.example"]
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidConfiguration", result.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void MapperCannotDeclareBothSourceKinds()
    {
        var desired = Desired() with
        {
            Mappers = [new RealmReconcileMapper("friendly name", "department", "unused", "department", "department")]
        };

        Assert.Throws<ArgumentException>(() => RealmReconciler.ValidateDesired(desired));
    }

    [Theory]
    [InlineData("web-origin", "https://public.example")]
    [InlineData("web-origin", "https://*.example")]
    [InlineData("approved-origin", "https://*.example")]
    public void UnapprovedOrWildcardPublicOriginsAreRejected(string originKind, string origin)
    {
        var desired = originKind == "web-origin"
            ? Desired() with { WebOrigins = [origin] }
            : Desired() with { ApprovedHttpsOrigins = [origin] };

        Assert.Throws<ArgumentException>(() => RealmReconciler.ValidateDesired(desired));
    }

    [Theory]
    [InlineData("realm-empty")]
    [InlineData("client-empty")]
    [InlineData("mapper-attribute-empty")]
    [InlineData("mapper-note-empty")]
    [InlineData("client-name")]
    [InlineData("authority-scheme")]
    [InlineData("authority-path")]
    [InlineData("authority-query")]
    [InlineData("authority-user")]
    [InlineData("authority-fragment")]
    [InlineData("transport-path")]
    [InlineData("transport-mismatch")]
    [InlineData("approved-default")]
    [InlineData("approved-http")]
    [InlineData("approved-loopback")]
    [InlineData("approved-port")]
    [InlineData("approved-count")]
    [InlineData("theme")]
    [InlineData("missing-redirect")]
    [InlineData("missing-logout")]
    [InlineData("redirect-list")]
    [InlineData("redirect-duplicate")]
    [InlineData("redirect-query")]
    [InlineData("redirect-fragment")]
    [InlineData("redirect-user")]
    [InlineData("redirect-scheme")]
    [InlineData("origin-path")]
    [InlineData("mapper-list")]
    [InlineData("mapper-count")]
    [InlineData("mapper-name")]
    [InlineData("mapper-claim")]
    [InlineData("mapper-value")]
    [InlineData("mapper-attribute")]
    [InlineData("mapper-note")]
    [InlineData("mapper-duplicate")]
    [InlineData("mapper-remove-collision")]
    [InlineData("broker-list")]
    [InlineData("broker-count")]
    [InlineData("broker-alias")]
    [InlineData("broker-provider")]
    [InlineData("broker-label")]
    [InlineData("broker-enabled-oidc")]
    [InlineData("broker-oidc-client")]
    [InlineData("broker-disabled-google")]
    [InlineData("broker-missing-google-client")]
    [InlineData("broker-duplicate")]
    [InlineData("broker-remove-collision")]
    [InlineData("post-logout-list")]
    [InlineData("web-origin-list")]
    [InlineData("remove-mapper-list")]
    [InlineData("remove-broker-list")]
    public async Task MalformedManagedDeclarationFailsClosedBeforeAdministratorAuthentication(string field)
    {
        var baseline = Desired();
        var mapper = new RealmReconcileMapper("department", "department", "general");
        var broker = new RealmReconcileBroker("fixture", false);
        var desired = field switch
        {
            "realm-empty" => baseline with { Realm = "" },
            "client-empty" => baseline with { ClientId = "" },
            "mapper-attribute-empty" => baseline with { Mappers = [mapper with { UserAttribute = "" }] },
            "mapper-note-empty" => baseline with { Mappers = [mapper with { SessionNote = "" }] },
            "client-name" => baseline with { ClientId = "bad/client" },
            "authority-scheme" => baseline with { Authority = "http://localhost:8443/realms/sample" },
            "authority-path" => baseline with { Authority = "https://localhost:8443/realms/other" },
            "authority-query" => baseline with { Authority = "https://localhost:8443/realms/sample?a=b" },
            "authority-user" => baseline with { Authority = "https://user@localhost:8443/realms/sample" },
            "authority-fragment" => baseline with { Authority = "https://localhost:8443/realms/sample#fragment" },
            "transport-path" => baseline with { TransportOrigin = "https://localhost:8443/extra" },
            "transport-mismatch" => baseline with { TransportOrigin = "https://localhost:8444/" },
            "approved-default" => baseline with { ApprovedHttpsOrigins = default },
            "approved-http" => baseline with { ApprovedHttpsOrigins = ["http://localhost:8443"] },
            "approved-loopback" => baseline with { ApprovedHttpsOrigins = ["https://localhost"] },
            "approved-port" => baseline with { ApprovedHttpsOrigins = ["https://example.invalid:9443"] },
            "approved-count" => baseline with { ApprovedHttpsOrigins = Enumerable.Range(0, 33).Select(i => $"https://approved{i}.invalid").ToImmutableArray() },
            "theme" => baseline with { LoginThemeName = "../theme" },
            "missing-redirect" => baseline with { RedirectUris = [] },
            "missing-logout" => baseline with { PostLogoutRedirectUris = [] },
            "post-logout-list" => baseline with { PostLogoutRedirectUris = default },
            "redirect-list" => baseline with { RedirectUris = default },
            "redirect-duplicate" => baseline with { RedirectUris = [baseline.RedirectUris[0], baseline.RedirectUris[0]] },
            "redirect-query" => baseline with { RedirectUris = ["http://localhost:5055/callback?x=y"] },
            "redirect-fragment" => baseline with { RedirectUris = ["http://localhost:5055/callback#fragment"] },
            "redirect-user" => baseline with { RedirectUris = ["http://user@localhost:5055/callback"] },
            "redirect-scheme" => baseline with { RedirectUris = ["ftp://localhost:5055/callback"] },
            "origin-path" => baseline with { WebOrigins = ["http://localhost:5055/unexpected"] },
            "web-origin-list" => baseline with { WebOrigins = default },
            "mapper-list" => baseline with { Mappers = default },
            "mapper-count" => baseline with { Mappers = Enumerable.Repeat(mapper, 129).ToImmutableArray() },
            "mapper-name" => baseline with { Mappers = [mapper with { Name = " department" }] },
            "mapper-claim" => baseline with { Mappers = [mapper with { Claim = "department/bad" }] },
            "mapper-value" => baseline with { Mappers = [mapper with { Value = new string('a', 257) }] },
            "mapper-attribute" => baseline with { Mappers = [mapper with { UserAttribute = "bad/attribute" }] },
            "mapper-note" => baseline with { Mappers = [mapper with { SessionNote = "bad/note" }] },
            "mapper-duplicate" => baseline with { Mappers = [mapper, mapper] },
            "mapper-remove-collision" => baseline with { Mappers = [mapper], RemoveMappers = [mapper.Name] },
            "remove-mapper-list" => baseline with { RemoveMappers = default },
            "broker-list" => baseline with { Brokers = default },
            "broker-count" => baseline with { Brokers = Enumerable.Repeat(broker, 17).ToImmutableArray() },
            "broker-alias" => baseline with { Brokers = [broker with { Alias = "bad/alias" }] },
            "broker-provider" => baseline with { Brokers = [broker with { ProviderId = "unsupported" }] },
            "broker-label" => baseline with { Brokers = [broker with { DisplayName = "broken\nlabel" }] },
            "broker-enabled-oidc" => baseline with { Brokers = [broker with { Enabled = true }] },
            "broker-oidc-client" => baseline with { Brokers = [broker with { ClientId = "unexpected" }] },
            "broker-disabled-google" => baseline with { Brokers = [broker with { ProviderId = "google", ClientId = "google-client" }] },
            "broker-missing-google-client" => baseline with { Brokers = [broker with { ProviderId = "google", Enabled = true }] },
            "broker-duplicate" => baseline with { Brokers = [broker, broker] },
            "broker-remove-collision" => baseline with { Brokers = [broker], RemoveBrokers = [broker.Alias] },
            "remove-broker-list" => baseline with { RemoveBrokers = default },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("Validation must precede network access."));
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidConfiguration", result.Code);
        Assert.Equal("validation", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "AuthorizationDenied")]
    [InlineData(HttpStatusCode.Forbidden, "{}", "AuthorizationDenied")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", "AuthenticationRejected")]
    [InlineData(HttpStatusCode.OK, "{}", "AuthenticationRejected")]
    public async Task AuthenticationFailuresAreNotRetried(HttpStatusCode status, string body, string expectedCode)
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/realms/master/protocol/openid-connect/token")
                throw new InvalidOperationException("Authentication failure must stop before Admin API access.");
            return Json(status, body);
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal("administrator-authentication", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.Equal(0, result.Retries);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReadTransportAndRateLimitFailuresUseTwoBoundedRetries()
    {
        var realmReads = 0;
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");

            if (request.RequestUri.AbsolutePath == "/admin/realms/sample")
            {
                realmReads++;
                if (realmReads == 1) throw new HttpRequestException("scripted transport failure");
                return Json(realmReads == 2 ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable, "{}");
            }

            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var clock = Stopwatch.StartNew();

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("RetryExhausted", result.Code);
        Assert.Equal(2, result.Retries);
        Assert.Equal(3, realmReads);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1400), $"Expected the 500 ms and 1,000 ms retry delays; elapsed {clock.Elapsed}.");
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task RetryAfterBeyondNextDelayFailsWithoutAnotherRead()
    {
        var realmReads = 0;
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");

            if (request.RequestUri.AbsolutePath == "/admin/realms/sample")
            {
                realmReads++;
                var response = Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return response;
            }

            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("RetryBudgetRejected", result.Code);
        Assert.Equal(0, result.Retries);
        Assert.Equal(1, realmReads);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task OversizedAuthenticationResponseIsCappedBeforeAdminRequests()
    {
        var handler = new ScriptedHandler((request, _) => request.RequestUri!.AbsolutePath == "/realms/master/protocol/openid-connect/token"
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"" + new string('x', 1024 * 1024) + "\"}")
            : Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ResponseLimit", result.Code);
        Assert.Equal("administrator-authentication", result.Stage);
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.StartsWith("/admin/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedSuccessfulRealmResponseFailsClosedWithoutWrites()
    {
        var handler = new ScriptedHandler((request, _) => request.RequestUri!.AbsolutePath == "/realms/master/protocol/openid-connect/token"
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}")
            : Json(HttpStatusCode.OK, "{"));
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("Unexpected", result.Code);
        Assert.Equal("realm-reconcile", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.Equal(0, result.Retries);
    }

    [Fact]
    public async Task ManagedProviderDriftThatPersistsAfterPutExhaustsObservedRetries()
    {
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == brokerPath && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, """[{"alias":"disabled-preview","providerId":"oidc","enabled":true,"config":{"clientId":"synthetic-disabled-provider","authorizationUrl":"https://provider.invalid/authorize","tokenUrl":"https://provider.invalid/token","userInfoUrl":"https://provider.invalid/userinfo","syncMode":"IMPORT","appsurface.issue888.managed":"generic-feasibility"}}]""");
            if (path == brokerPath + "/disabled-preview" && request.Method == HttpMethod.Put)
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with { Brokers = [new RealmReconcileBroker("disabled-preview", false)] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("RetryExhausted", result.Code);
        Assert.Equal("broker-reconcile", result.Stage);
        Assert.Equal(2, result.Retries);
        Assert.Equal(3, result.UnknownWrites);
        Assert.Equal(3, handler.Requests.Count(request => request.Method == HttpMethod.Put && request.PathAndQuery == brokerPath + "/disabled-preview"));
    }

    [Fact]
    public async Task CommittedUnknownRealmWriteIsReadBackWithoutRepeatingPost()
    {
        var realmCreated = false;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample")
                return realmCreated ? Json(HttpStatusCode.OK, RealmJson()) : Json(HttpStatusCode.NotFound, "{}");
            if (path == "/admin/realms" && request.Method == HttpMethod.Post)
            {
                realmCreated = true;
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (path == "/admin/realms/sample/clients?clientId=web-app")
                return Json(HttpStatusCode.Unauthorized, "{}");
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("AuthorizationDenied", result.Code);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post && request.PathAndQuery == "/admin/realms"));
        Assert.True(realmCreated);
    }

    [Fact]
    public async Task MaskedSecretReadbackMakesUnknownClientWriteFailWithoutRepeatingPost()
    {
        var clientCreated = false;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app")
                return clientCreated ? Json(HttpStatusCode.OK, "[" + ClientJson() + "]") : Json(HttpStatusCode.OK, "[]");
            if (path == "/admin/realms/sample/clients" && request.Method == HttpMethod.Post)
            {
                clientCreated = true;
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (path == "/admin/realms/sample/clients/client-internal/client-secret")
                return Json(HttpStatusCode.OK, "{\"value\":\"********\"}");
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with { Confidential = true };
        var credentials = Credentials() with { ClientSecret = "client-secret" };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("UnknownWriteOutcome", result.Code);
        Assert.Equal("Unknown", result.Effect);
        Assert.Equal("InspectBeforeRetry", result.Recovery);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post && request.PathAndQuery == "/admin/realms/sample/clients"));
        Assert.True(clientCreated);
    }

    [Fact]
    public async Task UserAttributeMapperWriteUsesAttributeMapperAndPassesEffectiveReadback()
    {
        JsonNode? writtenMapper = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models")
            {
                if (request.Method == HttpMethod.Post)
                {
                    writtenMapper = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    return Json(HttpStatusCode.Created, "{}");
                }
                return Json(HttpStatusCode.OK, writtenMapper is null ? "[]" : "[" + writtenMapper.ToJsonString() + "]");
            }
            if (path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Mappers = [new RealmReconcileMapper("Friendly department", "department", "unused", UserAttribute: "department")]
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal("oidc-usermodel-attribute-mapper", writtenMapper?["protocolMapper"]?.GetValue<string>());
        Assert.Equal("department", writtenMapper?["config"]?["user.attribute"]?.GetValue<string>());
        Assert.Equal("department", writtenMapper?["config"]?["claim.name"]?.GetValue<string>());
        Assert.Null(writtenMapper?["config"]?["claim.value"]);
        Assert.Single(handler.Requests, x => x.Method == HttpMethod.Post && x.PathAndQuery == "/admin/realms/sample/clients/client-internal/protocol-mappers/models");
    }

    [Fact]
    public async Task SessionNoteMapperWriteUsesSessionNoteMapperAndPassesEffectiveReadback()
    {
        JsonNode? writtenMapper = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models")
            {
                if (request.Method == HttpMethod.Post)
                {
                    writtenMapper = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    return Json(HttpStatusCode.Created, "{}");
                }
                return Json(HttpStatusCode.OK, writtenMapper is null ? "[]" : "[" + writtenMapper.ToJsonString() + "]");
            }
            if (path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Mappers = [new RealmReconcileMapper("Session department", "department", "unused", SessionNote: "department")]
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal("oidc-usersessionmodel-note-mapper", writtenMapper?["protocolMapper"]?.GetValue<string>());
        Assert.Equal("department", writtenMapper?["config"]?["user.session.note"]?.GetValue<string>());
        Assert.Equal("department", writtenMapper?["config"]?["claim.name"]?.GetValue<string>());
        Assert.Null(writtenMapper?["config"]?["claim.value"]);
    }

    [Fact]
    public async Task ExistingManagedMapperCanTransitionSourceKindAndRemoveOldConfig()
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var mapper = JsonNode.Parse("""[{"id":"mapper-id","name":"department","protocol":"openid-connect","protocolMapper":"oidc-hardcoded-claim-mapper","config":{"appsurface.issue888.managed":"generic-feasibility","claim.name":"department","claim.value":"old-value"}}]""")!.AsArray();
        JsonNode? updated = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == mapperPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, mapper.ToJsonString());
            if (path == mapperPath + "/mapper-id" && request.Method == HttpMethod.Put)
            {
                updated = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                mapper[0] = updated!.DeepClone();
                return Json(HttpStatusCode.NoContent, "");
            }
            if (path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with { Mappers = [new RealmReconcileMapper("department", "department", "unused", UserAttribute: "department")] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal("mapper-id", updated?["id"]?.GetValue<string>());
        Assert.Equal("oidc-usermodel-attribute-mapper", updated?["protocolMapper"]?.GetValue<string>());
        Assert.Equal("department", updated?["config"]?["user.attribute"]?.GetValue<string>());
        Assert.Null(updated?["config"]?["claim.value"]);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == mapperPath + "/mapper-id");
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true, "Ready", 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "Ready", 1)]
    [InlineData(HttpStatusCode.NoContent, false, "ReadbackMismatch", 0)]
    public async Task ConfidentialClientSecretUpdateRequiresReadback(
        HttpStatusCode acknowledgement, bool updatedSecretIsVisible, string expectedCode, int expectedUnknownWrites)
    {
        var existing = JsonNode.Parse(ClientJson())!;
        var secret = "old-secret";
        JsonNode? update = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + existing.ToJsonString() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/client-secret") return Json(HttpStatusCode.OK, "{\"value\":\"" + secret + "\"}");
            if (path == "/admin/realms/sample/clients/client-internal" && request.Method == HttpMethod.Put)
            {
                update = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                if (updatedSecretIsVisible) secret = update!["secret"]!.GetValue<string>();
                existing = update!.DeepClone();
                existing["id"] = "client-internal";
                return Json(acknowledgement, "");
            }
            if (path is "/admin/realms/sample/clients/client-internal/protocol-mappers/models" or "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var credentials = Credentials() with { ClientSecret = "correct-secret" };

        var result = await RealmReconciler.ReconcileAsync(client, Desired() with { Confidential = true }, credentials, CancellationToken.None);

        Assert.Equal(updatedSecretIsVisible, result.Ready);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(expectedUnknownWrites, result.UnknownWrites);
        Assert.Equal("correct-secret", update?["secret"]?.GetValue<string>());
        Assert.Equal(updatedSecretIsVisible ? "correct-secret" : "old-secret", secret);
        Assert.DoesNotContain("correct-secret", result.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == "/admin/realms/sample/clients/client-internal");
    }

    [Fact]
    public async Task CancellationAfterUncertainRealmWritePreservesUnknownOutcomeForInspection()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.NotFound, "{}");
            if (path == "/admin/realms" && request.Method == HttpMethod.Post)
            {
                cancellation.Cancel();
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), cancellation.Token);

        Assert.False(result.Ready);
        Assert.Equal("UnknownWriteOutcome", result.Code);
        Assert.Equal("Unknown", result.Effect);
        Assert.Equal("InspectBeforeRetry", result.Recovery);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal(0, result.Retries);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/admin/realms");
    }

    [Fact]
    public async Task DisabledOidcBrokerWriteUsesSyntheticConfigurationAndPassesExactReadback()
    {
        JsonNode? writtenBroker = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == "/admin/realms/sample/identity-provider/instances")
            {
                if (request.Method == HttpMethod.Post)
                {
                    writtenBroker = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    return Json(HttpStatusCode.Created, "{}");
                }
                return Json(HttpStatusCode.OK, writtenBroker is null ? "[]" : "[" + writtenBroker.ToJsonString() + "]");
            }
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Brokers = [new RealmReconcileBroker("disabled-preview", false)]
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal("oidc", writtenBroker?["providerId"]?.GetValue<string>());
        Assert.Equal("disabled-preview", writtenBroker?["alias"]?.GetValue<string>());
        Assert.Equal("synthetic-disabled-provider", writtenBroker?["config"]?["clientId"]?.GetValue<string>());
        Assert.Equal("https://provider.invalid/authorize", writtenBroker?["config"]?["authorizationUrl"]?.GetValue<string>());
        Assert.Equal("https://provider.invalid/token", writtenBroker?["config"]?["tokenUrl"]?.GetValue<string>());
        Assert.Equal("https://provider.invalid/userinfo", writtenBroker?["config"]?["userInfoUrl"]?.GetValue<string>());
        Assert.Equal("IMPORT", writtenBroker?["config"]?["syncMode"]?.GetValue<string>());
        Assert.False(writtenBroker?["enabled"]?.GetValue<bool>());
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/admin/realms/sample/identity-provider/instances");
    }

    [Fact]
    public async Task ExistingRealmDriftIsUpdatedAndReadBackBeforeClientReconciliation()
    {
        const string loginTheme = "branded";
        var realmUpdated = false;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample" && request.Method == HttpMethod.Put)
            {
                var patch = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal(loginTheme, patch?["loginTheme"]?.GetValue<string>());
                realmUpdated = true;
                return Json(HttpStatusCode.NoContent, "{}");
            }
            if (path == "/admin/realms/sample")
            {
                if (!realmUpdated)
                    return Json(HttpStatusCode.OK, "{\"id\":\"realm-internal\",\"realm\":\"sample\",\"enabled\":false,\"registrationAllowed\":true,\"sslRequired\":\"none\"}");
                var observedRealm = JsonNode.Parse(RealmJson())!;
                observedRealm["loginTheme"] = loginTheme;
                return Json(HttpStatusCode.OK, observedRealm.ToJsonString());
            }
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired() with { LoginThemeName = loginTheme }, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.True(realmUpdated);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == "/admin/realms/sample");
    }

    [Fact]
    public async Task AcknowledgedClientMutationMustMatchEffectiveReadback()
    {
        const string clientPath = "/admin/realms/sample/clients/client-internal";
        var existingClient = JsonNode.Parse(ReconcileClientJson())!;
        existingClient["enabled"] = false;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients?clientId=web-app")
                return Json(HttpStatusCode.OK, "[" + existingClient.ToJsonString() + "]");
            if (path == clientPath && request.Method == HttpMethod.Put)
                return Json(HttpStatusCode.NoContent, "");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models"
                || path == "/admin/realms/sample/identity-provider/instances")
                return Json(HttpStatusCode.OK, "[]");
            return null;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ReadbackMismatch", result.Code);
        Assert.Equal("effective-readback", result.Stage);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.UnknownWrites);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == clientPath);
    }

    [Fact]
    public async Task ExistingClientWithoutStableInternalIdFailsBeforeMutation()
    {
        var clientWithoutId = JsonNode.Parse(ReconcileClientJson())!.AsObject();
        clientWithoutId.Remove("id");
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) => request.RequestUri!.PathAndQuery == "/admin/realms/sample/clients?clientId=web-app"
                ? Json(HttpStatusCode.OK, "[" + clientWithoutId.ToJsonString() + "]")
                : null);
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ReadbackMismatch", result.Code);
        Assert.Equal("client-reconcile", result.Stage);
        Assert.Equal("None", result.Effect);
        Assert.Equal(0, result.Writes);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete
            || request.Method == HttpMethod.Post && request.PathAndQuery != "/realms/master/protocol/openid-connect/token");
    }

    [Fact]
    public async Task MissingClientAttributesAreRebuiltAndVerified()
    {
        const string clientPath = "/admin/realms/sample/clients/client-internal";
        var clientWithoutAttributes = JsonNode.Parse(ReconcileClientJson())!.AsObject();
        clientWithoutAttributes.Remove("attributes");
        var updated = false;
        JsonObject? clientPatch = null;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients?clientId=web-app")
                return Json(HttpStatusCode.OK, "[" + (updated ? ReconcileClientJson() : clientWithoutAttributes.ToJsonString()) + "]");
            if (path == clientPath && request.Method == HttpMethod.Put)
            {
                clientPatch = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
                updated = true;
                return Json(HttpStatusCode.NoContent, "");
            }
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models"
                || path == "/admin/realms/sample/identity-provider/instances")
                return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration")
                return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html")
                };
            return null;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal(1, result.Writes);
        Assert.Equal("https://localhost:8443/signed-out", clientPatch?["attributes"]?["post.logout.redirect.uris"]?.GetValue<string>());
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Put && request.PathAndQuery == clientPath));
    }

    [Theory]
    [InlineData("realm")]
    [InlineData("mapper")]
    [InlineData("broker")]
    public async Task AcknowledgedManagedWritesMustMatchEffectiveReadback(string target)
    {
        const string realmPath = "/admin/realms/sample";
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var driftedRealm = JsonNode.Parse(RealmJson())!;
        driftedRealm["registrationAllowed"] = true;
        const string driftedMapper = """[{"id":"mapper-id","name":"department","protocol":"openid-connect","protocolMapper":"oidc-hardcoded-claim-mapper","config":{"appsurface.issue888.managed":"generic-feasibility","claim.name":"department","claim.value":"old","jsonType.label":"String","id.token.claim":"true","access.token.claim":"true","userinfo.token.claim":"true"}}]""";
        const string driftedBroker = """[{"alias":"preview","providerId":"oidc","enabled":true,"trustEmail":false,"storeToken":false,"linkOnly":false,"config":{"appsurface.issue888.managed":"generic-feasibility","clientId":"synthetic-disabled-provider","authorizationUrl":"https://provider.invalid/authorize","tokenUrl":"https://provider.invalid/token","userInfoUrl":"https://provider.invalid/userinfo","syncMode":"IMPORT"}}]""";
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (target == "realm" && path == realmPath && request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, driftedRealm.ToJsonString());
                if (target == "realm" && path == realmPath && request.Method == HttpMethod.Put)
                    return Json(HttpStatusCode.NoContent, "");
                if (path == mapperPath && request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, target == "mapper" ? driftedMapper : "[]");
                if (target == "mapper" && path == mapperPath + "/mapper-id" && request.Method == HttpMethod.Put)
                    return Json(HttpStatusCode.NoContent, "");
                if (path == brokerPath && request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, target == "broker" ? driftedBroker : "[]");
                if (target == "broker" && path == brokerPath + "/preview" && request.Method == HttpMethod.Put)
                    return Json(HttpStatusCode.NoContent, "");
                return null;
            });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Mappers = target == "mapper" ? [new RealmReconcileMapper("department", "department", "sales")] : [],
            Brokers = target == "broker" ? [new RealmReconcileBroker("preview", false)] : []
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ReadbackMismatch", result.Code);
        Assert.Equal("effective-readback", result.Stage);
        Assert.Equal("Observed", result.Effect);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.UnknownWrites);
        var expectedWritePath = target switch
        {
            "realm" => realmPath,
            "mapper" => mapperPath + "/mapper-id",
            "broker" => brokerPath + "/preview",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == expectedWritePath);
    }

    [Fact]
    public async Task CommittedGoogleBrokerWriteWithMaskedSecretFailsUnknownWithoutRetryingPost()
    {
        JsonNode? persistedBroker = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == "/admin/realms/sample/identity-provider/instances")
            {
                if (request.Method == HttpMethod.Post)
                {
                    var stored = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                    stored["config"]!["clientSecret"] = "********";
                    stored["trustEmail"] = false;
                    stored["storeToken"] = false;
                    stored["linkOnly"] = false;
                    persistedBroker = stored;
                    return Json(HttpStatusCode.ServiceUnavailable, "{}");
                }
                return Json(HttpStatusCode.OK, persistedBroker is null ? "[]" : "[" + persistedBroker.ToJsonString() + "]");
            }
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with { Brokers = [new RealmReconcileBroker("google", true, "google", "google-client")] };
        var credentials = Credentials() with { BrokerSecrets = ImmutableDictionary<string, string>.Empty.Add("google", "google-client-secret") };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("UnknownWriteOutcome", result.Code);
        Assert.Equal("Unknown", result.Effect);
        Assert.Equal("InspectBeforeRetry", result.Recovery);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal("google", persistedBroker?["providerId"]?.GetValue<string>());
        Assert.Equal("openid profile email", persistedBroker?["config"]?["defaultScope"]?.GetValue<string>());
        Assert.Equal(1, handler.Requests.Count(x => x.Method == HttpMethod.Post && x.PathAndQuery == "/admin/realms/sample/identity-provider/instances"));
    }

    [Fact]
    public async Task GoogleBrokerSecretsStayBoundToTheirExactAliases()
    {
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        const string primarySecret = "fixture-primary-google-secret";
        const string secondarySecret = "fixture-secondary-google-secret";
        var persisted = new JsonArray();
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == brokerPath && request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, persisted.ToJsonString());
                if (path == brokerPath && request.Method == HttpMethod.Post)
                {
                    persisted.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
                    return Json(HttpStatusCode.NoContent, "");
                }
                if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models")
                    return Json(HttpStatusCode.OK, "[]");
                if (path == "/realms/sample/.well-known/openid-configuration")
                    return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
                if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html")
                    };
                return null;
            });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Brokers =
            [
                new RealmReconcileBroker("primary", true, "google", "primary-client"),
                new RealmReconcileBroker("secondary", true, "google", "secondary-client")
            ]
        };
        var credentials = Credentials() with
        {
            BrokerSecrets = ImmutableDictionary<string, string>.Empty
                .Add("primary", primarySecret)
                .Add("secondary", secondarySecret)
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal(2, result.Writes);
        Assert.Equal(primarySecret, persisted.Single(node => node?["alias"]?.GetValue<string>() == "primary")?["config"]?["clientSecret"]?.GetValue<string>());
        Assert.Equal(secondarySecret, persisted.Single(node => node?["alias"]?.GetValue<string>() == "secondary")?["config"]?["clientSecret"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExistingGoogleBrokerUncertainUpdateReadsBackSecretAndRetainsUnownedConfiguration()
    {
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var broker = JsonNode.Parse("""[{"alias":"google","providerId":"google","enabled":true,"trustEmail":true,"storeToken":true,"linkOnly":true,"displayName":"Existing label","config":{"appsurface.issue888.managed":"generic-feasibility","clientId":"old-client","clientSecret":"old-secret","defaultScope":"openid profile email","syncMode":"IMPORT","consumer.setting":"keep-me","authorizationUrl":"https://old.example/authorize"}}]""")!.AsArray();
        JsonNode? update = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == brokerPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, broker.ToJsonString());
            if (path == brokerPath + "/google" && request.Method == HttpMethod.Put)
            {
                update = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var stored = update!.DeepClone();
                stored["config"]!["clientSecret"] = "google-client-secret";
                broker[0] = stored;
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with { Brokers = [new RealmReconcileBroker("google", true, "google", "google-client")] };
        var credentials = Credentials() with { BrokerSecrets = ImmutableDictionary<string, string>.Empty.Add("google", "google-client-secret") };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal(0, result.Retries);
        Assert.Single(handler.Requests, x => x.Method == HttpMethod.Put && x.PathAndQuery == brokerPath + "/google");
        Assert.Equal("Existing label", update?["displayName"]?.GetValue<string>());
        Assert.Equal("keep-me", update?["config"]?["consumer.setting"]?.GetValue<string>());
        Assert.Null(update?["config"]?["authorizationUrl"]);
        Assert.Equal("google-client", update?["config"]?["clientId"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("foreign", "UnmanagedCollision")]
    [InlineData("owned", "Ready")]
    public async Task ExistingManagedMapperUpdateRejectsForeignNameCollisionAndRetainsOtherConfig(string ownership, string expectedCode)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var mapper = JsonNode.Parse("""[{"id":"mapper-id","name":"department","protocol":"openid-connect","protocolMapper":"oidc-hardcoded-claim-mapper","config":{"claim.name":"department","claim.value":"old","consumer.setting":"keep-me"}}]""")!.AsArray();
        mapper[0]!["config"]!["appsurface.issue888.managed"] = ownership == "foreign" ? "consumer" : "generic-feasibility";
        JsonNode? update = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == mapperPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, mapper.ToJsonString());
            if (path == mapperPath + "/mapper-id" && request.Method == HttpMethod.Put)
            {
                update = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                mapper[0] = update!.DeepClone();
                return Json(HttpStatusCode.NoContent, "");
            }
            if (path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with { Mappers = [new RealmReconcileMapper("department", "department", "new", UserAttribute: "department")] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(ownership == "owned", result.Ready);
        Assert.Equal(ownership == "owned" ? 1 : 0, handler.Requests.Count(x => x.Method == HttpMethod.Put && x.PathAndQuery == mapperPath + "/mapper-id"));
        if (update is not null)
        {
            Assert.Equal("keep-me", update["config"]?["consumer.setting"]?.GetValue<string>());
            Assert.Null(update["config"]?["claim.value"]);
            Assert.Equal("department", update["config"]?["user.attribute"]?.GetValue<string>());
        }
    }

    // Protects the declared Google label and its effective readback without requiring an undeclared label.
    [Theory]
    [InlineData("Continue with Google", "Continue with Google", true)]
    [InlineData("Continue with Google", "Different label", false)]
    [InlineData(null, "Server default", true)]
    public async Task GoogleDisplayNameIsWrittenAndVerifiedWhenDeclared(string? declared, string observed, bool ready)
    {
        JsonNode? written = null;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token") return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == "/admin/realms/sample/identity-provider/instances")
            {
                if (request.Method == HttpMethod.Post)
                {
                    written = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    return Json(HttpStatusCode.Created, "{}");
                }
                if (written is null) return Json(HttpStatusCode.OK, "[]");
                var effective = written.DeepClone();
                effective["displayName"] = observed;
                effective["config"]!["clientSecret"] = "********";
                return Json(HttpStatusCode.OK, "[" + effective.ToJsonString() + "]");
            }
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return Json(HttpStatusCode.InternalServerError, "{}");
        });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Brokers = [new RealmReconcileBroker("google", true, "google", "google-client") { DisplayName = declared }]
        };
        var credentials = Credentials() with
        {
            BrokerSecrets = ImmutableDictionary<string, string>.Empty.Add("google", "synthetic-provider-secret")
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.Equal(ready, result.Ready);
        Assert.Equal(declared, written?["displayName"]?.GetValue<string>());
        if (!ready) Assert.Equal("ReadbackMismatch", result.Code);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/admin/realms/sample/identity-provider/instances");
    }

    [Theory]
    [InlineData("managed", "Ready")]
    [InlineData("foreign-mapper", "UnmanagedCollision")]
    [InlineData("foreign-broker", "UnmanagedCollision")]
    [InlineData("retained-mapper", "ReadbackMismatch")]
    [InlineData("retained-broker", "ReadbackMismatch")]
    public async Task ManagedRemovalsRequirePackageOwnershipAndDefinitiveReadback(string variant, string expectedCode)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var mapperExists = true;
        var brokerExists = true;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.OK, """{"access_token":"fixture-token"}""");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app")
                return Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == mapperPath)
            {
                var owner = variant == "foreign-mapper" ? "consumer" : "generic-feasibility";
                return Json(HttpStatusCode.OK, mapperExists
                    ? $$$"""[{"id":"mapper-id","name":"department","config":{"appsurface.issue888.managed":"{{{owner}}}"}}]"""
                    : "[]");
            }
            if (path == brokerPath)
            {
                var owner = variant == "foreign-broker" ? "consumer" : "generic-feasibility";
                return Json(HttpStatusCode.OK, brokerExists
                    ? $$$"""[{"alias":"legacy-provider","config":{"appsurface.issue888.managed":"{{{owner}}}"}}]"""
                    : "[]");
            }
            if (request.Method == HttpMethod.Delete && path == mapperPath + "/mapper-id")
            {
                mapperExists = variant == "retained-mapper";
                return Json(HttpStatusCode.NoContent, "");
            }
            if (request.Method == HttpMethod.Delete && path == brokerPath + "/legacy-provider")
            {
                brokerExists = variant == "retained-broker";
                return Json(HttpStatusCode.NoContent, "");
            }
            if (path == "/realms/sample/.well-known/openid-configuration")
                return Json(HttpStatusCode.OK, """{"issuer":"https://localhost:8443/realms/sample"}""");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""<form id="kc-form-login"></form>""") };
            throw new InvalidOperationException("Unexpected Admin API request.");
        });
        var desired = Desired() with { RemoveMappers = ["department", "absent-mapper"], RemoveBrokers = ["legacy-provider", "absent-broker"] };
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(variant == "managed", result.Ready);
        Assert.Equal(variant != "foreign-mapper",
            handler.Requests.Any(request => request.Method == HttpMethod.Delete && request.PathAndQuery == mapperPath + "/mapper-id"));
        Assert.Equal(variant is not ("foreign-mapper" or "foreign-broker"),
            handler.Requests.Any(request => request.Method == HttpMethod.Delete && request.PathAndQuery == brokerPath + "/legacy-provider"));
        Assert.DoesNotContain(handler.Requests,
            request => request.Method == HttpMethod.Delete && request.PathAndQuery.Contains("absent", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfidentialClientBecomesPublicOnlyAfterExplicitDowngrade(bool allow)
    {
        var existing = JsonNode.Parse(ClientJson())!.AsObject();
        existing["attributes"]!["consumer.managed"] = "retained";
        var changed = false;
        var handler = new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.OK, """{"access_token":"fixture-token"}""");
            if (path == "/admin/realms/sample") return Json(HttpStatusCode.OK, RealmJson());
            if (path == "/admin/realms/sample/clients?clientId=web-app")
                return Json(HttpStatusCode.OK, "[" + existing.ToJsonString() + "]");
            if (path == "/admin/realms/sample/clients/client-internal" && request.Method == HttpMethod.Put)
            {
                var record = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
                Assert.True(record["publicClient"]!.GetValue<bool>());
                Assert.False(record.ContainsKey("secret"));
                Assert.Equal("retained", record["attributes"]!["consumer.managed"]!.GetValue<string>());
                existing = record;
                existing["id"] = "client-internal";
                changed = true;
                return Json(HttpStatusCode.NoContent, "");
            }
            if (path is "/admin/realms/sample/clients/client-internal/protocol-mappers/models" or "/admin/realms/sample/identity-provider/instances")
                return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration")
                return Json(HttpStatusCode.OK, """{"issuer":"https://localhost:8443/realms/sample"}""");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""<form id="kc-form-login"></form>""") };
            throw new InvalidOperationException("Unexpected Admin API request.");
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired() with { AllowPublicDowngrade = allow }, Credentials(), CancellationToken.None);

        Assert.Equal(allow, result.Ready);
        Assert.Equal(allow, changed);
        Assert.Equal(allow ? "Ready" : "DowngradeDenied", result.Code);
    }

    [Fact]
    public async Task ExactOwnedMapperVariantsAndDisabledBrokerAreAlreadyConverged()
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var mapperRecords = """
            [
              {"id":"hardcoded-id","name":"department-copy","protocol":"openid-connect","protocolMapper":"oidc-hardcoded-claim-mapper","config":{"appsurface.issue888.managed":"generic-feasibility","claim.name":"department","claim.value":"sales","jsonType.label":"String","id.token.claim":"true","access.token.claim":"true","userinfo.token.claim":"true"}},
              {"id":"attribute-id","name":"department-attribute","protocol":"openid-connect","protocolMapper":"oidc-usermodel-attribute-mapper","config":{"appsurface.issue888.managed":"generic-feasibility","claim.name":"department","user.attribute":"department","jsonType.label":"String","id.token.claim":"true","access.token.claim":"true","userinfo.token.claim":"true"}},
              {"id":"note-id","name":"department-note","protocol":"openid-connect","protocolMapper":"oidc-usersessionmodel-note-mapper","config":{"appsurface.issue888.managed":"generic-feasibility","claim.name":"department","user.session.note":"department","jsonType.label":"String","id.token.claim":"true","access.token.claim":"true","userinfo.token.claim":"true"}}
            ]
            """;
        var brokerRecords = """
            [{"alias":"preview","providerId":"oidc","enabled":false,"trustEmail":false,"storeToken":false,"linkOnly":false,"config":{"appsurface.issue888.managed":"generic-feasibility","clientId":"synthetic-disabled-provider","authorizationUrl":"https://provider.invalid/authorize","tokenUrl":"https://provider.invalid/token","userInfoUrl":"https://provider.invalid/userinfo","syncMode":"IMPORT"}}]
            """;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == mapperPath) return Json(HttpStatusCode.OK, mapperRecords);
            if (path == brokerPath) return Json(HttpStatusCode.OK, brokerRecords);
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Mappers =
            [
                new RealmReconcileMapper("department-copy", "department", "sales"),
                new RealmReconcileMapper("department-attribute", "department", "unused", UserAttribute: "department"),
                new RealmReconcileMapper("department-note", "department", "unused", SessionNote: "department")
            ],
            Brokers = [new RealmReconcileBroker("preview", false)]
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal(0, result.Writes);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete
            || request.Method == HttpMethod.Post && !request.PathAndQuery.EndsWith("/token", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("client", "client-reconcile")]
    [InlineData("mapper", "mapper-reconcile")]
    [InlineData("broker", "broker-reconcile")]
    public async Task DuplicateManagedIdentityIsRejectedBeforeAnyMutation(string duplicateKind, string expectedStage)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients?clientId=web-app")
                return duplicateKind == "client"
                    ? Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "," + ReconcileClientJson() + "]")
                    : Json(HttpStatusCode.OK, "[" + ReconcileClientJson() + "]");
            if (path == mapperPath)
                return duplicateKind == "mapper"
                    ? Json(HttpStatusCode.OK, """[{"id":"one","name":"department","config":{"appsurface.issue888.managed":"generic-feasibility"}},{"id":"two","name":"department","config":{"appsurface.issue888.managed":"generic-feasibility"}}]""")
                    : Json(HttpStatusCode.OK, "[]");
            if (path == brokerPath)
                return duplicateKind == "broker"
                    ? Json(HttpStatusCode.OK, """[{"alias":"preview","config":{"appsurface.issue888.managed":"generic-feasibility"}},{"alias":"preview","config":{"appsurface.issue888.managed":"generic-feasibility"}}]""")
                    : Json(HttpStatusCode.OK, "[]");
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with
        {
            Mappers = duplicateKind == "mapper" ? [new RealmReconcileMapper("department", "department", "sales")] : [],
            Brokers = duplicateKind == "broker" ? [new RealmReconcileBroker("preview", false)] : []
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("AmbiguousIdentity", result.Code);
        Assert.Equal(expectedStage, result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete
            || request.Method == HttpMethod.Post && !request.PathAndQuery.EndsWith("/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UncertainOwnedMapperAndBrokerRemovalsAreConfirmedWithoutRepeatingDeletes()
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var mapperExists = true;
        var brokerExists = true;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == mapperPath && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, mapperExists ? """[{"id":"mapper-id","name":"old-mapper","config":{"appsurface.issue888.managed":"generic-feasibility"}}]""" : "[]");
            if (path == brokerPath && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, brokerExists ? """[{"alias":"old-broker","config":{"appsurface.issue888.managed":"generic-feasibility"}}]""" : "[]");
            if (path == mapperPath + "/mapper-id" && request.Method == HttpMethod.Delete)
            {
                mapperExists = false;
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (path == brokerPath + "/old-broker" && request.Method == HttpMethod.Delete)
            {
                brokerExists = false;
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with { RemoveMappers = ["old-mapper"], RemoveBrokers = ["old-broker"] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal(2, result.Writes);
        Assert.Equal(2, result.UnknownWrites);
        Assert.Equal(0, result.Retries);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete && request.PathAndQuery == mapperPath + "/mapper-id");
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete && request.PathAndQuery == brokerPath + "/old-broker");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AuthorizationDenied")]
    [InlineData(HttpStatusCode.Forbidden, "AuthorizationDenied")]
    [InlineData(HttpStatusCode.BadRequest, "WriteRejected")]
    public async Task MapperWriteRejectionIsDefinitiveAndDoesNotAttemptReadback(HttpStatusCode status, string expectedCode)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == mapperPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, "[]");
            if (path == mapperPath && request.Method == HttpMethod.Post) return Json(status, "{}");
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with { Mappers = [new RealmReconcileMapper("department", "department", "sales")] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal("mapper-reconcile", result.Stage);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.UnknownWrites);
        Assert.Single(handler.Requests, request => request.PathAndQuery == mapperPath && request.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", false)]
    [InlineData(HttpStatusCode.Forbidden, "{}", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", true)]
    [InlineData(HttpStatusCode.OK, "null", false)]
    public async Task FailedMapperReadbackAfterUncertainWritePreservesUnknownOutcome(
        HttpStatusCode status, string body, bool useFutureRetryDate)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var writeSent = false;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == mapperPath && request.Method == HttpMethod.Get)
            {
                if (!writeSent) return Json(HttpStatusCode.OK, "[]");
                var response = Json(status, body);
                if (useFutureRetryDate)
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(1));
                return response;
            }
            if (path == mapperPath && request.Method == HttpMethod.Post)
            {
                writeSent = true;
                return Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with { Mappers = [new RealmReconcileMapper("department", "department", "sales")] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("UnknownWriteOutcome", result.Code);
        Assert.Equal("Unknown", result.Effect);
        Assert.Equal("InspectBeforeRetry", result.Recovery);
        Assert.Equal("mapper-reconcile", result.Stage);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal(0, result.Retries);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == mapperPath);
    }

    [Fact]
    public async Task UnexpectedWriteFailurePreservesUnknownEffectForOwnerInspection()
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == mapperPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, "[]");
            if (path == mapperPath && request.Method == HttpMethod.Post)
                throw new InvalidOperationException("The scripted transport failed after the write began.");
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with { Mappers = [new RealmReconcileMapper("department", "department", "sales")] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("Unexpected", result.Code);
        Assert.Equal("Unknown", result.Effect);
        Assert.Equal("OwnerAction", result.Recovery);
        Assert.Equal("mapper-reconcile", result.Stage);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.Retries);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == mapperPath);
    }

    [Fact]
    public async Task AcknowledgedClientCreateWithoutVisibleIdentityFailsClosed()
    {
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[]");
            if (path == "/admin/realms/sample/clients" && request.Method == HttpMethod.Post)
                return Json(HttpStatusCode.Created, "{}");
            return null;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ReadbackMismatch", result.Code);
        Assert.Equal("client-reconcile", result.Stage);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.UnknownWrites);
        Assert.Equal(2, handler.Requests.Count(request => request.PathAndQuery == "/admin/realms/sample/clients?clientId=web-app"));
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/admin/realms/sample/clients");
    }

    [Fact]
    public async Task MissingMapperCollectionReadbackFailsClosedBeforeAnyWrite()
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == mapperPath) return Json(HttpStatusCode.NotFound, "{}");
            return null;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client,
            Desired() with { Mappers = [new RealmReconcileMapper("department", "department", "sales")] },
            Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ReadbackRejected", result.Code);
        Assert.Equal("mapper-reconcile", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.Equal(0, result.Retries);
    }

    [Fact]
    public async Task LostMapperCreateResponseIsConfirmedFromReadbackWithoutRepeatingPost()
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var persisted = new JsonArray();
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == mapperPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, persisted.ToJsonString());
            if (path == mapperPath && request.Method == HttpMethod.Post)
            {
                var record = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                record["id"] = "created-mapper";
                persisted.Add(record);
                throw new HttpRequestException("The response was lost after the server committed the mapper.");
            }
            if (path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with { Mappers = [new RealmReconcileMapper("department", "department", "sales")] };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal(0, result.Retries);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == mapperPath);
        Assert.Equal("created-mapper", persisted[0]?["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExistingGoogleBrokerWithMaskedMatchingSecretIsSafelyReassertedAndReadBack()
    {
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var broker = JsonNode.Parse("""[{"alias":"google","providerId":"google","enabled":true,"trustEmail":false,"storeToken":false,"linkOnly":false,"displayName":"Continue with Google","config":{"appsurface.issue888.managed":"generic-feasibility","clientId":"google-client","clientSecret":"********","defaultScope":"openid profile email","syncMode":"IMPORT"}}]""")!.AsArray();
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == brokerPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, broker.ToJsonString());
            if (path == brokerPath + "/google" && request.Method == HttpMethod.Put)
            {
                var update = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
                Assert.Equal("Continue with Google", update["displayName"]?.GetValue<string>());
                broker[0] = update.DeepClone();
                broker[0]!["config"]!["clientSecret"] = "********";
                return Json(HttpStatusCode.NoContent, "");
            }
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return null;
        });
        using var client = Client(handler);
        var desired = Desired() with { Brokers = [new RealmReconcileBroker("google", true, "google", "google-client")] };
        var credentials = Credentials() with { BrokerSecrets = ImmutableDictionary<string, string>.Empty.Add("google", "google-client-secret") };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal("ConfigurationObservedProviderUnverified", result.ProviderAssurance);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == brokerPath + "/google");
        Assert.DoesNotContain("google-client-secret", broker.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unapproved-callback", false)]
    [InlineData("unapproved-web-origin", false)]
    [InlineData("approved-callback-and-web-origin", true)]
    public async Task PublicCallbacksAndWebOriginsRequireTheirExactApprovedOrigin(string scenario, bool expectedReady)
    {
        const string issuer = "https://login.example/realms/sample";
        var desired = Desired() with
        {
            Authority = issuer,
            TransportOrigin = "https://localhost:8443/",
            ApprovedHttpsOrigins = scenario == "approved-callback-and-web-origin"
                ? ["https://login.example", "https://app.example"] : ["https://login.example"],
            RedirectUris = scenario == "unapproved-callback"
                ? ["https://app.example/callback"] : ["https://login.example/callback"],
            PostLogoutRedirectUris = ["https://login.example/signed-out"],
            WebOrigins = scenario == "unapproved-web-origin"
                ? ["https://app.example"] : ["https://login.example"]
        };
        var observedClient = JsonNode.Parse(ReconcileClientJson())!;
        observedClient["redirectUris"] = new JsonArray(desired.RedirectUris.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        observedClient["webOrigins"] = new JsonArray(desired.WebOrigins.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        observedClient["attributes"]!["post.logout.redirect.uris"] = string.Join("##", desired.PostLogoutRedirectUris);
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients?clientId=web-app") return Json(HttpStatusCode.OK, "[" + observedClient.ToJsonString() + "]");
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models" || path == "/admin/realms/sample/identity-provider/instances")
                return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"" + issuer + "\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
            return null;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.Equal(expectedReady, result.Ready);
        Assert.Equal(expectedReady ? "Ready" : "InvalidConfiguration", result.Code);
        if (expectedReady)
            Assert.Contains(handler.Requests, request => request.PathAndQuery.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal));
        else
            Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("consumer")]
    [InlineData(null)]
    public async Task DesiredBrokerAliasOwnedByAnotherActorIsNeverUpdated(string? owner)
    {
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var currentBroker = JsonNode.Parse("""[{"alias":"preview","providerId":"oidc","config":{}}]""")!.AsArray();
        if (owner is not null) currentBroker[0]!["config"]!["appsurface.issue888.managed"] = owner;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
            if (path == brokerPath) return Json(HttpStatusCode.OK, currentBroker.ToJsonString());
            return null;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client,
            Desired() with { Brokers = [new RealmReconcileBroker("preview", false)] }, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("UnmanagedCollision", result.Code);
        Assert.Equal("broker-reconcile", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete
            || request.Method == HttpMethod.Post && !request.PathAndQuery.EndsWith("/token", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("metadata-unavailable", "IssuerMismatch", 0)]
    [InlineData("issuer-mismatch", "IssuerMismatch", 0)]
    [InlineData("challenge-unavailable", "ChallengeRejected", 1)]
    [InlineData("challenge-without-login-form", "ChallengeRejected", 1)]
    public async Task ReadinessChallengeRequiresSuccessfulMetadataAndLoginForm(string scenario, string expectedCode, int expectedChallengeRequests)
    {
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models" || path == "/admin/realms/sample/identity-provider/instances")
                return Json(HttpStatusCode.OK, "[]");
            if (path == "/realms/sample/.well-known/openid-configuration")
                return scenario == "metadata-unavailable"
                    ? Json(HttpStatusCode.ServiceUnavailable, "{}")
                    : Json(HttpStatusCode.OK, scenario == "issuer-mismatch"
                        ? "{\"issuer\":\"https://other.example/realms/sample\"}"
                        : "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
            if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                return scenario == "challenge-unavailable"
                    ? Json(HttpStatusCode.Forbidden, "{}")
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<form id=\"kc-form-other\"></form>", Encoding.UTF8, "text/html")
                    };
            return null;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal("metadata-challenge", result.Stage);
        Assert.Equal(expectedChallengeRequests, handler.Requests.Count(request => request.PathAndQuery.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AuthenticationBodyLimitIsEnforcedWhenContentLengthIsUnknown()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.PathAndQuery != "/realms/master/protocol/openid-connect/token")
                return Json(HttpStatusCode.InternalServerError, "{}");
            var response = Json(HttpStatusCode.OK, "{\"access_token\":\"" + new string('x', 1024 * 1024) + "\"}");
            response.Content!.Headers.ContentLength = null;
            return response;
        });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ResponseLimit", result.Code);
        Assert.Equal("administrator-authentication", result.Stage);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("administrative-readback", "realm-reconcile")]
    [InlineData("login-challenge", "metadata-challenge")]
    public async Task ResponseBodyLimitsAreEnforcedWhenContentLengthIsUnknown(string scenario, string expectedStage)
    {
        const string realmPath = "/admin/realms/sample";
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (scenario == "administrative-readback" && path == realmPath)
                {
                    var response = Json(HttpStatusCode.OK, new string('x', 1024 * 1024 + 8192));
                    response.Content!.Headers.ContentLength = null;
                    return response;
                }
                if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models"
                    || path == "/admin/realms/sample/identity-provider/instances")
                    return Json(HttpStatusCode.OK, "[]");
                if (path == "/realms/sample/.well-known/openid-configuration")
                    return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
                if (scenario == "login-challenge" && path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(new string('x', 16 * 1024 + 8192), Encoding.UTF8, "text/html")
                    };
                    response.Content.Headers.ContentLength = null;
                    return response;
                }
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ResponseLimit", result.Code);
        Assert.Equal(expectedStage, result.Stage);
        Assert.Equal(0, result.Writes);
        if (scenario == "administrative-readback")
            Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete);
        else
            Assert.Single(handler.Requests, request => request.PathAndQuery.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("mapper")]
    [InlineData("broker")]
    public async Task UnmarkedManagedResourcesAreNeverAdoptedForRemoval(string target)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == mapperPath)
                    return Json(HttpStatusCode.OK, target == "mapper"
                        ? """[{"id":"mapper-id","name":"legacy","config":{}}]"""
                        : "[]");
                if (path == brokerPath)
                    return Json(HttpStatusCode.OK, target == "broker"
                        ? """[{"alias":"legacy","config":{}}]"""
                        : "[]");
                return null;
            });
        using var client = Client(handler);
        var desired = Desired() with
        {
            RemoveMappers = target == "mapper" ? ["legacy"] : [],
            RemoveBrokers = target == "broker" ? ["legacy"] : []
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("UnmanagedCollision", result.Code);
        Assert.Equal(target == "mapper" ? "mapper-reconcile" : "broker-reconcile", result.Stage);
        Assert.Equal(0, result.Writes);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("mapper")]
    [InlineData("broker")]
    public async Task UncertainRemovalWithUnavailableCollectionReadbackStaysUnknown(string target)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        var deleteSent = false;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == mapperPath && request.Method == HttpMethod.Get)
                    return deleteSent && target == "mapper"
                        ? Json(HttpStatusCode.NotFound, "{}")
                        : Json(HttpStatusCode.OK, target == "mapper"
                            ? """[{"id":"mapper-id","name":"legacy","config":{"appsurface.issue888.managed":"generic-feasibility"}}]"""
                            : "[]");
                if (path == brokerPath && request.Method == HttpMethod.Get)
                    return deleteSent && target == "broker"
                        ? Json(HttpStatusCode.NotFound, "{}")
                        : Json(HttpStatusCode.OK, target == "broker"
                            ? """[{"alias":"legacy","config":{"appsurface.issue888.managed":"generic-feasibility"}}]"""
                            : "[]");
                if (request.Method == HttpMethod.Delete
                    && (path == mapperPath + "/mapper-id" || path == brokerPath + "/legacy"))
                {
                    deleteSent = true;
                    return Json(HttpStatusCode.ServiceUnavailable, "{}");
                }
                return null;
            });
        using var client = Client(handler);
        var desired = Desired() with
        {
            RemoveMappers = target == "mapper" ? ["legacy"] : [],
            RemoveBrokers = target == "broker" ? ["legacy"] : []
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("UnknownWriteOutcome", result.Code);
        Assert.Equal(target == "mapper" ? "mapper-reconcile" : "broker-reconcile", result.Stage);
        Assert.Equal("Unknown", result.Effect);
        Assert.Equal("InspectBeforeRetry", result.Recovery);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("mismatch")]
    public async Task UncertainClientCreateNeedsMatchingReadbackBeforeRetry(string observedState)
    {
        var clientReads = 0;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/admin/realms/sample/clients?clientId=web-app")
                {
                    clientReads++;
                    if (clientReads == 1 || observedState == "absent") return Json(HttpStatusCode.OK, "[]");
                    var wrong = JsonNode.Parse(ReconcileClientJson())!;
                    wrong["enabled"] = false;
                    return Json(HttpStatusCode.OK, "[" + wrong.ToJsonString() + "]");
                }
                if (path == "/admin/realms/sample/clients" && request.Method == HttpMethod.Post)
                {
                    var response = Json(HttpStatusCode.ServiceUnavailable, "{}");
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return response;
                }
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("RetryBudgetRejected", result.Code);
        Assert.Equal("client-reconcile", result.Stage);
        Assert.Equal("Observed", result.Effect);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Equal(0, result.Retries);
        Assert.Equal(2, clientReads);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == "/admin/realms/sample/clients");
    }

    [Theory]
    [InlineData("missing-direct-grants")]
    [InlineData("enabled-service-accounts")]
    [InlineData("missing-redirect-uris")]
    [InlineData("missing-web-origins")]
    [InlineData("wrong-logout-uri")]
    public async Task AcknowledgedClientMustRetainExplicitGrantAndOriginReadback(string mismatch)
    {
        const string clientQuery = "/admin/realms/sample/clients?clientId=web-app";
        const string clientPath = "/admin/realms/sample/clients/client-internal";
        var effectiveClient = JsonNode.Parse(ReconcileClientJson())!;
        switch (mismatch)
        {
            case "missing-direct-grants": effectiveClient.AsObject().Remove("directAccessGrantsEnabled"); break;
            case "enabled-service-accounts": effectiveClient["serviceAccountsEnabled"] = true; break;
            case "missing-redirect-uris": effectiveClient.AsObject().Remove("redirectUris"); break;
            case "missing-web-origins": effectiveClient.AsObject().Remove("webOrigins"); break;
            case "wrong-logout-uri": effectiveClient["attributes"]!["post.logout.redirect.uris"] = "https://localhost:8443/other"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mismatch));
        }
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == clientQuery) return Json(HttpStatusCode.OK, "[" + effectiveClient.ToJsonString() + "]");
                if (path == clientPath && request.Method == HttpMethod.Put) return Json(HttpStatusCode.NoContent, "");
                if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models"
                    || path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ReadbackMismatch", result.Code);
        Assert.Equal("effective-readback", result.Stage);
        Assert.Equal("Observed", result.Effect);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.UnknownWrites);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == clientPath);
    }

    [Theory]
    [InlineData("unexpected-user-attribute")]
    [InlineData("wrong-user-attribute")]
    [InlineData("wrong-session-note")]
    [InlineData("missing-exposure-flag")]
    public async Task EffectiveMapperReadbackMustMatchOptionalSourceFields(string mismatch)
    {
        const string mapperPath = "/admin/realms/sample/clients/client-internal/protocol-mappers/models";
        var mapper = JsonNode.Parse("""[{"id":"mapper-id","name":"department","protocol":"openid-connect","protocolMapper":"oidc-hardcoded-claim-mapper","config":{"appsurface.issue888.managed":"generic-feasibility","claim.name":"department","claim.value":"sales","jsonType.label":"String","id.token.claim":"true","access.token.claim":"true","userinfo.token.claim":"true"}}]""")!.AsArray();
        if (mismatch == "unexpected-user-attribute") mapper[0]!["config"]!["user.attribute"] = "legacy-field";
        else if (mismatch == "wrong-user-attribute")
        {
            mapper[0]!["protocolMapper"] = "oidc-usermodel-attribute-mapper";
            mapper[0]!["config"]!.AsObject().Remove("claim.value");
            mapper[0]!["config"]!["user.attribute"] = "different-attribute";
        }
        else if (mismatch == "wrong-session-note")
        {
            mapper[0]!["protocolMapper"] = "oidc-usersessionmodel-note-mapper";
            mapper[0]!["config"]!.AsObject().Remove("claim.value");
            mapper[0]!["config"]!["user.session.note"] = "different-note";
        }
        else mapper[0]!["config"]!["userinfo.token.claim"] = "false";
        var desiredMapper = mismatch switch
        {
            "wrong-user-attribute" => new RealmReconcileMapper("department", "department", "unused", UserAttribute: "department"),
            "wrong-session-note" => new RealmReconcileMapper("department", "department", "unused", SessionNote: "department"),
            _ => new RealmReconcileMapper("department", "department", "sales")
        };
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == mapperPath && request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, mapper.ToJsonString());
                if (path == mapperPath + "/mapper-id" && request.Method == HttpMethod.Put) return Json(HttpStatusCode.NoContent, "");
                if (path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
                if (path == "/realms/sample/.well-known/openid-configuration") return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
                if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired() with { Mappers = [desiredMapper] }, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("ReadbackMismatch", result.Code);
        Assert.Equal("effective-readback", result.Stage);
        Assert.Equal(1, result.Writes);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == mapperPath + "/mapper-id");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    public async Task UncertainGoogleBrokerWriteCannotBeConfirmedWithoutExactSecret(string secretState)
    {
        const string brokerPath = "/admin/realms/sample/identity-provider/instances";
        JsonNode? persisted = null;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models") return Json(HttpStatusCode.OK, "[]");
                if (path == brokerPath && request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, persisted is null ? "[]" : "[" + persisted.ToJsonString() + "]");
                if (path == brokerPath && request.Method == HttpMethod.Post)
                {
                    persisted = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    if (secretState == "missing") persisted!["config"]!.AsObject().Remove("clientSecret");
                    else persisted!["config"]!["clientSecret"] = "";
                    return Json(HttpStatusCode.ServiceUnavailable, "{}");
                }
                return null;
            });
        using var client = Client(handler);
        var desired = Desired() with { Brokers = [new RealmReconcileBroker("google", true, "google", "google-client")] };
        var credentials = Credentials() with
        {
            BrokerSecrets = ImmutableDictionary<string, string>.Empty.Add("google", "fixture-google-secret")
        };

        var result = await RealmReconciler.ReconcileAsync(client, desired, credentials, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("UnknownWriteOutcome", result.Code);
        Assert.Equal("broker-reconcile", result.Stage);
        Assert.Equal("Unknown", result.Effect);
        Assert.Equal("InspectBeforeRetry", result.Recovery);
        Assert.Equal(1, result.Writes);
        Assert.Equal(1, result.UnknownWrites);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.PathAndQuery == brokerPath);
    }

    [Fact]
    public async Task RequestTimeoutOnReadUsesTheBoundedTransientRetry()
    {
        var realmReads = 0;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Realm | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                if (request.RequestUri!.PathAndQuery == "/admin/realms/sample" && request.Method == HttpMethod.Get
                    && ++realmReads == 1)
                    return Json(HttpStatusCode.RequestTimeout, "{}");
                if (request.RequestUri!.PathAndQuery == "/admin/realms/sample/clients/client-internal/protocol-mappers/models"
                    || request.RequestUri.PathAndQuery == "/admin/realms/sample/identity-provider/instances")
                    return Json(HttpStatusCode.OK, "[]");
                if (request.RequestUri.PathAndQuery == "/realms/sample/.well-known/openid-configuration")
                    return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
                if (request.RequestUri.PathAndQuery.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Equal(1, result.Retries);
        Assert.Equal(3, realmReads); // The initial attempt, its retry, and the final effective readback.
    }

    [Fact]
    public async Task UnexpectedFailureAfterAcknowledgedWriteReportsObservedEffect()
    {
        var driftedRealm = JsonNode.Parse(RealmJson())!;
        driftedRealm["registrationAllowed"] = true;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/admin/realms/sample" && request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, driftedRealm.ToJsonString());
                if (path == "/admin/realms/sample" && request.Method == HttpMethod.Put)
                    return Json(HttpStatusCode.NoContent, "");
                if (path == "/admin/realms/sample/clients?clientId=web-app")
                    throw new InvalidOperationException("Scripted failure after the realm write was acknowledged.");
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("Unexpected", result.Code);
        Assert.Equal("client-reconcile", result.Stage);
        Assert.Equal("Observed", result.Effect);
        Assert.Equal("OwnerAction", result.Recovery);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.UnknownWrites);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == "/admin/realms/sample");
    }

    [Theory]
    [InlineData("unapproved-authority")]
    [InlineData("callback-port")]
    [InlineData("web-origin-port")]
    public async Task PublicAuthorityAndCallbacksRequireExactApprovedOrigins(string scenario)
    {
        var desired = Desired() with { ApprovedHttpsOrigins = ["https://app.example"] };
        desired = scenario switch
        {
            "unapproved-authority" => desired with
            {
                Authority = "https://login.example/realms/sample",
                TransportOrigin = "https://localhost:8443/"
            },
            "callback-port" => desired with { RedirectUris = ["https://app.example:8443/callback"] },
            "web-origin-port" => desired with { WebOrigins = ["https://app.example:8443"] },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("Rejected origins must not reach transport."));
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, desired, Credentials(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("InvalidConfiguration", result.Code);
        Assert.Equal("validation", result.Stage);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("department/name")]
    public void MapperNamesRejectEmptyAndUnsupportedCharacters(string name)
    {
        var desired = Desired() with { Mappers = [new RealmReconcileMapper(name, "department", "sales")] };

        Assert.Throws<ArgumentException>(() => RealmReconciler.ValidateDesired(desired));
    }

    [Fact]
    public async Task MissingRealmIdAndPublicClientFlagAreHandledWithoutInventingState()
    {
        const string realmPath = "/admin/realms/sample";
        const string clientQuery = "/admin/realms/sample/clients?clientId=web-app";
        const string clientPath = "/admin/realms/sample/clients/client-internal";
        var realm = JsonNode.Parse(RealmJson())!;
        realm.AsObject().Remove("id");
        var currentClient = JsonNode.Parse(ReconcileClientJson())!;
        currentClient.AsObject().Remove("publicClient");
        JsonNode? effectiveClient = currentClient;
        JsonNode? update = null;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == realmPath) return Json(HttpStatusCode.OK, realm.ToJsonString());
                if (path == clientQuery) return Json(HttpStatusCode.OK, "[" + effectiveClient!.ToJsonString() + "]");
                if (path == clientPath && request.Method == HttpMethod.Put)
                {
                    update = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    effectiveClient = update!.DeepClone();
                    effectiveClient["id"] = "client-internal";
                    return Json(HttpStatusCode.NoContent, "");
                }
                if (path == "/admin/realms/sample/clients/client-internal/protocol-mappers/models"
                    || path == "/admin/realms/sample/identity-provider/instances") return Json(HttpStatusCode.OK, "[]");
                if (path == "/realms/sample/.well-known/openid-configuration")
                    return Json(HttpStatusCode.OK, "{\"issuer\":\"https://localhost:8443/realms/sample\"}");
                if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<form id=\"kc-form-login\"></form>", Encoding.UTF8, "text/html") };
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), CancellationToken.None);

        Assert.True(result.Ready, $"Expected ready reconciliation, got {result.Code} at {result.Stage}.");
        Assert.Null(result.RealmId);
        Assert.Equal("client-internal", result.ClientInternalId);
        Assert.True(update?["publicClient"]?.GetValue<bool>() == true);
        Assert.Equal(1, result.Writes);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == clientPath);
    }

    [Fact]
    public async Task CancellationAfterAcknowledgedWriteReportsObservedEffect()
    {
        using var cancellation = new CancellationTokenSource();
        var driftedRealm = JsonNode.Parse(RealmJson())!;
        driftedRealm["registrationAllowed"] = true;
        var handler = SuccessfulHandler(CommonSuccessRoutes.AdministratorToken | CommonSuccessRoutes.Client,
            (request, _) =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/admin/realms/sample" && request.Method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, driftedRealm.ToJsonString());
                if (path == "/admin/realms/sample" && request.Method == HttpMethod.Put)
                    return Json(HttpStatusCode.NoContent, "");
                if (path == "/admin/realms/sample/clients?clientId=web-app")
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                return null;
            });
        using var client = Client(handler);

        var result = await RealmReconciler.ReconcileAsync(client, Desired(), Credentials(), cancellation.Token);

        Assert.False(result.Ready);
        Assert.Equal("Cancelled", result.Code);
        Assert.Equal("client-reconcile", result.Stage);
        Assert.Equal("Observed", result.Effect);
        Assert.Equal("NoRetry", result.Recovery);
        Assert.Equal(1, result.Writes);
        Assert.Equal(0, result.UnknownWrites);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put && request.PathAndQuery == "/admin/realms/sample");
    }

    private static RealmReconcileDesired Desired() => new(
        "sample",
        "web-app",
        "https://localhost:8443/realms/sample",
        false,
        false,
        ImmutableArray.Create("https://localhost:8443/callback"),
        ImmutableArray.Create("https://localhost:8443/signed-out"),
        ImmutableArray.Create("https://localhost:8443"),
        ImmutableArray<RealmReconcileMapper>.Empty,
        ImmutableArray<RealmReconcileBroker>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty);

    private static RealmReconcileCredentials Credentials() => new("admin", "admin-password", null);

    private static HttpClient Client(ScriptedHandler handler) => new(handler) { BaseAddress = BaseAddress };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string RealmJson() => """
        {"id":"realm-internal","realm":"sample","enabled":true,"registrationAllowed":false,"sslRequired":"external"}
        """;

    private static string ClientJson()
    {
        var attributes = new JsonObject { ["post.logout.redirect.uris"] = "https://localhost:8443/signed-out" };
        var client = new JsonObject
        {
            ["id"] = "client-internal",
            ["clientId"] = "web-app",
            ["enabled"] = true,
            ["publicClient"] = false,
            ["protocol"] = "openid-connect",
            ["clientAuthenticatorType"] = "client-secret",
            ["standardFlowEnabled"] = true,
            ["implicitFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["serviceAccountsEnabled"] = false,
            ["redirectUris"] = new JsonArray("https://localhost:8443/callback"),
            ["webOrigins"] = new JsonArray("https://localhost:8443"),
            ["attributes"] = attributes
        };
        return client.ToJsonString();
    }

    private static string ReconcileClientJson()
    {
        var client = JsonNode.Parse(ClientJson())!;
        client["publicClient"] = true;
        return client.ToJsonString();
    }

    private sealed record RecordedRequest(HttpMethod Method, string PathAndQuery);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.True(request.RequestUri.IsLoopback);
            Assert.Equal("localhost", request.RequestUri.Host);
            Assert.Equal(8443, request.RequestUri.Port);
            Requests.Add(new RecordedRequest(request.Method, pathAndQuery));
            return Task.FromResult(respond(request, Requests.Count));
        }
    }
}
