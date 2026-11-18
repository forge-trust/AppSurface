using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInIOTests
{
    [Fact]
    public void RejectedBudgetAdmissionsPreserveAllPreviouslyAcceptedCounters()
    {
        var budget = new CoverageFanInBudget(10);
        budget.AdmitInput(2, xml: true, json: false);
        budget.AdmitInput(3, xml: false, json: true);
        budget.AdmitXmlRecords(4, 5);
        budget.AdmitStagedFile(6);
        var before = budget.Snapshot();

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AdmitInput(-1, false, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AdmitInput(1, true, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AdmitXmlRecords(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AdmitXmlRecords(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.AdmitStagedFile(-1));
        Assert.Throws<CoverageFanInEvidenceException>(() => budget.Charge(-1));
        Assert.Throws<CoverageFanInEvidenceException>(() => budget.AdmitStagedFile(5));

        Assert.Equal(before, budget.Snapshot());
        Assert.Equal(4, budget.Remaining);
    }

    [Fact]
    public void XmlRecordAdmissionEnforcesCombinedLimitAndLeavesCountersUnchangedOnRejection()
    {
        var budget = new CoverageFanInBudget(10, maximumRecords: 4);
        budget.AdmitXmlRecords(1, 1);
        var beforeRejectedAdmission = budget.Snapshot();

        var failure = Assert.Throws<CoverageFanInEvidenceException>(() => budget.AdmitXmlRecords(1, 2));
        Assert.Equal("limit-exceeded", failure.Cause);
        Assert.Equal("budget", failure.Role);
        Assert.Equal(beforeRejectedAdmission, budget.Snapshot());

        budget.AdmitXmlRecords(1, 1);
        Assert.Equal(4, budget.Snapshot().LineRecords + budget.Snapshot().BranchRecords);
        Assert.Throws<CoverageFanInEvidenceException>(() => budget.AdmitXmlRecords(0, 1));
        Assert.Equal(4, budget.Snapshot().LineRecords + budget.Snapshot().BranchRecords);
    }

    [Fact]
    public void XmlRecordLimitSpansIoInstancesButSameInstanceCacheDoesNotReadmit()
    {
        var lineBytes = Encoding.UTF8.GetBytes("<coverage><line /></coverage>");
        var conditionBytes = Encoding.UTF8.GetBytes("<coverage><condition /></coverage>");
        var budget = new CoverageFanInBudget(1000, maximumRecords: 3);
        var bounds = Bounds(records: 3);
        using var firstIo = new CoverageFanInIO(bounds, CancellationToken.None, budget);
        var firstDocument = firstIo.Xml(lineBytes);
        Assert.Same(firstDocument, firstIo.Xml(lineBytes));
        Assert.Equal(1, budget.Snapshot().LineRecords + budget.Snapshot().BranchRecords);

        using var secondIo = new CoverageFanInIO(bounds, CancellationToken.None, budget);
        var secondDocument = secondIo.Xml(lineBytes);
        Assert.NotSame(firstDocument, secondDocument);
        Assert.Same(secondDocument, secondIo.Xml(lineBytes));
        var conditionDocument = secondIo.Xml(conditionBytes);
        Assert.Same(conditionDocument, secondIo.Xml(conditionBytes));
        var atLimit = budget.Snapshot();
        Assert.Equal(3, atLimit.LineRecords + atLimit.BranchRecords);

        using var thirdIo = new CoverageFanInIO(bounds, CancellationToken.None, budget);
        Assert.Equal("limit-exceeded", Assert.Throws<CoverageFanInEvidenceException>(() => thirdIo.Xml(conditionBytes)).Cause);
        Assert.Equal(atLimit, budget.Snapshot());

        // No shared budget retains the legacy per-instance accounting: each IO gets its own configured allowance.
        using var legacyFirstIo = new CoverageFanInIO(Bounds(records: 1), CancellationToken.None);
        using var legacySecondIo = new CoverageFanInIO(Bounds(records: 1), CancellationToken.None);
        _ = legacyFirstIo.Xml(lineBytes);
        _ = legacySecondIo.Xml(lineBytes);
    }

    [Fact]
    public async Task ConcurrentXmlAdmissionsCannotExceedTheSharedRecordLimit()
    {
        var budget = new CoverageFanInBudget(1000, maximumRecords: 1);
        var bounds = Bounds(records: 1);
        using var firstIo = new CoverageFanInIO(bounds, CancellationToken.None, budget);
        using var secondIo = new CoverageFanInIO(bounds, CancellationToken.None, budget);
        var firstBytes = Encoding.UTF8.GetBytes("<coverage><line /></coverage>");
        var secondBytes = Encoding.UTF8.GetBytes("<coverage><condition /></coverage>");
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();

        Task<bool> Admit(CoverageFanInIO io, byte[] bytes) => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            try { _ = io.Xml(bytes); return true; }
            catch (CoverageFanInEvidenceException exception) when (exception.Cause == "limit-exceeded") { return false; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var first = Admit(firstIo, firstBytes);
        var second = Admit(secondIo, secondBytes);
        ready.Wait();
        start.Set();
        var admitted = await Task.WhenAll(first, second);

        Assert.NotEqual(admitted[0], admitted[1]);
        var snapshot = budget.Snapshot();
        Assert.Equal(1, snapshot.LineRecords + snapshot.BranchRecords);
    }

    [Theory]
    [InlineData("<coverage><line/><line/></coverage>", 1, 20, 40)]
    [InlineData("<coverage><condition/><condition/></coverage>", 20, 1, 40)]
    [InlineData("<coverage><line/><condition/></coverage>", 20, 20, 1)]
    public void V3XmlRecordBoundsReportTheBudgetCause(string xml, int lines, int branches, int records)
    {
        var budget = new CoverageFanInBudget(1000, maximumRecords: records);
        using var io = new CoverageFanInIO(Bounds(lines: lines, branches: branches, records: records), CancellationToken.None, budget);

        var failure = Assert.Throws<CoverageFanInEvidenceException>(() => io.Xml(Encoding.UTF8.GetBytes(xml)));

        Assert.Equal("limit-exceeded", failure.Cause);
        Assert.Equal("budget", failure.Role);
        Assert.Equal(0, budget.Snapshot().LineRecords + budget.Snapshot().BranchRecords);
    }

    [Fact]
    public void GeneratedOutputReadbackRequiresExactPathAndIdentityAndConsumesCreditOnce()
    {
        var budget = new CoverageFanInBudget(20);
        budget.ChargeGeneratedOutput("report.xml", "identity-1", 4);
        budget.ChargeGeneratedOutput("report.xml", "identity-1", 3);

        Assert.Equal(0, budget.ConsumeGeneratedOutputReadback("other.xml", "identity-1", 7));
        Assert.Equal(0, budget.ConsumeGeneratedOutputReadback("report.xml", "identity-2", 7));
        Assert.Equal(5, budget.ConsumeGeneratedOutputReadback("report.xml", "identity-1", 5));
        Assert.Equal(0, budget.ConsumeGeneratedOutputReadback("report.xml", "identity-1", 5));
        Assert.Equal(13, budget.Remaining);
    }

    [Fact]
    public void V3ProcessesRetainedInputsAndStagingUseOneBudgetWithoutChargingCachedReadsTwice()
    {
        using var temp = TestDirectory.Create();
        var input = temp.CreateDirectory("input");
        var file = Path.Join(input, "facts.json");
        File.WriteAllBytes(file, new byte[10]);
        var xmlBytes = Encoding.UTF8.GetBytes("<coverage><line><condition /></line></coverage>");
        var xmlPath = Path.Join(input, "coverage.xml");
        File.WriteAllBytes(xmlPath, xmlBytes);
        var otherInput = temp.CreateDirectory("other-input");
        File.WriteAllBytes(Path.Join(otherInput, "more.json"), new byte[3]);
        var expectedUsed = 5 + 10 + xmlBytes.Length + 3 + 10 + 5;
        var budget = new CoverageFanInBudget(expectedUsed);
        budget.Charge(5); // Prior bounded Git probe, including its streams.
        using var io = new CoverageFanInIO(Bounds(total: 100), CancellationToken.None, budget);
        io.Acquire([(input, false)], requireCoordination: false);
        Assert.Equal(10, io.Read(file).Length);
        Assert.Equal(10, io.Read(file).Length);
        Assert.Equal(xmlBytes.Length, io.Read(xmlPath, xml: true).Length);
        Assert.Same(io.Xml(xmlBytes), io.Xml(xmlBytes));
        Assert.Equal(expectedUsed - 5 - 10 - xmlBytes.Length, budget.Remaining);

        using var secondIo = new CoverageFanInIO(Bounds(total: 100), CancellationToken.None, budget);
        secondIo.Acquire([(otherInput, false)], requireCoordination: false);
        Assert.Equal(3, secondIo.Read(Path.Join(otherInput, "more.json")).Length);
        Assert.Equal(expectedUsed - 5 - 10 - xmlBytes.Length - 3, budget.Remaining);

        io.AdmitOutput([new byte[10]]); Assert.Equal(5, budget.Remaining);
        Assert.Equal("limit-exceeded", Assert.Throws<CoverageFanInEvidenceException>(() => io.AdmitMetadata(6)).Cause);
        Assert.Equal(5, budget.Remaining);
        io.AdmitMetadata(5); Assert.Equal(0, budget.Remaining);
        Assert.Equal(new CoverageFanInBudgetSnapshot(expectedUsed, expectedUsed, xmlBytes.Length, 13, 1, 1, 15, 2), budget.Snapshot());
        io.VerifyBindings();
        secondIo.VerifyBindings();
    }

    private static CoverageFanInBounds Bounds(long xml = 10000, long json = 1000, long total = 100000,
        int depth = 20, int lines = 20, int elements = 100, int attributes = 10, int token = 1000,
        int branches = 20, int records = 40) => new(xml, json, total, depth, lines, elements, attributes, token, branches, records);

    [Fact]
    public void ReaderUsesRetainedNativeFilesAndEnforcesExactByteBoundaries()
    {
        using var temp = TestDirectory.Create();
        var directory = temp.CreateDirectory("input");
        File.WriteAllText(Path.Join(directory, "exact.json"), "0123456789");
        File.WriteAllText(Path.Join(directory, "second.json"), "0123456789");
        using var io = new CoverageFanInIO(Bounds(json: 10, total: 19), CancellationToken.None);
        io.Acquire([(directory, false)], requireCoordination: false);
        Assert.Equal("0123456789", Encoding.UTF8.GetString(io.Read(Path.Join(directory, "exact.json"))));
        Assert.Equal("0123456789", Encoding.UTF8.GetString(io.Read(Path.Join(directory, "exact.json"))));
        Assert.Throws<InvalidDataException>(() => io.Read(Path.Join(directory, "second.json")));
        Assert.Throws<InvalidOperationException>(() => io.Read(Path.Join(temp.Path, "outside.json")));
        io.VerifyBindings();
    }

    [Theory]
    [InlineData("diff.patch", 20 * 1024 * 1024, 21 * 1024 * 1024, true)]
    [InlineData("diff.patch", 20 * 1024 * 1024 + 1, 21 * 1024 * 1024, false)]
    [InlineData("diff.patch", 1024 * 1024 + 1, 1024 * 1024, false)]
    [InlineData("other.patch", 1024 * 1024 + 1, 21 * 1024 * 1024, false)]
    public void RetainedDiffUsesExactGitByteLimitAndStillChargesTotalBudget(string name, int length,
        int total, bool accepted)
    {
        using var temp = TestDirectory.Create();
        var input = temp.CreateDirectory("input");
        var path = Path.Join(input, name);
        using (var file = File.Create(path)) file.SetLength(length);
        using var io = new CoverageFanInIO(Bounds(json: 1024 * 1024, total: total), CancellationToken.None);
        io.Acquire([(input, false)], requireCoordination: false);

        if (accepted)
        {
            Assert.Equal(length, io.Read(path).Length);
            io.VerifyBindings();
        }
        else Assert.Throws<InvalidDataException>(() => io.Read(path));
    }

    [Theory]
    [InlineData("<a><b><c /></b></a>", 1, 20, 100, 10, 1000, 20, 40)]
    [InlineData("<a><line/><line/></a>", 20, 1, 100, 10, 1000, 20, 40)]
    [InlineData("<a><b/><c/></a>", 20, 20, 2, 10, 1000, 20, 40)]
    [InlineData("<a x='1' y='2'/>", 20, 20, 100, 1, 1000, 20, 40)]
    [InlineData("<a x='1234'/>", 20, 20, 100, 10, 3, 20, 40)]
    [InlineData("<a><condition/><condition/></a>", 20, 20, 100, 10, 1000, 1, 40)]
    [InlineData("<a><line/><condition/></a>", 20, 20, 100, 10, 1000, 20, 1)]
    public void XmlAdmissionRejectsStructureBeforeMaterializing(string xml, int depth, int lines, int elements,
        int attributes, int token, int branches, int records)
    {
        using var io = new CoverageFanInIO(Bounds(depth: depth, lines: lines, elements: elements,
            attributes: attributes, token: token, branches: branches, records: records), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => io.Xml(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void XmlWireRejectsDtdAndControlledNormalizationRejectsInternalEntities()
    {
        using var io = new CoverageFanInIO(Bounds(), CancellationToken.None);
        Assert.Throws<System.Xml.XmlException>(() => io.Xml(Encoding.UTF8.GetBytes(
            "<!DOCTYPE coverage SYSTEM 'http://cobertura.sourceforge.net/xml/coverage-04.dtd'><coverage/>")));
        Assert.Throws<InvalidDataException>(() => io.Xml(Encoding.UTF8.GetBytes(
            "<!DOCTYPE coverage [<!ENTITY x 'x'>]><coverage/>"), allowInertGeneratorDoctype: true));
        Assert.Throws<InvalidDataException>(() => io.Xml(Encoding.UTF8.GetBytes(
            "<!DOCTYPE coverage SYSTEM 'https://untrusted.invalid/arbitrary.dtd'><coverage/>"), allowInertGeneratorDoctype: true));
        Assert.Null(io.Xml(Encoding.UTF8.GetBytes(
            "<!DOCTYPE coverage SYSTEM 'http://cobertura.sourceforge.net/xml/coverage-04.dtd'><coverage/>"),
            allowInertGeneratorDoctype: true).DocumentType);
    }

    [Fact]
    public void ReaderRefusesLinkedFilesWithoutFollowingThem()
    {
        if (OperatingSystem.IsWindows()) return; // Privileged symlink creation is exercised in native Windows lanes.
        using var temp = TestDirectory.Create();
        var input = temp.CreateDirectory("input");
        var outside = Path.Join(temp.Path, "outside.json"); File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Join(input, "linked.json"), outside);
        using var io = new CoverageFanInIO(Bounds(), CancellationToken.None);
        io.Acquire([(input, false)], requireCoordination: false);
        Assert.Throws<IOException>(() => io.Read(Path.Join(input, "linked.json")));
        Assert.Equal("secret", File.ReadAllText(outside));
    }

    [Fact]
    public void ReadOnlyLeasesContendWithWritersAndReleaseAfterCancellation()
    {
        using var temp = TestDirectory.Create();
        var root = temp.CreateDirectory("owned");
        // Writer provisions coordination on Windows; read-only IO must never create it.
        using (var writer = CoverageArtifactOperationLease.Acquire(root, true)) { }
        using (var io = new CoverageFanInIO(Bounds(), CancellationToken.None))
        {
            io.Acquire([(root, false)]);
            Assert.Throws<CoverageExecutionException>(() => CoverageArtifactOperationLease.Acquire(root, true));
        }
        using var next = CoverageArtifactOperationLease.Acquire(root, true);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var cancelled = new CoverageFanInIO(Bounds(), cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => cancelled.Acquire([(root, false)]));
    }

    [Fact]
    public void FailedMultiRootAcquisitionReleasesEarlierLeasesAndRetainsContendedLease()
    {
        using var temp = TestDirectory.Create();
        var first = temp.CreateDirectory("first");
        var second = temp.CreateDirectory("second");
        string firstIdentity;
        string secondIdentity;
        using (var firstRoot = CoverageRunOutputLease.AcquireArtifactDirectory(first, false, false)!)
            firstIdentity = firstRoot.ArtifactDirectoryIdentity;
        using (var secondRoot = CoverageRunOutputLease.AcquireArtifactDirectory(second, false, false)!)
            secondIdentity = secondRoot.ArtifactDirectoryIdentity;

        var acquiredFirst = StringComparer.Ordinal.Compare(firstIdentity, secondIdentity) < 0 ? first : second;
        var contendedSecond = acquiredFirst == first ? second : first;
        var contendedIdentity = acquiredFirst == first ? secondIdentity : firstIdentity;
        using var competingWriter = CoverageArtifactOperationLease.Acquire(contendedSecond, true);
        using var io = new CoverageFanInIO(Bounds(), CancellationToken.None);

        Assert.Throws<CoverageExecutionException>(() => io.Acquire([(first, false), (second, false)]));

        using var releasedWriter = CoverageArtifactOperationLease.Acquire(acquiredFirst, true);
        Assert.NotEqual(contendedIdentity, releasedWriter.PhysicalIdentity);
        Assert.Equal(contendedIdentity, competingWriter.PhysicalIdentity);
    }

    [Fact]
    public void MetadataAndStagingShareTheAlreadyAdmittedInputBudget()
    {
        using var temp = TestDirectory.Create();
        var root = temp.CreateDirectory("input");
        File.WriteAllText(Path.Join(root, "input.json"), "1234");
        using var io = new CoverageFanInIO(Bounds(total: 10), CancellationToken.None);
        io.Acquire([(root, false)], requireCoordination: false);
        _ = io.Read(Path.Join(root, "input.json"));
        io.AdmitOutput([new byte[5]]);
        io.AdmitMetadata(1);
        Assert.Throws<InvalidDataException>(() => io.AdmitMetadata(1));
    }

    [Fact]
    public void XmlOutputChecksExactByteBoundaryRemainingInvocationBudgetAndCancellation()
    {
        var document = System.Xml.Linq.XDocument.Parse("<coverage><packages/></coverage>");
        byte[] expected;
        using (var normal = new CoverageFanInIO(Bounds(), CancellationToken.None))
            expected = normal.EncodeXml(document);
        using (var exact = new CoverageFanInIO(Bounds(xml: expected.Length), CancellationToken.None))
            Assert.Equal(expected, exact.EncodeXml(document));
        using (var over = new CoverageFanInIO(Bounds(xml: expected.Length - 1), CancellationToken.None))
            Assert.Throws<InvalidDataException>(() => over.EncodeXml(document));
        using (var total = new CoverageFanInIO(Bounds(total: expected.Length), CancellationToken.None))
        {
            total.AdmitMetadata(1);
            Assert.Throws<InvalidDataException>(() => total.EncodeXml(document));
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelled = new CoverageFanInIO(Bounds(), cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => cancelled.EncodeXml(document));
    }

    [Fact]
    public void XmlOutputMustFitTheSameStructureAndOperationRecordLimitsAsInputs()
    {
        var document = System.Xml.Linq.XDocument.Parse("<coverage><line/><line/></coverage>");
        using var limited = new CoverageFanInIO(Bounds(lines: 1), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => limited.EncodeXml(document));
        using var exact = new CoverageFanInIO(Bounds(lines: 2, records: 2), CancellationToken.None);
        var bytes = exact.EncodeXml(document);
        Assert.Same(exact.Xml(bytes), exact.Xml(bytes));
        Assert.Throws<InvalidDataException>(() => exact.EncodeXml(document));
    }

    [Fact]
    public void AdmissionRefusesOversizedBytesRetainedDtdReuseAndUnknownInventory()
    {
        using var temp = TestDirectory.Create();
        var input = temp.CreateDirectory("input");
        var path = Path.Join(input, "payload.json");
        File.WriteAllText(path, "12345");
        using (var limited = new CoverageFanInIO(Bounds(json: 4), CancellationToken.None))
        {
            limited.Acquire([(input, false)], requireCoordination: false);
            Assert.Throws<InvalidDataException>(() => limited.Read(path));
            Assert.Throws<InvalidOperationException>(() => limited.Acquire([(input, false)], requireCoordination: false));
            Assert.Throws<InvalidDataException>(() => limited.ExactInventory(input, ["required.json"]));
        }
        using (var alias = new CoverageFanInIO(Bounds(), CancellationToken.None))
            Assert.Throws<InvalidDataException>(() => alias.Acquire([(input, false), (input, false)], requireCoordination: false));
        using (var missing = new CoverageFanInIO(Bounds(), CancellationToken.None))
            Assert.Throws<IOException>(() => missing.Acquire([(Path.Join(temp.Path, "missing"), false)], requireCoordination: false));
        using var io = new CoverageFanInIO(Bounds(), CancellationToken.None);
        var inert = Encoding.UTF8.GetBytes("<!DOCTYPE coverage SYSTEM 'http://cobertura.sourceforge.net/xml/coverage-04.dtd'><coverage/>");
        Assert.Null(io.Xml(inert, allowInertGeneratorDoctype: true).DocumentType);
        Assert.Throws<InvalidDataException>(() => io.Xml(inert));
        using var tiny = new CoverageFanInIO(Bounds(xml: 2), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => tiny.Xml(Encoding.UTF8.GetBytes("<coverage/>")));
    }

    [Theory]
    [InlineData("<a>long text</a>")]
    [InlineData("<a value='long text' />")]
    public void XmlTextAndAttributeTokenLimitsRejectBeforeMaterialization(string xml)
    {
        using var io = new CoverageFanInIO(Bounds(token: 3), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => io.Xml(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void OutputCustodyRejectsRepeatedBindingInputAliasesAndMissingParents()
    {
        using var temp = TestDirectory.Create();
        var input = temp.CreateDirectory("input");
        var output = Path.Join(temp.Path, "output");
        using var io = new CoverageFanInIO(Bounds(), CancellationToken.None);
        io.Acquire([(input, false)], requireCoordination: false);
        Assert.Throws<InvalidDataException>(() => io.BindOutput(input, false, () => { }));
        Assert.Throws<DirectoryNotFoundException>(() => io.BindOutput(Path.Join(temp.Path, "missing", "output"), false, () => { }));
        var created = 0;
        var lease = io.BindOutput(output, false, () => created++);
        Assert.True(lease.IsExclusive);
        Assert.Equal(1, created);
        Assert.Throws<InvalidOperationException>(() => io.BindOutput(Path.Join(temp.Path, "other"), false, () => { }));
        io.VerifyBindings();
    }

    [Fact]
    public void AcquiringOutputCustodyCannotAdoptOrEraseOccupiedOutput()
    {
        using var temp = TestDirectory.Create();
        var output = Path.Join(temp.Path, "output");
        using (CoverageRunOutputLease.AcquireArtifactDirectory(output, true, true)) { }
        var path = Path.Join(output, "foreign.json"); File.WriteAllText(path, "preserve");
        using var io = new CoverageFanInIO(Bounds(), CancellationToken.None);
        Assert.Throws<IOException>(() => io.BindOutput(output, false, () => throw new InvalidOperationException()));
        var lease = io.BindOutput(output, true, () => throw new InvalidOperationException());
        Assert.True(lease.IsExclusive);
        Assert.Equal("preserve", File.ReadAllText(path));
    }

    [Fact]
    public void RetainedStreamHashRejectsGrowthAndHonorsCancellation()
    {
        using var exact = new MemoryStream("1234"u8.ToArray());
        Assert.Equal(CoverageFanInIO.Hash("1234"u8.ToArray()), CoverageFanInIO.HashStream(exact, 4, CancellationToken.None));
        exact.Position = 0;
        Assert.Throws<InvalidDataException>(() => CoverageFanInIO.HashStream(exact, 3, CancellationToken.None));
        exact.Position = 0;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CoverageFanInIO.HashStream(exact, 4, cancelled.Token));
        using var io = new CoverageFanInIO(Bounds(total: 3), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => io.AdmitOutput([new byte[4]]));
    }

    [Fact]
    public void RetainedInputNewObjectOrTruncationCannotReuseItsAdmittedBytes()
    {
        if (OperatingSystem.IsWindows()) return; // Windows denies the competing writer while handles are retained.
        using var temp = TestDirectory.Create();
        var root = temp.CreateDirectory("input");
        var path = Path.Join(root, "input.json"); File.WriteAllText(path, "original");
        using (var io = new CoverageFanInIO(Bounds(), CancellationToken.None))
        {
            io.Acquire([(root, false)], requireCoordination: false);
            _ = io.Read(path);
            File.Move(path, Path.Join(root, "retained-original.json"));
            File.WriteAllText(path, "original");
            Assert.Throws<IOException>(io.VerifyBindings);
        }
        using var truncated = new CoverageFanInIO(Bounds(), CancellationToken.None);
        truncated.Acquire([(root, false)], requireCoordination: false);
        _ = truncated.Read(path);
        File.WriteAllText(path, "short");
        Assert.Throws<IOException>(truncated.VerifyBindings);
    }

    [Fact]
    public void RetainedInputReplacementOrSameObjectModificationFailsFinalBindingChecks()
    {
        using var temp = TestDirectory.Create();
        var root = temp.CreateDirectory("input");
        var path = Path.Join(root, "input.json"); File.WriteAllText(path, "original");
        using var io = new CoverageFanInIO(Bounds(), CancellationToken.None);
        io.Acquire([(root, false)], requireCoordination: false);
        _ = io.Read(path);
        if (OperatingSystem.IsWindows())
        {
            // The native reader disallows write sharing. A foreign writer cannot modify this retained file.
            Assert.Throws<IOException>(() => File.WriteAllText(path, "modified"));
            io.VerifyBindings();
        }
        else
        {
            File.WriteAllText(path, "modified");
            Assert.Throws<IOException>(io.VerifyBindings);
        }
    }
}
