namespace ForgeTrust.AppSurface.Durable.CloudTasks;

/// <summary>Describes the fixed Cloud Tasks queue and payload-free worker wake target.</summary>
/// <remarks>
/// This value validates syntax only; it does not prove that the queue, location, service account, permissions, or
/// effective queue routing exist. The OIDC account identifies the task's worker request identity. It is distinct from
/// the OAuth access token used by the creator. See
/// <see href="https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#reference"/>.
/// </remarks>
public sealed class CloudTasksActivationTarget
{
    /// <summary>Creates an immutable, syntactically validated Cloud Tasks target.</summary>
    /// <param name="projectId">A project ID, numeric project number, or supported legacy project identifier.</param>
    /// <param name="locationId">A lowercase location segment; no provider lookup is performed.</param>
    /// <param name="queueId">A Cloud Tasks queue ID.</param>
    /// <param name="workerEndpoint">An absolute HTTPS worker endpoint with no user info or fragment.</param>
    /// <param name="oidcServiceAccountEmail">The explicit service account used for the worker OIDC token.</param>
    /// <param name="audience">The explicit worker OIDC audience; it is not inferred from the endpoint.</param>
    /// <param name="dispatchDeadline">The worker dispatch deadline, from 15 seconds through 30 minutes.</param>
    /// <exception cref="ArgumentException">A value is required or violates a documented syntax constraint.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value exceeds a documented package bound.</exception>
    public CloudTasksActivationTarget(
        string projectId,
        string locationId,
        string queueId,
        Uri workerEndpoint,
        string oidcServiceAccountEmail,
        string audience,
        TimeSpan dispatchDeadline)
    {
        ProjectId = CloudTasksValidation.ValidateProjectId(projectId, nameof(projectId));
        LocationId = CloudTasksValidation.ValidateLocationId(locationId, nameof(locationId));
        QueueId = CloudTasksValidation.ValidateQueueId(queueId, nameof(queueId));
        WorkerEndpoint = CloudTasksValidation.ValidateEndpoint(workerEndpoint, nameof(workerEndpoint));
        OidcServiceAccountEmail = CloudTasksValidation.ValidateEmail(oidcServiceAccountEmail, nameof(oidcServiceAccountEmail));
        Audience = CloudTasksValidation.ValidateAudience(audience, nameof(audience));
        DispatchDeadline = CloudTasksValidation.ValidateDispatchDeadline(dispatchDeadline, nameof(dispatchDeadline));
        QueueName = $"projects/{ProjectId}/locations/{LocationId}/queues/{QueueId}";
    }

    /// <summary>Gets the project identifier used in the Cloud Tasks resource name.</summary>
    public string ProjectId { get; }

    /// <summary>Gets the location segment used in the Cloud Tasks resource name.</summary>
    public string LocationId { get; }

    /// <summary>Gets the queue segment used in the Cloud Tasks resource name.</summary>
    public string QueueId { get; }

    /// <summary>Gets the validated HTTPS endpoint requested for the worker.</summary>
    public Uri WorkerEndpoint { get; }

    /// <summary>Gets the explicit service-account email used for the task's OIDC token.</summary>
    public string OidcServiceAccountEmail { get; }

    /// <summary>Gets the explicit audience requested for the task's OIDC token.</summary>
    public string Audience { get; }

    /// <summary>Gets the worker dispatch deadline with whole-millisecond precision.</summary>
    public TimeSpan DispatchDeadline { get; }

    /// <summary>Gets the canonical queue resource name derived from validated target values.</summary>
    public string QueueName { get; }
}

/// <summary>Describes one caller-selected task identity to submit as an empty worker wake.</summary>
public sealed class CloudTasksActivationRequest
{
    /// <summary>Creates a request that contains no application payload or per-task transport policy.</summary>
    /// <param name="target">The non-null immutable queue and worker target.</param>
    /// <param name="taskId">The caller-selected task ID, containing only letters, digits, hyphens, and underscores.</param>
    /// <exception cref="ArgumentException">The target is null or the task ID is invalid.</exception>
    public CloudTasksActivationRequest(CloudTasksActivationTarget target, string taskId)
    {
        Target = target ?? throw CloudTasksValidation.Required(nameof(target), "a CloudTasksActivationTarget");
        TaskId = CloudTasksValidation.ValidateTaskId(taskId, nameof(taskId));
        TaskName = $"{Target.QueueName}/tasks/{TaskId}";
    }

    /// <summary>Gets the immutable target used for the submission.</summary>
    public CloudTasksActivationTarget Target { get; }

    /// <summary>Gets the validated caller-selected task ID.</summary>
    public string TaskId { get; }

    /// <summary>Gets the expected task resource name derived from the validated target and task ID.</summary>
    public string TaskName { get; }
}

/// <summary>Identifies the closed set of safe Cloud Tasks creation outcomes.</summary>
public enum CloudTasksDispatchDisposition
{
    /// <summary>The provider returned the exact expected created task name.</summary>
    Created = 0,
    /// <summary>The task name already exists; equivalence and current delivery are not established.</summary>
    AlreadyExists = 1,
    /// <summary>Evidence establishes that this invocation did not submit a task or the provider definitively rejected it.</summary>
    Rejected = 2,
    /// <summary>The task may have been accepted, but the available evidence is incomplete or ambiguous.</summary>
    Indeterminate = 3,
}

