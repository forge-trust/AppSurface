using System.Net;
using System.Text.Json;

namespace AuthWebRazorWireProofExample.Tests;

[Collection(AuthWebRazorWireProofCollection.Name)]
public sealed class AuthWebRazorWireProofExampleTests
{
    private readonly AuthWebRazorWireProofFixture _fixture;

    public AuthWebRazorWireProofExampleTests(AuthWebRazorWireProofFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, "Challenge", "Unauthenticated", null)]
    [InlineData("unknown", HttpStatusCode.Unauthorized, "Challenge", "Unauthenticated", null)]
    [InlineData("viewer", HttpStatusCode.Forbidden, "Forbid", "Forbidden", "viewer-1")]
    [InlineData("operator", HttpStatusCode.OK, "Allowed", "None", "operator-1")]
    public async Task ApiProof_ReturnsCanonicalOutcomeForProofUser(
        string? proofUser,
        HttpStatusCode expectedStatusCode,
        string expectedOutcome,
        string expectedReason,
        string? expectedSubject)
    {
        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth-proof");
        if (!string.IsNullOrWhiteSpace(proofUser))
        {
            request.Headers.Add("X-Proof-User", proofUser);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal("Minimal API", ReadString(json, "surface"));
        Assert.Equal("OperatorsOnly", ReadString(json, "policy"));
        Assert.Equal(expectedOutcome, ReadString(json, "outcome"));
        Assert.Equal(expectedReason, ReadString(json, "reason"));
        Assert.Equal((int)expectedStatusCode, ReadInt32(json, "statusCode"));
        Assert.Equal(expectedSubject, ReadNullableString(json, "subject"));
    }

    [Theory]
    [InlineData("anonymous", "anonymous", "Challenge", "Unauthenticated", "unauthenticated", null)]
    [InlineData("unknown", "anonymous", "Challenge", "Unauthenticated", "unauthenticated", null)]
    [InlineData("viewer", "viewer", "Forbid", "Forbidden", "forbidden", "viewer-1")]
    [InlineData("operator", "operator", "Allowed", "None", "allowed", "operator-1")]
    public async Task BrowserProof_RendersApiAndRazorWireStatesForPersona(
        string proofUser,
        string expectedPersona,
        string expectedOutcome,
        string expectedReason,
        string expectedState,
        string? expectedSubject)
    {
        using var client = _fixture.CreateClientWithCookies();

        var html = await client.GetStringAsync($"/?proofUser={proofUser}");

        Assert.Contains($"data-auth-proof-persona=\"{expectedPersona}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-auth-proof-api-outcome=\"{expectedOutcome}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-auth-proof-ui-outcome=\"{expectedOutcome}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-auth-proof-ui-reason=\"{expectedReason}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-auth-proof-state=\"{expectedState}\"", html, StringComparison.Ordinal);
        Assert.Contains($"<td>{expectedPersona}</td>", html, StringComparison.Ordinal);

        if (expectedSubject is null)
        {
            Assert.Contains("<dd>none</dd>", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"data-auth-proof-subject=\"{expectedSubject}\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Documentation_DiscoverySurfacesPointToTheBrowserFirstProof()
    {
        var sampleReadme = _fixture.ReadRepositoryFile("examples", "auth-web-razorwire-proof", "README.md");
        var authAspNetCoreReadme = _fixture.ReadRepositoryFile("Auth", "ForgeTrust.AppSurface.Auth.AspNetCore", "README.md");
        var authReadme = _fixture.ReadRepositoryFile("Auth", "ForgeTrust.AppSurface.Auth", "README.md");
        var examplesReadme = _fixture.ReadRepositoryFile("examples", "README.md");
        var rootReadme = _fixture.ReadRepositoryFile("README.md");
        var packageIndex = _fixture.ReadRepositoryFile("packages", "package-index.yml");
        var packageReadme = _fixture.ReadRepositoryFile("packages", "README.md");

        Assert.Contains(
            "dotnet run --project examples/auth-web-razorwire-proof/AuthWebRazorWireProofExample.csproj --urls http://127.0.0.1:5058",
            sampleReadme,
            StringComparison.Ordinal);
        Assert.Contains("Your host owns auth. This sample only changes the local proof persona.", sampleReadme, StringComparison.Ordinal);
        Assert.Contains("| `operator` | `200` | `Allowed` | `None` | allowed |", sampleReadme, StringComparison.Ordinal);
        Assert.Contains("X-Proof-User", sampleReadme, StringComparison.Ordinal);
        Assert.Contains("auth-web-razorwire-proof/README.md", authAspNetCoreReadme, StringComparison.Ordinal);
        Assert.Contains("Auth Web/RazorWire proof", examplesReadme, StringComparison.Ordinal);
        Assert.Contains("examples/auth-web-razorwire-proof/README.md", rootReadme, StringComparison.Ordinal);
        Assert.Contains("examples/auth-web-razorwire-proof/README.md", packageIndex, StringComparison.Ordinal);

        var publicDocs = string.Join(
            Environment.NewLine,
            sampleReadme,
            authAspNetCoreReadme,
            authReadme,
            examplesReadme,
            rootReadme,
            packageIndex,
            packageReadme);

        Assert.DoesNotContain("#419", publicDocs, StringComparison.Ordinal);
        Assert.DoesNotContain("#421", publicDocs, StringComparison.Ordinal);
        Assert.DoesNotContain("#422", publicDocs, StringComparison.Ordinal);
        Assert.DoesNotContain("adds a result-bearing RazorWire auth adapter", publicDocs, StringComparison.Ordinal);
        Assert.DoesNotContain("projects the same auth result states into RazorWire UI components", publicDocs, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductSource_DoesNotIntroduceFutureAuthPublicApi()
    {
        var source = string.Join(
            Environment.NewLine,
            _fixture.ReadProductSourceFiles("Auth", "Web/ForgeTrust.RazorWire"));

        Assert.DoesNotContain("RequireSurfacePolicy(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("class AuthGate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("class AuthView", source, StringComparison.Ordinal);
        Assert.DoesNotContain("class PermissionGate", source, StringComparison.Ordinal);
    }

    private static string ReadString(JsonDocument json, string propertyName)
    {
        return json.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"JSON property '{propertyName}' was null.");
    }

    private static string? ReadNullableString(JsonDocument json, string propertyName)
    {
        var property = json.RootElement.GetProperty(propertyName);

        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    private static int ReadInt32(JsonDocument json, string propertyName)
    {
        return json.RootElement.GetProperty(propertyName).GetInt32();
    }
}
