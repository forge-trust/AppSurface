using System.Net;
using System.Text.Json;
using AuthAspireKeycloakIdentityBootstrap;
using AuthAspireKeycloakLocalSeedStore;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class IdentityBootstrapTests
{
    [Theory]
    [InlineData("https://keycloak.example.test/realms/appsurface-dev", "appsurface-dev")]
    [InlineData("http://localhost:8080/realms/appsurface-dev", "appsurface-dev")]
    [InlineData("https://localhost:8080/realms/other", "appsurface-dev")]
    [InlineData("https://localhost:8080/realms/appsurface%2fdev", "appsurface-dev")]
    public void Read_WhenAuthorityIsNotTheProjectedLocalRealm_RejectsItBeforeAnyRequest(
        string authority,
        string realmName)
    {
        var environment = CreateEnvironment(authority, realmName);

        var exception = Assert.Throws<InvalidDataException>(() => IdentityBootstrapConfiguration.Read(GetValue));

        Assert.Equal("The local seed authority is invalid.", exception.Message);

        string? GetValue(string name) => environment.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void Read_WhenAuthorityMatchesTheProjectedLocalRealm_AcceptsTheConfiguration()
    {
        var environment = CreateEnvironment("https://127.0.0.1:8443/realms/appsurface-dev", "appsurface-dev");

        var configuration = IdentityBootstrapConfiguration.Read(
            name => environment.TryGetValue(name, out var value) ? value : null);

        Assert.Equal(new Uri("https://127.0.0.1:8443/realms/appsurface-dev"), configuration.Authority);
        Assert.Equal("appsurface-dev", configuration.RealmName);
    }

    [Fact]
    public async Task RunAsync_WhenTheProjectedAuthorityIsInvalid_RejectsBeforeTheAdminPasswordCanReachTransport()
    {
        var environment = CreateEnvironment("https://keycloak.example.test/realms/appsurface-dev", "appsurface-dev");
        using var handler = new SequenceHandler();
        using var client = new HttpClient(handler);

        var exitCode = await Program.RunAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            client);

        Assert.Equal(1, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunAsync_WhenTheLocalBrokerDoesNotExist_CreatesItAndPersistsTheSeedEvidence()
    {
        using var directory = new TempDirectory();
        var storePath = Path.Join(directory.Path, "local-seed-store.json");
        var environment = CreateEnvironment("https://localhost:8443/realms/appsurface-dev", "appsurface-dev", storePath);
        using var handler = new SequenceHandler(
            JsonResponse("""{"access_token":"master-token"}"""),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);

        var exitCode = await Program.RunAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            client);

        Assert.Equal(0, exitCode);
        Assert.Collection(
            handler.Requests,
            token =>
            {
                Assert.Equal(HttpMethod.Post, token.Method);
                Assert.Equal("https://localhost:8443/realms/master/protocol/openid-connect/token", token.Uri);
                Assert.Contains("password=LOCAL_TEST_SECRET", token.Content, StringComparison.Ordinal);
            },
            brokerLookup =>
            {
                Assert.Equal(HttpMethod.Get, brokerLookup.Method);
                Assert.Equal("https://localhost:8443/admin/realms/appsurface-dev/identity-provider/instances/local-broker", brokerLookup.Uri);
            },
            brokerCreate =>
            {
                Assert.Equal(HttpMethod.Post, brokerCreate.Method);
                Assert.Equal("https://localhost:8443/admin/realms/appsurface-dev/identity-provider/instances", brokerCreate.Uri);
                using var payload = JsonDocument.Parse(brokerCreate.Content!);
                Assert.Equal("https://localhost:8443/realms/appsurface-dev", payload.RootElement.GetProperty("config").GetProperty("issuer").GetString());
            });

        var snapshot = new LocalSeedStore(storePath).ReadSnapshot();
        Assert.Equal([new BrokerAliasRecord("local-broker", "https://localhost:8443/realms/appsurface-dev", "appsurface-web")], snapshot.BrokerAliases);
        Assert.Equal([new IdentitySubjectMapRecord("founder", "subject-founder-001")], snapshot.IdentitySubjectMaps);
    }

    [Fact]
    public async Task RunAsync_WhenTheExistingStoreCannotConvergeToOneBrokerAlias_ReturnsFailure()
    {
        using var directory = new TempDirectory();
        var storePath = Path.Join(directory.Path, "local-seed-store.json");
        var store = new LocalSeedStore(storePath);
        store.UpsertBrokerAlias("unrelated-broker", "https://issuer.example.test", "other-client");
        var environment = CreateEnvironment("https://localhost:8443/realms/appsurface-dev", "appsurface-dev", storePath);
        using var handler = new SequenceHandler(
            JsonResponse("""{"access_token":"master-token"}"""),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);

        var exitCode = await Program.RunAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            client);

        Assert.Equal(1, exitCode);
        Assert.Equal(2, new LocalSeedStore(storePath).ReadSnapshot().BrokerAliases.Count);
    }

    [Theory]
    [MemberData(nameof(FailingResponses))]
    public async Task RunAsync_WhenTheKeycloakAdminProtocolFails_ReturnsFailure(
        HttpResponseMessage[] responses)
    {
        using var directory = new TempDirectory();
        var environment = CreateEnvironment("https://localhost:8443/realms/appsurface-dev", "appsurface-dev", Path.Join(directory.Path, "store.json"));
        using var handler = new SequenceHandler(responses);
        using var client = new HttpClient(handler);

        var exitCode = await Program.RunAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            client);

        Assert.Equal(1, exitCode);
    }

    public static IEnumerable<object[]> FailingResponses()
    {
        yield return [new HttpResponseMessage[] { new(HttpStatusCode.InternalServerError) }];
        yield return [new HttpResponseMessage[] { JsonResponse("{}") }];
        yield return
        [
            new HttpResponseMessage[]
            {
                JsonResponse("""{"access_token":"master-token"}"""),
                new(HttpStatusCode.InternalServerError),
            },
        ];
        yield return
        [
            new HttpResponseMessage[]
            {
                JsonResponse("""{"access_token":"master-token"}"""),
                new(HttpStatusCode.OK),
                new(HttpStatusCode.InternalServerError),
            },
        ];
    }

    [Fact]
    public void SequenceHandler_WhenDisposed_DisposesUnconsumedResponseContent()
    {
        var content = new TrackingContent();
        using var response = new HttpResponseMessage { Content = content };
        using var handler = new SequenceHandler(response);

        handler.Dispose();

        Assert.True(content.IsDisposed);
    }

    private static Dictionary<string, string?> CreateEnvironment(
        string authority,
        string realmName,
        string storePath = "/tmp/identity-bootstrap-store.json") =>
        new(StringComparer.Ordinal)
        {
            ["APPSURFACE_KEYCLOAK_LOCAL_SEED_AUTHORITY"] = authority,
            ["APPSURFACE_KEYCLOAK_LOCAL_SEED_REALM_NAME"] = realmName,
            ["APPSURFACE_KEYCLOAK_LOCAL_SEED_PUBLIC_CLIENT_ID"] = "appsurface-web",
            ["LOCAL_SEED_ADMIN_USERNAME"] = "admin",
            ["LOCAL_SEED_ADMIN_PASSWORD"] = "LOCAL_TEST_SECRET",
            ["LOCAL_SEED_STORE_PATH"] = storePath,
        };

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        internal List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new RequestRecord(request.Method, request.RequestUri?.ToString(), content));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("The test did not configure a response for this request.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.Count > 0)
                {
                    _responses.Dequeue().Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        internal bool IsDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed record RequestRecord(HttpMethod Method, string? Uri, string? Content);
}
