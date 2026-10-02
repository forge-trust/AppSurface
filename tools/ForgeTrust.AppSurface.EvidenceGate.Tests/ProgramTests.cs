using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.EvidenceGate;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class ProgramTests
{
    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "other" },
        new[] { "run" },
        new[] { "run", "--plan", "plan", "--policy", "policy", "--repository", "repository" },
        new[] { "run", "--plan", "first", "--plan", "second", "--repository", "repository", "--output-dir", "output" },
        new[] { "run", "--plan", "", "--policy", "policy", "--repository", "repository", "--output-dir", "output" },
        new[] { "verify-gate" },
        new[] { "verify-gate", "--plan", "plan", "--manifest", "manifest", "--policy", "policy", "--repository", "repository", "--identity", "identity", "--artifacts-dir" },
        new[] { "verify-gate", "--plan", "plan", "--manifest", "first", "--manifest", "second", "--policy", "policy", "--repository", "repository", "--identity", "identity" },
        new[] { "verify-gate", "--plan", "plan", "--manifest", "manifest", "--policy", "policy", "--repository", "repository", "--identity", "identity", "--artifacts-dir", "artifacts" },
        new[] { "verify-gate", "--plan", "plan", "--manifest", "manifest", "--policy", "policy", "--repository", "repository", "--identity", "identity", "--output-dir", "output", "--artifacts-dir" },
        new[] { "verify-gate", "--plan", "plan", "--manifest", "manifest", "--policy", "policy", "--repository", "repository", "--identity", "identity", "--output-dir", "output", "--artifacts-dir", "artifacts", "--plan", "duplicate" },
        new[] { "verify-gate", "--plan", "plan", "--manifest", "manifest", "--policy", "policy", "--repository", "repository", "--identity", "identity", "--output-dir", "output", "--unknown", "value" },
    };

    [Fact]
    public async Task HelpWritesBothCommandFormsAndExitsSuccessfully()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(["--help"], standardOutput, standardError);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: appsurface-evidence-gate run", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: appsurface-evidence-gate verify-gate", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("Gate verification is a separate trusted-controller operation.", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task MainUsesFailClosedDispatchForHelpInvalidAndAcceptedCommands()
    {
        using var fixture = new TemporaryDirectory();
        var planPath = Path.Join(fixture.Path, "plan.json");
        await File.WriteAllTextAsync(planPath, "{not-json");

        Assert.Equal(0, await Program.Main(["--help"]));
        Assert.Equal(64, await Program.Main(["run"]));
        Assert.Equal(64, await Program.Main(["verify-gate"]));
        Assert.Equal(2, await Program.Main(
        [
            "run",
            "--plan", planPath,
            "--policy", Path.Join(fixture.Path, "policy.json"),
            "--repository", Path.Join(fixture.Path, "repository"),
            "--output-dir", Path.Join(fixture.Path, "output"),
        ]));
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task MalformedDuplicateOrMissingArgumentsAreRejected(string[] args)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(args, standardOutput, standardError);

        Assert.Equal(64, exitCode);
        Assert.Empty(standardOutput.ToString());
        Assert.Contains("Invalid command arguments.", standardError.ToString(), StringComparison.Ordinal);
        Assert.Contains(args.Length > 0 && args[0] == "verify-gate" ? "verify-gate" : " run ", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommandRoutesToHostRunnerAndFailsClosedOnMalformedPlan()
    {
        using var fixture = new TemporaryDirectory();
        var planPath = Path.Join(fixture.Path, "plan.json");
        var outputDirectory = Path.Join(fixture.Path, "output");
        await File.WriteAllTextAsync(planPath, "{not-json");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "run",
                "--plan", planPath,
                "--policy", Path.Join(fixture.Path, "policy.json"),
                "--repository", Path.Join(fixture.Path, "repository"),
                "--output-dir", outputDirectory,
            ],
            standardOutput,
            standardError);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGH103", standardError.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(outputDirectory, "evidence-manifest.json")));
    }

    [Fact]
    public async Task VerifyGateCommandRoutesToVerifierAndFailsClosedOnMissingInput()
    {
        using var fixture = new TemporaryDirectory();
        var outputDirectory = Path.Join(fixture.Path, "output");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "verify-gate",
                "--plan", Path.Join(fixture.Path, "missing-plan.json"),
                "--manifest", Path.Join(fixture.Path, "manifest.json"),
                "--policy", Path.Join(fixture.Path, "policy.json"),
                "--repository", Path.Join(fixture.Path, "repository"),
                "--identity", Path.Join(fixture.Path, "identity.json"),
                "--output-dir", outputDirectory,
            ],
            standardOutput,
            standardError);

        Assert.Equal(2, exitCode);
        Assert.Contains("ASEGG102", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("ASEGG102", standardError.ToString(), StringComparison.Ordinal);
        var resultPath = Path.Join(outputDirectory, "evidence-gate-verification.json");
        Assert.True(File.Exists(resultPath));
        var result = EvidenceCanonicalJson.Deserialize<EvidencePullRequestGateVerificationResult>(await File.ReadAllBytesAsync(resultPath));
        Assert.False(result.IsEligible);
        Assert.Equal("ASEGG102", result.Code);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "appsurface-evidencegate-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
