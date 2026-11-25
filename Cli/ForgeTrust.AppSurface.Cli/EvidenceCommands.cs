using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the discoverable root for AppSurface EvidenceHost commands.
/// </summary>
[Command("evidence", Description = "Inspect AppSurface EvidenceHost commands for deterministic CI evidence planning and claims.")]
internal sealed partial class EvidenceCommand : ICommand
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers root help; subcommands carry behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync("Use 'appsurface evidence init --sample' to create a starter, 'appsurface evidence doctor' to inspect prerequisites, 'appsurface evidence explain' to resolve policy, 'appsurface evidence run' to execute selected built-in evidence, or 'appsurface evidence verify <manifest>' to validate immutable output.");
    }
}

/// <summary>
/// Creates a marked, non-overwriting EvidenceHost starter for an existing repository.
/// </summary>
[Command("evidence init", Description = "Create a non-overwriting EvidenceHost starter and sample policy in an existing repository.")]
internal sealed partial class EvidenceInitCommand(EvidenceCliWorkflow workflow) : ICommand
{
    private readonly EvidenceCliWorkflow _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

    /// <summary>Gets or sets the destination directory for generated starter files.</summary>
    [CommandOption("root", Description = "Starter directory. Defaults to .appsurface/evidence.")]
    public string RootPath { get; set; } = Path.Join(".appsurface", "evidence");

    /// <summary>Gets or sets a value indicating whether an existing marked starter may be replaced.</summary>
    [CommandOption("force", Description = "Replace only existing files carrying the AppSurface Evidence starter marker.")]
    public bool Force { get; set; }

    /// <summary>Gets or sets a value indicating whether to generate the supported v1 sample.</summary>
    [CommandOption("sample", Description = "Generate the supported v1 EvidenceHost sample. This is the default behavior.")]
    public bool Sample { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            var result = await _workflow.InitializeAsync(RootPath, Force, console.RegisterCancellationHandler());
            await console.Output.WriteLineAsync($"Evidence starter created: {result.RootPath}");
            foreach (var file in result.CreatedFiles)
            {
                await console.Output.WriteLineAsync($"  created {file}");
            }

            await console.Output.WriteLineAsync($"Next: appsurface evidence doctor --policy {Path.Join(result.RootPath, "evidence.policy.json")} --path docs/README.md");
        }
        catch (EvidenceCliException exception)
        {
            throw new CommandException(exception.Message);
        }
    }
}

/// <summary>
/// Checks selected EvidenceHost prerequisites without provisioning resources or executing tests.
/// </summary>
[Command("evidence doctor", Description = "Check policy, diff, envelope, Docker, and browser prerequisites without provisioning resources.")]
internal sealed partial class EvidenceDoctorCommand(EvidenceCliWorkflow workflow) : EvidencePlanningCommandBase(workflow)
{
    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            var report = await Workflow.DoctorAsync(CreatePlanningRequest(), console.RegisterCancellationHandler());
            await console.Output.WriteLineAsync($"Evidence doctor: {report.Status}");
            foreach (var check in report.Checks)
            {
                await console.Output.WriteLineAsync($"  {check.Status} {check.Id}: {check.Message}");
                if (!string.IsNullOrWhiteSpace(check.NextAction))
                {
                    await console.Output.WriteLineAsync($"    Next: {check.NextAction}");
                }
            }

            if (string.Equals(report.Status, "blocked", StringComparison.Ordinal))
            {
                throw new CommandException("ASEVD210: Evidence doctor is blocked. Fix the named prerequisite before running evidence.");
            }
        }
        catch (EvidencePlanningException exception)
        {
            throw new CommandException(exception.Message);
        }
        catch (EvidenceCliException exception)
        {
            throw new CommandException(exception.Message);
        }
    }
}

/// <summary>
/// Resolves a policy and explicit diff into a plan without starting resources or executing producers.
/// </summary>
[Command("evidence explain", Description = "Explain the selected profile, obligations, producers, resources, and optional read-only receipt candidates.")]
internal sealed partial class EvidenceExplainCommand(EvidenceCliWorkflow workflow, CoverageEvidenceProducer? coverageProducer = null) : EvidencePlanningCommandBase(workflow)
{
    private readonly EvidenceReuseExecutionResolver? _reuseResolver = coverageProducer is null ? null : new EvidenceReuseExecutionResolver(coverageProducer);

