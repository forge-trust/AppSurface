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
