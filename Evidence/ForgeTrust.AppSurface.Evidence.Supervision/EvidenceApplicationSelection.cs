using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Root-only selection of a compiled definition, independently resolved policy and actual accounts.</summary>
/// <remarks>Only this factory selects compiled registrations. Neither a candidate audit nor descriptor JSON
/// constructs this holder. Native bundle ownership and process admission remain separate obligations.</remarks>
internal sealed class EvidenceApplicationSelection
{
    private EvidenceApplicationSelection(EvidenceClosedApplicationDefinition definition, string catalogueDigest,
        EvidencePlan plan, EvidenceClosedApplicationIdentities identities)
    {
        Definition = definition; Plan = plan;
        EntryDigest = EvidenceClosedApplicationCatalogue.ComputeEntryDigest(definition);
        Descriptor = new(definition.Id, definition.Version, definition.BuildId, catalogueDigest, EntryDigest,
            definition.AspireSdkVersion, Array.AsReadOnly(definition.Resources.Select(x => x.Declaration).ToArray()),
            Array.AsReadOnly(definition.Producers.Select(x => x.Declaration).ToArray()),
            Array.AsReadOnly(definition.BundleFiles.Select(x => new EvidenceLinuxApplicationBundleFile(x.RelativePath,
                (EvidenceLinuxApplicationBundleRole)x.Role, x.LengthBytes, x.Sha256, x.Mode)).ToArray()),
            new(definition.Capabilities.ReadOnlyInputs, definition.Capabilities.ScratchBytes, definition.Capabilities.MemoryBytes,
                definition.Capabilities.MaximumTasks, definition.Capabilities.MaximumOutputBytes, definition.Capabilities.StartSeconds,
                definition.Capabilities.StoppingSeconds), identities.ApplicationUid, identities.ApplicationGid,
            identities.ResultsGid, identities.ResourceAccessGid);
    }

    /// <summary>Gets the immutable compile-owned registration.</summary>
    internal EvidenceClosedApplicationDefinition Definition { get; }
    /// <summary>Gets the exact complete canonical entry digest.</summary>
    internal string EntryDigest { get; }
    /// <summary>Gets resolved policy metadata; not an admission lease.</summary>
    internal EvidencePlan Plan { get; }
    /// <summary>Gets the complete authenticated v2 descriptor projection.</summary>
    internal EvidenceLinuxApplicationDescriptor Descriptor { get; }

    /// <summary>Selects compiled metadata only after authentic owner/input/account binding.</summary>
    internal static EvidenceApplicationSelection Create(EvidenceProtectedLaunchInput input, LinuxOwnerActivation owner,
        LinuxRunAccounts accounts, CancellationToken token)
    {
        owner.RequireLaunchInput(input, token);
        accounts.RequireOwnedBy(owner, token);
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(input.ReadPolicyBytes(token));
        var plan = ResolvePlan(input, token);
        var ids = new EvidenceClosedApplicationIdentities(accounts.WorkerUid, accounts.WorkerGid,
            accounts.SubjectUid, accounts.SubjectGid, accounts.ApplicationUid, accounts.ApplicationGid,
            accounts.ResultsGid, accounts.ResourceAccessGid);
        var selected = EvidenceClosedApplicationCatalogue.ResolveRoot(input.Request.ApplicationId!,
            input.Request.ApplicationEntryDigest!, policy, plan, ids);
        if (selected.Definition.Capabilities.StoppingSeconds > input.Request.StoppingSeconds) throw Rejected();
        owner.RequireLaunchInput(input, token);
        return new(selected.Definition, selected.CatalogueDigest, plan, ids);
    }

    /// <summary>Resolves protected policy/diff inputs without selecting or enrolling an implementation.</summary>
    internal static EvidencePlan ResolvePlan(EvidenceProtectedLaunchInput input, CancellationToken token)
    {
        input.Recheck(token);
        var request = input.Request;
        if (request.ApplicationId is null || request.ApplicationEntryDigest is null) throw Rejected();
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(input.ReadPolicyBytes(token));
        var paths = request.Paths.Select(p => new NormalizedDiffPath(p)).ToList();
        if (input.ReadDiffBytes(token) is { } bytes)
            paths.AddRange(EvidenceUnifiedDiffReader.Read(System.Text.Encoding.UTF8.GetString(bytes)));
        var plan = new EvidencePlanner().Resolve(policy, paths);
        if (plan.Profile.Scope != EvidenceProfileScope.Targeted || plan.Profile.Resources.Count != 1
            || plan.Profile.Producers.Count != 1 || plan.Profile.Producers[0].Kind != "coverage"
            || !request.ObservationProfileIds.Contains(plan.Profile.Id, StringComparer.Ordinal)
            || !request.ObservationProducerIds.Contains(plan.Profile.Producers[0].Id, StringComparer.Ordinal)) throw Rejected();
        input.Recheck(token);
        return plan;
    }

    private static EvidenceAdmissionException Rejected() => new("ASEVD407", "No compiled restricted application registration is available.");
}
