using System.Globalization;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Renders the same bounded classified result as stable v1 JSON or operator-oriented text.</summary>
/// <remarks>
/// JSON uses an explicit writer so every property, null, array and ordering decision is fixed. Unsupported or
/// malformed render inputs throw a fixed contract exception before output is written. Both formats are capped at 32 KiB and
/// end with exactly one line feed. See <see cref="DurableDoctorClassifier"/> for the observation contract.
/// </remarks>
internal static class DurableDoctorRenderer
{
    internal const int MaximumOutputBytes = 32 * 1024;

    /// <summary>Renders one result as JSON only for the exact format token <c>json</c>; all other tokens use safe text.</summary>
    /// <param name="result">The classifier's immutable result.</param>
    /// <param name="request">The validated intent used to produce result and command actions, or null for invalid input.</param>
    /// <param name="format">The validated format; an unknown value safely falls back to text.</param>
    /// <returns>A bounded final-newline string.</returns>
    internal static string Render(DurableDoctorResult result, DurableDoctorRequest? request, string format)
    {
        var json = string.Equals(format, "json", StringComparison.Ordinal);
        bool renderable;
        try
        {
            renderable = IsRenderable(result, request);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or NullReferenceException or OverflowException or InvalidDataException or UriFormatException)
        {
            throw InvalidResult();
        }

        if (!renderable)
        {
            throw InvalidResult();
        }

        var rendered = json ? RenderJson(result) : RenderText(result);
        if (Encoding.UTF8.GetByteCount(rendered) <= MaximumOutputBytes)
        {
            return rendered;
        }

        throw new InvalidOperationException("Durable doctor output exceeds the v1 byte limit.");
    }

