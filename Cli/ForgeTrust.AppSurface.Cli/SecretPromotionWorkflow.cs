using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Creates provider clients for declared Google promotion endpoints.
/// </summary>
/// <remarks>
/// The factory is a command-only seam. It keeps transfer credentials distinct from the read-only runtime configuration
/// client and lets tests bind each named endpoint to a deterministic fake without reading environment credentials.
/// </remarks>
internal interface ISecretPromotionGoogleClientFactory
{
    IAppSurfaceGoogleSecretTransferClient Create(SecretPromotionEndpoint endpoint);
}

internal sealed class DefaultSecretPromotionGoogleClientFactory(IAppSurfaceGoogleSecretTransferClient applicationDefaultClient) :
    ISecretPromotionGoogleClientFactory
{
    public IAppSurfaceGoogleSecretTransferClient Create(SecretPromotionEndpoint endpoint)
    {
        if (string.Equals(endpoint.Credential?.Mode, "applicationDefault", StringComparison.OrdinalIgnoreCase))
        {
            return applicationDefaultClient;
        }

        if (string.Equals(endpoint.Credential?.Mode, "credentialFile", StringComparison.OrdinalIgnoreCase))
        {
            var credentialFilePath = ValidateCredentialFile(endpoint.Credential?.Path);
            try
            {
                return GoogleSecretManagerTransferClientAdapter.FromCredentialFile(credentialFilePath);
            }
            catch (IOException)
            {
                throw SecretPromotionCommandExtensions.Usage("Google credentialFile could not be loaded.");
            }
            catch (UnauthorizedAccessException)
            {
                throw SecretPromotionCommandExtensions.Usage("Google credentialFile could not be loaded.");
            }
            catch (InvalidOperationException)
            {
                throw SecretPromotionCommandExtensions.Usage("Google credentialFile could not be loaded.");
            }
        }

        throw SecretPromotionCommandExtensions.Usage(
            $"Google endpoint '{endpoint.Name}' must explicitly select credential.mode 'applicationDefault' or 'credentialFile'.");
    }

    private static string ValidateCredentialFile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw SecretPromotionCommandExtensions.Usage("A Google credentialFile profile requires credential.path.");
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw SecretPromotionCommandExtensions.Usage("Google credentialFile must use an absolute path.");
        }

        var path = Path.GetFullPath(value);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.Directory) != 0)
        {
            throw SecretPromotionCommandExtensions.Usage("Google credentialFile must identify an existing regular file.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode exposed =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & exposed) != 0)
            {
                throw SecretPromotionCommandExtensions.Usage(
                    "Google credentialFile must not be readable, writable, or executable by group or other users.");
            }
        }

        return path;
    }
}