    /// <summary>Gets or sets the output directory for plan and summary artifacts.</summary>
    [CommandOption("output", Description = "Evidence artifact directory. Defaults to TestResults/evidence.")]
    public string OutputDirectory { get; set; } = Path.Join("TestResults", "evidence");

    /// <summary>Gets or sets whether to inspect local receipt candidates without acquiring execution ownership.</summary>
    [CommandOption("reuse", Description = "Read-only candidate inspection. Does not create stores, acquire execution leases, update LRU, restore artifacts, or run producers.")]
    public bool Reuse { get; set; }

    /// <summary>Gets or sets the optional local producer receipt store directory.</summary>
    [CommandOption("receipt-store", Description = "Override the local receipt store selected by APPSURFACE_EVIDENCE_RECEIPT_STORE or the per-user default.")]
    public string? ReceiptStore { get; set; }

    /// <summary>Gets or sets the finite positive receipt wait budget used by the later run.</summary>
    [CommandOption("receipt-wait-timeout", Description = EvidenceReuseCommandOptions.ReceiptWaitTimeoutDescription)]
    public string ReceiptWaitTimeout { get; set; } = EvidenceReuseCommandOptions.DefaultReceiptWaitTimeout;

    /// <summary>Gets or sets the solution supplied to the built-in coverage identity resolver.</summary>
    [CommandOption("solution", Description = "Solution used to identify built-in coverage settings; no build or tests are run by explain.")]
    public string? SolutionPath { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            var cancellationToken = console.RegisterCancellationHandler();
            TimeSpan receiptWait = default;
            if (Reuse)
            {
                receiptWait = EvidenceReuseCommandOptions.ParseReceiptWaitTimeout(ReceiptWaitTimeout);
                ValidateReadOnlyOutput(OutputDirectory, ReceiptStore);
            }
            var resolution = Reuse
                ? await Workflow.ResolveAsync(CreatePlanningRequest(), cancellationToken)
                : null;
            var plan = resolution?.Plan ?? await Workflow.ExplainAsync(CreatePlanningRequest(), cancellationToken);
            EvidenceReuseCommandOptions.ValidateContext(Reuse, plan);
            var decisions = Reuse
                ? await (_reuseResolver ?? throw new CommandException("ASEVD216: Read-only reuse explanation requires the built-in coverage adapter. Fix: run the installed appsurface tool with its Evidence services registered. Code: reuse.explain_unavailable."))
                    .ExplainAsync(plan, resolution?.DiffSnapshot, SolutionPath, ReceiptStore,
                        receiptWait, cancellationToken)
                : null;
            await Workflow.WritePlanAsync(plan, OutputDirectory, cancellationToken, decisions);
            await console.Output.WriteLineAsync(decisions is null
                ? EvidenceCliWorkflow.FormatSummary(plan)
                : EvidenceCliWorkflow.FormatSummary(plan, decisions));
            await console.Output.WriteLineAsync($"Artifacts: {Path.Join(OutputDirectory, "evidence-plan.json")}, {Path.Join(OutputDirectory, "evidence-summary.json")}");
        }
        catch (EvidencePlanningException exception)
        {
            throw new CommandException(exception.Message);
        }
        catch (EvidenceCliException exception)
        {
            throw new CommandException(exception.Message);
        }
    }

    /// <summary>Rejects overlapping plan output and effective receipt-store roots before read-only inspection.</summary>
    /// <param name="outputDirectory">Directory that will receive the plan and summary.</param>
    /// <param name="receiptStore">Explicit store override; null uses the normal environment/user-local precedence.</param>
    /// <remarks>Comparison is deliberately case-insensitive on every platform so portable spelling aliases cannot
    /// bypass this pre-write guard. It does not create either directory or acquire receipt ownership.</remarks>
    /// <exception cref="CommandException">The roots overlap or cannot be resolved safely.</exception>
    internal static void ValidateReadOnlyOutput(string outputDirectory, string? receiptStore)
    {
        try
        {
            var store = EvidenceReceiptStore.ResolveRoot(receiptStore);
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
            using var outputLease = CoverageRunOutputLease.AcquireArtifactDirectory(output, false, false);
            using var storeLease = CoverageRunOutputLease.AcquireArtifactDirectory(store, false, false);
            if (EvidenceReceiptStore.PathsOverlap(store, output))
                throw new CommandException("ASEVD217: Read-only reuse explain requires output outside the receipt store. Choose a separate --output directory. Code: reuse.explain_output_overlaps_store. No plan artifacts or receipt state were written.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException
            or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new CommandException("ASEVD217: Read-only reuse explain paths could not be resolved. Choose valid, separate --output and --receipt-store directories. Code: reuse.explain_output_unavailable. No plan artifacts or receipt state were written.");
        }
    }
}

