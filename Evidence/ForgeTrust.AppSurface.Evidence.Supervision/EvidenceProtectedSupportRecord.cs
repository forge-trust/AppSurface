using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Closed support data read from the root-protected checkout, outside measured deployment trees.</summary>
/// <remarks>Parsing and matching are data operations only; they authenticate no provider and grant no capability.</remarks>
internal sealed class EvidenceProtectedSupportRecord
{
    public const string RecordSchema = EvidenceProtectedSupportRecordData.RecordSchema;
    public const int MaximumRecordBytes = EvidenceProtectedSupportRecordData.MaximumRecordBytes;
    public const string QualificationWorkflowPath = EvidenceProtectedSupportRecordData.QualificationWorkflowPath;
    public const string CurrentWorkflowPath = EvidenceProtectedSupportRecordData.ProtectedCurrentWorkflowPath;
    public const string ProtectedRepository = EvidenceProtectedSupportRecordData.ProtectedRepository;
    public const string ProtectedRef = EvidenceProtectedSupportRecordData.ProtectedRef;

    private EvidenceProtectedSupportRecord(EvidenceProtectedSupportRecordData data) => Data = data;
    internal EvidenceProtectedSupportRecordData Data { get; }

    /// <summary>Returns the digest of the frozen canonical bytes; this is an identifier, not an authenticity proof.</summary>
    internal string ComputeContentSha256() => EvidenceDigest.Sha256(EvidenceCanonicalJson.Serialize(Data));

    /// <summary>Parses only when exact raw bytes match a digest supplied by an independent root anchor.</summary>
    internal static EvidenceProtectedSupportRecord Parse(ReadOnlySpan<byte> utf8Json, string? expectedContentSha256)
    {
        if (utf8Json.Length is 0 or > MaximumRecordBytes || !EvidenceProtectedSupportRecordData.Hash(expectedContentSha256)
            || !string.Equals(EvidenceDigest.Sha256(utf8Json), expectedContentSha256, StringComparison.Ordinal))
            throw EvidenceProtectedSupportRecordData.InvalidRecord();
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
            EvidenceProtectedSupportRecordData.RequireObject(document.RootElement,
            ["Schema", "Provider", "Platform", "Protocol", "RepositoryName", "RepositoryId", "ProtectedBaseRevision",
             "CurrentWorkflowSha256", "CurrentWorkflowPath", "Tool", "Runtime", "RuntimeHostPath",
             "RuntimeHostSha256", "EntryAssemblyPath", "EntryAssemblySha256", "Applications", "ProfileIds", "ProducerIds",
             "CapabilityIds", "Qualification"]);
            var root = document.RootElement;
            EvidenceProtectedSupportRecordData.RequireManifest(root.GetProperty("Tool"));
            EvidenceProtectedSupportRecordData.RequireManifest(root.GetProperty("Runtime"));
            EvidenceProtectedSupportRecordData.RequireArrayObjects(root.GetProperty("Applications"), ["Id", "Version", "BuildId",
                "CatalogueSha256", "EntrySha256", "Bundle"]);
            foreach (var app in root.GetProperty("Applications").EnumerateArray())
                EvidenceProtectedSupportRecordData.RequireManifest(app.GetProperty("Bundle"));
            EvidenceProtectedSupportRecordData.RequireObject(root.GetProperty("Qualification"), ["RepositoryId", "RunId",
                "RunAttempt", "WorkflowPath", "WorkflowSha256", "WorkflowCommit", "Event", "Ref", "CompletedRunState",
                "Conclusion", "SubjectRevision", "SourceCaptureSha256", "SourceTree", "ArtifactId", "ArtifactSha256",
                "ProofReceipts"]);
            EvidenceProtectedSupportRecordData.RequireArrayObjects(
                root.GetProperty("Qualification").GetProperty("ProofReceipts"), ["Id", "Sha256"]);
            EvidenceProtectedSupportRecordData.RequireStringArray(root.GetProperty("ProfileIds"));
            EvidenceProtectedSupportRecordData.RequireStringArray(root.GetProperty("ProducerIds"));
            EvidenceProtectedSupportRecordData.RequireStringArray(root.GetProperty("CapabilityIds"));

            var data = EvidenceCanonicalJson.Deserialize<EvidenceProtectedSupportRecordData>(utf8Json, MaximumRecordBytes);
            EvidenceProtectedSupportRecordData.Validate(data);
            if (!EvidenceCanonicalJson.Serialize(data).AsSpan().SequenceEqual(utf8Json))
                throw EvidenceProtectedSupportRecordData.InvalidRecord();
            return new EvidenceProtectedSupportRecord(EvidenceProtectedSupportRecordData.Freeze(data));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException
                                     or ArgumentException or FormatException or OverflowException or NullReferenceException
                                     or InvalidDataException or NotSupportedException)
        {
            throw EvidenceProtectedSupportRecordData.InvalidRecord();
        }
    }
}

