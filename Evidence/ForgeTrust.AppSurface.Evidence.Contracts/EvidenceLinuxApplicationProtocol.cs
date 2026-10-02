using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Closed roles in an independently inspected restricted application bundle.</summary>
internal enum EvidenceLinuxApplicationBundleRole
{
    /// <summary>Managed AppHost DLL.</summary>
    AppHost,
    /// <summary>AppHost runtime configuration.</summary>
    AppHostRuntimeConfiguration,
    /// <summary>Managed resource DLL.</summary>
    Resource,
    /// <summary>Resource runtime configuration.</summary>
    ResourceRuntimeConfiguration,
    /// <summary>Native DCP executable.</summary>
    Dcp,
    /// <summary>Native DCP extension executable.</summary>
    DcpExtension,
    /// <summary>Immutable dependency payload.</summary>
    Dependency,
    /// <summary>Immutable declared application input.</summary>
    DeclaredInput,
    /// <summary>Managed dependency manifest ending in .deps.json.</summary>
    DependencyManifest,
}

/// <summary>Root-inspected metadata, never a caller-selected executable or host path.</summary>
/// <param name="RelativePath">Normalized ASCII name below the protected bundle.</param>
/// <param name="Role">Closed payload role.</param>
/// <param name="LengthBytes">Positive exact byte length, at most 128 MiB.</param>
/// <param name="Sha256">Lower-case SHA-256 of independently retained bytes.</param>
/// <param name="Mode">Unix permission bits, 0444 or executable 0555.</param>
internal sealed record EvidenceLinuxApplicationBundleFile(string RelativePath, EvidenceLinuxApplicationBundleRole Role,
    long LengthBytes, string Sha256, uint Mode);

/// <summary>Finite grants for one secret-free, private-network restricted application.</summary>
/// <param name="ReadOnlyInputs">Exactly one declared immutable bundle name.</param>
/// <param name="ScratchBytes">Positive scratch allowance, at most 1 GiB.</param>
/// <param name="MemoryBytes">Positive memory allowance, at most 1 GiB.</param>
/// <param name="MaximumTasks">Positive task allowance, at most 64.</param>
/// <param name="MaximumOutputBytes">Positive received-output allowance, at most 1 MiB.</param>
/// <param name="StartSeconds">Positive startup cap, at most 120 seconds.</param>
/// <param name="StoppingSeconds">Positive stop cap, at most 30 seconds.</param>
/// <remarks>No secrets, privileged groups, external network or protected/result-root grants exist in this schema.</remarks>
internal sealed record EvidenceLinuxApplicationCapabilities(IReadOnlyList<string> ReadOnlyInputs, long ScratchBytes,
    long MemoryBytes, int MaximumTasks, long MaximumOutputBytes, int StartSeconds, int StoppingSeconds);

/// <summary>Immutable typed application facts parsed from the authenticated v2 root descriptor.</summary>
/// <param name="ApplicationId">Closed compiled application identifier.</param>
/// <param name="ApplicationVersion">Closed application version.</param>
/// <param name="BuildId">Reviewed immutable build identity.</param>
/// <param name="CatalogueDigest">Complete compiled catalogue digest.</param>
/// <param name="EntryDigest">Complete compiled entry digest.</param>
/// <param name="AspireSdkVersion">Exactly 13.4.4.</param>
/// <param name="Resources">Complete resource declarations.</param>
/// <param name="Producers">Complete producer declarations, including artifacts and coverage gates.</param>
/// <param name="BundleFiles">Root-inspected complete immutable inventory.</param>
/// <param name="Capabilities">Exact bounded application grants.</param>
/// <param name="ApplicationUid">Separate positive application UID.</param>
/// <param name="ApplicationGid">Separate positive application primary GID.</param>
/// <param name="ResultsGid">Producer-results GID, never granted to the application.</param>
/// <param name="ResourceAccessGid">Separate resource-access GID.</param>
/// <remarks>These facts supply no proof, admission, lease or factory authority by themselves.</remarks>
internal sealed record EvidenceLinuxApplicationDescriptor(string ApplicationId, string ApplicationVersion, string BuildId,
    string CatalogueDigest, string EntryDigest, string AspireSdkVersion, IReadOnlyList<EvidenceResourceDeclaration> Resources,
    IReadOnlyList<EvidenceProducerDeclaration> Producers, IReadOnlyList<EvidenceLinuxApplicationBundleFile> BundleFiles,
    EvidenceLinuxApplicationCapabilities Capabilities, uint ApplicationUid, uint ApplicationGid, uint ResultsGid,
    uint ResourceAccessGid);

