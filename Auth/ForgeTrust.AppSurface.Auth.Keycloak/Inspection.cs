using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>One observed conformance check; no release or vulnerability policy is implied.</summary>
/// <param name = "Id">Stable check identity.</param>
/// <param name = "Expected">Safe expected content or configuration identity.</param>
/// <param name = "Observed">Actual safe observed identity.</param>
public sealed record KeycloakObservedCheck(string Id, string Expected, string Observed)
{
    /// <summary>The declared operation, theme input, or fixed profile owning this observation.</summary>
    public string OperationId { get; init; } = "image";
    /// <summary>Only passing observations belong to returned evidence; failures throw a typed diagnostic.</summary>
    public string Outcome => "passed";
}

/// <summary>Safe input-image identity retained with an inspection result.</summary>
/// <param name = "Role">The recipe-local input role.</param>
/// <param name = "IndexDigest">Exact upstream index digest.</param>
/// <param name = "ManifestDigest">Selected platform manifest digest.</param>
/// <param name = "ConfigDigest">Exact image config digest.</param>
/// <param name = "Platform">The explicit image platform.</param>
public sealed record KeycloakObservedImageInput(string Role, string IndexDigest, string ManifestDigest, string ConfigDigest, string Platform);
/// <summary>Versioned framework operation that was evaluated.</summary>
/// <param name = "OperationId">Declared stable operation identity.</param>
/// <param name = "Family">The closed operation family.</param>
/// <param name = "Version">The evaluated profile or transformer version.</param>
public sealed record KeycloakObservedProfile(string OperationId, string Family, string Version);
/// <summary>Passing observations created only by the supported exact-image inspector.</summary>
public sealed class VerifiedKeycloakImageEvidence
{
    internal VerifiedKeycloakImageEvidence(string plan, KeycloakInspectionImage image, KeycloakRecipeMode mode, IEnumerable<KeycloakObservedCheck> checks, CapturedKeycloakPlan? captured = null)
    {
        (PlanDigest, Image, Mode) = (plan, image, mode);
        RecipeDigest = captured?.RecipeDigest ?? string.Empty;
        PolicyDigest = captured?.PolicyDigest ?? string.Empty;
        var recipe = captured?.Recipe;
        InputImages = recipe is null ? [] : new[]
        {
            new KeycloakObservedImageInput("source", recipe.SourceImage.Index, recipe.SourceImage.PlatformManifest, recipe.SourceImage.ConfigDigest!, recipe.SourceImage.Platform)
        }.Concat(recipe.RuntimeFrontendOciIdentities.Concat(recipe.Operations.OfType<KeycloakOperation.RuntimeProfileOperation>().SelectMany(x => x.Profile.RuntimeOciIdentities)).Select(x => new KeycloakObservedImageInput(x.Id!, x.Digest, x.PlatformManifestDigest!, x.ConfigDigest!, x.Platform!))).ToImmutableArray();
        Profiles = recipe?.Operations.Select(operation => operation switch
        {
            KeycloakOperation.ArtifactReplacementGroupOperation => new KeycloakObservedProfile(operation.Id, "artifact-group", "1"),
            KeycloakOperation.RuntimeProfileOperation runtime => new KeycloakObservedProfile(operation.Id, "runtime:" + runtime.Profile.TemplateName, runtime.Profile.Version),
            KeycloakOperation.EmbeddedArchiveReplacementOperation archive => new KeycloakObservedProfile(operation.Id, "archive:" + archive.Replacement.TemplateName, archive.Replacement.Version),
            _ => throw new InvalidDataException("Unsupported observation profile.")
        }).ToImmutableArray() ?? [];
        TemplateBaselineSha256 = recipe?.TemplateBaseline?.Sha256;
        Checks = checks.Select(check => check with { OperationId = Owner(recipe, check.Id) }).ToImmutableArray();
    }

    private static string Owner(KeycloakSecurityRecipe? recipe, string check)
    {
        if (recipe is null)
            return "image";
        if (check.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = "/" + check[5..];
            var theme = recipe.ThemeInputs.FirstOrDefault(x => x.DestinationPath == path);
            if (theme is not null)
                return theme.Id;
            foreach (var operation in recipe.Operations)
                if (operation is KeycloakOperation.ArtifactReplacementGroupOperation group && group.Group.Replacements.Any(x => x.Replacement.Destination == path) || operation is KeycloakOperation.EmbeddedArchiveReplacementOperation archive && archive.Replacement.ModuleSet!.Destination == path)
                    return operation.Id;
            return "source";
        }

        return check.StartsWith("template:", StringComparison.Ordinal) ? "template-baseline" : recipe.Operations.OfType<KeycloakOperation.RuntimeProfileOperation>().SingleOrDefault()?.Id ?? "theme-only";
    }

