using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies safe discovery and explicit cleanup of private TestResults directories.</summary>
public sealed class TestResultsCleanCommandTests
{
    [Fact]
    public async Task Clean_preview_lists_matching_directories_and_estimated_size_without_deleting_them()
    {
        using var root = TestDirectory.Create();
        var first = root.CreateDirectory("TestResults");
        var second = root.CreateDirectory("src/Tests/testresults");
        root.WriteFile("TestResults/first.bin", "abc");
        root.WriteFile("src/Tests/testresults/second.bin", "defg");
        var unrelated = root.CreateDirectory("src/Tests/TestResults-archive");
        using var console = new FakeInMemoryConsole();
        var workflow = new TestResultsCleanupWorkflow();

        var result = await workflow.CleanAsync(
            new TestResultsCleanupRequest(root.Path, Apply: false),
            console,
            CancellationToken.None);

        Assert.Equal(root.Path, result.RootDirectory);
        Assert.Equal([first, second], result.Directories);
        Assert.Equal(7, result.EstimatedBytes);
        Assert.Equal(0, result.ReparsePointsSkipped);
        Assert.False(result.Applied);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.True(Directory.Exists(unrelated));

        var output = console.ReadOutputString();
        Assert.Contains("Found 2 TestResults directories using approximately 7 B.", output, StringComparison.Ordinal);
        Assert.Contains("  TestResults", output, StringComparison.Ordinal);
        Assert.Contains("  src/Tests/testresults", output, StringComparison.Ordinal);
        Assert.Contains("Preview only. Re-run with --apply", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_apply_deletes_only_discovered_TestResults_directories()
    {
        using var root = TestDirectory.Create();
        var first = root.CreateDirectory("TestResults");
        var second = root.CreateDirectory("tests/Integration/TestResults");
        root.WriteFile("TestResults/first.bin", "abc");
        root.WriteFile("tests/Integration/TestResults/second.bin", "defg");
        var unrelated = root.WriteFile("tests/Integration/TestResults-old/keep.txt", "must remain");
        using var console = new FakeInMemoryConsole();

        var command = new CoverageCleanCommand(new TestResultsCleanupWorkflow())
        {
            RootDirectory = root.Path,
            All = true,
            Apply = true,
        };

        await command.ExecuteAsync(console, CancellationToken.None);

        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
        Assert.True(File.Exists(unrelated));
        Assert.Contains("Removed 2 TestResults directories and reclaimed approximately 7 B.", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_does_not_delete_the_scan_root_even_when_it_is_named_TestResults()
    {
        using var parent = TestDirectory.Create();
        var root = parent.CreateDirectory("TestResults");
        root = Path.GetFullPath(root);
        parent.WriteFile("TestResults/keep.txt", "root remains");
        using var console = new FakeInMemoryConsole();

        var result = await new TestResultsCleanupWorkflow().CleanAsync(
            new TestResultsCleanupRequest(root, Apply: true),
            console,
            CancellationToken.None);

        Assert.Empty(result.Directories);
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Join(root, "keep.txt")));
        Assert.Contains("Removed 0 TestResults directories", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_skips_linked_directories_without_traversing_or_deleting_their_targets()
    {
        using var root = TestDirectory.Create();
        var scanRoot = root.CreateDirectory("scan");
        var external = root.CreateDirectory("outside/TestResults");
        root.WriteFile("outside/TestResults/keep.txt", "linked target remains");
        var link = Path.Join(scanRoot, "linked-worktree");
        if (!TryCreateDirectoryLink(link, Path.GetDirectoryName(external)!))
        {
            return;
        }

        using var console = new FakeInMemoryConsole();
        var result = await new TestResultsCleanupWorkflow().CleanAsync(
            new TestResultsCleanupRequest(scanRoot, Apply: true),
            console,
            CancellationToken.None);

        Assert.Empty(result.Directories);
        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.True(Directory.Exists(external));
        Assert.True(File.Exists(Path.Join(external, "keep.txt")));
        Assert.Contains("Skipped 1 symbolic-link or reparse-point entry", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_deletes_a_TestResults_link_entry_without_following_its_target()
    {
        using var root = TestDirectory.Create();
        var results = root.CreateDirectory("TestResults");
        var external = root.CreateDirectory("external-data");
        root.WriteFile("external-data/keep.txt", "linked target remains");
        var link = Path.Join(results, "linked-data");
        if (!TryCreateDirectoryLink(link, external))
        {
            return;
        }

        using var console = new FakeInMemoryConsole();
        var result = await new TestResultsCleanupWorkflow().CleanAsync(
            new TestResultsCleanupRequest(root.Path, Apply: true),
            console,
            CancellationToken.None);

        Assert.Equal([results], result.Directories);
        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.False(Directory.Exists(results));
        Assert.True(Directory.Exists(external));
        Assert.True(File.Exists(Path.Join(external, "keep.txt")));
    }

    [Fact]
    public async Task Clean_stops_on_the_first_deletion_failure_and_reports_safe_recovery_guidance()
    {
        using var root = TestDirectory.Create();
        var first = root.CreateDirectory("a/TestResults");
        var second = root.CreateDirectory("b/TestResults");
        root.WriteFile("a/TestResults/first.bin", "abc");
        root.WriteFile("b/TestResults/second.bin", "defg");
        using var console = new FakeInMemoryConsole();
        var workflow = new TestResultsCleanupWorkflow(
            (directory, _) =>
            {
                if (directory == second)
                {
                    throw new IOException("sentinel deletion failure");
                }

                Directory.Delete(directory, recursive: true);
            });

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await workflow.CleanAsync(new TestResultsCleanupRequest(root.Path, Apply: true), console, CancellationToken.None));

        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.Contains("ASTEST103", error.Message, StringComparison.Ordinal);
        Assert.Contains("after removing 1 of 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("Close processes holding files", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel deletion failure", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Clean_wraps_all_supported_deletion_failures(int exceptionKind)
    {
        using var root = TestDirectory.Create();
        root.CreateDirectory("TestResults");
        using var console = new FakeInMemoryConsole();
        var workflow = new TestResultsCleanupWorkflow(
            (_, _) => throw CreateDeletionException(exceptionKind));

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await workflow.CleanAsync(new TestResultsCleanupRequest(root.Path, Apply: true), console, CancellationToken.None));

        Assert.Contains("ASTEST103", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_rethrows_existing_command_diagnostics_without_wrapping_them()
    {
        using var root = TestDirectory.Create();
        root.CreateDirectory("TestResults");
        using var console = new FakeInMemoryConsole();
        var workflow = new TestResultsCleanupWorkflow((_, _) => throw new CommandException("existing diagnostic"));

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await workflow.CleanAsync(new TestResultsCleanupRequest(root.Path, Apply: true), console, CancellationToken.None));

        Assert.Equal("existing diagnostic", error.Message);
    }

    [Fact]
    public async Task Clean_preview_measures_nested_content_and_formats_kibibytes()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("TestResults/nested/payload.bin", new string('x', 1024));
        using var console = new FakeInMemoryConsole();

        var result = await new TestResultsCleanupWorkflow().CleanAsync(
            new TestResultsCleanupRequest(root.Path, Apply: false),
            console,
            CancellationToken.None);

        Assert.Single(result.Directories);
        Assert.Equal(1024, result.EstimatedBytes);
        Assert.Contains("Found 1 TestResults directory using approximately 1 KiB.", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_honors_cancellation_before_scanning_the_default_root()
    {
        using var console = new FakeInMemoryConsole();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new TestResultsCleanupWorkflow().CleanAsync(
                new TestResultsCleanupRequest(" ", Apply: false),
                console,
                cancellation.Token));
    }

    [Fact]
    public async Task Clean_rejects_an_invalid_root_path()
    {
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await new TestResultsCleanupWorkflow().CleanAsync(
                new TestResultsCleanupRequest("\0", Apply: false),
                console,
                CancellationToken.None));

        Assert.Contains("ASTEST101", error.Message, StringComparison.Ordinal);
        Assert.Contains("cleanup root is invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_rejects_a_linked_root_without_traversing_its_target()
    {
        using var root = TestDirectory.Create();
        var external = root.CreateDirectory("external");
        root.WriteFile("external/TestResults/keep.txt", "linked target remains");
        var linkedRoot = Path.Join(root.Path, "linked-root");
        if (!TryCreateDirectoryLink(linkedRoot, external))
        {
            return;
        }

        using var console = new FakeInMemoryConsole();
        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await new TestResultsCleanupWorkflow().CleanAsync(
                new TestResultsCleanupRequest(linkedRoot, Apply: false),
                console,
                CancellationToken.None));

        Assert.Contains("ASTEST102", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(external, "TestResults", "keep.txt")));
    }

    [Fact]
    public void DeleteDirectoryTree_unlinks_a_link_replaced_after_discovery_without_touching_its_target()
    {
        using var root = TestDirectory.Create();
        var external = root.CreateDirectory("external");
        var sentinel = root.WriteFile("external/keep.txt", "target remains");
        var link = Path.Join(root.Path, "TestResults");
        if (!TryCreateDirectoryLink(link, external))
        {
            return;
        }

        TestResultsCleanupWorkflow.DeleteDirectoryTree(link, CancellationToken.None);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task Clean_unlinks_linked_files_inside_TestResults_without_touching_their_targets()
    {
        using var root = TestDirectory.Create();
        var results = root.CreateDirectory("TestResults");
        var external = root.WriteFile("external/keep.txt", "target remains");
        if (!TryCreateFileLink(Path.Join(results, "linked-file.txt"), external))
        {
            return;
        }

        using var console = new FakeInMemoryConsole();
        await new TestResultsCleanupWorkflow().CleanAsync(
            new TestResultsCleanupRequest(root.Path, Apply: true),
            console,
            CancellationToken.None);

        Assert.False(Directory.Exists(results));
        Assert.Equal("target remains", File.ReadAllText(external));
    }

    [Fact]
    public async Task Clean_deletes_nested_regular_directories()
    {
        using var root = TestDirectory.Create();
        var results = root.CreateDirectory("TestResults");
        root.WriteFile("TestResults/nested/payload.bin", "nested content");
        using var console = new FakeInMemoryConsole();

        await new TestResultsCleanupWorkflow().CleanAsync(
            new TestResultsCleanupRequest(root.Path, Apply: true),
            console,
            CancellationToken.None);

        Assert.False(Directory.Exists(results));
    }

    [Fact]
    public async Task Clean_wraps_root_inspection_failure_after_the_existing_root_disappears()
    {
        using var root = TestDirectory.Create();
        var scanRoot = root.CreateDirectory("scan");
        using var console = new FakeInMemoryConsole();
        var workflow = new TestResultsCleanupWorkflow(
            TestResultsCleanupWorkflow.DeleteDirectoryTree,
            afterRootExists: Directory.Delete);

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await workflow.CleanAsync(new TestResultsCleanupRequest(scanRoot, Apply: false), console, CancellationToken.None));

        Assert.Contains("ASTEST101", error.Message, StringComparison.Ordinal);
        Assert.Contains("could not be inspected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clean_skips_a_root_replaced_by_a_link_before_discovery()
    {
        using var root = TestDirectory.Create();
        var scanRoot = root.CreateDirectory("scan");
        var external = root.CreateDirectory("external");
        var sentinel = root.WriteFile("external/TestResults/keep.txt", "target remains");
        var capabilityProbe = Path.Join(root.Path, "link-capability-probe");
        if (!TryCreateDirectoryLink(capabilityProbe, external))
        {
            return;
        }

        Directory.Delete(capabilityProbe);
        using var console = new FakeInMemoryConsole();
        var workflow = new TestResultsCleanupWorkflow(
            TestResultsCleanupWorkflow.DeleteDirectoryTree,
            beforeDiscovery: directory =>
            {
                Directory.Delete(directory);
                Directory.CreateSymbolicLink(directory, external);
            });

        var result = await workflow.CleanAsync(
            new TestResultsCleanupRequest(scanRoot, Apply: false),
            console,
            CancellationToken.None);

        Assert.Empty(result.Directories);
        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.Equal("target remains", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task Clean_wraps_discovery_failure_after_the_root_disappears()
    {
        using var root = TestDirectory.Create();
        var scanRoot = root.CreateDirectory("scan");
        using var console = new FakeInMemoryConsole();
        var workflow = new TestResultsCleanupWorkflow(
            TestResultsCleanupWorkflow.DeleteDirectoryTree,
            beforeDiscovery: Directory.Delete);

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await workflow.CleanAsync(new TestResultsCleanupRequest(scanRoot, Apply: false), console, CancellationToken.None));

        Assert.Contains("ASTEST104", error.Message, StringComparison.Ordinal);
        Assert.Contains("could not inspect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clean_helper_methods_cover_root_size_format_and_plural_boundaries()
    {
        using var root = TestDirectory.Create();
        var filesystemRoot = Path.GetPathRoot(root.Path)!;

        Assert.False(TestResultsCleanupWorkflow.IsFilesystemRoot(root.Path));
        Assert.True(TestResultsCleanupWorkflow.IsFilesystemRoot(filesystemRoot));
        Assert.Equal(long.MaxValue, TestResultsCleanupWorkflow.SaturatingAdd(long.MaxValue - 1, 1));
        Assert.Equal(long.MaxValue, TestResultsCleanupWorkflow.SaturatingAdd(long.MaxValue, 1));
        Assert.Equal("1 B", TestResultsCleanupWorkflow.FormatBytes(1));
        Assert.EndsWith(" PiB", TestResultsCleanupWorkflow.FormatBytes(long.MaxValue), StringComparison.Ordinal);
        Assert.Equal("entry", TestResultsCleanupWorkflow.Pluralize("entry", 1));
        Assert.Equal("entries", TestResultsCleanupWorkflow.Pluralize("entry", 2));
        Assert.Equal("artifacts", TestResultsCleanupWorkflow.Pluralize("artifact", 2));
    }

    [Fact]
    public async Task Clean_wraps_an_unreadable_descendant_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TestDirectory.Create();
        var unreadable = root.CreateDirectory("unreadable");
        var originalMode = File.GetUnixFileMode(unreadable);
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        try
        {
            using var console = new FakeInMemoryConsole();
            var error = await Assert.ThrowsAsync<CommandException>(async () =>
                await new TestResultsCleanupWorkflow().CleanAsync(
                    new TestResultsCleanupRequest(root.Path, Apply: false),
                    console,
                    CancellationToken.None));

            Assert.Contains("ASTEST104", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(unreadable, originalMode);
        }
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("")]
    public async Task Clean_rejects_missing_or_filesystem_root_scan_boundaries(string requestedRoot)
    {
        using var root = TestDirectory.Create();
        var value = requestedRoot.Length == 0 ? Path.GetPathRoot(root.Path)! : Path.Join(root.Path, requestedRoot);
        using var console = new FakeInMemoryConsole();

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await new TestResultsCleanupWorkflow().CleanAsync(
                new TestResultsCleanupRequest(value, Apply: false),
                console,
                CancellationToken.None));

        Assert.Contains(requestedRoot.Length == 0 ? "ASTEST102" : "ASTEST101", error.Message, StringComparison.Ordinal);
        Assert.Contains("Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-clean", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_constructor_rejects_null_dependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new CoverageCleanCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new TestResultsCleanupWorkflow(null!));
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static Exception CreateDeletionException(int exceptionKind) => exceptionKind switch
    {
        0 => new IOException("simulated IO failure"),
        1 => new UnauthorizedAccessException("simulated access failure"),
        2 => new NotSupportedException("simulated unsupported failure"),
        _ => throw new ArgumentOutOfRangeException(nameof(exceptionKind)),
    };

    private sealed class TestDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "appsurface-test-results-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public string CreateDirectory(string relativePath)
        {
            var path = TestPathUtils.PathUnder(Path, relativePath);
            Directory.CreateDirectory(path);
            return path;
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
}