/// <summary>Contains a validated expected task name and a closed, package-owned diagnostic.</summary>
/// <remarks>A receipt reports creation evidence only. It does not prove delivery, Work selection, or Work completion.</remarks>
public sealed class CloudTasksDispatchReceipt
{
    /// <summary>Creates a receipt only for a valid task resource name and an allowed disposition/code pair.</summary>
    /// <param name="taskName">The expected validated task resource name, sourced from request intent.</param>
    /// <param name="disposition">The closed operational outcome.</param>
    /// <param name="diagnosticCode">A canonical package-owned code valid for the disposition.</param>
    /// <exception cref="ArgumentException">The task name, disposition, or diagnostic pair is invalid.</exception>
    public CloudTasksDispatchReceipt(string taskName, CloudTasksDispatchDisposition disposition, string diagnosticCode)
    {
        TaskName = CloudTasksValidation.ValidateTaskName(taskName, nameof(taskName));
        if (!Enum.IsDefined(disposition))
        {
            throw CloudTasksValidation.Syntax(nameof(disposition), "a defined CloudTasksDispatchDisposition");
        }

        if (!CloudTasksDiagnosticCatalog.IsAllowed(disposition, diagnosticCode))
        {
            throw CloudTasksValidation.Syntax(nameof(diagnosticCode), "a canonical diagnostic code allowed for the disposition");
        }

        Disposition = disposition;
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets the expected task resource name derived from caller request intent.</summary>
    public string TaskName { get; }

    /// <summary>Gets the safe dispatch disposition.</summary>
    public CloudTasksDispatchDisposition Disposition { get; }

    /// <summary>Gets the canonical package-owned diagnostic code.</summary>
    public string DiagnosticCode { get; }
}

/// <summary>Supplies only an OAuth access token used to authorize Cloud Tasks task creation.</summary>
/// <remarks>Implementations must be thread-safe singletons, honor cancellation, and avoid logging returned secrets.</remarks>
public interface ICloudTasksAccessTokenSource
{
    /// <summary>Obtains an OAuth access token cooperatively under the caller's linked submission budget.</summary>
    /// <param name="cancellationToken">Caller cancellation or the overall submission budget signal.</param>
    /// <returns>The bare token without a <c>Bearer </c> prefix.</returns>
    /// <exception cref="OperationCanceledException">The source honors a requested cancellation signal.</exception>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Dispatches one logical named, payload-free Cloud Tasks activation task.</summary>
public interface ICloudTasksActivationClient
{
    /// <summary>Submits one validated task request and returns bounded, safe creation evidence.</summary>
    /// <param name="request">The immutable caller intent.</param>
    /// <param name="cancellationToken">Caller cancellation, propagated as a sanitized cancellation exception.</param>
    /// <returns>A safe receipt; creation acceptance does not establish worker delivery or completion.</returns>
    /// <exception cref="ArgumentException">The request is null; ASCTV001 validation metadata identifies the required request.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation is observed before or during the operation.</exception>
    Task<CloudTasksDispatchReceipt> DispatchAsync(
        CloudTasksActivationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Defines the required overall budget for token acquisition plus Cloud Tasks creation.</summary>
public sealed class CloudTasksActivationClientOptions
{
    /// <summary>Creates validated immutable submission options; no implicit timeout is selected.</summary>
    /// <param name="submissionTimeout">A positive timeout within the supported .NET timer range.</param>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is nonpositive or outside the timer range.</exception>
    public CloudTasksActivationClientOptions(TimeSpan submissionTimeout)
    {
        SubmissionTimeout = CloudTasksValidation.ValidateTimeout(submissionTimeout, nameof(submissionTimeout));
    }

    /// <summary>Gets the budget covering token acquisition and provider creation together.</summary>
    public TimeSpan SubmissionTimeout { get; }
}

/// <summary>Defines the required bound for one Cloud Run metadata-server token request.</summary>
public sealed class CloudRunMetadataAccessTokenOptions
{
    /// <summary>Creates validated immutable metadata options; no implicit timeout is selected.</summary>
    /// <param name="requestTimeout">A positive timeout within the supported .NET timer range.</param>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is nonpositive or outside the timer range.</exception>
    public CloudRunMetadataAccessTokenOptions(TimeSpan requestTimeout)
    {
        RequestTimeout = CloudTasksValidation.ValidateTimeout(requestTimeout, nameof(requestTimeout));
    }

    /// <summary>Gets the configured upper bound for a standalone metadata request.</summary>
    public TimeSpan RequestTimeout { get; }
}

/// <summary>Maps the closed package diagnostic catalog to its sole valid dispatch disposition.</summary>
internal static class CloudTasksDiagnosticCatalog
{
    /// <summary>Returns whether the exact ordinal diagnostic is valid for the specified disposition.</summary>
    internal static bool IsAllowed(CloudTasksDispatchDisposition disposition, string? code) => (disposition, code) switch
    {
        (CloudTasksDispatchDisposition.Created, "ASCT001") => true,
        (CloudTasksDispatchDisposition.AlreadyExists, "ASCT002") => true,
        (CloudTasksDispatchDisposition.Rejected, "ASCT101" or "ASCT102" or "ASCT103"
            or "ASCT201" or "ASCT202" or "ASCT203" or "ASCT204" or "ASCT205" or "ASCT206" or "ASCT207") => true,
        (CloudTasksDispatchDisposition.Indeterminate, "ASCT301" or "ASCT302" or "ASCT303" or "ASCT304"
            or "ASCT305" or "ASCT306" or "ASCT307") => true,
        _ => false,
    };
}
