using System.Net;
using System.Net.Sockets;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakPortPreflightTests
{
    [Fact]
    public void ThrowIfOccupied_WhenPortIsAlreadyBound_ThrowsSafeDiagnostic()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var exception = Assert.Throws<AppSurfaceKeycloakException>(() =>
            AppSurfaceKeycloakPortPreflight.ThrowIfOccupied(port, nameof(AppSurfaceKeycloakOptions.KeycloakPort)));

        Assert.Equal(AppSurfaceKeycloakDiagnosticCodes.PortOccupied, exception.Code);
        Assert.Contains(nameof(AppSurfaceKeycloakOptions.KeycloakPort), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAvailable_WhenEphemeralPortIsReleased_ReturnsTrue()
    {
        var port = ReserveAndReleasePort();

        var available = AppSurfaceKeycloakPortPreflight.IsAvailable(port);

        Assert.True(available);
    }

    private static int ReserveAndReleasePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
