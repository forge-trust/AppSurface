using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Exercises the release evidence path against a real producer ZIP and five on-disk host receipts.</summary>
public sealed class TailwindAggregatePublicationIntegrationTests : IDisposable
{
    private const string PackageId = "ForgeTrust.AppSurface.Web.Tailwind";
    private const string Version = "1.2.3-ci.798";
    private static readonly string[] Rids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];
    private readonly string _root = TestPathUtils.PathUnder("/private/tmp", "tailwind-aggregate-publication", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Aggregate_ValidatesRealProducerBundleAndFiveReceipts_AndRejectsEvidenceMutations()
    {
        using var fixture = await Fixture.CreateAsync(_root);

        var aggregate = await fixture.AggregateAsync("aggregate-valid");
        Assert.True(aggregate.Succeeded,
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(Path.GetDirectoryName(aggregate.ReportPath)!, "summary.md")) + "\n" +
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(Path.GetDirectoryName(aggregate.ReportPath)!, "diagnostics.json")));
        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(aggregate.ReportPath)))
        {
            Assert.Equal("appsurface-tailwind-native-host-evidence-v2", json.RootElement.GetProperty("schema").GetString());
            Assert.Equal(5, json.RootElement.GetProperty("hosts").GetArrayLength());
            Assert.Contains(json.RootElement.GetProperty("files").EnumerateArray(), item => item.GetProperty("path").GetString() == "summary.md");
        }

        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var entry = manifest.Entries.Single();
        var planned = new PlannedPackageArtifact(entry, TestPathUtils.PathUnder(fixture.Bundle, entry.ArtifactFileName));
        var aggregateDirectory = Path.GetDirectoryName(aggregate.ReportPath)!;
        var preflightReport = TestPathUtils.PathUnder(_root, "publish-preflight-report");
        var publicationDirectory = TestPathUtils.PathUnder(_root, "prepared-publication");
        var request = new TailwindPublicationRequest(
            fixture.Repository, fixture.Bundle, fixture.ManifestPath,
            TestPathUtils.PathUnder(fixture.Bundle, TailwindProofSubjectService.FileName), "501", fixture.SubjectHash,
            "12345", "901", fixture.SourceCommit, aggregateDirectory, "702", Sha256(File.ReadAllBytes(aggregate.ReportPath)),
            publicationDirectory, TestPathUtils.PathUnder(preflightReport, "publication-start-receipt.json"), "", preflightReport);
        var prepared = await TailwindEvidenceWorkflow.PreparePublicationAsync(request, manifest, [planned], CancellationToken.None);
        var stagedPath = TestPathUtils.PathUnder(publicationDirectory, entry.ArtifactFileName);
        var stagedHash = await PackageHash.ComputeSha512Async(stagedPath, CancellationToken.None);
        var stagedWriteTime = File.GetLastWriteTimeUtc(stagedPath);

        var startRequest = request with
        {
            PublicationStartArtifactId = "703",
            ReportDirectory = TestPathUtils.PathUnder(_root, "publication-start-validation-report")
        };
        var validated = await TailwindEvidenceWorkflow.ValidatePublicationStartAsync(startRequest, manifest, [planned], CancellationToken.None);
        Assert.Single(prepared.Packages);
        Assert.Equal(prepared.NativeInvocationId, validated.NativeInvocationId);
        Assert.Equal(stagedHash, await PackageHash.ComputeSha512Async(stagedPath, CancellationToken.None));
        Assert.Equal(stagedWriteTime, File.GetLastWriteTimeUtc(stagedPath));
        Assert.True(File.Exists(TestPathUtils.PathUnder(startRequest.ReportDirectory, "diagnostics.json")));

        var stagedBytes = await File.ReadAllBytesAsync(stagedPath);
        await File.AppendAllTextAsync(stagedPath, "substituted package bytes");
        var changedPackage = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ValidatePublicationStartAsync(startRequest with
            {
                ReportDirectory = TestPathUtils.PathUnder(_root, "changed-package-report")
            }, manifest, [planned], CancellationToken.None));
        Assert.Contains("Prepared publication file", changedPackage.Message, StringComparison.Ordinal);
        await File.WriteAllBytesAsync(stagedPath, stagedBytes);

        var originalStartReceipt = await File.ReadAllBytesAsync(request.PublicationStartReceiptPath);
        var changedReceipt = JsonNode.Parse(originalStartReceipt)!;
        changedReceipt["aggregateArtifactId"] = "999";
        await File.WriteAllTextAsync(request.PublicationStartReceiptPath, changedReceipt.ToJsonString());
        var changedAuthority = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ValidatePublicationStartAsync(startRequest with
            {
                ReportDirectory = TestPathUtils.PathUnder(_root, "changed-authority-report")
            }, manifest, [planned], CancellationToken.None));
        Assert.Contains("aggregateArtifactId", changedAuthority.Message, StringComparison.Ordinal);
        await File.WriteAllBytesAsync(request.PublicationStartReceiptPath, originalStartReceipt);

        await File.AppendAllTextAsync(aggregate.ReportPath, "\n");
        var changedAggregate = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ValidatePublicationStartAsync(startRequest with
            {
                ReportDirectory = TestPathUtils.PathUnder(_root, "changed-aggregate-report")
            }, manifest, [planned], CancellationToken.None));
        Assert.Contains("SHA-256", changedAggregate.Message, StringComparison.Ordinal);

        var originalReceipt = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(fixture.Evidence, Rids[0], "tailwind-native-host-proof.json"));
        foreach (var mutation in new[] { "missing-host", "mutated-host-identity", "mutated-cli-hash", "duplicate-receipt-field" })
        {
            if (mutation != "missing-host") await File.WriteAllBytesAsync(TestPathUtils.PathUnder(fixture.Evidence, Rids[0], "tailwind-native-host-proof.json"), originalReceipt);
            fixture.ApplyMutation(mutation);
            var failed = await fixture.AggregateAsync("aggregate-" + mutation);
            Assert.False(failed.Succeeded);
            Assert.False(File.Exists(failed.ReportPath));
            var diagnosticPath = TestPathUtils.PathUnder(Path.GetDirectoryName(failed.ReportPath)!, "diagnostics.json");
            Assert.True(File.Exists(diagnosticPath));
            using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(diagnosticPath));
            Assert.Equal("failed", diagnostics.RootElement.GetProperty("status").GetString());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private Fixture(string root, string repository, string sourceCommit, string bundle, string manifestPath, string subjectHash,
            string evidence, string map)
        {
            _root = root;
            Repository = repository;
            SourceCommit = sourceCommit;
            Bundle = bundle;
            ManifestPath = manifestPath;
            SubjectHash = subjectHash;
            Evidence = evidence;
            Map = map;
        }

        public string Repository { get; }
        public string SourceCommit { get; }
        public string Bundle { get; }
        public string ManifestPath { get; }
        public string SubjectHash { get; }
        public string Evidence { get; }
        public string Map { get; }

        public static async Task<Fixture> CreateAsync(string parent)
        {
            var root = TestPathUtils.PathUnder(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourceRoot = FindRepositoryRoot();
            var commit = await Git(sourceRoot, "rev-parse", "HEAD");
            var repository = TestPathUtils.PathUnder(root, "clean-source");
            await Git(sourceRoot, "clone", "--local", "--no-hardlinks", "--quiet", sourceRoot, repository);
            Assert.Equal(commit, await Git(repository, "rev-parse", "HEAD"));

            var bundle = TestPathUtils.PathUnder(root, "producer-bundle");
            Directory.CreateDirectory(bundle);
            var fileName = $"{PackageId}.{Version}.nupkg";
            var archivePath = TestPathUtils.PathUnder(bundle, fileName);
            var releaseManifest = ReleaseManifest();
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                Add(archive, $"{PackageId}.nuspec", $"<package><metadata><id>{PackageId}</id><version>{Version}</version></metadata></package>");
                Add(archive, "build/tailwind.release.json", releaseManifest);
                Add(archive, "build/tailwind.version", "4.1.0");
                Add(archive, "contentFiles/any/any/tailwind.css", ".fixture{color:red}");
            }

            var entry = new PackageArtifactManifestEntry(PackageId, "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj",
                "publish", fileName, PackageHash.ComputeSha512(archivePath), false);
            var manifest = new PackageArtifactManifest(1, Version, DateTimeOffset.Parse("2026-09-23T00:00:00Z"), [entry]);
            var manifestPath = TestPathUtils.PathUnder(bundle, "package-artifact-manifest.json");
            await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
            var package = new TailwindSubjectPackage(PackageId, Version, fileName, entry.Sha512);
            var subject = await TailwindProofSubjectService.CreateAsync(bundle, manifestPath, "12345", "901", "1", commit, [package], CancellationToken.None);
            var subjectHash = await TailwindProofSubjectService.WriteAsync(subject, bundle, CancellationToken.None);

            var evidence = TestPathUtils.PathUnder(root, "host-evidence");
            Directory.CreateDirectory(evidence);
            var hostMap = new List<object>();
            for (var index = 0; index < Rids.Length; index++)
            {
                var rid = Rids[index];
                var host = TestPathUtils.PathUnder(evidence, rid);
                var packages = TestPathUtils.PathUnder(host, "packages");
                var payloadRoot = TestPathUtils.PathUnder(host, "payload");
                Directory.CreateDirectory(packages);
                Directory.CreateDirectory(payloadRoot);
                File.Copy(archivePath, TestPathUtils.PathUnder(packages, fileName));
                var payloadFiles = new List<object>();
                foreach (var item in new[]
                {
                    (Path: "build/tailwind.release.json", Bytes: Encoding.UTF8.GetBytes(releaseManifest)),
                    (Path: "build/tailwind.version", Bytes: Encoding.UTF8.GetBytes("4.1.0")),
                    (Path: "contentFiles/any/any/tailwind.css", Bytes: Encoding.UTF8.GetBytes(".fixture{color:red}"))
                })
                {
                    var dest = TestPathUtils.PathUnder(payloadRoot, item.Path.Split('/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllBytesAsync(dest, item.Bytes);
                    payloadFiles.Add(new { packageRelativePath = item.Path, evidencePath = "payload/" + item.Path, sha256 = Sha256(item.Bytes) });
                }
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(host, "native-consumer-report.md"), "# Fixture native consumer evidence\n");
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(host, "summary.md"), "# Native host proof\n\nStatus: succeeded\n");
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(host, "diagnostics.json"), "{\"schema\":\"appsurface-tailwind-diagnostic-v1\",\"status\":\"succeeded\"}\n");

                var hostOs = rid.StartsWith("linux", StringComparison.Ordinal) ? "Linux" : rid.StartsWith("osx", StringComparison.Ordinal) ? "macOS" : "Windows";
                var arch = rid.EndsWith("arm64", StringComparison.Ordinal) ? "Arm64" : "X64";
                var asset = BinaryFor(rid);
                var receipt = new
                {
                    schema = "appsurface-tailwind-native-host-proof-v2",
                    status = "succeeded",
                    repositoryId = subject.RepositoryId,
                    sourceCommit = subject.SourceCommit,
                    producerRunId = subject.ProducerRunId,
                    producerAttempt = subject.ProducerAttempt,
                    producerArtifactId = "501",
                    subjectSha256 = subjectHash,
                    artifactManifestSha256 = subject.ArtifactManifestSha256,
                    packageVersion = subject.PackageVersion,
                    payloadProjectionVersion = 1,
                    nativeInvocationId = "native-798-1",
                    nativeRunId = "902",
                    nativeAttempt = "1",
                    expectedRid = rid,
                    observedRid = rid,
                    hostOs,
                    osArchitecture = rid == "win-x64" ? "X64" : arch,
                    processArchitecture = rid == "win-x64" ? "X64" : arch,
                    runnerLabel = "fixture-runner",
                    sdkVersion = "10.0.100",
                    firstPartyPackages = new[] { new { packageId = PackageId, packageVersion = Version, producerSha512 = entry.Sha512,
                        restoredSha512 = entry.Sha512, archivePath = $"packages/{fileName}", payloadVerified = true, payloadFiles } },
                    tailwindManifestSha256 = subject.TailwindManifestSha256,
                    restoredTailwindManifestSha256 = subject.TailwindManifestSha256,
                    binaryName = asset.BinaryName,
                    binarySha256 = asset.Hash,
                    checks = new { generatedCss = true, hostCacheBinary = true, noRuntimeCompanionDependency = true, noNativeConsumerOutput = true, postBuildPayloadUnchanged = true },
                    files = Inventory(host, "tailwind-native-host-proof.json"),
                    diagnosticPath = "native-consumer-report.md"
                };
                await File.WriteAllBytesAsync(TestPathUtils.PathUnder(host, "tailwind-native-host-proof.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, PackageArtifactJson.Options));
                hostMap.Add(new { rid, artifactId = (501 + index).ToString(), directory = rid });
            }

            var map = TestPathUtils.PathUnder(root, "host-map.json");
            await File.WriteAllBytesAsync(map, JsonSerializer.SerializeToUtf8Bytes(hostMap));
            return new Fixture(root, repository, commit, bundle, manifestPath, subjectHash, evidence, map);
        }

        public async Task<(bool Succeeded, string ReportPath)> AggregateAsync(string reportName)
        {
            var report = TestPathUtils.PathUnder(_root, reportName);
            var options = new TailwindCommandOptions(
                "aggregate", TailwindProofSubjectService.FileName, "501", SubjectHash, "12345", "901", "1", SourceCommit,
                "native-798-1", null, null, report, Evidence, Map, null, null, null, null, null, null, null, []);
            var result = await TailwindEvidenceWorkflow.AggregateAsync(Repository, Bundle, ManifestPath, options, CancellationToken.None);
            return (result.Succeeded, result.ReportPath);
        }

        public void ApplyMutation(string mutation)
        {
            var rid = mutation == "missing-host" ? Rids[^1] : Rids[0];
            var receiptPath = TestPathUtils.PathUnder(Evidence, rid, "tailwind-native-host-proof.json");
            if (mutation == "missing-host") { File.Delete(receiptPath); return; }
            var json = File.ReadAllText(receiptPath);
            if (mutation == "mutated-host-identity")
            {
                json = Regex.Replace(json, "\\\"observedRid\\\"\\s*:\\s*\\\"linux-x64\\\"", "\"observedRid\":\"osx-x64\"", RegexOptions.CultureInvariant);
            }
            else if (mutation == "mutated-cli-hash")
            {
                json = Regex.Replace(json, "\\\"binarySha256\\\"\\s*:\\s*\\\"[0-9a-f]{64}\\\"", $"\"binarySha256\":\"{new string('f', 64)}\"", RegexOptions.CultureInvariant);
            }
            else
            {
                json = Regex.Replace(json, "\\\"status\\\"\\s*:\\s*\\\"succeeded\\\"", "$0,\n  \"status\": \"succeeded\"", RegexOptions.CultureInvariant);
            }
            Assert.NotEqual(File.ReadAllText(receiptPath), json);
            File.WriteAllText(receiptPath, json);
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }

    private static object[] Inventory(string root, params string[] excluded)
    {
        var exclude = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(item => !exclude.Contains(item.Relative))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => (object)new { path = item.Relative, sha256 = Sha256(File.ReadAllBytes(item.Path)) }).ToArray();
    }

    private static string ReleaseManifest() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        version = "4.1.0",
        baseUrl = "https://example.test/tailwind",
        assets = new[]
        {
            new { rid = "linux-x64", binaryName = "tailwindcss-linux-x64", sha256 = new string('a', 64) },
            new { rid = "linux-arm64", binaryName = "tailwindcss-linux-arm64", sha256 = new string('b', 64) },
            new { rid = "osx-x64", binaryName = "tailwindcss-macos-x64", sha256 = new string('c', 64) },
            new { rid = "osx-arm64", binaryName = "tailwindcss-macos-x64", sha256 = new string('d', 64) },
            new { rid = "win-x64", binaryName = "tailwindcss-windows-x64.exe", sha256 = new string('e', 64) }
        }
    });

    private static (string BinaryName, string Hash) BinaryFor(string rid) => rid switch
    {
        "linux-x64" => ("tailwindcss-linux-x64", new string('a', 64)),
        "linux-arm64" => ("tailwindcss-linux-arm64", new string('b', 64)),
        "osx-x64" => ("tailwindcss-macos-x64", new string('c', 64)),
        "osx-arm64" => ("tailwindcss-macos-x64", new string('d', 64)),
        _ => ("tailwindcss-windows-x64.exe", new string('e', 64))
    };

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null && !File.Exists(TestPathUtils.PathUnder(current.FullName, "ForgeTrust.AppSurface.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root for the exact-source fixture.");
    }

    private static async Task<string> Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch git for test setup.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return (await stdout).Trim();
    }

}
