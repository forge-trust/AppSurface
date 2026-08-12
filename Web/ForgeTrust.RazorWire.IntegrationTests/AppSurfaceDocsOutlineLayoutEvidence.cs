using System.Globalization;
using System.Text.Json;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Evaluates the desktop Docs outline layout contract and, only for a failed contract, persists diagnostic evidence.
/// </summary>
/// <remarks>
/// This support is deliberately local to the Docs Playwright regression. A second browser regression can extract a
/// shared diagnostic component once there is an actual common contract to preserve.
/// </remarks>
internal sealed class AppSurfaceDocsOutlineLayoutEvidence
{
    /// <summary>Gets the environment variable that selects an absolute root for failure evidence.</summary>
    internal const string ArtifactDirectoryEnvironmentVariable = "APP_SURFACE_TEST_ARTIFACTS_DIR";

    /// <summary>Gets the JSON schema version written with each captured layout snapshot.</summary>
    internal const int SchemaVersion = 1;

    private const string ArtifactDirectoryName = "docs-outline-layout";
    private const string SnapshotFileName = "outline-layout.json";
    private const string ScreenshotFileName = "viewport.png";

    /// <summary>Gets the stable failure-evidence trace file name.</summary>
    internal const string TraceFileName = "trace.zip";

    private const string TempDirectoryName = "appsurface-docs-outline-layout-evidence";
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new() { WriteIndented = true };
    private readonly Func<string?> _artifactDirectory;
    private readonly Func<string> _tempDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<Guid> _newGuid;

    /// <summary>
    /// Initializes a failure-evidence writer with test seams for the clock, identifier, and file-root configuration.
    /// </summary>
    /// <param name="artifactDirectory">Reads the optional configured artifact root.</param>
    /// <param name="tempDirectory">Reads the process temp root used when no artifact root is configured.</param>
    /// <param name="utcNow">Gets the timestamp included in an evidence directory name.</param>
    /// <param name="newGuid">Creates the collision-resistant suffix included in an evidence directory name.</param>
    internal AppSurfaceDocsOutlineLayoutEvidence(
        Func<string?>? artifactDirectory = null,
        Func<string>? tempDirectory = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<Guid>? newGuid = null)
    {
        _artifactDirectory = artifactDirectory ?? (() => Environment.GetEnvironmentVariable(ArtifactDirectoryEnvironmentVariable));
        _tempDirectory = tempDirectory ?? Path.GetTempPath;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _newGuid = newGuid ?? Guid.NewGuid;
    }

    /// <summary>Evaluates all user-visible desktop layout invariants from one captured browser snapshot.</summary>
    internal static DocsOutlineLayoutEvaluation Evaluate(DocsOutlineLayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool? toggleVisible = snapshot.ToggleExists
            ? !string.Equals(snapshot.ToggleDisplay, "none", StringComparison.Ordinal)
            : null;
        bool? primaryOverlapsOutline = snapshot.Primary is { Exists: true, Bounds: not null } primary
            && snapshot.Outline is { Exists: true, Bounds: not null } outline
            ? primary.Bounds.Right > outline.Bounds.Left
            : null;
        bool? horizontalOverflow = snapshot.DocumentElementScrollWidth is not null
            ? snapshot.DocumentElementScrollWidth > snapshot.WindowInnerWidth
            : null;

        return new DocsOutlineLayoutEvaluation(
        [
            new DocsOutlineLayoutInvariant(
                "desktop-toggle-visible",
                "false",
                FormatObserved(toggleVisible, snapshot.ToggleExists ? snapshot.ToggleDisplay ?? "missing" : "missing"),
                toggleVisible is false),
            new DocsOutlineLayoutInvariant(
                "primary-overlaps-outline",
                "false",
                FormatObserved(
                    primaryOverlapsOutline,
                    snapshot.Primary is { Exists: true, Bounds: not null } observedPrimary
                        && snapshot.Outline is { Exists: true, Bounds: not null } observedOutline
                        ? $"primary.right={observedPrimary.Bounds.Right.ToString(CultureInfo.InvariantCulture)}, outline.left={observedOutline.Bounds.Left.ToString(CultureInfo.InvariantCulture)}"
                        : "missing primary or outline"),
                primaryOverlapsOutline is false),
            new DocsOutlineLayoutInvariant(
                "horizontal-overflow",
                "false",
                FormatObserved(
                    horizontalOverflow,
                    snapshot.DocumentElementScrollWidth is null
                        ? "missing documentElement.scrollWidth"
                        : $"documentElement.scrollWidth={snapshot.DocumentElementScrollWidth}, window.innerWidth={snapshot.WindowInnerWidth}"),
                horizontalOverflow is false)
        ]);
    }

