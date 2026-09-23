using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task ReleaseMode_RejectsUnsupportedRidAfterValidatingProducerBinding()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "unsupported-rid-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, "not-a-rid", "unsupported-rid", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Empty(runner.Requests);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("failed", diagnostics.RootElement.GetProperty("status").GetString());
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("Unsupported expected RID", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Message").GetString());
    }

    [Fact]
    public async Task ReleaseMode_RejectsHostMismatchBeforeStartingRestore()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "host-mismatch-report");
        var unsupportedOnThisHost = SupportedRids.First(rid => rid != CurrentRid());

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, unsupportedOnThisHost, "host-mismatch", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("Native host mismatch", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Message").GetString());
    }

    [Fact]
    public async Task ReleaseMode_DefaultValidatorRejectsOnePackageFixtureBeforeRestore()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new LocalProofRunner();
        var report = TestPathUtils.PathUnder(_root, "default-validator-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "default-validator", report), runner, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(runner.Requests);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("producer-binding", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("package plan", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseMode_RestoresPrivatePackageCacheBuildsAndWritesNativeReceipt()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new NativeReleaseRunner(producer, CurrentRid());
        var report = TestPathUtils.PathUnder(_root, "release-success-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "release-success", report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync);

        Assert.True(result.Succeeded, await File.ReadAllTextAsync(TestPathUtils.PathUnder(report, "summary.md")));
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(["sdk-version", "restore", "locked restore", "build"], runner.Requests.Select(request => request.FailureVerb));
        using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReportPath));
        Assert.Equal("appsurface-tailwind-native-host-proof-v2", receipt.RootElement.GetProperty("schema").GetString());
        Assert.Equal("succeeded", receipt.RootElement.GetProperty("status").GetString());
        Assert.Equal(CurrentRid(), receipt.RootElement.GetProperty("observedRid").GetString());
        Assert.True(receipt.RootElement.GetProperty("checks").GetProperty("postBuildPayloadUnchanged").GetBoolean());
        Assert.True(File.Exists(TestPathUtils.PathUnder(runner.ConsumerDirectory!, "wwwroot", "css", "site.gen.css")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "diagnostics.json")));
    }

    [Fact]
    public async Task ReleaseMode_RejectsProtectedPayloadMutationAfterBuildWithoutReceipt()
    {
        var producer = await CreateProducerBundleAsync();
        var runner = new NativeReleaseRunner(producer, CurrentRid(), mutateProtectedPayloadAfterBuild: true);
        var report = TestPathUtils.PathUnder(_root, "release-mutation-report");

        var result = await TailwindNativeConsumerWorkflow.RunAsync(producer.Repository, producer.Bundle, producer.ManifestPath,
            ReleaseOptions(producer, CurrentRid(), "release-mutation", report), runner, CancellationToken.None,
            ValidateFixtureProducerArtifactsAsync);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Equal(["sdk-version", "restore", "locked restore", "build"], runner.Requests.Select(request => request.FailureVerb));
        Assert.False(File.Exists(result.ReportPath));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.Equal("post-build-revalidation", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Contains("Expanded first-party package payload bytes changed", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("Message").GetString());
    }

    private static readonly string[] SupportedRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];
    private static readonly byte[] FakeCliBytes = Encoding.UTF8.GetBytes("fixture tailwind cli bytes");

    private TailwindCommandOptions ReleaseOptions(ProducerBundle producer, string rid, string name, string report)
        => TailwindCommandOptions.Extract([
            "--mode", "release",
            "--producer-subject", TailwindProofSubjectService.FileName,
            "--producer-artifact-id", "501",
            "--expected-subject-sha256", producer.SubjectSha256,
            "--repository-id", "12345",
            "--producer-run-id", "901",
            "--producer-attempt", "1",
            "--source-commit", producer.SourceCommit,
            "--native-invocation-id", "native-test-" + name,
            "--expected-rid", rid,
            "--work-directory", TestPathUtils.PathUnder(_root, "work-" + name),
            "--report-directory", report
        ]);

    private async Task<ProducerBundle> CreateProducerBundleAsync()
    {
        var sourceRepository = FindRepositoryRoot();
        var repository = TestPathUtils.PathUnder(_root, "clean-source-" + Guid.NewGuid().ToString("N"));
        await CloneSourceAsync(sourceRepository, repository);
        var commit = await RunGitAsync(repository, "rev-parse");
        var bundle = TestPathUtils.PathUnder(_root, "producer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        var sourceManifest = CreateSyntheticReleaseManifest();
        var packageId = "ForgeTrust.AppSurface.Web.Tailwind";
        var fileName = $"{packageId}.1.2.3.nupkg";
        var archivePath = TestPathUtils.PathUnder(bundle, fileName);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "build/", []);
            AddEntry(archive, "build/tailwind.release.json", sourceManifest);
            AddEntry(archive, "build/tailwind.version", Encoding.UTF8.GetBytes("4.1.18"));
            AddEntry(archive, "build/ForgeTrust.AppSurface.Web.Tailwind.targets", Encoding.UTF8.GetBytes("<Project />"));
            AddEntry(archive, "contentFiles/any/any/tailwind.css", Encoding.UTF8.GetBytes(".fixture{color:red}"));
        }

        var packageHash = PackageHash.ComputeSha512(archivePath);
        var entry = new PackageArtifactManifestEntry(packageId,
            "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj",
            "publish", fileName, packageHash, false);
        var manifest = new PackageArtifactManifest(1, "1.2.3", DateTimeOffset.Parse("2026-09-23T00:00:00Z"), [entry]);
        var manifestPath = TestPathUtils.PathUnder(bundle, "package-artifact-manifest.json");
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
        var closure = new[] { new TailwindSubjectPackage(packageId, "1.2.3", fileName, packageHash) };
        var subject = await TailwindProofSubjectService.CreateAsync(bundle, manifestPath, "12345", "901", "1", commit,
            closure, CancellationToken.None);
        var subjectHash = await TailwindProofSubjectService.WriteAsync(subject, bundle, CancellationToken.None);
        return new ProducerBundle(repository, bundle, manifestPath, commit, subjectHash);
    }

    private static byte[] CreateSyntheticReleaseManifest()
    {
        var hash = Convert.ToHexString(SHA256.HashData(FakeCliBytes)).ToLowerInvariant();
        var assets = SupportedRids.Select(rid => new
        {
            rid,
            binaryName = BinaryName(rid),
            sha256 = hash
        }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            version = "4.1.18",
            baseUrl = "https://example.invalid/tailwind-test",
            assets
        });
    }

    private static string BinaryName(string rid) => rid switch
    {
        "linux-x64" => "tailwindcss-linux-x64",
        "linux-arm64" => "tailwindcss-linux-arm64",
        "osx-x64" => "tailwindcss-macos-x64",
        "osx-arm64" => "tailwindcss-macos-arm64",
        "win-x64" => "tailwindcss-windows-x64.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, "Unsupported test RID.")
    };

    private static Task ValidateFixtureProducerArtifactsAsync(
        string repositoryRoot,
        string artifactsInputPath,
        PackageArtifactManifest manifest,
        IReadOnlyList<TailwindSubjectPackage> producerClosure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Single(producerClosure);
        var closurePackage = Assert.Single(producerClosure);
        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(closurePackage.PackageId, entry.PackageId);
        Assert.Equal(closurePackage.ArtifactFileName, entry.ArtifactFileName);
        Assert.Equal(closurePackage.PackageSha512, entry.Sha512);
        Assert.True(File.Exists(TestPathUtils.PathUnder(artifactsInputPath, entry.ArtifactFileName)));
        Assert.True(File.Exists(TestPathUtils.PathUnder(repositoryRoot, "packages", "package-index.yml")));
        return Task.CompletedTask;
    }

    private static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        var processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        return (os, architecture) switch
        {
            ("Linux", "X64") => "linux-x64",
            ("Linux", "Arm64") => "linux-arm64",
            ("macOS", "X64") => "osx-x64",
            ("macOS", "Arm64") => "osx-arm64",
            ("Windows", "X64") when processArchitecture == "X64" => "win-x64",
            ("Windows", "Arm64") when processArchitecture == "X64" => "win-x64",
            _ => throw new InvalidOperationException($"Unsupported test host {os}/{architecture}.")
        };
    }

    private static void AddEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "packages", "package-index.yml")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static async Task<string> RunGitAsync(string directory, string argument)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(argument);
        start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }

    private static async Task CloneSourceAsync(string source, string destination)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = source, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "clone", "--local", "--no-hardlinks", "--quiet", source, destination })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git clone.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git clone failed: {error}{output}");
    }

    private sealed record ProducerBundle(string Repository, string Bundle, string ManifestPath, string SourceCommit, string SubjectSha256);

    private sealed class NativeReleaseRunner(ProducerBundle producer, string rid, bool mutateProtectedPayloadAfterBuild = false) : ICommandRunner
    {
        private const string PackageId = "ForgeTrust.AppSurface.Web.Tailwind";
        private const string PackageVersion = "1.2.3";
        private const string TailwindVersion = "4.1.18";
        private readonly string _packageFileName = $"{PackageId}.{PackageVersion}.nupkg";

        public List<CommandRunRequest> Requests { get; } = [];
        public string? ConsumerDirectory { get; private set; }
        private string? _restoredPackageDirectory;

        public async Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ConsumerDirectory = request.WorkingDirectory;
            if (request.FailureVerb == "sdk-version") return new CommandRunResult("10.0.100", string.Empty);
            if (request.FailureVerb is "restore" or "locked restore")
            {
                if (request.FailureVerb == "restore") await CreateRestoreOutputsAsync(request, cancellationToken);
                return new CommandRunResult(string.Empty, string.Empty);
            }
            Assert.Equal("build", request.FailureVerb);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(request.WorkingDirectory, "wwwroot", "css", "site.gen.css"), ".generated{color:red}", cancellationToken);
            Directory.CreateDirectory(TestPathUtils.PathUnder(request.WorkingDirectory, "bin", "Release", "net10.0"));
            var work = Directory.GetParent(request.WorkingDirectory)!.FullName;
            var binary = TestPathUtils.PathUnder(work, "tailwind-cache", "tailwind-" + TailwindVersion, rid, BinaryName(rid));
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            await File.WriteAllBytesAsync(binary, FakeCliBytes, cancellationToken);
            if (mutateProtectedPayloadAfterBuild)
            {
                Assert.NotNull(_restoredPackageDirectory);
                var protectedFile = TestPathUtils.PathUnder(_restoredPackageDirectory!, "build", "tailwind.version");
                await File.AppendAllTextAsync(protectedFile, "-tampered", cancellationToken);
            }
            return new CommandRunResult(string.Empty, string.Empty);
        }

        private async Task CreateRestoreOutputsAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(request.Environment);
            var packageCache = environment["NUGET_PACKAGES"]!;
            _restoredPackageDirectory = TestPathUtils.PathUnder(packageCache, PackageId.ToLowerInvariant(), PackageVersion.ToLowerInvariant());
            Directory.CreateDirectory(_restoredPackageDirectory);
            var cachedArchive = TestPathUtils.PathUnder(_restoredPackageDirectory, _packageFileName);
            File.Copy(TestPathUtils.PathUnder(producer.Bundle, _packageFileName), cachedArchive);
            ZipFile.ExtractToDirectory(cachedArchive, _restoredPackageDirectory);

            var packagePath = $"{PackageId.ToLowerInvariant()}/{PackageVersion}";
            var nodeKey = $"{PackageId}/{PackageVersion}";
            var targetNode = new Dictionary<string, object>
            {
                ["type"] = "package",
                ["dependencies"] = new Dictionary<string, string>(),
                ["build"] = new Dictionary<string, object>()
            };
            var assets = new
            {
                targets = new Dictionary<string, object> { ["net10.0"] = new Dictionary<string, object> { [nodeKey] = targetNode } },
                libraries = new Dictionary<string, object> { [nodeKey] = new { type = "package", path = packagePath } },
                packageFolders = new Dictionary<string, object> { [Path.GetFullPath(packageCache) + Path.DirectorySeparatorChar] = new Dictionary<string, object>() },
                project = new
                {
                    frameworks = new Dictionary<string, object> { ["net10.0"] = new Dictionary<string, object>() },
                    restore = new Dictionary<string, object>()
                }
            };
            var assetsPath = TestPathUtils.PathUnder(request.WorkingDirectory, "obj", "project.assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(assetsPath)!);
            await File.WriteAllBytesAsync(assetsPath, JsonSerializer.SerializeToUtf8Bytes(assets), cancellationToken);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(request.WorkingDirectory, "packages.lock.json"), "{\"version\":1,\"dependencies\":{}}", cancellationToken);
        }
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