/// <summary>
/// Executes the selected built-in evidence producer and emits a truthful manifest.
/// </summary>
[Command("evidence run", Description = "Run selected built-in evidence and write a plan, manifest, and human summary.")]
internal sealed partial class EvidenceRunCommand(EvidenceCliWorkflow workflow, CoverageEvidenceProducer coverageProducer) : EvidencePlanningCommandBase(workflow)
{
    private readonly CoverageEvidenceProducer _coverageProducer = coverageProducer ?? throw new ArgumentNullException(nameof(coverageProducer));

    /// <summary>Gets or sets the output directory for plan, manifest, and producer artifacts.</summary>
    [CommandOption("output", Description = "Evidence artifact directory. Defaults to TestResults/evidence.")]
    public string OutputDirectory { get => _outputDirectory; set { _outputDirectory = value; OutputDirectorySpecified = true; } }
    private string _outputDirectory = Path.Join("TestResults", "evidence");
    private bool OutputDirectorySpecified { get; set; }

    /// <summary>Gets or sets the solution supplied to the in-process coverage producer.</summary>
    [CommandOption("solution", Description = "Solution supplied to the built-in coverage producer when the selected profile requires coverage.")]
    public string? SolutionPath { get; set; }

    /// <summary>Gets or sets a value indicating whether the run is informative only and cannot satisfy a gate.</summary>
    [CommandOption("observation-only", Description = "Emit an informational observation claim instead of a gate-eligible claim.")]
    public bool ObservationOnly { get; set; }

    /// <summary>Gets or sets whether verified local producer receipts may be reused.</summary>
    [CommandOption("reuse", Description = "Opt in to local reuse of verified producer results. Reused claims are informational only.")]
    public bool Reuse { get; set; }

    /// <summary>Gets or sets whether candidate receipts must also pass a fresh execution audit.</summary>
    [CommandOption("verify-reuse", Description = "Audit each reused candidate with a complete fresh execution; requires --reuse.")]
    public bool VerifyReuse { get; set; }

    /// <summary>Gets or sets the optional local producer receipt store directory.</summary>
    [CommandOption("receipt-store", Description = "Override the local receipt store selected by APPSURFACE_EVIDENCE_RECEIPT_STORE or the per-user default.")]
    public string? ReceiptStore { get; set; }

    /// <summary>Gets or sets the finite native receipt-lease wait budget.</summary>
    [CommandOption("receipt-wait-timeout", Description = EvidenceReuseCommandOptions.ReceiptWaitTimeoutDescription)]
    public string ReceiptWaitTimeout { get; set; } = EvidenceReuseCommandOptions.DefaultReceiptWaitTimeout;

    /// <summary>Gets or sets the factory for the built-in <c>test</c> producer; tests replace it to supply a fake test-run source.</summary>
    internal Func<string, TestEvidenceProducerOptions, TestEvidenceProducer> TestProducerFactory { get; set; } =
        static (id, options) => new TestEvidenceProducer(id, options);

    /// <summary>Gets or sets the resolver for the GitHub step summary file; <see langword="null"/> or blank writes no step summary.</summary>
    /// <remarks>Tests replace it so a test run never appends to the step summary of the CI job that runs the tests.</remarks>
    internal Func<string?> StepSummaryPathResolver { get; set; } =
        static () => Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

