using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Detached parser and matcher controls; synthetic values never represent protected qualification evidence.</summary>
public sealed class EvidenceProtectedSupportRecordTests
{
    [Fact]
    public void CanonicalBytesRequireTheIndependentExpectedDigestAndParsedListsAreReadOnly()
    {
        var record = DataShapeOnly();
        var bytes = EvidenceCanonicalJson.Serialize(record);
        var expected = EvidenceDigest.Sha256(bytes);

        var parsed = EvidenceProtectedSupportRecord.Parse(bytes, expected);

        Assert.Equal(expected, parsed.ComputeContentSha256());
        Assert.NotSame(record.Applications, parsed.Data.Applications);
        var applications = Assert.IsAssignableFrom<IList<EvidenceSupportApplication>>(parsed.Data.Applications);
        Assert.True(applications.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => applications[0] = applications[0]);
        var files = Assert.IsAssignableFrom<IList<EvidenceSupportFile>>(parsed.Data.Tool.Files);
        Assert.True(files.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => files[0] = files[0]);
        var receipts = Assert.IsAssignableFrom<IList<EvidenceSupportReceipt>>(parsed.Data.Qualification.ProofReceipts);
        Assert.True(receipts.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => receipts[0] = receipts[0]);
        Assert.Throws<FormatException>(() => EvidenceProtectedSupportRecord.Parse(bytes, Hex('0')));
        Assert.Throws<FormatException>(() => EvidenceProtectedSupportRecord.Parse(bytes, null));
        Assert.Throws<FormatException>(() => EvidenceProtectedSupportRecord.Parse(bytes.Concat(new byte[] { (byte)' ' }).ToArray(),
            EvidenceDigest.Sha256(bytes.Concat(new byte[] { (byte)' ' }).ToArray())));
        // This proves canonical parsing and byte binding only, never that the named run or receipts existed.
    }

