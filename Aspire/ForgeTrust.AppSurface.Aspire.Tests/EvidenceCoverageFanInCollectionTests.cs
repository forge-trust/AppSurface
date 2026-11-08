using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Aspire;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceCoverageFanInCollectionTests
{
    [Theory]
    [InlineData("native")]
    [InlineData("solution")]
    public async Task CaptureAsync_WritesManifestBackedValidatedBundle(string scope)
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var output = Path.Combine(fixture.Repository, "captured");
        var request = CaptureRequest(fixture, scope, output);

        await EvidenceCoverageFanIn.CaptureAsync(request);

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")))!.AsObject();
        Assert.Equal(scope, manifest["scope"]!.GetValue<string>());
        Assert.Equal(scope == "native" ? "Linux" : "Darwin", manifest["platform"]!.GetValue<string>());
        Assert.True(manifest["files"]!.AsObject().ContainsKey("coverage.cobertura.xml"));
        Assert.Equal(scope == "native", manifest["files"]!.AsObject().ContainsKey("collector.cobertura.xml"));
        Assert.Equal(scope == "solution", manifest["files"]!.AsObject().ContainsKey("timings.json"));
    }

    [Fact]
    public async Task CaptureAsync_RejectsIncompleteSolutionTimingInventory()
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var badTimings = Path.Combine(fixture.Repository, "bad-timings.json");
        await File.WriteAllTextAsync(badTimings, "{}");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceCoverageFanIn.CaptureAsync(
            CaptureRequest(fixture, "solution", Path.Combine(fixture.Repository, "captured"), timings: badTimings)).AsTask());

        Assert.Contains("Malformed coverage capture", exception.Message);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("output")]
    [InlineData("report")]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("sdk")]
    [InlineData("platform")]
    [InlineData("scope")]
    [InlineData("configuration")]
    [InlineData("projects-null")]
    [InlineData("projects-empty")]
    [InlineData("run-null")]
    [InlineData("run-empty")]
    [InlineData("run-key")]
    [InlineData("run-value")]
    [InlineData("raw-missing")]
    [InlineData("unexpected-timings")]
    [InlineData("native-platform")]
    [InlineData("timings-missing")]
    [InlineData("unexpected-raw")]
    [InlineData("solution-missing")]
    [InlineData("inventory-mismatch")]
    public async Task CaptureAsync_RejectsIncompleteOrInconsistentInputsBeforePublication(string defect)
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var output = Path.Combine(fixture.Repository, "capture");
        var solution = defect is "timings-missing" or "unexpected-raw" or "solution-missing" or "inventory-mismatch";
        var request = CaptureRequest(fixture, solution ? "solution" : "native", output);
        request = defect switch
        {
            "root" => request with { RepositoryRoot = " " },
            "output" => request with { OutputDirectory = " " },
            "report" => request with { ReportPath = " " },
            "commit" => request with { Commit = " " },
            "tree" => request with { Tree = " " },
            "sdk" => request with { Sdk = " " },
            "platform" => request with { Platform = " " },
            "scope" => request with { Scope = "other" },
            "configuration" => request with { Configuration = "other" },
            "projects-null" => request with { ExpectedTestProjects = null! },
            "projects-empty" => request with { ExpectedTestProjects = new HashSet<string>() },
            "run-null" => request with { Run = null! },
            "run-empty" => request with { Run = new Dictionary<string, string?>() },
            "run-key" => request with { Run = new Dictionary<string, string?> { [" "] = "1" } },
            "run-value" => request with { Run = new Dictionary<string, string?> { ["run"] = " " } },
            "raw-missing" => request with { RawReportPath = null },
            "unexpected-timings" => request with { TimingsPath = request.ReportPath },
            "native-platform" => request with { Platform = "other" },
            "timings-missing" => request with { TimingsPath = null },
            "unexpected-raw" => request with { RawReportPath = request.ReportPath },
            "inventory-mismatch" => request with { ExpectedTestProjects = new HashSet<string> { "other.Tests.csproj" } },
            _ => request,
        };
        if (defect == "solution-missing") File.Delete(Path.Combine(fixture.Repository, "ForgeTrust.AppSurface.slnx"));

        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceCoverageFanIn.CaptureAsync(request).AsTask());

        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureAsync_RejectsAnOccupiedDestinationAndPreservesCallerContent(bool directory)
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var output = Path.Combine(fixture.Repository, "occupied");
        if (directory) Directory.CreateDirectory(output);
        var sentinel = directory ? Path.Combine(output, "keep.txt") : output;
        await File.WriteAllTextAsync(sentinel, "caller-owned");

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceCoverageFanIn.CaptureAsync(CaptureRequest(fixture, "native", output)).AsTask());

        Assert.Equal("caller-owned", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task CaptureAsync_RejectsADestinationContainingItsInputs()
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceCoverageFanIn.CaptureAsync(
            CaptureRequest(fixture, "native", fixture.Native[0])).AsTask());
    }

    [Fact]
    public async Task CaptureAsync_RejectsNullAndObservesCancellation()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceCoverageFanIn.CaptureAsync(null!).AsTask());
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = Path.Combine(fixture.Repository, "cancelled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceCoverageFanIn.CaptureAsync(
            CaptureRequest(fixture, "native", output), cancellation.Token).AsTask());
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("malformed-manifest")]
    [InlineData("invalid-counter")]
    [InlineData("occupied-directory")]
    [InlineData("occupied-file")]
    public async Task ComposeAsync_FailsBeforeOverwritingCallerEvidence(string defect)
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var output = Path.Combine(fixture.Repository, "composition");
        var request = fixture.Request with { ComposedOutputDirectory = output };
        if (defect == "malformed-manifest")
            await File.WriteAllTextAsync(Path.Combine(fixture.Native[0], "manifest.json"), "{}");
        if (defect == "invalid-counter")
            await fixture.MutateManifestAsync(fixture.Native[0], manifest => manifest["schemaVersion"] = "not-an-int");
        if (defect == "occupied-directory") Directory.CreateDirectory(output);
        if (defect == "occupied-file") await File.WriteAllTextAsync(output, "caller-owned");

        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceCoverageFanIn.ComposeAsync(request).AsTask());

        if (defect == "occupied-file") Assert.Equal("caller-owned", await File.ReadAllTextAsync(output));
        else Assert.False(File.Exists(Path.Combine(output, "verification.json")));
    }

    [Fact]
    public async Task ComposeAsync_ComposesCapturesAndWritesReceiptBeforeGateReportsExist()
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var output = Path.Combine(fixture.Repository, "new-composition");
        var request = fixture.Request with { ComposedOutputDirectory = output };

        await EvidenceCoverageFanIn.ComposeAsync(request);

        Assert.True(File.Exists(Path.Combine(output, "coverage.cobertura.xml")));
        Assert.True(File.Exists(Path.Combine(output, "primary-paths.cobertura.xml")));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "verification.json")));
        Assert.Equal(4, receipt.RootElement.GetProperty("inputs").GetArrayLength());
        Assert.Equal(new[] { 1, 1, 1, 1 }, receipt.RootElement.GetProperty("inventory").GetProperty("inputClassLines").EnumerateArray().Select(x => x.GetInt32()));
        var verified = await EvidenceCoverageFanIn.VerifyAsync(request);
        Assert.True(verified.Verified);
    }

    [Theory]
    [InlineData("missing-native")]
    [InlineData("duplicate-platform")]
    [InlineData("run-mismatch")]
    [InlineData("report-hash")]
    public async Task ComposeAsync_RejectsMixedOrModifiedCaptureEvidence(string failure)
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        if (failure == "duplicate-platform")
            await fixture.MutateManifestAsync(fixture.Native[1], manifest => manifest["platform"] = "Linux");
        else if (failure == "run-mismatch")
            await fixture.MutateManifestAsync(fixture.Native[0], manifest => manifest["run"]!["GITHUB_RUN_ID"] = "2");
        else
            await File.AppendAllTextAsync(Path.Combine(fixture.Native[0], "coverage.cobertura.xml"), " ");

        var request = fixture.Request with
        {
            NativeCaptureDirectories = failure == "missing-native" ? fixture.Native.Take(2).ToArray() : fixture.Native,
            ComposedOutputDirectory = Path.Combine(fixture.Repository, "new-composition"),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceCoverageFanIn.ComposeAsync(request).AsTask());
    }

    private static EvidenceCoverageCaptureRequest CaptureRequest(
        EvidenceCoverageFanInTests.FanInFixture fixture,
        string scope,
        string output,
        string? timings = null)
    {
        var source = scope == "solution" ? fixture.Primary : fixture.Native[0];
        return new EvidenceCoverageCaptureRequest(
            fixture.Repository,
            output,
            Path.Combine(source, "coverage.cobertura.xml"),
            scope == "native" ? Path.Combine(source, "collector.cobertura.xml") : null,
            scope == "solution" ? timings ?? Path.Combine(source, "timings.json") : null,
            scope,
            "commit",
            "tree",
            "sdk",
            "Debug",
            new Dictionary<string, string?> { ["GITHUB_REPOSITORY"] = "repo", ["GITHUB_RUN_ID"] = "1", ["GITHUB_RUN_ATTEMPT"] = "1" },
            scope == "native" ? "Linux" : "Darwin",
            new HashSet<string>(StringComparer.Ordinal) { "tests/Foo.Tests/Foo.Tests.csproj" });
    }
}