    /// <summary>Gets or sets the resolver for the repository root that declared test project paths, scoped coverage projects and patch paths are relative to.</summary>
    internal Func<string> RepositoryRootResolver { get; set; } =
        static () => GitRepositoryRootResolver.FindRepositoryRoot(Directory.GetCurrentDirectory());

    /// <inheritdoc />
    /// <remarks>
    /// Order: resolve the plan; reject selections that cannot run truthfully (<c>ASEVD214</c>, <c>ASEVD215</c>);
    /// retire the earlier manifest, summary and selected <c>test</c> and scoped coverage producer artifact directories (<c>ASEVD216</c> when one is a link); write the plan; run producers one at a
    /// time; confirm returned artifacts on disk and downgrade a producer whose artifact is not there; write the
    /// manifest and summary. Kinds <c>coverage</c> and <c>test</c> are built in; any other kind is <c>Unavailable</c>.
    /// </remarks>
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            var receiptWait = ParseReuseOptions(Reuse, VerifyReuse, ReceiptWaitTimeout);

            var cancellationToken = console.RegisterCancellationHandler();
            var resolution = await Workflow.ResolveAsync(CreatePlanningRequest(), cancellationToken);
            var plan = resolution.Plan;
            EvidenceReuseCommandOptions.ValidateContext(Reuse, plan);
            TestEvidenceRunSupport.ValidateSelection(plan);

            var hasTestProducer = plan.Profile.Producers.Any(TestEvidenceRunSupport.IsTestProducer);
            var testReports = new List<string>();
            string outputDirectory;
            try
            {
                outputDirectory = Reuse
                    ? EvidenceReuseOutput.CreateFreshRoot(
                        OutputDirectorySpecified ? OutputDirectory : null,
                        Path.Join("TestResults", "evidence"))
                    : OutputDirectory;
            }
            catch (Exception exception) when (Reuse && exception is (IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException))
            {
                throw new CommandException($"ASEVD217: A fresh Evidence output root could not be claimed ({exception.GetType().Name}). Choose a new --output path or repair access to TestResults/evidence/runs. Code: reuse.output_unavailable.");
            }
            if (!Reuse)
            {
                TestEvidenceRunSupport.RetireOutputs(outputDirectory, plan);
            }

            await Workflow.WritePlanAsync(plan, outputDirectory, cancellationToken);
            if (hasTestProducer)
            {
                await console.Output.WriteLineAsync(TestEvidenceRunSupport.PreviewLine);
            }

            EvidenceManifest manifest;
            IReadOnlyList<EvidenceReuseSummaryDecision>? reuseDecisions = null;
            if (Reuse)
            {
                var reuseResult = await new EvidenceReuseExecutionResolver(_coverageProducer).RunAsync(
                    plan, resolution.DiffSnapshot, SolutionPath, outputDirectory, VerifyReuse, ObservationOnly, ReceiptStore,
                    receiptWait, console.Output, console.Error, cancellationToken);
                manifest = reuseResult.Manifest;
                reuseDecisions = reuseResult.Decisions;
            }
            else if (plan.Profile.Resources.Count > 0)
            {
                manifest = EvidenceCliWorkflow.BuildResourceBackedRefusal(plan, ObservationOnly);
            }
            else
            {
                var results = new List<EvidenceProducerResult>();
                var artifactRoot = Path.Join(outputDirectory, "artifacts");
                foreach (var producer in plan.Profile.Producers)
                {
                    var produced = TestEvidenceRunSupport.IsTestProducer(producer)
                        ? await RunTestProducerAsync(plan, producer, artifactRoot, console, testReports, cancellationToken)
                        : await RunProducerAsync(producer, resolution.DiffSnapshot, outputDirectory, artifactRoot, console, cancellationToken);
                    var checkedResult = await TestEvidenceRunSupport.EnforceArtifactsAsync(artifactRoot, produced, cancellationToken);
                    if (!ReferenceEquals(checkedResult, produced))
                    {
                        await console.Error.WriteLineAsync(checkedResult.Diagnostic);
                    }

                    results.Add(checkedResult);
                }
                manifest = EvidenceManifestBuilder.Build(plan, results, ObservationOnly);
            }

