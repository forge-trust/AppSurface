using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>The host build contract for one verified context; it grants no publication or deployment authority.</summary>
public sealed class KeycloakBuildContext
{
    internal ContextMaterializationResult Result { get; }
    internal string PlanDigest { get; }

    internal KeycloakBuildContext(ContextMaterializationResult result, string planDigest)
    {
        (Result, PlanDigest) = (result, planDigest);
    }

    /// <summary>Canonical context manifest identity, excluding the local directory path.</summary>
    public string Digest => Result.ContextDigest;
    /// <summary>The owned context path used only by the host builder; excluded from portable identities and evidence.</summary>
    public string DirectoryPath => Result.ContextPath;

    /// <summary>Creates the fixed offline Docker build argument list after verifying the context.</summary>
    /// <param name = "imageTag">An explicit local tag under the host's build policy.</param>
    /// <param name = "cancellationToken">Cancels context verification before the host starts Docker.</param>
    /// <returns>Arguments for Docker 29; invoke with an argument-list API and no shell interpolation.</returns>
    public IReadOnlyList<string> CreateDockerBuildArguments(string imageTag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageTag) || imageTag.Length > 128 || imageTag.StartsWith('-') || imageTag.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not '/' and not ':'))
            throw KeycloakDiagnostics.Failure(KeycloakFailureCode.Invalid, KeycloakSafePhase.Build, PlanDigest, KeycloakEffectState.Committed, "Supply a valid explicit local image tag under host build policy.");
        Verify(cancellationToken);
        return Array.AsReadOnly(new[] { "build", "--network=none", "--provenance=false", "--platform", Result.Platform == ContextPlatform.LinuxArm64 ? "linux/arm64" : "linux/amd64", "--build-context", "appsurface-runtime=oci-layout://" + Path.Combine(DirectoryPath, "inputs", "oci", "runtime"), "--build-context", "appsurface-frontend=oci-layout://" + Path.Combine(DirectoryPath, "inputs", "oci", "frontend"), "--tag", imageTag, DirectoryPath });
    }

    /// <summary>Actual repaired CLI content identity, when archive replacement was requested.</summary>
    public string? RepairedCliSha256 => Result.RepairedCliSha256;

    /// <summary>Verifies the exact owned context immediately before the host invokes its builder.</summary>
    /// <param name = "cancellationToken">Cancels verification before the host build.</param>
    public void Verify(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = ContextMaterialization.Revalidate(Result.ContextPath, Result.ManifestDigest, cancellationToken);
        }
        catch (Exception ex)
        {
            throw KeycloakDiagnostics.Map(ex, KeycloakSafePhase.Build, PlanDigest, KeycloakEffectState.Committed);
        }
    }
}