    /// <summary>
    /// Captures evidence only when a previously evaluated layout contract failed.
    /// </summary>
    /// <remarks>
    /// Keeping this decision at the evidence boundary prevents successful browser runs from creating empty artifact
    /// directories or stopping their trace with an output file.
    /// </remarks>
    internal async Task<DocsOutlineLayoutCapture?> CaptureIfFailedAsync(
        DocsOutlineLayoutEvaluation evaluation,
        DocsOutlineLayoutSnapshot snapshot,
        Func<string, CancellationToken, Task> captureScreenshot,
        Func<string, CancellationToken, Task> stopTracing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (evaluation.Passed)
        {
            return null;
        }

        return await CaptureAsync(snapshot, captureScreenshot, stopTracing, cancellationToken);
    }

    /// <summary>
    /// Captures the snapshot, viewport screenshot, and Playwright trace in that order without masking the original
    /// layout mismatch if an individual diagnostic capture fails.
    /// </summary>
    /// <param name="snapshot">The layout state collected from the rendered page.</param>
    /// <param name="captureScreenshot">Writes a viewport screenshot to the supplied path.</param>
    /// <param name="stopTracing">Stops the active trace and writes it to the supplied path.</param>
    /// <param name="cancellationToken">Cancels JSON persistence and the supplied capture operations.</param>
    /// <returns>The paths and any secondary capture failures for inclusion in the primary assertion message.</returns>
    internal async Task<DocsOutlineLayoutCapture> CaptureAsync(
        DocsOutlineLayoutSnapshot snapshot,
        Func<string, CancellationToken, Task> captureScreenshot,
        Func<string, CancellationToken, Task> stopTracing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(captureScreenshot);
        ArgumentNullException.ThrowIfNull(stopTracing);

        string directory;
        try
        {
            directory = CreateCaptureDirectory(ResolveArtifactRoot(_artifactDirectory(), _tempDirectory()), _utcNow(), _newGuid());
        }
        catch (Exception exception) when (!MustPropagate(exception))
        {
            return DocsOutlineLayoutCapture.WithFailure("evidence-directory", null, exception);
        }

        var capture = new DocsOutlineLayoutCapture(directory);
        var snapshotPath = ResolveArtifactPath(directory, SnapshotFileName);
        await CaptureStageAsync(
            capture,
            "outline-layout.json",
            snapshotPath,
            () => File.WriteAllTextAsync(
                snapshotPath,
                JsonSerializer.Serialize(snapshot, SnapshotJsonOptions),
                cancellationToken));

        var screenshotPath = ResolveArtifactPath(directory, ScreenshotFileName);
        await CaptureStageAsync(
            capture,
            "viewport.png",
            screenshotPath,
            () => captureScreenshot(screenshotPath, cancellationToken));

        var tracePath = ResolveArtifactPath(directory, TraceFileName);
        await CaptureStageAsync(
            capture,
            "trace.zip",
            tracePath,
            () => stopTracing(tracePath, cancellationToken));

        return capture;
    }

