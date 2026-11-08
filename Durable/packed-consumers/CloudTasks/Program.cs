using ForgeTrust.AppSurface.Durable.CloudTasks;
using Microsoft.Extensions.DependencyInjection;

await ProveCustomTokenRegistrationAsync();
ProveMetadataRegistration();

static async Task ProveCustomTokenRegistrationAsync()
{
    var services = new ServiceCollection();
    services.AddSingleton<ICloudTasksAccessTokenSource>(new FixedTokenSource("invalid token"));
    services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(20)));
    await using var provider = services.BuildServiceProvider(validateScopes: true);
    var client = provider.GetRequiredService<ICloudTasksActivationClient>();
    var target = new CloudTasksActivationTarget(
        "example-project",
        "us-central1",
        "activation",
        new Uri("https://activation.example.test/run"),
        "worker@example-project.iam.gserviceaccount.com",
        "https://activation.example.test",
        TimeSpan.FromSeconds(30));
    var receipt = await client.DispatchAsync(new CloudTasksActivationRequest(target, "consumer-proof-1"));
    if (receipt.Disposition != CloudTasksDispatchDisposition.Rejected || receipt.DiagnosticCode != "ASCT102")
        throw new InvalidOperationException("Expected malformed custom token to be rejected as ASCT102 before provider I/O.");
    Console.WriteLine(receipt.DiagnosticCode);
}

static void ProveMetadataRegistration()
{
    var services = new ServiceCollection();
    services.AddCloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(2)));
    services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(20)));
    using var provider = services.BuildServiceProvider(validateScopes: true);
    _ = provider.GetRequiredService<ICloudTasksActivationClient>();
}

sealed class FixedTokenSource(string token) : ICloudTasksAccessTokenSource
{
    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(token);
}
