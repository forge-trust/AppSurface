using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies narrow owned-artifact cleanup and the explicit broad TestResults mode.</summary>
public sealed class CoverageCleanCommandTests
{
    private const string MarkerContents = "AppSurface coverage output directory";

    [Fact]
    public async Task Clean_default_mode_previews_then_removes_only_known_AppSurface_coverage_artifacts()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("TestResults/coverage-merged");
        CoverageRunOutputGuard.Prepare(output, root.Path, [], clean: false);
        var coverage = root.WriteFile("TestResults/coverage-merged/coverage.cobertura.xml", "<coverage />");
        var projectLog = root.WriteFile("TestResults/coverage-merged/projects/example/dotnet-test.log", "coverage-owned");
        var unrelated = root.WriteFile("TestResults/coverage-merged/keep.txt", "not AppSurface-owned");
        using var previewConsole = new FakeInMemoryConsole();
        using var applyConsole = new FakeInMemoryConsole();
        var command = new CoverageCleanCommand(new TestResultsCleanupWorkflow()) { OutputDirectory = output };

        await command.ExecuteAsync(previewConsole, CancellationToken.None);

        var preview = previewConsole.ReadOutputString();
        Assert.Contains("Found 2 AppSurface coverage artifacts.", preview, StringComparison.Ordinal);
        Assert.Contains("coverage.cobertura.xml", preview, StringComparison.Ordinal);
        Assert.Contains("projects", preview, StringComparison.Ordinal);
        Assert.Contains("Preview only. Re-run with --apply", preview, StringComparison.Ordinal);
        Assert.True(File.Exists(coverage));
        Assert.True(File.Exists(projectLog));
        Assert.True(File.Exists(unrelated));

        command.Apply = true;
        await command.ExecuteAsync(applyConsole, CancellationToken.None);

