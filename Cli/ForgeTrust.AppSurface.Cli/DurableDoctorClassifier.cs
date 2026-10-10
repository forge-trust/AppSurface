using System.Globalization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Purely classifies one completed Durable doctor observation and its validated request.</summary>
/// <remarks>
/// This type performs no I/O and reads no process clock. A compatible schema always receives an independent
/// store-epoch check; selected worker evidence is interpreted only after that prerequisite. See the durable doctor
/// contract for the v1 short-circuit, category, and output matrices.
/// </remarks>
internal static partial class DurableDoctorClassifier
{
    private const int MaximumSchemaVersions = 64;
    private const string ApplicationVerifierUrl =
        "https://github.com/forge-trust/AppSurface/blob/main/examples/durable-external-activation/README.md";
    private static readonly string[] CheckNames = ["credential", "schema", "epoch", "retention", "worker"];
    private static readonly string[] TerminalUnavailableCategories = ["session-affinity", "dependency", "deadline", "cleanup"];
    private static readonly Uri ApplicationVerifierDocumentation = new(ApplicationVerifierUrl, UriKind.Absolute);

    /// <summary>Classifies a complete service observation; malformed or incomplete evidence becomes ASDUR415.</summary>
    internal static DurableDoctorResult Classify(DurableDoctorRequest request, DurableDoctorObservation observation)
    {
        if (!IsValidRequest(request))
        {
            return Terminal(null, "invalid-input", ["input"]);
        }

        if (observation is null)
        {
            return Terminal(request, "failed", ["catalog-contract"]);
        }

        try
        {
            return ClassifyValidated(request, observation);
        }
        catch (DoctorContractException)
        {
            return Terminal(request, "failed", ["catalog-contract"]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or OverflowException or NullReferenceException or IndexOutOfRangeException)
        {
            return Terminal(request, "failed", ["catalog-contract"]);
        }
    }

    /// <summary>Builds one fixed terminal envelope, discarding all database evidence and retaining valid request intent.</summary>
    /// <param name="request">Validated request intent, or null when input validation did not construct a request.</param>
    /// <param name="status">One of invalid-input, unavailable, canceled, or failed.</param>
    /// <param name="categories">Fixed categories appropriate to the selected terminal status.</param>
    /// <returns>A result with the canonical descriptor and status-specific exit code.</returns>
    internal static DurableDoctorResult Terminal(
        DurableDoctorRequest? request,
        string status,
        IReadOnlyList<string> categories)
    {
        var validRequest = IsValidRequest(request) ? request : null;
        var terminalStatus = status switch
        {
            "invalid-input" when validRequest is null => "invalid-input",
            "unavailable" => "unavailable",
            "canceled" => "canceled",
            "failed" => "failed",
            _ => "failed",
        };

        var expectedCategories = terminalStatus switch
        {
            "invalid-input" => new[] { "input" },
            "unavailable" => TerminalUnavailableCategories,
            "canceled" => new[] { "caller-canceled" },
            _ => new[] { "catalog-contract" },
        };
        if (!TryNormalizeCategories(categories, expectedCategories, 1, expectedCategories.Length, out var normalized)
            || terminalStatus == "invalid-input" && categories.Count != 1)
        {
            terminalStatus = "failed";
            normalized = ["catalog-contract"];
            validRequest ??= IsValidRequest(request) ? request : null;
        }

        var (code, exitCode) = terminalStatus switch
        {
            "invalid-input" => (DurableProblemCodes.DoctorInputInvalid, 3),
            "unavailable" => (DurableProblemCodes.StoreUnavailable, 4),
            "canceled" => (DurableProblemCodes.DoctorCanceled, 1),
            _ => (DurableProblemCodes.DoctorContractFailed, 1),
        };
        var descriptor = RequireDescriptor(code);
        var action = CreateAction(descriptor.DocumentationUrl, validRequest, terminalStatus == "invalid-input"
            ? ActionKind.Help
            : ActionKind.RetryDoctor);
        var finding = new DurableDoctorFinding(
            descriptor.Code,
            descriptor.Problem,
            descriptor.Cause,
            descriptor.Fix,
            descriptor.DocumentationUrl,
            normalized,
            action);

        var checks = validRequest is null
            ? CreateUnrequestedChecks()
            : CreateChecks(validRequest, "not-checked", "not-checked", "not-checked", "not-checked", "not-checked");
        return new DurableDoctorResult(
            terminalStatus,
            exitCode,
            checks,
            observedAtUtc: null,
            schema: null,
            storeId: null,
            configuredRuntimeEpoch: validRequest?.ConfiguredRuntimeEpoch,
            activeRuntimeEpoch: null,
            credential: null,
            retention: null,
            worker: null,
            findings: [finding],
            nextAction: action);
    }

    /// <summary>Validates command-derived request bounds before using any request value in result or action output.</summary>
    internal static bool IsValidRequest(DurableDoctorRequest? request)
    {
        if (request is null || request.ConfiguredRuntimeEpoch == Guid.Empty
            || !IsEnvironmentName(request.ConnectionEnvironmentName)
            || !IsEnvironmentName(request.EpochEnvironmentName)
            || request.Timeout < TimeSpan.FromSeconds(1)
            || request.Timeout > TimeSpan.FromMinutes(2)
            || request.Format is not ("text" or "json"))
        {
            return false;
        }

        if (request.WorkerId is null)
        {
            return request.StaleAfter is null;
        }

        return IsWorkerId(request.WorkerId)
            && request.StaleAfter is { } staleAfter
            && staleAfter >= TimeSpan.FromSeconds(1)
            && staleAfter <= TimeSpan.FromHours(1);
    }

    private static DurableDoctorResult ClassifyValidated(
        DurableDoctorRequest request,
        DurableDoctorObservation observation)
    {
        var credentialFailures = ValidateCategoryEvidence(
            observation.CredentialFailures,
            DurableDoctorChecks.Credential,
            maximumCount: 6,
            allowNull: false);
        if (credentialFailures.Count != 0)
        {
            if (observation.Schema is not null
                || observation.RetentionFailures is not null
                || observation.ObservedAtUtc is not null
                || observation.StoreId is not null
                || observation.ActiveRuntimeEpoch is not null
                || observation.Heartbeat is not null)
            {
                throw new DoctorContractException();
            }

            var descriptor = RequireDescriptor(DurableProblemCodes.RestrictedRuntimeCredentialRequired);
            var action = CreateAction(descriptor.DocumentationUrl, request, ActionKind.RetryDoctor);
            var finding = Finding(descriptor, credentialFailures, action);
            return new DurableDoctorResult(
                "findings",
                2,
                CreateChecks(request, "finding", "not-checked", "not-checked", "not-checked", "not-checked"),
                observedAtUtc: null,
                schema: null,
                storeId: null,
                configuredRuntimeEpoch: request.ConfiguredRuntimeEpoch,
                activeRuntimeEpoch: null,
                credential: new DurableDoctorCredentialResult(false, credentialFailures),
                retention: null,
                worker: null,
                findings: [finding],
                nextAction: action);
        }

        var schemaStatus = observation.Schema ?? throw new DoctorContractException();
        ValidateSchemaStatus(schemaStatus);
        var projectedSchema = ProjectSchema(schemaStatus);
        if (schemaStatus.Compatibility != DurableRuntimeSchemaCompatibility.Compatible)
        {
            if (observation.RetentionFailures is not null
                || observation.ObservedAtUtc is not null
                || observation.StoreId is not null
                || observation.ActiveRuntimeEpoch is not null
                || observation.Heartbeat is not null)
            {
                throw new DoctorContractException();
            }

            var code = SchemaCode(schemaStatus.Compatibility);
            var descriptor = RequireDescriptor(code);
            var action = CreateAction(descriptor.DocumentationUrl, request, ActionKind.SchemaStatus);
            var finding = Finding(descriptor, ["schema-compatibility"], action);
            Guid? exposedStoreId = schemaStatus.Compatibility is DurableRuntimeSchemaCompatibility.Missing
                or DurableRuntimeSchemaCompatibility.Inconsistent
                ? (Guid?)null
                : schemaStatus.StoreId;
            var exposedActiveEpoch = schemaStatus.Compatibility is DurableRuntimeSchemaCompatibility.Missing
                or DurableRuntimeSchemaCompatibility.Inconsistent
                ? null
                : schemaStatus.ActiveRuntimeEpoch;

            return new DurableDoctorResult(
                "findings",
                2,
                CreateChecks(request, "passed", "finding", "not-checked", "not-checked", "not-checked"),
                observedAtUtc: null,
                schema: projectedSchema,
                storeId: exposedStoreId,
                configuredRuntimeEpoch: request.ConfiguredRuntimeEpoch,
                activeRuntimeEpoch: exposedActiveEpoch,
                credential: new DurableDoctorCredentialResult(true, []),
                retention: null,
                worker: null,
                findings: [finding],
                nextAction: action);
        }

        var retentionFailures = ValidateCategoryEvidence(
            observation.RetentionFailures,
            DurableDoctorChecks.Retention,
            maximumCount: 9,
            allowNull: false);
        var observedAt = observation.ObservedAtUtc ?? throw new DoctorContractException();
        ValidateTimestamp(observedAt);
        var storeId = observation.StoreId is { } observedStoreId && observedStoreId != Guid.Empty
            ? observedStoreId
            : throw new DoctorContractException();
        var activeEpoch = observation.ActiveRuntimeEpoch;
        if (activeEpoch == Guid.Empty || schemaStatus.StoreId != storeId || schemaStatus.ActiveRuntimeEpoch != activeEpoch)
        {
            throw new DoctorContractException();
        }

        var heartbeat = ValidateHeartbeat(request, observation.Heartbeat);
        var findings = new List<DurableDoctorFinding>(3);
        var epochMatches = activeEpoch == request.ConfiguredRuntimeEpoch;
        var epochStatus = epochMatches ? "passed" : "finding";
        var workerStatus = request.WorkerId is null ? "not-requested" : "passed";
        DurableDoctorWorkerResult? worker = null;

        if (!epochMatches)
        {
            var descriptor = RequireDescriptor(DurableProblemCodes.RecoveryEpochRequired);
            var action = CreateAction(descriptor.DocumentationUrl, request, ActionKind.RetryDoctor);
            findings.Add(Finding(descriptor, ["active-epoch"], action));
            if (heartbeat is not null)
            {
                workerStatus = "finding";
                worker = ProjectWorker(request, heartbeat, observedAt, "epoch-incompatible");
            }
        }

        var retentionAvailable = retentionFailures.Count == 0;
        if (!retentionAvailable)
        {
            var descriptor = RequireDescriptor(DurableProblemCodes.HeartbeatRetentionUnavailable);
            var action = CreateAction(descriptor.DocumentationUrl, request, ActionKind.RetryDoctor);
            findings.Add(Finding(descriptor, retentionFailures, action));
        }

        if (heartbeat is not null && epochMatches)
        {
            var workerState = ClassifyWorker(
                heartbeat,
                observedAt,
                request.ConfiguredRuntimeEpoch,
                request.StaleAfter!.Value,
                out var workerCode,
                out var workerCategory);
            worker = ProjectWorker(request, heartbeat, observedAt, workerState);
            if (workerCode is not null)
            {
                workerStatus = "finding";
                var descriptor = RequireDescriptor(workerCode);
                var action = CreateAction(descriptor.DocumentationUrl, request, ActionKind.RetryDoctor);
                findings.Add(Finding(descriptor, [workerCategory!], action));
            }
        }

        if (findings.Count > 12)
        {
            throw new DoctorContractException();
        }

        var nextAction = findings.Count == 0
            ? CreateApplicationVerifierAction()
            : findings[0].NextAction;
        var status = findings.Count == 0 ? "passed" : "findings";
        return new DurableDoctorResult(
            status,
            findings.Count == 0 ? 0 : 2,
            CreateChecks(request, "passed", "passed", epochStatus, retentionAvailable ? "passed" : "finding", workerStatus),
            observedAt,
            projectedSchema,
            storeId,
            request.ConfiguredRuntimeEpoch,
            activeEpoch,
            new DurableDoctorCredentialResult(true, []),
            new DurableDoctorRetentionResult(retentionAvailable, retentionFailures),
            worker,
            findings,
            nextAction);
    }

    private static DurableDoctorHeartbeat? ValidateHeartbeat(
        DurableDoctorRequest request,
        DurableDoctorHeartbeat? heartbeat)
    {
        if (request.WorkerId is null)
        {
            if (heartbeat is not null)
            {
                throw new DoctorContractException();
            }
            return null;
        }

        if (heartbeat is null)
        {
            throw new DoctorContractException();
        }

        if (!heartbeat.Found)
        {
            if (heartbeat.RuntimeEpoch is not null || heartbeat.LastHeartbeatAtUtc is not null || heartbeat.IsDraining is not null)
            {
                throw new DoctorContractException();
            }
            return heartbeat;
        }

        if (heartbeat.RuntimeEpoch is not { } rowEpoch || rowEpoch == Guid.Empty
            || heartbeat.LastHeartbeatAtUtc is not { } lastHeartbeatAtUtc
            || heartbeat.IsDraining is null)
        {
            throw new DoctorContractException();
        }
        ValidateTimestamp(lastHeartbeatAtUtc);
        return heartbeat;
    }

    private static DurableDoctorWorkerResult ProjectWorker(
        DurableDoctorRequest request,
        DurableDoctorHeartbeat heartbeat,
        DateTimeOffset observedAtUtc,
        string state)
    {
        var ageTicks = heartbeat.Found
            ? Math.Max(0L, (observedAtUtc - heartbeat.LastHeartbeatAtUtc!.Value).Ticks)
            : (long?)null;
        return new DurableDoctorWorkerResult(
            request.WorkerId!,
            heartbeat.Found,
            state,
            heartbeat.RuntimeEpoch,
            heartbeat.LastHeartbeatAtUtc,
            ageTicks,
            request.StaleAfter!.Value.Ticks,
            heartbeat.IsDraining).Normalize();
    }

    private static string ClassifyWorker(
        DurableDoctorHeartbeat heartbeat,
        DateTimeOffset observedAtUtc,
        Guid configuredRuntimeEpoch,
        TimeSpan staleAfter,
        out string? code,
        out string? category)
    {
        code = null;
        category = null;
        if (!heartbeat.Found)
        {
            code = DurableProblemCodes.WorkerHeartbeatMissing;
            category = "worker-heartbeat-missing";
            return "not-started";
        }

        if (heartbeat.RuntimeEpoch != configuredRuntimeEpoch)
        {
            code = DurableProblemCodes.WorkerHeartbeatEpochMismatch;
            category = "worker-heartbeat-epoch";
            return "epoch-incompatible";
        }

        if (heartbeat.IsDraining == true)
        {
            code = DurableProblemCodes.WorkerDraining;
            category = "worker-draining";
            return "draining";
        }

        var ageTicks = Math.Max(0L, (observedAtUtc - heartbeat.LastHeartbeatAtUtc!.Value).Ticks);
        if (ageTicks > staleAfter.Ticks)
        {
            code = DurableProblemCodes.ActivatorStale;
            category = "worker-heartbeat-stale";
            return "stale";
        }

        return "current";
    }

    private static DurableDoctorSchemaResult ProjectSchema(DurableRuntimeSchemaStatus status)
    {
        var requiredVersion = status.RequiredVersion;
        return status.Compatibility switch
        {
            DurableRuntimeSchemaCompatibility.Missing => new DurableDoctorSchemaResult(
                "missing", 0, requiredVersion, null, null, null, null, [], status.PendingVersions).Copy(),
            DurableRuntimeSchemaCompatibility.Inconsistent => new DurableDoctorSchemaResult(
                "inconsistent", null, requiredVersion, null, null, null, null, null, null),
            DurableRuntimeSchemaCompatibility.Compatible => new DurableDoctorSchemaResult(
                "compatible", status.InstalledVersion, requiredVersion,
                status.MinimumReaderVersion, status.MaximumReaderVersion,
                status.MinimumWriterVersion, status.MaximumWriterVersion,
                status.AppliedVersions, status.PendingVersions).Copy(),
            DurableRuntimeSchemaCompatibility.UpgradeRequired => new DurableDoctorSchemaResult(
                "upgrade-required", status.InstalledVersion, requiredVersion,
                status.MinimumReaderVersion, status.MaximumReaderVersion,
                status.MinimumWriterVersion, status.MaximumWriterVersion,
                status.AppliedVersions, status.PendingVersions).Copy(),
            DurableRuntimeSchemaCompatibility.StoreTooNew => new DurableDoctorSchemaResult(
                "store-too-new", status.InstalledVersion, requiredVersion,
                status.MinimumReaderVersion, status.MaximumReaderVersion,
                status.MinimumWriterVersion, status.MaximumWriterVersion,
                status.AppliedVersions, status.PendingVersions).Copy(),
            _ => throw new DoctorContractException(),
        };
    }

    private static void ValidateSchemaStatus(DurableRuntimeSchemaStatus status)
    {
        if (!Enum.IsDefined(status.Compatibility)
            || status.RequiredVersion < 1
            || status.InstalledVersion < 0
            || status.MinimumReaderVersion < 0
            || status.MaximumReaderVersion < 0
            || status.MinimumWriterVersion < 0
            || status.MaximumWriterVersion < 0)
        {
            throw new DoctorContractException();
        }

        ValidateVersions(status.AppliedVersions);
        ValidateVersions(status.PendingVersions);
        if (status.ActiveRuntimeEpoch == Guid.Empty)
        {
            throw new DoctorContractException();
        }

        if (status.Compatibility == DurableRuntimeSchemaCompatibility.Missing)
        {
            if (status.StoreId != Guid.Empty
                || status.ActiveRuntimeEpoch is not null
                || status.InstalledVersion != 0
                || status.MinimumReaderVersion != 0
                || status.MaximumReaderVersion != 0
                || status.MinimumWriterVersion != 0
                || status.MaximumWriterVersion != 0
                || status.AppliedVersions.Count != 0
                || status.PendingVersions.Count != status.RequiredVersion
                || status.PendingVersions.Where((version, index) => version != index + 1).Any())
            {
                throw new DoctorContractException();
            }
            return;
        }

        if (status.Compatibility == DurableRuntimeSchemaCompatibility.Inconsistent)
        {
            // Inconsistent metadata is never published. Still bound its already-captured arrays and integer shapes.
            return;
        }

        if (status.StoreId == Guid.Empty
            || status.MinimumReaderVersion > status.MaximumReaderVersion
            || status.MinimumWriterVersion > status.MaximumWriterVersion
            || status.AppliedVersions.Any(version => version > status.InstalledVersion)
            || status.PendingVersions.Any(version => version <= status.InstalledVersion)
            || status.AppliedVersions.Intersect(status.PendingVersions).Any())
        {
            throw new DoctorContractException();
        }

        var protocolSupported = status.RequiredVersion >= status.MinimumReaderVersion
            && status.RequiredVersion <= status.MaximumReaderVersion
            && status.RequiredVersion >= status.MinimumWriterVersion
            && status.RequiredVersion <= status.MaximumWriterVersion;
        switch (status.Compatibility)
        {
            case DurableRuntimeSchemaCompatibility.Compatible when status.InstalledVersion < status.RequiredVersion || !protocolSupported:
            case DurableRuntimeSchemaCompatibility.UpgradeRequired when status.InstalledVersion >= status.RequiredVersion:
            case DurableRuntimeSchemaCompatibility.StoreTooNew when status.InstalledVersion < status.RequiredVersion || protocolSupported:
                throw new DoctorContractException();
        }
    }

    private static void ValidateVersions(IReadOnlyList<int> versions)
    {
        if (versions is null || versions.Count > MaximumSchemaVersions)
        {
            throw new DoctorContractException();
        }

        var previous = 0;
        foreach (var version in versions)
        {
            if (version <= previous)
            {
                throw new DoctorContractException();
            }
            previous = version;
        }
    }

    private static IReadOnlyList<string> ValidateCategoryEvidence(
        IReadOnlyList<string>? categories,
        IReadOnlyList<string> allowed,
        int maximumCount,
        bool allowNull = true)
    {
        if (categories is null)
        {
            if (allowNull)
            {
                return [];
            }
            throw new DoctorContractException();
        }

        if (categories.Count > maximumCount
            || categories.Any(category => category is null || !allowed.Contains(category, StringComparer.Ordinal))
            || categories.Distinct(StringComparer.Ordinal).Count() != categories.Count)
        {
            throw new DoctorContractException();
        }

        var failed = new HashSet<string>(categories, StringComparer.Ordinal);
        return Array.AsReadOnly(allowed.Where(failed.Contains).ToArray());
    }

    private static bool TryNormalizeCategories(
        IReadOnlyList<string>? categories,
        IReadOnlyList<string> allowed,
        int minimumCount,
        int maximumCount,
        out IReadOnlyList<string> normalized)
    {
        normalized = [];
        if (categories is null
            || categories.Count < minimumCount
            || categories.Count > maximumCount
            || categories.Any(category => category is null || !allowed.Contains(category, StringComparer.Ordinal))
            || categories.Distinct(StringComparer.Ordinal).Count() != categories.Count)
        {
            return false;
        }

        var failed = new HashSet<string>(categories, StringComparer.Ordinal);
        normalized = Array.AsReadOnly(DurableDoctorChecks.All.Where(failed.Contains).ToArray());
        return normalized.Count == categories.Count;
    }

    private static IReadOnlyList<DurableDoctorCheckResult> CreateChecks(
        DurableDoctorRequest request,
        string credential,
        string schema,
        string epoch,
        string retention,
        string worker) =>
    [
        new("credential", true, credential),
        new("schema", true, schema),
        new("epoch", true, epoch),
        new("retention", true, retention),
        new("worker", request.WorkerId is not null, request.WorkerId is null ? "not-requested" : worker),
    ];

    private static IReadOnlyList<DurableDoctorCheckResult> CreateUnrequestedChecks() =>
        Array.AsReadOnly(CheckNames.Select(static name => new DurableDoctorCheckResult(name, false, "not-requested")).ToArray());

    private static string SchemaCode(DurableRuntimeSchemaCompatibility compatibility) => compatibility switch
    {
        DurableRuntimeSchemaCompatibility.Missing => DurableProblemCodes.SchemaMissing,
        DurableRuntimeSchemaCompatibility.UpgradeRequired => DurableProblemCodes.SchemaUpgradeRequired,
        DurableRuntimeSchemaCompatibility.StoreTooNew => DurableProblemCodes.SchemaVersionUnsupported,
        DurableRuntimeSchemaCompatibility.Inconsistent => DurableProblemCodes.SchemaInconsistent,
        _ => throw new DoctorContractException(),
    };

    private static DurableDoctorFinding Finding(
        DurableDiagnosticDescriptor descriptor,
        IReadOnlyList<string> failedChecks,
        DurableDoctorAction action) =>
        new(
            descriptor.Code,
            descriptor.Problem,
            descriptor.Cause,
            descriptor.Fix,
            descriptor.DocumentationUrl,
            failedChecks,
            action.Copy());

    private static DurableDiagnosticDescriptor RequireDescriptor(string code)
    {
        if (!DurableDiagnosticCatalog.TryGet(code, out var descriptor)
            || descriptor is null
            || !string.Equals(descriptor.Code, code, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(descriptor.Problem)
            || string.IsNullOrWhiteSpace(descriptor.Cause)
            || string.IsNullOrWhiteSpace(descriptor.Fix)
            || descriptor.Problem.Length > 500
            || descriptor.Cause.Length > 1_000
            || descriptor.Fix.Length > 1_000
            || descriptor.Problem.Any(char.IsControl)
            || descriptor.Cause.Any(char.IsControl)
            || descriptor.Fix.Any(char.IsControl)
            || descriptor.DocumentationUrl is null
            || !descriptor.DocumentationUrl.IsAbsoluteUri
            || descriptor.DocumentationUrl.Scheme != Uri.UriSchemeHttps
            || descriptor.DocumentationUrl.AbsoluteUri.Length > 2_000)
        {
            throw new DoctorContractException();
        }

        return descriptor;
    }

    private static DurableDoctorAction CreateAction(Uri documentationUrl, DurableDoctorRequest? request, ActionKind kind)
    {
        if (kind == ActionKind.Help || request is null)
        {
            return new DurableDoctorAction(
                "command",
                new DurableDoctorCommandAction("appsurface", ["durable", "doctor", "--help"]),
                [],
                documentationUrl);
        }

        if (kind == ActionKind.SchemaStatus)
        {
            return new DurableDoctorAction(
                "command",
                new DurableDoctorCommandAction("appsurface", [
                    "durable", "schema", "status", "--connection-env", request.ConnectionEnvironmentName,
                ]),
                [],
                documentationUrl);
        }

        var arguments = new List<string>(14)
        {
            "durable", "doctor",
            "--connection-env", request.ConnectionEnvironmentName,
            "--runtime-epoch-env", request.EpochEnvironmentName,
        };
        if (request.WorkerId is not null)
        {
            arguments.Add("--worker-id");
            arguments.Add(request.WorkerId);
            arguments.Add("--stale-after");
            arguments.Add(FormatDuration(request.StaleAfter!.Value));
        }
        arguments.Add("--timeout");
        arguments.Add(FormatDuration(request.Timeout));
        arguments.Add("--format");
        arguments.Add(request.Format);
        if (arguments.Count > 14 || arguments.Any(static argument => argument.Length > 200))
        {
            throw new DoctorContractException();
        }

        return new DurableDoctorAction(
            "command",
            new DurableDoctorCommandAction("appsurface", arguments),
            [],
            documentationUrl);
    }

    /// <summary>Returns the exact renderer expectation for a finding's canonical follow-up action.</summary>
    internal static DurableDoctorAction ExpectedActionFor(DurableDoctorRequest request, string code)
    {
        if (!IsValidRequest(request))
        {
            throw new DoctorContractException();
        }
        var descriptor = RequireDescriptor(code);
        var kind = code switch
        {
            DurableProblemCodes.SchemaMissing
                or DurableProblemCodes.SchemaUpgradeRequired
                or DurableProblemCodes.SchemaVersionUnsupported
                or DurableProblemCodes.SchemaInconsistent => ActionKind.SchemaStatus,
            DurableProblemCodes.DoctorInputInvalid => ActionKind.Help,
            _ => ActionKind.RetryDoctor,
        };
        return CreateAction(descriptor.DocumentationUrl, request, kind);
    }

    private static DurableDoctorAction CreateApplicationVerifierAction() =>
        new(
            "application-verifier",
            null,
            ["consumer verifier command"],
            ApplicationVerifierDocumentation);

    private static bool IsEnvironmentName(string? value) =>
        value is { Length: > 0 and <= 200 }
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && (char.IsLetter(value[0]) || value[0] == '_')
        && value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsWorkerId(string value) =>
        value.Length is > 0 and <= 200
        && value.All(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or ':');

    private static string FormatDuration(TimeSpan duration)
    {
        var wholeSeconds = duration.Ticks / TimeSpan.TicksPerSecond;
        var fractionalTicks = duration.Ticks % TimeSpan.TicksPerSecond;
        if (fractionalTicks == 0)
        {
            return wholeSeconds.ToString(CultureInfo.InvariantCulture) + "s";
        }

        var fraction = fractionalTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return wholeSeconds.ToString(CultureInfo.InvariantCulture) + "." + fraction + "s";
    }

    private static void ValidateTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp == DateTimeOffset.MinValue || timestamp == DateTimeOffset.MaxValue)
        {
            throw new DoctorContractException();
        }
    }

    private enum ActionKind
    {
        Help,
        RetryDoctor,
        SchemaStatus,
    }

    private sealed class DoctorContractException : Exception;
}
