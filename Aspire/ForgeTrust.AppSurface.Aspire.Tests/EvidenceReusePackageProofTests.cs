using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

[Collection(EvidenceCoverageFanInProducerCollection.Name)]
public sealed class EvidenceReusePackageProofTests
{
    private const string Version = "0.2.0-proof";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task AdmissionRecordsActualArchiveAndAssemblyHashes()
    {
        using var fixture = new FeedFixture();
        var result = await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit);
        Assert.Equal(7, result.Count);
        foreach (var package in result.Values)
        {
            Assert.Equal(Commit, package.SourceCommit);
            Assert.Equal(Version, package.Version);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(package.Archive))), package.Sha256);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(package.Id))), package.AssemblySha256);
        }
    }

    [Theory]
    [InlineData("flat-dependencies")]
    [InlineData("external-dependency")]
    public async Task AdmissionHandlesUngroupedPrivateEdgesAndIgnoresOrdinaryExternalDependencies(string shape)
    {
        using var fixture = new FeedFixture(shape);

        var packages = await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit);

        Assert.Equal(7, packages.Count);
        Assert.Contains("ForgeTrust.AppSurface.Evidence.Coverage", packages.Keys);
    }

    [Theory]
    [InlineData("different-build-version")]
    [InlineData("root-build-version")]
    public async Task CoherentBundledBuildVersionMayDifferFromPackageVersionWhenCandidateDllHashesAgree(string versionShape)
    {
        using var fixture = new FeedFixture(versionShape);
        var packages = await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit);
        Assert.Equal(7, packages.Count);
    }

    [Theory]
    [InlineData("support-open")]
    [InlineData("aspire-skew")]
    [InlineData("coverage-case")]
    [InlineData("second-framework-open")]
    [InlineData("tool-graph-skew")]
    [InlineData("tool-bundled-sha")]
    [InlineData("second-framework-missing")]
    [InlineData("source-skew")]
    [InlineData("missing-contracts")]
    [InlineData("archive-id")]
    [InlineData("archive-version")]
    public async Task BadCandidateIsRejectedBeforeCreatingOutput(string failure)
    {
        using var fixture = new FeedFixture(failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit));
        var output = Path.Join(fixture.Root, "must-not-exist");
        var result = await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
            Path.Join(fixture.Root, "not-used-dotnet"), Commit, CancellationToken.None);
        Assert.Equal(1, result);
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancelledAdmissionCreatesNoConsumerOutput()
    {
        using var fixture = new FeedFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = Path.Join(fixture.Root, "must-not-exist");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceReusePackageProof.RunAsync(fixture.Root,
            Version, output, Path.Join(fixture.Root, "not-used-dotnet"), Commit, cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task CancellingPackedProofKillsAndJoinsTheActualSdkProbeBeforePropagatingCallerCancellation()
    {
        var feed = new FeedFixture();
        var outputRoot = Directory.CreateTempSubdirectory("packed-cancel-output-");
        var output = TestPathUtils.PathUnder(outputRoot.FullName, "proof-output");
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        SdkCancellationProbeFixture? probe = null;
        Process? process = null;
        Task<int>? run = null;
        var joined = false;
        var processJoined = false;

        try
        {
            probe = new SdkCancellationProbeFixture(feed.Root);
            var activeProbe = probe;
            var activeRun = EvidenceReusePackageProof.RunAsync(feed.Root, Version, output, activeProbe.ExecutablePath,
                Commit, cancellation.Token);
            run = activeRun;
            var pidPath = TestPathUtils.PathUnder(activeProbe.ProbeRoot, "sdk-probe.pid");
            int pid;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (File.Exists(pidPath)
                    && int.TryParse(await File.ReadAllTextAsync(pidPath, deadline.Token),
                        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid))
                    break;
                // gstack-timing-ok: bounded readiness polling for the probe's actual PID, not elapsed-time evidence.
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }

            process = Process.GetProcessById(pid);
            Assert.True(IsProbeProcess(process, activeProbe.ExecutablePath), "The recorded PID must belong to the private renamed probe apphost.");
            Assert.False(activeRun.IsCompleted);

            var activeLogPath = TestPathUtils.PathUnder(output, "sdk.log");
            Assert.True(File.Exists(activeLogPath), "Stage diagnostics must exist before the active SDK process exits.");
            using var activeLog = JsonDocument.Parse(await File.ReadAllBytesAsync(activeLogPath, deadline.Token));
            Assert.Equal("sdk", activeLog.RootElement.GetProperty("stage").GetString());
            Assert.Equal("starting", activeLog.RootElement.GetProperty("state").GetString());

            var globalJsonPath = TestPathUtils.PathUnder(output, "global.json");
            Assert.True(File.Exists(globalJsonPath), "The pinned SDK configuration must exist while the SDK query process is active.");
            using var globalJson = JsonDocument.Parse(await File.ReadAllBytesAsync(globalJsonPath, deadline.Token));
            Assert.Equal("10.0.402", globalJson.RootElement.GetProperty("sdk").GetProperty("version").GetString());
            Assert.Equal("disable", globalJson.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString());
            Assert.False(globalJson.RootElement.GetProperty("sdk").GetProperty("allowPrerelease").GetBoolean());

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeRun.WaitAsync(deadline.Token));
            Assert.True(cancellation.IsCancellationRequested);
            await process.WaitForExitAsync().WaitAsync(deadline.Token);
            Assert.True(process.HasExited);
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "host-app")));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "cli-inputs")));
            Assert.False(File.Exists(TestPathUtils.PathUnder(output, "proof.json")));
        }
        finally
        {
            cancellation.Cancel();
            if (probe is not null)
            {
                await File.WriteAllTextAsync(probe.StopPath, "stop");
                if (process is null) process = TryGetProbeProcess(probe.ProbeRoot, probe.ExecutablePath);
                if (process is null)
                {
                    processJoined = !File.Exists(TestPathUtils.PathUnder(probe.ProbeRoot, "sdk-probe.pid"));
                }
                else if (process.HasExited)
                {
                    processJoined = true;
                }
                else if (IsProbeProcess(process, probe.ExecutablePath))
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        if (!process.HasExited && IsProbeProcess(process, probe.ExecutablePath))
                            process.Kill(entireProcessTree: true);
                        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException) { }
                    }
                    processJoined = process.HasExited;
                }
            }
            else processJoined = true;

            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (run.IsCompleted) { }
                catch (Exception) when (run.IsCompleted) { }
                catch (TimeoutException) { }
            }
            joined = (run?.IsCompleted ?? true) && processJoined;
            process?.Dispose();
            if (joined)
            {
                probe?.Dispose();
                feed.Dispose();
                outputRoot.Delete(recursive: true);
            }
            // If joining fails, retain feed, output, and probe files and keep their environment available for diagnosis.
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/version")]
    [InlineData("bad version")]
    public async Task InvalidVersionCannotAdmitAnOutput(string version)
    {
        using var fixture = new FeedFixture();
        var output = TestPathUtils.PathUnder(fixture.Root, "output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, version, Commit));
        Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, version, output,
            TestPathUtils.PathUnder(fixture.Root, "dotnet"), Commit, CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public async Task InvalidSourcePinCannotAdmitAnOutput(string commit)
    {
        using var fixture = new FeedFixture();
        var output = TestPathUtils.PathUnder(fixture.Root, "output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, commit));
        Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
            TestPathUtils.PathUnder(fixture.Root, "dotnet"), commit, CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("existing-directory")]
    [InlineData("existing-file")]
    [InlineData("inside-feed")]
    [InlineData("missing-sdk")]
    public async Task RejectedOutputOrSdkPreservesCallerOwnedFiles(string defect)
    {
        using var fixture = new FeedFixture();
        var output = TestPathUtils.PathUnder(fixture.Root, "output");
        var sdk = TestPathUtils.PathUnder(fixture.Root, "sdk");
        // Admission checks existence before starting a process; these bytes must never execute.
        if (defect != "missing-sdk") await File.WriteAllTextAsync(sdk, "caller SDK marker");
        if (defect == "existing-directory")
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(output, "caller.txt"), "caller output marker");
        }
        if (defect == "existing-file") await File.WriteAllTextAsync(output, "caller file marker");

        Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output, sdk, Commit, CancellationToken.None));

        if (defect == "existing-directory")
            Assert.Equal("caller output marker", await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "caller.txt")));
        else if (defect == "existing-file") Assert.Equal("caller file marker", await File.ReadAllTextAsync(output));
        else Assert.False(Directory.Exists(output));
        if (defect != "missing-sdk") Assert.Equal("caller SDK marker", await File.ReadAllTextAsync(sdk));
    }

    [Fact]
    public async Task OutputInsideSourceRepositoryIsRejectedBeforeCreatingIt()
    {
        using var fixture = new FeedFixture();
        var repository = FindSourceRepository();
        var output = TestPathUtils.PathUnder(repository, ".gstack", "tmp", "proof-output-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The proof only checks that this SDK path exists before rejecting the unsafe output location.
            Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                typeof(EvidenceReusePackageProofTests).Assembly.Location, Commit, CancellationToken.None));
            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            else if (File.Exists(output)) File.Delete(output);
        }
    }

    [Theory]
    [InlineData("--repository")]
    [InlineData("--policy")]
    [InlineData("--base")]
    public async Task PackedProofOperationRejectsUnrelatedOptionsBeforeCreatingOutput(string unrelatedOption)
    {
        using var fixture = new FeedFixture();
        var output = TestPathUtils.PathUnder(fixture.Root, "output");
        var result = await ForgeTrust.AppSurface.Evidence.Validation.Program.RunAsync(
            ["--operation", "reuse-package-proof", "--feed", fixture.Root, "--version", Version,
                "--source-commit", Commit, "--dotnet", TestPathUtils.PathUnder(fixture.Root, "sdk"),
                "--output", output, unrelatedOption, "caller-owned" ]);

        Assert.Equal(1, result);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("missing-archive")]
    [InlineData("oversized-archive")]
    [InlineData("duplicate-entry")]
    [InlineData("too-many-entries")]
    [InlineData("empty-nuspec")]
    [InlineData("oversized-nuspec")]
    [InlineData("dtd-nuspec")]
    [InlineData("missing-dependencies")]
    [InlineData("duplicate-private-edge")]
    [InlineData("unknown-family-edge")]
    [InlineData("case-family-edge")]
    [InlineData("oversized-tool-deps")]
    [InlineData("empty-tool-build-version")]
    [InlineData("missing-tool-components")]
    [InlineData("case-tool-component")]
    [InlineData("missing-required-tool-component")]
    public async Task MalformedCandidateArchivesCannotCreateConsumerState(string defect)
    {
        using var fixture = new FeedFixture();
        var tool = defect.Contains("tool", StringComparison.Ordinal);
        var id = tool ? "ForgeTrust.AppSurface.Cli" : "ForgeTrust.AppSurface.Evidence.Coverage";
        var archivePath = TestPathUtils.PathUnder(fixture.Root, $"{id}.{Version}.nupkg");
        if (defect == "missing-archive") File.Delete(archivePath);
        else if (defect == "oversized-archive")
        {
            using var file = File.OpenWrite(archivePath);
            file.SetLength(512L * 1024 * 1024 + 1);
        }
        else
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
            if (defect == "duplicate-entry") archive.CreateEntry($"LIB/NET10.0/{id.ToUpperInvariant()}.DLL");
            else if (defect == "too-many-entries")
            {
                for (var index = archive.Entries.Count; index <= 10000; index++) archive.CreateEntry("extra-" + index);
            }
            else if (tool)
            {
                var name = "tools/net10.0/any/ForgeTrust.AppSurface.Cli.deps.json";
                string contents;
                using (var reader = new StreamReader(archive.GetEntry(name)!.Open())) contents = await reader.ReadToEndAsync();
                if (defect == "oversized-tool-deps") contents = new string(' ', 8 * 1024 * 1024 + 1);
                else
                {
                    var document = JsonNode.Parse(contents)!.AsObject();
                    var libraries = document["libraries"]!.AsObject();
                    const string component = "ForgeTrust.AppSurface.Evidence.Planner/" + Version;
                    if (defect == "empty-tool-build-version")
                    {
                        var value = libraries[id + "/" + Version]!.DeepClone();
                        libraries.Remove(id + "/" + Version);
                        libraries[id + "/"] = value;
                    }
                    else if (defect == "missing-tool-components")
                    {
                        foreach (var key in libraries.Select(pair => pair.Key).Where(key => !key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray())
                            libraries.Remove(key);
                    }
                    else if (defect == "case-tool-component")
                    {
                        var value = libraries[component]!.DeepClone();
                        libraries.Remove(component);
                        libraries[component.ToLowerInvariant()] = value;
                    }
                    else libraries.Remove(component);
                    contents = document.ToJsonString();
                }
                RewriteEntry(archive, name, contents);
            }
            else
            {
                var name = id + ".nuspec";
                string contents;
                using (var reader = new StreamReader(archive.GetEntry(name)!.Open())) contents = await reader.ReadToEndAsync();
                if (defect == "empty-nuspec") contents = string.Empty;
                else if (defect == "oversized-nuspec") contents = new string(' ', 1024 * 1024 + 1);
                else if (defect == "dtd-nuspec") contents = "<!DOCTYPE package [<!ENTITY value 'blocked'>]><package>&value;</package>";
                else
                {
                    var document = XDocument.Parse(contents);
                    var dependencies = document.Root!.Element("metadata")!.Element("dependencies")!;
                    var group = dependencies.Element("group")!;
                    if (defect == "missing-dependencies") dependencies.Remove();
                    else if (defect == "duplicate-private-edge") group.Add(new XElement(group.Element("dependency")!));
                    else if (defect == "unknown-family-edge") group.Add(new XElement("dependency",
                        new XAttribute("id", "ForgeTrust.Unknown"), new XAttribute("version", Version)));
                    else group.Add(new XElement("dependency", new XAttribute("id", "forgetrust.appsurface.evidence.planner"),
                        new XAttribute("version", Version)));
                    contents = document.ToString();
                }
                RewriteEntry(archive, name, contents);
            }
        }
        Assert.NotNull(await Record.ExceptionAsync(() => EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit)));
        var output = TestPathUtils.PathUnder(fixture.Root, "output");
        Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
            TestPathUtils.PathUnder(fixture.Root, "unused-sdk"), Commit, CancellationToken.None));
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CandidateArchiveMissingItsRequiredLibraryAssemblyIsRejectedBeforeOutputCreation()
    {
        using var fixture = new FeedFixture();
        const string id = "ForgeTrust.AppSurface.Evidence.Coverage";
        var archivePath = TestPathUtils.PathUnder(fixture.Root, $"{id}.{Version}.nupkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            archive.GetEntry($"lib/net10.0/{id}.dll")!.Delete();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit));
        var output = TestPathUtils.PathUnder(fixture.Root, "missing-library-assembly-output");
        Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
            TestPathUtils.PathUnder(fixture.Root, "unused-sdk"), Commit, CancellationToken.None));
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(output));
        using var admittedArchive = ZipFile.OpenRead(archivePath);
        Assert.DoesNotContain(admittedArchive.Entries, entry => entry.FullName == $"lib/net10.0/{id}.dll");
    }

    private static void RewriteEntry(ZipArchive archive, string name, string contents)
    {
        archive.GetEntry(name)!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(contents);
    }

    private static string FindSourceRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the source repository containing ForgeTrust.AppSurface.slnx.");
    }

    [Fact]
    public async Task AnAdmittedArchiveThatCannotInstallRetainsAFailedStageWithoutStartingConsumers()
    {
        using var fixture = new FeedFixture();
        var scratch = Directory.CreateTempSubdirectory("packed-tool-failure-").FullName;
        var output = TestPathUtils.PathUnder(scratch, "output");
        try
        {
            // These archives have an admitted graph and hashes, but deliberately lack tool-install metadata.
            // An actual SDK failure must become retained failed evidence, never a complete proof.
            var admitted = await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit);
            Assert.Equal(7, admitted.Count);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var result = await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                EvidencePackedReuseJourneyTests.ResolveDotnet(), Commit, deadline.Token);

            Assert.Equal(1, result);
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "proof.json")));
            Assert.False(proof.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal("tool-install", proof.RootElement.GetProperty("stage").GetString());
            Assert.Equal("Native validation contract failed: packed-reuse/stage-tool-install",
                proof.RootElement.GetProperty("contract").GetString());
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, "tool-install.log")));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "host-app")));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "cli-inputs")));
            foreach (var package in admitted.Values)
                Assert.Equal(package.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(package.Archive))));
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [Fact]
    public async Task AdmittedFeedWhoseSdkFileCannotStartRetainsFailureProofWithoutStartingConsumers()
    {
        using var fixture = new FeedFixture();
        var scratch = Directory.CreateTempSubdirectory("packed-process-start-failure-").FullName;
        var output = TestPathUtils.PathUnder(scratch, "output");
        var sdk = TestPathUtils.PathUnder(scratch, "unlaunchable-dotnet");
        var sdkBytes = Encoding.UTF8.GetBytes("not an executable");
        try
        {
            await File.WriteAllBytesAsync(sdk, sdkBytes);
            Assert.True(File.Exists(sdk));
            Assert.Equal(7, (await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit)).Count);

            var result = await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                sdk, Commit, CancellationToken.None);

            Assert.Equal(1, result);
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "proof.json")));
            Assert.False(proof.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal("admission", proof.RootElement.GetProperty("stage").GetString());
            Assert.Equal("Win32Exception", proof.RootElement.GetProperty("failureType").GetString());
            Assert.Empty(proof.RootElement.GetProperty("stages").EnumerateArray());
            Assert.False(File.Exists(TestPathUtils.PathUnder(output, "sdk.log")));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "host-app")));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "cli-inputs")));
            Assert.Equal(sdkBytes, await File.ReadAllBytesAsync(sdk));
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [Fact]
    public async Task FailedSdkProcessRedactsInheritedSecretFromActualLogsAndRetainsFailureProof()
    {
        using var fixture = new FeedFixture();
        var scratch = Directory.CreateTempSubdirectory("packed-sdk-redaction-");
        var output = TestPathUtils.PathUnder(scratch.FullName, "output");
        const string secret = "private-sdk-probe-error-marker";
        using var probe = new SdkCancellationProbeFixture(fixture.Root, "nonzero", secret);
        try
        {
            Assert.Equal(7, (await EvidenceReusePackageProof.InspectFeedAsync(fixture.Root, Version, Commit)).Count);

            var result = await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                probe.ExecutablePath, Commit, CancellationToken.None);

            Assert.Equal(1, result);
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "proof.json")));
            Assert.False(proof.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal("admission", proof.RootElement.GetProperty("stage").GetString());
            Assert.Equal("InvalidOperationException", proof.RootElement.GetProperty("failureType").GetString());

            var sdkLog = await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "sdk.log"));
            Assert.Contains("[redacted]", sdkLog, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, sdkLog, StringComparison.Ordinal);
            var sdkStage = Assert.Single(proof.RootElement.GetProperty("stages").EnumerateArray());
            Assert.Equal("sdk", sdkStage.GetProperty("Stage").GetString());
            Assert.Equal(17, sdkStage.GetProperty("ExitCode").GetInt32());
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "installed-tool")));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "host-app")));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "cli-inputs")));
        }
        finally { Directory.Delete(scratch.FullName, recursive: true); }
    }

    private sealed class FeedFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("evidence-packed-admission-").FullName;
        internal FeedFixture(string? failure = null)
        {
            foreach (var id in EvidenceReusePackageProof.RequiredPackageIds)
            {
                if (failure == "missing-contracts" && id.EndsWith("Evidence.Contracts", StringComparison.Ordinal)) continue;
                var target = id switch
                {
                    "ForgeTrust.AppSurface.Evidence.Coverage" => "ForgeTrust.AppSurface.Evidence.Contracts",
                    "ForgeTrust.AppSurface.Coverage" or "ForgeTrust.AppSurface.Evidence.Aspire" => "ForgeTrust.AppSurface.Evidence.Coverage",
                    _ => null
                };
                XElement Group(bool wrong) => new("group", new XAttribute("targetFramework", wrong ? "net9.0" : "net10.0"), (target is null || (wrong && failure == "second-framework-missing")) ? null :
                    new XElement("dependency", new XAttribute("id", failure == "coverage-case" && id.EndsWith(".Coverage", StringComparison.Ordinal) ? target.ToLowerInvariant() : target),
                        new XAttribute("version", (wrong && failure == "second-framework-open") || (failure == "support-open" && id.EndsWith("Evidence.Coverage", StringComparison.Ordinal))
                            ? $"[{Version},)" : failure == "aspire-skew" && id.EndsWith("Evidence.Aspire", StringComparison.Ordinal) ? "[0.1.0]" : $"[{Version}]")));
                var groupedDependencies = new[] { Group(false), Group(true) };
                if (failure == "external-dependency" && id == "ForgeTrust.AppSurface.Evidence.Coverage")
                    groupedDependencies[0].Add(new XElement("dependency", new XAttribute("id", "Newtonsoft.Json"), new XAttribute("version", "[13.0.1]")));
                var dependencies = failure == "flat-dependencies" && id == "ForgeTrust.AppSurface.Evidence.Coverage"
                    ? new XElement("dependencies", new XElement("dependency", new XAttribute("id", target!), new XAttribute("version", $"[{Version}]")))
                    : new XElement("dependencies", groupedDependencies);
                var document = new XDocument(new XElement("package", new XElement("metadata",
                    new XElement("id", failure == "archive-id" && target is not null ? id.ToLowerInvariant() : id),
                    new XElement("version", failure == "archive-version" && target is not null ? "0.0.0" : Version),
                    new XElement("repository", new XAttribute("commit", failure == "source-skew" && target is not null ? new string('a', 40) : Commit)),
                    dependencies)));
                using var archive = ZipFile.Open(Path.Join(Root, $"{id}.{Version}.nupkg"), ZipArchiveMode.Create);
                using (var writer = new StreamWriter(archive.CreateEntry(id + ".nuspec").Open())) writer.Write(document);
                var dllPath = id.EndsWith(".Cli", StringComparison.Ordinal) ? "tools/net10.0/any/" : "lib/net10.0/";
                using (var writer = new StreamWriter(archive.CreateEntry(dllPath + id + ".dll").Open())) writer.Write(id);
                if (id == "ForgeTrust.AppSurface.Cli")
                {
                    var libraries = new Dictionary<string, object>();
                    foreach (var name in new[] { id, "ForgeTrust.AppSurface.Evidence.Cli", "ForgeTrust.AppSurface.Evidence.Contracts", "ForgeTrust.AppSurface.Evidence.Planner", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Coverage" })
                    {
                        if (name != id)
                        {
                            using var bundledWriter = new StreamWriter(archive.CreateEntry(dllPath + name + ".dll").Open());
                            bundledWriter.Write(failure == "tool-bundled-sha" ? "tampered" : name);
                        }
                        libraries[name + "/" + (failure == "tool-graph-skew" && name.EndsWith("Evidence.Coverage", StringComparison.Ordinal) ? "0.0.0"
                            : failure == "different-build-version" || failure == "root-build-version" && name == id ? "0.1.0" : Version)] = new { type = "project" };
                    }
                    using var writer = new StreamWriter(archive.CreateEntry(dllPath + id + ".deps.json").Open());
                    writer.Write(JsonSerializer.Serialize(new { libraries }));
                }
            }
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class SdkCancellationProbeFixture : IDisposable
    {
        private readonly Dictionary<string, string?> _originalEnvironment;
        private bool _disposed;
        internal string ProbeRoot { get; }
        internal string StopPath { get; }
        internal string ExecutablePath { get; }

        internal SdkCancellationProbeFixture(string root, string mode = "cancel", string? secretEnvironmentValue = null)
        {
            var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
            var source = TestPathUtils.PathUnder(repository, "tests", "ForgeTrust.AppSurface.CoverageLeaseProbe", "bin",
                configuration, "net10.0");
            var appHostName = "ForgeTrust.AppSurface.CoverageLeaseProbe" + (OperatingSystem.IsWindows() ? ".exe" : "");
            Assert.True(File.Exists(TestPathUtils.PathUnder(source, appHostName)));
            var bin = TestPathUtils.PathUnder(root, "sdk-bin");
            Directory.CreateDirectory(bin);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, TestPathUtils.PathUnder(bin, Path.GetFileName(file)));
            ExecutablePath = TestPathUtils.PathUnder(bin, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            File.Copy(TestPathUtils.PathUnder(bin, appHostName), ExecutablePath);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            ProbeRoot = TestPathUtils.PathUnder(root, "sdk-probe");
            Directory.CreateDirectory(ProbeRoot);
            StopPath = TestPathUtils.PathUnder(ProbeRoot, "stop-sdk-probe");
            var values = new Dictionary<string, string?>
            {
                ["APPSURFACE_IDENTITY_SDK_PROBE_ROOT"] = ProbeRoot,
                ["APPSURFACE_IDENTITY_SDK_PROBE_MODE"] = mode
            };
            if (secretEnvironmentValue is not null) values["APPSURFACE_917_SECRET"] = secretEnvironmentValue;
            _originalEnvironment = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            try
            {
                foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (name, value) in _originalEnvironment) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static Process? TryGetProbeProcess(string probeRoot, string executablePath)
    {
        var pidPath = TestPathUtils.PathUnder(probeRoot, "sdk-probe.pid");
        if (!File.Exists(pidPath)
            || !int.TryParse(File.ReadAllText(pidPath), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid))
            return null;
        try
        {
            var process = Process.GetProcessById(pid);
            if (IsProbeProcess(process, executablePath)) return process;
            process.Dispose();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        return null;
    }

    private static bool IsProbeProcess(Process process, string executablePath)
    {
        try
        {
            var actual = process.MainModule?.FileName;
            if (actual is null) return false;
            var normalizedActual = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(actual));
            var normalizedExpected = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(executablePath));
            return string.Equals(normalizedActual, normalizedExpected,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
