using System.Globalization;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation;

/// <summary>Projects an activation result onto the exact sample HTTP wire contract.</summary>
/// <param name="Outcome">Closed activation outcome name.</param>
/// <param name="ObservedHealthState">Defined observed state name, or null.</param>
/// <param name="ProblemCode">Validated bounded problem code, or null.</param>
/// <param name="PumpResult">Exact aggregate projection for Completed, or null.</param>
internal sealed record ActivationResponse(
    string Outcome,
    string? ObservedHealthState,
    string? ProblemCode,
    PumpResultResponse? PumpResult)
{
    /// <summary>Projects every validated service field, retaining explicit nulls and aggregate counts.</summary>
    /// <param name="result">Provider-neutral activation result.</param>
    internal static ActivationResponse From(DurableExternalActivationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var pump = result.PumpResult;
        return new ActivationResponse(
            result.Kind.ToString(),
            result.ObservedHealthState?.ToString(),
            result.ProblemCode,
            pump is null
                ? null
                : new PumpResultResponse(
                    pump.Discovered,
                    pump.Claimed,
                    pump.Processed,
                    pump.Deferred,
                    pump.Failed,
                    pump.HasMore,
                    pump.NextDueAtUtc?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
                    pump.Elapsed.Ticks));
    }
}

/// <summary>Contains the complete lossless pump aggregate returned by one completed activation.</summary>
/// <param name="Discovered">Candidate count.</param>
/// <param name="Claimed">Successful claim count.</param>
/// <param name="Processed">Successfully processed count.</param>
/// <param name="Deferred">Policy-deferred count.</param>
/// <param name="Failed">Safely failed or suspended count.</param>
/// <param name="HasMore">Whether immediately eligible work may remain.</param>
/// <param name="NextDueAtUtc">Seven-digit UTC timestamp or null.</param>
/// <param name="ElapsedTicks">Elapsed duration in 100-nanosecond ticks.</param>
internal sealed record PumpResultResponse(
    int Discovered,
    int Claimed,
    int Processed,
    int Deferred,
    int Failed,
    bool HasMore,
    string? NextDueAtUtc,
    long ElapsedTicks);
