using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

[Collection("Native evidence program")]
public sealed class NativeEvidenceProgramTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanInFailure_RedactsPhysicalInputPathsAndBoundsRetainedDiagnostics(bool longPath)
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        var relative = longPath ? string.Join('/', Enumerable.Repeat(new string('x', 120), 10)) + "/missing.json" : "missing.json";
        var missing = TestPathUtils.PathUnder(fixture.Repository, relative);
        var request = fixture.Request with { PrimaryGateJson = missing };
        var expected = "Invalid or oversized evidence: " + missing.Replace(fixture.Repository, "<input>", StringComparison.Ordinal);
        if (longPath) Assert.True(expected.Length > 1024);
        var root = Directory.CreateTempSubdirectory("appsurface-native-program-diagnostic-").FullName;
        try
        {
            var package = Path.Join(root, "candidate.nupkg");
            await File.WriteAllBytesAsync(package, []);
            var requestPath = Path.Join(root, "fan-in.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, Program.JsonOptions));
            var output = Path.Join(root, "evidence");

            var result = await InvokeAsync(["--package", package, "--version", "1.0.0", "--source-commit", "source",
                "--dotnet", ResolveDotnetHost(), "--output", output, "--mode", "storage", "--scratch-parent", root,
                "--fan-in-request", requestPath]);

            Assert.Equal(1, result.ExitCode);
            var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(output, "evidence-plan.json")));
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(output, "evidence-manifest.json")));
            Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            var producer = Assert.Single(manifest.ProducerResults, item => item.ProducerId == "native-fan-in");
            Assert.Equal(EvidenceProducerOutcome.Failed, producer.Outcome);
            Assert.Empty(producer.SatisfiedAssertionIds);
            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(output, "artifacts/native-fan-in/proof/result.json")));
            Assert.False(artifact.RootElement.GetProperty("verified").GetBoolean());
            var diagnostic = artifact.RootElement.GetProperty("diagnostic").GetString()!;
            Assert.Equal(longPath ? expected[..1024] : expected, diagnostic);
            Assert.DoesNotContain(fixture.Repository, diagnostic, StringComparison.Ordinal);
            Assert.Equal("Native coverage fan-in verification failed: " + diagnostic, producer.Diagnostic);
            Assert.True(manifest.Metrics.CleanupCompleted);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EmptyArguments_ReturnTwoAndPrintsUsage()
    {
        var result = await InvokeAsync([]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--package <candidate.nupkg>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--output <fresh-evidence-directory>", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ReturnsZeroAndPrintsUsage()
    {
        var result = await InvokeAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("AppSurface EvidenceHost acceptance", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--scope all|native|coverage|packages", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("unsupported-option")]
    public async Task Collect_RejectsOutputCollisionOrUnsupportedOptionsWithoutModifyingCallerInput(string failure)
    {
        using var fixture = new NativeCaptureFixture();
        var output = Path.Join(fixture.Root, "collection");
        if (failure == "directory") Directory.CreateDirectory(output);
        if (failure == "file") await File.WriteAllTextAsync(output, "caller-owned");
        var args = new List<string> { "--operation", "collect", "--output", output };
        if (failure == "unsupported-option") args.AddRange(["--scope", "coverage"]);

        var result = await InvokeAsync(args.ToArray());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ArgumentException", result.StandardError, StringComparison.Ordinal);
        if (failure == "file") Assert.Equal("caller-owned", await File.ReadAllTextAsync(output));
        if (failure == "directory") Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        if (failure == "unsupported-option") Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("local-plan")]
    public async Task LocalOperations_DispatchToSharedPolicyAndCompleteAnEmptyInventory(string operation)
    {
        using var fixture = new NativeCaptureFixture();
        var output = Path.Join(fixture.Root, "local-evidence");
        var policy = Path.Join(fixture.Root, "policy.json");
        File.Copy(Path.Join(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), ".github", "evidence.policy.json"), policy);

        var result = await InvokeAsync(["--operation", operation, "--repository", fixture.Repository,
            "--base", fixture.Commit, "--policy", policy, "--output", output]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No local changes", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(output, "proof", "evidence-manifest.json")));
    }

    [Fact]
    public async Task CaptureNative_UsesRealCheckoutAndWritesBoundIdentityReceipt()
    {
        using var fixture = new NativeCaptureFixture();
        var sdk = await RunAsync(ResolveDotnetHost(), ["--version"], fixture.Repository);
        Assert.Equal(0, sdk.ExitCode);
        var runValues = new Dictionary<string, string?>
        {
            ["GITHUB_REPOSITORY"] = "example/native-capture-tests",
            ["GITHUB_RUN_ID"] = "123456",
            ["GITHUB_RUN_ATTEMPT"] = "2",
        };
        var previous = runValues.ToDictionary(pair => pair.Key, pair => Environment.GetEnvironmentVariable(pair.Key));
        try
        {
            foreach (var (key, value) in runValues) Environment.SetEnvironmentVariable(key, value);
            var result = await InvokeAsync(["--operation", "capture-native", "--repository", fixture.Repository,
                "--report", fixture.Report, "--raw-report", fixture.RawReport, "--output", fixture.Output,
                "--dotnet", ResolveDotnetHost()]);

            Assert.True(result.ExitCode == 0, result.StandardError);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(fixture.Output, "manifest.json")));
            var root = manifest.RootElement;
            Assert.Equal(fixture.Commit, root.GetProperty("commit").GetString());
            Assert.Equal(fixture.Tree, root.GetProperty("tree").GetString());
            Assert.Equal(sdk.StandardOutput.Trim(), root.GetProperty("sdk").GetString());
            Assert.Equal("native", root.GetProperty("scope").GetString());
            Assert.Equal(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "Linux",
                root.GetProperty("platform").GetString());
            Assert.Equal(runValues.Count, root.GetProperty("run").EnumerateObject().Count());
            foreach (var (key, value) in runValues) Assert.Equal(value, root.GetProperty("run").GetProperty(key).GetString());
            Assert.Equal(["collector.cobertura.xml", "coverage.cobertura.xml"],
                root.GetProperty("files").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.True(File.Exists(Path.Join(fixture.Output, "collector.cobertura.xml")));
            Assert.True(File.Exists(Path.Join(fixture.Output, "coverage.cobertura.xml")));
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("missing-host")]
    [InlineData("unsupported-option")]
    [InlineData("missing-project-inventory")]
    public async Task CaptureNative_RejectsInvalidCheckoutOrOptions(string failure)
    {
        using var fixture = new NativeCaptureFixture();
        var runValues = new Dictionary<string, string?>
        {
            ["GITHUB_REPOSITORY"] = "example/native-capture-tests",
            ["GITHUB_RUN_ID"] = "123456",
            ["GITHUB_RUN_ATTEMPT"] = "2",
        };
        var previous = runValues.ToDictionary(pair => pair.Key, pair => Environment.GetEnvironmentVariable(pair.Key));
        var args = new List<string> { "--operation", "capture-native", "--repository", fixture.Repository,
            "--report", fixture.Report, "--raw-report", fixture.RawReport, "--output", fixture.Output,
            "--dotnet", failure == "missing-host" ? Path.Join(fixture.Root, "missing-dotnet") : ResolveDotnetHost() };
        if (failure == "dirty") await File.WriteAllTextAsync(Path.Join(fixture.Repository, "dirty.txt"), "dirty");
        if (failure == "unsupported-option") args.AddRange(["--scope", "native"]);
        if (failure == "missing-project-inventory")
        {
            await File.WriteAllTextAsync(Path.Join(fixture.Repository, "ForgeTrust.AppSurface.slnx"), "<Solution />");
            fixture.AmendSolutionCommit();
            Assert.Empty(fixture.RunGit("status", "--porcelain"));
            foreach (var (key, value) in runValues) Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            var result = await InvokeAsync(args.ToArray());

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Native validation failed", result.StandardError, StringComparison.Ordinal);
            Assert.Contains(failure switch
            {
                "dirty" => "InvalidOperationException",
                "missing-host" => "EvidenceCommandException",
                "unsupported-option" or "missing-project-inventory" => "ArgumentException",
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            }, result.StandardError, StringComparison.Ordinal);
            Assert.False(Directory.Exists(fixture.Output));
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Theory]
    [InlineData("unknown", "--unknown value")]
    [InlineData("missing", "--package candidate.nupkg")]
    [InlineData("duplicate", "--package first.nupkg --package second.nupkg")]
    [InlineData("odd", "--package")]
    public async Task InvalidArguments_ReturnOneAndLeaveFilesystemUntouched(string _, string commandLine)
    {
        var result = await InvokeAsync(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Native validation failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("ArgumentException", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingOutput_ReturnsOneAndPreservesCallerFiles()
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-program-output-").FullName;
        try
        {
            var output = Path.Join(root, "evidence");
            Directory.CreateDirectory(output);
            var sentinel = Path.Join(output, "caller-owned.txt");
            await File.WriteAllTextAsync(sentinel, "preserve me");

            var result = await InvokeAsync(["--package", Path.Join(root, "candidate.nupkg"), "--version", "1.0.0",
                "--source-commit", "source", "--dotnet", ResolveDotnetHost(), "--output", output]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("ArgumentException", result.StandardError, StringComparison.Ordinal);
            Assert.Equal("preserve me", await File.ReadAllTextAsync(sentinel));
            Assert.DoesNotContain("evidence-plan.json", Directory.EnumerateFiles(output).Select(Path.GetFileName));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CanceledInvocation_Returns130AndDoesNotPublishPartialOutput()
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-program-cancel-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Join(root, "candidate.nupkg"), []);
            var output = Path.Join(root, "evidence");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await InvokeAsync(["--package", Path.Join(root, "candidate.nupkg"), "--version", "1.0.0",
                "--source-commit", "source", "--dotnet", ResolveDotnetHost(), "--output", output], cancellation.Token);

            Assert.True(result.ExitCode == 130, result.StandardError);
            Assert.Contains("Native validation cancelled", result.StandardError, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("consumer")]
    public async Task MinimalCandidate_ProducesNativePlanAndManifestThroughOrchestration(string mode)
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-program-plan-").FullName;
        try
        {
            var package = Path.Join(root, "candidate.nupkg");
            await File.WriteAllBytesAsync(package, []);
            var output = Path.Join(root, "evidence");
            var result = await InvokeAsync(["--package", package, "--version", "1.0.0", "--source-commit", "source",
                "--dotnet", ResolveDotnetHost(), "--output", output, "--mode", mode, "--scratch-parent", root,
                "--maximum-scratch-bytes", "1048576", "--minimum-free-bytes", "0"]);

            Assert.Equal(1, result.ExitCode);
            Assert.True(File.Exists(Path.Join(output, "evidence-plan.json")));
            Assert.True(File.Exists(Path.Join(output, "evidence-manifest.json")));
            Assert.Contains(mode == "storage" ? "packed-storage" : "installed-consumer", result.StandardOutput, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanInRequest_AddsRequiredNativeVerificationToTheManifest(bool tampered)
    {
        using var fixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fixture.WriteMinimalBundleAsync();
        if (tampered)
        {
            var gate = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.ComposedGate))!;
            gate["passed"] = false;
            await File.WriteAllTextAsync(fixture.ComposedGate, gate.ToJsonString());
        }
        var root = Directory.CreateTempSubdirectory("appsurface-native-program-fanin-").FullName;
        try
        {
            var package = Path.Join(root, "candidate.nupkg");
            await File.WriteAllBytesAsync(package, []);
            var request = Path.Join(root, "fan-in.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(fixture.Request, Program.JsonOptions));
            var output = Path.Join(root, "evidence");

            var result = await InvokeAsync(["--package", package, "--version", "1.0.0", "--source-commit", "source",
                "--dotnet", ResolveDotnetHost(), "--output", output, "--mode", "storage", "--scratch-parent", root,
                "--fan-in-request", request]);

            Assert.Equal(1, result.ExitCode); // Invalid storage package must still prevent completion.
            var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(Path.Join(output, "evidence-plan.json")));
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(output, "evidence-manifest.json")));
            Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
            var native = Assert.Single(manifest.ProducerResults, producer => producer.ProducerId == "native-fan-in");
            Assert.Equal(tampered ? EvidenceProducerOutcome.Failed : EvidenceProducerOutcome.Passed, native.Outcome);
            Assert.Equal(tampered ? [] : new[] { "coverage/verified-native-fan-in@1" }, native.SatisfiedAssertionIds);
            Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
            Assert.True(File.Exists(Path.Join(output, "artifacts/native-fan-in/proof/result.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--mode", "unknown")]
    [InlineData("--version", " ")]
    [InlineData("--dotnet", "missing-dotnet")]
    [InlineData("--consumer-profile", "null")]
    [InlineData("--fan-in-request", "null")]
    public async Task InvalidExecutionInputs_CannotPublishACompleteClaim(string option, string value)
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-program-invalid-").FullName;
        try
        {
            var output = Path.Join(root, "evidence");
            var args = new List<string> { "--package", Path.Join(root, "candidate.nupkg"), "--version", "1.0.0", "--source-commit", "source",
                "--dotnet", ResolveDotnetHost(), "--output", output };
            if (option is "--consumer-profile" or "--fan-in-request")
            {
                var json = Path.Join(root, "input.json");
                await File.WriteAllTextAsync(json, value);
                args.AddRange([option, json]);
            }
            else if (option == "--dotnet") args[args.IndexOf(option) + 1] = Path.Join(root, value);
            else if (option == "--version") args[args.IndexOf(option) + 1] = value;
            else args.AddRange([option, value]);

            var result = await InvokeAsync(args.ToArray());

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("ArgumentException", result.StandardError, StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<InvocationResult> InvokeAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var originalOutput = global::System.Console.Out;
        var originalError = global::System.Console.Error;
        try
        {
            global::System.Console.SetOut(output);
            global::System.Console.SetError(error);
            var exitCode = args.SequenceEqual(new[] { "--help" })
                ? await Program.Main(args)
                : await Program.RunAsync(args, cancellationToken);
            return new InvocationResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            global::System.Console.SetOut(originalOutput);
            global::System.Console.SetError(originalError);
            output.Dispose();
            error.Dispose();
        }
    }

    private static string ResolveDotnetHost()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host) && Path.IsPathFullyQualified(host)) return host;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root)) return Path.Join(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, executable)).First(File.Exists);
    }

    private static async Task<ProcessResult> RunAsync(string executable, string[] arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start process.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class NativeCaptureFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("appsurface-native-capture-").FullName;
        public string Repository => Path.Join(Root, "repo");
        public string Report => Path.Join(Root, "normalized.xml");
        public string RawReport => Path.Join(Root, "raw.xml");
        public string Output => Path.Join(Root, "capture");
        public string Commit { get; private set; }
        public string Tree { get; private set; }
        public NativeCaptureFixture()
        {
            Directory.CreateDirectory(Repository);
            File.WriteAllText(Path.Join(Repository, "ForgeTrust.AppSurface.slnx"),
                "<Solution><Project Path=\"tests/Fixture.Tests/Fixture.Tests.csproj\" /></Solution>");
            File.WriteAllText(Path.Join(Repository, "tracked.txt"), "tracked");
            var xml = $"<coverage lines-valid=\"1\" lines-covered=\"1\" branches-valid=\"0\" branches-covered=\"0\"><sources /><packages><package name=\"Fixture\"><classes><class name=\"Fixture\" filename=\"{Path.Join(Repository, "tracked.txt").Replace("\\", "/")}\"><methods /><lines><line number=\"1\" hits=\"1\" /></lines></class></classes></package></packages></coverage>";
            File.WriteAllText(Report, xml);
            File.WriteAllText(RawReport, xml);
            RunGit("init");
            RunGit("config", "user.email", "tests@example.invalid");
            RunGit("config", "user.name", "Evidence Tests");
            RunGit("add", "ForgeTrust.AppSurface.slnx", "tracked.txt");
            RunGit("commit", "-m", "fixture");
            Commit = RunGit("rev-parse", "HEAD").Trim();
            Tree = RunGit("rev-parse", "HEAD^{tree}").Trim();
        }

        public void AmendSolutionCommit()
        {
            RunGit("add", "ForgeTrust.AppSurface.slnx");
            RunGit("commit", "--amend", "--no-edit");
            Commit = RunGit("rev-parse", "HEAD").Trim();
            Tree = RunGit("rev-parse", "HEAD^{tree}").Trim();
        }

        public string RunGit(params string[] args)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in args) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record InvocationResult(int ExitCode, string StandardOutput, string StandardError);
}

[CollectionDefinition("Native evidence program", DisableParallelization = true)]
public sealed class NativeEvidenceProgramCollection;
