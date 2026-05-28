using System.Globalization;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Shared CliFx option surface and diagnostic handling for release commands.
/// </summary>
internal abstract class ReleaseCommandBase
{
    private readonly ReleaseExecutionContext _executionContext;
    private readonly ICommandRunner _commandRunner;
    private readonly IReleaseClock _clock;

    /// <summary>
    /// Creates a release command.
    /// </summary>
    /// <param name="executionContext">Execution context supplied by the entry point.</param>
    /// <param name="commandRunner">Runner used for Git and GitHub CLI calls.</param>
    /// <param name="clock">Clock used by prepare defaults.</param>
    protected ReleaseCommandBase(ReleaseExecutionContext executionContext, ICommandRunner commandRunner, IReleaseClock clock)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(clock);

        _executionContext = executionContext;
        _commandRunner = commandRunner;
        _clock = clock;
    }

    /// <summary>
    /// Gets the SemVer 2.0 release version without a leading <c>v</c>.
    /// </summary>
    [CommandOption("version", Description = "SemVer 2.0 release version without a leading v.")]
    public string? VersionText { get; set; }

    /// <summary>
    /// Gets the release date for prepare, formatted as <c>YYYY-MM-DD</c>.
    /// </summary>
    [CommandOption("date", Description = "Release date for prepare. Defaults to today's UTC date.")]
    public string? DateText { get; set; }

    /// <summary>
    /// Gets a value indicating whether the command should avoid repository mutations or publishing.
    /// </summary>
    [CommandOption("dry-run", Description = "Validate and print the report without mutating repository files or publishing.")]
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets the repository root. Defaults to the current directory.
    /// </summary>
    [CommandOption("repo-root", Description = "Repository root. Defaults to the current directory.")]
    public string? RepositoryRoot { get; set; }

    /// <summary>
    /// Gets an optional readiness report output path.
    /// </summary>
    [CommandOption("report", Description = "Optional readiness report path.")]
    public string? ReportPath { get; set; }

    /// <summary>
    /// Runs command logic with release diagnostic rendering.
    /// </summary>
    /// <param name="console">CliFx console.</param>
    /// <param name="executeAsync">Command implementation.</param>
    /// <returns>A task that completes after command execution.</returns>
    protected async ValueTask ExecuteWithDiagnosticsAsync(IConsole console, Func<ReleaseOptions, CancellationToken, Task<int>> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(executeAsync);

        var cancellationToken = console.RegisterCancellationHandler();
        try
        {
            var options = BuildOptions();
            Environment.ExitCode = await executeAsync(options, cancellationToken);
        }
        catch (ReleaseToolException ex)
        {
            console.Error.WriteLine(ex.Diagnostic.Render());
            Environment.ExitCode = 1;
        }
        catch (IOException ex)
        {
            console.Error.WriteLine(ReleaseDiagnostic.Error(
                "release-io-failure",
                "Release automation could not read or write a required file.",
                ex.Message,
                "Check the path, permissions, and whether another process is holding the file open.",
                "tools/ForgeTrust.AppSurface.Release/README.md#diagnostics").Render());
            Environment.ExitCode = 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            console.Error.WriteLine(ReleaseDiagnostic.Error(
                "release-path-permission-denied",
                "Release automation was denied access to a required path.",
                ex.Message,
                "Run from the repository root and make sure the release files are writable.",
                "tools/ForgeTrust.AppSurface.Release/README.md#diagnostics").Render());
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Creates service objects for the resolved repository root.
    /// </summary>
    /// <param name="options">Resolved command options.</param>
    /// <returns>Workspace, checker, preparation, and publishing services.</returns>
    protected ReleaseServices CreateServices(ReleaseOptions options)
    {
        var workspace = new ReleaseWorkspace(options.RepositoryRoot);
        var checker = new ReleaseChecker(workspace, _commandRunner);
        var preparation = new ReleasePreparation(workspace, checker, _clock);
        var publishing = new ReleasePublishing(workspace, _commandRunner);
        return new ReleaseServices(workspace, checker, preparation, publishing);
    }

    private ReleaseOptions BuildOptions()
    {
        if (string.IsNullOrWhiteSpace(VersionText))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-version-required",
                "A release version is required.",
                "The command did not include `--version <x.y.z[-label.n]>`.",
                "Pass a SemVer 2.0 version without a leading `v`, for example `--version 0.1.0-preview.1`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#quickstart"));
        }

        var parsedVersion = SemVer.Parse(VersionText);
        DateOnly? parsedDate = null;
        if (!string.IsNullOrWhiteSpace(DateText)
            && DateOnly.TryParseExact(DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDateValue))
        {
            parsedDate = parsedDateValue;
        }
        else if (!string.IsNullOrWhiteSpace(DateText))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-date-invalid",
                $"Release date '{DateText}' is invalid.",
                "Release dates must be stable across local machines and CI.",
                "Use the ISO date shape `YYYY-MM-DD`, for example `--date 2026-05-25`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepare"));
        }

        var repoRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(RepositoryRoot) ? _executionContext.CurrentDirectory : RepositoryRoot);
        return new ReleaseOptions(
            CommandName,
            repoRoot,
            parsedVersion,
            ResolveTag(parsedVersion),
            parsedDate,
            DryRun,
            ResolveOptionalPath(repoRoot, ReportPath),
            ResolveGitHubOutputPath(repoRoot),
            FailOnWarnings,
            AllowExistingTargets);
    }

    /// <summary>
    /// Gets the command name used by release validation logic.
    /// </summary>
    protected abstract string CommandName { get; }

    /// <summary>
    /// Resolves the command tag, when relevant.
    /// </summary>
    /// <param name="version">Parsed release version.</param>
    /// <returns>The tag value, or <see langword="null"/> for commands that do not use a tag.</returns>
    protected virtual string? ResolveTag(SemVer version) => null;

    /// <summary>
    /// Resolves the GitHub Actions output path, when relevant.
    /// </summary>
    /// <param name="repoRoot">Resolved repository root.</param>
    /// <returns>The output path, or <see langword="null"/> for commands that do not write workflow outputs.</returns>
    protected virtual string? ResolveGitHubOutputPath(string repoRoot) => null;

    /// <summary>
    /// Gets whether this command should turn warning diagnostics into a failing exit code.
    /// </summary>
    protected virtual bool FailOnWarnings => false;

    /// <summary>
    /// Gets whether this command may review already-generated release artifacts.
    /// </summary>
    protected virtual bool AllowExistingTargets => false;

    /// <summary>
    /// Writes the rendered command report to stdout and to the optional report path.
    /// </summary>
    /// <param name="options">Resolved command options.</param>
    /// <param name="rendered">Rendered Markdown report.</param>
    /// <param name="standardOut">Standard output writer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the report is written.</returns>
    protected static async Task WriteReportAsync(
        ReleaseOptions options,
        string rendered,
        TextWriter standardOut,
        CancellationToken cancellationToken)
    {
        await standardOut.WriteLineAsync(rendered);
        if (options.ReportPath is not null && (!options.DryRun || !ReleaseWorkspace.IsUnderPath(options.RepositoryRoot, options.ReportPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.ReportPath)!);
            await File.WriteAllTextAsync(options.ReportPath, rendered, cancellationToken);
        }
    }

    private static string? ResolveOptionalPath(string repoRoot, string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value, repoRoot);
    }

    /// <summary>
    /// Command services bound to a resolved workspace.
    /// </summary>
    /// <param name="Workspace">Workspace path helper.</param>
    /// <param name="Checker">Readiness checker.</param>
    /// <param name="Preparation">Release preparation workflow.</param>
    /// <param name="Publishing">Release publishing workflow.</param>
    protected sealed record ReleaseServices(
        ReleaseWorkspace Workspace,
        ReleaseChecker Checker,
        ReleasePreparation Preparation,
        ReleasePublishing Publishing);
}
