using System.Text.Json;
using ForgeTrust.AppSurface.Docs;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Parses alias declarations while retaining the provenance needed for safe operator and archive projections.
/// </summary>
internal sealed class AppSurfaceDocsVersionAliasCatalogResolver
{
    private const string DocumentationReferenceRoot = "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#asdocsalias";
    private readonly DocsUrlBuilder _urls;
    private readonly ILogger _logger;
    private readonly string _instanceName;

    /// <summary>Creates a resolver for one Docs route family.</summary>
    /// <param name="urls">The route builder associated with the catalog instance.</param>
    /// <param name="logger">The service logger used for bounded structured diagnostics.</param>
    /// <param name="instanceName">Validated, bounded Docs product identity; Default for legacy module hosts.</param>
    internal AppSurfaceDocsVersionAliasCatalogResolver(DocsUrlBuilder urls, ILogger logger, string instanceName = "Default")
    {
        _urls = urls;
        _logger = logger;
        _instanceName = AppSurfaceDocsInstanceDeclaration.NormalizeName(instanceName);
    }

    /// <summary>Reads whether the alias namespace was declared and retains validatable alias metadata.</summary>
    /// <param name="root">The already-validated catalog JSON root.</param>
    /// <returns>The declaration snapshot; it contains no retained JSON document state.</returns>
    internal AliasDeclaration Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(root, "aliases", out var aliases)
            || aliases.ValueKind == JsonValueKind.Null)
        {
            return new AliasDeclaration(false, []);
        }

        if (aliases.ValueKind != JsonValueKind.Array)
        {
            Log("ASDOCSALIAS001", "collection-shape", "Set aliases to an array of alias objects or null.", null, []);
            return new AliasDeclaration(true, []);
        }

        var elements = aliases.EnumerateArray();
        var declarations = new List<AliasDraft>();
        var index = 0;
        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Log("ASDOCSALIAS002", "item-shape", "Replace this item with an alias object.", null, [index]);
                declarations.Add(AliasDraft.InvalidItem(index));
                index++;
                continue;
            }

            declarations.Add(ParseItem(element, index));
            index++;
        }

        return new AliasDeclaration(declarations.Count > 0, declarations);
    }

    /// <summary>Resolves parsed aliases by exact version identifier and creates immutable read models.</summary>
    /// <param name="declaration">The parsed declaration snapshot.</param>
    /// <param name="versions">The resolved exact versions in authored order.</param>
    /// <param name="targetResolutionAvailable">Whether trusted-root resolution succeeded.</param>
    /// <returns>Resolved aliases in authored order, retaining conflicting entries for operator inspection.</returns>
    internal IReadOnlyList<AppSurfaceDocsResolvedVersionAlias> Resolve(
        AliasDeclaration declaration,
        IReadOnlyList<AppSurfaceDocsResolvedVersion> versions,
        bool targetResolutionAvailable = true)
    {
        if (!declaration.IsActive || declaration.Items.Count == 0)
        {
            return [];
        }

        var versionByName = new Dictionary<string, AppSurfaceDocsResolvedVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            versionByName.TryAdd(version.Version.Trim(), version);
        }

        var nameGroups = new Dictionary<string, List<AliasDraft>>(StringComparer.Ordinal);
        foreach (var item in declaration.Items)
        {
            if (item.Name is not null)
            {
                if (!nameGroups.TryGetValue(item.Name, out var group))
                {
                    group = [];
                    nameGroups.Add(item.Name, group);
                }

                group.Add(item);
            }
        }

        var conflictedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in nameGroups)
        {
            if (pair.Value.Count < 2)
            {
                continue;
            }

            conflictedNames.Add(pair.Key);
            Log(
                "ASDOCSALIAS005",
                "duplicate-name",
                "Rename aliases so each normalized name appears once.",
                pair.Key,
                pair.Value.Select(item => item.Index).ToArray());
        }

        var result = new List<AppSurfaceDocsResolvedVersionAlias>(declaration.Items.Count);
        foreach (var item in declaration.Items)
        {
            var isConflict = item.Name is not null && conflictedNames.Contains(item.Name);
            var code = isConflict ? "ASDOCSALIAS005" : item.DiagnosticCode;
            AppSurfaceDocsResolvedVersion? target = null;
            if (!isConflict && item.IsDefinitionValid && item.Visibility == AppSurfaceDocsVersionVisibility.Public)
            {
                if (!targetResolutionAvailable)
                {
                    code = "ASDOCSALIAS008";
                    Log(code, "target-resolution-unavailable", "Restore the trusted release root and publish verified exact releases.", item.Name, [item.Index]);
                }
                else if (item.ConfiguredVersion is not null && !versionByName.TryGetValue(item.ConfiguredVersion, out target))
                {
                    code = "ASDOCSALIAS006";
                    Log(code, "unknown-target", "Set version to an exact identifier in the versions collection.", item.Name, [item.Index]);
                }
                else if (target is not null && target.Visibility != AppSurfaceDocsVersionVisibility.Public)
                {
                    code = "ASDOCSALIAS007";
                    target = null;
                    Log(code, "hidden-target", "Select a public exact version or keep this alias hidden.", item.Name, [item.Index]);
                }
                else if (target is null || !target.IsAvailable
                    || target.ArchiveVerificationState != AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified
                    || target.VerifiedReleaseArchive is null)
                {
                    code = "ASDOCSALIAS008";
                    Log(code, "unavailable-target", "Publish and verify the exact target before exposing this alias.", item.Name, [item.Index]);
                }
            }

            var available = !isConflict
                && item.IsDefinitionValid
                && item.Visibility == AppSurfaceDocsVersionVisibility.Public
                && target is
                {
                    IsAvailable: true,
                    ArchiveVerificationState: AppSurfaceDocsReleaseArchiveVerificationState.AvailableVerified,
                    VerifiedReleaseArchive: not null
                };
            var issue = code switch
            {
                "ASDOCSALIAS002" or "ASDOCSALIAS003" or "ASDOCSALIAS004" => "This release entry is not configured correctly.",
                "ASDOCSALIAS005" => "This alias name is configured more than once.",
                "ASDOCSALIAS006" or "ASDOCSALIAS007" => "The selected release is not available.",
                "ASDOCSALIAS008" => "The selected release is currently unavailable.",
                _ => null
            };

            result.Add(new AppSurfaceDocsResolvedVersionAlias
            {
                Name = item.Name,
                Label = item.Label,
                Summary = item.Summary,
                Visibility = item.Visibility,
                ConfiguredVersion = item.ConfiguredVersion,
                TargetVersion = target,
                RootUrl = item.Name is null ? null : _urls.BuildAliasRootUrl(item.Name),
                IsAvailable = available,
                IsDefinitionValid = item.IsDefinitionValid,
                HasExplicitPublicVisibility = item.HasExplicitPublicVisibility,
                IsNameConflict = isConflict,
                DiagnosticCode = code,
                AvailabilityIssue = issue
            });
        }

        return result;
    }

    private AliasDraft ParseItem(JsonElement element, int index)
    {
        var hasNameProperty = TryGetPropertyIgnoreCase(element, "name", out var nameElement);
        var nameIsString = hasNameProperty && nameElement.ValueKind == JsonValueKind.String;
        var normalizedName = string.Empty;
        var nameIsValid = nameIsString && AppSurfaceDocsVersionAliasName.TryNormalize(nameElement.GetString(), out normalizedName);
        var name = nameIsValid ? normalizedName : null;

        var hasVersion = TryReadOptionalString(element, "version", out var version, out var versionValid);
        var hasLabel = TryReadOptionalString(element, "label", out var label, out var labelValid);
        var hasSummary = TryReadOptionalString(element, "summary", out var summary, out var summaryValid);
        var visibilityValid = TryReadVisibility(element, out var visibility, out var explicitPublic);

        // A missing name is a name error; a non-object was already classified as an item-shape error.
        var diagnostic = !nameIsValid
            ? "ASDOCSALIAS003"
            : !hasVersion || !versionValid || string.IsNullOrWhiteSpace(version)
                || !labelValid || !summaryValid || !visibilityValid
                    ? "ASDOCSALIAS004"
                    : null;
        if (diagnostic is not null)
        {
            var reason = diagnostic == "ASDOCSALIAS003" ? "invalid-name" : "invalid-metadata";
            var action = diagnostic == "ASDOCSALIAS003"
                ? "Use a 1-64 character ASCII alias name with alphanumeric edges."
                : "Provide a non-empty exact version and string-or-null label/summary/visibility metadata.";
            Log(diagnostic, reason, action, name, [index]);
        }

        return new AliasDraft(
            index,
            name,
            hasVersion && versionValid && !string.IsNullOrWhiteSpace(version) ? version!.Trim() : null,
            hasLabel && labelValid && !string.IsNullOrWhiteSpace(label) ? label!.Trim() : name,
            hasSummary && summaryValid && !string.IsNullOrWhiteSpace(summary) ? summary!.Trim() : null,
            visibility,
            explicitPublic,
            diagnostic is null,
            diagnostic);
    }

    private void Log(string code, string reason, string action, string? name, IReadOnlyList<int> indices)
    {
        var documentationReference = DocumentationReferenceRoot + code[^3..].ToLowerInvariant();
        _logger.LogWarning(
            "AppSurface Docs alias configuration diagnostic {AliasDiagnosticCode}: {AliasReason}. {AliasAction} See {DocumentationReference}. InstanceName={InstanceName}; AliasName={AliasName}; EntryIndices={EntryIndices}; GroupCount={GroupCount}.",
            code,
            reason,
            action,
            documentationReference,
            _instanceName,
            name,
            string.Join(',', indices),
            indices.Count);
    }

    private static bool TryReadOptionalString(JsonElement parent, string propertyName, out string? value, out bool valid)
    {
        value = null;
        valid = true;
        var found = TryGetPropertyIgnoreCase(parent, propertyName, out var element);
        if (!found || element.ValueKind == JsonValueKind.Null)
        {
            return found;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            valid = false;
            return true;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadVisibility(
        JsonElement parent,
        out AppSurfaceDocsVersionVisibility? visibility,
        out bool explicitPublic)
    {
        visibility = AppSurfaceDocsVersionVisibility.Public;
        explicitPublic = false;
        if (!TryGetPropertyIgnoreCase(parent, "visibility", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        AppSurfaceDocsVersionVisibility parsed;
        if (element.ValueKind == JsonValueKind.String)
        {
            if (!Enum.TryParse(element.GetString(), true, out parsed) || !Enum.IsDefined(parsed))
            {
                visibility = null;
                return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Number
                 && element.TryGetInt32(out var numericValue)
                 && Enum.IsDefined(typeof(AppSurfaceDocsVersionVisibility), numericValue))
        {
            parsed = (AppSurfaceDocsVersionVisibility)numericValue;
        }
        else
        {
            visibility = null;
            return false;
        }

        visibility = parsed;
        explicitPublic = parsed == AppSurfaceDocsVersionVisibility.Public;
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement parent, string propertyName, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>Snapshot of declaration activity and parsed entries.</summary>
    /// <param name="IsActive">Whether a nonempty or malformed non-null declaration owns the namespace.</param>
    /// <param name="Items">Authored alias entries in source order.</param>
    internal sealed record AliasDeclaration(bool IsActive, IReadOnlyList<AliasDraft> Items);

    /// <summary>One parsed alias with independent name, metadata, and explicit-visibility provenance.</summary>
    /// <param name="Index">The authored array index.</param>
    /// <param name="Name">The normalized name only when it passes the shared validator.</param>
    /// <param name="ConfiguredVersion">The trimmed exact version identifier when it is a nonempty string.</param>
    /// <param name="Label">The trimmed label or the normalized name when blank.</param>
    /// <param name="Summary">The trimmed summary, or <see langword="null"/> when absent or blank.</param>
    /// <param name="Visibility">The parsed visibility, defaulting to public for omitted or null values.</param>
    /// <param name="HasExplicitPublicVisibility">Whether a valid explicit Public value was authored.</param>
    /// <param name="IsDefinitionValid">Whether name and all metadata satisfy the alias descriptor contract.</param>
    /// <param name="DiagnosticCode">The structured diagnostic code for an invalid definition, if any.</param>
    internal sealed record AliasDraft(
        int Index,
        string? Name,
        string? ConfiguredVersion,
        string? Label,
        string? Summary,
        AppSurfaceDocsVersionVisibility? Visibility,
        bool HasExplicitPublicVisibility,
        bool IsDefinitionValid,
        string? DiagnosticCode)
    {
        internal static AliasDraft InvalidItem(int index)
        {
            return new AliasDraft(index, null, null, null, null, null, false, false, "ASDOCSALIAS002");
        }
    }
}

/// <summary>
/// Resolved alias state for host routing and operator diagnostics. This is a non-positional record so future additive
/// fields do not change constructor or deconstruction contracts.
/// </summary>
/// <remarks>
/// <see cref="ConfiguredVersion"/> is operator-side configuration and may identify a hidden release. Public archive
/// consumers must use a deliberate safe projection instead of serializing this record. <see cref="TargetVersion"/>
/// is populated only for a known public target; it is the same resolved-version object used by exact-version mounts.
/// </remarks>
public sealed record AppSurfaceDocsResolvedVersionAlias
{
    /// <summary>Gets the normalized validated name, or <see langword="null"/> when the authored name is invalid.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the validated reader-facing label, or <see langword="null"/> when unavailable.</summary>
    public string? Label { get; init; }

    /// <summary>Gets optional validated reader-facing summary text.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the valid configured visibility, or <see langword="null"/> when invalid.</summary>
    public AppSurfaceDocsVersionVisibility? Visibility { get; init; }

    /// <summary>Gets the configured exact target identifier for operator inspection only.</summary>
    public string? ConfiguredVersion { get; init; }

    /// <summary>Gets the existing resolved public exact-version target, or <see langword="null"/> when not public or unresolved.</summary>
    public AppSurfaceDocsResolvedVersion? TargetVersion { get; init; }

    /// <summary>Gets the alias root URL when the authored name is safe.</summary>
    public string? RootUrl { get; init; }

    /// <summary>Gets whether the alias can be mounted on its verified public target.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Gets whether the authored alias passed shape, name, and metadata validation.</summary>
    public bool IsDefinitionValid { get; init; }

    /// <summary>Gets whether a valid explicit Public visibility value was authored.</summary>
    public bool HasExplicitPublicVisibility { get; init; }

    /// <summary>Gets whether another entry has the same normalized name.</summary>
    public bool IsNameConflict { get; init; }

    /// <summary>Gets the stable ASDOCSALIAS diagnostic code, or <see langword="null"/>.</summary>
    public string? DiagnosticCode { get; init; }

    /// <summary>Gets a sanitized availability explanation suitable for deliberate public projection.</summary>
    public string? AvailabilityIssue { get; init; }
}
