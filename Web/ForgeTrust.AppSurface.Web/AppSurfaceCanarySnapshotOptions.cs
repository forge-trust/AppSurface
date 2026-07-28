namespace ForgeTrust.AppSurface.Web;

/// <summary>Configures bounded execution for <c>GET /_appsurface/canaries</c>.</summary>
/// <remarks>
/// A snapshot is current deploy evidence, not a background job or readiness probe. Cancellation remains cooperative:
/// evaluators that ignore their token can delay the response, but they are never left running after it completes.
/// </remarks>
public sealed class AppSurfaceCanarySnapshotOptions
{
    /// <summary>Gets or sets the maximum selected canaries. The default is 64 and the supported range is 1-256.</summary>
    public int MaxSelectedCanaries { get; set; } = 64;

    /// <summary>Gets or sets the maximum concurrent evaluator invocations. The default is 4.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Gets or sets the timeout applied to each started evaluator. The default is 10 seconds.</summary>
    public TimeSpan PerCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the total snapshot admission deadline. The default is 30 seconds.</summary>
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Creates an internal immutable-at-mapping copy of these host settings.</summary>
    internal AppSurfaceCanarySnapshotOptions Copy() =>
        new()
        {
            MaxSelectedCanaries = MaxSelectedCanaries,
            MaxConcurrency = MaxConcurrency,
            PerCheckTimeout = PerCheckTimeout,
            OverallTimeout = OverallTimeout,
        };
}