            await Workflow.WriteManifestAsync(manifest, outputDirectory, cancellationToken, reuseDecisions);
            await console.Output.WriteLineAsync(reuseDecisions is null
                ? EvidenceCliWorkflow.FormatSummary(manifest)
                : EvidenceCliWorkflow.FormatSummary(manifest, reuseDecisions));
            foreach (var report in testReports)
            {
                await console.Output.WriteLineAsync(report);
            }

            await console.Output.WriteLineAsync($"Artifacts: {Path.Join(outputDirectory, "evidence-plan.json")}, {Path.Join(outputDirectory, "evidence-manifest.json")}, {Path.Join(outputDirectory, "evidence-summary.json")}");
            await WriteGitHubSummaryAsync(StepSummaryPathResolver(), manifest, testReports, cancellationToken);
            if (manifest.ClaimKind == EvidenceClaimKind.None)
            {
                throw new CommandException(hasTestProducer
                    ? "ASEVD211: Evidence is incomplete. Read the per-project reasons printed above and evidence-summary.json before allowing a gate to proceed; test producer output is not kept."
                    : "ASEVD211: Evidence is incomplete. Inspect evidence-summary.json and the producer output before allowing a gate to proceed.");
            }
        }
        catch (EvidencePlanningException exception)
        {
            throw new CommandException(exception.Message);
        }
        catch (EvidenceCliException exception)
        {
            throw new CommandException(exception.Message);
        }
    }

    /// <summary>Parses the wait duration and rejects invalid options before output, store, resource or producer work.</summary>
    /// <param name="reuse">Whether local receipt reuse is requested.</param>
    /// <param name="verifyReuse">Whether a fresh receipt audit is requested.</param>
    /// <param name="receiptWaitTimeout">Invariant finite positive duration string.</param>
    /// <returns>The validated receipt wait duration.</returns>
    internal static TimeSpan ParseReuseOptions(bool reuse, bool verifyReuse, string receiptWaitTimeout)
    {
        if (verifyReuse && !reuse)
            throw new CommandException("ASEVD213: --verify-reuse requires --reuse. Fix: pass both options or remove --verify-reuse. Code: reuse.verify_requires_reuse. No store or output was created.");
        return EvidenceReuseCommandOptions.ParseReceiptWaitTimeout(receiptWaitTimeout);
    }

    private async Task<EvidenceProducerResult> RunProducerAsync(
        EvidenceProducerDeclaration producer,
        EvidenceDiffSnapshot? diffSnapshot,
        string outputDirectory,
        string artifactRoot,
        IConsole console,
        CancellationToken cancellationToken)
    {
        var writers = CoverageTextWriters.Create(console.Output, console.Error);
        if (!ScopedCoverageReceipt.IsScopedCoverageProducer(producer))
        {
            return await _coverageProducer.RunAsync(producer, SolutionPath, outputDirectory, diffSnapshot, writers, cancellationToken);
        }

        // A scoped producer writes its receipt under artifacts/<id>; the planner guarantees the id is one safe segment.
        var execution = await _coverageProducer.RunWithInventoryAsync(
            producer,
            SolutionPath,
            outputDirectory,
            diffSnapshot,
            writers,
            cancellationToken,
            artifactWriter: new EvidenceArtifactWriter(producer, Path.Join(artifactRoot, producer.Id)),
            repositoryRoot: RepositoryRootResolver());
        return execution.Result;
    }

    private async Task<EvidenceProducerResult> RunTestProducerAsync(
        EvidencePlan plan,
        EvidenceProducerDeclaration declaration,
        string artifactRoot,
        IConsole console,
        List<string> reports,
        CancellationToken cancellationToken)
    {
        var producer = TestProducerFactory(
            declaration.Id,
            new TestEvidenceProducerOptions(RepositoryRootResolver())
            {
                DiagnosticsDirectory = Path.Join(OutputDirectory, "diagnostics"),
                Progress = console.Error,
            });
        var result = await producer.ProduceAsync(
            new EvidenceProducerContext(plan, declaration, TimeProvider.System, new EvidenceArtifactWriter(declaration, Path.Join(artifactRoot, declaration.Id))),
            cancellationToken);

        // The table is read back from the receipt that was just written, so the console shows exactly what a gate will read.
        var receiptPath = Path.Join(artifactRoot, declaration.Id, TestEvidenceProducer.ReceiptRelativePath);
        reports.Add(TestEvidenceRunSupport.FormatCountsTable(
            declaration.Id,
            TestEvidenceRunSupport.TryReadReceipt(receiptPath, out var rows) ? rows : null));
        foreach (var line in TestEvidenceRunSupport.FormatIncomplete(declaration.Id, producer.LastProjects))
        {
            await console.Error.WriteLineAsync(line);
        }

        foreach (var notice in TestEvidenceRunSupport.FormatMinimumNotices(declaration.Test!.Projects, producer.LastProjects))
        {
            await console.Output.WriteLineAsync(notice);
        }

        if (producer.LastTracePath is { } tracePath)
        {
            await console.Error.WriteLineAsync($"The test producer stopped on an internal error. Trace (exception type and stack frames only): {tracePath}");
        }

        return result;
    }

    private static async Task WriteGitHubSummaryAsync(string? summaryPath, EvidenceManifest manifest, IReadOnlyList<string> testReports, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(summaryPath))
        {
            return;
        }

        var markdown = new StringBuilder($"## AppSurface Evidence{Environment.NewLine}{Environment.NewLine}- Claim: `{manifest.ClaimKind}`{Environment.NewLine}- Execution: `{manifest.ExecutionVerdict}`{Environment.NewLine}- Closed obligations: {manifest.ClosedObligationIds.Count}{Environment.NewLine}- Unmediated obligations: {manifest.UnmediatedObligationIds.Count}{Environment.NewLine}");
        foreach (var report in testReports)
        {
            markdown.AppendLine().AppendLine(report);
        }

        await File.AppendAllTextAsync(summaryPath, markdown.ToString(), new UTF8Encoding(false), cancellationToken);
    }
}