/// <summary>Validated owned-process startup metadata; contains no readiness assertion.</summary>
/// <param name="LeaseId">Root-generated 32-character lower-case hexadecimal lease.</param>
/// <param name="AppHostPid">Positive AppHost PID.</param>
/// <param name="ApplicationUid">Exact descriptor application UID.</param>
/// <param name="ApplicationGid">Exact descriptor application GID.</param>
/// <param name="Cgroup">Exact /system.slice/issue779-app-&lt;lease&gt;.service cgroup.</param>
internal sealed record EvidenceLinuxApplicationStartReceipt(string LeaseId, int AppHostPid, uint ApplicationUid,
    uint ApplicationGid, string Cgroup);

/// <summary>Root-observed kernel-peer and HTTP readiness metadata, separate from AppHost output.</summary>
/// <param name="LeaseId">Exact previously started lease.</param>
/// <param name="ResourceId">Exact declared resource identifier.</param>
/// <param name="ApplicationUid">Exact application UID.</param>
/// <param name="Cgroup">Exact previously started application cgroup.</param>
/// <param name="HttpStatus">Exactly 200.</param>
/// <param name="ReceivedBytes">Nonnegative received readiness bytes, at most 4096.</param>
internal sealed record EvidenceLinuxApplicationResourceReceipt(string LeaseId, string ResourceId, uint ApplicationUid,
    string Cgroup, int HttpStatus, long ReceivedBytes);

internal sealed partial class EvidenceLinuxWorkerSupervisor
{
    private EvidenceLinuxApplicationStartReceipt? _applicationStartReceipt;
    private int _applicationStartClaimed;

    /// <summary>Starts only the descriptor-selected compiled application through the existing root-authenticated broker.</summary>
    /// <param name="appId">Exact descriptor application ID.</param>
    /// <param name="entryDigest">Exact descriptor entry digest.</param>
    /// <param name="cancellationToken">Startup cancellation; existing stop/wait use separate fresh connections.</param>
    /// <returns>Owned startup metadata without a readiness claim.</returns>
    /// <remarks>Admission and monotonic arming precede all I/O. A failed start cannot be retried in this supervisor.</remarks>
    internal async Task<EvidenceLinuxApplicationStartReceipt> StartApplicationAsync(string appId, string entryDigest,
        CancellationToken cancellationToken)
    {
        RequireApplicationArmed();
        var application = Descriptor.Application;
        ApplicationRequire(application is not null && ApplicationId(appId) && ApplicationHash(entryDigest)
            && appId == application.ApplicationId && entryDigest == application.EntryDigest);
        if (Interlocked.CompareExchange(ref _applicationStartClaimed, 1, 0) != 0) throw ApplicationFailure("ASEVD410");
        var response = await RequestApplicationAsync(new { op = "application-start", application_id = appId,
            entry_digest = entryDigest }, cancellationToken).ConfigureAwait(false);
        var receipt = ParseApplicationStartReceipt(response, application);
        Volatile.Write(ref _applicationStartReceipt, receipt);
        return receipt;
    }

