using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Planner;

/// <summary>Role of one immutable file in the first restricted native HTTP application bundle.</summary>
internal enum EvidenceClosedBundleRole
{
    /// <summary>Managed AppHost entry DLL.</summary>
    AppHost,
    /// <summary>AppHost runtime configuration.</summary>
    AppHostRuntimeConfiguration,
    /// <summary>Managed native HTTP resource DLL.</summary>
    Resource,
    /// <summary>Resource runtime configuration.</summary>
    ResourceRuntimeConfiguration,
    /// <summary>Executable Linux DCP host.</summary>
    Dcp,
    /// <summary>Pinned DCP extension beneath dcp/ext.</summary>
    DcpExtension,
    /// <summary>Read-only managed or native dependency.</summary>
    Dependency,
    /// <summary>Closed immutable application input.</summary>
    DeclaredInput,
}

/// <summary>Compile-owned bundle metadata; relative names never select an executable from a request.</summary>
/// <param name="RelativePath">Normalized ASCII path below the root-owned bundle.</param>
/// <param name="Role">Closed payload role.</param>
/// <param name="LengthBytes">Exact positive file length, at most 128 MiB.</param>
/// <param name="Sha256">Exact lower-case SHA-256 of the independently inspected file.</param>
/// <param name="Mode">Read-only Unix permission bits: 0444, or 0555 for native executables.</param>
internal sealed record EvidenceClosedBundleFile(
    string RelativePath, EvidenceClosedBundleRole Role, long LengthBytes, string Sha256, uint Mode);

/// <summary>Complete resource declaration and its closed native HTTP adapter identity.</summary>
/// <param name="Declaration">Every policy readiness/dependency/deadline field.</param>
/// <param name="CapabilityClass">Exactly native-http-uds for this prerequisite.</param>
/// <param name="CapabilityVersion">Exactly 1.0.0 for this prerequisite.</param>
/// <param name="ResourceName">Exactly native-http in the closed AppHost topology.</param>
internal sealed record EvidenceClosedResourceRegistration(
    EvidenceResourceDeclaration Declaration, string CapabilityClass, string CapabilityVersion, string ResourceName);

/// <summary>Complete producer declaration and compile-owned implementation identity.</summary>
/// <param name="Declaration">Every producer, assertion, artifact, gate and dependency field.</param>
/// <param name="ImplementationId">Exactly coverage for the first adapter.</param>
/// <param name="ImplementationVersion">Exactly 1.0.0 for the first adapter.</param>
internal sealed record EvidenceClosedProducerRegistration(
    EvidenceProducerDeclaration Declaration, string ImplementationId, string ImplementationVersion);

/// <summary>Finite secret-free grants for the first native HTTP application; only private network and scratch apply.</summary>
/// <param name="ReadOnlyInputs">Exact declared-input bundle names; no ambient or absolute path grant.</param>
/// <param name="ScratchBytes">Positive writable-scratch allowance, at most 1 GiB.</param>
/// <param name="MemoryBytes">Positive memory allowance, at most 1 GiB.</param>
/// <param name="MaximumTasks">Positive process/thread allowance, at most 64.</param>
/// <param name="MaximumOutputBytes">Positive received-output allowance, at most 1 MiB.</param>
/// <param name="StartSeconds">Positive application startup cap, at most 120 seconds.</param>
/// <param name="StoppingSeconds">Positive fresh stop cap, at most 30 seconds.</param>
/// <remarks>Private network, zero secrets, no privileged groups and no protected/result-root projection are implicit.</remarks>
internal sealed record EvidenceClosedApplicationCapabilities(
    IReadOnlyList<string> ReadOnlyInputs, long ScratchBytes, long MemoryBytes, int MaximumTasks,
    long MaximumOutputBytes, int StartSeconds, int StoppingSeconds);

/// <summary>Root-selected identities to be checked by the future authenticated v2 descriptor adapter.</summary>
/// <param name="WorkerUid">Protected nonroot worker UID.</param>
/// <param name="WorkerGid">Protected worker primary GID.</param>
/// <param name="ProducerUid">Separate restricted producer UID.</param>
/// <param name="ProducerGid">Producer primary GID.</param>
/// <param name="ApplicationUid">Separate application/DCP/resource UID.</param>
/// <param name="ApplicationGid">Application primary GID.</param>
/// <param name="ResultsGid">Producer-results group; never granted to the application.</param>
/// <param name="ResourceAccessGid">Separate resource-access group; conveys no result-root access.</param>
/// <remarks>All UIDs are positive and distinct; all five GIDs are positive and distinct. This is metadata, not a lease.</remarks>
internal sealed record EvidenceClosedApplicationIdentities(
    uint WorkerUid, uint WorkerGid, uint ProducerUid, uint ProducerGid,
    uint ApplicationUid, uint ApplicationGid, uint ResultsGid, uint ResourceAccessGid);