/// <summary>Defines the reuse options and admission checks shared by run and read-only explain.</summary>
internal static class EvidenceReuseCommandOptions
{
    /// <summary>The invariant default receipt wait duration used by both commands.</summary>
    internal const string DefaultReceiptWaitTimeout = "00:30:00";

    /// <summary>The shared help text for the receipt wait option.</summary>
    internal const string ReceiptWaitTimeoutDescription =
        $"Maximum finite positive wait for another local reuse owner. Defaults to {DefaultReceiptWaitTimeout}.";

    /// <summary>Parses the receipt wait duration before either command touches local state.</summary>
    /// <param name="receiptWaitTimeout">An invariant finite positive duration.</param>
    /// <returns>The validated receipt wait duration.</returns>
    /// <exception cref="CommandException">The duration is invalid or nonpositive.</exception>
    internal static TimeSpan ParseReceiptWaitTimeout(string receiptWaitTimeout)
    {
        if (!TimeSpan.TryParse(receiptWaitTimeout, CultureInfo.InvariantCulture, out var receiptWait)
            || receiptWait <= TimeSpan.Zero)
            throw new CommandException("ASEVD214: --receipt-wait-timeout must be a finite positive duration. Fix: pass a value such as 00:30:00. Code: reuse.invalid_wait_timeout. No store or output was created.");
        return receiptWait;
    }

    /// <summary>Rejects local reuse in release or protected CI contexts and in profiles that select the preview test producer (<c>ASEVD219</c>) or a scoped coverage producer (<c>ASEVD220</c>).</summary>
    /// <param name="reuse">Whether reuse is enabled; fresh invocations retain their existing behavior.</param>
    /// <param name="plan">The selected policy-bound plan.</param>
    /// <param name="environmentReader">Optional per-call environment seam, without process-wide test mutation.</param>
    internal static void ValidateContext(bool reuse, EvidencePlan plan, Func<string, string?>? environmentReader = null)
    {
        if (reuse && EvidenceReuseContext.IsProtected(plan.Profile.Scope, environmentReader ?? Environment.GetEnvironmentVariable))
            throw new CommandException("ASEVD215: local producer reuse is unavailable for release profiles and protected CI. Fix: run without --reuse in the trusted release workflow. Code: reuse.gate_context_forbidden. No producer or resource was started.");
        if (reuse && plan.Profile.Producers.Any(TestEvidenceRunSupport.IsTestProducer))
            throw new CommandException("ASEVD219: The preview test producer requires a fresh CLI run. Run the selected profile without --reuse. Code: reuse.test_preview_requires_fresh. No producer or resource was started.");
        if (reuse && plan.Profile.Producers.Any(ScopedCoverageReceipt.IsScopedCoverageProducer))
            throw new CommandException("ASEVD220: A scoped coverage producer requires a fresh CLI run. Run the selected profile without --reuse. Code: reuse.scoped_coverage_requires_fresh. No producer or resource was started.");
    }
}

