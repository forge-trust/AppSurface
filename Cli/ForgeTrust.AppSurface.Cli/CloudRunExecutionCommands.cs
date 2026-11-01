using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

namespace ForgeTrust.AppSurface.Cli;

[Command("deployment gcp-cloud-run observe-execution", Description = "Observe one exact Cloud Run Job execution without mutating it.")]
internal sealed partial class CloudRunExecutionCommand : ICommand
{
    private readonly CloudRunExecutionWorkflow _workflow;

    public CloudRunExecutionCommand(CloudRunExecutionWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    [CommandOption("bundle-dir", Description = "Published deployment bundle directory.")]
    public string? BundleDirectory { get; set; }
    [CommandOption("job", Description = "Logical migration Job identifier.")]
    public string? LogicalJobId { get; set; }
    [CommandOption("execution", Description = "Canonical projects/.../executions/... resource name.")]
    public string? ExecutionResourceName { get; set; }
    [CommandOption("timeout", Description = "Total observation timeout (for example 60s). Default: 60s.")]
    public string Timeout { get; set; } = "60s";
    [CommandOption("poll-interval", Description = "Delay between observations (for example 2s). Default: 2s.")]
    public string PollInterval { get; set; } = "2s";
    [CommandOption("json", Description = "Write one safe JSON result.")]
    public bool Json { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var cancellationToken = console.RegisterCancellationHandler();
        CloudRunExecutionWorkflowResult result;
        try
        {
            result = await _workflow.RunAsync(BundleDirectory, LogicalJobId, ExecutionResourceName, Timeout, PollInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new(null, "ASDEPLOY186");
        }
        Environment.ExitCode = await new CloudRunExecutionOutput(console.Output, console.Error, Json)
            .DeliverAsync(result, cancellationToken).ConfigureAwait(false);
    }
}
