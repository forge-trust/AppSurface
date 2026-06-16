using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Validates product-intelligence options that affect registry composition.
/// </summary>
/// <remarks>
/// This validator builds the same immutable registry as the dispatcher path so duplicate names and unsafe custom
/// contracts fail during service-provider validation when hosts enable options validation on build/start.
/// </remarks>
internal sealed class AppSurfaceProductIntelligenceOptionsValidator
    : IValidateOptions<AppSurfaceProductIntelligenceOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AppSurfaceProductIntelligenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            _ = new DefaultAppSurfaceProductEventRegistry(options.RegisteredEventContracts);
            return ValidateOptionsResult.Success;
        }
        catch (AppSurfaceProductEventContractRegistrationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
