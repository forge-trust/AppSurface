using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakHttpsAuthorityQaRegressionTests
{
    // Regression: ISSUE-001 — Keycloak metadata was addressed over HTTP by the local proof.
    // Found by /qa on 2026-08-07. Report: .gstack/qa-reports/qa-report-localhost-2026-08-07.md.
    [Fact]
    public void Authority_UsesHttpsForTheLocalKeycloakEndpoint()
    {
        Assert.Equal("https://localhost:8080/realms/appsurface-dev", AppSurfaceKeycloakDefaults.Authority());
    }

}
