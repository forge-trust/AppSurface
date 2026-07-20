using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Config.LocalSecrets;
using ForgeTrust.AppSurface.Core;

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

/// <summary>Persists value-free receipt snapshots for crash-safe promotion recovery.</summary>
internal interface ISecretPromotionReceiptWriter
{
    /// <summary>Atomically replaces the receipt at <paramref name="path"/>.</summary>
    /// <param name="path">Destination receipt path.</param>
    /// <param name="receipt">Value-free journal snapshot.</param>
    void Write(string path, SecretPromotionReceipt receipt);
}

/// <summary>Writes receipt snapshots through same-directory atomic replacement.</summary>
internal sealed class AtomicSecretPromotionReceiptWriter : ISecretPromotionReceiptWriter
{
    /// <inheritdoc />
    public void Write(string path, SecretPromotionReceipt receipt)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var temporaryFileName = $".appsurface-receipt-{Guid.NewGuid():N}.tmp";
        var temporaryPath = PathUtils.PathUnder(directory, temporaryFileName);
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(receipt, SecretPromotionWorkflow.JsonOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // The temporary artifact is value-free; the original receipt remains authoritative.
            }

            throw SecretPromotionCommandExtensions.Usage("--receipt could not be written.");
        }
    }
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

    /// <summary>Validates a credential file path without returning its contents or path in diagnostics.</summary>
    /// <param name="value">Absolute credential-file path.</param>
    /// <param name="isWindows">Optional platform seam used to verify fail-closed Windows behavior.</param>
    /// <returns>The canonical absolute path after posture validation.</returns>
    internal static string ValidateCredentialFile(string? value, bool? isWindows = null)
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

        if (isWindows ?? OperatingSystem.IsWindows())
        {
            throw SecretPromotionCommandExtensions.Usage(
                "Google credentialFile is not supported on Windows because AppSurface cannot verify a restrictive file ACL.");
        }

        if (!OperatingSystem.IsWindows())
        {
            ValidateCredentialDirectoryAncestors(path);
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

    private static void ValidateCredentialDirectoryAncestors(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var root = Path.GetPathRoot(directory)!;
        var current = root;
        var relative = Path.GetRelativePath(root, directory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Join(current, segment);
            var info = new DirectoryInfo(current);
            var isLink = info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            var allowedSystemAlias = IsAllowedMacOsSystemDirectoryAlias(current, info.LinkTarget);
            if (!info.Exists || isLink && !allowedSystemAlias)
            {
                throw SecretPromotionCommandExtensions.Usage(
                    "Google credentialFile parent directories must exist and must not use symbolic links.");
            }

            var mode = info.UnixFileMode;
            var sharedWrite = mode.HasFlag(UnixFileMode.GroupWrite) || mode.HasFlag(UnixFileMode.OtherWrite);
            if (!allowedSystemAlias && sharedWrite && !mode.HasFlag(UnixFileMode.StickyBit))
            {
                throw SecretPromotionCommandExtensions.Usage(
                    "Google credentialFile parent directories must not be writable by group or other users unless sticky.");
            }
        }
    }

    private static bool IsAllowedMacOsSystemDirectoryAlias(string path, string? target)
    {
        if (!OperatingSystem.IsMacOS() || target is null)
        {
            return false;
        }

        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedTarget = Path.TrimEndingDirectorySeparator(target.StartsWith(Path.DirectorySeparatorChar)
            ? target
            : Path.Join(Path.GetPathRoot(normalizedPath), target));
        return normalizedPath == "/var" && normalizedTarget == "/private/var"
            || normalizedPath == "/tmp" && normalizedTarget == "/private/tmp";
    }
}

