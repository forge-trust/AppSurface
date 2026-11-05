using System.Reflection;
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
    [CommandOption("request", Description = "Required UTF-8 JSON envelope: {schemaVersion:2,operation,request}. Request members use exact camelCase names, string enums, and the typed operation request schema; unknown, duplicate, missing required, wrongly typed, or over-limit input is rejected before execution. The file and its ancestors must be regular, link-free filesystem objects.")]
    public string? Request { get; set; }

    /// <summary>Gets or sets the new result-file destination for this operation.</summary>
    [CommandOption("result", Description = "Write the bounded schema-version 2 terminal result atomically to this new file in an existing link-free directory. Existing files are never replaced or adopted. Required for capture, compose, and accept; optional for read-only verify.")]
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
        ICoverageFanInResultDelivery? delivery = null;
        try
        {
            if (string.IsNullOrWhiteSpace(Request)) throw new CoverageFanInCliInputException();
            if (requiresResult && string.IsNullOrWhiteSpace(Result)) throw new CoverageFanInCliInputException();

            var bytes = ReadBoundedRequest(Request);
            parsed = CoverageFanInRequestReader.Parse(bytes);
            var selectedOperation = CollectRequested ? "collect" : operation;
            if (!string.Equals(parsed.Operation, selectedOperation, StringComparison.Ordinal)
                || (operation == "capture" && (CollectRequested != (parsed.Operation == "collect"))))
                throw new CoverageFanInCliInputException();
            CoverageFanInRequestReader.ValidateOperationRequest(parsed);
            CoverageFanInRequestReader.ValidateCanonicalPolicy(parsed);

            if (!string.IsNullOrWhiteSpace(Result)) delivery = _deliveryFactory.Prepare(Result);
        }
        catch (CoverageFanInCliInputException)
        {
            await WriteInputFailureAsync(console, "ASCOVF001", "request",
                "Supply a valid schemaVersion 2 request and a fresh result path in an existing link-free directory.").ConfigureAwait(false);
            throw new CommandException("Coverage fan-in input is invalid; see the bounded diagnostic output.", 2);
        }
        catch (Exception exception) when (IsInputFailure(exception))
        {
            await WriteInputFailureAsync(console, "ASCOVF001", "request",
                "Supply a valid schemaVersion 2 request and a fresh result path in an existing link-free directory.").ConfigureAwait(false);
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

            if (cancellationToken.IsCancellationRequested)
            {
                status = "cancelled";
                operationOutcome = CoverageOperationOutcome.Cancelled;
                effectsKnown = true;
                exitCode = 130;
                diagnostics.Add(new("ASCOVF004", "cancelled", "operation", "Retry once after confirming that no operation output was created."));
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
                    await WriteInputFailureAsync(console, "ASCOVF001", "request",
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
                    status = StatusFor(result.Outcome);
                    exitCode = ExitCodeFor(result.Outcome);
                }
                catch (OperationCanceledException)
                {
                    status = "cancelled";
                    operationOutcome = CoverageOperationOutcome.Cancelled;
                    exitCode = 130;
                    diagnostics.Add(new("ASCOVF004", "cancelled", "operation", "Inspect the destination and reconcile any committed generation before retrying."));
                }
                catch (TimeoutException)
                {
                    status = "timedOut";
                    operationOutcome = CoverageOperationOutcome.TimedOut;
                    exitCode = 124;
                    diagnostics.Add(new("ASCOVF005", "timedOut", "operation", "Inspect the destination and reconcile any committed generation before retrying."));
                }
                catch (Exception)
                {
                    status = "failed";
                    exitCode = 1;
                    diagnostics.Add(new("ASCOVF003", "execution", "operation", "Inspect the destination and reconcile possible partial effects before retrying."));
                }
            }

            var deliveryInfo = DeliveryInfo(delivery, delivery is null ? "notRequested" : "committed", predictCommit: true);
            var envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown, result, deliveryInfo, diagnostics);
            var document = SerializeBounded(envelope, out var compacted);
            if (compacted)
            {
                status = "incomplete";
                if (exitCode == 0) exitCode = 1;
                diagnostics.Add(new("ASCOVF006", "result", "result", "The result exceeded the 1 MiB terminal limit; inspect the retained operation artifacts directly."));
                envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown, null,
                    DeliveryInfo(delivery, delivery is null ? "notRequested" : "committed", predictCommit: true), diagnostics);
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
                    diagnostics.Add(new("ASCOVF002", "delivery", "result", "Reconcile the operation using its operation ID and committed-generation status before retrying result delivery."));
                    envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown, result,
                        DeliveryInfo(delivery, "failed"), diagnostics);
                    document = SerializeBounded(envelope, out var deliveryCompacted);
                    if (deliveryCompacted)
                    {
                        envelope = CreateEnvelope(status, operationId, operationOutcome, effects, effectsKnown, null,
                            DeliveryInfo(delivery, "failed"), diagnostics);
                        document = SerializeBounded(envelope, out _);
                    }
                }
            }

            if (result is not null)
                foreach (var diagnostic in result.Diagnostics.Take(16))
                    await console.Error.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Recovery}").ConfigureAwait(false);
            foreach (var diagnostic in diagnostics)
                await console.Error.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Recovery}").ConfigureAwait(false);

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

    private async ValueTask WriteInputFailureAsync(IConsole console, string code, string field, string recovery)
    {
        var diagnostic = new CoverageFanInCliDiagnostic(code, "input", field, recovery);
        var envelope = CreateEnvelope("invalidInput", null, null, null, true, null,
            new(false, "notStarted", false, false), [diagnostic]);
        if (Json)
        {
            var bytes = SerializeBounded(envelope, out _);
            await console.Output.WriteLineAsync(System.Text.Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
        }
        else
        {
            await console.Output.WriteLineAsync($"coverage fan-in {operation}: invalid input").ConfigureAwait(false);
            await console.Error.WriteLineAsync($"{code}: {recovery}").ConfigureAwait(false);
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
        if (deliveryFailed)
            await console.Error.WriteLineAsync("  result delivery failed; reconcile the operation before retrying.").ConfigureAwait(false);
    }

    private CoverageFanInCliEnvelope CreateEnvelope(string status, string? operationId,
        CoverageOperationOutcome? operationOutcome, CoverageOperationEffects? effects, bool effectsKnown,
        CoverageFanInResult? result, CoverageFanInCliDelivery delivery, IReadOnlyList<CoverageFanInCliDiagnostic> diagnostics)
        => new(2, result?.Operation ?? operation, status, operationId, operationOutcome, effects, effectsKnown,
            result is null ? null : CoverageFanInCliResult.From(result), delivery, diagnostics.Take(16).ToArray());

    private static CoverageFanInCliDelivery DeliveryInfo(ICoverageFanInResultDelivery? delivery, string status, bool predictCommit = false)
        => delivery is null ? new(false, status, false, null)
            : predictCommit ? new(true, status, true, true)
            : new(true, status, delivery.ResultFileCreated, delivery.ResultFileCommitted);

    private static byte[] SerializeBounded(CoverageFanInCliEnvelope envelope, out bool compacted)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length <= MaximumJsonBytes)
        {
            compacted = false;
            return bytes;
        }

        compacted = true;
        var diagnostic = new CoverageFanInCliDiagnostic("ASCOVF006", "result", "result",
            "The result exceeded the 1 MiB terminal limit; inspect the retained operation artifacts directly.");
        var summary = envelope with { Result = null, Diagnostics = [diagnostic] };
        bytes = JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions);
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