    /// <summary>Stable evidence schema; unknown versions require a coordinated upgrade.</summary>
    public string Schema => "appsurface-keycloak-observed-image-v1";
    /// <summary>The exact captured plan content identity.</summary>
    public string PlanDigest { get; }
    /// <summary>The canonical recipe accepted during capture.</summary>
    public string RecipeDigest { get; }
    /// <summary>The independently approved host policy accepted during capture.</summary>
    public string PolicyDigest { get; }
    /// <summary>Safe source, runtime and frontend input identities evaluated by this inspection.</summary>
    public ImmutableArray<KeycloakObservedImageInput> InputImages { get; }
    /// <summary>The evaluated closed operation profile versions.</summary>
    public ImmutableArray<KeycloakObservedProfile> Profiles { get; }
    /// <summary>The reviewed upstream template hash, when required by the recipe.</summary>
    public string? TemplateBaselineSha256 { get; }
    /// <summary>The exact framework inspector implementation profile.</summary>
    public string InspectorVersion => "docker-29-oci-v1";
    /// <summary>The deterministic archive transformer implementation profile.</summary>
    public string TransformerVersion => "managed-zip-v1";
    /// <summary>The exact inspected image content, config, platform, and rootfs identities.</summary>
    public KeycloakInspectionImage Image { get; }
    /// <summary>The scope actually evaluated by this inspection.</summary>
    public KeycloakRecipeMode Mode { get; }
    /// <summary>Complete passing observations for the declared scope.</summary>
    public ImmutableArray<KeycloakObservedCheck> Checks { get; }
    /// <summary>Human-readable outcome with an explicit release boundary.</summary>
    public string Outcome => Mode == KeycloakRecipeMode.ThemeOnly ? "THEME-ONLY VERIFIED; security customization not evaluated" : "IMAGE VERIFIED; release gates pending";