/// <summary>Internal serialization data; only the private parsed wrapper is accepted by the matcher.</summary>
internal sealed record EvidenceProtectedSupportRecordData
{
    public const string RecordSchema = "evidence-protected-support-v3";
    public const int MaximumRecordBytes = 1024 * 1024;
    public const string QualificationWorkflowPath = ".github/workflows/evidencehost-protected-qualification.yml";
    public const string ProtectedCurrentWorkflowPath = ".github/workflows/evidencehost-protected-parent.yml";
    public const string ProtectedRepository = "forge-trust/AppSurface";
    public const string ProtectedRef = "refs/heads/main";

    // This payload is a serialization DTO, not a success token; constructing it cannot create a parsed wrapper.
    public EvidenceProtectedSupportRecordData() { }

    public string Schema { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string RepositoryName { get; init; } = string.Empty;
    public long RepositoryId { get; init; }
    public string ProtectedBaseRevision { get; init; } = string.Empty;
    public string CurrentWorkflowSha256 { get; init; } = string.Empty;
    public string CurrentWorkflowPath { get; init; } = string.Empty;
    public EvidenceSupportManifest Tool { get; init; } = new(string.Empty, Array.Empty<EvidenceSupportFile>());
    public EvidenceSupportManifest Runtime { get; init; } = new(string.Empty, Array.Empty<EvidenceSupportFile>());
    public string RuntimeHostPath { get; init; } = string.Empty;
    public string RuntimeHostSha256 { get; init; } = string.Empty;
    public string EntryAssemblyPath { get; init; } = string.Empty;
    public string EntryAssemblySha256 { get; init; } = string.Empty;
    public IReadOnlyList<EvidenceSupportApplication> Applications { get; init; } = Array.Empty<EvidenceSupportApplication>();
    public IReadOnlyList<string> ProfileIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ProducerIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CapabilityIds { get; init; } = Array.Empty<string>();
    public EvidenceQualificationEvidence Qualification { get; init; } = new(
        RepositoryId: 0, RunId: 0, RunAttempt: 0,
        WorkflowPath: QualificationWorkflowPath, WorkflowSha256: string.Empty, WorkflowCommit: string.Empty,
        Event: string.Empty, Ref: string.Empty, CompletedRunState: string.Empty, Conclusion: string.Empty,
        SubjectRevision: string.Empty, SourceCaptureSha256: string.Empty, SourceTree: string.Empty,
        ArtifactId: string.Empty, ArtifactSha256: string.Empty, ProofReceipts: Array.Empty<EvidenceSupportReceipt>());

    /// <summary>Checks data shape only; it does not authenticate the record source or its evidence receipts.</summary>
    internal static void Validate(EvidenceProtectedSupportRecordData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Need(value.Schema == RecordSchema && value.Provider == "github-actions" && value.Platform == "linux-x64"
            && value.Protocol == "evidence-worker-linux-v2" && value.RepositoryName == ProtectedRepository
            && value.RepositoryId > 0 && Hash40(value.ProtectedBaseRevision)
            && Hash(value.CurrentWorkflowSha256) && value.CurrentWorkflowPath == ProtectedCurrentWorkflowPath
            && value.Tool is not null && value.Runtime is not null && value.Applications is not null
            && value.ProfileIds is not null && value.ProducerIds is not null && value.CapabilityIds is not null
            && value.Qualification is not null);
        ValidateManifest(value.Tool);
        ValidateManifest(value.Runtime);
        Need(value.Runtime.Files.Any(file => file.Path == value.RuntimeHostPath && file.Sha256 == value.RuntimeHostSha256));
        Need(value.Tool.Files.Any(file => file.Path == value.EntryAssemblyPath && file.Sha256 == value.EntryAssemblySha256));
        Need(value.Applications.Count is > 0 and <= 8 && value.Applications.All(app => app is not null));
        Unique(value.Applications.Select(app => app.Id));
        Need(value.Applications.Select(app => app.Id).SequenceEqual(value.Applications.Select(app => app.Id).Order(StringComparer.Ordinal)));
        foreach (var app in value.Applications)
        {
            Need(Identifier(app.Id) && Identifier(app.Version) && Identifier(app.BuildId)
                && Hash(app.CatalogueSha256) && Hash(app.EntrySha256) && app.Bundle is not null);
            ValidateManifest(app.Bundle);
        }
        ValidateIds(value.ProfileIds, 32);
        ValidateIds(value.ProducerIds, 32);
        ValidateIds(value.CapabilityIds, 64);
        ValidateQualification(value.Qualification, value.RepositoryId);
    }

    private static void ValidateQualification(EvidenceQualificationEvidence value, long repositoryId)
    {
        Need(value is not null && value.RepositoryId == repositoryId && value.RunId > 0 && value.RunAttempt > 0
            && value.WorkflowPath == QualificationWorkflowPath && Hash(value.WorkflowSha256) && Hash40(value.WorkflowCommit)
            && value.Event == "workflow_dispatch" && value.Ref == ProtectedRef
            && value.CompletedRunState == "completed" && value.Conclusion == "success"
            && Hash40(value.SubjectRevision) && Hash(value.SourceCaptureSha256) && Hash40(value.SourceTree)
            && Identifier(value.ArtifactId) && Hash(value.ArtifactSha256)
            && value.ProofReceipts is not null && value.ProofReceipts.Count is > 0 and <= 32
            && value.ProofReceipts.All(receipt => receipt is not null));
        Unique(value.ProofReceipts.Select(receipt => receipt.Id));
        Need(value.ProofReceipts.Select(receipt => receipt.Id)
            .SequenceEqual(value.ProofReceipts.Select(receipt => receipt.Id).Order(StringComparer.Ordinal))
            && value.ProofReceipts.All(receipt => Identifier(receipt.Id) && Hash(receipt.Sha256)));
    }

    private static void ValidateManifest(EvidenceSupportManifest manifest)
    {
        Need(manifest is not null && Hash(manifest.Sha256) && manifest.Files is not null
            && manifest.Files.Count is > 0 and <= 4096 && manifest.Files.All(file => file is not null));
        var previous = string.Empty;
        var totalBytes = 0L;
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            Need(SafeRelative(file.Path) && Encoding.UTF8.GetByteCount(file.Path) <= 4095
                && string.CompareOrdinal(previous, file.Path) < 0 && aliases.Add(file.Path)
                && file.Length is >= 0 and <= 128L * 1024 * 1024 && file.Mode is 0x124 or 0x16d && Hash(file.Sha256));
            Need(file.Length <= 512L * 1024 * 1024 - totalBytes);
            totalBytes += file.Length;
            previous = file.Path;
        }
        var names = new HashSet<string>(manifest.Files.Select(file => file.Path), StringComparer.Ordinal);
        foreach (var name in names)
        {
            for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                Need(!names.Contains(name[..slash]));
        }
        Need(manifest.Sha256 == EvidenceDigest.CanonicalSha256(manifest.Files));
    }