/// <summary>Compile-owned definition; contains data only, never a delegate, host path, argument vector or proof.</summary>
/// <param name="Id">Closed application ID.</param>
/// <param name="Version">Closed application version.</param>
/// <param name="BuildId">Immutable reviewed build identity, separately pinned to finished binaries by the root parent.</param>
/// <param name="AspireSdkVersion">Exactly 13.4.4.</param>
/// <param name="Policy">Complete protected policy, including unselected profiles and rules.</param>
/// <param name="ProfileId">Exact selected resource-backed profile.</param>
/// <param name="Resources">Complete closed resource registrations.</param>
/// <param name="Producers">Complete closed producer registrations.</param>
/// <param name="BundleFiles">Exact immutable bundle inventory, including DCP and executable modes.</param>
/// <param name="Capabilities">Finite application grants.</param>
internal sealed record EvidenceClosedApplicationDefinition(
    string Id, string Version, string BuildId, string AspireSdkVersion, EvidencePolicy Policy, string ProfileId,
    IReadOnlyList<EvidenceClosedResourceRegistration> Resources,
    IReadOnlyList<EvidenceClosedProducerRegistration> Producers,
    IReadOnlyList<EvidenceClosedBundleFile> BundleFiles, EvidenceClosedApplicationCapabilities Capabilities);

/// <summary>Observed metadata from a future authenticated root descriptor; none of it populates the compiled table.</summary>
/// <param name="ApplicationId">Root-selected compiled application ID.</param>
/// <param name="ApplicationVersion">Root-selected version.</param>
/// <param name="BuildId">Root-selected immutable build identity.</param>
/// <param name="CatalogueDigest">Independently expected canonical catalogue digest.</param>
/// <param name="EntryDigest">Independently expected canonical entry digest.</param>
/// <param name="Provider">Exactly github-actions.</param>
/// <param name="Platform">Exactly linux-x64.</param>
/// <param name="WorkerProtocol">Exactly evidence-worker-linux-v2.</param>
/// <param name="Resources">Complete actual registered resource declarations.</param>
/// <param name="Producers">Complete actual registered producer declarations.</param>
/// <param name="BundleFiles">Root-inspected bundle bytes/modes inventory.</param>
/// <param name="Capabilities">Root-selected exact grants.</param>
/// <param name="Identities">Actual separate UID/GID map.</param>
internal sealed record EvidenceClosedApplicationBinding(
    string ApplicationId, string ApplicationVersion, string BuildId, string CatalogueDigest, string EntryDigest,
    string Provider, string Platform, string WorkerProtocol,
    IReadOnlyList<EvidenceResourceDeclaration> Resources, IReadOnlyList<EvidenceProducerDeclaration> Producers,
    IReadOnlyList<EvidenceClosedBundleFile> BundleFiles, EvidenceClosedApplicationCapabilities Capabilities,
    EvidenceClosedApplicationIdentities Identities);

/// <summary>Audits and resolves a closed immutable catalogue without granting admission or launching work.</summary>
/// <remarks>
/// Production has no entries. Only a same-assembly compile-owned partial implementation may register data.
/// Candidate audit APIs are pure structural checks: passing one cannot register an entry, create a runtime
/// capability, accept a consumer proof, or bypass real Connect/admission/allocation. Arrays in snapshots are
/// copied and wrapped read-only, including every nested policy/declaration list.
/// </remarks>
internal static partial class EvidenceClosedApplicationCatalogue
{
    /// <summary>Maximum separately compiled application definitions.</summary>
    internal const int MaximumEntries = 8;
    /// <summary>Maximum immutable bundle files per definition.</summary>
    internal const int MaximumBundleFiles = 256;
    private const long MaximumFileBytes = 128L * 1024 * 1024;
    private const long MaximumBundleBytes = 512L * 1024 * 1024;
    private static readonly IReadOnlyList<EvidenceClosedApplicationDefinition> CompiledEntries = BuildCompiledEntries();

