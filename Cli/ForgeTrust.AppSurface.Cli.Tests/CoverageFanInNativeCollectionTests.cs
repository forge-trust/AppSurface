using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInNativeCollectionTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("drop-method")]
    [InlineData("invent-hit")]
    [InlineData("invent-full-branch")]
    [InlineData("changed-owner")]
    [InlineData("wrong-root-total")]
    [InlineData("one-original")]
    [InlineData("empty-original")]
    [InlineData("null-originals")]
    [InlineData("malformed-point")]
    [InlineData("original-negative-covered")]
    public void NativeVerifierIndependentlyChecksEveryOriginalAgainstHandAuthoredConservativeGolden(string mutation)
    {
        const string root = "/consumer";
        var missed = XDocument.Parse("""
            <coverage lines-covered="0" lines-valid="1" branches-covered="0" branches-valid="2">
              <packages><package name="P"><classes><class name="C" filename="src/C.cs">
                <methods><method name="Run" signature="()"><lines><line number="1" hits="0" branch="false"/></lines></method></methods>
                <lines><line number="1" hits="0" branch="true" condition-coverage="0% (0/2)"/></lines>
              </class></classes></package></packages>
            </coverage>
            """);
        var partial = XDocument.Parse("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="2">
              <packages><package name="P"><classes><class name="C" filename="src/C.cs">
                <methods><method name="Run" signature="()"><lines><line number="1" hits="2" branch="false"/></lines></method></methods>
                <lines><line number="1" hits="2" branch="true" condition-coverage="50% (1/2)"/></lines>
              </class></classes></package></packages>
            </coverage>
            """);
        var golden = XDocument.Parse("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="0" branches-valid="2">
              <packages><package name="P"><classes><class name="C" filename="src/C.cs">
                <methods><method name="Run" signature="()"><lines><line number="1" hits="2" branch="false"/></lines></method></methods>
                <lines><line number="1" hits="2" branch="true" condition-coverage="0% (0/2)"/></lines>
              </class></classes></package></packages>
            </coverage>
            """);
        IReadOnlyList<(XDocument Document, string SourceRoot)> originals = [(missed, root), (partial, root)];
        CoverageFanInIndependentVerifier.VerifyNativeCollection(originals, golden);
        if (mutation == "none") return;
        switch (mutation)
        {
            case "drop-method": golden.Descendants("method").Single().Remove(); break;
            case "invent-hit": golden.Descendants("class").Single().Element("lines")!.Element("line")!.SetAttributeValue("hits", "3"); break;
            case "invent-full-branch":
                golden.Descendants("class").Single().Element("lines")!.Element("line")!.SetAttributeValue("condition-coverage", "100% (2/2)");
                golden.Root!.SetAttributeValue("branches-covered", "2"); break;
            case "changed-owner": golden.Descendants("class").Single().SetAttributeValue("name", "Other"); break;
            case "wrong-root-total": golden.Root!.SetAttributeValue("lines-valid", "2"); break;
            case "one-original": originals = [(missed, root)]; break;
            case "empty-original": originals = [(missed, root), (XDocument.Parse("<coverage/>"), root)]; break;
            case "null-originals": originals = null!; break;
            case "malformed-point": partial.Descendants("method").Single().Element("lines")!.Element("line")!.SetAttributeValue("hits", "2147483648"); break;
            case "original-negative-covered": partial.Root!.SetAttributeValue("lines-covered", "-1"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNativeCollection(originals, golden));
    }

    [Theory]
    [InlineData("lines", 2, 3)]
    [InlineData("branches", 3, 4)]
    public void NativeVerifierAcceptsValidLargerOriginalSummariesButDerivesProjectionFromSerializedFacts(
        string dimension, int covered, int valid)
    {
        const string root = "/consumer";
        var original = XDocument.Parse(Fixture.Report);
        var otherOriginal = XDocument.Parse(Fixture.Report);
        var golden = XDocument.Parse(Fixture.Report);
        original.Root!.SetAttributeValue(dimension + "-covered", covered);
        original.Root.SetAttributeValue(dimension + "-valid", valid);
        var retainedSummary = original.ToString(SaveOptions.DisableFormatting);

        CoverageFanInIndependentVerifier.VerifyNativeCollection(
            [(original, root), (otherOriginal, root)], golden);

        Assert.Equal(retainedSummary, original.ToString(SaveOptions.DisableFormatting));
        Assert.Equal("1", golden.Root!.Attribute("lines-valid")!.Value);
        Assert.Equal("1", golden.Root.Attribute("lines-covered")!.Value);
        Assert.Equal("2", golden.Root.Attribute("branches-valid")!.Value);
        Assert.Equal("2", golden.Root.Attribute("branches-covered")!.Value);
        // Unknown summary-only points remain in original evidence, not in the serialized-point projection.
        golden.Root.SetAttributeValue(dimension + "-valid", valid);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNativeCollection(
            [(original, root), (otherOriginal, root)], golden));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("bad-shape")]
    [InlineData("traversal")]
    [InlineData("wrong-prefix")]
    [InlineData("missing-directory")]
    [InlineData("missing-report")]
    [InlineData("two-reports")]
    [InlineData("duplicate-project")]
    [InlineData("failed-project")]
    [InlineData("depth")]
    [InlineData("cancelled")]
    public void AttachmentDiscoveryBindsSuccessfulProjectInvocationsAndRejectsAmbiguity(string fault)
    {
        using var temp = TestDirectory.Create();
        var collection = temp.CreateDirectory("collection");
        var invocation = "projects/a/collector-results/invocation";
        var attachment = temp.WriteFile("collection/" + invocation + "/attachment/coverage.cobertura.xml", Fixture.Report);
        // This second canonical copy is not original collector evidence.
        temp.WriteFile("collection/projects/a/coverage.cobertura.xml", "<coverage/>");
        switch (fault)
        {
            case "valid": break;
            case "bad-shape": invocation = "projects/a/collector-results"; break;
            case "traversal": invocation = "projects/../collector-results/invocation"; break;
            case "wrong-prefix": invocation = "other/a/collector-results/invocation"; break;
            case "missing-directory": invocation = "projects/a/collector-results/missing"; break;
            case "missing-report": File.Delete(attachment); break;
            case "two-reports": temp.WriteFile("collection/" + invocation + "/second/coverage.cobertura.xml", Fixture.Report); break;
            case "depth":
                File.Delete(attachment);
                temp.WriteFile("collection/" + invocation + "/a/b/c/coverage.cobertura.xml", Fixture.Report); break;
        }
        var entries = new JsonArray
        {
            new JsonObject
            {
                ["project"] = Fixture.Projects[0], ["exitCode"] = fault == "failed-project" ? 1 : 0,
                ["executionStatus"] = "completed", ["coverageArtifactStatus"] = "produced",
                ["invocationDirectory"] = invocation
            }
        };
        if (fault == "duplicate-project") entries.Add(entries[0]!.DeepClone());
        WriteDiscoveryTimings(collection, entries);
        var expected = DiscoveryExpected(fault == "depth" ? 1 : 64);
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancelled") cancellation.Cancel();
        var discover = () => CoverageFanInEngine.FindNativeReports(collection, [Fixture.Projects[0]], expected, cancellation.Token);
        if (fault == "valid") Assert.Equal(attachment, Assert.Single(discover()).Value);
        else if (fault == "cancelled") Assert.ThrowsAny<OperationCanceledException>(discover);
        else if (fault == "missing-directory") Assert.ThrowsAny<IOException>(discover);
        else Assert.Throws<InvalidDataException>(discover);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttachmentDiscoveryRejectsLinkedInvocationAndAttachmentDirectories(bool linkInvocation)
    {
        using var temp = TestDirectory.Create();
        var collection = temp.CreateDirectory("collection");
        var outside = temp.CreateDirectory("outside");
        temp.WriteFile("outside/coverage.cobertura.xml", Fixture.Report);
        var parent = temp.CreateDirectory("collection/projects/a/collector-results");
        var invocation = Path.Join(parent, "invocation");
        if (linkInvocation) TestFileSystem.CreateDirectoryLinkOrSkip(invocation, outside);
        else
        {
            Directory.CreateDirectory(invocation);
            TestFileSystem.CreateDirectoryLinkOrSkip(Path.Join(invocation, "attachment"), outside);
        }
        WriteDiscoveryTimings(collection, new JsonArray(new JsonObject
        {
            ["project"] = Fixture.Projects[0],
            ["exitCode"] = 0,
            ["executionStatus"] = "completed",
            ["coverageArtifactStatus"] = "produced",
            ["invocationDirectory"] = "projects/a/collector-results/invocation"
        }));
        var before = File.ReadAllBytes(Path.Join(outside, "coverage.cobertura.xml"));

        Assert.ThrowsAny<IOException>(() => CoverageFanInEngine.FindNativeReports(
            collection, [Fixture.Projects[0]], DiscoveryExpected(), CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(Path.Join(outside, "coverage.cobertura.xml")));
    }

    [Fact]
    public void AttachmentDiscoveryMapsOutOfOrderTimingEntriesToTheirOwnOriginals()
    {
        using var temp = TestDirectory.Create();
        var collection = temp.CreateDirectory("collection");
        var first = temp.WriteFile("collection/projects/a/collector-results/first/id/coverage.cobertura.xml", Fixture.Report);
        var second = temp.WriteFile("collection/projects/b/collector-results/second/id/coverage.cobertura.xml", Fixture.Report + "\n");
        temp.WriteFile("collection/coverage.cobertura.xml", "canonical is not evidence");
        var projects = new JsonArray();
        foreach (var index in new[] { 1, 0 })
            projects.Add(new JsonObject
            {
                ["project"] = Fixture.Projects[index],
                ["exitCode"] = 0,
                ["executionStatus"] = "completed",
                ["coverageArtifactStatus"] = "produced",
                ["invocationDirectory"] = index == 0 ? "projects/a/collector-results/first" : "projects/b/collector-results/second"
            });
        WriteDiscoveryTimings(collection, projects, 2);
        var expected = DiscoveryExpected();
        var timingsBytes = File.ReadAllBytes(Path.Join(collection, "timings.json"));
        var observedHashes = new List<string>();

        var found = CoverageFanInEngine.FindNativeReports(collection, Fixture.Projects, expected, CancellationToken.None,
            observedHashes.Add);

        Assert.Equal(first, found[Fixture.Projects[0]]);
        Assert.Equal(second, found[Fixture.Projects[1]]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(timingsBytes)), Assert.Single(observedHashes));
        Assert.Equal(timingsBytes, File.ReadAllBytes(Path.Join(collection, "timings.json")));
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("same-length-mutation")]
    [InlineData("missing-selected")]
    public void SelectedOriginalHashesBindDiscoveryToTheRetainedCaptureRead(string change)
    {
        using var temp = TestDirectory.Create();
        var collection = temp.CreateDirectory("collection");
        var selected = temp.WriteFile("collection/projects/a/collector-results/invocation/attachment/coverage.cobertura.xml",
            Fixture.Report);
        WriteDiscoveryTimings(collection, new JsonArray(new JsonObject
        {
            ["project"] = Fixture.Projects[0],
            ["exitCode"] = 0,
            ["executionStatus"] = "completed",
            ["coverageArtifactStatus"] = "produced",
            ["invocationDirectory"] = "projects/a/collector-results/invocation"
        }));
        var expected = DiscoveryExpected();
        var observations = new List<IReadOnlyDictionary<string, string>>();
        var original = File.ReadAllBytes(selected);
        var found = CoverageFanInEngine.FindNativeReports(collection, [Fixture.Projects[0]], expected,
            CancellationToken.None, observeReportHashes: observations.Add);
        Assert.Equal(selected, found[Fixture.Projects[0]]);
        var hashes = Assert.Single(observations);
        var fact = Assert.Single(hashes);
        Assert.Equal(selected, fact.Key);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), fact.Value);

        if (change == "same-length-mutation")
        {
            var modified = Encoding.UTF8.GetBytes(Fixture.Report.Replace("hits=\"1\"", "hits=\"2\"", StringComparison.Ordinal));
            Assert.Equal(original.Length, modified.Length);
            Assert.NotEqual(original, modified);
            File.WriteAllBytes(selected, modified);
        }
        else if (change == "missing-selected") File.Delete(selected);

        using var io = new CoverageFanInIO(expected.Bounds, CancellationToken.None);
        io.Acquire([(Path.GetDirectoryName(selected)!, false)], requireCoordination: false);
        if (change == "same-length-mutation")
            Assert.Throws<InvalidDataException>(() => CoverageFanInEngine.VerifySelectedNativeReports(io, hashes));
        else if (change == "missing-selected")
            Assert.ThrowsAny<IOException>(() => CoverageFanInEngine.VerifySelectedNativeReports(io, hashes));
        else
        {
            CoverageFanInEngine.VerifySelectedNativeReports(io, hashes);
            Assert.Equal(original, io.Read(selected, xml: true));
            io.VerifyBindings();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AttachmentDiscoveryCannotReturnSelectedHashesOutsideXmlOrTotalByteBudget(bool xmlLimit)
    {
        using var temp = TestDirectory.Create();
        var collection = temp.CreateDirectory("collection");
        var selected = temp.WriteFile("collection/projects/a/collector-results/invocation/attachment/coverage.cobertura.xml",
            Fixture.Report);
        WriteDiscoveryTimings(collection, new JsonArray(new JsonObject
        {
            ["project"] = Fixture.Projects[0],
            ["exitCode"] = 0,
            ["executionStatus"] = "completed",
            ["coverageArtifactStatus"] = "produced",
            ["invocationDirectory"] = "projects/a/collector-results/invocation"
        }));
        var reportBytes = File.ReadAllBytes(selected);
        var timingsBytes = File.ReadAllBytes(Path.Join(collection, "timings.json"));
        var expected = xmlLimit
            ? DiscoveryExpected(xmlBytes: reportBytes.LongLength - 1)
            : DiscoveryExpected(totalBytes: reportBytes.LongLength + timingsBytes.LongLength - 1);
        var observations = new List<IReadOnlyDictionary<string, string>>();

        Assert.Throws<InvalidDataException>(() => CoverageFanInEngine.FindNativeReports(
            collection, [Fixture.Projects[0]], expected, CancellationToken.None, observeReportHashes: observations.Add));
        Assert.Empty(observations);
        Assert.Equal(reportBytes, File.ReadAllBytes(selected));
    }

    [Fact]
    public void AttachmentDiscoveryEnforcesTheCumulativeEntryBudgetAcrossDirectories()
    {
        using var temp = TestDirectory.Create();
        var collection = temp.CreateDirectory("collection");
        var invocation = temp.CreateDirectory("collection/projects/a/collector-results/invocation");
        var first = Directory.CreateDirectory(Path.Join(invocation, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Join(invocation, "second")).FullName;
        for (var index = 0; index < 5001; index++)
        {
            File.WriteAllBytes(Path.Join(first, "diagnostic-" + index), []);
            File.WriteAllBytes(Path.Join(second, "diagnostic-" + index), []);
        }
        File.WriteAllText(Path.Join(first, "coverage.cobertura.xml"), Fixture.Report);
        WriteDiscoveryTimings(collection, new JsonArray(new JsonObject
        {
            ["project"] = Fixture.Projects[0],
            ["exitCode"] = 0,
            ["executionStatus"] = "completed",
            ["coverageArtifactStatus"] = "produced",
            ["invocationDirectory"] = "projects/a/collector-results/invocation"
        }));

        Assert.Throws<InvalidDataException>(() => CoverageFanInEngine.FindNativeReports(
            collection, [Fixture.Projects[0]], DiscoveryExpected(), CancellationToken.None));
    }

    [Fact]
    public void AttachmentDiscoverySharesTheEntryBudgetAcrossProjectsBeforeReportingSelectedHashes()
    {
        using var temp = TestDirectory.Create();
        var collection = temp.CreateDirectory("collection");
        var projects = new JsonArray();
        for (var projectIndex = 0; projectIndex < Fixture.Projects.Length; projectIndex++)
        {
            var invocation = $"projects/project-{projectIndex}/collector-results/invocation";
            var attachment = temp.CreateDirectory("collection/" + invocation + "/attachment");
            for (var index = 0; index < 5999; index++)
                File.WriteAllBytes(Path.Join(attachment, "diagnostic-" + index), []);
            File.WriteAllText(Path.Join(attachment, "coverage.cobertura.xml"), Fixture.Report);
            projects.Add(new JsonObject
            {
                ["project"] = Fixture.Projects[projectIndex],
                ["exitCode"] = 0,
                ["executionStatus"] = "completed",
                ["coverageArtifactStatus"] = "produced",
                ["invocationDirectory"] = invocation
            });
        }
        WriteDiscoveryTimings(collection, projects, 2);
        var observations = new List<IReadOnlyDictionary<string, string>>();

        Assert.Throws<InvalidDataException>(() => CoverageFanInEngine.FindNativeReports(
            collection, Fixture.Projects, DiscoveryExpected(), CancellationToken.None,
            observeReportHashes: observations.Add));

        Assert.Empty(observations);
    }

    private static void WriteDiscoveryTimings(string collection, JsonArray projects, int count = 1) =>
        File.WriteAllText(Path.Join(collection, "timings.json"), new JsonObject
        {
            ["configuration"] = "Release",
            ["coverageDriver"] = "collector",
            ["merge"] = new JsonObject { ["exitCode"] = 0 },
            ["artifacts"] = new JsonObject { ["coverageFiles"] = count },
            ["projects"] = projects
        }.ToJsonString());

    private static CoverageFanInExpected DiscoveryExpected(int depth = 64, long xmlBytes = 536870912,
        long totalBytes = 4294967296)
    {
        var policy = CoveragePolicyEncoding.Freeze(new(
            new(new string('a', 40), new string('b', 40), "10.0.402", "Release"),
            new("local", "native-discovery", "run", 1), [Fixture.Projects[0]], [], new(CoverageGateMode.AggregateOnly),
            Limits: new(XmlBytes: xmlBytes, TotalBytes: totalBytes, Depth: depth)));
        return new(JsonSerializer.SerializeToElement(policy, CoveragePolicyEncoding.Json), CoveragePolicyEncoding.Digest(policy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexedOriginalsRetainBytesAndImportAcceptVerifyWithExplicitOrFallbackInventory(bool fallback)
    {
        using var fixture = await Fixture.Create(fallback);
        await fixture.CapturePrimary();
        var captured = await CoverageFanIn.CaptureAsync(fixture.NativeRequest());
        AssertSuccess(captured);
        Assert.Equal(CoverageObservation.TrustedProducer, captured.Observation);
        Assert.Equal(Fixture.PayloadNames.Append("generation.json").Order(StringComparer.Ordinal).ToArray(),
            captured.Artifacts.Select(item => item.Role).Order(StringComparer.Ordinal).ToArray());
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Native, "collector-inventory.json")));
        Assert.Equal(1, inventory.RootElement.GetProperty("schemaVersion").GetInt32());
        var reports = inventory.RootElement.GetProperty("reports").EnumerateArray().ToArray();
        Assert.Equal(Fixture.Projects, reports.Select(item => item.GetProperty("project").GetString()).ToArray());
        for (var index = 0; index < reports.Length; index++)
        {
            Assert.Equal(Fixture.Collector(index), reports[index].GetProperty("file").GetString());
            Assert.Equal(File.ReadAllBytes(fixture.RawPaths[Fixture.Projects[index]]),
                File.ReadAllBytes(Path.Join(fixture.Native, Fixture.Collector(index))));
        }
        Assert.Equal(File.ReadAllBytes(fixture.Timings), File.ReadAllBytes(Path.Join(fixture.Native, "timings.json")));

        var transported = fixture.Transport();
        var imported = await CoverageFanIn.CaptureAsync(fixture.Import(transported));
        AssertSuccess(imported);
        Assert.True(imported.Effects.Imported);
        Assert.Equal(CoverageObservation.TrustedProducer, imported.Observation);
        var replaced = await CoverageFanIn.CaptureAsync(fixture.Import(transported) with { OutputMode = CoverageOutputMode.ReplaceOwned });
        AssertSuccess(replaced);
        Assert.NotEqual(imported.Effects.Generation, replaced.Effects.Generation);
        Assert.False(replaced.Effects.OutputCreated);
        Assert.Equal(File.ReadAllBytes(Path.Join(transported, "manifest.json")), File.ReadAllBytes(Path.Join(fixture.Receiving, "manifest.json")));
        AssertSuccess(await CoverageFanIn.ComposeAsync(fixture.Compose()));
        var accepted = await CoverageFanIn.AcceptAsync(fixture.Accept());
        AssertSuccess(accepted);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Verified);
        Assert.True(accepted.Effects.Imported);
        Assert.Equal("PatchAccepted", accepted.PatchOutcome);
        var verified = await CoverageFanIn.VerifyAsync(fixture.Verify());
        AssertSuccess(verified);
        Assert.True(verified.Verified);

        var original = File.ReadAllBytes(Path.Join(fixture.Receiving, Fixture.Collector(1)));
        File.AppendAllText(Path.Join(fixture.Receiving, Fixture.Collector(1)), "\n<!-- changed after acceptance -->");
        Assert.NotEqual(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.VerifyAsync(fixture.Verify())).Outcome);
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
        File.WriteAllBytes(Path.Join(fixture.Receiving, Fixture.Collector(1)), original);
        AssertSuccess(await CoverageFanIn.VerifyAsync(fixture.Verify()));
    }

    [Fact]
    public async Task TwoPartialOriginalsCannotInventFullBranchCoverage()
    {
        using var fixture = await Fixture.Create();
        foreach (var path in fixture.RawPaths.Values)
            File.WriteAllText(path, Fixture.Report.Replace("branches-covered=\"2\"", "branches-covered=\"1\"", StringComparison.Ordinal)
                .Replace("100% (2/2)", "50% (1/2)", StringComparison.Ordinal));

        AssertSuccess(await CoverageFanIn.CaptureAsync(fixture.NativeRequest()));

        var projected = XDocument.Load(Path.Join(fixture.Native, "coverage.cobertura.xml"));
        Assert.Equal("1", projected.Root!.Attribute("branches-covered")!.Value);
        Assert.Equal("2", projected.Root.Attribute("branches-valid")!.Value);
        Assert.Equal("50% (1/2)", projected.Descendants("class").Single().Element("lines")!.Element("line")!
            .Attribute("condition-coverage")!.Value);
        Assert.Equal(File.ReadAllBytes(fixture.RawPaths[Fixture.Projects[0]]), File.ReadAllBytes(Path.Join(fixture.Native, Fixture.Collector(0))));
    }

    [Theory]
    [InlineData("missing-project")]
    [InlineData("unknown-project")]
    [InlineData("same-path")]
    [InlineData("missing-timings")]
    [InlineData("failed-project")]
    [InlineData("legacy-single")]
    [InlineData("conflicting-normalized")]
    [InlineData("conflicting-single")]
    public async Task CaptureRejectsIncompleteOrAmbiguousOriginalInventoryBeforePublication(string fault)
    {
        using var fixture = await Fixture.Create();
        var request = fixture.NativeRequest();
        var paths = fixture.RawPaths.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        switch (fault)
        {
            case "missing-project": paths.Remove(Fixture.Projects[1]); break;
            case "unknown-project": paths["checks/Other.csproj"] = paths[Fixture.Projects[1]]; paths.Remove(Fixture.Projects[1]); break;
            case "same-path": paths[Fixture.Projects[1]] = paths[Fixture.Projects[0]]; break;
            case "missing-timings": request = request with { TimingsPath = null }; break;
            case "failed-project":
                var timings = JsonNode.Parse(File.ReadAllBytes(fixture.Timings))!;
                timings["projects"]![1]!["exitCode"] = 1;
                File.WriteAllText(fixture.Timings, timings.ToJsonString());
                break;
            case "legacy-single":
                request = request with
                {
                    RawCoveragePaths = null,
                    TimingsPath = null,
                    CoveragePath = paths[Fixture.Projects[0]],
                    RawCoveragePath = paths[Fixture.Projects[0]]
                };
                break;
            case "conflicting-normalized": request = request with { CoveragePath = paths[Fixture.Projects[0]] }; break;
            case "conflicting-single": request = request with { RawCoveragePath = paths[Fixture.Projects[0]] }; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        if (fault != "legacy-single") request = request with { RawCoveragePaths = paths };

        var result = await CoverageFanIn.CaptureAsync(request);

        Assert.NotEqual(CoverageOperationOutcome.Succeeded, result.Outcome);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.False(Directory.Exists(fixture.Native));
    }

    [Theory]
    [InlineData("missing-project")]
    [InlineData("duplicate-project")]
    [InlineData("swapped-projects")]
    [InlineData("duplicate-file")]
    [InlineData("swapped-files")]
    [InlineData("outside-file")]
    [InlineData("wrong-schema")]
    [InlineData("reports-object")]
    [InlineData("extra-index-field")]
    [InlineData("duplicate-json-field")]
    [InlineData("malformed-index")]
    [InlineData("missing-raw")]
    [InlineData("changed-raw")]
    [InlineData("changed-projection")]
    [InlineData("unknown-file")]
    [InlineData("incomplete-timings")]
    [InlineData("duplicate-timings-project")]
    [InlineData("failed-timings-project")]
    [InlineData("legacy-single")]
    public async Task ImportRejectsSemanticTamperingEvenWithRefreshedPayloadHashes(string fault)
    {
        using var fixture = await Fixture.Create();
        AssertSuccess(await CoverageFanIn.CaptureAsync(fixture.NativeRequest()));
        var transported = fixture.Transport();
        Mutate(transported, fault);
        RefreshHashes(transported);

        var result = await CoverageFanIn.CaptureAsync(fixture.Import(transported));

        Assert.NotEqual(CoverageOperationOutcome.Succeeded, result.Outcome);
        Assert.False(result.Effects.Imported && result.Effects.GenerationCommitted);
        Assert.False(Directory.Exists(fixture.Receiving));
    }

    [Theory]
    [InlineData("changed-raw")]
    [InlineData("changed-projection")]
    [InlineData("duplicate-project")]
    public async Task InvalidImportedReplacementPreservesThePriorOwnedGeneration(string fault)
    {
        using var fixture = await Fixture.Create();
        AssertSuccess(await CoverageFanIn.CaptureAsync(fixture.NativeRequest()));
        var transported = fixture.Transport();
        AssertSuccess(await CoverageFanIn.CaptureAsync(fixture.Import(transported)));
        var before = Snapshot(fixture.Receiving);
        Mutate(transported, fault);
        RefreshHashes(transported);

        var result = await CoverageFanIn.CaptureAsync(fixture.Import(transported) with { OutputMode = CoverageOutputMode.ReplaceOwned });

        Assert.NotEqual(CoverageOperationOutcome.Succeeded, result.Outcome);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.Equal(before, Snapshot(fixture.Receiving));
    }

    private static void Mutate(string directory, string fault)
    {
        var indexPath = Path.Join(directory, "collector-inventory.json");
        var index = JsonNode.Parse(File.ReadAllBytes(indexPath))!;
        var reports = index["reports"]!.AsArray();
        switch (fault)
        {
            case "missing-project": reports.RemoveAt(1); break;
            case "duplicate-project": reports[1]!["project"] = reports[0]!["project"]!.GetValue<string>(); break;
            case "swapped-projects":
                reports[0]!["project"] = Fixture.Projects[1]; reports[1]!["project"] = Fixture.Projects[0]; break;
            case "duplicate-file": reports[1]!["file"] = Fixture.Collector(0); break;
            case "swapped-files": reports[0]!["file"] = Fixture.Collector(1); reports[1]!["file"] = Fixture.Collector(0); break;
            case "outside-file": reports[0]!["file"] = "../collector-0000.cobertura.xml"; break;
            case "wrong-schema": index["schemaVersion"] = 2; break;
            case "reports-object": index["reports"] = new JsonObject(); break;
            case "extra-index-field": index["unexpected"] = true; break;
            case "duplicate-json-field": File.WriteAllText(indexPath, "{\"schemaVersion\":1,\"schemaVersion\":1,\"reports\":[]}"); return;
            case "malformed-index": File.WriteAllText(indexPath, "{"); return;
            case "missing-raw": File.Delete(Path.Join(directory, Fixture.Collector(1))); return;
            case "changed-raw":
                File.WriteAllText(Path.Join(directory, Fixture.Collector(1)), Fixture.Report.Replace("hits=\"1\"", "hits=\"2\"", StringComparison.Ordinal)); return;
            case "changed-projection":
                var projection = XDocument.Load(Path.Join(directory, "coverage.cobertura.xml"));
                projection.Descendants("class").Single().Element("lines")!.Element("line")!.SetAttributeValue("number", "2");
                projection.Save(Path.Join(directory, "coverage.cobertura.xml")); return;
            case "unknown-file": File.WriteAllText(Path.Join(directory, "unrecognized.txt"), "preserve"); return;
            case "incomplete-timings":
            case "duplicate-timings-project":
            case "failed-timings-project":
                var path = Path.Join(directory, "timings.json");
                var timings = JsonNode.Parse(File.ReadAllBytes(path))!;
                if (fault == "incomplete-timings") timings["projects"]!.AsArray().RemoveAt(1);
                else if (fault == "duplicate-timings-project") timings["projects"]![1]!["project"] = Fixture.Projects[0];
                else timings["projects"]![1]!["executionStatus"] = "skipped";
                File.WriteAllText(path, timings.ToJsonString()); return;
            case "legacy-single":
                File.Copy(Path.Join(directory, Fixture.Collector(0)), Path.Join(directory, "collector.cobertura.xml"));
                foreach (var name in new[] { "collector-inventory.json", "timings.json", Fixture.Collector(0), Fixture.Collector(1) })
                    File.Delete(Path.Join(directory, name));
                return;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        File.WriteAllText(indexPath, index.ToJsonString());
    }

    private static void RefreshHashes(string directory)
    {
        var path = Path.Join(directory, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
        var files = new JsonObject();
        foreach (var item in Directory.EnumerateFiles(directory).Where(item => Path.GetFileName(item) != "manifest.json"))
            files[Path.GetFileName(item)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item)));
        manifest["files"] = files;
        File.WriteAllText(path, manifest.ToJsonString());
    }

    private static string[] Snapshot(string directory) => Directory.EnumerateFiles(directory)
        .Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static void AssertSuccess(CoverageFanInResult result) =>
        Assert.True(result.Outcome == CoverageOperationOutcome.Succeeded, JsonSerializer.Serialize(result));

    private sealed class Fixture : IDisposable
    {
        internal static readonly string[] Projects = ["checks/A.csproj", "checks/B.csproj"];
        internal static readonly string[] PayloadNames =
            ["collector-0000.cobertura.xml", "collector-0001.cobertura.xml", "collector-inventory.json", "coverage.cobertura.xml", "manifest.json", "timings.json"];
        internal const string Report = """
            <coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2">
              <packages><package name="Consumer"><classes><class name="Decision" filename="src/Decision.cs">
                <methods><method name="Run" signature="()"><lines><line number="1" hits="1" branch="false"/></lines></method></methods>
                <lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines>
              </class></classes></package></packages>
            </coverage>
            """;
        private readonly TestDirectory _temp = TestDirectory.Create();
        private readonly string? _artifactHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        internal string Repository => Path.Join(_temp.Path, "repo");
        internal string Native => Path.Join(_temp.Path, "native");
        internal string Primary => Path.Join(_temp.Path, "primary");
        internal string Receiving => Path.Join(_temp.Path, "receiving");
        internal string Composed => Path.Join(_temp.Path, "composed");
        internal string Accepted => Path.Join(_temp.Path, "accepted");
        internal string Timings => Path.Join(_temp.Path, "producer", "timings.json");
        internal CoverageFanInPolicy Policy { get; private set; } = null!;
        internal Dictionary<string, string> RawPaths { get; } = new(StringComparer.Ordinal);
        internal static string Collector(int index) => "collector-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".cobertura.xml";

        internal static async Task<Fixture> Create(bool fallback = false)
        {
            var f = new Fixture();
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(f._temp.Path, "store"));
            foreach (var project in Projects) f._temp.WriteFile("repo/" + project, "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
            f._temp.WriteFile("repo/src/Decision.cs", "class Decision { int Run() => 1; }\n");
            await Command(f.Repository, "git", "init");
            await Command(f.Repository, "git", "config", "user.email", "coverage@example.invalid");
            await Command(f.Repository, "git", "config", "user.name", "Native coverage tests");
            await Command(f.Repository, "git", "add", ".");
            await Command(f.Repository, "git", "commit", "-m", "base");
            var baseline = await Command(f.Repository, "git", "rev-parse", "HEAD");
            f._temp.WriteFile("repo/src/Decision.cs", "class Decision { int Run() => 2; }\n");
            await Command(f.Repository, "git", "add", ".");
            await Command(f.Repository, "git", "commit", "-m", "current");
            var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "Linux";
            f.Policy = new(new(await Command(f.Repository, "git", "rev-parse", "HEAD"),
                await Command(f.Repository, "git", "rev-parse", "HEAD^{tree}"), await Command(f.Repository, "dotnet", "--version"), "Release"),
                new("local", "native-collection-tests", Guid.NewGuid().ToString("N"), 1), Projects,
                [new("native", platform, "FullyQualifiedName~Consumer.", fallback ? null : Projects)],
                new(CoverageGateMode.AggregateAndPatch, DiffBase: baseline));
            for (var index = 0; index < Projects.Length; index++)
                f.RawPaths.Add(Projects[index], f._temp.WriteFile("producer/raw-" + index + ".xml", Report));
            f._temp.WriteFile("producer/timings.json", JsonSerializer.Serialize(new
            {
                configuration = "Release",
                coverageDriver = "collector",
                merge = new { exitCode = 0 },
                artifacts = new { coverageFiles = 2 },
                projects = Projects.Select((project, index) => new
                {
                    project,
                    exitCode = 0,
                    executionStatus = "completed",
                    coverageArtifactStatus = "produced",
                    invocationDirectory = $"projects/project-{index}/collector-results/invocation"
                })
            }));
            return f;
        }

        internal CoverageCaptureRequest NativeRequest() => new(Policy, Repository, Native, "native",
            TimingsPath: Timings, RawCoveragePaths: RawPaths);
        internal async Task CapturePrimary() => AssertSuccess(await CoverageFanIn.CaptureAsync(
            new(Policy, Repository, Primary, "primary", RawPaths[Projects[0]], Timings)));
        internal CoverageCaptureRequest Import(string transport) => new(Policy, Repository, Receiving, "native",
            ImportDirectory: transport, OriginalSourceRoot: CoverageRunOutputLease.NormalizePlatformPath(Repository));
        internal CoverageComposeRequest Compose() => new(Policy, Repository, Primary, [Receiving], Composed);
        internal CoverageAcceptRequest Accept() => new(Policy, Repository, Primary, [Receiving], Composed, Accepted);
        internal CoverageVerifyRequest Verify() => new(Policy, Repository, Primary, [Receiving], Composed, Accepted);
        internal string Transport()
        {
            var path = _temp.CreateDirectory("transport-" + Guid.NewGuid().ToString("N"));
            foreach (var item in Directory.EnumerateFiles(Native).Where(item =>
                !Path.GetFileName(item).StartsWith('.') && Path.GetFileName(item) != "generation.json"))
                File.Copy(item, Path.Join(path, Path.GetFileName(item)));
            return path;
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _artifactHome);
            _temp.Dispose();
        }
        private static async Task<string> Command(string directory, string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await error);
            return (await output).Trim();
        }
    }
}
