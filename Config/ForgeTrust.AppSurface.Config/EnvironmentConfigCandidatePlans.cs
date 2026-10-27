using System.Collections.Immutable;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Identifies whether an environment candidate is scoped to an environment prefix.</summary>
public enum EnvironmentCandidateScope
{
    /// <summary>The candidate includes the environment name.</summary>
    Scoped,
    /// <summary>The candidate omits the environment name.</summary>
    Unscoped,
}

/// <summary>Describes one native environment variable name.</summary>
/// <param name="Name">The candidate name.</param><param name="Scope">Its lookup scope.</param>
/// <param name="IsLegacyAlias">Whether this is a compatibility alias.</param>
/// <param name="IsExplicitMapping">Whether this name comes from an explicit mapping.</param>
public sealed record EnvironmentNativeCandidate(string Name, EnvironmentCandidateScope Scope, bool IsLegacyAlias, bool IsExplicitMapping);

/// <summary>Immutable ordered native environment-name candidates for a logical key.</summary>
/// <remarks>
/// Constructor and <c>with</c> assignments copy the candidate collection. Plain record copies share only immutable
/// state. Names do not prove variable presence, native ownership, provider acceptance, or successful resolution.
/// </remarks>
/// <example><code>var copy = plan with { Candidates = suppliedCandidates };</code></example>
/// <seealso href="https://appsurface.dev/guides/config-logical-keys#environment-candidate-inspection">Candidate reference</seealso>
/// <param name="Key">The logical key inspected.</param><param name="Candidates">Ordered candidates.</param>
public sealed record EnvironmentNativeKeyPlan(AppSurfaceConfigKey Key, IReadOnlyList<EnvironmentNativeCandidate> Candidates)
{
    private readonly ImmutableArray<EnvironmentNativeCandidate> _candidates = Copy(Candidates);
    private readonly AppSurfaceConfigKey _key = EnvironmentConfigCandidateErrors.Require(Key, nameof(Key), nameof(EnvironmentNativeKeyPlan));
    /// <summary>Gets the inspected logical key.</summary>
    public AppSurfaceConfigKey Key { get => _key; init => _key = EnvironmentConfigCandidateErrors.Require(value, nameof(Key), nameof(EnvironmentNativeKeyPlan)); }
    /// <summary>Gets an immutable, independently copied candidate list.</summary>
    public IReadOnlyList<EnvironmentNativeCandidate> Candidates { get => _candidates; init => _candidates = Copy(value); }
    private static ImmutableArray<EnvironmentNativeCandidate> Copy(IReadOnlyList<EnvironmentNativeCandidate> value)
    {
        _ = EnvironmentConfigCandidateErrors.Require(value, nameof(Candidates), "CopyCandidates");
        var builder = ImmutableArray.CreateBuilder<EnvironmentNativeCandidate>(value.Count);
        foreach (var candidate in value) builder.Add(candidate ?? throw EnvironmentConfigCandidateErrors.NullEntry(nameof(Candidates), "CopyCandidates"));
        return builder.MoveToImmutable();
    }
}

/// <summary>Describes one direct composition-path environment name.</summary>
/// <param name="Name">The candidate name.</param><param name="Scope">Its lookup scope.</param>
/// <param name="IsHierarchical">Whether the name came from the hierarchical producer, even if its spelling also matches a flat producer.</param>
public sealed record EnvironmentCompositionPathCandidate(string Name, EnvironmentCandidateScope Scope, bool IsHierarchical);