/// <summary>
/// Runs declared, value-safe promotion jobs.
/// </summary>
internal sealed class SecretPromotionWorkflow(ISecretPromotionGoogleClientFactory googleFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public SecretPromotionPlanResult CreatePlan(SecretPromotionPlanRequest request)
    {
        var loaded = LoadConfiguration(request.ConfigPath);
        var job = FindJob(loaded.Configuration, request.JobName);
        var endpoints = ResolveEndpoints(loaded.Configuration, job);
        ValidateJob(job, endpoints, request.Replace);

        var draftRows = job.Rows.Select((row, index) => CreatePlanRow(row, index + 1, endpoints, request.Context)).ToArray();
        if (draftRows.GroupBy(row => row.LocalStorageName, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw SecretPromotionCommandExtensions.Usage("Promotion job contains duplicate normalized LocalSecrets keys.");
        }

        var rows = draftRows.Select(row => CaptureDestinationPrecondition(row, endpoints.Destination, request.Context)).ToArray();
        var summaryRows = rows.Select(row => ProbeRow(row, endpoints, request.Context, request.Replace)).ToArray();
        var succeeded = summaryRows.All(IsReadyOrSkipped);
        var plan = new SecretPromotionPlanArtifact(
            1,
            job.Name,
            loaded.Digest,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(request.Expiry),
            request.Replace,
            IsProduction(endpoints.Destination),
            succeeded,
            rows);
        WriteJson(request.OutputPlanPath, plan, "--out");

        return new SecretPromotionPlanResult(new SecretPromotionSummary(
            "plan", job.Name, false, succeeded, summaryRows, request.OutputPlanPath, null));
    }

    public SecretPromotionSummary Apply(SecretPromotionApplyRequest request)
    {
        var loaded = LoadConfiguration(request.ConfigPath);
        var plan = LoadPlan(request.PlanPath);
        if (!string.Equals(plan.ConfigDigest, loaded.Digest, StringComparison.Ordinal))
        {
            throw SecretPromotionCommandExtensions.Usage("The plan does not match the supplied endpoint configuration.");
        }

        if (plan.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw SecretPromotionCommandExtensions.Usage("The plan has expired. Create a new plan before applying.");
        }

        if (!plan.Ready)
        {
            throw SecretPromotionCommandExtensions.Usage("The plan contains failed preflight rows and cannot be applied.");
        }

        var job = FindJob(loaded.Configuration, plan.JobName);
        var endpoints = ResolveEndpoints(loaded.Configuration, job);
        ValidateJob(job, endpoints, plan.Replace);
        ValidatePlanRows(plan.Rows, job, endpoints, request.Context);
        if (IsProduction(endpoints.Destination) && !string.Equals(request.Confirmation, plan.JobName, StringComparison.Ordinal))
        {
            throw SecretPromotionCommandExtensions.Usage("A production destination requires --confirm with the exact job name.");
        }

        var plannedRows = plan.Rows.Select(row => RehydrateRow(row, endpoints, request.Context)).ToArray();
        var completedRows = LoadCompletedRows(request.ResumeReceiptPath, plan);
        var preflight = plannedRows
            .Where(row => !completedRows.Contains(row.RowNumber))
            .Select(row => RecheckRow(row, endpoints, request.Context, plan.Replace))
            .ToDictionary(row => row.RowNumber);
        var plannedResults = plannedRows
            .Select(row => completedRows.Contains(row.RowNumber)
                ? row.Result("Skipped", "ResumeSkippedConfirmedWrite", "secret-promotion-resume-skipped", null, false)
                : preflight[row.RowNumber])
            .ToArray();
        if (plannedResults.Any(row => !IsReadyOrSkipped(row)))
        {
            var blocked = new SecretPromotionSummary("apply", plan.JobName, request.Apply, false, plannedResults, null, null);
            WriteReceipt(request, plan, blocked);
            return blocked;
        }

        if (!request.Apply)
        {
            return new SecretPromotionSummary("apply", plan.JobName, false, true, plannedResults, null, null);
        }

        var results = new List<SecretPromotionRowResult>(plannedRows.Length);
        foreach (var row in plannedRows)
        {
            if (completedRows.Contains(row.RowNumber))
            {
                results.Add(row.Result("Skipped", "ResumeSkippedConfirmedWrite", "secret-promotion-resume-skipped", null, false));
                continue;
            }

            var preflightResult = preflight[row.RowNumber];
            results.Add(preflightResult.Status == "DestinationExists"
                ? SkipExistingDestination(preflightResult)
                : ApplyRow(row, endpoints, request.Context));
        }

        var succeeded = results.All(row => row.Status is "Written" or "Skipped");
        var summary = new SecretPromotionSummary("apply", plan.JobName, true, succeeded, results, null, null);
        WriteReceipt(request, plan, summary);
        return summary;
    }

    private static LoadedConfiguration LoadConfiguration(string path)
    {
        var fullPath = Path.GetFullPath(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SecretPromotionCommandExtensions.Usage("--config could not be read.");
        }

        try
        {
            var configuration = JsonSerializer.Deserialize<SecretPromotionConfiguration>(bytes, JsonOptions);
            if (configuration?.Version != 1 ||
                configuration.Jobs is null ||
                configuration.Endpoints is null ||
                configuration.Endpoints.Any(static endpoint => endpoint is null) ||
                configuration.Jobs.Any(static job => job is null || job.Rows is null || job.Rows.Any(static row => row is null)))
            {
                throw SecretPromotionCommandExtensions.Usage("--config must be a version 1 secret-promotion configuration with endpoints and jobs.");
            }

            return new LoadedConfiguration(configuration, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (JsonException)
        {
            throw SecretPromotionCommandExtensions.Usage("--config must be valid secret-promotion JSON.");
        }
    }

    private static SecretPromotionPlanArtifact LoadPlan(string path)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<SecretPromotionPlanArtifact>(File.ReadAllBytes(Path.GetFullPath(path)), JsonOptions);
            if (plan?.Version != 1 || plan.Rows is null || string.IsNullOrWhiteSpace(plan.JobName))
            {
                throw SecretPromotionCommandExtensions.Usage("--plan must be a version 1 AppSurface secret-promotion plan.");
            }

            return plan;
        }
        catch (JsonException)
        {
            throw SecretPromotionCommandExtensions.Usage("--plan must be valid secret-promotion JSON.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SecretPromotionCommandExtensions.Usage("--plan could not be read.");
        }
    }

    private static SecretPromotionJob FindJob(SecretPromotionConfiguration configuration, string name)
    {
        var matches = configuration.Jobs.Where(job => string.Equals(job.Name, name, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw SecretPromotionCommandExtensions.Usage($"No declared promotion job named '{name}' exists."),
            _ => throw SecretPromotionCommandExtensions.Usage($"Promotion job '{name}' is declared more than once.")
        };
    }

    private static ResolvedEndpoints ResolveEndpoints(SecretPromotionConfiguration configuration, SecretPromotionJob job)
    {
        var source = ResolveEndpoint(configuration, job.Source);
        var destination = ResolveEndpoint(configuration, job.Destination);
        if (string.Equals(source.Name, destination.Name, StringComparison.Ordinal))
        {
            throw SecretPromotionCommandExtensions.Usage("A promotion job cannot use the same source and destination endpoint.");
        }

        return new ResolvedEndpoints(source, destination);
    }

    private static SecretPromotionEndpoint ResolveEndpoint(SecretPromotionConfiguration configuration, string? name)
    {
        if (string.Equals(name, "local", StringComparison.Ordinal))
        {
            return new SecretPromotionEndpoint("local", "local", "development", null);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw SecretPromotionCommandExtensions.Usage("Promotion jobs must name source and destination endpoints.");
        }

        var matches = configuration.Endpoints.Where(endpoint => string.Equals(endpoint.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || string.Equals(matches[0].Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw SecretPromotionCommandExtensions.Usage($"Endpoint '{name}' must be declared once and use a supported remote provider.");
        }

        return matches[0];
    }

    private static void ValidateJob(SecretPromotionJob job, ResolvedEndpoints endpoints, bool replace)
    {
        if (string.IsNullOrWhiteSpace(job.Name) || job.Rows.Count == 0)
        {
            throw SecretPromotionCommandExtensions.Usage("A promotion job must have a name and at least one row.");
        }

        if (!IsSupported(endpoints.Source) || !IsSupported(endpoints.Destination))
        {
            throw SecretPromotionCommandExtensions.Usage("V1 supports only the built-in local endpoint and Google endpoints.");
        }

        var duplicateKeys = job.Rows.GroupBy(row => row.Key, StringComparer.Ordinal).FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateKeys is not null)
        {
            throw SecretPromotionCommandExtensions.Usage("Promotion job rows require unique non-empty keys.");
        }

        if (IsProduction(endpoints.Destination) && IsLocal(endpoints.Source) && !job.AllowMutableLocalSource)
        {
            throw SecretPromotionCommandExtensions.Usage("A LocalSecrets source targeting production requires allowMutableLocalSource: true in the declared job.");
        }

        if (IsProduction(endpoints.Destination) && !IsLocal(endpoints.Source))
        {
            foreach (var row in job.Rows)
            {
                if (!IsNumericVersionResource(row.Source))
                {
                    throw SecretPromotionCommandExtensions.Usage("Production Google sources must use explicit numeric version resources.");
                }
            }
        }

        var duplicates = job.Rows
            .Select(row => IsLocal(endpoints.Destination) ? row.Key : ResourceForDestination(row, endpoints.Destination))
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw SecretPromotionCommandExtensions.Usage("Promotion job contains duplicate destination resources.");
        }

        _ = replace;
    }

    private SecretPromotionPlanRow CreatePlanRow(
        SecretPromotionJobRow row,
        int rowNumber,
        ResolvedEndpoints endpoints,
        SecretsCommandContext context) =>
        CreateDeclaredPlanRow(row, rowNumber, endpoints, context) with
        {
            DestinationHasEnabledVersions = IsLocal(endpoints.Destination) ? null : false,
            DestinationExists = IsLocal(endpoints.Destination) ? false : null
        };

    private SecretPromotionRowResult ProbeRow(SecretPromotionPlanRow row, ResolvedEndpoints endpoints, SecretsCommandContext context, bool replace)
    {
        var source = ProbeSource(row, endpoints.Source, context);
        if (source is not null)
        {
            return source;
        }

        return ProbeDestination(row, endpoints.Destination, context, replace, includePrecondition: true);
    }

    private SecretPromotionPlanRow CaptureDestinationPrecondition(
        SecretPromotionPlanRow row,
        SecretPromotionEndpoint endpoint,
        SecretsCommandContext context)
    {
        if (IsLocal(endpoint))
        {
            var probe = ProbeLocal(context, row.Key);
            return probe.Status switch
            {
                LocalSecretResultStatus.Found => row with { DestinationExists = true },
                LocalSecretResultStatus.Missing => row with { DestinationExists = false },
                _ => row
            };
        }

        var remoteProbe = googleFactory.Create(endpoint).ProbeSecret(row.DestinationResource!, TimeSpan.FromSeconds(5));
        return remoteProbe.Status == GoogleSecretManagerTransferStatus.Ready
            ? row with { DestinationHasEnabledVersions = remoteProbe.HasEnabledVersions }
            : row;
    }

    private SecretPromotionRowResult RecheckRow(SecretPromotionPlanRow row, ResolvedEndpoints endpoints, SecretsCommandContext context, bool replace)
    {
        var source = ProbeSource(row, endpoints.Source, context);
        if (source is not null)
        {
            return source;
        }

        var destination = ProbeDestination(row, endpoints.Destination, context, replace, includePrecondition: true);
        if (destination.Status != "Ready")
        {
            return destination;
        }

        return row.Result("Ready", "ApplyPreflight", null, null, null);
    }

    private SecretPromotionRowResult? ProbeSource(SecretPromotionPlanRow row, SecretPromotionEndpoint endpoint, SecretsCommandContext context)
    {
        if (IsLocal(endpoint))
        {
            var probe = ProbeLocal(context, row.Key);
            return probe.Status switch
            {
                LocalSecretResultStatus.Found => null,
                LocalSecretResultStatus.Missing => row.Result("SourceMissing", "ProbeLocalSource", "local-secret-promotion-source-missing", "Local secret was not found.", false),
                _ => row.Result("Failed", "ProbeLocalSource", probe.Diagnostic?.Code, probe.Diagnostic?.Problem, probe.Diagnostic?.Retryable)
            };
        }

        var result = googleFactory.Create(endpoint).ProbeSecretVersion(row.SourceResource!, TimeSpan.FromSeconds(5));
        return result.Status == GoogleSecretManagerTransferStatus.Ready
            ? null
            : row.GoogleFailure("ProbeGoogleSource", result.Status, result.Diagnostic);
    }

    private SecretPromotionRowResult ProbeDestination(
        SecretPromotionPlanRow row,
        SecretPromotionEndpoint endpoint,
        SecretsCommandContext context,
        bool replace,
        bool includePrecondition)
    {
        if (IsLocal(endpoint))
        {
            var probe = ProbeLocal(context, row.Key);
            if (probe.Status is not (LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing))
            {
                return row.Result("Failed", "ProbeLocalDestination", probe.Diagnostic?.Code, probe.Diagnostic?.Problem, probe.Diagnostic?.Retryable);
            }

            var exists = probe.Status == LocalSecretResultStatus.Found;
            if (includePrecondition && row.DestinationExists != exists)
            {
                return row.Result("DestinationChanged", "ProbeLocalDestination", "secret-promotion-destination-changed", "Local destination state changed after planning.", false);
            }

            return exists && !replace
                ? row.Result("DestinationExists", "ProbeLocalDestination", "secret-promotion-destination-exists", "Local destination already exists.", false)
                : row.Result("Ready", exists ? "WouldReplaceLocal" : "WouldWriteLocal", null, null, null);
        }

        var result = googleFactory.Create(endpoint).ProbeSecret(row.DestinationResource!, TimeSpan.FromSeconds(5));
        if (result.Status != GoogleSecretManagerTransferStatus.Ready)
        {
            return row.GoogleFailure("ProbeGoogleDestination", result.Status, result.Diagnostic);
        }

        if (includePrecondition && row.DestinationHasEnabledVersions != result.HasEnabledVersions)
        {
            return row.Result("DestinationChanged", "ProbeGoogleDestination", "secret-promotion-destination-changed", "Google destination state changed after planning.", false);
        }

        return result.HasEnabledVersions && !replace
            ? row.Result("DestinationExists", "ProbeGoogleDestination", "secret-promotion-destination-exists", "Google destination has enabled versions.", false)
            : row.Result("Ready", result.HasEnabledVersions ? "WouldAddVersion" : "WouldWriteFirstVersion", null, null, null);
    }

    private SecretPromotionRowResult ApplyRow(SecretPromotionPlanRow row, ResolvedEndpoints endpoints, SecretsCommandContext context)
    {
        if (!TryReadSource(row, endpoints.Source, context, out var value, out var failure))
        {
            return failure!;
        }

        return IsLocal(endpoints.Destination)
            ? WriteLocal(row, context, value!)
            : WriteGoogle(row, endpoints.Destination, value!);
    }

    private bool TryReadSource(
        SecretPromotionPlanRow row,
        SecretPromotionEndpoint endpoint,
        SecretsCommandContext context,
        out string? value,
        out SecretPromotionRowResult? failure)
    {
        value = null;
        failure = null;
        if (IsLocal(endpoint))
        {
            var localResult = context.Store.Get(IdentityFrom(row, context));
            if (localResult.Status == LocalSecretResultStatus.Found && localResult.Value is not null)
            {
                value = localResult.Value;
                return true;
            }

            failure = row.Result("SourceMissing", "ReadLocalSource", localResult.Diagnostic?.Code, localResult.Diagnostic?.Problem, localResult.Diagnostic?.Retryable);
            return false;
        }

        var result = googleFactory.Create(endpoint).AccessSecretVersion(row.SourceResource!, TimeSpan.FromSeconds(5));
        if (result.Status != GoogleSecretManagerTransferStatus.Ready || result.Payload is null)
        {
            failure = row.GoogleFailure("AccessGoogleSource", result.Status, result.Diagnostic);
            return false;
        }

        if (!TryDecodeUtf8(result.Payload.Data, out value))
        {
            failure = row.Result("Failed", "DecodeGoogleSource", "secret-promotion-invalid-payload", "Google source payload is not valid UTF-8 text.", false);
            return false;
        }

        return true;
    }

    private static SecretPromotionRowResult WriteLocal(SecretPromotionPlanRow row, SecretsCommandContext context, string value)
    {
        var result = context.Store.Set(IdentityFrom(row, context), value);
        return result.Status == LocalSecretResultStatus.Found
            ? row.Result("Written", "WroteLocal", null, null, null)
            : row.Result("Failed", "WriteLocal", result.Diagnostic?.Code, result.Diagnostic?.Problem, result.Diagnostic?.Retryable);
    }

    private SecretPromotionRowResult WriteGoogle(SecretPromotionPlanRow row, SecretPromotionEndpoint endpoint, string value)
    {
        var result = googleFactory.Create(endpoint).AddSecretVersion(row.DestinationResource!, value, TimeSpan.FromSeconds(5));
        if (result.Status == GoogleSecretManagerTransferStatus.Written)
        {
            return row.Result("Written", "AddedGoogleVersion", null, null, null, result.WrittenVersionResourceName);
        }

        return result.Status is GoogleSecretManagerTransferStatus.Unavailable or GoogleSecretManagerTransferStatus.Cancelled
            ? row.Result("IndeterminateWrite", "AddGoogleVersion", result.Diagnostic?.Code ?? "secret-promotion-indeterminate-write", "Google may have accepted the version before the response failed. Reconcile before resuming.", false)
            : row.GoogleFailure("AddGoogleVersion", result.Status, result.Diagnostic);
    }

    private static AppSurfaceLocalSecretResult ProbeLocal(SecretsCommandContext context, string key)
    {
        if (context.Store is not IAppSurfaceLocalSecretMetadataStore metadataStore)
        {
            return AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic("local-secret-metadata-unsupported", "Local metadata probes are unavailable.", "The LocalSecrets store does not implement metadata probes.", "Use a metadata-capable LocalSecrets store.", "local-secrets-without-a-remote-vault"),
                context.Store.Name);
        }

        return metadataStore.Probe(IdentityFromKey(key, context));
    }

    private static AppSurfaceLocalSecretIdentity IdentityFrom(SecretPromotionPlanRow row, SecretsCommandContext context) =>
        IdentityFromKey(row.Key, context);

    private static AppSurfaceLocalSecretIdentity IdentityFromKey(string key, SecretsCommandContext context)
    {
        var identity = context.Normalizer.Normalize(context.ApplicationName, context.Environment, context.KeyPrefix, key);
        if (!identity.Succeeded)
        {
            throw SecretPromotionCommandExtensions.Usage(identity.Diagnostic!.ToDisplayString());
        }

        return identity.Identity!;
    }

    private static SecretPromotionPlanRow RehydrateRow(SecretPromotionPlanRow row, ResolvedEndpoints endpoints, SecretsCommandContext context)
    {
        _ = endpoints;
        _ = context;
        return row;
    }

    private static void ValidatePlanRows(
        IReadOnlyList<SecretPromotionPlanRow> planRows,
        SecretPromotionJob job,
        ResolvedEndpoints endpoints,
        SecretsCommandContext context)
    {
        var declaredRows = job.Rows
            .Select((row, index) => CreateDeclaredPlanRow(row, index + 1, endpoints, context))
            .ToArray();
        if (planRows.Count != declaredRows.Length)
        {
            throw SecretPromotionCommandExtensions.Usage("The plan rows do not match the declared promotion job.");
        }

        for (var index = 0; index < declaredRows.Length; index++)
        {
            var declared = declaredRows[index];
            var planned = planRows[index];
            if (planned.RowNumber != declared.RowNumber ||
                !string.Equals(planned.Key, declared.Key, StringComparison.Ordinal) ||
                !string.Equals(planned.SourceEndpoint, declared.SourceEndpoint, StringComparison.Ordinal) ||
                !string.Equals(planned.SourceResource, declared.SourceResource, StringComparison.Ordinal) ||
                !string.Equals(planned.DestinationEndpoint, declared.DestinationEndpoint, StringComparison.Ordinal) ||
                !string.Equals(planned.DestinationResource, declared.DestinationResource, StringComparison.Ordinal) ||
                !string.Equals(planned.LocalStorageName, declared.LocalStorageName, StringComparison.Ordinal))
            {
                throw SecretPromotionCommandExtensions.Usage("The plan rows do not match the declared promotion job.");
            }
        }
    }

    private static SecretPromotionPlanRow CreateDeclaredPlanRow(
        SecretPromotionJobRow row,
        int rowNumber,
        ResolvedEndpoints endpoints,
        SecretsCommandContext context)
    {
        var identity = context.Normalizer.Normalize(context.ApplicationName, context.Environment, context.KeyPrefix, row.Key);
        if (!identity.Succeeded)
        {
            throw SecretPromotionCommandExtensions.Usage(identity.Diagnostic!.ToDisplayString());
        }

        return new SecretPromotionPlanRow(
            rowNumber,
            row.Key,
            endpoints.Source.Name,
            ResourceForSource(row, endpoints.Source),
            endpoints.Destination.Name,
            ResourceForDestination(row, endpoints.Destination),
            identity.Identity!.StorageName,
            null,
            null);
    }

    private static string ResourceForSource(SecretPromotionJobRow row, SecretPromotionEndpoint endpoint)
    {
        if (IsLocal(endpoint))
        {
            if (!string.IsNullOrWhiteSpace(row.Source))
            {
                throw SecretPromotionCommandExtensions.Usage("Local source rows must not declare source resources.");
            }

            return "local";
        }

        if (!IsVersionResource(row.Source))
        {
            throw SecretPromotionCommandExtensions.Usage("Google source rows require full projects/.../secrets/.../versions/... resources.");
        }

        return row.Source!;
    }

    private static string ResourceForDestination(SecretPromotionJobRow row, SecretPromotionEndpoint endpoint)
    {
        if (IsLocal(endpoint))
        {
            if (!string.IsNullOrWhiteSpace(row.Destination))
            {
                throw SecretPromotionCommandExtensions.Usage("Local destination rows must not declare destination resources.");
            }

            return "local";
        }

        if (!IsSecretParentResource(row.Destination))
        {
            throw SecretPromotionCommandExtensions.Usage("Google destination rows require full projects/.../secrets/... resources.");
        }

        return row.Destination!;
    }

    private static bool IsSupported(SecretPromotionEndpoint endpoint) =>
        IsLocal(endpoint) || string.Equals(endpoint.Provider, "google", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocal(SecretPromotionEndpoint endpoint) =>
        string.Equals(endpoint.Provider, "local", StringComparison.OrdinalIgnoreCase);

    private static bool IsProduction(SecretPromotionEndpoint endpoint) =>
        string.Equals(endpoint.Environment, "production", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecretParentResource(string? value)
    {
        var parts = value?.Split('/') ?? [];
        return parts.Length == 4 && parts[0] == "projects" && parts[2] == "secrets" && parts.All(static part => !string.IsNullOrWhiteSpace(part));
    }

    private static bool IsVersionResource(string? value)
    {
        var parts = value?.Split('/') ?? [];
        return parts.Length == 6 && parts[0] == "projects" && parts[2] == "secrets" && parts[4] == "versions" && parts.All(static part => !string.IsNullOrWhiteSpace(part));
    }

    private static bool IsNumericVersionResource(string? value) =>
        IsVersionResource(value) && value!.Split('/')[5].All(static character => character is >= '0' and <= '9');

    private static bool IsReadyOrSkipped(SecretPromotionRowResult row) =>
        row.Status is "Ready" or "Skipped" or "DestinationExists";

    private static SecretPromotionRowResult SkipExistingDestination(SecretPromotionRowResult row) =>
        row with
        {
            Status = "Skipped",
            Action = "SkippedExistingDestination"
        };

    private static bool TryDecodeUtf8(byte[] value, out string? text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            return false;
        }
    }

    private static void WriteJson<T>(string path, T value, string option)
    {
        try
        {
            File.WriteAllText(Path.GetFullPath(path), JsonSerializer.Serialize(value, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SecretPromotionCommandExtensions.Usage($"{option} could not be written.");
        }
    }

    private static IReadOnlySet<int> LoadCompletedRows(string? receiptPath, SecretPromotionPlanArtifact plan)
    {
        if (string.IsNullOrWhiteSpace(receiptPath))
        {
            return new HashSet<int>();
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<SecretPromotionReceipt>(File.ReadAllBytes(Path.GetFullPath(receiptPath)), JsonOptions);
            if (receipt is null || receipt.PlanJob != plan.JobName || receipt.ConfigDigest != plan.ConfigDigest)
            {
                throw SecretPromotionCommandExtensions.Usage("--resume does not match the supplied plan.");
            }

            return receipt.Rows.Where(row => row.Status == "Written").Select(row => row.RowNumber).ToHashSet();
        }
        catch (JsonException)
        {
            throw SecretPromotionCommandExtensions.Usage("--resume must be a valid AppSurface secret-promotion receipt.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SecretPromotionCommandExtensions.Usage("--resume could not be read.");
        }
    }

    private static void WriteReceipt(SecretPromotionApplyRequest request, SecretPromotionPlanArtifact plan, SecretPromotionSummary summary)
    {
        var path = request.ReceiptPath ?? $"{request.PlanPath}.receipt.json";
        WriteJson(path, new SecretPromotionReceipt(plan.JobName, plan.ConfigDigest, summary.Rows), "--receipt");
    }

    private sealed record LoadedConfiguration(SecretPromotionConfiguration Configuration, string Digest);
    private sealed record ResolvedEndpoints(SecretPromotionEndpoint Source, SecretPromotionEndpoint Destination);
}

internal static class SecretPromotionOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async ValueTask WriteAsync(CliFx.Infrastructure.IConsole console, SecretPromotionSummary summary, bool json)
    {
        if (json)
        {
            await console.Output.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOptions));
            return;
        }

        await console.Output.WriteLineAsync($"Operation: {summary.Operation}; Job: {summary.Job}; Mode: {(summary.Apply ? "apply" : "dry-run")}");
        foreach (var row in summary.Rows)
        {
            await console.Output.WriteLineAsync($"{row.Status}: {row.Key} {row.Action} {row.DestinationResource}");
            if (!string.IsNullOrWhiteSpace(row.DiagnosticCode))
            {
                await console.Output.WriteLineAsync($"  Diagnostic: {row.DiagnosticCode}");
            }
        }
    }
}

internal sealed record SecretPromotionPlanRequest(string ConfigPath, string JobName, string OutputPlanPath, bool Replace, TimeSpan Expiry, SecretsCommandContext Context);
internal sealed record SecretPromotionApplyRequest(string ConfigPath, string PlanPath, bool Apply, string? Confirmation, string? ReceiptPath, string? ResumeReceiptPath, SecretsCommandContext Context);
internal sealed record SecretPromotionPlanResult(SecretPromotionSummary Summary);

internal sealed record SecretPromotionConfiguration(int Version, IReadOnlyList<SecretPromotionEndpoint> Endpoints, IReadOnlyList<SecretPromotionJob> Jobs);
internal sealed record SecretPromotionEndpoint(string Name, string Provider, string Environment, SecretPromotionCredential? Credential);
internal sealed record SecretPromotionCredential(string Mode, string? Path);
internal sealed record SecretPromotionJob(string Name, string Source, string Destination, bool AllowMutableLocalSource, IReadOnlyList<SecretPromotionJobRow> Rows);
internal sealed record SecretPromotionJobRow(string Key, string? Source, string? Destination);

internal sealed record SecretPromotionPlanArtifact(int Version, string JobName, string ConfigDigest, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, bool Replace, bool Production, bool Ready, IReadOnlyList<SecretPromotionPlanRow> Rows);
internal sealed record SecretPromotionPlanRow(int RowNumber, string Key, string SourceEndpoint, string? SourceResource, string DestinationEndpoint, string? DestinationResource, string LocalStorageName, bool? DestinationHasEnabledVersions, bool? DestinationExists)
{
    public SecretPromotionRowResult Result(string status, string action, string? diagnosticCode, string? problem, bool? retryable, string? writtenResource = null) =>
        new(RowNumber, Key, SourceEndpoint, SourceResource, DestinationEndpoint, writtenResource ?? DestinationResource, status, action, diagnosticCode, problem, retryable);

    public SecretPromotionRowResult GoogleFailure(string action, GoogleSecretManagerTransferStatus status, AppSurfaceGoogleSecretTransferDiagnostic? diagnostic) =>
        Result(
            status switch
            {
                GoogleSecretManagerTransferStatus.Missing => "SourceMissing",
                GoogleSecretManagerTransferStatus.AccessDenied => "AccessDenied",
                GoogleSecretManagerTransferStatus.Unavailable => "Unavailable",
                GoogleSecretManagerTransferStatus.Cancelled => "Cancelled",
                GoogleSecretManagerTransferStatus.InvalidResource => "InvalidResource",
                GoogleSecretManagerTransferStatus.NotEnabled => "SourceVersionNotEnabled",
                _ => "Failed"
            },
            action,
            diagnostic?.Code ?? "google-secret-promotion-failed",
            diagnostic?.Problem ?? "Google Secret Manager promotion failed.",
            diagnostic?.Retryable);
}

internal sealed record SecretPromotionRowResult(int RowNumber, string Key, string SourceEndpoint, string? SourceResource, string DestinationEndpoint, string? DestinationResource, string Status, string Action, string? DiagnosticCode, string? Problem, bool? Retryable);
internal sealed record SecretPromotionSummary(string Operation, string Job, bool Apply, bool Succeeded, IReadOnlyList<SecretPromotionRowResult> Rows, string? PlanPath, string? ReceiptPath);
internal sealed record SecretPromotionReceipt(string PlanJob, string ConfigDigest, IReadOnlyList<SecretPromotionRowResult> Rows);
