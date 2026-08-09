namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class ReleaseGuidanceRendererTests : IDisposable
{
    private readonly string _repositoryRoot = Path.Combine(Path.GetTempPath(), "ReleaseGuidanceRendererTests", Guid.NewGuid().ToString("N"));

    public ReleaseGuidanceRendererTests()
    {
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public void TemplateRelativePath_ShouldUseNonMarkdownExtension()
    {
        Assert.EndsWith(".template", ReleaseGuidanceRenderer.TemplateRelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdatesAsync_RendersFiniteVariantWithCanonicalAbsoluteUrls()
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            """
            # Example

            <!-- appsurface-release-guidance: begin -->
            ## Release Guidance

            stale
            <!-- appsurface-release-guidance: end -->
            """);

        var updates = await new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]);

        var update = Assert.Single(updates);
        Assert.Equal("default", update.Variant);
        Assert.Contains(ReleaseGuidanceRenderer.PackageChooserUrl, update.ExpectedContent, StringComparison.Ordinal);
        Assert.Contains(ReleaseGuidanceRenderer.ReleaseHubUrl, update.ExpectedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("../../packages/README.md", update.ExpectedContent, StringComparison.Ordinal);
        Assert.Contains(ReleaseGuidanceRenderer.BeginMarker, update.ExpectedContent, StringComparison.Ordinal);
        Assert.Contains(ReleaseGuidanceRenderer.EndMarker, update.ExpectedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdatesAsync_MigratesOneLegacyHeadingAndPreservesFollowingAuthoredSection()
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            """
            # Example

            ## Release Guidance

            old policy

            ## Usage

            Keep this authored guidance byte-for-byte.

            ---

            [Back to Example](../README.md)
            """);

        var updates = await new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("apphost")]);

        var update = Assert.Single(updates);
        Assert.Contains("This AppHost-oriented package", update.ExpectedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("old policy", update.ExpectedContent, StringComparison.Ordinal);
        Assert.Contains("## Usage\n\nKeep this authored guidance byte-for-byte.", update.ExpectedContent, StringComparison.Ordinal);
        Assert.Contains("---\n\n[Back to Example](../README.md)", update.ExpectedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdatesAsync_MigratesLegacySectionPastMarkdownFenceContent()
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            """
            # Example

            ## Release Guidance

            old policy

            ```yaml
            heading: not a section
            ---
            ```

            ## Usage

            Keep this authored guidance.
            """);

        var updates = await new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]);

        var update = Assert.Single(updates);
        Assert.DoesNotContain("heading: not a section", update.ExpectedContent, StringComparison.Ordinal);
        Assert.Contains("## Usage\n\nKeep this authored guidance.", update.ExpectedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdatesAsync_PreservesCrLfForManagedRegion()
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            "# Example\r\n\r\n<!-- appsurface-release-guidance: begin -->\r\n## Release Guidance\r\n\r\nstale\r\n<!-- appsurface-release-guidance: end -->\r\n");

        var updates = await new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("experimental")]);

        var update = Assert.Single(updates);
        Assert.DoesNotContain("\n", update.ExpectedContent.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("custom")]
    public async Task CreateUpdatesAsync_RejectsMissingBlankAndUnknownVariants(string? variant)
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            """
            <!-- appsurface-release-guidance: begin -->
            ## Release Guidance
            <!-- appsurface-release-guidance: end -->
            """);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry(variant)]));

        Assert.Contains("release_guidance_variant", error.Message, StringComparison.Ordinal);
        Assert.Contains("Docs: tools/ForgeTrust.AppSurface.PackageIndex/README.md#release-guidance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdatesAsync_RejectsMissingOrDuplicatedMarkers()
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            """
            <!-- appsurface-release-guidance: begin -->
            ## Release Guidance
            <!-- appsurface-release-guidance: begin -->
            """);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]));

        Assert.Contains("malformed release-guidance markers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdatesAsync_RejectsReadmePathThroughSymbolicLink()
    {
        await WriteTemplateAsync();
        var externalDirectory = Path.Combine(Path.GetTempPath(), "ReleaseGuidanceRendererTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDirectory);
        try
        {
            await File.WriteAllTextAsync(
                TestPathUtils.PathUnder(externalDirectory, "README.md"),
                "# Example\n\n## Release Guidance\n\nstale\n");
            var linkedDirectory = TestPathUtils.PathUnder(_repositoryRoot, "Example");
            if (!TryCreateDirectorySymlink(linkedDirectory, externalDirectory))
            {
                return;
            }

            var error = await Assert.ThrowsAsync<PackageIndexException>(
                () => new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]));

            Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(externalDirectory))
            {
                Directory.Delete(externalDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreateUpdatesAsync_RejectsEscapingRootedAndMissingReadmePathsWithoutChangingOutsideFile()
    {
        await WriteTemplateAsync();
        var parentDirectory = Directory.GetParent(_repositoryRoot)!.FullName;
        var outsideDirectoryName = "ReleaseGuidanceRendererOutside" + Guid.NewGuid().ToString("N");
        var outsideDirectory = TestPathUtils.PathUnder(parentDirectory, outsideDirectoryName);
        Directory.CreateDirectory(outsideDirectory);
        var outsideReadmePath = TestPathUtils.PathUnder(outsideDirectory, "README.md");
        const string outsideContent = "# Outside\n\n## Release Guidance\n\nDo not change.\n";
        await File.WriteAllTextAsync(outsideReadmePath, outsideContent);
        try
        {
            var outsidePaths = new[]
            {
                $"../{outsideDirectoryName}/README.md",
                outsideReadmePath,
                "Missing/README.md"
            };

            foreach (var startHerePath in outsidePaths)
            {
                var error = await Assert.ThrowsAsync<PackageIndexException>(
                    () => new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default", startHerePath)]));

                Assert.Contains("unavailable", error.Message, StringComparison.Ordinal);
            }

            Assert.Equal(outsideContent, await File.ReadAllTextAsync(outsideReadmePath));
        }
        finally
        {
            if (Directory.Exists(outsideDirectory))
            {
                Directory.Delete(outsideDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task CreateUpdatesAsync_RejectsInvalidTemplateUrlTokens(string mutation)
    {
        await WriteTemplateAsync();
        var templatePath = TestPathUtils.PathUnder(_repositoryRoot, ReleaseGuidanceRenderer.TemplateRelativePath);
        var template = await File.ReadAllTextAsync(templatePath);
        var invalidTemplate = mutation switch
        {
            "missing" => template.Replace(" and [hub]({{ReleaseHubUrl}}).", ".", StringComparison.Ordinal),
            "duplicate" => template.Replace(
                "<!-- appsurface-release-guidance-template: default end -->",
                "[again]({{ReleaseHubUrl}})\n<!-- appsurface-release-guidance-template: default end -->",
                StringComparison.Ordinal),
            "unknown" => template.Replace(
                "<!-- appsurface-release-guidance-template: default end -->",
                "[unknown]({{UnknownUrl}})\n<!-- appsurface-release-guidance-template: default end -->",
                StringComparison.Ordinal),
            _ => throw new Xunit.Sdk.XunitException($"Unknown template mutation '{mutation}'.")
        };
        await File.WriteAllTextAsync(templatePath, invalidTemplate);
        await WriteFileAsync(
            "Example/README.md",
            "# Example\n\n<!-- appsurface-release-guidance: begin -->\n## Release Guidance\n\nstale\n<!-- appsurface-release-guidance: end -->\n");

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]));

        Assert.Contains("invalid URL tokens", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUpdatesAsync_RejectsMalformedTemplateMarkerPair()
    {
        await WriteTemplateAsync();
        var templatePath = TestPathUtils.PathUnder(_repositoryRoot, ReleaseGuidanceRenderer.TemplateRelativePath);
        var template = await File.ReadAllTextAsync(templatePath);
        await File.WriteAllTextAsync(
            templatePath,
            template.Replace(
                "<!-- appsurface-release-guidance-template: default end -->",
                "<!-- appsurface-release-guidance-template: default begin -->",
                StringComparison.Ordinal));
        await WriteFileAsync(
            "Example/README.md",
            "# Example\n\n<!-- appsurface-release-guidance: begin -->\n## Release Guidance\n\nstale\n<!-- appsurface-release-guidance: end -->\n");

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ReleaseGuidanceRenderer().CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]));

        Assert.Contains("template variant 'default' is malformed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyUpdatesAsync_WritesOnlyStaleManagedReadmesAndVerifyReportsDrift()
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            """
            <!-- appsurface-release-guidance: begin -->
            ## Release Guidance

            stale
            <!-- appsurface-release-guidance: end -->
            """);

        var renderer = new ReleaseGuidanceRenderer();
        var updates = await renderer.CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]);
        var staleError = CaptureVerificationFailure(updates);
        Assert.Contains("README release guidance is stale", staleError.Message, StringComparison.Ordinal);

        var changed = await renderer.ApplyUpdatesAsync(updates);
        var verifiedUpdates = await renderer.CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]);

        Assert.Equal(1, changed);
        ReleaseGuidanceRenderer.VerifyUpdates(verifiedUpdates);
        Assert.Equal(0, await renderer.ApplyUpdatesAsync(verifiedUpdates));
    }

    [Fact]
    public async Task ApplyUpdatesAsync_CancellationLeavesReadmeAndRecoveryArtifactsUntouched()
    {
        await WriteTemplateAsync();
        const string originalContent = "<!-- appsurface-release-guidance: begin -->\n## Release Guidance\n\nstale\n<!-- appsurface-release-guidance: end -->\n";
        await WriteFileAsync("Example/README.md", originalContent);
        var renderer = new ReleaseGuidanceRenderer();
        var updates = await renderer.CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => renderer.ApplyUpdatesAsync(updates, cancellationSource.Token));

        Assert.Contains("reconciliation failed", error.Message, StringComparison.Ordinal);
        Assert.Equal(originalContent, await File.ReadAllTextAsync(TestPathUtils.PathUnder(_repositoryRoot, "Example", "README.md")));
        Assert.Empty(Directory.GetFiles(TestPathUtils.PathUnder(_repositoryRoot, "Example"), ".*.release-guidance.*"));
    }

    [Fact]
    public async Task ApplyUpdatesAsync_RollsBackGeneratedDocumentWhenManagedReadmeChangedAfterRendering()
    {
        await WriteTemplateAsync();
        await WriteFileAsync(
            "Example/README.md",
            "<!-- appsurface-release-guidance: begin -->\n## Release Guidance\n\nstale\n<!-- appsurface-release-guidance: end -->\n");
        var renderer = new ReleaseGuidanceRenderer();
        var guidanceUpdate = Assert.Single(await renderer.CreateUpdatesAsync(_repositoryRoot, [CreateEntry("default")]));
        const string concurrentContent = "# Concurrent edit\n";
        await File.WriteAllTextAsync(guidanceUpdate.FullPath, concurrentContent);
        var generatedDocumentPath = TestPathUtils.PathUnder(_repositoryRoot, "Generated", "packages.md");
        var generatedDocumentUpdate = new ReleaseGuidanceUpdate(
            RepositoryRoot: null,
            FullPath: generatedDocumentPath,
            DisplayPath: "Generated/packages.md",
            CurrentContent: string.Empty,
            ExpectedContent: "# Generated package index\n",
            Variant: "package chooser",
            TargetExisted: false);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => renderer.ApplyUpdatesAsync([generatedDocumentUpdate, guidanceUpdate]));

        Assert.Contains("changed after generation started", error.Message, StringComparison.Ordinal);
        Assert.Equal(concurrentContent, await File.ReadAllTextAsync(guidanceUpdate.FullPath));
        Assert.False(File.Exists(generatedDocumentPath));
        Assert.Empty(Directory.GetFiles(TestPathUtils.PathUnder(_repositoryRoot, "Generated"), ".*.release-guidance.*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    private static ResolvedPackageEntry CreateEntry(string? variant, string startHerePath = "Example/README.md")
    {
        var manifest = new PackageManifestEntry
        {
            Project = "Example/Example.csproj",
            StartHerePath = startHerePath,
            ReleaseGuidanceVariant = variant
        };
        var metadata = new PackageProjectMetadata(manifest.Project, "Example", "net10.0", true, false, "Library", []);
        return new ResolvedPackageEntry(manifest, metadata);
    }

    private static PackageIndexException CaptureVerificationFailure(IReadOnlyList<ReleaseGuidanceUpdate> updates)
    {
        try
        {
            ReleaseGuidanceRenderer.VerifyUpdates(updates);
        }
        catch (PackageIndexException error)
        {
            return error;
        }

        throw new Xunit.Sdk.XunitException("Expected release-guidance verification to fail for stale content.");
    }

    private async Task WriteTemplateAsync()
    {
        await WriteFileAsync(
            ReleaseGuidanceRenderer.TemplateRelativePath,
            """
            <!-- appsurface-release-guidance-template: default begin -->
            ## Release Guidance

            Default [chooser]({{PackageChooserUrl}}) and [hub]({{ReleaseHubUrl}}).
            <!-- appsurface-release-guidance-template: default end -->

            <!-- appsurface-release-guidance-template: apphost begin -->
            ## Release Guidance

            This AppHost-oriented package uses [chooser]({{PackageChooserUrl}}) and [hub]({{ReleaseHubUrl}}).
            <!-- appsurface-release-guidance-template: apphost end -->

            <!-- appsurface-release-guidance-template: experimental begin -->
            ## Release Guidance

            Experimental [chooser]({{PackageChooserUrl}}) and [hub]({{ReleaseHubUrl}}).
            <!-- appsurface-release-guidance-template: experimental end -->
            """);
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var path = TestPathUtils.PathUnder(_repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