    [Fact]
    public void ParserRejectsExcessiveDepthWithOnlyTheFixedDiagnostic()
    {
        var nested = new string('[', 20) + "\"depth-canary\"" + new string(']', 20);
        var bytes = Encoding.UTF8.GetBytes("{\"Tool\":" + nested + "}");
        Assert.True(bytes.Length < EvidenceProtectedSupportRecord.MaximumRecordBytes);

        var error = Assert.Throws<FormatException>(() => Parse(bytes));

        Assert.Equal("Protected support record is invalid.", error.Message);
        Assert.Null(error.InnerException);
        Assert.False(error.ToString().Contains("depth-canary", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Provider", "null")]
    [InlineData("Protocol", "\"evidence-worker-linux-v1\"")]
    [InlineData("Protocol", "\"evidence-worker-linux-v3\"")]
    [InlineData("Tool", "null")]
    [InlineData("Qualification", "null")]
    [InlineData("RepositoryId", "true")]
    [InlineData("RepositoryId", "9223372036854775808")]
    [InlineData("Qualification.RunAttempt", "0")]
    [InlineData("Qualification.RunId", "0")]
    [InlineData("Qualification.CompletedRunState", "\"in_progress\"")]
    [InlineData("Qualification.Conclusion", "\"failure\"")]
    [InlineData("Qualification.Event", "\"push\"")]
    [InlineData("Qualification.Ref", "\"refs/heads/feature\"")]
    public void ParserRejectsTypedNullMalformedRangeOrNonSuccessfulQualification(string path, string replacementJson)
    {
        var bytes = CanonicalDataBytes();
        _ = Parse(bytes); // Each mutation is tested against a known-parseable canonical data shape.
        var node = JsonNode.Parse(bytes)!.AsObject();
        SetJson(node, path, JsonNode.Parse(replacementJson));
        Reject(EvidenceCanonicalJson.Serialize(node));
    }

    [Theory]
    [InlineData("Qualification.WorkflowPath", "\".github/workflows/other.yml\"")]
    [InlineData("Qualification.WorkflowSha256", "\"bad\"")]
    public void ParserRejectsMalformedQualificationWorkflowIdentity(string path, string replacementJson)
    {
        var bytes = CanonicalDataBytes();
        _ = Parse(bytes);
        var node = JsonNode.Parse(bytes)!.AsObject();
        SetJson(node, path, JsonNode.Parse(replacementJson));
        Reject(EvidenceCanonicalJson.Serialize(node));
    }

    [Fact]
    public void QualificationAndCurrentWorkflowIdentitiesRemainSeparateDataFields()
    {
        var fixture = DataShapeOnly();
        Assert.NotEqual(fixture.Qualification.WorkflowPath, fixture.CurrentWorkflowPath);
        Assert.NotEqual(fixture.Qualification.WorkflowSha256, fixture.CurrentWorkflowSha256);
        Assert.Equal(40, fixture.Qualification.WorkflowCommit.Length);
        var parsed = Parse(CanonicalDataBytes());
        Assert.Equal(EvidenceProtectedSupportRecord.QualificationWorkflowPath, parsed.Data.Qualification.WorkflowPath);
        Assert.Equal(fixture.CurrentWorkflowPath, parsed.Data.CurrentWorkflowPath);

        var wrongCurrentWorkflow = JsonNode.Parse(CanonicalDataBytes())!.AsObject();
        wrongCurrentWorkflow["CurrentWorkflowPath"] = ".github/workflows/other.yml";
        Reject(EvidenceCanonicalJson.Serialize(wrongCurrentWorkflow));
    }

    [Fact]
    public void ParserRejectsNullNestedRequiredStringsAndCollections()
    {
        var bytes = CanonicalDataBytes();
        _ = Parse(bytes);
        var node = JsonNode.Parse(bytes)!.AsObject();
        node["RuntimeHostPath"] = null;
        Reject(EvidenceCanonicalJson.Serialize(node));

        node = JsonNode.Parse(bytes)!.AsObject();
        node["Qualification"]!.AsObject()["ProofReceipts"] = null;
        Reject(EvidenceCanonicalJson.Serialize(node));

        node = JsonNode.Parse(bytes)!.AsObject();
        node["Applications"]!.AsArray()[0]!.AsObject()["Bundle"]!.AsObject()["Files"]!.AsArray()[0]!
            .AsObject()["Path"] = null;
        Reject(EvidenceCanonicalJson.Serialize(node));
    }

    [Fact]
    public void ParserRejectsUnknownDuplicateAliasNoncanonicalAndNestedDigestDrift()
    {
        var raw = Encoding.UTF8.GetString(EvidenceCanonicalJson.Serialize(DataShapeOnly()));
        Reject(Encoding.UTF8.GetBytes(raw.Insert(1, "\"Unknown\":0,")));
        Reject(Encoding.UTF8.GetBytes(raw.Insert(1, "\"provider\":\"github-actions\",")));
        Reject(Encoding.UTF8.GetBytes(raw.Insert(1, "\"Schema\":\"evidence-protected-support-v3\",")));
        Reject(Encoding.UTF8.GetBytes(raw.Replace("\"Length\":3", "\"Length\":3,\"Unknown\":1", StringComparison.Ordinal)));
        var digestDrift = JsonNode.Parse(raw)!.AsObject();
        digestDrift["Tool"]!.AsObject()["Sha256"] = Hex('9');
        Reject(EvidenceCanonicalJson.Serialize(digestDrift));
        Reject(Encoding.UTF8.GetBytes(raw.Replace("{", "{ ", StringComparison.Ordinal)));
        Reject(new byte[EvidenceProtectedSupportRecord.MaximumRecordBytes + 1]);
    }

    [Theory]
    [InlineData("Provider")]
    [InlineData("Protocol")]
    [InlineData("CurrentWorkflowPath")]
    [InlineData("CurrentWorkflowSha256")]
    [InlineData("RepositoryId")]
    [InlineData("BaseRevision")]
    [InlineData("Tool")]
    [InlineData("Runtime")]
    [InlineData("CurrentWorkflowCommit")]
    [InlineData("ApplicationId")]
    [InlineData("Bundle")]
    [InlineData("ProfileIds")]
    [InlineData("ProducerIds")]
    [InlineData("CapabilityIds")]
    [InlineData("CurrentEvent")]
    [InlineData("CurrentRef")]
    [InlineData("CurrentRunId")]
    [InlineData("CurrentRunAttempt")]
    [InlineData("CurrentSubjectRevision")]
    [InlineData("CurrentRefProtected")]
    public void MatcherRejectsMismatchedCurrentProtectedFacts(string field)
    {
        var (record, facts) = DataShapeOnlyAndFacts();
        var wrong = field switch
        {
            "Provider" => facts with { Provider = "other-provider" },
            "Protocol" => facts with { Protocol = "evidence-worker-linux-v3" },
            "CurrentWorkflowPath" => facts with { CurrentWorkflowPath = ".github/workflows/other.yml" },
            "CurrentWorkflowSha256" => facts with { CurrentWorkflowSha256 = Hex('b') },
            "RepositoryId" => facts with { RepositoryId = facts.RepositoryId + 1 },
            "BaseRevision" => facts with { BaseRevision = Hex40('b') },
            "Tool" => facts with { Tool = Changed(facts.Tool) },
            "Runtime" => facts with { Runtime = Changed(facts.Runtime) },
            "CurrentWorkflowCommit" => facts with { CurrentWorkflowCommit = "not-a-commit" },
            "ApplicationId" => facts with { Application = facts.Application with { Id = "other-app" } },
            "Bundle" => facts with { Application = facts.Application with { Bundle = Changed(facts.Application.Bundle) } },
            "ProfileIds" => facts with { ProfileIds = ["unreviewed-profile"] },
            "ProducerIds" => facts with { ProducerIds = ["unreviewed-producer"] },
            "CapabilityIds" => facts with { CapabilityIds = ["unreviewed-capability"] },
            "CurrentEvent" => facts with { Event = "pull_request" },
            "CurrentRef" => facts with { Ref = "refs/heads/feature" },
            "CurrentRunId" => facts with { CurrentRunId = 0 },
            "CurrentRunAttempt" => facts with { CurrentRunAttempt = 0 },
            "CurrentSubjectRevision" => facts with { CurrentSubjectRevision = "not-a-commit" },
            _ => facts with { RefProtected = false },
        };
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, wrong));
    }

    [Fact]
    public void MatcherCanMatchEqualSyntheticDataWithoutMakingAnAcceptanceClaim()
    {
        var (record, facts) = DataShapeOnlyAndFacts();
        Assert.True(EvidenceProtectedSupportMatcher.Matches(record, facts));
        // This is only the data equality branch. The fixture's run, completion fields and hashes are fabricated.
    }

    [Fact]
    public void MatcherAllowsDifferentValidCurrentCommitWhenReviewedWorkflowBytesMatch()
    {
        var (record, facts) = DataShapeOnlyAndFacts();
        var laterCurrentCommit = facts with { CurrentWorkflowCommit = Hex40('c') };

        Assert.True(EvidenceProtectedSupportMatcher.Matches(record, laterCurrentCommit));
    }

    [Fact]
    public void ParserRejectsV2RecordSchema()
    {
        var bytes = CanonicalDataBytes();
        _ = Parse(bytes);
        var oldSchema = JsonNode.Parse(bytes)!.AsObject();
        oldSchema["Schema"] = "evidence-protected-support-v2";
        Reject(EvidenceCanonicalJson.Serialize(oldSchema));
    }

    [Fact]
    public void ParserRejectsRemovedStoredCurrentWorkflowCommitField()
    {
        var bytes = CanonicalDataBytes();
        _ = Parse(bytes);
        var oldStoredField = JsonNode.Parse(bytes)!.AsObject();
        oldStoredField["CurrentWorkflowCommit"] = Hex40('d');
        Reject(EvidenceCanonicalJson.Serialize(oldStoredField));
    }

    [Fact]
    public void EmptyProtectedRegistryNeverMatches()
    {
        var (_, facts) = DataShapeOnlyAndFacts();
        Assert.False(EvidenceProtectedSupportMatcher.MatchesAny(Array.Empty<EvidenceProtectedSupportRecord>(), facts));
    }

    [Fact]
    public void ParserRequiresNonemptyBoundedApplicationAndIdentifierLists()
    {
        var baseline = DataShapeOnly();
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline));

