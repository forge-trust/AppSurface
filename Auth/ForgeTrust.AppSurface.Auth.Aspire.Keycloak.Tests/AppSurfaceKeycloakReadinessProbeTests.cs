using System.Net;
using System.Text.Json.Nodes;
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

        Assert.Equal("https://localhost:8080/realms/appsurface-dev", result.Authority);
        Assert.Equal("appsurface-web", result.ClientId);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenConfiguredLoginThemeMatchesRealmEvidence_Succeeds()
    {
        using var directory = new TempDirectory();
        var options = CreateOptionsWithTheme(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var result = await probe.CheckOnceAsync();

        Assert.Equal("appsurface-dev", result.Realm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other-theme")]
    public async Task CheckOnceAsync_WhenConfiguredLoginThemeDoesNotMatchRealmEvidence_ThrowsRealmDiagnostic(string? realmTheme)
    {
        using var directory = new TempDirectory();
        var options = CreateOptionsWithTheme(directory.Path);
        var realmImport = AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        var realm = JsonNode.Parse(File.ReadAllText(realmImport))!.AsObject();
        if (realmTheme is null)
        {
            realm.Remove("loginTheme");
        }
        else
        {
            realm["loginTheme"] = realmTheme;
        }

        File.WriteAllText(realmImport, realm.ToJsonString());
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains("login theme", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lastName", "Other User", "expected local profile surname")]
    [InlineData("email", "other@appsurface.local", "expected local profile email")]
    [InlineData("emailVerified", false, "expected verified local profile email")]
    public async Task CheckOnceAsync_WhenSeededUserProfileEvidenceIsIncomplete_ThrowsRealmDiagnostic(
        string propertyName,
        object propertyValue,
        string expectedMessage)
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var realmImportPath = AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        var realm = JsonNode.Parse(File.ReadAllText(realmImportPath))!.AsObject();
        var seededUser = realm["users"]!.AsArray()[0]!.AsObject();
        seededUser[propertyName] = JsonValue.Create(propertyValue);
        File.WriteAllText(realmImportPath, realm.ToJsonString());
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenIssuerMismatch_ThrowsMetadataDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(request =>
        {
            AssertMetadataRequest(request);
            return Json("""{"issuer":"https://localhost:8080/realms/other"}""");
        }));
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
        using var client = new HttpClient(new StubHandler(request =>
        {
            AssertMetadataRequest(request);
            return Json("""{}""");
        }));
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
        using var client = new HttpClient(new StubHandler(request =>
        {
            AssertMetadataRequest(request);
            throw new HttpRequestException("connection refused");
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable, exception.Code);
        Assert.Contains(nameof(HttpRequestException), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection refused", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenMetadataRequestTimesOut_ThrowsUnavailableDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(request =>
        {
            AssertMetadataRequest(request);
            throw new TaskCanceledException("request timed out");
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.MetadataUnavailable, exception.Code);
        Assert.Contains("configured readiness HTTP timeout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenCallerCancelsMetadataRequest_PropagatesCancellation()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new StubHandler(request =>
        {
            AssertMetadataRequest(request);
            throw new OperationCanceledException(cancellation.Token);
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckOnceAsync(cancellation.Token));
    }

    [Fact]
    public async Task CheckOnceAsync_WhenMetadataReturnsFailure_ThrowsUnavailableDiagnostic()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(request =>
        {
            AssertMetadataRequest(request);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
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
                return Json("""{"issuer":"https://localhost:8080/realms/appsurface-dev"}""");
            }

            AssertAuthorizationRequest(request);

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
                return Json("""{"issuer":"https://localhost:8080/realms/appsurface-dev"}""");
            }

            AssertAuthorizationRequest(request);

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
                return Json("""{"issuer":"https://localhost:8080/realms/appsurface-dev"}""");
            }

            AssertAuthorizationRequest(request);

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
    public async Task CheckOnceAsync_WhenAuthorizationResponseExceedsBodyLimit_DoesNotMaterializeIt()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
            {
                return Json("""{"issuer":"https://localhost:8080/realms/appsurface-dev"}""");
            }

            AssertAuthorizationRequest(request);
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Content = new LargeBodyContent(),
                Headers = { Location = new Uri("http://localhost:5059/signin-appsurface-oidc?code=local") },
            };
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var result = await probe.CheckOnceAsync();

        Assert.Equal("appsurface-dev", result.Realm);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenUnknownLengthAuthorizationResponseExceedsBodyLimit_RemainsBounded()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        var content = new UnknownLengthLargeBodyContent();
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal) == true)
            {
                return Json("{\"issuer\":\"https://localhost:8080/realms/appsurface-dev\"}");
            }

            AssertAuthorizationRequest(request);
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Content = content,
                Headers = { Location = new Uri("http://localhost:5059/signin-appsurface-oidc?code=local") },
            };
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var result = await probe.CheckOnceAsync();

        Assert.Equal("appsurface-dev", result.Realm);
        Assert.Equal(16 * 1024, content.BytesRead);
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
                return Json("""{"issuer":"https://localhost:8080/realms/appsurface-dev"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var probe = new AppSurfaceKeycloakReadinessProbe(options, client);

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckOnceAsync_WhenRealmEvidenceCannotBeRead_ThrowsRealmEvidenceDiagnostic(bool ioFailure)
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        AppSurfaceKeycloakRealmGenerator.WriteRealmImport(options);
        using var client = new HttpClient(new StubHandler(MetadataThenOk));
        var probe = new AppSurfaceKeycloakReadinessProbe(
            options,
            client,
            _ => throw (ioFailure
                ? new IOException("read failed")
                : new UnauthorizedAccessException("access denied")));

        var exception = await Assert.ThrowsAsync<AppSurfaceKeycloakException>(() => probe.CheckOnceAsync());

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.RealmEvidenceInvalid, exception.Code);
        Assert.Contains(ioFailure ? nameof(IOException) : nameof(UnauthorizedAccessException), exception.Message, StringComparison.Ordinal);
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
                return Json("""{"issuer":"https://localhost:8080/realms/appsurface-dev"}""");
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
            return Json("""{"issuer":"https://localhost:8080/realms/appsurface-dev"}""");
        }

        AssertAuthorizationRequest(request);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login</html>"),
        };
    }

    private static void AssertMetadataRequest(HttpRequestMessage request) =>
        Assert.Equal(
            "/realms/appsurface-dev/.well-known/openid-configuration",
            request.RequestUri?.AbsolutePath);

    private static void AssertAuthorizationRequest(HttpRequestMessage request)
    {
        Assert.Equal(
            "/realms/appsurface-dev/protocol/openid-connect/auth",
            request.RequestUri?.AbsolutePath);
        Assert.Contains("client_id=appsurface-web", request.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains(
            $"redirect_uri={Uri.EscapeDataString("http://localhost:5059/signin-appsurface-oidc")}",
            request.RequestUri?.Query,
            StringComparison.Ordinal);
    }

    private static AppSurfaceKeycloakOptions CreateOptions(string directory) =>
        new()
        {
            RealmImportDirectory = directory,
        };

    private static AppSurfaceKeycloakOptions CreateOptionsWithTheme(string directory)
    {
        var themeDirectory = Path.Join(directory, "theme");
        Directory.CreateDirectory(Path.Join(themeDirectory, "login", "resources"));
        File.WriteAllText(Path.Join(themeDirectory, "login", "theme.properties"), "parent=keycloak\n");
        var options = CreateOptions(directory);
        options.LoginTheme = AppSurfaceKeycloakThemeOptions.Login(
            "application",
            themeDirectory,
            AppSurfaceKeycloakImageReference.Parse($"quay.io/keycloak/keycloak:26.6@sha256:{new string('a', 64)}"));
        return options;
    }

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

    private sealed class LargeBodyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The oversized authorization body should not be materialized.");

        protected override bool TryComputeLength(out long length)
        {
            length = 16 * 1024 + 1;
            return true;
        }
    }

    private sealed class UnknownLengthLargeBodyContent : HttpContent
    {
        private readonly byte[] _body = new byte[(16 * 1024) + 1];

        public int BytesRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The probe should read the response stream directly.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new TrackingReadStream(_body, read => BytesRead += read));
    }

    private sealed class TrackingReadStream : MemoryStream
    {
        private readonly Action<int> _onRead;

        public TrackingReadStream(byte[] buffer, Action<int> onRead)
            : base(buffer, writable: false)
        {
            _onRead = onRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _onRead(read);
            return read;
        }
    }
}