        Assert.False(File.Exists(coverage));
        Assert.False(File.Exists(projectLog));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(Path.Join(output, ".appsurface-coverage-output")));
        Assert.Contains("Removed 2 AppSurface coverage artifacts.", applyConsole.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_default_mode_does_not_create_a_missing_output_directory()
    {
        using var root = TestDirectory.Create();
        var output = Path.Join(root.Path, "TestResults", "coverage-merged");
        using var console = new FakeInMemoryConsole();

        await new CoverageCleanCommand(new TestResultsCleanupWorkflow())
        {
            OutputDirectory = output,
            Apply = true,
        }.ExecuteAsync(console, CancellationToken.None);

        Assert.False(Directory.Exists(output));
        Assert.Contains("No coverage output directory exists. Nothing to clean.", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_default_mode_rejects_a_populated_unmarked_output()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("TestResults/coverage-merged");
        root.WriteFile("TestResults/coverage-merged/coverage.cobertura.xml", "untrusted");

        var error = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.CleanExistingOwnedOutput(output, apply: false));

        Assert.Contains("ASCOV109", error.Message, StringComparison.Ordinal);
        Assert.Contains("not marked as AppSurface-owned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_default_mode_leaves_an_empty_unmarked_output_unchanged()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("TestResults/coverage-merged");
        using var console = new FakeInMemoryConsole();

        await new CoverageCleanCommand(new TestResultsCleanupWorkflow())
        {
            OutputDirectory = output,
            Apply = true,
        }.ExecuteAsync(console, CancellationToken.None);

        Assert.True(Directory.Exists(output));
        Assert.False(File.Exists(Path.Join(output, ".appsurface-coverage-output")));
        Assert.Contains("No AppSurface-owned coverage artifacts were found. Nothing to clean.", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_default_mode_reports_a_single_owned_artifact()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("TestResults/coverage-merged");
        root.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        root.WriteFile("TestResults/coverage-merged/coverage.cobertura.xml", "<coverage />");
        using var console = new FakeInMemoryConsole();

        await new CoverageCleanCommand(new TestResultsCleanupWorkflow())
        {
            OutputDirectory = output,
        }.ExecuteAsync(console, CancellationToken.None);

        Assert.Contains("Found 1 AppSurface coverage artifact.", console.ReadOutputString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_default_mode_reports_a_nested_known_artifact_with_its_top_level_parent_once()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("TestResults/coverage-merged");
        root.WriteFile("TestResults/coverage-merged/.appsurface-coverage-output", MarkerContents + Environment.NewLine);
        root.WriteFile("TestResults/coverage-merged/projects/example/coverage.json", "{}");
        using var console = new FakeInMemoryConsole();

        await new CoverageCleanCommand(new TestResultsCleanupWorkflow())
        {
            OutputDirectory = output,
        }.ExecuteAsync(console, CancellationToken.None);

        var preview = console.ReadOutputString();
        Assert.Contains("Found 1 AppSurface coverage artifact.", preview, StringComparison.Ordinal);
        Assert.Contains("  projects", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("projects/example/coverage.json", preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_default_mode_uses_the_standard_output_when_no_output_is_supplied()
    {
        using var root = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();

        await new CoverageCleanCommand(new TestResultsCleanupWorkflow(), () => root.Path).ExecuteAsync(console, CancellationToken.None);

        Assert.Contains(
            $"Coverage output: {CoverageRunOutputLease.NormalizePlatformPath(Path.Join(root.Path, "TestResults", "coverage-merged"))}",
            console.ReadOutputString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_default_mode_rejects_unsafe_output_values()
    {
        using var root = TestDirectory.Create();
        var file = root.WriteFile("output.txt", "not a directory");
        string[] unsafeOutputs =
        [
            " ",
            "\0",
            file,
            Path.GetPathRoot(root.Path)!,
            Directory.GetCurrentDirectory(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];

        foreach (var output in unsafeOutputs)
        {
            var error = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.CleanExistingOwnedOutput(output, apply: false));

            Assert.Contains("ASCOV109", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Clean_default_mode_rejects_a_linked_output_directory()
    {
        using var root = TestDirectory.Create();
        var external = root.CreateDirectory("external-output");
        var linkedOutput = Path.Join(root.Path, "linked-output");
        TestFileSystem.CreateDirectoryLinkOrSkip(linkedOutput, external);

        var error = Assert.Throws<CommandException>(() => CoverageRunOutputGuard.CleanExistingOwnedOutput(linkedOutput, apply: false));

        Assert.Contains("ASCOV109", error.Message, StringComparison.Ordinal);
        Assert.Contains("symbolic link or reparse point", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_default_mode_wraps_acquisition_failures_in_the_safe_output_diagnostic()
    {
        using var root = TestDirectory.Create();
        var output = root.CreateDirectory("TestResults/coverage-merged");

        var error = Assert.Throws<CommandException>(() =>
            CoverageRunOutputGuard.CleanExistingOwnedOutput(
                output,
                apply: false,
                _ => throw new IOException("simulated acquisition failure")));

        Assert.Contains("ASCOV109", error.Message, StringComparison.Ordinal);
        Assert.Contains("existing artifact tree", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("simulated acquisition failure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_default_mode_rejects_a_null_acquisition_operation()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CoverageRunOutputGuard.CleanExistingOwnedOutput("coverage", apply: false, null!));
    }

    [Fact]
    public void Clean_existing_lease_rejects_a_file_path_without_retaining_resources()
    {
        using var root = TestDirectory.Create();
        var file = root.WriteFile("not-a-directory", "file remains");

        Assert.ThrowsAny<IOException>(() => CoverageRunOutputLease.AcquireExisting(file));

        Assert.Equal("file remains", File.ReadAllText(file));
    }

    [Fact]
    public async Task Clean_rejects_root_without_the_explicit_all_mode()
    {
        using var root = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var command = new CoverageCleanCommand(new TestResultsCleanupWorkflow()) { RootDirectory = root.Path };

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--root is available only with --all", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clean_rejects_output_when_the_explicit_all_mode_is_selected()
    {
        using var root = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var command = new CoverageCleanCommand(new TestResultsCleanupWorkflow())
        {
            All = true,
            OutputDirectory = root.CreateDirectory("output"),
        };

        var error = await Assert.ThrowsAsync<CommandException>(async () =>
            await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--output cannot be used with --all", error.Message, StringComparison.Ordinal);
    }

}
