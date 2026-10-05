using ForgeTrust.AppSurface.EvidenceGate;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class GitHubActionsEvidenceAuthorityFactoryTests
{
    [Fact]
    public void TryCreateFromEnvironment_ShouldRejectMissingOrUnsafeToken()
    {
        Assert.Null(Create(token: null, apiUrl: null));
        Assert.Null(Create(token: "", apiUrl: null));
        Assert.Null(Create(token: " ", apiUrl: null));
        Assert.Null(Create(token: new string('a', 4097), apiUrl: null));
        Assert.Null(Create(token: "token\nvalue", apiUrl: null));
    }

    [Theory]
    [InlineData("http://api.github.com")]
    [InlineData("https://user:PASSWORD@api.github.com")]
    [InlineData("https://api.github.com?token=secret")]
    [InlineData("https://api.github.com#fragment")]
    [InlineData("not-a-url")]
    public void TryCreateFromEnvironment_ShouldRejectUnsafeApiEndpoint(string apiUrl)
    {
        Assert.Null(Create(token: "bounded-token", apiUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://api.github.com")]
    [InlineData("https://github.enterprise.example/api/v3")]
    public void TryCreateFromEnvironment_ShouldCreateProviderForTrustedHttpsEndpoint(string? apiUrl)
    {
        using var provider = Create(token: "bounded-token", apiUrl);
        Assert.NotNull(provider);
    }

    [Fact]
    public void TryCreateFromEnvironment_ShouldRequireEnvironmentReader()
    {
        Assert.Throws<ArgumentNullException>(() => GitHubActionsEvidenceAuthorityProvider.TryCreateFromEnvironment(null!));
    }

    private static GitHubActionsEvidenceAuthorityProvider? Create(string? token, string? apiUrl) =>
        GitHubActionsEvidenceAuthorityProvider.TryCreateFromEnvironment(name => name switch
        {
            "GITHUB_TOKEN" => token,
            "GITHUB_API_URL" => apiUrl,
            _ => null,
        });
}
