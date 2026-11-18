using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Runs a versioned coverage fan-in request and renders one bounded terminal result.</summary>
/// <remarks>Request parsing and result-path custody complete before the public fan-in facade is invoked.</remarks>
internal abstract partial class CoverageFanInCommandBase(string operation, bool requiresResult,
    ICoverageFanInResultDeliveryFactory? deliveryFactory = null) : ICommand
{
    private const int MaximumJsonBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ICoverageFanInResultDeliveryFactory _deliveryFactory = deliveryFactory ?? SafeCoverageFanInResultDeliveryFactory.Instance;

    /// <summary>Gets or sets the required strict schema-version 2 request document.</summary>
    [CommandOption("request", Description = "Required UTF-8 JSON envelope: schemaVersion:2 (legacy) or schemaVersion:3 (report applicability). The schema version must match policy.contractVersion; v2 omits that policy field, while v3 requires contractVersion=3 and reportProfileId=coverlet-collector-cobertura-v1. Exact camelCase fields, string enums, duplicate/unknown rejection, lossless values, and the 1 MiB bound apply. The file and its ancestors must be regular, link-free filesystem objects.")]
    public string? Request { get; set; }

    /// <summary>Gets or sets the new result-file destination for this operation.</summary>
    [CommandOption("result", Description = "Write the bounded terminal result using the request's matching schema version (2 or 3) atomically to this new file in an existing link-free directory. Existing files are never replaced or adopted. Required for capture, compose, and accept; optional for read-only verify.")]
    public string? Result { get; set; }

    /// <summary>Gets or sets whether one terminal JSON document is written to standard output.</summary>
    [CommandOption("json", Description = "Write exactly one bounded terminal JSON document to stdout; progress and diagnostics remain on stderr.")]
    public bool Json { get; set; }

    /// <summary>Gets whether capture selected collection-and-capture.</summary>
    protected virtual bool CollectRequested => false;

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IConsole console) => ExecuteAsync(console, console.RegisterCancellationHandler());

    /// <summary>Executes one strictly parsed fan-in operation using the public Coverage facade.</summary>
    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);

        ParsedCoverageFanInRequest parsed;
        var requestContractVersion = 2;
        ICoverageFanInResultDelivery? delivery = null;
        try
        {
            if (string.IsNullOrWhiteSpace(Request)) throw new CoverageFanInCliInputException();
            if (requiresResult && string.IsNullOrWhiteSpace(Result)) throw new CoverageFanInCliInputException();

            var bytes = ReadBoundedRequest(Request);
            requestContractVersion = CoverageFanInRequestReader.TryReadEnvelopeVersion(bytes);
            parsed = CoverageFanInRequestReader.Parse(bytes);
            requestContractVersion = parsed.ContractVersion;
            var selectedOperation = CollectRequested ? "collect" : operation;
            if (!string.Equals(parsed.Operation, selectedOperation, StringComparison.Ordinal)
                || (operation == "capture" && (CollectRequested != (parsed.Operation == "collect"))))
                throw new CoverageFanInCliInputException();
            CoverageFanInRequestReader.ValidateOperationRequest(parsed);
            CoverageFanInRequestReader.ValidateCanonicalPolicy(parsed);

            if (!string.IsNullOrWhiteSpace(Result)) delivery = _deliveryFactory.Prepare(Result);
        }
        catch (CoverageFanInCliInputException exception)
        {
            await WriteInputFailureAsync(console, requestContractVersion, exception.Code, exception.Cause,
                exception.Field, exception.Recovery ?? InputRecovery(requestContractVersion)).ConfigureAwait(false);
            throw new CommandException("Coverage fan-in input is invalid; see the bounded diagnostic output.", 2);
        }
        catch (Exception exception) when (IsInputFailure(exception))
        {
            await WriteInputFailureAsync(console, requestContractVersion, "ASCOVF001", "invalid-policy", "request",
                InputRecovery(requestContractVersion)).ConfigureAwait(false);
            throw new CommandException("Coverage fan-in input is invalid; see the bounded diagnostic output.", 2);
        }

        using (delivery)
        {
            CoverageFanInResult? result = null;
            string status;
            string? operationId = null;
            CoverageOperationOutcome? operationOutcome = null;
            CoverageOperationEffects? effects = null;
            var effectsKnown = false;
            var exitCode = 0;
            var diagnostics = new List<CoverageFanInCliDiagnostic>();
            var deliveryFailed = false;
            var suppressResult = false;
            var requestContext = CoverageFanInCliCodec.ContextFor(parsed);

            if (cancellationToken.IsCancellationRequested)
            {
                status = "cancelled";
                operationOutcome = CoverageOperationOutcome.Cancelled;
                effectsKnown = true;
                exitCode = 130;
                diagnostics.Add(CoverageFanInCliCodec.Diagnostic(parsed.ContractVersion, "ASCOVF004", "cancelled", "operation",
                    "Retry once after confirming that no operation output was created.", "cancelled", requestContext));
            }
            else
            {
                ValueTask<CoverageFanInResult> pending;
                try
                {
                    pending = InvokeAsync(parsed, console.Error, cancellationToken);
                }
                catch (Exception exception) when (IsInputFailure(exception))
                {
                    await WriteInputFailureAsync(console, parsed.ContractVersion, "ASCOVF001", "invalid-policy", "request",
                        "Correct the typed policy or operation selectors; validation failed before fan-in execution.").ConfigureAwait(false);
                    throw new CommandException("Coverage fan-in input is invalid; see the bounded diagnostic output.", 2);
                }

                try
                {
                    result = await pending.ConfigureAwait(false);
                    operationId = result.OperationId;
                    operationOutcome = result.Outcome;
                    effects = result.Effects;
                    effectsKnown = true;
                    if (parsed.ContractVersion == 3 && !CoverageFanInCliResult.IsValidForV3(result))
                    {
                        status = "failed";
                        exitCode = 1;
                        suppressResult = true;
                        diagnostics.Add(CoverageFanInCliCodec.Diagnostic(parsed.ContractVersion, "ASCOVF003", "applicability",
                            "result", "Do not consume this result. Recompute applicability from the retained reports and pinned diff, then report the operation ID to the maintainer.",
                            "applicability-mismatch", requestContext));
                    }
                    else
                    {
                        status = StatusFor(result.Outcome);
                        exitCode = ExitCodeFor(result.Outcome);
                    }
                }
                catch (OperationCanceledException)
                {
                    status = "cancelled";
                    operationOutcome = CoverageOperationOutcome.Cancelled;
                    exitCode = 130;
                    diagnostics.Add(CoverageFanInCliCodec.Diagnostic(parsed.ContractVersion, "ASCOVF004", "cancelled", "operation",
                        "Inspect the destination and reconcile any committed generation before retrying.", "cancelled", requestContext));
                }
                catch (TimeoutException)
                {
                    status = "timedOut";
                    operationOutcome = CoverageOperationOutcome.TimedOut;
                    exitCode = 124;
                    diagnostics.Add(CoverageFanInCliCodec.Diagnostic(parsed.ContractVersion, "ASCOVF005", "timedOut", "operation",
                        "Inspect the destination and reconcile any committed generation before retrying.", "deadline-exceeded", requestContext));
                }
                catch (Exception exception)
                {
                    status = "failed";
                    exitCode = 1;
                    diagnostics.Add(CoverageFanInCliCodec.Diagnostic(parsed.ContractVersion, "ASCOVF003", "execution", "operation",
                        "Preserve any retained generation, reconcile possible partial effects, and report the operation to the maintainer before one corrected retry.",
                        "unexpected-failure", CoverageFanInCliCodec.ContextFor(parsed, exception.GetType()),
                        CoverageFanInCliCodec.TraceFor(exception)));
                }
            }

            var deliveryInfo = DeliveryInfo(delivery, delivery is null ? "notRequested" : "committed", predictCommit: true);
            var envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown,
                suppressResult ? null : result, deliveryInfo, diagnostics, parsed.ContractVersion);
            var document = SerializeBounded(envelope, out var compacted);
            if (compacted)
            {
                status = "incomplete";
                if (exitCode == 0) exitCode = 1;
                diagnostics.Add(CoverageFanInCliCodec.Diagnostic(parsed.ContractVersion, "ASCOVF006", "result", "result",
                    "The result exceeded the 1 MiB terminal limit; inspect the retained operation artifacts directly.",
                    "limit-exceeded", requestContext));
                envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown, null,
                    DeliveryInfo(delivery, delivery is null ? "notRequested" : "committed", predictCommit: true), diagnostics,
                    parsed.ContractVersion);
                document = SerializeBounded(envelope, out _);
            }

            if (delivery is not null)
            {
                try
                {
                    delivery.Write(document);
                }
                catch (Exception)
                {
                    deliveryFailed = true;
                    status = "deliveryFailed";
                    if (exitCode == 0) exitCode = 1;
                    diagnostics.Add(CoverageFanInCliCodec.Diagnostic(parsed.ContractVersion, "ASCOVF002", "delivery", "result",
                        "Reconcile the operation using its operation ID and committed-generation status before retrying result delivery.",
                        "delivery-failed", requestContext));
                    envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown,
                        suppressResult ? null : result, DeliveryInfo(delivery, "failed"), diagnostics, parsed.ContractVersion);
                    document = SerializeBounded(envelope, out var deliveryCompacted);
                    if (deliveryCompacted)
                    {
                        envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown, null,
                            DeliveryInfo(delivery, "failed"), diagnostics, parsed.ContractVersion);
                        document = SerializeBounded(envelope, out _);
                    }
                }
            }

            if (result is not null)
                foreach (var diagnostic in result.Diagnostics.Take(16))
                    await console.Error.WriteLineAsync(CoverageFanInCliCodec.FormatDiagnostic(diagnostic, parsed.ContractVersion)).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics)
                await console.Error.WriteLineAsync(CoverageFanInCliCodec.FormatDiagnostic(diagnostic, parsed.ContractVersion)).ConfigureAwait(false);

            if (Json)
            {
                await console.Output.WriteLineAsync(System.Text.Encoding.UTF8.GetString(document)).ConfigureAwait(false);
            }
            else
            {
                await WriteHumanResultAsync(console, envelope, deliveryFailed).ConfigureAwait(false);
            }

            if (exitCode != 0)
                throw new CommandException("Coverage fan-in did not complete successfully; see the bounded result and diagnostics.", exitCode);
        }
    }

    /// <summary>Dispatch seam for adapter fault tests; production command types use the supported facade below.</summary>
    internal virtual ValueTask<CoverageFanInResult> InvokeAsync(ParsedCoverageFanInRequest request, TextWriter diagnostics,
        CancellationToken cancellationToken) => request.Operation switch
        {
            "capture" => CoverageFanIn.CaptureAsync((CoverageCaptureRequest)request.Request,
                cancellationToken, diagnostics, diagnostics),
            "collect" => CoverageFanIn.CollectAndCaptureAsync((CoverageCollectRequest)request.Request,
                cancellationToken, diagnostics, diagnostics),
            "compose" => CoverageFanIn.ComposeAsync((CoverageComposeRequest)request.Request,
                cancellationToken, diagnostics, diagnostics),
            "accept" => CoverageFanIn.AcceptAsync((CoverageAcceptRequest)request.Request,
                cancellationToken, diagnostics, diagnostics),
            "verify" => CoverageFanIn.VerifyAsync((CoverageVerifyRequest)request.Request,
                cancellationToken, diagnostics, diagnostics),
            _ => throw new CoverageFanInCliInputException(),
        };

    private static byte[] ReadBoundedRequest(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            CoverageRunOutputLease.ValidateArtifactComponent(fileName);
            var parentPath = Path.GetDirectoryName(fullPath) ?? throw new CoverageFanInCliInputException();
            using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(parentPath,
                createMissing: false, requirePrivate: false) ?? throw new CoverageFanInCliInputException();
            using var input = parent.OpenArtifactFile(fileName, createNew: false, requirePrivate: false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var allowed = MaximumJsonBytes + 1 - (int)buffer.Length;
                if (allowed <= 0) throw new CoverageFanInCliInputException();
                var count = input.Read(chunk, 0, Math.Min(chunk.Length, allowed));
                if (count == 0) break;
                buffer.Write(chunk, 0, count);
                if (buffer.Length > MaximumJsonBytes) throw new CoverageFanInCliInputException();
            }
            return buffer.ToArray();
        }
        catch (CoverageFanInCliInputException) { throw; }
        catch (Exception exception) when (IsInputFailure(exception)) { throw new CoverageFanInCliInputException(); }
    }

    private static bool IsInputFailure(Exception exception) => exception is ArgumentException or IOException
        or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException;

    private static string InputRecovery(int contractVersion) => contractVersion == 3
        ? "Use schemaVersion=3 with policy.contractVersion=3 and reportProfileId=coverlet-collector-cobertura-v1; correct the request before execution."
        : "Supply a valid schemaVersion 2 request and a fresh result path in an existing link-free directory.";

    private static string StatusFor(CoverageOperationOutcome outcome) => outcome switch
    {
        CoverageOperationOutcome.Succeeded => "succeeded",
        CoverageOperationOutcome.Rejected => "rejected",
        CoverageOperationOutcome.Cancelled => "cancelled",
        CoverageOperationOutcome.TimedOut => "timedOut",
        CoverageOperationOutcome.Incomplete => "incomplete",
        _ => "failed",
    };

    private static int ExitCodeFor(CoverageOperationOutcome outcome) => outcome switch
    {
        CoverageOperationOutcome.Succeeded => 0,
        CoverageOperationOutcome.Cancelled => 130,
        CoverageOperationOutcome.TimedOut => 124,
        _ => 1,
    };

    private async ValueTask WriteInputFailureAsync(IConsole console, int contractVersion, string code, string cause,
        string field, string recovery)
    {
        var diagnostic = CoverageFanInCliCodec.Diagnostic(contractVersion, code, "input", field, recovery,
            cause, CoverageFanInCliCodec.ContextFor(contractVersion));
        var envelope = CreateEnvelope("invalidInput", null, null, null, true, null,
            new(false, "notStarted", false, false), [diagnostic], contractVersion);
        if (Json)
        {
            var bytes = SerializeBounded(envelope, out _);
            await console.Output.WriteLineAsync(System.Text.Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
        }
        else
        {
            await console.Output.WriteLineAsync($"coverage fan-in {operation}: invalid input").ConfigureAwait(false);
            await console.Error.WriteLineAsync(CoverageFanInCliCodec.FormatDiagnostic(diagnostic, contractVersion)).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteHumanResultAsync(IConsole console, CoverageFanInCliEnvelope envelope, bool deliveryFailed)
    {
        await console.Output.WriteLineAsync($"coverage fan-in {envelope.Operation}: {envelope.Status}").ConfigureAwait(false);
        if (envelope.OperationOutcome is { } outcome)
        {
            var committed = envelope.Effects?.GenerationCommitted switch { true => "yes", false => "no", null => "unknown" };
            await console.Output.WriteLineAsync($"  operation outcome: {StatusFor(outcome)}; committed generation: {committed}").ConfigureAwait(false);
        }
        if (envelope.Result?.Accepted == true)
            await console.Output.WriteLineAsync("  accepted generation: independently verified").ConfigureAwait(false);
        if (envelope.SchemaVersion == 3 && envelope.Result?.PatchApplicability is { } applicability)
        {
            await WriteMetricAsync(console, "primary line", applicability.Primary.Line).ConfigureAwait(false);
            await WriteMetricAsync(console, "primary branch", applicability.Primary.Branch).ConfigureAwait(false);
            await WriteMetricAsync(console, "composed line", applicability.Composed.Line).ConfigureAwait(false);
            await WriteMetricAsync(console, "composed branch", applicability.Composed.Branch).ConfigureAwait(false);
            if (applicability.Primary.Line.Outcome == CoveragePatchApplicabilityOutcome.NotApplicable
                || applicability.Primary.Branch.Outcome == CoveragePatchApplicabilityOutcome.NotApplicable
                || applicability.Composed.Line.Outcome == CoveragePatchApplicabilityOutcome.NotApplicable
                || applicability.Composed.Branch.Outcome == CoveragePatchApplicabilityOutcome.NotApplicable)
                await console.Output.WriteLineAsync("  trust: no changed measurements were reported for an N/A dimension under the accepted collector and policy; this does not establish source completeness.").ConfigureAwait(false);
        }
        if (deliveryFailed)
            await console.Error.WriteLineAsync("  result delivery failed; reconcile the operation before retrying.").ConfigureAwait(false);
    }

    private static async ValueTask WriteMetricAsync(IConsole console, string label, CoveragePatchMetricResult metric)
    {
        if (metric.Outcome == CoveragePatchApplicabilityOutcome.NotApplicable)
        {
            await console.Output.WriteLineAsync($"  {label}: N/A (no reported patch points; changed lines {metric.ChangedLines})").ConfigureAwait(false);
            return;
        }
        if (metric.Outcome == CoveragePatchApplicabilityOutcome.Unverified)
        {
            await console.Output.WriteLineAsync($"  {label}: unverified ({metric.Reason})").ConfigureAwait(false);
            return;
        }
        var percent = metric.Percent is { } value ? value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "%" : "unknown";
        await console.Output.WriteLineAsync($"  {label}: measured {metric.Covered}/{metric.Measurable} reported points; changed lines {metric.ChangedLines}; {percent}").ConfigureAwait(false);
    }

    private CoverageFanInCliEnvelope CreateEnvelope(string status, string? operationId,
        CoverageOperationOutcome? operationOutcome, CoverageOperationEffects? effects, bool effectsKnown,
        CoverageFanInResult? result, CoverageFanInCliDelivery delivery, IReadOnlyList<CoverageFanInCliDiagnostic> diagnostics,
        int contractVersion)
        => new(contractVersion, result?.Operation ?? operation, status, operationId, operationOutcome, effects, effectsKnown,
            result is null ? null : CoverageFanInCliResult.From(result, contractVersion), delivery, diagnostics.Take(16).ToArray());

    private static CoverageFanInCliDelivery DeliveryInfo(ICoverageFanInResultDelivery? delivery, string status, bool predictCommit = false)
        => delivery is null ? new(false, status, false, null)
            : predictCommit ? new(true, status, true, true)
            : new(true, status, delivery.ResultFileCreated, delivery.ResultFileCommitted);

    private static byte[] SerializeBounded(CoverageFanInCliEnvelope envelope, out bool compacted)
    {
        var bytes = CoverageFanInCliCodec.Serialize(envelope, JsonOptions);
        if (bytes.Length <= MaximumJsonBytes)
        {
            compacted = false;
            return bytes;
        }

        compacted = true;
        var diagnostic = new CoverageFanInCliDiagnostic("ASCOVF006", "result", "result",
            "The result exceeded the 1 MiB terminal limit; inspect the retained operation artifacts directly.");
        var summary = envelope with { Result = null, Diagnostics = [diagnostic] };
        bytes = CoverageFanInCliCodec.Serialize(summary, JsonOptions);
        if (bytes.Length > MaximumJsonBytes)
            throw new InvalidOperationException("Bounded coverage fan-in result could not be encoded.");
        return bytes;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            MaxDepth = 64,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            WriteIndented = true,
        };
        options.Converters.Add(new StrictCoverageEnumConverterFactory());
        return options;
    }
}

[Command("coverage fan-in capture", Description = "Capture validated producer evidence (fresh output is the default); requires a clean Git source and complete primary or declared supplementary producer artifacts. Use homogeneous schemaVersion/policy contractVersion 2 for legacy behavior or 3 with reportProfileId=coverlet-collector-cobertura-v1 for report-based applicability. Next run compose with the exact policy supplement set. --collect runs the complete declared inventory and requires the .NET SDK, test projects, and collector prerequisites; it defaults to one worker with restore and build enabled.")]
internal sealed partial class CoverageFanInCaptureCommand : CoverageFanInCommandBase
{
    public CoverageFanInCaptureCommand() : base("capture", requiresResult: true) { }
    internal CoverageFanInCaptureCommand(ICoverageFanInResultDeliveryFactory deliveryFactory)
        : base("capture", requiresResult: true, deliveryFactory) { }

    /// <summary>Gets or sets whether capture invokes the complete collection-and-capture operation.</summary>
    [CommandOption("collect", Description = "Select operation=collect and invoke the same public collection-and-capture facade. The request must be a typed CoverageCollectRequest; it cannot be combined with operation=capture.")]
    public bool Collect { get; set; }

    protected override bool CollectRequested => Collect;
}

[Command("coverage fan-in compose", Description = "Compose one primary capture and the exact declared supplementary captures using the requested fresh output mode. Requires complete v2 captures for legacy policies or complete v3 captures for report-applicability policies; versions cannot be mixed. Source/run identity and a clean Git source must match. Next run accept; composition alone does not accept coverage.")]
internal sealed partial class CoverageFanInComposeCommand : CoverageFanInCommandBase
{
    public CoverageFanInComposeCommand() : base("compose", requiresResult: true) { }
    internal CoverageFanInComposeCommand(ICoverageFanInResultDeliveryFactory deliveryFactory)
        : base("compose", requiresResult: true, deliveryFactory) { }
}

[Command("coverage fan-in accept", Description = "Run the declared aggregate gate, requested patch gate, and independent verification before accepting a generation. Requires primary and supplementary captures, a composed report, explicit thresholds and diff policy, and a clean Git source. V3 PatchAccepted includes valid notApplicable dimensions; inspect all four patch applicability outcomes and the report-based trust boundary instead of inferring measured coverage from PatchAccepted. Next run verify against the accepted generation.")]
internal sealed partial class CoverageFanInAcceptCommand : CoverageFanInCommandBase
{
    public CoverageFanInAcceptCommand() : base("accept", requiresResult: true) { }
    internal CoverageFanInAcceptCommand(ICoverageFanInResultDeliveryFactory deliveryFactory)
        : base("accept", requiresResult: true, deliveryFactory) { }
}

[Command("coverage fan-in verify", Description = "Read-only verification of an existing accepted generation; requires the matching primary/supplementary captures, composed output, same-version policy, source identity, and acceptance directory. V3 verification preserves all four measured/notApplicable/unverified dimensions; PatchAccepted alone does not imply measured coverage. Creates no coordination or report files. Next consume the generation only when verified=true and its applicability matches your requirement; --result is optional and must be fresh if supplied.")]
internal sealed partial class CoverageFanInVerifyCommand : CoverageFanInCommandBase
{
    public CoverageFanInVerifyCommand() : base("verify", requiresResult: false) { }
    internal CoverageFanInVerifyCommand(ICoverageFanInResultDeliveryFactory deliveryFactory)
        : base("verify", requiresResult: false, deliveryFactory) { }
}

internal sealed record ParsedCoverageFanInRequest(string Operation, object Request)
{
    internal int ContractVersion { get; init; } = 2;
}

internal sealed record CoverageFanInCliEnvelope(int SchemaVersion, string Operation, string Status,
    string? OperationId, CoverageOperationOutcome? OperationOutcome, CoverageOperationEffects? Effects,
    bool EffectsKnown, CoverageFanInCliResult? Result, CoverageFanInCliDelivery Delivery,
    IReadOnlyList<CoverageFanInCliDiagnostic> Diagnostics);

/// <summary>Terminal-safe projection of the public result that omits caller and host filesystem paths.</summary>
internal sealed record CoverageFanInCliResult(string Operation, CoverageOperationOutcome Outcome, string OperationId,
    string? PolicyDigest, CoverageOperationEffects Effects, IReadOnlyList<CoverageFanInCliArtifact> Artifacts,
    IReadOnlyList<CoverageDiagnostic> Diagnostics, bool Accepted, bool Verified, string PatchOutcome,
    CoverageObservation? Observation, CoverageEvaluationIntent EvaluationIntent, bool PrimaryImported,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CoveragePatchApplicability? PatchApplicability = null,
    CoverageFanInAccounting? Accounting = null)
{
    internal static CoverageFanInCliResult From(CoverageFanInResult result) => From(result, 2);

    internal static CoverageFanInCliResult From(CoverageFanInResult result, int contractVersion) => new(
        result.Operation, result.Outcome, result.OperationId, result.PolicyDigest, result.Effects,
        result.Artifacts.Select(artifact => new CoverageFanInCliArtifact(
            SafeRole(artifact.Role), artifact.Length, artifact.Sha256)).ToArray(),
        result.Diagnostics, result.Accepted, result.Verified, result.PatchOutcome, result.Observation,
        result.EvaluationIntent, result.PrimaryImported,
        contractVersion == 3 ? result.PatchApplicability : null,
        contractVersion == 3 ? result.Accounting : null);

    internal static bool IsValidForV3(CoverageFanInResult result)
    {
        if (result.Operation is ("capture" or "compose") && result.PatchApplicability is not null) return false;
        if (result.PatchApplicability is { } applicability && !IsValid(applicability)) return false;
        return !string.Equals(result.PatchOutcome, "PatchAccepted", StringComparison.Ordinal)
            || result.PatchApplicability is { } accepted && IsCompleteAcceptedApplicability(accepted);
    }

    private static bool IsValid(CoveragePatchApplicability applicability)
    {
        if (applicability.Primary is null || applicability.Composed is null
            || !IsValidLine(applicability.Primary.Line) || !IsValid(applicability.Primary.Branch)
            || !IsValidLine(applicability.Composed.Line) || !IsValid(applicability.Composed.Branch)) return false;
        var changedLineCounts = new[] { applicability.Primary.Line, applicability.Primary.Branch,
            applicability.Composed.Line, applicability.Composed.Branch }
            .Where(metric => metric.Outcome != CoveragePatchApplicabilityOutcome.Unverified)
            .Select(metric => metric.ChangedLines).Distinct().ToArray();
        return changedLineCounts.Length <= 1;
    }

    private static bool IsCompleteAcceptedApplicability(CoveragePatchApplicability applicability)
        => IsValid(applicability)
            && applicability.Primary.Line.Outcome != CoveragePatchApplicabilityOutcome.Unverified
            && applicability.Primary.Branch.Outcome != CoveragePatchApplicabilityOutcome.Unverified
            && applicability.Composed.Line.Outcome != CoveragePatchApplicabilityOutcome.Unverified
            && applicability.Composed.Branch.Outcome != CoveragePatchApplicabilityOutcome.Unverified;

    private static bool IsValid(CoveragePatchMetricResult metric)
    {
        if (metric is null) return false;
        return metric.Outcome switch
        {
            CoveragePatchApplicabilityOutcome.Measured => metric.Reason == "reported-points"
                && metric.ChangedLines is >= 0 && metric.Measurable is > 0 && metric.Covered is >= 0
                && metric.Covered <= metric.Measurable && metric.Percent is >= 0 and <= 100,
            CoveragePatchApplicabilityOutcome.NotApplicable => metric.Reason == "no-reported-patch-points"
                && metric.ChangedLines is >= 0 && metric.Measurable == 0 && metric.Covered == 0 && metric.Percent is null,
            CoveragePatchApplicabilityOutcome.Unverified => metric.Reason == "invalid-evidence"
                && metric.ChangedLines is null && metric.Measurable is null && metric.Covered is null && metric.Percent is null,
            _ => false,
        };
    }

    private static bool IsValidLine(CoveragePatchMetricResult metric)
    {
        if (!IsValid(metric)) return false;
        return metric.Outcome != CoveragePatchApplicabilityOutcome.Measured
            || metric.Measurable is { } measurable && metric.ChangedLines is { } changedLines && measurable <= changedLines;
    }

    private static string SafeRole(string role) => role switch
    {
        "acceptance.json" => "acceptance",
        "collector.cobertura.xml" => "collectorCoverage",
        "collector-inventory.json" => "collectorInventory",
        "composed-gate.json" => "composedGate",
        "composed-gate.md" => "composedGateSummary",
        "coverage.cobertura.xml" => "coverage",
        "diff.patch" => "patchDiff",
        "generation.json" => "generation",
        "manifest.json" => "manifest",
        "primary-gate.json" => "primaryGate",
        "primary-gate.md" => "primaryGateSummary",
        "primary-paths.cobertura.xml" => "primaryCoverage",
        "rejection.json" => "rejection",
        "timings.json" => "timings",
        "verification.json" => "verification",
        _ when role.StartsWith("collector-", StringComparison.Ordinal) && role.EndsWith(".cobertura.xml", StringComparison.Ordinal) => "collectorCoverage",
        _ => "artifact",
    };
}

internal sealed record CoverageFanInCliArtifact(string Role, long Length, string Sha256);

internal sealed record CoverageFanInCliDelivery(bool Requested, string Status, bool ResultFileCreated,
    bool? ResultFileCommitted);

internal sealed record CoverageFanInCliDiagnostic(string Code, string Stage, string Field, string Recovery)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? Cause { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyDictionary<string, string>? Context { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyList<string>? Trace { get; init; }
}

internal sealed class CoverageFanInCliInputException(string field = "request", string cause = "invalid-policy",
    string? recovery = null, string code = "ASCOVF001") : ArgumentException
{
    internal string Field { get; } = field;
    internal string Cause { get; } = cause;
    internal string? Recovery { get; } = recovery;
    internal string Code { get; } = code;
}

internal static class CoverageFanInCliCodec
{
    private const string ReportProfileId = "coverlet-collector-cobertura-v1";
    private const string Documentation = "https://appsurface.dev/guides/coverage-fan-in#diagnostics";
    private static readonly string[] ContextKeys =
    ["baseCommit", "contractVersion", "diffPolicyId", "exceptionType", "gitExitCode", "gitVersion", "headCommit", "reportProfileId"];
    private static readonly string[] TraceNamespaces =
    ["ForgeTrust.AppSurface.Coverage", "ForgeTrust.AppSurface.Evidence.Coverage", "ForgeTrust.AppSurface.Cli"];
    private static readonly HashSet<string> Causes = new(StringComparer.Ordinal)
    {
        "unsupported-version", "unsupported-profile", "invalid-policy", "missing-diff", "malformed-diff",
        "truncated-diff", "unsupported-diff", "merge-base-unavailable", "identity-mismatch", "report-invalid",
        "reported-record-loss", "applicability-mismatch", "limit-exceeded", "threshold-failed", "cancelled",
        "deadline-exceeded", "unexpected-failure", "delivery-failed", "unsupported-git-version"
    };

    internal static byte[] Serialize(CoverageFanInCliEnvelope envelope, JsonSerializerOptions options)
        => envelope.SchemaVersion switch
        {
            2 => JsonSerializer.SerializeToUtf8Bytes(ToV2(envelope), options),
            3 => JsonSerializer.SerializeToUtf8Bytes(ToV3(envelope), options),
            _ => throw new InvalidOperationException("Unsupported coverage fan-in result contract version.")
        };

    internal static CoverageFanInCliDiagnostic Diagnostic(int version, string code, string stage, string field,
        string recovery, string? cause = null, IReadOnlyDictionary<string, string>? context = null,
        IReadOnlyList<string>? trace = null)
        => new(SafeToken(code, 32), SafeToken(stage, 64), SafeToken(field, 64), SafeText(recovery, 512))
        {
            Cause = version == 3 ? SafeCause(cause) : null,
            Context = version == 3 ? SafeContext(context) : null,
            Trace = version == 3 ? SafeTrace(trace) : null
        };

    internal static IReadOnlyDictionary<string, string>? ContextFor(int version)
        => version == 3 ? new Dictionary<string, string>(StringComparer.Ordinal) { ["contractVersion"] = "3" } : null;

    internal static IReadOnlyDictionary<string, string>? ContextFor(ParsedCoverageFanInRequest request, Type? exceptionType = null)
    {
        if (request.ContractVersion != 3) return null;
        var policy = PolicyFor(request);
        var context = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contractVersion"] = "3",
            ["reportProfileId"] = policy.ReportProfileId ?? ReportProfileId,
            ["headCommit"] = policy.Source.Commit
        };
        if (policy.Gate.DiffBase is not null) context["baseCommit"] = policy.Gate.DiffBase;
        if (exceptionType is not null) context["exceptionType"] = exceptionType.Name;
        return SafeContext(context);
    }

    internal static IReadOnlyList<string> TraceFor(Exception exception)
    {
        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames() ?? [];
        var safe = new List<string>(8);
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            var fullName = type?.FullName;
            if (method is null || fullName is null || !IsAllowedTypeName(fullName)) continue;
            var candidate = $"{fullName}.{method.Name}";
            if (!IsSafeFrame(candidate)) continue;
            safe.Add(candidate);
            if (safe.Count == 8) break;
        }
        return safe.Count == 0
            ? ["handled-at:ForgeTrust.AppSurface.Cli.CoverageFanInCommandBase.ExecuteAsync"]
            : safe;
    }

    internal static string FormatDiagnostic(CoverageDiagnostic diagnostic, int version)
    {
        if (version == 2) return $"{diagnostic.Code}: {diagnostic.Recovery}";
        var projected = ToV3(diagnostic);
        return FormatDiagnostic(projected.Code, projected.Stage, projected.Field, projected.Recovery,
            projected.Cause, projected.Context, projected.Trace);
    }

    internal static string FormatDiagnostic(CoverageFanInCliDiagnostic diagnostic, int version)
        => version == 2
            ? $"{diagnostic.Code}: {diagnostic.Recovery}"
            : FormatDiagnostic(diagnostic.Code, diagnostic.Stage, diagnostic.Field, diagnostic.Recovery,
                SafeCause(diagnostic.Cause), SafeContext(diagnostic.Context), SafeTrace(diagnostic.Trace));

    internal static IReadOnlyList<string>? SafeTrace(IReadOnlyList<string>? trace)
    {
        if (trace is null) return null;
        return trace.Where(IsSafeFrame).Take(8).Select(value => SafeText(value, 256)).ToArray();
    }

    private static object ToV2(CoverageFanInCliEnvelope envelope)
        => new CoverageFanInCliEnvelopeV2(envelope.SchemaVersion, envelope.Operation, envelope.Status,
            envelope.OperationId, envelope.OperationOutcome, ToEffects(envelope.Effects), envelope.EffectsKnown,
            envelope.Result is null ? null : ToV2(envelope.Result), ToDelivery(envelope.Delivery),
            envelope.Diagnostics.Select(ToV2).ToArray());

    private static object ToV3(CoverageFanInCliEnvelope envelope)
        => new CoverageFanInCliEnvelopeV3(envelope.SchemaVersion, envelope.Operation, envelope.Status,
            envelope.OperationId, envelope.OperationOutcome, ToEffects(envelope.Effects), envelope.EffectsKnown,
            envelope.Result is null ? null : ToV3(envelope.Result), ToDelivery(envelope.Delivery),
            envelope.Diagnostics.Select(ToV3).ToArray());

    private static CoverageFanInCliResultV2 ToV2(CoverageFanInCliResult result)
        => new(result.Operation, result.Outcome, result.OperationId, result.PolicyDigest, ToRequiredEffects(result.Effects),
            result.Artifacts.Select(ToArtifact).ToArray(), result.Diagnostics.Select(ToV2).ToArray(),
            result.Accepted, result.Verified, result.PatchOutcome, result.Observation, result.EvaluationIntent,
            result.PrimaryImported);

    private static CoverageFanInCliResultV3 ToV3(CoverageFanInCliResult result)
        => new(result.Operation, result.Outcome, result.OperationId, result.PolicyDigest, ToRequiredEffects(result.Effects),
            result.Artifacts.Select(ToArtifact).ToArray(), result.Diagnostics.Select(ToV3).ToArray(),
            result.Accepted, result.Verified, result.PatchOutcome, result.Observation, result.EvaluationIntent,
            result.PrimaryImported, result.PatchApplicability is null ? null : ToV3(result.PatchApplicability),
            result.Accounting is null ? null : new CoverageFanInCliAccounting(result.Accounting.UsedBytes, result.Accounting.LimitBytes,
                result.Accounting.XmlBytes, result.Accounting.JsonBytes, result.Accounting.LineRecords,
                result.Accounting.BranchRecords, result.Accounting.StagedBytes, result.Accounting.StagedFiles));

    private static CoverageFanInCliDiagnosticV2 ToV2(CoverageFanInCliDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Stage, diagnostic.Field, diagnostic.Recovery);

    private static CoverageFanInCliDiagnosticV3 ToV3(CoverageFanInCliDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Stage, diagnostic.Field, diagnostic.Recovery,
            SafeCause(diagnostic.Cause), SafeContext(diagnostic.Context), SafeTrace(diagnostic.Trace));

    private static CoverageFanInCliOperationDiagnosticV2 ToV2(CoverageDiagnostic diagnostic)
        => new(SafeToken(diagnostic.Code, 32), SafeToken(diagnostic.Stage, 64), SafeToken(diagnostic.Field, 64),
            SafeText(diagnostic.Recovery, 512), diagnostic.Documentation);

    private static CoverageFanInCliOperationDiagnosticV3 ToV3(CoverageDiagnostic diagnostic)
        => new(SafeToken(diagnostic.Code, 32), SafeToken(diagnostic.Stage, 64), SafeToken(diagnostic.Field, 64),
            SafeText(diagnostic.Recovery, 512), Documentation, SafeCause(diagnostic.Cause),
            SafeContext(diagnostic.Context), SafeTrace(diagnostic.Trace));

    private static CoverageFanInCliPatchApplicability ToV3(CoveragePatchApplicability applicability)
        => new(ToV3(applicability.Primary), ToV3(applicability.Composed));

    private static CoverageFanInCliPatchMetricSet ToV3(CoveragePatchMetricSet metrics)
        => new(ToV3(metrics.Line), ToV3(metrics.Branch));

    private static CoverageFanInCliPatchMetric ToV3(CoveragePatchMetricResult metric)
        => new(metric.Outcome switch
        {
            CoveragePatchApplicabilityOutcome.Measured => "measured",
            CoveragePatchApplicabilityOutcome.NotApplicable => "notApplicable",
            CoveragePatchApplicabilityOutcome.Unverified => "unverified",
            _ => throw new InvalidOperationException("Unknown patch applicability outcome.")
        }, metric.Reason, metric.ChangedLines, metric.Measurable, metric.Covered, metric.Percent);

    private static CoverageFanInCliEffects? ToEffects(CoverageOperationEffects? effects)
        => effects is null ? null : new(effects.OutputCreated, effects.GenerationCommitted, effects.CleanupComplete,
            effects.Imported, effects.Generation);

    private static CoverageFanInCliEffects ToRequiredEffects(CoverageOperationEffects effects)
        => new(effects.OutputCreated, effects.GenerationCommitted, effects.CleanupComplete,
            effects.Imported, effects.Generation);

    private static CoverageFanInCliArtifact ToArtifact(CoverageFanInCliArtifact artifact)
        => new(artifact.Role, artifact.Length, artifact.Sha256);

    private static CoverageFanInCliDelivery ToDelivery(CoverageFanInCliDelivery delivery)
        => new(delivery.Requested, delivery.Status, delivery.ResultFileCreated, delivery.ResultFileCommitted);

    private static string? SafeCause(string? value) => value is not null && Causes.Contains(value) ? value : null;

    private static IReadOnlyDictionary<string, string>? SafeContext(IReadOnlyDictionary<string, string>? context)
    {
        if (context is null) return null;
        var safe = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in context)
        {
            if (Array.BinarySearch(ContextKeys, pair.Key, StringComparer.Ordinal) < 0 || !IsSafeContextValue(pair.Key, pair.Value)) continue;
            safe[pair.Key] = pair.Value;
        }
        return safe.Count == 0 ? null : safe;
    }

    private static bool IsSafeContextValue(string key, string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(char.IsControl)) return false;
        return key switch
        {
            "contractVersion" => value == "3",
            "reportProfileId" => value == ReportProfileId,
            "baseCommit" or "headCommit" => (value.Length is 40 or 64) && value.All(Uri.IsHexDigit),
            "gitExitCode" => int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "exceptionType" => IsIdentifier(value),
            "diffPolicyId" or "gitVersion" => value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_'),
            _ => false
        };
    }

    private static bool IsAllowedTypeName(string name)
        => TraceNamespaces.Any(prefix => name.StartsWith(prefix + ".", StringComparison.Ordinal));

    private static bool IsSafeFrame(string frame)
    {
        if (frame.StartsWith("handled-at:", StringComparison.Ordinal))
            frame = frame["handled-at:".Length..];
        if (frame.Length is 0 or > 256 || !IsAllowedTypeName(frame)) return false;
        return frame.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '+' or '`' or '<' or '>' or '-');
    }

    private static bool IsIdentifier(string value)
        => value.Length is > 0 and <= 128 && (char.IsAsciiLetter(value[0]) || value[0] == '_')
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string SafeToken(string value, int maximum)
        => string.IsNullOrEmpty(value) ? "unknown"
            : new string(value.Take(maximum).Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-' ? character : '_').ToArray());

    private static string SafeText(string value, int maximum)
    {
        var safe = new System.Text.StringBuilder(Math.Min(value.Length, maximum));
        foreach (var character in value)
        {
            if (safe.Length >= maximum) break;
            safe.Append(char.IsControl(character) ? ' ' : character);
        }
        return safe.ToString();
    }

    private static string FormatDiagnostic(string code, string stage, string field, string recovery, string? cause,
        IReadOnlyDictionary<string, string>? context, IReadOnlyList<string>? trace)
    {
        var contextText = context is null ? string.Empty
            : "; context=" + string.Join(',', context.Select(pair => $"{pair.Key}={pair.Value}"));
        var traceText = trace is null ? string.Empty : "; trace=" + string.Join(" <- ", trace);
        return SafeText($"{SafeToken(code, 32)} {SafeToken(stage, 64)} {SafeToken(field, 64)}: {cause ?? "invalid-policy"}{contextText}{traceText}. Fix: {SafeText(recovery, 512)}", 2048);
    }

    private static CoverageFanInPolicy PolicyFor(ParsedCoverageFanInRequest request) => request.Request switch
    {
        CoverageCaptureRequest capture => capture.Policy,
        CoverageCollectRequest collect => collect.Policy,
        CoverageComposeRequest compose => compose.Policy,
        CoverageAcceptRequest accept => accept.Policy,
        CoverageVerifyRequest verify => verify.Policy,
        _ => throw new InvalidOperationException("Unknown coverage fan-in request.")
    };

    private sealed record CoverageFanInCliEnvelopeV2(int SchemaVersion, string Operation, string Status,
        string? OperationId, CoverageOperationOutcome? OperationOutcome, CoverageFanInCliEffects? Effects,
        bool EffectsKnown, CoverageFanInCliResultV2? Result, CoverageFanInCliDelivery Delivery,
        IReadOnlyList<CoverageFanInCliDiagnosticV2> Diagnostics);

    private sealed record CoverageFanInCliEnvelopeV3(int SchemaVersion, string Operation, string Status,
        string? OperationId, CoverageOperationOutcome? OperationOutcome, CoverageFanInCliEffects? Effects,
        bool EffectsKnown, CoverageFanInCliResultV3? Result, CoverageFanInCliDelivery Delivery,
        IReadOnlyList<CoverageFanInCliDiagnosticV3> Diagnostics);

    private sealed record CoverageFanInCliResultV2(string Operation, CoverageOperationOutcome Outcome, string OperationId,
        string? PolicyDigest, CoverageFanInCliEffects Effects, IReadOnlyList<CoverageFanInCliArtifact> Artifacts,
        IReadOnlyList<CoverageFanInCliOperationDiagnosticV2> Diagnostics, bool Accepted, bool Verified,
        string PatchOutcome, CoverageObservation? Observation, CoverageEvaluationIntent EvaluationIntent, bool PrimaryImported);

    private sealed record CoverageFanInCliResultV3(string Operation, CoverageOperationOutcome Outcome, string OperationId,
        string? PolicyDigest, CoverageFanInCliEffects Effects, IReadOnlyList<CoverageFanInCliArtifact> Artifacts,
        IReadOnlyList<CoverageFanInCliOperationDiagnosticV3> Diagnostics, bool Accepted, bool Verified,
        string PatchOutcome, CoverageObservation? Observation, CoverageEvaluationIntent EvaluationIntent, bool PrimaryImported,
        CoverageFanInCliPatchApplicability? PatchApplicability, CoverageFanInCliAccounting? Accounting);

    private sealed record CoverageFanInCliAccounting(long UsedBytes, long LimitBytes, long XmlBytes, long JsonBytes,
        long LineRecords, long BranchRecords, long StagedBytes, long StagedFiles);

    private sealed record CoverageFanInCliEffects(bool OutputCreated, bool GenerationCommitted, bool CleanupComplete,
        bool Imported, string? Generation);
    private sealed record CoverageFanInCliDiagnosticV2(string Code, string Stage, string Field, string Recovery);
    private sealed record CoverageFanInCliDiagnosticV3(string Code, string Stage, string Field, string Recovery,
        string? Cause, IReadOnlyDictionary<string, string>? Context, IReadOnlyList<string>? Trace);
    private sealed record CoverageFanInCliOperationDiagnosticV2(string Code, string Stage, string Field, string Recovery,
        string Documentation);
    private sealed record CoverageFanInCliOperationDiagnosticV3(string Code, string Stage, string Field, string Recovery,
        string Documentation, string? Cause, IReadOnlyDictionary<string, string>? Context, IReadOnlyList<string>? Trace);
    private sealed record CoverageFanInCliPatchApplicability(CoverageFanInCliPatchMetricSet Primary,
        CoverageFanInCliPatchMetricSet Composed);
    private sealed record CoverageFanInCliPatchMetricSet(CoverageFanInCliPatchMetric Line,
        CoverageFanInCliPatchMetric Branch);
    private sealed record CoverageFanInCliPatchMetric(string Outcome, string Reason, int? ChangedLines,
        int? Measurable, int? Covered, decimal? Percent);
}

