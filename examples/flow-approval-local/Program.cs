using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.Options;

var definition = FlowGraphBuilder<ApprovalState>
    .Create("approval-request")
    .AddNode("review", new ApprovalReviewNode())
    .StartAt("review")
    .Build();

var runner = new InMemoryFlowRunner<ApprovalState>(Options.Create(new AppSurfaceFlowOptions()));
var waiting = await runner.RunAsync(definition, new ApprovalState("deploy-preview", "created", null));

Console.WriteLine($"{waiting.Status}: {waiting.WaitingEventName}");

if (waiting.Status != FlowRunStatus.Waiting || waiting.NodeId is null || waiting.Context is null)
{
    throw new InvalidOperationException("Expected the approval flow to wait for input before resuming.");
}

var completed = await runner.ResumeAsync(
    definition,
    waiting.NodeId,
    waiting.Context,
    new FlowResumeEvent("approval-submitted", true));

Console.WriteLine($"{completed.Status}: {completed.Context?.Status}");

internal sealed record ApprovalState(string RequestId, string Status, bool? Approved);

internal sealed class ApprovalReviewNode : IFlowNode<ApprovalState>
{
    public ValueTask<FlowNodeOutcome<ApprovalState>> ExecuteAsync(
        FlowExecutionContext<ApprovalState> context,
        CancellationToken cancellationToken = default)
    {
        if (context.ResumeEvent?.Payload is bool approved)
        {
            return ValueTask.FromResult<FlowNodeOutcome<ApprovalState>>(
                approved
                    ? FlowNodeOutcome<ApprovalState>.Complete(context.State with { Status = "approved", Approved = true })
                    : FlowNodeOutcome<ApprovalState>.Fault("approval.denied", "The approval request was denied."));
        }

        return ValueTask.FromResult<FlowNodeOutcome<ApprovalState>>(
            FlowNodeOutcome<ApprovalState>.Wait(
                "approval-submitted",
                context.State with { Status = "waiting-for-approval" },
                new FlowTimeout(TimeSpan.FromHours(24))));
    }
}
