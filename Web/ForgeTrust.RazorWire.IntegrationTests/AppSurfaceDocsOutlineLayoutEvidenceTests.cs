using System.Text.Json;

namespace ForgeTrust.RazorWire.IntegrationTests;

public sealed class AppSurfaceDocsOutlineLayoutEvidenceTests
{
    [Fact]
    public void Evaluate_AcceptsTheFullDesktopLayoutContract()
    {
        var evaluation = AppSurfaceDocsOutlineLayoutEvidence.Evaluate(CreateSnapshot());

        Assert.True(evaluation.Passed);
        Assert.Empty(evaluation.Failed);
    }

    [Fact]
    public void Evaluate_ReportsEveryFailedDesktopInvariant_WithExpectedAndObservedValues()
    {
        var evaluation = AppSurfaceDocsOutlineLayoutEvidence.Evaluate(CreateSnapshot(
            toggleDisplay: "block",
            documentElementScrollWidth: 1400,
            primary: Element(left: 0, right: 900),
            outline: Element(left: 850, right: 1200)));

        var failed = evaluation.Failed;

        Assert.False(evaluation.Passed);
        Assert.Equal(
            ["desktop-toggle-visible", "primary-overlaps-outline", "horizontal-overflow"],
            failed.Select(invariant => invariant.Id));
        Assert.All(failed, invariant =>
        {
            Assert.Equal("false", invariant.Expected);
            Assert.NotEmpty(invariant.Observed);
        });
    }

    [Fact]
    public void Evaluate_ReportsMissingOrDetachedLayoutNodes_AsFailedContracts()
    {
        var evaluation = AppSurfaceDocsOutlineLayoutEvidence.Evaluate(CreateSnapshot(
            toggleDisplay: null,
            documentElementScrollWidth: null,
            primary: null,
            outline: new DocsOutlineLayoutElement { Exists = false },
            primaryExists: false,
            outlineExists: false));

        var failed = evaluation.Failed;

        Assert.Equal(3, failed.Count);
        Assert.Contains(failed, invariant => invariant.Id == "desktop-toggle-visible" && invariant.Observed == "missing");
        Assert.Contains(failed, invariant => invariant.Id == "primary-overlaps-outline" && invariant.Observed == "missing primary or outline");
        Assert.Contains(failed, invariant => invariant.Id == "horizontal-overflow" && invariant.Observed == "missing documentElement.scrollWidth");
    }

