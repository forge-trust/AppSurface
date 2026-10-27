using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Keeps local PostgreSQL fixtures on the same interface as their host connections.</summary>
internal static class PostgreSqlTestPortBindings
{
    /// <summary>
    /// Binds a Unix-socket Docker daemon's published ports to IPv4 loopback, preserving random host ports.
    /// Remote daemons retain their configured interfaces. Apply this modifier before container startup;
    /// mixing wildcard and loopback bindings can route a local connection to another container on the same port.
    /// </summary>
    internal static void BindLocalDaemonToLoopback(CreateContainerParameters parameters)
        => BindLocalDaemonToLoopback(parameters, TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint);

    /// <summary>Applies the interface policy using an explicit daemon endpoint for deterministic tests.</summary>
    internal static void BindLocalDaemonToLoopback(CreateContainerParameters parameters, Uri endpoint)
    {
        if (endpoint.Scheme != "unix" || parameters.HostConfig?.PortBindings is not { } ports)
        {
            return;
        }

        foreach (var bindings in ports.Values)
        {
            foreach (var binding in bindings)
            {
                binding.HostIP = "127.0.0.1";
            }
        }
    }
}