        Reject(EvidenceCanonicalJson.Serialize(baseline with { Applications = Array.Empty<EvidenceSupportApplication>() }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with { ProfileIds = Array.Empty<string>() }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with { ProducerIds = Array.Empty<string>() }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with { CapabilityIds = Array.Empty<string>() }));

        var application = baseline.Applications[0];
        var eightApplications = Enumerable.Range(0, 8)
            .Select(index => application with { Id = $"app-{index:D2}" }).ToArray();
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline with
        { Applications = Array.AsReadOnly(eightApplications) }));
        var nineApplications = Enumerable.Range(0, 9)
            .Select(index => application with { Id = $"app-{index:D2}" }).ToArray();
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Applications = Array.AsReadOnly(nineApplications) }));
        var maximumIds = baseline with
        {
            ProfileIds = Identifiers("profile", 32),
            ProducerIds = Identifiers("producer", 32),
            CapabilityIds = Identifiers("capability", 64),
        };
        _ = Parse(EvidenceCanonicalJson.Serialize(maximumIds));
        Reject(EvidenceCanonicalJson.Serialize(maximumIds with { ProfileIds = Identifiers("profile", 33) }));
        Reject(EvidenceCanonicalJson.Serialize(maximumIds with { ProducerIds = Identifiers("producer", 33) }));
        Reject(EvidenceCanonicalJson.Serialize(maximumIds with { CapabilityIds = Identifiers("capability", 65) }));
    }

    [Fact]
    public void ParserBoundsManifestRowsAndRejectsUnsortedAliasedAndFileDirectoryPrefixPaths()
    {
        var baseline = DataShapeOnly();
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline));

        var maximumFiles = Enumerable.Range(0, 4095)
            .Select(index => SupportFile($"f{index:D4}.dll")).Append(SupportFile("worker.dll")).ToArray();
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline with { Tool = Manifest(maximumFiles) }));
        var oversizedFiles = Enumerable.Range(0, 4096)
            .Select(index => SupportFile($"f{index:D4}.dll")).Append(SupportFile("worker.dll")).ToArray();
        Reject(EvidenceCanonicalJson.Serialize(baseline with { Tool = Manifest(oversizedFiles) }));

        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([SupportFile("worker.dll"), SupportFile("a.dll")]) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([SupportFile("Worker.dll"), SupportFile("worker.dll")]) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([SupportFile("worker.dll"), SupportFile("worker.dll/child.dll")]) }));
    }

    [Fact]
    public void ParserChecksManifestLengthModeAndHashAtTheirValidBoundaries()
    {
        var baseline = DataShapeOnly();
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline));

        var maximumLength = SupportFile("worker.dll", 128L * 1024 * 1024);
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline with { Tool = Manifest([maximumLength]) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([maximumLength with { Length = maximumLength.Length + 1 }]) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([maximumLength with { Length = -1 }]) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([maximumLength with { Mode = 0x1a4 }]) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([maximumLength with { Sha256 = "not-a-sha256" }]) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Tool = Manifest([maximumLength with { Sha256 = Hex('A') }]) }));
    }

    [Fact]
    public void ParserAcceptsAggregateLengthLimitAndRejectsTheNextByte()
    {
        var baseline = DataShapeOnly();
        var files = new[]
        {
            SupportFile("a.bin", 128L * 1024 * 1024),
            SupportFile("b.bin", 128L * 1024 * 1024),
            SupportFile("c.bin", 128L * 1024 * 1024),
            SupportFile("d.bin", 128L * 1024 * 1024),
            SupportFile("worker.dll", 0),
        };
        // These are manifest length claims only; no corresponding large files are created or read.
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline with { Tool = Manifest(files) }));
        var overLimit = files.Append(SupportFile("z.bin", 1)).ToArray();
        Reject(EvidenceCanonicalJson.Serialize(baseline with { Tool = Manifest(overLimit) }));
    }

    [Fact]
    public void ParserRejectsNullApplicationAndReceiptRowsButAllowsNestedBundleEntryPath()
    {
        var bytes = CanonicalDataBytes();
        _ = Parse(bytes);
        var node = JsonNode.Parse(bytes)!.AsObject();
        node["Applications"]!.AsArray()[0] = null;
        Reject(EvidenceCanonicalJson.Serialize(node));

        node = JsonNode.Parse(bytes)!.AsObject();
        node["Qualification"]!["ProofReceipts"]!.AsArray()[0] = null;
        Reject(EvidenceCanonicalJson.Serialize(node));

        var baseline = DataShapeOnly();
        var bundleEntry = SupportFile("lib/net10.0/app.dll", sha256: Hex('5'));
        var application = baseline.Applications[0] with
        { EntrySha256 = bundleEntry.Sha256, Bundle = Manifest([bundleEntry]) };
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline with
        { Applications = Array.AsReadOnly(new[] { application }) }));
    }

    [Fact]
    public void MatcherRejectsInvalidCurrentCollectionsAndAcceptsOnlyEqualSetsRegardlessOfOrder()
    {
        var (record, facts) = DataShapeOnlyAndFacts();
        Assert.True(EvidenceProtectedSupportMatcher.Matches(record, facts));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, null!));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, facts with { Application = null! }));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, facts with { Tool = null! }));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, facts with { ProfileIds = null! }));
        var reordered = facts with
        {
            ProfileIds = facts.ProfileIds.Reverse().ToArray(),
            ProducerIds = facts.ProducerIds.Reverse().ToArray(),
            CapabilityIds = facts.CapabilityIds.Reverse().ToArray(),
        };
        Assert.True(EvidenceProtectedSupportMatcher.Matches(record, reordered));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, facts with { ProfileIds = ["different-profile"] }));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, facts with
        { CapabilityIds = Identifiers("capability", 65) }));
    }

    [Fact]
    public void MatchesAnyAcceptsItsLimitAndRejectsOversizeOrNullEntries()
    {
        var (record, facts) = DataShapeOnlyAndFacts();
        Assert.True(EvidenceProtectedSupportMatcher.MatchesAny(
            Enumerable.Repeat(record, EvidenceProtectedSupportMatcher.MaximumRecords).ToArray(), facts));
        Assert.False(EvidenceProtectedSupportMatcher.MatchesAny(
            Enumerable.Repeat(record, EvidenceProtectedSupportMatcher.MaximumRecords + 1).ToArray(), facts));
        Assert.False(EvidenceProtectedSupportMatcher.MatchesAny(new EvidenceProtectedSupportRecord[] { null! }, facts));
        Assert.False(EvidenceProtectedSupportMatcher.MatchesAny(null!, facts));
        Assert.False(EvidenceProtectedSupportMatcher.MatchesAny([record], null!));
    }

    [Fact]
    public void ParserAcceptsMaximumQualificationReceiptCountAndRejectsEmptyOrOversizedLists()
    {
        var baseline = DataShapeOnly();
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Qualification = baseline.Qualification with { ProofReceipts = Array.Empty<EvidenceSupportReceipt>() } }));

        var maximum = Enumerable.Range(0, 32)
            .Select(index => new EvidenceSupportReceipt($"receipt-{index:D2}", Hex('f'))).ToArray();
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline with
        { Qualification = baseline.Qualification with { ProofReceipts = Array.AsReadOnly(maximum) } }));
        var oversized = Enumerable.Range(0, 33)
            .Select(index => new EvidenceSupportReceipt($"receipt-{index:D2}", Hex('f'))).ToArray();
        Reject(EvidenceCanonicalJson.Serialize(baseline with
        { Qualification = baseline.Qualification with { ProofReceipts = Array.AsReadOnly(oversized) } }));
    }

    [Fact]
    public void ParserEnforcesEmptyAndRelativePathLengthAndUtf8Bounds()
    {
        var baseline = DataShapeOnly();
        Reject(EvidenceCanonicalJson.Serialize(baseline with { Tool = Manifest(Array.Empty<EvidenceSupportFile>()) }));
        _ = Parse(EvidenceCanonicalJson.Serialize(baseline with { Tool = ManifestIncludingWorker(new string('a', 4095)) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with { Tool = ManifestIncludingWorker(new string('a', 4096)) }));
        Reject(EvidenceCanonicalJson.Serialize(baseline with { Tool = ManifestIncludingWorker(new string('é', 2048)) }));

        foreach (var invalidPath in new[] { "", "/absolute", "../escape", "dir/../escape", "dir\\escape", "dir//file", "dir/./file", "\u0001bad" })
            Reject(EvidenceCanonicalJson.Serialize(baseline with { Tool = ManifestIncludingWorker(invalidPath) }));
    }

    [Fact]
    public void MatcherComparesCurrentIdentifierSetsByContentsAndSize()
    {
        var (_, baseFacts) = DataShapeOnlyAndFacts();
        var data = DataShapeOnly() with
        {
            ProfileIds = Identifiers("profile", 2),
            ProducerIds = Identifiers("producer", 2),
            CapabilityIds = Identifiers("capability", 2),
        };
        var record = Parse(EvidenceCanonicalJson.Serialize(data));
        var reverseOrder = baseFacts with
        {
            ProfileIds = data.ProfileIds.Reverse().ToArray(),
            ProducerIds = data.ProducerIds.Reverse().ToArray(),
            CapabilityIds = data.CapabilityIds.Reverse().ToArray(),
        };
        Assert.True(EvidenceProtectedSupportMatcher.Matches(record, reverseOrder));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, reverseOrder with
        { ProfileIds = [data.ProfileIds[0]] }));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, reverseOrder with
        { ProducerIds = [data.ProducerIds[0], "different-producer"] }));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, reverseOrder with { ProducerIds = null! }));
        Assert.False(EvidenceProtectedSupportMatcher.Matches(record, reverseOrder with { CapabilityIds = null! }));
    }

    private static void Reject(byte[] bytes) => Assert.Throws<FormatException>(() => Parse(bytes));
    private static EvidenceProtectedSupportRecord Parse(byte[] bytes) =>
        EvidenceProtectedSupportRecord.Parse(bytes, EvidenceDigest.Sha256(bytes));
    private static byte[] CanonicalDataBytes() => EvidenceCanonicalJson.Serialize(DataShapeOnly());
    private static void SetJson(JsonObject root, string path, JsonNode? value)
    {
        var members = path.Split('.');
        var target = root;
        for (var index = 0; index < members.Length - 1; index++) target = target[members[index]]!.AsObject();
        target[members[^1]] = value;
    }
    private static EvidenceSupportManifest Changed(EvidenceSupportManifest value) => value with { Sha256 = Hex('9') };
    private static string Hex(char value) => new(value, 64);
    private static string Hex40(char value) => new(value, 40);

    // Synthetic, self-consistent bytes support parser/equality boundary tests only; these are not qualification data.
    private static EvidenceProtectedSupportRecordData DataShapeOnly()
    {
        var tool = Manifest(new EvidenceSupportFile("worker.dll", 0x124, 3, Hex('1')));
        var runtime = Manifest(new EvidenceSupportFile("dotnet", 0x16d, 3, Hex('2')));
        var bundle = Manifest(new EvidenceSupportFile("app.dll", 0x124, 3, Hex('3')));
        return new EvidenceProtectedSupportRecordData
        {
            Schema = EvidenceProtectedSupportRecord.RecordSchema,
            Provider = "github-actions",
            Platform = "linux-x64",
            Protocol = "evidence-worker-linux-v2",
            RepositoryName = EvidenceProtectedSupportRecord.ProtectedRepository,
            RepositoryId = 42,
            ProtectedBaseRevision = Hex40('7'),
            CurrentWorkflowPath = ".github/workflows/evidencehost-protected-parent.yml",
            CurrentWorkflowSha256 = Hex('6'),
            Tool = tool,
            Runtime = runtime,
            RuntimeHostPath = "dotnet",
            RuntimeHostSha256 = Hex('2'),
            EntryAssemblyPath = "worker.dll",
            EntryAssemblySha256 = Hex('1'),
            Applications = Array.AsReadOnly(new[] { new EvidenceSupportApplication("app", "1.0", "build-1",
                Hex('4'), Hex('5'), bundle) }),
            ProfileIds = ["profile-1"],
            ProducerIds = ["producer-1"],
            CapabilityIds = ["cli"],
            Qualification = new EvidenceQualificationEvidence(42, 99, 1,
                EvidenceProtectedSupportRecord.QualificationWorkflowPath, Hex('a'), Hex40('8'),
                "workflow_dispatch", EvidenceProtectedSupportRecord.ProtectedRef, "completed", "success",
                Hex40('8'), Hex('b'), Hex40('c'), "artifact-2", Hex('e'),
                Array.AsReadOnly(new[] { new EvidenceSupportReceipt("data-only-placeholder", Hex('f')) })),
        };
    }

    private static (EvidenceProtectedSupportRecord Record, EvidenceProtectedSupportFacts Facts) DataShapeOnlyAndFacts()
    {
        var data = DataShapeOnly();
        var bytes = EvidenceCanonicalJson.Serialize(data);
        var record = EvidenceProtectedSupportRecord.Parse(bytes, EvidenceDigest.Sha256(bytes));
        var app = record.Data.Applications[0];
        var facts = new EvidenceProtectedSupportFacts("github-actions", "linux-x64", "evidence-worker-linux-v2",
            EvidenceProtectedSupportRecord.ProtectedRepository, 42, "push", EvidenceProtectedSupportRecord.ProtectedRef,
            true, record.Data.CurrentWorkflowPath, record.Data.CurrentWorkflowSha256, record.Data.ProtectedBaseRevision,
            100, 2, Hex40('d'), Hex40('f'), record.Data.Tool, record.Data.Runtime, record.Data.RuntimeHostPath,
            record.Data.RuntimeHostSha256, record.Data.EntryAssemblyPath, record.Data.EntryAssemblySha256, app,
            record.Data.ProfileIds, record.Data.ProducerIds, record.Data.CapabilityIds);
        return (record, facts);
    }

    private static EvidenceSupportManifest Manifest(EvidenceSupportFile file)
    {
        var files = Array.AsReadOnly(new[] { file });
        return new EvidenceSupportManifest(EvidenceDigest.CanonicalSha256(files), files);
    }

    private static EvidenceSupportManifest Manifest(IReadOnlyList<EvidenceSupportFile> files)
    {
        var copy = Array.AsReadOnly(files.ToArray());
        return new EvidenceSupportManifest(EvidenceDigest.CanonicalSha256(copy), copy);
    }

    private static EvidenceSupportFile SupportFile(string path, long length = 3, int mode = 0x124, string? sha256 = null) =>
        new(path, mode, length, sha256 ?? Hex('1'));

    private static EvidenceSupportManifest ManifestIncludingWorker(string additionalPath) =>
        Manifest(new[] { SupportFile(additionalPath), SupportFile("worker.dll") }
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());

    private static IReadOnlyList<string> Identifiers(string prefix, int count) =>
        Array.AsReadOnly(Enumerable.Range(0, count).Select(index => $"{prefix}-{index:D3}").ToArray());
}