public static partial class KeycloakSecurity
{
    /// <summary>Materializes only verified captured bytes into an absent host-owned build context.</summary>
    /// <param name = "plan">The accepted captured plan.</param>
    /// <param name = "absentContext">An absent local context directory; its parent must already exist.</param>
    /// <param name = "cancellationToken">Cancels the complete invocation before publication.</param>
    /// <returns>A context identity and verification contract for the host-owned builder.</returns>
    /// <exception cref = "KeycloakSecurityException">Materialization failed; no passing context was returned.</exception>
    public static async Task<KeycloakBuildContext> MaterializeAsync(CapturedKeycloakPlan plan, string absentContext, CancellationToken cancellationToken = default)
    {
        string? scratch = null;
        using var disk = TemporaryDiskBudget.BeginInvocation();
        using var events = KeycloakStageEvents.Begin(KeycloakStagePhase.Materialization, plan?.RecipeDigest, plan?.PolicyDigest, plan?.Mode.ToString(), plan?.Digest, "materialization");
        try
        {
            ArgumentNullException.ThrowIfNull(plan);
            using var accepted = plan.Snapshot.Enter();
            scratch = Path.Combine(PrivateTemporaryRoot(), "appsurface-materialize-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(plan.Recipe.ResourceLimits.MaxOperationSeconds, 300)));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var copied = await SnapshotStore.CopyForAcceptedUseAsync(plan.Snapshot, accepted, Path.Combine(scratch, "inputs"), stop.Token).ConfigureAwait(false);
            var prepared = PrepareContext(plan.Recipe, copied, scratch, stop.Token);
            var expectedArchive = plan.Recipe.Operations.OfType<KeycloakOperation.EmbeddedArchiveReplacementOperation>().SingleOrDefault()?.Replacement.ModuleSet?.ReplacementArtifact.Sha256;
            prepared = prepared with
            {
                ExpectedRepairedCliSha256 = expectedArchive
            };
            var result = await ContextMaterialization.MaterializeAsync(prepared, absentContext, stop.Token, new ContextMaterializationLimits(MaxAggregateBytes: plan.Recipe.ResourceLimits.MaxOutputBytes)).ConfigureAwait(false);
            var context = new KeycloakBuildContext(result, plan.Digest);
            events.SetCounts(materialCount: plan.Recipe.MaterialReferences.Length);
            events.SetContext(plan.Digest, operationId: "materialization");
            events.Complete(KeycloakEffectState.Committed);
            return context;
        }
        catch (Exception ex)
        {
            var failure = KeycloakDiagnostics.Map(ex, KeycloakSafePhase.Materialization, plan?.Digest ?? "materialize", KeycloakEffectState.None, events.CorrelationId);
            events.Reject(failure);
            throw failure;
        }
        finally
        {
            CleanOwned(scratch);
        }
    }

    internal static ContextMaterializationInput PrepareContext(KeycloakSecurityRecipe recipe, string inputs, string scratch, CancellationToken token)
    {
        var source = recipe.SourceImage;
        var expectedSource = new OciExpectedImage(null, source.PlatformManifest, source.Platform, source.ConfigDigest!, source.DiffIds);
        var reader = new OciReader(OciReader.SourceCaptureLimits);
        var observed = reader.Read(Material(inputs, source.ArchiveMaterialId!), expectedSource, token);
        const string prefix = "opt/keycloak/";
        var sourceFiles = observed.Entries.Where(x => x.Path.StartsWith(prefix, StringComparison.Ordinal) && x.Type == "file").ToArray();
        Require(sourceFiles.Length != 0 && !observed.Entries.Any(x => x.Path.StartsWith(prefix, StringComparison.Ordinal) && x.Type is not ("file" or "directory")), "Unsupported source tree node.");
        var extracted = Path.Combine(scratch, "source");
        _ = reader.Read(Material(inputs, source.ArchiveMaterialId!), expectedSource, new OciReadSelection(sourceFiles.Select(x => x.Path).ToHashSet(StringComparer.Ordinal), extracted), token);
        var root = new ContextSourceRoot(Path.Combine(extracted, "opt", "keycloak"), sourceFiles.Select(x => new ContextSourceFile(x.Path[prefix.Length..], x.Sha256[7..], x.Length, (UnixFileMode)x.Mode)).ToImmutableArray(), observed.Entries.Where(x => x.Path.StartsWith(prefix, StringComparison.Ordinal) && x.Type == "directory").Select(x => new ContextSourceDirectory(x.Path[prefix.Length..], (UnixFileMode)x.Mode)).ToImmutableArray());
        var themeOnly = recipe.Mode == KeycloakRecipeMode.ThemeOnly;
        var runtime = recipe.Operations.OfType<KeycloakOperation.RuntimeProfileOperation>().SingleOrDefault()?.Profile;
        var runtimeImage = themeOnly ? new OciIdentity("source", source.Index) with
        {
            Platform = source.Platform,
            ArchiveMaterialId = source.ArchiveMaterialId,
            PlatformManifestDigest = source.PlatformManifest,
            ConfigDigest = source.ConfigDigest,
            IndexMaterialId = source.IndexMaterialId,
            DiffIds = source.DiffIds
        }

        : runtime!.RuntimeOciIdentities.Single();
        var frontendImage = recipe.RuntimeFrontendOciIdentities.Single(x => x.Id == "frontend");
        ContextOciLayout Layout(OciIdentity image, string role)
        {
            var output = Path.Combine(scratch, role);
            OciLayout.CopyVerifiedGraph(Material(inputs, image.ArchiveMaterialId!), image.PlatformManifestDigest!, image.ConfigDigest!, output, token);
            return new(role, output, image.PlatformManifestDigest!, ContextMaterialization.ComputeLayoutDigest(output));
        }

        var replacements = themeOnly ? ImmutableArray<ContextReplacement>.Empty : recipe.Operations.OfType<KeycloakOperation.ArtifactReplacementGroupOperation>().SelectMany(x => x.Group.Replacements).Select(x => new ContextReplacement(x.Original.Destination, x.Original.Sha256, MaterialBySha(recipe, inputs, x.Replacement.Sha256), x.Replacement.Sha256)).ToImmutableArray();
        ArchiveTransformSpec? transform = null;
        if (!themeOnly && recipe.Operations.OfType<KeycloakOperation.EmbeddedArchiveReplacementOperation>().Any())
        {
            var archive = recipe.Operations.OfType<KeycloakOperation.EmbeddedArchiveReplacementOperation>().Single().Replacement;
            var moduleSet = archive.ModuleSet!;
            var modules = moduleSet.SourceModuleSha256ByIdentity.Select(item =>
            {
                var original = ParseCoordinate(item.Key);
                var replacementPair = moduleSet.ReplacementModuleSha256ByIdentity.Single(x =>
                {
                    var c = ParseCoordinate(x.Key);
                    return c.Group == original.Group && c.Artifact == original.Artifact && c.Classifier == original.Classifier;
                });
                var replacement = ParseCoordinate(replacementPair.Key);
                var modulePrefix = archive.ClassPrefixes.Single(x => original.Artifact == "jackson-core" ? x == "com/fasterxml/jackson/core/" : x == "com/fasterxml/jackson/databind/");
                var service = archive.ServicePaths.Single(x => original.Artifact == "jackson-core" ? x.EndsWith("com.fasterxml.jackson.core.JsonFactory", StringComparison.Ordinal) : x.EndsWith("com.fasterxml.jackson.core.ObjectCodec", StringComparison.Ordinal));
                return new ArchiveModuleSpec(original.Group, original.Artifact, original.Version, replacement.Version, modulePrefix, service["META-INF/services/".Length..], replacementPair.Value, original.Classifier, MaterialBySha(recipe, inputs, replacementPair.Value));
            }).ToImmutableArray();
            var bounds = new ArchiveBounds(MaxEntries: checked((int)recipe.ResourceLimits.MaxArchiveEntries), MaxExpandedBytes: recipe.ResourceLimits.MaxArchiveExpandedBytes, MaxOutputBytes: recipe.ResourceLimits.MaxOutputBytes);
            transform = new ArchiveTransformSpec(Path.Combine(root.RootPath, moduleSet.Destination["/opt/keycloak/".Length..]), Path.Combine(scratch, "repaired.jar"), moduleSet.SourceArtifact.Sha256, modules, bounds, true);
        }

        var themes = recipe.ThemeInputs.Where(x => x.DestinationPath != recipe.EntrypointPath).Select(x => new ContextThemeInput(x.DestinationPath!, Material(inputs, x.LocalInputId!), x.Sha256, (UnixFileMode)420)).ToImmutableArray();
        ContextHostInput? entry = null;
        if (recipe.EntrypointPath != "/opt/keycloak/bin/kc.sh")
        {
            var declared = recipe.ThemeInputs.Single(x => x.DestinationPath == recipe.EntrypointPath);
            entry = new(declared.DestinationPath!, Material(inputs, declared.LocalInputId!), declared.Sha256, (UnixFileMode)493);
        }

        var debs = themeOnly ? ImmutableArray<ContextDeb>.Empty : runtime!.RequiredPackages.Select(x => new ContextDeb($"{x.Name}_{x.Version}_{x.Architecture}.deb", Material(inputs, x.MaterialId!), x.Sha256)).ToImmutableArray();
        return new(root, Layout(runtimeImage, "runtime"), Layout(frontendImage, "frontend"), debs, replacements, transform, themes, entry, source.Platform == "linux/arm64" ? ContextPlatform.LinuxArm64 : ContextPlatform.LinuxAmd64, recipe.KeycloakVersion, ArchiveDestination: recipe.Operations.OfType<KeycloakOperation.EmbeddedArchiveReplacementOperation>().SingleOrDefault()?.Replacement.ModuleSet?.Destination, ThemeOnly: themeOnly);
    }
}
