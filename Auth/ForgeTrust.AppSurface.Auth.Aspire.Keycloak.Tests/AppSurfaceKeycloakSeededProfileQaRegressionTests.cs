using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakSeededProfileQaRegressionTests
{
    // Regression: ISSUE-002 — Keycloak required seeded users to complete a profile before redirecting.
    // Found by /qa on 2026-08-07. Report: .gstack/qa-reports/qa-report-localhost-2026-08-07.md.
    [Fact]
    public void RealmGenerator_SeedsProfileFieldsRequiredForDirectLogin()
    {
        using var document = JsonDocument.Parse(AppSurfaceKeycloakRealmGenerator.Generate(new AppSurfaceKeycloakOptions()));
        var users = document.RootElement.GetProperty("users").EnumerateArray().ToArray();

        Assert.NotEmpty(users);

        foreach (var user in users)
        {
            var username = user.GetProperty("username").GetString();

            Assert.Equal("Local User", user.GetProperty("lastName").GetString());
            Assert.Equal($"{username}@appsurface.local", user.GetProperty("email").GetString());
            Assert.True(user.GetProperty("emailVerified").GetBoolean());
        }
    }
}