[Command("coverage fan-in capture", Description = "Capture validated producer evidence (fresh output is the default); requires a clean Git source and complete primary or declared supplementary producer artifacts. Next run compose with the exact policy supplement set. --collect runs the complete declared inventory and requires the .NET SDK, test projects, and collector prerequisites; it defaults to one worker with restore and build enabled.")]
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

[Command("coverage fan-in compose", Description = "Compose one primary capture and the exact declared supplementary captures using the requested fresh output mode. Requires complete v2 captures, matching source/run identity, and a clean Git source. Next run accept; composition alone does not accept coverage.")]
internal sealed partial class CoverageFanInComposeCommand : CoverageFanInCommandBase
{
    public CoverageFanInComposeCommand() : base("compose", requiresResult: true) { }
    internal CoverageFanInComposeCommand(ICoverageFanInResultDeliveryFactory deliveryFactory)
        : base("compose", requiresResult: true, deliveryFactory) { }
}

[Command("coverage fan-in accept", Description = "Run the declared aggregate gate, requested patch gate, and independent verification before accepting a generation. Requires primary and supplementary captures, a composed report, explicit thresholds and diff policy, and a clean Git source. Next run verify against the accepted generation.")]
internal sealed partial class CoverageFanInAcceptCommand : CoverageFanInCommandBase
{
    public CoverageFanInAcceptCommand() : base("accept", requiresResult: true) { }
    internal CoverageFanInAcceptCommand(ICoverageFanInResultDeliveryFactory deliveryFactory)
        : base("accept", requiresResult: true, deliveryFactory) { }
}

