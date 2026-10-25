using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Deployment;

namespace ForgeTrust.AppSurface.Deployment.GcpCloudRun;

/// <summary>The last supported state of the caller-selected execution.</summary>
public enum CloudRunExecutionState
{
    /// <summary>Positively observed nonterminal execution.</summary>
    Running,
    /// <summary>The matching execution completed successfully.</summary>
    Succeeded,
    /// <summary>The matching execution completed with failed tasks.</summary>
    Failed,
    /// <summary>The matching execution was cancelled remotely.</summary>
    Cancelled,
    /// <summary>Complete matching evidence was not established.</summary>
    Unknown
}

/// <summary>Requests read-only observation against one trusted published bundle.</summary>
/// <param name="Deployment">The complete three-artifact deployment expectation; hashes do not authenticate its publisher.</param>
/// <param name="LogicalJobId">Exact logical Job in the bundle.</param>
/// <param name="ExecutionResourceName">Canonical projects/project/locations/region/jobs/job/executions/execution resource; never latest.</param>
/// <param name="Timeout">Positive total observation budget, at most 4,294,967,294 milliseconds.</param>
/// <param name="PollInterval">Positive delay between sequential reads, no greater than Timeout.</param>
public sealed record CloudRunExecutionObservationRequest(
    DeploymentRenderResult Deployment,
    string LogicalJobId,
    string ExecutionResourceName,
    TimeSpan Timeout,
    TimeSpan PollInterval);

/// <summary>A safe immutable observation, without provider output, environment values or exception text.</summary>
/// <param name="LogicalJobId">Validated logical identity, or empty when invalid.</param>
/// <param name="ExecutionResourceName">Validated selected resource, or empty when invalid.</param>
/// <param name="State">Supported last state, or Unknown.</param>
/// <param name="ConfigurationMatched">Whether every required captured operational setting positively matched.</param>
/// <param name="ObservedAt">UTC evidence receipt time; failure results use the local decision time.</param>
/// <param name="DiagnosticCode">Fixed ASDEPLOY170–189 diagnostic; see the deployment reference.</param>
public sealed record CloudRunExecutionObservation(
    string LogicalJobId,
    string ExecutionResourceName,
    CloudRunExecutionState State,
    bool ConfigurationMatched,
    DateTimeOffset ObservedAt,
    string DiagnosticCode);

