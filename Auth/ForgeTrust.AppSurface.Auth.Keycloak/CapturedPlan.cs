using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>A read-only captured plan whose accepted operations own their private verified input copies.</summary>
/// <remarks>Disposal prevents new consumers. It never deletes the caller-owned snapshot; remove it only after all operations return.</remarks>
public sealed class CapturedKeycloakPlan : IDisposable
{
    internal SnapshotHandle Snapshot { get; }
    internal KeycloakSecurityRecipe Recipe { get; }
    internal KeycloakMaterialTrustPolicy Policy { get; }

    internal CapturedKeycloakPlan(SnapshotHandle snapshot, KeycloakSecurityRecipe recipe, KeycloakMaterialTrustPolicy policy)
    {
        (Snapshot, Recipe, Policy) = (snapshot, recipe, policy);
    }

    /// <summary>The canonical content identity including recipe, host policy, and captured bytes.</summary>
    public string Digest => Snapshot.Digest;
    /// <summary>The canonical recipe content identity.</summary>
    public string RecipeDigest => Snapshot.RecipeDigest;
    /// <summary>The host policy content identity accepted during capture.</summary>
    public string PolicyDigest => Snapshot.PolicyDigest;
    /// <summary>The explicit target image platform.</summary>
    public string Platform => Recipe.SourceImage.Platform;
    /// <summary>The declared inspection scope.</summary>
    public KeycloakRecipeMode Mode => Recipe.Mode;

    /// <summary>Prevents new consumers while preserving accepted operations and caller-owned files.</summary>
    public void Dispose() => Snapshot.Dispose();
}

