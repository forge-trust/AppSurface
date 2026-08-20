using CliFx;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Verifies the public conflict-free release-note composition workflow.</summary>
public sealed class ReleaseComposeCommandTests
{
    [Fact]
    public async Task Compose_previews_then_writes_a_deterministic_consumer_note_without_changing_sources()
    {
        using var root = TestDirectory.Create();
        var template = root.WriteFile(
            "releases/unreleased.md",
            """
            # Next release

            ## Added
            <!-- appsurface:unreleased-entries section="added" -->

            ## Fixed
            <!-- appsurface:unreleased-entries section="fixed" -->
            """);
        root.WriteFile(
            "releases/unreleased.entries/2026-08-20-zulu.md",
            """
            <!-- appsurface:unreleased-entry section="added" -->
            - [Zulu guide](../../guides/zulu.md)
            """);
        root.WriteFile(
            "releases/unreleased.entries/2026-08-20-alpha.md",
            """
            <!-- appsurface:unreleased-entry section="added" -->
            - Alpha capability.
            """);
        root.WriteFile(
            "releases/unreleased.entries/2026-08-20-fixed.md",
            """
            <!-- appsurface:unreleased-entry section="fixed" -->
            - Fixed a consumer-facing regression.
            """);
        using var previewConsole = new FakeInMemoryConsole();
        using var applyConsole = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            OutputPath = "releases/v1.2.3.md",
        };

        await command.ExecuteAsync(previewConsole, CancellationToken.None);

