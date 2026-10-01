using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Bounds structural discovery and synchronous providerless work for opted-in configuration roots.</summary>
/// <remarks>All values must be positive. Configuration is captured once per host; rebuild the host to change it.
/// The providerless deadline is cooperative and shared by all unconstrained references in one root invocation.</remarks>
public sealed class AppSurfaceConfigOptions
{
    /// <summary>Total providerless resolution budget per root; defaults to 30 seconds.</summary>
    public TimeSpan ProviderlessResolutionBudget { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum member depth beneath a root; defaults to 32.</summary>
    public int MaxCompositionGraphDepth { get; set; } = 32;
    /// <summary>Maximum discovered graph nodes including the root; defaults to 4096.</summary>
    public int MaxCompositionGraphNodes { get; set; } = 4096;
    /// <summary>Maximum scalar secret destinations in one root; defaults to 256.</summary>
    public int MaxSecretDestinationsPerRoot { get; set; } = 256;
}

/// <summary>Rejects nonpositive discovery/deadline limits before a host reports ready.</summary>
internal sealed class AppSurfaceConfigOptionsValidator : IValidateOptions<AppSurfaceConfigOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AppSurfaceConfigOptions options)
    {
        var errors = new List<string>();
        if (options.ProviderlessResolutionBudget <= TimeSpan.Zero) errors.Add("ProviderlessResolutionBudget must be positive.");
        if (options.MaxCompositionGraphDepth <= 0) errors.Add("MaxCompositionGraphDepth must be positive.");
        if (options.MaxCompositionGraphNodes <= 0) errors.Add("MaxCompositionGraphNodes must be positive.");
        if (options.MaxSecretDestinationsPerRoot <= 0) errors.Add("MaxSecretDestinationsPerRoot must be positive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
