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
        var result = await EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit);
        Assert.Equal(7, result.Packages.Count);
        foreach (var package in result.Packages.Values)
        {
            Assert.Equal(Commit, package.SourceCommit);
            Assert.Equal(Version, package.Version);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(package.Archive))), package.Sha256);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(package.Id))), package.AssemblySha256);
        }

        using var tool = ZipFile.OpenRead(result.Packages["ForgeTrust.AppSurface.Cli"].Archive);
        Assert.Equal(
            ["ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Coverage", "ForgeTrust.AppSurface.Evidence.Cli",
                "ForgeTrust.AppSurface.Evidence.Contracts", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Evidence.Planner"],
            result.ToolAssemblyHashes.Keys.Order(StringComparer.Ordinal));
        foreach (var (id, sha256) in result.ToolAssemblyHashes)
        {
            var entry = tool.GetEntry($"tools/net10.0/any/{id}.dll");
            Assert.NotNull(entry);
            using var contents = entry.Open();
            Assert.Equal(Convert.ToHexStringLower(await SHA256.HashDataAsync(contents)), sha256);
        }
    }

    [Fact]
    public async Task SeparateEvidenceCliMustMatchTheToolBundleBeforeOutputAdmission()
    {
        using var fixture = new FeedFixture("separate-cli-skew");
        var output = TestPathUtils.PathUnder(fixture.Root, "proof-output");

        Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
            typeof(EvidenceReusePackageProofTests).Assembly.Location, Commit, CancellationToken.None));

        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("flat-dependencies")]
    [InlineData("external-dependency")]
    public async Task AdmissionHandlesUngroupedPrivateEdgesAndIgnoresOrdinaryExternalDependencies(string shape)
    {
        using var fixture = new FeedFixture(shape);

        var (packages, _) = await EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit);

        Assert.Equal(7, packages.Count);
        Assert.Contains("ForgeTrust.AppSurface.Evidence.Coverage", packages.Keys);
    }

    [Fact]
    public async Task ToolBundleValidationIgnoresUnrelatedForgeTrustLibraries()
    {
        using var fixture = new FeedFixture("unrelated-tool-family");

        var (packages, _) = await EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit);

        Assert.Equal(7, packages.Count);
    }

    [Theory]
    [InlineData("different-build-version")]
    [InlineData("root-build-version")]
    public async Task CoherentBundledBuildVersionMayDifferFromPackageVersionWhenCandidateDllHashesAgree(string versionShape)
    {
        using var fixture = new FeedFixture(versionShape);
        var (packages, _) = await EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit));
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
            var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var repositoryGlobalJson = await File.ReadAllTextAsync(Path.Join(repository, "global.json"), deadline.Token);
            var proofGlobalJson = await File.ReadAllTextAsync(globalJsonPath, deadline.Token);
            Assert.Equal(repositoryGlobalJson, proofGlobalJson);
            using var globalJson = JsonDocument.Parse(proofGlobalJson);
            Assert.Equal("disable", globalJson.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString());
            Assert.False(globalJson.RootElement.GetProperty("sdk").GetProperty("allowPrerelease").GetBoolean());

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeRun.WaitAsync(deadline.Token));
            Assert.False(deadline.IsCancellationRequested, "The proof must cancel before the test deadline.");
            Assert.True(activeRun.IsCanceled);
            Assert.True(cancellation.IsCancellationRequested);
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
            probe?.Dispose();
            if (joined)
            {
                feed.Dispose();
                outputRoot.Delete(recursive: true);
            }
            Assert.True(joined, "The proof task and its owned SDK probe must both be joined before the test exits.");
            // If joining fails, retain feed, output, and probe files for diagnosis.
        }
    }

    [Fact]
    public async Task ReportsPrimarySdkFailureWhenFailureReportCannotBeWritten()
    {
        var feed = new FeedFixture();
        using var probe = new SdkCancellationProbeFixture(feed.Root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var scratch = Directory.CreateTempSubdirectory("packed-proof-report-write-failure-");
        var output = TestPathUtils.PathUnder(scratch.FullName, "output");
        var originalError = global::System.Console.Error;
        using var error = new StringWriter();
        global::System.Console.SetError(error);
        var run = EvidenceReusePackageProof.RunAsync(feed.Root, Version, output,
            probe.ExecutablePath, Commit, deadline.Token);
        Process? process = null;

        try
        {
            while ((process = TryGetProbeProcess(probe.ProbeRoot, probe.ExecutablePath)) is null)
            {
                // gstack-timing-ok: wait for the live probe to publish its PID before replacing the report path.
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }

            Assert.False(run.IsCompleted);
            Assert.True(Directory.Exists(output));
            var reportPath = TestPathUtils.PathUnder(output, "proof.json");
            Directory.CreateDirectory(reportPath);
            await File.WriteAllTextAsync(probe.StopPath, "stop", deadline.Token);

            Assert.Equal(1, await run.WaitAsync(deadline.Token));
            var diagnostics = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Collection(diagnostics,
                original => Assert.StartsWith(
                    "ASEVD917 Packed reuse proof failed at admission: Native validation contract failed: packed-reuse/pinned-sdk-",
                    original, StringComparison.Ordinal),
                secondary => Assert.Equal("ASEVD917 Could not write the packed reuse failure report 'proof.json'.", secondary));
            Assert.True(process.HasExited);
            Assert.Empty(Directory.EnumerateFileSystemEntries(reportPath));
        }
        finally
        {
            global::System.Console.SetError(originalError);
            try
            {
                await deadline.CancelAsync();
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (run.IsCompleted) { }
                if (process is null || process.HasExited)
                {
                    feed.Dispose();
                    scratch.Delete(recursive: true);
                }
            }
            finally { process?.Dispose(); }
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, version, Commit));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, commit));
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
        using var scratch = new ProofScratch();
        var output = TestPathUtils.PathUnder(defect == "inside-feed" ? fixture.Root : scratch.Root, "output");
        var sdk = TestPathUtils.PathUnder(scratch.Root, "sdk");
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
    [InlineData("repository")]
    [InlineData("feed")]
    public async Task OutputThroughAnExternalSymlinkIntoProtectedRootsIsRejected(string targetKind)
    {
        using var fixture = new FeedFixture();
        var externalRoot = Directory.CreateTempSubdirectory("packed-output-link-");
        var target = targetKind == "repository" ? FindSourceRepository() : fixture.Root;
        var link = TestPathUtils.PathUnder(externalRoot.FullName, "protected");
        Directory.CreateSymbolicLink(link, target);
        var output = TestPathUtils.PathUnder(link, "proof-output");
        try
        {
            Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                typeof(EvidenceReusePackageProofTests).Assembly.Location, Commit, CancellationToken.None));
            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(output));
        }
        finally { externalRoot.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExistingExclusiveOutputClaimPreventsASecondInvocationFromWriting()
    {
        using var fixture = new FeedFixture();
        var scratch = Directory.CreateTempSubdirectory("packed-output-claim-");
        var output = TestPathUtils.PathUnder(scratch.FullName, "proof-output");
        var claimPath = output + ".packed-reuse.claim";
        const string marker = "another invocation owns this output";
        Directory.CreateDirectory(scratch.FullName);
        await File.WriteAllTextAsync(claimPath, marker);
        try
        {
            Assert.Equal(1, await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                typeof(EvidenceReusePackageProofTests).Assembly.Location, Commit, CancellationToken.None));

            Assert.False(Directory.Exists(output));
            Assert.Equal(marker, await File.ReadAllTextAsync(claimPath));
        }
        finally { scratch.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("--repository")]
    [InlineData("--policy")]
    [InlineData("--base")]
    public async Task PackedProofOperationRejectsUnrelatedOptionsBeforeCreatingOutput(string unrelatedOption)
    {
        using var fixture = new FeedFixture();
        using var scratch = new ProofScratch();
        var output = TestPathUtils.PathUnder(scratch.Root, "output");
        var result = await ForgeTrust.AppSurface.Evidence.Validation.Program.RunAsync(
            ["--operation", "reuse-package-proof", "--feed", fixture.Root, "--version", Version,
                "--source-commit", Commit, "--dotnet", typeof(EvidenceReusePackageProofTests).Assembly.Location,
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
    [InlineData("malformed-tool-component-key")]
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
                    else if (defect == "malformed-tool-component-key")
                    {
                        libraries["ForgeTrust.AppSurface.Evidence.Readiness/extra/" + Version] = libraries[component]!.DeepClone();
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
        Assert.NotNull(await Record.ExceptionAsync(() => EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit)));
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
            EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit));
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

    private static void ReplaceStageLogWithEmptyDirectory(string logPath)
    {
        File.Delete(logPath);
        Directory.CreateDirectory(logPath);
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
            var (admitted, _) = await EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit);
            Assert.Equal(7, admitted.Count);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var result = await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                TestDotnetHost.ResolveExisting(), Commit, deadline.Token);

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
            Assert.Equal(7, (await EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit)).Packages.Count);

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
            Assert.Equal(7, (await EvidenceReusePackageAdmission.InspectFeedAsync(fixture.Root, Version, Commit)).Packages.Count);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PackedProofWaitsForHeldOutputPipesAfterRootExit(bool recordProgress)
    {
        using var fixture = ProofScratch.ForProbe();
        using var probe = new SdkCancellationProbeFixture(fixture.Root, "root-exits-with-pipe-child");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var journal = TestPathUtils.PathUnder(fixture.Root, "packed-progress.jsonl");
        using var progress = recordProgress ? PackedProofProgress.TryCreate(journal) : null;
        if (recordProgress) Assert.NotNull(progress);
        const string childPidFile = "sdk-probe.child-pid";
        var childPidPath = TestPathUtils.PathUnder(probe.ProbeRoot, childPidFile);
        var start = PackedProofProcess.CreateStartInfo(probe.ExecutablePath, [], fixture.Root);
        var run = PackedProofProcess.RunAsync("cli-audit", start, fixture.Root, CancellationToken.None, progress: progress);
        Process? child = null;
        try
        {
            while (!File.Exists(childPidPath))
            {
                // gstack-timing-ok: wait for the owned descendant to publish its PID independently of journal readiness.
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }

            if (recordProgress)
            {
                child = TryGetProbeProcess(probe.ProbeRoot, probe.ExecutablePath, childPidFile);
                Assert.NotNull(child);
                ProgressRecord[] entries;
                while (!(entries = await ReadProgressAsync(journal, deadline.Token)).Any(entry => entry.Boundary == "process-exited"))
                {
                    // gstack-timing-ok: wait for the root exit observation while the owned child still holds the pipes.
                    await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
                }

                Assert.False(child.HasExited);
                Assert.Equal(["process-started", "process-exited"], entries.Select(entry => entry.Boundary));
                Assert.Equal(0, entries[1].ExitCode);
            }
            else
            {
                var rootPidPath = TestPathUtils.PathUnder(probe.ProbeRoot, "sdk-probe.root-pid");
                while (!File.Exists(rootPidPath))
                {
                    // gstack-timing-ok: wait for the root's published PID before observing its exit.
                    await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
                }

                var rootPid = int.Parse(await File.ReadAllTextAsync(rootPidPath, deadline.Token),
                    System.Globalization.CultureInfo.InvariantCulture);
                Process? root;
                try { root = Process.GetProcessById(rootPid); }
                catch (ArgumentException) { root = null; } // The published root PID has already exited.
                using (root)
                {
                    if (root is { HasExited: false })
                    {
                        Assert.True(IsProbeProcess(root, probe.ExecutablePath) || root.HasExited,
                            "The published root PID must identify the private probe.");
                        await root.WaitForExitAsync(deadline.Token);
                    }
                }

                using var pipeHolder = TryGetProbeProcess(probe.ProbeRoot, probe.ExecutablePath, childPidFile);
                Assert.NotNull(pipeHolder);
                Assert.False(pipeHolder.HasExited);
            }

            Assert.False(run.IsCompleted, "The runner must still be waiting for the inherited output pipes.");
        }
        finally
        {
            // The no-journal case reaches cleanup without an open handle, like a failed readiness assertion.
            child ??= TryGetProbeProcess(probe.ProbeRoot, probe.ExecutablePath, childPidFile);
            using (child)
            {
                await File.WriteAllTextAsync(probe.StopPath, "stop");
                await fixture.WaitForSettlementAsync(run, child?.WaitForExitAsync(), TimeSpan.FromSeconds(15));
            }
        }

        Assert.False(fixture.Retain, "The proof task and its owned descendant must both be joined.");
        Assert.Equal(0, (await run).ExitCode);
        if (recordProgress)
        {
            Assert.Equal(new[] { "process-exited", "process-started", "stderr-drained", "stdout-drained" },
                (await ReadProgressAsync(journal, deadline.Token)).Select(entry => entry.Boundary).Order(StringComparer.Ordinal));
        }
        else Assert.False(File.Exists(journal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProgressRecordsProcessAndStreamOutcomesDuringCancellationCleanup(bool rejectTreeKill, bool rejectRootKill)
    {
        using var fixture = ProofScratch.ForProbe();
        using var probe = new SdkCancellationProbeFixture(fixture.Root);
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var journal = TestPathUtils.PathUnder(fixture.Root, "packed-progress.jsonl");
        using var progress = PackedProofProgress.TryCreate(journal);
        Assert.NotNull(progress);
        var rootKillAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var treeKillFailure = new AggregateException("Simulated process-tree termination failure.");
        var rootKillFailure = new NotSupportedException("Simulated root termination failure.");
        var start = PackedProofProcess.CreateStartInfo(probe.ExecutablePath, [], fixture.Root);
        var run = PackedProofProcess.RunAsync("cli-audit", start, fixture.Root, caller.Token, progress: progress,
            terminateProcess: rejectTreeKill ? (process, entireProcessTree) =>
            {
                if (entireProcessTree) throw treeKillFailure;
                rootKillAttempt.SetResult();
                if (rejectRootKill) throw rootKillFailure;
                process.Kill();
            }
        : null);
        Process? ownedProcess = null;
        try
        {
            var pidPath = TestPathUtils.PathUnder(probe.ProbeRoot, "sdk-probe.pid");
            while (!File.Exists(pidPath))
            {
                // gstack-timing-ok: wait for the probe to publish its PID, not an elapsed-time assertion.
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            ownedProcess = TryGetProbeProcess(probe.ProbeRoot, probe.ExecutablePath);
            Assert.NotNull(ownedProcess);
            caller.Cancel();
            if (rejectRootKill)
            {
                await rootKillAttempt.Task.WaitAsync(deadline.Token);
                Assert.False(run.IsCompleted);
                await File.WriteAllTextAsync(probe.StopPath, "stop", deadline.Token);
                var failure = await Assert.ThrowsAsync<AggregateException>(() => run.WaitAsync(deadline.Token));
                Assert.Collection(failure.InnerExceptions,
                    exception => Assert.Same(treeKillFailure, exception),
                    exception => Assert.Same(rootKillFailure, exception));
            }
            else if (rejectTreeKill)
            {
                await rootKillAttempt.Task.WaitAsync(deadline.Token);
                Assert.Same(treeKillFailure, await Assert.ThrowsAsync<AggregateException>(() => run.WaitAsync(deadline.Token)));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(deadline.Token));
                Assert.True(run.IsCanceled);
            }
            Assert.True(ownedProcess.HasExited);

            var entries = await ReadProgressAsync(journal, deadline.Token);
            var boundaries = entries.Select(entry => entry.Boundary).ToArray();
            Assert.Contains("wait-interrupted", boundaries);
            Assert.Equal(rejectTreeKill, boundaries.Contains("cleanup-kill-failed"));
            Assert.Equal(rejectRootKill, boundaries.Contains("cleanup-root-kill-failed"));
            Assert.NotNull(Assert.Single(entries, entry => entry.Boundary == "cleanup-process-exited").ExitCode);
            Assert.Single(boundaries, boundary => boundary is "cleanup-stdout-drained" or "cleanup-stdout-cancelled" or "cleanup-stdout-faulted");
            Assert.Single(boundaries, boundary => boundary is "cleanup-stderr-drained" or "cleanup-stderr-cancelled" or "cleanup-stderr-faulted");
            Assert.Equal("interruption-cleanup-completed", boundaries[^1]);
        }
        finally
        {
            ownedProcess ??= TryGetProbeProcess(probe.ProbeRoot, probe.ExecutablePath);
            using (ownedProcess)
            {
                caller.Cancel();
                await File.WriteAllTextAsync(probe.StopPath, "stop");
                await fixture.WaitForSettlementAsync(run, ownedProcess?.WaitForExitAsync(), TimeSpan.FromSeconds(10));
            }
        }

        Assert.False(fixture.Retain, "The proof task and its owned SDK probe must both be joined.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeCleanupDeadlineRetainsJournalAndStopMarker(bool proofRunPending)
    {
        var fixture = ProofScratch.ForProbe();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = proofRunPending ? pending.Task : Task.CompletedTask;
        var processExit = proofRunPending ? Task.CompletedTask : pending.Task;
        try
        {
            var journal = TestPathUtils.PathUnder(fixture.Root, "packed-progress.jsonl");
            var stop = TestPathUtils.PathUnder(fixture.Root, "stop-sdk-probe");
            await File.WriteAllTextAsync(journal, "progress");
            await File.WriteAllTextAsync(stop, "stop");
            await Assert.ThrowsAsync<TimeoutException>(() =>
                fixture.WaitForSettlementAsync(run, processExit, TimeSpan.Zero));

            fixture.Dispose();
            Assert.Equal("progress", await File.ReadAllTextAsync(journal));
            Assert.Equal("stop", await File.ReadAllTextAsync(stop));
        }
        finally
        {
            pending.TrySetResult();
            await fixture.WaitForSettlementAsync(run, processExit, TimeSpan.Zero);
            fixture.Dispose();
        }
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task ProbeCleanupRetainsScratchWhenBothTasksSettleAfterTheDeadline()
    {
        var fixture = ProofScratch.ForProbe();
        var run = new TaskCompletionSource();
        var exit = new TaskCompletionSource();
        var journal = TestPathUtils.PathUnder(fixture.Root, "packed-progress.jsonl");
        try
        {
            await File.WriteAllTextAsync(journal, "progress");
            await Assert.ThrowsAsync<TimeoutException>(() =>
                fixture.WaitForSettlementAsync(run.Task, exit.Task, TimeSpan.Zero, wait: async completion =>
                {
                    try { await completion.WaitAsync(TimeSpan.Zero); }
                    finally
                    {
                        run.TrySetResult();
                        exit.TrySetResult();
                        await completion;
                    }
                }));

            fixture.Dispose();
            Assert.Equal("progress", await File.ReadAllTextAsync(journal));
        }
        finally
        {
            run.TrySetResult();
            exit.TrySetResult();
            await fixture.WaitForSettlementAsync(run.Task, exit.Task, TimeSpan.Zero);
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ProgressRecordAfterStreamClosurePreservesExistingEntries()
    {
        using var fixture = new ProofScratch();
        var journal = TestPathUtils.PathUnder(fixture.Root, "packed-progress.jsonl");
        using var stream = File.Create(journal);
        using var progress = new PackedProofProgress(stream);
        progress.Record("cli-audit", "process-started");

        stream.Dispose();
        progress.Record("cli-audit", "process-exited", 0);

        var entry = Assert.Single(await ReadProgressAsync(journal, CancellationToken.None));
        Assert.Equal("process-started", entry.Boundary);
    }

    private static async Task<ProgressRecord[]> ReadProgressAsync(string journal, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(cancellationToken);
        var complete = text[..(text.LastIndexOf('\n') + 1)];
        return complete.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var row = JsonDocument.Parse(line);
            Assert.Equal("cli-audit", row.RootElement.GetProperty("stage").GetString());
            var boundary = row.RootElement.GetProperty("boundary").GetString()!;
            var exitCode = row.RootElement.GetProperty("exitCode");
            return new ProgressRecord(boundary, exitCode.ValueKind == JsonValueKind.Null ? null : exitCode.GetInt32());
        }).ToArray();
    }

    private sealed record ProgressRecord(string Boundary, int? ExitCode);

    [Fact]
    public async Task NonzeroProbeExitIsReturnedWhenFinalStageLogCannotBeWritten()
    {
        using var fixture = new FeedFixture();
        using var probe = new SdkCancellationProbeFixture(fixture.Root, "nonzero");
        var logPath = TestPathUtils.PathUnder(fixture.Root, "sdk.log");
        var start = PackedProofProcess.CreateStartInfo(probe.ExecutablePath, [], fixture.Root);

        var result = await PackedProofProcess.RunAsync("sdk", start, fixture.Root, CancellationToken.None,
            stageTimeout: TimeSpan.FromSeconds(30), recordExit: _ => ReplaceStageLogWithEmptyDirectory(logPath));

        Assert.Equal(17, result.ExitCode);
        Assert.Null(result.LogPath);
        Assert.True(Directory.Exists(logPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(logPath));
    }

    [Fact]
    public async Task ExcessiveProbeOutputPreservesItsFailureWhenFinalStageLogCannotBeWritten()
    {
        using var fixture = new FeedFixture();
        using var probe = new SdkCancellationProbeFixture(fixture.Root, "excessive-stdout");
        var logPath = TestPathUtils.PathUnder(fixture.Root, "sdk.log");
        var start = PackedProofProcess.CreateStartInfo(probe.ExecutablePath, [], fixture.Root);

        await Assert.ThrowsAsync<InvalidDataException>(() => PackedProofProcess.RunAsync("sdk", start,
            fixture.Root, CancellationToken.None, stageTimeout: TimeSpan.FromSeconds(30),
            recordExit: _ => ReplaceStageLogWithEmptyDirectory(logPath)));

        Assert.True(Directory.Exists(logPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(logPath));
    }

    [Theory]
    [InlineData("excessive-stdout")]
    [InlineData("excessive-stderr")]
    public async Task ExcessiveSdkOutputRetainsBoundedFailureProofWithoutStartingConsumers(string mode)
    {
        using var fixture = new FeedFixture();
        using var probe = new SdkCancellationProbeFixture(fixture.Root, mode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var scratch = Directory.CreateTempSubdirectory("packed-sdk-output-limit-");
        var output = TestPathUtils.PathUnder(scratch.FullName, "new-parent", "output");
        try
        {
            var result = await EvidenceReusePackageProof.RunAsync(fixture.Root, Version, output,
                probe.ExecutablePath, Commit, deadline.Token);

            Assert.Equal(1, result);
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(
                TestPathUtils.PathUnder(output, "proof.json"), deadline.Token));
            Assert.False(proof.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal("InvalidDataException", proof.RootElement.GetProperty("failureType").GetString());
            Assert.Equal("sdk", Assert.Single(proof.RootElement.GetProperty("stages").EnumerateArray())
                .GetProperty("Stage").GetString());
            var sdkLog = new FileInfo(TestPathUtils.PathUnder(output, "sdk.log"));
            Assert.InRange(sdkLog.Length, 1, 4 * 1024 * 1024);
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(output, "installed-tool")));
        }
        finally { Directory.Delete(scratch.FullName, recursive: true); }
    }

    private sealed class FeedFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("evidence-packed-admission-").FullName;
        internal FeedFixture(string? failure = null)
        {
            foreach (var id in EvidenceReusePackageAdmission.CandidateMinimumPackageIds)
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
                    if (failure == "unrelated-tool-family")
                        libraries["ForgeTrust.AppSurface.UnrelatedProduct/1.2.3"] = new { type = "project" };
                    using var writer = new StreamWriter(archive.CreateEntry(dllPath + id + ".deps.json").Open());
                    writer.Write(JsonSerializer.Serialize(new { libraries }));
                }
            }
            if (failure == "separate-cli-skew")
            {
                const string id = "ForgeTrust.AppSurface.Evidence.Cli";
                var document = new XDocument(new XElement("package", new XElement("metadata",
                    new XElement("id", id), new XElement("version", Version),
                    new XElement("repository", new XAttribute("commit", Commit)),
                    new XElement("dependencies", new XElement("group", new XAttribute("targetFramework", "net10.0"),
                        new XElement("dependency", new XAttribute("id", "ForgeTrust.AppSurface.Evidence.Coverage"),
                            new XAttribute("version", $"[{Version}]")))))));
                using var archive = ZipFile.Open(Path.Join(Root, $"{id}.{Version}.nupkg"), ZipArchiveMode.Create);
                using (var writer = new StreamWriter(archive.CreateEntry(id + ".nuspec").Open())) writer.Write(document);
                using (var writer = new StreamWriter(archive.CreateEntry($"lib/net10.0/{id}.dll").Open())) writer.Write("separate-cli");
            }
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ProofScratch : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("evidence-proof-output-").FullName;

        internal bool Retain { get; private set; }

        /// <summary>Creates scratch that retains diagnostics until the probe settles.</summary>
        internal static ProofScratch ForProbe() => new() { Retain = true };

        /// <summary>Allows deletion after the proof task settles and the observed owned process exits.</summary>
        /// <param name="run">The proof task whose outcome the test checks separately.</param>
        /// <param name="ownedProcessExit">The owned process exit task, if its identity was confirmed.</param>
        /// <param name="timeout">The cleanup wait allowance.</param>
        /// <param name="wait">Optional test seam for ordering deadline expiry and task settlement.</param>
        /// <remarks>A missing process observation or expired cleanup deadline retains the scratch files.</remarks>
        internal async Task WaitForSettlementAsync(
            Task run, Task? ownedProcessExit, TimeSpan timeout, Func<Task, Task>? wait = null)
        {
            Retain = true;
            var exit = ownedProcessExit ?? Task.CompletedTask;
            var settlement = Task.WhenAll(run, exit);
            var completion = ObserveSettlementAsync(settlement);
            await (wait?.Invoke(completion) ?? completion.WaitAsync(timeout));

            if (!exit.IsCompletedSuccessfully)
                await settlement;

            Retain = ownedProcessExit is null;

            // Keep task outcomes separate from the deadline and observe faults even after it expires.
            static async Task ObserveSettlementAsync(Task task)
                => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        public void Dispose()
        {
            if (Retain)
                global::System.Console.Error.WriteLine($"Retained probe diagnostics in {Root} because settlement could not be confirmed.");
            else NativeEvidenceTestDirectory.Delete(Root);
        }
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
            var bin = TestPathUtils.PathUnder(root, "sdk-bin");
            ExecutablePath = CoverageLeaseProbeStager.Stage(bin, "dotnet");

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

    private static Process? TryGetProbeProcess(
        string probeRoot, string executablePath, string pidFileName = "sdk-probe.pid")
    {
        var pidPath = TestPathUtils.PathUnder(probeRoot, pidFileName);
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
