using System.Net;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

public sealed class RealmBaselineVerifierTests
{
    private const string BaselineAuthority = "https://localhost:8443/realms/sample";
    private static readonly Uri BaseAddress = new(BaselineAuthority);

    [Theory]
    [InlineData("missing")]
    [InlineData("false")]
    public async Task RealmBaselineRequiresVerifiedSeededUserEmail(string verificationState)
    {
        var directory = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "issue888-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(TestPathUtils.PathUnder(directory, "sample-realm.json"), BaselineImport(verificationState));
            var handler = BaselineRedirectHandler();
            using var client = new HttpClient(handler);

            var error = await Assert.ThrowsAsync<RealmBaselineException>(() => RealmBaselineVerifier.CheckOnceAsync(
                client,
                BaselineDesired(directory),
                File.ReadAllText,
                CancellationToken.None));

            Assert.Equal("ASKEYC005", error.Code);
            Assert.Contains("verified local profile email", error.SafeMessage, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application")]
    public async Task RealmBaselineAcceptsVerifiedUserAndRedirectedAuthorizationChallenge(string? expectedTheme)
    {
        var directory = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "issue888-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(TestPathUtils.PathUnder(directory, "sample-realm.json"), BaselineImport("true"));
            var handler = BaselineRedirectHandler();
            using var client = new HttpClient(handler);

            await RealmBaselineVerifier.CheckOnceAsync(
                client,
                BaselineDesired(directory) with { LoginThemeName = expectedTheme },
                File.ReadAllText,
                CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains(handler.Requests, request => request.PathAndQuery.StartsWith(
                "/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("other-theme", "local-user", "expected login theme")]
    [InlineData("application", "missing-user", "does not contain seeded user 'missing-user'")]
    public async Task RealmBaselineRejectsMismatchedThemeOrSeededUser(string expectedTheme, string expectedUser, string diagnostic)
    {
        var directory = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), "issue888-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(TestPathUtils.PathUnder(directory, "sample-realm.json"), BaselineImport("true"));
            var handler = BaselineRedirectHandler();
            using var client = new HttpClient(handler);
            var desired = BaselineDesired(directory) with { LoginThemeName = expectedTheme, SeededUserNames = [expectedUser] };

            var error = await Assert.ThrowsAsync<RealmBaselineException>(() => RealmBaselineVerifier.CheckOnceAsync(
                client, desired, File.ReadAllText, CancellationToken.None));

            Assert.Equal("ASKEYC005", error.Code);
            Assert.Contains(diagnostic, error.SafeMessage, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealmBaselineRejectsBlankImportDirectoryAfterMetadataCheck()
    {
        var handler = BaselineRedirectHandler();
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => RealmBaselineVerifier.CheckOnceAsync(
            client,
            BaselineDesired(" "),
            _ => throw new InvalidOperationException("The import reader must not be called."),
            CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RealmBaselineRejectsBackslashInUnixRealmImportFileName()
    {
        if (OperatingSystem.IsWindows()) return;

        var handler = BaselineRedirectHandler();
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => RealmBaselineVerifier.CheckOnceAsync(
            client,
            BaselineDesired("unused") with { Realm = "bad\\realm" },
            _ => throw new InvalidOperationException("The import reader must not be called."),
            CancellationToken.None));

        Assert.Equal("realm", error.ParamName);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RealmBaselineTransportFailureUsesUnknownPortForInvalidAuthority()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("synthetic transport failure"));
        using var client = new HttpClient(handler) { BaseAddress = BaseAddress };

        var error = await Assert.ThrowsAsync<RealmBaselineException>(() => RealmBaselineVerifier.CheckOnceAsync(
            client,
            BaselineDesired("unused") with { Authority = string.Empty },
            _ => throw new InvalidOperationException("The import reader must not be called."),
            CancellationToken.None));

        Assert.Equal("ASKEYC003", error.Code);
        Assert.Contains("port unknown", error.SafeMessage, StringComparison.Ordinal);
    }

    private static RealmBaselineDesired BaselineDesired(string realmImportDirectory) => new(
        BaselineAuthority,
        "web-app",
        "sample",
        ["http://localhost:5055/callback"],
        ["http://localhost:5055/signed-out", "http://localhost:5055/alternate-signed-out"],
        ["local-user"],
        realmImportDirectory,
        LoginThemeName: null);

    private static string BaselineImport(string verificationState)
    {
        var verifiedProperty = verificationState == "missing" ? string.Empty : $",\"emailVerified\":{verificationState}";
        return $$"""
            {
              "realm": "sample",
              "loginTheme": "application",
              "clients": [
                {
                  "clientId": "web-app",
                  "redirectUris": ["http://localhost:5055/callback"],
                  "attributes": { "post.logout.redirect.uris": "http://localhost:5055/signed-out ## http://localhost:5055/alternate-signed-out" }
                }
              ],
              "users": [
                {
                  "username": "local-user",
                  "lastName": "Local User",
                  "email": "local-user@appsurface.local"{{verifiedProperty}}
                }
              ]
            }
            """;
    }

    private static ScriptedHandler BaselineRedirectHandler() => new((request, _) =>
    {
        var path = request.RequestUri!.PathAndQuery;
        if (path == "/realms/sample/.well-known/openid-configuration")
            return Json(HttpStatusCode.OK, "{\"issuer\":\"" + BaselineAuthority + "\"}");
        if (path.StartsWith("/realms/sample/protocol/openid-connect/auth?", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent(string.Empty) };
        throw new InvalidOperationException($"Unexpected baseline request: {request.Method} {path}.");
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

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
