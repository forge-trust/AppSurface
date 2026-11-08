using Docker.DotNet.Models;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class PostgreSqlTestPortBindingsTests
{
    [Theory]
    [InlineData("unix:///var/run/docker.sock", "127.0.0.1", "127.0.0.1")]
    [InlineData("tcp://remote-docker.example:2376", "0.0.0.0", "::")]
    [InlineData("npipe://./pipe/docker_engine", "0.0.0.0", "::")]
    public void InterfacePolicyPreservesHostPortsAndRemoteDaemonBindings(string endpoint, string expectedIpv4, string expectedIpv6)
    {
        var bindings = new List<PortBinding>
        {
            new() { HostIP = "0.0.0.0", HostPort = "0" },
            new() { HostIP = "::", HostPort = "54321" }
        };
        var parameters = new CreateContainerParameters
        {
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>> { ["5432/tcp"] = bindings }
            }
        };

        PostgreSqlTestPortBindings.BindLocalDaemonToLoopback(parameters, new Uri(endpoint));

        Assert.Equal(expectedIpv4, bindings[0].HostIP);
        Assert.Equal(expectedIpv6, bindings[1].HostIP);
        Assert.Equal("0", bindings[0].HostPort);
        Assert.Equal("54321", bindings[1].HostPort);
    }

    [Fact]
    public void MissingPortConfigurationIsNotInvented()
    {
        var parameters = new CreateContainerParameters();
        PostgreSqlTestPortBindings.BindLocalDaemonToLoopback(parameters, new Uri("unix:///var/run/docker.sock"));
        Assert.Null(parameters.HostConfig);
        parameters.HostConfig = new HostConfig();
        PostgreSqlTestPortBindings.BindLocalDaemonToLoopback(parameters, new Uri("unix:///var/run/docker.sock"));
        Assert.Null(parameters.HostConfig.PortBindings);
    }
}