    /// <summary>Resolves only compile-owned entries; production always rejects with ASEVD407 while the table is empty.</summary>
    /// <param name="policy">Independently resolved protected policy.</param>
    /// <param name="plan">Exact planner result for the protected changed paths.</param>
    /// <param name="binding">Authenticated root observations, never admission authority themselves.</param>
    /// <returns>Read-only definition metadata; not an admission or application lease.</returns>
    internal static EvidenceClosedApplicationDefinition Resolve(
        EvidencePolicy policy, EvidencePlan plan, EvidenceClosedApplicationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var entry = CompiledEntries.SingleOrDefault(item => item.Id == binding.ApplicationId);
        if (entry is null)
        {
            throw new EvidenceAdmissionException("ASEVD407", "No compiled restricted application registration is available.");
        }
        VerifyCandidateBinding(entry, ComputeCatalogueDigest(CompiledEntries), policy, plan, binding);
        return entry;
    }

    /// <summary>Validates and defensively snapshots candidate metadata; cannot enroll it in Resolve.</summary>
    /// <param name="definition">Candidate data from source/build review.</param>
    /// <returns>A deep read-only snapshot whose canonical digest includes all declarations and grants.</returns>
    internal static EvidenceClosedApplicationDefinition Snapshot(EvidenceClosedApplicationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Require(Id(definition.Id) && Id(definition.Version) && Id(definition.BuildId) && Id(definition.ProfileId));
        Require(definition.AspireSdkVersion == "13.4.4");
        ValidatePolicyBounds(definition.Policy);
        try { EvidencePlanner.ValidatePolicy(definition.Policy); }
        catch (EvidencePlanningException) { throw Invalid(); }
        var profile = definition.Policy.Profiles.SingleOrDefault(item => item.Id == definition.ProfileId);
        Require(profile is not null && profile.Resources.Count == 1 && profile.Producers.Count > 0);
        Require(Count(definition.Resources, 1) && definition.Resources.Count == 1);
        Require(Count(definition.Producers, EvidenceProfileLimits.MaximumProducers));
        foreach (var resource in definition.Resources)
        {
            Require(resource is not null && resource.Declaration is not null);
            Require(resource.CapabilityClass == "native-http-uds" && resource.CapabilityVersion == "1.0.0"
                && resource.ResourceName == "native-http" && resource.Declaration.Readiness == "aspire_health"
                && resource.Declaration.Requires is { Count: 0 });
        }
        foreach (var producer in definition.Producers)
        {
            Require(producer is not null && producer.Declaration is not null);
            Require(producer.ImplementationId == "coverage" && producer.ImplementationVersion == "1.0.0"
                && producer.Declaration.Kind == producer.ImplementationId && producer.Declaration.Version == producer.ImplementationVersion);
        }
        ValidatePolicyBounds(definition.Policy with { Profiles = [profile! with
        {
            Resources = definition.Resources.Select(static item => item.Declaration).ToArray(),
            Producers = definition.Producers.Select(static item => item.Declaration).ToArray(),
        }], Rules = [] });
        Require(SameDeclarations(definition.Resources.Select(static item => item.Declaration).ToArray(), profile!.Resources, static item => item.Id));
        Require(SameDeclarations(definition.Producers.Select(static item => item.Declaration).ToArray(), profile.Producers, static item => item.Id));
        ValidateBundle(definition.BundleFiles);
        ValidateCapabilities(definition.Capabilities);
        Require(definition.BundleFiles.Where(static item => item.Role == EvidenceClosedBundleRole.DeclaredInput)
            .Select(static item => item.RelativePath).Order(StringComparer.Ordinal)
            .SequenceEqual(definition.Capabilities.ReadOnlyInputs.Order(StringComparer.Ordinal), StringComparer.Ordinal));
        var snapshot = definition with
        {
            Policy = FreezePolicy(definition.Policy),
            Resources = ReadOnly(definition.Resources.OrderBy(static item => item.Declaration.Id, StringComparer.Ordinal)
                .Select(static item => item with { Declaration = FreezeResource(item.Declaration) })),
            Producers = ReadOnly(definition.Producers.OrderBy(static item => item.Declaration.Id, StringComparer.Ordinal)
                .Select(static item => item with { Declaration = FreezeProducer(item.Declaration) })),
            BundleFiles = ReadOnly(definition.BundleFiles.OrderBy(static item => item.RelativePath, StringComparer.Ordinal)),
            Capabilities = definition.Capabilities with { ReadOnlyInputs = ReadOnly(definition.Capabilities.ReadOnlyInputs.Order(StringComparer.Ordinal)) },
        };
        Require(EvidenceCanonicalJson.Serialize(snapshot).Length <= 1024 * 1024);
        return snapshot;
    }

