using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Validation;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceReusePackageProofTests
{
    private const string Version = "0.2.0-proof";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task AdmissionRecordsActualArchiveAndAssemblyHashes()
    {
        using var fixture = new FeedFixture();
        var result = await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit);
        Assert.Equal(7, result.Count);
        foreach (var package in result.Values)
        {
            Assert.Equal(Commit, package.SourceCommit);
            Assert.Equal(Version, package.Version);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(package.Archive))), package.Sha256);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(package.Id))), package.AssemblySha256);
        }
    }

    [Theory]
    [InlineData("different-build-version")]
    [InlineData("root-build-version")]
    public async Task CoherentBundledBuildVersionMayDifferFromPackageVersionWhenCandidateDllHashesAgree(string versionShape)
    {
        using var fixture = new FeedFixture(versionShape);
        var packages = await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit);
        Assert.Equal(7, packages.Count);
    }

    [Theory]
    [InlineData("support-open")]
    [InlineData("aspire-skew")]
    [InlineData("coverage-case")]
    [InlineData("second-framework-open")]
    [InlineData("tool-graph-skew")]
    [InlineData("tool-bundled-sha")]
    [InlineData("second-framework-missing")]
    [InlineData("source-skew")]
    [InlineData("missing-contracts")]
    [InlineData("archive-id")]
    [InlineData("archive-version")]
    public async Task BadCandidateIsRejectedBeforeCreatingOutput(string failure)
    {
        using var fixture = new FeedFixture(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit));
        var output = Path.Join(fixture.Root, "must-not-exist");
        var result = await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
            Path.Join(fixture.Root, "not-used-dotnet"), Commit, CancellationToken.None);
        Assert.Equal(1, result);
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancelledAdmissionCreatesNoConsumerOutput()
    {
        using var fixture = new FeedFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = Path.Join(fixture.Root, "must-not-exist");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceReusePackageProof.RunAsync(fixture.Root,
            Version, output, Path.Join(fixture.Root, "not-used-dotnet"), Commit, cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    private sealed class FeedFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("evidence-packed-admission-").FullName;
        internal FeedFixture(string? failure = null)
        {
            foreach (var id in EvidenceReusePackageProof.RequiredPackageIds)
            {
                if (failure == "missing-contracts" && id.EndsWith("Evidence.Contracts", StringComparison.Ordinal)) continue;
                var target = id switch
                {
                    "ForgeTrust.AppSurface.Evidence.Coverage" => "ForgeTrust.AppSurface.Evidence.Contracts",
                    "ForgeTrust.AppSurface.Coverage" or "ForgeTrust.AppSurface.Evidence.Aspire" => "ForgeTrust.AppSurface.Evidence.Coverage",
                    _ => null
                };
                XElement Group(bool wrong) => new("group", new XAttribute("targetFramework", wrong ? "net9.0" : "net10.0"), (target is null || (wrong && failure == "second-framework-missing")) ? null :
                    new XElement("dependency", new XAttribute("id", failure == "coverage-case" && id.EndsWith(".Coverage", StringComparison.Ordinal) ? target.ToLowerInvariant() : target),
                        new XAttribute("version", (wrong && failure == "second-framework-open") || (failure == "support-open" && id.EndsWith("Evidence.Coverage", StringComparison.Ordinal))
                            ? $"[{Version},)" : failure == "aspire-skew" && id.EndsWith("Evidence.Aspire", StringComparison.Ordinal) ? "[0.1.0]" : $"[{Version}]")));
                var document = new XDocument(new XElement("package", new XElement("metadata",
                    new XElement("id", failure == "archive-id" && target is not null ? id.ToLowerInvariant() : id),
                    new XElement("version", failure == "archive-version" && target is not null ? "0.0.0" : Version),
                    new XElement("repository", new XAttribute("commit", failure == "source-skew" && target is not null ? new string('a', 40) : Commit)),
                    new XElement("dependencies", Group(false), Group(true)))));
                using var archive = ZipFile.Open(Path.Join(Root, $"{id}.{Version}.nupkg"), ZipArchiveMode.Create);
                using (var writer = new StreamWriter(archive.CreateEntry(id + ".nuspec").Open())) writer.Write(document);
                var dllPath = id.EndsWith(".Cli", StringComparison.Ordinal) ? "tools/net10.0/any/" : "lib/net10.0/";
                using (var writer = new StreamWriter(archive.CreateEntry(dllPath + id + ".dll").Open())) writer.Write(id);
                if (id == "ForgeTrust.AppSurface.Cli")
                {
                    var libraries = new Dictionary<string, object>();
                    foreach (var name in new[] { id, "ForgeTrust.AppSurface.Evidence.Cli", "ForgeTrust.AppSurface.Evidence.Contracts", "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Coverage" })
                    {
                        if (name != id)
                        {
                            using var bundledWriter = new StreamWriter(archive.CreateEntry(dllPath + name + ".dll").Open());
                            bundledWriter.Write(failure == "tool-bundled-sha" ? "tampered" : name);
                        }
                        libraries[name + "/" + (failure == "tool-graph-skew" && name.EndsWith("Evidence.Coverage", StringComparison.Ordinal) ? "0.0.0"
                            : failure == "different-build-version" || failure == "root-build-version" && name == id ? "0.1.0" : Version)] = new { type = "project" };
                    }
                    using var writer = new StreamWriter(archive.CreateEntry(dllPath + id + ".deps.json").Open());
                    writer.Write(JsonSerializer.Serialize(new { libraries }));
                }
            }
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