[Command("coverage fan-in verify", Description = "Read-only verification of an existing accepted generation; requires the matching primary/supplementary captures, composed output, policy, source identity, and acceptance directory. Creates no coordination or report files. Next consume the generation only when verified=true; --result is optional and must be fresh if supplied.")]
internal sealed partial class CoverageFanInVerifyCommand : CoverageFanInCommandBase
{
    public CoverageFanInVerifyCommand() : base("verify", requiresResult: false) { }
    internal CoverageFanInVerifyCommand(ICoverageFanInResultDeliveryFactory deliveryFactory)
        : base("verify", requiresResult: false, deliveryFactory) { }
}

internal sealed record ParsedCoverageFanInRequest(string Operation, object Request);

internal sealed record CoverageFanInCliEnvelope(int SchemaVersion, string Operation, string Status,
    string? OperationId, CoverageOperationOutcome? OperationOutcome, CoverageOperationEffects? Effects,
    bool EffectsKnown, CoverageFanInCliResult? Result, CoverageFanInCliDelivery Delivery,
    IReadOnlyList<CoverageFanInCliDiagnostic> Diagnostics);

/// <summary>Terminal-safe projection of the public result that omits caller and host filesystem paths.</summary>
internal sealed record CoverageFanInCliResult(string Operation, CoverageOperationOutcome Outcome, string OperationId,
    string? PolicyDigest, CoverageOperationEffects Effects, IReadOnlyList<CoverageFanInCliArtifact> Artifacts,
    IReadOnlyList<CoverageDiagnostic> Diagnostics, bool Accepted, bool Verified, string PatchOutcome,
    CoverageObservation? Observation, CoverageEvaluationIntent EvaluationIntent)
{
    internal static CoverageFanInCliResult From(CoverageFanInResult result) => new(
        result.Operation, result.Outcome, result.OperationId, result.PolicyDigest, result.Effects,
        result.Artifacts.Select(artifact => new CoverageFanInCliArtifact(
            SafeRole(artifact.Role), artifact.Length, artifact.Sha256)).ToArray(),
        result.Diagnostics, result.Accepted, result.Verified, result.PatchOutcome, result.Observation,
        result.EvaluationIntent);

    private static string SafeRole(string role) => role switch
    {
        "acceptance.json" => "acceptance",
        "collector.cobertura.xml" => "collectorCoverage",
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
        _ => "artifact",
    };
}

internal sealed record CoverageFanInCliArtifact(string Role, long Length, string Sha256);

internal sealed record CoverageFanInCliDelivery(bool Requested, string Status, bool ResultFileCreated,
    bool? ResultFileCommitted);

internal sealed record CoverageFanInCliDiagnostic(string Code, string Stage, string Field, string Recovery);

internal sealed class CoverageFanInCliInputException : Exception { }

