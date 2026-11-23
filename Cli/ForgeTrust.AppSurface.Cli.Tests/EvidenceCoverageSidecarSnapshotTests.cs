using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceCoverageSidecarSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SidecarDigestBindsTheParsedBytesEvenWhenTheFileChangesAfterParsing(bool mutate)
    {
        using var directory = TestDirectory.Create();
        var coverage = TestPathUtils.PathUnder(directory.Path, "coverage");
        Directory.CreateDirectory(coverage);
        var reports = new Dictionary<string, string>
        {
            ["coverage.cobertura.xml"] = "<coverage />",
            ["coverage-gate.json"] = "{}",
            ["coverage-gate.md"] = "passed",
            ["timings.json"] = "{\"projects\":[]}",
            ["summary.txt"] = "summary",
        };
        var publications = new List<CoverageArtifactInventoryEntry>();
        foreach (var (name, contents) in reports)
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            File.WriteAllBytes(TestPathUtils.PathUnder(coverage, name), bytes);
            publications.Add(new(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        var sidecar = TestPathUtils.PathUnder(coverage, CoverageArtifactSession.SidecarName);
        var run = new CoverageArtifactCurrentRun(new()
        { RunId = Guid.NewGuid().ToString("N"), Outcome = "success" }, publications, coverage, directory.Path);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var original = JsonSerializer.SerializeToUtf8Bytes(run, options);
        File.WriteAllBytes(sidecar, original);
        var coverageFile = TestPathUtils.PathUnder(coverage, "coverage.cobertura.xml");
        var execution = new CoverageEvidenceExecutionResult(new CoverageRunResult(true, coverage, coverageFile),
            new CoverageGateResult(coverageFile, new(1, 1, 100), new(1, 1, 100), 95, 85, true,
                TestPathUtils.PathUnder(coverage, "coverage-gate.json"), TestPathUtils.PathUnder(coverage, "coverage-gate.md")));
        var mutated = JsonSerializer.SerializeToUtf8Bytes(run with
        { ManifestBase = run.ManifestBase with { Outcome = "failure" } }, options);

        CoverageEvidenceInventory Capture() => CoverageEvidenceInventory.Capture(directory.Path, execution,
            publications, CancellationToken.None, () =>
            {
                if (mutate) File.WriteAllBytes(sidecar, mutated);
            });

        if (mutate)
        {
            var error = Assert.Throws<CoverageEvidenceInventoryException>(Capture);
            Assert.Equal("inventory-file-changed", error.Reason);
            Assert.Equal(mutated, File.ReadAllBytes(sidecar));
        }
        else
        {
            var captured = Assert.Single(Capture().Files, file => file.RelativePath.EndsWith(CoverageArtifactSession.SidecarName, StringComparison.Ordinal));
            Assert.Equal(original.Length, captured.LengthBytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), captured.Sha256);
        }
    }
}