    /// <summary>Hashes the complete validated canonical entry; raw bundle bytes are independently inspected by root.</summary>
    /// <param name="definition">Complete candidate definition to validate and snapshot.</param>
    /// <returns>Lower-case SHA-256 of the canonical snapshot.</returns>
    internal static string ComputeEntryDigest(EvidenceClosedApplicationDefinition definition) => EvidenceDigest.CanonicalSha256(Snapshot(definition));

    /// <summary>Hashes at most eight unique entries in ordinal ID order, including a fixed catalogue schema.</summary>
    /// <param name="definitions">Candidate source data; this API cannot populate the compiled registry.</param>
    /// <returns>The structural digest of the complete ordered catalogue.</returns>
    internal static string ComputeCatalogueDigest(IReadOnlyList<EvidenceClosedApplicationDefinition> definitions)
    {
        Require(Count(definitions, MaximumEntries));
        var entries = definitions.Select(Snapshot).OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();
        Require(entries.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() == entries.Length);
        Require(entries.Select(static item => (EvidenceDigest.CanonicalSha256(item.Policy), item.ProfileId)).Distinct().Count() == entries.Length);
        return EvidenceDigest.CanonicalSha256(new { Schema = "evidence-closed-application-catalogue-v1", Entries = entries });
    }

    /// <summary>Pure audit of exact candidate/binding correspondence; does not issue a resolution or admission capability.</summary>
    /// <param name="definition">Candidate immutable definition to audit.</param>
    /// <param name="catalogueDigest">Independently computed complete candidate catalogue digest.</param>
    /// <param name="policy">Complete protected policy.</param>
    /// <param name="plan">Independently resolved plan.</param>
    /// <param name="binding">Observed complete registrations, bundle metadata, grants and identities.</param>
    internal static void VerifyCandidateBinding(EvidenceClosedApplicationDefinition definition, string catalogueDigest,
        EvidencePolicy policy, EvidencePlan plan, EvidenceClosedApplicationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var entry = Snapshot(definition);
        ValidatePolicyBounds(policy);
        Require(plan is not null && plan.ChangedPaths is not null && plan.ChangedPaths.Count <= 4096);
        EvidencePlan resolved;
        try { resolved = new EvidencePlanner().Resolve(policy, plan!.ChangedPaths); }
        catch (EvidencePlanningException) { throw Invalid(); }
        Require(Equal(entry.Policy, policy) && Equal(resolved, plan) && plan.Profile.Id == entry.ProfileId);
        Require(binding.ApplicationId == entry.Id && binding.ApplicationVersion == entry.Version && binding.BuildId == entry.BuildId);
        Require(Hash(catalogueDigest) && binding.CatalogueDigest == catalogueDigest && binding.EntryDigest == ComputeEntryDigest(entry));
        Require(binding.Provider == "github-actions" && binding.Platform == "linux-x64" && binding.WorkerProtocol == "evidence-worker-linux-v2");
        Require(Count(binding.Resources, EvidenceProfileLimits.MaximumResources) && Count(binding.Producers, EvidenceProfileLimits.MaximumProducers));
        ValidatePolicyBounds(entry.Policy with { Profiles = [plan.Profile with { Resources = binding.Resources, Producers = binding.Producers }], Rules = [] });
        Require(SameDeclarations(binding.Resources, entry.Resources.Select(static item => item.Declaration).ToArray(), static item => item.Id));
        Require(SameDeclarations(binding.Producers, entry.Producers.Select(static item => item.Declaration).ToArray(), static item => item.Id));
        ValidateBundle(binding.BundleFiles);
        Require(Equal(binding.BundleFiles.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToArray(), entry.BundleFiles));
        ValidateCapabilities(binding.Capabilities);
        Require(Equal(binding.Capabilities with { ReadOnlyInputs = ReadOnly(binding.Capabilities.ReadOnlyInputs.Order(StringComparer.Ordinal)) }, entry.Capabilities));
        ValidateIdentities(binding.Identities);
    }

