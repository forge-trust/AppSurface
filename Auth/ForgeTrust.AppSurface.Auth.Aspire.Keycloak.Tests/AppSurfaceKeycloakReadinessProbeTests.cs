using System.Net;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakReadinessProbeTests
{
    [Fact]
    public async Task CheckOnceAsync_WhenMetadataRealmAndChallengeValid_Succeeds()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var result = await probe.CheckOnceAsync();

        Assert.Equal("http://localhost:8080/realms/appsurface-dev", result.Authority);
        Assert.Equal("appsurface-web", result.ClientId);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenIssuerMismatch_ThrowsMetadataDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(_ => Json("""{"issuer":"http://localhost:8080/realms/other"}""")));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.MetadataInvalid, exception.Code);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenIssuerMissing_ThrowsMetadataDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(_ => Json("""{}""")));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.MetadataInvalid, exception.Code);
        Assert.Contains("<missing>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenMetadataRequestFails_ThrowsUnavailableDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused")));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable, exception.Code);
        Assert.Contains("connection refused", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenMetadataReturnsFailure_ThrowsUnavailableDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable, exception.Code);
        Assert.Contains("HTTP 503", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenAuthorizationRejectsClient_ThrowsChallengeDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
            {
                return Json("""{"issuer":"http://localhost:8080/realms/appsurface-dev"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("invalid_redirect_uri"),
            };
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.AuthorizationChallengeInvalid, exception.Code);
        Assert.DoesNotContain("appsurface-admin-local-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenAuthorizationRedirects_Succeeds()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
            {
                return Json("""{"issuer":"http://localhost:8080/realms/appsurface-dev"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://localhost:5059/signin-appsurface-oidc?code=local") },
            };
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var result = await probe.CheckOnceAsync();

        Assert.Equal("appsurface-dev", result.Realm);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenAuthorizationEndpointFails_ThrowsChallengeDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
            {
                return Json("""{"issuer":"http://localhost:8080/realms/appsurface-dev"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("server error"),
            };
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.AuthorizationChallengeInvalid, exception.Code);
        Assert.Contains("HTTP 500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenRealmEvidenceMalformed_ThrowsRealmEvidenceDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        Directory.CreateDirectory(options.RealmImportDirectory);
        File.WriteAllText(AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(options.RealmImportDirectory, options.Realm), "{not-json");
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
            {
                return Json("""{"issuer":"http://localhost:8080/realms/appsurface-dev"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenRealmEvidenceFileMissing_ThrowsRealmEvidenceDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"clients":[],"users":[{"username":"admin"},{"username":"viewer"}]}""", "expected realm id")]
    [InlineData("""{"realm":"appsurface-dev","users":[{"username":"admin"},{"username":"viewer"}]}""", "clients array")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"]}]}""", "users array")]
    [InlineData("""{"realm":"appsurface-dev","clients":[42],"users":[{"username":"admin"},{"username":"viewer"}]}""", "public client id")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"redirectUris":["http://localhost:5059/signin-appsurface-oidc"]}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "public client id")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":42}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "public client id")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"other","redirectUris":["http://localhost:5059/signin-appsurface-oidc"]}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "public client id")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":[42]}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "redirect URI")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"]}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "post-logout redirect URI")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","attributes":{"post.logout.redirect.uris":"http://localhost:5059/signout-callback-appsurface-oidc"}}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "client redirect URIs")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"],"attributes":{"post.logout.redirect.uris":"http://localhost:5059/signout-callback-appsurface-oidc"}}],"users":[{"username":"admin"}]}""", "seeded user")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"],"attributes":{"post.logout.redirect.uris":"http://localhost:5059/signout-callback-appsurface-oidc"}}],"users":[42,{"username":"viewer"}]}""", "seeded user")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"],"attributes":{"post.logout.redirect.uris":"http://localhost:5059/signout-callback-appsurface-oidc"}}],"users":[{"username":42},{"username":"viewer"}]}""", "seeded user")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"],"attributes":[]}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "post-logout redirect URI")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"],"attributes":{"post.logout.redirect.uris":""}}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "post-logout redirect URI")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"],"attributes":{"post.logout.redirect.uris":42}}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "post-logout redirect URI")]
    [InlineData("""{"realm":"appsurface-dev","clients":[{"clientId":"appsurface-web","redirectUris":["http://localhost:5059/signin-appsurface-oidc"],"attributes":{}}],"users":[{"username":"admin"},{"username":"viewer"}]}""", "post-logout redirect URI")]
    public async Task CheckOnceAsync_WhenRealmEvidenceIncomplete_ThrowsSpecificDiagnostic(string json, string expectedMessage)
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        Directory.CreateDirectory(options.RealmImportDirectory);
        File.WriteAllText(AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(options.RealmImportDirectory, options.Realm), json);
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenRealmEvidenceMissingRedirect_ThrowsRealmEvidenceDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        Directory.CreateDirectory(options.RealmImportDirectory);
        File.WriteAllText(
            AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(options.RealmImportDirectory, options.Realm),
            """
            {
              "realm": "appsurface-dev",
              "clients": [
                {
                  "clientId": "appsurface-web",
                  "redirectUris": [ "http://localhost:5059/other" ],
                  "attributes": {
                    "post.logout.redirect.uris": "http://localhost:5059/signout-callback-appsurface-oidc"
                  }
                }
              ],
              "users": [
                { "username": "admin" },
                { "username": "viewer" }
              ]
            }
            """);
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
            {
                return Json("""{"issuer":"http://localhost:8080/realms/appsurface-dev"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains("redirect URI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenLaterConfiguredRedirectMissing_ThrowsRealmEvidenceDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.RedirectUris.Add(new Uri("http://127.0.0.1:5059/signin-appsurface-oidc"));
        Directory.CreateDirectory(options.RealmImportDirectory);
        File.WriteAllText(
            AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(options.RealmImportDirectory, options.Realm),
            """
            {
              "realm": "appsurface-dev",
              "clients": [
                {
                  "clientId": "appsurface-web",
                  "redirectUris": [ "http://localhost:5059/signin-appsurface-oidc" ],
                  "attributes": {
                    "post.logout.redirect.uris": "http://localhost:5059/signout-callback-appsurface-oidc"
                  }
                }
              ],
              "users": [
                { "username": "admin" },
                { "username": "viewer" }
              ]
            }
            """);
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains("http://127.0.0.1:5059/signin-appsurface-oidc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenLaterConfiguredPostLogoutRedirectMissing_ThrowsRealmEvidenceDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.PostLogoutRedirectUris.Add(new Uri("http://127.0.0.1:5059/signout-callback-appsurface-oidc"));
        Directory.CreateDirectory(options.RealmImportDirectory);
        File.WriteAllText(
            AppSurfaceKeycloakRealmImportPaths.GetRealmImportFilePath(options.RealmImportDirectory, options.Realm),
            """
            {
              "realm": "appsurface-dev",
              "clients": [
                {
                  "clientId": "appsurface-web",
                  "redirectUris": [ "http://localhost:5059/signin-appsurface-oidc" ],
                  "attributes": {
                    "post.logout.redirect.uris": "http://localhost:5059/signout-callback-appsurface-oidc"
                  }
                }
              ],
              "users": [
                { "username": "admin" },
                { "username": "viewer" }
              ]
            }
            """);
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains("http://127.0.0.1:5059/signout-callback-appsurface-oidc", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage MetadataThenOk(HttpRequestMessage request)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
        {
            return Json("""{"issuer":"http://localhost:8080/realms/appsurface-dev"}""");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login</html>"),
        };
    }

    private static AppSurfaceKeycloakOptions CreateOptions(string directory) =>
        new()
        {
            RealmImportDirectory = directory,
        };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