    private static string RenderJson(DurableDoctorResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("status", result.Status);
            writer.WriteNumber("exitCode", result.ExitCode);
            WriteChecks(writer, result.RequestedChecks);
            WriteTimestamp(writer, "observedAtUtc", result.ObservedAtUtc);
            WriteSchema(writer, result.Schema);
            WriteGuid(writer, "storeId", result.StoreId);
            WriteGuid(writer, "configuredRuntimeEpoch", result.ConfiguredRuntimeEpoch);
            WriteGuid(writer, "activeRuntimeEpoch", result.ActiveRuntimeEpoch);
            WriteCredential(writer, result.Credential);
            WriteRetention(writer, result.Retention);
            WriteWorker(writer, result.Worker);
            WriteFindings(writer, result.Findings);
            writer.WritePropertyName("nextAction");
            WriteAction(writer, result.NextAction);
            writer.WriteEndObject();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteChecks(Utf8JsonWriter writer, IReadOnlyList<DurableDoctorCheckResult> checks)
    {
        writer.WritePropertyName("requestedChecks");
        writer.WriteStartArray();
        foreach (var check in checks)
        {
            writer.WriteStartObject();
            writer.WriteString("name", check.Name);
            writer.WriteBoolean("requested", check.Requested);
            writer.WriteString("status", check.Status);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSchema(Utf8JsonWriter writer, DurableDoctorSchemaResult? schema)
    {
        writer.WritePropertyName("schema");
        if (schema is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("compatibility", schema.Compatibility);
        WriteNullableNumber(writer, "installedVersion", schema.InstalledVersion);
        writer.WriteNumber("requiredVersion", schema.RequiredVersion);
        WriteNullableNumber(writer, "minimumReaderVersion", schema.MinimumReaderVersion);
        WriteNullableNumber(writer, "maximumReaderVersion", schema.MaximumReaderVersion);
        WriteNullableNumber(writer, "minimumWriterVersion", schema.MinimumWriterVersion);
        WriteNullableNumber(writer, "maximumWriterVersion", schema.MaximumWriterVersion);
        WriteIntegerArray(writer, "appliedVersions", schema.AppliedVersions);
        WriteIntegerArray(writer, "pendingVersions", schema.PendingVersions);
        writer.WriteEndObject();
    }

    private static void WriteCredential(Utf8JsonWriter writer, DurableDoctorCredentialResult? credential)
    {
        writer.WritePropertyName("credential");
        if (credential is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteBoolean("restricted", credential.Restricted);
        WriteStringArray(writer, "failedChecks", credential.FailedChecks);
        writer.WriteEndObject();
    }

    private static void WriteRetention(Utf8JsonWriter writer, DurableDoctorRetentionResult? retention)
    {
        writer.WritePropertyName("retention");
        if (retention is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteBoolean("available", retention.Available);
        WriteStringArray(writer, "failedChecks", retention.FailedChecks);
        writer.WriteEndObject();
    }

    private static void WriteWorker(Utf8JsonWriter writer, DurableDoctorWorkerResult? worker)
    {
        writer.WritePropertyName("worker");
        if (worker is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("workerId", worker.WorkerId);
        writer.WriteBoolean("found", worker.Found);
        writer.WriteString("state", worker.State);
        WriteGuid(writer, "runtimeEpoch", worker.RuntimeEpoch);
        WriteTimestamp(writer, "lastHeartbeatAtUtc", worker.LastHeartbeatAtUtc);
        WriteNullableNumber(writer, "ageTicks", worker.AgeTicks);
        writer.WriteNumber("staleAfterTicks", worker.StaleAfterTicks);
        if (worker.IsDraining is { } isDraining)
        {
            writer.WriteBoolean("isDraining", isDraining);
        }
        else
        {
            writer.WriteNull("isDraining");
        }
        writer.WriteEndObject();
    }

    private static void WriteFindings(Utf8JsonWriter writer, IReadOnlyList<DurableDoctorFinding> findings)
    {
        writer.WritePropertyName("findings");
        writer.WriteStartArray();
        foreach (var finding in findings)
        {
            writer.WriteStartObject();
            writer.WriteString("code", finding.Code);
            writer.WriteString("problem", finding.Problem);
            writer.WriteString("cause", finding.Cause);
            writer.WriteString("fix", finding.Fix);
            writer.WriteString("documentationUrl", finding.DocumentationUrl.AbsoluteUri);
            WriteStringArray(writer, "failedChecks", finding.FailedChecks);
            writer.WritePropertyName("nextAction");
            WriteAction(writer, finding.NextAction);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteAction(Utf8JsonWriter writer, DurableDoctorAction action)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", action.Kind);
        writer.WritePropertyName("command");
        if (action.Command is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("executable", action.Command.Executable);
            WriteStringArray(writer, "arguments", action.Command.Arguments);
            writer.WriteEndObject();
        }
        WriteStringArray(writer, "requiredInputs", action.RequiredInputs);
        writer.WriteString("documentationUrl", action.DocumentationUrl.AbsoluteUri);
        writer.WriteEndObject();
    }

    private static void WriteGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } guid)
        {
            writer.WriteString(name, guid.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } timestamp)
        {
            writer.WriteString(name, timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableNumber<T>(Utf8JsonWriter writer, string name, T? value)
        where T : struct
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, Convert.ToInt64(number, CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteIntegerArray(Utf8JsonWriter writer, string name, IReadOnlyList<int>? values)
    {
        writer.WritePropertyName(name);
        if (values is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteNumberValue(value);
        }
        writer.WriteEndArray();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    private static string RenderText(DurableDoctorResult result)
    {
        var builder = new StringBuilder();
        Line(builder, $"Diagnosis: {result.Status} (exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)})");
        Line(builder, $"Observed at (UTC): {result.ObservedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "not captured"}");
        Line(builder, "Checks:");
        foreach (var check in result.RequestedChecks)
        {
            Line(builder, $"  {check.Name}: {check.Status}");
        }

        if (result.Status == "passed")
        {
            Line(builder, "Store/runtime checks passed.");
            WriteCleanWorkerIntent(builder, result);
            Line(builder, "Boundary: These checks passed only for this captured observation; they do not prove deployment authorization, process ownership, future readiness, or successful application execution.");
            Line(builder, "Next action: Run the application's composition verifier; this standalone doctor cannot infer its command.");
            Line(builder, "  Required input: consumer verifier command");
            Line(builder, $"  Documentation: {result.NextAction.DocumentationUrl.AbsoluteUri}");
            WriteTextFacts(builder, result, includeWorkerFacts: result.Worker is not null);
            return builder.ToString();
        }

        Line(builder, "Boundary: Findings describe only this non-mutating observation; they do not authorize deployment or prove successful application execution.");
        WriteTextFacts(builder, result, includeWorkerFacts: true);
        for (var index = 0; index < result.Findings.Count; index++)
        {
            var finding = result.Findings[index];
            Line(builder, string.Empty);
            Line(builder, $"Finding {index + 1}:");
            Line(builder, $"  Code: {finding.Code}");
            Line(builder, $"  Problem: {finding.Problem}");
            Line(builder, $"  Cause: {finding.Cause}");
            Line(builder, $"  Fix: {finding.Fix}");
            Line(builder, $"  Failed checks: {string.Join(", ", finding.FailedChecks)}");
            Line(builder, $"  Documentation: {finding.DocumentationUrl.AbsoluteUri}");
            WriteTextAction(builder, finding.NextAction);
        }

        return builder.ToString();
    }

    private static void WriteCleanWorkerIntent(StringBuilder builder, DurableDoctorResult result)
    {
        if (result.Worker is not { } worker)
        {
            Line(builder, "Worker check: not requested; no heartbeat claim is made.");
            return;
        }

        var staleAfter = TimeSpan.FromTicks(worker.StaleAfterTicks).ToString("c", CultureInfo.InvariantCulture);
        Line(builder, $"Worker check: requested for '{worker.WorkerId}'; its heartbeat was observed against the supplied stale threshold {staleAfter}.");
    }

    private static void WriteTextFacts(StringBuilder builder, DurableDoctorResult result, bool includeWorkerFacts)
    {
        Line(builder, $"Store ID: {FormatGuid(result.StoreId)}");
        Line(builder, $"Configured runtime epoch: {FormatGuid(result.ConfiguredRuntimeEpoch)}");
        Line(builder, $"Active runtime epoch: {FormatGuid(result.ActiveRuntimeEpoch)}");
        if (result.Schema is { } schema)
        {
            Line(builder, $"Schema: {schema.Compatibility}; installed={FormatNumber(schema.InstalledVersion)}; required={schema.RequiredVersion.ToString(CultureInfo.InvariantCulture)}");
        }
        if (result.Credential is { } credential)
        {
            Line(builder, $"Credential: restricted={credential.Restricted.ToString().ToLowerInvariant()}; failed checks={FormatCategories(credential.FailedChecks)}");
        }
        if (result.Retention is { } retention)
        {
            Line(builder, $"Retention: available={retention.Available.ToString().ToLowerInvariant()}; failed checks={FormatCategories(retention.FailedChecks)}");
        }
        if (!includeWorkerFacts)
        {
            return;
        }

        if (result.Worker is { } worker)
        {
            Line(builder, $"Worker: id={worker.WorkerId}; state={worker.State}; found={worker.Found.ToString().ToLowerInvariant()}; ageTicks={FormatNumber(worker.AgeTicks)}; staleAfterTicks={worker.StaleAfterTicks.ToString(CultureInfo.InvariantCulture)}; draining={worker.IsDraining?.ToString().ToLowerInvariant() ?? "unavailable"}");
        }
        else if (result.RequestedChecks[4].Requested)
        {
            Line(builder, "Worker: requested; no compatible-schema heartbeat observation was captured.");
        }
        else
        {
            Line(builder, "Worker: not requested; no heartbeat claim is made.");
        }
    }

    private static void WriteTextAction(StringBuilder builder, DurableDoctorAction action)
    {
        if (action.Command is { } command)
        {
            var words = new[] { command.Executable }.Concat(command.Arguments).Select(QuotePosixShellArgument);
            Line(builder, $"  Next command: {string.Join(" ", words)}");
        }
        foreach (var requiredInput in action.RequiredInputs)
        {
            Line(builder, $"  Required input: {requiredInput}");
        }
        Line(builder, $"  Action documentation: {action.DocumentationUrl.AbsoluteUri}");
    }

    private static bool IsRenderable(DurableDoctorResult? result, DurableDoctorRequest? request)
    {
        if (result is null || !IsMappedStatus(result.Status, result.ExitCode)
            || result.RequestedChecks.Count != 5
            || result.Findings.Count > 12
            || result.Findings.Any(static finding => finding is null)
            || result.Findings.Select(static finding => finding.Code).Distinct(StringComparer.Ordinal).Count() != result.Findings.Count)
        {
            return false;
        }

        if (request is not null && DurableDoctorClassifier.IsValidRequest(request)
            && result.ConfiguredRuntimeEpoch != request.ConfiguredRuntimeEpoch)
        {
            return false;
        }
        if (!ChecksAreValid(result.RequestedChecks, request))
        {
            return false;
        }

        if (result.Status is "invalid-input" or "unavailable" or "canceled" or "failed")
        {
            var terminalCode = result.Status switch
            {
                "invalid-input" => DurableProblemCodes.DoctorInputInvalid,
                "unavailable" => DurableProblemCodes.StoreUnavailable,
                "canceled" => DurableProblemCodes.DoctorCanceled,
                _ => DurableProblemCodes.DoctorContractFailed,
            };
            if (result.Findings.Count != 1
                || result.ObservedAtUtc is not null
                || result.Schema is not null
                || result.StoreId is not null
                || result.ActiveRuntimeEpoch is not null
                || result.Credential is not null
                || result.Retention is not null
                || result.Worker is not null
                || result.Findings[0].Code != terminalCode
                || !TerminalCategoriesAreValid(result.Status, result.Findings[0].FailedChecks))
            {
                return false;
            }
        }
        else if (result.Status == "passed" && result.Findings.Count != 0
            || result.Status == "findings" && result.Findings.Count == 0)
        {
            return false;
        }

        foreach (var finding in result.Findings)
        {
            if (!FindingIsCanonical(finding, request))
            {
                return false;
            }
        }
        if (!FactsAreConsistent(result, request))
        {
            return false;
        }
        var expectedAction = result.Findings.Count == 0
            ? CreateExpectedVerifierAction()
            : result.Findings[0].NextAction;
        return ActionsEqual(result.NextAction, expectedAction);
    }

    private static bool ChecksAreValid(IReadOnlyList<DurableDoctorCheckResult> checks, DurableDoctorRequest? request)
    {
        var validRequest = request is not null && DurableDoctorClassifier.IsValidRequest(request);
        var names = new[] { "credential", "schema", "epoch", "retention", "worker" };
        for (var index = 0; index < checks.Count; index++)
        {
            var check = checks[index];
            var isRequested = validRequest && (index != 4 || request!.WorkerId is not null);
            if (!string.Equals(check.Name, names[index], StringComparison.Ordinal)
                || check.Requested != isRequested
                || check.Status is not ("passed" or "finding" or "not-checked" or "not-requested")
                || check.Requested == (check.Status == "not-requested"))
            {
                return false;
            }
            if (!validRequest && check.Status != "not-requested")
            {
                return false;
            }
            if (check.Status == "not-checked" && !check.Requested)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TerminalCategoriesAreValid(string status, IReadOnlyList<string> categories) => status switch
    {
        "invalid-input" => categories.SequenceEqual(["input"], StringComparer.Ordinal),
        "unavailable" => categories.Count is >= 1 and <= 4
            && categories.All(static category => category is "session-affinity" or "dependency" or "deadline" or "cleanup")
            && categories.Distinct(StringComparer.Ordinal).Count() == categories.Count
            && new[] { "session-affinity", "dependency", "deadline", "cleanup" }
                .Where(categories.Contains).SequenceEqual(categories, StringComparer.Ordinal),
        "canceled" => categories.SequenceEqual(["caller-canceled"], StringComparer.Ordinal),
        "failed" => categories.SequenceEqual(["catalog-contract"], StringComparer.Ordinal),
        _ => false,
    };

    private static bool FactsAreConsistent(DurableDoctorResult result, DurableDoctorRequest? request)
    {
        var validRequest = request is not null && DurableDoctorClassifier.IsValidRequest(request);
        Guid? expectedEpoch = validRequest ? request!.ConfiguredRuntimeEpoch : null;
        if (result.ConfiguredRuntimeEpoch != expectedEpoch)
        {
            return false;
        }

        if (result.Status is "invalid-input" or "unavailable" or "canceled" or "failed")
        {
            return result.RequestedChecks.All(static check => check.Status is "not-checked" or "not-requested")
                && result.ObservedAtUtc is null
                && result.Schema is null
                && result.StoreId is null
                && result.ActiveRuntimeEpoch is null
                && result.Credential is null
                && result.Retention is null
                && result.Worker is null;
        }

        if (result.Credential is not { } credential)
        {
            return false;
        }

        if (!credential.Restricted)
        {
            return validRequest
                && credential.FailedChecks.Count > 0
                && IsOrderedSubset(credential.FailedChecks, DurableDoctorChecks.Credential)
                && result.Status == "findings"
                && result.Findings.Count == 1
                && result.Findings[0].Code == DurableProblemCodes.RestrictedRuntimeCredentialRequired
                && result.Findings[0].FailedChecks.SequenceEqual(credential.FailedChecks, StringComparer.Ordinal)
                && result.RequestedChecks[0].Status == "finding"
                && result.RequestedChecks.Skip(1).All(static check => check.Status is "not-checked" or "not-requested")
                && result.ObservedAtUtc is null
                && result.Schema is null
                && result.StoreId is null
                && result.ActiveRuntimeEpoch is null
                && result.Retention is null
                && result.Worker is null;
        }

        if (credential.FailedChecks.Count != 0
            || result.RequestedChecks[0].Status != "passed"
            || result.Schema is not { } schema
            || !SchemaShapeIsValid(schema))
        {
            return false;
        }

        if (schema.Compatibility is "missing" or "inconsistent" or "upgrade-required" or "store-too-new")
        {
            var expectedCode = schema.Compatibility switch
            {
                "missing" => DurableProblemCodes.SchemaMissing,
                "upgrade-required" => DurableProblemCodes.SchemaUpgradeRequired,
                "store-too-new" => DurableProblemCodes.SchemaVersionUnsupported,
                _ => DurableProblemCodes.SchemaInconsistent,
            };
            var identityIsValid = schema.Compatibility switch
            {
                "missing" or "inconsistent" => result.StoreId is null && result.ActiveRuntimeEpoch is null,
                _ => result.StoreId is { } store && store != Guid.Empty
                    && (result.ActiveRuntimeEpoch is null || result.ActiveRuntimeEpoch.Value != Guid.Empty),
            };
            return validRequest
                && result.Status == "findings"
                && result.Findings.Count == 1
                && result.Findings[0].Code == expectedCode
                && result.Findings[0].FailedChecks.SequenceEqual(["schema-compatibility"], StringComparer.Ordinal)
                && result.RequestedChecks[1].Status == "finding"
                && result.RequestedChecks.Skip(2).All(static check => check.Status is "not-checked" or "not-requested")
                && result.Retention is null
                && result.Worker is null
                && result.ObservedAtUtc is null
                && (result.ActiveRuntimeEpoch is null || result.ActiveRuntimeEpoch != Guid.Empty)
                && identityIsValid;
        }

        return CompatibleFactsAreValid(result, request, schema);
    }

    private static bool CompatibleFactsAreValid(
        DurableDoctorResult result,
        DurableDoctorRequest? request,
        DurableDoctorSchemaResult schema)
    {
        if (request is null || !DurableDoctorClassifier.IsValidRequest(request)
            || result.Status is not ("passed" or "findings")
            || schema.Compatibility != "compatible"
            || result.RequestedChecks[1].Status != "passed"
            || result.ObservedAtUtc is not { } observedAt
            || !IsObservationTimestampValid(observedAt)
            || result.StoreId is not { } storeId || storeId == Guid.Empty
            || result.ActiveRuntimeEpoch == Guid.Empty
            || result.Retention is not { } retention
            || retention.Available != (retention.FailedChecks.Count == 0)
            || !IsOrderedSubset(retention.FailedChecks, DurableDoctorChecks.Retention)
            || result.RequestedChecks[3].Status != (retention.Available ? "passed" : "finding")
            || !WorkerShapeIsValid(result.Worker, result.ActiveRuntimeEpoch, observedAt, request, result.RequestedChecks[4].Status)
            || result.RequestedChecks[2].Status != (result.ActiveRuntimeEpoch == request.ConfiguredRuntimeEpoch ? "passed" : "finding"))
        {
            return false;
        }

        var expectedCodes = new List<string>(3);
        if (result.ActiveRuntimeEpoch != request.ConfiguredRuntimeEpoch)
        {
            expectedCodes.Add(DurableProblemCodes.RecoveryEpochRequired);
        }
        if (!retention.Available)
        {
            expectedCodes.Add(DurableProblemCodes.HeartbeatRetentionUnavailable);
        }
        if (result.ActiveRuntimeEpoch == request.ConfiguredRuntimeEpoch && result.Worker is { } worker)
        {
            if (!worker.Found)
            {
                expectedCodes.Add(DurableProblemCodes.WorkerHeartbeatMissing);
            }
            else if (worker.RuntimeEpoch != request.ConfiguredRuntimeEpoch)
            {
                expectedCodes.Add(DurableProblemCodes.WorkerHeartbeatEpochMismatch);
            }
            else if (worker.IsDraining == true)
            {
                expectedCodes.Add(DurableProblemCodes.WorkerDraining);
            }
            else if (worker.AgeTicks > worker.StaleAfterTicks)
            {
                expectedCodes.Add(DurableProblemCodes.ActivatorStale);
            }
        }

        return result.Findings.Select(static finding => finding.Code).SequenceEqual(expectedCodes, StringComparer.Ordinal)
            && (expectedCodes.Count == 0) == (result.Status == "passed");
    }

    private static bool WorkerShapeIsValid(
        DurableDoctorWorkerResult? worker,
        Guid? activeEpoch,
        DateTimeOffset observedAt,
        DurableDoctorRequest request,
        string checkStatus)
    {
        if (request.WorkerId is null)
        {
            return worker is null && checkStatus == "not-requested";
        }
        if (worker is null || checkStatus is not ("passed" or "finding")
            || worker.WorkerId != request.WorkerId
            || worker.StaleAfterTicks != request.StaleAfter!.Value.Ticks
            || worker.StaleAfterTicks <= 0)
        {
            return false;
        }

        string expectedState;
        if (!worker.Found)
        {
            if (worker.RuntimeEpoch is not null || worker.LastHeartbeatAtUtc is not null
                || worker.AgeTicks is not null || worker.IsDraining is not null)
            {
                return false;
            }
            expectedState = activeEpoch == request.ConfiguredRuntimeEpoch ? "not-started" : "epoch-incompatible";
        }
        else
        {
            if (worker.RuntimeEpoch is not { } rowEpoch || rowEpoch == Guid.Empty
                || worker.LastHeartbeatAtUtc is not { } heartbeatAt
                || !IsObservationTimestampValid(heartbeatAt)
                || worker.IsDraining is not { } isDraining
                || worker.AgeTicks is not { } ageTicks || ageTicks < 0
                || ageTicks != Math.Max(0L, (observedAt - heartbeatAt).Ticks))
            {
                return false;
            }

            expectedState = activeEpoch != request.ConfiguredRuntimeEpoch || rowEpoch != request.ConfiguredRuntimeEpoch
                ? "epoch-incompatible"
                : isDraining
                    ? "draining"
                    : ageTicks > worker.StaleAfterTicks
                        ? "stale"
                        : "current";
        }

        var shouldHaveWorkerFinding = activeEpoch != request.ConfiguredRuntimeEpoch
            || expectedState is "not-started" or "epoch-incompatible" or "draining" or "stale";
        return worker.State == expectedState
            && checkStatus == (shouldHaveWorkerFinding ? "finding" : "passed");
    }

    private static bool SchemaShapeIsValid(DurableDoctorSchemaResult schema)
    {
        if (schema.RequiredVersion is < 1 or > 64)
        {
            return false;
        }

        if (schema.Compatibility == "inconsistent")
        {
            return schema.InstalledVersion is null
                && schema.MinimumReaderVersion is null
                && schema.MaximumReaderVersion is null
                && schema.MinimumWriterVersion is null
                && schema.MaximumWriterVersion is null
                && schema.AppliedVersions is null
                && schema.PendingVersions is null;
        }

        if (schema.Compatibility == "missing")
        {
            return schema.InstalledVersion == 0
                && schema.MinimumReaderVersion is null
                && schema.MaximumReaderVersion is null
                && schema.MinimumWriterVersion is null
                && schema.MaximumWriterVersion is null
                && schema.AppliedVersions is { Count: 0 }
                && schema.PendingVersions is { } pendingMissing
                && pendingMissing.Count == schema.RequiredVersion
                && pendingMissing.Select((value, index) => value == index + 1).All(static contiguous => contiguous);
        }

        if (schema.Compatibility is not ("compatible" or "upgrade-required" or "store-too-new")
            || schema.InstalledVersion is not { } installed || installed < 0
            || schema.MinimumReaderVersion is not { } minReader
            || schema.MaximumReaderVersion is not { } maxReader
            || schema.MinimumWriterVersion is not { } minWriter
            || schema.MaximumWriterVersion is not { } maxWriter
            || minReader < 0 || maxReader < minReader || minWriter < 0 || maxWriter < minWriter
            || schema.AppliedVersions is not { } applied || schema.PendingVersions is not { } pending
            || !VersionsAreValid(applied) || !VersionsAreValid(pending)
            || applied.Any(version => version > installed)
            || pending.Any(version => version <= installed)
            || applied.Intersect(pending).Any())
        {
            return false;
        }

        var supportsPackage = schema.RequiredVersion >= minReader && schema.RequiredVersion <= maxReader
            && schema.RequiredVersion >= minWriter && schema.RequiredVersion <= maxWriter;
        return schema.Compatibility switch
        {
            "compatible" => installed >= schema.RequiredVersion && supportsPackage,
            "upgrade-required" => installed < schema.RequiredVersion,
            "store-too-new" => installed >= schema.RequiredVersion && !supportsPackage,
            _ => false,
        };
    }

    private static bool VersionsAreValid(IReadOnlyList<int>? versions) => versions is { Count: <= 64 }
        && versions.All(static version => version > 0)
        && versions.SequenceEqual(versions.OrderBy(static version => version))
        && versions.Distinct().Count() == versions.Count;

    private static bool IsOrderedSubset(IReadOnlyList<string> values, IReadOnlyList<string> allowed) =>
        values.Count <= allowed.Count
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count
        && allowed.Where(values.Contains).SequenceEqual(values, StringComparer.Ordinal);

    private static bool IsObservationTimestampValid(DateTimeOffset timestamp) =>
        timestamp != DateTimeOffset.MinValue && timestamp != DateTimeOffset.MaxValue;

    private static InvalidOperationException InvalidResult() =>
        new("Durable doctor result does not satisfy the v1 rendering contract.");

    private static bool FindingIsCanonical(DurableDoctorFinding finding, DurableDoctorRequest? request)
    {
        if (!DurableDiagnosticCatalog.TryGet(finding.Code, out var descriptor)
            || descriptor is null
            || descriptor.Code != finding.Code
            || descriptor.Problem != finding.Problem
            || descriptor.Cause != finding.Cause
            || descriptor.Fix != finding.Fix
            || !descriptor.DocumentationUrl.Equals(finding.DocumentationUrl)
            || !IsHttps(finding.DocumentationUrl)
            || finding.FailedChecks.Count is < 1 or > 28
            || finding.FailedChecks.Any(category => category is null || !DurableDoctorChecks.All.Contains(category, StringComparer.Ordinal))
            || finding.FailedChecks.Distinct(StringComparer.Ordinal).Count() != finding.FailedChecks.Count)
        {
            return false;
        }

        var sorted = DurableDoctorChecks.All.Where(finding.FailedChecks.Contains).ToArray();
        if (!finding.FailedChecks.SequenceEqual(sorted, StringComparer.Ordinal))
        {
            return false;
        }
        var expectedAction = CreateExpectedAction(finding.Code, request, descriptor.DocumentationUrl);
        return ActionsEqual(finding.NextAction, expectedAction)
            && finding.NextAction.DocumentationUrl.Equals(finding.DocumentationUrl);
    }

    private static DurableDoctorAction CreateExpectedAction(string code, DurableDoctorRequest? request, Uri url)
    {
        if (request is not null && DurableDoctorClassifier.IsValidRequest(request))
        {
            return DurableDoctorClassifier.ExpectedActionFor(request, code);
        }
        var command = new DurableDoctorCommandAction("appsurface", ["durable", "doctor", "--help"]);
        return new DurableDoctorAction("command", command, [], url);
    }

    private static DurableDoctorAction CreateExpectedVerifierAction() =>
        new(
            "application-verifier",
            null,
            ["consumer verifier command"],
            new Uri("https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/README.md", UriKind.Absolute));

    private static bool ActionsEqual(DurableDoctorAction? actual, DurableDoctorAction expected)
    {
        if (actual is null || actual.Kind != expected.Kind || actual.Command?.Executable != expected.Command?.Executable
            || !actual.RequiredInputs.SequenceEqual(expected.RequiredInputs, StringComparer.Ordinal)
            || !actual.DocumentationUrl.Equals(expected.DocumentationUrl))
        {
            return false;
        }
        if (actual.Command is null || expected.Command is null)
        {
            return actual.Command is null && expected.Command is null;
        }
        return actual.Command.Arguments.SequenceEqual(expected.Command.Arguments, StringComparer.Ordinal);
    }

    private static bool IsMappedStatus(string status, int exitCode) => (status, exitCode) switch
    {
        ("passed", 0) or ("findings", 2) or ("invalid-input", 3) or ("unavailable", 4) or ("canceled", 1) or ("failed", 1) => true,
        _ => false,
    };

    private static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps;

    private static string FormatGuid(Guid? value) => value?.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant() ?? "unavailable";

    private static string FormatNumber<T>(T? value) where T : struct =>
        value is { } number ? Convert.ToString(number, CultureInfo.InvariantCulture) ?? "unavailable" : "unavailable";

    private static string FormatCategories(IReadOnlyList<string> categories) =>
        categories.Count == 0 ? "none" : string.Join(", ", categories);

    private static string QuotePosixShellArgument(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void Line(StringBuilder builder, string value) => builder.Append(value).Append('\n');
}
