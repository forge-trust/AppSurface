using System.Collections.Immutable;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Canonical schema-1 JSON serialization for recipes and trust policies.</summary>
public static class KeycloakCanonicalJson
{
    /// <summary>Serializes every recipe field, including every nested operation and material reference.</summary>
    public static byte[] Write(KeycloakSecurityRecipe recipe)
    {
        KeycloakRecipeValidation.Validate(recipe);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", recipe.SchemaVersion);
            writer.WritePropertyName("sourceImage");
            WriteImage(writer, recipe.SourceImage);
            writer.WritePropertyName("runtimeFrontendOciIdentities");
            writer.WriteStartArray();
            foreach (var item in recipe.RuntimeFrontendOciIdentities)
                WriteOci(writer, item);
            writer.WriteEndArray();
            writer.WritePropertyName("operations");
            writer.WriteStartArray();
            foreach (var item in recipe.Operations)
                WriteOperation(writer, item);
            writer.WriteEndArray();
            writer.WritePropertyName("templateBaseline");
            WriteBaseline(writer, recipe.TemplateBaseline);
            writer.WriteString("mode", recipe.Mode.ToString());
            writer.WriteString("keycloakVersion", recipe.KeycloakVersion);
            writer.WriteString("entrypointPath", recipe.EntrypointPath);
            writer.WritePropertyName("themeInputs");
            writer.WriteStartArray();
            foreach (var item in recipe.ThemeInputs)
                WriteThemeInput(writer, item);
            writer.WriteEndArray();
            WriteStrings(writer, "capturedHostInputs", recipe.CapturedHostInputs);
            WriteStrings(writer, "sourceProfiles", recipe.SourceProfiles);
            writer.WritePropertyName("materialReferences");
            writer.WriteStartArray();
            foreach (var item in recipe.MaterialReferences)
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("exactHttpsUri", item.ExactHttpsUri);
                writer.WriteString("sha256", item.Sha256);
                writer.WriteNumber("maxBytes", item.MaxBytes);
                writer.WriteBoolean("capturedHostInput", item.CapturedHostInput);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("resourceLimits");
            writer.WriteStartObject();
            writer.WriteNumber("maxMaterialBytes", recipe.ResourceLimits.MaxMaterialBytes);
            writer.WriteNumber("maxArchiveEntries", recipe.ResourceLimits.MaxArchiveEntries);
            writer.WriteNumber("maxArchiveExpandedBytes", recipe.ResourceLimits.MaxArchiveExpandedBytes);
            writer.WriteNumber("maxOutputBytes", recipe.ResourceLimits.MaxOutputBytes);
            writer.WriteNumber("maxOperationSeconds", recipe.ResourceLimits.MaxOperationSeconds);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Serializes the separately owned material trust policy.</summary>
    public static byte[] Write(KeycloakMaterialTrustPolicy policy)
    {
        KeycloakRecipeValidation.Validate(policy);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", policy.Version);
            writer.WriteString("ownerApprovalReference", policy.OwnerApprovalReference);
            WriteStrings(writer, "permittedHttpsOrigins", policy.PermittedHttpsOrigins);
            WriteStrings(writer, "permittedPathMappings", policy.PermittedPathMappings);
            WriteStrings(writer, "permittedCoordinateMappings", policy.PermittedCoordinateMappings);
            writer.WritePropertyName("materialSha256ByIdentity");
            writer.WriteStartObject();
            foreach (var item in policy.MaterialSha256ByIdentity.OrderBy(x => x.Key, StringComparer.Ordinal))
                writer.WriteString(item.Key, item.Value);
            writer.WriteEndObject();
            WriteStrings(writer, "signingFingerprints", policy.SigningFingerprints);
            WriteStrings(writer, "signingAlgorithms", policy.SigningAlgorithms);
            WriteStrings(writer, "withdrawnKeys", policy.WithdrawnKeys);
            WriteStrings(writer, "historicalExactReleaseBytes", policy.HistoricalExactReleaseBytes);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Strictly deserializes a complete recipe and all nested operations.</summary>
    public static KeycloakSecurityRecipe ReadRecipe(ReadOnlySpan<byte> utf8Json)
    {
        RequireBoundedJson(utf8Json);
        try
        {
            return ReadRecipeCore(utf8Json);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new JsonException("Recipe JSON does not match the complete supported schema.");
        }
    }

    private static KeycloakSecurityRecipe ReadRecipeCore(ReadOnlySpan<byte> utf8Json)
    {
        using var document = Parse(utf8Json);
        var root = document.RootElement;
        CheckObject(root, "schemaVersion", "sourceImage", "runtimeFrontendOciIdentities", "operations", "templateBaseline", "mode", "keycloakVersion", "entrypointPath", "themeInputs", "capturedHostInputs", "sourceProfiles", "materialReferences", "resourceLimits");
        var image = root.GetProperty("sourceImage");
        CheckObject(image, "index", "platformManifest", "platform", "indexMaterialId", "archiveMaterialId", "configDigest", "diffIds");
        var oci = ReadOci(root.GetProperty("runtimeFrontendOciIdentities"));
        var operations = ReadOperations(root.GetProperty("operations"));
        var themes = ReadThemes(root.GetProperty("themeInputs"));
        var limits = root.GetProperty("resourceLimits");
        CheckObject(limits, "maxMaterialBytes", "maxArchiveEntries", "maxArchiveExpandedBytes", "maxOutputBytes", "maxOperationSeconds");
        var recipe = KeycloakSecurityRecipe.Create(root.GetProperty("schemaVersion").GetInt32(), new KeycloakImageIdentity(image.GetString("index"), image.GetString("platformManifest"), image.GetString("platform")) { IndexMaterialId = image.GetString("indexMaterialId"), ArchiveMaterialId = image.GetString("archiveMaterialId"), ConfigDigest = image.GetString("configDigest"), DiffIds = ReadStringArray(image.GetProperty("diffIds"), "diffIds") }, oci, operations, ParseEnum<KeycloakRecipeMode>(root.GetString("mode"), "mode"), themes, ReadStringArray(root.GetProperty("capturedHostInputs"), "capturedHostInputs")) with
        {
            TemplateBaseline = ReadBaseline(root.GetProperty("templateBaseline")),
            KeycloakVersion = root.GetString("keycloakVersion"),
            EntrypointPath = root.GetString("entrypointPath"),
            SourceProfiles = ReadStringArray(root.GetProperty("sourceProfiles"), "sourceProfiles"),
            MaterialReferences = ReadMaterials(root.GetProperty("materialReferences")),
            ResourceLimits = new(limits.GetProperty("maxMaterialBytes").GetInt64(), limits.GetProperty("maxArchiveEntries").GetInt64(), limits.GetProperty("maxArchiveExpandedBytes").GetInt64(), limits.GetProperty("maxOutputBytes").GetInt64(), limits.GetProperty("maxOperationSeconds").GetInt32())
        };
        return KeycloakRecipeValidation.Validate(recipe);
    }

    /// <summary>Strictly deserializes every trust-policy field.</summary>
    public static KeycloakMaterialTrustPolicy ReadPolicy(ReadOnlySpan<byte> utf8Json)
    {
        RequireBoundedJson(utf8Json);
        try
        {
            return ReadPolicyCore(utf8Json);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new JsonException("Policy JSON does not match the complete supported schema.");
        }
    }

    private static KeycloakMaterialTrustPolicy ReadPolicyCore(ReadOnlySpan<byte> utf8Json)
    {
        using var document = Parse(utf8Json);
        var root = document.RootElement;
        CheckObject(root, "version", "ownerApprovalReference", "permittedHttpsOrigins", "permittedPathMappings", "permittedCoordinateMappings", "materialSha256ByIdentity", "signingFingerprints", "signingAlgorithms", "withdrawnKeys", "historicalExactReleaseBytes");
        var mappings = root.GetProperty("materialSha256ByIdentity");
        RequireKind(mappings, JsonValueKind.Object, "materialSha256ByIdentity");
        var values = new List<KeyValuePair<string, string>>();
        foreach (var item in mappings.EnumerateObject())
            values.Add(new(item.Name, item.Value.GetStringValue()));
        return KeycloakRecipeValidation.Validate(KeycloakMaterialTrustPolicy.Create(root.GetString("version"), root.GetString("ownerApprovalReference"), ReadStringArray(root.GetProperty("permittedHttpsOrigins"), "permittedHttpsOrigins"), ReadStringArray(root.GetProperty("permittedPathMappings"), "permittedPathMappings"), ReadStringArray(root.GetProperty("permittedCoordinateMappings"), "permittedCoordinateMappings"), values, ReadStringArray(root.GetProperty("signingFingerprints"), "signingFingerprints"), ReadStringArray(root.GetProperty("signingAlgorithms"), "signingAlgorithms"), ReadStringArray(root.GetProperty("withdrawnKeys"), "withdrawnKeys"), ReadStringArray(root.GetProperty("historicalExactReleaseBytes"), "historicalExactReleaseBytes")));
    }

    /// <summary>Compatibility name for the strict recipe reader.</summary>
    public static KeycloakSecurityRecipe Read(ReadOnlySpan<byte> utf8Json) => ReadRecipe(utf8Json);
    private static KeycloakTemplateBaseline? ReadBaseline(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        CheckObject(value, "archivePath", "archiveSha256", "entry", "sha256", "materialId");
        return new(value.GetString("archivePath"), value.GetString("archiveSha256"), value.GetString("entry"), value.GetString("sha256"), value.GetString("materialId"));
    }

    private static void WriteBaseline(Utf8JsonWriter writer, KeycloakTemplateBaseline? baseline)
    {
        if (baseline is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("archivePath", baseline.ArchivePath);
        writer.WriteString("archiveSha256", baseline.ArchiveSha256);
        writer.WriteString("entry", baseline.Entry);
        writer.WriteString("sha256", baseline.Sha256);
        writer.WriteString("materialId", baseline.MaterialId);
        writer.WriteEndObject();
    }

    private static ImmutableArray<OciIdentity> ReadOci(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Array, "runtimeFrontendOciIdentities");
        var result = new List<OciIdentity>();
        foreach (var item in value.EnumerateArray())
        {
            CheckObject(item, "id", "platform", "reference", "digest", "archiveMaterialId", "platformManifestDigest", "configDigest", "indexMaterialId", "diffIds");
            result.Add(new OciIdentity(item.GetString("reference"), item.GetString("digest")) { Id = item.GetString("id"), Platform = item.GetString("platform"), ArchiveMaterialId = item.GetString("archiveMaterialId"), PlatformManifestDigest = item.GetString("platformManifestDigest"), ConfigDigest = item.GetString("configDigest"), IndexMaterialId = item.GetString("indexMaterialId"), DiffIds = ReadStringArray(item.GetProperty("diffIds"), "diffIds") });
        }

        return result.ToImmutableArray();
    }

    private static List<KeycloakOperation> ReadOperations(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Array, "operations");
        var result = new List<KeycloakOperation>();
        foreach (var item in value.EnumerateArray())
        {
            CheckObject(item, true, "id", "kind", "group", "profile", "replacement");
            var id = item.GetString("id");
            result.Add(item.GetString("kind") switch
            {
                "ArtifactReplacementGroup" => new KeycloakOperation.ArtifactReplacementGroupOperation(ReadGroup(item.GetProperty("group"), id)),
                "RuntimeProfile" => new KeycloakOperation.RuntimeProfileOperation(ReadProfile(item.GetProperty("profile"), id)),
                "EmbeddedArchiveReplacement" => new KeycloakOperation.EmbeddedArchiveReplacementOperation(ReadArchive(item.GetProperty("replacement"), id)),
                _ => throw new JsonException("Unknown operation kind.")
            });
        }

        return result;
    }

    private static List<KeycloakThemeInput> ReadThemes(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Array, "themeInputs");
        var result = new List<KeycloakThemeInput>();
        foreach (var item in value.EnumerateArray())
        {
            CheckObject(item, true, "id", "sha256", "localInputId", "destinationPath", "exactUri", "capturedHostInput");
            result.Add(new KeycloakThemeInput(item.GetString("id"), item.GetString("sha256"), item.GetNullableString("exactUri"), item.GetNullableString("capturedHostInput")) { LocalInputId = item.GetNullableString("localInputId"), DestinationPath = item.GetNullableString("destinationPath") });
        }

        return result;
    }

