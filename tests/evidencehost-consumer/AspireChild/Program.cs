using System.Net.Sockets;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

if (args.Length != 4 || args[0] != "--scratch" || args[2] != "--case" ||
    args[3] is not ("normal" or "readiness-failure" or "factory-stall" or "cancel" or "stuck-descendant"))
{
    throw new ArgumentException("Expected the root-selected scratch and closed control case.");
}

var scratch = Path.GetFullPath(args[1]);
var control = args[3];
// The root controller creates this marker only after its external watchdog is armed.
while (!File.Exists(Path.Combine(scratch, "armed")))
{
    await Task.Delay(25);
}

var builder = await CreateFactoryAsync();
await using var application = builder.Build();
try
{
    await application.StartAsync();
    await application.WaitForShutdownAsync();
}
finally
{
    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await application.StopAsync(cleanup.Token);
}

async Task<IDistributedApplicationBuilder> CreateFactoryAsync()
{
    await File.WriteAllTextAsync(Path.Combine(scratch, "factory-entered"), "factory-entered\n");
    if (control == "factory-stall")
    {
        using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    var payload = AppContext.BaseDirectory;
    var socket = Path.Combine(scratch, "http.sock");
    var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
    {
        DisableDashboard = true,
        EnableResourceLogging = true,
        ProjectDirectory = scratch,
        Args = ["--Dcp:CliPath", Path.Combine(payload, "dcp", "dcp"),
            "--Dcp:ExtensionsPath", Path.Combine(payload, "dcp", "ext"),
            "--Dcp:WaitForResourceCleanup", "true"]
    });
    appBuilder.Services.AddHealthChecks().AddCheck("native-http-uds", new SocketHealth(socket));
    appBuilder.AddExecutable("native-http", Environment.ProcessPath!, scratch,
        [Path.Combine(payload, "resource", "NativeHttpResource.dll"), "--socket", socket, "--case", control])
        .WithEnvironment("PROOF_PROTECTED_TOOLS", Environment.GetEnvironmentVariable("PROOF_PROTECTED_TOOLS")!)
        .WithEnvironment("PROOF_PROTECTED_OUTPUT", Environment.GetEnvironmentVariable("PROOF_PROTECTED_OUTPUT")!)
        .WithEnvironment("PROOF_ALLOWED_INPUT", Environment.GetEnvironmentVariable("PROOF_ALLOWED_INPUT")!)
        .WithHealthCheck("native-http-uds");
    if (appBuilder.Resources.Count != 1 || appBuilder.Resources[0] is not ExecutableResource)
    {
        throw new InvalidOperationException("The closed catalogue permits exactly one executable resource.");
    }

    return appBuilder;
}

internal sealed class SocketHealth(string path) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            using var response = await client.GetAsync("http://localhost/health", cancellationToken);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("HTTP health failed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("UDS health unavailable.");
        }
    }
}
