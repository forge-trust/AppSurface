using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ForgeTrust.AppSurface.Testing;
using Xunit.Abstractions;
using ValidationProgram = ForgeTrust.AppSurface.Evidence.Validation.Program;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidencePackedReuseJourneyTests(ITestOutputHelper output)
{
    private const string DiagnosticsParentVariable = "APPSURFACE_PACKED_JOURNEY_DIAGNOSTICS";

    [Fact]
    public void PhysicalDirectoryPreservesFilesystemRoots()
    {
        var temporaryRoot = new DirectoryInfo(Path.GetTempPath()).Root;
        var applicationRoot = new DirectoryInfo(AppContext.BaseDirectory).Root;

        Assert.Equal(temporaryRoot.FullName, PhysicalDirectory(temporaryRoot));
        Assert.Equal(applicationRoot.FullName, PhysicalDirectory(applicationRoot));
    }

    [Fact]
    public void DiagnosticsParentRejectsRelativeMissingAndCheckoutDirectories()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository, "relative-diagnostics"));
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository,
            Path.Join(Path.GetTempPath(), "missing-diagnostics-" + Guid.NewGuid().ToString("N"))));
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository, repository));
        Assert.Throws<ArgumentException>(() => CreateScratchDirectory(repository, AppContext.BaseDirectory));
    }

    [Fact]
    public void DiagnosticsParentReceivesUniquePrivateChildrenAndPreservesExistingFiles()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var parent = Directory.CreateTempSubdirectory("packed-diagnostics-parent-").FullName;
        try
        {
            var sentinel = Path.Join(parent, "sentinel.txt");
            File.WriteAllText(sentinel, "caller-owned");
            var first = CreateScratchDirectory(repository, parent);
            var second = CreateScratchDirectory(repository, parent);
            Assert.NotEqual(first, second);
            Assert.Equal(PhysicalDirectory(new DirectoryInfo(parent)), Directory.GetParent(first)!.FullName);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectoryInfo(first).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                Assert.Equal(WindowsIdentity.GetCurrent().User, security.GetOwner(typeof(SecurityIdentifier)));
                var rule = Assert.Single(security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
                Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
                Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(first));
            }
            NativeEvidenceTestDirectory.Delete(first);
            NativeEvidenceTestDirectory.Delete(second);
            Assert.Equal("caller-owned", File.ReadAllText(sentinel));
        }
        finally
        {
            NativeEvidenceTestDirectory.Delete(parent);
        }
    }

    [Fact]
    public async Task ActualPackedConsumersRestoreTheOriginalReportsAndSkipWarmHostStartup()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        // Assembly.Location uses the physical path on macOS; pin scratch to the same spelling
        // before the proof compares the independently loaded consumer assemblies to their root.
        var scratch = CreateScratchDirectory(repository, Environment.GetEnvironmentVariable(DiagnosticsParentVariable));
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

            var consumerLog = TestPathUtils.PathUnder(scratch, "consumer-proof.log");
            await File.WriteAllTextAsync(consumerLog, JsonSerializer.Serialize(new
            {
                stage = "consumer-proof",
                state = "starting",
                startedAtUtc = DateTimeOffset.UtcNow
            }), deadline.Token);
            var result = await ValidationProgram.RunAsync(
                ["--operation", "reuse-package-proof", "--feed", feed, "--version", version,
                    "--output", proofRoot, "--dotnet", dotnet, "--source-commit", commit], deadline.Token);
            await File.WriteAllTextAsync(consumerLog, JsonSerializer.Serialize(new
            {
                stage = "consumer-proof",
                state = "completed",
                exitCode = result
            }), CancellationToken.None);
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
            if (passed) NativeEvidenceTestDirectory.Delete(scratch);
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

    /// <summary>Creates private test scratch, optionally beneath an existing diagnostics parent outside the checkout.</summary>
    /// <remarks>
    /// A configured parent receives a random private child (Unix mode 0700 or protected current-user Windows ACL),
    /// including when runner diagnostics and platform temporary storage occupy different volumes.
    /// Successful journeys delete only their child; failures retain it. Without a parent, BCL temporary creation is used.
    /// Physical paths reject a diagnostics parent that resolves into the checkout. This is trusted runner setup,
    /// not an adversarial filesystem sandbox. The caller owns cleanup and must not treat retained logs as acceptance.
    /// </remarks>
    internal static string CreateScratchDirectory(string repository, string? diagnosticsParent)
    {
        string? parent = null;
        if (!string.IsNullOrEmpty(diagnosticsParent))
        {
            if (!Path.IsPathFullyQualified(diagnosticsParent) || !Directory.Exists(diagnosticsParent))
                throw new ArgumentException("Packed journey diagnostics require an existing absolute parent.", nameof(diagnosticsParent));
            parent = PhysicalDirectory(new DirectoryInfo(diagnosticsParent));
            var checkout = Path.TrimEndingDirectorySeparator(PhysicalDirectory(new DirectoryInfo(repository)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (parent.Equals(checkout, comparison) || parent.StartsWith(checkout + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("Packed journey diagnostics must be outside the checkout.", nameof(diagnosticsParent));
        }

        if (parent is null) return PhysicalDirectory(Directory.CreateTempSubdirectory("evidence-packed-journey-"));
        var destination = Path.Join(parent, "evidence-packed-journey-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current Windows user has no security identifier.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(destination).Create(security);
        }
        else Directory.CreateDirectory(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return PhysicalDirectory(new DirectoryInfo(destination));
    }

    private static string PhysicalDirectory(DirectoryInfo directory)
    {
        // Volume and UNC roots are terminal; Windows cannot inspect them as links.
        if (directory.Parent is not { } parent) return directory.FullName;
        if (directory.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            return PhysicalDirectory(new DirectoryInfo(target.FullName));
        return TestPathUtils.PathUnder(PhysicalDirectory(parent), directory.Name);
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
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(logs, stage + ".log"), JsonSerializer.Serialize(new
        {
            stage,
            state = "starting",
            startedAtUtc = DateTimeOffset.UtcNow
        }), cancellationToken);
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
