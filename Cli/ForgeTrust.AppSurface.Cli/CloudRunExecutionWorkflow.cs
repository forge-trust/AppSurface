using System.Text;
using ForgeTrust.AppSurface.Deployment;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Loads one finite published bundle and passes only the remaining monotonic allowance to observation.</summary>
/// <remarks>Bundle files are opened through the shared coverage artifact reader, which rejects non-regular files and does not follow links. Hashes establish consistency, not publisher authenticity.</remarks>
internal sealed class CloudRunExecutionWorkflow
{
    private const int MaximumArtifactBytes = 4 * 1024 * 1024;
    private static readonly string[] ArtifactNames = [
        "deployment-intent.v1.json", "gcp-cloud-run-migration.tf.json", "gcp-cloud-run-migration.plan.json"];
    private readonly IGcpCloudRunExecutionObserver _observer;
    private readonly TimeProvider _timeProvider;

    public CloudRunExecutionWorkflow(IGcpCloudRunExecutionObserver observer, TimeProvider timeProvider)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal async Task<CloudRunExecutionWorkflowResult> RunAsync(
        string? bundleDirectory, string? logicalJobId, string? execution, string timeout, string pollInterval, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        if (!CloudRunExecutionArgumentAdmission.TryValidate(bundleDirectory, logicalJobId, execution, timeout, pollInterval, out var timeoutValue, out var pollValue))
        {
            return CloudRunExecutionWorkflowResult.Invalid();
        }

        var observing = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            void CheckBudget()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_timeProvider.GetElapsedTime(started) >= timeoutValue) throw new TimeoutException();
            }
            var render = await LoadBundleAsync(bundleDirectory!, CheckBudget, cancellationToken).ConfigureAwait(false);
            var remaining = timeoutValue - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return CloudRunExecutionWorkflowResult.Unknown();
            var request = new CloudRunExecutionObservationRequest(render, logicalJobId!, execution!, remaining, pollValue > remaining ? remaining : pollValue);
            observing = true;
            var observation = await _observer.ObserveAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_timeProvider.GetElapsedTime(started) >= timeoutValue && observation.State != CloudRunExecutionState.Running)
                return CloudRunExecutionWorkflowResult.Unknown();
            return CloudRunExecutionWorkflowResult.From(observation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentValidationException exception)
        {
            return new CloudRunExecutionWorkflowResult(null, exception.Diagnostic.Code);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CloudRunExecutionWorkflowResult.Unknown();
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CloudRunExecutionWorkflowResult.Unknown();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return observing ? new(null, "ASDEPLOY189") : CloudRunExecutionWorkflowResult.LocalFailure();
        }
    }

    private static async Task<DeploymentRenderResult> LoadBundleAsync(string directory, Action checkBudget, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(directory);
        if (!CloudRunExecutionArgumentAdmission.IsPathSafe(full)) throw new InvalidDataException();
        var info = new DirectoryInfo(full);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
        checkBudget();
        var entries = new List<FileSystemInfo>();
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            checkBudget();
            if (entries.Count == 4) throw new InvalidDataException();
            entries.Add(entry);
        }
        var allowed = new HashSet<string>(ArtifactNames, StringComparer.Ordinal) { DeploymentArtifactBundleWriter.OwnershipMarkerFileName };
        if (entries.Any(e => !allowed.Contains(e.Name)) || entries.Any(e => e.Attributes.HasFlag(FileAttributes.Directory))) throw new InvalidDataException();
        var marker = entries.SingleOrDefault(e => e.Name == DeploymentArtifactBundleWriter.OwnershipMarkerFileName);
        if (marker is not null && marker.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
        var artifacts = new List<DeploymentArtifact>();
        foreach (var name in ArtifactNames)
        {
            checkBudget();
            var file = entries.SingleOrDefault(e => e.Name == name);
            if (file is null || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
            await using var stream = CoverageRunArtifactReader.OpenRegularFile(
                Path.GetPathRoot(full)!, full, file.FullName);
            using var memory = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                checkBudget();
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > MaximumArtifactBytes) throw new InvalidDataException();
                memory.Write(buffer, 0, read);
            }
            artifacts.Add(DeploymentArtifact.Create(name, memory.ToArray()));
        }

        var target = "gcp-cloud-run";
        if (marker is not null)
        {
            checkBudget();
            await using var stream = CoverageRunArtifactReader.OpenRegularFile(
                Path.GetPathRoot(full)!, full, marker.FullName);
            var bytes = new byte[128];
            var count = 0;
            while (count < bytes.Length)
            {
                checkBudget();
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count == bytes.Length) throw new InvalidDataException();
            target = new UTF8Encoding(false, true).GetString(bytes, 0, count).Trim();
        }
        if (!string.Equals(target, "gcp-cloud-run", StringComparison.Ordinal)) throw new InvalidDataException();
        checkBudget();
        return new DeploymentRenderResult(target, artifacts.AsReadOnly());
    }
}

internal sealed record CloudRunExecutionWorkflowResult(CloudRunExecutionObservation? Observation, string DiagnosticCode)
{
    internal static CloudRunExecutionWorkflowResult From(CloudRunExecutionObservation observation) => new(observation, observation.DiagnosticCode);
    internal static CloudRunExecutionWorkflowResult Invalid() => new(null, "ASDEPLOY170");
    internal static CloudRunExecutionWorkflowResult Unknown() => new(null, "ASDEPLOY181");
    internal static CloudRunExecutionWorkflowResult LocalFailure() => new(null, "ASDEPLOY171");
}
