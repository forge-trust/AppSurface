using ForgeTrust.AppSurface.Testing;
using HangReader = ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunHangDiagnosticsReader;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunHangDiagnosticsReaderTests
{
    [Fact]
    public void Inspect_ReturnsSafeLastStartedNameAndPathRelativeToOutputRoot()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/host-a/Sequence.xml", "<TestSequence><Test Name=\"Namespace.Test+Case_1\" Source=\"/private/host/path.dll\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("found", result.Status);
        var observation = Assert.Single(result.Sequences);
        Assert.Equal("results/host-a/Sequence.xml", observation.RelativePath);
        Assert.Equal("Namespace.Test+Case_1", observation.LastStartedTest);
        Assert.DoesNotContain("private", observation.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Sequence_07a9f5f3-76e3-42c3-9b83-cdbf16953823.xml")]
    [InlineData("07a9f5f3-76e3-42c3-9b83-cdbf16953823_Sequence.xml")]
    public void Inspect_RecognizesVstestGuidSequenceNames(string fileName)
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/host/" + fileName,
            "<TestSequence><Test Name=\"Sample.HangingTest\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("found", result.Status);
        Assert.Equal("Sample.HangingTest", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Fact]
    public void Inspect_UsesTheLastDirectStartedTestAndIgnoresNestedMetadata()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/SEQUENCE.XML",
            "<TestSequence><Test Name=\"First.Test\" />"
            + "<Metadata><Test Name=\"Nested.Spoof\" /></Metadata>"
            + "<Test Name=\"Last.Test\"></Test></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("found", result.Status);
        Assert.Equal("Last.Test", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Fact]
    public void Inspect_IgnoresTestUnderNestedTestSequenceAfterDirectTest()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml",
            "<TestSequence><Test Name=\"Direct.Test\" />"
            + "<Metadata><TestSequence><Test Name=\"Nested.Spoof\" /></TestSequence></Metadata>"
            + "</TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("found", result.Status);
        Assert.Equal("Direct.Test", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Theory]
    [InlineData("<TestSequence xmlns=\"urn:spoof\"><Test Name=\"Spoof\" /></TestSequence>")]
    [InlineData("<TestSequence><Test Name=\"Spoof\" /></TestSequence><Unexpected />")]
    public void Inspect_RejectsNonVstestSequenceDocument(string xml)
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", xml);

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("malformed", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_LeavesLastStartedNameEmptyWhenSequenceHasNoStartedTest()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", "<TestSequence><Metadata /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("name-omitted", result.Status);
        Assert.Null(Assert.Single(result.Sequences).LastStartedTest);
    }

    [Theory]
    [InlineData("1StartsWithDigit")]
    [InlineData("Contains Space")]
    [InlineData("Contains/Slash")]
    [InlineData("Contains\u202eBidi")]
    public void Inspect_OmitsNamesOutsideTheDisplayIdentifierAllowlist(string name)
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", $"<TestSequence><Test Name=\"{name}\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("name-omitted", result.Status);
        Assert.Null(Assert.Single(result.Sequences).LastStartedTest);
    }

    [Fact]
    public void Inspect_AcceptsUnderscoreAndMaximumLengthIdentifier()
    {
        using var fixture = new Fixture();
        var name = "_" + new string('A', HangReader.MaximumNameLength - 1);
        fixture.Write("output/results/Sequence.xml", $"<TestSequence><Test Name=\"{name}\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("found", result.Status);
        Assert.Equal(name, Assert.Single(result.Sequences).LastStartedTest);
    }

    [Theory]
    [InlineData("<TestSequence><Test Name=\"Good\"></TestSequence>", "malformed")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><TestSequence><Test Name=\"&e;\" /></TestSequence>", "malformed")]
    public void Inspect_RejectsMalformedOrDtdXml(string xml, string expectedStatus)
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", xml);

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_RejectsUnexpectedDocumentRoot()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", "<Other><TestSequence><Test Name=\"Spoofed\" /></TestSequence></Other>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("malformed", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_LimitsXmlDepth()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml",
            "<TestSequence>" + string.Concat(Enumerable.Repeat("<A>", HangReader.MaximumXmlDepth))
            + "<Test Name=\"Deep\" />"
            + string.Concat(Enumerable.Repeat("</A>", HangReader.MaximumXmlDepth)) + "</TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("malformed", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_LimitsTotalTestEntries()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml",
            "<TestSequence>" + string.Concat(Enumerable.Repeat("<Test Name=\"Safe\" />", HangReader.MaximumTests + 1))
            + "</TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output, new FrozenClock());

        Assert.Equal("inspection-limited", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_OmitsUnsafeNamesButRetainsScopedPath()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", "<TestSequence><Test Name=\"../secret\\nInjected\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("name-omitted", result.Status);
        Assert.Null(Assert.Single(result.Sequences).LastStartedTest);
    }

    [Fact]
    public void Inspect_OmitsAbsentNames()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", "<TestSequence><Test /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("name-omitted", result.Status);
        Assert.Null(Assert.Single(result.Sequences).LastStartedTest);
    }

    [Fact]
    public void Inspect_OmitsNamesLongerThanDisplayLimit()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml",
            $"<TestSequence><Test Name=\"{new string('A', HangReader.MaximumNameLength + 1)}\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("name-omitted", result.Status);
    }

    [Fact]
    public void Inspect_ReportsOversizedFile()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", new string(' ', (int)HangReader.MaximumFileBytes + 1));

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("oversized", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_ReportsMissingSequence()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Results);

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("missing", result.Status);
    }

    [Fact]
    public void Inspect_SkipsOverlongEnumeratedEntryWithoutReadingItsAttributes()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Results);
        var validPath = fixture.Write("output/results/Sequence.xml",
            "<TestSequence><Test Name=\"Sample.FollowingTest\" /></TestSequence>");
        var overlongPath = TestPathUtils.PathUnder(
            fixture.Results, new string('x', HangReader.MaximumNameLength + 1));
        var live = HangReader.InspectionIo.Live;
        var io = live with
        {
            EnumerateChildren = path => Path.GetFullPath(path) == Path.GetFullPath(fixture.Results)
                ? [new FileInfo(overlongPath), new FileInfo(validPath)]
                : live.EnumerateChildren(path),
            GetChildAttributes = child => child.FullName == overlongPath
                ? throw new InvalidOperationException("overlong entry attributes must not be read")
                : live.GetChildAttributes(child)
        };

        var result = HangReader.Inspect(fixture.Results, fixture.Output, io: io);

        Assert.Equal("found", result.Status);
        Assert.Equal("Sample.FollowingTest", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Fact]
    public void Inspect_ReportsUnreadableDirectoryWhenEnumerationFails()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Write("output/results/denied/Sequence.xml", "<TestSequence><Test Name=\"Hidden\" /></TestSequence>");
        var denied = TestPathUtils.PathUnder(fixture.Results, "denied");
        var originalMode = File.GetUnixFileMode(denied);
        try
        {
            File.SetUnixFileMode(denied, UnixFileMode.None);
            var enumerationBlocked = false;
            try
            {
                // A privileged test runner may still enumerate this directory.
                _ = Directory.EnumerateFileSystemEntries(denied).Any();
            }
            catch (UnauthorizedAccessException)
            {
                enumerationBlocked = true;
            }

            if (!enumerationBlocked) return;

            var result = HangReader.Inspect(fixture.Results, fixture.Output);

            Assert.Equal("unreadable", result.Status);
            Assert.Empty(result.Sequences);
        }
        finally
        {
            File.SetUnixFileMode(denied, originalMode);
        }
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Inspect_ReportsUnreadableWhenPathAttributesFail(Type exceptionType)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Results);
        var io = HangReader.InspectionIo.Live with
        {
            GetPathAttributes = _ => throw CreateIoException(exceptionType)
        };

        var result = HangReader.Inspect(fixture.Results, fixture.Output, io: io);

        Assert.Equal("unreadable", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Inspect_ReportsUnreadableWhenDirectoryEnumerationFails(Type exceptionType)
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence.xml", "<TestSequence><Test Name=\"Started\" /></TestSequence>");
        fixture.Write("output/results/child/placeholder.txt", string.Empty);
        var childDirectory = TestPathUtils.PathUnder(fixture.Results, "child");
        var live = HangReader.InspectionIo.Live;
        var io = live with
        {
            EnumerateChildren = path => Path.GetFullPath(path) == Path.GetFullPath(childDirectory)
                ? throw CreateIoException(exceptionType)
                : live.EnumerateChildren(path)
        };

        var result = HangReader.Inspect(fixture.Results, fixture.Output, io: io);

        Assert.Equal("unreadable", result.Status);
        Assert.Equal("Started", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Inspect_ReportsUnreadableWhenLazyDirectoryEnumerationFails(Type exceptionType)
    {
        using var fixture = new Fixture();
        var sequencePath = fixture.Write("output/results/Sequence.xml",
            "<TestSequence><Test Name=\"Started\" /></TestSequence>");
        var sequence = new FileInfo(sequencePath);
        var live = HangReader.InspectionIo.Live;
        var io = live with
        {
            EnumerateChildren = path => Path.GetFullPath(path) == Path.GetFullPath(fixture.Results)
                ? YieldThenThrow(sequence, exceptionType)
                : live.EnumerateChildren(path)
        };

        var result = HangReader.Inspect(fixture.Results, fixture.Output, io: io);

        Assert.Equal("unreadable", result.Status);
        Assert.Equal("Started", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Theory]
    [InlineData(typeof(IOException), true, "unreadable")]
    [InlineData(typeof(UnauthorizedAccessException), true, "unreadable")]
    [InlineData(typeof(IOException), false, "found")]
    [InlineData(typeof(UnauthorizedAccessException), false, "found")]
    public void Inspect_HandlesChildAttributeFailuresByEntryKind(
        Type exceptionType,
        bool sequenceEntry,
        string expectedStatus)
    {
        using var fixture = new Fixture();
        var sequencePath = fixture.Write("output/results/Sequence.xml",
            "<TestSequence><Test Name=\"Started\" /></TestSequence>");
        var failingPath = sequenceEntry
            ? sequencePath
            : fixture.Write("output/results/ordinary.txt", string.Empty);
        var live = HangReader.InspectionIo.Live;
        var io = live with
        {
            GetChildAttributes = child => Path.GetFullPath(child.FullName) == Path.GetFullPath(failingPath)
                ? throw CreateIoException(exceptionType)
                : live.GetChildAttributes(child)
        };

        var result = HangReader.Inspect(fixture.Results, fixture.Output, io: io);

        Assert.Equal(expectedStatus, result.Status);
        if (sequenceEntry) Assert.Empty(result.Sequences);
        else Assert.Equal("Started", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Theory]
    [InlineData(typeof(IOException), "disk read failed", "unreadable")]
    [InlineData(typeof(IOException), "path escaped root", "escaping")]
    [InlineData(typeof(UnauthorizedAccessException), "access denied", "unreadable")]
    public void Inspect_ReportsFileOpenFailuresAndRetainsEarlierObservations(
        Type exceptionType,
        string message,
        string expectedStatus)
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/a/Sequence.xml", "<TestSequence><Test Name=\"Earlier\" /></TestSequence>");
        fixture.Write("output/results/b/Sequence.xml", "<TestSequence><Test Name=\"Failed\" /></TestSequence>");
        var live = HangReader.InspectionIo.Live;
        var failingPath = TestPathUtils.PathUnder(fixture.Results, "b", "Sequence.xml");
        var io = live with
        {
            EnumerateChildren = path => live.EnumerateChildren(path).OrderBy(child => child.Name, StringComparer.Ordinal),
            OpenRegularFile = (output, root, path) => Path.GetFullPath(path) == Path.GetFullPath(failingPath)
                ? throw CreateIoException(exceptionType, message)
                : live.OpenRegularFile(output, root, path)
        };

        var result = HangReader.Inspect(fixture.Results, fixture.Output, io: io);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal("Earlier", Assert.Single(result.Sequences).LastStartedTest);
    }

    [Fact]
    public void Inspect_IgnoresFilesWithUnrecognizedSequenceSuffix()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/Sequence_not-a-guid.xml", "<TestSequence><Test Name=\"Spoof\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("missing", result.Status);
    }

    [Fact]
    public void Inspect_RejectsDirectoryOutsideOutputRoot()
    {
        using var fixture = new Fixture();
        var outside = TestPathUtils.PathUnder(Path.GetTempPath(), $"hang-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            var result = HangReader.Inspect(outside, fixture.Output);
            Assert.Equal("escaping", result.Status);
            Assert.Empty(result.Sequences);
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Inspect_RejectsCaseChangedOutputRootOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Results);
        var caseChanged = TestPathUtils.PathUnder(Path.GetDirectoryName(fixture.Output)!, "OUTPUT", "results");

        var result = HangReader.Inspect(caseChanged, fixture.Output);

        Assert.Equal("escaping", result.Status);
    }

    [Fact]
    public void Inspect_ReportsEscapingSequenceSymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Results);
        var target = fixture.Write("outside.xml", "<TestSequence><Test Name=\"Outside\" /></TestSequence>");
        File.CreateSymbolicLink(TestPathUtils.PathUnder(fixture.Results, "Sequence.xml"), target);

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("escaping", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_ReportsEscapingDirectorySymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var target = fixture.Write("outside/Sequence.xml", "<TestSequence><Test Name=\"Outside\" /></TestSequence>");
        Directory.CreateDirectory(fixture.Results);
        Directory.CreateSymbolicLink(TestPathUtils.PathUnder(fixture.Results, "outside-link"), Path.GetDirectoryName(target)!);

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("escaping", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_ReportsEscapingOwnedRootSymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Results);
        var linkedRoot = TestPathUtils.PathUnder(fixture.Output, "linked-results");
        Directory.CreateSymbolicLink(linkedRoot, fixture.Results);

        var result = HangReader.Inspect(linkedRoot, fixture.Output);

        Assert.Equal("escaping", result.Status);
    }

    [Fact]
    public void Inspect_ReportsEscapingOutputAncestorSymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var realOutput = fixture.Write("real-output/results/Sequence.xml",
            "<TestSequence><Test Name=\"Hidden\" /></TestSequence>");
        var linkedOutput = TestPathUtils.PathUnder(Path.GetDirectoryName(fixture.Output)!, "linked-output");
        Directory.CreateSymbolicLink(linkedOutput, Path.GetDirectoryName(Path.GetDirectoryName(realOutput)!)!);

        var result = HangReader.Inspect(TestPathUtils.PathUnder(linkedOutput, "results"), linkedOutput);

        Assert.Equal("escaping", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_ReturnsOneObservationPerParallelHost()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/host-1/Sequence.xml", "<TestSequence><Test Name=\"First\" /></TestSequence>");
        fixture.Write("output/results/host-2/Sequence.xml", "<TestSequence><Test Name=\"Second\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("found", result.Status);
        Assert.Equal(2, result.Sequences.Count);
        Assert.Equal(new[] { "First", "Second" }, result.Sequences.Select(item => item.LastStartedTest).Order().ToArray());
        Assert.All(result.Sequences, item => Assert.StartsWith("results/host-", item.RelativePath, StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_LimitsHighlyBranchingDirectoryTree()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Results);
        for (var index = 0; index <= HangReader.MaximumDirectories; index++)
            Directory.CreateDirectory(TestPathUtils.PathUnder(fixture.Results, $"dir-{index:D3}"));

        var result = HangReader.Inspect(fixture.Results, fixture.Output, new FrozenClock());

        Assert.Equal("inspection-limited", result.Status);
    }

    [Fact]
    public void Inspect_LimitsDepthBeforeEnteringDeeperDirectory()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/a/b/c/d/e/Sequence.xml", "<TestSequence><Test Name=\"Deep\" /></TestSequence>");

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("inspection-limited", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_LimitsEntriesAcrossDirectories()
    {
        using var fixture = new Fixture();
        for (var directory = 0; directory < 5; directory++)
        {
            for (var entry = 0; entry < HangReader.MaximumEntriesPerDirectory; entry++)
            {
                fixture.Write($"output/results/dir-{directory:D2}/entry-{entry:D3}.txt", string.Empty);
            }
        }

        var result = HangReader.Inspect(fixture.Results, fixture.Output, new FrozenClock());

        Assert.Equal("inspection-limited", result.Status);
    }

    [Fact]
    public void Inspect_LimitsEntriesWithinOneDirectory()
    {
        using var fixture = new Fixture();
        for (var entry = 0; entry <= HangReader.MaximumEntriesPerDirectory; entry++)
        {
            fixture.Write($"output/results/entry-{entry:D3}.txt", string.Empty);
        }

        var result = HangReader.Inspect(fixture.Results, fixture.Output, new FrozenClock());

        Assert.Equal("inspection-limited", result.Status);
    }

    [Fact]
    public void Inspect_LimitsBestEffortTimeBudget()
    {
        using var fixture = new Fixture();
        fixture.Write("output/results/entry.txt", string.Empty);

        var result = HangReader.Inspect(fixture.Results, fixture.Output, new SteppingClock());

        Assert.Equal("inspection-limited", result.Status);
    }

    [Fact]
    public void Inspect_LimitsSequenceCandidatesEvenWhenEveryFileIsMalformed()
    {
        using var fixture = new Fixture();
        for (var index = 0; index <= HangReader.MaximumSequences; index++)
        {
            fixture.Write($"output/results/host-{index:D2}/Sequence.xml", "<TestSequence>");
        }

        var result = HangReader.Inspect(fixture.Results, fixture.Output, new FrozenClock());

        Assert.Equal("inspection-limited", result.Status);
        Assert.Empty(result.Sequences);
    }

    [Fact]
    public void Inspect_AcceptsExactlyTheMaximumSequenceCount()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < HangReader.MaximumSequences; index++)
        {
            fixture.Write($"output/results/host-{index:D2}/Sequence.xml",
                $"<TestSequence><Test Name=\"Sample.Test{index:D2}\" /></TestSequence>");
        }

        var result = HangReader.Inspect(fixture.Results, fixture.Output);

        Assert.Equal("found", result.Status);
        Assert.Equal(HangReader.MaximumSequences, result.Sequences.Count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = TestPathUtils.PathUnder(Path.GetTempPath(), $"hang-reader-{Guid.NewGuid():N}");
        internal string Output => TestPathUtils.PathUnder(_path, "output");
        internal string Results => TestPathUtils.PathUnder(Output, "results");

        internal Fixture()
        {
            Directory.CreateDirectory(Output);
        }

        internal string Write(string relativePath, string content)
        {
            var path = TestPathUtils.PathUnder(_path, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class SteppingClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, TimeSpan.TicksPerSecond);
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }

    private static Exception CreateIoException(Type exceptionType, string message = "injected I/O failure")
        => exceptionType == typeof(IOException)
            ? new IOException(message)
            : exceptionType == typeof(UnauthorizedAccessException)
                ? new UnauthorizedAccessException(message)
                : throw new ArgumentOutOfRangeException(nameof(exceptionType));

    private static IEnumerable<FileSystemInfo> YieldThenThrow(FileSystemInfo entry, Type exceptionType)
    {
        yield return entry;
        throw CreateIoException(exceptionType);
    }
}