internal static class CoverageFanInRequestReader
{
    internal static ParsedCoverageFanInRequest Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new CoverageFanInCliInputException();
        CoveragePolicyEncoding.CheckSyntax(root);
        var rootFields = ReadExactObject(root, "schemaVersion", "operation", "request");
        if (!rootFields["schemaVersion"].TryGetInt32(out var version) || version != 2
            || rootFields["operation"].ValueKind != JsonValueKind.String)
            throw new CoverageFanInCliInputException();
        var operation = rootFields["operation"].GetString();
        var requestElement = rootFields["request"];
        var (type, requiredMembers) = operation switch
        {
            "capture" => (typeof(CoverageCaptureRequest), new[] { "policy", "repositoryRoot", "outputDirectory", "producerId" }),
            "collect" => (typeof(CoverageCollectRequest), new[] { "policy", "repositoryRoot", "outputDirectory", "collectionDirectory", "producerId", "deadlineSeconds" }),
            "compose" => (typeof(CoverageComposeRequest), new[] { "policy", "repositoryRoot", "primaryCaptureDirectory", "supplementaryCaptureDirectories", "outputDirectory" }),
            "accept" => (typeof(CoverageAcceptRequest), new[] { "policy", "repositoryRoot", "primaryCaptureDirectory", "supplementaryCaptureDirectories", "composedOutputDirectory", "outputDirectory" }),
            "verify" => (typeof(CoverageVerifyRequest), new[] { "policy", "repositoryRoot", "primaryCaptureDirectory", "supplementaryCaptureDirectories", "composedOutputDirectory", "acceptedOutputDirectory" }),
            _ => throw new CoverageFanInCliInputException(),
        };
        ValidateRequired(requestElement, requiredMembers);
        ValidateRequestShape(type, requestElement);
        var request = JsonSerializer.Deserialize(requestElement, type, CoveragePolicyEncoding.Json)
            ?? throw new CoverageFanInCliInputException();
        return new(operation!, request);
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
                        || capture.TimingsPath is not null || capture.RawCoveragePath is not null)
                        throw new CoverageFanInCliInputException();
                }
                else if (!HasText(capture.CoveragePath) || capture.OriginalSourceRoot is not null
                    || (capture.ProducerId == "primary"
                        ? !HasText(capture.TimingsPath) || capture.RawCoveragePath is not null
                        : !HasText(capture.RawCoveragePath) || capture.TimingsPath is not null))
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

    private static Dictionary<string, JsonElement> ReadExactObject(JsonElement element, params string[] expectedNames)
    {
        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) throw new CoverageFanInCliInputException();
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, property.Value))
                throw new CoverageFanInCliInputException();
        }
        if (expected.Any(name => !result.ContainsKey(name))) throw new CoverageFanInCliInputException();
        return result;
    }

    private static void ValidateRequired(JsonElement request, IReadOnlyList<string> required)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new CoverageFanInCliInputException();
        var members = request.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        if (required.Any(name => !members.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null))
            throw new CoverageFanInCliInputException();
    }

    private static void ValidateRequestShape(Type type, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new CoverageFanInCliInputException();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is not null)
            .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), property => property, StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
        {
            if (!properties.TryGetValue(member.Name, out var property) || !values.TryAdd(member.Name, member.Value))
                throw new CoverageFanInCliInputException();
            ValidateValue(property.PropertyType, member.Value, new NullabilityInfoContext().Create(property));
        }

        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(candidate => candidate.GetParameters().Length).FirstOrDefault()
            ?? throw new CoverageFanInCliInputException();
        foreach (var parameter in constructor.GetParameters().Where(parameter => !parameter.IsOptional))
        {
            var memberName = JsonNamingPolicy.CamelCase.ConvertName(parameter.Name ?? string.Empty);
            if (!values.ContainsKey(memberName)) throw new CoverageFanInCliInputException();
        }
    }

    private static void ValidateValue(Type type, JsonElement value, NullabilityInfo? nullability)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(type);
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (nullableUnderlying is not null || (!type.IsValueType && nullability?.ReadState == NullabilityState.Nullable)) return;
            throw new CoverageFanInCliInputException();
        }
        if (nullableUnderlying is not null) type = nullableUnderlying;

        if (type == typeof(string))
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new CoverageFanInCliInputException();
            return;
        }
        if (type == typeof(bool))
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new CoverageFanInCliInputException();
            return;
        }
        if (type == typeof(int))
        {
            _ = value.Deserialize<int>(CoveragePolicyEncoding.Json);
            return;
        }
        if (type == typeof(long))
        {
            _ = value.Deserialize<long>(CoveragePolicyEncoding.Json);
            return;
        }
        if (type == typeof(decimal))
        {
            if (!value.TryGetDecimal(out _)) throw new CoverageFanInCliInputException();
            return;
        }
        if (type.IsEnum)
        {
            if (value.ValueKind != JsonValueKind.String || !StrictCoverageEnumConverterFactory.IsToken(type, value.GetString()))
                throw new CoverageFanInCliInputException();
            return;
        }
        if (type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            if (value.ValueKind != JsonValueKind.Array) throw new CoverageFanInCliInputException();
            var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var elementNullability = nullability?.GenericTypeArguments.FirstOrDefault();
            foreach (var item in value.EnumerateArray()) ValidateValue(elementType, item, elementNullability);
            return;
        }
        ValidateRequestShape(type, value);
    }
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