internal static class CoverageFanInRequestReader
{
    internal static ParsedCoverageFanInRequest Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 64,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new CoverageFanInCliInputException();
            CoveragePolicyEncoding.CheckSyntax(root);
            var rootFields = ReadObject(root, ["schemaVersion", "operation", "request"]);
            var version = ReadInt32(rootFields["schemaVersion"]);
            if (version is not (2 or 3))
                throw new CoverageFanInCliInputException("schemaVersion", "unsupported-version",
                    "Select supported schemaVersion 2 or 3 before creating a result destination.");
            var operation = ReadString(rootFields["operation"]);
            var requestElement = rootFields["request"];
            var request = ReadOperation(operation, requestElement, version);
            var policy = PolicyOf(request);
            if (policy.ContractVersion != version)
                throw new CoverageFanInCliInputException("policy", "unsupported-version",
                    "Use one contract version consistently in schemaVersion and policy.contractVersion.");
            return new(operation, request) { ContractVersion = version };
        }
        catch (CoverageFanInCliInputException) { throw; }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new CoverageFanInCliInputException();
        }
    }

    internal static int TryReadEnvelopeVersion(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out var value)
                && value.ValueKind == JsonValueKind.Number
                && TryReadInt32(value, out var version) && version == 3 ? 3 : 2;
        }
        catch (JsonException) { return 2; }
    }

    internal static void ValidateCanonicalPolicy(ParsedCoverageFanInRequest parsed)
    {
        var policy = parsed.Request switch
        {
            CoverageCaptureRequest capture => capture.Policy,
            CoverageCollectRequest collect => collect.Policy,
            CoverageComposeRequest compose => compose.Policy,
            CoverageAcceptRequest accept => accept.Policy,
            CoverageVerifyRequest verify => verify.Policy,
            _ => throw new CoverageFanInCliInputException(),
        };

        _ = CoveragePolicyEncoding.CanonicalBytes(policy);
    }

    internal static void ValidateOperationRequest(ParsedCoverageFanInRequest parsed)
    {
        static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
        static void RequireProducer(CoverageFanInPolicy policy, string producerId)
        {
            if (producerId != "primary" && !policy.Supplements.Any(supplement => supplement.Id == producerId))
                throw new CoverageFanInCliInputException();
        }

        switch (parsed.Request)
        {
            case CoverageCaptureRequest capture:
                RequireProducer(capture.Policy, capture.ProducerId);
                if (capture.ImportDirectory is not null)
                {
                    if (!HasText(capture.OriginalSourceRoot) || capture.CoveragePath is not null
                        || capture.TimingsPath is not null || capture.RawCoveragePath is not null || capture.RawCoveragePaths is not null)
                        throw new CoverageFanInCliInputException();
                }
                else if (capture.OriginalSourceRoot is not null
                    || (capture.ProducerId == "primary"
                        ? !HasText(capture.CoveragePath) || !HasText(capture.TimingsPath)
                            || capture.RawCoveragePath is not null || capture.RawCoveragePaths is not null
                        : capture.RawCoveragePaths is not null
                            ? capture.CoveragePath is not null || !HasText(capture.TimingsPath) || capture.RawCoveragePath is not null
                            : !HasText(capture.RawCoveragePath) || capture.TimingsPath is not null
                                || capture.CoveragePath is not null && !HasText(capture.CoveragePath)))
                {
                    throw new CoverageFanInCliInputException();
                }
                break;
            case CoverageCollectRequest collect:
                RequireProducer(collect.Policy, collect.ProducerId);
                if (!HasText(collect.CollectionDirectory) || collect.DeadlineSeconds is < 1 or > 86400
                    || collect.Parallelism < 1)
                    throw new CoverageFanInCliInputException();
                break;
            case CoverageComposeRequest compose:
                RequireCaptureInventory(compose.Policy, compose.PrimaryCaptureDirectory, compose.SupplementaryCaptureDirectories);
                if (!HasText(compose.OutputDirectory)) throw new CoverageFanInCliInputException();
                break;
            case CoverageAcceptRequest accept:
                RequireCaptureInventory(accept.Policy, accept.PrimaryCaptureDirectory, accept.SupplementaryCaptureDirectories);
                if (!HasText(accept.ComposedOutputDirectory) || !HasText(accept.OutputDirectory))
                    throw new CoverageFanInCliInputException();
                if (parsed.ContractVersion == 3 && accept.Policy.Gate.Mode == CoverageGateMode.AggregateAndPatch
                    && !IsSha256(accept.ExpectedPatchSha256))
                    throw new CoverageFanInCliInputException("expectedPatchSha256", "invalid-policy",
                        "Set expectedPatchSha256 to the exact pinned patch digest before v3 patch acceptance.");
                break;
            case CoverageVerifyRequest verify:
                RequireCaptureInventory(verify.Policy, verify.PrimaryCaptureDirectory, verify.SupplementaryCaptureDirectories);
                if (!HasText(verify.ComposedOutputDirectory) || !HasText(verify.AcceptedOutputDirectory))
                    throw new CoverageFanInCliInputException();
                break;
            default:
                throw new CoverageFanInCliInputException();
        }

        static void RequireCaptureInventory(CoverageFanInPolicy policy, string primary,
            IReadOnlyList<string> supplementary)
        {
            if (!HasText(primary) || supplementary is null || supplementary.Count != policy.Supplements.Count)
                throw new CoverageFanInCliInputException();
        }
    }

    private static object ReadOperation(string operation, JsonElement element, int version)
    {
        switch (operation)
        {
            case "capture":
                {
                    var fields = ReadObject(element,
                        ["policy", "repositoryRoot", "outputDirectory", "producerId"],
                        ["coveragePath", "timingsPath", "rawCoveragePath", "importDirectory", "originalSourceRoot", "outputMode", "rawCoveragePaths"]);
                    return new CoverageCaptureRequest(ReadPolicy(fields["policy"], version), ReadString(fields["repositoryRoot"]),
                        ReadString(fields["outputDirectory"]), ReadString(fields["producerId"]),
                        ReadOptionalString(fields, "coveragePath"), ReadOptionalString(fields, "timingsPath"),
                        ReadOptionalString(fields, "rawCoveragePath"), ReadOptionalString(fields, "importDirectory"),
                        ReadOptionalString(fields, "originalSourceRoot"), ReadOptionalEnum(fields, "outputMode", CoverageOutputMode.Fresh),
                        ReadOptionalStringDictionary(fields, "rawCoveragePaths"));
                }
            case "collect":
                {
                    var fields = ReadObject(element,
                        ["policy", "repositoryRoot", "outputDirectory", "collectionDirectory", "producerId", "deadlineSeconds"],
                        ["solutionPath", "parallelism", "noRestore", "noBuild"]);
                    return new CoverageCollectRequest(ReadPolicy(fields["policy"], version), ReadString(fields["repositoryRoot"]),
                        ReadString(fields["outputDirectory"]), ReadString(fields["collectionDirectory"]),
                        ReadString(fields["producerId"]), ReadInt32(fields["deadlineSeconds"]),
                        ReadOptionalString(fields, "solutionPath"), ReadOptionalInt32(fields, "parallelism", 1),
                        ReadOptionalBoolean(fields, "noRestore", false), ReadOptionalBoolean(fields, "noBuild", false));
                }
            case "compose":
                {
                    var fields = ReadObject(element,
                        ["policy", "repositoryRoot", "primaryCaptureDirectory", "supplementaryCaptureDirectories", "outputDirectory"],
                        ["outputMode", "evaluationIntent"]);
                    return new CoverageComposeRequest(ReadPolicy(fields["policy"], version), ReadString(fields["repositoryRoot"]),
                        ReadString(fields["primaryCaptureDirectory"]), ReadStringArray(fields["supplementaryCaptureDirectories"]),
                        ReadString(fields["outputDirectory"]), ReadOptionalEnum(fields, "outputMode", CoverageOutputMode.Fresh),
                        ReadOptionalEnum(fields, "evaluationIntent", CoverageEvaluationIntent.CurrentTrustedRun));
                }
            case "accept":
                {
                    var fields = ReadObject(element,
                        ["policy", "repositoryRoot", "primaryCaptureDirectory", "supplementaryCaptureDirectories", "composedOutputDirectory", "outputDirectory"],
                        ["outputMode", "evaluationIntent", "expectedPatchSha256"]);
                    return new CoverageAcceptRequest(ReadPolicy(fields["policy"], version), ReadString(fields["repositoryRoot"]),
                        ReadString(fields["primaryCaptureDirectory"]), ReadStringArray(fields["supplementaryCaptureDirectories"]),
                        ReadString(fields["composedOutputDirectory"]), ReadString(fields["outputDirectory"]),
                        ReadOptionalEnum(fields, "outputMode", CoverageOutputMode.Fresh),
                        ReadOptionalEnum(fields, "evaluationIntent", CoverageEvaluationIntent.CurrentTrustedRun))
                    { ExpectedPatchSha256 = ReadOptionalString(fields, "expectedPatchSha256") };
                }
            case "verify":
                {
                    var fields = ReadObject(element,
                        ["policy", "repositoryRoot", "primaryCaptureDirectory", "supplementaryCaptureDirectories", "composedOutputDirectory", "acceptedOutputDirectory"],
                        ["evaluationIntent"]);
                    return new CoverageVerifyRequest(ReadPolicy(fields["policy"], version), ReadString(fields["repositoryRoot"]),
                        ReadString(fields["primaryCaptureDirectory"]), ReadStringArray(fields["supplementaryCaptureDirectories"]),
                        ReadString(fields["composedOutputDirectory"]), ReadString(fields["acceptedOutputDirectory"]),
                        ReadOptionalEnum(fields, "evaluationIntent", CoverageEvaluationIntent.CurrentTrustedRun));
                }
            default:
                throw new CoverageFanInCliInputException("operation", "invalid-policy",
                    "Use one registered operation: capture, collect, compose, accept, or verify.");
        }
    }

    private static CoverageFanInPolicy ReadPolicy(JsonElement element, int version)
    {
        var required = version == 3
            ? new[] { "contractVersion", "reportProfileId", "source", "run", "testProjects", "supplements", "gate" }
            : ["source", "run", "testProjects", "supplements", "gate"];
        var fields = ReadObject(element, required, ["instrumentation", "limits"]);
        if (version == 3)
        {
            var selected = ReadInt32(fields["contractVersion"]);
            if (selected != 3)
                throw new CoverageFanInCliInputException("policy", "unsupported-version",
                    "Use contractVersion=3 when schemaVersion=3.");
            var profile = ReadString(fields["reportProfileId"]);
            if (profile != "coverlet-collector-cobertura-v1")
                throw new CoverageFanInCliInputException("policy", "unsupported-profile",
                    "Set reportProfileId to the supported coverlet-collector-cobertura-v1 profile.");
        }

        var policy = new CoverageFanInPolicy(ReadSource(fields["source"]), ReadRun(fields["run"]),
            ReadStringArray(fields["testProjects"]), ReadSupplements(fields["supplements"]), ReadGate(fields["gate"]),
            ReadInstrumentation(fields), ReadLimits(fields))
        {
            ContractVersion = version,
            ReportProfileId = version == 3 ? ReadString(fields["reportProfileId"]) : null
        };
        return policy;
    }

    private static CoverageSourceIdentity ReadSource(JsonElement element)
    {
        var fields = ReadObject(element, ["commit", "tree", "sdk", "configuration"]);
        return new(ReadString(fields["commit"]), ReadString(fields["tree"]), ReadString(fields["sdk"]),
            ReadString(fields["configuration"]));
    }

    private static CoverageRunIdentity ReadRun(JsonElement element)
    {
        var fields = ReadObject(element, ["producerSystem", "runScope", "runId", "attempt"]);
        return new(ReadString(fields["producerSystem"]), ReadString(fields["runScope"]), ReadString(fields["runId"]),
            ReadInt32(fields["attempt"]));
    }

    private static CoverageGatePolicy ReadGate(JsonElement element)
    {
        var fields = ReadObject(element, ["mode"], ["minLine", "minBranch", "minPatchLine", "minPatchBranch", "tolerance", "patchLineMode", "diffBase"]);
        return new(ReadEnum<CoverageGateMode>(fields["mode"]), ReadOptionalDecimal(fields, "minLine", 95m),
            ReadOptionalDecimal(fields, "minBranch", 85m), ReadOptionalDecimal(fields, "minPatchLine", 95m),
            ReadOptionalDecimal(fields, "minPatchBranch", 85m), ReadOptionalDecimal(fields, "tolerance", 0.5m),
            ReadOptionalNonNullString(fields, "patchLineMode", "codecov"), ReadOptionalString(fields, "diffBase"));
    }

    private static CoverageInstrumentation? ReadInstrumentation(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("instrumentation", out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        var values = ReadObject(element, [], ["driver", "format", "include", "exclude", "normalization"]);
        return new(ReadOptionalNonNullString(values, "driver", "collector"), ReadOptionalNonNullString(values, "format", "cobertura"),
            ReadOptionalString(values, "include"), ReadOptionalNonNullString(values, "exclude", "[*.Tests]*,[*.IntegrationTests]*"),
            ReadOptionalNonNullString(values, "normalization", "appsurface-coverage-merge"));
    }

    private static CoverageOperationLimits? ReadLimits(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("limits", out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        var values = ReadObject(element, [], ["xmlBytes", "jsonBytes", "totalBytes", "depth", "lineRecordsPerReport",
            "inputBundles", "xmlElementsPerReport", "attributesPerElement", "tokenCharacters", "branchRecordsPerReport",
            "totalRecords", "operationSeconds", "identitySeconds"]);
        return new(ReadOptionalInt64(values, "xmlBytes", 536870912), ReadOptionalInt64(values, "jsonBytes", 1048576),
            ReadOptionalInt64(values, "totalBytes", 4294967296), ReadOptionalInt32(values, "depth", 64),
            ReadOptionalInt32(values, "lineRecordsPerReport", 1000000), ReadOptionalInt32(values, "inputBundles", 32),
            ReadOptionalInt32(values, "xmlElementsPerReport", 8000000), ReadOptionalInt32(values, "attributesPerElement", 32),
            ReadOptionalInt32(values, "tokenCharacters", 65536), ReadOptionalInt32(values, "branchRecordsPerReport", 2000000),
            ReadOptionalInt32(values, "totalRecords", 2000000), ReadOptionalInt32(values, "operationSeconds", 300),
            ReadOptionalInt32(values, "identitySeconds", 30));
    }

    private static IReadOnlyList<CoverageSupplement> ReadSupplements(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new CoverageFanInCliInputException();
        var supplements = new List<CoverageSupplement>();
        foreach (var item in element.EnumerateArray())
        {
            var fields = ReadObject(item, ["id", "platform", "filter"], ["testProjects"]);
            supplements.Add(new(ReadString(fields["id"]), ReadString(fields["platform"]), ReadString(fields["filter"]),
                ReadOptionalStringArray(fields, "testProjects")));
        }
        return supplements;
    }

    private static Dictionary<string, string>? ReadOptionalStringDictionary(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Object) throw new CoverageFanInCliInputException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !values.TryAdd(property.Name, ReadString(property.Value)))
                throw new CoverageFanInCliInputException();
        }
        return values;
    }

    private static string[] ReadStringArray(JsonElement element)
        => ReadStringArrayCore(element);

    private static string[]? ReadOptionalStringArray(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        return ReadStringArrayCore(element);
    }

    private static string[] ReadStringArrayCore(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new CoverageFanInCliInputException();
        return element.EnumerateArray().Select(ReadString).ToArray();
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement element, string[] required,
        string[]? optional = null)
    {
        optional ??= [];
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) throw new CoverageFanInCliInputException();
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !result.TryAdd(property.Name, property.Value))
                throw new CoverageFanInCliInputException();
        if (required.Any(name => !result.ContainsKey(name))) throw new CoverageFanInCliInputException();
        return result;
    }

    private static string ReadString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw new CoverageFanInCliInputException();
        return element.GetString()!;
    }

    private static string? ReadOptionalString(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        return ReadString(element);
    }

    private static string ReadOptionalNonNullString(IReadOnlyDictionary<string, JsonElement> fields, string name, string fallback)
        => fields.TryGetValue(name, out var element) ? ReadString(element) : fallback;

    private static int ReadInt32(JsonElement element)
    {
        if (!TryReadInt32(element, out var value)) throw new CoverageFanInCliInputException();
        return value;
    }

    private static bool TryReadInt32(JsonElement element, out int value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var number)
            || number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue) return false;
        value = decimal.ToInt32(number);
        return true;
    }

    private static long ReadInt64(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var number)
            || number != decimal.Truncate(number) || number < long.MinValue || number > long.MaxValue)
            throw new CoverageFanInCliInputException();
        return decimal.ToInt64(number);
    }

    private static decimal ReadDecimal(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value))
            throw new CoverageFanInCliInputException();
        return value;
    }

    private static bool ReadBoolean(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CoverageFanInCliInputException()
        };

    private static TEnum ReadEnum<TEnum>(JsonElement element) where TEnum : struct, Enum
    {
        if (element.ValueKind != JsonValueKind.String) throw new CoverageFanInCliInputException();
        var token = element.GetString();
        foreach (var value in Enum.GetValues<TEnum>())
            if (string.Equals(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()), token, StringComparison.Ordinal)) return value;
        throw new CoverageFanInCliInputException();
    }

    private static TEnum ReadOptionalEnum<TEnum>(IReadOnlyDictionary<string, JsonElement> fields, string name, TEnum fallback)
        where TEnum : struct, Enum
        => fields.TryGetValue(name, out var element) ? ReadEnum<TEnum>(element) : fallback;

    private static int ReadOptionalInt32(IReadOnlyDictionary<string, JsonElement> fields, string name, int fallback)
        => fields.TryGetValue(name, out var element) ? ReadInt32(element) : fallback;

    private static long ReadOptionalInt64(IReadOnlyDictionary<string, JsonElement> fields, string name, long fallback)
        => fields.TryGetValue(name, out var element) ? ReadInt64(element) : fallback;

    private static decimal ReadOptionalDecimal(IReadOnlyDictionary<string, JsonElement> fields, string name, decimal fallback)
        => fields.TryGetValue(name, out var element) ? ReadDecimal(element) : fallback;

    private static bool ReadOptionalBoolean(IReadOnlyDictionary<string, JsonElement> fields, string name, bool fallback)
        => fields.TryGetValue(name, out var element) ? ReadBoolean(element) : fallback;

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static CoverageFanInPolicy PolicyOf(object request) => request switch
    {
        CoverageCaptureRequest capture => capture.Policy,
        CoverageCollectRequest collect => collect.Policy,
        CoverageComposeRequest compose => compose.Policy,
        CoverageAcceptRequest accept => accept.Policy,
        CoverageVerifyRequest verify => verify.Policy,
        _ => throw new CoverageFanInCliInputException()
    };

}

