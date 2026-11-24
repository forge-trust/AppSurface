using System.Globalization;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>One row of the counts table, read back from a <c>test-receipt.json</c>.</summary>
/// <param name="Project">Declared project path.</param>
/// <param name="Discovered">Discovered tests.</param>
/// <param name="Executed">Tests that passed or failed.</param>
/// <param name="Passed">Tests that passed.</param>
/// <param name="Failed">Tests that failed.</param>
/// <param name="Skipped">Tests that were not executed.</param>
/// <param name="Reason">Reason code, with the sub-reason after a slash when present.</param>
internal sealed record TestReceiptRow(string Project, int Discovered, int Executed, int Passed, int Failed, int Skipped, string Reason);

/// <summary>
/// The parts of <c>appsurface evidence run</c> and <c>explain</c> that exist for the preview <c>test</c> producer:
/// selection checks, retiring earlier outputs, the artifact check before a manifest is written, and operator output.
/// </summary>
/// <remarks>
/// Nothing here prints a test name: the table is read back from the receipt, and the per-project lines come from
/// reasons, phases and commands. Filter text is public plan data and may appear in <c>explain</c> and rerun hints.
/// </remarks>
internal static class TestEvidenceRunSupport
{
    /// <summary>Documentation page the per-project lines link to.</summary>
    public const string DocumentationPage = "Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md";

    /// <summary>The line printed by <c>explain</c> and <c>run</c> whenever a <c>test</c> producer is selected.</summary>
    public const string PreviewLine =
        "Preview: the 'test' producer kind, its assertion id and its receipt format may change without a compatibility step. See "
        + DocumentationPage + "#test-producer-preview";

    /// <summary>The line that follows every counts table.</summary>
    public const string NoCoverageClaimLine = "No coverage claim was made.";

    /// <summary>Returns whether a declaration is a built-in <c>test</c> producer.</summary>
    public static bool IsTestProducer(EvidenceProducerDeclaration producer) =>
        string.Equals(producer.Kind, EvidenceTestProducerContract.Kind, StringComparison.Ordinal);

    /// <summary>
    /// Rejects selections <c>run</c> cannot execute truthfully before anything is written.
    /// </summary>
    /// <param name="plan">The resolved plan.</param>
    /// <exception cref="EvidenceCliException">
    /// <c>ASEVD214</c>: a profile with a <c>test</c> producer declares resources. <c>ASEVD215</c>: two artifact-writing
    /// producers have ids that differ only by case, so they would share one artifact directory on some file systems.
    /// </exception>
    public static void ValidateSelection(EvidencePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Profile.Resources.Count > 0 && plan.Profile.Producers.FirstOrDefault(IsTestProducer) is { } resourceBacked)
        {
            throw new EvidenceCliException(
                "ASEVD214",
                $"Profile '{plan.Profile.Id}' selects test producer '{resourceBacked.Id}' and declares resources; the 'test' kind is not available through a resource-backed profile in this preview.",
                "Move the test producer to a profile without resources, or run resource-backed evidence through an EvidenceHost with its own producer.");
        }

        var collision = plan.Profile.Producers
            .Where(static producer => producer.ArtifactSlots.Count > 0)
            .GroupBy(static producer => producer.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (collision is not null)
        {
            throw new EvidenceCliException(
                "ASEVD215",
                $"Producers {string.Join(" and ", collision.Select(static producer => $"'{producer.Id}'"))} write artifacts and have ids that differ only by case.",
                "Rename one producer; each artifact-writing producer needs its own artifact directory on every file system.");
        }
    }

