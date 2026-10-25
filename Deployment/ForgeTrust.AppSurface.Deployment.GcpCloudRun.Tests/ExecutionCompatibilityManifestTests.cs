using System.Security.Cryptography;
using System.Text.Json;

public sealed class ExecutionCompatibilityManifestTests
{
    [Fact]
    public void ManifestHashesEveryFixtureWorksheetAndReplayScript()
    {
        // protects=offline evidence provenance; fails_when=a checked-in fixture or worksheet drifts from its
        // manifest; no_claim=hashes authenticate a publisher or attest to a live provider read.
        var root = FindFixtureRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "compatibility.json")));

        foreach (var item in manifest.RootElement.GetProperty("worksheets").EnumerateArray())
        {
            var worksheetPath = item.GetProperty("path").GetString()!;
            AssertHash(root, worksheetPath, item.GetProperty("sha256").GetString()!);
            using var worksheet = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, worksheetPath)));
            foreach (var fixtureName in new[] { "positiveFixture", "negativeFixture" })
            {
                var fixture = worksheet.RootElement.GetProperty(fixtureName);
                AssertHash(Path.GetDirectoryName(Path.Combine(root, worksheetPath))!, fixture.GetProperty("path").GetString()!, fixture.GetProperty("sha256").GetString()!);
            }
        }

        foreach (var item in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
            AssertHash(root, item.GetProperty("path").GetString()!, item.GetProperty("sha256").GetString()!);

        var replay = manifest.RootElement.GetProperty("tool").GetProperty("replayScript");
        AssertHash(root, replay.GetProperty("path").GetString()!, replay.GetProperty("sha256").GetString()!);
        Assert.Equal("candidate-not-certified", manifest.RootElement.GetProperty("status").GetString());
        Assert.False(manifest.RootElement.GetProperty("tool").GetProperty("formatterReplay").GetProperty("credentialsUsed").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("tool").GetProperty("formatterReplay").GetProperty("networkRead").GetBoolean());
        var counterReplay = manifest.RootElement.GetProperty("tool").GetProperty("formatterReplay").GetProperty("counterDefaultReplay");
        Assert.True(counterReplay.GetProperty("equal").GetBoolean());
        Assert.Equal(5, counterReplay.GetProperty("scope").GetArrayLength());
    }

    [Fact]
    public void PositiveFixturesRequireSucceededCountAndPreserveOmittedOtherCounters()
    {
        // protects=truthful captured shape; fails_when=omitted counter fields are fabricated in the fixtures;
        // no_claim=the local SDK replay alone is live-cloud compatibility evidence.
        var root = FindFixtureRoot();
        foreach (var path in new[] { "schema-preparation/execution.json", "catalog-refresh/execution.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)));
            var status = document.RootElement.GetProperty("status");
            Assert.Equal(1, status.GetProperty("succeededCount").GetInt32());
            var names = status.EnumerateObject().Select(p => p.Name).ToArray();
            foreach (var omitted in new[] { "runningCount", "failedCount", "cancelledCount", "retriedCount" })
                Assert.DoesNotContain(omitted, names);
        }
    }

    private static void AssertHash(string root, string relativePath, string expected)
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expected, actual);
    }

    private static string FindFixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployment", "ForgeTrust.AppSurface.Deployment.GcpCloudRun.Tests", "Fixtures", "ExecutionEvidence", "v1");
            if (File.Exists(Path.Combine(candidate, "compatibility.json")))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Execution evidence fixture root was not found.");
    }
}
