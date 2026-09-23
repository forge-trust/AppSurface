using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindNativeConsumerWorkflowTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder("/private/tmp", "tailwind-native-consumer-workflow", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LocalMode_UsesCompatibilityProofAndMarksEvidenceIneligibleForRelease()
    {
        var manifest = await WriteManifestAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "local-report");
        var result = await TailwindNativeConsumerWorkflow.RunAsync(_root, Path.GetDirectoryName(manifest)!, manifest,
            Options("local", report), runner, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("local-only", result.Status);
        Assert.Equal("bash", Assert.Single(runner.Requests).FileName);
        Assert.Contains("scripts/verify-tailwind-package-consumer.sh", runner.Requests[0].Arguments);
        using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReportPath));
        Assert.Equal("appsurface-tailwind-local-proof-v1", evidence.RootElement.GetProperty("schema").GetString());
        Assert.False(evidence.RootElement.GetProperty("releaseEligible").GetBoolean());
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "diagnostics.json")));
    }

    [Fact]
    public async Task LocalProofFailure_WritesFailedDiagnosticWithoutReceipt()
    {
        var manifest = await WriteManifestAsync();
        var runner = new LocalProofRunner(fail: true);
        var report = TestPathUtils.PathUnder(_root, "failed-local-report");
        var result = await TailwindNativeConsumerWorkflow.RunAsync(_root, Path.GetDirectoryName(manifest)!, manifest,
            Options("local", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.False(File.Exists(result.ReportPath));
        using var diagnostic = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("failed", diagnostic.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownMode_FailsBeforeRunningConsumer()
    {
        var manifest = await WriteManifestAsync();
        var runner = new LocalProofRunner();
        var result = await TailwindNativeConsumerWorkflow.RunAsync(_root, Path.GetDirectoryName(manifest)!, manifest,
            Options("unknown", TestPathUtils.PathUnder(_root, "unknown-report")), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
    }

    private TailwindCommandOptions Options(string mode, string report)
        => TailwindCommandOptions.Extract([
            "--mode", mode,
            "--work-directory", TestPathUtils.PathUnder(_root, "work-" + mode),
            "--report-directory", report
        ]);

    private async Task<string> WriteManifestAsync()
    {
        var bundle = TestPathUtils.PathUnder(_root, "bundle");
        Directory.CreateDirectory(bundle);
        var entry = new PackageArtifactManifestEntry("ForgeTrust.AppSurface.Web.Tailwind",
            "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj",
            "publish", "ForgeTrust.AppSurface.Web.Tailwind.1.2.3.nupkg", new string('a', 128), false);
        var manifest = new PackageArtifactManifest(1, "1.2.3", DateTimeOffset.Parse("2026-09-23T00:00:00Z"), [entry]);
        var path = TestPathUtils.PathUnder(bundle, "package-artifact-manifest.json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class LocalProofRunner(bool fail = false) : ICommandRunner
    {
        public List<CommandRunRequest> Requests { get; } = [];

        public async Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (fail) throw new PackageIndexException("local consumer failed");
            var reportIndex = Array.IndexOf(request.Arguments.ToArray(), "--report-path");
            Assert.True(reportIndex >= 0);
            var report = request.Arguments[reportIndex + 1];
            await File.WriteAllTextAsync(report, "# Local Tailwind consumer proof\nSuccess.\n", cancellationToken);
            return new CommandRunResult("ok", string.Empty);
        }
    }
}
