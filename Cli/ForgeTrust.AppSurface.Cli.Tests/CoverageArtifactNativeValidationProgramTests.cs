using ForgeTrust.AppSurface.Testing;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using NativeProgram = ForgeTrust.AppSurface.Evidence.Validation.Program;

namespace ForgeTrust.AppSurface.Cli.Tests;

// The native collector retains the entrypoint's raw MoveNext records separately from
// the folded primary report. Exercise the actual receipt/exit boundary in that cohort.
[Collection("CoverageGate process state")]
public sealed class CoverageArtifactNativeValidationProgramTests
{
    [Theory]
    [InlineData("Native package journeys selected:", "cancelled", true, 130)]
    [InlineData("Recipe:", "failed", false, 1)]
    public async Task CancellationAfterPlanOrReceiptFinalization_UsesReceiptOutcomeForExitCode(
        string cancellationLine,
        string expectedOutcome,
        bool expectedCallerCancellation,
        int expectedExitCode)
    {
        using var directory = TestDirectory.Create();
        var package = TestPathUtils.PathUnder(directory.Path, "candidate.nupkg");
        await File.WriteAllBytesAsync(package, []);
        var output = TestPathUtils.PathUnder(directory.Path, "evidence");
        using var cancellation = new CancellationTokenSource();
        using var writer = new CancelOnLineWriter(cancellation, cancellationLine);
        using var errors = new StringWriter();
        var originalOutput = global::System.Console.Out;
        var originalError = global::System.Console.Error;
        int exitCode;
        try
        {
            global::System.Console.SetOut(writer);
            global::System.Console.SetError(errors);
            exitCode = await NativeProgram.RunAsync(
                ["--package", package, "--version", "1.0.0", "--source-commit", "source",
                    "--dotnet", ResolveDotnetHost(), "--output", output, "--mode", "storage",
                    "--scratch-parent", directory.Path, "--maximum-scratch-bytes", "1048576",
                    "--minimum-free-bytes", "0"], cancellation.Token);
        }
        finally
        {
            global::System.Console.SetOut(originalOutput);
            global::System.Console.SetError(originalError);
        }

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains("Recipe: " + expectedOutcome, writer.ToString(), StringComparison.Ordinal);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "evidence-plan.json")));
        var receipt = NativeValidationReceiptCodec.Deserialize(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "native-validation-receipt.json")));
        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal(expectedOutcome, receipt.RunOutcome);
        Assert.Equal(expectedCallerCancellation, receipt.CallerCancellationRequested);
        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal("none", receipt.EvidenceHostClaim);
        Assert.Equal("none", receipt.EvidenceHostEligibility);
        Assert.False(receipt.EvidenceHostProofProduced);
        Assert.False(File.Exists(TestPathUtils.PathUnder(output, "evidence-manifest.json")));
    }

    private sealed class CancelOnLineWriter(CancellationTokenSource cancellation, string linePrefix) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith(linePrefix, StringComparison.Ordinal) == true) cancellation.Cancel();
        }
    }

    private static string ResolveDotnetHost()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host) && Path.IsPathFullyQualified(host)) return host;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root)) return TestPathUtils.PathUnder(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => TestPathUtils.PathUnder(directory, executable)).First(File.Exists);
    }
}