    /// <summary>
    /// Deletes the manifest, the summary and the selected <c>test</c> producers' artifact directories of an earlier run.
    /// </summary>
    /// <param name="outputDirectory">The evidence output directory.</param>
    /// <param name="plan">The plan about to run.</param>
    /// <remarks>
    /// <para>
    /// Without this, a rerun that is cancelled after the plan is rewritten would leave the earlier passing manifest in
    /// place, still bound to an identical plan. After a cancelled rerun, <c>verify</c> now reports a missing manifest.
    /// </para>
    /// <para>
    /// Only <c>test</c> producers have artifact directories retired: they are the only producers the CLI gives an
    /// artifact writer, and the planner guarantees their ids are one safe path segment. A producer of another kind may
    /// have an id such as <c>../../x</c>, so its id is never joined into a path that is deleted.
    /// </para>
    /// </remarks>
    /// <exception cref="EvidenceCliException">
    /// <c>ASEVD216</c>: the <c>artifacts</c> directory or a producer directory to retire is a symbolic link or other
    /// reparse point, so deleting through it could remove files outside the output directory. Nothing is deleted
    /// when this is thrown.
    /// </exception>
    public static void RetireOutputs(string outputDirectory, EvidencePlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        // Every link check runs before anything is deleted, so a rejected run leaves the earlier outputs as they were.
        var artifactRoot = Path.Join(outputDirectory, "artifacts");
        string[] retired = [];
        if (Directory.Exists(artifactRoot))
        {
            RejectLinkedDirectory(artifactRoot);
            retired = plan.Profile.Producers
                .Where(IsTestProducer)
                .Select(producer => Path.Join(artifactRoot, producer.Id))
                .Where(Directory.Exists)
                .ToArray();
            foreach (var directory in retired)
            {
                RejectLinkedDirectory(directory);
            }
        }

        File.Delete(Path.Join(outputDirectory, "evidence-manifest.json"));
        File.Delete(Path.Join(outputDirectory, "evidence-summary.json"));
        foreach (var directory in retired)
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RejectLinkedDirectory(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new EvidenceCliException(
                "ASEVD216",
                $"Evidence artifact directory '{directory}' is a symbolic link or other reparse point.",
                "Replace it with an ordinary directory inside the evidence output directory, or choose another --output; run never deletes through a link.");
        }
    }

    /// <summary>
    /// Confirms every artifact a producer returned is on disk as recorded, and downgrades the result when one is not.
    /// </summary>
    /// <param name="artifactRoot">The <c>artifacts</c> directory of the run.</param>
    /// <param name="result">The producer result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The unchanged result, or a result with outcome <c>Unavailable</c>, no satisfied assertion ids and no artifacts.
    /// The manifest builder then reports a missing required artifact as <c>Invalid</c>, so an obligation can never
    /// close beside a receipt that is not there.
    /// </returns>
    public static async Task<EvidenceProducerResult> EnforceArtifactsAsync(string artifactRoot, EvidenceProducerResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var artifact in result.Artifacts ?? [])
        {
            var status = await EvidenceArtifactFileValidation.CheckAsync(artifactRoot, result.ProducerId, artifact, cancellationToken).ConfigureAwait(false);
            if (status != EvidenceArtifactFileStatus.Valid)
            {
                return result with
                {
                    Outcome = EvidenceProducerOutcome.Unavailable,
                    SatisfiedAssertionIds = [],
                    Artifacts = null,
                    Diagnostic = $"Artifact '{artifact.LogicalName}' of producer '{result.ProducerId}' failed its on-disk check ({status}) before the manifest was written. Rerun appsurface evidence run.",
                };
            }
        }

