using System.Text;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Verifies the complete release-preparation pull-request diff from a base ref to the checked-out HEAD.
/// </summary>
/// <remarks>
/// This command is the supported local and CI entry point for release-preparation provenance. Normal callers should
/// use <c>./eng/release verify-prep-diff --base-ref main</c>; <c>--witness</c> exists only as a controlled test/CI seam.
/// </remarks>
[Command("verify-prep-diff", Description = "Classify the full release-preparation diff and verify generated package documentation provenance.")]
internal sealed partial class ReleaseVerifyPreparationDiffCommand : ICommand
{
    private readonly ReleaseExecutionContext _executionContext;
    private readonly ICommandRunner _commandRunner;

    /// <summary>
    /// Creates the command.
    /// </summary>
    /// <param name="executionContext">Invocation root used to resolve repository-relative options.</param>
    /// <param name="commandRunner">Bounded process runner used by the full-diff verifier.</param>
    public ReleaseVerifyPreparationDiffCommand(ReleaseExecutionContext executionContext, ICommandRunner commandRunner)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(commandRunner);
        _executionContext = executionContext;
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Gets the repository root. Defaults to the command invocation directory.
    /// </summary>
    [CommandOption("repo-root", Description = "Repository root. Defaults to the current directory.")]
    public string? RepositoryRoot { get; set; }

    /// <summary>
    /// Gets the base branch or ref. The default is <c>main</c>, fetched as <c>origin/main</c>.
    /// </summary>
    [CommandOption("base-ref", Description = "Base branch or ref. Defaults to main and is fetched as origin/main.")]
    public string? BaseRef { get; set; }

    /// <summary>
    /// Gets whether to skip the base-ref refresh for an intentionally offline, already-current checkout.
    /// </summary>
    [CommandOption("no-fetch", Description = "Do not fetch the requested base ref; fail with diagnostics if the local remote ref is missing or stale.")]
    public bool NoFetch { get; set; }

    /// <summary>
    /// Gets an optional pre-created PackageIndex witness path used only by controlled CI/test integrations.
    /// </summary>
    [CommandOption("witness", Description = "Advanced CI/test seam: pre-created PackageIndex witness JSON path. Normal callers should omit this option.")]
    public string? WitnessPath { get; set; }

    /// <summary>
    /// Gets an optional Markdown report destination.
    /// </summary>
    [CommandOption("report", Description = "Optional Markdown report path. Relative paths resolve from the repository root.")]
    public string? ReportPath { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var cancellationToken = console.RegisterCancellationHandler();
        try
        {
            var repositoryRoot = string.IsNullOrWhiteSpace(RepositoryRoot)
                ? Path.GetFullPath(_executionContext.CurrentDirectory)
                : Path.GetFullPath(RepositoryRoot, _executionContext.CurrentDirectory);
            var result = await new ReleasePreparationDiffVerifier(_commandRunner).VerifyAsync(
                repositoryRoot,
                string.IsNullOrWhiteSpace(BaseRef) ? "main" : BaseRef,
                NoFetch,
                string.IsNullOrWhiteSpace(WitnessPath) ? null : Path.GetFullPath(WitnessPath, repositoryRoot),
                cancellationToken);
            var report = ReleasePreparationDiffReportRenderer.Render(result);
            if (!string.IsNullOrWhiteSpace(ReportPath))
            {
                var reportPath = Path.GetFullPath(ReportPath, repositoryRoot);
                var directory = Path.GetDirectoryName(reportPath);
                Directory.CreateDirectory(directory!);
                await File.WriteAllTextAsync(reportPath, report, cancellationToken);
            }

            await console.Output.WriteAsync(report);
            Environment.ExitCode = result.IsValid ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            console.Error.WriteLine(ReleaseDiagnostic.Error(
                "release-prep-report-io-failure",
                "Release preparation could not write its requested report.",
                ex.Message,
                "Use an ordinary writable report path and rerun verify-prep-diff.",
                "tools/ForgeTrust.AppSurface.Release/README.md#verify-prep-diff").Render());
            Environment.ExitCode = 1;
        }
    }
}

/// <summary>
/// Markdown renderer for <see cref="ReleasePreparationDiffResult"/>.
/// </summary>
internal static class ReleasePreparationDiffReportRenderer
{
    /// <summary>
    /// Renders the full-diff identity, changes, and structured diagnostics without allowing diff content to alter the table shape.
    /// </summary>
    /// <param name="result">Classified release-preparation diff.</param>
    /// <returns>Stable Markdown report suitable for the GitHub step summary.</returns>
    internal static string Render(ReleasePreparationDiffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.AppendLine("# Release-preparation diff report");
        builder.AppendLine();
        builder.AppendLine($"- Base ref: `{EscapeInline(result.BaseRef)}`");
        builder.AppendLine($"- Base tip: `{EscapeInline(result.BaseTipCommit ?? "unavailable")}`");
        builder.AppendLine($"- Merge base: `{EscapeInline(result.MergeBaseCommit ?? "unavailable")}`");
        builder.AppendLine($"- Head: `{EscapeInline(result.HeadCommit ?? "unavailable")}`");
        builder.AppendLine($"- Result: {(result.IsValid ? "pass" : "fail")}");
        builder.AppendLine();
        builder.AppendLine("## Changed files");
        builder.AppendLine();
        builder.AppendLine("| Status | Path | Original path |");
        builder.AppendLine("| --- | --- | --- |");
        if (result.Changes.Count == 0)
        {
            builder.AppendLine("| — | — | — |");
        }
        else
        {
            foreach (var change in result.Changes)
            {
                builder.Append('|').Append(EscapeTable(change.Status)).Append('|').Append(EscapeTable(change.Path)).Append('|').Append(EscapeTable(change.OriginalPath ?? string.Empty)).AppendLine("|");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Diagnostics");
        builder.AppendLine();
        builder.AppendLine("| Severity | Code | Problem | Cause | Fix | Docs |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        if (result.Diagnostics.Count == 0)
        {
            builder.AppendLine("| — | — | — | — | — | — |");
        }
        else
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                builder.Append('|').Append(EscapeTable(diagnostic.Severity))
                    .Append('|').Append(EscapeTable(diagnostic.Code))
                    .Append('|').Append(EscapeTable(diagnostic.Problem))
                    .Append('|').Append(EscapeTable(diagnostic.Cause))
                    .Append('|').Append(EscapeTable(diagnostic.Fix))
                    .Append('|').Append(EscapeTable(diagnostic.Docs)).AppendLine("|");
            }
        }

        return builder.ToString();
    }

    private static string EscapeInline(string value) => EscapeTable(value).Replace("\\|", "|", StringComparison.Ordinal);

    private static string EscapeTable(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '|':
                    builder.Append("\\|");
                    break;
                case '`':
                    builder.Append("\\`");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                default:
                    builder.Append(char.IsControl(character) ? $"\\u{(int)character:x4}" : character);
                    break;
            }
        }

        return builder.ToString();
    }
}
