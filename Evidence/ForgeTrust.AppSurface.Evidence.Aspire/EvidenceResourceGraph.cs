using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Aspire;

/// <summary>Validates profile resources and provides their closures and deterministic dependency order.</summary>
internal sealed class EvidenceResourceGraph
{
    private readonly IReadOnlyList<EvidenceResourceDeclaration> _resources;
    private readonly IReadOnlyDictionary<string, EvidenceResourceDeclaration> _byId;

    internal EvidenceResourceGraph(IEnumerable<EvidenceResourceDeclaration> resources)
    {
        _resources = resources.ToArray();
        _byId = _resources.ToDictionary(static resource => resource.Id, StringComparer.Ordinal);
    }

    /// <summary>Validates producer closures first, then every unused branch with shared visited state.</summary>
    internal void ValidatePreparation(IEnumerable<EvidenceProducerDeclaration> producers)
    {
        var traversal = new Traversal(this, GraphDiagnostics.Prepared);
        traversal.Visit(producers.SelectMany(static producer => producer.RequiredResources));
        traversal.Visit(_resources.Select(static resource => resource.Id));
    }

    /// <summary>Gets sorted startup IDs while preserving the contract's versioned closure behavior.</summary>
    internal IReadOnlyList<string> GetRequiredResourceIds(EvidencePlan plan, IEnumerable<EvidenceProducerDeclaration> freshProducers)
    {
        var traversal = new Traversal(this, GraphDiagnostics.Prepared);
        if (plan.ContractVersion == "1.0")
            traversal.Visit(_resources.Select(static resource => resource.Id));
        else
            traversal.Visit(freshProducers.SelectMany(static producer => producer.RequiredResources));
        return Array.AsReadOnly(traversal.Visited.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the transitive resource closure used to classify a prepared producer's readiness.</summary>
    internal IReadOnlySet<string> GetProducerResourceClosure(EvidenceProducerDeclaration producer)
    {
        var traversal = new Traversal(this, GraphDiagnostics.Prepared);
        traversal.Visit(producer.RequiredResources);
        return traversal.Visited;
    }

    /// <summary>Gets dependency-first order with ordinal roots and declared dependency order.</summary>
    internal IReadOnlyList<EvidenceResourceDeclaration> GetOrderedResources()
    {
        var traversal = new Traversal(this, GraphDiagnostics.Ordinary);
        traversal.Visit(_resources.OrderBy(static resource => resource.Id, StringComparer.Ordinal)
            .Select(static resource => resource.Id));
        return traversal.Ordered;
    }

    private sealed class Traversal(EvidenceResourceGraph graph, GraphDiagnostics diagnostics)
    {
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly List<EvidenceResourceDeclaration> _ordered = [];

        internal IReadOnlySet<string> Visited => _visited;
        internal IReadOnlyList<EvidenceResourceDeclaration> Ordered => _ordered;

        internal void Visit(IEnumerable<string> roots)
        {
            foreach (var id in roots) Visit(id, referringResource: null);
        }

        private void Visit(string id, EvidenceResourceDeclaration? referringResource)
        {
            if (_visiting.Contains(id)) throw diagnostics.Cycle(id);
            if (_visited.Contains(id)) return;
            if (!graph._byId.TryGetValue(id, out var resource))
            {
                if (referringResource is null && diagnostics.MissingRootThrowsKeyNotFound)
                    throw new KeyNotFoundException(id);
                throw diagnostics.Missing(referringResource, id);
            }

            _visiting.Add(id);
            foreach (var dependencyId in resource.Requires)
                Visit(dependencyId, resource);
            _visiting.Remove(id);
            _visited.Add(id);
            _ordered.Add(resource);
        }
    }

    private sealed record GraphDiagnostics(Func<EvidenceResourceDeclaration?, string, EvidenceHostException> Missing,
        Func<string, EvidenceHostException> Cycle, bool MissingRootThrowsKeyNotFound)
    {
        internal static GraphDiagnostics Prepared { get; } = new(
            (_, _) => new EvidenceHostException("ASEVD304", "The resource graph is missing a dependency or contains a cycle.", "Declare an acyclic complete resource graph before preparation."),
            _ => new EvidenceHostException("ASEVD304", "The resource graph is missing a dependency or contains a cycle.", "Declare an acyclic complete resource graph before preparation."),
            false);

        internal static GraphDiagnostics Ordinary { get; } = new(
            (resource, dependencyId) => new EvidenceHostException("ASEVD305",
                $"Resource '{resource!.Id}' requires undeclared resource '{dependencyId}'.", "Declare every required resource in the selected profile."),
            id => new EvidenceHostException("ASEVD304", $"Resource dependency cycle includes '{id}'.", "Declare an acyclic resource dependency graph."),
            true);
    }
}
