using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Loads the redistributed package payload inventory used by <c>verify-packages</c>.
/// </summary>
internal sealed class PackagePayloadInventoryLoader
{
    internal const string DefaultRelativePath = "packages/third-party-payloads.yml";

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Reads the repository-owned payload inventory from its default location.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root used to resolve the inventory path.</param>
    /// <param name="cancellationToken">Cancellation token used while reading the file.</param>
    /// <returns>The parsed redistributed payload inventory.</returns>
    internal async Task<PackagePayloadInventory> LoadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var inventoryPath = Path.Combine(repositoryRoot, DefaultRelativePath);
        if (!File.Exists(inventoryPath))
        {
            throw new PackageIndexException(
                $"Package payload inventory '{DefaultRelativePath}' does not exist. Problem: verify-packages cannot prove redistributed payload coverage without the inventory. Cause: package payload provenance has not been declared. Fix: add {DefaultRelativePath} with notice or audit records for redistributed package payloads. Docs: packages/README.md#redistributed-payloads.");
        }

        var content = await File.ReadAllTextAsync(inventoryPath, cancellationToken);
        return Parse(content, DefaultRelativePath);
    }

    /// <summary>
    /// Parses payload inventory YAML content.
    /// </summary>
    /// <param name="content">YAML inventory content.</param>
    /// <param name="displayPath">Path shown in validation errors.</param>
    /// <returns>The parsed inventory.</returns>
    internal PackagePayloadInventory Parse(string content, string displayPath = DefaultRelativePath)
    {
        PackagePayloadInventory? inventory;
        try
        {
            inventory = _deserializer.Deserialize<PackagePayloadInventory>(content);
        }
        catch (YamlException ex)
        {
            throw new PackageIndexException(
                $"Package payload inventory '{displayPath}' could not be parsed. Problem: the YAML contract is invalid. Cause: {ex.Message}. Fix: repair the YAML syntax and keep schema_version: 1. Docs: packages/README.md#redistributed-payloads.");
        }

        if (inventory is null)
        {
            throw new PackageIndexException(
                $"Package payload inventory '{displayPath}' is empty. Problem: verify-packages cannot prove redistributed payload coverage. Cause: no inventory records were parsed. Fix: add schema_version: 1 plus notice or audit records. Docs: packages/README.md#redistributed-payloads.");
        }

        inventory.Validate(displayPath);
        return inventory;
    }
}

/// <summary>
/// Repository-owned contract for redistributed package payload notice, audit, and generated evidence.
/// </summary>
internal sealed class PackagePayloadInventory
{
    /// <summary>
    /// Gets the inventory schema version. Version <c>1</c> is the only supported v1 contract.
    /// </summary>
    public int SchemaVersion { get; init; }

    /// <summary>
    /// Gets third-party payload declarations that require package notices and provenance evidence.
    /// </summary>
    public List<PackagePayloadNoticeRecord> Notices { get; init; } = [];

    /// <summary>
    /// Gets narrow audit records for generated-first-party or otherwise non-notice payload evidence.
    /// </summary>
    public List<PackagePayloadAuditRecord> Audits { get; init; } = [];

    /// <summary>
    /// Validates the parsed inventory shape before package-specific evidence checks run.
    /// </summary>
    /// <param name="displayPath">Path shown in validation errors.</param>
    internal void Validate(string displayPath)
    {
        if (SchemaVersion != 1)
        {
            throw new PackageIndexException(
                $"Package payload inventory '{displayPath}' must set schema_version: 1. Problem: unsupported schema version '{SchemaVersion}'. Cause: verify-packages only supports the v1 payload inventory contract. Fix: set schema_version: 1 and use packages/README.md#redistributed-payloads as the contract. Docs: packages/README.md#redistributed-payloads.");
        }

        if (Notices.Count == 0 && Audits.Count == 0)
        {
            throw new PackageIndexException(
                $"Package payload inventory '{displayPath}' must declare at least one notice or audit record. Problem: no redistributed payload evidence exists. Cause: the inventory is empty. Fix: add notice records for third-party payloads or audit records for generated-first-party evidence. Docs: packages/README.md#redistributed-payloads.");
        }

        var recordIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var notice in Notices)
        {
            RequireNonEmpty(displayPath, "notices.id", notice.Id);
            RequireUniqueRecordId(displayPath, recordIds, notice.Id);
            RequireNonEmpty(displayPath, $"notice '{notice.Id}' package_id", notice.PackageId);
            RequireNonEmpty(displayPath, $"notice '{notice.Id}' component", notice.Component);
            RequireNonEmpty(displayPath, $"notice '{notice.Id}' version", notice.Version);
            RequireNonEmpty(displayPath, $"notice '{notice.Id}' license", notice.License);
            RequireNonEmpty(displayPath, $"notice '{notice.Id}' source_url", notice.SourceUrl);
            RequireList(displayPath, $"notice '{notice.Id}' payload_patterns", notice.PayloadPatterns);
            RequireList(displayPath, $"notice '{notice.Id}' notice_paths", notice.NoticePaths);
            RequireList(displayPath, $"notice '{notice.Id}' markers", notice.Markers);
        }