    internal static EvidenceProtectedSupportRecordData Freeze(EvidenceProtectedSupportRecordData value) => value with
    {
        Tool = Freeze(value.Tool), Runtime = Freeze(value.Runtime),
        Applications = Array.AsReadOnly(value.Applications.Select(app => app with { Bundle = Freeze(app.Bundle) }).ToArray()),
        ProfileIds = Array.AsReadOnly(value.ProfileIds.ToArray()), ProducerIds = Array.AsReadOnly(value.ProducerIds.ToArray()),
        CapabilityIds = Array.AsReadOnly(value.CapabilityIds.ToArray()),
        Qualification = value.Qualification with { ProofReceipts = Array.AsReadOnly(value.Qualification.ProofReceipts.ToArray()) },
    };

    private static EvidenceSupportManifest Freeze(EvidenceSupportManifest value) => value with
    { Files = Array.AsReadOnly(value.Files.ToArray()) };
    private static void ValidateIds(IReadOnlyList<string> values, int maximum)
    {
        Need(values.Count is > 0 && values.Count <= maximum);
        Unique(values);
        Need(values.SequenceEqual(values.Order(StringComparer.Ordinal)) && values.All(Identifier));
    }
    internal static void RequireManifest(JsonElement value)
    {
        RequireObject(value, ["Sha256", "Files"]);
        RequireArrayObjects(value.GetProperty("Files"), ["Path", "Mode", "Length", "Sha256"]);
    }
    internal static void RequireArrayObjects(JsonElement value, string[] names)
    {
        Need(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 4096);
        foreach (var item in value.EnumerateArray()) RequireObject(item, names);
    }
    internal static void RequireStringArray(JsonElement value)
    {
        Need(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 4096);
        foreach (var item in value.EnumerateArray()) Need(item.ValueKind == JsonValueKind.String);
    }
    internal static void RequireObject(JsonElement value, string[] names)
    {
        Need(value.ValueKind == JsonValueKind.Object);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            Need(exact.Add(property.Name) && aliases.Add(property.Name) && names.Contains(property.Name, StringComparer.Ordinal));
        Need(exact.Count == names.Length && names.All(exact.Contains));
    }
    private static void Unique(IEnumerable<string> values) => Need(values.Distinct(StringComparer.Ordinal).Count() == values.Count());
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Hash40(string? value) => value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    internal static bool SafeWorkflowPath(string? value) => value is not null
        && value.StartsWith(".github/workflows/", StringComparison.Ordinal) && SafeRelative(value);
    private static bool SafeRelative(string? value) => value is { Length: > 0 and <= 4095 }
        && !value.StartsWith('/') && !value.Contains('\\') && !value.Any(char.IsControl)
        && !value.Split('/').Any(segment => segment is "" or "." or "..");
    private static void Need([DoesNotReturnIf(false)] bool condition) { if (!condition) throw InvalidRecord(); }
    internal static FormatException InvalidRecord() => new("Protected support record is invalid.");
}

