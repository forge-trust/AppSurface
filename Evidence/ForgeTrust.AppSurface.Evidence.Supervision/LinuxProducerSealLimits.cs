using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Pure count, depth and byte bounds for the native producer seal-tree walk.</summary>
/// <remarks>
/// Call only with counts and lengths collected from the actual retained directory and file handles. This policy
/// does not open, inspect, own, seal or authorize a workspace; the caller retains those native checks and emits
/// the original fixed failure diagnostics before rejecting the tree.
/// </remarks>
internal static class LinuxProducerSealLimits
{
    internal const int MaximumDepth = 32;

    /// <summary>Checks a child count before the native walker adds the next discovered entry.</summary>
    internal static bool NodeCapacityReachedBeforeAdd(int entriesCount) =>
        entriesCount >= LinuxProducerWorkspaceLayout.MaximumNodes;

    /// <summary>Checks the completed collection count with its original inclusive maximum.</summary>
    internal static bool CollectedNodeCountExceeded(int entriesCount) =>
        entriesCount > LinuxProducerWorkspaceLayout.MaximumNodes;

    /// <summary>The maximum accepted recursive child depth is inclusive.</summary>
    internal static bool DepthExceeded(int depth) => depth > MaximumDepth;

    /// <summary>Accumulates one actual entry length, ignoring directory lengths and rejecting unsigned overflow.</summary>
    /// <param name="currentTotal">The total for earlier regular-file entries, bounded by the existing tree maximum.</param>
    /// <param name="directory">Whether the actual entry is a directory; directory metadata lengths are not charged.</param>
    /// <param name="length">The unsigned length observed from the retained entry metadata.</param>
    /// <param name="nextTotal">The unchanged total for directories/rejection, or the accepted file total.</param>
    /// <returns>True when the entry is within the existing aggregate byte bound.</returns>
    internal static bool TryAccumulateBytes(long currentTotal, bool directory, ulong length, out long nextTotal)
    {
        nextTotal = currentTotal;
        if (directory) return true;
        if (length > (ulong)(LinuxProducerWorkspaceLayout.MaximumTreeBytes - currentTotal)) return false;
        nextTotal = checked(currentTotal + (long)length);
        return true;
    }
}