/// <summary>
/// Verifies that an immutable evidence manifest binds to its resolved plan and that its retained artifacts are intact, without rerunning producers.
/// </summary>
[Command("evidence verify", Description = "Verify plan and manifest digests and retained artifacts without rerunning producers.")]
internal sealed partial class EvidenceVerifyCommand(EvidenceCliWorkflow workflow) : ICommand
{
    private readonly EvidenceCliWorkflow _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

    /// <summary>Gets or sets the manifest path to verify.</summary>
    [CommandParameter(0, Description = "Generated evidence-manifest.json path to verify.")]
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolved plan path. Defaults next to the manifest.</summary>
    [CommandOption("plan", Description = "Generated evidence-plan.json path. Defaults next to the manifest.")]
    public string? PlanPath { get; set; }

    /// <summary>Gets or sets whether the verified manifest must be eligible for gate admission.</summary>
    [CommandOption("for-gate", Description = "Require gate admission in addition to canonical integrity; local reused evidence is informational only.")]
    public bool ForGate { get; set; }

    /// <summary>Gets or sets an explicit artifact directory holding one subdirectory per producer id.</summary>
    [CommandOption("artifact-root", Description = "Override the original run layout with a directory holding one subdirectory per producer id.")]
    public string? ArtifactRoot { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ManifestPath))
            {
                throw new CommandException("ASEVD212: evidence verify requires an evidence-manifest.json path.");
            }

            var planPath = string.IsNullOrWhiteSpace(PlanPath)
                ? Path.Join(Path.GetDirectoryName(Path.GetFullPath(ManifestPath))!, "evidence-plan.json")
                : PlanPath;
            var (_, manifest) = await _workflow.VerifyAsync(planPath, ManifestPath, ArtifactRoot, console.RegisterCancellationHandler(), ForGate);
            await console.Output.WriteLineAsync($"Evidence manifest verified: {manifest.ClaimKind} ({manifest.Eligibility})");
        }
        catch (EvidenceCliException exception)
        {
            throw new CommandException(exception.Message);
        }
    }
}

/// <summary>
/// Provides shared policy and diff options for non-mutating and execution EvidenceHost commands.
/// </summary>
internal abstract partial class EvidencePlanningCommandBase(EvidenceCliWorkflow workflow) : ICommand
{
    /// <summary>
    /// Gets the policy-planning workflow shared by derived EvidenceHost commands.
    /// </summary>
    protected EvidenceCliWorkflow Workflow { get; } = workflow ?? throw new ArgumentNullException(nameof(workflow));

    /// <summary>Gets or sets the checked-in evidence policy path.</summary>
    [CommandOption("policy", Description = "Checked-in evidence.policy.json path. Defaults to .appsurface/evidence/evidence.policy.json.")]
    public string PolicyPath { get; set; } = Path.Join(".appsurface", "evidence", "evidence.policy.json");

    /// <summary>Gets or sets explicit changed repository-relative paths. Repeat for multiple paths.</summary>
    [CommandOption("path", Description = "Repeatable normalized repository-relative changed path.")]
    public string[] Paths { get; set; } = [];

    /// <summary>Gets or sets an optional unified diff used to derive changed paths.</summary>
    [CommandOption("diff-file", Description = "Unified diff file used to derive changed paths without local Git history.")]
    public string? DiffFile { get; set; }

    /// <inheritdoc />
    public abstract ValueTask ExecuteAsync(IConsole console);

    /// <summary>
    /// Creates the explicit policy-and-diff input consumed by a planning operation.
    /// </summary>
    protected EvidencePlanningRequest CreatePlanningRequest() => new(PolicyPath, Paths, DiffFile);
}