    /// <summary>Compile-only seam: a separately built partial source may add immutable data, never runtime callbacks.</summary>
    static partial void RegisterCompiledEntries(List<EvidenceClosedApplicationDefinition> entries);

    private static IReadOnlyList<EvidenceClosedApplicationDefinition> BuildCompiledEntries()
    {
        var entries = new List<EvidenceClosedApplicationDefinition>();
        RegisterCompiledEntries(entries);
        _ = ComputeCatalogueDigest(entries);
        return ReadOnly(entries.Select(Snapshot));
    }

    private static void ValidateBundle(IReadOnlyList<EvidenceClosedBundleFile> files)
    {
        Require(Count(files, MaximumBundleFiles) && files.Count >= 6);
        Require(files.All(static item => item is not null && Relative(item.RelativePath)));
        Require(files.Select(static item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Count);
        long total = 0;
        foreach (var item in files)
        {
            Require(Relative(item.RelativePath) && Enum.IsDefined(item.Role) && Hash(item.Sha256)
                && item.LengthBytes is > 0 and <= MaximumFileBytes && item.LengthBytes <= MaximumBundleBytes - total);
            total += item.LengthBytes;
            Require(item.Mode == 0x124 || item.Mode == 0x16d);
            Require(item.Mode == 0x124 || item.Role is EvidenceClosedBundleRole.Dcp or EvidenceClosedBundleRole.DcpExtension);
            if (item.Role == EvidenceClosedBundleRole.Dcp) Require(item.RelativePath == "dcp/dcp" && item.Mode == 0x16d);
            if (item.Role == EvidenceClosedBundleRole.DcpExtension) Require(item.RelativePath.StartsWith("dcp/ext/", StringComparison.Ordinal) && item.Mode == 0x16d);
            if (item.Role is EvidenceClosedBundleRole.AppHost or EvidenceClosedBundleRole.Resource) Require(item.RelativePath.EndsWith(".dll", StringComparison.Ordinal));
            if (item.Role is EvidenceClosedBundleRole.AppHostRuntimeConfiguration or EvidenceClosedBundleRole.ResourceRuntimeConfiguration)
                Require(item.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));
            Require(!files.Any(other => other != item && other.RelativePath.StartsWith(item.RelativePath + "/", StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var role in new[] { EvidenceClosedBundleRole.AppHost, EvidenceClosedBundleRole.AppHostRuntimeConfiguration,
            EvidenceClosedBundleRole.Resource, EvidenceClosedBundleRole.ResourceRuntimeConfiguration, EvidenceClosedBundleRole.Dcp, EvidenceClosedBundleRole.DeclaredInput })
            Require(files.Count(item => item.Role == role) == 1);
    }

    private static void ValidateCapabilities(EvidenceClosedApplicationCapabilities value)
    {
        Require(value is not null && Count(value.ReadOnlyInputs, 1) && value.ReadOnlyInputs.Count == 1 && value.ReadOnlyInputs.All(Relative)
            && value.ScratchBytes is > 0 and <= 1024L * 1024 * 1024 && value.MemoryBytes is > 0 and <= 1024L * 1024 * 1024
            && value.MaximumTasks is > 0 and <= 64 && value.MaximumOutputBytes is > 0 and <= 1024 * 1024
            && value.StartSeconds is > 0 and <= 120 && value.StoppingSeconds is > 0 and <= 30);
    }

    private static void ValidateIdentities(EvidenceClosedApplicationIdentities value)
    {
        Require(value is not null);
        var uids = new[] { value.WorkerUid, value.ProducerUid, value.ApplicationUid };
        var gids = new[] { value.WorkerGid, value.ProducerGid, value.ApplicationGid, value.ResultsGid, value.ResourceAccessGid };
        Require(uids.All(static item => item > 0) && uids.Distinct().Count() == uids.Length
            && gids.All(static item => item > 0) && gids.Distinct().Count() == gids.Length);
    }

    private static void ValidatePolicyBounds(EvidencePolicy policy)
    {
        Require(policy is not null && Id(policy.Id) && Id(policy.Version) && Id(policy.ConservativeProfileId)
            && Count(policy.Profiles, 32) && Count(policy.Rules, 128));
        foreach (var rule in policy.Rules) Require(rule is not null && Id(rule.Id) && Text(rule.Pattern, 256) && Id(rule.ProfileId));
        foreach (var profile in policy.Profiles)
        {
            Require(profile is not null && Id(profile.Id) && Enum.IsDefined(profile.Scope)
                && Count(profile.Resources, EvidenceProfileLimits.MaximumResources) && Count(profile.Producers, EvidenceProfileLimits.MaximumProducers)
                && Count(profile.Obligations, EvidenceProfileLimits.MaximumObligations));
            foreach (var resource in profile.Resources)
                Require(resource is not null && Id(resource.Id) && Text(resource.Readiness, 128) && resource.DeadlineSeconds is > 0 and <= 120
                    && Count(resource.Requires, 16) && resource.Requires.All(Id) && resource.Requires.Distinct(StringComparer.Ordinal).Count() == resource.Requires.Count);
            foreach (var producer in profile.Producers)
            {
                Require(producer is not null && Id(producer.Id) && Id(producer.Kind) && Id(producer.Version) && producer.TimeoutSeconds is > 0 and <= 600
                    && Count(producer.RequiredResources, 16) && producer.RequiredResources.All(Id) && Count(producer.AssertionIds, 128)
                    && producer.AssertionIds.All(static item => Text(item, 128)) && Count(producer.ArtifactSlots, 128));
                Require(producer.RequiredResources.Distinct(StringComparer.Ordinal).Count() == producer.RequiredResources.Count
                    && producer.AssertionIds.Distinct(StringComparer.Ordinal).Count() == producer.AssertionIds.Count);
                foreach (var slot in producer.ArtifactSlots)
                    Require(slot is not null && Id(slot.LogicalName) && Relative(slot.RelativeRoot) && Text(slot.MediaType, 128)
                        && slot.MaximumBytes is >= 0 and <= 256L * 1024 * 1024);
            }
            foreach (var obligation in profile.Obligations)
                Require(obligation is not null && Id(obligation.Id) && Text(obligation.RiskClass, 128) && Text(obligation.Rationale, 4096)
                    && Text(obligation.RequiredAssertionId, 128) && Count(obligation.RequiredProducerIds, 32) && obligation.RequiredProducerIds.All(Id));
        }
    }

    private static bool SameDeclarations<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, string> id) where T : class =>
        left.All(static item => item is not null) && right.All(static item => item is not null)
        && left.Count == right.Count && left.Select(id).Distinct(StringComparer.Ordinal).Count() == left.Count
        && Equal(left.OrderBy(id, StringComparer.Ordinal).ToArray(), right.OrderBy(id, StringComparer.Ordinal).ToArray());

    private static EvidencePolicy FreezePolicy(EvidencePolicy policy) => policy with
    {
        Rules = ReadOnly(policy.Rules),
        Profiles = ReadOnly(policy.Profiles.Select(static profile => profile with
        {
            Resources = ReadOnly(profile.Resources.Select(FreezeResource)),
            Producers = ReadOnly(profile.Producers.Select(FreezeProducer)),
            Obligations = ReadOnly(profile.Obligations.Select(static item => item with { RequiredProducerIds = ReadOnly(item.RequiredProducerIds) })),
        })),
    };

    private static EvidenceResourceDeclaration FreezeResource(EvidenceResourceDeclaration value) => value with { Requires = ReadOnly(value.Requires) };
    private static EvidenceProducerDeclaration FreezeProducer(EvidenceProducerDeclaration value) => value with
    {
        RequiredResources = ReadOnly(value.RequiredResources), AssertionIds = ReadOnly(value.AssertionIds), ArtifactSlots = ReadOnly(value.ArtifactSlots),
    };
    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static bool Count<T>(IReadOnlyList<T>? values, int maximum) => values is not null && values.Count <= maximum;
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool Id(string? value) => Text(value, 128) && value!.All(static item => char.IsAsciiLetterOrDigit(item) || item is '-' or '_' or '.');
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(static item => item is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Relative(string? value) => Text(value, 256) && value![0] != '/' && value.All(static item => char.IsAsciiLetterOrDigit(item) || item is '-' or '_' or '.' or '/')
        && value.Split('/').All(static part => part.Length > 0 && part is not "." and not "..");
    private static bool Equal<TLeft, TRight>(TLeft left, TRight right) => EvidenceCanonicalJson.Serialize(left).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(right));
    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw Invalid(); }
    private static EvidenceAdmissionException Invalid() => new("ASEVD404", "Closed application metadata is invalid or does not exactly match its registration.");
}