    /// <summary>Waits for independent root-observed readiness of one resource in the previously started application.</summary>
    /// <param name="leaseId">Exact validated startup lease.</param>
    /// <param name="resourceId">Exact descriptor resource ID.</param>
    /// <param name="cancellationToken">Readiness cancellation; does not acknowledge stop or owned exit.</param>
    /// <returns>Validated kernel-peer and bounded HTTP readiness metadata.</returns>
    internal async Task<EvidenceLinuxApplicationResourceReceipt> WaitForApplicationResourceAsync(string leaseId,
        string resourceId, CancellationToken cancellationToken)
    {
        RequireApplicationArmed();
        var application = Descriptor.Application;
        var started = Volatile.Read(ref _applicationStartReceipt);
        if (application is null || started is null || leaseId != started.LeaseId || !ApplicationId(resourceId)
            || !application.Resources.Any(item => item.Id == resourceId)) throw ApplicationFailure("ASEVD410");
        var response = await RequestApplicationAsync(new { op = "resource-wait", lease_id = leaseId,
            resource_id = resourceId }, cancellationToken).ConfigureAwait(false);
        return ParseApplicationResourceReceipt(response, application, started, resourceId);
    }

    /// <summary>Parses closed, bounded v2 application metadata and defensively copies every declaration list.</summary>
    /// <param name="value">Application object from the authenticated root descriptor.</param>
    /// <param name="workerUid">Actual authenticated worker UID.</param>
    /// <param name="workerGid">Actual authenticated worker primary GID.</param>
    /// <param name="producerUid">Separately restricted producer UID.</param>
    /// <param name="producerGid">Separately restricted producer primary GID.</param>
    /// <returns>Immutable metadata with three distinct positive UIDs and five distinct positive GIDs.</returns>
    /// <exception cref="EvidenceAdmissionException">ASEVD402 for invalid, unsupported or ambiguous data.</exception>
    internal static EvidenceLinuxApplicationDescriptor ParseApplicationDescriptor(JsonElement value, uint workerUid,
        uint workerGid, uint producerUid, uint producerGid)
    {
        ApplicationObject(value, "application_id", "application_version", "build_id", "catalogue_digest", "entry_digest",
            "aspire_sdk_version", "resources", "producers", "bundle_files", "capabilities", "application_uid",
            "application_gid", "results_gid", "resource_access_gid");
        ApplicationRequire(Encoding.UTF8.GetByteCount(value.GetRawText()) <= 1024 * 1024);
        var resources = ApplicationList(value.GetProperty("resources"), EvidenceProfileLimits.MaximumResources, ParseApplicationResource);
        var producers = ApplicationList(value.GetProperty("producers"), EvidenceProfileLimits.MaximumProducers, ParseApplicationProducer);
        ApplicationRequire(resources.Count > 0 && producers.Count > 0);
        ApplicationRequire(resources.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() == resources.Count
            && producers.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() == producers.Count);
        var resourceIds = resources.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        ApplicationRequire(resources.All(item => !item.Requires.Contains(item.Id, StringComparer.Ordinal)
            && item.Requires.All(resourceIds.Contains)) && producers.All(item => item.RequiredResources.All(resourceIds.Contains)));
        var bundle = ApplicationList(value.GetProperty("bundle_files"), 256, ParseApplicationBundleFile);
        ValidateApplicationBundle(bundle);
        var capabilities = ParseApplicationCapabilities(value.GetProperty("capabilities"));
        ApplicationRequire(bundle.Where(static item => item.Role == EvidenceLinuxApplicationBundleRole.DeclaredInput)
            .Select(static item => item.RelativePath).SequenceEqual(capabilities.ReadOnlyInputs, StringComparer.Ordinal));
        var appUid = ApplicationUInt(value, "application_uid"); var appGid = ApplicationUInt(value, "application_gid");
        var resultsGid = ApplicationUInt(value, "results_gid"); var resourceGid = ApplicationUInt(value, "resource_access_gid");
        var uids = new[] { workerUid, producerUid, appUid }; var gids = new[] { workerGid, producerGid, appGid, resultsGid, resourceGid };
        ApplicationRequire(uids.All(static item => item > 0) && uids.Distinct().Count() == 3
            && gids.All(static item => item > 0) && gids.Distinct().Count() == 5);
        var sdk = ApplicationString(value, "aspire_sdk_version", 128);
        ApplicationRequire(sdk == "13.4.4");
        return new(ApplicationIdentifier(value, "application_id"), ApplicationIdentifier(value, "application_version"),
            ApplicationIdentifier(value, "build_id"), ApplicationDigest(value, "catalogue_digest"),
            ApplicationDigest(value, "entry_digest"), sdk, resources, producers, bundle, capabilities, appUid, appGid,
            resultsGid, resourceGid);
    }