/// <summary>Observes one selected execution without submitting, cancelling, retrying or changing cloud resources.</summary>
public interface IGcpCloudRunExecutionObserver
{
    /// <summary>Validates the bundle and observes within one monotonic budget.</summary>
    /// <param name="request">The immutable expected bundle, logical identity and exact selected execution.</param>
    /// <param name="cancellationToken">Caller cancellation, propagated after local reader cleanup.</param>
    /// <returns>Safe evidence, or Unknown for invalid non-null requests and insufficient provider evidence.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled observation.</exception>
    Task<CloudRunExecutionObservation> ObserveAsync(CloudRunExecutionObservationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Coordinates immutable expectations, strict evidence classification and sequential bounded reads.</summary>
/// <remarks>Only the injected bounded-reader contract is used. There is no legacy unbounded-runner adapter, cache, retry policy or current-Job fallback.</remarks>
public sealed class GcpCloudRunExecutionObserver : IGcpCloudRunExecutionObserver
{
    private readonly IBoundedGcloudExecutionReader _reader;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an observer with explicit bounded I/O and time dependencies.</summary>
    /// <param name="reader">A reader honoring byte limits, cancellation and complete process/reader settlement.</param>
    /// <param name="timeProvider">Monotonic budget and UTC receipt clock.</param>
    public GcpCloudRunExecutionObserver(IBoundedGcloudExecutionReader reader, TimeProvider timeProvider)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Creates the production fixed-command observer using the system clock.</summary>
    public static GcpCloudRunExecutionObserver Create() => new(new BoundedGcloudExecutionReader(), TimeProvider.System);

    /// <inheritdoc />
    public async Task<CloudRunExecutionObservation> ObserveAsync(CloudRunExecutionObservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = _timeProvider.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        var logical = ExecutionResource.IsLogicalId(request.LogicalJobId) ? request.LogicalJobId : string.Empty;
        var identity = ExecutionResource.TryParse(request.ExecutionResourceName, out var resource) ? resource : null;
        var selected = identity?.Name ?? string.Empty;
        CloudRunExecutionObservation Failure(string code) => new(logical, selected, CloudRunExecutionState.Unknown, false, _timeProvider.GetUtcNow(), code);
        TimeSpan Remaining() => request.Timeout - _timeProvider.GetElapsedTime(start);
        if (logical.Length == 0 || identity is null || !ExecutionResource.ValidDurations(request.Timeout, request.PollInterval))
            return Failure("ASDEPLOY170");

        GcpCloudRunDeploymentPlan plan;
        GcpCloudRunMigrationParity expected;
        try
        {
            plan = GcpCloudRunBundleValidator.ValidateForObservation(request.Deployment);
            expected = plan.Expected.SingleOrDefault(item => item.LogicalId == logical)!;
            if (expected is null || !identity.Matches(plan, expected)) return Failure("ASDEPLOY172");
        }
        catch (Exception exception) when (exception is DeploymentValidationException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure("ASDEPLOY171");
        }

        string? uid = null;
        CloudRunExecutionObservation? lastRunning = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Remaining();
            if (remaining <= TimeSpan.Zero)
                return lastRunning is null ? Failure("ASDEPLOY181") : lastRunning with { DiagnosticCode = "ASDEPLOY182" };
            BoundedGcloudExecutionReadResult read;
            try
            {
                read = await _reader.ReadAsync(selected, remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Failure(exception is OperationCanceledException ? "ASDEPLOY181" : "ASDEPLOY179");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Remaining() <= TimeSpan.Zero) return Failure("ASDEPLOY181");
            if (read is null) return Failure("ASDEPLOY179");
            if (read.Outcome != BoundedGcloudExecutionReadOutcome.Complete || read.ExitCode != 0)
                return Failure(ReadFailureCode(read.Outcome));
            var bytes = read.StandardOutput;
            // The sealed result constructor enforces the inclusive byte cap and strict UTF-8 before
            // this immutable copy can exist, including for injected readers.
            var received = _timeProvider.GetUtcNow();
            var evidence = CloudRunExecutionEvidence.Classify(bytes, plan, expected, identity);
            cancellationToken.ThrowIfCancellationRequested();
            if (Remaining() <= TimeSpan.Zero) return Failure("ASDEPLOY181");
            if (evidence.Uid is not null && uid is not null && evidence.Uid != uid) return Failure("ASDEPLOY173");
            if (evidence.State == CloudRunExecutionState.Unknown) return Failure(evidence.DiagnosticCode);
            uid = evidence.Uid;
            var observation = new CloudRunExecutionObservation(logical, selected, evidence.State, true, received, evidence.DiagnosticCode);
            if (evidence.State != CloudRunExecutionState.Running) return observation;
            lastRunning = observation;
            remaining = Remaining();
            if (remaining <= TimeSpan.Zero) return lastRunning with { DiagnosticCode = "ASDEPLOY182" };
            var delay = remaining < request.PollInterval ? remaining : request.PollInterval;
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ReadFailureCode(BoundedGcloudExecutionReadOutcome outcome) => outcome switch
    {
        BoundedGcloudExecutionReadOutcome.ToolUnavailable => "ASDEPLOY178",
        BoundedGcloudExecutionReadOutcome.OutputLimit or BoundedGcloudExecutionReadOutcome.InvalidEncoding => "ASDEPLOY180",
        BoundedGcloudExecutionReadOutcome.Deadline => "ASDEPLOY181",
        BoundedGcloudExecutionReadOutcome.CleanupUnconfirmed => "ASDEPLOY187",
        _ => "ASDEPLOY179"
    };
}

/// <summary>Canonical selected identity shared by admission and classification; no alias lookup occurs.</summary>
internal sealed record ExecutionResource(string Name, string Project, string Region, string Job, string Execution)
{
    private static readonly Regex Pattern = new("^projects/(?<p>[a-z][a-z0-9-]{4,28}[a-z0-9]|[1-9][0-9]{0,18})/locations/(?<r>[a-z]+(?:-[a-z0-9]+)+[0-9])/jobs/(?<j>[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?)/executions/(?<e>[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Parses only canonical full selected resources.</summary>
    internal static bool TryParse(string? value, out ExecutionResource? resource)
    {
        resource = null;
        if (value is null || value.Length > 512) return false;
        var match = Pattern.Match(value);
        if (!match.Success) return false;
        var project = match.Groups["p"].Value;
        if (char.IsDigit(project[0]) && !long.TryParse(project, out _)) return false;
        resource = new(value, project, match.Groups["r"].Value, match.Groups["j"].Value, match.Groups["e"].Value);
        return true;
    }

    /// <summary>Checks literal scope against declared expected bindings.</summary>
    internal bool Matches(GcpCloudRunDeploymentPlan plan, GcpCloudRunMigrationParity expected) =>
        (Project == plan.Project || Project == plan.ProjectNumber) && Region == plan.Region && Job == expected.PhysicalJobName;

    /// <summary>Admits safe logical IDs without echoing invalid input.</summary>
    internal static bool IsLogicalId(string? value) => value is { Length: > 0 and <= 63 } && Regex.IsMatch(value, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Checks finite positive TimeProvider timer allowances without rounding tiny budgets up.</summary>
    internal static bool ValidDurations(TimeSpan timeout, TimeSpan interval) => timeout > TimeSpan.Zero && interval > TimeSpan.Zero && interval <= timeout && timeout.TotalMilliseconds <= 4_294_967_294d;
}
