namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Tracks the child-node budget shared by one expanded report.
/// </summary>
internal sealed class ConfigAuditReportTraversalContext
{
    private int _remainingNodes;

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
    /// Consumes one report-wide child node when capacity remains.
    /// </summary>
    public bool TryConsumeNode()
    {
        if (_remainingNodes <= 0)
        {
            WasTruncated = true;
            return false;
        }

        _remainingNodes--;
        return true;
    }
}