    /// <summary>Writes observations once to a fresh caller-owned evidence path.</summary>
    /// <param name = "absentFile">The absent local evidence file.</param>
    /// <param name = "cancellationToken">Cancels before publication.</param>
    /// <returns>The absolute published evidence path.</returns>
    public async Task<string> WriteAsync(string absentFile, CancellationToken cancellationToken = default)
    {
        string? temporary = null;
        using var disk = TemporaryDiskBudget.BeginInvocation();
        using var events = KeycloakStageEvents.Begin(KeycloakStagePhase.Evidence, RecipeDigest, PolicyDigest, Mode.ToString(), PlanDigest, "evidence");
        try
        {
            var output = Path.GetFullPath(absentFile);
            OciLayout.RequireNoLinks(output);
            KeycloakSecurity.Require(!Path.Exists(output), "Evidence output must be absent.");
            var parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            temporary = Path.Combine(parent, ".evidence-" + Guid.NewGuid().ToString("N"));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = Schema, planDigest = PlanDigest, recipeDigest = RecipeDigest, policyDigest = PolicyDigest, inputImages = InputImages, profiles = Profiles, templateBaselineSha256 = TemplateBaselineSha256, inspectorVersion = InspectorVersion, transformerVersion = TransformerVersion, image = Image, mode = Mode.ToString(), outcome = Outcome, checks = Checks }, new JsonSerializerOptions { WriteIndented = true });
            TemporaryDiskBudget.Account(bytes.LongLength);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                file.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            OciLayout.RequireNoLinks(parent);
            AtomicFilePublication.Publish(temporary, output);
            events.SetCounts(checkCount: Checks.Length);
            events.Complete(KeycloakEffectState.Committed);
            return output;
        }
        catch (Exception ex)
        {
            var failure = KeycloakDiagnostics.Map(ex, KeycloakSafePhase.Evidence, events.CheckId ?? "evidence", KeycloakEffectState.None, events.CorrelationId);
            events.Reject(failure);
            throw failure;
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

public static partial class KeycloakSecurity
{
    /// <summary>Inspects actual immutable Docker content against the captured plan and runs bound read-only probes.</summary>
    /// <param name = "plan">The captured plan defining all expected operations.</param>
    /// <param name = "image">The explicit immutable final image identity, including ordered rootfs identities.</param>
    /// <param name = "cancellationToken">Cancels the complete inspection and owned cleanup.</param>
    /// <returns>Passing observed evidence only after every declared check succeeds.</returns>
    /// <exception cref = "KeycloakSecurityException">An inspection or probe failed; no passing evidence is returned.</exception>
    public static async Task<VerifiedKeycloakImageEvidence> InspectAsync(CapturedKeycloakPlan plan, KeycloakInspectionImage image, CancellationToken cancellationToken = default) => await InspectCoreAsync(plan, image, cancellationToken).ConfigureAwait(false);
    internal static async Task<VerifiedKeycloakImageEvidence> InspectCoreAsync(CapturedKeycloakPlan plan, KeycloakInspectionImage image, CancellationToken cancellationToken = default, DockerInspectionCommand? command = null)
    {
        string? scratch = null;
        using var disk = TemporaryDiskBudget.BeginInvocation();
        using var events = KeycloakStageEvents.Begin(KeycloakStagePhase.Inspection, plan?.RecipeDigest, plan?.PolicyDigest, plan?.Mode.ToString(), plan?.Digest, "inspection");
        try
        {
            ArgumentNullException.ThrowIfNull(plan);
            using var accepted = plan.Snapshot.Enter();
            DockerInspection.Validate(image);
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This inspection profile requires Unix file modes.");
            Require(plan.Platform == image.Platform, "Final image platform differs from plan.");
            scratch = Path.Combine(PrivateTemporaryRoot(), "appsurface-inspect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(plan.Recipe.ResourceLimits.MaxOperationSeconds, 300)));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var inputs = await SnapshotStore.CopyForAcceptedUseAsync(plan.Snapshot, accepted, Path.Combine(scratch, "inputs"), stop.Token).ConfigureAwait(false);
            var prepared = PrepareContext(plan.Recipe, inputs, scratch, stop.Token);
            prepared = prepared with
            {
                ExpectedRepairedCliSha256 = plan.Recipe.Operations.OfType<KeycloakOperation.EmbeddedArchiveReplacementOperation>().SingleOrDefault()?.Replacement.ModuleSet?.ReplacementArtifact.Sha256
            };
            var expected = await ContextMaterialization.MaterializeAsync(prepared, Path.Combine(scratch, "expected"), stop.Token, new ContextMaterializationLimits(MaxAggregateBytes: plan.Recipe.ResourceLimits.MaxOutputBytes)).ConfigureAwait(false);
            var payloads = new HashSet<string>(["var/lib/dpkg/status", "etc/dpkg/dpkg.cfg.d/excludes", "etc/passwd"], StringComparer.Ordinal);
            var actual = await DockerInspection.ExportAsync(image, Path.Combine(scratch, "actual.tar"), payloads, stop.Token, command).ConfigureAwait(false);
            var files = actual.Entries.ToDictionary(x => x.Path, StringComparer.Ordinal);
            var checks = new List<KeycloakObservedCheck>();
            if (plan.Recipe.TemplateBaseline is { } baselineTemplate)
                checks.Add(new("template:reviewed-upstream-baseline", baselineTemplate.Sha256, baselineTemplate.Sha256));
            var expectedRoot = Path.Combine(expected.ContextPath, "rootfs");
            var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                stop.Token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(expectedRoot, path).Replace('\\', '/');
                expectedPaths.Add(relative);
                using var read = File.OpenRead(path);
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(read));
                events.SetContext(plan.Digest, "inspection", "file:" + relative);
                OciReader.ValidateExpectedFiles(files, new Dictionary<string, OciExpectedFile>(StringComparer.Ordinal) { [relative] = new(relative, "sha256:" + hash, Mode: (int)File.GetUnixFileMode(path), Uid: 1000, Gid: 0) });
                var observed = files[relative];
                checks.Add(new("file:" + relative, "sha256:" + hash, observed!.Sha256));
            }

            var expectedDirectories = Directory.EnumerateDirectories(expectedRoot, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(expectedRoot, x).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
            Require(!actual.Entries.Any(x => x.Path.StartsWith("opt/keycloak/", StringComparison.Ordinal) && !(x.Type == "file" && expectedPaths.Contains(x.Path)) && !(x.Type == "directory" && expectedDirectories.Contains(x.Path))), "Final application inventory contains undeclared files, links or directories.");
            foreach (var path in Directory.EnumerateDirectories(expectedRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(expectedRoot, path).Replace('\\', '/');
                if (relative == "opt")
                    continue;
                Require(files.TryGetValue(relative, out var observed) && observed.Type == "directory" && observed.Uid == 1000 && observed.Gid == 0 && observed.Mode == (int)File.GetUnixFileMode(path), "Final application directory differs.");
            }

            if (plan.Mode == KeycloakRecipeMode.ThemeOnly)
            {
                var source = plan.Recipe.SourceImage;
                var baselineTheme = new OciReader(OciReader.SourceCaptureLimits).Read(Material(inputs, source.ArchiveMaterialId!), new(null, source.PlatformManifest, image.Platform, source.ConfigDigest!, source.DiffIds), stop.Token);
                var baselineOutside = baselineTheme.Entries.Where(x => x.Path != "opt" && x.Path != "opt/keycloak" && !x.Path.StartsWith("opt/keycloak/", StringComparison.Ordinal)).ToDictionary(x => x.Path, StringComparer.Ordinal);
                var actualOutside = actual.Entries.Where(x => x.Path != "opt" && x.Path != "opt/keycloak" && !x.Path.StartsWith("opt/keycloak/", StringComparison.Ordinal)).ToDictionary(x => x.Path, StringComparer.Ordinal);
                Require(baselineOutside.Count == actualOutside.Count && baselineOutside.All(pair => actualOutside.TryGetValue(pair.Key, out var item) && item.Type == pair.Value.Type && item.Sha256 == pair.Value.Sha256 && item.Mode == pair.Value.Mode && item.Uid == pair.Value.Uid && item.Gid == pair.Value.Gid && item.LinkTarget == pair.Value.LinkTarget), "Theme-only composition changed the source filesystem outside the declared application tree.");
                Require(actual.ConfigUser == baselineTheme.ConfigUser && actual.ConfigEntrypoint.SequenceEqual(baselineTheme.ConfigEntrypoint) && actual.ConfigEnvironment.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(baselineTheme.ConfigEnvironment.OrderBy(x => x.Key, StringComparer.Ordinal)), "Theme-only composition changed source configuration.");
                checks.Add(new("scope:theme-only", "security customization not evaluated", "security customization not evaluated"));
                events.SetCounts(checkCount: checks.Count);
                events.SetContext(plan.Digest, operationId: "inspection");
                events.Complete(KeycloakEffectState.None);
                return new(plan.Digest, image, plan.Mode, checks, plan);
            }

            var runtime = plan.Recipe.Operations.OfType<KeycloakOperation.RuntimeProfileOperation>().Single().Profile;
            var runtimeImage = runtime.RuntimeOciIdentities.Single();
            var baseline = new OciReader().Read(Material(inputs, runtimeImage.ArchiveMaterialId!), new(null, runtimeImage.PlatformManifestDigest!, image.Platform, runtimeImage.ConfigDigest!, runtimeImage.DiffIds, PayloadPaths: payloads), stop.Token);
            var packages = runtime.RequiredPackages.Select(package =>
            {
                using var bytes = File.OpenRead(Material(inputs, package.MaterialId!));
                return DebPayloadReader.Read(bytes, package.Sha256, new(package.Name, package.Version, package.Architecture), stop.Token);
            }).ToArray();
            events.SetContext(plan.Digest, "inspection", "runtime:conformance");
            var runtimeResult = RuntimeConformance.Verify(baseline, actual, packages, runtime.RemovedPackages, RuntimeConformance.ReadPolicy(baseline, actual));
            checks.AddRange(runtimeResult.Packages.Select(x => new KeycloakObservedCheck("package:" + x.Package + ":" + x.Architecture, x.Version, x.Version)));
            checks.AddRange(runtimeResult.Payloads.Select(x => new KeycloakObservedCheck("payload:" + x.Path, x.Disposition, x.Disposition)));
            checks.Add(new("runtime:installed-inventory", runtimeResult.ActualStatusSha256, runtimeResult.ActualStatusSha256));
            foreach (var removed in runtime.RemovedPackages)
                checks.Add(new("removed:" + removed.Name, "absent", "absent"));
            checks.Add(new("profile:nonroot-certificates-helpers", "fixed-runtime-v1", "fixed-runtime-v1"));
            events.SetContext(plan.Digest, "inspection", "runtime:config");
            Require(actual.ConfigUser == "1000:0" && actual.ConfigEntrypoint.SequenceEqual([plan.Recipe.EntrypointPath]) && actual.ConfigEnvironment.GetValueOrDefault("KC_RUN_IN_CONTAINER") == "true", "Final config differs from profile.");
            events.SetContext(plan.Digest, "inspection", "runtime:probe");
            var probe = await DockerInspection.ProbeAsync(image, plan.Recipe.KeycloakVersion, stop.Token, command).ConfigureAwait(false);
            checks.Add(new("runtime:immutable-handoff", image.ManifestDigest, probe.Image));
            checks.Add(new("runtime:config", image.ConfigDigest, probe.Config));
            checks.Add(new("runtime:platform", plan.Platform, probe.Platform));
            events.SetCounts(checkCount: checks.Count);
            events.SetContext(plan.Digest, operationId: "inspection");
            events.Complete(KeycloakEffectState.None);
            return new(plan.Digest, image, plan.Mode, checks, plan);
        }
        catch (Exception ex)
        {
            var failure = KeycloakDiagnostics.Map(ex, KeycloakSafePhase.Inspection, events.CheckId ?? "inspection", KeycloakEffectState.None, events.CorrelationId);
            events.Reject(failure);
            throw failure;
        }
        finally
        {
            CleanOwned(scratch);
        }
    }
}
