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
[Command("evidence explain", Description = "Explain the selected profile, obligations, producers, and resources without executing evidence.")]
internal sealed partial class EvidenceExplainCommand(EvidenceCliWorkflow workflow) : EvidencePlanningCommandBase(workflow)
{
    /// <summary>Gets or sets the output directory for plan and summary artifacts.</summary>
    [CommandOption("output", Description = "Evidence artifact directory. Defaults to TestResults/evidence.")]
    public string OutputDirectory { get; set; } = Path.Join("TestResults", "evidence");

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            var cancellationToken = console.RegisterCancellationHandler();
            var plan = await Workflow.ExplainAsync(CreatePlanningRequest(), cancellationToken);
            await Workflow.WritePlanAsync(plan, OutputDirectory, cancellationToken);
            await console.Output.WriteLineAsync(EvidenceCliWorkflow.FormatSummary(plan));
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
    public string OutputDirectory { get; set; } = Path.Join("TestResults", "evidence");

    /// <summary>Gets or sets the solution supplied to the in-process coverage producer.</summary>
    [CommandOption("solution", Description = "Solution supplied to the built-in coverage producer when the selected profile requires coverage.")]
    public string? SolutionPath { get; set; }

    /// <summary>Gets or sets a value indicating whether the run is informative only and cannot satisfy a gate.</summary>
    [CommandOption("observation-only", Description = "Emit an informational observation claim instead of a gate-eligible claim.")]
    public bool ObservationOnly { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        try
        {
            var cancellationToken = console.RegisterCancellationHandler();
            var resolution = await Workflow.ResolveAsync(CreatePlanningRequest(), cancellationToken);
            var plan = resolution.Plan;
            await Workflow.WritePlanAsync(plan, OutputDirectory, cancellationToken);
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
            {
                foreach (var producer in plan.Profile.Producers)
                {
                    results.Add(await RunProducerAsync(producer, resolution.DiffSnapshot, console, cancellationToken));
                }
            }

            var manifest = EvidenceManifestBuilder.Build(plan, results, ObservationOnly, resourceResults: resourceResults);
            await Workflow.WriteManifestAsync(manifest, OutputDirectory, cancellationToken);
            await console.Output.WriteLineAsync(EvidenceCliWorkflow.FormatSummary(manifest));
            await console.Output.WriteLineAsync($"Artifacts: {Path.Join(OutputDirectory, "evidence-plan.json")}, {Path.Join(OutputDirectory, "evidence-manifest.json")}, {Path.Join(OutputDirectory, "evidence-summary.json")}");
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

    private async Task<EvidenceProducerResult> RunProducerAsync(
        EvidenceProducerDeclaration producer,
        EvidenceDiffSnapshot? diffSnapshot,
        IConsole console,
        CancellationToken cancellationToken)
    {
        return await _coverageProducer.RunAsync(
            producer,
            SolutionPath,
            OutputDirectory,
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
            var (_, manifest) = await _workflow.VerifyAsync(planPath, ManifestPath, console.RegisterCancellationHandler());
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