        Assert.Contains("Validated 3 append-only release-note entries", previewConsole.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("Preview only. Would write releases/v1.2.3.md", previewConsole.ReadOutputString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(root.Path, "releases", "v1.2.3.md")));
        Assert.Contains("<!-- appsurface:unreleased-entries section=\"added\" -->", File.ReadAllText(template), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(root.Path, "releases", "unreleased.entries", "2026-08-20-alpha.md")));

        command.Apply = true;
        await command.ExecuteAsync(applyConsole, CancellationToken.None);

        var output = File.ReadAllText(Path.Join(root.Path, "releases", "v1.2.3.md"));
        Assert.Contains("- Alpha capability.\n\n- [Zulu guide](../guides/zulu.md)", output, StringComparison.Ordinal);
        Assert.Contains("## Fixed\n- Fixed a consumer-facing regression.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- appsurface:unreleased-entries", output, StringComparison.Ordinal);
        Assert.Contains("Wrote composed release note to releases/v1.2.3.md.", applyConsole.ReadOutputString(), StringComparison.Ordinal);
        Assert.Contains("<!-- appsurface:unreleased-entries section=\"fixed\" -->", File.ReadAllText(template), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(root.Path, "releases", "unreleased.entries", "2026-08-20-zulu.md")));
    }

    [Fact]
    public async Task Compose_uses_default_consumer_layout_and_prints_the_composed_preview_when_no_output_is_selected()
    {
        using var root = TestDirectory.Create();
        root.WriteFile(
            "releases/unreleased.md",
            """
            # Unreleased
            <!-- appsurface:unreleased-entries section="included" -->
            """);
        root.WriteFile(
            "releases/unreleased.entries/2026-08-20-defaults.md",
            """
            <!-- appsurface:unreleased-entry section="included" -->
            - Default consumer layout works.
            """);
        using var console = new FakeInMemoryConsole();

        await new ReleaseComposeCommand(() => root.Path).ExecuteAsync(console, CancellationToken.None);

        var result = console.ReadOutputString();
        Assert.Contains("Validated 1 append-only release-note entry", result, StringComparison.Ordinal);
        Assert.Contains("Preview follows. Pass --output <path> --apply", result, StringComparison.Ordinal);
        Assert.Contains("- Default consumer layout works.", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(root.Path, "releases", "v1.0.0.md")));
    }

    [Fact]
    public async Task Compose_allows_a_missing_entries_directory_and_previews_the_unchanged_template()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "# Unreleased\n<!-- appsurface:unreleased-entries section=\"included\" -->");
        using var console = new FakeInMemoryConsole();

        await new ReleaseComposeCommand(() => root.Path).ExecuteAsync(console, CancellationToken.None);

        var result = console.ReadOutputString();
        Assert.Contains("Validated 0 append-only release-note entries", result, StringComparison.Ordinal);
        Assert.Contains("# Unreleased", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- appsurface:unreleased-entries", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_rejects_an_entry_section_that_is_not_declared_by_the_consumer_template()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("notes/template.md", "<!-- appsurface:unreleased-entries section=\"added\" -->");
        root.WriteFile(
            "notes/entries/2026-08-20-mismatch.md",
            """
            <!-- appsurface:unreleased-entry section="fixed" -->
            - Invalid section.
            """);
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            TemplatePath = "notes/template.md",
            EntriesDirectory = "notes/entries",
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("not declared by the living-note template", error.Message, StringComparison.Ordinal);
        Assert.Contains("Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_requires_an_explicit_distinct_output_before_it_writes()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "<!-- appsurface:unreleased-entries section=\"included\" -->");
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path) { Apply = true };

        var missingOutput = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--apply requires --output", missingOutput.Message, StringComparison.Ordinal);

        command.OutputPath = "releases/unreleased.md";
        var templateOutput = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--output must differ from --template", templateOutput.Message, StringComparison.Ordinal);
        Assert.Contains("<!-- appsurface:unreleased-entries section=\"included\" -->", File.ReadAllText(Path.Join(root.Path, "releases", "unreleased.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_rejects_paths_outside_the_selected_project_root()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "<!-- appsurface:unreleased-entries section=\"included\" -->");
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            OutputPath = "../outside.md",
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--output must stay below --root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_rejects_a_missing_project_root_and_template_with_documented_errors()
    {
        using var root = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var missingRoot = new ReleaseComposeCommand(() => root.Path)
        {
            RootDirectory = Path.Join(root.Path, "missing-root"),
        };

        var rootError = await Assert.ThrowsAsync<CommandException>(
            async () => await missingRoot.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("does not exist or is not a directory", rootError.Message, StringComparison.Ordinal);

        var missingTemplate = new ReleaseComposeCommand(() => root.Path);
        var templateError = await Assert.ThrowsAsync<CommandException>(
            async () => await missingTemplate.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("release-note template 'releases/unreleased.md' does not exist", templateError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_rejects_an_output_in_a_missing_directory_without_creating_it()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "<!-- appsurface:unreleased-entries section=\"included\" -->");
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            OutputPath = "missing/v1.0.0.md",
            Apply = true,
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("output directory for 'missing/v1.0.0.md' does not exist", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(root.Path, "missing")));
    }

    [Fact]
    public async Task Compose_rejects_a_template_below_a_linked_ancestor()
    {
        using var root = TestDirectory.Create();
        var externalDirectory = root.CreateDirectory("external-template");
        File.WriteAllText(Path.Join(externalDirectory, "unreleased.md"), "<!-- appsurface:unreleased-entries section=\"included\" -->");
        TestFileSystem.CreateDirectoryLinkOrSkip(Path.Join(root.Path, "linked-template"), externalDirectory);
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            TemplatePath = "linked-template/unreleased.md",
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--template must not traverse a symbolic link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_rejects_an_entries_directory_below_a_linked_ancestor()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "<!-- appsurface:unreleased-entries section=\"included\" -->");
        var externalDirectory = root.CreateDirectory("external-entries/entries");
        root.WriteFile(
            "external-entries/entries/2026-08-20-external.md",
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n- Must not be read.");
        TestFileSystem.CreateDirectoryLinkOrSkip(Path.Join(root.Path, "linked-entries"), Path.GetDirectoryName(externalDirectory)!);
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            EntriesDirectory = "linked-entries/entries",
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--entries must not traverse a symbolic link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_rejects_a_linked_output_ancestor_without_writing_outside_the_project_root()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "<!-- appsurface:unreleased-entries section=\"included\" -->");
        var externalDirectory = root.CreateDirectory("external-release-notes");
        var linkedDirectory = Path.Join(root.Path, "linked-release-notes");
        TestFileSystem.CreateDirectoryLinkOrSkip(linkedDirectory, externalDirectory);
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            OutputPath = "linked-release-notes/v1.0.0.md",
            Apply = true,
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--output must not traverse a symbolic link", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(externalDirectory, "v1.0.0.md")));
    }

    [Fact]
    public async Task Compose_rejects_a_case_variant_of_the_template_output()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "<!-- appsurface:unreleased-entries section=\"included\" -->");
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            OutputPath = "RELEASES/UNRELEASED.MD",
            Apply = true,
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("--output must differ from --template", error.Message, StringComparison.Ordinal);
        Assert.Contains("<!-- appsurface:unreleased-entries section=\"included\" -->", File.ReadAllText(Path.Join(root.Path, "releases", "unreleased.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_translates_invalid_path_input_to_the_documented_command_error()
    {
        using var root = TestDirectory.Create();
        root.WriteFile("releases/unreleased.md", "<!-- appsurface:unreleased-entries section=\"included\" -->");
        using var console = new FakeInMemoryConsole();
        var command = new ReleaseComposeCommand(() => root.Path)
        {
            OutputPath = "\0",
        };

        var error = await Assert.ThrowsAsync<CommandException>(
            async () => await command.ExecuteAsync(console, CancellationToken.None));

        Assert.Contains("selected release-note paths is invalid", error.Message, StringComparison.Ordinal);
        Assert.Contains("Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-release-compose", error.Message, StringComparison.Ordinal);
    }
}
