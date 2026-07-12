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
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
