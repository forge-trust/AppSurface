using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class PackageChecksProducerTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory;
        public TemporaryDirectory(string prefix)
        {
            _directory = Directory.CreateTempSubdirectory(prefix);
        }
        public string FullName => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
    private static readonly string[] Assertions = ["package/manifest@1", "package/generated-docs@1", "package/archives-and-consumers@1"];

    [Fact]
    public async Task ProduceAsync_RunsOnlyThreePackageChecksAndRetainsBoundedProofs()
    {
        using var root = new TemporaryDirectory("package-checks-tests-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var settings = Settings(root.FullName, output);
        var requests = new List<EvidenceCommandRequest>();
        var producer = new PackageChecksProducer(settings, async (request, _) =>
        {
            requests.Add(request);
            if (request.Arguments.Contains("verify-packages", StringComparer.Ordinal))
            {
                var version = ValueAfter(request.Arguments, "--package-version");
                var artifacts = ValueAfter(request.Arguments, "--artifacts-output");
                Directory.CreateDirectory(artifacts);
                var archive = TestPathUtils.PathUnder(artifacts, "Example." + version + ".nupkg");
                await File.WriteAllBytesAsync(archive, [1, 2, 3]);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json"),
                    JsonSerializer.Serialize(new { package_version = version, entries = new[] { new { package_id = "Example", artifact_file_name = Path.GetFileName(archive), sha512 = hash } } }));
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-validation-report.md"), $"# Package validation\nVersion: {version}\nAll packages passed.\n");
            }
            return new EvidenceCommandResult(0, "bounded stdout", "bounded stderr", 12);
        });
        var (context, artifacts) = Context(output);

        var result = await producer.ProduceAsync(context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(Assertions, result.SatisfiedAssertionIds);
        Assert.Equal(3, requests.Count);
        Assert.Equal(new[] { "gate", "verify", "verify-packages" }, requests.Select(r => r.Arguments.Contains("verify-packages", StringComparer.Ordinal) ? "verify-packages" : r.Arguments[^1]));
        Assert.All(requests, request =>
        {
            Assert.Equal(Path.GetFullPath(root.FullName), request.WorkingDirectory);
            Assert.StartsWith(TestPathUtils.PathUnder(output, "package-checks"), request.Environment["NUGET_PACKAGES"], StringComparison.Ordinal);
            Assert.Equal(settings.DotnetPath, request.Environment["DOTNET_HOST_PATH"]);
            Assert.StartsWith(Path.GetDirectoryName(settings.DotnetPath) + Path.PathSeparator, request.Environment["PATH"], StringComparison.Ordinal);
            Assert.DoesNotContain("restore", request.Arguments);
            Assert.DoesNotContain("build", request.Arguments);
        });
        var version = ValueAfter(requests[2].Arguments, "--package-version");
        Assert.Matches("^0\\.0\\.0-evidence\\.[0-9a-f]{32}$", version);
        Assert.Contains("--configuration", requests[0].Arguments);
        Assert.Contains("--property:RestoreConfigFile=" + Path.Join(root.FullName, "NuGet.package-gate.config"), requests[0].Arguments);
        Assert.Equal("Release", ValueAfter(requests[0].Arguments, "--configuration"));
        Assert.Contains(artifacts.WrittenArtifacts, a => a.LogicalName == "package-manifest");
        Assert.Contains(artifacts.WrittenArtifacts, a => a.LogicalName == "package-report");
        using var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "proof", "result.json")));
        Assert.Equal(version, proof.RootElement.GetProperty("packageVersion").GetString());
        Assert.DoesNotContain("bounded stdout", await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "proof", "transcript.json")));
    }

    [Fact]
    public async Task ProduceAsync_WithholdsCompletedResultUntilTranscriptIsRetained()
    {
        using var root = new TemporaryDirectory("package-checks-completion-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var (context, writer) = Context(output);
        await writer.WriteAsync("transcript", "proof/transcript.json", "existing transcript"u8.ToArray());
        var producer = new PackageChecksProducer(Settings(root.FullName, output), async (request, _) =>
        {
            if (request.Arguments.Contains("verify-packages", StringComparer.Ordinal))
            {
                var version = ValueAfter(request.Arguments, "--package-version");
                var artifacts = ValueAfter(request.Arguments, "--artifacts-output");
                var archiveBytes = new byte[] { 1, 2, 3 };
                const string archiveName = "Example.nupkg";
                await File.WriteAllBytesAsync(Path.Join(artifacts, archiveName), archiveBytes);
                var sha512 = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(archiveBytes)).ToLowerInvariant();
                await File.WriteAllTextAsync(Path.Join(artifacts, "package-artifact-manifest.json"),
                    JsonSerializer.Serialize(new { package_version = version, entries = new[] { new { artifact_file_name = archiveName, sha512 } } }));
                await File.WriteAllTextAsync(Path.Join(artifacts, "package-validation-report.md"), $"# Package validation\nVersion: {version}\nAll packages passed.\n");
            }
            return new EvidenceCommandResult(0, "", "", 1);
        });

        var result = await producer.ProduceAsync(context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        using var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(output, "proof", "result.json")));
        Assert.False(proof.RootElement.GetProperty("completed").GetBoolean());
        Assert.Contains("transcript (InvalidOperationException)", result.Diagnostic);
        Assert.Equal("existing transcript", await File.ReadAllTextAsync(Path.Join(output, "proof", "transcript.json")));
    }

    [Fact]
    public void Constructor_RejectsNullSettings()
    {
        Assert.Throws<ArgumentNullException>(() => new PackageChecksProducer(null!));
    }

    [Fact]
    public async Task ProduceAsync_RejectsMismatchedProducerDeclarationBeforeRunningCommands()
    {
        using var root = new TemporaryDirectory("package-checks-declaration-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var commandCount = 0;
        var producer = new PackageChecksProducer(Settings(root.FullName, output), (_, _) =>
        {
            commandCount++;
            return Task.FromResult(new EvidenceCommandResult(0, "", "", 1));
        });
        var context = Context(output).Context;
        context = context with { Producer = context.Producer with { Id = "other-producer" } };

        var result = await producer.ProduceAsync(context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("declaration mismatch", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, commandCount);
    }

    [Fact]
    public async Task ProduceAsync_FailsWhenArtifactWriterIsMissingAfterPackageValidation()
    {
        using var root = new TemporaryDirectory("package-checks-no-writer-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var producer = new PackageChecksProducer(Settings(root.FullName, output), async (request, _) =>
        {
            if (request.Arguments.Contains("verify-packages", StringComparer.Ordinal))
            {
                var version = ValueAfter(request.Arguments, "--package-version");
                var artifacts = ValueAfter(request.Arguments, "--artifacts-output");
                Directory.CreateDirectory(artifacts);
                var archiveBytes = new byte[] { 1, 2, 3 };
                var archive = TestPathUtils.PathUnder(artifacts, "Example." + version + ".nupkg");
                await File.WriteAllBytesAsync(archive, archiveBytes);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(archiveBytes)).ToLowerInvariant();
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json"),
                    JsonSerializer.Serialize(new { package_version = version, entries = new[] { new { package_id = "Example", artifact_file_name = Path.GetFileName(archive), sha512 = hash } } }));
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-validation-report.md"), $"# Package validation\nVersion: {version}\nAll packages passed.\n");
            }
            return new EvidenceCommandResult(0, "", "", 1);
        });
        var context = Context(output).Context with { Artifacts = null };

        var result = await producer.ProduceAsync(context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("no artifact writer", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    [Theory]
    [InlineData("result")]
    [InlineData("transcript")]
    [InlineData("both")]
    [InlineData("io")]
    public async Task ProduceAsync_ReportsUnavailableFailureArtifactsWithoutLosingThePrimaryFailure(string blocked)
    {
        using var root = new TemporaryDirectory("package-checks-failure-writer-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var (context, writer) = Context(output);
        if (blocked == "io") await File.WriteAllTextAsync(Path.Join(output, "proof"), "caller-owned");
        else
            foreach (var name in new[] { "result", "transcript" }.Where(name => blocked == "both" || blocked == name))
                await writer.WriteAsync(name, $"proof/{name}.json", Encoding.UTF8.GetBytes("existing " + name));
        var producer = new PackageChecksProducer(Settings(root.FullName, output), (_, _) =>
            Task.FromResult(new EvidenceCommandResult(1, "", "private failure detail", 1)));

        var result = await producer.ProduceAsync(context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("failed at gate", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains("Failure artifacts unavailable:", result.Diagnostic);
        Assert.DoesNotContain(root.FullName, result.Diagnostic);
        Assert.DoesNotContain("private failure detail", result.Diagnostic);
        if (blocked == "io")
        {
            Assert.Contains("result (IOException)", result.Diagnostic);
            Assert.Contains("transcript (IOException)", result.Diagnostic);
            Assert.Empty(writer.WrittenArtifacts);
            Assert.Equal("caller-owned", await File.ReadAllTextAsync(Path.Join(output, "proof")));
        }
        else
        {
            Assert.Equal(2, writer.WrittenArtifacts.Count);
            foreach (var name in new[] { "result", "transcript" })
            {
                var contents = await File.ReadAllTextAsync(Path.Join(output, "proof", name + ".json"));
                if (blocked == "both" || blocked == name)
                {
                    Assert.Contains(name + " (InvalidOperationException)", result.Diagnostic);
                    Assert.Equal("existing " + name, contents);
                }
                else
                {
                    Assert.DoesNotContain(name + " (", result.Diagnostic);
                    using var document = JsonDocument.Parse(contents);
                    if (name == "result") Assert.False(document.RootElement.GetProperty("completed").GetBoolean());
                    else Assert.Single(document.RootElement.EnumerateArray());
                }
            }
        }
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(3, false, true)]
    [InlineData(3, true, true)]
    [InlineData(3, false, false)]
    public async Task ProduceAsync_FailsCommandErrorsTruncationOrMissingProof(int failAt, bool truncate, bool writeProof)
    {
        using var root = new TemporaryDirectory("package-checks-failure-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var count = 0;
        var producer = new PackageChecksProducer(Settings(root.FullName, output), async (request, _) =>
        {
            count++;
            if (request.Arguments.Contains("verify-packages", StringComparer.Ordinal) && writeProof)
            {
                var artifactsPath = ValueAfter(request.Arguments, "--artifacts-output");
                Directory.CreateDirectory(artifactsPath);
                var version = ValueAfter(request.Arguments, "--package-version");
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifactsPath, "package-artifact-manifest.json"), JsonSerializer.Serialize(new { package_version = version, entries = new[] { new { package_id = "P" } } }));
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifactsPath, "package-validation-report.md"), "# Package validation\n");
            }
            return new EvidenceCommandResult(count == failAt ? 1 : 0, "", "", 1) { OutputTruncated = truncate && count == failAt };
        });

        var result = await producer.ProduceAsync(Context(output).Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(failAt == 1 ? 1 : failAt == 2 ? 2 : 3, count);
        using var failureProof = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "proof", "result.json")));
        Assert.False(failureProof.RootElement.GetProperty("completed").GetBoolean());
        Assert.Equal(failAt == 1 ? "gate" : failAt == 2 ? "verify" : "verify-packages", failureProof.RootElement.GetProperty("stage").GetString());
        Assert.Equal(count, failureProof.RootElement.GetProperty("commands").GetArrayLength());
        using var transcript = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "proof", "transcript.json")));
        Assert.Equal(count, transcript.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("archive-hash")]
    [InlineData("missing-manifest")]
    public async Task ProduceAsync_RejectsArchiveHashTamperingAndMissingManifest(string defect)
    {
        using var root = new TemporaryDirectory("package-checks-manifest-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var producer = new PackageChecksProducer(Settings(root.FullName, output), async (request, _) =>
        {
            if (request.Arguments.Contains("verify-packages", StringComparer.Ordinal))
            {
                var version = ValueAfter(request.Arguments, "--package-version");
                var artifacts = ValueAfter(request.Arguments, "--artifacts-output");
                Directory.CreateDirectory(artifacts);
                var archive = TestPathUtils.PathUnder(artifacts, "Example." + version + ".nupkg");
                await File.WriteAllBytesAsync(archive, [1, 2, 3]);
                if (defect == "archive-hash")
                    await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json"), JsonSerializer.Serialize(new
                    {
                        package_version = version,
                        entries = new[] { new { package_id = "Example", artifact_file_name = Path.GetFileName(archive), sha512 = new string('0', 128) } },
                    }));
                else
                    await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json"), "");
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-validation-report.md"), $"# Package validation\nVersion: {version}\nAll packages passed.\n");
                if (defect == "missing-manifest") File.Delete(TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json"));
            }
            return new EvidenceCommandResult(0, "", "", 1);
        });

        var result = await producer.ProduceAsync(Context(output).Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("wrong-version")]
    [InlineData("empty-entries")]
    [InlineData("malformed-entry")]
    [InlineData("empty-proof")]
    [InlineData("oversized-proof")]
    [InlineData("invalid-report")]
    [InlineData("missing-package-word")]
    [InlineData("validation-failed")]
    [InlineData("missing-archive")]
    [InlineData("linked-archive")]
    [InlineData("unsafe-name")]
    [InlineData("duplicate-name")]
    [InlineData("extra-archive")]
    public async Task ProduceAsync_RejectsMalformedOrUnboundedPackageProofs(string defect)
    {
        using var root = new TemporaryDirectory("package-checks-invalid-proof-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var producer = new PackageChecksProducer(Settings(root.FullName, output), async (request, _) =>
        {
            if (!request.Arguments.Contains("verify-packages", StringComparer.Ordinal))
                return new EvidenceCommandResult(0, "", "", 1);

            var version = ValueAfter(request.Arguments, "--package-version");
            var artifacts = ValueAfter(request.Arguments, "--artifacts-output");
            Directory.CreateDirectory(artifacts);
            var archiveName = "Example." + version + ".nupkg";
            var archive = TestPathUtils.PathUnder(artifacts, archiveName);
            await File.WriteAllBytesAsync(archive, [1, 2, 3]);
            if (defect == "linked-archive")
            {
                var target = TestPathUtils.PathUnder(root.FullName, "archive-target.bin");
                await File.WriteAllBytesAsync(target, [1, 2, 3]);
                File.Delete(archive);
                File.CreateSymbolicLink(archive, target);
            }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();
            var manifest = JsonSerializer.Serialize(new
            {
                package_version = version,
                entries = new[] { new { package_id = "Example", artifact_file_name = archiveName, sha512 = hash } },
            });
            switch (defect)
            {
                case "invalid-json": manifest = "{"; break;
                case "wrong-version": manifest = manifest.Replace(version, "0.0.0-wrong", StringComparison.Ordinal); break;
                case "empty-entries": manifest = JsonSerializer.Serialize(new { package_version = version, entries = Array.Empty<object>() }); break;
                case "malformed-entry": manifest = JsonSerializer.Serialize(new { package_version = version, entries = new[] { new { package_id = "Example" } } }); break;
                case "empty-proof": manifest = ""; break;
                case "oversized-proof": manifest = new string('x', 2 * 1024 * 1024 + 1); break;
                case "invalid-report": break;
                case "missing-archive": File.Delete(archive); break;
                case "unsafe-name": manifest = JsonSerializer.Serialize(new { package_version = version, entries = new[] { new { package_id = "Example", artifact_file_name = "../escape.nupkg", sha512 = hash } } }); break;
                case "duplicate-name": manifest = JsonSerializer.Serialize(new { package_version = version, entries = new[] { new { package_id = "Example", artifact_file_name = archiveName, sha512 = hash }, new { package_id = "Example2", artifact_file_name = archiveName, sha512 = hash } } }); break;
                case "extra-archive": await File.WriteAllBytesAsync(TestPathUtils.PathUnder(artifacts, "unlisted.nupkg"), [4]); break;
            }
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json"), manifest);
            var report = defect switch
            {
                "invalid-report" => "# Package validation failed",
                "missing-package-word" => $"# Verification\nVersion: {version}\nAll checks passed.\n",
                "validation-failed" => $"# Package validation failed\nVersion: {version}\n",
                _ => $"# Package validation\nVersion: {version}\nAll packages passed.\n",
            };
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "package-validation-report.md"), report);
            return new EvidenceCommandResult(0, "", "", 1);
        });

        var result = await producer.ProduceAsync(Context(output).Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        using var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "proof", "result.json")));
        Assert.False(proof.RootElement.GetProperty("completed").GetBoolean());
        Assert.Equal("verify-packages", proof.RootElement.GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData("missing-repository")]
    [InlineData("missing-dotnet")]
    [InlineData("forbidden-output")]
    [InlineData("stale-output")]
    [InlineData("relative-repository")]
    [InlineData("relative-dotnet")]
    [InlineData("relative-output")]
    public async Task ProduceAsync_RejectsInvalidOrNonFreshSettings(string defect)
    {
        using var root = new TemporaryDirectory("package-checks-settings-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        var valid = Settings(root.FullName, output);
        var settings = defect switch
        {
            "missing-repository" => valid with { RepositoryRoot = TestPathUtils.PathUnder(root.FullName, "absent") },
            "missing-dotnet" => valid with { DotnetPath = TestPathUtils.PathUnder(root.FullName, "absent-dotnet") },
            "forbidden-output" => valid with { OutputDirectory = TestPathUtils.PathUnder(root.FullName, "bin", "evidence") },
            "stale-output" => valid,
            "relative-repository" => valid with { RepositoryRoot = "relative-repository" },
            "relative-dotnet" => valid with { DotnetPath = "dotnet" },
            "relative-output" => valid with { OutputDirectory = "relative-evidence-output" },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        if (defect == "stale-output") Directory.CreateDirectory(TestPathUtils.PathUnder(output, "package-checks"));
        var commandCount = 0;
        var producer = new PackageChecksProducer(settings, (_, _) =>
        {
            commandCount++;
            return Task.FromResult(new EvidenceCommandResult(0, "", "", 1));
        });

        var result = await producer.ProduceAsync(Context(output).Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(0, commandCount);
        using var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, "proof", "result.json")));
        Assert.False(proof.RootElement.GetProperty("completed").GetBoolean());
        Assert.Equal("settings", proof.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task ProduceAsync_AllowsEvidenceOutputAtRepositoryParent()
    {
        using var root = new TemporaryDirectory("package-checks-parent-output-");
        var repository = TestPathUtils.PathUnder(root.FullName, "repository");
        Directory.CreateDirectory(repository);
        var output = root.FullName;
        var commandCount = 0;
        var producer = new PackageChecksProducer(Settings(repository, output), (_, _) =>
        {
            commandCount++;
            return Task.FromResult(new EvidenceCommandResult(1, "", "gate failed", 1));
        });

        var result = await producer.ProduceAsync(Context(output).Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("failed at gate", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, commandCount);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output, "proof", "result.json")));
    }

    [Fact]
    public async Task ProduceAsync_PropagatesCancellation()
    {
        using var root = new TemporaryDirectory("package-checks-cancel-");
        var output = TestPathUtils.PathUnder(root.FullName, "evidence");
        Directory.CreateDirectory(output);
        using var cancellation = new CancellationTokenSource();
        var producer = new PackageChecksProducer(Settings(root.FullName, output), (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<EvidenceCommandResult>(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await producer.ProduceAsync(Context(output).Context, cancellation.Token));
    }

    private static RepositoryCheckSettings Settings(string repository, string output) =>
        new(Path.GetFullPath(repository), ResolveDotnet(), output);

    private static (EvidenceProducerContext Context, EvidenceArtifactWriter Writer) Context(string output)
    {
        var declaration = new EvidenceProducerDeclaration("package-checks", "native-validation", "1", [], Assertions,
        [
            new("result", "proof", "application/json", true, 1024 * 1024),
            new("transcript", "proof", "application/json", true, 1024 * 1024),
            new("package-manifest", "proof", "application/json", true, 2 * 1024 * 1024),
            new("package-report", "proof", "text/markdown", true, 2 * 1024 * 1024),
        ], 1800);
        var writer = new EvidenceArtifactWriter(declaration, output);
        var plan = new EvidencePlan("1", "policy", "digest", "diff", new("profile", EvidenceProfileScope.Targeted, [], [declaration], []), [], [], "input");
        return (new EvidenceProducerContext(plan, declaration, TimeProvider.System, writer), writer);
    }

    private static string ValueAfter(IReadOnlyList<string> args, string option) => args[Array.IndexOf(args.ToArray(), option) + 1];

    private static string ResolveDotnet()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root ? TestPathUtils.PathUnder(root, "dotnet") : null }
            .Where(static path => !string.IsNullOrWhiteSpace(path)).Cast<string>();
        var explicitHost = candidates.FirstOrDefault(File.Exists);
        if (explicitHost is not null) return Path.GetFullPath(explicitHost);
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => string.IsNullOrEmpty(directory) ? executable : TestPathUtils.PathUnder(directory, executable)).First(File.Exists);
    }
}