/// <summary>
/// Runs declared, value-safe promotion jobs.
/// </summary>
internal sealed class SecretPromotionWorkflow(
    ISecretPromotionGoogleClientFactory googleFactory,
    ISecretPromotionReceiptWriter? receiptWriter = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    private readonly ISecretPromotionReceiptWriter _receiptWriter = receiptWriter ?? new AtomicSecretPromotionReceiptWriter();

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

        var rows = draftRows.Select(row => CaptureDestinationPrecondition(row, endpoints.Destination)).ToArray();
        var summaryRows = rows.Select(row => ProbeRow(row, endpoints, request.Context, request.Replace)).ToArray();
        var succeeded = summaryRows.All(IsReadyOrSkipped);
        var createdAtUtc = DateTimeOffset.UtcNow;
        var expiresAtUtc = createdAtUtc.Add(request.Expiry);
        var production = IsProduction(endpoints.Destination);
        var planIdentity = ComputePlanIdentity(
            job.Name,
            loaded.Digest,
            createdAtUtc,
            expiresAtUtc,
            request.Replace,
            production,
            succeeded,
            rows);
        var plan = new SecretPromotionPlanArtifact(
            1,
            job.Name,
            loaded.Digest,
            createdAtUtc,
            expiresAtUtc,
            request.Replace,
            production,
            succeeded,
            planIdentity,
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

        ValidatePlanIdentity(plan);

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
        var resumeState = LoadResumeState(request.ResumeReceiptPath, plan);
        var completedRows = resumeState.CompletedRows;
        VerifyCompletedRows(completedRows, endpoints.Destination);
        var preflight = plannedRows
            .Where(row => !completedRows.ContainsKey(row.RowNumber))
            .Select(row => RecheckRow(row, endpoints, request.Context, plan.Replace))
            .ToDictionary(row => row.RowNumber);
        var plannedResults = plannedRows
            .Select(row => completedRows.ContainsKey(row.RowNumber)
                ? row.Result("Skipped", "ResumeSkippedConfirmedWrite", "secret-promotion-resume-skipped", null, false)
                : preflight[row.RowNumber])
            .ToArray();
        if (plannedResults.Any(row => !IsReadyOrSkipped(row)))
        {
            var blocked = new SecretPromotionSummary("apply", plan.JobName, request.Apply, false, plannedResults, null, null);
            var blockedJournalRows = resumeState.Rows.ToList();
            foreach (var row in plannedResults.Where(row => !completedRows.ContainsKey(row.RowNumber)))
            {
                SetJournalResult(blockedJournalRows, row);
            }

            WriteReceipt(request, plan, CreateJournalSummary(plan.JobName, blockedJournalRows));
            return blocked;
        }

        if (!request.Apply)
        {
            return new SecretPromotionSummary("apply", plan.JobName, false, true, plannedResults, null, null);
        }

        var results = new List<SecretPromotionRowResult>(plannedRows.Length);
        var journalResults = resumeState.Rows.ToList();
        WriteReceipt(request, plan, CreateJournalSummary(plan.JobName, journalResults));
        foreach (var row in plannedRows)
        {
            if (completedRows.ContainsKey(row.RowNumber))
            {
                results.Add(row.Result("Skipped", "ResumeSkippedConfirmedWrite", "secret-promotion-resume-skipped", null, false));
                continue;
            }

            var preflightResult = preflight[row.RowNumber];
            if (preflightResult.Status == "DestinationExists")
            {
                results.Add(SkipExistingDestination(preflightResult));
                SetJournalResult(journalResults, results[^1]);
                WriteReceipt(request, plan, CreateJournalSummary(plan.JobName, journalResults));
                continue;
            }

            results.Add(row.Result(
                "IndeterminateWrite",
                "WritePending",
                "secret-promotion-write-pending",
                "The destination write must be reconciled if this operation is interrupted.",
                false));
            SetJournalResult(journalResults, results[^1]);
            WriteReceipt(request, plan, CreateJournalSummary(plan.JobName, journalResults));

            results[^1] = ApplyRow(row, endpoints, request.Context);
            SetJournalResult(journalResults, results[^1]);
            WriteReceipt(request, plan, CreateJournalSummary(plan.JobName, journalResults));
        }

        var succeeded = results.All(row => row.Status is "Written" or "Skipped");
        var summary = new SecretPromotionSummary("apply", plan.JobName, true, succeeded, results, null, null);
        WriteReceipt(request, plan, CreateJournalSummary(plan.JobName, journalResults));
        return summary;
    }

    private static SecretPromotionSummary CreateJournalSummary(
        string jobName,
        IReadOnlyList<SecretPromotionRowResult> rows) =>
        new("apply", jobName, true, false, rows, null, null);

    private static void SetJournalResult(List<SecretPromotionRowResult> journalRows, SecretPromotionRowResult result)
    {
        var index = result.RowNumber - 1;
        if (index < journalRows.Count)
        {
            journalRows[index] = result;
            return;
        }

        if (index == journalRows.Count)
        {
            journalRows.Add(result);
            return;
        }

        throw SecretPromotionCommandExtensions.Usage("The receipt journal is not in declared row order.");
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
            if (plan?.Version != 1 ||
                plan.Rows is null ||
                plan.Rows.Any(static row => row is null) ||
                string.IsNullOrWhiteSpace(plan.JobName))
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

        if (IsLocal(endpoints.Destination))
        {
            throw SecretPromotionCommandExtensions.Usage("V1 promotion destinations must be declared Google endpoints.");
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
            .Select(row => ResourceForDestination(row, endpoints.Destination))
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
            DestinationHasEnabledVersions = false,
            DestinationExists = null
        };

    private SecretPromotionRowResult ProbeRow(SecretPromotionPlanRow row, ResolvedEndpoints endpoints, SecretsCommandContext context, bool replace)
    {
        var source = ProbeSource(row, endpoints.Source, context);
        if (source is not null)
        {
            return source;
        }

        return ProbeDestination(row, endpoints.Destination, replace, includePrecondition: true);
    }

    private SecretPromotionPlanRow CaptureDestinationPrecondition(
        SecretPromotionPlanRow row,
        SecretPromotionEndpoint endpoint)
    {
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

        var destination = ProbeDestination(row, endpoints.Destination, replace, includePrecondition: true);
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
            : row.GoogleSourceFailure("ProbeGoogleSource", result.Status, result.Diagnostic);
    }

    private SecretPromotionRowResult ProbeDestination(
        SecretPromotionPlanRow row,
        SecretPromotionEndpoint endpoint,
        bool replace,
        bool includePrecondition)
    {
        var result = googleFactory.Create(endpoint).ProbeSecret(row.DestinationResource!, TimeSpan.FromSeconds(5));
        if (result.Status != GoogleSecretManagerTransferStatus.Ready)
        {
            return row.GoogleDestinationFailure("ProbeGoogleDestination", result.Status, result.Diagnostic);
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

        return WriteGoogle(row, endpoints.Destination, value!);
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

            failure = localResult.Status == LocalSecretResultStatus.Missing
                ? row.Result("SourceMissing", "ReadLocalSource", localResult.Diagnostic?.Code, localResult.Diagnostic?.Problem, localResult.Diagnostic?.Retryable)
                : row.Result("Failed", "ReadLocalSource", localResult.Diagnostic?.Code, localResult.Diagnostic?.Problem, localResult.Diagnostic?.Retryable);
            return false;
        }

        var result = googleFactory.Create(endpoint).AccessSecretVersion(row.SourceResource!, TimeSpan.FromSeconds(5));
        if (result.Status != GoogleSecretManagerTransferStatus.Ready || result.Payload is null)
        {
            failure = row.GoogleSourceFailure("AccessGoogleSource", result.Status, result.Diagnostic);
            return false;
        }

        if (!TryDecodeUtf8(result.Payload.Data, out value))
        {
            failure = row.Result("Failed", "DecodeGoogleSource", "secret-promotion-invalid-payload", "Google source payload is not valid UTF-8 text.", false);
            return false;
        }

        return true;
    }

    private SecretPromotionRowResult WriteGoogle(SecretPromotionPlanRow row, SecretPromotionEndpoint endpoint, string value)
    {
        var result = googleFactory.Create(endpoint).AddSecretVersion(row.DestinationResource!, value, TimeSpan.FromSeconds(5));
        if (result.Status == GoogleSecretManagerTransferStatus.Written)
        {
            return row.Result("Written", "AddedGoogleVersion", null, null, null, result.WrittenVersionResourceName);
        }

        return result.Status == GoogleSecretManagerTransferStatus.IndeterminateWrite
            ? row.Result("IndeterminateWrite", "AddGoogleVersion", result.Diagnostic?.Code ?? "secret-promotion-indeterminate-write", "Google may have accepted the version before the response failed. Reconcile before resuming.", false)
            : row.GoogleDestinationFailure("AddGoogleVersion", result.Status, result.Diagnostic);
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

    private static string ComputePlanIdentity(
        string jobName,
        string configDigest,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        bool replace,
        bool production,
        bool ready,
        IReadOnlyList<SecretPromotionPlanRow> rows)
    {
        var material = new SecretPromotionPlanIdentityMaterial(
            1,
            jobName,
            configDigest,
            createdAtUtc,
            expiresAtUtc,
            replace,
            production,
            ready,
            rows);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(material, JsonOptions)))
            .ToLowerInvariant();
    }

    private static void ValidatePlanIdentity(SecretPromotionPlanArtifact plan)
    {
        var expected = ComputePlanIdentity(
            plan.JobName,
            plan.ConfigDigest,
            plan.CreatedAtUtc,
            plan.ExpiresAtUtc,
            plan.Replace,
            plan.Production,
            plan.Ready,
            plan.Rows);
        if (!string.Equals(plan.PlanIdentity, expected, StringComparison.Ordinal))
        {
            throw SecretPromotionCommandExtensions.Usage("The plan identity is invalid. Create a new plan before applying.");
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

    private static ResumeReceiptState LoadResumeState(string? receiptPath, SecretPromotionPlanArtifact plan)
    {
        if (string.IsNullOrWhiteSpace(receiptPath))
        {
            return new ResumeReceiptState([], new Dictionary<int, SecretPromotionRowResult>());
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<SecretPromotionReceipt>(File.ReadAllBytes(Path.GetFullPath(receiptPath)), JsonOptions);
            if (receipt is null ||
                receipt.PlanJob != plan.JobName ||
                receipt.ConfigDigest != plan.ConfigDigest ||
                receipt.PlanIdentity != plan.PlanIdentity)
            {
                throw SecretPromotionCommandExtensions.Usage("--resume does not match the supplied plan.");
            }

            if (receipt.Rows is null || receipt.Rows.Any(static row => row is null))
            {
                throw SecretPromotionCommandExtensions.Usage("--resume contains rows that do not match the supplied plan.");
            }

            ValidateReceiptRows(receipt.Rows, plan.Rows);
            if (receipt.Rows.Any(static row => row.Status == "IndeterminateWrite"))
            {
                throw SecretPromotionCommandExtensions.Usage(
                    "--resume contains an indeterminate write. Reconcile the destination before creating a new plan.");
            }

            var completedRows = receipt.Rows
                .Where(static row => row.Status == "Written")
                .ToDictionary(static row => row.RowNumber);
            return new ResumeReceiptState(receipt.Rows, completedRows);
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

    private void VerifyCompletedRows(
        IReadOnlyDictionary<int, SecretPromotionRowResult> completedRows,
        SecretPromotionEndpoint destination)
    {
        if (completedRows.Count == 0)
        {
            return;
        }

        var client = googleFactory.Create(destination);
        foreach (var row in completedRows.Values.OrderBy(static row => row.RowNumber))
        {
            var result = client.ProbeSecretVersion(row.DestinationResource!, TimeSpan.FromSeconds(5));
            if (result.Status != GoogleSecretManagerTransferStatus.Ready)
            {
                throw SecretPromotionCommandExtensions.Usage(
                    "--resume written-version evidence could not be verified. Reconcile the destination before retrying.");
            }
        }
    }

    private void WriteReceipt(SecretPromotionApplyRequest request, SecretPromotionPlanArtifact plan, SecretPromotionSummary summary)
    {
        var path = request.ReceiptPath ?? $"{request.PlanPath}.receipt.json";
        _receiptWriter.Write(
            path,
            new SecretPromotionReceipt(plan.JobName, plan.ConfigDigest, plan.PlanIdentity, summary.Rows));
    }

    private static void ValidateReceiptRows(
        IReadOnlyList<SecretPromotionRowResult> receiptRows,
        IReadOnlyList<SecretPromotionPlanRow> planRows)
    {
        if (receiptRows.Count > planRows.Count)
        {
            throw SecretPromotionCommandExtensions.Usage("--resume contains rows that do not match the supplied plan.");
        }

        for (var index = 0; index < receiptRows.Count; index++)
        {
            var receiptRow = receiptRows[index];
            var planned = planRows[index];
            if (receiptRow.RowNumber != planned.RowNumber ||
                !string.Equals(receiptRow.Key, planned.Key, StringComparison.Ordinal) ||
                !string.Equals(receiptRow.SourceEndpoint, planned.SourceEndpoint, StringComparison.Ordinal) ||
                !string.Equals(receiptRow.SourceResource, planned.SourceResource, StringComparison.Ordinal) ||
                !string.Equals(receiptRow.DestinationEndpoint, planned.DestinationEndpoint, StringComparison.Ordinal) ||
                !IsReceiptDestinationResource(receiptRow, planned) ||
                receiptRow.Status == "Written" && receiptRow.Action != "AddedGoogleVersion")
            {
                throw SecretPromotionCommandExtensions.Usage("--resume contains rows that do not match the supplied plan.");
            }
        }
    }

    private static bool IsReceiptDestinationResource(
        SecretPromotionRowResult receiptRow,
        SecretPromotionPlanRow planned) =>
        string.Equals(receiptRow.DestinationResource, planned.DestinationResource, StringComparison.Ordinal) ||
        receiptRow.Status == "Written" &&
        IsNumericVersionResource(receiptRow.DestinationResource) &&
        receiptRow.DestinationResource?.StartsWith($"{planned.DestinationResource}/versions/", StringComparison.Ordinal) == true;

    private sealed record LoadedConfiguration(SecretPromotionConfiguration Configuration, string Digest);
    private sealed record ResolvedEndpoints(SecretPromotionEndpoint Source, SecretPromotionEndpoint Destination);
    private sealed record ResumeReceiptState(
        IReadOnlyList<SecretPromotionRowResult> Rows,
        IReadOnlyDictionary<int, SecretPromotionRowResult> CompletedRows);
    private sealed record SecretPromotionPlanIdentityMaterial(
        int Version,
        string JobName,
        string ConfigDigest,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        bool Replace,
        bool Production,
        bool Ready,
        IReadOnlyList<SecretPromotionPlanRow> Rows);
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

/// <summary>Describes a value-free request to create a promotion plan.</summary>
/// <param name="ConfigPath">Path to the reviewed endpoint and job configuration.</param>
/// <param name="JobName">Exact declared job name to plan.</param>
/// <param name="OutputPlanPath">Destination for the value-free plan artifact.</param>
/// <param name="Replace">Whether existing enabled Google versions may receive another version.</param>
/// <param name="Expiry">Positive lifetime after which apply must reject the plan.</param>
/// <param name="Context">LocalSecrets identity and metadata-only store context.</param>
internal sealed record SecretPromotionPlanRequest(string ConfigPath, string JobName, string OutputPlanPath, bool Replace, TimeSpan Expiry, SecretsCommandContext Context);

/// <summary>Describes a dry-run or mutation-gated promotion apply request.</summary>
/// <param name="ConfigPath">Path to the same reviewed configuration used for planning.</param>
/// <param name="PlanPath">Path to the unexpired value-free plan.</param>
/// <param name="Apply">Whether destination mutations are permitted; false performs apply preflight only.</param>
/// <param name="Confirmation">Exact job name required for a production destination.</param>
/// <param name="ReceiptPath">Optional value-free receipt path; defaults beside the plan.</param>
/// <param name="ResumeReceiptPath">Optional prior receipt whose confirmed writes may be skipped.</param>
/// <param name="Context">LocalSecrets identity and store context used only after preflight permits payload access.</param>
internal sealed record SecretPromotionApplyRequest(string ConfigPath, string PlanPath, bool Apply, string? Confirmation, string? ReceiptPath, string? ResumeReceiptPath, SecretsCommandContext Context);

/// <summary>Wraps the display-safe result returned after plan creation.</summary>
/// <param name="Summary">Value-free row summary and plan location.</param>
internal sealed record SecretPromotionPlanResult(SecretPromotionSummary Summary);

/// <summary>Defines the versioned endpoint and named-job configuration authorization boundary.</summary>
/// <param name="Version">Configuration schema version; v1 is required.</param>
/// <param name="Endpoints">Explicit remote endpoint profiles; LocalSecrets is the built-in <c>local</c> endpoint.</param>
/// <param name="Jobs">Reviewed jobs that bind exact sources, Google destinations, and rows.</param>
internal sealed record SecretPromotionConfiguration(int Version, IReadOnlyList<SecretPromotionEndpoint> Endpoints, IReadOnlyList<SecretPromotionJob> Jobs);

/// <summary>Declares one named provider endpoint without embedding credential values.</summary>
/// <param name="Name">Case-sensitive endpoint name referenced by jobs.</param>
/// <param name="Provider">Provider identifier; v1 accepts <c>google</c> plus built-in local sources.</param>
/// <param name="Environment">Environment label used to enforce production rules.</param>
/// <param name="Credential">Explicit Google credential mode; null is valid only for the built-in local endpoint.</param>
internal sealed record SecretPromotionEndpoint(string Name, string Provider, string Environment, SecretPromotionCredential? Credential);

/// <summary>Chooses a Google credential acquisition mode without carrying raw credentials.</summary>
/// <param name="Mode"><c>applicationDefault</c> or <c>credentialFile</c>.</param>
/// <param name="Path">Absolute restricted credential-file path when that mode is selected; never emitted.</param>
internal sealed record SecretPromotionCredential(string Mode, string? Path);

/// <summary>Declares one reviewed, direction-specific promotion job.</summary>
/// <param name="Name">Case-sensitive job identity used by plan, confirmation, and receipt checks.</param>
/// <param name="Source">Built-in <c>local</c> or a declared Google endpoint name.</param>
/// <param name="Destination">Declared Google endpoint name; local destinations are rejected in v1.</param>
/// <param name="AllowMutableLocalSource">Explicit production exception for a mutable LocalSecrets source.</param>
/// <param name="Rows">Ordered explicit key and resource mappings.</param>
internal sealed record SecretPromotionJob(string Name, string Source, string Destination, bool AllowMutableLocalSource, IReadOnlyList<SecretPromotionJobRow> Rows);

/// <summary>Maps one logical LocalSecrets key to explicit provider resources.</summary>
/// <param name="Key">Required logical AppSurface configuration key and row identity.</param>
/// <param name="Source">Explicit Google version resource, or null for a LocalSecrets source.</param>
/// <param name="Destination">Explicit existing Google secret parent resource.</param>
internal sealed record SecretPromotionJobRow(string Key, string? Source, string? Destination);

/// <summary>Persists an expiring, value-free plan bound to all safety-relevant fields.</summary>
/// <param name="Version">Plan schema version.</param>
/// <param name="JobName">Declared job identity.</param>
/// <param name="ConfigDigest">Digest of the exact endpoint configuration bytes.</param>
/// <param name="CreatedAtUtc">UTC creation timestamp included in plan identity.</param>
/// <param name="ExpiresAtUtc">UTC expiration enforced before payload access.</param>
/// <param name="Replace">Whether another enabled Google version is authorized.</param>
/// <param name="Production">Value-free production label captured for review.</param>
/// <param name="Ready">Whether all plan-time probes were ready or intentionally skipped.</param>
/// <param name="PlanIdentity">Stable SHA-256 identity over every plan safety field and row precondition.</param>
/// <param name="Rows">Ordered canonical mappings and destination preconditions; never payloads.</param>
internal sealed record SecretPromotionPlanArtifact(int Version, string JobName, string ConfigDigest, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, bool Replace, bool Production, bool Ready, string PlanIdentity, IReadOnlyList<SecretPromotionPlanRow> Rows);

/// <summary>Captures one canonical, value-free plan row and its destination precondition.</summary>
/// <param name="RowNumber">One-based declared processing order.</param>
/// <param name="Key">Explicit logical LocalSecrets key.</param>
/// <param name="SourceEndpoint">Declared source endpoint name.</param>
/// <param name="SourceResource">Canonical Google version resource, or <c>local</c>.</param>
/// <param name="DestinationEndpoint">Declared Google destination endpoint name.</param>
/// <param name="DestinationResource">Canonical existing Google secret parent.</param>
/// <param name="LocalStorageName">Normalized LocalSecrets identity used to detect duplicate logical mappings.</param>
/// <param name="DestinationHasEnabledVersions">Google enabled-version precondition captured during planning.</param>
/// <param name="DestinationExists">Reserved provider-neutral existence precondition; null for v1 Google destinations.</param>
internal sealed record SecretPromotionPlanRow(int RowNumber, string Key, string SourceEndpoint, string? SourceResource, string DestinationEndpoint, string? DestinationResource, string LocalStorageName, bool? DestinationHasEnabledVersions, bool? DestinationExists)
{
    /// <summary>Creates a value-free row result while preserving canonical row identity.</summary>
    /// <param name="status">Stable workflow status.</param>
    /// <param name="action">Display-safe operation classification.</param>
    /// <param name="diagnosticCode">Optional stable diagnostic code.</param>
    /// <param name="problem">Optional paste-safe problem statement.</param>
    /// <param name="retryable">Whether an operator may retry without reconciliation.</param>
    /// <param name="writtenResource">Confirmed written Google version resource, when available.</param>
    /// <returns>A result that never includes secret payloads.</returns>
    public SecretPromotionRowResult Result(string status, string action, string? diagnosticCode, string? problem, bool? retryable, string? writtenResource = null) =>
        new(RowNumber, Key, SourceEndpoint, SourceResource, DestinationEndpoint, writtenResource ?? DestinationResource, status, action, diagnosticCode, problem, retryable);

    /// <summary>Maps a Google source failure without misclassifying destination absence.</summary>
    /// <param name="action">Source probe or access action.</param>
    /// <param name="status">Provider status to classify.</param>
    /// <param name="diagnostic">Optional value-safe provider diagnostic.</param>
    /// <returns>A value-free source result.</returns>
    public SecretPromotionRowResult GoogleSourceFailure(string action, GoogleSecretManagerTransferStatus status, AppSurfaceGoogleSecretTransferDiagnostic? diagnostic) =>
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

    /// <summary>Maps a Google destination failure, preserving indeterminate write state.</summary>
    /// <param name="action">Destination probe or write action.</param>
    /// <param name="status">Provider status to classify.</param>
    /// <param name="diagnostic">Optional value-safe provider diagnostic.</param>
    /// <returns>A value-free destination result.</returns>
    public SecretPromotionRowResult GoogleDestinationFailure(string action, GoogleSecretManagerTransferStatus status, AppSurfaceGoogleSecretTransferDiagnostic? diagnostic) =>
        Result(
            status switch
            {
                GoogleSecretManagerTransferStatus.AccessDenied => "AccessDenied",
                GoogleSecretManagerTransferStatus.Unavailable => "Unavailable",
                GoogleSecretManagerTransferStatus.Cancelled => "Cancelled",
                GoogleSecretManagerTransferStatus.InvalidResource => "InvalidResource",
                GoogleSecretManagerTransferStatus.IndeterminateWrite => "IndeterminateWrite",
                _ => "Failed"
            },
            action,
            diagnostic?.Code ?? "google-secret-promotion-failed",
            diagnostic?.Problem ?? "Google Secret Manager promotion failed.",
            diagnostic?.Retryable);
}

/// <summary>Reports one value-free row outcome.</summary>
/// <param name="RowNumber">One-based declared processing order.</param>
/// <param name="Key">Logical AppSurface key; never its value.</param>
/// <param name="SourceEndpoint">Declared source endpoint name.</param>
/// <param name="SourceResource">Canonical source identity.</param>
/// <param name="DestinationEndpoint">Declared Google destination endpoint name.</param>
/// <param name="DestinationResource">Planned secret parent or confirmed written version resource.</param>
/// <param name="Status">Stable result classification.</param>
/// <param name="Action">Display-safe action classification.</param>
/// <param name="DiagnosticCode">Optional stable diagnostic code.</param>
/// <param name="Problem">Optional paste-safe problem statement.</param>
/// <param name="Retryable">Whether retry is safe without reconciliation.</param>
internal sealed record SecretPromotionRowResult(int RowNumber, string Key, string SourceEndpoint, string? SourceResource, string DestinationEndpoint, string? DestinationResource, string Status, string Action, string? DiagnosticCode, string? Problem, bool? Retryable);

/// <summary>Aggregates ordered value-free plan or apply results.</summary>
/// <param name="Operation"><c>plan</c> or <c>apply</c>.</param>
/// <param name="Job">Declared job name.</param>
/// <param name="Apply">Whether payload reads and destination writes were permitted.</param>
/// <param name="Succeeded">Whether every row completed or was intentionally skipped.</param>
/// <param name="Rows">Ordered value-free row outcomes.</param>
/// <param name="PlanPath">Written plan path for plan output.</param>
/// <param name="ReceiptPath">Written receipt path when surfaced by a caller.</param>
internal sealed record SecretPromotionSummary(string Operation, string Job, bool Apply, bool Succeeded, IReadOnlyList<SecretPromotionRowResult> Rows, string? PlanPath, string? ReceiptPath);

/// <summary>Provides the durable, value-free apply journal and resume evidence.</summary>
/// <param name="PlanJob">Exact job name from the plan.</param>
/// <param name="ConfigDigest">Exact configuration digest from the plan.</param>
/// <param name="PlanIdentity">Identity binding every safety field and destination precondition.</param>
/// <param name="Rows">Atomically persisted ordered outcomes; indeterminate rows block automatic resume.</param>
internal sealed record SecretPromotionReceipt(string PlanJob, string ConfigDigest, string PlanIdentity, IReadOnlyList<SecretPromotionRowResult> Rows);
