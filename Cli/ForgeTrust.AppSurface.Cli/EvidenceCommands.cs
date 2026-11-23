using System.Diagnostics.CodeAnalysis;
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
    [CommandOption("receipt-wait-timeout", Description = "Maximum finite positive wait for another local reuse owner. Defaults to 00:30:00.")]
    public string ReceiptWaitTimeout { get; set; } = "00:30:00";

    /// <summary>Gets or sets the solution supplied to the built-in coverage identity resolver.</summary>
    [CommandOption("solution", Description = "Solution used to identify built-in coverage settings; no build or tests are run by explain.")]
    public string? SolutionPath { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            var cancellationToken = console.RegisterCancellationHandler();
            if (Reuse)
            {
                EvidenceRunCommand.ValidateReuseOptions(true, false, ReceiptWaitTimeout);
                ValidateReadOnlyOutput(OutputDirectory, ReceiptStore);
            }
            var resolution = Reuse
                ? await Workflow.ResolveAsync(CreatePlanningRequest(), cancellationToken)
                : null;
            var plan = resolution?.Plan ?? await Workflow.ExplainAsync(CreatePlanningRequest(), cancellationToken);
            EvidenceRunCommand.ValidateReuseContext(Reuse, plan);
            var decisions = Reuse
                ? await (_reuseResolver ?? throw new CommandException("ASEVD216: Read-only reuse explanation requires the built-in coverage adapter. Fix: run the installed appsurface tool with its Evidence services registered. Code: reuse.explain_unavailable."))
                    .ExplainAsync(plan, resolution?.DiffSnapshot, SolutionPath, ReceiptStore,
                        TimeSpan.Parse(ReceiptWaitTimeout, System.Globalization.CultureInfo.InvariantCulture), cancellationToken)
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
            var store = Path.TrimEndingDirectorySeparator(Path.GetFullPath(EvidenceReceiptStore.ResolveRoot(
                receiptStore, Environment.GetEnvironmentVariable("APPSURFACE_EVIDENCE_RECEIPT_STORE"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
            using var outputLease = CoverageRunOutputLease.AcquireArtifactDirectory(output, false, false);
            using var storeLease = CoverageRunOutputLease.AcquireArtifactDirectory(store, false, false);
            static bool Within(string parent, string candidate) => candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            if (Within(store, output) || Within(output, store))
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
    [CommandOption("receipt-wait-timeout", Description = "Maximum finite positive wait for another local reuse owner. Defaults to 00:30:00.")]
    public string ReceiptWaitTimeout { get; set; } = "00:30:00";

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            ValidateReuseOptions(Reuse, VerifyReuse, ReceiptWaitTimeout);
            var receiptWait = TimeSpan.Parse(ReceiptWaitTimeout, System.Globalization.CultureInfo.InvariantCulture);

            var cancellationToken = console.RegisterCancellationHandler();
            var resolution = await Workflow.ResolveAsync(CreatePlanningRequest(), cancellationToken);
            var plan = resolution.Plan;
            ValidateReuseContext(Reuse, plan);

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
            await Workflow.WritePlanAsync(plan, outputDirectory, cancellationToken);
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
            else
            {
                var results = new List<EvidenceProducerResult>();
                var resourceResults = plan.Profile.Resources
                    .Select(resource => new EvidenceResourceResult(
                        resource.Id,
                        EvidenceResourceOutcome.Unavailable,
                        0,
                        "Resource-backed evidence requires an explicitly registered consumer EvidenceHost; the CLI does not provision or infer consumer resources."))
                    .ToArray();
                if (resourceResults.Length > 0)
                {
                    results.AddRange(plan.Profile.Producers.Select(producer => new EvidenceProducerResult(
                        producer.Id,
                        EvidenceProducerOutcome.Unavailable,
                        [],
                        "The selected profile requires consumer-owned resources. Run it through ForgeTrust.AppSurface.Evidence.Aspire with explicit registrations.")));
                }
                else
                    foreach (var producer in plan.Profile.Producers)
                        results.Add(await RunProducerAsync(producer, resolution.DiffSnapshot, outputDirectory, console, cancellationToken));
                manifest = EvidenceManifestBuilder.Build(plan, results, ObservationOnly, resourceResults: resourceResults);
            }

            await Workflow.WriteManifestAsync(manifest, outputDirectory, cancellationToken, reuseDecisions);
            await console.Output.WriteLineAsync(reuseDecisions is null
                ? EvidenceCliWorkflow.FormatSummary(manifest)
                : EvidenceCliWorkflow.FormatSummary(manifest, reuseDecisions));
            await console.Output.WriteLineAsync($"Artifacts: {Path.Join(outputDirectory, "evidence-plan.json")}, {Path.Join(outputDirectory, "evidence-manifest.json")}, {Path.Join(outputDirectory, "evidence-summary.json")}");
            await WriteGitHubSummaryAsync(manifest, cancellationToken);
            if (manifest.ClaimKind == EvidenceClaimKind.None)
            {
                throw new CommandException("ASEVD211: Evidence is incomplete. Inspect evidence-summary.json and the producer output before allowing a gate to proceed.");
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

    /// <summary>Rejects invalid opt-in and duration options before any output, store, resource or producer work.</summary>
    /// <param name="reuse">Whether local receipt reuse is requested.</param>
    /// <param name="verifyReuse">Whether a fresh receipt audit is requested.</param>
    /// <param name="receiptWaitTimeout">Invariant finite positive duration string.</param>
    internal static void ValidateReuseOptions(bool reuse, bool verifyReuse, string receiptWaitTimeout)
    {
        if (verifyReuse && !reuse)
            throw new CommandException("ASEVD213: --verify-reuse requires --reuse. Fix: pass both options or remove --verify-reuse. Code: reuse.verify_requires_reuse. No store or output was created.");
        if (!TimeSpan.TryParse(receiptWaitTimeout, System.Globalization.CultureInfo.InvariantCulture, out var receiptWait)
            || receiptWait <= TimeSpan.Zero || receiptWait == Timeout.InfiniteTimeSpan)
            throw new CommandException("ASEVD214: --receipt-wait-timeout must be a finite positive duration. Fix: pass a value such as 00:30:00. Code: reuse.invalid_wait_timeout. No store or output was created.");
    }

    /// <summary>Rejects local reuse in release or protected CI contexts before execution or receipt inspection.</summary>
    /// <param name="reuse">Whether reuse is enabled; fresh invocations retain their existing behavior.</param>
    /// <param name="plan">The selected policy-bound plan.</param>
    /// <param name="environmentReader">Optional per-call environment seam, without process-wide test mutation.</param>
    internal static void ValidateReuseContext(bool reuse, EvidencePlan plan, Func<string, string?>? environmentReader = null)
    {
        if (reuse && (plan.Profile.Scope == EvidenceProfileScope.Release
            || string.Equals((environmentReader ?? Environment.GetEnvironmentVariable)("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)))
            throw new CommandException("ASEVD215: local producer reuse is unavailable for release profiles and protected CI. Fix: run without --reuse in the trusted release workflow. Code: reuse.gate_context_forbidden. No producer or resource was started.");
    }

    private async Task<EvidenceProducerResult> RunProducerAsync(
        EvidenceProducerDeclaration producer,
        EvidenceDiffSnapshot? diffSnapshot,
        string outputDirectory,
        IConsole console,
        CancellationToken cancellationToken)
    {
        return await _coverageProducer.RunAsync(
            producer,
            SolutionPath,
            outputDirectory,
            diffSnapshot,
            CoverageTextWriters.Create(console.Output, console.Error),
            cancellationToken);
    }

    private static async Task WriteGitHubSummaryAsync(EvidenceManifest manifest, CancellationToken cancellationToken)
    {
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(summaryPath))
        {
            return;
        }

        var markdown = $"## AppSurface Evidence{Environment.NewLine}{Environment.NewLine}- Claim: `{manifest.ClaimKind}`{Environment.NewLine}- Execution: `{manifest.ExecutionVerdict}`{Environment.NewLine}- Closed obligations: {manifest.ClosedObligationIds.Count}{Environment.NewLine}- Unmediated obligations: {manifest.UnmediatedObligationIds.Count}{Environment.NewLine}";
        await File.AppendAllTextAsync(summaryPath, markdown, new UTF8Encoding(false), cancellationToken);
    }
}

/// <summary>
/// Verifies that an immutable evidence manifest binds to its resolved plan without rerunning producers.
/// </summary>
[Command("evidence verify", Description = "Verify plan and manifest digests without rerunning producers.")]
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
            var (_, manifest) = await _workflow.VerifyAsync(planPath, ManifestPath, console.RegisterCancellationHandler(), ForGate);
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
