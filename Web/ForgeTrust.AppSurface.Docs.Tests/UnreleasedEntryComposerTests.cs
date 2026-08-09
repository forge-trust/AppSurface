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
            ]);

        Assert.Contains("- Alpha.\n\n- Zulu.", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- appsurface:unreleased-entries", composed, StringComparison.Ordinal);
        Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(validTemplate.Replace("included\" -->", "included\" -->\n<!-- appsurface:unreleased-entries section=\"included\" -->", StringComparison.Ordinal), []));
        Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(validTemplate + "\n<!-- appsurface:unreleased-entries section=\"future\" -->", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnreleasedEntryComposer.MarkerFor("future"));
        Assert.True(UnreleasedEntryComposer.IsEntryPath("releases\\unreleased.entries\\2026-08-08-valid-entry.md"));
        Assert.False(UnreleasedEntryComposer.IsEntryPath("releases/unreleased.entries/nested/2026-08-08-valid-entry.md"));
        Assert.False(UnreleasedEntryComposer.IsEntryPath("releases/unreleased.entries/not-an-entry.md"));
    }

    public static IEnumerable<object[]> InvalidEntries =>
    [
        ["not-an-entry.md", "- Missing directive.\n", "YYYY-MM-DD-topic.md"],
        ["2026-13-40-invalid-date.md", "<!-- appsurface:unreleased-entry section=\"included\" -->\n- Invalid date.\n", "YYYY-MM-DD-topic.md"],
        ["2026-08-08-invalid-directive.md", "<!-- appsurface:unreleased-entry section=\"included\"\n- Missing directive terminator.\n", "must begin with"],
        ["2026-08-08-unsupported-section.md", "<!-- appsurface:unreleased-entry section=\"future\" -->\n- Unsupported section.\n", "uses unsupported section"],
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
