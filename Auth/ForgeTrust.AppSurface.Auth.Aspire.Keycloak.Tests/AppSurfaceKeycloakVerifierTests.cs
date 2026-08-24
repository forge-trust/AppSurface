using AuthAspireKeycloakVerifier;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakVerifierTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("301")]
    public async Task RunAsync_WhenTimeoutOutOfRange_ReturnsSafeParseDiagnostic(string timeoutSeconds)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var client = new HttpClient(new StubHandler());

        var exitCode = await Program.RunAsync(
            ["--target", "http://localhost:5059", "--timeout-seconds", timeoutSeconds],
            output,
            error,
            client);

        Assert.Equal(2, exitCode);
        Assert.Contains("--timeout-seconds must be between 1 and 300", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("300", 300)]
    public void Parse_WhenTimeoutAtInclusiveBoundary_AcceptsValue(string timeoutSeconds, int expectedSeconds)
    {
        var options = VerifierOptions.Parse(
            ["--target", "http://localhost:5059", "--timeout-seconds", timeoutSeconds],
            _ => "realm-import.json");

        Assert.True(options.IsValid);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.Timeout);
        Assert.Empty(options.Error);
    }

    [Fact]
    public void Parse_WhenRealmImportEvidencePathMissing_ReturnsSafeDiagnostic()
    {
        var options = VerifierOptions.Parse(
            ["--target", "http://localhost:5059"],
            _ => null);

        Assert.False(options.IsValid);
        Assert.Contains("AUTH_ASPIRE_KEYCLOAK_REALM_IMPORT_FILE", options.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeUntilReadyAsync_WhenTimeoutOccursAfterProbeFailures_IncludesTimeoutDiagnostic()
    {
        var options = new VerifierOptions(
            new Uri("http://localhost:5059/"),
            "appsurface-web",
            "realm-import.json",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            true,
            string.Empty);
        using var timeout = new CancellationTokenSource();
        using var client = new HttpClient(new CancellingHandler(timeout));

        var failures = await Program.ProbeUntilReadyAsync(options, client, timeout.Token, timeout.Token);

        Assert.Contains("/auth/proof/status returned HTTP 500.", failures);
        Assert.Contains("verification timed out before probes succeeded.", failures);
    }

    [Fact]
    public async Task VerifyLocalSeedStoreAsync_RequiresExactlyTheTwoOrderedConsumerResults()
    {
        using var directory = new TempDirectory();
        var storePath = Path.Join(directory.Path, "seed-store.json");
        var store = new AuthAspireKeycloakLocalSeedStore.LocalSeedStore(storePath);
        store.UpsertBrokerAlias("local-broker", "https://issuer/realms/appsurface-dev", "appsurface-web");
        store.UpsertIdentitySubjectMap("founder", "subject-founder-001");
        store.UpsertCandidateFixture("candidate:founder", "subject-founder-001");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.VerifyLocalSeedStoreAsync(
            name => name switch
            {
                "LOCAL_SEED_STORE_PATH" => storePath,
                "APPSURFACE_KEYCLOAK_LOCAL_SEED_AUTHORITY" => "https://issuer/realms/appsurface-dev",
                "APPSURFACE_KEYCLOAK_LOCAL_SEED_PUBLIC_CLIENT_ID" => "appsurface-web",
                _ => null,
            },
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Contains("exactly once", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task VerifyLocalSeedStoreAsync_WhenTheStoreIsMissingOrIncomplete_ReturnsSafeFailure()
    {
        using var directory = new TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var missingPathExitCode = await Program.VerifyLocalSeedStoreAsync(_ => null, output, error);
        var incompletePathExitCode = await Program.VerifyLocalSeedStoreAsync(
            name => name switch
            {
                "LOCAL_SEED_STORE_PATH" => Path.Join(directory.Path, "missing.json"),
                "APPSURFACE_KEYCLOAK_LOCAL_SEED_AUTHORITY" => "https://issuer/realms/appsurface-dev",
                "APPSURFACE_KEYCLOAK_LOCAL_SEED_PUBLIC_CLIENT_ID" => "appsurface-web",
                _ => null,
            },
            output,
            error);

        Assert.Equal(1, missingPathExitCode);
        Assert.Equal(1, incompletePathExitCode);
        Assert.Contains("local seed verification input is missing", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("ordered local seed records are missing or invalid", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task VerifyLocalSeedStoreAsync_WhenBrokerMetadataOrTheStoreDocumentIsInvalid_ReturnsSafeFailure()
    {
        using var directory = new TempDirectory();
        var storePath = Path.Join(directory.Path, "seed-store.json");
        var store = new AuthAspireKeycloakLocalSeedStore.LocalSeedStore(storePath);
        store.UpsertBrokerAlias("local-broker", "https://wrong-issuer", "wrong-client");
        store.UpsertIdentitySubjectMap("founder", "subject-founder-001");
        store.UpsertCandidateFixture("candidate:founder", "subject-founder-001");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var environment = CreateSeedStoreEnvironment(storePath);

        var wrongBrokerExitCode = await Program.VerifyLocalSeedStoreAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            output,
            error);
        File.WriteAllText(storePath, "{");
        var malformedDocumentExitCode = await Program.VerifyLocalSeedStoreAsync(
            name => environment.TryGetValue(name, out var value) ? value : null,
            output,
            error);

        Assert.Equal(1, wrongBrokerExitCode);
        Assert.Equal(1, malformedDocumentExitCode);
        Assert.Contains("ordered local seed records are missing or invalid", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("local seed store could not be read (", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    private static IReadOnlyDictionary<string, string> CreateSeedStoreEnvironment(string storePath) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LOCAL_SEED_STORE_PATH"] = storePath,
            ["APPSURFACE_KEYCLOAK_LOCAL_SEED_AUTHORITY"] = "https://issuer/realms/appsurface-dev",
            ["APPSURFACE_KEYCLOAK_LOCAL_SEED_PUBLIC_CLIENT_ID"] = "appsurface-web",
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid verifier options must fail before HTTP transport is used.");
    }

    private sealed class CancellingHandler(CancellationTokenSource timeout) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 3)
            {
                timeout.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