    /// <summary>Validates a closed startup acknowledgement without treating it as readiness.</summary>
    /// <param name="value">Authenticated application-start response.</param>
    /// <param name="expected">Independently parsed expected descriptor.</param>
    /// <returns>Immutable owned-process receipt.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for an invalid acknowledgement.</exception>
    internal static EvidenceLinuxApplicationStartReceipt ParseApplicationStartReceipt(JsonElement value,
        EvidenceLinuxApplicationDescriptor expected)
    {
        try
        {
            ApplicationObject(value, "ok", "lease_id", "apphost_pid", "application_uid", "application_gid", "cgroup", "owned");
            ApplicationRequire(expected is not null && ApplicationTrue(value, "ok") && ApplicationTrue(value, "owned"));
            var lease = ApplicationString(value, "lease_id", 32); ApplicationRequire(ApplicationLease(lease));
            var pid = ApplicationLong(value, "apphost_pid", 1, int.MaxValue);
            var uid = ApplicationUInt(value, "application_uid"); var gid = ApplicationUInt(value, "application_gid");
            var cgroup = ApplicationString(value, "cgroup", 128);
            ApplicationRequire(uid > 0 && gid > 0 && uid == expected.ApplicationUid && gid == expected.ApplicationGid
                && cgroup == ApplicationCgroup(lease));
            return new(lease, (int)pid, uid, gid, cgroup);
        }
        catch (EvidenceAdmissionException) { throw ApplicationFailure("ASEVD410"); }
    }

    /// <summary>Validates independent kernel-peer and HTTP readiness for the exact owned lease and resource.</summary>
    /// <param name="value">Authenticated resource-wait response.</param>
    /// <param name="expected">Independently parsed expected descriptor.</param>
    /// <param name="started">Previously validated owned startup receipt.</param>
    /// <param name="resourceId">Exact expected descriptor resource ID.</param>
    /// <returns>Immutable bounded readiness metadata.</returns>
    /// <exception cref="EvidenceAdmissionException">Fixed ASEVD410 for an invalid or unsuccessful acknowledgement.</exception>
    internal static EvidenceLinuxApplicationResourceReceipt ParseApplicationResourceReceipt(JsonElement value,
        EvidenceLinuxApplicationDescriptor expected, EvidenceLinuxApplicationStartReceipt started, string resourceId)
    {
        try
        {
            ApplicationObject(value, "ok", "lease_id", "resource_id", "application_uid", "cgroup", "kernel_peer_checked",
                "http_status", "healthy", "received_bytes");
            ApplicationRequire(expected is not null && started is not null && started.AppHostPid > 0 && ApplicationLease(started.LeaseId)
                && started.ApplicationUid == expected.ApplicationUid && started.ApplicationGid == expected.ApplicationGid
                && started.Cgroup == ApplicationCgroup(started.LeaseId) && ApplicationId(resourceId)
                && expected.Resources.Any(item => item.Id == resourceId));
            ApplicationRequire(ApplicationTrue(value, "ok") && ApplicationTrue(value, "kernel_peer_checked") && ApplicationTrue(value, "healthy"));
            var lease = ApplicationString(value, "lease_id", 32); var resource = ApplicationIdentifier(value, "resource_id");
            var uid = ApplicationUInt(value, "application_uid"); var cgroup = ApplicationString(value, "cgroup", 128);
            var status = ApplicationLong(value, "http_status", 200, 200); var bytes = ApplicationLong(value, "received_bytes", 0, 4096);
            ApplicationRequire(lease == started.LeaseId && resource == resourceId && uid == expected.ApplicationUid && cgroup == started.Cgroup);
            return new(lease, resource, uid, cgroup, (int)status, bytes);
        }
        catch (EvidenceAdmissionException) { throw ApplicationFailure("ASEVD410"); }
    }

