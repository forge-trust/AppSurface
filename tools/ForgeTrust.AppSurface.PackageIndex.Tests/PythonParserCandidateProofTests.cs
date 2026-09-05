using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PythonParserCandidateProofTests : IDisposable
{
    private readonly string _repositoryRoot = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-python-parser-proof-tests", Guid.NewGuid().ToString("N"));

    public PythonParserCandidateProofTests()
    {
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task Workflow_RecordsArchiveInventoryAndRejectsPackageOverBudget()
    {
        var candidatePackagePath = CreateCandidatePackage();
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("report.json"), MaximumCompressedPackageBytes: 1),
            CancellationToken.None);

        Assert.False(report.IsEligibleForFurtherReview);
        Assert.Contains("compressed_archive_exceeds_budget", report.RejectionReasons);
        Assert.Empty(report.Archive.MissingRuntimeIdentifiers);
        Assert.Empty(report.Archive.UnexpectedRuntimeIdentifiers);
        Assert.Empty(report.Archive.LicenseAndNoticePaths);
        Assert.Equal("metadata_recorded_not_accepted", report.Archive.ProvenanceReview.Status);
        Assert.Null(report.Archive.InspectionFailure);
        Assert.True(report.Archive.HasCompleteProvenanceMetadata);
        Assert.Equal(PythonParserCandidateProofWorkflow.CandidatePackageId, report.Archive.Metadata.PackageId);
        Assert.Equal(PythonParserCandidateProofWorkflow.CandidatePackageVersion, report.Archive.Metadata.PackageVersion);
        Assert.Equal(9, report.Archive.NativeRuntimeAssets.Count);
        Assert.Contains("runtimes/osx-arm64/native/tree-sitter-python.bin", report.Archive.NativeRuntimeAssets.Single(asset => asset.RuntimeIdentifier == "osx-arm64").NativeAssetPaths);

        await using var reportStream = File.OpenRead(ReportPath("report.json"));
        using var reportJson = await JsonDocument.ParseAsync(reportStream);
        Assert.Equal("treesitter-dotnet-1.3.0.nupkg", reportJson.RootElement.GetProperty("archive").GetProperty("packageFileName").GetString());
        Assert.Contains("compressed_archive_exceeds_budget", reportJson.RootElement.GetProperty("rejectionReasons").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Workflow_RecordsSortedLicenseAndNoticeInventoryForHumanReview()
    {
        var candidatePackagePath = CreateCandidatePackage(
            licenseAndNoticePaths:
            [
                "licenses/COPYING",
                "NOTICE.txt",
                "legal/third-party-notices.md"
            ]);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("notice-inventory.json"), MaximumCompressedPackageBytes: long.MaxValue),
            CancellationToken.None);

        Assert.True(report.IsEligibleForFurtherReview);
        Assert.Equal(["NOTICE.txt", "legal/third-party-notices.md", "licenses/COPYING"], report.Archive.LicenseAndNoticePaths);
        Assert.Equal("metadata_recorded_not_accepted", report.Archive.ProvenanceReview.Status);
        Assert.Contains("separate human redistribution review", report.Archive.ProvenanceReview.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package-id")]
    [InlineData("package-version")]
    [InlineData("provenance")]
    public async Task Workflow_RecordsPackageIdentityAndProvenanceRejections(string scenario)
    {
        var candidatePackagePath = CreateCandidatePackage(
            nuspecContent: scenario switch
            {
                "package-id" => CreateNuspec(packageId: "Different.Parser"),
                "package-version" => CreateNuspec(packageVersion: "9.9.9"),
                "provenance" => CreateNuspec(repositoryCommit: string.Empty),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported proof scenario.")
            });
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath($"{scenario}.json"), MaximumCompressedPackageBytes: long.MaxValue),
            CancellationToken.None);

        var expectedRejection = scenario switch
        {
            "package-id" => "package_id_mismatch",
            "package-version" => "package_version_mismatch",
            "provenance" => "provenance_metadata_incomplete",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported proof scenario.")
        };
        Assert.Contains(expectedRejection, report.RejectionReasons);
        Assert.False(report.IsEligibleForFurtherReview);
    }

    [Fact]
    public async Task Workflow_RecordsUnexpectedRuntimeIdentifier()
    {
        var candidatePackagePath = CreateCandidatePackage(
            runtimeIdentifiers:
            [
                "linux-arm", "linux-arm64", "linux-x64", "linux-x86", "osx-arm64", "osx-x64", "win-arm64", "win-x64", "win-x86", "browser-wasm"
            ]);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("unexpected-rid.json"), MaximumCompressedPackageBytes: long.MaxValue),
            CancellationToken.None);

        Assert.Contains("native_runtime_identifier_unexpected", report.RejectionReasons);
        Assert.Equal(["browser-wasm"], report.Archive.UnexpectedRuntimeIdentifiers);
    }

    [Fact]
    public async Task Workflow_RecordsMissingRequiredRuntimeIdentifier()
    {
        var candidatePackagePath = CreateCandidatePackage(runtimeIdentifiers: ["osx-arm64"]);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("missing-rid.json"), MaximumCompressedPackageBytes: long.MaxValue),
            CancellationToken.None);

        Assert.Contains("native_runtime_identifier_missing", report.RejectionReasons);
        Assert.Contains("win-x64", report.Archive.MissingRuntimeIdentifiers);
    }

    [Theory]
    [InlineData(0, null, "exactly one root .nuspec")]
    [InlineData(2, "<package><metadata>", "exactly one root .nuspec")]
    [InlineData(1, "<package><metadata>", "XmlException")]
    [InlineData(1, "<package />", "missing metadata")]
    public async Task Workflow_RecordsMalformedNuspecAsStructuredRejection(
        int rootNuspecCount,
        string? nuspecContent,
        string expectedFailure)
    {
        var candidatePackagePath = CreateCandidatePackage(rootNuspecCount: rootNuspecCount, nuspecContent: nuspecContent);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath($"invalid-{rootNuspecCount}.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains(expectedFailure, report.Archive.InspectionFailure, StringComparison.Ordinal);
        Assert.Equal("not_evaluated", report.Archive.ProvenanceReview.Status);
    }

    [Fact]
    public async Task Workflow_RecordsInvalidZipAsStructuredRejection()
    {
        var candidatePackagePath = TestPathUtils.PathUnder(_repositoryRoot, "invalid.nupkg");
        await File.WriteAllTextAsync(candidatePackagePath, "not a zip archive");
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("invalid-zip.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("InvalidDataException", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RecordsDuplicateNuspecMetadataElementsAsStructuredRejection()
    {
        var candidatePackagePath = CreateCandidatePackage(
            nuspecContent: """
                <package>
                  <metadata>
                    <id>TreeSitter.DotNet</id>
                    <id>Duplicate.Parser</id>
                    <version>1.3.0</version>
                    <license type="expression">MIT</license>
                    <repository type="git" url="https://example.test/tree-sitter-dotnet.git" commit="0123456789abcdef" />
                  </metadata>
                </package>
                """);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("duplicate-metadata.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("duplicate 'id' elements", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RecordsCorruptArchivePayloadAsStructuredRejection()
    {
        const string payload = "candidate-payload-that-will-be-corrupted";
        var candidatePackagePath = CreateCandidatePackage(uncompressedPayload: payload);
        var bytes = await File.ReadAllBytesAsync(candidatePackagePath);
        var payloadOffset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(payload));
        Assert.True(payloadOffset >= 0);
        bytes[payloadOffset] ^= 0x01;
        await File.WriteAllBytesAsync(candidatePackagePath, bytes);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("corrupt-payload.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("CRC-32 checksum", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RejectsArchiveEntriesWithForgedDeclaredPayloadLengths()
    {
        var candidatePackagePath = CreateCandidatePackage();
        await SetArchiveEntryDeclaredUncompressedBytesAsync(candidatePackagePath, "TreeSitter.DotNet.nuspec", 1);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("mismatched-length.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("CRC-32 checksum", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RejectsOversizedArchiveBeforeHashingOrOpeningIt()
    {
        var candidatePackagePath = TestPathUtils.PathUnder(_repositoryRoot, "oversized.nupkg");
        await using (var stream = File.Create(candidatePackagePath))
        {
            stream.SetLength(PythonParserCandidateProofWorkflow.MaximumArchiveBytesToInspect + 1);
        }

        var workflow = new PythonParserCandidateProofWorkflow();
        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("oversized.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Equal(string.Empty, report.Archive.Sha256);
        Assert.Contains("Compressed archive exceeds", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RejectsArchivesWithExcessiveEntryCounts()
    {
        var candidatePackagePath = CreateCandidatePackage(extraEntryCount: PythonParserCandidateProofWorkflow.MaximumArchiveEntryCount);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("entry-limit.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("static inspection limit", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RejectsEntriesWhoseDeclaredUncompressedBytesExceedTheInspectionBudget()
    {
        var candidatePackagePath = CreateCandidatePackage();
        await SetArchiveEntryDeclaredUncompressedBytesAsync(
            candidatePackagePath,
            "TreeSitter.DotNet.nuspec",
            checked((uint)(PythonParserCandidateProofWorkflow.MaximumUncompressedArchiveBytes + 1)));
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("declared-uncompressed-limit.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("uncompressed static inspection limit", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RecordsEmptyArchivesAsStructuredRejections()
    {
        var candidatePackagePath = CreateCandidatePackage(runtimeIdentifiers: [], rootNuspecCount: 0);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("empty-archive.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("exactly one root .nuspec", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(PythonParserCandidateProofWorkflow.MaximumUncompressedArchiveBytes - 1, 1L, false)]
    [InlineData(PythonParserCandidateProofWorkflow.MaximumUncompressedArchiveBytes, 1L, true)]
    [InlineData(long.MaxValue, 1L, true)]
    public void Workflow_RejectsDeclaredUncompressedArchiveOverflow(long currentTotal, long nextEntryLength, bool expected)
    {
        Assert.Equal(expected, PythonParserCandidateProofWorkflow.ExceedsUncompressedArchiveByteLimit(currentTotal, nextEntryLength));
    }

    [Fact]
    public async Task Workflow_RejectsOversizedNuspecBeforeParsingIt()
    {
        var candidatePackagePath = CreateCandidatePackage(nuspecContent: new string('x', checked((int)PythonParserCandidateProofWorkflow.MaximumNuspecBytes + 1)));
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("nuspec-limit.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("nuspec exceeds", report.Archive.InspectionFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_RejectsReportsOutsideRepositoryArtifacts()
    {
        var candidatePackagePath = CreateCandidatePackage();
        var workflow = new PythonParserCandidateProofWorkflow();

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, TestPathUtils.PathUnder(_repositoryRoot, "report.json")),
                CancellationToken.None));

        Assert.Contains("within the repository artifacts directory", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-repository-root", "repository root")]
    [InlineData("missing-candidate", "package")]
    [InlineData("wrong-extension", ".nupkg")]
    public async Task Workflow_RejectsInvalidRequestPaths(string scenario, string expectedMessage)
    {
        var candidatePackagePath = scenario switch
        {
            "missing-candidate" => TestPathUtils.PathUnder(_repositoryRoot, "missing.nupkg"),
            "wrong-extension" => TestPathUtils.PathUnder(_repositoryRoot, "candidate.txt"),
            _ => CreateCandidatePackage()
        };
        if (scenario == "wrong-extension")
        {
            await File.WriteAllTextAsync(candidatePackagePath, "not a package");
        }

        var repositoryRoot = scenario == "missing-repository-root"
            ? TestPathUtils.PathUnder(_repositoryRoot, "missing-repository-root")
            : _repositoryRoot;
        var workflow = new PythonParserCandidateProofWorkflow();

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new PythonParserCandidateProofRequest(repositoryRoot, candidatePackagePath, ReportPath($"invalid-request-{scenario}.json")),
                CancellationToken.None));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workflow_RefusesToOverwriteExistingReports()
    {
        var candidatePackagePath = CreateCandidatePackage();
        Directory.CreateDirectory(TestPathUtils.PathUnder(_repositoryRoot, "artifacts"));
        await File.WriteAllTextAsync(ReportPath("existing.json"), "preserve me");
        var workflow = new PythonParserCandidateProofWorkflow();

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("existing.json")),
                CancellationToken.None));

        Assert.Contains("must not overwrite an existing artifact", error.Message, StringComparison.Ordinal);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(ReportPath("existing.json")));
    }

    [Fact]
    public async Task Workflow_RejectsReportPathsThatTraverseSymbolicLinks()
    {
        var candidatePackagePath = CreateCandidatePackage();
        Directory.CreateDirectory(TestPathUtils.PathUnder(_repositoryRoot, "artifacts"));
        var externalTarget = TestPathUtils.PathUnder(_repositoryRoot, "outside.json");
        await File.WriteAllTextAsync(externalTarget, "preserve me");
        File.CreateSymbolicLink(ReportPath("linked.json"), externalTarget);
        var workflow = new PythonParserCandidateProofWorkflow();

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("linked.json")),
                CancellationToken.None));

        Assert.Contains("must not traverse or overwrite symbolic links", error.Message, StringComparison.Ordinal);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(externalTarget));
    }

    [Fact]
    public async Task Workflow_WritesNestedArtifactReportsWithoutTraversingLinks()
    {
        var candidatePackagePath = CreateCandidatePackage();
        var nestedReportPath = TestPathUtils.PathUnder(_repositoryRoot, "artifacts", "nested", "candidate.json");
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, nestedReportPath),
            CancellationToken.None);

        Assert.True(report.IsEligibleForFurtherReview);
        Assert.True(File.Exists(nestedReportPath));
    }

    [Fact]
    public async Task Workflow_RecordsMissingPackageMetadataAndRepositoryAsRejections()
    {
        var candidatePackagePath = CreateCandidatePackage(
            nuspecContent: """
                <package>
                  <metadata>
                    <version>1.3.0</version>
                    <license type="expression">MIT</license>
                  </metadata>
                </package>
                """);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("incomplete-metadata.json")),
            CancellationToken.None);

        Assert.Contains("package_id_mismatch", report.RejectionReasons);
        Assert.Contains("provenance_metadata_incomplete", report.RejectionReasons);
        Assert.Equal(string.Empty, report.Archive.Metadata.PackageId);
        Assert.Equal(string.Empty, report.Archive.Metadata.RepositoryType);
        Assert.Equal(string.Empty, report.Archive.Metadata.RepositoryUrl);
        Assert.Equal(string.Empty, report.Archive.Metadata.RepositoryCommit);
    }

    [Fact]
    public async Task Workflow_DoesNotClassifyNonNativeRuntimeShapedPathsAsNativeAssets()
    {
        var candidatePackagePath = CreateCandidatePackage(
            licenseAndNoticePaths:
            [
                "content/a/b/LICENSE",
                "runtimes/browser-wasm/lib/NOTICE"
            ]);
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("non-native-runtime-paths.json")),
            CancellationToken.None);

        Assert.True(report.IsEligibleForFurtherReview);
        Assert.Equal(9, report.Archive.NativeRuntimeAssets.Count);
        Assert.DoesNotContain(report.Archive.NativeRuntimeAssets, asset => asset.RuntimeIdentifier == "browser-wasm");
        Assert.Equal(["content/a/b/LICENSE", "runtimes/browser-wasm/lib/NOTICE"], report.Archive.LicenseAndNoticePaths);
    }

    [Fact]
    public async Task Program_InvokesCandidateInspectionAndReportsARejectionWithoutTreatingItAsCliFailure()
    {
        var candidatePackagePath = CreateCandidatePackage();
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();
        PythonParserCandidateProofRequest? capturedRequest = null;

        var exitCode = await Program.RunAsync(
            [
                "inspect-python-parser-candidate",
                "--python-parser-package", candidatePackagePath,
                "--python-parser-proof-report", "artifacts/candidate-proof.json"
            ],
            standardOut,
            standardError,
            _repositoryRoot,
            inspectPythonParserCandidateAsync: (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(CreateReport(["compressed_archive_exceeds_budget"]));
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(candidatePackagePath, capturedRequest!.CandidatePackagePath);
        Assert.Equal(ReportPath("candidate-proof.json"), capturedRequest.ReportPath);
        Assert.Contains("rejected (compressed_archive_exceeds_budget)", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task Program_UsesTheDefaultCandidateInspectorWhenNoOverrideIsSupplied()
    {
        var candidatePackagePath = CreateCandidatePackage();
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "inspect-python-parser-candidate",
                "--python-parser-package", candidatePackagePath,
                "--python-parser-proof-report", "artifacts/default-inspector.json"
            ],
            standardOut,
            standardError,
            _repositoryRoot);

        Assert.Equal(0, exitCode);
        Assert.Contains("eligible for further review", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
        Assert.True(File.Exists(ReportPath("default-inspector.json")));
    }

    [Fact]
    public async Task Program_ReportsAnEligibleCandidateForFurtherReview()
    {
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "inspect-python-parser-candidate",
                "--python-parser-package", "candidate.nupkg"
            ],
            standardOut,
            standardError,
            _repositoryRoot,
            inspectPythonParserCandidateAsync: (_, _) => Task.FromResult(CreateReport([])));

        Assert.Equal(0, exitCode);
        Assert.Contains("eligible for further review", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task Program_RequiresTheCandidatePackageOption()
    {
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["inspect-python-parser-candidate"],
            standardOut,
            standardError,
            _repositoryRoot);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, standardOut.ToString());
        Assert.Contains("requires '--python-parser-package <path>'", standardError.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    private string CreateCandidatePackage(
        IReadOnlyList<string>? runtimeIdentifiers = null,
        IReadOnlyList<string>? licenseAndNoticePaths = null,
        string? nuspecContent = null,
        int rootNuspecCount = 1,
        int extraEntryCount = 0,
        string? uncompressedPayload = null)
    {
        var packageDirectory = TestPathUtils.PathUnder(_repositoryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        var packagePath = TestPathUtils.PathUnder(packageDirectory, "treesitter-dotnet-1.3.0.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        for (var index = 0; index < rootNuspecCount; index++)
        {
            var suffix = index == 0 ? string.Empty : $".{index}";
            WriteArchiveEntry(archive, $"TreeSitter.DotNet{suffix}.nuspec", nuspecContent ?? CreateNuspec());
        }

        foreach (var licenseOrNoticePath in licenseAndNoticePaths ?? [])
        {
            WriteArchiveEntry(archive, licenseOrNoticePath, licenseOrNoticePath);
        }

        foreach (var runtimeIdentifier in runtimeIdentifiers ??
                 ["linux-arm", "linux-arm64", "linux-x64", "linux-x86", "osx-arm64", "osx-x64", "win-arm64", "win-x64", "win-x86"])
        {
            WriteArchiveEntry(archive, $"runtimes/{runtimeIdentifier}/native/tree-sitter-python.bin", runtimeIdentifier);
        }

        for (var index = 0; index < extraEntryCount; index++)
        {
            WriteArchiveEntry(archive, $"content/{index:D4}.txt", string.Empty);
        }

        if (uncompressedPayload is not null)
        {
            WriteArchiveEntry(archive, "content/payload.txt", uncompressedPayload, CompressionLevel.NoCompression);
        }

        return packagePath;
    }

    private string ReportPath(string fileName) => TestPathUtils.PathUnder(_repositoryRoot, "artifacts", fileName);

    private static string CreateNuspec(
        string packageId = PythonParserCandidateProofWorkflow.CandidatePackageId,
        string packageVersion = PythonParserCandidateProofWorkflow.CandidatePackageVersion,
        string licenseExpression = "MIT",
        string repositoryType = "git",
        string repositoryUrl = "https://example.test/tree-sitter-dotnet.git",
        string repositoryCommit = "0123456789abcdef") =>
        $$"""
        <package>
          <metadata>
            <id>{{packageId}}</id>
            <version>{{packageVersion}}</version>
            <license type="expression">{{licenseExpression}}</license>
            <repository type="{{repositoryType}}" url="{{repositoryUrl}}" commit="{{repositoryCommit}}" />
          </metadata>
        </package>
        """;

    private static void WriteArchiveEntry(
        ZipArchive archive,
        string path,
        string content,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static async Task SetArchiveEntryDeclaredUncompressedBytesAsync(
        string archivePath,
        string entryPath,
        uint declaredUncompressedBytes)
    {
        const uint centralDirectoryFileHeaderSignature = 0x02014B50;
        const int centralDirectoryFileHeaderLength = 46;
        const int uncompressedSizeOffset = 24;
        const int fileNameLengthOffset = 28;
        const int extraFieldLengthOffset = 30;
        const int fileCommentLengthOffset = 32;
        var archiveBytes = await File.ReadAllBytesAsync(archivePath);
        var entryNameBytes = Encoding.UTF8.GetBytes(entryPath);

        for (var offset = 0; offset <= archiveBytes.Length - centralDirectoryFileHeaderLength; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(offset)) != centralDirectoryFileHeaderSignature)
            {
                continue;
            }

            var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(offset + fileNameLengthOffset));
            var extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(offset + extraFieldLengthOffset));
            var fileCommentLength = BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(offset + fileCommentLengthOffset));
            var recordLength = centralDirectoryFileHeaderLength + fileNameLength + extraFieldLength + fileCommentLength;
            if (offset + recordLength > archiveBytes.Length)
            {
                break;
            }

            var entryName = archiveBytes.AsSpan(offset + centralDirectoryFileHeaderLength, fileNameLength);
            if (entryName.SequenceEqual(entryNameBytes))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(archiveBytes.AsSpan(offset + uncompressedSizeOffset), declaredUncompressedBytes);
                await File.WriteAllBytesAsync(archivePath, archiveBytes);
                return;
            }

            offset += recordLength - 1;
        }

        throw new InvalidOperationException($"Could not find ZIP central-directory entry '{entryPath}'.");
    }

    private static PythonParserCandidateProofReport CreateReport(IReadOnlyList<string> rejectionReasons) =>
        new(
            new PythonParserCandidateArchiveEvidence(
                "TreeSitter.DotNet.1.3.0.nupkg",
                "ABC",
                PythonParserCandidateProofWorkflow.MaximumCompressedPackageBytes + 1,
                1,
                1,
                new PythonParserCandidateNuspecMetadata(
                    PythonParserCandidateProofWorkflow.CandidatePackageId,
                    PythonParserCandidateProofWorkflow.CandidatePackageVersion,
                    "MIT",
                    "git",
                    "https://example.test/tree-sitter-dotnet.git",
                    "0123456789abcdef"),
                [],
                [],
                [],
                [],
                new PythonParserCandidateProvenanceReviewEvidence("metadata_recorded_not_accepted", "Not accepted."),
                null),
            rejectionReasons);
}
