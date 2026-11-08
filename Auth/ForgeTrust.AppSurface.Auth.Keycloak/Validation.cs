using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Validates closed recipe and host trust-policy declarations before effects.</summary>
public static class KeycloakRecipeValidation
{
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Digest = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MavenPart = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] SupportedPlatforms = ["linux/amd64", "linux/arm64"];
    private static readonly string[] RuntimeTemplates = ["temurin-ubuntu"];
    private static readonly string[] ArchiveTemplates = ["jackson"];
    private const long MaxMaterialBytes = 1L << 30;
    private const long MaxArchiveEntries = 65536;
    private const long MaxExpandedBytes = 1L << 30;
    private const int MaxOperationSeconds = 300;
    /// <summary>Validates schema, required fields, duplicate IDs, empty groups, overlap, and supported operation shapes.</summary>
    public static KeycloakSecurityRecipe Validate(KeycloakSecurityRecipe recipe)
    {
        if (recipe is null || recipe.SourceImage is null)
            throw Error(KeycloakFailureCode.Required, "recipe", "Provide a complete recipe.");
        if (recipe.SchemaVersion != 1)
            throw Error(KeycloakFailureCode.Unsupported, "schemaVersion", "Use schema version 1.");
        if (recipe.SourceProfiles.IsDefault || recipe.SourceProfiles.IsEmpty || recipe.EntrypointPath is null)
            throw Error(KeycloakFailureCode.Required, "recipeProfile", "Provide initialized source profiles and a reviewed entrypoint.");
        if (!Enum.IsDefined(recipe.Mode) || recipe.KeycloakVersion != "26.7.2" || !recipe.SourceProfiles.SequenceEqual(["keycloak-26.7.2"], StringComparer.Ordinal) || (recipe.EntrypointPath != "/opt/keycloak/bin/kc.sh" && !recipe.EntrypointPath.StartsWith("/opt/keycloak/bin/", StringComparison.Ordinal)))
            throw Error(KeycloakFailureCode.Invalid, "recipeProfile", "Use the supported Keycloak 26.7.2 profile and reviewed entrypoint path.");
        ValidateImagePath(recipe.EntrypointPath);
        if (!Digest.IsMatch(recipe.SourceImage.Index) || !Digest.IsMatch(recipe.SourceImage.PlatformManifest) || !SupportedPlatforms.Contains(recipe.SourceImage.Platform, StringComparer.Ordinal))
            throw Error(KeycloakFailureCode.Invalid, "sourceImage", "Use a supported lowercase digest and Linux amd64 or arm64 platform.");
        if (!AsciiNonEmpty(recipe.SourceImage.IndexMaterialId) || !AsciiNonEmpty(recipe.SourceImage.ArchiveMaterialId) || !Digest.IsMatch(recipe.SourceImage.ConfigDigest ?? string.Empty) || recipe.SourceImage.DiffIds.IsDefaultOrEmpty || recipe.SourceImage.DiffIds.Any(d => !Digest.IsMatch(d)))
            throw Error(KeycloakFailureCode.Required, "sourceImage", "Provide exact index/archive material IDs, config digest, and layer diff IDs.");
        if (recipe.RuntimeFrontendOciIdentities.IsDefault || recipe.RuntimeFrontendOciIdentities.IsEmpty || recipe.Operations.IsDefault || recipe.ThemeInputs.IsDefault || recipe.CapturedHostInputs.IsDefault || recipe.SourceProfiles.IsDefault || recipe.MaterialReferences.IsDefault || recipe.MaterialReferences.IsEmpty)
            throw Error(KeycloakFailureCode.Required, "recipe", "Provide initialized immutable collections.");
        foreach (var frontend in recipe.RuntimeFrontendOciIdentities)
            ValidateOci(frontend);
        RequireUnique(recipe.RuntimeFrontendOciIdentities.Select(x => x?.Id), "runtimeFrontendOciIdentities");
        if (recipe.RuntimeFrontendOciIdentities.Length != 1 || recipe.RuntimeFrontendOciIdentities[0].Id != "frontend")
            throw Error(KeycloakFailureCode.Unsupported, "frontend", "Use exactly one supported frontend identity.");
        RequireUnique(recipe.ThemeInputs.Select(x => x?.Id), "themeInputs");
        RequireUnique(recipe.MaterialReferences.Select(x => x?.Id), "materialReferences");
        if (recipe.Mode == KeycloakRecipeMode.Security && recipe.Operations.IsDefaultOrEmpty)
            throw Error(KeycloakFailureCode.Invalid, "operations", "Provide a non-empty security operation list.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var familyOrder = -1;
        var runtimeCount = 0;
        var archiveCount = 0;
        foreach (var op in recipe.Operations)
        {
            if (op is null || !AsciiNonEmpty(op.Id) || !ids.Add(op.Id))
                throw Error(KeycloakFailureCode.Conflict, "operations", "Use unique non-empty printable ASCII operation IDs.");
            switch (op)
            {
                case KeycloakOperation.ArtifactReplacementGroupOperation x when x.Group is null || x.Group.Replacements.IsDefaultOrEmpty || x.Group.RequiredMembers.IsDefaultOrEmpty:
                    throw Error(KeycloakFailureCode.Invalid, op.Id, "Declare every required alignment member.");
                case KeycloakOperation.ArtifactReplacementGroupOperation x:
                    if (familyOrder > 0)
                        throw Error(KeycloakFailureCode.Conflict, op.Id, "Artifact replacement groups must precede later operation families.");
                    ValidateGroup(x.Group, destinations);
                    familyOrder = 0;
                    break;
                case KeycloakOperation.RuntimeProfileOperation x when x.Profile is null || string.IsNullOrWhiteSpace(x.Profile.Version) || string.IsNullOrWhiteSpace(x.Profile.TemplateName) || x.Profile.SupportedSourceProfiles.IsDefaultOrEmpty || x.Profile.RuntimeOciIdentities.IsDefaultOrEmpty || x.Profile.RepositorySnapshots.IsDefaultOrEmpty || x.Profile.RequiredPackages.IsDefaultOrEmpty || x.Profile.RemovedPackages.IsDefaultOrEmpty || x.Profile.ExpectedObservations.IsDefaultOrEmpty:
                    throw Error(KeycloakFailureCode.Required, op.Id, "Provide complete versioned runtime profile metadata.");
                case KeycloakOperation.RuntimeProfileOperation x:
                    if (familyOrder > 1 || ++runtimeCount > 1 || x.Profile.Version != "1" || !RuntimeTemplates.Contains(x.Profile.TemplateName, StringComparer.Ordinal) || !x.Profile.SupportedSourceProfiles.SequenceEqual(["keycloak-26.7.2"], StringComparer.Ordinal))
                        throw Error(KeycloakFailureCode.Unsupported, op.Id, "Use one complete version 1 runtime profile in the supported operation order.");
                    ValidateRuntimeProfile(x.Profile);
                    familyOrder = 1;
                    break;
                case KeycloakOperation.EmbeddedArchiveReplacementOperation x when x.Replacement is null || (x.Replacement.ClassPrefixes.IsDefaultOrEmpty && x.Replacement.VersionedClasses.IsDefaultOrEmpty && x.Replacement.ServicePaths.IsDefaultOrEmpty):
                    throw Error(KeycloakFailureCode.Invalid, op.Id, "Declare archive members.");
                case KeycloakOperation.EmbeddedArchiveReplacementOperation x when x.Replacement is null || x.Replacement.ModuleSet is null:
                    throw Error(KeycloakFailureCode.Required, op.Id, "Declare complete source and replacement archive modules.");
                case KeycloakOperation.EmbeddedArchiveReplacementOperation x:
                    if (++archiveCount > 1 || x.Replacement.Version != "1" || !ArchiveTemplates.Contains(x.Replacement.TemplateName, StringComparer.Ordinal))
                        throw Error(KeycloakFailureCode.Unsupported, op.Id, "Use at most one complete version 1 archive profile in the supported operation order.");
                    ValidateArchive(x.Replacement);
                    familyOrder = 2;
                    break;
                default:
                    throw Error(KeycloakFailureCode.Unsupported, "operations", "Use a supported closed operation.");
            }
        }

        if (recipe.ResourceLimits is null || recipe.ResourceLimits.MaxMaterialBytes <= 0 || recipe.ResourceLimits.MaxMaterialBytes > MaxMaterialBytes || recipe.ResourceLimits.MaxArchiveEntries <= 0 || recipe.ResourceLimits.MaxArchiveEntries > MaxArchiveEntries || recipe.ResourceLimits.MaxArchiveExpandedBytes <= 0 || recipe.ResourceLimits.MaxArchiveExpandedBytes > MaxExpandedBytes || recipe.ResourceLimits.MaxOutputBytes <= 0 || recipe.ResourceLimits.MaxOutputBytes > MaxExpandedBytes || recipe.ResourceLimits.MaxOperationSeconds <= 0 || recipe.ResourceLimits.MaxOperationSeconds > MaxOperationSeconds)
            throw Error(KeycloakFailureCode.Invalid, "resourceLimits", "Provide positive finite limits within the supported ceilings.");
        var materialIds = recipe.MaterialReferences.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var themeDestinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in recipe.ThemeInputs)
        {
            if (input is null || !Sha256.IsMatch(input.Sha256) || !AsciiNonEmpty(input.Id) || !AsciiNonEmpty(input.LocalInputId) || !materialIds.Contains(input.LocalInputId!) || input.DestinationPath is null)
                throw Error(KeycloakFailureCode.Invalid, input?.Id ?? "theme", "Use an ASCII material binding and canonical theme destination.");
            ValidateThemeDestination(input.DestinationPath);
            if (!themeDestinations.Add(input.DestinationPath) || destinations.Contains(input.DestinationPath))
                throw Error(KeycloakFailureCode.Conflict, input.Id, "Theme destinations must be unique and cannot overlap artifact replacements.");
        }

        foreach (var material in recipe.MaterialReferences)
        {
            if (material is null || !AsciiNonEmpty(material.Id) || !Sha256.IsMatch(material.Sha256) || material.MaxBytes <= 0 || material.MaxBytes > MaxMaterialBytes)
                throw Error(KeycloakFailureCode.Invalid, "material", "Use an ASCII material ID, lowercase SHA-256, and bounded material size.");
            if (material.CapturedHostInput)
            {
                if (!string.IsNullOrEmpty(material.ExactHttpsUri) || !recipe.CapturedHostInputs.Contains(material.Id, StringComparer.Ordinal))
                    throw Error(KeycloakFailureCode.Invalid, material.Id, "Host-captured material must have an empty URI and explicit capture binding.");
            }
            else if (string.IsNullOrEmpty(material.ExactHttpsUri))
                throw Error(KeycloakFailureCode.Required, material.Id, "Downloadable material requires an exact HTTPS URI.");
            else
                RequireCanonicalUri(material.ExactHttpsUri, material.Id);
        }

        if (recipe.TemplateBaseline is { } baseline)
        {
            ValidateImagePath(baseline.ArchivePath);
            if (!Sha256.IsMatch(baseline.ArchiveSha256) || !Sha256.IsMatch(baseline.Sha256) || !materialIds.Contains(baseline.MaterialId) || string.IsNullOrWhiteSpace(baseline.Entry) || baseline.Entry.StartsWith('/') || baseline.Entry.Contains('\\') || baseline.Entry.Split('/').Any(x => x is "" or "." or ".."))
                throw Error(KeycloakFailureCode.Invalid, "templateBaseline", "Bind the exact safe upstream archive member and reviewed material digest.");
        }

        if (recipe.ThemeInputs.Any(x => x.DestinationPath!.EndsWith(".ftl", StringComparison.Ordinal)) && recipe.TemplateBaseline is null)
            throw Error(KeycloakFailureCode.Required, "templateBaseline", "Supply the reviewed upstream template baseline before replacing templates.");
        ValidateMaterialReferences(recipe, materialIds);
        if (recipe.Mode == KeycloakRecipeMode.Security && runtimeCount != 1)
            throw Error(KeycloakFailureCode.Required, "operations", "Security recipes require exactly one runtime profile.");
        if (recipe.Mode == KeycloakRecipeMode.ThemeOnly && recipe.Operations.Length != 0)
            throw Error(KeycloakFailureCode.Conflict, "operations", "Theme-only recipes cannot contain security operations.");
        return recipe;
    }

    /// <summary>Validates host-local capture bindings separately from recipe identity.</summary>
    public static KeycloakCaptureInputs Validate(KeycloakCaptureInputs inputs)
    {
        if (inputs is null || !AsciiNonEmpty(inputs.ReviewedRecipeId) || inputs.LocalInputs.IsDefault || inputs.LocalInputs.IsEmpty)
            throw Error(KeycloakFailureCode.Required, "captureInputs", "Provide a reviewed recipe ID and local bindings.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs.LocalInputs)
            if (input is null || !ids.Add(input.MaterialId) || !AsciiNonEmpty(input.MaterialId) || string.IsNullOrWhiteSpace(input.LocalPath) || !Sha256.IsMatch(input.Sha256) || input.MaxBytes <= 0)
                throw Error(KeycloakFailureCode.Invalid, "captureInputs", "Provide unique bounded local bindings with exact digests.");
        return inputs;
    }

    private static void ValidateGroup(ArtifactReplacementGroup group, HashSet<string> destinations)
    {
        if (group is null || group.Replacements.IsDefault || group.RequiredMembers.IsDefault || group.Replacements.IsEmpty || group.RequiredMembers.IsEmpty)
            throw Error(KeycloakFailureCode.Invalid, "group", "Declare a complete non-empty alignment group.");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in group.RequiredMembers)
        {
            ValidateCoordinate(member);
            if (!declared.Add(CoordinateKey(member)))
                throw Error(KeycloakFailureCode.Conflict, group.Id, "Required alignment members must be unique.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var replacement in group.Replacements)
        {
            ValidateArtifact(replacement.Original);
            ValidateArtifact(replacement.Replacement);
            if (!IsSameArtifact(replacement.Original.Coordinate, replacement.Replacement.Coordinate) || replacement.Original.Destination != replacement.Replacement.Destination)
                throw Error(KeycloakFailureCode.Conflict, group.Id, "Each replacement must preserve artifact identity and destination while changing only the version.");
            var replacementKey = CoordinateKey(replacement.Replacement.Coordinate);
            if (!declared.Contains(replacementKey) || !destinations.Add(replacement.Replacement.Destination))
                throw Error(KeycloakFailureCode.Conflict, group.Id, "Alignment members must be declared exactly and destinations must be unique.");
            seen.Add(replacementKey);
        }

        if (!declared.SetEquals(seen))
            throw Error(KeycloakFailureCode.Conflict, group.Id, "Required alignment members must form a complete declared set.");
    }

    private static void ValidateRuntimeProfile(RuntimeProfile profile)
    {
        if (!profile.RequiredPackages.Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "libssl3t64", "openssl", "openssl-provider-legacy" }, StringComparer.Ordinal) || !profile.RemovedPackages.Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "curl", "libcurl4t64" }, StringComparer.Ordinal))
            throw Error(KeycloakFailureCode.Unsupported, profile.Id, "Use the complete supported OpenSSL package closure and curl/libcurl removals.");
        if (profile.RuntimeOciIdentities.Length != 1)
            throw Error(KeycloakFailureCode.Unsupported, profile.Id, "Use exactly one runtime OCI identity.");
        foreach (var identity in profile.RuntimeOciIdentities)
            ValidateOci(identity);
        foreach (var snapshot in profile.RepositorySnapshots)
        {
            if (snapshot is null || !Sha256.IsMatch(snapshot.Sha256) || !AsciiNonEmpty(snapshot.SigningFingerprint) || !AsciiNonEmpty(snapshot.KeyringMaterialId) || snapshot.Components.IsDefaultOrEmpty || snapshot.Architectures.IsDefaultOrEmpty || (snapshot.PackageIndexMaterialIds is null || snapshot.PackageIndexMaterialIds.Count == 0) || snapshot.PackageIndexMaterialIds.Any(x => !AsciiNonEmpty(x.Key) || !AsciiNonEmpty(x.Value)))
                throw Error(KeycloakFailureCode.Invalid, profile.Id, "Provide complete signed repository snapshot metadata.");
            RequireCanonicalUri(snapshot.ExactHttpsUri, profile.Id + ".repositorySnapshot");
        }

        RequireUnique(profile.RuntimeOciIdentities.Select(x => x?.Id), profile.Id + ".runtimeOciIdentities");
        RequireUnique(profile.RepositorySnapshots.Select(x => x?.Id), profile.Id + ".repositorySnapshots");
        var architecture = profile.RuntimeOciIdentities.Single().Platform == "linux/arm64" ? "arm64" : "amd64";
        foreach (var package in profile.RequiredPackages)
            if (package is null || !Sha256.IsMatch(package.Sha256) || !AsciiNonEmpty(package.Name) || !DebianVersion(package.Version) || package.Architecture != architecture || !AsciiNonEmpty(package.MaterialId))
                throw Error(KeycloakFailureCode.Invalid, profile.Id, "Provide complete package closure digests and material bindings.");
        foreach (var package in profile.RemovedPackages)
            if (package is null || !AsciiNonEmpty(package.Name) || package.Architecture != architecture || !DebianVersion(package.ExpectedVersion))
                throw Error(KeycloakFailureCode.Invalid, profile.Id, "Provide a real removed-package identity without captured-byte fields.");
    }

    private static void ValidateArchive(EmbeddedArchiveReplacement archive)
    {
        ValidateCoordinate(archive.Source);
        ValidateCoordinate(archive.Replacement);
        var modules = archive.ModuleSet!;
        ValidateArtifact(modules.SourceArtifact);
        ValidateArtifact(modules.ReplacementArtifact);
        ValidateImagePath(modules.Destination);
        if (!IsSameArtifact(archive.Source, modules.SourceArtifact.Coordinate) || !IsSameArtifact(archive.Replacement, modules.ReplacementArtifact.Coordinate) || modules.SourceArtifact.Destination != modules.ReplacementArtifact.Destination || modules.SourceArtifact.Destination != modules.Destination || modules.SourceModuleSha256ByIdentity.IsEmpty || modules.ReplacementModuleSha256ByIdentity.IsEmpty || modules.SourceModuleSha256ByIdentity.Count != modules.ReplacementModuleSha256ByIdentity.Count)
            throw Error(KeycloakFailureCode.Conflict, archive.Id, "Archive artifacts, coordinates, destination, and module maps must be complete and aligned.");
        foreach (var digest in modules.SourceModuleSha256ByIdentity.Values.Concat(modules.ReplacementModuleSha256ByIdentity.Values))
            if (!Sha256.IsMatch(digest))
                throw Error(KeycloakFailureCode.Invalid, archive.Id, "Use lowercase SHA-256 module hashes.");
    }

    private static void ValidateArtifact(ArtifactReference artifact)
    {
        if (artifact is null || !Sha256.IsMatch(artifact.Sha256) || string.IsNullOrEmpty(artifact.Destination))
            throw Error(KeycloakFailureCode.Invalid, "artifact", "Provide a complete artifact digest and image destination.");
        ValidateCoordinate(artifact.Coordinate);
        ValidateImagePath(artifact.Destination);
    }

    private static void ValidateOci(OciIdentity identity)
    {
        if (identity is null || !AsciiNonEmpty(identity.Id) || !SupportedPlatforms.Contains(identity.Platform, StringComparer.Ordinal) || !Ascii(identity.Reference) || !Digest.IsMatch(identity.Digest) || !AsciiNonEmpty(identity.ArchiveMaterialId) || !Digest.IsMatch(identity.PlatformManifestDigest ?? string.Empty) || !Digest.IsMatch(identity.ConfigDigest ?? string.Empty) || identity.DiffIds.IsDefaultOrEmpty || identity.DiffIds.Any(d => !Digest.IsMatch(d)) || !AsciiNonEmpty(identity.IndexMaterialId))
            throw Error(KeycloakFailureCode.Invalid, "oci", "Provide complete pinned OCI archive, index, platform, manifest, config, and diff IDs.");
    }

    private static void ValidateCoordinate(ArtifactCoordinate coordinate)
    {
        if (coordinate is null || !MavenPart.IsMatch(coordinate.Group) || !MavenPart.IsMatch(coordinate.Artifact) || !MavenPart.IsMatch(coordinate.Version) || (coordinate.Classifier is not null && !MavenPart.IsMatch(coordinate.Classifier)) || !MavenPart.IsMatch(coordinate.Extension))
            throw Error(KeycloakFailureCode.Invalid, "coordinate", "Provide a canonical ASCII Maven coordinate.");
    }

    private static string CoordinateKey(ArtifactCoordinate coordinate) => $"{coordinate.Group}:{coordinate.Artifact}:{coordinate.Version}:{coordinate.Classifier ?? string.Empty}:{coordinate.Extension}";
    private static bool IsSameArtifact(ArtifactCoordinate original, ArtifactCoordinate replacement) => original.Group == replacement.Group && original.Artifact == replacement.Artifact && original.Classifier == replacement.Classifier && original.Extension == replacement.Extension;
    private static void ValidateImagePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value == "/" || value[0] != '/' || value.Any(c => c is < '\u0021' or > '\u007e') || value.Contains('\\') || value.Split('/').Skip(1).Any(part => part.Length == 0 || part is "." or ".."))
            throw Error(KeycloakFailureCode.Invalid, "imagePath", "Use an absolute canonical image-internal path.");
    }

    private static void ValidateThemeDestination(string value)
    {
        ValidateImagePath(value);
        if (value != "/opt/keycloak/theme-manifest.sha256" && !value.StartsWith("/opt/keycloak/themes/", StringComparison.Ordinal) && !value.StartsWith("/opt/keycloak/bin/", StringComparison.Ordinal))
            throw Error(KeycloakFailureCode.Invalid, "themeDestination", "Use the supported Keycloak theme or entrypoint destination.");
    }

    private static void RequireUnique(IEnumerable<string?> values, string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!AsciiNonEmpty(value) || !seen.Add(value!))
                throw Error(KeycloakFailureCode.Conflict, id, "Use unique non-empty ASCII identities.");
    }

    private static void ValidateMaterialReferences(KeycloakSecurityRecipe recipe, HashSet<string> materialIds)
    {
        var references = new List<string?>
        {
            recipe.SourceImage.IndexMaterialId,
            recipe.SourceImage.ArchiveMaterialId
        };
        references.AddRange(recipe.RuntimeFrontendOciIdentities.SelectMany(x => new[] { x.ArchiveMaterialId, x.IndexMaterialId }));
        foreach (var runtime in recipe.Operations.OfType<KeycloakOperation.RuntimeProfileOperation>())
        {
            references.AddRange(runtime.Profile.RuntimeOciIdentities.SelectMany(x => new[] { x.ArchiveMaterialId, x.IndexMaterialId }));
            references.AddRange(runtime.Profile.RepositorySnapshots.SelectMany(x => new[] { x.KeyringMaterialId }.Concat(x.PackageIndexMaterialIds?.Values ?? [])));
            references.AddRange(runtime.Profile.RequiredPackages.Select(x => x.MaterialId));
        }

        foreach (var reference in references)
            if (!AsciiNonEmpty(reference) || !materialIds.Contains(reference!))
                throw Error(KeycloakFailureCode.Conflict, "materialReferences", "Every material reference must resolve to one declared material.");
    }

    private static void RequireCanonicalUri(string value, string id)
    {
        try
        {
            _ = KeycloakCanonicalization.CanonicalizeHttpsUri(value);
        }
        catch (ArgumentException)
        {
            throw Error(KeycloakFailureCode.Invalid, id, "Use one canonical HTTPS URI without aliases, escapes, or control characters.");
        }
    }

    private static bool DebianVersion(string? value) => value is { Length: > 0 and <= 256 } && char.IsAsciiDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-' or ':' or '~');
    private static bool AsciiNonEmpty(string? value) => value is { Length: > 0 } && Ascii(value);
    /// <summary>Validates policy version, owner approval, exact origins, and material digests without network access.</summary>
    public static KeycloakMaterialTrustPolicy Validate(KeycloakMaterialTrustPolicy policy)
    {
        if (policy is null || !AsciiNonEmpty(policy.Version) || !AsciiNonEmpty(policy.OwnerApprovalReference) || policy.PermittedHttpsOrigins.IsDefaultOrEmpty || policy.PermittedPathMappings.IsDefaultOrEmpty || policy.PermittedCoordinateMappings.IsDefaultOrEmpty || policy.MaterialSha256ByIdentity is null || policy.MaterialSha256ByIdentity.Count == 0 || policy.SigningFingerprints.IsDefault || policy.SigningAlgorithms.IsDefault || policy.WithdrawnKeys.IsDefault || policy.HistoricalExactReleaseBytes.IsDefault)
            throw Error(KeycloakFailureCode.Required, "policy", "Provide initialized policy fields and owner approval reference.");
        RequireUnique(policy.PermittedHttpsOrigins, "permittedHttpsOrigins");
        RequireUnique(policy.PermittedPathMappings, "permittedPathMappings");
        RequireUnique(policy.PermittedCoordinateMappings, "permittedCoordinateMappings");
        RequireUnique(policy.SigningFingerprints, "signingFingerprints");
        RequireUnique(policy.SigningAlgorithms, "signingAlgorithms");
        RequireUnique(policy.WithdrawnKeys, "withdrawnKeys");
        RequireUnique(policy.HistoricalExactReleaseBytes, "historicalExactReleaseBytes");
        foreach (var origin in policy.PermittedHttpsOrigins)
            RequireCanonicalUri(origin, "permittedHttpsOrigins");
        foreach (var mapping in policy.PermittedPathMappings.Concat(policy.PermittedCoordinateMappings))
            if (!AsciiNonEmpty(mapping) || mapping.Contains("..", StringComparison.Ordinal) || mapping.Contains('\\'))
                throw Error(KeycloakFailureCode.Invalid, "policyMapping", "Use safe non-empty policy mappings.");
        foreach (var item in policy.MaterialSha256ByIdentity)
            if (!AsciiNonEmpty(item.Key) || !Sha256.IsMatch(item.Value))
                throw Error(KeycloakFailureCode.Invalid, "materialDigest", "Use non-empty identities and lowercase SHA-256 values.");
        return policy;
    }

    /// <summary>Returns recommendation metadata only; it never performs a retry.</summary>
    public static KeycloakRetryMetadata RetryFor(KeycloakEffectState effect, bool transientNetworkOrReadOnlyProbe, bool verifiedCleanedStaging = false, int previousExplicitAttempts = 0) => effect == KeycloakEffectState.Unknown ? KeycloakRetryMetadata.InspectBeforeRetry : previousExplicitAttempts > 0 || effect == KeycloakEffectState.Committed || (effect == KeycloakEffectState.Staged && !verifiedCleanedStaging) ? KeycloakRetryMetadata.None : transientNetworkOrReadOnlyProbe ? KeycloakRetryMetadata.ExplicitTransientOnce : KeycloakRetryMetadata.None;
    private static bool Ascii(string value) => value.All(c => c is >= '\u0021' and <= '\u007e');
    private static KeycloakSecurityException Error(KeycloakFailureCode code, string id, string action) => KeycloakDiagnostics.Failure(code, KeycloakSafePhase.Validation, id, KeycloakEffectState.None, action);
}

