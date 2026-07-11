using System.Text.Json;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the discoverable root for declared secret-promotion commands.
/// </summary>
[Command("secrets transfer", Description = "Create and apply declared, value-safe secret promotion plans.")]
internal sealed partial class SecretsTransferCommand : ICommand
{
    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync("Use 'appsurface secrets transfer plan' to create a job plan, then 'appsurface secrets transfer apply --apply' to execute it.");
    }
}

/// <summary>
/// Creates a value-free promotion plan for one declared endpoint job.
/// </summary>
[Command("secrets transfer plan", Description = "Probe and record a declared secret promotion job without reading values.")]
internal sealed partial class SecretsTransferPlanCommand(SecretPromotionWorkflow workflow) : SecretsCommandBase
{
    [CommandOption("config", Description = "Path to the declared secret-promotion JSON configuration.")]
    public string? ConfigPath { get; set; }

    [CommandOption("job", Description = "Name of the reviewed promotion job to plan.")]
    public string? JobName { get; set; }

    [CommandOption("out", Description = "Path for the value-free plan artifact.")]
    public string? OutputPlanPath { get; set; }

    [CommandOption("replace", Description = "Permit the declared job to replace destination state using provider-specific semantics.")]
    public bool Replace { get; set; }

    [CommandOption("expires-minutes", Description = "Minutes before the plan expires. Defaults to 15.")]
    public double ExpiresMinutes { get; set; } = 15;

    [CommandOption("json", Description = "Write a JSON plan summary.")]
    public bool Json { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var request = new SecretPromotionPlanRequest(
            SecretPromotionCommandExtensions.Require(ConfigPath, "--config"),
            SecretPromotionCommandExtensions.Require(JobName, "--job"),
            SecretPromotionCommandExtensions.Require(OutputPlanPath, "--out"),
            Replace,
            BuildExpiry(ExpiresMinutes),
            BuildContext());
        var result = workflow.CreatePlan(request);
        await SecretPromotionOutput.WriteAsync(console, result.Summary, Json);
        if (!result.Summary.Succeeded)
        {
            Environment.ExitCode = 1;
        }
    }

    private static TimeSpan BuildExpiry(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 60)
        {
            throw SecretPromotionCommandExtensions.Usage("--expires-minutes must be a finite number greater than 0 and at most 60.");
        }

        return TimeSpan.FromMinutes(value);
    }
}

/// <summary>
/// Applies one previously-created and still-valid secret-promotion plan.
/// </summary>
[Command("secrets transfer apply", Description = "Revalidate and apply a declared secret promotion plan.")]
internal sealed partial class SecretsTransferApplyCommand(SecretPromotionWorkflow workflow) : SecretsCommandBase
{
    [CommandOption("config", Description = "Path to the declared secret-promotion JSON configuration used to create the plan.")]
    public string? ConfigPath { get; set; }

    [CommandOption("plan", Description = "Path to the value-free plan artifact created by secrets transfer plan.")]
    public string? PlanPath { get; set; }

    [CommandOption("apply", Description = "Execute the plan. Omit to inspect the apply preflight only.")]
    public bool Apply { get; set; }

    [CommandOption("confirm", Description = "Required exact job name when a production-labelled endpoint is the destination.")]
    public string? Confirmation { get; set; }

    [CommandOption("receipt", Description = "Path for the value-free apply receipt. Defaults beside the plan.")]
    public string? ReceiptPath { get; set; }

    [CommandOption("resume", Description = "Resume a receipt by skipping rows already confirmed as written.")]
    public string? ResumeReceiptPath { get; set; }

    [CommandOption("json", Description = "Write a JSON apply summary.")]
    public bool Json { get; set; }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IConsole console)
    {
        var request = new SecretPromotionApplyRequest(
            SecretPromotionCommandExtensions.Require(ConfigPath, "--config"),
            SecretPromotionCommandExtensions.Require(PlanPath, "--plan"),
            Apply,
            Confirmation,
            ReceiptPath,
            ResumeReceiptPath,
            BuildContext());
        var result = workflow.Apply(request);
        await SecretPromotionOutput.WriteAsync(console, result, Json);
        if (!result.Succeeded)
        {
            Environment.ExitCode = 1;
        }
    }
}

internal static class SecretPromotionCommandExtensions
{
    internal static string Require(string? value, string option) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw Usage($"{option} is required.");

    internal static CommandException Usage(string message) => new(message, exitCode: 2);
}
