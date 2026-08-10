using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.ReleaseContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseToolTests : IDisposable
{
    private const string TaggedReleaseNoteContent = "# Release 0.1.0-preview.1\n";
    private static string PreparedReleaseSidecarContent(string version) => $"""
        release:
          schema: appsurface-release-sidecar-v1
          state: prepared
          id: v{version}
        title: Release {version}
        trust:
          status: Prepared
        """;

    private static string TaggedReleaseSidecarContent => PreparedReleaseSidecarContent("0.1.0-preview.1");
    private const string CurrentReleaseSidecarContent = "title: Current coordinated release\nsummary: Permanent pointer metadata.\n";

    private readonly string _repositoryRoot;
    private readonly string _externalRoot;

    public ReleaseToolTests()
    {
        _repositoryRoot = Path.Join(Path.GetTempPath(), "ReleaseToolTests", Guid.NewGuid().ToString("N"));
        _externalRoot = _repositoryRoot + "-external";
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task HelpAndUnknownCommandUseDocumentedUsagePaths()
    {
        var help = await RunAsync(["--help"], new FakeCommandRunner());
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("USAGE", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("check", help.Stdout, StringComparison.Ordinal);

        var unknown = await RunAsync(["frobnicate"], new FakeCommandRunner());
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("frobnicate", unknown.Stderr, StringComparison.Ordinal);

        var unknownWithReleaseVersion = await RunAsync(["frobnicate", "--version", "0.1.0"], new FakeCommandRunner());
        Assert.Equal(1, unknownWithReleaseVersion.ExitCode);
        Assert.Contains("Unrecognized command 'frobnicate'.", unknownWithReleaseVersion.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("System.FormatException", unknownWithReleaseVersion.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsMissingRequiredSources()
    {
        await WriteFileAsync(
            "CHANGELOG.md",
            "# Changelog\n");

        var result = await RunAsync(["check", "--version", "0.1.0-preview.1"], new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-required-file-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/unreleased.md", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsVersionWithLeadingTagPrefix()
    {
        var result = await RunAsync(["check", "--version", "v0.1.0"], new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-version-leading-v", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Problem:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Cause:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Fix:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Docs:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseFailuresUseDiagnosticEnvelope()
    {
        var missingVersion = await RunAsync(["check"], new FakeCommandRunner());
        Assert.Equal(1, missingVersion.ExitCode);
        Assert.Contains("Code: release-version-required", missingVersion.Stderr, StringComparison.Ordinal);

        var missingOptionValue = await RunAsync(["check", "--version"], new FakeCommandRunner());
        Assert.Equal(1, missingOptionValue.ExitCode);
        Assert.Contains("Code: release-version-required", missingOptionValue.Stderr, StringComparison.Ordinal);

        var invalidDate = await RunAsync(["prepare", "--version", "0.1.0-preview.1", "--date", "05/25/2026"], new FakeCommandRunner());
        Assert.Equal(1, invalidDate.ExitCode);
        Assert.Contains("Code: release-date-invalid", invalidDate.Stderr, StringComparison.Ordinal);

        var unknownOption = await RunAsync(["check", "--version", "0.1.0-preview.1", "--bogus"], new FakeCommandRunner());
        Assert.Equal(1, unknownOption.ExitCode);
        Assert.Contains("--bogus", unknownOption.Stderr, StringComparison.Ordinal);

        var invalidVersion = await RunAsync(["check", "--version", "01.0.0"], new FakeCommandRunner());
        Assert.Equal(1, invalidVersion.ExitCode);
        Assert.Contains("Code: release-version-invalid", invalidVersion.Stderr, StringComparison.Ordinal);
        Assert.Contains("Severity: error", invalidVersion.Stderr, StringComparison.Ordinal);

        var overflowingVersion = await RunAsync(["check", "--version", "999999999999999999.0.0"], new FakeCommandRunner());
        Assert.Equal(1, overflowingVersion.ExitCode);
        Assert.Contains("Code: release-version-invalid", overflowingVersion.Stderr, StringComparison.Ordinal);

        var missingTag = await RunAsync(["publish", "--version", "0.1.0-preview.1"], new FakeCommandRunner());
        Assert.Equal(1, missingTag.ExitCode);
        Assert.Contains("Code: release-tag-required", missingTag.Stderr, StringComparison.Ordinal);

        var mismatchedTag = await RunAsync(["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.2"], new FakeCommandRunner());
        Assert.Equal(1, mismatchedTag.ExitCode);
        Assert.Contains("Code: release-tag-version-mismatch", mismatchedTag.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnlyOptionsAreRejectedByOtherCommands()
    {
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--fail-on-warnings"],
            new FakeCommandRunner());

        Assert.Equal(1, prepare.ExitCode);
        Assert.Contains("--fail-on-warnings", prepare.Stderr, StringComparison.Ordinal);

        var publish = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--allow-existing-targets"],
            new FakeCommandRunner());

        Assert.Equal(1, publish.ExitCode);
        Assert.Contains("--allow-existing-targets", publish.Stderr, StringComparison.Ordinal);

        var prepareWithDocs = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--docs-catalog", "dist/docs/versions.json"],
            new FakeCommandRunner());

        Assert.Equal(1, prepareWithDocs.ExitCode);
        Assert.Contains("--docs-catalog", prepareWithDocs.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareDryRunDoesNotWriteGeneratedFiles()
    {
        await SeedRepositoryAsync();
        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25", "--dry-run"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Join(_repositoryRoot, "releases", "v0.1.0-preview.1.md")));
        Assert.Contains("## Manual review gate", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("## Release evidence bundle", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("## Dry-run plan", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.release.json", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.evidence.json", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareComposesAndArchivesAppendOnlyUnreleasedEntries()
    {
        await SeedRepositoryAsync();
        const string entryPath = "releases/unreleased.entries/2026-08-08-release-workflow.md";
        await WriteFileAsync(
            entryPath,
            """
            <!-- appsurface:unreleased-entry section="included" -->
            ### Release workflow

            - Parallel pull requests add independent release-note entries.
            """);

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        var releaseNote = await ReadFileAsync("releases/v0.1.0-preview.1.md");
        Assert.Contains("### Release workflow", releaseNote, StringComparison.Ordinal);
        Assert.Contains("Parallel pull requests add independent release-note entries.", releaseNote, StringComparison.Ordinal);
        Assert.False(File.Exists(RepositoryPath(entryPath)));
        Assert.Contains("## Archived unreleased entries", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(entryPath, result.Stdout, StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(await ReadFileAsync("releases/v0.1.0-preview.1.release.json"));
        Assert.Equal(
            [entryPath],
            manifest.RootElement.GetProperty("consumedUnreleasedEntryPaths").EnumerateArray().Select(path => path.GetString()));

        var nextUnreleased = await ReadFileAsync("releases/unreleased.md");
        Assert.Contains("<!-- appsurface:unreleased-entries section=\"included\" -->", nextUnreleased, StringComparison.Ordinal);
        Assert.DoesNotContain("Parallel pull requests add independent release-note entries.", nextUnreleased, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsMalformedAppendOnlyUnreleasedEntry()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.entries/not-an-entry.md",
            "- Missing the required directive.\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-unreleased-entry-invalid", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("YYYY-MM-DD-topic.md", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsNestedAppendOnlyEntryDirectory()
    {
        await SeedRepositoryAsync();
        Directory.CreateDirectory(RepositoryPath("releases/unreleased.entries/nested"));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must be flat", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsAppendOnlyEntryWithUnsupportedSection()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.entries/2026-08-08-unsupported-section.md",
            """
            <!-- appsurface:unreleased-entry section="unknown" -->
            - This section is not part of the living-note template.
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("uses unsupported section", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsAppendOnlyEntryWithoutMarkdown()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.entries/2026-08-08-empty.md",
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must contain Markdown", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsTemplateWithDuplicateAppendOnlyMarker()
    {
        await SeedRepositoryAsync();
        var template = await ReadFileAsync("releases/unreleased.md");
        await WriteFileAsync(
            "releases/unreleased.md",
            template.Replace(
                "<!-- appsurface:unreleased-entries section=\"included\" -->",
                "<!-- appsurface:unreleased-entries section=\"included\" -->\n<!-- appsurface:unreleased-entries section=\"included\" -->",
                StringComparison.Ordinal));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must contain exactly one", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreleasedEntryComposerComposesEntriesAtSectionBottomInFilenameOrder()
    {
        var template = """
            # Unreleased

            ## Taking shape
            <!-- appsurface:unreleased-entries section="taking-shape" -->

            ## Included
            - Existing note.
            <!-- appsurface:unreleased-entries section="included" -->

            ## Migration watch
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """;

        var composed = UnreleasedEntryComposer.Compose(
            template,
            [
                new UnreleasedEntry("/entries/2026-08-08-zulu.md", "included", "- Zulu entry."),
                new UnreleasedEntry("/entries/2026-08-08-alpha.md", "included", "- Alpha entry."),
                new UnreleasedEntry("/entries/2026-08-08-taking-shape.md", "taking-shape", "- Shaping entry.")
            ]);

        Assert.Contains("- Existing note.\n- Alpha entry.\n\n- Zulu entry.", composed, StringComparison.Ordinal);
        Assert.Contains("## Taking shape\n- Shaping entry.", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- appsurface:unreleased-entries", composed, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreleasedEntryComposerRejectsUnsupportedTemplateMarkerAndPaths()
    {
        var unsupportedMarker = """
            # Unreleased
            <!-- appsurface:unreleased-entries section="taking-shape" -->
            <!-- appsurface:unreleased-entries section="included" -->
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            <!-- appsurface:unreleased-entries section="future" -->
            """;

        var templateException = Assert.Throws<UnreleasedEntryException>(
            () => UnreleasedEntryComposer.Compose(unsupportedMarker, []));
        Assert.Contains("no unsupported entry markers", templateException.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => UnreleasedEntryComposer.MarkerFor("future"));
        Assert.True(UnreleasedEntryComposer.IsEntryPath("releases\\unreleased.entries\\2026-08-08-valid-entry.md"));
        Assert.False(UnreleasedEntryComposer.IsEntryPath("releases/unreleased.entries/nested/2026-08-08-valid-entry.md"));
        Assert.False(UnreleasedEntryComposer.IsEntryPath("releases/unreleased.entries/not-an-entry.md"));
        Assert.Throws<ArgumentException>(() => UnreleasedEntryComposer.IsEntryPath(" "));
    }

    [Fact]
    public async Task UnreleasedEntryComposerAcceptsUtf8BomAndHonorsCancellation()
    {
        var entriesDirectory = RepositoryPath("releases/unreleased.entries");
        Directory.CreateDirectory(entriesDirectory);
        var entryPath = TestPathUtils.PathUnder(entriesDirectory, "2026-08-08-bom-entry.md");
        await File.WriteAllTextAsync(
            entryPath,
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n- BOM entry.\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var entries = await UnreleasedEntryComposer.LoadAsync(entriesDirectory, CancellationToken.None);

        var entry = Assert.Single(entries.Entries);
        Assert.Equal("included", entry.Section);
        Assert.Equal("- BOM entry.", entry.Markdown);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => UnreleasedEntryComposer.LoadAsync(entriesDirectory, cancellation.Token));
    }

    [Fact]
    public async Task CheckRejectsSymlinkedAppendOnlyEntrySources()
    {
        await SeedRepositoryAsync();
        var entriesDirectory = RepositoryPath("releases/unreleased.entries");
        var externalEntriesDirectory = ExternalPath("unreleased.entries");
        Directory.CreateDirectory(Path.GetDirectoryName(entriesDirectory)!);
        Directory.CreateDirectory(externalEntriesDirectory);
        if (!TryCreateSymbolicLink(entriesDirectory, externalEntriesDirectory, isDirectory: true))
        {
            return;
        }

        var directoryResult = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, directoryResult.ExitCode);
        Assert.Contains("must not be a symlink", directoryResult.Stdout, StringComparison.Ordinal);

        Directory.Delete(entriesDirectory);
        Directory.CreateDirectory(entriesDirectory);
        var externalEntry = ExternalPath("linked-entry.md");
        Directory.CreateDirectory(Path.GetDirectoryName(externalEntry)!);
        await File.WriteAllTextAsync(
            externalEntry,
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n- Linked entry.\n");
        var linkedEntry = TestPathUtils.PathUnder(entriesDirectory, "2026-08-08-linked-entry.md");
        if (!TryCreateSymbolicLink(linkedEntry, externalEntry, isDirectory: false))
        {
            return;
        }

        var entryResult = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, entryResult.ExitCode);
        Assert.Contains("must not be a symlink", entryResult.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsShortOverlappingAppendOnlyEntryDirective()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.entries/2026-08-08-short-directive.md",
            """
            <!-- appsurface:unreleased-entry section=" -->
            - This malformed directive shares its one quote between the prefix and suffix checks.
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-unreleased-entry-invalid", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("must begin with", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("## Competing section")]
    [InlineData("  ## Indented competing section")]
    [InlineData("Competing section\n=================")]
    [InlineData("Competing section\r\n=================\r\n")]
    public async Task CheckRejectsAppendOnlyEntryThatCreatesATopLevelSection(string heading)
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.entries/2026-08-08-top-level-heading.md",
            $"""
            <!-- appsurface:unreleased-entry section="included" -->
            {heading}
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not introduce a top-level '#' or '##' section", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsAppendOnlyEntryThatContainsACompositionMarker()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.entries/2026-08-08-composition-marker.md",
            """
            <!-- appsurface:unreleased-entry section="included" -->
            <!-- appsurface:unreleased-entries section="included" -->

            - This marker would corrupt composition.
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not contain an AppSurface unreleased-entry composition marker", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsTemplateThatLacksAMarkerEvenWhenNoEntriesExist()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.md",
            """
            # Unreleased

            ## What is taking shape

            - Missing the taking-shape marker.

            ## Included in the next coordinated version

            <!-- appsurface:unreleased-entries section="included" -->

            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-unreleased-entry-invalid", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("taking-shape", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareWritesExternalReportDuringDryRun()
    {
        await SeedRepositoryAsync();
        var reportPath = Path.Join(Path.GetTempPath(), "ReleaseToolReports", Guid.NewGuid().ToString("N"), "prepare-report.md");

        var result = await RunAsync(
            [
                "prepare",
                "--version",
                "0.1.0-preview.1",
                "--date",
                "2026-05-25",
                "--dry-run",
                "--report",
                reportPath
            ],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(reportPath));
        var report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("# Release readiness report", report, StringComparison.Ordinal);
        Assert.Contains("## Manual review gate", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareWritesReleaseArtifactsAndFreezesCoordinatedCurrentPointer()
    {
        await SeedRepositoryAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            stdout,
            stderr,
            _repositoryRoot,
            commandRunner: FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, exitCode);
        var report = stdout.ToString();
        Assert.Contains("## Manual review gate", report, StringComparison.Ordinal);
        Assert.Contains("## Release evidence bundle", report, StringComparison.Ordinal);
        Assert.Contains("## Files written", report, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.md", report, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.evidence.json", report, StringComparison.Ordinal);
        Assert.Contains("releases/current.md", report, StringComparison.Ordinal);
        Assert.DoesNotContain("releases/current.md.yml", report, StringComparison.Ordinal);
        Assert.Contains("CHANGELOG.md", report, StringComparison.Ordinal);

        var releaseNote = await ReadFileAsync("releases/v0.1.0-preview.1.md");
        Assert.Contains("# Release 0.1.0-preview.1", releaseNote, StringComparison.Ordinal);

        var sidecar = await ReadFileAsync("releases/v0.1.0-preview.1.md.yml");
        Assert.Contains("title: Release 0.1.0-preview.1", sidecar, StringComparison.Ordinal);
        Assert.Contains("state: prepared", sidecar, StringComparison.Ordinal);
        Assert.Contains("status: Prepared", sidecar, StringComparison.Ordinal);
        Assert.DoesNotContain("status: Tagged", sidecar, StringComparison.Ordinal);

        var manifestJson = await ReadFileAsync("releases/v0.1.0-preview.1.release.json");
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal("appsurface-release-manifest-v2", manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal("0.1.0-preview.1", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("prerelease", manifest.RootElement.GetProperty("releaseClassification").GetString());
        Assert.Equal("abc123", manifest.RootElement.GetProperty("preparationBaseCommit").GetString());
        Assert.Contains(
            manifest.RootElement.GetProperty("generatedFiles").EnumerateArray(),
            path => string.Equals(path.GetString(), "releases/v0.1.0-preview.1.evidence.json", StringComparison.Ordinal));
        Assert.Empty(manifest.RootElement.GetProperty("consumedUnreleasedEntryPaths").EnumerateArray());

        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        using var evidence = JsonDocument.Parse(evidenceJson);
        Assert.Equal("appsurface-release-evidence-bundle-v2", evidence.RootElement.GetProperty("schema").GetString());
        Assert.Equal("0.1.0-preview.1", evidence.RootElement.GetProperty("version").GetString());
        Assert.Equal("releases/v0.1.0-preview.1.release.json", evidence.RootElement.GetProperty("releaseManifestPath").GetString());
        var artifactDigests = evidence.RootElement.GetProperty("releaseArtifactDigests").EnumerateArray().ToArray();
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/v0.1.0-preview.1.md", StringComparison.Ordinal));
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/v0.1.0-preview.1.md.yml", StringComparison.Ordinal));
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/v0.1.0-preview.1.release.json", StringComparison.Ordinal));
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/current.md", StringComparison.Ordinal));
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/current.md.yml", StringComparison.Ordinal));
        var resolutions = evidence.RootElement.GetProperty("coordinatedPackageReleaseNoteResolutions").EnumerateArray().ToArray();
        Assert.Empty(resolutions);
        Assert.Equal("notConfigured", evidence.RootElement.GetProperty("docsArchive").GetProperty("status").GetString());
        Assert.NotEmpty(evidence.RootElement.GetProperty("subject").GetProperty("sha256").GetString()!);

        var currentRelease = await ReadFileAsync("releases/current.md");
        Assert.Contains("./v0.1.0-preview.1.md", currentRelease, StringComparison.Ordinal);
        var currentReleaseSidecar = await ReadFileAsync("releases/current.md.yml");
        Assert.Equal(CurrentReleaseSidecarContent, currentReleaseSidecar);

        var packageIndex = await ReadFileAsync("packages/package-index.yml");
        Assert.DoesNotContain("release_notes_path: releases/v0.1.0-preview.1.md", packageIndex, StringComparison.Ordinal);
        Assert.Contains("classification: support", packageIndex, StringComparison.Ordinal);
        Assert.Contains("release_notes_path: releases/unreleased.md", packageIndex, StringComparison.Ordinal);

        var changelog = await ReadFileAsync("CHANGELOG.md");
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", changelog, StringComparison.Ordinal);
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release manifest: `releases/v0.1.0-preview.1.release.json`", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release evidence bundle: `releases/v0.1.0-preview.1.evidence.json`", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("- Current work.", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("[v0.1.0-preview.1.release.json]", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("## No tagged releases yet", changelog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TagMessageRendersCanonicalBindingForPreparedArtifacts()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        var manifest = CreateReleaseManifestJson();
        var evidence = CreateReleaseEvidenceJson(manifest);
        runner.Add("git rev-parse HEAD", new CommandResult(0, "abc123\n", ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.md", new CommandResult(0, TaggedReleaseNoteContent, ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.md.yml", new CommandResult(0, TaggedReleaseSidecarContent, ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.release.json", new CommandResult(0, manifest, ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.evidence.json", new CommandResult(0, evidence, ""));

        var result = await RunAsync(["tag-message", "--version", "0.1.0-preview.1"], runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("AppSurface-Release-Id: v0.1.0-preview.1", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"AppSurface-Release-Prepared-Sidecar-Sha256: {ReleaseEvidence.ComputeSha256Hex(TaggedReleaseSidecarContent)}", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"AppSurface-Release-Manifest-Sha256: {ReleaseEvidence.ComputeSha256Hex(manifest)}", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TagMessageUsesHeadArtifactsInsteadOfDirtyWorktreeFiles()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        var manifest = CreateReleaseManifestJson();
        var evidence = CreateReleaseEvidenceJson(manifest);
        runner.Add("git rev-parse HEAD", new CommandResult(0, "abc123\n", ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.md", new CommandResult(0, TaggedReleaseNoteContent, ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.md.yml", new CommandResult(0, TaggedReleaseSidecarContent, ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.release.json", new CommandResult(0, manifest, ""));
        runner.Add("git show abc123:releases/v0.1.0-preview.1.evidence.json", new CommandResult(0, evidence, ""));

        var beforeDirtyWorktree = await RunAsync(["tag-message", "--version", "0.1.0-preview.1"], runner);
        await WriteFileAsync("releases/v0.1.0-preview.1.md.yml", "untrusted dirty worktree sidecar\n");
        var afterDirtyWorktree = await RunAsync(["tag-message", "--version", "0.1.0-preview.1"], runner);

        Assert.Equal(0, beforeDirtyWorktree.ExitCode);
        Assert.Equal(0, afterDirtyWorktree.ExitCode);
        Assert.Equal(beforeDirtyWorktree.Stdout, afterDirtyWorktree.Stdout);
    }

    [Fact]
    public async Task TagMessageRejectsMissingPreparedArtifactFromHead()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        runner.Add("git rev-parse HEAD", new CommandResult(0, "abc123\n", ""));

        var result = await RunAsync(["tag-message", "--version", "0.1.0-preview.1"], runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-message-artifact-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TagMessageRejectsBlankHeadOutput()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        runner.Add("git rev-parse HEAD", new CommandResult(0, "   \n", ""));

        var result = await RunAsync(["tag-message", "--version", "0.1.0-preview.1"], runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-message-head-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRendersV2TaggedProjectionWithoutMutatingPreparedSource()
    {
        await SeedRepositoryAsync();
        var runner = await CreateSuccessfulV2PublishRunnerAsync();

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("state: tagged", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("status: Tagged", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("GitHub Release publication is bound to that annotated tag.", result.Stdout, StringComparison.Ordinal);
        var preparedSidecar = await ReadFileAsync("releases/v0.1.0-preview.1.md.yml");
        Assert.Contains("state: prepared", preparedSidecar, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaggedProjectionResolverDefaultsMissingTagToVersionTag()
    {
        await SeedRepositoryAsync();
        var version = SemVer.Parse("0.1.0-preview.1");
        var resolver = new ReleaseTaggedProjectionResolver(
            new ReleaseWorkspace(_repositoryRoot),
            CreateSuccessfulPublishRunner());
        var options = new ReleaseOptions(
            "inspect",
            _repositoryRoot,
            version,
            Tag: null,
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var projection = await resolver.ResolveAsync(options, CancellationToken.None);

        Assert.Equal(version.TagName, projection.Tag);
    }

    [Fact]
    public async Task InspectAcceptsNumericV2PreparationBaseCommit()
    {
        await SeedRepositoryAsync();
        var preparationBaseCommit = new string('0', 40);
        var runner = await CreateSuccessfulV2PublishRunnerAsync(preparationBaseCommit);

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task InspectRejectsV2PreparationBaseCommitWithNonHexCharacter()
    {
        await SeedRepositoryAsync();
        var runner = await CreateSuccessfulV2PublishRunnerAsync(new string('g', 40));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-preparation-base-commit-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAcceptsCrLfAnnotatedTagObject()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var binding = CreateReleaseTagBinding();
        var tagObject = CreateAnnotatedTagObject();
        var messageStart = tagObject.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        runner.Add(
            "git cat-file -p refs/tags/v0.1.0-preview.1",
            new CommandResult(
                0,
                (tagObject[..messageStart]
                    + $"Release notes reviewed\nSigned-off-by: Release Tests <release-tests@example.test>\n\n{binding.Render()}")
                    .Replace("\n", "\r\n", StringComparison.Ordinal),
                ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("state: tagged", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0000")]
    [InlineData("+0a00")]
    [InlineData("+00a0")]
    [InlineData("+0060")]
    [InlineData("+1401")]
    [InlineData("+1460")]
    [InlineData("+1500")]
    public async Task InspectRejectsImpossibleTaggerOffsets(string offset)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "git cat-file -p refs/tags/v0.1.0-preview.1",
            new CommandResult(0, CreateAnnotatedTagObject(offset), ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-tagger-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRejectsTaggerWithNonNumericEpoch()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "git cat-file -p refs/tags/v0.1.0-preview.1",
            new CommandResult(0, CreateAnnotatedTagObject().Replace("1770000000", "not-an-epoch", StringComparison.Ordinal), ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-tagger-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRejectsAnnotatedTagWithoutTaggerHeader()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var binding = CreateReleaseTagBinding();
        runner.Add(
            "git cat-file -p refs/tags/v0.1.0-preview.1",
            new CommandResult(0, $"object abc123\ntype commit\ntag v0.1.0-preview.1\n\n{binding.Render()}", ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-tagger-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRejectsTaggerLineInAnnotatedTagMessageWithoutTaggerHeader()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var binding = CreateReleaseTagBinding();
        var tagObject = $"object abc123\ntype commit\ntag v0.1.0-preview.1\n\ntagger Release Tests <release-tests@example.test> 1770000000 +0000\n{binding.Render()}";
        runner.Add("git cat-file -p refs/tags/v0.1.0-preview.1", new CommandResult(0, tagObject, ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-tagger-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAcceptsNegativeTaggerOffset()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "git cat-file -p refs/tags/v0.1.0-preview.1",
            new CommandResult(0, CreateAnnotatedTagObject("-0500"), ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("state: tagged", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRejectsOutOfRangeTaggerTimestamp()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "git cat-file -p refs/tags/v0.1.0-preview.1",
            new CommandResult(0, CreateAnnotatedTagObject().Replace("1770000000", "253402300800", StringComparison.Ordinal), ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-tagger-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRejectsMissingAnnotatedTagBeforeReadingArtifacts()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        runner.Add("git cat-file -t refs/tags/v0.1.0-preview.1", new CommandResult(1, "", ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectReportsTagLookupStderr()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        runner.Add("git cat-file -t refs/tags/v0.1.0-preview.1", new CommandResult(1, "", "tag reference is unavailable"));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("tag reference is unavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "release-tag-trailer-missing")]
    [InlineData("duplicate", "release-tag-trailer-invalid")]
    [InlineData("unknown", "release-tag-trailer-invalid")]
    [InlineData("malformed-digest", "release-tag-trailer-invalid")]
    [InlineData("nonhex-digest", "release-tag-trailer-invalid")]
    [InlineData("non-digit-range-digest", "release-tag-trailer-invalid")]
    [InlineData("wrong-release-id", "release-tag-trailer-mismatch")]
    [InlineData("stale", "release-tag-trailer-mismatch")]
    public async Task InspectRejectsInvalidOrStaleReleaseTagBinding(string mutation, string expectedCode)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var tagObject = CreateAnnotatedTagObject();
        var binding = CreateReleaseTagBinding();
        var messageStart = tagObject.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        tagObject = mutation switch
        {
            "missing" => tagObject[..messageStart] + "release prepared\n",
            "duplicate" => tagObject.Replace(
                "\n\n",
                $"\n\n{ReleaseTagBinding.ReleaseIdKey}: {binding.ReleaseId}\n",
                StringComparison.Ordinal),
            "unknown" => tagObject.Replace(
                "\n\n",
                "\n\nAppSurface-Release-Unrecognized: value\n",
                StringComparison.Ordinal),
            "malformed-digest" => tagObject.Replace(
                binding.ManifestSha256,
                binding.ManifestSha256.ToUpperInvariant(),
                StringComparison.Ordinal),
            "nonhex-digest" => tagObject.Replace(
                binding.ManifestSha256,
                new string('g', 64),
                StringComparison.Ordinal),
            "non-digit-range-digest" => tagObject.Replace(
                binding.ManifestSha256,
                new string(':', 64),
                StringComparison.Ordinal),
            "wrong-release-id" => tagObject.Replace(
                binding.ReleaseId,
                "v0.1.0-preview.2",
                StringComparison.Ordinal),
            "stale" => tagObject.Replace(
                binding.ManifestSha256,
                new string('0', 64),
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };
        runner.Add("git cat-file -p refs/tags/v0.1.0-preview.1", new CommandResult(0, tagObject, ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Code: {expectedCode}", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reserved-final-key", "release-tag-trailer-invalid")]
    [InlineData("ordinary-final-key", "release-tag-trailer-missing")]
    [InlineData("padded-value", "release-tag-trailer-invalid")]
    [InlineData("empty-value", "release-tag-trailer-invalid")]
    [InlineData("empty-message", "release-tag-trailer-missing")]
    [InlineData("short-digest", "release-tag-trailer-invalid")]
    [InlineData("missing-message-separator", "release-tag-trailer-missing")]
    public async Task InspectRejectsMalformedFinalReleaseTrailerBlock(string mutation, string expectedCode)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var tagObject = CreateAnnotatedTagObject();
        var binding = CreateReleaseTagBinding();
        var messageStart = tagObject.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        tagObject = mutation switch
        {
            "reserved-final-key" => tagObject.Replace(
                ReleaseTagBinding.ReleaseIdKey,
                "AppSurface-Release-Not-Id",
                StringComparison.Ordinal),
            "ordinary-final-key" => tagObject.Replace(
                $"{ReleaseTagBinding.ReleaseIdKey}: {binding.ReleaseId}",
                "Unrelated: value",
                StringComparison.Ordinal),
            "padded-value" => tagObject.Replace(
                $"{ReleaseTagBinding.ReleaseIdKey}: {binding.ReleaseId}",
                $"{ReleaseTagBinding.ReleaseIdKey}:  {binding.ReleaseId}",
                StringComparison.Ordinal),
            "empty-value" => tagObject.Replace(
                $"{ReleaseTagBinding.ReleaseIdKey}: {binding.ReleaseId}",
                $"{ReleaseTagBinding.ReleaseIdKey}: ",
                StringComparison.Ordinal),
            "empty-message" => tagObject[..messageStart],
            "short-digest" => tagObject.Replace(binding.ManifestSha256, "abc", StringComparison.Ordinal),
            "missing-message-separator" => tagObject.Replace("\n\n", "\n", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };
        runner.Add("git cat-file -p refs/tags/v0.1.0-preview.1", new CommandResult(0, tagObject, ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Code: {expectedCode}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRejectsMalformedTaggerHeader()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var tagObject = CreateAnnotatedTagObject().Replace(
            "tagger Release Tests <release-tests@example.test> 1770000000 +0000",
            "tagger ",
            StringComparison.Ordinal);
        runner.Add("git cat-file -p refs/tags/v0.1.0-preview.1", new CommandResult(0, tagObject, ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-tagger-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectLeavesOutputUntouchedWhenTagValidationFails()
    {
        await SeedRepositoryAsync();
        var output = ExternalPath("tagged-projection.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "keep this sentinel\n");
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "git cat-file -p refs/tags/v0.1.0-preview.1",
            new CommandResult(0, CreateAnnotatedTagObject("+1401"), ""));

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--out", output],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-tagger-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("keep this sentinel\n", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task InspectWritesValidatedProjectionToExternalOutputPath()
    {
        await SeedRepositoryAsync();
        var output = ExternalPath("tagged-projection.yml");

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--out", output],
            CreateSuccessfulPublishRunner());

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains($"release.binding: {ReleaseTagBinding.RequiredKeyCount}/{ReleaseTagBinding.RequiredKeyCount} verified", result.Stdout, StringComparison.Ordinal);
        var projection = await File.ReadAllTextAsync(output);
        Assert.Contains("state: tagged", projection, StringComparison.Ordinal);
        Assert.Contains("status: Tagged", projection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectWritesThroughOpenedDirectoryAfterParentDirectoryIsReplaced()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var parent = ExternalPath("projection-race/parent");
        var parkedParent = ExternalPath("projection-race/parked-parent");
        var attackerDirectory = ExternalPath("projection-race/attacker");
        var output = Path.Join(parent, "tagged-projection.yml");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(attackerDirectory);
        var probe = ExternalPath("projection-race/symbolic-link-probe");
        if (!TryCreateSymbolicLink(probe, attackerDirectory, isDirectory: true))
        {
            return;
        }

        Directory.Delete(probe);
        var replaced = false;
        using var hook = ReleaseProjectionOutputWriter.UseDirectoryOpenedHookForTesting(directory =>
        {
            if (!string.Equals(directory, parent, StringComparison.Ordinal))
            {
                return;
            }

            Directory.Move(parent, parkedParent);
            Directory.CreateSymbolicLink(parent, attackerDirectory);
            replaced = true;
        });

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--out", output],
            CreateSuccessfulPublishRunner());

        Assert.True(replaced);
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Contains("state: tagged", await File.ReadAllTextAsync(Path.Join(parkedParent, "tagged-projection.yml")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(attackerDirectory, "tagged-projection.yml")));
    }

    [Fact]
    public async Task InspectOutputWriterDoesNotReplaceOutputWhenCancelled()
    {
        var output = ExternalPath("cancelled-projection.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "keep this sentinel\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReleaseProjectionOutputWriter.WriteAsync(output, "replacement\n", cancellation.Token));

        Assert.Equal("keep this sentinel\n", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task InspectOutputWriterRemovesTemporaryFileWhenCancelledDuringWrite()
    {
        var output = ExternalPath("cancelled-during-write-projection.yml");
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(output, "keep this sentinel\n");
        using var cancellation = new CancellationTokenSource();
        using var hook = ReleaseProjectionOutputWriter.UseTemporaryFileOpenedHookForTesting(cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReleaseProjectionOutputWriter.WriteAsync(output, "replacement\n", cancellation.Token));

        Assert.Equal("keep this sentinel\n", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.EnumerateFiles(directory, ".cancelled-during-write-projection.yml.*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InspectOutputWriterRemovesTemporaryFileWhenPermissionHardeningFails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var output = ExternalPath("permission-hardening-failure-projection.yml");
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        using var hook = ReleaseProjectionOutputWriter.UseUnixFChmodFailureForTesting(error: 5);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            ReleaseProjectionOutputWriter.WriteAsync(output, "replacement\n", CancellationToken.None));

        Assert.Equal("release-inspect-output-path-invalid", exception.Diagnostic.Code);
        Assert.Empty(Directory.EnumerateFiles(directory, ".permission-hardening-failure-projection.yml.*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task InspectOutputWriterRejectsOutputDirectoryCreatedAfterInspection()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var output = ExternalPath("output-created-during-write-projection.yml");
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        using var hook = ReleaseProjectionOutputWriter.UseDirectoryOpenedHookForTesting(_ => Directory.CreateDirectory(output));

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            ReleaseProjectionOutputWriter.WriteAsync(output, "replacement\n", CancellationToken.None));

        Assert.Equal("release-inspect-output-path-invalid", exception.Diagnostic.Code);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(directory, ".output-created-during-write-projection.yml.*.tmp", SearchOption.TopDirectoryOnly));
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    [Fact]
    public async Task InspectOutputWriterRejectsTemporaryFileCreationWithoutDirectoryWritePermission()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var output = ExternalPath("temporary-file-permission-denied-projection.yml");
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var originalPermissions = File.GetUnixFileMode(directory);
        using var hook = ReleaseProjectionOutputWriter.UseDirectoryOpenedHookForTesting(_ =>
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute));

        try
        {
            var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
                ReleaseProjectionOutputWriter.WriteAsync(output, "replacement\n", CancellationToken.None));

            Assert.Equal("release-inspect-output-path-invalid", exception.Diagnostic.Code);
        }
        finally
        {
            File.SetUnixFileMode(directory, originalPermissions);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    [Fact]
    public async Task InspectOutputWriterRejectsMissingChildDirectoryWithoutParentWritePermission()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = ExternalPath("missing-child-permission-denied");
        var output = Path.Join(parent, "child", "projection.yml");
        Directory.CreateDirectory(parent);
        var originalPermissions = File.GetUnixFileMode(parent);

        try
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
                ReleaseProjectionOutputWriter.WriteAsync(output, "replacement\n", CancellationToken.None));

            Assert.Equal("release-inspect-output-path-invalid", exception.Diagnostic.Code);
        }
        finally
        {
            File.SetUnixFileMode(parent, originalPermissions);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    [Fact]
    public async Task InspectOutputWriterPreservesCancellationWhenTemporaryFileCleanupIsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var output = ExternalPath("temporary-file-cleanup-denied-projection.yml");
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var originalPermissions = File.GetUnixFileMode(directory);
        using var cancellation = new CancellationTokenSource();
        using var hook = ReleaseProjectionOutputWriter.UseTemporaryFileOpenedHookForTesting(() =>
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            cancellation.Cancel();
        });

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ReleaseProjectionOutputWriter.WriteAsync(output, "replacement\n", cancellation.Token));
        }
        finally
        {
            File.SetUnixFileMode(directory, originalPermissions);
            foreach (var temporaryFile in Directory.EnumerateFiles(directory, ".temporary-file-cleanup-denied-projection.yml.*.tmp", SearchOption.TopDirectoryOnly))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    [Fact]
    public async Task InspectRequiresMatchingExplicitTag()
    {
        await SeedRepositoryAsync();

        var missingTag = await RunAsync(["inspect", "--version", "0.1.0-preview.1"], new FakeCommandRunner());
        var mismatchedTag = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.2"],
            new FakeCommandRunner());

        Assert.Equal(1, missingTag.ExitCode);
        Assert.Contains("Code: release-tag-required", missingTag.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, mismatchedTag.ExitCode);
        Assert.Contains("Code: release-tag-version-mismatch", mismatchedTag.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("root")]
    public async Task InspectRejectsDirectoryAndRootOutputPaths(string outputKind)
    {
        await SeedRepositoryAsync();
        var output = string.Equals(outputKind, "directory", StringComparison.Ordinal)
            ? ExternalPath("output-directory")
            : Path.GetPathRoot(_externalRoot)!;
        if (string.Equals(outputKind, "directory", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(output);
        }

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--out", output],
            CreateSuccessfulPublishRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-inspect-output-path-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectRejectsSymbolicLinkOutputFile()
    {
        await SeedRepositoryAsync();
        var target = ExternalPath("projection-target.yml");
        var link = ExternalPath("projection-link.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "keep this sentinel\n");
        if (!TryCreateSymbolicLink(link, target, isDirectory: false))
        {
            return;
        }

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--out", link],
            CreateSuccessfulPublishRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-inspect-output-path-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.Equal("keep this sentinel\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task InspectRejectsOutputPathInsideRepository()
    {
        await SeedRepositoryAsync();
        var output = RepositoryPath("artifacts/tagged-projection.yml");

        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--out", output],
            CreateSuccessfulPublishRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-inspect-output-path-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task InspectRejectsOutputPathUnderSymbolicLinkDirectory()
    {
        await SeedRepositoryAsync();
        var symlinkDirectory = ExternalPath("linked-output");
        if (!TryCreateSymbolicLink(symlinkDirectory, _repositoryRoot, isDirectory: true))
        {
            return;
        }

        var output = Path.Join(symlinkDirectory, "tagged-projection.yml");
        var result = await RunAsync(
            ["inspect", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--out", output],
            CreateSuccessfulPublishRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-inspect-output-path-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepositoryPath("tagged-projection.yml")));
    }

    [Fact]
    public void InspectOutputCanonicalizesOnlyFixedMacOsTemporaryDirectoryAliases()
    {
        Assert.Equal("/private/tmp", ReleaseProjectionOutputWriter.NormalizePlatformPath("/tmp", isMacOs: true));
        Assert.Equal("/private/tmp/tagged-projection.yml", ReleaseProjectionOutputWriter.NormalizePlatformPath("/tmp/tagged-projection.yml", isMacOs: true));
        Assert.Equal("/private/var", ReleaseProjectionOutputWriter.NormalizePlatformPath("/var", isMacOs: true));
        Assert.Equal("/private/var/folders/tagged-projection.yml", ReleaseProjectionOutputWriter.NormalizePlatformPath("/var/folders/tagged-projection.yml", isMacOs: true));
        Assert.Equal("/tagged-release-link", ReleaseProjectionOutputWriter.NormalizePlatformPath("/tagged-release-link", isMacOs: true));
        Assert.Equal("/tmp/tagged-projection.yml", ReleaseProjectionOutputWriter.NormalizePlatformPath("/tmp/tagged-projection.yml", isMacOs: false));
    }

    [Fact]
    public void PreparedSidecarValidationAcceptsEmptyYamlDocument()
    {
        var sidecar = ReleaseSidecar.Parse(string.Empty, "fixture.yml");

        var error = Assert.Throws<ReleaseToolException>(() => sidecar.EnsurePrepared(SemVer.Parse("0.1.0-preview.1"), "fixture.yml"));

        Assert.Equal("release-legacy-tag-binding-unsupported", error.Diagnostic.Code);
    }

    [Fact]
    public void PreparedSidecarValidationAllowsScalarTrustMetadata()
    {
        var content = """
            release:
              schema: appsurface-release-sidecar-v1
              state: prepared
              id: v0.1.0-preview.1
            trust:
              status: Prepared
              review_attempts: 3
            """;
        var sidecar = ReleaseSidecar.Parse(content, "fixture.yml");

        sidecar.EnsurePrepared(SemVer.Parse("0.1.0-preview.1"), "fixture.yml");
    }

    [Fact]
    public void TaggedProjectionConsumesItsPreparedSidecar()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var sidecar = ReleaseSidecar.Parse(TaggedReleaseSidecarContent, "fixture.yml");

        var projection = sidecar.ToTaggedProjection(version, DateTimeOffset.UnixEpoch, "fixture.yml");
        var error = Assert.Throws<ReleaseToolException>(() => sidecar.EnsurePrepared(version, "fixture.yml"));

        Assert.Contains("state: tagged", projection, StringComparison.Ordinal);
        Assert.Equal("release-sidecar-state-invalid", error.Diagnostic.Code);
    }

    [Theory]
    [InlineData("title: Release\n", "release-legacy-tag-binding-unsupported")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n", "release-legacy-tag-binding-unsupported")]
    [InlineData("release:\n  schema: legacy-sidecar\n", "release-sidecar-schema-invalid")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: tagged\n  id: v0.1.0-preview.1\ntrust:\n  status: Prepared\n", "release-sidecar-state-invalid")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: prepared\ntrust:\n  status: Prepared\n", "release-sidecar-id-mismatch")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: prepared\n  id: v0.1.0-preview.2\ntrust:\n  status: Prepared\n", "release-sidecar-id-mismatch")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: prepared\n  id: v0.1.0-preview.1\ntrust: {}\n", "release-sidecar-final-claim-invalid")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: prepared\n  id: v0.1.0-preview.1\ntrust:\n  status: Tagged\n", "release-sidecar-final-claim-invalid")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: prepared\n  id: v0.1.0-preview.1\ntrust:\n  status: Prepared\n  summary: This page is the final narrative release note.\n", "release-sidecar-final-claim-invalid")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: prepared\n  id: v0.1.0-preview.1\ntrust:\n  status: Prepared\n  freshness: Tagged at 2026-08-02T00:00:00Z.\n", "release-sidecar-final-claim-invalid")]
    [InlineData("release:\n  schema: appsurface-release-sidecar-v1\n  state: prepared\n  id: v0.1.0-preview.1\ntrust:\n  status: Prepared\n  sources:\n    - GitHub Release publication is bound to that annotated tag.\n", "release-sidecar-final-claim-invalid")]
    public void PreparedSidecarValidationRejectsInvalidStateContracts(string content, string expectedCode)
    {
        var sidecar = ReleaseSidecar.Parse(content, "fixture.yml");

        var error = Assert.Throws<ReleaseToolException>(() => sidecar.EnsurePrepared(SemVer.Parse("0.1.0-preview.1"), "fixture.yml"));

        Assert.Equal(expectedCode, error.Diagnostic.Code);
    }

    [Fact]
    public void PreparedSidecarValidationRejectsMalformedYaml()
    {
        var error = Assert.Throws<ReleaseToolException>(() => ReleaseSidecar.Parse("release: [", "fixture.yml"));

        Assert.Equal("release-sidecar-invalid", error.Diagnostic.Code);
    }

    [Fact]
    public async Task PublishRejectsInvalidBindingBeforeCallingGitHub()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var manifest = CreateReleaseManifestJson();
        var evidence = CreateReleaseEvidenceJson(manifest);
        var evidenceBundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(evidence, ReleaseJson.Options)!;
        var binding = new ReleaseTagBinding(
            "v0.1.0-preview.1",
            ReleaseEvidence.ComputeSha256Hex(TaggedReleaseSidecarContent),
            ReleaseEvidence.ComputeSha256Hex(manifest),
            evidenceBundle.Subject.Sha256);
        var invalidTagObject = $"object abc123\ntype commit\ntag v0.1.0-preview.1\ntagger Release Tests <release-tests@example.test> 1770000000 +0000\n\n{binding.Render()}"
            .Replace("AppSurface-Release-Manifest-Sha256: ", "AppSurface-Release-Manifest-Sha256: f", StringComparison.Ordinal);
        runner.Add("git cat-file -p refs/tags/v0.1.0-preview.1", new CommandResult(0, invalidTagObject, ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-tag-trailer-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("gh ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareRejectsNonCanonicalCurrentPointerAndPreservesPermanentSidecar()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/current.md", "# Stale pointer\n");
        await WriteFileAsync("releases/current.md.yml", "title: Permanent pointer metadata\n");

        var prepared = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, prepared.ExitCode);
        var pointer = await ReadFileAsync("releases/current.md");
        Assert.Equal("# Stale pointer\n", pointer);
        Assert.Contains("release-current-page-body-invalid", prepared.Stdout, StringComparison.Ordinal);
        Assert.Equal("title: Permanent pointer metadata\n", await ReadFileAsync("releases/current.md.yml"));
    }

    [Fact]
    public async Task PrepareRejectsConcurrentCurrentPointerChangeBeforeWritingAnyReleaseArtifacts()
    {
        await SeedRepositoryAsync();
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var checker = new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123"));
        var preparation = new ReleasePreparation(
            workspace,
            checker,
            new SystemReleaseClock(),
            _ => WriteFileAsync("releases/current.md", "# Concurrent pointer\n"));
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-current-pointer-concurrent-update", error.Diagnostic.Code);
        Assert.False(File.Exists(RepositoryPath("releases/v0.1.0-preview.1.md")));
    }

    [Fact]
    public async Task PrepareRejectsConcurrentCurrentPointerSidecarChangeBeforeWritingAnyReleaseArtifacts()
    {
        await SeedRepositoryAsync();
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var checker = new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123"));
        var preparation = new ReleasePreparation(
            workspace,
            checker,
            new SystemReleaseClock(),
            _ => WriteFileAsync("releases/current.md.yml", "title: Concurrent pointer\n"));
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-current-pointer-concurrent-update", error.Diagnostic.Code);
        Assert.False(File.Exists(RepositoryPath("releases/v0.1.0-preview.1.md")));
    }

    [Fact]
    public async Task PrepareRejectsConcurrentUnreleasedEntryChangeBeforeWritingAnyReleaseArtifacts()
    {
        await SeedRepositoryAsync();
        const string entryPath = "releases/unreleased.entries/2026-08-08-concurrent-entry.md";
        await WriteFileAsync(
            entryPath,
            """
            <!-- appsurface:unreleased-entry section="included" -->
            - Original entry.
            """);
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var checker = new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123"));
        var preparation = new ReleasePreparation(
            workspace,
            checker,
            new SystemReleaseClock(),
            _ => WriteFileAsync(
                entryPath,
                """
                <!-- appsurface:unreleased-entry section="included" -->
                - Concurrent entry.
                """));
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-unreleased-entry-concurrent-update", error.Diagnostic.Code);
        Assert.False(File.Exists(RepositoryPath("releases/v0.1.0-preview.1.md")));
        Assert.Contains("Concurrent entry.", await ReadFileAsync(entryPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareRestoresEntryChangedDuringGuardedArchiveHandoff()
    {
        await SeedRepositoryAsync();
        const string entryPath = "releases/unreleased.entries/2026-08-08-archive-handoff.md";
        await WriteFileAsync(
            entryPath,
            """
            <!-- appsurface:unreleased-entry section="included" -->
            - Original entry.
            """);
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var checker = new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123"));
        var preparation = new ReleasePreparation(
            workspace,
            checker,
            new SystemReleaseClock(),
            beforeArchiveEntryAsync: (_, _) => WriteFileAsync(
                entryPath,
                """
                <!-- appsurface:unreleased-entry section="included" -->
                - Concurrent entry.
                """));
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-unreleased-entry-concurrent-update", error.Diagnostic.Code);
        Assert.Contains("restored without deletion", error.Diagnostic.Cause, StringComparison.Ordinal);
        Assert.Contains("Concurrent entry.", await ReadFileAsync(entryPath), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(RepositoryPath("releases/.release-prep-recovery")));
        Assert.Equal(ReleaseCurrentPointer.BuildNone(), await ReadFileAsync("releases/current.md"));
    }

    [Fact]
    public async Task PrepareRetainsChangedArchiveCandidateWhenAnotherWriterRecreatesTheEntryPath()
    {
        await SeedRepositoryAsync();
        const string entryPath = "releases/unreleased.entries/2026-08-08-archive-recovery.md";
        await WriteFileAsync(
            entryPath,
            """
            <!-- appsurface:unreleased-entry section="included" -->
            - Original entry.
            """);
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var checker = new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123"));
        var preparation = new ReleasePreparation(
            workspace,
            checker,
            new SystemReleaseClock(),
            beforeArchiveEntryAsync: (_, _) => WriteFileAsync(
                entryPath,
                """
                <!-- appsurface:unreleased-entry section="included" -->
                - Changed candidate.
                """),
            afterArchiveEntryHandoffAsync: (_, _, _) => WriteFileAsync(
                entryPath,
                """
                <!-- appsurface:unreleased-entry section="included" -->
                - Later entry.
                """));
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-unreleased-entry-concurrent-update", error.Diagnostic.Code);
        Assert.Contains("preserved at 'releases/.release-prep-recovery/", error.Diagnostic.Cause, StringComparison.Ordinal);
        Assert.Contains("Later entry.", await ReadFileAsync(entryPath), StringComparison.Ordinal);
        var recoveryPath = Assert.Single(Directory.EnumerateFiles(RepositoryPath("releases/.release-prep-recovery")));
        Assert.EndsWith(".recovery", recoveryPath, StringComparison.Ordinal);
        Assert.Contains("Changed candidate.", await File.ReadAllTextAsync(recoveryPath), StringComparison.Ordinal);
        Assert.Equal(ReleaseCurrentPointer.BuildNone(), await ReadFileAsync("releases/current.md"));
    }

    [Fact]
    public async Task PrepareKeepsNewEntryCreatedAfterGuardedArchiveHandoff()
    {
        await SeedRepositoryAsync();
        const string entryPath = "releases/unreleased.entries/2026-08-08-later-entry.md";
        await WriteFileAsync(
            entryPath,
            """
            <!-- appsurface:unreleased-entry section="included" -->
            - Original entry.
            """);
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var checker = new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123"));
        var preparation = new ReleasePreparation(
            workspace,
            checker,
            new SystemReleaseClock(),
            afterArchiveEntryHandoffAsync: (_, _, _) => WriteFileAsync(
                entryPath,
                """
                <!-- appsurface:unreleased-entry section="included" -->
                - Later entry.
                """));
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var result = await preparation.PrepareAsync(options, CancellationToken.None);

        Assert.False(result.Check.HasErrors);
        Assert.Contains("Original entry.", await ReadFileAsync("releases/v0.1.0-preview.1.md"), StringComparison.Ordinal);
        Assert.Contains("Later entry.", await ReadFileAsync(entryPath), StringComparison.Ordinal);
        Assert.Equal(ReleaseCurrentPointer.Build(SemVer.Parse("0.1.0-preview.1")), await ReadFileAsync("releases/current.md"));
        Assert.Empty(Directory.EnumerateFiles(RepositoryPath("releases/.release-prep-recovery")));
    }

    [Fact]
    public async Task PrepareReportsAnEntryRemovedBeforeGuardedArchiveHandoff()
    {
        await SeedRepositoryAsync();
        const string entryPath = "releases/unreleased.entries/2026-08-08-removed-before-handoff.md";
        await WriteFileAsync(
            entryPath,
            """
            <!-- appsurface:unreleased-entry section="included" -->
            - Original entry.
            """);
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var checker = new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123"));
        var preparation = new ReleasePreparation(
            workspace,
            checker,
            new SystemReleaseClock(),
            beforeArchiveEntryAsync: (_, _) =>
            {
                File.Delete(RepositoryPath(entryPath));
                return Task.CompletedTask;
            });
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-unreleased-entry-concurrent-update", error.Diagnostic.Code);
        Assert.Contains("could not move the entry", error.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareConvertsEntryValidationFailureAfterReadinessCheckToDiagnostic()
    {
        await SeedRepositoryAsync();
        const string entryPath = "releases/unreleased.entries/2026-08-08-validation-race.md";
        await WriteFileAsync(
            entryPath,
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n- Original entry.\n");
        var runner = FakeCommandRunner.WithSourceCommit("abc123");
        var replacedEntryAfterReadinessCheck = false;
        runner.BeforeRun = invocation =>
        {
            if (replacedEntryAfterReadinessCheck
                || !string.Equals(invocation.Executable, "git", StringComparison.Ordinal)
                || invocation.Arguments.Count != 2
                || !string.Equals(invocation.Arguments[0], "rev-parse", StringComparison.Ordinal)
                || !string.Equals(invocation.Arguments[1], "HEAD", StringComparison.Ordinal))
            {
                return;
            }

            File.WriteAllText(RepositoryPath(entryPath), "- Entry no longer has a directive.\n");
            replacedEntryAfterReadinessCheck = true;
        };
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var preparation = new ReleasePreparation(workspace, new ReleaseChecker(workspace, runner), new SystemReleaseClock());
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.True(replacedEntryAfterReadinessCheck);
        Assert.Equal("release-unreleased-entry-invalid", error.Diagnostic.Code);
        Assert.Contains("must begin with", error.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareRejectsSymlinkedEntryRecoveryDirectory()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "releases/unreleased.entries/2026-08-08-recovery-directory.md",
            "<!-- appsurface:unreleased-entry section=\"included\" -->\n- Original entry.\n");
        var recoveryDirectory = RepositoryPath("releases/.release-prep-recovery");
        var externalRecoveryDirectory = ExternalPath("recovery-directory");
        Directory.CreateDirectory(externalRecoveryDirectory);
        if (!TryCreateSymbolicLink(recoveryDirectory, externalRecoveryDirectory, isDirectory: true))
        {
            return;
        }

        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var preparation = new ReleasePreparation(
            workspace,
            new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123")),
            new SystemReleaseClock());
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(
            () => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-preparation-output-path-unsafe", error.Diagnostic.Code);
        Assert.Contains("Directory segment", error.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentPointerRequiresAnExactCanonicalTemplate()
    {
        Assert.True(ReleaseCurrentPointer.TryParse(ReleaseCurrentPointer.BuildNone(), out var initial));
        Assert.Null(initial);
        Assert.True(ReleaseCurrentPointer.TryParse(ReleaseCurrentPointer.Build(SemVer.Parse("1.2.3")), out var version));
        Assert.Equal(SemVer.Parse("1.2.3"), version);
        Assert.False(ReleaseCurrentPointer.TryParse("<!-- appsurface-current-coordinated-release: v1.2.3 -->\n# Current coordinated release\n\nDifferent prose.\n", out _));
        Assert.False(ReleaseCurrentPointer.TryParse("\uFEFF" + ReleaseCurrentPointer.BuildNone(), out _));
        Assert.False(ReleaseCurrentPointer.TryParse(ReleaseCurrentPointer.BuildNone().TrimEnd('\n'), out _));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void CurrentPointerAcceptsCanonicalCrLfAndCrLineEndings(string lineEnding)
    {
        var initialContent = ReleaseCurrentPointer.BuildNone().Replace("\n", lineEnding, StringComparison.Ordinal);
        Assert.True(ReleaseCurrentPointer.TryParse(initialContent, out var initial));
        Assert.Null(initial);

        var expectedVersion = SemVer.Parse("1.2.3");
        var versionedContent = ReleaseCurrentPointer.Build(expectedVersion).Replace("\n", lineEnding, StringComparison.Ordinal);
        Assert.True(ReleaseCurrentPointer.TryParse(versionedContent, out var parsed));
        Assert.Equal(expectedVersion, parsed);
    }

    [Theory]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-99999999999999999999", "1.0.0-100000000000000000000", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("2.0.0", "1.999.999", 1)]
    public void SemVerComparisonUsesSemVerPrecedence(string left, string right, int expectedSign)
    {
        var comparison = SemVer.Parse(left).CompareTo(SemVer.Parse(right));
        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Fact]
    public async Task CurrentPointerGateUsesLatestReachableAnnotatedTag()
    {
        var runner = new FakeCommandRunner();
        runner.Add("git for-each-ref --format=%(refname:short) refs/tags/v*", new CommandResult(0, "v1.0.0-preview.2\nv1.0.0\nv2.0.0\n", ""));
        runner.Add("git cat-file -t refs/tags/v1.0.0-preview.2", new CommandResult(0, "tag\n", ""));
        runner.Add("git cat-file -t refs/tags/v1.0.0", new CommandResult(0, "tag\n", ""));
        runner.Add("git cat-file -t refs/tags/v2.0.0", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags/v1.0.0-preview.2^{commit}", new CommandResult(0, "a\n", ""));
        runner.Add("git rev-parse refs/tags/v1.0.0^{commit}", new CommandResult(0, "b\n", ""));
        runner.Add("git rev-parse refs/tags/v2.0.0^{commit}", new CommandResult(0, "c\n", ""));
        runner.Add("git merge-base --is-ancestor a base", new CommandResult(0, "", ""));
        runner.Add("git merge-base --is-ancestor b base", new CommandResult(0, "", ""));
        runner.Add("git merge-base --is-ancestor c base", new CommandResult(1, "", "unreachable"));

        var gate = new ReleaseCurrentPointerGate(new ReleaseWorkspace(_repositoryRoot), runner);
        var diagnostics = await gate.ValidateAsync(SemVer.Parse("1.0.1"), ReleaseCurrentPointer.Build(SemVer.Parse("1.0.0")), "base", CancellationToken.None);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CurrentPointerGateRejectsStaleMarkerAndExistingTargetTag()
    {
        var runner = new FakeCommandRunner();
        runner.Add("git for-each-ref --format=%(refname:short) refs/tags/v*", new CommandResult(0, "v1.0.0\nv1.1.0\n", ""));
        foreach (var tag in new[] { "v1.0.0", "v1.1.0" })
        {
            runner.Add($"git cat-file -t refs/tags/{tag}", new CommandResult(0, "tag\n", ""));
            runner.Add($"git rev-parse refs/tags/{tag}^{{commit}}", new CommandResult(0, tag + "\n", ""));
            runner.Add($"git merge-base --is-ancestor {tag} base", new CommandResult(0, "", ""));
        }

        var gate = new ReleaseCurrentPointerGate(new ReleaseWorkspace(_repositoryRoot), runner);
        var diagnostics = await gate.ValidateAsync(SemVer.Parse("1.0.0"), ReleaseCurrentPointer.Build(SemVer.Parse("1.0.0")), "base", CancellationToken.None);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-stale");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-target-tag-exists");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-version-not-newer");
    }

    [Fact]
    public async Task CurrentPointerGateReportsTagDiscoveryFailuresInsteadOfAssumingNoHistory()
    {
        var runner = new FakeCommandRunner();
        runner.Add("git for-each-ref --format=%(refname:short) refs/tags/v*", new CommandResult(128, "", "not a git repository"));
        var gate = new ReleaseCurrentPointerGate(new ReleaseWorkspace(_repositoryRoot), runner);

        var diagnostics = await gate.ValidateAsync(SemVer.Parse("1.0.0"), ReleaseCurrentPointer.BuildNone(), "base", CancellationToken.None);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-tag-discovery-failed");
    }

    [Fact]
    public async Task CurrentPointerGateRejectsAnExistingUnreachableTargetTag()
    {
        var runner = new FakeCommandRunner();
        runner.Add("git for-each-ref --format=%(refname:short) refs/tags/v*", new CommandResult(0, "v1.0.0\n", ""));
        runner.Add("git cat-file -t refs/tags/v1.0.0", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags/v1.0.0^{commit}", new CommandResult(0, "unreachable\n", ""));
        runner.Add("git merge-base --is-ancestor unreachable base", new CommandResult(1, "", ""));
        runner.Add("git rev-parse --verify --quiet refs/tags/v1.0.0", new CommandResult(0, "tag-object\n", ""));
        var gate = new ReleaseCurrentPointerGate(new ReleaseWorkspace(_repositoryRoot), runner);

        var diagnostics = await gate.ValidateAsync(SemVer.Parse("1.0.0"), ReleaseCurrentPointer.BuildNone(), "base", CancellationToken.None);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-target-tag-exists");
    }

    [Fact]
    public async Task PrepareRejectsHeadChangeBeforeWritingAnyReleaseArtifacts()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        runner.AddSequence("git rev-parse HEAD", new CommandResult(0, "base\n", ""), new CommandResult(0, "changed\n", ""));
        runner.Add("git for-each-ref --format=%(refname:short) refs/tags/v*", new CommandResult(0, "", ""));
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var preparation = new ReleasePreparation(workspace, new ReleaseChecker(workspace, runner), new SystemReleaseClock());
        var options = new ReleaseOptions("prepare", _repositoryRoot, SemVer.Parse("0.1.0-preview.1"), null, new DateOnly(2026, 5, 25), false, null, null, false, false);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(() => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-preparation-base-commit-concurrent-update", error.Diagnostic.Code);
        Assert.False(File.Exists(RepositoryPath("releases/v0.1.0-preview.1.md")));
    }

    [Fact]
    public async Task PrepareRejectsWhenPreparationBaseCommitCannotBeResolved()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-preparation-base-commit-unavailable", result.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(RepositoryPath("releases/v0.1.0-preview.1.md")));
    }

    [Fact]
    public async Task PrepareEmitsSortedV2CoordinatedResolutionsAndValidatesTheV2Manifest()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Zeta/ForgeTrust.AppSurface.Zeta.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
                order: 20
              - project: Alpha/ForgeTrust.AppSurface.Alpha.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
                order: 10
              - project: Explicit/ForgeTrust.AppSurface.Explicit.csproj
                classification: public
                publish_decision: publish
                release_track: explicit
                release_notes_path: releases/unreleased.md
                order: 30
            """);

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        var manifestJson = await ReadFileAsync("releases/v0.1.0-preview.1.release.json");
        Assert.True(ReleaseManifestV2Validator.TryDeserialize(manifestJson, out var manifest, out var issue), issue);
        Assert.Equal(
            ["Alpha/ForgeTrust.AppSurface.Alpha.csproj", "Explicit/ForgeTrust.AppSurface.Explicit.csproj", "Zeta/ForgeTrust.AppSurface.Zeta.csproj"],
            manifest!.PublishedPackageProjects);
        Assert.Equal(
            ["Alpha/ForgeTrust.AppSurface.Alpha.csproj", "Zeta/ForgeTrust.AppSurface.Zeta.csproj"],
            manifest.CoordinatedPackageReleaseNoteResolutions.Select(resolution => resolution.Project));
        Assert.All(manifest.CoordinatedPackageReleaseNoteResolutions, resolution =>
        {
            Assert.Equal("releases/current.md", resolution.AliasPath);
            Assert.Equal("releases/v0.1.0-preview.1.md", resolution.ResolvedPath);
            Assert.Equal("v0.1.0-preview.1", resolution.ReleaseTag);
            Assert.Equal("abc123", resolution.PreparationBaseCommit);
        });

        var sourceRoot = FindSourceRoot();
        using var manifestSchema = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(sourceRoot, "tools/ForgeTrust.AppSurface.Release/schemas/release-manifest-v2.schema.json")));
        using var evidenceSchema = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(sourceRoot, "tools/ForgeTrust.AppSurface.Release/schemas/release-evidence-v2.schema.json")));
        Assert.False(manifestSchema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.False(evidenceSchema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("coordinatedPackageReleaseNoteResolutions", manifestSchema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("coordinatedPackageReleaseNoteResolutions", evidenceSchema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            "^(?:[0-9a-f]{64}|generated)$",
            evidenceSchema.RootElement
                .GetProperty("$defs")
                .GetProperty("docsArchive")
                .GetProperty("properties")
                .GetProperty("releaseManifestSha256")
                .GetProperty("pattern")
                .GetString());
    }

    [Fact]
    public void ReleaseManifestV2Validator_RejectsMalformedUnknownAndIncompleteContracts()
    {
        const string validManifest = """
            {"schema":"appsurface-release-manifest-v2","version":"0.1.0-preview.1","tag":"v0.1.0-preview.1","date":"2026-05-25","preparationBaseCommit":"abc123","releaseClassification":"prerelease","generatedFiles":[],"publishedPackageProjects":[],"coordinatedPackageReleaseNoteResolutions":[],"diagnostics":[],"warningIds":[],"consumedUnreleasedEntryPaths":[]}
            """;

        Assert.False(ReleaseManifestV2Validator.TryDeserialize("{", out var malformedManifest, out var malformedIssue));
        Assert.Null(malformedManifest);
        Assert.False(string.IsNullOrWhiteSpace(malformedIssue));

        Assert.False(ReleaseManifestV2Validator.TryDeserialize("{\"schema\":\"appsurface-release-manifest-v2\"}", out var incompleteManifest, out var incompleteIssue));
        Assert.Null(incompleteManifest);
        Assert.Equal("Release manifest has missing, unknown, or V1-only properties.", incompleteIssue);

        Assert.False(ReleaseManifestV2Validator.TryDeserialize(validManifest[..^1] + ",\"sourceCommit\":\"abc123\"}", out var unknownPropertyManifest, out var unknownPropertyIssue));
        Assert.Null(unknownPropertyManifest);
        Assert.Equal("Release manifest has missing, unknown, or V1-only properties.", unknownPropertyIssue);

        Assert.False(ReleaseManifestV2Validator.TryDeserialize(validManifest.Replace("appsurface-release-manifest-v2", "appsurface-release-manifest-v1", StringComparison.Ordinal), out var wrongSchemaManifest, out var wrongSchemaIssue));
        Assert.Null(wrongSchemaManifest);
        Assert.Equal("Release manifest schema must be 'appsurface-release-manifest-v2'.", wrongSchemaIssue);

        Assert.False(ReleaseManifestV2Validator.TryDeserialize(validManifest.Replace("\"preparationBaseCommit\":\"abc123\"", "\"preparationBaseCommit\":null", StringComparison.Ordinal), out var missingValueManifest, out var missingValueIssue));
        Assert.Null(missingValueManifest);
        Assert.Equal("Release manifest has missing required V2 values.", missingValueIssue);

        Assert.False(ReleaseManifestV2Validator.TryDeserialize(validManifest.Replace("\"publishedPackageProjects\":[]", "\"publishedPackageProjects\":[\"Core/ForgeTrust.AppSurface.Core.csproj\",\"Core/ForgeTrust.AppSurface.Core.csproj\"]", StringComparison.Ordinal), out var duplicateProjectManifest, out var duplicateProjectIssue));
        Assert.Null(duplicateProjectManifest);
        Assert.Equal("Release manifest V2 package resolutions are invalid or not ordinally sorted.", duplicateProjectIssue);
    }

    [Fact]
    public void ReleaseManifestV2Validator_RequiresPackageIndexCompleteResolutionSet()
    {
        var packages = PackageIndexSummary.Load(
            """
            packages:
              - project: Core/ForgeTrust.AppSurface.Core.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                classification: public
                publish_decision: publish
                release_notes_path: releases/unreleased.md
            """);
        var manifest = new ReleaseManifestV2(
            ReleaseManifestV2Validator.Schema,
            "0.1.0-preview.1",
            "v0.1.0-preview.1",
            "2026-05-25",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "prerelease",
            [],
            packages.PublicPublishedPackages.Select(package => package.Project).OrderBy(project => project, StringComparer.Ordinal).ToArray(),
            [],
            [],
            []);

        Assert.False(ReleaseManifestV2Validator.TryValidatePackageSet(manifest, packages.PublicPublishedPackages, out var issue));
        Assert.Contains("Core/ForgeTrust.AppSurface.Core.csproj", issue, StringComparison.Ordinal);
        Assert.Contains("coordinated rows", issue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareReportsInvalidSidecarThroughDiagnosticEnvelope()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/unreleased.md.yml", "title: [\n");

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-sidecar-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Problem:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Cause:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Fix:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Docs:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareStableReleaseRecordsPolicyWarningInManifest()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        var manifestJson = await ReadFileAsync("releases/v0.1.0.release.json");
        Assert.Contains("\"warningIds\": [", manifestJson, StringComparison.Ordinal);
        Assert.Contains("release-stable-package-policy-missing", manifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckWritesReportWhenReportPathIsRequested()
    {
        await SeedRepositoryAsync();
        var reportPath = Path.Join(_repositoryRoot, "artifacts", "release-report.md");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--report", reportPath],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(reportPath));
        Assert.Contains("# Release readiness report", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsReportWriteFailuresThroughDiagnosticEnvelope()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--report", _repositoryRoot],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.True(
            result.Stderr.Contains("Code: release-io-failure", StringComparison.Ordinal)
                || result.Stderr.Contains("Code: release-path-permission-denied", StringComparison.Ordinal),
            result.Stderr);
    }

    [Fact]
    public async Task PrepareRejectsExistingVersionedTargets()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/v0.1.0-preview.1.md", "# Existing\n");

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-target-exists", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCanFailOnStablePolicyWarnings()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--fail-on-warnings"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-stable-package-policy-missing", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckDoesNotWarnForStableReleaseWhenStableWorkflowExists()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--fail-on-warnings"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("release-stable-package-policy-missing", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-prerelease-label-unprotected", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckWarnsWhenPrereleaseLabelCannotTriggerProtectedPublishing()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["check", "--version", "0.1.0-foo.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release-prerelease-label-unprotected", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("will not trigger protected NuGet prerelease publishing", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsTargetAndNarrativeWarningsWithoutFailingByDefault()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/v0.1.0-preview.1.md", "# Existing\n");
        await WriteFileAsync(
            "releases/unreleased.md",
            """
            # Unreleased

            ## What is taking shape

            <!-- appsurface:unreleased-entries section="taking-shape" -->

            ## Included in the next coordinated version

            TODO: replace this placeholder before release.

            <!-- appsurface:unreleased-entries section="included" -->

            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release-target-exists", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-migration-guidance-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-placeholder-copy", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCanAllowExistingTargetsForPreparedReleaseReview()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--fail-on-warnings", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("## Release evidence bundle", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-target-exists", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRequiresCompleteGeneratedTargetsForPreparedReleaseReview()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/v0.1.0-preview.1.release.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--fail-on-warnings", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-generated-target-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.md", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.md.yml", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.evidence.json", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-target-exists", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsStalePreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidencePath = RepositoryPath("releases/v0.1.0-preview.1.evidence.json");
        var staleEvidence = (await File.ReadAllTextAsync(evidencePath)).Replace(
            "\"version\": \"0.1.0-preview.1\"",
            "\"version\": \"0.1.0-preview.2\"",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(evidencePath, staleEvidence);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-version-mismatch", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-evidence-subject-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPreparedReleaseEvidenceWithV1EvidenceAndV2Manifest()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseNote = await ReadFileAsync("releases/v0.1.0-preview.1.md");
        var releaseSidecar = await ReadFileAsync("releases/v0.1.0-preview.1.md.yml");
        var releaseManifest = (await ReadFileAsync("releases/v0.1.0-preview.1.release.json")).Replace(
            "\"preparationBaseCommit\": \"abc123\"",
            "\"preparationBaseCommit\": \"other-content-source\"",
            StringComparison.Ordinal);
        await WriteFileAsync("releases/v0.1.0-preview.1.release.json", releaseManifest);
        var evidence = ReleaseEvidence.BuildDraft(
            new ReleaseWorkspace(_repositoryRoot),
            version,
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            releaseNote,
            releaseSidecar,
            releaseManifest,
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")]);
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", ReleaseEvidence.Serialize(evidence));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-release-manifest-schema-invalid", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-evidence-subject-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPreparedReleaseEvidenceWhenV2PreparationBaseCommitDiffersFromManifest()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        const string mismatchedPreparationBaseCommit = "other-content-source";
        var mismatched = ReleaseEvidenceV2.RefreshSubject(bundle with
        {
            Commits = bundle.Commits with { PreparationBaseCommit = mismatchedPreparationBaseCommit },
            CoordinatedPackageReleaseNoteResolutions = bundle.CoordinatedPackageReleaseNoteResolutions
                .Select(resolution => resolution with { PreparationBaseCommit = mismatchedPreparationBaseCommit })
                .ToArray()
        });
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", ReleaseEvidence.Serialize(mismatched));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-release-manifest-schema-invalid", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("preparation base commit or coordinated resolutions differ", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsStalePreparedReleaseArtifactBytes()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await File.AppendAllTextAsync(RepositoryPath("releases/v0.1.0-preview.1.md"), "\nLate edit.\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-artifact-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsMissingPreparedReleaseArtifactBytes()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        File.Delete(RepositoryPath("releases/v0.1.0-preview.1.md.yml"));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-artifact-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsMissingPreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        File.Delete(RepositoryPath("releases/v0.1.0-preview.1.evidence.json"));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-missing", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparedEvidenceValidationHandlesMissingReleasesDirectory()
    {
        var result = await ReleaseEvidence.ValidatePreparedAsync(
            new ReleaseWorkspace(_repositoryRoot),
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "abc123",
            CancellationToken.None);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-missing");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-duplicate");
    }

    [Fact]
    public async Task CheckRejectsMalformedPreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", "{\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-schema-invalid", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsNullPreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", "null\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-schema-invalid", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPreparedReleaseEvidenceWithMissingNestedFields()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        var malformed = bundle with
        {
            ReleaseManifestDigest = new ReleaseEvidenceFileDigest(null!, bundle.ReleaseManifestDigest.Value)
        };
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", ReleaseEvidence.Serialize(malformed));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-schema-invalid", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPreparedReleaseEvidenceFinalizedForDifferentCommit()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            Commits = bundle.Commits with { ReleasePreparationCommit = "old-release-prep-commit" },
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", ReleaseEvidence.Serialize(mismatched));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-release-preparation-commit-mismatch", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-evidence-subject-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V2EvidenceSubjectDigest_ExcludesLaterCommitAndWorkflowIdentities()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        var refreshed = ReleaseEvidenceV2.RefreshSubject(bundle with
        {
            Commits = bundle.Commits with
            {
                ReleasePreparationCommit = "release-preparation-commit",
                TagCommit = "tag-commit",
                WorkflowRunId = "workflow-run"
            }
        });

        Assert.Equal(bundle.Subject.Sha256, refreshed.Subject.Sha256);
    }

    [Fact]
    public async Task PrepareRejectsSymlinkedGeneratedOutputDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var releasesPath = RepositoryPath("releases");
        var externalReleasesPath = TestPathUtils.PathUnder(_externalRoot, "releases");
        Directory.CreateDirectory(_externalRoot);
        Directory.Move(releasesPath, externalReleasesPath);
        Directory.CreateSymbolicLink(releasesPath, externalReleasesPath);

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-preparation-output-path-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(TestPathUtils.PathUnder(externalReleasesPath, "v0.1.0-preview.1.md")));
    }

    [Fact]
    public async Task PrepareRejectsSymlinkedExistingGeneratedOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        Directory.CreateDirectory(_externalRoot);
        var currentReleasePath = RepositoryPath("releases/current.md");
        var externalCurrentReleasePath = TestPathUtils.PathUnder(_externalRoot, "current.md");
        File.Move(currentReleasePath, externalCurrentReleasePath);
        File.CreateSymbolicLink(currentReleasePath, externalCurrentReleasePath);

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-preparation-output-path-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(RepositoryPath("releases/v0.1.0-preview.1.md")));
    }

    [Fact]
    public async Task PrepareRejectsDirectoryAtGeneratedOutputPath()
    {
        await SeedRepositoryAsync();
        Directory.CreateDirectory(RepositoryPath("releases/v0.1.0-preview.1.md"));
        var workspace = new ReleaseWorkspace(_repositoryRoot);
        var preparation = new ReleasePreparation(
            workspace,
            new ReleaseChecker(workspace, FakeCommandRunner.WithSourceCommit("abc123")),
            new SystemReleaseClock());
        var options = new ReleaseOptions(
            "prepare",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: new DateOnly(2026, 5, 25),
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: true);

        var error = await Assert.ThrowsAsync<ReleaseToolException>(() => preparation.PrepareAsync(options, CancellationToken.None));

        Assert.Equal("release-preparation-output-path-unsafe", error.Diagnostic.Code);
        Assert.Contains("directory", error.Diagnostic.Cause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAllowsNeighboringHistoricalEvidenceVersions()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/v0.1.0-preview.2.evidence.json", "{}\n");
        await WriteFileAsync("releases/v0.1.00.evidence.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("release-evidence-duplicate", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckIgnoresMalformedNeighboringEvidenceFileNames()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/vnot-semver.evidence.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("release-evidence-duplicate", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceBuildDraftAllowsEmptyPackageUpdates()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var evidence = ReleaseEvidence.BuildDraft(
            new ReleaseWorkspace(_repositoryRoot),
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            CreateReleaseManifestJson(),
            []);

        Assert.Empty(evidence.PackageReleaseNotePaths);
        Assert.Equal("releases/v0.1.0-preview.1.evidence.json", evidence.Subject.Name);
        Assert.NotEmpty(evidence.Subject.Sha256);
        Assert.NotEqual("2026-05-25T00:00:00Z", evidence.GeneratedAtUtc);

        var generatedAt = DateTimeOffset.Parse(evidence.GeneratedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(generatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void ReleaseEvidenceBuildDraftOrdersPackageUpdatesByProject()
    {
        var evidence = ReleaseEvidence.BuildDraft(
            new ReleaseWorkspace(_repositoryRoot),
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            CreateReleaseManifestJson(),
            [
                new PackagePathUpdate("Web/ForgeTrust.AppSurface.Web.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md"),
                new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")
            ]);

        Assert.Collection(
            evidence.PackageReleaseNotePaths,
            package => Assert.Equal("Core/ForgeTrust.AppSurface.Core.csproj", package.Project),
            package => Assert.Equal("Web/ForgeTrust.AppSurface.Web.csproj", package.Project));
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsDocsCatalogMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var manifestDigest = new string('a', 64);
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "releases/0.1.0-preview.1",
                ReleaseManifestSha256: manifestDigest,
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: new ReleaseEvidenceCatalogEntry("releases/other", manifestDigest)),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-catalog-entry-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsDocsCatalogDigestMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "releases/0.1.0-preview.1",
                ReleaseManifestSha256: new string('a', 64),
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: new ReleaseEvidenceCatalogEntry("releases/0.1.0-preview.1", new string('c', 64))),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-catalog-entry-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsMalformedTagEvidence()
    {
        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            CreateReleaseManifestJson(),
            "{");

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsNullTagEvidence()
    {
        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            CreateReleaseManifestJson(),
            "null\n");

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public async Task PublishRejectsTagEvidenceWithNonStringSchemaThroughDiagnosticEnvelope()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json",
            new CommandResult(0, "{\"schema\": 2}\n", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-schema-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsUnsupportedSchema()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            Schema = "appsurface-release-evidence-bundle-v0",
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsMissingTopLevelFields()
    {
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJson(releaseManifest).Replace(
            "\"schema\": \"appsurface-release-evidence-bundle-v1\"",
            "\"schema\": null",
            StringComparison.Ordinal);

        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("version")]
    [InlineData("tag")]
    [InlineData("releaseClassification")]
    [InlineData("releaseNotePath")]
    [InlineData("releaseSidecarPath")]
    [InlineData("releaseManifestPath")]
    [InlineData("evidencePath")]
    [InlineData("releaseManifestDigest")]
    [InlineData("releaseArtifactDigests")]
    [InlineData("packageReleaseNotePaths")]
    [InlineData("docsArchive")]
    [InlineData("commits")]
    [InlineData("generatedBy")]
    [InlineData("generatedAtUtc")]
    [InlineData("subject")]
    public void ReleaseEvidenceValidationRejectsEveryMissingTopLevelField(string propertyName)
    {
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJsonWithNull(releaseManifest, propertyName);

        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Theory]
    [InlineData("releaseManifestDigest.algorithm")]
    [InlineData("releaseManifestDigest.value")]
    [InlineData("releaseArtifactDigests.0.path")]
    [InlineData("releaseArtifactDigests.0.algorithm")]
    [InlineData("releaseArtifactDigests.0.value")]
    [InlineData("docsArchive.status")]
    [InlineData("generatedBy.tool")]
    [InlineData("subject.name")]
    [InlineData("subject.sha256")]
    [InlineData("packageReleaseNotePaths.0.project")]
    [InlineData("packageReleaseNotePaths.0.releaseNotesPath")]
    public void ReleaseEvidenceValidationRejectsEveryMissingNestedField(string path)
    {
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJsonWithNull(releaseManifest, path.Split('.'));

        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsTagAndTagCommitMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            Tag = "v0.1.0-preview.2",
            Commits = bundle.Commits with { TagCommit = "other-tag-commit" },
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-version-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-tag-commit-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsReleaseManifestDigestMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            ReleaseManifestDigest = new ReleaseEvidenceFileDigest("sha512", new string('a', 64)),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-release-manifest-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsContentSourceCommitMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson("other-content-source");
        var evidence = ReleaseEvidence.BuildDraft(
            new ReleaseWorkspace(Path.Join(Path.GetTempPath(), "ReleaseToolEvidenceFixtures")),
            version,
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")]);

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(evidence));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-content-source-commit-mismatch");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsMissingReleaseArtifactDigest()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            ReleaseArtifactDigests = bundle.ReleaseArtifactDigests
                .Where(digest => !string.Equals(digest.Path, "releases/v0.1.0-preview.1.md.yml", StringComparison.Ordinal))
                .ToArray(),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsInvalidReleaseArtifactDigest()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            ReleaseArtifactDigests = bundle.ReleaseArtifactDigests
                .Select(digest => string.Equals(digest.Path, "releases/v0.1.0-preview.1.md", StringComparison.Ordinal)
                    ? digest with { Algorithm = "sha512" }
                    : digest)
                .ToArray(),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsIncompleteDocsArchiveFields()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "releases/0.1.0-preview.1",
                ReleaseManifestSha256: null,
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: null),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-archive-incomplete");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsDocsArchiveMissingExactTreePath()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: null,
                ReleaseManifestSha256: new string('a', 64),
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: null),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-archive-incomplete");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsUnsafeDocsArchivePathAndDigest()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "../outside",
                ReleaseManifestSha256: "not-a-sha",
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: null),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-exacttreepath-unsafe");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-manifest-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationAcceptsCompleteDocsArchiveFieldsWithoutCatalogEntry()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "releases/0.1.0-preview.1",
                ReleaseManifestSha256: new string('a', 64),
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: null),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-catalog-entry-mismatch");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-manifest-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceSummaryReportsOptionalAttestationMode()
    {
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;

        var summary = (bundle with
        {
            Attestation = new ReleaseEvidenceAttestation("github-artifact-attestation", "subject", new string('a', 64))
        }).ToSummary("validated");

        Assert.Equal("github-artifact-attestation", summary.Attestation);
    }

    [Fact]
    public void ReleaseReportRendererPrintsNonPendingEvidenceSummaryFields()
    {
        var result = new ReleaseCheckResult(
            "0.1.0-preview.1",
            "prerelease",
            "abc123",
            ["releases/v0.1.0-preview.1.evidence.json"],
            new ReleaseEvidenceSummary(
                "releases/v0.1.0-preview.1.evidence.json",
                "appsurface-release-evidence-bundle-v1",
                "tag-bound evidence validated for publish",
                new string('a', 64),
                new string('b', 64),
                "releases/0.1.0-preview.1",
                "availableVerified",
                "dist/docs/versions.json",
                "dist/docs",
                "dist/docs/releases/0.1.0-preview.1",
                3,
                "tag-commit",
                "not required"),
            [],
            []);

        var report = ReleaseReportRenderer.RenderCheck(result);

        Assert.Contains("- Docs archive manifest SHA-256: `" + new string('b', 64) + "`", report, StringComparison.Ordinal);
        Assert.Contains("- Catalog exact tree path: `releases/0.1.0-preview.1`", report, StringComparison.Ordinal);
        Assert.Contains("- Docs archive verification: `availableVerified`", report, StringComparison.Ordinal);
        Assert.Contains("- Docs catalog input: `dist/docs/versions.json`", report, StringComparison.Ordinal);
        Assert.Contains("- Docs verified file count: `3`", report, StringComparison.Ordinal);
        Assert.Contains("- Tag commit: `tag-commit`", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseReportRendererPrintsFullDiagnosticEnvelope()
    {
        var result = new ReleaseCheckResult(
            "0.1.0-preview.1",
            "prerelease",
            "abc123",
            ["releases/v0.1.0-preview.1.md"],
            null,
            [
                ReleaseDiagnostic.Error(
                    "release-target-exists",
                    "A generated release artifact already exists.",
                    "The versioned note is create-only and is present in the worktree.",
                    "Remove or restore only the generated release artifacts, then rerun the check.",
                    "releases/coordinated-release-links.md")
            ],
            []);

        var report = ReleaseReportRenderer.RenderCheck(result);

        var expected =
            """
            # Release readiness report

            - Version: `0.1.0-preview.1`
            - Classification: `prerelease`
            - Source commit: `abc123`
            - Errors: `1`
            - Warnings: `0`

            ## Generated files
            - `releases/v0.1.0-preview.1.md`

            ## Errors
            - Severity: `error`
              - Code: `release-target-exists`
              - Problem: A generated release artifact already exists.
              - Cause: The versioned note is create-only and is present in the worktree.
              - Fix: Remove or restore only the generated release artifacts, then rerun the check.
              - Docs: releases/coordinated-release-links.md

            ## Warnings
            - None
            """ + Environment.NewLine;

        Assert.Equal(
            expected.Replace("\r\n", "\n", StringComparison.Ordinal),
            report.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasePreparationReportNamesArtifactsAndRollbackValidation()
    {
        var check = new ReleaseCheckResult(
            "0.1.0-preview.1",
            "prerelease",
            "abc123",
            ["releases/v0.1.0-preview.1.md"],
            null,
            [],
            []);
        var result = new ReleasePreparationResult(
            check,
            ["releases/v0.1.0-preview.1.md", "releases/current.md", "CHANGELOG.md"],
            false,
            null)
        {
            ArchivedUnreleasedEntryPaths = ["releases/unreleased.entries/2026-08-08-release-workflow.md"]
        };

        var report = ReleaseReportRenderer.RenderPreparation(result);

        Assert.Contains("## Preparation recovery", report, StringComparison.Ordinal);
        Assert.Contains("- State: preparation writes artifacts sequentially; a failed run may leave a partial generated set.", report, StringComparison.Ordinal);
        Assert.Contains("  - `releases/v0.1.0-preview.1.md`", report, StringComparison.Ordinal);
        Assert.Contains("  - `releases/current.md`", report, StringComparison.Ordinal);
        Assert.Contains("  - `CHANGELOG.md`", report, StringComparison.Ordinal);
        Assert.Contains("## Archived unreleased entries", report, StringComparison.Ordinal);
        Assert.Contains("  - `releases/unreleased.entries/2026-08-08-release-workflow.md`", report, StringComparison.Ordinal);
        Assert.Contains(
            "- Safe rollback validation: run `git diff --check`, confirm generated artifacts are absent or match the pre-run state, confirm archived unreleased entries are restored to the pre-run state, then rerun `./eng/release check --version 0.1.0-preview.1 --allow-existing-targets` before another prepare attempt.",
            report,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsTagBoundPackagePathMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            PackageReleaseNotePaths =
            [
                new ReleaseEvidencePackagePath("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md")
            ],
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-package-path-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Theory]
    [InlineData("\"releaseArtifactDigests\": [", "\"releaseArtifactDigests\": [ null,")]
    [InlineData("\"packageReleaseNotePaths\": [", "\"packageReleaseNotePaths\": [ null,")]
    public void ReleaseEvidenceValidationRejectsNullArrayEntries(string oldValue, string newValue)
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJson(releaseManifest).Replace(oldValue, newValue, StringComparison.Ordinal);

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public async Task CheckReportsMissingPackagePolicyAndEmptyPublicPackageSet()
    {
        await SeedRepositoryAsync();
        File.Delete(Path.Join(_repositoryRoot, ".github", "workflows", "nuget-prerelease-publish.yml"));
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/Support.csproj
                classification: support
                publish_decision: support_publish
                release_notes_path: releases/unreleased.md
                order: 10
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-prerelease-package-path-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-no-public-packages", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsInvalidPackageManifestThroughDiagnosticEnvelope()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("packages/package-index.yml", "packages: [\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-package-index-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPublicPublishedPackageWithReadinessBlocker()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj
                classification: public
                publish_decision: publish
                release_notes_path: releases/unreleased.md
                readiness_blocker: "#642"
                order: 10
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("`release-public-package-readiness-blocked`", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("#642", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("publish_decision: do_not_publish", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPublicPublishedPackageWithoutReleaseLink()
    {
        const string project = "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj";
        await SeedRepositoryAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj
                classification: public
                publish_decision: publish
                order: 10
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
        FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-package-link-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(project, result.Stderr, StringComparison.Ordinal);
        Assert.Contains("package-release-link-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoricalV1ReleaseFixturesRemainReadableWithoutMutation()
    {
        var sourceRoot = FindSourceRoot();
        var evidencePath = TestPathUtils.PathUnder(sourceRoot, "releases", "v0.1.0.evidence.json");
        var manifestPath = TestPathUtils.PathUnder(sourceRoot, "releases", "v0.1.0.release.json");
        var notePath = TestPathUtils.PathUnder(sourceRoot, "releases", "v0.1.0.md");
        var sidecarPath = TestPathUtils.PathUnder(sourceRoot, "releases", "v0.1.0.md.yml");
        var evidence = await File.ReadAllTextAsync(evidencePath);
        var manifest = await File.ReadAllTextAsync(manifestPath);
        var note = await File.ReadAllTextAsync(notePath);
        var sidecar = await File.ReadAllTextAsync(sidecarPath);

        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0"),
            "stable",
            "v0.1.0",
            "e042717616171e6563d29d0368c8ad60a6a5bb60",
            note,
            sidecar,
            manifest,
            evidence);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Summary);
        Assert.Equal("appsurface-release-evidence-bundle-v1", result.Summary!.Schema);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWithoutStablePackageProof()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulStablePublishRunner();
        runner.Add("gh run list --workflow nuget-stable-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-stable-packages-not-published", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishStableReleaseValidatesStablePackageWorkflow()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAllowsStableReleaseBeforeDocsPublicationPlanExists()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"version\": \"0.1.0\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAllowsStableReleaseWithGeneratedDocsManifestDigestBeforeDocsPublicationPlanExists()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with
        {
            ReleaseManifestSha256 = ReleaseEvidence.DocsArchiveGeneratedDigest
        };
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"docsReleaseManifestSha256\": \"{ReleaseEvidence.DocsArchiveGeneratedDigest}\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWithoutDocsArchiveEvidence()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner();
        var manifest = CreateReleaseManifestJson(versionText: "0.1.0");
        runner.Add(
            "git show v0.1.0:releases/v0.1.0.evidence.json",
            new CommandResult(
                0,
                CreateReleaseEvidenceJson(
                    manifest,
                    "0.1.0",
                    releaseSidecarContent: PreparedReleaseSidecarContent("0.1.0")),
                ""));

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-docs-archive-required", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWithoutDocsArchiveCatalogEntry()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs, includeDocsCatalogEntry: false);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-docs-archive-incomplete", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogEntryDoesNotMatchEvidence()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);
        await File.WriteAllTextAsync(
            docs.CatalogPath,
            JsonSerializer.Serialize(
                new
                {
                    versions = new[]
                    {
                        new
                        {
                            version = "0.1.0",
                            exactTreePath = "releases/other",
                            releaseManifestSha256 = docs.ReleaseManifestSha256,
                            visibility = "Public"
                        }
                    }
                },
                ReleaseJson.Options) + Environment.NewLine);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-catalog-entry-mismatch", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenArchiveBytesChangeAfterCatalogPin()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);
        await File.WriteAllTextAsync(
            Path.Join(docs.TrustedReleaseRootPath, "releases", "0.1.0", "index.html"),
            "<!doctype html><title>changed</title>");

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenRouteManifestIsMalformed()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0", routeManifestJson: "{");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(".appsurface-docs-route-manifest.json", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenRouteManifestRoutesAreInvalid()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "../outside",
                  "recoveryAliases": [],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsafe canonical route", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenManifestOmitsRuntimeServeableAsset()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllBytesAsync(
            TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath, "favicon.ico"),
            [0, 1, 2, 3]);
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("favicon.ico", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogContainsDuplicateSelectedVersion()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            },
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("appears more than once", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogVersionIsHidden()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "hidden"
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("hidden", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid-visibility")]
    [InlineData("missing-exact-tree")]
    [InlineData("non-string-exact-tree")]
    [InlineData("missing-digest")]
    [InlineData("invalid-digest")]
    public async Task PublishRejectsStableReleaseWhenCatalogEntryShapeIsInvalid(string shape)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        object entry = shape switch
        {
            "invalid-visibility" => new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = 2
            },
            "missing-exact-tree" => new
            {
                version = "0.1.0",
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            },
            "non-string-exact-tree" => new
            {
                version = "0.1.0",
                exactTreePath = 5,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            },
            "missing-digest" => new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                visibility = "Public"
            },
            _ => new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = "not-a-sha256",
                visibility = "Public"
            }
        };
        await WriteDocsCatalogAsync(docs, entry);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("internal")]
    [InlineData(null)]
    public async Task PublishRejectsStableReleaseWhenCatalogStringFieldsAreInvalid(string? visibility)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = 5,
                visibility
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogVisibilityNumberIsNotIntegral()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = 0.5
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid visibility", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogVisibilityIsBoolean()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = true
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid visibility", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogVisibilityIsMissing()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("public visibility", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task PublishHandlesNumericCatalogVisibility(int visibility, int expectedExitCode)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(expectedExitCode, result.ExitCode);
        if (expectedExitCode == 0)
        {
            Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("hidden", result.Stderr, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp/appsurface-docs")]
    [InlineData("releases/.hidden")]
    public async Task PublishRejectsStableReleaseWhenCatalogExactTreePathIsUnsafe(string exactTreePath)
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = exactTreePath };
        await WriteDocsCatalogAsync(docs);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-docs-exacttreepath-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("exactTreePath is unsafe", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogExactTreeIsMissing()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = "releases/missing" };
        await WriteDocsCatalogAsync(docs);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("does not exist", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsUnreadableCatalogShape()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllTextAsync(docs.CatalogPath, "{}\n");

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("versions", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsMalformedCatalogPayload()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllTextAsync(docs.CatalogPath, "{");

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("could not be read", result.Diagnostics[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsMissingCheckFallbackCatalog()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.Delete(docs.CatalogPath);

        var result = await ValidateStableDocsArchiveGateAsync(
            docs,
            command: "check",
            docsCatalogPath: null);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-input-missing");
        Assert.Contains("local review fallback", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsCatalogWithoutSelectedVersion()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            "ignored",
            new
            {
                version = "0.2.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256
            });

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("not present", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsFileTrustedReleaseRoot()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var trustedRootFile = RepositoryPath("docs-root.txt");
        await File.WriteAllTextAsync(trustedRootFile, "not a directory");

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: trustedRootFile);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("not a directory", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsSymlinkTrustedReleaseRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var symlinkRoot = RepositoryPath("docs-root-link");
        Directory.CreateSymbolicLink(symlinkRoot, docs.TrustedReleaseRootPath);

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: symlinkRoot);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("symlink", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tmp/appsurface-docs")]
    [InlineData("releases/.hidden")]
    public async Task StableDocsArchiveGateRejectsUnsafeCatalogExactTreePath(string exactTreePath)
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = exactTreePath };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-exacttreepath-unsafe");
        Assert.Contains("exactTreePath", result.Diagnostics[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsBlankCatalogExactTreePath()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = " " };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("missing exactTreePath", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsInvalidCatalogExactTreePathCharacters()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = "bad\u0000path" };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-exacttreepath-unsafe");
        Assert.Contains("is invalid", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsSymlinkExactTreeSegment()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var releasesPath = Path.Join(docs.TrustedReleaseRootPath, "releases");
        var realReleasesParent = RepositoryPath("real-docs");
        Directory.CreateDirectory(realReleasesParent);
        var realReleasesPath = Path.Join(realReleasesParent, "releases");
        Directory.Move(releasesPath, realReleasesPath);
        Directory.CreateSymbolicLink(releasesPath, realReleasesPath);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("symlink", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateRejectsCandidateOutsideTrustedRoot()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        var outsideRoot = RepositoryPath("dist-other/docs");
        Directory.CreateDirectory(trustedRoot);
        Directory.CreateDirectory(outsideRoot);

        var result = ReleaseDocsArchiveGate.TryValidateNoReparseSegments(
            trustedRoot,
            outsideRoot,
            out var detail);

        Assert.False(result);
        Assert.Contains("outside trusted root", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateRejectsMissingCandidateSegment()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        Directory.CreateDirectory(trustedRoot);

        var result = ReleaseDocsArchiveGate.TryValidateNoReparseSegments(
            trustedRoot,
            Path.Join(trustedRoot, "missing"),
            out var detail);

        Assert.False(result);
        Assert.Contains("does not exist", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateAcceptsOrdinaryCandidateSegment()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        var candidate = Path.Join(trustedRoot, "releases", "0.1.0");
        Directory.CreateDirectory(candidate);

        var result = ReleaseDocsArchiveGate.TryValidateNoReparseSegments(
            trustedRoot,
            candidate,
            out var detail);

        Assert.True(result);
        Assert.Null(detail);
    }

    [Fact]
    public void StableDocsArchiveGateRejectsEmptyExactTreePath()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        Directory.CreateDirectory(trustedRoot);

        var result = ReleaseDocsArchiveGate.TryResolveExactTreePath(
            trustedRoot,
            " ",
            out var physicalExactTreePath,
            out var issue);

        Assert.False(result);
        Assert.Null(physicalExactTreePath);
        Assert.Contains("empty", issue, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateRejectsExactTreePathEscapingUnnormalizedTrustedRoot()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        Directory.CreateDirectory(trustedRoot);

        var result = ReleaseDocsArchiveGate.TryResolveExactTreePath(
            trustedRoot + Path.DirectorySeparatorChar,
            "releases/0.1.0",
            out var physicalExactTreePath,
            out var issue);

        Assert.False(result);
        Assert.NotNull(physicalExactTreePath);
        Assert.Contains("escapes the trusted release root", issue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateDefaultsTrustedRootToCatalogDirectory()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: null);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsExactTreeAtTrustedRoot()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        foreach (var file in Directory.EnumerateFiles(TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath)))
        {
            var target = TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        Directory.Delete(TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, "releases"), recursive: true);
        File.Delete(docs.CatalogPath);
        docs = docs with
        {
            CatalogPath = RepositoryPath("versions-root.json"),
            ExactTreePath = "."
        };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: docs.TrustedReleaseRootPath);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsRouteManifestWithRecoveryAliasesOnly()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "packages",
                  "recoveryAliases": ["old-packages"]
                }
              ]
            }
            """);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
        var proof = result.Proof;
        Assert.Equal(ReleaseDocsArchiveGate.VerifiedState, proof.State);
        Assert.Equal(docs.CatalogPath, proof.CatalogPath);
        Assert.Equal(docs.TrustedReleaseRootPath, proof.TrustedReleaseRootPath);
        Assert.Equal(docs.ExactTreePath, proof.CatalogExactTreePath);
        Assert.Equal(docs.ReleaseManifestSha256, proof.CatalogReleaseManifestSha256);
        Assert.Equal(TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath), proof.PhysicalExactTreePath);
        Assert.Equal(docs.FileCount, proof.VerifiedFileCount);
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsListedServeableAssetExtensions()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var files = new List<IReadOnlyDictionary<string, object?>>();
        var indexBytes = await ReadDocsArchiveFileAsync(docs, "index.html");
        files.Add(CreateDocsManifestFile("index.html", indexBytes.Length, Sha256(indexBytes)));
        var routeManifestBytes = await ReadDocsArchiveFileAsync(docs, ".appsurface-docs-route-manifest.json");
        files.Add(CreateDocsManifestFile(
            ".appsurface-docs-route-manifest.json",
            routeManifestBytes.Length,
            Sha256(routeManifestBytes)));

        foreach (var relativePath in new[]
                 {
                     "app.js",
                     "site.css",
                     "icon.svg",
                     "image.png",
                     "photo.jpg",
                     "photo-alt.jpeg",
                     "spinner.gif",
                     "hero.webp",
                     "favicon.ico",
                     "font.woff",
                     "font.woff2",
                     "font.ttf",
                     "font.eot"
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(relativePath);
            await File.WriteAllBytesAsync(DocsArchivePath(docs, relativePath), bytes);
            files.Add(CreateDocsManifestFile(relativePath, bytes.Length, Sha256(bytes)));
        }

        docs = await RewriteDocsReleaseManifestAsync(docs, "appsurface-docs-release-manifest-v1", files);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
        Assert.Equal(files.Count, result.Proof.VerifiedFileCount);

        static string Sha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsUnlistedNonServeableArchiveFiles()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllTextAsync(DocsArchivePath(docs, "notes.txt"), "not served by docs routing");

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
        Assert.Equal(docs.FileCount, result.Proof.VerifiedFileCount);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestSchemaIsInvalid()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(docs, "wrong-schema", []);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("schema", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestIsMissing()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.Delete(DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"));

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("is missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestDigestMismatchesCatalogPin()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllTextAsync(
            DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"),
            "{}");

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("digest does not match", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestIsSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var manifestPath = DocsArchivePath(docs, ".appsurface-docs-release-manifest.json");
        var realManifestPath = DocsArchivePath(docs, "real-release-manifest.json");
        File.Move(manifestPath, realManifestPath);
        File.CreateSymbolicLink(manifestPath, realManifestPath);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("symlink", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestCannotBeRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.SetUnixFileMode(DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"), UnixFileMode.None);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("could not be read", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestPayloadIsUnreadable()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, "{");

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("payload is unreadable", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestPayloadIsNull()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, "null");

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("schema", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestOmitsFiles()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var manifestJson = JsonSerializer.Serialize(
            new
            {
                schema = "appsurface-docs-release-manifest-v1"
            },
            ReleaseJson.Options) + Environment.NewLine;
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, manifestJson);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("not listed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestEntryIsInvalid()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                new Dictionary<string, object?>
                {
                    ["path"] = "index.html",
                    ["length"] = 1,
                    ["contentType"] = "text/html",
                    ["hashAlgorithm"] = "md5",
                    ["sha256"] = new string('a', 64)
                }
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid file entry", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-path")]
    [InlineData("negative-length")]
    [InlineData("blank-digest")]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestEntryShapeIsInvalid(string shape)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var entry = CreateDocsManifestFile("index.html", 1, new string('a', 64)).ToDictionary();
        switch (shape)
        {
            case "missing-path":
                entry["path"] = null;
                break;
            case "negative-length":
                entry["length"] = -1;
                break;
            case "blank-digest":
                entry["sha256"] = " ";
                break;
        }

        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [entry]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid file entry", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsBlankReleaseManifestContentType()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var indexBytes = await ReadDocsArchiveFileAsync(docs, "index.html");
        var entry = CreateDocsManifestFile(
            "index.html",
            indexBytes.Length,
            Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant()).ToDictionary();
        entry["contentType"] = " ";
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [entry]);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("invalid contentType", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsNullReleaseManifestContentType()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var indexBytes = await ReadDocsArchiveFileAsync(docs, "index.html");
        var entry = CreateDocsManifestFile(
            "index.html",
            indexBytes.Length,
            Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant()).ToDictionary();
        entry["contentType"] = null;
        var routeManifestBytes = await ReadDocsArchiveFileAsync(docs, ".appsurface-docs-route-manifest.json");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                entry,
                CreateDocsManifestFile(
                    ".appsurface-docs-route-manifest.json",
                    routeManifestBytes.Length,
                    Convert.ToHexString(SHA256.HashData(routeManifestBytes)).ToLowerInvariant())
            ]);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestContainsNullEntry()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var manifestJson = """
            {
              "schema": "appsurface-docs-release-manifest-v1",
              "files": [null]
            }
            """;
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, manifestJson);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid file entry", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestContainsUnsafeFilePath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("assets/.secret.json", 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsafe file path", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/assets/app.css")]
    [InlineData("assets\\app.css")]
    [InlineData("assets:app.css")]
    [InlineData("assets/app.css?cache=1")]
    [InlineData("assets//app.css")]
    [InlineData("assets/../app.css")]
    [InlineData("assets/./app.css")]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestPathShapeIsUnsafe(string path)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile(path, 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsafe file path", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestListsMissingFile()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("missing.css", 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("lists missing file", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestContainsDuplicatePath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var index = await ReadDocsArchiveFileAsync(docs, "index.html");
        var indexSha256 = Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant();
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("index.html", index.Length, indexSha256),
                CreateDocsManifestFile("index.html", index.Length, indexSha256)
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("duplicate path", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestLengthMismatches()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var index = await ReadDocsArchiveFileAsync(docs, "index.html");
        var indexSha256 = Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant();
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("index.html", index.Length + 1, indexSha256)
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("different byte length", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestListsSymlinkFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.CreateSymbolicLink(DocsArchivePath(docs, "linked.css"), DocsArchivePath(docs, "index.html"));
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("linked.css", 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("symlink", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestFileUsesSymlinkAncestor()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var realAssetsPath = RepositoryPath("real-doc-assets");
        Directory.CreateDirectory(realAssetsPath);
        var assetBytes = Encoding.UTF8.GetBytes("body{}");
        await File.WriteAllBytesAsync(Path.Join(realAssetsPath, "app.css"), assetBytes);
        Directory.CreateSymbolicLink(DocsArchivePath(docs, "assets"), realAssetsPath);
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("assets/app.css", assetBytes.Length, Convert.ToHexString(SHA256.HashData(assetBytes)).ToLowerInvariant())
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("symlink", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenArchiveContainsUnlistedSymlinkDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var realAssetsPath = RepositoryPath("unlisted-real-doc-assets");
        Directory.CreateDirectory(realAssetsPath);
        await File.WriteAllTextAsync(Path.Join(realAssetsPath, "app.css"), "body{}");
        Directory.CreateSymbolicLink(DocsArchivePath(docs, "unlisted-assets"), realAssetsPath);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("symlink", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenArchiveDirectoryCannotBeEnumerated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var restrictedPath = DocsArchivePath(docs, "restricted");
        Directory.CreateDirectory(restrictedPath);
        File.SetUnixFileMode(restrictedPath, UnixFileMode.None);

        try
        {
            var result = await RunStablePublishWithDocsAsync(docs);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("could not be inspected", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(restrictedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task StableDocsArchiveGateReportsTrustedRootInspectionFailure()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        using var inspectorScope = ReleaseDocsArchiveGate.UseFileSystemInspectorForTesting(
            new DelegatingFileSystemInspector
            {
                DirectoryAttributes = directory => string.Equals(directory.FullName, docs.TrustedReleaseRootPath, StringComparison.Ordinal)
                    ? throw new IOException("trusted root attributes unavailable")
                    : directory.Attributes
            });

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Null(result.Proof);
        Assert.Equal("release-docs-archive-verification-failed", diagnostic.Code);
        Assert.Contains("trusted root", diagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains("trusted root attributes unavailable", diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateMatchesPhysicalPathsUsingFilesystemCasingRules()
    {
        string[] manifestPaths = ["docs/intelligence/package.html"];

        var caseInsensitivePaths = ReleaseDocsArchiveGate.CreatePhysicalManifestPathSet(
            manifestPaths,
            StringComparer.OrdinalIgnoreCase);
        var caseSensitivePaths = ReleaseDocsArchiveGate.CreatePhysicalManifestPathSet(
            manifestPaths,
            StringComparer.Ordinal);

        Assert.Contains("docs/Intelligence/package.html", caseInsensitivePaths);
        Assert.DoesNotContain("docs/Intelligence/package.html", caseSensitivePaths);
    }

    [Fact]
    public void StableDocsArchiveGateDetectsTheArchiveRootsFilesystemCasingRules()
    {
        var caseInsensitive = ReleaseDocsArchiveGate.ResolvePhysicalPathComparer(
            "/release/archive",
            _ => true,
            _ => ["/release/archive/.appsurface-docs-release-manifest.json"]);
        var caseSensitive = ReleaseDocsArchiveGate.ResolvePhysicalPathComparer(
            "/release/archive",
            _ => false,
            _ => ["/release/archive/.appsurface-docs-release-manifest.json"]);
        var uppercaseRoot = ReleaseDocsArchiveGate.ResolvePhysicalPathComparer(
            "/RELEASE/ARCHIVE",
            _ => false,
            _ => ["/RELEASE/ARCHIVE/.appsurface-docs-release-manifest.json"]);
        var numericRoot = ReleaseDocsArchiveGate.ResolvePhysicalPathComparer(
            "/123/456",
            _ => true,
            _ => ["/123/456/.appsurface-docs-release-manifest.json"]);
        var ambiguousSibling = ReleaseDocsArchiveGate.ResolvePhysicalPathComparer(
            "/release/archive",
            _ => true,
            _ =>
            [
                "/release/archive/.appsurface-docs-release-manifest.json",
                "/release/archive/.appsurface-docs-release-manifest.jsoN"
            ]);
        var unreadableParent = ReleaseDocsArchiveGate.ResolvePhysicalPathComparer(
            "/release/archive",
            _ => true,
            _ => throw new IOException("Parent enumeration failed."));
        var unreadableProbe = ReleaseDocsArchiveGate.ResolvePhysicalPathComparer(
            "/release/archive",
            _ => throw new IOException("Manifest probe failed."),
            _ => ["/release/archive/.appsurface-docs-release-manifest.json"]);

        Assert.Same(StringComparer.OrdinalIgnoreCase, caseInsensitive);
        Assert.Same(StringComparer.Ordinal, caseSensitive);
        Assert.Same(StringComparer.Ordinal, uppercaseRoot);
        Assert.Same(StringComparer.OrdinalIgnoreCase, numericRoot);
        Assert.Same(StringComparer.Ordinal, ambiguousSibling);
        Assert.Same(StringComparer.Ordinal, unreadableParent);
        Assert.Same(StringComparer.Ordinal, unreadableProbe);
    }

    [Fact]
    public async Task StableDocsArchiveGateReportsExactTreeSegmentInspectionFailure()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var releasesPath = Path.Join(docs.TrustedReleaseRootPath, "releases");
        using var inspectorScope = ReleaseDocsArchiveGate.UseFileSystemInspectorForTesting(
            new DelegatingFileSystemInspector
            {
                DirectoryAttributes = directory => string.Equals(directory.FullName, releasesPath, StringComparison.Ordinal)
                    ? throw new IOException("release segment attributes unavailable")
                    : directory.Attributes
            });

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Null(result.Proof);
        Assert.Equal("release-docs-archive-verification-failed", diagnostic.Code);
        Assert.Contains("exact tree is unsafe", diagnostic.Problem, StringComparison.Ordinal);
        Assert.Contains("release segment attributes unavailable", diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenArchiveEntryAttributesCannotBeRead()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        using var inspectorScope = ReleaseDocsArchiveGate.UseFileSystemInspectorForTesting(
            new DelegatingFileSystemInspector
            {
                FileSystemInfoAttributes = entry => string.Equals(entry.Name, "index.html", StringComparison.Ordinal)
                    ? throw new IOException("archive entry attributes unavailable")
                    : entry.Attributes
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("archive entry attributes unavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestFileAttributesCannotBeRead()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        using var inspectorScope = ReleaseDocsArchiveGate.UseFileSystemInspectorForTesting(
            new DelegatingFileSystemInspector
            {
                FileAttributes = file => string.Equals(file.Name, ".appsurface-docs-release-manifest.json", StringComparison.Ordinal)
                    ? throw new IOException("release manifest attributes unavailable")
                    : file.Attributes
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("release manifest attributes unavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestFileDigestMismatches()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var index = await ReadDocsArchiveFileAsync(docs, "index.html");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("index.html", index.Length, new string('b', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("different SHA-256 digest", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestListedFileCannotBeRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.SetUnixFileMode(DocsArchivePath(docs, "index.html"), UnixFileMode.None);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("could not be read", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenRouteManifestAliasCollidesWithCanonicalRoute()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "packages",
                  "recoveryAliases": [],
                  "declaredAliases": []
                },
                {
                  "sourcePath": "cli.html",
                  "canonicalRoutePath": "cli",
                  "recoveryAliases": ["packages"],
                  "declaredAliases": []
                }
              ]
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("collides with a canonical route", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """
        {
          "schema": "wrong",
          "entries": []
        }
        """,
        "is malformed")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "recoveryAliases": [],
              "declaredAliases": []
            }
          ]
        }
        """,
        "require canonicalRoutePath")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": [],
              "declaredAliases": []
            },
            {
              "sourcePath": "packages.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": [],
              "declaredAliases": []
            }
          ]
        }
        """,
        "duplicate canonical route")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "matches its canonical route")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["../outside"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "alias '../outside' is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages?preview=true",
              "recoveryAliases": [],
              "declaredAliases": []
            }
          ]
        }
        """,
        "unsafe canonical route")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old\\\\packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old//packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old/ /packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old-packages", "old-packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is duplicated")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["install"],
              "declaredAliases": []
            },
            {
              "sourcePath": "cli.html",
              "canonicalRoutePath": "cli",
              "recoveryAliases": ["install"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "points at multiple canonical routes")]
    [InlineData(
        "null",
        "malformed")]
    public async Task PublishRejectsStableReleaseWhenRouteManifestShapeIsInvalid(
        string routeManifestJson,
        string expectedMessage)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0", routeManifestJson);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAcceptsStableReleaseWhenRouteManifestUsesEmptyRoutePath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": " ",
                  "recoveryAliases": [],
                  "declaredAliases": []
                }
              ]
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAcceptsStableReleaseWhenRouteManifestUsesDeclaredAliasAndFragment()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "packages#intro",
                  "declaredAliases": ["old-packages#intro"]
                }
              ]
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAcceptsStableReleaseWhenRouteManifestSkipsBlankAliases()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "packages",
                  "recoveryAliases": [" ", "old-packages"],
                  "declaredAliases": [""]
                }
              ]
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAcceptsStableReleaseWhenRouteManifestOmitsEntries()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1"
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckStablePreparedReleaseVerifiesFallbackDocsCatalog()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidenceJson = await ReadFileAsync("releases/v0.1.0.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        await WriteFileAsync("releases/v0.1.0.evidence.json", ReleaseEvidence.Serialize(PinDocsArchive(bundle, docs)));

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("- Docs archive verification: `availableVerified`", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("- Docs verified file count: `2`", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckStablePreparedReleaseAcceptsGeneratedEvidenceDigestAgainstVerifiedCatalogDigest()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidenceJson = await ReadFileAsync("releases/v0.1.0.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        var generatedDigestBundle = ReleaseEvidenceV2.RefreshSubject(bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "configured",
                docs.ExactTreePath,
                ReleaseEvidence.DocsArchiveGeneratedDigest,
                "appsurface-docs-release-manifest-v1",
                docs.FileCount,
                new ReleaseEvidenceCatalogEntry(docs.ExactTreePath, ReleaseEvidence.DocsArchiveGeneratedDigest))
        });
        await WriteFileAsync("releases/v0.1.0.evidence.json", ReleaseEvidence.Serialize(generatedDigestBundle));

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("- Docs archive verification: `availableVerified`", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckStablePreparedReleaseRequiresDocsEvidenceWithoutAllowExistingTargets()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var result = await RunAsync(
            ["check", "--version", "0.1.0"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-docs-archive-required", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckStablePreparedReleaseVerifiesConfiguredDocsCatalog()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidenceJson = await ReadFileAsync("releases/v0.1.0.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        await WriteFileAsync("releases/v0.1.0.evidence.json", ReleaseEvidence.Serialize(PinDocsArchive(bundle, docs)));

        var result = await RunAsync(
            [
                "check",
                "--version",
                "0.1.0",
                "--allow-existing-targets",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("- Docs archive verification: `availableVerified`", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishUsesConfiguredBaseRefForReachability()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(baseRef: "release/0.1.0", docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--base-ref",
                "release/0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("origin/release/0.1.0")]
    [InlineData("refs/heads/release/0.1.0")]
    [InlineData("refs/remotes/origin/release/0.1.0")]
    public async Task PublishNormalizesBranchishBaseRefForReachability(string baseRef)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(baseRef: "release/0.1.0", docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--base-ref",
                baseRef,
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task PublishAllowsObjectIdLengthBranchNameWhenItIsNotAFullObjectId()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var baseRef = "0123456789abcdef0123456789abcdef0123456g";
        var runner = CreateSuccessfulStablePublishRunner(baseRef: baseRef, docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--base-ref",
                baseRef,
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("origin/")]
    [InlineData("origin/-release/0.1.0")]
    [InlineData("refs/heads/")]
    [InlineData("refs/heads/-release/0.1.0")]
    [InlineData("refs/remotes/origin/")]
    [InlineData("refs/tags/v0.1.0")]
    [InlineData("refs/remotes/upstream/release/0.1.0")]
    [InlineData("/release/0.1.0")]
    [InlineData(".release/0.1.0")]
    [InlineData("release..0.1.0")]
    [InlineData("release//0.1.0")]
    [InlineData("release/.hidden")]
    [InlineData("release.lock")]
    [InlineData("topic@{1}")]
    [InlineData("release 0.1.0")]
    [InlineData("release\\0.1.0")]
    [InlineData("release~0.1.0")]
    [InlineData("release^0.1.0")]
    [InlineData("release:0.1.0")]
    [InlineData("qa?hotfix")]
    [InlineData("qa*hotfix")]
    [InlineData("release[hotfix]")]
    [InlineData("@")]
    [InlineData("release.")]
    [InlineData("release/")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task PublishRejectsUnsupportedBaseRefShapes(string baseRef)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulStablePublishRunner(baseRef: "release/0.1.0");

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--base-ref", baseRef, "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-base-ref-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("git cat-file -t refs/tags/v0.1.0-preview.1", "commit", "release-tag-lightweight")]
    [InlineData("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", "stdout failure", "release-tag-commit-missing")]
    [InlineData("git merge-base --is-ancestor abc123 origin/main", "", "release-tag-unreachable-from-base-ref")]
    [InlineData("gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0-preview.1\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", "", "release-prerelease-packages-not-published")]
    [InlineData("gh release view v0.1.0-preview.1 --json isDraft,url", "{\"isDraft\":false,\"url\":\"https://example.test\"}", "release-github-release-exists")]
    [InlineData("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.md", "", "release-note-missing-from-tag")]
    public async Task PublishReportsTagAndGitHubValidationFailures(string failingCommand, string stdout, string expectedCode)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var failingResult = expectedCode switch
        {
            "release-tag-lightweight" => new CommandResult(0, stdout, ""),
            "release-github-release-exists" => new CommandResult(0, stdout, ""),
            "release-prerelease-packages-not-published" => new CommandResult(0, stdout, ""),
            _ => new CommandResult(1, stdout, "validation failed")
        };
        runner.Add(failingCommand, failingResult);

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Code: {expectedCode}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsV2TagThatDoesNotContainPreparationBaseCommit()
    {
        await SeedRepositoryAsync();
        var runner = await CreateSuccessfulV2PublishRunnerAsync();
        runner.Add("git merge-base --is-ancestor aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa abc123", new CommandResult(1, "", "not an ancestor"));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-preparation-base-commit-not-contained-by-tag", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsV2EvidenceWithNonCanonicalPreparationBaseCommit()
    {
        await SeedRepositoryAsync();
        var runner = await CreateSuccessfulV2PublishRunnerAsync("HEAD");

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-preparation-base-commit-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsTagBoundEvidenceDiagnosticsBeforeReleaseCreation()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        runner.Add(
            "git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json",
            new CommandResult(0, ReleaseEvidence.Serialize(bundle with { Schema = "unsupported" }), ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-schema-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishCanReturnJsonWithoutGithubOutputFile()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            CreateSuccessfulPublishRunner());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"prerelease\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"evidencePath\": \"releases/v0.1.0-preview.1.evidence.json\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishValidatesSchemaV2CurrentPointerArtifacts()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            await CreateSuccessfulV2PublishRunnerAsync());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"evidencePath\": \"releases/v0.1.0-preview.1.evidence.json\"", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("releases/current.md", "release-current-pointer-missing-from-tag")]
    [InlineData("releases/current.md.yml", "release-current-pointer-sidecar-missing-from-tag")]
    public async Task PublishRejectsSchemaV2EvidenceWhenFrozenCurrentPointerArtifactIsMissing(string path, string expectedCode)
    {
        await SeedRepositoryAsync();
        var runner = await CreateSuccessfulV2PublishRunnerAsync();
        runner.Add(
            $"git show v0.1.0-preview.1:{path}",
            new CommandResult(1, "", "missing frozen pointer artifact"));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Code: {expectedCode}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReturnsDiagnosticWhenSchemaV2ResolutionUsesTaggedPathAsAlias()
    {
        await SeedRepositoryAsync();
        var runner = await CreateSuccessfulV2PublishRunnerAsync();
        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        var malformed = bundle with
        {
            CoordinatedPackageReleaseNoteResolutions =
            [
                new CoordinatedPackageReleaseNoteResolution(
                    "Core/ForgeTrust.AppSurface.Core.csproj",
                    "coordinated",
                    bundle.ReleaseNotePath,
                    bundle.ReleaseNotePath,
                    bundle.Tag,
                    bundle.Commits.PreparationBaseCommit)
            ]
        };
        runner.Add(
            "git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json",
            new CommandResult(0, ReleaseEvidence.Serialize(malformed), ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-package-link-mismatch", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsPrereleaseWithGeneratedDocsManifestDigest()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0-preview.1")) with
        {
            ReleaseManifestSha256 = ReleaseEvidence.DocsArchiveGeneratedDigest
        };

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            CreateSuccessfulPublishRunner(docs));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-docs-manifest-digest-mismatch", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishEmitsStructuredOutputsForAnnotatedPrereleaseTag()
    {
        await SeedRepositoryAsync();
        var githubOutput = Path.Join(_repositoryRoot, "github-output.txt");
        var runner = CreateSuccessfulPublishRunner();

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0-preview.1",
                "--tag",
                "v0.1.0-preview.1",
                "--dry-run",
                "--github-output",
                githubOutput
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"tag\": \"v0.1.0-preview.1\"", result.Stdout, StringComparison.Ordinal);

        var output = await File.ReadAllTextAsync(githubOutput);
        Assert.Contains("tag=v0.1.0-preview.1", output, StringComparison.Ordinal);
        Assert.Contains("tag_commit=abc123", output, StringComparison.Ordinal);
        Assert.Contains("evidence_path=releases/v0.1.0-preview.1.evidence.json", output, StringComparison.Ordinal);
        Assert.Contains("evidence_subject_sha256=", output, StringComparison.Ordinal);
        Assert.Contains("evidence_tag_commit=abc123", output, StringComparison.Ordinal);
        Assert.Contains("prerelease=true", output, StringComparison.Ordinal);
        Assert.Contains("notes_file=", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorsHandleFallbackShapes()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var workspace = new ReleaseWorkspace(_repositoryRoot);

        Assert.Equal(Path.Join(_repositoryRoot, "CHANGELOG.md"), workspace.ChangelogPath);
        Assert.Equal(Path.Join(_repositoryRoot, "releases", "v0.1.0-preview.1.md"), workspace.ReleaseNotePath(version));
        Assert.True(ReleaseWorkspace.IsUnderPath(_repositoryRoot, Path.Join(_repositoryRoot, "releases")));
        Assert.False(ReleaseWorkspace.IsUnderPath(_repositoryRoot, Path.GetTempPath()));

        var changelog = ChangelogEditor.RollForward(
            """
            # Changelog

            ## Unreleased

            ## No tagged releases yet

            AppSurface is still defining its first release boundary.
            """,
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release manifest: `releases/v0.1.0-preview.1.release.json`", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release evidence bundle: `releases/v0.1.0-preview.1.evidence.json`", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("## No tagged releases yet", changelog, StringComparison.Ordinal);

        var releaseNote = ReleaseNoteBuilder.Build(
            version,
            new DateOnly(2026, 5, 25),
            "# Unreleased\n\nThis is the living release note for the next coordinated AppSurface version. It stays provisional until a tag is cut.\n");
        Assert.Contains($"# Release {version}{Environment.NewLine}{Environment.NewLine}", releaseNote, StringComparison.Ordinal);

        var appendedChangelog = ChangelogEditor.RollForward(
            "# Changelog\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", appendedChangelog, StringComparison.Ordinal);
        Assert.Contains("- Release evidence bundle: `releases/v0.1.0-preview.1.evidence.json`", appendedChangelog, StringComparison.Ordinal);
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", appendedChangelog, StringComparison.Ordinal);

        var terminalUnreleasedChangelog = ChangelogEditor.RollForward(
            "# Changelog\n\n## Unreleased\n\n- Current work.\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.DoesNotContain("- Current work.", terminalUnreleasedChangelog, StringComparison.Ordinal);
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", terminalUnreleasedChangelog, StringComparison.Ordinal);
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", terminalUnreleasedChangelog, StringComparison.Ordinal);
        Assert.Contains(
            $"- Authoring workflow: [Release authoring checklist](./releases/release-authoring-checklist.md){Environment.NewLine}{Environment.NewLine}## 0.1.0-preview.1 - 2026-05-25",
            terminalUnreleasedChangelog,
            StringComparison.Ordinal);

        var multiReleaseChangelog = ChangelogEditor.RollForward(
            "# Changelog\n\n## Unreleased\n\n## 0.0.1 - 2026-01-01\n\n- Previous work.\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", multiReleaseChangelog, StringComparison.Ordinal);
        Assert.Matches("## Unreleased[\\s\\S]*## 0\\.1\\.0-preview\\.1 - 2026-05-25[\\s\\S]*## 0\\.0\\.1 - 2026-01-01", multiReleaseChangelog);

        var placeholderWithFollowingRelease = ChangelogEditor.RollForward(
            """
            # Changelog

            ## Unreleased

            ## No tagged releases yet

            Placeholder.

            ## 0.0.1 - 2026-01-01

            - Previous work.
            """,
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.DoesNotContain("Placeholder.", placeholderWithFollowingRelease, StringComparison.Ordinal);
        Assert.Contains("## 0.0.1 - 2026-01-01", placeholderWithFollowingRelease, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleRegistersReleaseServicesAndNoOpHooks()
    {
        var module = new ReleaseCliModule();
        var context = new StartupContext([], module);
        var services = new ServiceCollection();

        module.ConfigureServices(context, services);
        module.ConfigureHostBeforeServices(context, Host.CreateDefaultBuilder());
        module.ConfigureHostAfterServices(context, Host.CreateDefaultBuilder());
        module.RegisterDependentModules(new ModuleDependencyBuilder());

        using var provider = services.BuildServiceProvider();
        Assert.Equal(Directory.GetCurrentDirectory(), provider.GetRequiredService<ReleaseExecutionContext>().CurrentDirectory);
        Assert.IsType<ProcessCommandRunner>(provider.GetRequiredService<ICommandRunner>());
        Assert.IsType<SystemReleaseClock>(provider.GetRequiredService<IReleaseClock>());
    }

    [Fact]
    public void WorkspaceRejectsRootedRepositoryRelativePaths()
    {
        var workspace = new ReleaseWorkspace(_repositoryRoot);

        var exception = Assert.Throws<ArgumentException>(() => workspace.PathFor(Path.GetTempPath()));

        Assert.Equal("relativePath", exception.ParamName);
    }

    [Fact]
    public void WorkspaceRejectsTraversalRepositoryRelativePaths()
    {
        var workspace = new ReleaseWorkspace(_repositoryRoot);

        var exception = Assert.Throws<ArgumentException>(() => workspace.PathFor("../outside.md"));

        Assert.Equal("relativePath", exception.ParamName);
    }

    [Fact]
    public async Task ProcessCommandRunnerTimesOutStuckCommands()
    {
        var runner = new ProcessCommandRunner();
        var invocation = CreateSlowCommandInvocation();

        var result = await runner.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("timed out", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReportsPrereleasePackageWorkflowErrors()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0-preview.1\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"",
            new CommandResult(1, "", "workflow query failed"));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-prerelease-packages-not-published", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("workflow query failed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReportsCommandStdoutWhenStderrIsEmpty()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", new CommandResult(1, "stdout failure", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stdout failure", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReportsGitBlobCommandStdoutWhenStderrIsEmpty()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json", new CommandResult(1, "blob stdout failure", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("blob stdout failure", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsNullTagEvidenceWithStructuredDiagnostic()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json", new CommandResult(0, "null\n", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-schema-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("NullReferenceException", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsTagsThatCannotCreateTempPath()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        runner.Add("git cat-file -t refs/tags//", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags//^{commit}", new CommandResult(0, "abc123\n", ""));
        runner.Add("git merge-base --is-ancestor abc123 origin/main", new CommandResult(0, "", ""));
        runner.Add("gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"/\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "https://github.com/example/actions/runs/1\n", ""));
        runner.Add("gh release view / --json isDraft,url", new CommandResult(1, "", "release not found"));
        var publishing = new ReleasePublishing(new ReleaseWorkspace(_repositoryRoot), runner);
        var options = new ReleaseOptions(
            "publish",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            "/",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => publishing.PublishAsync(options, CancellationToken.None));

        Assert.Equal("release-tag-invalid-temp-path", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task PublishingDefaultsMissingTagToVersionTag()
    {
        await SeedRepositoryAsync();
        var publishing = new ReleasePublishing(
            new ReleaseWorkspace(_repositoryRoot),
            CreateSuccessfulPublishRunner());
        var options = new ReleaseOptions(
            "publish",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            Tag: null,
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var outputs = await publishing.PublishAsync(options, CancellationToken.None);

        Assert.Equal("v0.1.0-preview.1", outputs.Tag);
    }

    [Fact]
    public async Task PublishWritesMultilineGithubOutputs()
    {
        await SeedRepositoryAsync();
        var githubOutput = Path.Join(_repositoryRoot, "artifacts", "github-output.txt");
        var publishing = new ReleasePublishing(new ReleaseWorkspace(_repositoryRoot), new FakeCommandRunner());
        var options = new ReleaseOptions(
            "publish",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            "v0.1.0-preview.1",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: githubOutput,
            FailOnWarnings: false,
            AllowExistingTargets: false);
        var outputs = new PublishOutputs(
            "0.1.0-preview.1",
            "v0.1.0-preview.1",
            "abc123",
            "releases/v0.1.0-preview.1.md",
            "first\nsecond",
            "prerelease",
            "releases/v0.1.0-preview.1.evidence.json",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "abc123",
            null,
            Prerelease: true,
            DryRun: true);

        await publishing.WriteOutputsAsync(outputs, options, CancellationToken.None);

        var output = await File.ReadAllTextAsync(githubOutput);
        Assert.Contains("notes_file<<EOF_", output, StringComparison.Ordinal);
        Assert.Contains("first\nsecond", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsGithubOutputRootPath()
    {
        var publishing = new ReleasePublishing(new ReleaseWorkspace(_repositoryRoot), new FakeCommandRunner());
        var options = new ReleaseOptions(
            "publish",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            "v0.1.0-preview.1",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: Path.GetPathRoot(_repositoryRoot),
            FailOnWarnings: false,
            AllowExistingTargets: false);
        var outputs = new PublishOutputs(
            "0.1.0-preview.1",
            "v0.1.0-preview.1",
            "abc123",
            "releases/v0.1.0-preview.1.md",
            "notes.md",
            "prerelease",
            "releases/v0.1.0-preview.1.evidence.json",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "abc123",
            null,
            Prerelease: true,
            DryRun: true);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => publishing.WriteOutputsAsync(outputs, options, CancellationToken.None));

        Assert.Equal("release-github-output-path-invalid", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task DocsPublicationCreatesDeterministicArchiveCatalogAndRecoveryOutputs()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var exactTree = TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath);
        var archive = RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz");
        var archiveAgain = RepositoryPath("artifacts/appsurface-docs-v0.1.0-again.tar.gz");
        var staging = ExternalPath("pages");
        var stagingAgain = ExternalPath("pages-again");
        var planPath = RepositoryPath("artifacts/docs-publication-plan.json");
        var planAgainPath = RepositoryPath("artifacts/docs-publication-plan-again.json");
        var summaryPath = RepositoryPath("artifacts/docs-publication-summary.md");
        var githubOutput = RepositoryPath("artifacts/github-output.txt");
        Directory.CreateDirectory(Path.Join(staging, "stale"));
        await File.WriteAllTextAsync(Path.Join(staging, "stale", "old.html"), "old docs");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                exactTree,
                "--archive-output",
                archive,
                "--pages-staging-root",
                staging,
                "--plan-output",
                planPath,
                "--summary-output",
                summaryPath,
                "--expected-release-manifest-sha256",
                docs.ReleaseManifestSha256,
                "--github-output",
                githubOutput
            ],
            new FakeCommandRunner());
        var second = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                exactTree,
                "--archive-output",
                archiveAgain,
                "--pages-staging-root",
                stagingAgain,
                "--plan-output",
                planAgainPath,
                "--summary-output",
                RepositoryPath("artifacts/docs-publication-summary-again.md"),
                "--expected-release-manifest-sha256",
                docs.ReleaseManifestSha256
            ],
            new FakeCommandRunner());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.True(File.Exists(archive));
        Assert.True(File.Exists(archive + ".sha256"));
        Assert.Equal(await ComputeSha256Async(archive), await ComputeSha256Async(archiveAgain));
        Assert.False(File.Exists(Path.Join(staging, "stale", "old.html")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(staging, docs.ExactTreePath, ".appsurface-docs-release-manifest.json")));
        var catalog = await File.ReadAllTextAsync(Path.Join(staging, "versions.json"));
        Assert.Contains("\"recommendedVersion\": \"0.1.0\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"exactTreePath\": \"releases/0.1.0\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"supportState\": \"Current\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"supportState\": \"Supported\"", catalog, StringComparison.Ordinal);
        Assert.Contains(docs.ReleaseManifestSha256, catalog, StringComparison.Ordinal);
        var plan = await File.ReadAllTextAsync(planPath);
        Assert.Contains("\"schema\": \"appsurface-docs-publication-plan-v1\"", plan, StringComparison.Ordinal);
        Assert.Contains("\"supportState\": \"Current\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("\"supportState\": \"Supported\"", plan, StringComparison.Ordinal);
        Assert.Contains("\"publicAssetReplaceAllowed\": false", plan, StringComparison.Ordinal);
        var summary = await File.ReadAllTextAsync(summaryPath);
        Assert.Contains("Resume commands", summary, StringComparison.Ordinal);
        Assert.Contains("gh release edit v0.1.0 --draft=false", summary, StringComparison.Ordinal);
        var outputs = await File.ReadAllTextAsync(githubOutput);
        Assert.Contains("archive_sha256=", outputs, StringComparison.Ordinal);
        Assert.Contains("recovery_summary_path=", outputs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationPreservesPriorReleaseArchiveCatalogEntries()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var existingPagesRoot = RepositoryPath("artifacts/existing-pages");
        Directory.CreateDirectory(Path.Join(existingPagesRoot, "docs"));
        await File.WriteAllTextAsync(Path.Join(existingPagesRoot, "docs", "index.html"), "current docs root");
        var existingReleaseRoot = Path.Join(existingPagesRoot, "releases", "0.0.9");
        Directory.CreateDirectory(existingReleaseRoot);
        await File.WriteAllTextAsync(Path.Join(existingReleaseRoot, "index.html"), "older release");
        await File.WriteAllTextAsync(
            Path.Join(existingPagesRoot, "versions.json"),
            JsonSerializer.Serialize(
                new
                {
                    recommendedVersion = "0.0.9",
                    versions = new object[]
                    {
                        new
                        {
                            version = "0.0.9",
                            label = "0.0.9",
                            supportState = "Current",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/0.0.9",
                            releaseManifestSha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                        },
                        new
                        {
                            version = "0.0.8",
                            label = "0.0.8",
                            supportState = "Maintained",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/0.0.8",
                            releaseManifestSha256 = "2222222222222222222222222222222222222222222222222222222222222222"
                        },
                        new
                        {
                            version = "0.0.7",
                            label = "0.0.7",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/0.0.7",
                            releaseManifestSha256 = "3333333333333333333333333333333333333333333333333333333333333333"
                        },
                        "ignored legacy value",
                        new
                        {
                            label = "missing version",
                            exactTreePath = "releases/missing-version"
                        },
                        new
                        {
                            version = "0.1.0",
                            label = "duplicate current",
                            supportState = "Current",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/0.1.0",
                            releaseManifestSha256 = "4444444444444444444444444444444444444444444444444444444444444444"
                        },
                        new
                        {
                            version = "",
                            label = "blank version",
                            supportState = "Maintained",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/blank",
                            releaseManifestSha256 = "5555555555555555555555555555555555555555555555555555555555555555"
                        },
                        new
                        {
                            version = "0.2.0-preview.1",
                            label = "0.2.0-preview.1",
                            supportState = "Maintained",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/0.2.0-preview.1",
                            releaseManifestSha256 = "6666666666666666666666666666666666666666666666666666666666666666"
                        },
                        new
                        {
                            version = "not-a-version",
                            label = "not-a-version",
                            supportState = "Maintained",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/not-a-version",
                            releaseManifestSha256 = "7777777777777777777777777777777777777777777777777777777777777777"
                        }
                    }
                },
                ReleaseJson.Options));

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                existingPagesRoot,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(0, result.ExitCode);
        var staging = ExternalPath("pages");
        Assert.True(File.Exists(Path.Join(staging, "docs", "index.html")));
        Assert.True(File.Exists(Path.Join(staging, "releases", "0.0.9", "index.html")));
        var catalog = await File.ReadAllTextAsync(Path.Join(staging, "versions.json"));
        Assert.Contains("\"version\": \"0.0.9\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"0.0.8\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"0.0.7\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"supportState\": \"Maintained\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"0.1.0\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"label\": \"duplicate current\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"supportState\": \"Current\"", catalog, StringComparison.Ordinal);
        using var catalogDocument = JsonDocument.Parse(catalog);
        var catalogVersions = catalogDocument.RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(version => version.GetProperty("version").GetString()!)
            .ToArray();
        Assert.Equal(
            ["0.1.0", "0.0.9", "0.0.8", "0.0.7", "", "0.2.0-preview.1", "not-a-version"],
            catalogVersions);
    }

    [Fact]
    public async Task DocsPublicationRejectsInvalidExistingPagesCatalogWithDiagnostic()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var existingPagesRoot = RepositoryPath("artifacts/existing-pages");
        Directory.CreateDirectory(existingPagesRoot);
        await File.WriteAllTextAsync(Path.Join(existingPagesRoot, "versions.json"), "{");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                existingPagesRoot,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-catalog-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("versions.json", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationCreatesMaintainedPrereleaseCatalogEntry()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0-preview.1");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0-preview.1",
                "--tag",
                "v0.1.0-preview.1",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0-preview.1.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(0, result.ExitCode);
        var catalog = await File.ReadAllTextAsync(Path.Join(ExternalPath("pages"), "versions.json"));
        Assert.Contains("\"version\": \"0.1.0-preview.1\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"supportState\": \"Maintained\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"recommendedVersion\": null", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationPreservesRecommendedVersionWhenNotPromoting()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var existingPagesRoot = RepositoryPath("artifacts/existing-pages");
        Directory.CreateDirectory(existingPagesRoot);
        await File.WriteAllTextAsync(
            Path.Join(existingPagesRoot, "versions.json"),
            JsonSerializer.Serialize(
                new
                {
                    recommendedVersion = "9.0.0",
                    versions = new[]
                    {
                        new
                        {
                            version = "9.0.0",
                            label = "9.0.0",
                            supportState = "Current",
                            visibility = "Public",
                            advisoryState = "None",
                            exactTreePath = "releases/9.0.0",
                            releaseManifestSha256 = "1111111111111111111111111111111111111111111111111111111111111111"
                        }
                    }
                },
                ReleaseJson.Options));

        var planPath = RepositoryPath("artifacts/docs-publication-plan.json");
        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                existingPagesRoot,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                planPath,
                "--promote-recommended",
                "false"
            ],
            new FakeCommandRunner());

        Assert.Equal(0, result.ExitCode);
        var catalog = await File.ReadAllTextAsync(Path.Join(ExternalPath("pages"), "versions.json"));
        Assert.Contains("\"recommendedVersion\": \"9.0.0\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"0.1.0\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"9.0.0\"", catalog, StringComparison.Ordinal);
        Assert.Contains("\"supportState\": \"Maintained\"", catalog, StringComparison.Ordinal);
        var plan = await File.ReadAllTextAsync(planPath);
        Assert.Contains("\"recommendedVersion\": \"9.0.0\"", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsManifestDigestMismatch()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json"),
                "--expected-release-manifest-sha256",
                "0000000000000000000000000000000000000000000000000000000000000000"
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-manifest-digest-mismatch", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsUnsafeExactTreePath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var originalExactTree = TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath);
        var unsafeExactTree = RepositoryPath(".hidden/releases/0.1.0");
        Directory.CreateDirectory(unsafeExactTree);
        foreach (var file in Directory.EnumerateFiles(originalExactTree))
        {
            File.Copy(file, TestPathUtils.PathUnder(unsafeExactTree, Path.GetFileName(file)));
        }

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                unsafeExactTree,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-path-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(".hidden/releases/0.1.0", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsRecommendedVersionDowngrade()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var existingPagesRoot = RepositoryPath("artifacts/existing-pages");
        Directory.CreateDirectory(existingPagesRoot);
        await File.WriteAllTextAsync(
            Path.Join(existingPagesRoot, "versions.json"),
            JsonSerializer.Serialize(
                new
                {
                    versions = new[]
                    {
                        new
                        {
                            version = "0.1.1",
                            exactTreePath = "releases/0.1.1",
                            releaseManifestSha256 = docs.ReleaseManifestSha256
                        }
                    }
                },
                ReleaseJson.Options));

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                existingPagesRoot,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-recommended-downgrade", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsRecommendedVersionDowngradeFromOrphanedRecommendedVersion()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var existingPagesRoot = RepositoryPath("artifacts/existing-pages");
        Directory.CreateDirectory(existingPagesRoot);
        await File.WriteAllTextAsync(
            Path.Join(existingPagesRoot, "versions.json"),
            JsonSerializer.Serialize(
                new
                {
                    recommendedVersion = "9.0.0",
                    versions = new[]
                    {
                        new
                        {
                            version = "0.0.9",
                            exactTreePath = "releases/0.0.9",
                            releaseManifestSha256 = docs.ReleaseManifestSha256
                        }
                    }
                },
                ReleaseJson.Options));

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                existingPagesRoot,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-recommended-downgrade", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("9.0.0", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsUnsafePagesStagingRootBeforeDeleting()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var sentinel = RepositoryPath("sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep me");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                _repositoryRoot,
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-output-path-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task DocsPublicationRejectsPagesStagingRootThatOverlapsExactTree()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var originalExactTree = TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath);
        var exactTree = ExternalPath("exact/releases/0.1.0");
        foreach (var file in Directory.EnumerateFiles(originalExactTree, "*", SearchOption.AllDirectories))
        {
            var target = TestPathUtils.PathUnder(exactTree, Path.GetRelativePath(originalExactTree, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                exactTree,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                exactTree,
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-output-path-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("docs exact tree", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("plan")]
    [InlineData("summary")]
    public async Task DocsPublicationRejectsGeneratedOutputPathsUnderExistingPagesRoot(string outputKind)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var existingPagesRoot = RepositoryPath("artifacts/existing-pages");
        Directory.CreateDirectory(existingPagesRoot);
        var archive = string.Equals(outputKind, "archive", StringComparison.Ordinal)
            ? Path.Join(existingPagesRoot, "appsurface-docs-v0.1.0.tar.gz")
            : RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz");
        var plan = string.Equals(outputKind, "plan", StringComparison.Ordinal)
            ? Path.Join(existingPagesRoot, "docs-publication-plan.json")
            : RepositoryPath("artifacts/docs-publication-plan.json");
        var summary = string.Equals(outputKind, "summary", StringComparison.Ordinal)
            ? Path.Join(existingPagesRoot, "docs-publication-summary.md")
            : RepositoryPath("artifacts/docs-publication-summary.md");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                existingPagesRoot,
                "--archive-output",
                archive,
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                plan,
                "--summary-output",
                summary
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-output-path-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("existing Pages payload", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsMissingExistingPagesRootBeforeWritingOutputs()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var archive = RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz");
        var plan = RepositoryPath("artifacts/docs-publication-plan.json");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                ExternalPath("missing-pages"),
                "--archive-output",
                archive,
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                plan
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-existing-pages-missing", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(archive));
        Assert.False(File.Exists(plan));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("plan")]
    [InlineData("summary")]
    public async Task DocsPublicationRejectsGeneratedOutputPathsUnderExactTree(string outputKind)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var exactTree = TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath);
        var archive = string.Equals(outputKind, "archive", StringComparison.Ordinal)
            ? Path.Join(exactTree, "appsurface-docs-v0.1.0.tar.gz")
            : RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz");
        var plan = string.Equals(outputKind, "plan", StringComparison.Ordinal)
            ? Path.Join(exactTree, "docs-publication-plan.json")
            : RepositoryPath("artifacts/docs-publication-plan.json");
        var summary = string.Equals(outputKind, "summary", StringComparison.Ordinal)
            ? Path.Join(exactTree, "docs-publication-summary.md")
            : RepositoryPath("artifacts/docs-publication-summary.md");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                exactTree,
                "--archive-output",
                archive,
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                plan,
                "--summary-output",
                summary
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-output-path-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("inside the docs exact tree", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsReparsePointArchiveEntries()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var exactTree = TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath);
        if (!TryCreateSymbolicLink(
                Path.Join(exactTree, "linked-index.html"),
                Path.Join(exactTree, "index.html"),
                isDirectory: false))
        {
            return;
        }

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                exactTree,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-reparse-entry", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("linked-index.html", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsReparsePointPagesEntries()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var existingPagesRoot = RepositoryPath("artifacts/existing-pages");
        Directory.CreateDirectory(existingPagesRoot);
        await File.WriteAllTextAsync(Path.Join(existingPagesRoot, "index.html"), "current docs");
        if (!TryCreateSymbolicLink(
                Path.Join(existingPagesRoot, "linked-index.html"),
                Path.Join(existingPagesRoot, "index.html"),
                isDirectory: false))
        {
            return;
        }

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--existing-pages-root",
                existingPagesRoot,
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-reparse-entry", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("linked-index.html", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsGithubOutputRootPath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json"),
                "--github-output",
                Path.GetPathRoot(_repositoryRoot)!
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-github-output-path-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsMissingTag()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-tag-required", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsMissingRequiredPaths()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-archive-output-required", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsInvalidPromoteRecommendedValue()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json"),
                "--promote-recommended",
                "maybe"
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-promote-recommended-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsTagMismatch()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.1",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-tag-mismatch", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsMissingExactTree()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                RepositoryPath("dist/docs/releases/missing"),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-exact-tree-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsPublicationRejectsMissingReleaseManifest()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.Delete(DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"));

        var result = await RunAsync(
            [
                "docs-publication",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--docs-exact-tree",
                TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath),
                "--archive-output",
                RepositoryPath("artifacts/appsurface-docs-v0.1.0.tar.gz"),
                "--pages-staging-root",
                ExternalPath("pages"),
                "--plan-output",
                RepositoryPath("artifacts/docs-publication-plan.json")
            ],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-publication-manifest-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsInvalidGithubReleaseStateJson()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulStablePublishRunner();
        runner.Add("gh release view v0.1.0 --json isDraft,url", new CommandResult(0, "{", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-github-release-state-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsUnknownGithubReleaseStateFailure()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulStablePublishRunner();
        runner.Add("gh release view v0.1.0 --json isDraft,url", new CommandResult(1, "", "HTTP 401: Bad credentials"));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-github-release-state-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Bad credentials", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Repository not found")]
    [InlineData("HTTP 404: Not Found")]
    public async Task PublishRejectsGenericGithubNotFoundFailures(string stderr)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulStablePublishRunner();
        runner.Add("gh release view v0.1.0 --json isDraft,url", new CommandResult(1, "", stderr));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-github-release-state-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(stderr, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAllowsReplacingSameTagDraftRelease()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);
        runner.Add("gh release view v0.1.0 --json isDraft,url", new CommandResult(0, "{\"isDraft\":true,\"url\":\"https://example.test/releases/v0.1.0\"}", ""));

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }

        if (Directory.Exists(_externalRoot))
        {
            Directory.Delete(_externalRoot, recursive: true);
        }
    }

    private async Task SeedRepositoryAsync()
    {
        await WriteFileAsync(
            ".github/workflows/nuget-prerelease-publish.yml",
            "name: NuGet Prerelease Publish\n");
        await WriteFileAsync(
            "CHANGELOG.md",
            """
            # Changelog

            ## Unreleased

            ### Added

            - Current work.

            ## No tagged releases yet

            AppSurface is still defining its first release boundary.
            """);
        await WriteFileAsync(
            "releases/unreleased.md",
            """
            # Unreleased

            This is the living release note for the next coordinated AppSurface version.

            ## What is taking shape

            - The release story is almost ready.
            <!-- appsurface:unreleased-entries section="taking-shape" -->

            ## Included in the next coordinated version

            ### Release and docs surface

            - The release cockpit prepares release pull requests.
            <!-- appsurface:unreleased-entries section="included" -->

            ## Migration watch

            - No migration steps are required.
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """);
        await WriteFileAsync(
            "releases/unreleased.md.yml",
            """
            title: Unreleased
            summary: Living proof artifact.
            page_type: release-note
            nav_group: Releases
            order: 15
            """);
        await WriteFileAsync("releases/current.md", ReleaseCurrentPointer.BuildNone());
        await WriteFileAsync("releases/current.md.yml", CurrentReleaseSidecarContent);
        await WriteFileAsync(
            "releases/templates/tagged-release-template.md",
            "# Release x.y.z\n");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Core/ForgeTrust.AppSurface.Core.csproj
                classification: public
                publish_decision: publish
                release_notes_path: releases/unreleased.md
                order: 10
              - project: Web/ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj
                classification: support
                publish_decision: support_publish
                release_notes_path: releases/unreleased.md
                order: 20
            """);
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var path = RepositoryPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static string FindSourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(TestPathUtils.PathUnder(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the AppSurface source root from the test output directory.");
    }

    private async Task<string> ReadFileAsync(string relativePath)
    {
        return await File.ReadAllTextAsync(RepositoryPath(relativePath));
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, CancellationToken.None);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string RepositoryPath(string relativePath)
    {
        return TestPathUtils.PathUnder(_repositoryRoot, relativePath);
    }

    private string ExternalPath(string relativePath)
    {
        return TestPathUtils.PathUnder(_externalRoot, relativePath);
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

    private async Task<CliResult> RunAsync(string[] args, FakeCommandRunner runner)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunAsync(
            args,
            stdout,
            stderr,
            _repositoryRoot,
            commandRunner: runner);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private CommandInvocation CreateSlowCommandInvocation()
    {
        if (OperatingSystem.IsWindows())
        {
            return new CommandInvocation(
                "cmd.exe",
                ["/c", "ping -n 6 127.0.0.1 > nul"],
                _repositoryRoot,
                TimeSpan.FromMilliseconds(50));
        }

        return new CommandInvocation(
            "/bin/sh",
            ["-c", "sleep 5"],
            _repositoryRoot,
            TimeSpan.FromMilliseconds(50));
    }

    private static FakeCommandRunner CreateSuccessfulPublishRunner(DocsArchiveFixture? docs = null)
    {
        var runner = new FakeCommandRunner();
        var releaseManifest = CreateReleaseManifestJson();
        runner.Add("git cat-file -t refs/tags/v0.1.0-preview.1", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", new CommandResult(0, "abc123\n", ""));
        runner.Add("git merge-base --is-ancestor abc123 origin/main", new CommandResult(0, "", ""));
        runner.Add("gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0-preview.1\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "https://github.com/example/actions/runs/1\n", ""));
        runner.Add("gh release view v0.1.0-preview.1 --json isDraft,url", new CommandResult(1, "", "release not found"));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.md", new CommandResult(0, TaggedReleaseNoteContent, ""));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.md.yml", new CommandResult(0, TaggedReleaseSidecarContent, ""));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.release.json", new CommandResult(0, releaseManifest, ""));
        runner.Add(
            "git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json",
            new CommandResult(
                0,
                CreateReleaseEvidenceJson(
                    releaseManifest,
                    "0.1.0-preview.1",
                    docs?.ExactTreePath,
                    docs?.ReleaseManifestSha256,
                    docs?.FileCount),
                ""));
        return runner;
    }

    private static ReleaseTagBinding CreateReleaseTagBinding()
    {
        var manifest = CreateReleaseManifestJson();
        var evidence = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(manifest), ReleaseJson.Options)!;
        return new ReleaseTagBinding(
            "v0.1.0-preview.1",
            ReleaseEvidence.ComputeSha256Hex(TaggedReleaseSidecarContent),
            ReleaseEvidence.ComputeSha256Hex(manifest),
            evidence.Subject.Sha256);
    }

    private static string CreateAnnotatedTagObject(string offset = "+0000")
    {
        var binding = CreateReleaseTagBinding();
        return $"object abc123\ntype commit\ntag v0.1.0-preview.1\ntagger Release Tests <release-tests@example.test> 1770000000 {offset}\n\n{binding.Render()}";
    }

    private async Task<FakeCommandRunner> CreateSuccessfulV2PublishRunnerAsync(string preparationBaseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit(preparationBaseCommit));
        Assert.Equal(0, prepare.ExitCode);

        var runner = CreateSuccessfulPublishRunner();
        runner.Add($"git merge-base --is-ancestor {preparationBaseCommit} abc123", new CommandResult(0, "", ""));
        const string tag = "v0.1.0-preview.1";
        foreach (var path in new[]
                 {
                     "releases/v0.1.0-preview.1.md",
                     "releases/v0.1.0-preview.1.md.yml",
                     "releases/v0.1.0-preview.1.release.json",
                     "releases/v0.1.0-preview.1.evidence.json",
                     "releases/current.md",
                     "releases/current.md.yml"
                 })
        {
            runner.Add($"git show {tag}:{path}", new CommandResult(0, await ReadFileAsync(path), ""));
        }
        runner.Add(
            $"git show {tag}:packages/package-index.yml",
            new CommandResult(0, await ReadFileAsync("packages/package-index.yml"), ""));

        return runner;
    }

    private static FakeCommandRunner CreateSuccessfulStablePublishRunner(
        string baseRef = "main",
        DocsArchiveFixture? docs = null,
        bool includeDocsCatalogEntry = true)
    {
        var runner = new FakeCommandRunner();
        var releaseManifest = CreateReleaseManifestJson(versionText: "0.1.0");
        var releaseSidecar = PreparedReleaseSidecarContent("0.1.0");
        var evidenceDocsExactTreePath = docs?.ExactTreePath ?? "releases/0.1.0";
        var evidenceDocsReleaseManifestSha256 = docs?.ReleaseManifestSha256 ?? new string('0', 64);
        var evidenceDocsFileCount = docs?.FileCount ?? 1;
        var releaseEvidence = CreateReleaseEvidenceJson(
            releaseManifest,
            "0.1.0",
            evidenceDocsExactTreePath,
            evidenceDocsReleaseManifestSha256,
            evidenceDocsFileCount,
            includeDocsCatalogEntry,
            releaseSidecar);
        runner.Add("git cat-file -t refs/tags/v0.1.0", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags/v0.1.0^{commit}", new CommandResult(0, "abc123\n", ""));
        runner.Add($"git merge-base --is-ancestor abc123 origin/{baseRef}", new CommandResult(0, "", ""));
        runner.Add("gh run list --workflow nuget-stable-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "https://github.com/example/actions/runs/2\n", ""));
        runner.Add("gh release view v0.1.0 --json isDraft,url", new CommandResult(1, "", "release not found"));
        runner.Add("git show v0.1.0:releases/v0.1.0.md", new CommandResult(0, TaggedReleaseNoteContent, ""));
        runner.Add("git show v0.1.0:releases/v0.1.0.md.yml", new CommandResult(0, releaseSidecar, ""));
        runner.Add("git show v0.1.0:releases/v0.1.0.release.json", new CommandResult(0, releaseManifest, ""));
        runner.Add("git show v0.1.0:releases/v0.1.0.evidence.json", new CommandResult(0, releaseEvidence, ""));
        return runner;
    }

    private async Task<CliResult> RunStablePublishWithDocsAsync(DocsArchiveFixture docs)
    {
        return await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            CreateSuccessfulStablePublishRunner(docs: docs));
    }

    private async Task<ReleaseDocsArchiveGateResult> ValidateStableDocsArchiveGateAsync(
        DocsArchiveFixture docs,
        string? trustedRootPath = "__fixture__",
        string command = "publish",
        string? docsCatalogPath = "__fixture__")
    {
        var options = new ReleaseOptions(
            command,
            _repositoryRoot,
            SemVer.Parse("0.1.0"),
            "v0.1.0",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false,
            DocsCatalogPath: string.Equals(docsCatalogPath, "__fixture__", StringComparison.Ordinal)
                ? docs.CatalogPath
                : docsCatalogPath,
            DocsTrustedReleaseRootPath: string.Equals(trustedRootPath, "__fixture__", StringComparison.Ordinal)
                ? docs.TrustedReleaseRootPath
                : trustedRootPath);
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(
            CreateReleaseEvidenceJson(
                CreateReleaseManifestJson(versionText: "0.1.0"),
                "0.1.0",
                docs.ExactTreePath,
                docs.ReleaseManifestSha256,
                docs.FileCount),
            ReleaseJson.Options)!;

        return await ReleaseDocsArchiveGate.ValidateStableAsync(
            new ReleaseWorkspace(_repositoryRoot),
            options,
            bundle,
            CancellationToken.None);
    }

    private static string CreateReleaseManifestJson(string? sourceCommit = "abc123", string versionText = "0.1.0-preview.1")
    {
        var version = SemVer.Parse(versionText);
        var releasePath = $"releases/v{version}.md";
        var manifest = new ReleaseManifest(
            "appsurface-release-manifest-v1",
            version.ToString(),
            version.TagName,
            "2026-05-25",
            sourceCommit,
            version.IsStable ? "stable" : "prerelease",
            [
                releasePath,
                $"releases/v{version}.md.yml",
                $"releases/v{version}.release.json",
                $"releases/v{version}.evidence.json"
            ],
            ["Core/ForgeTrust.AppSurface.Core.csproj"],
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", releasePath)],
            [],
            []);
        return JsonSerializer.Serialize(manifest, ReleaseJson.Options) + Environment.NewLine;
    }

    private static string CreateReleaseEvidenceJson(
        string releaseManifestJson,
        string versionText = "0.1.0-preview.1",
        string? docsExactTreePath = null,
        string? docsReleaseManifestSha256 = null,
        int? docsFileCount = null,
        bool includeDocsCatalogEntry = true,
        string? releaseSidecarContent = null)
    {
        var version = SemVer.Parse(versionText);
        var releasePath = $"releases/v{version}.md";
        var workspace = new ReleaseWorkspace(Path.Join(Path.GetTempPath(), "ReleaseToolEvidenceFixtures"));
        var evidence = ReleaseEvidence.BuildDraft(
            workspace,
            version,
            version.IsStable ? "stable" : "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            releaseSidecarContent ?? TaggedReleaseSidecarContent,
            releaseManifestJson,
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", releasePath)]);
        if (!string.IsNullOrWhiteSpace(docsExactTreePath) && !string.IsNullOrWhiteSpace(docsReleaseManifestSha256))
        {
            evidence = RefreshSubject(evidence with
            {
                DocsArchive = new ReleaseEvidenceDocsArchive(
                    "catalogPinned",
                    docsExactTreePath,
                    docsReleaseManifestSha256,
                    "appsurface-docs-release-manifest-v1",
                    docsFileCount,
                    includeDocsCatalogEntry
                        ? new ReleaseEvidenceCatalogEntry(docsExactTreePath, docsReleaseManifestSha256)
                        : null)
            });
        }

        return ReleaseEvidence.Serialize(evidence);
    }

    private async Task<DocsArchiveFixture> SeedDocsArchiveAsync(
        string versionText,
        string routeManifestJson = """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": []
        }
        """)
    {
        var exactTreePath = $"releases/{versionText}";
        var trustedReleaseRootPath = RepositoryPath("dist/docs");
        var exactTreePhysicalPath = Path.Join(trustedReleaseRootPath, "releases", versionText);
        Directory.CreateDirectory(exactTreePhysicalPath);

        var indexBytes = Encoding.UTF8.GetBytes("<!doctype html><title>AppSurface Docs</title>");
        var indexPath = Path.Join(exactTreePhysicalPath, "index.html");
        await File.WriteAllBytesAsync(indexPath, indexBytes);
        var indexSha256 = Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant();
        var routeManifestBytes = Encoding.UTF8.GetBytes(routeManifestJson);
        var routeManifestPath = Path.Join(exactTreePhysicalPath, ".appsurface-docs-route-manifest.json");
        await File.WriteAllBytesAsync(routeManifestPath, routeManifestBytes);
        var routeManifestSha256 = Convert.ToHexString(SHA256.HashData(routeManifestBytes)).ToLowerInvariant();
        var manifestJson = JsonSerializer.Serialize(
            new
            {
                schema = "appsurface-docs-release-manifest-v1",
                files = new[]
                {
                    new
                    {
                        path = "index.html",
                        length = indexBytes.Length,
                        contentType = "text/html",
                        hashAlgorithm = "sha256",
                        sha256 = indexSha256
                    },
                    new
                    {
                        path = ".appsurface-docs-route-manifest.json",
                        length = routeManifestBytes.Length,
                        contentType = "application/json",
                        hashAlgorithm = "sha256",
                        sha256 = routeManifestSha256
                    }
                }
            },
            ReleaseJson.Options) + Environment.NewLine;
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        await File.WriteAllBytesAsync(Path.Join(exactTreePhysicalPath, ".appsurface-docs-release-manifest.json"), manifestBytes);
        var releaseManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var catalogPath = Path.Join(trustedReleaseRootPath, "versions.json");
        var catalogJson = JsonSerializer.Serialize(
            new
            {
                versions = new[]
                {
                    new
                    {
                        version = versionText,
                        label = versionText,
                        exactTreePath,
                        releaseManifestSha256,
                        visibility = "Public"
                    }
                }
            },
            ReleaseJson.Options) + Environment.NewLine;
        await File.WriteAllTextAsync(catalogPath, catalogJson);
        return new DocsArchiveFixture(
            catalogPath,
            trustedReleaseRootPath,
            exactTreePath,
            releaseManifestSha256,
            FileCount: 2,
            versionText);
    }

    private async Task WriteDocsCatalogAsync(DocsArchiveFixture docs, params object[] entries)
    {
        var versions = entries.Length > 0
            ? entries
            :
            [
                new
                {
                    version = docs.VersionText,
                    label = docs.VersionText,
                    exactTreePath = docs.ExactTreePath,
                    releaseManifestSha256 = docs.ReleaseManifestSha256,
                    visibility = "Public"
                }
            ];
        var catalogJson = JsonSerializer.Serialize(new { versions }, ReleaseJson.Options) + Environment.NewLine;
        await File.WriteAllTextAsync(docs.CatalogPath, catalogJson);
    }

    private async Task<DocsArchiveFixture> RewriteDocsReleaseManifestAsync(
        DocsArchiveFixture docs,
        string schema,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> files)
    {
        var manifestJson = JsonSerializer.Serialize(
            new
            {
                schema,
                files
            },
            ReleaseJson.Options) + Environment.NewLine;
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        await File.WriteAllBytesAsync(
            DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"),
            manifestBytes);
        var updatedDocs = docs with
        {
            ReleaseManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            FileCount = files.Count
        };
        await WriteDocsCatalogAsync(updatedDocs);
        return updatedDocs;
    }

    private async Task<DocsArchiveFixture> RewriteDocsReleaseManifestPayloadAsync(DocsArchiveFixture docs, string payload)
    {
        var manifestBytes = Encoding.UTF8.GetBytes(payload);
        await File.WriteAllBytesAsync(
            DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"),
            manifestBytes);
        var updatedDocs = docs with
        {
            ReleaseManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant()
        };
        await WriteDocsCatalogAsync(updatedDocs);
        return updatedDocs;
    }

    private static IReadOnlyDictionary<string, object?> CreateDocsManifestFile(string path, long length, string sha256)
    {
        return new Dictionary<string, object?>
        {
            ["path"] = path,
            ["length"] = length,
            ["contentType"] = "text/plain",
            ["hashAlgorithm"] = "sha256",
            ["sha256"] = sha256
        };
    }

    private async Task<byte[]> ReadDocsArchiveFileAsync(DocsArchiveFixture docs, string relativePath)
    {
        return await File.ReadAllBytesAsync(DocsArchivePath(docs, relativePath));
    }

    private static string DocsArchivePath(DocsArchiveFixture docs, string relativePath)
    {
        return TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath, relativePath);
    }

    private static ReleaseEvidenceBundle PinDocsArchive(ReleaseEvidenceBundle bundle, DocsArchiveFixture docs)
    {
        return RefreshSubject(bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                docs.ExactTreePath,
                docs.ReleaseManifestSha256,
                "appsurface-docs-release-manifest-v1",
                docs.FileCount,
                new ReleaseEvidenceCatalogEntry(docs.ExactTreePath, docs.ReleaseManifestSha256))
        });
    }

    private static ReleaseEvidenceBundleV2 PinDocsArchive(ReleaseEvidenceBundleV2 bundle, DocsArchiveFixture docs)
    {
        return ReleaseEvidenceV2.RefreshSubject(bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                docs.ExactTreePath,
                docs.ReleaseManifestSha256,
                "appsurface-docs-release-manifest-v1",
                docs.FileCount,
                new ReleaseEvidenceCatalogEntry(docs.ExactTreePath, docs.ReleaseManifestSha256))
        });
    }

    private static ReleaseEvidenceBundle RefreshSubject(ReleaseEvidenceBundle bundle)
    {
        var subjectInput = new ReleaseEvidenceSubjectInput(
            bundle.Schema,
            bundle.Version,
            bundle.Tag,
            bundle.ReleaseClassification,
            bundle.ReleaseNotePath,
            bundle.ReleaseSidecarPath,
            bundle.ReleaseManifestPath,
            bundle.EvidencePath,
            bundle.ReleaseManifestDigest,
            bundle.ReleaseArtifactDigests,
            bundle.PackageReleaseNotePaths,
            bundle.DocsArchive,
            bundle.Commits,
            bundle.GeneratedBy,
            bundle.Attestation);
        var subjectDigest = ReleaseEvidence.ComputeSha256Hex(JsonSerializer.Serialize(subjectInput, ReleaseJson.Options));
        return bundle with
        {
            Subject = bundle.Subject with { Sha256 = subjectDigest }
        };
    }

    private static string CreateReleaseEvidenceJsonWithNull(string releaseManifestJson, params string[] path)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(CreateReleaseEvidenceJson(releaseManifestJson))!.AsObject();
        System.Text.Json.Nodes.JsonNode current = root;
        for (var index = 0; index < path.Length - 1; index++)
        {
            current = int.TryParse(path[index], out var arrayIndex)
                ? current.AsArray()[arrayIndex]!
                : current[path[index]]!;
        }

        current.AsObject()[path[^1]] = null;
        return root.ToJsonString(ReleaseJson.Options) + Environment.NewLine;
    }

    private sealed class DelegatingFileSystemInspector : ReleaseDocsArchiveGate.IFileSystemInspector
    {
        internal Func<DirectoryInfo, bool>? DirectoryExists { get; init; }

        internal Func<DirectoryInfo, FileAttributes>? DirectoryAttributes { get; init; }

        internal Func<DirectoryInfo, FileSystemInfo[]>? FileSystemInfos { get; init; }

        internal Func<FileSystemInfo, FileAttributes>? FileSystemInfoAttributes { get; init; }

        internal Func<FileInfo, bool>? FileExists { get; init; }

        internal Func<FileInfo, FileAttributes>? FileAttributes { get; init; }

        bool ReleaseDocsArchiveGate.IFileSystemInspector.DirectoryExists(DirectoryInfo directory)
        {
            return DirectoryExists?.Invoke(directory) ?? directory.Exists;
        }

        FileAttributes ReleaseDocsArchiveGate.IFileSystemInspector.GetDirectoryAttributes(DirectoryInfo directory)
        {
            return DirectoryAttributes?.Invoke(directory) ?? directory.Attributes;
        }

        FileSystemInfo[] ReleaseDocsArchiveGate.IFileSystemInspector.EnumerateFileSystemInfos(DirectoryInfo directory)
        {
            return FileSystemInfos?.Invoke(directory) ?? directory.EnumerateFileSystemInfos().ToArray();
        }

        FileAttributes ReleaseDocsArchiveGate.IFileSystemInspector.GetFileSystemInfoAttributes(FileSystemInfo entry)
        {
            return FileSystemInfoAttributes?.Invoke(entry) ?? entry.Attributes;
        }

        bool ReleaseDocsArchiveGate.IFileSystemInspector.FileExists(FileInfo file)
        {
            return FileExists?.Invoke(file) ?? file.Exists;
        }

        FileAttributes ReleaseDocsArchiveGate.IFileSystemInspector.GetFileAttributes(FileInfo file)
        {
            return FileAttributes?.Invoke(file) ?? file.Attributes;
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed record DocsArchiveFixture(
        string CatalogPath,
        string TrustedReleaseRootPath,
        string ExactTreePath,
        string ReleaseManifestSha256,
        int FileCount,
        string VersionText);

}