/// <summary>Canonical HTTPS URI boundary validation.</summary>
public static class KeycloakCanonicalization
{
    /// <summary>Canonicalizes HTTPS DNS URIs and rejects userinfo, fragments, IP hosts, encoded separators, and dot segments.</summary>
    public static string CanonicalizeHttpsUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c is < '\u0021' or > '\u007e') || value.Contains('\\') || value.Contains('%') || value.Contains("?", StringComparison.Ordinal) || value.Contains("#", StringComparison.Ordinal) || value.Split('/').Any(x => x is "." or "..") || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "https", StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            throw new ArgumentException("Only canonical HTTPS DNS URIs are supported.", nameof(value));
        var host = new System.Globalization.IdnMapping().GetAscii(uri.Host).ToLowerInvariant();
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (host.Length == 0 || path.Contains("%2f", StringComparison.OrdinalIgnoreCase) || path.Contains("%5c", StringComparison.OrdinalIgnoreCase) || path.Contains("%2e", StringComparison.OrdinalIgnoreCase) || path.Contains("%25", StringComparison.OrdinalIgnoreCase) || path.Split('/').Any(x => x is "." or ".."))
            throw new ArgumentException("The URI is ambiguous.", nameof(value));
        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var canonical = $"https://{host}{port}/{path.TrimStart('/')}";
        if (!string.Equals(value, canonical, StringComparison.Ordinal))
            throw new ArgumentException("The URI is not canonical.", nameof(value));
        return canonical;
    }
}
