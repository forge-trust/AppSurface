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
        Assert.Contains("Native AppSurface EvidenceHost acceptance", result.StandardOutput, StringComparison.Ordinal);
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

    private sealed record InvocationResult(int ExitCode, string StandardOutput, string StandardError);
}

[CollectionDefinition("Native evidence program", DisableParallelization = true)]
public sealed class NativeEvidenceProgramCollection;
