using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Evidence.Readiness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

Check(args.Length > 1, "proof root and assembly IDs required");
var root = Path.GetFullPath(args[0]);
Check(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "synthetic local child only");
var ids = args.Skip(1).ToArray();
var assemblies = ids.Select(id => Assembly.Load(id)).Select(assembly => new
{
    name = assembly.GetName().Name!, path = assembly.Location,
    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
}).ToArray();
var declaration = new EvidenceProducerDeclaration("http", "custom", "1", ["web"], ["http/ok@1"],
    [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 4096)], 30)
{ Inputs = new EvidenceProducerInputs(["input.txt"], []) };
var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
    [new EvidenceResourceDeclaration("web", "completion", 30, [])], [declaration],
    [new EvidenceObligation("http", "http", "Real Kestrel response", ["http"], "http/ok@1")]);
var policy = new EvidencePolicy("packed-host", "1", profile.Id, [profile], [new EvidencePolicyRule("all", "**", profile.Id)]);
var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("input.txt")]);
var state = new State();
EvidenceHostOptions Options(string run) => new() { Reuse = true, RepositoryRoot = Path.Join(root, "repo"),
    ReceiptStore = Path.Join(root, "receipts"), ArtifactDirectory = Path.Join(root, run),
    ProducerCleanupTimeout = TimeSpan.FromSeconds(10), ReceiptWaitTimeout = TimeSpan.FromSeconds(30) };
var firstProducer = new Producer(state);
var firstFactoryCount = 0;
EvidenceManifest first;
Console.WriteLine("packed-host: preparing fresh run");
await using (var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
{
    registration.AddProducer(firstProducer, "packed-http-v1");
    registration.AddResource("web", "kestrel-body-and-health-v1");
}, Options("first")))
{
    Check(prepared.RequiredResourceIds.SequenceEqual(new[] { "web" }), "first exact resource closure");
    first = await prepared.RunAsync(async (requested, token) =>
    {
        Check(requested.SequenceEqual(new[] { "web" }), "first factory exact web");
        firstFactoryCount++;
        Console.WriteLine("packed-host: starting Kestrel");
        var contentRoot = Path.Join(root, "web-root");
        Directory.CreateDirectory(contentRoot);
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        { ContentRootPath = contentRoot, EnvironmentName = "Production", Args = [] });
        Console.WriteLine("packed-host: Kestrel builder created");
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel();
        builder.Services.AddRouting();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        Console.WriteLine("packed-host: Kestrel application built");
        app.MapGet("/health", () => "ready");
        app.MapGet("/evidence", () => "owned-http-evidence");
        try
        {
            Console.WriteLine("packed-host: Kestrel StartAsync entered");
            await app.StartAsync(token);
            state.Endpoint = new Uri(app.Urls.Single());
            Console.WriteLine("packed-host: Kestrel started");
            return new EvidenceHostResourceScope([new Readiness(state)], new Owner(app, state));
        }
        catch { await app.DisposeAsync(); throw; }
    });
    Console.WriteLine("packed-host: fresh RunAsync completed");
    Check(first.ExecutionVerdict == EvidenceExecutionVerdict.Passed && first.ProducerResults.Single().Outcome == EvidenceProducerOutcome.Passed,
        "fresh host passed");
}
Check(state.Cleaned == 1 && firstProducer.Runs == 1 && first.Metrics.CleanupCompleted, "fresh actual application cleanup");
Console.WriteLine("packed-host: fresh prepared owner disposed");
var secondProducer = new Producer(state);
var secondFactoryCount = 0;
EvidenceManifest second;
string restored;
Console.WriteLine("packed-host: preparing warm run");
await using (var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
{
    registration.AddProducer(secondProducer, "packed-http-v1");
    registration.AddResource("web", "kestrel-body-and-health-v1");
}, Options("second")))
{
    Check(prepared.RequiredResourceIds.Count == 0, "all hit empty resource closure");
    second = await prepared.RunAsync((_, _) =>
    { secondFactoryCount++; throw new InvalidOperationException("All-hit factory must not start Kestrel"); });
    Check(second.ExecutionVerdict == EvidenceExecutionVerdict.Passed && second.ClaimKind == EvidenceClaimKind.LocalReusedComplete
        && second.Eligibility == EvidenceClaimEligibility.Informational && second.ProducerResults.Single().Outcome == EvidenceProducerOutcome.Reused,
        "warm host informational reuse");
    Check(EvidenceManifestBuilder.Verify(prepared.Plan, second) && !EvidenceManifestBuilder.VerifyForGate(prepared.Plan, second), "current claim verification");
    restored = Path.Join(prepared.ArtifactDirectories["http"], "reports", "result.txt");
    Check(await File.ReadAllTextAsync(restored) == "owned-http-evidence", "restored real HTTP artifact");
    var artifact = second.ProducerResults.Single().Artifacts!.Single();
    Check(artifact.Sha256 == Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(restored))), "restored artifact digest");
}
Check(firstFactoryCount == 1 && secondFactoryCount == 0 && secondProducer.Runs == 0 && state.Cleaned == 1, "selective Kestrel startup");
await File.WriteAllTextAsync(Path.Join(root, "host-proof.json"), JsonSerializer.Serialize(new
{
    passed = true, firstFactoryCount, secondFactoryCount, firstProducerRuns = firstProducer.Runs,
    secondProducerRuns = secondProducer.Runs, cleanedApplications = state.Cleaned,
    firstOutcome = first.ProducerResults.Single().Outcome.ToString(), secondOutcome = second.ProducerResults.Single().Outcome.ToString(),
    claim = second.ClaimKind.ToString(), eligibility = second.Eligibility.ToString(), restoredArtifact = restored, assemblies
}, new JsonSerializerOptions { WriteIndented = true }));
static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
sealed class State { internal Uri? Endpoint; internal int Cleaned; }
sealed class Producer(State state) : IEvidenceProducer
{
    public string Id => "http";
    internal int Runs;
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken token)
    {
        Runs++;
        Console.WriteLine("packed-host: HTTP evidence starting");
        using var client = new HttpClient();
        var value = await client.GetStringAsync(new Uri(state.Endpoint!, "/evidence"), token);
        Console.WriteLine("packed-host: HTTP evidence received");
        if (value != "owned-http-evidence") throw new InvalidOperationException("Real HTTP response mismatch");
        await context.Artifacts!.WriteAsync("report", "reports/result.txt", Encoding.UTF8.GetBytes(value), token);
        return new(Id, EvidenceProducerOutcome.Passed, ["http/ok@1"], Artifacts: context.Artifacts.WrittenArtifacts);
    }
}
sealed class Readiness(State state) : IEvidenceResourceReadiness
{
    public string Id => "web";
    public async Task WaitUntilReadyAsync(CancellationToken token)
    {
        using var client = new HttpClient();
        await HttpReadinessProbe.WaitAsync(client, state.Endpoint!, "/health", TimeSpan.FromSeconds(10), cancellationToken: token);
    }
}
sealed class Owner(WebApplication app, State state) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Console.WriteLine("packed-host: stopping Kestrel");
        await app.StopAsync(deadline.Token);
        await app.DisposeAsync();
        state.Cleaned++;
        Console.WriteLine("packed-host: Kestrel cleanup completed");
    }
}
