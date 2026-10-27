using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageArtifactRegistryObservationTests
{
    [Theory]
    [InlineData("overflow-null")]
    [InlineData("overflow-run")]
    [InlineData("overflow-identity")]
    [InlineData("overflow-bytes")]
    [InlineData("receipt-null")]
    [InlineData("receipt-run")]
    [InlineData("receipt-outcome")]
    [InlineData("receipt-bytes")]
    [InlineData("receipt-reason")]
    public async Task InvalidObservationMetadataIsPreservedAndCannotReplacePrimaryRunOutcome(string fault)
    {
        using var temp = TestDirectory.Create();
        var root = Path.Join(temp.Path, "store");
        using (CoverageArtifactStore.Open(root)) { }
        var registryPath = Path.Join(root, "registry.json");
        var registry = JsonNode.Parse(File.ReadAllText(registryPath))!;
        var id = Guid.NewGuid().ToString("N");
        var overflow = new JsonObject
        {
            ["RunId"] = id,
            ["OutputIdentity"] = "observed",
            ["CurrentBytes"] = 1,
            ["ObservedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        var receipt = new JsonObject
        {
            ["RunId"] = id,
            ["Outcome"] = "success",
            ["StoredBytes"] = 1,
            ["ExpiredAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["Reason"] = "age-or-byte-policy"
        };
        switch (fault)
        {
            case "overflow-null": registry["OverflowObservations"] = new JsonArray((JsonNode?)null); break;
            case "overflow-run": overflow["RunId"] = "invalid"; break;
            case "overflow-identity": overflow["OutputIdentity"] = null; break;
            case "overflow-bytes": overflow["CurrentBytes"] = -1; break;
            case "receipt-null": registry["ExpiryReceipts"] = new JsonArray((JsonNode?)null); break;
            case "receipt-run": receipt["RunId"] = "invalid"; break;
            case "receipt-outcome": receipt["Outcome"] = "passed"; break;
            case "receipt-bytes": receipt["StoredBytes"] = -1; break;
            case "receipt-reason": receipt["Reason"] = null; break;
        }
        if (fault.StartsWith("overflow-", StringComparison.Ordinal) && fault != "overflow-null")
            registry["OverflowObservations"] = new JsonArray(overflow);
        if (fault.StartsWith("receipt-", StringComparison.Ordinal) && fault != "receipt-null")
            registry["ExpiryReceipts"] = new JsonArray(receipt);
        File.WriteAllText(registryPath, registry.ToJsonString());
        var original = File.ReadAllBytes(registryPath);
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.Open(root));
        Assert.Throws<InvalidDataException>(() => CoverageArtifactStore.OpenReadOnly(root));
        var before = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", root);
        try
        {
            var repository = temp.CreateDirectory("repo");
            var output = temp.CreateDirectory("repo/output");
            var payload = Path.Join(output, "coverage.xml");
            File.WriteAllText(payload, "failed run evidence");
            using var errors = new StringWriter();
            using var session = await CoverageArtifactSession.BeginAsync(output, repository, false,
                CoverageTextWriters.Create(TextWriter.Null, errors), TimeProvider.System, default);
            session.Inventory.RecordFile(payload);
            await session.CompleteAsync("failed", new("revision", "unknown", "policy"));
            Assert.Contains("ASCOV201 storage:", errors.ToString(), StringComparison.Ordinal);
            Assert.Equal("failed run evidence", File.ReadAllText(payload));
            Assert.False(File.Exists(Path.Join(output, CoverageArtifactSession.SidecarName)));
        }
        finally { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", before); }
        Assert.Equal(original, File.ReadAllBytes(registryPath));
    }
}
