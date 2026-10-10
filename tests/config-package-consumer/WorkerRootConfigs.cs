using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Config;

namespace ConfigPackageConsumer.WorkerRoot;

public sealed record WorkerPublicOptions(
    bool EvidenceSupportEnabled,
    bool WorkerAdmissionEnabled,
    bool PlanModeActive);

internal static class WorkerRootFixtureCounters
{
    private static readonly ConcurrentDictionary<string, int> Activations = new(StringComparer.Ordinal);

    public static int Total => Activations.Values.Sum();

    public static int For(string typeName) => Activations.TryGetValue(typeName, out var count) ? count : 0;

    public static void Reset() => Activations.Clear();

    internal static void Activated(string typeName) => Activations.AddOrUpdate(typeName, 1, static (_, count) => count + 1);
}

internal abstract class TrackedWorkerRootConfig<T> : Config<T>
    where T : class
{
    protected TrackedWorkerRootConfig(string typeName) => WorkerRootFixtureCounters.Activated(typeName);
}

[ConfigKey("Skoolit.DurableWorkers", root: true)]
internal sealed class DurableWorkerPublicOptionsConfig : TrackedWorkerRootConfig<WorkerPublicOptions>
{
    public DurableWorkerPublicOptionsConfig() : base(nameof(DurableWorkerPublicOptionsConfig)) { }
}

[ConfigKey("Skoolit.AlphaEvidence.SourceOnly", root: true)]
internal sealed class AlphaEvidenceSourceOnlyPublicOptionsConfig : TrackedWorkerRootConfig<string>
{
    public AlphaEvidenceSourceOnlyPublicOptionsConfig() : base(nameof(AlphaEvidenceSourceOnlyPublicOptionsConfig)) { }
}

[ConfigKey("Skoolit.Forwarding.Extraction", root: true)]
internal sealed class ForwardingExtractionPublicOptionsConfig : TrackedWorkerRootConfig<string>
{
    public ForwardingExtractionPublicOptionsConfig() : base(nameof(ForwardingExtractionPublicOptionsConfig)) { }
}

[ConfigKey("Skoolit.DataProtection", root: true)]
internal sealed class SharedDataProtectionPublicOptionsConfig : TrackedWorkerRootConfig<string>
{
    public SharedDataProtectionPublicOptionsConfig() : base(nameof(SharedDataProtectionPublicOptionsConfig)) { }
}
