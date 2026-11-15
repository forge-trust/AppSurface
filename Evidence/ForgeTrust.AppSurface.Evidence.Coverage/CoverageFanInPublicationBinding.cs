using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

internal static partial class CoverageFanInEngine
{
    private static async Task PublishAsync(CoverageFanInEngineRequest r, string stage,
        IReadOnlyDictionary<string, byte[]> files, InvocationState state, CancellationToken token, CoverageFanInIO? invocation = null)
    {
        var lease = invocation?.BindOutput(Full(r.OutputDirectory!), r.ReplaceOwned,
            () => { state.Created = true; state.EffectsStarted = true; });
        var result = await CoverageFanInPublication.PublishAsync(Full(r.OutputDirectory!), Full(r.RepositoryRoot), stage,
            state.Result("Incomplete").OperationId, r.Expected.Digest, files, r.ReplaceOwned, r.Expected.Bounds, token,
            onCreated: () => { state.Created = true; state.EffectsStarted = true; },
            onEffectsStarted: () => state.EffectsStarted = true, heldOutputLease: lease, imported: state.Imported,
            admitMetadata: invocation is null ? null : invocation.AdmitMetadata,
            validateBeforeCommit: async cancellation =>
            {
                invocation?.VerifyBindings();
                await VerifySourceAsync(r, cancellation).ConfigureAwait(false);
            }).ConfigureAwait(false);
        state.Generation = result.Generation;
        state.Artifacts.AddRange(result.Artifacts);
        state.Committed = true;
    }

    /// <summary>Checks completed local custody and private registration without creating metadata or adopting a tree.</summary>
    private static void VerifyGeneration(CoverageFanInEngineRequest r, CoverageFanInIO io, string directory,
        string stage, IReadOnlyList<string> payloadNames)
    {
        var markerBytes = io.Read(Path.Join(directory, CoverageFanInIO.CompletionName));
        var marker = Parse(markerBytes);
        if (marker.GetProperty("schemaVersion").GetInt32() != 2 || marker.GetProperty("stage").GetString() != stage
            || marker.GetProperty("policyDigest").GetString() != r.Expected.Digest
            || marker.GetProperty("physicalIdentity").GetString() != io.PhysicalIdentity(directory)
            || marker.GetProperty("materialization").GetString() is not ("local" or "imported")
            || !Guid.TryParseExact(marker.GetProperty("generation").GetString(), "N", out _)
            || !Guid.TryParseExact(marker.GetProperty("operationId").GetString(), "N", out _))
            throw new InvalidDataException("Local generation custody differs.");
        var payloads = marker.GetProperty("files");
        ExactProperties(payloads, payloadNames);
        io.ExactInventory(directory, payloadNames.Append(CoverageFanInIO.CompletionName));
        var inventory = new List<CoverageArtifactInventoryEntry>();
        foreach (var name in payloadNames)
        {
            var bytes = io.Read(Path.Join(directory, name), xml: name.EndsWith(".xml", StringComparison.Ordinal));
            var entry = payloads.GetProperty(name);
            var hash = CoverageFanInIO.Hash(bytes);
            if (entry.GetProperty("length").GetInt64() != bytes.LongLength || entry.GetProperty("sha256").GetString() != hash)
                throw new InvalidDataException("Local generation payload differs.");
            inventory.Add(new(name, bytes.LongLength, hash));
        }
        inventory.Add(new(CoverageFanInIO.CompletionName, markerBytes.LongLength, CoverageFanInIO.Hash(markerBytes)));
        using var store = CoverageArtifactStore.OpenReadOnly();
        var registration = store.GetCurrentRun(Full(directory));
        if (registration is null || registration.ManifestBase.OutputIdentity != io.PhysicalIdentity(directory)
            || registration.ManifestBase.RunId != marker.GetProperty("generation").GetString()
            || !registration.Inventory.OrderBy(x => x.Path, StringComparer.Ordinal).SequenceEqual(inventory.OrderBy(x => x.Path, StringComparer.Ordinal)))
            throw new InvalidDataException("Local generation registration is absent or differs.");
    }
}