internal sealed record EvidenceSupportFile(string Path, int Mode, long Length, string Sha256);
internal sealed record EvidenceSupportManifest(string Sha256, IReadOnlyList<EvidenceSupportFile> Files);
internal sealed record EvidenceSupportApplication(string Id, string Version, string BuildId,
    string CatalogueSha256, string EntrySha256, EvidenceSupportManifest Bundle);
internal sealed record EvidenceSupportReceipt(string Id, string Sha256);

/// <summary>Prior qualification claims; values require independent provider/artifact verification before use.</summary>
internal sealed record EvidenceQualificationEvidence(long RepositoryId, long RunId, int RunAttempt,
    string WorkflowPath, string WorkflowSha256, string WorkflowCommit, string Event, string Ref,
    string CompletedRunState, string Conclusion, string SubjectRevision, string SourceCaptureSha256, string SourceTree,
    string ArtifactId, string ArtifactSha256, IReadOnlyList<EvidenceSupportReceipt> ProofReceipts);

/// <summary>Current protected-run facts supplied by a separate trusted-context resolver.</summary>
internal sealed record EvidenceProtectedSupportFacts(string Provider, string Platform, string Protocol,
    string RepositoryName, long RepositoryId, string Event, string Ref, bool RefProtected,
    string CurrentWorkflowPath, string CurrentWorkflowSha256, string BaseRevision,
    long CurrentRunId, int CurrentRunAttempt, string CurrentWorkflowCommit, string CurrentSubjectRevision,
    EvidenceSupportManifest Tool, EvidenceSupportManifest Runtime, string RuntimeHostPath, string RuntimeHostSha256,
    string EntryAssemblyPath, string EntryAssemblySha256, EvidenceSupportApplication Application,
    IReadOnlyList<string> ProfileIds, IReadOnlyList<string> ProducerIds, IReadOnlyList<string> CapabilityIds);

