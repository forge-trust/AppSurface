using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindEvidenceWorkflowTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(
        TailwindTestPaths.TemporaryRoot,
        "tailwind-evidence-workflow-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PublicationFile_MustBeADirectChildOfItsTrustedDirectory()
    {
        var direct = TestPathUtils.PathUnder(_root, "publication-start-receipt.json");
        var nested = TestPathUtils.PathUnder(_root, "subdirectory", "publication-start-receipt.json");

        TailwindEvidenceWorkflow.RequireDirectChild(_root, direct);

        var error = Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.RequireDirectChild(_root, nested));
        Assert.Contains("direct child", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aggregate_RejectsStaleReportWithoutChangingPriorEvidence()
    {
        var report = TestPathUtils.PathUnder(_root, "stale-aggregate");
        Directory.CreateDirectory(report);
        var diagnostics = TestPathUtils.PathUnder(report, "diagnostics.json");
        var summary = TestPathUtils.PathUnder(report, "summary.md");
        var aggregate = TestPathUtils.PathUnder(report, "tailwind-native-aggregate.json");
        await File.WriteAllTextAsync(diagnostics, "prior diagnostics");
        await File.WriteAllTextAsync(summary, "prior summary");
        await File.WriteAllTextAsync(aggregate, "prior aggregate");
        var options = TailwindCommandOptions.Extract(["--report-directory", report]);

        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindEvidenceWorkflow.AggregateAsync(
            _root, _root, aggregate, options, CancellationToken.None));

        Assert.Equal("prior diagnostics", await File.ReadAllTextAsync(diagnostics));
        Assert.Equal("prior summary", await File.ReadAllTextAsync(summary));
        Assert.Equal("prior aggregate", await File.ReadAllTextAsync(aggregate));
    }

    [Fact]
    public void HostArtifactDirectory_MustBeAnExistingUnlinkedChild()
    {
        var child = TestPathUtils.PathUnder(_root, "linux-x64");
        Directory.CreateDirectory(child);

        Assert.Equal(child, TailwindEvidenceWorkflow.ResolveChildDirectory(_root, "linux-x64"));

        var error = Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.ResolveChildDirectory(_root, "../outside"));
        Assert.Contains("Unsafe host artifact directory", error.Message, StringComparison.Ordinal);
    }

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

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task ReadHostArtifactMap_RejectsNonArrayRoot(string json)
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, "nonarray-host-artifacts.json");
        await File.WriteAllTextAsync(path, json);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None));

        Assert.Contains("exactly five entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadHostArtifactMap_RejectsNullRidField()
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, "null-rid-host-artifacts.json");
        await File.WriteAllTextAsync(path, """
            [{"rid":null,"artifactId":"101","directory":"linux-x64"},
             {"rid":"linux-arm64","artifactId":"102","directory":"linux-arm64"},
             {"rid":"osx-x64","artifactId":"103","directory":"osx-x64"},
             {"rid":"osx-arm64","artifactId":"104","directory":"osx-arm64"},
             {"rid":"win-x64","artifactId":"105","directory":"win-x64"}]
            """);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None));

        Assert.Contains("rid", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("out-of-order", "five ordered supported RIDs")]
    [InlineData("wrong-directory", "unsupported RID or directory")]
    [InlineData("blank-rid", "must be a non-empty string")]
    [InlineData("blank-directory", "must be a non-empty string")]
    [InlineData("leading-zero-id", "canonical decimal string")]
    [InlineData("alphabetic-id", "canonical decimal string")]
    [InlineData("overlong-id", "canonical decimal string")]
    public async Task ReadHostArtifactMap_RejectsInvalidIdentityFields(string mutation, string expectedDiagnostic)
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
            case "out-of-order": (hosts[0], hosts[1]) = (hosts[1], hosts[0]); break;
            case "wrong-directory": hosts[0] = ("linux-x64", "101", "linux-arm64"); break;
            case "blank-rid": hosts[0] = (" ", "101", "linux-x64"); break;
            case "blank-directory": hosts[0] = ("linux-x64", "101", " "); break;
            case "leading-zero-id": hosts[0] = ("linux-x64", "0101", "linux-x64"); break;
            case "alphabetic-id": hosts[0] = ("linux-x64", "a101", "linux-x64"); break;
            case "overlong-id": hosts[0] = ("linux-x64", new string('1', 21), "linux-x64"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown map mutation.");
        }

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(WriteMap(hosts), CancellationToken.None));

        Assert.Contains(expectedDiagnostic, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadHostArtifactMap_RejectsAnOversizedDocumentBeforeParsing()
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, "oversized-host-artifacts.json");
        await File.WriteAllTextAsync(path, new string(' ', TailwindProofSubjectService.MaximumDocumentBytes + 1));

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None));

        Assert.Contains("16 MiB limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("duplicate-field")]
    [InlineData("unknown-field")]
    [InlineData("missing-field")]
    public async Task ReadHostArtifactMap_RejectsMalformedOrAmbiguousJson(string mutation)
    {
        Directory.CreateDirectory(_root);
        var path = TestPathUtils.PathUnder(_root, "malformed-host-artifacts.json");
        var first = mutation switch
        {
            "not-json" => "not json",
            "duplicate-field" => "{\"rid\":\"linux-x64\",\"rid\":\"linux-x64\",\"artifactId\":\"101\",\"directory\":\"linux-x64\"}",
            "unknown-field" => "{\"rid\":\"linux-x64\",\"artifactId\":\"101\",\"directory\":\"linux-x64\",\"extra\":true}",
            _ => "{\"rid\":\"linux-x64\",\"artifactId\":\"101\"}"
        };
        await File.WriteAllTextAsync(path, mutation == "not-json" ? first : "[" + first + "]");

        var error = await Record.ExceptionAsync(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, CancellationToken.None));
        Assert.NotNull(error);
        Assert.True(error is PackageIndexException or JsonException, $"Unexpected exception type: {error.GetType().Name}");
    }

    [Fact]
    public async Task ReadHostArtifactMap_HonorsCancellationWhileReading()
    {
        var path = WriteMap(
        [
            ("linux-x64", "101", "linux-x64"),
            ("linux-arm64", "102", "linux-arm64"),
            ("osx-x64", "103", "osx-x64"),
            ("osx-arm64", "104", "osx-arm64"),
            ("win-x64", "105", "win-x64")
        ]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TailwindEvidenceWorkflow.ReadHostArtifactMapAsync(path, cancellation.Token));
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

    [Theory]
    [InlineData("producer-binding", "producer-binding-invalid")]
    [InlineData("initial-restore", "restore-failed")]
    [InlineData("locked-restore", "locked-restore-failed")]
    [InlineData("restored-graph-and-payload", "restored-payload-invalid")]
    [InlineData("native-build", "native-build-failed")]
    [InlineData("post-build-revalidation", "post-build-payload-changed")]
    [InlineData("aggregate", "aggregate-invalid")]
    [InlineData("publish-preflight", "publication-preflight-invalid")]
    [InlineData("local-consumer", "local-proof-failed")]
    [InlineData("unrecognized-stage", "proof-prerequisite-failed")]
    public async Task FailureReport_MapsStageRedactsSecretsAndBoundsMessages(string stage, string code)
    {
        var report = TestPathUtils.PathUnder(_root, "diagnostics", stage);
        var message = "token=super-secret password: hunter2 api_key=hidden https://user:pass@example.test/" + new string('x', 2100);

        await TailwindEvidenceWorkflow.WriteFailureReportBestEffortAsync(
            report, stage, new PackageIndexException(message), CancellationToken.None,
            details: new { ignored = true }, releaseEligible: false);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        var root = document.RootElement;
        Assert.Equal("appsurface-tailwind-diagnostic-v1", root.GetProperty("schema").GetString());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal(stage, root.GetProperty("stage").GetString());
        Assert.False(root.GetProperty("releaseEligible").GetBoolean());
        var error = Assert.Single(root.GetProperty("errors").EnumerateArray());
        Assert.Equal(code, error.GetProperty("Code").GetString());
        var safeMessage = error.GetProperty("Message").GetString()!;
        Assert.DoesNotContain("super-secret", safeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", safeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", safeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", safeMessage, StringComparison.Ordinal);
        Assert.EndsWith("…", safeMessage, StringComparison.Ordinal);
        Assert.True(safeMessage.Length <= 2001);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("NextAction").GetString()));
        Assert.Equal("https://github.com/forge-trust/AppSurface/issues/798", error.GetProperty("DocsUrl").GetString());
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "summary.md")));
    }

    [Fact]
    public async Task FailureReport_IsBestEffortWhenTheReportPathCannotBeCreated()
    {
        Directory.CreateDirectory(_root);
        var pathThatIsAFile = TestPathUtils.PathUnder(_root, "not-a-directory");
        await File.WriteAllTextAsync(pathThatIsAFile, "preserve this file");

        await TailwindEvidenceWorkflow.WriteFailureReportBestEffortAsync(
            pathThatIsAFile, "aggregate", new PackageIndexException("invalid evidence"), CancellationToken.None);

        Assert.Equal("preserve this file", await File.ReadAllTextAsync(pathThatIsAFile));
    }

    [Fact]
    public async Task DiagnosticReport_RejectsInvalidArgumentsAndTruncatesLargeErrorLists()
    {
        var report = TestPathUtils.PathUnder(_root, "diagnostic-contract");
        var errors = Enumerable.Range(0, 101)
            .Select(index => new TailwindDiagnosticError("E" + index, "message", null, null, "retry", "https://example.test"))
            .ToArray();

        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(
            report, "unknown", "aggregate", true, [], "summary", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(
            report, "failed", " ", true, [], "summary", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(
            report, "failed", "aggregate", true, null!, "summary", CancellationToken.None));

        await TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(
            report, "failed", "aggregate", true, errors, "first summary", CancellationToken.None);

        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json"))))
        {
            Assert.Equal(100, document.RootElement.GetProperty("errors").GetArrayLength());
            Assert.True(document.RootElement.GetProperty("errorsTruncated").GetBoolean());
        }
        Assert.Contains("first summary", await File.ReadAllTextAsync(TestPathUtils.PathUnder(report, "summary.md")), StringComparison.Ordinal);

        await Assert.ThrowsAnyAsync<IOException>(() => TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(
            report, "succeeded", "aggregate", true, [], "must not replace", CancellationToken.None));
        await TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(
            report, "succeeded", "aggregate", true, [], "replacement summary", CancellationToken.None, overwrite: true);
        using var replaced = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("succeeded", replaced.RootElement.GetProperty("status").GetString());
        Assert.False(replaced.RootElement.GetProperty("errorsTruncated").GetBoolean());
    }

    [Fact]
    public async Task AtomicReceiptWrite_CancellationLeavesNoPartialReceiptOrTemporaryFile()
    {
        var path = TestPathUtils.PathUnder(_root, "cancelled", "receipt.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TailwindEvidenceWorkflow.WriteAtomicCreateNewJsonAsync(
            path, new { schema = "appsurface-test-v1" }, cancellation.Token));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Aggregate_MissingProducerInputsReturnsFailureAndStructuredDiagnostics()
    {
        var report = TestPathUtils.PathUnder(_root, "aggregate-failure");
        var options = TailwindCommandOptions.Extract([]) with { ReportDirectory = report };

        var result = await TailwindEvidenceWorkflow.AggregateAsync(
            _root, TestPathUtils.PathUnder(_root, "bundle"), TestPathUtils.PathUnder(_root, "manifest.json"),
            options, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Equal(TestPathUtils.PathUnder(report, "tailwind-native-aggregate.json"), result.ReportPath);
        Assert.False(File.Exists(result.ReportPath));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("failed", diagnostics.RootElement.GetProperty("status").GetString());
        Assert.Equal("aggregate", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Equal("aggregate-invalid", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Code").GetString());
    }

    [Fact]
    public async Task ProducerBindingFailure_WritesDiagnosticsAndDoesNotCreateResolvedOutput()
    {
        var report = TestPathUtils.PathUnder(_root, "producer-binding-failure");
        var output = TestPathUtils.PathUnder(_root, "resolved-binding.json");
        var options = TailwindCommandOptions.Extract([]) with { ReportDirectory = report };

        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindEvidenceWorkflow.ValidateAndWriteProducerBindingAsync(
            _root, TestPathUtils.PathUnder(_root, "bundle"), TestPathUtils.PathUnder(_root, "manifest.json"),
            options, output, CancellationToken.None));

        Assert.False(File.Exists(output));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Equal("producer-binding-invalid", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Code").GetString());
        Assert.False(diagnostics.RootElement.GetProperty("releaseEligible").GetBoolean());
    }

    [Fact]
    public void EnumerateInventory_ReturnsSortedHashesAndOmitsOnlyRequestedFiles()
    {
        var evidence = TestPathUtils.PathUnder(_root, "inventory");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(TestPathUtils.PathUnder(evidence, "z.txt"), "last");
        File.WriteAllText(TestPathUtils.PathUnder(evidence, "receipt.json"), "self");
        File.WriteAllText(TestPathUtils.PathUnder(evidence, "a.txt"), "first");

        var inventory = TailwindEvidenceWorkflow.EnumerateInventory(evidence, "receipt.json");

        Assert.Equal(2, inventory.Length);
        var json = JsonSerializer.SerializeToElement(inventory);
        Assert.Equal(["a.txt", "z.txt"], json.EnumerateArray().Select(item => item.GetProperty("path").GetString()));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("first"u8)).ToLowerInvariant(),
            json[0].GetProperty("sha256").GetString());
        Assert.Throws<DirectoryNotFoundException>(() => TailwindEvidenceWorkflow.EnumerateInventory(
            TestPathUtils.PathUnder(_root, "absent")));
    }

    [Fact]
    public void ResolveOptionPath_ResolvesRelativeAndAbsolutePathsAndRequiresAValue()
    {
        Assert.Equal(Path.GetFullPath(TestPathUtils.PathUnder(_root, "relative", "evidence.json")),
            TailwindEvidenceWorkflow.ResolveOptionPath(Path.Combine("relative", "evidence.json"), _root, "--evidence-input"));
        var absolute = TestPathUtils.PathUnder(_root, "absolute.json");
        Assert.Equal(Path.GetFullPath(absolute), TailwindEvidenceWorkflow.ResolveOptionPath(absolute, _root, "--evidence-input"));
        Assert.Throws<PackageIndexException>(() => TailwindEvidenceWorkflow.ResolveOptionPath(null, _root, "--evidence-input"));
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
