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
    public async Task Workflow_RecordsArchiveInventoryAndSuccessfulIsolatedSmokeWhenPackageExceedsBudget()
    {
        var candidatePackagePath = CreateCandidatePackage();
        var reportPath = Path.Combine(_repositoryRoot, "report.json");
        var workDirectory = Path.Combine(_repositoryRoot, "artifacts", "proof");
        var consumerDirectory = Path.Combine(workDirectory, "consumer");
        var commandRunner = new RecordingCommandRunner(
        [
            new ExternalCommandResult(0, $"Restored {consumerDirectory} (in 123 ms)", string.Empty),
            new ExternalCommandResult(0, "RID=osx-arm64\nVALID=module\nMALFORMED=module\nLARGE_SOURCE_BYTES=1000000\nLARGE=module\n", string.Empty)
        ]);
        var workflow = new PythonParserCandidateProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(_repositoryRoot, candidatePackagePath, workDirectory, reportPath, MaximumCompressedPackageBytes: 1),
            CancellationToken.None);

        Assert.False(report.IsEligibleForFurtherReview);
        Assert.Contains("compressed_archive_exceeds_budget", report.RejectionReasons);
        Assert.Empty(report.Archive.MissingRuntimeIdentifiers);
        Assert.Empty(report.Archive.UnexpectedRuntimeIdentifiers);
        Assert.True(report.Archive.HasCompleteProvenanceMetadata);
        Assert.Equal(PythonParserCandidateProofWorkflow.CandidatePackageId, report.Archive.Metadata.PackageId);
        Assert.Equal(PythonParserCandidateProofWorkflow.CandidatePackageVersion, report.Archive.Metadata.PackageVersion);
        Assert.Equal(9, report.Archive.NativeRuntimeAssets.Count);
        Assert.Contains("runtimes/osx-arm64/native/tree-sitter-python.bin", report.Archive.NativeRuntimeAssets.Single(asset => asset.RuntimeIdentifier == "osx-arm64").NativeAssetPaths);
        Assert.Equal(0, report.Restore!.ExitCode);
        Assert.Equal("Restored <consumer> (in <duration>)", report.Restore.StandardOutput);
        Assert.Equal(0, report.Smoke!.ExitCode);
        Assert.Contains("RID=osx-arm64", report.Smoke.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(2, commandRunner.Requests.Count);
        Assert.Equal("dotnet", commandRunner.Requests[0].FileName);
        Assert.Equal(["restore", "PythonParserCandidateSmoke.csproj", "--configfile", "NuGet.config", "--disable-parallel"], commandRunner.Requests[0].Arguments);
        Assert.Equal(["run", "--project", "PythonParserCandidateSmoke.csproj", "--no-restore"], commandRunner.Requests[1].Arguments);
        Assert.Contains("new Language(\"Python\")", await File.ReadAllTextAsync(Path.Combine(workDirectory, "consumer", "Program.cs")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(workDirectory, "local-feed", "treesitter.dotnet.1.3.0.nupkg")));

        await using var reportStream = File.OpenRead(reportPath);
        using var reportJson = await JsonDocument.ParseAsync(reportStream);
        Assert.Equal("treesitter-dotnet-1.3.0.nupkg", reportJson.RootElement.GetProperty("archive").GetProperty("packageFileName").GetString());
        Assert.Contains("compressed_archive_exceeds_budget", reportJson.RootElement.GetProperty("rejectionReasons").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Workflow_RecordsRestoreFailureWithoutStartingSmokeProcess()
    {
        var candidatePackagePath = CreateCandidatePackage();
        var commandRunner = new RecordingCommandRunner([new ExternalCommandResult(1, string.Empty, "restore failed")]);
        var workflow = new PythonParserCandidateProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(
                _repositoryRoot,
                candidatePackagePath,
                Path.Combine(_repositoryRoot, "artifacts", "restore-failure"),
                Path.Combine(_repositoryRoot, "restore-failure.json"),
                MaximumCompressedPackageBytes: 1),
            CancellationToken.None);

        Assert.Contains("consumer_restore_failed", report.RejectionReasons);
        Assert.NotNull(report.Restore);
        Assert.Null(report.Smoke);
        Assert.Single(commandRunner.Requests);
    }

    [Fact]
    public async Task Workflow_RejectsMissingRequiredRuntimeAssetWhileRetainingSmokeEvidence()
    {
        var candidatePackagePath = CreateCandidatePackage(runtimeIdentifiers: ["osx-arm64"]);
        var commandRunner = new RecordingCommandRunner(
        [
            new ExternalCommandResult(0, "Restored", string.Empty),
            new ExternalCommandResult(0, "RID=osx-arm64", string.Empty)
        ]);
        var workflow = new PythonParserCandidateProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new PythonParserCandidateProofRequest(
                _repositoryRoot,
                candidatePackagePath,
                Path.Combine(_repositoryRoot, "artifacts", "missing-rid"),
                Path.Combine(_repositoryRoot, "missing-rid.json"),
                MaximumCompressedPackageBytes: long.MaxValue),
            CancellationToken.None);

        Assert.Contains("native_runtime_identifier_missing", report.RejectionReasons);
        Assert.Contains("win-x64", report.Archive.MissingRuntimeIdentifiers);
        Assert.NotNull(report.Smoke);
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
                "--python-parser-proof-work-dir", "artifacts/candidate-proof",
                "--python-parser-proof-report", "artifacts/candidate-proof.json"
            ],
            standardOut,
            standardError,
            _repositoryRoot,
            inspectPythonParserCandidateAsync: (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(CreateRejectedReport());
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(candidatePackagePath, capturedRequest!.CandidatePackagePath);
        Assert.Equal(Path.Combine(_repositoryRoot, "artifacts", "candidate-proof"), capturedRequest.WorkDirectory);
        Assert.Equal(Path.Combine(_repositoryRoot, "artifacts", "candidate-proof.json"), capturedRequest.ReportPath);
        Assert.Contains("rejected (compressed_archive_exceeds_budget)", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task Workflow_RejectsAWorkspaceThatContainsTheCandidatePackage()
    {
        var workDirectory = Path.Combine(_repositoryRoot, "unsafe-workspace");
        Directory.CreateDirectory(workDirectory);
        var candidatePackagePath = CreateCandidatePackage(directory: workDirectory);
        var workflow = new PythonParserCandidateProofWorkflow(new RecordingCommandRunner([]));

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new PythonParserCandidateProofRequest(
                    _repositoryRoot,
                    candidatePackagePath,
                    workDirectory,
                    Path.Combine(_repositoryRoot, "unsafe.json")),
                CancellationToken.None));

        Assert.Contains("must not be contained", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    private string CreateCandidatePackage(IReadOnlyList<string>? runtimeIdentifiers = null, string? directory = null)
    {
        var packageDirectory = directory ?? Path.Combine(_repositoryRoot, "candidate");
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, "treesitter-dotnet-1.3.0.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteArchiveEntry(
            archive,
            "TreeSitter.DotNet.nuspec",
            """
            <package>
              <metadata>
                <id>TreeSitter.DotNet</id>
                <version>1.3.0</version>
                <license type="expression">MIT</license>
                <repository type="git" url="https://example.test/tree-sitter-dotnet.git" commit="0123456789abcdef" />
              </metadata>
            </package>
            """);
        foreach (var runtimeIdentifier in runtimeIdentifiers ??
                 ["linux-arm", "linux-arm64", "linux-x64", "linux-x86", "osx-arm64", "osx-x64", "win-arm64", "win-x64", "win-x86"])
        {
            WriteArchiveEntry(archive, $"runtimes/{runtimeIdentifier}/native/tree-sitter-python.bin", runtimeIdentifier);
        }

        return packagePath;
    }

    private static void WriteArchiveEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static PythonParserCandidateProofReport CreateRejectedReport() =>
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
                []),
            null,
            null,
            ["compressed_archive_exceeds_budget"]);

    private sealed class RecordingCommandRunner : IExternalCommandRunner
    {
        private readonly Queue<ExternalCommandResult> _results;

        internal RecordingCommandRunner(IEnumerable<ExternalCommandResult> results)
        {
            _results = new Queue<ExternalCommandResult>(results);
        }

        internal List<ExternalCommandRequest> Requests { get; } = [];

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No recorded command result remains.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }
}