/// <summary>Captures supported recipe inputs and their independent host authorizations.</summary>
public static partial class KeycloakSecurity
{
    /// <summary>Captures approved exact inputs into a fresh caller-owned directory in one attempt.</summary>
    /// <param name = "recipe">The explicit closed recipe including all source, runtime, profile and material pins.</param>
    /// <param name = "trustPolicy">The separately approved host policy; the recipe cannot broaden it.</param>
    /// <param name = "inputs">Explicit host-captured material bindings. Local paths are excluded from identity.</param>
    /// <param name = "ownedDirectory">An absent snapshot directory on a supported local filesystem.</param>
    /// <param name = "cancellationToken">Cancels the complete invocation, including acquisition and verification.</param>
    /// <returns>A handle only after all bytes, graph identities and provenance checks pass.</returns>
    /// <exception cref = "KeycloakSecurityException">Capture failed without a passing plan.</exception>
    public static async Task<CapturedKeycloakPlan> CaptureAsync(KeycloakSecurityRecipe recipe, KeycloakMaterialTrustPolicy trustPolicy, KeycloakCaptureInputs inputs, string ownedDirectory, CancellationToken cancellationToken = default)
    {
        string? stage = null;
        using var disk = TemporaryDiskBudget.BeginInvocation();
        using var events = KeycloakStageEvents.Begin(KeycloakStagePhase.Capture, operationId: "capture");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This capture profile requires Unix file modes.");
            KeycloakRecipeValidation.Validate(recipe);
            KeycloakRecipeValidation.Validate(trustPolicy);
            KeycloakRecipeValidation.Validate(inputs);
            var recipeBytes = KeycloakCanonicalJson.Write(recipe);
            var policyBytes = KeycloakCanonicalJson.Write(trustPolicy);
            var recipeDigest = Hash(recipeBytes);
            var policyDigest = Hash(policyBytes);
            events.SetIdentity(recipeDigest, policyDigest, recipe.SourceProfiles.FirstOrDefault());
            Require(inputs.ReviewedRecipeId == recipeDigest, "Capture bindings do not name the reviewed recipe.");
            var bindings = inputs.LocalInputs.ToDictionary(x => x.MaterialId, StringComparer.Ordinal);
            Require(bindings.Count == inputs.LocalInputs.Length && bindings.Keys.All(x => recipe.CapturedHostInputs.Contains(x, StringComparer.Ordinal)), "Undeclared local material binding.");
            var destination = Path.GetFullPath(ownedDirectory);
            OciLayout.RequireNoLinks(destination);
            Require(!Path.Exists(destination), "Capture destination must be absent.");
            var parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            stage = Path.Combine(parent, ".capture-inputs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(recipe.ResourceLimits.MaxOperationSeconds, 300)));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
            var files = new List<CaptureFile>();
            long total = 0;
            foreach (var material in recipe.MaterialReferences)
            {
                stop.Token.ThrowIfCancellationRequested();
                Require(trustPolicy.MaterialSha256ByIdentity.TryGetValue(material.Id, out var approved) && approved == material.Sha256, "Host policy does not approve the material digest.");
                string path;
                if (material.CapturedHostInput || bindings.ContainsKey(material.Id))
                {
                    if (!material.CapturedHostInput)
                        VerifyAuthorizedUri(material.ExactHttpsUri, trustPolicy);
                    Require(bindings.TryGetValue(material.Id, out var binding) && binding.Sha256 == material.Sha256 && binding.MaxBytes <= material.MaxBytes, "Local material binding differs.");
                    path = Path.GetFullPath(binding!.LocalPath);
                    OciLayout.RequireNoLinks(path);
                }
                else
                {
                    var uri = VerifyAuthorizedUri(material.ExactHttpsUri, trustPolicy);
                    path = Path.Combine(stage, material.Id);
                    // Explicit declared size is a ceiling for local capture and exact length for downloaded material.
                    await MaterialAcquisition.DownloadAsync(new(material.Id, material.Sha256, material.MaxBytes, [uri], material.Id, ExactLength: false), path, TimeSpan.FromSeconds(Math.Min(recipe.ResourceLimits.MaxOperationSeconds, 300)), stop.Token).ConfigureAwait(false);
                }

                var length = new FileInfo(path).Length;
                Require(length >= 0 && length <= material.MaxBytes && length <= recipe.ResourceLimits.MaxMaterialBytes && (!bindings.TryGetValue(material.Id, out var boundInput) || length <= boundInput.MaxBytes), "Material exceeds its declared size.");
                total = checked(total + length);
                Require(total <= 2L * 1024 * 1024 * 1024, "Acquisition aggregate exceeds profile.");
                files.Add(new(material.Id, path, material.Sha256, length, 420));
            }

            Require(files.Count is > 0 and <= 256, "Material count exceeds profile.");
            events.SetCounts(materialCount: files.Count, materialBytes: total);
            // All verification runs on verified private copies, never on mutable workspace paths.
            var verified = Path.Combine(stage, "verified");
            using var copied = await SnapshotStore.CaptureAsync(files, recipeDigest, policyDigest, verified, stop.Token).ConfigureAwait(false);
            VerifyCapturedInputs(recipe, trustPolicy, verified, stop.Token);
            var sealedFiles = copied.Files.Select(x => new CaptureFile(x.Id, Path.Combine(verified, x.Id), x.Sha256, x.Length, x.Mode));
            var snapshot = await SnapshotStore.CaptureAsync(sealedFiles, recipeDigest, policyDigest, destination, stop.Token).ConfigureAwait(false);
            var result = new CapturedKeycloakPlan(snapshot, recipe, trustPolicy);
            events.SetContext(result.Digest, operationId: "capture");
            events.Complete(KeycloakEffectState.Committed);
            return result;
        }
        catch (Exception ex) when (ex is not StackOverflowException)
        {
            var failure = KeycloakDiagnostics.Map(ex, KeycloakSafePhase.Capture, "capture", KeycloakEffectState.None, events.CorrelationId);
            events.Reject(failure);
            throw failure;
        }
        finally
        {
            CleanOwned(stage);
        }
    }

    private static Uri VerifyAuthorizedUri(string value, KeycloakMaterialTrustPolicy policy)
    {
        var exact = KeycloakCanonicalization.CanonicalizeHttpsUri(value);
        var uri = new Uri(exact);
        var origin = uri.GetLeftPart(UriPartial.Authority) + "/";
        Require(policy.PermittedHttpsOrigins.Any(x => KeycloakCanonicalization.CanonicalizeHttpsUri(x) == origin), "Material origin is not approved.");
        Require(policy.PermittedPathMappings.Any(x => WithinNamespace(exact, KeycloakCanonicalization.CanonicalizeHttpsUri(x))), "Material path is not approved.");
        return uri;
    }

    internal static string PrivateTemporaryRoot()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS())
        {
            if (path.StartsWith("/var/", StringComparison.Ordinal))
                return "/private" + path;
            if (path is "/tmp" or "/tmp/")
                return "/private/tmp";
        }

        return path;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool WithinNamespace(string exact, string mapping) => exact == mapping || exact.StartsWith(mapping.TrimEnd('/') + "/", StringComparison.Ordinal);
    internal static void Require(bool valid, string message)
    {
        if (!valid)
            throw new InvalidDataException(message);
    }

    internal static void CleanOwned(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
