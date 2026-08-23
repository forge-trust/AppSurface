using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PythonParserCandidateProofTests : IDisposable
{
    private readonly string _repositoryRoot = Path.Combine(Path.GetTempPath(), "appsurface-python-parser-proof-tests", Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData(0, null, "exactly one root .nuspec")]
    [InlineData(2, "<package><metadata>", "exactly one root .nuspec")]
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
        var candidatePackagePath = Path.Combine(_repositoryRoot, "invalid.nupkg");
        await File.WriteAllTextAsync(candidatePackagePath, "not a zip archive");
        var workflow = new PythonParserCandidateProofWorkflow();

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, ReportPath("invalid-zip.json")),
            CancellationToken.None);

        Assert.Equal(["archive_inspection_failed"], report.RejectionReasons);
        Assert.Contains("InvalidDataException", report.Archive.InspectionFailure, StringComparison.Ordinal);
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
                new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, Path.Combine(_repositoryRoot, "report.json")),
                CancellationToken.None));

        Assert.Contains("within the repository artifacts directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Program_InvokesCandidateInspectionAndReportsARejectionWithoutTreatingItAsCliFailure()
    {
        var candidatePackagePath = CreateCandidatePackage();
        var standardOut = new StringWriter();
        var standardError = new StringWriter();
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
    public async Task Program_ReportsAnEligibleCandidateForFurtherReview()
    {
        var standardOut = new StringWriter();
        var standardError = new StringWriter();

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
        var standardOut = new StringWriter();
        var standardError = new StringWriter();

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
        int extraEntryCount = 0)
    {
        var packageDirectory = Path.Combine(_repositoryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, "treesitter-dotnet-1.3.0.nupkg");
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

        return packagePath;
    }

    private string ReportPath(string fileName) => Path.Combine(_repositoryRoot, "artifacts", fileName);

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

    private static void WriteArchiveEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
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
