namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Tracks the child-node budget shared by one expanded report.
/// </summary>
/// <remarks>
/// Each context belongs to exactly one report traversal. Do not share an instance across concurrent traversals: the
/// caller owns ordering child reads and consumption so failed member reads do not consume report capacity.
/// </remarks>
internal sealed class ConfigAuditReportTraversalContext
{
    private int _remainingNodes;

    /// <summary>
    /// Initializes a report-local child-node budget.
    /// </summary>
    /// <param name="maxNodes">The maximum number of child nodes the report may emit. Must be at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxNodes"/> is less than one.</exception>
    public ConfigAuditReportTraversalContext(int maxNodes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNodes, 1);
        _remainingNodes = maxNodes;
    }

    /// <summary>
    /// Gets a value indicating whether the global budget stopped a child from being created.
    /// </summary>
    public bool WasTruncated { get; private set; }

    /// <summary>
    /// Gets a value indicating whether a member read may still produce a child node.
    /// </summary>
    /// <remarks>
    /// When capacity is exhausted, this property returns <see langword="false"/> and sets
    /// <see cref="WasTruncated"/> so the report can explain why traversal stopped. It does not consume capacity.
    /// </remarks>
    public bool HasRemainingCapacity
    {
        get
        {
            if (_remainingNodes > 0)
            {
                return true;
            }

            WasTruncated = true;
            return false;
        }
    }

    /// <summary>
    /// Consumes one report-wide child node when capacity remains.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when one child node was consumed; otherwise <see langword="false"/> after setting
    /// <see cref="WasTruncated"/> because the shared report-wide capacity is exhausted.
    /// </returns>
    public bool TryConsumeNode()
    {
        if (!HasRemainingCapacity)
        {
            WasTruncated = true;
            return false;
        }

        _remainingNodes--;
        return true;
    }
}