    private void RequireApplicationArmed()
    {
        if (Volatile.Read(ref _closed) != 0 || !IsArmed) throw ApplicationFailure("ASEVD410");
    }

    private async Task<JsonElement> RequestApplicationAsync<T>(T request, CancellationToken cancellationToken)
    {
        try { return await RequestAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (EvidenceAdmissionException error) when (error.Code == "ASEVD420") { throw ApplicationFailure("ASEVD410"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or SocketException or IOException) { throw ApplicationFailure("ASEVD410"); }
    }

    private static EvidenceResourceDeclaration ParseApplicationResource(JsonElement value)
    {
        ApplicationObject(value, "id", "readiness", "deadline_seconds", "requires");
        var readiness = ApplicationString(value, "readiness", 128);
        ApplicationRequire(readiness is "aspire_health" or "completion");
        return new(ApplicationIdentifier(value, "id"), readiness, (int)ApplicationLong(value, "deadline_seconds", 1, 120),
            ApplicationIdentifiers(value.GetProperty("requires"), 16));
    }

    private static EvidenceProducerDeclaration ParseApplicationProducer(JsonElement value)
    {
        ApplicationObject(value, "id", "kind", "version", "required_resources", "assertion_ids", "artifact_slots", "timeout_seconds", "coverage_gate");
        var assertions = ApplicationList(value.GetProperty("assertion_ids"), 128, static item => ApplicationText(item, 128));
        var artifacts = ApplicationList(value.GetProperty("artifact_slots"), 128, ParseApplicationArtifactSlot);
        ApplicationRequire(assertions.Distinct(StringComparer.Ordinal).Count() == assertions.Count
            && artifacts.Select(static item => item.LogicalName).Distinct(StringComparer.Ordinal).Count() == artifacts.Count);
        var gate = value.GetProperty("coverage_gate");
        return new(ApplicationIdentifier(value, "id"), ApplicationIdentifier(value, "kind"), ApplicationIdentifier(value, "version"),
            ApplicationIdentifiers(value.GetProperty("required_resources"), 16), assertions, artifacts,
            (int)ApplicationLong(value, "timeout_seconds", 1, 600), gate.ValueKind == JsonValueKind.Null ? null : ParseApplicationCoverageGate(gate));
    }

    private static EvidenceArtifactSlot ParseApplicationArtifactSlot(JsonElement value)
    {
        ApplicationObject(value, "logical_name", "relative_root", "media_type", "required", "maximum_bytes");
        var path = ApplicationString(value, "relative_root", 256); ApplicationRequire(ApplicationRelative(path));
        var required = value.GetProperty("required"); ApplicationRequire(required.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return new(ApplicationIdentifier(value, "logical_name"), path, ApplicationString(value, "media_type", 128),
            required.GetBoolean(), ApplicationLong(value, "maximum_bytes", 0, 256L * 1024 * 1024));
    }

    private static EvidenceCoverageGateRequirements ParseApplicationCoverageGate(JsonElement value)
    {
        ApplicationObject(value, "min_line_percent", "min_branch_percent", "min_patch_line_percent", "min_patch_branch_percent", "patch_line_mode", "tolerance_percent");
        var mode = ApplicationString(value, "patch_line_mode", 128); ApplicationRequire(mode is "measurable" or "codecov");
        return new(ApplicationPercentage(value.GetProperty("min_line_percent")), ApplicationPercentage(value.GetProperty("min_branch_percent")),
            ApplicationNullablePercentage(value.GetProperty("min_patch_line_percent")), ApplicationNullablePercentage(value.GetProperty("min_patch_branch_percent")),
            mode, ApplicationPercentage(value.GetProperty("tolerance_percent")));
    }

    private static EvidenceLinuxApplicationBundleFile ParseApplicationBundleFile(JsonElement value)
    {
        ApplicationObject(value, "relative_path", "role", "length_bytes", "sha256", "mode");
        var path = ApplicationString(value, "relative_path", 256); ApplicationRequire(ApplicationRelative(path));
        var role = ApplicationString(value, "role", 128) switch
        {
            "apphost" => EvidenceLinuxApplicationBundleRole.AppHost,
            "apphost_runtime_configuration" => EvidenceLinuxApplicationBundleRole.AppHostRuntimeConfiguration,
            "resource" => EvidenceLinuxApplicationBundleRole.Resource,
            "resource_runtime_configuration" => EvidenceLinuxApplicationBundleRole.ResourceRuntimeConfiguration,
            "dcp" => EvidenceLinuxApplicationBundleRole.Dcp,
            "dcp_extension" => EvidenceLinuxApplicationBundleRole.DcpExtension,
            "dependency" => EvidenceLinuxApplicationBundleRole.Dependency,
            "declared_input" => EvidenceLinuxApplicationBundleRole.DeclaredInput,
            "dependency_manifest" => EvidenceLinuxApplicationBundleRole.DependencyManifest,
            _ => throw ApplicationFailure("ASEVD402"),
        };
        var mode = ApplicationUInt(value, "mode");
        ApplicationRequire(mode == 0x124 || mode == 0x16d);
        ApplicationRequire(role is EvidenceLinuxApplicationBundleRole.Dcp or EvidenceLinuxApplicationBundleRole.DcpExtension
            ? mode == 0x16d : mode == 0x124);
        if (role == EvidenceLinuxApplicationBundleRole.Dcp) ApplicationRequire(path == "dcp/dcp");
        if (role == EvidenceLinuxApplicationBundleRole.DcpExtension) ApplicationRequire(path.StartsWith("dcp/ext/", StringComparison.Ordinal));
        if (role is EvidenceLinuxApplicationBundleRole.AppHost or EvidenceLinuxApplicationBundleRole.Resource) ApplicationRequire(path.EndsWith(".dll", StringComparison.Ordinal));
        if (role is EvidenceLinuxApplicationBundleRole.AppHostRuntimeConfiguration or EvidenceLinuxApplicationBundleRole.ResourceRuntimeConfiguration)
            ApplicationRequire(path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));
        if (role == EvidenceLinuxApplicationBundleRole.DependencyManifest) ApplicationRequire(path.EndsWith(".deps.json", StringComparison.Ordinal));
        return new(path, role, ApplicationLong(value, "length_bytes", 1, 128L * 1024 * 1024), ApplicationDigest(value, "sha256"), mode);
    }

    private static void ValidateApplicationBundle(IReadOnlyList<EvidenceLinuxApplicationBundleFile> files)
    {
        ApplicationRequire(files.Count >= 6 && files.Select(static item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Count);
        long total = 0;
        foreach (var file in files)
        {
            ApplicationRequire(file.LengthBytes <= 512L * 1024 * 1024 - total); total += file.LengthBytes;
            ApplicationRequire(!files.Any(other => other != file && other.RelativePath.StartsWith(file.RelativePath + "/", StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var role in new[] { EvidenceLinuxApplicationBundleRole.AppHost, EvidenceLinuxApplicationBundleRole.AppHostRuntimeConfiguration,
            EvidenceLinuxApplicationBundleRole.Resource, EvidenceLinuxApplicationBundleRole.ResourceRuntimeConfiguration,
            EvidenceLinuxApplicationBundleRole.Dcp, EvidenceLinuxApplicationBundleRole.DeclaredInput })
            ApplicationRequire(files.Count(item => item.Role == role) == 1);
    }

    private static EvidenceLinuxApplicationCapabilities ParseApplicationCapabilities(JsonElement value)
    {
        ApplicationObject(value, "read_only_inputs", "scratch_bytes", "memory_bytes", "maximum_tasks", "maximum_output_bytes", "start_seconds", "stopping_seconds");
        var inputs = ApplicationList(value.GetProperty("read_only_inputs"), 1, static item => ApplicationText(item, 256));
        ApplicationRequire(inputs.Count == 1 && inputs.All(ApplicationRelative));
        return new(inputs, ApplicationLong(value, "scratch_bytes", 1, 1024L * 1024 * 1024),
            ApplicationLong(value, "memory_bytes", 1, 1024L * 1024 * 1024), (int)ApplicationLong(value, "maximum_tasks", 1, 64),
            ApplicationLong(value, "maximum_output_bytes", 1, 1024 * 1024), (int)ApplicationLong(value, "start_seconds", 1, 120),
            (int)ApplicationLong(value, "stopping_seconds", 1, 30));
    }

    private static void ApplicationObject(JsonElement value, params string[] fields)
    {
        ApplicationRequire(value.ValueKind == JsonValueKind.Object);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            ApplicationRequire(names.Add(property.Name) && fields.Contains(property.Name, StringComparer.Ordinal));
        ApplicationRequire(names.Count == fields.Length);
    }

    private static IReadOnlyList<T> ApplicationList<T>(JsonElement value, int maximum, Func<JsonElement, T> parse)
    {
        ApplicationRequire(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum);
        return Array.AsReadOnly(value.EnumerateArray().Select(parse).ToArray());
    }

    private static IReadOnlyList<string> ApplicationIdentifiers(JsonElement value, int maximum)
    {
        var items = ApplicationList(value, maximum, static item => ApplicationText(item, 128));
        ApplicationRequire(items.All(ApplicationId) && items.Distinct(StringComparer.Ordinal).Count() == items.Count);
        return items;
    }

    private static string ApplicationString(JsonElement value, string field, int maximum) => ApplicationText(value.GetProperty(field), maximum);
    private static string ApplicationText(JsonElement value, int maximum)
    {
        ApplicationRequire(value.ValueKind == JsonValueKind.String);
        var text = value.GetString(); ApplicationRequire(!string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl));
        return text;
    }
    private static string ApplicationIdentifier(JsonElement value, string field)
    {
        var text = ApplicationString(value, field, 128); ApplicationRequire(ApplicationId(text)); return text;
    }
    private static string ApplicationDigest(JsonElement value, string field)
    {
        var text = ApplicationString(value, field, 64); ApplicationRequire(ApplicationHash(text)); return text;
    }
    private static long ApplicationLong(JsonElement value, string field, long minimum, long maximum)
    {
        var item = value.GetProperty(field); ApplicationRequire(item.ValueKind == JsonValueKind.Number
            && item.TryGetInt64(out var number) && number >= minimum && number <= maximum);
        return item.GetInt64();
    }
    private static uint ApplicationUInt(JsonElement value, string field)
    {
        var item = value.GetProperty(field); ApplicationRequire(item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out _)); return item.GetUInt32();
    }
    private static decimal ApplicationPercentage(JsonElement value)
    {
        ApplicationRequire(value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number is >= 0 and <= 100);
        return value.GetDecimal();
    }
    private static decimal? ApplicationNullablePercentage(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : ApplicationPercentage(value);
    private static bool ApplicationTrue(JsonElement value, string field) => value.GetProperty(field).ValueKind == JsonValueKind.True;
    private static bool ApplicationId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(static item => char.IsAsciiLetterOrDigit(item) || item is '-' or '_' or '.');
    private static bool ApplicationHash(string? value) => value is { Length: 64 } && value.All(ApplicationHex);
    private static bool ApplicationLease(string? value) => value is { Length: 32 } && value.All(ApplicationHex);
    private static bool ApplicationHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static bool ApplicationRelative(string value) => value.Length <= 256 && value[0] != '/'
        && value.All(static item => char.IsAsciiLetterOrDigit(item) || item is '-' or '_' or '.' or '/')
        && value.Split('/').All(static item => item.Length > 0 && item is not "." and not "..");
    private static string ApplicationCgroup(string lease) => "/system.slice/issue779-app-" + lease + ".service";
    private static void ApplicationRequire([DoesNotReturnIf(false)] bool condition) { if (!condition) throw ApplicationFailure("ASEVD402"); }
    private static EvidenceAdmissionException ApplicationFailure(string code) => new(code, code == "ASEVD402"
        ? "The protected application descriptor or request is invalid."
        : "Restricted application ownership or independent resource readiness was not confirmed.");
}
