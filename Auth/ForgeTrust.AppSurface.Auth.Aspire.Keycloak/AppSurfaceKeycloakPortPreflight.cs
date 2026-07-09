using System.Net;
using System.Net.Sockets;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Provides early local fixed-port diagnostics before Aspire starts containers or projects.
/// </summary>
public static class AppSurfaceKeycloakPortPreflight
{
    /// <summary>
    /// Throws a safe diagnostic when a configured fixed port is already occupied.
    /// </summary>
    /// <param name="port">The local TCP port to check.</param>
    /// <param name="optionName">The option name that supplied the fixed port.</param>
    public static void ThrowIfOccupied(int port, string optionName)
    {
        if (!IsAvailable(port))
        {
            throw new AppSurfaceKeycloakException(
                AppSurfaceKeycloakDiagnosticCodes.PortOccupied,
                $"Problem: local port {port} is occupied. Cause: AppSurface Keycloak option {optionName} uses a fixed port that another process already bound. Fix: stop the other process or override {optionName}. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md. Code: {AppSurfaceKeycloakDiagnosticCodes.PortOccupied}.");
        }
    }

    /// <summary>
    /// Returns whether a local TCP port can be bound at preflight time.
    /// </summary>
    /// <param name="port">The local TCP port to check.</param>
    /// <returns><see langword="true"/> when the port can be bound; otherwise <see langword="false"/>.</returns>
    public static bool IsAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
