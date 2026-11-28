namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Finds the resources transitively reachable from a set of required resource IDs.</summary>
internal static class EvidenceResourceReachability
{
    /// <summary>Walks required IDs and their declared dependencies using ordinal identity.</summary>
    /// <param name="declarations">Resource declarations indexed by ID.</param>
    /// <param name="roots">Required resource IDs for the operation.</param>
    /// <param name="cancellationToken">Cancellation checked as dependencies are queued.</param>
    /// <returns>Each reachable ID once, with a null declaration for an undeclared ID.</returns>
    /// <remarks>The sequence describes membership. Callers apply their own missing-resource policy and ordering.</remarks>
    internal static IEnumerable<(string Id, EvidenceResourceDeclaration? Declaration)> Enumerate(
        IReadOnlyDictionary<string, EvidenceResourceDeclaration> declarations, IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id)) continue;
            var declaration = declarations.GetValueOrDefault(id);
            yield return (id, declaration);
            if (declaration is null) continue;
            foreach (var dependency in declaration.Requires)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.Push(dependency);
            }
        }
    }
}