/// <summary>Pure equality matcher over root-supplied facts; a match is not an admission capability.</summary>
internal static class EvidenceProtectedSupportMatcher
{
    internal const int MaximumRecords = 64;

    internal static bool MatchesAny(IReadOnlyList<EvidenceProtectedSupportRecord> records, EvidenceProtectedSupportFacts facts)
    {
        if (records is null || facts is null || records.Count > MaximumRecords) return false;
        return records.All(record => record is not null) && records.Any(record => Matches(record, facts));
    }

    internal static bool Matches(EvidenceProtectedSupportRecord record, EvidenceProtectedSupportFacts facts)
    {
        if (record is null || facts is null) return false;
        try { EvidenceProtectedSupportRecordData.Validate(record.Data); }
        catch (Exception error) when (error is FormatException or NullReferenceException or OverflowException) { return false; }
        try
        {
            // Current run identity is intentionally distinct from the completed qualification run.
            var recordData = record.Data;
            return facts.Provider == "github-actions" && facts.Platform == "linux-x64"
                && facts.Protocol == "evidence-worker-linux-v2" && facts.RepositoryName == EvidenceProtectedSupportRecord.ProtectedRepository
                && facts.RepositoryId > 0 && (facts.Event is "push" or "workflow_dispatch")
                && facts.Ref == EvidenceProtectedSupportRecord.ProtectedRef && facts.RefProtected
                && facts.CurrentWorkflowPath == EvidenceProtectedSupportRecord.CurrentWorkflowPath
                && Hash(facts.CurrentWorkflowSha256)
                && facts.CurrentRunId > 0 && facts.CurrentRunAttempt > 0 && Hash40(facts.CurrentWorkflowCommit)
                && Hash40(facts.CurrentSubjectRevision) && Hash40(facts.BaseRevision)
                && recordData.Provider == facts.Provider && recordData.Platform == facts.Platform && recordData.Protocol == facts.Protocol
                && recordData.RepositoryName == facts.RepositoryName && recordData.RepositoryId == facts.RepositoryId
                && recordData.CurrentWorkflowSha256 == facts.CurrentWorkflowSha256
                && recordData.CurrentWorkflowPath == facts.CurrentWorkflowPath && recordData.ProtectedBaseRevision == facts.BaseRevision
                && Same(recordData.Tool, facts.Tool) && Same(recordData.Runtime, facts.Runtime)
                && recordData.RuntimeHostPath == facts.RuntimeHostPath && recordData.RuntimeHostSha256 == facts.RuntimeHostSha256
                && recordData.EntryAssemblyPath == facts.EntryAssemblyPath && recordData.EntryAssemblySha256 == facts.EntryAssemblySha256
                && recordData.Applications.Any(app => Same(app, facts.Application))
                && SameSet(recordData.ProfileIds, facts.ProfileIds) && SameSet(recordData.ProducerIds, facts.ProducerIds)
                && SameSet(recordData.CapabilityIds, facts.CapabilityIds);
        }
        catch (Exception error) when (error is NullReferenceException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    private static bool Same(EvidenceSupportManifest left, EvidenceSupportManifest right) => left.Sha256 == right.Sha256
        && left.Files.SequenceEqual(right.Files);
    private static bool Same(EvidenceSupportApplication left, EvidenceSupportApplication right) =>
        left.Id == right.Id && left.Version == right.Version && left.BuildId == right.BuildId
        && left.CatalogueSha256 == right.CatalogueSha256 && left.EntrySha256 == right.EntrySha256
        && Same(left.Bundle, right.Bundle);
    private static bool SameSet(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Hash40(string? value) => value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