internal sealed class StrictCoverageEnumConverterFactory : JsonConverterFactory
{
    internal static bool IsToken(Type enumType, string? token)
        => token is not null && Enum.GetNames(enumType).Any(name => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(name), token, StringComparison.Ordinal));

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(StrictCoverageEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class StrictCoverageEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            var token = reader.GetString();
            foreach (var value in Enum.GetValues<TEnum>())
                if (string.Equals(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()), token, StringComparison.Ordinal)) return value;
            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException();
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
    }
}

internal interface ICoverageFanInResultDeliveryFactory
{
    ICoverageFanInResultDelivery Prepare(string path);
}

internal interface ICoverageFanInResultDelivery : IDisposable
{
    bool ResultFileCreated { get; }
    bool? ResultFileCommitted { get; }
    void Write(ReadOnlySpan<byte> document);
}

internal sealed class SafeCoverageFanInResultDeliveryFactory : ICoverageFanInResultDeliveryFactory
{
    internal static SafeCoverageFanInResultDeliveryFactory Instance { get; } = new();

    private SafeCoverageFanInResultDeliveryFactory() { }

    public ICoverageFanInResultDelivery Prepare(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            CoverageRunOutputLease.ValidateArtifactComponent(fileName);
            var parentPath = Path.GetDirectoryName(fullPath) ?? throw new CoverageFanInCliInputException();
            var parent = CoverageRunOutputLease.AcquireArtifactDirectory(parentPath,
                createMissing: false, requirePrivate: false) ?? throw new CoverageFanInCliInputException();
            try
            {
                try
                {
                    using var existing = parent.OpenArtifactFile(fileName, createNew: false, requirePrivate: false);
                    throw new CoverageFanInCliInputException();
                }
                catch (FileNotFoundException) { }
                return new SafeCoverageFanInResultDelivery(parent, fileName);
            }
            catch
            {
                parent.Dispose();
                throw;
            }
        }
        catch (CoverageFanInCliInputException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new CoverageFanInCliInputException();
        }
    }
}