        foreach (var audit in Audits)
        {
            RequireNonEmpty(displayPath, "audits.id", audit.Id);
            RequireUniqueRecordId(displayPath, recordIds, audit.Id);
            RequireNonEmpty(displayPath, $"audit '{audit.Id}' package_id", audit.PackageId);
            RequireNonEmpty(displayPath, $"audit '{audit.Id}' evidence_kind", audit.EvidenceKind);
            RequireNonEmpty(displayPath, $"audit '{audit.Id}' reason", audit.Reason);
            RequireNonEmpty(displayPath, $"audit '{audit.Id}' reviewed_on", audit.ReviewedOn);
            RequireNonEmpty(displayPath, $"audit '{audit.Id}' source", audit.Source);
            RequireNonEmpty(displayPath, $"audit '{audit.Id}' revalidate_when", audit.RevalidateWhen);
            RequireList(displayPath, $"audit '{audit.Id}' applies_to", audit.AppliesTo);
            RequireList(displayPath, $"audit '{audit.Id}' source_paths", audit.SourcePaths);
            if (string.Equals(audit.EvidenceKind, "generated_first_party", StringComparison.OrdinalIgnoreCase))
            {
                RequireList(displayPath, $"audit '{audit.Id}' generated_paths", audit.GeneratedPaths);
            }
        }
    }

    private static void RequireNonEmpty(string displayPath, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PackageIndexException(
                $"Package payload inventory '{displayPath}' must define {name}. Problem: required payload evidence is missing. Cause: the record is incomplete. Fix: fill the required field. Docs: packages/README.md#redistributed-payloads.");
        }
    }

    private static void RequireList(string displayPath, string name, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new PackageIndexException(
                $"Package payload inventory '{displayPath}' must define non-empty {name}. Problem: required payload evidence is missing. Cause: the record has no usable values. Fix: add at least one non-empty value. Docs: packages/README.md#redistributed-payloads.");
        }
    }

    private static void RequireUniqueRecordId(string displayPath, ISet<string> recordIds, string id)
    {
        if (!recordIds.Add(id))
        {
            throw new PackageIndexException(
                $"Package payload inventory '{displayPath}' declares duplicate record id '{id}'. Problem: package payload evidence ids must be stable and unique. Cause: two records share the same id. Fix: rename one record id. Docs: packages/README.md#redistributed-payloads.");
        }
    }
}

/// <summary>
/// Third-party redistributed payload evidence that requires package notice coverage.
/// </summary>
internal sealed class PackagePayloadNoticeRecord
{
    /// <summary>Gets the stable inventory record id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the package id that must contain the declared payload.</summary>
    public string PackageId { get; init; } = string.Empty;

    /// <summary>Gets the redistributed component name shown in package reports.</summary>
    public string Component { get; init; } = string.Empty;

    /// <summary>Gets the redistributed component version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the redistributed component license expression or short name.</summary>
    public string License { get; init; } = string.Empty;

    /// <summary>Gets the upstream source or project URL for maintainer review.</summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>Gets package entry glob patterns that identify the redistributed payload.</summary>
    public List<string> PayloadPatterns { get; init; } = [];

    /// <summary>Gets package entry paths that must contain the third-party notice text.</summary>
    public List<string> NoticePaths { get; init; } = [];

    /// <summary>Gets marker strings that must appear in at least one notice file.</summary>
    public List<string> Markers { get; init; } = [];

    /// <summary>Gets optional source files that must exist in the repository for this evidence.</summary>
    public List<string> SourcePaths { get; init; } = [];

    /// <summary>Gets an optional repository file whose content must contain <see cref="VersionSourceContains"/>.</summary>
    public string? VersionSourcePath { get; init; }

    /// <summary>Gets text that must appear in <see cref="VersionSourcePath"/> when the source is deterministic.</summary>
    public string? VersionSourceContains { get; init; }
}

/// <summary>
/// Audit evidence for package payloads that are generated-first-party or otherwise not third-party notice records.
/// </summary>
internal sealed class PackagePayloadAuditRecord
{
    /// <summary>Gets the stable inventory record id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the package id covered by this audit record.</summary>
    public string PackageId { get; init; } = string.Empty;

    /// <summary>Gets package entry glob patterns or evidence labels covered by this audit.</summary>
    public List<string> AppliesTo { get; init; } = [];

    /// <summary>Gets the suspicious classifier rule or evidence class that caused this audit.</summary>
    public string? MatchedRule { get; init; }

    /// <summary>Gets the audit evidence type, such as generated_first_party.</summary>
    public string EvidenceKind { get; init; } = string.Empty;

    /// <summary>Gets repository source paths that must exist for this audit to stay valid.</summary>
    public List<string> SourcePaths { get; init; } = [];

    /// <summary>Gets generated repository paths that must exist for generated-first-party evidence.</summary>
    public List<string> GeneratedPaths { get; init; } = [];

    /// <summary>Gets the human-readable reason this record is not a third-party notice declaration.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets the date or versioned source used for the audit review.</summary>
    public string ReviewedOn { get; init; } = string.Empty;

    /// <summary>Gets the reviewer, source document, or script that established this audit.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Gets the event that must force revalidation of the audit.</summary>
    public string RevalidateWhen { get; init; } = string.Empty;
}
