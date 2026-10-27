using System.Text.Json;
using System.Text.Json.Nodes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageStoragePresentationTests
{
    [Fact]
    public async Task HumanInspectAndMaintainExposeRelevantBoundsAndPreviewState()
    {
        using var temp = TestDirectory.Create();
        using var configured = new Domain(Path.Join(temp.Path, "missing-store"));
        var repository = temp.CreateDirectory("repo/TestResults");
        File.WriteAllText(Path.Join(repository, "coverage.xml"), "12345");
        using var inspection = new FakeInMemoryConsole();
        await new CoverageStorageInspectCommand { Root = Path.Join(temp.Path, "repo") }.ExecuteAsync(inspection, default);
        Assert.Contains("5 B across 1 files", inspection.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("100,000 entries; 30 seconds", inspection.ReadOutputString(), StringComparison.Ordinal);
        using var maintenance = new FakeInMemoryConsole();
        await new CoverageStorageMaintainCommand().ExecuteAsync(maintenance, default);
        Assert.Contains("candidates: 0; apply: False", maintenance.ReadOutputString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(temp.Path, "missing-store")));
    }

    [Theory]
    [InlineData(2048L, "2.0 KiB")]
    [InlineData(2097152L, "2.0 MiB")]
    [InlineData(2147483648L, "2.00 GiB")]
    public async Task HumanStatusFormatsDeclaredCurrentInventoryWithoutAllocatingPayloads(long length, string displayed)
    {
        using var temp = TestDirectory.Create();
        var root = Path.Join(temp.Path, "store");
        using var configured = new Domain(root);
        var repository = temp.CreateDirectory("repo");
        var output = temp.CreateDirectory("repo/output");
        using (var store = CoverageArtifactStore.Open(root))
            await store.RegisterAsync(new(new CoverageArtifactRunManifest
            { RunId = Guid.NewGuid().ToString("N"), Outcome = "success", Version = "fixture", CreatedAtUtc = DateTimeOffset.UtcNow },
                [new("declared.xml", length, new string('a', 64))], output, repository), default);
        using var console = new FakeInMemoryConsole();
        await new CoverageStorageStatusCommand().ExecuteAsync(console, default);
        Assert.Contains(displayed + " current", console.ReadOutputString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, "declared.xml")));
    }

    [Fact]
    public async Task CapacityObservationReturnsPartialAccountingWithActionableDiagnostic()
    {
        using var temp = TestDirectory.Create();
        var root = Path.Join(temp.Path, "store");
        using var configured = new Domain(root);
        using (CoverageArtifactStore.Open(root)) { }
        var registryPath = Path.Join(root, "registry.json");
        var registry = JsonNode.Parse(File.ReadAllText(registryPath))!;
        registry["IsComplete"] = false;
        registry["IncompleteReason"] = "metadata capacity reached";
        registry["OverflowObservations"] = new JsonArray(new JsonObject
        {
            ["RunId"] = Guid.NewGuid().ToString("N"),
            ["OutputIdentity"] = "observed-only",
            ["CurrentBytes"] = 123,
            ["ObservedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        });
        File.WriteAllText(registryPath, registry.ToJsonString());
        using var console = new FakeInMemoryConsole();
        await Assert.ThrowsAsync<CliFx.CommandException>(async () =>
            await new CoverageStorageStatusCommand { Json = true }.ExecuteAsync(console, default));
        using var result = JsonDocument.Parse(console.ReadOutputString());
        Assert.Equal("partial", result.RootElement.GetProperty("status").GetString());
        Assert.Contains(result.RootElement.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == "ASCOV207");
        Assert.Equal("none", result.RootElement.GetProperty("sideEffects").GetString());
    }

    private sealed class Domain : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        internal Domain(string root)
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", root);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _previous);
    }
}