internal sealed class SafeCoverageFanInResultDelivery(CoverageRunOutputLease parent, string fileName)
    : ICoverageFanInResultDelivery
{
    private string? _temporaryName;
    private string? _temporaryIdentity;
    public bool ResultFileCreated { get; private set; }
    public bool? ResultFileCommitted { get; private set; } = false;

    public void Write(ReadOnlySpan<byte> document)
    {
        var temporaryName = $".fanin-{Guid.NewGuid():N}.tmp";
        _temporaryName = temporaryName;
        try
        {
            using (var output = parent.OpenArtifactFile(temporaryName, createNew: true, requirePrivate: false))
            {
                ResultFileCreated = true;
                _temporaryIdentity = CoverageRunOutputLease.GetArtifactObjectIdentity(output.SafeFileHandle);
                output.Write(document);
                output.Flush(flushToDisk: true);
            }
            parent.VerifyArtifactBinding();
            parent.MoveArtifactEntry(temporaryName, parent, fileName,
                expectedSourceIdentity: _temporaryIdentity);
            _temporaryName = null;
            ResultFileCommitted = true;
        }
        catch
        {
            ObserveCommit();
            CleanupTemporary();
            throw;
        }
    }

    private void ObserveCommit()
    {
        if (_temporaryIdentity is null) return;
        try
        {
            using var delivered = parent.OpenArtifactFile(fileName, createNew: false, requirePrivate: false);
            ResultFileCommitted = string.Equals(CoverageRunOutputLease.GetArtifactObjectIdentity(delivered.SafeFileHandle),
                _temporaryIdentity, StringComparison.Ordinal);
        }
        catch (FileNotFoundException) { ResultFileCommitted = false; }
        catch { ResultFileCommitted = null; }
    }

    private void CleanupTemporary()
    {
        if (_temporaryName is null || _temporaryIdentity is null) return;
        try { parent.DeleteArtifactEntry(_temporaryName, maximumEntries: 1, expectedIdentity: _temporaryIdentity); }
        catch { }
        _temporaryName = null;
    }

    public void Dispose()
    {
        CleanupTemporary();
        parent.Dispose();
    }
}
