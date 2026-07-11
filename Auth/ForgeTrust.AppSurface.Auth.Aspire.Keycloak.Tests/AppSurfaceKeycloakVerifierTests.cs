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
            _ => null);

        Assert.True(options.IsValid);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.Timeout);
        Assert.Empty(options.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid verifier options must fail before HTTP transport is used.");
    }
}