    [Fact]
    public void ResolveArtifactRoot_UsesNamespacedTempFallback_WhenConfigurationIsUnset()
    {
        var temp = Path.Join(Path.GetTempPath(), "docs-outline-layout-tests");

        var root = AppSurfaceDocsOutlineLayoutEvidence.ResolveArtifactRoot(null, temp);

        Assert.Equal(
            Path.GetFullPath(Path.Join(temp, "appsurface-docs-outline-layout-evidence")),
            root);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("relative/evidence")]
    public void ResolveArtifactRoot_RejectsWhitespaceAndRelativeConfiguredRoots(string configuredRoot)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AppSurfaceDocsOutlineLayoutEvidence.ResolveArtifactRoot(configuredRoot, Path.GetTempPath()));

        Assert.Contains(AppSurfaceDocsOutlineLayoutEvidence.ArtifactDirectoryEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveArtifactPath_RejectsRootedTraversalAndSiblingPrefixPaths()
    {
        var root = Path.Join(Path.GetTempPath(), "docs-outline-layout-tests", "evidence");
        var sibling = root + "-sibling";

        Assert.Throws<ArgumentException>(() =>
            AppSurfaceDocsOutlineLayoutEvidence.ResolveArtifactPath(root, Path.Join("..", "outside", "trace.zip")));
        var exception = Assert.Throws<ArgumentException>(() =>
            AppSurfaceDocsOutlineLayoutEvidence.ResolveArtifactPath(root, Path.Join(sibling, "trace.zip")));

        Assert.Equal("relativePath", exception.ParamName);
        Assert.Contains("relative", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() =>
            AppSurfaceDocsOutlineLayoutEvidence.ResolveArtifactPath(
                root,
                Path.Join("..", Path.GetFileName(sibling), "trace.zip")));
    }

    [Fact]
    public void CreateCaptureDirectory_UsesInvariantUtcTimestampAndGuidName()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var timestamp = new DateTimeOffset(2026, 8, 11, 12, 34, 56, 789, TimeSpan.Zero);
            var identifier = Guid.Parse("eb6ac2c6-9d6e-4bbd-a3cb-7b8c75a087b3");

            var directory = AppSurfaceDocsOutlineLayoutEvidence.CreateCaptureDirectory(root, timestamp, identifier);

            Assert.True(Directory.Exists(directory));
            Assert.Equal(
                "20260811T123456789Z-initial-eb6ac2c69d6e4bbda3cb7b8c75a087b3",
                Path.GetFileName(directory));
            Assert.True(IsNestedPath(root, directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureIfFailedAsync_DoesNotCreateEvidenceOrStopTrace_WhenInvariantsPass()
    {
        var root = Path.Join(CreateTemporaryDirectory(), "evidence");
        try
        {
            var writer = new AppSurfaceDocsOutlineLayoutEvidence(
                artifactDirectory: () => root,
                utcNow: () => new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
                newGuid: () => Guid.Parse("b62b9780-91c6-43b3-9ab6-4a21e98f49ca"));
            var screenshotCalls = 0;
            var traceStops = 0;
            var snapshot = CreateSnapshot();

            var capture = await writer.CaptureIfFailedAsync(
                AppSurfaceDocsOutlineLayoutEvidence.Evaluate(snapshot),
                snapshot,
                (_, _) =>
                {
                    screenshotCalls++;
                    return Task.CompletedTask;
                },
                (_, _) =>
                {
                    traceStops++;
                    return Task.CompletedTask;
                });

            Assert.Null(capture);
            Assert.Equal(0, screenshotCalls);
            Assert.Equal(0, traceStops);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureAsync_WritesTraceExactlyOnce_AfterSnapshotAndScreenshot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var writer = new AppSurfaceDocsOutlineLayoutEvidence(
                artifactDirectory: () => root,
                utcNow: () => new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
                newGuid: () => Guid.Parse("3c73170e-a7ff-46d7-9361-43f57ee353d8"));
            var order = new List<string>();

            var capture = await writer.CaptureAsync(
                CreateSnapshot(toggleDisplay: "block"),
                async (path, _) =>
                {
                    order.Add("screenshot");
                    await File.WriteAllTextAsync(path, "screenshot");
                },
                async (path, _) =>
                {
                    order.Add("trace");
                    await File.WriteAllTextAsync(path, "trace");
                });

            Assert.Equal(["screenshot", "trace"], order);
            Assert.Equal(["outline-layout.json", "viewport.png", "trace.zip"], capture.Stages.Select(stage => stage.Name));
            Assert.All(capture.Stages, stage => Assert.Null(stage.Error));
            Assert.True(capture.TraceStopAttempted);
            var snapshotPath = Path.Join(capture.Directory!, "outline-layout.json");
            Assert.True(File.Exists(snapshotPath));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath));
            Assert.Equal(AppSurfaceDocsOutlineLayoutEvidence.SchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(
                [
                    "BodyClientWidth",
                    "BodyScrollWidth",
                    "DocumentElementClientWidth",
                    "DocumentElementScrollWidth",
                    "Outline",
                    "OutlineEnhanced",
                    "OutlineExists",
                    "Primary",
                    "PrimaryExists",
                    "SchemaVersion",
                    "ToggleAriaExpanded",
                    "ToggleDisplay",
                    "ToggleExists",
                    "VisualViewportHeight",
                    "VisualViewportWidth",
                    "WindowInnerHeight",
                    "WindowInnerWidth"
                ],
                document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
            Assert.Equal(
                new[] { "Bounds", "Display", "GridColumn", "MaxWidth", "MinWidth", "OverflowX", "Position", "Width", "Exists" }.OrderBy(name => name),
                document.RootElement.GetProperty("Primary").EnumerateObject().Select(property => property.Name).OrderBy(name => name));
            Assert.Equal(
                new[] { "Bottom", "Height", "Left", "Right", "Top", "Width" }.OrderBy(name => name),
                document.RootElement.GetProperty("Primary").GetProperty("Bounds").EnumerateObject().Select(property => property.Name).OrderBy(name => name));
            Assert.True(File.Exists(Path.Join(capture.Directory!, "viewport.png")));
            Assert.True(File.Exists(Path.Join(capture.Directory!, "trace.zip")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureAsync_PreservesSnapshotAndPrimaryFailure_WhenScreenshotAndTraceFail()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var writer = new AppSurfaceDocsOutlineLayoutEvidence(
                artifactDirectory: () => root,
                utcNow: () => new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
                newGuid: () => Guid.Parse("48d555c4-b7ec-4a5c-97c2-da613b845970"));
            var snapshot = CreateSnapshot(toggleDisplay: "block");
            var evaluation = AppSurfaceDocsOutlineLayoutEvidence.Evaluate(snapshot);

            var capture = await writer.CaptureAsync(
                snapshot,
                (_, _) => Task.FromException(new IOException("screenshot unavailable")),
                (_, _) => Task.FromException(new IOException("trace unavailable")));
            var message = AppSurfaceDocsOutlineLayoutEvidence.FormatFailureMessage(evaluation, capture);

            Assert.True(File.Exists(Path.Join(capture.Directory!, "outline-layout.json")));
            Assert.Equal("outline-layout.json", capture.Stages[0].Name);
            Assert.Null(capture.Stages[0].Error);
            Assert.Equal("viewport.png", capture.Stages[1].Name);
            Assert.Equal("screenshot unavailable", capture.Stages[1].Error);
            Assert.Equal("trace.zip", capture.Stages[2].Name);
            Assert.Equal("trace unavailable", capture.Stages[2].Error);
            Assert.True(capture.TraceStopAttempted);
            Assert.StartsWith("DOCS-OUTLINE-LAYOUT schemaVersion=1;", message, StringComparison.Ordinal);
            Assert.Contains("desktop-toggle-visible (expected=false, observed=true", message, StringComparison.Ordinal);
            Assert.Contains("viewport.png capture failed (screenshot unavailable)", message, StringComparison.Ordinal);
            Assert.Contains("trace.zip capture failed (trace unavailable)", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureAsync_PropagatesCancellationAndCriticalRuntimeFailures()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var writer = new AppSurfaceDocsOutlineLayoutEvidence(artifactDirectory: () => root);
            var screenshotCalls = 0;
            var traceStops = 0;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                writer.CaptureAsync(
                    CreateSnapshot(toggleDisplay: "block"),
                    (_, _) =>
                    {
                        screenshotCalls++;
                        return Task.CompletedTask;
                    },
                    (_, _) =>
                    {
                        traceStops++;
                        return Task.CompletedTask;
                    },
                    new CancellationToken(canceled: true)));
            Assert.Equal(0, screenshotCalls);
            Assert.Equal(0, traceStops);

            await Assert.ThrowsAsync<OutOfMemoryException>(() =>
                writer.CaptureAsync(
                    CreateSnapshot(toggleDisplay: "block"),
                    (_, _) => Task.FromException(new OutOfMemoryException("critical runtime failure")),
                    (_, _) =>
                    {
                        traceStops++;
                        return Task.CompletedTask;
                    }));
            Assert.Equal(0, traceStops);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureAsync_ReportsWhenEvidenceSetupPreventsTraceStop()
    {
        var writer = new AppSurfaceDocsOutlineLayoutEvidence(artifactDirectory: () => "relative/evidence");
        var screenshotCalls = 0;
        var traceStops = 0;

        var capture = await writer.CaptureAsync(
            CreateSnapshot(toggleDisplay: "block"),
            (_, _) =>
            {
                screenshotCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                traceStops++;
                return Task.CompletedTask;
            });

        Assert.Null(capture.Directory);
        Assert.False(capture.TraceStopAttempted);
        Assert.Equal(0, screenshotCalls);
        Assert.Equal(0, traceStops);
        var stage = Assert.Single(capture.Stages);
        Assert.Equal("evidence-directory", stage.Name);
        Assert.NotNull(stage.Error);
    }

    private static DocsOutlineLayoutSnapshot CreateSnapshot(
        string? toggleDisplay = "none",
        double? documentElementScrollWidth = 1366,
        DocsOutlineLayoutElement? primary = null,
        DocsOutlineLayoutElement? outline = null,
        bool primaryExists = true,
        bool outlineExists = true)
    {
        var resolvedPrimary = primary ?? (primaryExists ? Element(left: 0, right: 900) : null);
        var resolvedOutline = outline ?? (outlineExists ? Element(left: 930, right: 1200) : null);
        return new DocsOutlineLayoutSnapshot
        {
            SchemaVersion = AppSurfaceDocsOutlineLayoutEvidence.SchemaVersion,
            WindowInnerWidth = 1366,
            WindowInnerHeight = 900,
            BodyScrollWidth = 1366,
            BodyClientWidth = 1366,
            DocumentElementScrollWidth = documentElementScrollWidth,
            DocumentElementClientWidth = 1366,
            VisualViewportWidth = 1366,
            VisualViewportHeight = 900,
            OutlineExists = resolvedOutline?.Exists ?? false,
            PrimaryExists = resolvedPrimary?.Exists ?? false,
            ToggleExists = toggleDisplay is not null,
            OutlineEnhanced = "true",
            ToggleAriaExpanded = "false",
            ToggleDisplay = toggleDisplay,
            Primary = resolvedPrimary,
            Outline = resolvedOutline
        };
    }

    private static DocsOutlineLayoutElement Element(double left, double right) =>
        new()
        {
            Exists = true,
            Bounds = new DocsOutlineLayoutBounds
            {
                Left = left,
                Top = 0,
                Right = right,
                Bottom = 600,
                Width = right - left,
                Height = 600
            },
            Display = "block",
            Position = "static",
            GridColumn = "auto",
            Width = $"{right - left}px",
            MinWidth = "0px",
            MaxWidth = "none",
            OverflowX = "visible"
        };

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Join(Path.GetTempPath(), "docs-outline-layout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsNestedPath(string root, string candidate)
    {
        var rootWithSeparator = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(candidate).StartsWith(rootWithSeparator, comparison);
    }
}