    /// <summary>Resolves the configured artifact root or the namespaced system-temp fallback.</summary>
    /// <remarks>
    /// Empty configuration means "use temp". Whitespace-only and relative configured values are rejected instead of
    /// silently emitting artifacts somewhere unexpected.
    /// </remarks>
    internal static string ResolveArtifactRoot(string? configuredRoot, string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);

        if (configuredRoot is null || configuredRoot.Length == 0)
        {
            return Path.GetFullPath(Path.Join(tempDirectory, TempDirectoryName));
        }

        if (string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathFullyQualified(configuredRoot))
        {
            throw new ArgumentException(
                $"{ArtifactDirectoryEnvironmentVariable} must be an absolute, non-whitespace path when configured.",
                nameof(configuredRoot));
        }

        return Path.GetFullPath(configuredRoot);
    }

    /// <summary>Creates a UTC- and GUID-named directory below the dedicated outline-evidence root.</summary>
    internal static string CreateCaptureDirectory(string artifactRoot, DateTimeOffset timestamp, Guid identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);

        var directoryName = string.Create(
            CultureInfo.InvariantCulture,
            $"{timestamp.UtcDateTime:yyyyMMddTHHmmssfffZ}-initial-{identifier:N}");
        var directory = ResolveArtifactPath(artifactRoot, Path.Join(ArtifactDirectoryName, directoryName));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Canonicalizes a relative artifact path and rejects rooted paths or paths outside <paramref name="root"/>,
    /// including sibling-prefix paths such as <c>/tmp/layout-evidence-old</c> for a
    /// <c>/tmp/layout-evidence</c> root.
    /// </summary>
    internal static string ResolveArtifactPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Evidence artifact paths must be relative to the configured root.", nameof(relativePath));
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootWithSeparator, comparison))
        {
            throw new ArgumentException("Evidence artifact paths must remain below the configured root.", nameof(relativePath));
        }

        return candidate;
    }

    internal static string FormatFailureMessage(DocsOutlineLayoutEvaluation evaluation, DocsOutlineLayoutCapture capture)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(capture);

        var failures = evaluation.Failed
            .Select(invariant => $"{invariant.Id} (expected={invariant.Expected}, observed={invariant.Observed})");
        return $"DOCS-OUTLINE-LAYOUT schemaVersion={SchemaVersion}; failed invariants: {string.Join("; ", failures)}. "
            + capture.FormatForFailureMessage();
    }

    private static async Task CaptureStageAsync(
        DocsOutlineLayoutCapture capture,
        string name,
        string path,
        Func<Task> operation)
    {
        try
        {
            await operation();
            capture.RecordSuccess(name, path);
        }
        catch (Exception exception) when (!MustPropagate(exception))
        {
            // Diagnostics must never replace the actionable layout assertion that triggered this capture.
            capture.RecordFailure(name, path, exception);
        }
    }

    private static string FormatObserved(bool? value, string detail) => value is null
        ? detail
        : $"{value.Value.ToString().ToLowerInvariant()} ({detail})";

    private static bool MustPropagate(Exception exception) => exception is
        OperationCanceledException
        or OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;
}

/// <summary>
/// Describes the desktop layout state sampled from a single rendered Docs page.
/// </summary>
/// <remarks>
/// Playwright materializes evaluated objects with a parameterless constructor and writable properties, so this shape
/// intentionally avoids a positional record even though it is immutable after test setup.
/// </remarks>
internal sealed class DocsOutlineLayoutSnapshot
{
    public int SchemaVersion { get; init; }

    public double WindowInnerWidth { get; init; }

    public double WindowInnerHeight { get; init; }

    public double? BodyScrollWidth { get; init; }

    public double? BodyClientWidth { get; init; }

    public double? DocumentElementScrollWidth { get; init; }

    public double? DocumentElementClientWidth { get; init; }

    public double? VisualViewportWidth { get; init; }

    public double? VisualViewportHeight { get; init; }

    public bool OutlineExists { get; init; }

    public bool PrimaryExists { get; init; }

    public bool ToggleExists { get; init; }

