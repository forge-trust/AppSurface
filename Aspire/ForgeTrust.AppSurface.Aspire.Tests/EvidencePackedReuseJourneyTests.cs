using System.Diagnostics;
using System.Text.Json;
using ForgeTrust.AppSurface.Testing;
using Xunit.Abstractions;
using ValidationProgram = ForgeTrust.AppSurface.Evidence.Validation.Program;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidencePackedReuseJourneyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ActualPackedConsumersRestoreTheOriginalReportsAndSkipWarmHostStartup()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        // Assembly.Location uses the physical path on macOS; pin scratch to the same spelling
        // before the proof compares the independently loaded consumer assemblies to their root.
        var scratch = PhysicalDirectory(Directory.CreateTempSubdirectory("evidence-packed-journey-"));
        var feed = TestPathUtils.PathUnder(scratch, "feed");
        var proofRoot = TestPathUtils.PathUnder(scratch, "proof");
        Directory.CreateDirectory(feed);
        var dotnet = ResolveDotnet();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var passed = false;
        try
        {
            var commit = (await RunAsync("git", ["rev-parse", "HEAD"], repository, scratch, "source", deadline.Token)).Trim();
            var version = "0.0.0-packed-test." + Guid.NewGuid().ToString("N");
            foreach (var project in new[]
            {
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/ForgeTrust.AppSurface.Evidence.Contracts.csproj",
                "Evidence/ForgeTrust.AppSurface.Evidence.Planner/ForgeTrust.AppSurface.Evidence.Planner.csproj",
                "Evidence/ForgeTrust.AppSurface.Evidence.Readiness/ForgeTrust.AppSurface.Evidence.Readiness.csproj",
                "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/ForgeTrust.AppSurface.Evidence.Coverage.csproj",
                "Coverage/ForgeTrust.AppSurface.Coverage/ForgeTrust.AppSurface.Coverage.csproj",
                "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/ForgeTrust.AppSurface.Evidence.Aspire.csproj",
                "Evidence/ForgeTrust.AppSurface.Evidence.Cli/ForgeTrust.AppSurface.Evidence.Cli.csproj"
            })
            {
                await RunAsync(dotnet,
                    ["pack", TestPathUtils.PathUnder(repository, project), "-c", "Release", "--output", feed,
                        "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false",
                        "-p:PackageVersion=" + version, "-p:RepositoryCommit=" + commit, "-p:SourceRevisionId=" + commit],
                    repository, scratch, "pack-" + Path.GetFileNameWithoutExtension(project), deadline.Token);
            }

            var result = await ValidationProgram.RunAsync(
                ["--operation", "reuse-package-proof", "--feed", feed, "--version", version,
                    "--output", proofRoot, "--dotnet", dotnet, "--source-commit", commit], deadline.Token);
            var reportPath = TestPathUtils.PathUnder(proofRoot, "proof.json");
            var report = await File.ReadAllTextAsync(reportPath, deadline.Token);
            Assert.True(result == 0, $"Packed journey failed. Retained report {reportPath}:{Environment.NewLine}{report}");
            using var document = JsonDocument.Parse(report);
            var proof = document.RootElement;
            Assert.True(proof.GetProperty("passed").GetBoolean());
            Assert.Equal(commit, proof.GetProperty("expectedSourceCommit").GetString());
            Assert.Equal(8, proof.GetProperty("packages").GetArrayLength());
            foreach (var mode in new[] { "built", "publish" })
            {
                var host = proof.GetProperty("host").GetProperty(mode);
                Assert.Equal(1, host.GetProperty("firstFactoryCount").GetInt32());
                Assert.Equal(0, host.GetProperty("secondFactoryCount").GetInt32());
                Assert.Equal(1, host.GetProperty("cleanedApplications").GetInt32());
                Assert.Equal("Reused", host.GetProperty("secondOutcome").GetString());
            }

            var runs = proof.GetProperty("cli").GetProperty("runs").EnumerateArray().ToArray();
            Assert.Equal(["fresh", "warm", "audit"], runs.Select(run => run.GetProperty("mode").GetString()));
            Assert.Equal(["Passed", "Reused", "Passed"], runs.Select(run => run.GetProperty("outcome").GetString()));
            Assert.Equal(runs[0].GetProperty("coverageSha256").GetString(), runs[1].GetProperty("coverageSha256").GetString());
            Assert.All(runs, run =>
            {
                Assert.Equal(6, run.GetProperty("testCaseCount").GetInt32());
                Assert.Equal(2, run.GetProperty("testProjectCount").GetInt32());
                Assert.True(run.GetProperty("artifactCount").GetInt32() > 5);
            });
            passed = true;
        }
        finally
        {
            if (passed) Directory.Delete(scratch, recursive: true);
            else output.WriteLine("Retained packed consumer diagnostics: " + scratch);
        }
    }

    internal static string ResolveDotnet()
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(explicitHost) && File.Exists(explicitHost)) return Path.GetFullPath(explicitHost);
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(TestPathUtils.PathUnder(root, executable)))
            return TestPathUtils.PathUnder(root, executable);
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Where(Directory.Exists).Select(directory => TestPathUtils.PathUnder(directory, executable)).First(File.Exists);
    }

    private static string PhysicalDirectory(DirectoryInfo directory)
    {
        if (directory.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            return PhysicalDirectory(new DirectoryInfo(target.FullName));
        return directory.Parent is { } parent
            ? TestPathUtils.PathUnder(PhysicalDirectory(parent), directory.Name)
            : directory.FullName;
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, string logs, string stage, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(logs, stage + ".log"), await stdout + await stderr,
                CancellationToken.None);
        }
        var standardOutput = await stdout;
        Assert.True(process.ExitCode == 0, $"{stage} exited {process.ExitCode}; retained logs: {logs}");
        return standardOutput;
    }
}
