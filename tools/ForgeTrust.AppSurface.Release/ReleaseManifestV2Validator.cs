using System.Text.Json;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Dispatches and validates the schema-v2 release-manifest contract before evidence consumes it.
/// </summary>
/// <remarks>
/// This deliberately performs raw JSON validation before typed deserialization. System.Text.Json otherwise ignores unknown fields,
/// which would make an accidental V1/V2 hybrid look valid even though the checked-in JSON schemas forbid it.
/// </remarks>
internal static class ReleaseManifestV2Validator
{
    internal const string Schema = "appsurface-release-manifest-v2";

    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "schema",
        "version",
        "tag",
        "date",
        "preparationBaseCommit",
        "releaseClassification",
        "generatedFiles",
        "publishedPackageProjects",
        "coordinatedPackageReleaseNoteResolutions",
        "diagnostics",
        "warningIds"
    };

    internal static bool TryDeserialize(string json, out ReleaseManifestV2? manifest, out string issue)
    {
        manifest = null;
        issue = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issue = "Release manifest JSON must be an object.";
                return false;
            }

            var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (!propertyNames.All(RequiredProperties.Contains)
                || RequiredProperties.Except(propertyNames, StringComparer.Ordinal).Any())
            {
                issue = "Release manifest has missing, unknown, or V1-only properties.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.String
                || !string.Equals(schema.GetString(), Schema, StringComparison.Ordinal))
            {
                issue = $"Release manifest schema must be '{Schema}'.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<ReleaseManifestV2>(json, ReleaseJson.Options);
            if (manifest is null
                || string.IsNullOrWhiteSpace(manifest.Version)
                || string.IsNullOrWhiteSpace(manifest.Tag)
                || string.IsNullOrWhiteSpace(manifest.PreparationBaseCommit)
                || manifest.GeneratedFiles is null
                || manifest.PublishedPackageProjects is null
                || manifest.CoordinatedPackageReleaseNoteResolutions is null
                || manifest.Diagnostics is null
                || manifest.WarningIds is null)
            {
                issue = "Release manifest has missing required V2 values.";
                manifest = null;
                return false;
            }

            var parsed = manifest;
            var projects = parsed.PublishedPackageProjects.ToArray();
            var resolutionProjects = parsed.CoordinatedPackageReleaseNoteResolutions.Select(item => item.Project).ToArray();
            if (!projects.SequenceEqual(projects.OrderBy(project => project, StringComparer.Ordinal), StringComparer.Ordinal)
                || !resolutionProjects.SequenceEqual(resolutionProjects.OrderBy(project => project, StringComparer.Ordinal), StringComparer.Ordinal)
                || resolutionProjects.Distinct(StringComparer.Ordinal).Count() != resolutionProjects.Length
                || parsed.CoordinatedPackageReleaseNoteResolutions.Any(item =>
                    !string.Equals(item.Source, "coordinated", StringComparison.Ordinal)
                    || !string.Equals(item.AliasPath, "releases/current.md", StringComparison.Ordinal)
                    || !string.Equals(item.ResolvedPath, $"releases/v{parsed.Version}.md", StringComparison.Ordinal)
                    || !string.Equals(item.ReleaseTag, parsed.Tag, StringComparison.Ordinal)
                    || !string.Equals(item.PreparationBaseCommit, parsed.PreparationBaseCommit, StringComparison.Ordinal)))
            {
                issue = "Release manifest V2 package resolutions are invalid or not ordinally sorted.";
                manifest = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            issue = ex.Message;
            return false;
        }
    }
}