        return result;
    }

    /// <summary>Formats the <c>explain</c> lines for the plan's <c>test</c> producers; empty when there is none.</summary>
    /// <param name="plan">The resolved plan.</param>
    /// <returns>One heading and one line per project, followed by <see cref="PreviewLine"/>.</returns>
    public static IReadOnlyList<string> FormatPlan(EvidencePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>();
        foreach (var producer in plan.Profile.Producers.Where(IsTestProducer))
        {
            lines.Add($"Test producer '{producer.Id}' (timeout {producer.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s):");
            // The planner guarantees a test producer carries its test block.
            foreach (var project in producer.Test!.Projects)
            {
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {project.Path} | filter: {project.Filter ?? "(none, every test)"} | minimumTests: {project.MinimumTests ?? 1}"));
            }
        }

        if (lines.Count > 0)
        {
            lines.Add(PreviewLine);
        }

        return lines;
    }

    /// <summary>Describes the plan's <c>test</c> producers for <c>evidence-summary.json</c>.</summary>
    /// <param name="plan">The resolved plan.</param>
    /// <returns>One object per <c>test</c> producer with its projects, filters and minimums.</returns>
    public static object[] DescribePlan(EvidencePlan plan) =>
    [
        .. plan.Profile.Producers.Where(IsTestProducer).Select(static producer => new
        {
            id = producer.Id,
            preview = true,
            projects = producer.Test!.Projects.Select(static project => new
            {
                path = project.Path,
                filter = project.Filter,
                minimumTests = project.MinimumTests ?? 1,
            }).ToArray(),
        }),
    ];

    /// <summary>Reads the counts rows back from a receipt file.</summary>
    /// <param name="receiptPath">The receipt path.</param>
    /// <param name="rows">One row per project; empty when the receipt cannot be read.</param>
    /// <returns>False when the file is missing or is not a schema version 1 receipt.</returns>
    public static bool TryReadReceipt(string receiptPath, out IReadOnlyList<TestReceiptRow> rows)
    {
        rows = [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
            {
                return false;
            }

            var parsed = new List<TestReceiptRow>();
            foreach (var project in root.GetProperty("projects").EnumerateArray())
            {
                var reason = project.GetProperty("reason").GetString() ?? string.Empty;
                if (project.GetProperty("subReason").GetString() is { } subReason)
                {
                    reason += "/" + subReason;
                }

                parsed.Add(new TestReceiptRow(
                    project.GetProperty("path").GetString() ?? string.Empty,
                    project.GetProperty("discovered").GetInt32(),
                    project.GetProperty("executed").GetInt32(),
                    project.GetProperty("passed").GetInt32(),
                    project.GetProperty("failed").GetInt32(),
                    project.GetProperty("skipped").GetInt32(),
                    reason));
            }

            rows = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Formats the counts table as GitHub-flavored Markdown, which is also readable on a console.</summary>
    /// <param name="producerId">Producer id shown in the heading.</param>
    /// <param name="rows">Receipt rows, or <see langword="null"/> when the receipt could not be read back.</param>
    /// <returns>The heading, the table or an explanation, and <see cref="NoCoverageClaimLine"/>.</returns>
    public static string FormatCountsTable(string producerId, IReadOnlyList<TestReceiptRow>? rows)
    {
        var text = new StringBuilder().Append("Test producer '").Append(producerId).AppendLine("':");
        if (rows is null)
        {
            text.AppendLine("The receipt could not be read back, so no counts are shown. Rerun appsurface evidence run.");
        }
        else
        {
            text.AppendLine("| Project | Discovered | Executed | Passed | Failed | Skipped | Reason |");
            text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var row in rows)
            {
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {row.Project.Replace("|", "\\|", StringComparison.Ordinal)} | {row.Discovered} | {row.Executed} | {row.Passed} | {row.Failed} | {row.Skipped} | {row.Reason} |"));
            }
        }

        return text.Append(NoCoverageClaimLine).ToString();
    }

    /// <summary>Formats the lines printed for an incomplete <c>test</c> producer run.</summary>
    /// <param name="producerId">Producer id.</param>
    /// <param name="projects">Per-project outcomes from the producer.</param>
    /// <returns>
    /// Empty when every project is complete. Otherwise a first line naming the operation and the first incomplete
    /// project (with the number of projects with failing tests when above zero), then for each incomplete project its
    /// producer id, path, phase, reason, next action and documentation anchor, followed by its rerun hint.
    /// </returns>
    public static IReadOnlyList<string> FormatIncomplete(string producerId, IReadOnlyList<TestProjectOutcome> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var incomplete = projects.Where(static project => project.Reason != TestRunReason.Complete).ToArray();
        if (incomplete.Length == 0)
        {
            return [];
        }

        var failing = projects.Count(static project => project.Reason == TestRunReason.TestsFailed);
        var first = string.Create(
            CultureInfo.InvariantCulture,
            $"evidence run: test producer '{producerId}' is incomplete for {incomplete[0].Path} ({incomplete.Length} of {projects.Count} project(s) incomplete");
        first += failing > 0 ? string.Create(CultureInfo.InvariantCulture, $", {failing} with failing tests).") : ").";
        var lines = new List<string> { first };
        foreach (var project in incomplete)
        {
            var reason = project.SubReason is null ? project.Reason.ToString() : $"{project.Reason}/{project.SubReason}";
            lines.Add($"- producer={producerId} project={project.Path} phase={project.Phase} reason={reason}");
            if (project.Detail is not null)
            {
                lines.Add($"  detail: {project.Detail}");
            }

            lines.Add($"  next: {DescribeNextAction(project.Reason)}");
            lines.Add($"  docs: {DocumentationPage}#{Anchor(project.Reason)}");
            lines.AddRange(FormatRerunHint(project.Command).Select(static line => "  " + line));
        }

        return lines;
    }

    /// <summary>The notice printed when a project discovered more than one test and declares no minimum.</summary>
    /// <param name="declared">The declared projects.</param>
    /// <param name="projects">Per-project outcomes, in declaration order.</param>
    /// <returns>One notice per affected project.</returns>
    public static IReadOnlyList<string> FormatMinimumNotices(IReadOnlyList<EvidenceTestProject> declared, IReadOnlyList<TestProjectOutcome> projects) =>
    [
        .. declared.Zip(projects)
            .Where(static pair => pair.First.MinimumTests is null && pair.Second.Counts.Discovered > 1)
            .Select(static pair => string.Create(
                CultureInfo.InvariantCulture,
                $"Notice: {pair.Second.Path} discovered {pair.Second.Counts.Discovered} tests and declares no minimumTests; a filter that starts matching fewer would still pass. Consider \"minimumTests\": {pair.Second.Counts.Discovered}.")),
    ];

    /// <summary>The one-line next action for a reason, also used by the README reason table.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>What to do next and whether rerunning unchanged can help.</returns>
    public static string DescribeNextAction(TestRunReason reason) => reason switch
    {
        TestRunReason.ProjectNotFound => "Policy error: correct the declared project path. Rerunning unchanged cannot help.",
        TestRunReason.ToolchainUnavailable => "Install the SDK that global.json requires or put the right dotnet on PATH. Rerunning unchanged cannot help.",
        TestRunReason.MultiTargetUnsupported => "This preview runs single-target projects only; declare a single-target test project. Rerunning unchanged cannot help.",
        TestRunReason.RunSettingsUnsupported => "Remove RunSettingsFilePath and VSTestSetting for this project or use other evidence; a run-settings file can select tests the policy does not name. Rerunning unchanged cannot help.",
        TestRunReason.UnsupportedRunner => "Microsoft.Testing.Platform is not supported in this preview (tracked in TODOS.md); use a VSTest project. Rerunning unchanged cannot help.",
        TestRunReason.RestoreFailed => "Run the printed dotnet restore command and fix the feed or package error. Rerunning unchanged can help after an outage.",
        TestRunReason.BuildFailed => "Run the printed command and fix the build errors. Rerunning unchanged cannot help.",
        TestRunReason.DiscoveryFailed => "Run the printed listing command and compare its output with the documented shape. Rerunning unchanged can help only for a transient exit code.",
        TestRunReason.RunAborted => "The test host stopped without reporting a failed test; run the printed command to see why. Rerunning unchanged can help.",
        TestRunReason.ResultsMissingOrStale => "The results file was missing, malformed or inconsistent. Rerunning unchanged can help.",
        TestRunReason.EmptyDiscovery => "The project and filter select no test; correct the filter. Rerunning unchanged cannot help.",
        TestRunReason.BelowMinimum => "Fewer tests were discovered than minimumTests; restore the missing tests or correct the minimum. Rerunning unchanged cannot help.",
        TestRunReason.AmbiguousIdentity => "Two tests print the same name; narrow the filter to avoid them or give them distinct names. Rerunning unchanged cannot help.",
        TestRunReason.MissingResult => "A listed test produced no result under its listed name; run the two printed commands and compare. Rerunning unchanged cannot help.",
        TestRunReason.UnexpectedResult => "A result has no listed test; run the two printed commands and compare. Rerunning unchanged cannot help.",
        TestRunReason.TestsFailed => "Run the printed command and fix the failing tests. Rerunning unchanged cannot help.",
        TestRunReason.TestsSkipped => "A selected test was skipped; exclude it by filter with a minimumTests guard or make it run. Rerunning unchanged cannot help.",
        TestRunReason.Complete => "Nothing to do.",
        TestRunReason.DeadlineExceeded => "The producer deadline passed during this project; raise timeoutSeconds or fix the hang. Rerunning unchanged rarely helps.",
        TestRunReason.NotRun => "The producer deadline passed before this project started; fix the earlier project or raise timeoutSeconds.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown test run reason."),
    };

    /// <summary>The README anchor of a reason, the lower-case reason name.</summary>
    public static string Anchor(TestRunReason reason) => "reason-" + reason.ToString().ToLowerInvariant();

    /// <summary>
    /// Formats the rerun hint: the exact argument list, and one shell line only when every argument can be quoted
    /// safely for a POSIX shell.
    /// </summary>
    /// <param name="command">The executable followed by its arguments; empty when no process decided the reason.</param>
    /// <returns>Hint lines; empty for an empty command.</returns>
    public static IReadOnlyList<string> FormatRerunHint(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            return [];
        }

        var lines = new List<string> { "rerun (run from the project directory), one argument per line:" };
        lines.AddRange(command.Select(static argument => "    " + argument));
        if (command.All(IsShellQuotable))
        {
            lines.Add("as one POSIX shell line: " + string.Join(' ', command.Select(QuoteForShell)));
        }

        return lines;
    }

    private static bool IsShellQuotable(string argument) =>
        !argument.Any(static character => character == '\'' || char.IsControl(character));

    private static string QuoteForShell(string argument) =>
        argument.Length > 0 && argument.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':' or '=' or ',')
            ? argument
            : "'" + argument + "'";
}
