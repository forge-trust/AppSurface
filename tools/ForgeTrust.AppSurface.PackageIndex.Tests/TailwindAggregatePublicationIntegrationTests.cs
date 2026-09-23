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
        var publisherEntries = await new TailwindPublicationEvidenceValidator().ValidateAsync(startRequest, manifest, [planned], CancellationToken.None);
        Assert.Single(prepared.Packages);
        Assert.Equal(stagedPath, Assert.Single(publisherEntries).ArtifactPath);
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
            await fixture.ApplyMutation(mutation);
            var failed = await fixture.AggregateAsync("aggregate-" + mutation);
            Assert.False(failed.Succeeded);
            Assert.False(File.Exists(failed.ReportPath));
            var diagnosticPath = TestPathUtils.PathUnder(Path.GetDirectoryName(failed.ReportPath)!, "diagnostics.json");
            Assert.True(File.Exists(diagnosticPath));
            using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(diagnosticPath));
            Assert.Equal("failed", diagnostics.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task ProgramRunAsync_DispatchesAggregateAndProducerBindingSuccess()
    {
        using var fixture = await Fixture.CreateAsync(_root);
        var aggregateReport = TestPathUtils.PathUnder(_root, "cli-aggregate-report");
        var bindingReport = TestPathUtils.PathUnder(_root, "cli-producer-binding-report");
        var bindingOutput = TestPathUtils.PathUnder(_root, "resolved-producer-binding.json");

        var aggregate = await fixture.RunEvidenceCliAsync("aggregate", aggregateReport,
            "--native-invocation-id", "native-798-1",
            "--evidence-input", fixture.Evidence,
            "--host-artifacts-map", fixture.Map);

        Assert.Equal(0, aggregate.ExitCode);
        Assert.Empty(aggregate.Stderr);
        Assert.Contains("aggregate succeeded", aggregate.Stdout, StringComparison.OrdinalIgnoreCase);
        var aggregatePath = TestPathUtils.PathUnder(aggregateReport, "tailwind-native-aggregate.json");
        using (var report = JsonDocument.Parse(await File.ReadAllBytesAsync(aggregatePath)))
        {
            Assert.Equal("appsurface-tailwind-native-host-evidence-v2", report.RootElement.GetProperty("schema").GetString());
            Assert.Equal(5, report.RootElement.GetProperty("hosts").GetArrayLength());
        }

        var binding = await fixture.RunEvidenceCliAsync("producer-binding", bindingReport,
            "--resolved-binding-output", bindingOutput);

        Assert.Equal(0, binding.ExitCode);
        Assert.Empty(binding.Stderr);
        Assert.Contains("Validated frozen producer binding", binding.Stdout, StringComparison.Ordinal);
        Assert.NotEqual(aggregateReport, bindingReport);
        Assert.True(File.Exists(TestPathUtils.PathUnder(aggregateReport, "diagnostics.json")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(bindingReport, "diagnostics.json")));
        using var resolved = JsonDocument.Parse(await File.ReadAllBytesAsync(bindingOutput));
        Assert.Equal("appsurface-tailwind-resolved-producer-binding-v1", resolved.RootElement.GetProperty("schema").GetString());
        Assert.Equal("901", resolved.RootElement.GetProperty("producerRunId").GetString());
        Assert.Equal("501", resolved.RootElement.GetProperty("producerArtifactId").GetString());

        var workflowReport = TestPathUtils.PathUnder(_root, "workflow-producer-binding-report");
        var workflowOutput = TestPathUtils.PathUnder(_root, "workflow-resolved-producer-binding.json");
        var options = fixture.CreateProducerBindingOptions(workflowReport);
        var writtenBinding = await TailwindEvidenceWorkflow.ValidateAndWriteProducerBindingAsync(
            fixture.Repository, fixture.Bundle, fixture.ManifestPath, options, workflowOutput, CancellationToken.None);

        Assert.Equal("901", writtenBinding.Subject.ProducerRunId);
        using var workflowResolved = JsonDocument.Parse(await File.ReadAllBytesAsync(workflowOutput));
        Assert.Equal("appsurface-tailwind-resolved-producer-binding-v1", workflowResolved.RootElement.GetProperty("schema").GetString());
        Assert.Equal(fixture.SubjectHash, workflowResolved.RootElement.GetProperty("subjectSha256").GetString());
        using var workflowDiagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(workflowReport, "diagnostics.json")));
        Assert.Equal("succeeded", workflowDiagnostics.RootElement.GetProperty("status").GetString());
        Assert.Equal("producer-binding", workflowDiagnostics.RootElement.GetProperty("stage").GetString());
        Assert.False(workflowDiagnostics.RootElement.GetProperty("releaseEligible").GetBoolean());
    }

    [Theory]
    [InlineData("missing-selected-payload", "incomplete or contains extra files")]
    [InlineData("extra-unselected-payload", "incomplete or contains extra files")]
    [InlineData("unsupported-assets-group", "Unsupported NuGet assets group")]
    [InlineData("missing-assets-inventory", "Aggregate file inventory is incomplete")]
    [InlineData("wrong-assets-target", "fixed net10.0 target")]
    [InlineData("multiple-assets-targets", "exactly one target")]
    [InlineData("missing-package-node", "must resolve exactly one target node")]
    [InlineData("duplicate-package-node", "must resolve exactly one target node")]
    [InlineData("payload-path-traversal", "Unsafe package path component")]
    [InlineData("changed-extracted-bytes", "not byte-identical to the producer archive")]
    public async Task Aggregate_RejectsInvalidHostAssetProjectionAndPayloadEvidence(string mutation, string expectedDiagnostic)
    {
        using var fixture = await Fixture.CreateAsync(_root);
        await fixture.ApplyMutation(mutation);

        var failed = await fixture.AggregateAsync("aggregate-" + mutation);

        Assert.False(failed.Succeeded);
        Assert.False(File.Exists(failed.ReportPath));
        var diagnosticsPath = TestPathUtils.PathUnder(Path.GetDirectoryName(failed.ReportPath)!, "diagnostics.json");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(diagnosticsPath));
        Assert.Equal("failed", diagnostics.RootElement.GetProperty("status").GetString());
        Assert.Contains(expectedDiagnostic, diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-required-rid-order", "exact ordered five-host set")]
    [InlineData("duplicate-host", "unknown or duplicate RID")]
    [InlineData("foreign-host", "unknown or duplicate RID")]
    [InlineData("unsafe-receipt-path", "Unsafe relative evidence path")]
    [InlineData("receipt-hash-mismatch", "Receipt hash mismatch")]
    [InlineData("inventory-omission", "Aggregate file inventory is incomplete")]
    [InlineData("inventory-surplus", "Aggregate file inventory is incomplete")]
    [InlineData("bad-status", "Evidence field 'status'")]
    [InlineData("bad-repository-binding", "Evidence field 'repositoryId'")]
    [InlineData("bad-source-binding", "Evidence field 'sourceCommit'")]
    [InlineData("bad-producer-artifact-binding", "Evidence field 'producerArtifactId'")]
    public async Task PublishPreflight_RejectsMutatedAggregateAuthorization(string mutation, string expectedDiagnostic)
    {
        using var fixture = await Fixture.CreateAsync(_root);
        var aggregate = await fixture.AggregateAsync("aggregate-authority-" + mutation);
        Assert.True(aggregate.Succeeded);

        var error = await fixture.RejectAggregateMutationAsync(aggregate.ReportPath, mutation);

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-checks", "native host receipt does not match the frozen")]
    [InlineData("failed-css-check", "did not prove 'generatedCss'")]
    [InlineData("missing-package-evidence", "native host receipt does not match the frozen")]
    [InlineData("empty-package-evidence", "closure count differs")]
    [InlineData("wrong-package-id", "omits or duplicates package")]
    [InlineData("failed-payload-check", "no successful payload verification")]
    [InlineData("bad-restored-archive-hash", "restoredSha512")]
    [InlineData("unsafe-archive-path", "Unsafe relative evidence path")]
    [InlineData("missing-payload-array", "must be an array")]
    [InlineData("wrong-binary-name", "selected binary")]
    [InlineData("changed-restored-manifest", "release manifest digest changed after restore")]
    [InlineData("wrong-diagnostic-path", "unexpected diagnostic path")]
    [InlineData("wrong-host-os", "hostOs")]
    [InlineData("wrong-process-architecture", "processArchitecture")]
    [InlineData("noncanonical-native-run-id", "native run ID")]
    public async Task Aggregate_RejectsUntrustedHostReceiptClaims(string mutation, string expectedDiagnostic)
    {
        using var fixture = await Fixture.CreateAsync(_root);
        var receiptPath = TestPathUtils.PathUnder(fixture.Evidence, Rids[0], "tailwind-native-host-proof.json");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
        var firstPackage = ((JsonArray)receipt["firstPartyPackages"]!)[0]!.AsObject();
        switch (mutation)
        {
            case "missing-checks": receipt.Remove("checks"); break;
            case "failed-css-check": receipt["checks"]!["generatedCss"] = false; break;
            case "missing-package-evidence": receipt.Remove("firstPartyPackages"); break;
            case "empty-package-evidence": receipt["firstPartyPackages"] = new JsonArray(); break;
            case "wrong-package-id": firstPackage["packageId"] = "ForgeTrust.Other"; break;
            case "failed-payload-check": firstPackage["payloadVerified"] = false; break;
            case "bad-restored-archive-hash": firstPackage["restoredSha512"] = new string('0', 128); break;
            case "unsafe-archive-path": firstPackage["archivePath"] = "../outside.nupkg"; break;
            case "missing-payload-array": firstPackage["payloadFiles"] = null; break;
            case "wrong-binary-name": receipt["binaryName"] = "untrusted"; break;
            case "changed-restored-manifest": receipt["restoredTailwindManifestSha256"] = new string('0', 64); break;
            case "wrong-diagnostic-path": receipt["diagnosticPath"] = "other.md"; break;
            case "wrong-host-os": receipt["hostOs"] = "FreeBSD"; break;
            case "wrong-process-architecture": receipt["processArchitecture"] = "Arm64"; break;
            case "noncanonical-native-run-id": receipt["nativeRunId"] = "01"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown host-receipt mutation.");
        }
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());

        var failed = await fixture.AggregateAsync("aggregate-host-claim-" + mutation);

        Assert.False(failed.Succeeded);
        Assert.False(File.Exists(failed.ReportPath));
        var diagnosticsPath = TestPathUtils.PathUnder(Path.GetDirectoryName(failed.ReportPath)!, "diagnostics.json");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(diagnosticsPath));
        Assert.Contains(expectedDiagnostic,
            diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Message").GetString(), StringComparison.Ordinal);
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
                Add(archive, "native/codec.bin", "native-fixture");
                Add(archive, "notes/unused.txt", "not selected by the assets graph");
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
                    (Path: "contentFiles/any/any/tailwind.css", Bytes: Encoding.UTF8.GetBytes(".fixture{color:red}")),
                    (Path: "native/codec.bin", Bytes: Encoding.UTF8.GetBytes("native-fixture"))
                })
                {
                    var dest = TestPathUtils.PathUnder(payloadRoot, item.Path.Split('/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllBytesAsync(dest, item.Bytes);
                    payloadFiles.Add(new { packageRelativePath = item.Path, evidencePath = "payload/" + item.Path, sha256 = Sha256(item.Bytes) });
                }
                var assetsPath = TestPathUtils.PathUnder(host, "consumer", "project.assets.json");
                Directory.CreateDirectory(Path.GetDirectoryName(assetsPath)!);
                var assets = new
                {
                    targets = new Dictionary<string, object>
                    {
                        ["net10.0"] = new Dictionary<string, object>
                        {
                            [$"{PackageId}/{Version}"] = new
                            {
                                type = "Package",
                                framework = ".NETCoreApp,Version=v10.0",
                                native = new Dictionary<string, object> { ["native/codec.bin"] = new { } }
                            }
                        }
                    }
                };
                await File.WriteAllBytesAsync(assetsPath, JsonSerializer.SerializeToUtf8Bytes(assets));
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

        public async Task<PackageIndexException> RejectAggregateMutationAsync(string aggregatePath, string mutation)
        {
            var aggregateDirectory = Path.GetDirectoryName(aggregatePath)!;
            var aggregate = JsonNode.Parse(await File.ReadAllTextAsync(aggregatePath))!.AsObject();
            var hosts = (JsonArray)aggregate["hosts"]!;
            var files = (JsonArray)aggregate["files"]!;
            switch (mutation)
            {
                case "wrong-required-rid-order":
                    var rids = (JsonArray)aggregate["requiredRids"]!;
                    var firstRid = rids[0]!.DeepClone();
                    rids[0] = rids[1]!.DeepClone();
                    rids[1] = firstRid;
                    break;
                case "duplicate-host":
                    hosts[4]!["rid"] = hosts[0]!["rid"]!.GetValue<string>();
                    break;
                case "foreign-host":
                    hosts[4]!["rid"] = "freebsd-x64";
                    break;
                case "unsafe-receipt-path":
                    hosts[0]!["receiptPath"] = "../outside/tailwind-native-host-proof.json";
                    break;
                case "receipt-hash-mismatch":
                    hosts[0]!["receiptSha256"] = new string('0', 64);
                    break;
                case "inventory-omission":
                    var summaryIndex = files.Select((item, index) => (item, index))
                        .Single(pair => pair.item!["path"]!.GetValue<string>() == "summary.md").index;
                    files.RemoveAt(summaryIndex);
                    break;
                case "inventory-surplus":
                    await File.WriteAllTextAsync(TestPathUtils.PathUnder(aggregateDirectory, "unbound-surplus.txt"), "surplus aggregate evidence");
                    break;
                case "bad-status":
                    aggregate["status"] = "failed";
                    break;
                case "bad-repository-binding":
                    aggregate["repositoryId"] = "54321";
                    break;
                case "bad-source-binding":
                    aggregate["sourceCommit"] = new string('a', 40);
                    break;
                case "bad-producer-artifact-binding":
                    aggregate["producerArtifactId"] = "999";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown aggregate mutation.");
            }

            await File.WriteAllTextAsync(aggregatePath, aggregate.ToJsonString());
            var manifest = await new PackageArtifactManifestReader().ReadAsync(ManifestPath, CancellationToken.None);
            var entry = manifest.Entries.Single();
            var planned = new PlannedPackageArtifact(entry, TestPathUtils.PathUnder(Bundle, entry.ArtifactFileName));
            var preflightReport = TestPathUtils.PathUnder(_root, "mutated-aggregate-report-" + mutation);
            var request = new TailwindPublicationRequest(
                Repository, Bundle, ManifestPath, TestPathUtils.PathUnder(Bundle, TailwindProofSubjectService.FileName),
                "501", SubjectHash, "12345", "901", SourceCommit, aggregateDirectory, "702",
                Sha256(await File.ReadAllBytesAsync(aggregatePath)),
                TestPathUtils.PathUnder(_root, "mutated-aggregate-publication-" + mutation),
                TestPathUtils.PathUnder(preflightReport, "publication-start-receipt.json"), "", preflightReport);

            return await Assert.ThrowsAsync<PackageIndexException>(() =>
                TailwindEvidenceWorkflow.PreparePublicationAsync(request, manifest, [planned], CancellationToken.None));
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> RunEvidenceCliAsync(
            string mode, string reportDirectory, params string[] additionalArguments)
        {
            var args = new List<string>
            {
                "verify-tailwind-evidence",
                "--repo-root", Repository,
                "--artifacts-input", Bundle,
                "--artifact-manifest", ManifestPath,
                "--mode", mode,
                "--producer-subject", TailwindProofSubjectService.FileName,
                "--producer-artifact-id", "501",
                "--expected-subject-sha256", SubjectHash,
                "--repository-id", "12345",
                "--producer-run-id", "901",
                "--source-commit", SourceCommit,
                "--report-directory", reportDirectory
            };
            args.AddRange(additionalArguments);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = await Program.RunAsync(args.ToArray(), stdout, stderr, Repository);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }

        public TailwindCommandOptions CreateProducerBindingOptions(string reportDirectory)
            => new("producer-binding", TailwindProofSubjectService.FileName, "501", SubjectHash, "12345", "901", "1", SourceCommit,
                null, null, null, reportDirectory, null, null, null, null, null, null, null, null, null, []);

        public async Task ApplyMutation(string mutation)
        {
            var rid = mutation == "missing-host" ? Rids[^1] : Rids[0];
            var receiptPath = TestPathUtils.PathUnder(Evidence, rid, "tailwind-native-host-proof.json");
            if (mutation == "missing-host") { File.Delete(receiptPath); return; }
            if (mutation is "missing-selected-payload" or "extra-unselected-payload" or "unsupported-assets-group" or "missing-assets-inventory"
                or "wrong-assets-target" or "multiple-assets-targets" or "missing-package-node" or "duplicate-package-node"
                or "payload-path-traversal" or "changed-extracted-bytes")
            {
                var host = TestPathUtils.PathUnder(Evidence, rid);
                var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
                if (mutation is "missing-selected-payload" or "extra-unselected-payload" or "payload-path-traversal" or "changed-extracted-bytes")
                {
                    var payloadFiles = (JsonArray)receipt["firstPartyPackages"]![0]!["payloadFiles"]!;
                    if (mutation is "missing-selected-payload" or "payload-path-traversal" or "changed-extracted-bytes")
                    {
                        var selected = payloadFiles.Select((node, index) => (node, index))
                            .Single(item => item.node!["packageRelativePath"]!.GetValue<string>() == "native/codec.bin");
                        if (mutation == "missing-selected-payload")
                        {
                            payloadFiles.RemoveAt(selected.index);
                        }
                        else if (mutation == "payload-path-traversal")
                        {
                            payloadFiles[selected.index]!["packageRelativePath"] = "../outside.bin";
                        }
                        else
                        {
                            var bytes = Encoding.UTF8.GetBytes("changed extracted native payload");
                            await File.WriteAllBytesAsync(TestPathUtils.PathUnder(host, "payload", "native", "codec.bin"), bytes);
                            payloadFiles[selected.index]!["sha256"] = Sha256(bytes);
                        }
                    }
                    else if (mutation == "extra-unselected-payload")
                    {
                        const string unusedPath = "notes/unused.txt";
                        var bytes = Encoding.UTF8.GetBytes("not selected by the assets graph");
                        var evidencePath = TestPathUtils.PathUnder(host, "payload", "notes", "unused.txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
                        await File.WriteAllBytesAsync(evidencePath, bytes);
                        payloadFiles.Add(new JsonObject
                        {
                            ["packageRelativePath"] = unusedPath,
                            ["evidencePath"] = "payload/" + unusedPath,
                            ["sha256"] = Sha256(bytes)
                        });
                    }
                }
                else if (mutation is "unsupported-assets-group" or "wrong-assets-target" or "multiple-assets-targets"
                    or "missing-package-node" or "duplicate-package-node")
                {
                    var assetsPath = TestPathUtils.PathUnder(host, "consumer", "project.assets.json");
                    var assets = JsonNode.Parse(await File.ReadAllTextAsync(assetsPath))!;
                    var targets = (JsonObject)assets["targets"]!;
                    var packageKey = PackageId + "/" + Version;
                    switch (mutation)
                    {
                        case "unsupported-assets-group":
                            targets["net10.0"]![packageKey]!["unexpectedGroup"] = new JsonObject();
                            break;
                        case "wrong-assets-target":
                            targets["net9.0"] = targets["net10.0"]!.DeepClone();
                            targets.Remove("net10.0");
                            break;
                        case "multiple-assets-targets":
                            targets["net9.0"] = targets["net10.0"]!.DeepClone();
                            break;
                        case "missing-package-node":
                            ((JsonObject)targets["net10.0"]!).Remove(packageKey);
                            break;
                        case "duplicate-package-node":
                            ((JsonObject)targets["net10.0"]!)[PackageId.ToLowerInvariant() + "/" + Version] =
                                targets["net10.0"]![packageKey]!.DeepClone();
                            break;
                    }
                    await File.WriteAllTextAsync(assetsPath, assets.ToJsonString());
                }
                if (mutation == "missing-assets-inventory")
                {
                    var files = (JsonArray)receipt["files"]!;
                    var assetsItem = files.Select((node, index) => (node, index))
                        .Single(item => item.node!["path"]!.GetValue<string>() == "consumer/project.assets.json");
                    files.RemoveAt(assetsItem.index);
                }
                else
                {
                    receipt["files"] = JsonSerializer.SerializeToNode(Inventory(host, "tailwind-native-host-proof.json"));
                }
                await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
                return;
            }
            var json = await File.ReadAllTextAsync(receiptPath);
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
            Assert.NotEqual(await File.ReadAllTextAsync(receiptPath), json);
            await File.WriteAllTextAsync(receiptPath, json);
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
