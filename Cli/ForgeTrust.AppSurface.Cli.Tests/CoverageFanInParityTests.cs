using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInParityTests
{
    [Fact]
    public async Task PortableThreePlatformCapturesImportComposeAcceptAndVerifyAgainstExactCurrentPolicy()
    {
        using var fixture = await Fixture.CreateAsync();
        var primary = await fixture.CapturePrimaryAsync();
        var native = await fixture.ImportThreePlatformCapturesAsync(fixture.Policy);

        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose(native))).Outcome);
        var accepted = await CoverageFanIn.AcceptAsync(fixture.Accept(native));
        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Verified);
        Assert.Equal(CoverageObservation.TrustedProducer, accepted.Observation);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.VerifyAsync(fixture.Verify(native))).Outcome);

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Join(primary, "manifest.json")));
        Assert.Equal(CoveragePolicyEncoding.Digest(fixture.Policy),
            manifest.RootElement.GetProperty("policyDigest").GetString());
        Assert.Equal(fixture.Policy.Source.Sdk, manifest.RootElement.GetProperty("source").GetProperty("sdk").GetString());
        using var timings = JsonDocument.Parse(File.ReadAllBytes(Path.Join(primary, "timings.json")));
        Assert.Equal(fixture.Policy.TestProjects.Order(StringComparer.Ordinal),
            timings.RootElement.GetProperty("projects").EnumerateArray()
                .Select(project => project.GetProperty("project").GetString()).Order(StringComparer.Ordinal));
        foreach (var path in native)
        {
            using var imported = JsonDocument.Parse(File.ReadAllBytes(Path.Join(path, "manifest.json")));
            Assert.Equal(2, imported.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("TrustedProducer", imported.RootElement.GetProperty("observation").GetString());
        }
    }

    [Fact]
    public async Task CompositionRejectsEachOmittedPlatformAndCapturesFromMixedAttempts()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CapturePrimaryAsync();
        var native = await fixture.ImportThreePlatformCapturesAsync(fixture.Policy);

        for (var omitted = 0; omitted < native.Length; omitted++)
        {
            var output = Path.Join(fixture.Temp.Path, "omitted-" + omitted);
            var remaining = native.Where((_, index) => index != omitted).ToArray();
            var result = await CoverageFanIn.ComposeAsync(fixture.Compose(remaining, output));
            Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
            Assert.False(Directory.Exists(output));
        }

        var mixedPolicy = fixture.Policy with { Run = fixture.Policy.Run with { Attempt = 2 } };
        var mixedSupplement = fixture.Policy.Supplements[1];
        var mixedWire = fixture.WritePortableCapture(mixedPolicy, mixedSupplement);
        var mixedPath = await fixture.ImportAsync(mixedPolicy, mixedSupplement, mixedWire, "mixed-attempt");
        var mixedSet = native.ToArray();
        mixedSet[1] = mixedPath;
        var mixedOutput = Path.Join(fixture.Temp.Path, "mixed-composition");
        var mixed = await CoverageFanIn.ComposeAsync(fixture.Compose(mixedSet, mixedOutput));
        Assert.Equal(CoverageOperationOutcome.Rejected, mixed.Outcome);
        Assert.False(Directory.Exists(mixedOutput));
    }

    [Fact]
    public async Task ImportRejectsChangedPersistedCoverageAndPreservesReceivingGeneration()
    {
        using var fixture = await Fixture.CreateAsync();
        var supplement = fixture.Policy.Supplements[0];
        var wire = fixture.WritePortableCapture(fixture.Policy, supplement);
        var persisted = await fixture.ImportAsync(fixture.Policy, supplement, wire, "persisted-native");
        var receiving = await fixture.ImportAsync(fixture.Policy, supplement, wire, "receiving-native");
        var persistedCoverage = TestPathUtils.PathUnder(persisted, "coverage.cobertura.xml");
        File.WriteAllText(persistedCoverage, File.ReadAllText(persistedCoverage)
            .Replace("hits=\"1\"", "hits=\"0\"", StringComparison.Ordinal));
        var before = Snapshot(receiving);

        var result = await CoverageFanIn.CaptureAsync(new(fixture.Policy, fixture.Repository, receiving, supplement.Id,
            ImportDirectory: persisted, OriginalSourceRoot: Path.GetFullPath(fixture.Repository),
            OutputMode: CoverageOutputMode.ReplaceOwned));

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(before, Snapshot(receiving));
        Assert.Contains("hits=\"0\"", File.ReadAllText(persistedCoverage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAndCompositionRejectTamperedRawCollectorBytes()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CapturePrimaryAsync();
        var supplement = fixture.Policy.Supplements[0];
        var tamperedWire = fixture.WritePortableCapture(fixture.Policy, supplement);
        File.AppendAllText(TestPathUtils.PathUnder(tamperedWire, "collector.cobertura.xml"), "<!-- tampered -->");
        var importOutput = TestPathUtils.PathUnder(fixture.Temp.Path, "tampered-raw-import");
        var imported = await CoverageFanIn.CaptureAsync(new(fixture.Policy, fixture.Repository, importOutput, supplement.Id,
            ImportDirectory: tamperedWire, OriginalSourceRoot: Path.GetFullPath(fixture.Repository)));
        Assert.Equal(CoverageOperationOutcome.Rejected, imported.Outcome);
        Assert.False(Directory.Exists(importOutput));

        var native = await fixture.ImportThreePlatformCapturesAsync(fixture.Policy);
        var persistedRaw = TestPathUtils.PathUnder(native[0], "collector.cobertura.xml");
        File.AppendAllText(persistedRaw, "<!-- tampered -->");
        var compositionOutput = TestPathUtils.PathUnder(fixture.Temp.Path, "tampered-raw-composition");
        var composed = await CoverageFanIn.ComposeAsync(fixture.Compose(native, compositionOutput));
        Assert.Equal(CoverageOperationOutcome.Rejected, composed.Outcome);
        Assert.False(Directory.Exists(compositionOutput));
        Assert.Contains("<!-- tampered -->", File.ReadAllText(persistedRaw), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeCaptureRejectsMissingRawCollectorAndEmptyReportBeforeOutputCreation()
    {
        using var fixture = await Fixture.CreateAsync();
        var platform = OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : "Linux";
        var supplement = fixture.Policy.Supplements.Single(x => x.Platform == platform);
        var missingRawOutput = Path.Join(fixture.Temp.Path, "missing-raw");
        var missingRaw = await CoverageFanIn.CaptureAsync(new(fixture.Policy, fixture.Repository,
            missingRawOutput, supplement.Id, CoveragePath: fixture.Report));
        Assert.Equal(CoverageOperationOutcome.Rejected, missingRaw.Outcome);
        Assert.False(Directory.Exists(missingRawOutput));

        var emptyReport = fixture.Temp.WriteFile("producer/empty.xml", "<coverage />");
        var emptyOutput = Path.Join(fixture.Temp.Path, "empty-report");
        var empty = await CoverageFanIn.CaptureAsync(new(fixture.Policy, fixture.Repository,
            emptyOutput, supplement.Id, CoveragePath: emptyReport, RawCoveragePath: fixture.Report));
        Assert.Equal(CoverageOperationOutcome.Rejected, empty.Outcome);
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public async Task FreshCaptureCollisionPreservesUnknownDestinationBytes()
    {
        using var fixture = await Fixture.CreateAsync();
        var output = fixture.Temp.CreateDirectory("occupied");
        var sentinel = Path.Join(output, "keep.txt");
        File.WriteAllText(sentinel, "owned by caller");

        var result = await CoverageFanIn.CaptureAsync(fixture.PrimaryRequest(output));

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.Equal("owned by caller", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFiles(output));
    }

    [Fact]
    public async Task OwnedReplacementRefusesProducerInputOverlapAndPreservesPriorGeneration()
    {
        using var fixture = await Fixture.CreateAsync();
        var output = await fixture.CapturePrimaryAsync();
        var before = Snapshot(output);
        var overlapping = fixture.PrimaryRequest(output) with
        {
            CoveragePath = Path.Join(output, "coverage.cobertura.xml"),
            TimingsPath = Path.Join(output, "timings.json"),
            OutputMode = CoverageOutputMode.ReplaceOwned
        };

        var result = await CoverageFanIn.CaptureAsync(overlapping);

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(before, Snapshot(output));
    }

    [Fact]
    public async Task OwnedReplacementRejectsLinkedManagedFileWithoutChangingOutsideData()
    {
        using var fixture = await Fixture.CreateAsync();
        var output = await fixture.CapturePrimaryAsync();
        var outside = fixture.Temp.CreateDirectory("outside-owned-replacement");
        var sentinel = TestPathUtils.PathUnder(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "outside bytes stay intact");
        var coverage = TestPathUtils.PathUnder(output, "coverage.cobertura.xml");
        File.Delete(coverage);
        TestFileSystem.CreateFileLinkOrSkip(coverage, sentinel);
        var beforeOutput = Snapshot(output);
        var beforeOutside = Snapshot(outside);

        var result = await CoverageFanIn.CaptureAsync(fixture.PrimaryRequest(output) with
        { OutputMode = CoverageOutputMode.ReplaceOwned });

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(beforeOutput, Snapshot(output));
        Assert.Equal(beforeOutside, Snapshot(outside));
        Assert.Equal("outside bytes stay intact", File.ReadAllText(sentinel));
        Assert.NotNull(new FileInfo(coverage).LinkTarget);
    }

    [Fact]
    public async Task LinkedOutputAncestorIsRejectedAndOutsideSentinelIsPreserved()
    {
        using var fixture = await Fixture.CreateAsync();
        var outside = fixture.Temp.CreateDirectory("outside");
        var sentinel = Path.Join(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve outside data");
        var link = Path.Join(fixture.Temp.Path, "linked-parent");
        TestFileSystem.CreateDirectoryLinkOrSkip(link, outside);
        var output = Path.Join(link, "capture");

        var result = await CoverageFanIn.CaptureAsync(fixture.PrimaryRequest(output));

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(Directory.Exists(Path.Join(outside, "capture")));
        Assert.Equal("preserve outside data", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task CompositionRejectsLinkedOutputParentAndPreservesOutsideSentinel()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CapturePrimaryAsync();
        var native = await fixture.ImportThreePlatformCapturesAsync(fixture.Policy);
        var outside = fixture.Temp.CreateDirectory("outside-composition");
        var sentinel = TestPathUtils.PathUnder(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve composition neighbor");
        var before = Snapshot(outside);
        var link = TestPathUtils.PathUnder(fixture.Temp.Path, "linked-composition-parent");
        TestFileSystem.CreateDirectoryLinkOrSkip(link, outside);
        var output = TestPathUtils.PathUnder(link, "composition");

        var result = await CoverageFanIn.ComposeAsync(fixture.Compose(native, output));

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(Directory.Exists(output));
        Assert.Equal(before, Snapshot(outside));
        Assert.Equal("preserve composition neighbor", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompositionReplacementPreservesModifiedOrUnknownOwnedContents(bool addUnknown)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CapturePrimaryAsync();
        var native = await fixture.ImportThreePlatformCapturesAsync(fixture.Policy);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose(native))).Outcome);
        if (addUnknown)
            File.WriteAllText(TestPathUtils.PathUnder(fixture.Composed, "caller-owned.txt"), "preserve these bytes");
        else
            File.AppendAllText(TestPathUtils.PathUnder(fixture.Composed, "coverage.cobertura.xml"), "<!-- caller change -->");
        var before = Snapshot(fixture.Composed);

        var result = await CoverageFanIn.ComposeAsync(fixture.Compose(native) with
        { OutputMode = CoverageOutputMode.ReplaceOwned });

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.Equal(before, Snapshot(fixture.Composed));
    }

    [Fact]
    public async Task ChangedCompositionIsRejectedBeforeAcceptancePublishesEitherGate()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.CapturePrimaryAsync();
        var native = await fixture.ImportThreePlatformCapturesAsync(fixture.Policy);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose(native))).Outcome);
        var compositionReport = Path.Join(fixture.Composed, "coverage.cobertura.xml");
        File.WriteAllText(compositionReport,
            File.ReadAllText(compositionReport).Replace("hits=\"1\"", "hits=\"0\"", StringComparison.Ordinal));

        var accepted = await CoverageFanIn.AcceptAsync(fixture.Accept(native));

        Assert.Equal(CoverageOperationOutcome.Rejected, accepted.Outcome);
        Assert.False(accepted.Accepted);
        Assert.False(accepted.Verified);
        Assert.False(Directory.Exists(fixture.Accepted));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "primary-gate.json")));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "composed-gate.json")));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string[] Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Hash(File.ReadAllBytes(path)))
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class Fixture : IDisposable
    {
        private const string ReportText = """<coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2" line-rate="1" branch-rate="1"><packages><package name="Example"><classes><class name="Example" filename="src/Example.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class></classes></package></packages></coverage>""";
        private readonly string? _priorStore = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        internal TestDirectory Temp { get; } = TestDirectory.Create();
        internal string Repository => Path.Join(Temp.Path, "repo");
        internal string Report => Path.Join(Temp.Path, "producer", "coverage.xml");
        internal string Timings => Path.Join(Temp.Path, "producer", "timings.json");
        internal string Primary => Path.Join(Temp.Path, "primary");
        internal string Composed => Path.Join(Temp.Path, "composed");
        internal string Accepted => Path.Join(Temp.Path, "accepted");
        internal CoverageFanInPolicy Policy { get; private set; } = null!;

        private Fixture()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(Temp.Path, "store"));
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Temp.WriteFile("repo/checks/Unit.csproj", "<Project />");
            fixture.Temp.WriteFile("repo/checks/Integration.csproj", "<Project />");
            fixture.Temp.WriteFile("repo/src/Example.cs", "public sealed class Example { }\n");
            await Command(fixture.Repository, "git", "init");
            await Command(fixture.Repository, "git", "config", "user.email", "coverage-tests@example.invalid");
            await Command(fixture.Repository, "git", "config", "user.name", "Coverage parity tests");
            await Command(fixture.Repository, "git", "add", ".");
            await Command(fixture.Repository, "git", "commit", "-m", "coverage parity fixture");
            var commit = await Command(fixture.Repository, "git", "rev-parse", "HEAD");
            var tree = await Command(fixture.Repository, "git", "rev-parse", "HEAD^{tree}");
            var sdk = await Command(fixture.Repository, "dotnet", "--version");
            fixture.Policy = new(new(commit, tree, sdk, "Release"),
                new("local", "issue-897-parity", Guid.NewGuid().ToString("N"), 1),
                ["checks/Unit.csproj", "checks/Integration.csproj"],
                // Each portable synthetic capture below contains one original collector
                // report. Declare that inventory explicitly; omission means both primary projects.
                [new("native-linux", "Linux", "Category=platform", ["checks/Integration.csproj"]),
                 new("native-windows", "Windows", "Category=platform", ["checks/Integration.csproj"]),
                 new("native-darwin", "Darwin", "Category=platform", ["checks/Integration.csproj"])],
                new(CoverageGateMode.AggregateOnly));
            fixture.Temp.WriteFile("producer/coverage.xml", ReportText);
            fixture.Temp.WriteFile("producer/timings.json", TimingsJson(fixture.Policy));
            return fixture;
        }

        internal CoverageCaptureRequest PrimaryRequest(string? output = null) =>
            new(Policy, Repository, output ?? Primary, "primary", Report, Timings);

        internal async Task<string> CapturePrimaryAsync()
        {
            var result = await CoverageFanIn.CaptureAsync(PrimaryRequest());
            Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
            return Primary;
        }

        internal async Task<string[]> ImportThreePlatformCapturesAsync(CoverageFanInPolicy policy)
        {
            var paths = new string[policy.Supplements.Count];
            for (var i = 0; i < paths.Length; i++)
            {
                var supplement = policy.Supplements[i];
                paths[i] = await ImportAsync(policy, supplement, WritePortableCapture(policy, supplement), "import-" + supplement.Id);
            }
            return paths;
        }

        internal string WritePortableCapture(CoverageFanInPolicy policy, CoverageSupplement supplement)
        {
            var wire = Temp.CreateDirectory("wire-" + supplement.Id + "-" + policy.Run.Attempt);
            var coverage = Encoding.UTF8.GetBytes(ReportText);
            var raw = Encoding.UTF8.GetBytes(ReportText);
            File.WriteAllBytes(Path.Join(wire, "coverage.cobertura.xml"), coverage);
            File.WriteAllBytes(Path.Join(wire, "collector.cobertura.xml"), raw);
            var canonical = JsonNode.Parse(Encoding.UTF8.GetString(CoveragePolicyEncoding.CanonicalBytes(policy)))!.AsObject();
            var manifest = new
            {
                schemaVersion = 2,
                producerId = supplement.Id,
                scope = "native",
                sourceRoot = Path.GetFullPath(Repository),
                source = canonical["source"]!.DeepClone(),
                run = canonical["run"]!.DeepClone(),
                policy = canonical.DeepClone(),
                policyDigest = CoveragePolicyEncoding.Digest(policy),
                policyEncoding = "coverage-policy-c14n-1",
                platform = supplement.Platform,
                filter = supplement.Filter,
                observation = "TrustedProducer",
                files = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coverage.cobertura.xml"] = Hash(coverage),
                    ["collector.cobertura.xml"] = Hash(raw)
                }
            };
            File.WriteAllBytes(Path.Join(wire, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            return wire;
        }

        internal async Task<string> ImportAsync(CoverageFanInPolicy policy, CoverageSupplement supplement,
            string wire, string outputName)
        {
            var output = Path.Join(Temp.Path, outputName);
            var result = await CoverageFanIn.CaptureAsync(new(policy, Repository, output, supplement.Id,
                ImportDirectory: wire, OriginalSourceRoot: Path.GetFullPath(Repository)));
            Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
            Assert.True(result.Effects.Imported);
            Assert.Equal(CoverageObservation.TrustedProducer, result.Observation);
            return output;
        }

        internal CoverageComposeRequest Compose(string[] native, string? output = null) =>
            new(Policy, Repository, Primary, native, output ?? Composed);
        internal CoverageAcceptRequest Accept(string[] native) =>
            new(Policy, Repository, Primary, native, Composed, Accepted);
        internal CoverageVerifyRequest Verify(string[] native) =>
            new(Policy, Repository, Primary, native, Composed, Accepted);

        private static string TimingsJson(CoverageFanInPolicy policy) => JsonSerializer.Serialize(new
        {
            configuration = policy.Source.Configuration,
            coverageDriver = "collector",
            merge = new { exitCode = 0 },
            artifacts = new { coverageFiles = policy.TestProjects.Count },
            projects = policy.TestProjects.Select(project => new
            {
                project,
                exitCode = 0,
                executionStatus = "completed",
                coverageArtifactStatus = "produced"
            })
        });

        private static async Task<string> Command(string root, string file, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new(file)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stderr);
            return (await stdout).Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _priorStore);
            Temp.Dispose();
        }
    }
}