    private static ImmutableArray<KeycloakMaterialReference> ReadMaterials(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Array, "materialReferences");
        return value.EnumerateArray().Select(item =>
        {
            CheckObject(item, "id", "exactHttpsUri", "sha256", "maxBytes", "capturedHostInput");
            return new KeycloakMaterialReference(item.GetString("id"), item.GetString("exactHttpsUri"), item.GetString("sha256"), item.GetProperty("maxBytes").GetInt64())
            {
                CapturedHostInput = item.GetProperty("capturedHostInput").GetBoolean()
            };
        }).ToImmutableArray();
    }

    private static ArtifactReplacementGroup ReadGroup(JsonElement value, string id)
    {
        CheckObject(value, "id", "replacements", "requiredMembers");
        RequireText(value.GetString("id"), id, "group.id");
        var replacements = new List<ArtifactReplacement>();
        var items = value.GetProperty("replacements");
        RequireKind(items, JsonValueKind.Array, "replacements");
        foreach (var item in items.EnumerateArray())
        {
            CheckObject(item, "original", "replacement");
            replacements.Add(new(ReadArtifact(item.GetProperty("original")), ReadArtifact(item.GetProperty("replacement"))));
        }

        return ArtifactReplacementGroup.Create(id, replacements, ReadCoordinates(value.GetProperty("requiredMembers"), "requiredMembers"));
    }

    private static RuntimeProfile ReadProfile(JsonElement value, string id)
    {
        CheckObject(value, "id", "version", "templateName", "supportedSourceProfiles", "runtimeOciIdentities", "repositorySnapshots", "requiredPackages", "runtimeRemovedPackages", "expectedObservations");
        RequireText(value.GetString("id"), id, "profile.id");
        var profile = RuntimeProfile.Create(id, value.GetString("version"), value.GetString("templateName"), ReadStringArray(value.GetProperty("supportedSourceProfiles"), "supportedSourceProfiles"));
        profile = profile with
        {
            RuntimeOciIdentities = ReadOci(value.GetProperty("runtimeOciIdentities")),
            RepositorySnapshots = ReadSnapshots(value.GetProperty("repositorySnapshots")),
            RequiredPackages = ReadPackages(value.GetProperty("requiredPackages")),
            RemovedPackages = ReadRemovedPackages(value.GetProperty("runtimeRemovedPackages")),
            ExpectedObservations = ReadStringArray(value.GetProperty("expectedObservations"), "expectedObservations")
        };
        return profile;
    }

    private static EmbeddedArchiveReplacement ReadArchive(JsonElement value, string id)
    {
        CheckObject(value, "id", "version", "templateName", "source", "replacement", "classPrefixes", "versionedClasses", "servicePaths", "moduleSet");
        RequireText(value.GetString("id"), id, "replacement.id");
        var module = value.GetProperty("moduleSet");
        CheckObject(module, "sourceArtifact", "replacementArtifact", "destination", "sourceModuleSha256ByIdentity", "replacementModuleSha256ByIdentity");
        return new EmbeddedArchiveReplacement(id, value.GetString("version"), value.GetString("templateName"), ReadCoordinate(value.GetProperty("source")), ReadCoordinate(value.GetProperty("replacement")), ReadStringArray(value.GetProperty("classPrefixes"), "classPrefixes"), ReadStringArray(value.GetProperty("versionedClasses"), "versionedClasses"), ReadStringArray(value.GetProperty("servicePaths"), "servicePaths"))
        {
            ModuleSet = new EmbeddedArchiveModuleSet(ReadArtifact(module.GetProperty("sourceArtifact")), ReadArtifact(module.GetProperty("replacementArtifact")), module.GetString("destination"), ReadMap(module.GetProperty("sourceModuleSha256ByIdentity")), ReadMap(module.GetProperty("replacementModuleSha256ByIdentity")))
        };
    }

    private static ArtifactReference ReadArtifact(JsonElement value)
    {
        CheckObject(value, true, "coordinate", "sha256", "path", "destination");
        var path = value.GetNullableString("path");
        if (!string.IsNullOrEmpty(path))
            throw new JsonException("Artifact path is ambient and must not be serialized.");
        return new(ReadCoordinate(value.GetProperty("coordinate")), value.GetString("sha256"), string.Empty, value.GetString("destination"));
    }

    private static ArtifactCoordinate ReadCoordinate(JsonElement value)
    {
        CheckObject(value, true, "group", "artifact", "version", "classifier", "extension");
        return new(value.GetString("group"), value.GetString("artifact"), value.GetString("version"), value.GetNullableString("classifier"), value.GetString("extension"));
    }

    private static ImmutableArray<ArtifactCoordinate> ReadCoordinates(JsonElement value, string name)
    {
        RequireKind(value, JsonValueKind.Array, name);
        return value.EnumerateArray().Select(ReadCoordinate).ToImmutableArray();
    }

    private static ImmutableArray<string> ReadStringArray(JsonElement value, string name)
    {
        RequireKind(value, JsonValueKind.Array, name);
        return value.EnumerateArray().Select(x => x.GetStringValue()).ToImmutableArray();
    }

    private static ImmutableArray<SignedRepositorySnapshot> ReadSnapshots(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Array, "repositorySnapshots");
        return value.EnumerateArray().Select(item =>
        {
            CheckObject(item, "id", "exactHttpsUri", "sha256", "signingFingerprint", "suite", "components", "architectures", "keyringMaterialId", "packageIndexMaterialIds");
            return new SignedRepositorySnapshot(item.GetString("id"), item.GetString("exactHttpsUri"), item.GetString("sha256"), item.GetString("signingFingerprint"), item.GetString("suite"), ReadStringArray(item.GetProperty("components"), "components"), ReadStringArray(item.GetProperty("architectures"), "architectures"))
            {
                KeyringMaterialId = item.GetString("keyringMaterialId"),
                PackageIndexMaterialIds = ReadMap(item.GetProperty("packageIndexMaterialIds"))
            };
        }).ToImmutableArray();
    }

    private static ImmutableArray<RuntimePackageSpec> ReadPackages(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Array, "packages");
        return value.EnumerateArray().Select(item =>
        {
            CheckObject(item, "name", "version", "sha256", "architecture", "materialId");
            return new RuntimePackageSpec(item.GetString("name"), item.GetString("version"), item.GetString("sha256"), item.GetString("architecture"))
            {
                MaterialId = item.GetString("materialId")
            };
        }).ToImmutableArray();
    }

    private static ImmutableArray<KeycloakRemovedPackageSpec> ReadRemovedPackages(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Array, "runtimeRemovedPackages");
        return value.EnumerateArray().Select(item =>
        {
            CheckObject(item, "name", "architecture", "expectedVersion");
            return new KeycloakRemovedPackageSpec(item.GetString("name"), item.GetString("architecture"), item.GetString("expectedVersion"));
        }).ToImmutableArray();
    }

    private static ImmutableDictionary<string, string> ReadMap(JsonElement value)
    {
        RequireKind(value, JsonValueKind.Object, "hashMap");
        return value.EnumerateObject().ToImmutableDictionary(x => x.Name, x => x.Value.GetStringValue(), StringComparer.Ordinal);
    }

    private static void WriteImage(Utf8JsonWriter w, KeycloakImageIdentity x)
    {
        w.WriteStartObject();
        w.WriteString("index", x.Index);
        w.WriteString("platformManifest", x.PlatformManifest);
        w.WriteString("platform", x.Platform);
        w.WriteString("indexMaterialId", x.IndexMaterialId);
        w.WriteString("archiveMaterialId", x.ArchiveMaterialId);
        w.WriteString("configDigest", x.ConfigDigest);
        WriteStrings(w, "diffIds", x.DiffIds);
        w.WriteEndObject();
    }

    private static void WriteOci(Utf8JsonWriter w, OciIdentity x)
    {
        w.WriteStartObject();
        w.WriteString("id", x.Id);
        w.WriteString("platform", x.Platform);
        w.WriteString("reference", x.Reference);
        w.WriteString("digest", x.Digest);
        w.WriteString("archiveMaterialId", x.ArchiveMaterialId);
        w.WriteString("platformManifestDigest", x.PlatformManifestDigest);
        w.WriteString("configDigest", x.ConfigDigest);
        w.WriteString("indexMaterialId", x.IndexMaterialId);
        WriteStrings(w, "diffIds", x.DiffIds);
        w.WriteEndObject();
    }

    private static void WriteOperation(Utf8JsonWriter w, KeycloakOperation x)
    {
        w.WriteStartObject();
        w.WriteString("id", x.Id);
        switch (x)
        {
            case KeycloakOperation.ArtifactReplacementGroupOperation group:
                w.WriteString("kind", "ArtifactReplacementGroup");
                w.WritePropertyName("group");
                WriteGroup(w, group.Group);
                break;
            case KeycloakOperation.RuntimeProfileOperation profile:
                w.WriteString("kind", "RuntimeProfile");
                w.WritePropertyName("profile");
                WriteProfile(w, profile.Profile);
                break;
            case KeycloakOperation.EmbeddedArchiveReplacementOperation archive:
                w.WriteString("kind", "EmbeddedArchiveReplacement");
                w.WritePropertyName("replacement");
                WriteArchive(w, archive.Replacement);
                break;
            default:
                throw new JsonException("Unknown operation type.");
        }

        w.WriteEndObject();
    }

    private static void WriteGroup(Utf8JsonWriter w, ArtifactReplacementGroup x)
    {
        w.WriteStartObject();
        w.WriteString("id", x.Id);
        w.WritePropertyName("replacements");
        w.WriteStartArray();
        foreach (var item in x.Replacements)
        {
            w.WriteStartObject();
            w.WritePropertyName("original");
            WriteArtifact(w, item.Original);
            w.WritePropertyName("replacement");
            WriteArtifact(w, item.Replacement);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WritePropertyName("requiredMembers");
        w.WriteStartArray();
        foreach (var item in x.RequiredMembers)
            WriteCoordinate(w, item);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteProfile(Utf8JsonWriter w, RuntimeProfile x)
    {
        w.WriteStartObject();
        w.WriteString("id", x.Id);
        w.WriteString("version", x.Version);
        w.WriteString("templateName", x.TemplateName);
        WriteStrings(w, "supportedSourceProfiles", x.SupportedSourceProfiles);
        w.WritePropertyName("runtimeOciIdentities");
        w.WriteStartArray();
        foreach (var item in x.RuntimeOciIdentities)
            WriteOci(w, item);
        w.WriteEndArray();
        w.WritePropertyName("repositorySnapshots");
        w.WriteStartArray();
        foreach (var item in x.RepositorySnapshots)
        {
            w.WriteStartObject();
            w.WriteString("id", item.Id);
            w.WriteString("exactHttpsUri", item.ExactHttpsUri);
            w.WriteString("sha256", item.Sha256);
            w.WriteString("signingFingerprint", item.SigningFingerprint);
            w.WriteString("suite", item.Suite);
            WriteStrings(w, "components", item.Components);
            WriteStrings(w, "architectures", item.Architectures);
            w.WriteString("keyringMaterialId", item.KeyringMaterialId);
            WriteMap(w, "packageIndexMaterialIds", item.PackageIndexMaterialIds);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        WritePackages(w, "requiredPackages", x.RequiredPackages);
        WriteRemovedPackages(w, x.RemovedPackages);
        WriteStrings(w, "expectedObservations", x.ExpectedObservations);
        w.WriteEndObject();
    }

    private static void WriteArchive(Utf8JsonWriter w, EmbeddedArchiveReplacement x)
    {
        w.WriteStartObject();
        w.WriteString("id", x.Id);
        w.WriteString("version", x.Version);
        w.WriteString("templateName", x.TemplateName);
        w.WritePropertyName("source");
        WriteCoordinate(w, x.Source);
        w.WritePropertyName("replacement");
        WriteCoordinate(w, x.Replacement);
        WriteStrings(w, "classPrefixes", x.ClassPrefixes);
        WriteStrings(w, "versionedClasses", x.VersionedClasses);
        WriteStrings(w, "servicePaths", x.ServicePaths);
        w.WritePropertyName("moduleSet");
        w.WriteStartObject();
        w.WritePropertyName("sourceArtifact");
        WriteArtifact(w, x.ModuleSet!.SourceArtifact);
        w.WritePropertyName("replacementArtifact");
        WriteArtifact(w, x.ModuleSet.ReplacementArtifact);
        w.WriteString("destination", x.ModuleSet.Destination);
        WriteMap(w, "sourceModuleSha256ByIdentity", x.ModuleSet.SourceModuleSha256ByIdentity);
        WriteMap(w, "replacementModuleSha256ByIdentity", x.ModuleSet.ReplacementModuleSha256ByIdentity);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WritePackages(Utf8JsonWriter w, string name, IEnumerable<RuntimePackageSpec> values)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var item in values)
        {
            w.WriteStartObject();
            w.WriteString("name", item.Name);
            w.WriteString("version", item.Version);
            w.WriteString("sha256", item.Sha256);
            w.WriteString("architecture", item.Architecture);
            w.WriteString("materialId", item.MaterialId);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteRemovedPackages(Utf8JsonWriter w, IEnumerable<KeycloakRemovedPackageSpec> values)
    {
        w.WritePropertyName("runtimeRemovedPackages");
        w.WriteStartArray();
        foreach (var item in values)
        {
            w.WriteStartObject();
            w.WriteString("name", item.Name);
            w.WriteString("architecture", item.Architecture);
            w.WriteString("expectedVersion", item.ExpectedVersion);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteMap(Utf8JsonWriter w, string name, IEnumerable<KeyValuePair<string, string>> values)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        foreach (var item in values.OrderBy(x => x.Key, StringComparer.Ordinal))
            w.WriteString(item.Key, item.Value);
        w.WriteEndObject();
    }

    private static void WriteArtifact(Utf8JsonWriter w, ArtifactReference x)
    {
        w.WriteStartObject();
        w.WritePropertyName("coordinate");
        WriteCoordinate(w, x.Coordinate);
        w.WriteString("sha256", x.Sha256);
        w.WriteString("destination", x.Destination);
        w.WriteEndObject();
    }

    private static void WriteCoordinate(Utf8JsonWriter w, ArtifactCoordinate x)
    {
        w.WriteStartObject();
        w.WriteString("group", x.Group);
        w.WriteString("artifact", x.Artifact);
        w.WriteString("version", x.Version);
        if (x.Classifier is not null)
            w.WriteString("classifier", x.Classifier);
        w.WriteString("extension", x.Extension);
        w.WriteEndObject();
    }

    private static void WriteThemeInput(Utf8JsonWriter w, KeycloakThemeInput x)
    {
        w.WriteStartObject();
        w.WriteString("id", x.Id);
        w.WriteString("sha256", x.Sha256);
        w.WriteString("localInputId", x.LocalInputId);
        w.WriteString("destinationPath", x.DestinationPath);
        if (x.ExactUri is not null)
            w.WriteString("exactUri", x.ExactUri);
        if (x.CapturedHostInput is not null)
            w.WriteString("capturedHostInput", x.CapturedHostInput);
        w.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var value in values)
            w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void RequireBoundedJson(ReadOnlySpan<byte> value)
    {
        if (value.Length > 1024 * 1024)
            throw new JsonException("Control JSON exceeds the 1 MiB supported profile.");
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> value) => JsonDocument.Parse(value.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 64 });
    private static void CheckObject(JsonElement value, params string[] allowed) => CheckObject(value, false, allowed);
    private static void CheckObject(JsonElement value, bool allowOptional, params string[] allowed)
    {
        RequireKind(value, JsonValueKind.Object, "object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new JsonException("Duplicate or unknown property.");
        if (!allowOptional)
            foreach (var name in allowed)
                if (!names.Contains(name))
                    throw new JsonException("Missing property.");
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, string name)
    {
        if (value.ValueKind != kind)
            throw new JsonException($"Property {name} has the wrong JSON kind.");
    }

    private static void RequireText(string actual, string expected, string field)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new JsonException($"{field} does not match its operation ID.");
    }

    private static T ParseEnum<T>(string value, string name)
        where T : struct, Enum => value.All(char.IsAsciiLetter) && Enum.TryParse<T>(value, false, out var result) && Enum.IsDefined(result) ? result : throw new JsonException($"Unknown {name}.");
}

internal static class KeycloakJsonExtensions
{
    public static string GetString(this JsonElement value, string name) => value.GetProperty(name).GetStringValue();
    public static string? GetNullableString(this JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? property.GetStringValue() : null;
    public static string GetStringValue(this JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { } result ? result : throw new JsonException("Expected a JSON string.");
}
