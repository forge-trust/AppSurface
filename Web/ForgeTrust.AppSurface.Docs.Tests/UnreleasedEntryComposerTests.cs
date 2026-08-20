using System.Text;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class UnreleasedEntryComposerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "AppSurfaceDocsUnreleasedEntryTests", Guid.NewGuid().ToString("N"));

    public UnreleasedEntryComposerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task LoadAsyncAcceptsUtf8BomAndRejectsCancellation()
    {
        var entriesDirectory = EntriesDirectory();
        Directory.CreateDirectory(entriesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(entriesDirectory, "2026-08-08-bom.md"),
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n- BOM entry.\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var entries = await UnreleasedEntryComposer.LoadAsync(entriesDirectory, CancellationToken.None);

        Assert.Equal("- BOM entry.", Assert.Single(entries.Entries).Markdown);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => UnreleasedEntryComposer.LoadAsync(entriesDirectory, cancellation.Token));
    }

    [Fact]
    public async Task LoadAsyncPreservesIndentationInEntryMarkdown()
    {
        var entriesDirectory = EntriesDirectory();
        Directory.CreateDirectory(entriesDirectory);
        await File.WriteAllTextAsync(
            TestPathUtils.PathUnder(entriesDirectory, "2026-08-08-indented-markdown.md"),
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n\n    Indented code content.\n\n    Still indented.\n");

        var entries = await UnreleasedEntryComposer.LoadAsync(entriesDirectory, CancellationToken.None);

        Assert.Equal("    Indented code content.\n\n    Still indented.", Assert.Single(entries.Entries).Markdown);
    }

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public async Task LoadAsyncRejectsInvalidEntries(string entryFileName, string content, string expectedMessage)
    {
        var entriesDirectory = EntriesDirectory();
        Directory.CreateDirectory(entriesDirectory);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(entriesDirectory, entryFileName), content);

        var exception = await Assert.ThrowsAsync<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.LoadAsync(entriesDirectory, CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsyncRejectsNestedDirectoriesAndSymbolicLinks()
    {
        var entriesDirectory = EntriesDirectory();
        Directory.CreateDirectory(Path.Combine(entriesDirectory, "nested"));

        var nestedDirectoryException = await Assert.ThrowsAsync<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.LoadAsync(entriesDirectory, CancellationToken.None));
        Assert.Contains("must be flat", nestedDirectoryException.Message, StringComparison.Ordinal);

        Directory.Delete(entriesDirectory, recursive: true);
        var externalDirectory = Path.Combine(_root, "external-directory");
        Directory.CreateDirectory(externalDirectory);
        if (!TryCreateSymbolicLink(entriesDirectory, externalDirectory, isDirectory: true))
        {
            return;
        }

        var directoryLinkException = await Assert.ThrowsAsync<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.LoadAsync(entriesDirectory, CancellationToken.None));
        Assert.Contains("must not be a symlink", directoryLinkException.Message, StringComparison.Ordinal);

        Directory.Delete(entriesDirectory);
        Directory.CreateDirectory(entriesDirectory);
        var externalFile = Path.Combine(_root, "external-entry.md");
        await File.WriteAllTextAsync(
            externalFile,
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n- Linked entry.\n");
        if (!TryCreateSymbolicLink(
                Path.Combine(entriesDirectory, "2026-08-08-linked-entry.md"),
                externalFile,
                isDirectory: false))
        {
            return;
        }

        var fileLinkException = await Assert.ThrowsAsync<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.LoadAsync(entriesDirectory, CancellationToken.None));
        Assert.Contains("must not be a symlink", fileLinkException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeValidatesTemplateMarkersAndEntryPaths()
    {
        var validTemplate = """
            # Unreleased
            <!-- appsurface:unreleased-entries section="taking-shape" -->
            <!-- appsurface:unreleased-entries section="included" -->
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """;

        var composed = UnreleasedEntryComposer.Compose(
            validTemplate,
            [
                new UnreleasedEntry("/entries/2026-08-08-zulu.md", "included", "- Zulu."),
                new UnreleasedEntry("/entries/2026-08-08-alpha.md", "included", "- Alpha.")
            ],
            Path.Join(_root, "releases", "unreleased.md"));

        Assert.Contains("- Alpha.\n\n- Zulu.", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- appsurface:unreleased-entries", composed, StringComparison.Ordinal);
        Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(validTemplate.Replace("included\" -->", "included\" -->\n<!-- appsurface:unreleased-entries section=\"included\" -->", StringComparison.Ordinal), [], Path.Join(_root, "releases", "unreleased.md")));
        var consumerTemplate = "# Next\n<!-- appsurface:unreleased-entries section=\"future\" -->";
        var consumerComposed = UnreleasedEntryComposer.Compose(
            consumerTemplate,
            [new UnreleasedEntry("/entries/2026-08-08-future.md", "future", "- Consumer-defined section.")],
            Path.Join(_root, "releases", "unreleased.md"));
        Assert.Contains("- Consumer-defined section.", consumerComposed, StringComparison.Ordinal);
        Assert.Equal("<!-- appsurface:unreleased-entries section=\"future\" -->", UnreleasedEntryComposer.MarkerFor("future"));
        Assert.Throws<ArgumentException>(() => UnreleasedEntryComposer.MarkerFor("future section"));
        Assert.True(UnreleasedEntryComposer.IsEntryPath("releases\\unreleased.entries\\2026-08-08-valid-entry.md"));
        Assert.False(UnreleasedEntryComposer.IsEntryPath("releases/unreleased.entries/nested/2026-08-08-valid-entry.md"));
        Assert.False(UnreleasedEntryComposer.IsEntryPath("releases/unreleased.entries/not-an-entry.md"));
        Assert.False(UnreleasedEntryComposer.IsEntryPath("docs/unreleased.entries/2026-08-08-valid-entry.md"));
    }

    [Fact]
    public void ComposeRejectsMarkersThatAreEmbeddedOrOnlyAppearInCodeBlocks()
    {
        const string validMarker = "<!-- appsurface:unreleased-entries section=\"included\" -->";
        var embeddedMarker = $"""
            # Unreleased
            {validMarker}
            Example: {validMarker}
            """;
        var codeBlockMarker = $"""
            # Unreleased
            {validMarker}

            ```markdown
            <!-- appsurface:unreleased-entries section="example" -->
            ```
            """;

        var embeddedException = Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(embeddedMarker, [], Path.Join(_root, "releases", "unreleased.md")));
        var codeBlockException = Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(codeBlockMarker, [], Path.Join(_root, "releases", "unreleased.md")));

        Assert.Contains("unsupported or malformed", embeddedException.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported or malformed", codeBlockException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeRejectsTerminalControlCharactersInTemplatesAndEntries()
    {
        const string template = "<!-- appsurface:unreleased-entries section=\"included\" -->";

        var templateException = Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(template + "\u001b[2J", [], Path.Join(_root, "releases", "unreleased.md")));
        var entryException = Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(
                template,
                [new UnreleasedEntry("/entries/2026-08-08-control.md", "included", "- \u001b[2J")],
                Path.Join(_root, "releases", "unreleased.md")));

        Assert.Contains("living-note template must not contain terminal control characters", templateException.Message, StringComparison.Ordinal);
        Assert.Contains("must not contain terminal control characters", entryException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeRebasesRelativeMarkdownLinksAndPreservesCode()
    {
        const string template = """
            # Unreleased
            <!-- appsurface:unreleased-entries section="taking-shape" -->
            <!-- appsurface:unreleased-entries section="included" -->
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """;
        var entryPath = Path.Join(EntriesDirectory(), "2026-08-08-rebased-links.md");
        var destinationPath = Path.Join(_root, "releases", "unreleased.md");
        var markdown = """
            - [Guide](../../Guides/README.md#start "Guide title")
            - [Local README](../README.md)
            - [Pointy](<../../Guides/Quick start.md>)
            - [Pointy with title](<../../Guides/Quick start.md> "Guide title")
            - ![Diagram](./assets/diagram.svg)
            - [`UnreleasedEntry`](../../tools/ForgeTrust.AppSurface.ReleaseContracts/UnreleasedEntryComposer.cs)
            - [Parenthesized](./guides/guide_(v1).md)
            - [Bare relative](Guides/README.md)
            - [Directory](./assets/)
            - [External](https://example.test/docs) and [Anchor](#details)
            - [Rooted](/docs/README.md) and [Windows rooted](\\server\share\README.md)
            - [Before code](../../Guides/before.md) and `[literal angle](<../../do-not-rewrite-angle.md>)`

            [guide-reference]: ../../README.md#release

            `[literal](../../do-not-rewrite.md)`

            ```sh
            [script](../../do-not-rewrite.sh)
            ```not-a-closer
            [still script](../../do-not-rewrite-still.sh)
            ```

            > ```sh
            > [quoted script](../../do-not-rewrite-quoted.sh)
            > ```

            >     [quoted indented script](../../do-not-rewrite-quoted-indented.sh)

            - Nested code:

                  [nested script](../../do-not-rewrite-indented.sh)

            An unmatched ` remains literal.
            - [After unmatched code](../../Guides/after.md)
            """;

        var composed = UnreleasedEntryComposer.Compose(
            template,
            [new UnreleasedEntry(entryPath, "included", markdown)],
            destinationPath);

        Assert.Contains("[Guide](../Guides/README.md#start \"Guide title\")", composed, StringComparison.Ordinal);
        Assert.Contains("[Local README](README.md)", composed, StringComparison.Ordinal);
        Assert.Contains("[Pointy](<../Guides/Quick start.md>)", composed, StringComparison.Ordinal);
        Assert.Contains("[Pointy with title](<../Guides/Quick start.md> \"Guide title\")", composed, StringComparison.Ordinal);
        Assert.Contains("![Diagram](unreleased.entries/assets/diagram.svg)", composed, StringComparison.Ordinal);
        Assert.Contains("[`UnreleasedEntry`](../tools/ForgeTrust.AppSurface.ReleaseContracts/UnreleasedEntryComposer.cs)", composed, StringComparison.Ordinal);
        Assert.Contains("[Parenthesized](unreleased.entries/guides/guide_(v1).md)", composed, StringComparison.Ordinal);
        Assert.Contains("[Bare relative](unreleased.entries/Guides/README.md)", composed, StringComparison.Ordinal);
        Assert.Contains("[Directory](unreleased.entries/assets/)", composed, StringComparison.Ordinal);
        Assert.Contains("[guide-reference]: ../README.md#release", composed, StringComparison.Ordinal);
        Assert.Contains("[External](https://example.test/docs) and [Anchor](#details)", composed, StringComparison.Ordinal);
        Assert.Contains("[Rooted](/docs/README.md) and [Windows rooted](\\\\server\\share\\README.md)", composed, StringComparison.Ordinal);
        Assert.Contains("[Before code](../Guides/before.md) and `[literal angle](<../../do-not-rewrite-angle.md>)`", composed, StringComparison.Ordinal);
        Assert.Contains("`[literal](../../do-not-rewrite.md)`", composed, StringComparison.Ordinal);
        Assert.Contains("[script](../../do-not-rewrite.sh)", composed, StringComparison.Ordinal);
        Assert.Contains("```not-a-closer\n[still script](../../do-not-rewrite-still.sh)\n```", composed, StringComparison.Ordinal);
        Assert.Contains("> [quoted script](../../do-not-rewrite-quoted.sh)", composed, StringComparison.Ordinal);
        Assert.Contains(">     [quoted indented script](../../do-not-rewrite-quoted-indented.sh)", composed, StringComparison.Ordinal);
        Assert.Contains("      [nested script](../../do-not-rewrite-indented.sh)", composed, StringComparison.Ordinal);
        Assert.Contains("[After unmatched code](../Guides/after.md)", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposePreservesLinksWhenSourceOrDestinationHasNoDirectory()
    {
        const string template = """
            # Unreleased
            <!-- appsurface:unreleased-entries section="taking-shape" -->
            <!-- appsurface:unreleased-entries section="included" -->
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """;
        const string markdown = "- [Guide](../Guides/README.md)";

        var sourceWithoutDirectory = UnreleasedEntryComposer.Compose(
            template,
            [new UnreleasedEntry("entry.md", "included", markdown)],
            Path.Join(_root, "releases", "unreleased.md"));
        var destinationWithoutDirectory = UnreleasedEntryComposer.Compose(
            template,
            [new UnreleasedEntry(Path.Join(_root, "releases", "unreleased.entries", "entry.md"), "included", markdown)],
            "unreleased.md");

        Assert.Contains(markdown, sourceWithoutDirectory, StringComparison.Ordinal);
        Assert.Contains(markdown, destinationWithoutDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposePreservesAnUnmatchedInlineCodeDelimiterAtTheEndOfMarkdown()
    {
        const string template = """
            # Unreleased
            <!-- appsurface:unreleased-entries section="taking-shape" -->
            <!-- appsurface:unreleased-entries section="included" -->
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """;
        const string markdown = "- Terminal unmatched `";

        var composed = UnreleasedEntryComposer.Compose(
            template,
            [new UnreleasedEntry(
                Path.Join(_root, "releases", "unreleased.entries", "2026-08-08-terminal-inline-code.md"),
                "included",
                markdown)],
            Path.Join(_root, "releases", "unreleased.md"));

        Assert.Contains(markdown, composed, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> InvalidEntries =>
    [
        ["not-an-entry.md", "- Missing directive.\n", "YYYY-MM-DD-topic.md"],
        ["2026-13-40-invalid-date.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\n- Invalid date.\n", "YYYY-MM-DD-topic.md"],
        ["2026-08-08-invalid-directive.md", "<!-- appsurface:unreleased-entry section=\"included\"\n- Missing directive terminator.\n", "must begin with"],
        ["2026-08-08-invalid-section.md", "<!-- appsurface:unreleased-entry section=\"Future\" -->\n- Invalid section.\n", "uses invalid section"],
        ["2026-08-08-empty.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\n", "must contain Markdown"],
        ["2026-08-08-marker.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\n<!-- appsurface:unreleased-entries section=\"included\" -->\n", "must not contain an AppSurface"],
        ["2026-08-08-top-level.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\n# Invalid heading\n", "must not introduce a top-level"],
        ["2026-08-08-setext-heading.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\nInjected\n========\n", "must not introduce a top-level"],
        ["2026-08-08-setext-crlf-heading.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\r\nInjected\r\n========\r\n", "must not introduce a top-level"],
        ["2026-08-08-indented-heading.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\n  ## Injected\n", "must not introduce a top-level"]
    ];

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string EntriesDirectory()
    {
        return Path.Combine(_root, "releases", UnreleasedEntryComposer.EntriesDirectoryName);
    }

    private static bool TryCreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }

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
}
