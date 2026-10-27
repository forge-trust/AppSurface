using System.Diagnostics;
using Grpc.Core;

namespace ForgeTrust.AppSurface.Durable.CloudTasks;

/// <summary>Owns one explicitly credentialed creation invocation and classifies only safe evidence.</summary>
internal sealed class CloudTasksActivationClient(
    ICloudTasksAccessTokenSource tokenSource,
    CloudTasksActivationClientOptions options,
    ICloudTasksRpcTransport transport,
    TimeProvider timeProvider) : ICloudTasksActivationClient
{
    /// <inheritdoc />
    public async Task<CloudTasksDispatchReceipt> DispatchAsync(CloudTasksActivationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw CloudTasksValidation.Required(nameof(request), "validated activation request");
        if (cancellationToken.IsCancellationRequested) throw Canceled(cancellationToken);
        var budget = new SubmissionBudget(options.SubmissionTimeout, timeProvider);
        using var timeout = new CancellationTokenSource(options.SubmissionTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        string token;
        try
        {
            token = tokenSource is CloudRunMetadataAccessTokenSource metadata
                ? await metadata.GetAccessTokenAsync(budget, linked.Token).ConfigureAwait(false)
                : await tokenSource.GetAccessTokenAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!CloudTasksDiagnostics.IsFatal(exception))
        {
            if (cancellationToken.IsCancellationRequested) throw Canceled(cancellationToken);
            if (budget.IsExpired || timeout.IsCancellationRequested) return Receipt("ASCT103");
            if (exception is TokenAcquisitionException metadataFailure)
                return Receipt(metadataFailure.InvalidToken ? "ASCT102" : "ASCT101");
            CloudTasksDiagnostics.Unexpected("token", "source", "ASCT101");
            return Receipt("ASCT101");
        }
        if (cancellationToken.IsCancellationRequested) throw Canceled(cancellationToken);
        if (budget.IsExpired || timeout.IsCancellationRequested) return Receipt("ASCT103");
        if (!CloudTasksValidation.IsValidToken(token)) return Receipt("ASCT102");
        try
        {
            // Exactly one SDK invocation. HTTP/2 protocol-declared unprocessed replays are owned by .NET.
            var returnedName = await transport.CreateAsync(request, token, budget.Remaining, linked.Token).ConfigureAwait(false);
            return Receipt(string.Equals(returnedName, request.TaskName, StringComparison.Ordinal) ? "ASCT001" : "ASCT306");
        }
        catch (Exception exception) when (!CloudTasksDiagnostics.IsFatal(exception))
        {
            // A definitive provider rejection already observed wins over a cancellation racing after its return.
            if (exception is RpcException acknowledged && DefinitiveCode(acknowledged.StatusCode) is { } code)
                return Receipt(code);
            if (exception is OperationCanceledException || exception is RpcException { StatusCode: StatusCode.Cancelled or StatusCode.DeadlineExceeded })
            {
                if (cancellationToken.IsCancellationRequested) throw Canceled(cancellationToken);
                if (budget.IsExpired || timeout.IsCancellationRequested) return Receipt("ASCT301");
            }
            if (exception is CloudTasksResponseLimitException) return Receipt("ASCT304");
            if (exception is RpcException rpc)
            {
                var mapped = rpc.StatusCode switch
                {
                    StatusCode.DeadlineExceeded => "ASCT301",
                    StatusCode.Unavailable => "ASCT302",
                    StatusCode.ResourceExhausted => "ASCT303",
                    StatusCode.Cancelled => "ASCT307",
                    _ => "ASCT305"
                };
                if (mapped == "ASCT305") CloudTasksDiagnostics.Unexpected("create", "rpc", mapped);
                return Receipt(mapped);
            }
            if (exception is OperationCanceledException) return Receipt("ASCT307");
            CloudTasksDiagnostics.Unexpected("create", "transport", "ASCT305");
            return Receipt(exception is HttpRequestException ? "ASCT302" : "ASCT305");
        }

        CloudTasksDispatchReceipt Receipt(string code) => new(request.TaskName, code switch
        {
            "ASCT001" => CloudTasksDispatchDisposition.Created,
            "ASCT002" => CloudTasksDispatchDisposition.AlreadyExists,
            "ASCT101" or "ASCT102" or "ASCT103" or "ASCT201" or "ASCT202" or "ASCT203" or "ASCT204" or "ASCT205" or "ASCT206" or "ASCT207" => CloudTasksDispatchDisposition.Rejected,
            _ => CloudTasksDispatchDisposition.Indeterminate
        }, code);
    }

    private static OperationCanceledException Canceled(CancellationToken token) => new("Cloud Tasks submission canceled by caller.", token);

    private static string? DefinitiveCode(StatusCode status) => status switch
    {
        StatusCode.AlreadyExists => "ASCT002",
        StatusCode.InvalidArgument => "ASCT201",
        StatusCode.Unauthenticated => "ASCT202",
        StatusCode.PermissionDenied => "ASCT203",
        StatusCode.NotFound => "ASCT204",
        StatusCode.FailedPrecondition => "ASCT205",
        StatusCode.OutOfRange => "ASCT206",
        StatusCode.Unimplemented => "ASCT207",
        _ => null
    };
}

/// <summary>Internal SDK boundary: no provider types cross the public package API.</summary>
internal interface ICloudTasksRpcTransport
{
    /// <summary>Creates one empty named wake, returning only the observed provider task name.</summary>
    Task<string?> CreateAsync(CloudTasksActivationRequest request, string token, TimeSpan remaining, CancellationToken cancellationToken);
}

/// <summary>Closed local response-bound signal without provider data.</summary>
internal sealed class CloudTasksResponseLimitException : Exception
{
    /// <summary>Constructs the safe, locally detected bound failure.</summary>
    internal CloudTasksResponseLimitException() : base("Cloud Tasks response exceeded a package bound.") { }
}

/// <summary>Observational, redacted diagnostics; ordinary listener failures cannot affect dispatch.</summary>
internal static class CloudTasksDiagnostics
{
    /// <summary>Name for standard .NET Activity listeners.</summary>
    internal const string SourceName = "ForgeTrust.AppSurface.Durable.CloudTasks";
    private static readonly ActivitySource Source = new(SourceName, typeof(CloudTasksDiagnostics).Assembly.GetName().Version?.ToString());

    /// <summary>Identifies process-corrupting errors which must never become receipts.</summary>
    internal static bool IsFatal(Exception exception) => exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    /// <summary>Emits only package-owned stage/cause/code values, linked to the current caller activity.</summary>
    internal static void Unexpected(string operation, string cause, string code)
    {
        try
        {
            if (!Source.HasListeners()) return;
            using var activity = Source.StartActivity("cloudtasks.dispatch.failure", ActivityKind.Internal);
            activity?.AddEvent(new ActivityEvent("cloudtasks.failure", tags: new ActivityTagsCollection
            {
                ["operation"] = operation,
                ["cause"] = cause,
                ["stage"] = operation == "token" ? "acquisition" : "submission",
                ["code"] = code,
                ["package.version"] = typeof(CloudTasksDiagnostics).Assembly.GetName().Version?.ToString(),
                ["phase.sequence"] = operation == "token" ? "validate>token>failure" : "validate>token>create>failure"
            }));
        }
        catch (Exception exception) when (!IsFatal(exception)) { }
    }
}
