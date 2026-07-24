using System.Runtime.InteropServices;
using CliFx;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Win32.SafeHandles;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunOutputGuardSecurityTests
{
    private const string MarkerContents = "AppSurface coverage output directory";

    [Fact]
    public void Validate_ShouldRejectExistingAncestorLinkWithoutTouchingExternalFiles()
    {
        using var root = TestDirectory.Create();
        var external = root.CreateDirectory("external");
        var externalOutput = root.CreateDirectory("external/coverage");
        var sentinel = root.WriteFile("external/coverage/summary.txt", "external sentinel");
        root.WriteFile("external/coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var linkedAncestor = Path.Join(root.Path, "linked-parent");
        Directory.CreateSymbolicLink(linkedAncestor, external);

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Validate(Path.Join(linkedAncestor, "coverage"), root.Path, []));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(externalOutput));
        Assert.Equal("external sentinel", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Validate_ShouldRejectMissingOutputBelowLinkedAncestor()
    {
        using var root = TestDirectory.Create();
        var external = root.CreateDirectory("external");
        var sentinel = root.WriteFile("external/sentinel.txt", "external sentinel");
        var linkedAncestor = Path.Join(root.Path, "linked-parent");
        Directory.CreateSymbolicLink(linkedAncestor, external);
        var output = Path.Join(linkedAncestor, "missing", "coverage");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Validate(output, root.Path, []));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(external, "missing")));
        Assert.Equal("external sentinel", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Prepare_ShouldFailClosedWhenAncestorIsReplacedBeforeMutation()
    {
        using var root = TestDirectory.Create();
        var guardedParent = root.CreateDirectory("guarded-parent");
        var output = root.CreateDirectory("guarded-parent/coverage");
        root.WriteFile("guarded-parent/coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        root.WriteFile("guarded-parent/coverage/summary.txt", "original stale output");
        var external = root.CreateDirectory("external");
        root.CreateDirectory("external/coverage");
        root.WriteFile("external/coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var externalSentinel = root.WriteFile("external/coverage/summary.txt", "external sentinel");
        var parkedParent = Path.Join(root.Path, "parked-parent");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(
                output,
                root.Path,
                [],
                clean: true,
                beforeMutation: () =>
                {
                    Directory.Move(guardedParent, parkedParent);
                    if (!OperatingSystem.IsWindows())
                    {
                        Directory.CreateSymbolicLink(guardedParent, external);
                    }
                }));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("external sentinel", File.ReadAllText(externalSentinel));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Directory.Exists(guardedParent));
            Assert.False(Directory.Exists(parkedParent));
        }
    }

    [Theory]
    [InlineData("projects")]
    [InlineData("reportgenerator")]
    public void Prepare_ShouldRejectManagedDirectoryLinkWithoutTouchingExternalFiles(string managedDirectory)
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var external = root.CreateDirectory("external");
        var sentinel = root.WriteFile("external/sentinel.txt", "external sentinel");
        Directory.CreateSymbolicLink(Path.Join(output, managedDirectory), external);

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: true));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", exception.Message, StringComparison.Ordinal);
        Assert.Equal("external sentinel", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData(".appsurface-coverage-output", MarkerContents)]
    [InlineData("summary.txt", "external sentinel")]
    public void Prepare_ShouldRejectManagedFileLinkWithoutTouchingExternalFile(
        string managedFile,
        string externalContents)
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        if (!string.Equals(managedFile, ".appsurface-coverage-output", StringComparison.Ordinal))
        {
            root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        }

        var external = root.WriteFile("external/target.txt", externalContents + Environment.NewLine);
        File.CreateSymbolicLink(TestPathUtils.PathUnder(output, managedFile), external);

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: true));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", exception.Message, StringComparison.Ordinal);
        Assert.Equal(externalContents, File.ReadAllText(external).Trim());
    }

    [Fact]
    public void Prepare_ShouldDenyCompetingWindowsDirectoryWriteWhileLeaseIsActive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");

        CoverageRunOutputGuard.Prepare(
            output,
            root.Path,
            [],
            clean: true,
            beforeCleanup: () =>
            {
                using var competing = CreateFile(
                    output,
                    WindowsGenericWrite,
                    WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                    0,
                    WindowsOpenExisting,
                    WindowsBackupSemantics | WindowsOpenReparsePoint,
                    0);
                Assert.True(competing.IsInvalid);
                Assert.Equal(WindowsSharingViolation, Marshal.GetLastPInvokeError());
            });

        Assert.True(File.Exists(Path.Join(output, ".appsurface-coverage-output")));
        Assert.True(Directory.Exists(Path.Join(output, "projects")));
    }

    [Fact]
    public void Prepare_ShouldAllowPreexistingWindowsWriteHandleOnAncestor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("parent/coverage");
        using var ancestorWriter = CreateFile(
            Path.Join(root.Path, "parent"),
            WindowsGenericWrite,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            0,
            WindowsOpenExisting,
            WindowsBackupSemantics | WindowsOpenReparsePoint,
            0);
        Assert.False(ancestorWriter.IsInvalid);

        CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: true);

        Assert.True(File.Exists(Path.Join(output, ".appsurface-coverage-output")));
        Assert.True(Directory.Exists(Path.Join(output, "projects")));
    }

    [Fact]
    public void Prepare_Clean_ShouldReplaceKnownOwnedArtifacts()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var summary = root.WriteFile("coverage/summary.txt", "stale summary");
        var projectArtifact = root.WriteFile("coverage/projects/old/coverage.cobertura.xml", "stale coverage");
        var report = root.WriteFile("coverage/reportgenerator/index.html", "stale report");

        CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: true);

        Assert.False(File.Exists(summary));
        Assert.False(File.Exists(projectArtifact));
        Assert.False(File.Exists(report));
        Assert.True(Directory.Exists(Path.Join(output, "projects")));
        Assert.Equal(MarkerContents, File.ReadAllText(Path.Join(output, ".appsurface-coverage-output")).Trim());
    }

    [Fact]
    public void Prepare_Clean_ShouldReplaceEveryKnownAndPatternedRootArtifact()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        string[] knownFiles =
        [
            "coverage.cobertura.xml",
            "coverage.json",
            "coverage-gate.json",
            "coverage-gate.md",
            "coverage-watchdog.json",
            "summary.txt",
            "timings.json",
            "reportgenerator-summary.txt",
            "slow-test-diagnostics.md",
            "slow-test-diagnostics.json",
            "junit-old.xml",
            "test-results-old.xml",
        ];
        foreach (var file in knownFiles)
        {
            root.WriteFile(TestPathUtils.RelativePath("coverage", file), "stale output");
        }

        var wrongExtension = root.WriteFile("coverage/junit-old.txt", "must remain");
        var unrelated = root.WriteFile("coverage/unrelated.xml", "must remain");
        var patternedDirectory = root.CreateDirectory("coverage/test-results-directory.xml");

        CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: true);

        Assert.All(knownFiles, file => Assert.False(File.Exists(TestPathUtils.PathUnder(output, file))));
        Assert.Equal("must remain", File.ReadAllText(wrongExtension));
        Assert.Equal("must remain", File.ReadAllText(unrelated));
        Assert.True(Directory.Exists(patternedDirectory));
    }

    [Fact]
    public void Prepare_Clean_ShouldRejectUnmarkedKnownArtifactsWithoutDeletingThem()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        var summary = root.WriteFile("coverage/summary.txt", "must remain");
        var projectArtifact = root.WriteFile("coverage/projects/old/sentinel.txt", "must remain");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: true));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not marked as AppSurface-owned", exception.Message, StringComparison.Ordinal);
        Assert.Equal("must remain", File.ReadAllText(summary));
        Assert.Equal("must remain", File.ReadAllText(projectArtifact));
        Assert.False(File.Exists(Path.Join(output, ".appsurface-coverage-output")));
    }

    [Fact]
    public void Prepare_NoClean_ShouldPreserveKnownOwnedArtifacts()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var summary = root.WriteFile("coverage/summary.txt", "existing summary");
        var projectArtifact = root.WriteFile("coverage/projects/old/coverage.cobertura.xml", "existing coverage");

        CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: false);

        Assert.Equal("existing summary", File.ReadAllText(summary));
        Assert.Equal("existing coverage", File.ReadAllText(projectArtifact));
        Assert.True(Directory.Exists(Path.Join(output, "projects")));
    }

    [Fact]
    public void Validate_ShouldRejectMarkerWithUnexpectedContents()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", "not an AppSurface ownership marker");
        var sentinel = root.WriteFile("coverage/summary.txt", "must remain");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Validate(output, root.Path, []));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("must remain", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Validate_ShouldRejectMarkerDirectory()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.CreateDirectory("coverage/.appsurface-coverage-output");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Validate(output, root.Path, []));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Join(output, ".appsurface-coverage-output")));
    }

    [Fact]
    public void Validate_ShouldRejectOversizedMarker()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        var marker = root.WriteFile("coverage/.appsurface-coverage-output", new string('x', 129));

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Validate(output, root.Path, []));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal(129, new FileInfo(marker).Length);
    }

    [Fact]
    public void Validate_ShouldAcceptMarkerWithoutTrailingNewline()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents);

        CoverageRunOutputGuard.Validate(output, root.Path, []);

        Assert.Equal(MarkerContents, File.ReadAllText(Path.Join(output, ".appsurface-coverage-output")));
    }

    [Fact]
    public void Validate_ShouldNotCreateMissingOutput()
    {
        using var root = TestDirectory.Create();
        var output = Path.Join(root.Path, "missing", "coverage");

        CoverageRunOutputGuard.Validate(output, root.Path, []);

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Validate_ShouldRejectOutputBelowRegularFile()
    {
        using var root = TestDirectory.Create();
        var parent = root.WriteFile("parent", "not a directory");
        var output = Path.Join(parent, "coverage");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Validate(output, root.Path, []));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("not a directory", File.ReadAllText(parent));
    }

    [Fact]
    public void Acquire_ShouldWrapOutputBelowRegularFileAsUnsafeOutput()
    {
        using var root = TestDirectory.Create();
        var parent = root.WriteFile("parent", "not a directory");
        var output = Path.Join(parent, "coverage");

        var exception = Assert.Throws<CommandException>(() => CoverageRunOutputLease.Acquire(output));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("securely acquired", exception.Message, StringComparison.Ordinal);
        Assert.Equal("not a directory", File.ReadAllText(parent));
    }

    [Fact]
    public void PrepareUnix_ShouldFailClosedWhenMarkerAppearsBeforeCreation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        var marker = Path.Join(output, ".appsurface-coverage-output");

        var exception = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.Prepare(
            output,
            root.Path,
            [],
            clean: true,
            beforeCleanup: () => File.WriteAllText(marker, "competing marker")));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("competing marker", File.ReadAllText(marker));
    }

    [Fact]
    public void Prepare_ShouldFailClosedWhenOutputIsMovedBeforeBindingRevalidation()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var parked = Path.Join(root.Path, "parked-coverage");

        var exception = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.Prepare(
            output,
            root.Path,
            [],
            clean: true,
            beforeMutation: () => Directory.Move(output, parked)));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Directory.Exists(output));
            Assert.False(Directory.Exists(parked));
        }
        else
        {
            Assert.True(Directory.Exists(parked));
            Assert.False(Directory.Exists(output));
        }
    }

    [Fact]
    public void PrepareUnix_ShouldFailClosedWhenArtifactIsRemovedBeforeCleanup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var summary = root.WriteFile("coverage/summary.txt", "stale output");

        var exception = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.Prepare(
            output,
            root.Path,
            [],
            clean: true,
            beforeCleanup: () => File.Delete(summary)));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(summary));
    }

    [Fact]
    public void PrepareUnix_ShouldFailClosedWhenManagedDirectoryGainsArtifactBeforeCleanup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        root.WriteFile("coverage/projects/original.txt", "stale output");

        var exception = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.Prepare(
            output,
            root.Path,
            [],
            clean: true,
            beforeCleanup: () => root.WriteFile("coverage/projects/late.txt", "must remain")));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            Directory.EnumerateFiles(output, "late.txt", SearchOption.AllDirectories),
            path => string.Equals(File.ReadAllText(path), "must remain", StringComparison.Ordinal));
    }

    [Fact]
    public void Prepare_ShouldFailClosedWhenManagedDirectoryIdentityChangesBeforeMutation()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var projects = root.CreateDirectory("coverage/projects");
        var original = root.WriteFile("coverage/projects/original.txt", "original output");
        var replacement = root.CreateDirectory("replacement-projects");
        var replacementSentinel = root.WriteFile("replacement-projects/original.txt", "must remain");
        var parkedProjects = Path.Join(root.Path, "parked-projects");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(
                output,
                root.Path,
                [],
                clean: true,
                beforeMutation: () =>
                {
                    Directory.Move(projects, parkedProjects);
                    Directory.Move(replacement, projects);
                }));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("original output", File.ReadAllText(Path.Join(parkedProjects, Path.GetFileName(original))));
        var replacementLocation = OperatingSystem.IsWindows() ? replacement : projects;
        Assert.Equal("must remain", File.ReadAllText(Path.Join(replacementLocation, Path.GetFileName(replacementSentinel))));
    }

    [Fact]
    public void Prepare_ShouldNotDeleteReplacementMovedInAfterAuthorization()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var projects = root.CreateDirectory("coverage/projects");
        var original = root.WriteFile("coverage/projects/original.txt", "original output");
        var replacement = root.CreateDirectory("replacement-projects");
        root.WriteFile("replacement-projects/original.txt", "must remain");
        var parkedProjects = Path.Join(root.Path, "parked-projects");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(
                output,
                root.Path,
                [],
                clean: true,
                beforeCleanup: () =>
                {
                    Directory.Move(projects, parkedProjects);
                    Directory.Move(replacement, projects);
                }));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("original output", File.ReadAllText(Path.Join(parkedProjects, Path.GetFileName(original))));
        var replacementLocation = OperatingSystem.IsWindows()
            ? replacement
            : Assert.Single(Directory.EnumerateDirectories(output, ".appsurface-clean-*"));
        Assert.Equal("must remain", File.ReadAllText(Path.Join(replacementLocation, "original.txt")));
    }

    [Fact]
    public void Prepare_ShouldNotDeleteFileReplacementMovedInAfterAuthorization()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var summary = root.WriteFile("coverage/summary.txt", "original output");
        var replacement = root.WriteFile("replacement-summary.txt", "must remain");
        var parkedSummary = Path.Join(root.Path, "parked-summary.txt");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(
                output,
                root.Path,
                [],
                clean: true,
                beforeCleanup: () =>
                {
                    File.Move(summary, parkedSummary);
                    File.Move(replacement, summary);
                }));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("original output", File.ReadAllText(parkedSummary));
        var replacementLocation = OperatingSystem.IsWindows()
            ? replacement
            : Assert.Single(Directory.EnumerateFiles(output, ".appsurface-clean-*"));
        Assert.Equal("must remain", File.ReadAllText(replacementLocation));
    }

    [Fact]
    public void Prepare_ShouldFailClosedWhenValidMarkerIdentityChangesBeforeMutation()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("coverage");
        var marker = root.WriteFile("coverage/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        var sentinel = root.WriteFile("coverage/summary.txt", "must remain");
        var parkedMarker = Path.Join(root.Path, "parked-marker");

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Prepare(
                output,
                root.Path,
                [],
                clean: true,
                beforeMutation: () =>
                {
                    File.Move(marker, parkedMarker);
                    File.WriteAllText(marker, MarkerContents + Environment.NewLine);
                }));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Equal("must remain", File.ReadAllText(sentinel));
        Assert.Equal(MarkerContents, File.ReadAllText(parkedMarker).Trim());
    }

    [Theory]
    [InlineData("/var/appsurface-coverage-output-alias")]
    [InlineData("/tmp/appsurface-coverage-output-alias")]
    public void Validate_ShouldTreatMacOsPrivateAliasesAsTheSameProtectedDirectory(string output)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var exception = Assert.Throws<CommandException>(
            () => CoverageRunOutputGuard.Validate(output, "/private" + output, []));

        Assert.Contains("ASCOV109", exception.Message, StringComparison.Ordinal);
        Assert.Contains("solution directory", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                "appsurface-coverage-output-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public string CreateDirectory(string relativePath)
        {
            return Directory.CreateDirectory(TestPathUtils.PathUnder(Path, relativePath)).FullName;
        }

        public string WriteFile(string relativePath, string contents)
        {
            var path = TestPathUtils.PathUnder(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private const uint WindowsGenericWrite = 0x40000000;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const int WindowsSharingViolation = 32;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