    public string? OutlineEnhanced { get; init; }

    public string? ToggleAriaExpanded { get; init; }

    public string? ToggleDisplay { get; init; }

    public DocsOutlineLayoutElement? Primary { get; init; }

    public DocsOutlineLayoutElement? Outline { get; init; }
}

/// <summary>Captures a layout element's existence, bounding box, and computed CSS diagnostics.</summary>
internal sealed class DocsOutlineLayoutElement
{
    public bool Exists { get; init; }

    public DocsOutlineLayoutBounds? Bounds { get; init; }

    public string? Display { get; init; }

    public string? Position { get; init; }

    public string? GridColumn { get; init; }

    public string? Width { get; init; }

    public string? MinWidth { get; init; }

    public string? MaxWidth { get; init; }

    public string? OverflowX { get; init; }
}

/// <summary>Stores a browser element's viewport-relative bounding rectangle.</summary>
internal sealed class DocsOutlineLayoutBounds
{
    public double Left { get; init; }

    public double Top { get; init; }

    public double Right { get; init; }

    public double Bottom { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }
}

/// <summary>Reports the individual desktop layout contracts evaluated from one snapshot.</summary>
internal sealed record DocsOutlineLayoutEvaluation(IReadOnlyList<DocsOutlineLayoutInvariant> Invariants)
{
    /// <summary>Gets the failed contracts in stable diagnostic order.</summary>
    internal IReadOnlyList<DocsOutlineLayoutInvariant> Failed => Invariants.Where(invariant => !invariant.Passed).ToArray();

    /// <summary>Gets whether every desktop layout contract passed.</summary>
    internal bool Passed => Failed.Count == 0;
}

/// <summary>Describes one semantic desktop layout contract, including its expected and observed values.</summary>
internal sealed record DocsOutlineLayoutInvariant(string Id, string Expected, string Observed, bool Passed);

/// <summary>Records the failure-only evidence paths and non-fatal capture outcomes.</summary>
internal sealed class DocsOutlineLayoutCapture
{
    private readonly List<DocsOutlineLayoutCaptureStage> _stages = [];

    internal DocsOutlineLayoutCapture(string? directory)
    {
        Directory = directory;
    }

    /// <summary>Gets the directory that contains evidence, when setup succeeded.</summary>
    internal string? Directory { get; }

    /// <summary>Gets capture stages in the attempted JSON, screenshot, trace order.</summary>
    internal IReadOnlyList<DocsOutlineLayoutCaptureStage> Stages => _stages;

    /// <summary>
    /// Gets whether the failure-only capture attempted to stop the active Playwright trace, whether or not that
    /// individual trace write succeeded.
    /// </summary>
    internal bool TraceStopAttempted => Stages.Any(stage =>
        string.Equals(stage.Name, AppSurfaceDocsOutlineLayoutEvidence.TraceFileName, StringComparison.Ordinal));

    internal static DocsOutlineLayoutCapture WithFailure(string name, string? path, Exception exception)
    {
        var capture = new DocsOutlineLayoutCapture(null);
        capture.RecordFailure(name, path, exception);
        return capture;
    }

    internal void RecordSuccess(string name, string path) => _stages.Add(new DocsOutlineLayoutCaptureStage(name, path, null));

    internal void RecordFailure(string name, string? path, Exception exception) =>
        _stages.Add(new DocsOutlineLayoutCaptureStage(name, path, exception.Message));

    internal string FormatForFailureMessage()
    {
        var stages = Stages.Select(stage => stage.Error is null
            ? $"{stage.Name}={stage.Path}"
            : $"{stage.Name} capture failed ({stage.Error})");
        return $"Evidence directory: {Directory ?? "unavailable"}. Capture outcomes: {string.Join("; ", stages)}";
    }
}

/// <summary>Describes a single attempted failure-evidence capture.</summary>
internal sealed record DocsOutlineLayoutCaptureStage(string Name, string? Path, string? Error);
