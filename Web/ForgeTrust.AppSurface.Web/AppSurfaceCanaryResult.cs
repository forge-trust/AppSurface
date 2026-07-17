namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Represents the status-only result of one named canary evaluation.
/// </summary>
public sealed class AppSurfaceCanaryResult
{
    /// <summary>
    /// Initializes a new result.
    /// </summary>
    /// <param name="status">A defined named-canary status.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is undefined.</exception>
    public AppSurfaceCanaryResult(AppSurfaceCanaryStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "The AppSurface canary status must be defined.");
        }

        Status = status;
    }

    /// <summary>Gets the current canary status.</summary>
    public AppSurfaceCanaryStatus Status { get; }
}