/// <summary>Immutable ordered direct composition-path environment-name candidates.</summary>
/// <remarks>
/// Constructor and <c>with</c> assignments copy the candidate collection. The legacy dotted projection may collapse
/// distinct literal paths to the same names. This plan does not enumerate all descendants or certify source ownership.
/// </remarks>
/// <example><code>var (path, candidates) = plan with { };</code></example>
/// <seealso href="https://appsurface.dev/guides/config-logical-keys#environment-candidate-inspection">Candidate reference</seealso>
/// <param name="Path">The literal path inspected.</param><param name="Candidates">Ordered candidates.</param>
public sealed record EnvironmentCompositionPathPlan(AppSurfaceConfigKey Path, IReadOnlyList<EnvironmentCompositionPathCandidate> Candidates)
{
    private readonly ImmutableArray<EnvironmentCompositionPathCandidate> _candidates = Copy(Candidates);
    private readonly AppSurfaceConfigKey _path = EnvironmentConfigCandidateErrors.Require(Path, nameof(Path), nameof(EnvironmentCompositionPathPlan));
    /// <summary>Gets the inspected literal path.</summary>
    public AppSurfaceConfigKey Path { get => _path; init => _path = EnvironmentConfigCandidateErrors.Require(value, nameof(Path), nameof(EnvironmentCompositionPathPlan)); }
    /// <summary>Gets an immutable, independently copied candidate list.</summary>
    public IReadOnlyList<EnvironmentCompositionPathCandidate> Candidates { get => _candidates; init => _candidates = Copy(value); }
    private static ImmutableArray<EnvironmentCompositionPathCandidate> Copy(IReadOnlyList<EnvironmentCompositionPathCandidate> value)
    {
        _ = EnvironmentConfigCandidateErrors.Require(value, nameof(Candidates), "CopyCandidates");
        var builder = ImmutableArray.CreateBuilder<EnvironmentCompositionPathCandidate>(value.Count);
        foreach (var candidate in value) builder.Add(candidate ?? throw EnvironmentConfigCandidateErrors.NullEntry(nameof(Candidates), "CopyCandidates"));
        return builder.MoveToImmutable();
    }
}

/// <summary>Inspects possible environment names without reading or resolving values.</summary>
/// <remarks>
/// Registered as a singleton by <see cref="AppSurfaceConfigModule"/>. Configure mapping options before container
/// construction; the first built-in provider or inspector resolution freezes the shared mappings in that container.
/// Every call materializes immutable results and retains no per-key cache or native claims. Callers own the inventory,
/// comparer, relevant roots/ancestors/member paths and policy; generated names alone certify none of those choices.
/// </remarks>
/// <example><code>
/// var key = AppSurfaceConfigKey.FromSegments("Service", "Api-Key");
/// var typed = inspector.InspectNativeKey("Production", key);
/// var strict = inspector.InspectNativeKey("Production", "Service:Api-Key");
/// var composition = inspector.InspectCompositionPath("Production", key);
/// </code></example>
/// <seealso href="https://appsurface.dev/guides/config-logical-keys#environment-candidate-inspection">Quickstart, ordering and limits</seealso>
public interface IEnvironmentConfigCandidateInspector
{
    /// <summary>Plans native names with typed semantics, ignoring any parser provenance on the key.</summary>
    /// <param name="environment">Environment spelling, normalized by the existing native rules; non-null, including empty.</param>
    /// <param name="key">Immutable logical identity. An exact mapping replaces convention names; no legacy aliases are added.</param>
    /// <returns>A materialized scoped-then-unscoped plan with ordinal first-producer metadata.</returns>
    /// <exception cref="ArgumentNullException">The environment or key is null.</exception>
    /// <exception cref="InvalidOperationException">The key cannot be encoded and has no exact mapping.</exception>
    EnvironmentNativeKeyPlan InspectNativeKey(string environment, AppSurfaceConfigKey key);

    /// <summary>Plans native names using strict parsing and the existing compatibility aliases.</summary>
    /// <param name="environment">Environment spelling, normalized by the existing native rules; non-null, including empty.</param>
    /// <param name="strictInput">Strict colon-delimited logical key. Dots remain literal, independent of manager migration settings.</param>
    /// <returns>Canonical scoped/unscoped names followed by distinct strict aliases, or only the exact mapped pair.</returns>
    /// <exception cref="ArgumentNullException">The environment or strict input is null.</exception>
    /// <exception cref="FormatException">Strict parsing rejects the supplied syntax.</exception>
    /// <exception cref="InvalidOperationException">The parsed key cannot be encoded and has no exact mapping.</exception>
    EnvironmentNativeKeyPlan InspectNativeKey(string environment, string strictInput);

    /// <summary>Plans direct composition names using the legacy dotted projection of literal segments.</summary>
    /// <param name="environment">Environment spelling, normalized by the existing composition rules; non-null, including empty.</param>
    /// <param name="path">Literal path. Native mappings do not apply and ancestors/descendants are not inferred.</param>
    /// <returns>Distinct scoped flat, unscoped flat, scoped hierarchical, then unscoped hierarchical names.</returns>
    /// <exception cref="ArgumentNullException">The environment or path is null.</exception>
    EnvironmentCompositionPathPlan InspectCompositionPath(string environment, AppSurfaceConfigKey path);
}
