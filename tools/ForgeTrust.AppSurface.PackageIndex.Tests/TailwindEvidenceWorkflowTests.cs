using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindEvidenceWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tailwind-evidence-workflow-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadHostArtifactMap_AcceptsExactlyTheOrderedFiveDistinctHosts()
    {
        var path = WriteMap(
        [
            ("linux-x64", "101", "linux-x64"),
            ("linux-arm64", "102", "linux-arm64"),
            ("osx-x64", "103", "osx-x64"),
            ("osx-arm64", "104", "osx-arm64"),
            ("win-x64", "105", "win-x64")
        ]);

        var hosts = await TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None);

        Assert.Equal(
            ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"],
            hosts.Select(host => host.Rid));
        Assert.Equal(["101", "102", "103", "104", "105"], hosts.Select(host => host.ArtifactId));
    }

    [Theory]
    [InlineData("missing-host")]
    [InlineData("foreign-host")]
    [InlineData("duplicate-host")]
    [InlineData("duplicate-artifact")]
    [InlineData("escaped-directory")]
    public async Task ReadHostArtifactMap_RejectsIncompleteForeignOrUnsafeMaps(string mutation)
    {
        var hosts = new List<(string Rid, string ArtifactId, string Directory)>
        {
            ("linux-x64", "101", "linux-x64"),
            ("linux-arm64", "102", "linux-arm64"),
            ("osx-x64", "103", "osx-x64"),
            ("osx-arm64", "104", "osx-arm64"),
            ("win-x64", "105", "win-x64")
        };

        switch (mutation)
        {
            case "missing-host":
                hosts.RemoveAt(hosts.Count - 1);
                break;
            case "foreign-host":
                hosts[4] = ("freebsd-x64", "105", "freebsd-x64");
                break;
            case "duplicate-host":
                hosts[4] = ("osx-x64", "105", "osx-x64");
                break;
            case "duplicate-artifact":
                hosts[4] = ("win-x64", "101", "win-x64");
                break;
            case "escaped-directory":
                hosts[0] = ("linux-x64", "101", "../outside");
                break;
        }

        var path = WriteMap(hosts);

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ReadHostArtifactMap_RejectsNonCanonicalArtifactIds()
    {
        var path = WriteMap(
        [
            ("linux-x64", "01", "linux-x64"),
            ("linux-arm64", "102", "linux-arm64"),
            ("osx-x64", "103", "osx-x64"),
            ("osx-arm64", "104", "osx-arm64"),
            ("win-x64", "105", "win-x64")
        ]);

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ReadHostArtifactMap_RejectsNumericArtifactIds()
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, "numeric-host-artifacts.json");
        await File.WriteAllTextAsync(path, """
            [{"rid":"linux-x64","artifactId":101,"directory":"linux-x64"},
             {"rid":"linux-arm64","artifactId":"102","directory":"linux-arm64"},
             {"rid":"osx-x64","artifactId":"103","directory":"osx-x64"},
             {"rid":"osx-arm64","artifactId":"104","directory":"osx-arm64"},
             {"rid":"win-x64","artifactId":"105","directory":"win-x64"}]
            """);

        await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledProof_UsesIndependentBoundedCleanupForFailureDiagnostics()
    {
        var report = TestPathUtils.PathUnder(_root, "cancelled-proof");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await TailwindEvidenceWorkflow.WriteFailureReportBestEffortAsync(
            report, "native-build", new OperationCanceledException("interrupted"), cancelled.Token);

        var path = TestPathUtils.PathUnder(report, "diagnostics.json");
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal("cancelled", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("native-build", document.RootElement.GetProperty("stage").GetString());
        Assert.False(File.Exists(TestPathUtils.PathUnder(report, "tailwind-native-host-proof.json")));
    }

    private string WriteMap(IEnumerable<(string Rid, string ArtifactId, string Directory)> hosts)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "host-artifacts.json");
        var json = JsonSerializer.Serialize(hosts.Select(host => new
        {
            rid = host.Rid,
            artifactId = host.ArtifactId,
            directory = host.Directory
        }));
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
