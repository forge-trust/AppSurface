using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Owns one result's serialization, write, flush and exit decision.</summary>
/// <remarks>Delivery failure is latched: no catch or cancellation path writes a second object or diagnostic.</remarks>
internal sealed class CloudRunExecutionOutput(
    TextWriter output, TextWriter error, bool json,
    Func<CloudRunExecutionEnvelope, string>? serializer = null)
{
    internal async Task<int> DeliverAsync(CloudRunExecutionWorkflowResult result, CancellationToken cancellationToken)
    {
        var observation = cancellationToken.IsCancellationRequested ? null : result.Observation;
        var code = cancellationToken.IsCancellationRequested ? "ASDEPLOY186" : result.DiagnosticCode;
        if (code.Length != 11 || !code.StartsWith("ASDEPLOY", StringComparison.Ordinal)
            || !int.TryParse(code.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 170 or > 189)
            code = "ASDEPLOY189";
        var state = observation is not null && Enum.IsDefined(observation.State) ? observation.State : CloudRunExecutionState.Unknown;
        var matched = observation?.ConfigurationMatched == true && state != CloudRunExecutionState.Unknown;
        var exit = matched ? state switch
        {
            CloudRunExecutionState.Succeeded => 0,
            CloudRunExecutionState.Failed or CloudRunExecutionState.Cancelled => 1,
            CloudRunExecutionState.Running => 3,
            _ => 2,
        } : 2;
        var envelope = new CloudRunExecutionEnvelope(
            "1.0", observation is not null && IsLogicalId(observation.LogicalJobId) ? observation.LogicalJobId : string.Empty,
            observation is not null && CloudRunExecutionArgumentAdmission.IsCanonicalExecution(observation.ExecutionResourceName) ? observation.ExecutionResourceName : string.Empty,
            state.ToString(), matched, (observation?.ObservedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), code);
        try
        {
            // Serialize while uncommitted. Once delivery begins, even cancellation cannot replace its bytes.
            var text = json ? (serializer ?? Serialize)(envelope) : RenderText(envelope);
            if (cancellationToken.IsCancellationRequested && code != "ASDEPLOY186")
            {
                envelope = envelope with { LogicalJobId = string.Empty, ExecutionResourceName = string.Empty, State = "Unknown", ConfigurationMatched = false, DiagnosticCode = "ASDEPLOY186" };
                text = json ? (serializer ?? Serialize)(envelope) : RenderText(envelope);
            }
            var sink = json || exit is 0 or 3 && envelope.DiagnosticCode != "ASDEPLOY186" ? output : error;
            await sink.WriteLineAsync(text).ConfigureAwait(false);
            await sink.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return cancellationToken.IsCancellationRequested ? 130 : 2;
        }
        return cancellationToken.IsCancellationRequested ? 130 : exit;
    }

    private static string Serialize(CloudRunExecutionEnvelope value) => JsonSerializer.Serialize(value);
    private static bool IsLogicalId(string value) => value is { Length: > 0 and <= 63 }
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string RenderText(CloudRunExecutionEnvelope value)
    {
        var fix = value.DiagnosticCode switch
        {
            "ASDEPLOY170" => "Provide each required option once with canonical values and finite ordered durations.",
            "ASDEPLOY171" => "Regenerate and provide one complete trusted three-artifact bundle.",
            "ASDEPLOY172" or "ASDEPLOY173" => "Check the exact selected execution against the bundle's declared project, region and Job.",
            "ASDEPLOY174" or "ASDEPLOY175" or "ASDEPLOY176" => "Keep acceptance unresolved and have the maintainer inspect the supported evidence format.",
            "ASDEPLOY177" => "Inspect the captured execution configuration and select the intended deployment execution.",
            "ASDEPLOY178" => "Install the documented gcloud version and check the caller-owned PATH.",
            "ASDEPLOY179" => "Inspect caller-owned access and provider availability before choosing another observation.",
            "ASDEPLOY180" => "Stop automation and ask the maintainer to inspect the bounded tool format.",
            "ASDEPLOY181" or "ASDEPLOY182" => "Keep acceptance unresolved and explicitly choose whether to observe again.",
            "ASDEPLOY183" => "Apply the caller-owned application acceptance policy.",
            "ASDEPLOY184" or "ASDEPLOY185" => "Ask the workload owner to inspect logs and choose recovery.",
            "ASDEPLOY186" => "Local observation was cancelled; the remote execution is unaffected.",
            "ASDEPLOY187" => "Reconcile local child and pipe cleanup before another observation.",
            _ => "Stop and ask the maintainer to inspect the observation stage.",
        };
        return $"{value.DiagnosticCode}: {value.State}; configurationMatched={value.ConfigurationMatched.ToString().ToLowerInvariant()}. Fix: {fix} Recovery: caller-owned; no remote mutation. Docs: https://github.com/forge-trust/AppSurface/blob/main/Deployment/reference.md#{value.DiagnosticCode.ToLowerInvariant()}";
    }
}

/// <summary>Versioned safe serialization shape; provider free text is never a member.</summary>
internal sealed record CloudRunExecutionEnvelope(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("logicalJobId")] string LogicalJobId,
    [property: JsonPropertyName("executionResourceName")] string ExecutionResourceName,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("configurationMatched")] bool ConfigurationMatched,
    [property: JsonPropertyName("observedAt")] string ObservedAt,
    [property: JsonPropertyName("diagnosticCode")] string DiagnosticCode);
